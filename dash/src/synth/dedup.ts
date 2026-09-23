// Cross-path deduplication for KyberDash (spec: docs/specs/kyberdash,
// task 9.3; R3.1–R3.3; design.md "Ingest layer"). A harness can describe the
// same session twice — once as session files the upstream parser reads (the
// file path, synthesized by task 9.1) and once as spans it exports to the
// OTLP receiver (the OTLP path, normalized by task 5). Without a collapse
// step the canonical store holds both descriptions and every total doubles.
//
// R3.2 forbids a second deduplication mechanism, so there is no new key
// scheme and no new identifier here. The key is upstream's own cross-provider
// deduplication key — `provider:session:message`, already carried into
// identity by task 9.1 as the `synth:`-namespaced span id and the
// per-session trace id (`traceIdFor`: `synth:<provider>:<session>`). This
// module reads that identity off a synthesized record verbatim, and derives
// the SAME string for an OTLP-sourced record from the same two facts
// upstream's key encodes — the provider (the harness attribution voted at
// normalization) and the session id (the `session.id` span attribute, the
// session identity the OTLP path already groups by). Both paths land on one
// string in one namespace; the store's idempotent upsert keyed on span id
// (R2.5) then persists the survivor.
//
// What "collapse" means (D7): a duplicate turn keeps its counters, cost, and
// identity from OTLP. File content fills the turn only when OTLP supplied no
// structured parts. Values from the two paths are never summed. A disagreement
// is still recorded for audit, but precedence is not a richness contest.

import type { CanonStore, SpanProblem } from '../canon/store.js'
import { contentFromParts, type CanonicalRecord } from '../canon/types.js'
import { DEFAULT_GROUP_ATTRIBUTE } from '../otel/aspire.js'
import { isExcludedHarness, SYNTH_SPAN_PREFIX } from './synth.js'

/** Code stamped on a recorded cross-path value disagreement (R3.3). */
export const DEDUP_DISAGREEMENT = 'DEDUP_DISAGREEMENT'

// ---------------------------------------------------------------------------
// The one key (R3.2: extend upstream's deduplication key, never a second)
// ---------------------------------------------------------------------------

/**
 * The session deduplication key of a canonical record, or null when the
 * record claims no cross-path session identity and can therefore never
 * collapse.
 *
 * A synthesized record's identity IS the extended key — its trace id is
 * `synth:<provider>:<session>` (traceIdFor, task 9.1) — so it is read
 * verbatim rather than re-derived: the record is the identity's owner, and
 * reading it is what guarantees the two paths meet on exactly the identity
 * the synthesizer already emits.
 *
 * An OTLP-sourced record derives the same string from the same two facts:
 * the provider is its voted harness, the session id is the `session.id`
 * span attribute preserved on the record's raw payload the way the adapters
 * store it (`baseRecord` keeps `raw` = the span's attributes). The
 * derivation reuses `traceIdFor`'s exact shape, so both derivations produce
 * one string — there is no second key anywhere in this module.
 */
export function deduplicationKeyFor(record: CanonicalRecord): string | null {
  // Hex OTLP span ids cannot begin with `synth:` ('s' is not a hex digit),
  // so the prefix cleanly separates the two paths' records.
  if (record.spanId.startsWith(SYNTH_SPAN_PREFIX)) {
    // A synthesized record detached from its trace claims no session identity.
    if (record.traceId === null || record.traceId === undefined) return null
    const rest = record.traceId.slice(SYNTH_SPAN_PREFIX.length)
    // `synth:<provider>:<session>`. Only the provider segment is split off:
    // a session id may itself contain a colon (OpenCode's `<db>:<session>`).
    const separator = rest.indexOf(':')
    if (separator === -1) return record.traceId
    return sessionKey(rest.slice(0, separator), rest.slice(separator + 1))
  }

  const sessionId = rawSessionId(record)
  if (sessionId === null) return null
  return sessionKey(record.harness, sessionId)
}

/** Session-level key; split harness ids stay distinct and are never guessed. */
function sessionKey(harness: string, sessionId: string): string {
  return `${SYNTH_SPAN_PREFIX}${harness}:${sessionId}`
}

/**
 * The session id an OTLP-sourced record carries, read from the same
 * attribute the OTLP path groups sessions by (R2.7). Only a non-empty
 * string claims an identity — missing, empty and structured values claim
 * nothing, and the record passes through uncollapsed rather than being
 * forced under a key the telemetry never stated.
 */
type ProvenanceCarrier = {
  provenance?: { nativeRecordId?: unknown }
  turnId?: unknown
  'gen_ai.response.id'?: unknown
  'gen_ai.message.id'?: unknown
}

