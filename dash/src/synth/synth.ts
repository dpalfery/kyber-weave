// Span synthesizer for KyberDash (spec: docs/specs/kyberdash, task 9.1;
// design.md "Ingest layer", decision D5). Upstream's parser already reads
// session files for 41 providers and emits `ParsedProviderCall`s; this module
// is where those calls become canonical records — "the session-file providers
// become span synthesizers" — so no analysis ever learns which path its data
// arrived by (R11.1, one data path).
//
// What this module is accountable for:
//
//   * R1.1 — every provider the upstream parser supports synthesizes with no
//     configuration: `PROVIDER_CONVENTIONS` carries an explicit row for each
//     upstream provider name, the exhaustiveness test pins it against
//     upstream's own `allProviderNames()`, and a name without a row still
//     takes the documented default rather than failing a first run.
//   * R1.4 — synthesis is a pure function over an in-memory array. No API
//     key, no proxy, no network call, no wrapper around the agent tool: the
//     constructor takes options, `synthesize` takes calls, nothing else is
//     consulted. The offline test pins this by stubbing `fetch` to throw.
//   * R1.5 — upstream's parallel cold-parse path (dash/src/parse-workers.ts)
//     yields file results in submission order, but completion order is
//     arbitrary and the install loop interleaves. `synthesizeParallel`
//     reproduces that arrival pattern — chunked, resolved concurrently,
//     assembled in input order — and the parity test asserts it deep-equals
//     the serial path. The property that makes that true by construction is
//     that `synthesizeCall` reads exactly one call and touches no shared
//     state: no batch boundaries, no index arithmetic, no order-dependent
//     keys (identity comes from the call's own deduplication key).
//
// Identity (design.md: "The synthesizer is where Requirement 3 is satisfied.
// It extends upstream's existing cross-provider deduplication key rather than
// adding a parallel mechanism"). Upstream already assigns every call a
// `deduplicationKey` (`provider:session:message`, unique across a pass), so
// the synthesized span id is that key under a `synth:` namespace — one
// identity scheme, extended, not a second one. Re-synthesizing the same call
// yields the same span id, which is what lets the store's idempotent upsert
// (R2.5) collapse a session seen through two paths (task 9.3's case) without
// any deduplication code living here. A `synth:`-prefixed id can also never
// collide with an OTLP hex span id.
//
// Validation is deliberately NOT re-done here: the record validator
// (`tokenValidator` / `recordValidationProblems`, canon/adapters/quarantine.ts)
// is the single seam that rejects records and persists problems (R4.3, R4.4),
// and the adapters follow the same split — `normalize` emits, `validate`
// rejects. Re-validating here would be a second mechanism for one job.

import { Buffer } from 'node:buffer'
import { createHash } from 'node:crypto'

import type { ParsedProviderCall } from '../providers/types.js'
export type { ParsedProviderCall } from '../providers/types.js'
import {
  contentFromParts,
  notMeasurable,
  type CanonicalRecord,
  type ContentPart,
  type CostBlock,
  type MetricAvailability,
  type TokenUsage,
} from '../canon/types.js'
import { exclusiveConvention, inclusiveConvention } from '../canon/adapters/copilot.js'
// The detector keys duplicates on serializeToolArgs; the producer reuses the
// same canonical form so both identities agree by construction. Acyclic:
// analysis/findings.ts never imports synth (it reads canonical records).
import { serializeToolArgs } from '../analysis/findings.js'
import { FILE_SOURCE_PREFIX, measurabilityFor } from '../canon/measurability.js'
import { TWIN_TURN_MAX_SKEW_MS } from '../canon/twin-dedupe.js'
import type {
  ReaderToolCall,
  ReaderToolResult,
  ReaderTurn,
  SourceRecordEnvelope,
  SourceRecordProvenance,
} from './readers/types.js'

export type {
  ReaderToolCall,
  ReaderToolResult,
  SourceRecordEnvelope,
  SourceRecordProvenance,
} from './readers/types.js'

// ---------------------------------------------------------------------------
// Identity scheme (R3.2: extend upstream's key, don't add a mechanism)
// ---------------------------------------------------------------------------

/** Namespace for span ids derived from upstream's deduplication key. */
export const SYNTH_SPAN_PREFIX = 'synth:'

/** Model/network identities that must never be persisted as a coding harness. */
export const EXCLUDED_HARNESS_IDS = new Set(['gemini', 'vercel-gateway'])

export function isExcludedHarness(id: string): boolean {
  return EXCLUDED_HARNESS_IDS.has(id)
}

export function harnessIdFor(call: ParsedProviderCall, envelope?: SourceRecordEnvelope): string {
  return envelope?.harnessId ?? call.provider
}

export function nativeSessionIdFor(call: ParsedProviderCall, envelope?: SourceRecordEnvelope): string {
  if (envelope?.nativeSessionId) return envelope.nativeSessionId
  return String(call.sessionId)
}

/**
 * Native record identity: prefer an explicit id, then `turnId`, then the
 * message segment of upstream's key. When none exist, a digest of session,
 * timestamp, kind, and ordinal — never refresh time or a database row number.
 */
