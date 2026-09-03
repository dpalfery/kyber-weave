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

import type { ParsedProviderCall } from '../../src/providers/types.js'
import type { CanonicalRecord, CostBlock, TokenUsage } from '../canon/types.js'
import { exclusiveConvention, inclusiveConvention } from '../canon/adapters/copilot.js'
import { FILE_SOURCE_PREFIX, measurabilityFor } from '../canon/measurability.js'

// ---------------------------------------------------------------------------
// Identity scheme (R3.2: extend upstream's key, don't add a mechanism)
// ---------------------------------------------------------------------------

/** Namespace for span ids derived from upstream's deduplication key. */
export const SYNTH_SPAN_PREFIX = 'synth:'

/** The trace a synthesized session's calls form: one per (provider, session). */
export function traceIdFor(call: ParsedProviderCall): string {
  return `${SYNTH_SPAN_PREFIX}${call.provider}:${call.sessionId}`
}

/**
 * The synthesized span id: upstream's cross-provider deduplication key, kept
 * verbatim under the `synth:` namespace. The key already encodes provider,
 * session and message identity — that is the whole point of reusing it.
 */
export function spanIdFor(call: ParsedProviderCall): string {
  return `${SYNTH_SPAN_PREFIX}${call.deduplicationKey}`
}

/**
 * The telemetry source name stamped on synthesized records. Identifies the
 * ingest path instance (the vendored parser reading this provider's files);
 * attribution never reads it (R6.2) — for a file-sourced record the harness
 * is the provider by construction, not a claim to be voted on.
 */
export function sourceFor(call: ParsedProviderCall): string {
  return `${FILE_SOURCE_PREFIX}${call.provider}`
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
      basis: call.costIsEstimated === true ? 'published' : 'harness',
      status: 'priced',
      value: call.costUSD,
      currency: 'USD',
      byModel: { [call.model]: call.costUSD },
    }
  }
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

  return {
    spanId: spanIdFor(call),
    traceId: traceIdFor(call),
    parentSpanId: null,
    source: sourceFor(call),
    harness: call.provider,
    name: `${call.provider}:${call.model}`,
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: call.timestamp,
    durationMs: call.activeDurationMs ?? 0,
    status: 'unspecified',
    tokens,
    content: {},
    cost: costBlockFor(call),
    measurability: measurabilityFor(call.provider),
    raw: call,
  }
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

  /** Serial synthesis: one record per call, in input order. */
  synthesize(parsedCalls: readonly ParsedProviderCall[]): CanonicalRecord[] {
    return parsedCalls.map((call) => synthesizeCall(call, this.conventions))
  }

  /** The serial path's explicit name; identical to {@link synthesize}. */
  synthesizeSerial(parsedCalls: readonly ParsedProviderCall[]): CanonicalRecord[] {
    return this.synthesize(parsedCalls)
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