function nativeRecordIdOf(record: CanonicalRecord): string | null {
  const raw = record.raw
  if (typeof raw !== 'object' || raw === null) return null
  const carrier = raw as ProvenanceCarrier
  if (typeof carrier.provenance?.nativeRecordId === 'string' && carrier.provenance.nativeRecordId !== '') {
    return carrier.provenance.nativeRecordId
  }
  if (typeof carrier.turnId === 'string' && carrier.turnId !== '') return carrier.turnId
  if (typeof carrier['gen_ai.response.id'] === 'string' && carrier['gen_ai.response.id'] !== '') {
    return carrier['gen_ai.response.id']
  }
  if (typeof carrier['gen_ai.message.id'] === 'string' && carrier['gen_ai.message.id'] !== '') {
    return carrier['gen_ai.message.id']
  }
  return null
}

function sessionIdOf(record: CanonicalRecord): string | null {
  if (typeof record.sessionId === 'string' && record.sessionId !== '') return record.sessionId
  return rawSessionId(record)
}

/**
 * Cross-path join requires the same classified harness, native session, and
 * native turn/message id. Position in either array is not evidence.
 */
export function turnJoinKeyFor(record: CanonicalRecord): string | null {
  if (isExcludedHarness(record.harness)) return null
  const sessionId = sessionIdOf(record)
  const nativeRecordId = nativeRecordIdOf(record)
  if (sessionId === null || nativeRecordId === null) return null
  return `${record.harness}:${sessionId}:${nativeRecordId}`
}

function rawSessionId(record: CanonicalRecord): string | null {
  const raw = record.raw
  if (typeof raw !== 'object' || raw === null) return null
  const value = (raw as Record<string, unknown>)[DEFAULT_GROUP_ATTRIBUTE]
  if (typeof value !== 'string' || value.length === 0) return null
  return value
}

// ---------------------------------------------------------------------------
// Disagreement (R3.3: record it, never discard it)
// ---------------------------------------------------------------------------

/** The counted values one path contributes for one session key. */
type SideSummary = {
  turns: number
  firstSpanId: string
  freshInput: number
  cacheRead: number
  cacheCreation: number
  output: number
  /** Null when no record on the side carries a reasoning counter at all. */
  reasoning: number | null
  reportedInput: number
  reportedOutput: number
  costValue: number
  costStatuses: Set<string>
}

function summarize(records: readonly CanonicalRecord[]): SideSummary {
  const summary: SideSummary = {
    turns: records.length,
    firstSpanId: records[0]?.spanId ?? '',
    freshInput: 0,
    cacheRead: 0,
    cacheCreation: 0,
    output: 0,
    reasoning: null,
    reportedInput: 0,
    reportedOutput: 0,
    costValue: 0,
    costStatuses: new Set(),
  }
  for (const record of records) {
    summary.freshInput += record.tokens.freshInput
    summary.cacheRead += record.tokens.cacheRead
    summary.cacheCreation += record.tokens.cacheCreation
    summary.output += record.tokens.output
    if (record.tokens.reasoning !== undefined) {
      summary.reasoning = (summary.reasoning ?? 0) + record.tokens.reasoning
    }
    summary.reportedInput += record.tokens.reportedInput
    summary.reportedOutput += record.tokens.reportedOutput
    if (record.cost.value !== undefined) summary.costValue += record.cost.value
    summary.costStatuses.add(record.cost.status)
  }
  return summary
}

/** The first value the two sides disagree on, phrased for a problem message. */
function firstDisagreement(file: SideSummary, otlp: SideSummary): string | null {
  if (file.turns !== otlp.turns) return `turn count (${file.turns} vs ${otlp.turns})`
  if (file.freshInput !== otlp.freshInput) {
    return `fresh input (${file.freshInput} vs ${otlp.freshInput})`
  }
  if (file.cacheRead !== otlp.cacheRead) {
    return `cache-read input (${file.cacheRead} vs ${otlp.cacheRead})`
  }
  if (file.cacheCreation !== otlp.cacheCreation) {
    return `cache-creation input (${file.cacheCreation} vs ${otlp.cacheCreation})`
  }
  if (file.output !== otlp.output) return `output tokens (${file.output} vs ${otlp.output})`
  if (file.reasoning !== otlp.reasoning) {
    return `reasoning tokens (${file.reasoning ?? 'absent'} vs ${otlp.reasoning ?? 'absent'})`
  }
  if (file.reportedInput !== otlp.reportedInput) {
    return `reported input (${file.reportedInput} vs ${otlp.reportedInput})`
  }
  if (file.reportedOutput !== otlp.reportedOutput) {
    return `reported output (${file.reportedOutput} vs ${otlp.reportedOutput})`
  }
  if (file.costValue !== otlp.costValue) {
    return `cost (${file.costValue} vs ${otlp.costValue})`
  }
  const fileStatuses = [...file.costStatuses].sort().join('+') || 'none'
  const otlpStatuses = [...otlp.costStatuses].sort().join('+') || 'none'
  if (fileStatuses !== otlpStatuses) return `cost status (${fileStatuses} vs ${otlpStatuses})`
  return null
}