export function nativeRecordIdentity(
  call: ParsedProviderCall,
  envelope?: SourceRecordEnvelope,
  ordinal = 0,
): { nativeRecordId: string; recordDigest?: string } {
  // Cursor added request ids for transcript pairing after digest-based spans
  // were already persisted. Keep that pairing id out of record identity so a
  // refresh replaces the old span instead of inserting the same turn again.
  const cursorPairingId = call.provider === 'cursor' ? call.turnId : undefined
  if (envelope?.nativeRecordId && envelope.nativeRecordId !== cursorPairingId) {
    return { nativeRecordId: envelope.nativeRecordId }
  }
  if (call.turnId !== undefined && call.turnId !== '' && cursorPairingId === undefined) {
    return { nativeRecordId: call.turnId }
  }
  const prefix = `${call.provider}:${call.sessionId}:`
  if (call.deduplicationKey.startsWith(prefix) && call.deduplicationKey.length > prefix.length) {
    return { nativeRecordId: call.deduplicationKey.slice(prefix.length) }
  }
  const digest = createHash('sha256')
      .update(`${nativeSessionIdFor(call, envelope)}\0${call.timestamp}\0llm.invoke\0${ordinal}`, 'utf8')
      .digest('hex')
      .slice(0, 16)
  return { nativeRecordId: digest, recordDigest: digest }
}

export function provenanceFor(
  call: ParsedProviderCall,
  envelope?: SourceRecordEnvelope,
  ordinal = 0,
): SourceRecordProvenance {
  const harnessId = harnessIdFor(call, envelope)
  const nativeSessionId = nativeSessionIdFor(call, envelope)
  const identity = nativeRecordIdentity(call, envelope, ordinal)
  return {
    harnessId,
    sourceKey: envelope?.sourceKey ?? `${harnessId}:${nativeSessionId}`,
    nativeSessionId,
    nativeRecordId: identity.nativeRecordId,
    ...(identity.recordDigest !== undefined ? { recordDigest: identity.recordDigest } : {}),
    ...(envelope?.sourceRevision !== undefined ? { sourceRevision: envelope.sourceRevision } : {}),
    ...(envelope?.parserContractVersion !== undefined
      ? { parserContractVersion: envelope.parserContractVersion }
      : {}),
    ...(envelope?.importedAt !== undefined ? { importedAt: envelope.importedAt } : {}),
    ...(envelope?.locationToken !== undefined ? { locationToken: envelope.locationToken } : {}),
  }
}

/** The trace a synthesized session's calls form: one per (harness, session). */
export function traceIdFor(call: ParsedProviderCall, envelope?: SourceRecordEnvelope): string {
  return `${SYNTH_SPAN_PREFIX}${harnessIdFor(call, envelope)}:${nativeSessionIdFor(call, envelope)}`
}

/**
 * Canonical source identity `<harness-id>:<native-session-id>:<native-record-id>`
 * under the `synth:` namespace so it cannot collide with an OTLP hex span id.
 */
export function spanIdFor(
  call: ParsedProviderCall,
  envelope?: SourceRecordEnvelope,
  ordinal = 0,
): string {
  const provenance = provenanceFor(call, envelope, ordinal)
  return `${SYNTH_SPAN_PREFIX}${provenance.harnessId}:${provenance.nativeSessionId}:${provenance.nativeRecordId}`
}

/**
 * The telemetry source name stamped on synthesized records. Identifies the
 * ingest path instance (the vendored parser reading this provider's files);
 * attribution never reads it (R6.2) — for a file-sourced record the harness
 * is the classified id when an envelope supplied one, otherwise the provider.
 */
export function sourceFor(call: ParsedProviderCall, envelope?: SourceRecordEnvelope): string {
  return `${FILE_SOURCE_PREFIX}${harnessIdFor(call, envelope)}`
}

// ---------------------------------------------------------------------------
// Token conventions at the parsed-call boundary (R4.2)
// ---------------------------------------------------------------------------

/**
 * What a provider's `ParsedProviderCall.inputTokens` means. Upstream
 * normalizes every provider to Anthropic semantics before emitting a call —
 * fresh/uncached input with the cache classes in their own counters — so the
 * exclusive convention is both the default and the row every current
 * provider carries. The rows stay explicit per provider because that is the
 * evidence trail: each row names a parser that was read and found to
 * subtract (or never include) the cache classes. The two spellings map to
 * the shared conversions in canon/adapters/copilot.ts, which exist precisely
 * because two conventions under one field name already cost a silent
 * miscount.
 */
export type TokenConvention = 'exclusive' | 'inclusive'

/**
 * The measured convention per upstream provider (`dash/src/providers/*`):
 *
 *   * claude — `parseApiCall` (dash/src/parser.ts) copies Anthropic's raw
 *     `usage.input_tokens`, which excludes both cache classes.
 *   * codex — "Normalize to Anthropic semantics: inputTokens = non-cached
 *     only" (dash/src/providers/codex.ts), subtracting `cached_input_tokens`.
 *   * gemini — subtracts the cached subset before emitting
 *     (`inputTokens: freshInput`), mirroring its inclusive wire counter.
 *   * copilot — the session.shutdown rollup's cache-INCLUSIVE counters are
 *     converted on the way in (`delta('inputTokens') - cacheRead -
 *     cacheWrite`); a measured store row is `costIsEstimated: false` with
 *     classes already split.
 *   * pi / omp — `usage.input` excludes cache; `usage.cacheRead` /
 *     `usage.cacheWrite` are separate counters (the same shape the pi
 *     adapter's GenAI evidence records).
 *   * every other provider funnels through the same Anthropic-semantics
 *     normalization before yielding a call.
 */
export const PROVIDER_CONVENTIONS: ReadonlyMap<string, TokenConvention> = new Map(
  (
    [
      'antigravity',
      'claude',
      'cline',
      'cline-cli',
      'codebuff',
      'codewhale',
      'codex',
      'copilot',
      // Upstream's three Copilot surface filters. They are the same harness
      // reached through different front ends, so they share Copilot's
      // convention rather than falling through to the default — a provider
      // without a row is exactly the silent-miscount case R4.2 exists to
      // catch, and the drift test above is what surfaced them.
      'copilot-agent',
      'copilot-cli',
      'copilot-vscode',
      'crush',
      'cursor',
      'cursor-agent',
      'devin',
      'droid',
      'dsh',
      'forge',
      'gemini',
      'goose',
      'grok',
      'hermes',
      'ibm-bob',
      'kilo-code',
      'kimi',
      'kimicode',
      'kiro',
      'lingtai-tui',
      'mistral-vibe',
      'mux',
      'omp',
      'open-design',
      'openclaude',
      'openclaw',
      'opencode',
      'pi',
      'quickdesk',
      'qwen',
      'roo-code',
      'vercel-gateway',
      'warp',
      'zcode',
      'zed',
      'zerostack',
    ] as const
  ).map((provider) => [provider, 'exclusive' as TokenConvention]),
)

/**
 * The convention for a provider without an explicit row: upstream's
 * normalization is a property of the `ParsedProviderCall` contract, not of
 * any one provider, so an unseen name still reads fresh-only input. An
 * upstream release that adds provider #42 works on day one and the
 * exhaustiveness test asks for its explicit row.
 */
export const DEFAULT_CONVENTION: TokenConvention = 'exclusive'

/** The convention a call's input counter follows. */
export function conventionFor(
  provider: string,
  conventions: ReadonlyMap<string, TokenConvention> = PROVIDER_CONVENTIONS,
): TokenConvention {
  return conventions.get(provider) ?? DEFAULT_CONVENTION
}

// ---------------------------------------------------------------------------
// Measurability declarations for the file-sourced path (R7.6, R8.5, R10.2)
// ---------------------------------------------------------------------------

/**
 * The file-sourced declarations — the unmeasurable-metric table, the
 * per-provider additions and the stamp `synthesizeCall` applies — live in
 * canon/measurability.ts beside the OTLP baseline, the adapter consultation
 * and the availability helpers the analyses read, so both ingest paths
 * answer one rule from one table instead of two that can drift. They are
 * re-exported here because the synthesizer stamps them on every record and
 * because task 9.1's callers import them from this module.
 */
export {
  FILE_SOURCE_UNMEASURABLE,
  PROVIDER_UNMEASURABLE,
  measurabilityFor,
} from '../canon/measurability.js'

// ---------------------------------------------------------------------------
// Cost (R5.1, R5.2, R5.4)
// ---------------------------------------------------------------------------

/**
 * Convert upstream's cost figure into a cost block carrying its basis. A
 * measured or provider-reported figure (`costIsEstimated` falsy) is carried
 * verbatim on the `harness` basis (R5.2). A figure upstream derived from its
 * bundled rate table (`costIsEstimated: true`) carries the `published` basis
 * — the same distinction R5.1 makes for our own rate tables, applied to
 * theirs. A zero is rendered no-rate, never a priced $0.00: upstream's
 * `calculateCost` returns 0 for an unrated model, and the parsed-call
 * contract carries no signal that distinguishes that from a genuine free
 * call, so the safe reading is the absent one (R5.4).
 */
export function costBlockFor(call: ParsedProviderCall): CostBlock {
  if (call.costUSD !== 0 && Number.isFinite(call.costUSD)) {
    return {
      // Every Copilot figure from a parser is LiteLLM-derived (API list rates) whatever its
      // costIsEstimated flag, so it is never harness-reported (R5.2/R5.3): it is re-priced from
      // the credits table. Only the genuine reader marks `costHarnessReported` and stays harness,
      // and it sets the marker only when the row has a real cost_usd. Copilot without the marker
      // defaults to published so a forgotten flag never leaves a LiteLLM-derived figure at API
      // list rates presented as harness-reported (R5.3).
      basis:
        call.costIsEstimated === true || (call.provider === 'copilot' && call.costHarnessReported !== true)
          ? 'published'
          : 'harness',
      status: 'priced',
      value: call.costUSD,
      currency: 'USD',
      byModel: { [call.model]: call.costUSD },
    }
  }
  // A published-rate provider's zero figure is a rate gap, not a missing basis.
  if (call.costIsEstimated === true) return { basis: 'published', status: 'no_rate' }
  return { basis: 'unknown', status: 'no_rate' }
}

// ---------------------------------------------------------------------------
// The one-call conversion
// ---------------------------------------------------------------------------

/**
 * Synthesize one canonical record from one parsed call. Pure: the record is
 * a function of the call alone, which is the property both cold-parse paths
 * rely on to agree (R1.5). Turn structure: a parsed call is one model
 * invocation, so `op` is `llm.invoke` and the record is the root of its
 * session trace — a parsed call carries no parent evidence, and no parent is
 * invented (R4.3's rule, applied on the way in rather than at the end).
 */