/** One side's figures, for the problem message. Numbers and ids only — never content. */
function describeSide(side: string, summary: SideSummary): string {
  const statuses = [...summary.costStatuses].sort().join('+') || 'none'
  return (
    `${side} path reported ${summary.turns} turn(s), ` +
    `freshInput=${summary.freshInput}, cacheRead=${summary.cacheRead}, ` +
    `cacheCreation=${summary.cacheCreation}, output=${summary.output}, ` +
    `reasoning=${summary.reasoning ?? 'absent'}, reportedInput=${summary.reportedInput}, ` +
    `reportedOutput=${summary.reportedOutput}, cost=${summary.costValue} (${statuses})`
  )
}

/**
 * Persist the two paths' disagreement over one session (R3.3) — both sides'
 * figures go into the problem message, so discarding the poorer side's
 * records loses nothing that cannot be audited. Agreement collapses
 * silently: a problem would be noise about a non-event. Severity is
 * `warning`, not the `error` of a validation failure (R4.4) — nothing was
 * rejected; a choice between two descriptions was made and recorded.
 */
function recordDisagreement(
  key: string,
  fileRecords: readonly CanonicalRecord[],
  otlpRecords: readonly CanonicalRecord[],
  winner: 'otlp',
  store: CanonStore,
): void {
  const file = summarize(fileRecords)
  const otlp = summarize(otlpRecords)
  const what = firstDisagreement(file, otlp)
  if (what === null) return

  const kept = otlp
  const problem: SpanProblem = {
    spanId: kept.firstSpanId,
    severity: 'warning',
    code: DEDUP_DISAGREEMENT,
    message:
      `session ${key} arrived through both ingest paths and they disagree on ${what}. ` +
      `${describeSide('file', file)}. ${describeSide('OTLP', otlp)}. ` +
      `kept the D7-preferred ${winner.toUpperCase()} path's counters (and file content only when OTLP had no parts; ` +
      `${kept.turns} record(s), first span ${kept.firstSpanId}); ` +
      `the other path's figures are recorded here rather than stored`,
    location: key,
  }
  store.recordProblem(problem)
}

// ---------------------------------------------------------------------------
// The collapse
// ---------------------------------------------------------------------------

/**
 * D7's per-turn source join. OTLP is the accounting authority; a file can
 * supply structured content only for a turn whose OTLP row had no parts.
 * Rebuilding the flat content from those parts prevents the legacy map from
 * disagreeing with the structured view consumed by context analysis.
 */
export function joinOtelAndFileTurn(
  otlpRecord: CanonicalRecord,
  fileRecord: CanonicalRecord,
): CanonicalRecord {
  if (otlpRecord.parts !== undefined && otlpRecord.parts.length > 0) return otlpRecord
  if (fileRecord.parts === undefined || fileRecord.parts.length === 0) return otlpRecord
  return {
    ...otlpRecord,
    parts: fileRecord.parts,
    content: contentFromParts(fileRecord.parts),
  }
}

/**
 * Collapse turns observed through both ingest paths (R3.1, R3.2): a pair
 * matches only when classified harness, native session, and native turn id
 * agree. Array position is not a join key. Gemini rows are dropped rather
 * than stored as a harness. Unmatched turns remain distinct.
 *
 * Disagreements between the paths are persisted to `store` as
 * `DEDUP_DISAGREEMENT` problems while OTLP remains the accounting authority.
 */
export function deduplicate(
  synthRecords: readonly CanonicalRecord[],
  otlpRecords: readonly CanonicalRecord[],
  store: CanonStore,
): CanonicalRecord[] {
  const files = synthRecords.filter((record) => !isExcludedHarness(record.harness))
  const otel = otlpRecords.filter((record) => !isExcludedHarness(record.harness))

  const fileByTurn = new Map<string, CanonicalRecord>()
  const unmatchedFile: CanonicalRecord[] = []
  for (const record of files) {
    const key = turnJoinKeyFor(record)
    if (key === null) {
      unmatchedFile.push(record)
      continue
    }
    fileByTurn.set(key, record)
  }

  const kept: CanonicalRecord[] = []
  const joinedFileKeys = new Set<string>()
  for (const otlp of otel) {
    const key = turnJoinKeyFor(otlp)
    const file = key === null ? undefined : fileByTurn.get(key)
    if (file === undefined || key === null) {
      kept.push(otlp)
      continue
    }
    joinedFileKeys.add(key)
    kept.push(joinOtelAndFileTurn(otlp, file))
    recordDisagreement(key, [file], [otlp], 'otlp', store)
  }

  for (const [key, file] of fileByTurn) {
    if (!joinedFileKeys.has(key)) kept.push(file)
  }
  kept.push(...unmatchedFile)
  return kept
}