export function synthesizeCall(
  call: ParsedProviderCall,
  conventions: ReadonlyMap<string, TokenConvention> = PROVIDER_CONVENTIONS,
  readerTurn?: ReaderTurn,
  envelope?: SourceRecordEnvelope,
  ordinal = 0,
): CanonicalRecord {
  const counts = {
    input: call.inputTokens,
    output: call.outputTokens,
    // Two spellings of the cache-read class exist upstream (claude leaves
    // `cachedInputTokens` at 0; the other providers mirror it). The
    // non-nullable `cacheReadInputTokens` is authoritative; the mirror is
    // read only when the authoritative field is 0, never summed with it.
    cacheRead: call.cacheReadInputTokens !== 0
      ? call.cacheReadInputTokens
      : call.cachedInputTokens,
    cacheCreation: call.cacheCreationInputTokens,
    ...(call.reasoningTokens !== 0 ? { reasoning: call.reasoningTokens } : {}),
  }
  const tokens: TokenUsage =
    conventionFor(call.provider, conventions) === 'inclusive'
      ? inclusiveConvention(counts)
      : exclusiveConvention(counts)

  const harness = harnessIdFor(call, envelope)
  const sessionId = nativeSessionIdFor(call, envelope)
  const provenance = provenanceFor(call, envelope, ordinal)
  const rawCall = readerTurn === undefined
    ? call
    : {
        ...call,
        ...(readerTurn.contextWindow !== undefined ? { contextWindow: readerTurn.contextWindow } : {}),
        ...(readerTurn.terminationReason !== undefined ? { terminationReason: readerTurn.terminationReason } : {}),
        ...(readerTurn.exitCode !== undefined ? { exitCode: readerTurn.exitCode } : {}),
        ...(readerTurn.isCorrection !== undefined ? { isCorrection: readerTurn.isCorrection, correctionRule: readerTurn.correctionRule } : {}),
      }

  return {
    spanId: spanIdFor(call, envelope, ordinal),
    traceId: traceIdFor(call, envelope),
    parentSpanId: null,
    source: sourceFor(call, envelope),
    harness,
    name: `${call.provider}:${call.model}`,
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: call.timestamp,
    // The session this turn belongs to, carried onto the record.
    //
    // Leaving it unset made `sessionKeys()` fall back to the trace id, which is
    // namespaced (`synth:<provider>:<session>`) while the OTLP path stores the
    // bare `session.id`. One session observed through both paths therefore
    // produced two derived sessions and two runs even after the cross-path
    // collapse had matched its records.
    ...(sessionId === '' ? {} : { sessionId }),
    durationMs: call.activeDurationMs ?? 0,
    status: readerTurn?.exitCode !== undefined && readerTurn.exitCode !== 0
      ? 'error'
      : (readerTurn?.terminationReason === 'task_complete' ? 'ok' : 'unspecified'),
    tokens,
    content: readerTurn === undefined ? {} : contentFromParts(readerTurn.parts),
    ...(readerTurn !== undefined ? { parts: readerTurn.parts } : {}),
    cost: costBlockFor(call),
    measurability: measurabilityFor(call.provider),
    raw: { ...rawCall, provenance },
  }
}

// ---------------------------------------------------------------------------
// Tool invocation synthesis (Issue #180, Task 5)
// ---------------------------------------------------------------------------

/** Maximum bytes of tool result content stored in content parts before truncation (64KB). */
export const MAX_TOOL_RESULT_BYTES = 65_536

/** Maximum bytes of tool arguments stored in raw before truncation (64KB). */
export const MAX_TOOL_ARGUMENTS_BYTES = 65_536

/**
 * Truncate a UTF-8 string to at most \`maxBytes\`, cutting strictly at a valid UTF-8 code point boundary.
 */
export function truncateUtf8(text: string, maxBytes: number): string {
  const buf = Buffer.from(text, "utf8")
  if (buf.byteLength <= maxBytes) return text

  let end = maxBytes
  while (end > 0 && (buf[end] & 0xc0) === 0x80) {
    end--
  }
  return buf.subarray(0, end).toString("utf8")
}

export type CanonicalToolRecord = CanonicalRecord & {
  attributes?: Record<string, unknown>
  provider?: string
  model?: string
}

/** A live reference to one string leaf inside a cloned arguments object. */
type StringLeaf = {
  get: () => string
  set: (value: string) => void
}

/**
 * Bound an object-shaped tool-arguments payload to `maxBytes` of serialized
 * JSON without changing its shape: string leaves are shortened longest-first
 * until the serialization fits, so small fields (paths, flags) survive intact
 * and the stored value stays an object. Truncating the serialization itself
 * would swap the type mid-flight (object to string, and not even valid JSON)
 * and make duplicate detection compare 64KB prefixes instead of arguments.
 *
 * The last-resort empty object only triggers when no string leaf exists to
 * shorten — a >64KB payload of pure numbers, which transcript input cannot
 * produce — and is still flagged truncated with the full size and hash kept.
 */
export function truncateObjectArguments(
  args: Record<string, unknown>,
  maxBytes: number,
): { bounded: Record<string, unknown>; truncated: boolean } {
  let clone: Record<string, unknown>
  try {
    clone = JSON.parse(JSON.stringify(args)) as Record<string, unknown>
  } catch {
    return { bounded: {}, truncated: true }
  }
  const serializedSize = (): number => Buffer.byteLength(JSON.stringify(clone) ?? '{}', 'utf8')
  if (serializedSize() <= maxBytes) return { bounded: clone, truncated: false }

  const leaves: StringLeaf[] = []
  const seen = new Set<unknown>()
  const walk = (node: unknown, set: (value: string) => void): void => {
    if (typeof node === 'string') {
      let current = node
      leaves.push({
        get: () => current,
        set: (value: string) => {
          current = value
          set(value)
        },
      })
    } else if (node !== null && typeof node === 'object' && !seen.has(node)) {
      seen.add(node)
      if (Array.isArray(node)) {
        node.forEach((item, i) => walk(item, (value) => {
          node[i] = value
        }))
      } else {
        for (const [key, value] of Object.entries(node)) {
          walk(value, (next) => {
            (node as Record<string, unknown>)[key] = next
          })
        }
      }
    }
  }
  walk(clone, () => {})

  for (let pass = 0; pass <= leaves.length; pass++) {
    const overage = serializedSize() - maxBytes
    if (overage <= 0) return { bounded: clone, truncated: true }
    let longest: StringLeaf | undefined
    let longestBytes = 0
    for (const leaf of leaves) {
      const size = Buffer.byteLength(leaf.get(), 'utf8')
      if (size > longestBytes) {
        longestBytes = size
        longest = leaf
      }
    }
    if (longest === undefined || longestBytes === 0) break
    longest.set(truncateUtf8(longest.get(), Math.max(0, longestBytes - overage)))
  }
  return serializedSize() <= maxBytes
    ? { bounded: clone, truncated: true }
    : { bounded: {}, truncated: true }
}

/**
 * Synthesize one child `tool.invoke` canonical record from a reader tool call
 * and its paired execution result.
 */
export function synthesizeToolCall(
  parentRecord: CanonicalRecord,
  toolCall: ReaderToolCall,
  toolResult: ReaderToolResult | undefined,
  index = 0,
  call?: ParsedProviderCall,
): CanonicalToolRecord {
  const isError = toolResult?.isError === true
  const status = toolResult === undefined ? 'unset' : (isError ? 'error' : 'ok')
  const resultBytes = toolResult?.content !== undefined
    ? Buffer.byteLength(toolResult.content, 'utf8')
    : undefined

  const serializedArgs = typeof toolCall.arguments === 'string'
    ? toolCall.arguments
    : JSON.stringify(toolCall.arguments ?? {})
  const argsBytes = Buffer.byteLength(serializedArgs, 'utf8')

  let boundedArguments: string | Record<string, unknown> = toolCall.arguments
  let argsTruncated = false
  if (typeof toolCall.arguments === 'string') {
    if (argsBytes > MAX_TOOL_ARGUMENTS_BYTES) {
      boundedArguments = truncateUtf8(toolCall.arguments, MAX_TOOL_ARGUMENTS_BYTES)
      argsTruncated = true
    }
  } else if (toolCall.arguments !== null && typeof toolCall.arguments === 'object') {
    if (argsBytes > MAX_TOOL_ARGUMENTS_BYTES) {
      boundedArguments = truncateObjectArguments(toolCall.arguments, MAX_TOOL_ARGUMENTS_BYTES).bounded
      argsTruncated = true
    }
  }

  let durationMs = 0
  let durationAvailability: MetricAvailability = notMeasurable('Tool duration was not reported in telemetry.')
  if (toolCall.durationMs !== undefined && Number.isFinite(toolCall.durationMs)) {
    durationMs = Math.max(0, toolCall.durationMs)
    durationAvailability = 'measured'
  } else if (toolCall.timestamp && toolResult?.timestamp) {
    const callTime = new Date(toolCall.timestamp).getTime()
    const resTime = new Date(toolResult.timestamp).getTime()
    const diff = resTime - callTime
    if (Number.isFinite(diff) && diff >= 0) {
      durationMs = diff
      durationAvailability = 'derived'
    }
  }

  const attributes: Record<string, unknown> = {
    'gen_ai.tool.name': toolCall.name,
    'gen_ai.tool.call_id': toolCall.id,
    'gen_ai.tool.status': status,
    ...(resultBytes !== undefined
      ? { 'gen_ai.tool.result_bytes': resultBytes }
      : {}),
    ...(toolCall.arguments !== undefined
      ? { 'gen_ai.tool.arguments_bytes': argsBytes }
      : {}),
    // Identity of the FULL pre-truncation arguments in canonical form, so
    // whitespace variants (`{"a":1}` vs `{ "a": 1 }`) share an identity.
    // Emitted only when truncated: complete arguments are compared by the
    // detector in normalised form already. It rides into `raw` with the
    // rest of the attributes, which is what survives the store.
    ...(argsTruncated
      ? { 'gen_ai.tool.arguments_hash': createHash('sha256').update(serializeToolArgs(toolCall.arguments), 'utf8').digest('hex') }
      : {}),
    ...(argsTruncated ? { 'gen_ai.tool.arguments_truncated': true } : {}),
    ...(durationAvailability === 'measured' || durationAvailability === 'derived'
      ? { 'gen_ai.tool.duration_ms': durationMs }
      : {}),
  }

  let parts: (ContentPart & { truncated?: boolean })[] = []
  if (toolResult?.content !== undefined) {
    const text = toolResult.content
    const textBytes = resultBytes ?? Buffer.byteLength(text, 'utf8')
    if (textBytes > MAX_TOOL_RESULT_BYTES) {
      parts = [
        {
          part: 'tool_result_content',
          text: truncateUtf8(text, MAX_TOOL_RESULT_BYTES),
          truncated: true,
          order: 0,
        },
      ]
    } else {
      parts = [
        {
          part: 'tool_result_content',
          text,
          order: 0,
        },
      ]
    }
  }

  const nameParts = parentRecord.name.split(':')
  const provider = call?.provider ?? (nameParts.length > 1 ? nameParts[0] : parentRecord.harness)
  const model = call?.model ?? (nameParts.length > 1 ? nameParts.slice(1).join(':') : undefined)

  const toolHash = createHash('sha256')
    .update(`${toolCall.id || index}:${toolCall.name}:${serializedArgs}`)
    .digest('hex')
    .slice(0, 12)

  return {
    spanId: `${parentRecord.spanId}-t${toolHash}`,
    traceId: parentRecord.traceId,
    parentSpanId: parentRecord.spanId,
    source: parentRecord.source,
    harness: parentRecord.harness,
    ...(parentRecord.sessionId !== undefined && parentRecord.sessionId !== null
      ? { sessionId: parentRecord.sessionId }
      : {}),
    ...(provider ? { provider } : {}),
    ...(model ? { model } : {}),
    name: toolCall.name,
    op: 'tool.invoke',
    kind: 'internal',
    timestamp: toolCall.timestamp ?? parentRecord.timestamp,
    durationMs,
    status,
    tokens: {
      freshInput: 0,
      cacheRead: 0,
      cacheCreation: 0,
      output: 0,
      reportedInput: 0,
      reportedOutput: 0,
    },
    content: {},
    parts,
    cost: { basis: 'unknown', status: 'no_rate' },
    measurability: {
      ...parentRecord.measurability,
      duration: durationAvailability,
    },
    attributes,
    raw: {
      ...attributes,
      arguments: boundedArguments,
      result: parts[0]?.text,
    },
  }
}

export function synthesizeToolCalls(
  parentRecord: CanonicalRecord,
  call: ParsedProviderCall,
  readerTurn?: ReaderTurn,
  sessionToolResults?: ReadonlyMap<string, ReaderToolResult>,
): CanonicalToolRecord[] {
  if (!readerTurn?.toolCalls || readerTurn.toolCalls.length === 0) {
    return []
  }

  const toolResults = readerTurn.toolResults ?? []
  return readerTurn.toolCalls.map((toolCall, i) => {
    const toolResult =
      sessionToolResults?.get(toolCall.id) ??
      toolResults.find((r) => r.toolCallId === toolCall.id)
    return synthesizeToolCall(parentRecord, toolCall, toolResult, i, call)
  })
}

// ---------------------------------------------------------------------------
// The synthesizer
// ---------------------------------------------------------------------------

/** Construction options for {@link Synthesizer}. */
export type SynthesizerOptions = {
  /**
   * How many calls each chunk of the parallel path holds. Arbitrary but
   * finite, so any corpus larger than one chunk exercises multiple
   * concurrently-resolving chunks — the arrival pattern R1.5 pins.
   */
  chunkSize?: number
  /**
   * Token-convention rows, overriding `PROVIDER_CONVENTIONS` for tests and
   * for a provider whose measured corpus contradicts its row.
   */
  conventions?: ReadonlyMap<string, TokenConvention>
}

/** Default chunk size; small enough that ordinary corpora span several chunks. */
export const DEFAULT_CHUNK_SIZE = 128

function chunk<T>(items: readonly T[], size: number): T[][] {
  const chunks: T[][] = []
  for (let i = 0; i < items.length; i += size) chunks.push(items.slice(i, i + size))
  return chunks
}

function isZeroCounters(call: ParsedProviderCall): boolean {
  return (
    call.inputTokens === 0 &&
    call.outputTokens === 0 &&
    call.cacheReadInputTokens === 0 &&
    call.cacheCreationInputTokens === 0 &&
    (call.cachedInputTokens ?? 0) === 0 &&
    (call.reasoningTokens ?? 0) === 0
  )
}

function isCandidateCollapse(call: ParsedProviderCall, harnessId?: string): boolean {
  if (isZeroCounters(call)) return false
  const harness = harnessId ?? call.provider
  return (
    harness === 'claude-desktop' ||
    harness === 'kyberdash/claude-desktop' ||
    call.provider === 'claude-desktop'
  )
}

function callCounterKey(call: ParsedProviderCall): string {
  return [
    call.sessionId ?? '',
    call.provider,
    call.model,
    call.inputTokens,
    call.outputTokens,
    call.cacheReadInputTokens,
    call.cacheCreationInputTokens,
    call.reasoningTokens ?? 0,
  ].join(':')
}

function mergeReaderTurns(keeper?: ReaderTurn, donor?: ReaderTurn): ReaderTurn | undefined {
  if (!keeper && !donor) return undefined
  if (!keeper) return donor
  if (!donor) return keeper

  const seenParts = new Set((keeper.parts ?? []).map((p) => `${p.part}\0${p.text}`))
  const extraParts = (donor.parts ?? []).filter((p) => {
    const k = `${p.part}\0${p.text}`
    if (seenParts.has(k)) return false
    seenParts.add(k)
    return true
  })
  const parts = [...(keeper.parts ?? []), ...extraParts]

  const toolCalls = [
    ...(keeper.toolCalls ?? []),
    ...(donor.toolCalls ?? []).filter((dt) => !keeper.toolCalls?.some((kt) => kt.id === dt.id)),
  ]

  const toolResults = [
    ...(keeper.toolResults ?? []),
    ...(donor.toolResults ?? []).filter((dr) => !keeper.toolResults?.some((kr) => kr.toolCallId === dr.toolCallId)),
  ]

  const toolsOffered = Array.from(new Set([...(keeper.toolsOffered ?? []), ...(donor.toolsOffered ?? [])]))

  return {
    ...keeper,
    parts,
    ...(toolCalls.length > 0 ? { toolCalls } : {}),
    ...(toolResults.length > 0 ? { toolResults } : {}),
    ...(toolsOffered.length > 0 ? { toolsOffered } : {}),
    terminationReason: keeper.terminationReason ?? donor.terminationReason,
    exitCode: keeper.exitCode ?? donor.exitCode,
    isCorrection: keeper.isCorrection ?? donor.isCorrection,
    correctionRule: keeper.correctionRule ?? donor.correctionRule,
  }
}

function mergeParsedCalls(keeper: ParsedProviderCall, donor: ParsedProviderCall): ParsedProviderCall {
  const tools = Array.from(new Set([...(keeper.tools ?? []), ...(donor.tools ?? [])]))
  const bashCommands = Array.from(new Set([...(keeper.bashCommands ?? []), ...(donor.bashCommands ?? [])]))
  const toolSequence = [...(keeper.toolSequence ?? []), ...(donor.toolSequence ?? [])]

  return {
    ...keeper,
    tools,
    bashCommands,
    ...(toolSequence.length > 0 ? { toolSequence } : {}),
    activeDurationMs: Math.max(keeper.activeDurationMs ?? 0, donor.activeDurationMs ?? 0),
  }
}

function mergeEnvelopes(keeper: SourceRecordEnvelope, donor: SourceRecordEnvelope): SourceRecordEnvelope {
  const mergedCall = mergeParsedCalls(keeper.call, donor.call)
  const mergedReaderTurn = mergeReaderTurns(keeper.readerTurn, donor.readerTurn)
  return {
    ...keeper,
    call: mergedCall,
    ...(mergedReaderTurn !== undefined ? { readerTurn: mergedReaderTurn } : {}),
  }
}

function collapseKey(counterKey: string, nativeId: string | undefined): string {
  return nativeId === undefined ? counterKey : `${counterKey}\0${nativeId}`
}

export function collapseEnvelopeTurns(envelopes: readonly SourceRecordEnvelope[]): SourceRecordEnvelope[] {
  if (envelopes.length <= 1) return [...envelopes]

  const byKey = new Map<string, SourceRecordEnvelope[]>()
  for (const env of envelopes) {
    if (!isCandidateCollapse(env.call, env.harnessId)) continue
    const key = collapseKey(callCounterKey(env.call), env.nativeRecordId)
    const list = byKey.get(key) ?? []
    list.push(env)
    byKey.set(key, list)
  }

  const dropped = new Set<SourceRecordEnvelope>()
  const mergedInto = new Map<SourceRecordEnvelope, SourceRecordEnvelope>()

  for (const group of byKey.values()) {
    if (group.length <= 1) continue
    const sorted = [...group].sort(
      (a, b) => new Date(a.call.timestamp).getTime() - new Date(b.call.timestamp).getTime(),
    )
    let cluster: SourceRecordEnvelope[] = []
    const closeCluster = () => {
      if (cluster.length > 1) {
        const keeper = cluster[0]!
        let current = keeper
        for (let i = 1; i < cluster.length; i++) {
          const donor = cluster[i]!
          dropped.add(donor)
          current = mergeEnvelopes(current, donor)
        }
        mergedInto.set(keeper, current)
      }
      cluster = []
    }
    for (const env of sorted) {
      const first = cluster[0]
      const exceedsSpan =
        first !== undefined &&
        Math.abs(new Date(env.call.timestamp).getTime() - new Date(first.call.timestamp).getTime()) >
          TWIN_TURN_MAX_SKEW_MS
      if (exceedsSpan) closeCluster()
      cluster.push(env)
    }
    closeCluster()
  }

  return envelopes.flatMap((env) => {
    if (dropped.has(env)) return []
    const merged = mergedInto.get(env)
    return [merged ?? env]
  })
}

type CallAndTurn = {
  call: ParsedProviderCall
  readerTurn?: ReaderTurn
}

export function collapseCallAndTurns(items: readonly CallAndTurn[]): CallAndTurn[] {
  if (items.length <= 1) return [...items]

  const byKey = new Map<string, CallAndTurn[]>()
  for (const item of items) {
    if (!isCandidateCollapse(item.call)) continue
    const key = collapseKey(callCounterKey(item.call), item.call.turnId)
    const list = byKey.get(key) ?? []
    list.push(item)
    byKey.set(key, list)
  }

  const dropped = new Set<CallAndTurn>()
  const mergedInto = new Map<CallAndTurn, CallAndTurn>()

  for (const group of byKey.values()) {
    if (group.length <= 1) continue
    const sorted = [...group].sort(
      (a, b) => new Date(a.call.timestamp).getTime() - new Date(b.call.timestamp).getTime(),
    )
    let cluster: CallAndTurn[] = []
    const closeCluster = () => {
      if (cluster.length > 1) {
        const keeper = cluster[0]!
        let current = keeper
        for (let i = 1; i < cluster.length; i++) {
          const donor = cluster[i]!
          dropped.add(donor)
          current = {
            call: mergeParsedCalls(current.call, donor.call),
            readerTurn: mergeReaderTurns(current.readerTurn, donor.readerTurn),
          }
        }
        mergedInto.set(keeper, current)
      }
      cluster = []
    }
    for (const item of sorted) {
      const first = cluster[0]
      const exceedsSpan =
        first !== undefined &&
        Math.abs(new Date(item.call.timestamp).getTime() - new Date(first.call.timestamp).getTime()) >
          TWIN_TURN_MAX_SKEW_MS
      if (exceedsSpan) closeCluster()
      cluster.push(item)
    }
    closeCluster()
  }

  return items.flatMap((item) => {
    if (dropped.has(item)) return []
    const merged = mergedInto.get(item)
    return [merged ?? item]
  })
}

/**
 * The span synthesizer (task 9.1). `synthesize` is the serial reference
 * path; `synthesizeSerial` names it explicitly so the parity test reads as
 * the pair it pins; `synthesizeParallel` models the worker-pool arrival
 * pattern of upstream's cold parse. Both produce the same records in the
 * same order for the same input — one record per call, input order
 * preserved, duplicates left for the store's idempotent upsert to collapse
 * via the shared span id (which is how one session through two paths
 * resolves to one identity, task 9.3).
 */
export class Synthesizer {
  private readonly chunkSize: number
  private readonly conventions: ReadonlyMap<string, TokenConvention>

  constructor(options: SynthesizerOptions = {}) {
    this.chunkSize = Math.max(1, options.chunkSize ?? DEFAULT_CHUNK_SIZE)
    this.conventions = options.conventions ?? PROVIDER_CONVENTIONS
  }

  /**
   * Serial synthesis: one record per call, in input order. When a D5 reader
   * supplied a corresponding turn from the same session file, its content is
   * carried into that record; counters continue to come only from the call.
   */
  synthesize(
    parsedCalls: readonly ParsedProviderCall[],
    readerTurns?: readonly (ReaderTurn | undefined)[],
  ): CanonicalRecord[] {
    const rawItems: CallAndTurn[] = parsedCalls.map((call, index) => ({
      call,
      readerTurn: readerTurns?.[index],
    }))
    const collapsed = collapseCallAndTurns(rawItems)

    const sessionToolResults = new Map<string, ReaderToolResult>()
    for (const item of collapsed) {
      if (item.readerTurn?.toolResults) {
        for (const res of item.readerTurn.toolResults) {
          if (res.toolCallId) {
            sessionToolResults.set(res.toolCallId, res)
          }
        }
      }
    }

    return collapsed.flatMap((item, index) => {
      if (isExcludedHarness(item.call.provider)) return []
      const parent = synthesizeCall(item.call, this.conventions, item.readerTurn, undefined, index)
      const toolRecords = synthesizeToolCalls(parent, item.call, item.readerTurn, sessionToolResults)
      return [parent, ...toolRecords]
    })
  }

  /**
   * Synthesize classified source envelopes. Split-surface harness ids are
   * taken from the envelope; Gemini/Vercel identities are dropped rather than
   * persisted as harnesses. Token validation remains the quarantine seam.
   */
  synthesizeEnvelopes(envelopes: readonly SourceRecordEnvelope[]): CanonicalRecord[] {
    const collapsed = collapseEnvelopeTurns(envelopes)
    const sessionToolResults = new Map<string, ReaderToolResult>()
    for (const envelope of collapsed) {
      if (envelope.readerTurn?.toolResults) {
        for (const res of envelope.readerTurn.toolResults) {
          if (res.toolCallId) {
            sessionToolResults.set(res.toolCallId, res)
          }
        }
      }
    }

    return collapsed.flatMap((envelope, index) => {
      if (isExcludedHarness(envelope.harnessId) || isExcludedHarness(envelope.call.provider)) return []
      const parent = synthesizeCall(envelope.call, this.conventions, envelope.readerTurn, envelope, index)
      const toolRecords = synthesizeToolCalls(parent, envelope.call, envelope.readerTurn, sessionToolResults)
      return [parent, ...toolRecords]
    })
  }

  /** The serial path's explicit name; identical to {@link synthesize}. */
  synthesizeSerial(
    parsedCalls: readonly ParsedProviderCall[],
    readerTurns?: readonly (ReaderTurn | undefined)[],
  ): CanonicalRecord[] {
    return this.synthesize(parsedCalls, readerTurns)
  }

  /**
   * Parallel synthesis, shaped like upstream's cold-parse path: the batch is
   * sliced into chunks, the chunks resolve concurrently (completion order
   * arbitrary, exactly as worker threads finish files out of order), and the
   * results are assembled in submission order — the same contract
   * `parseFilesInOrder` gives upstream's install loop. Deep-equal to
   * `synthesizeSerial` on the same input by construction, because
   * `synthesizeCall` reads one call and no shared state; this method exists
   * so that property stays tested rather than assumed (R1.5).
   */
  async synthesizeParallel(parsedCalls: readonly ParsedProviderCall[]): Promise<CanonicalRecord[]> {
    const chunks = chunk(parsedCalls, this.chunkSize)
    const synthesized = await Promise.all(
      chunks.map((batch) => Promise.resolve().then(() => this.synthesizeSerial(batch))),
    )
    return synthesized.flat()
  }
}
