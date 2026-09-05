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
import { SYNTH_SPAN_PREFIX } from './synth.js'

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
  if (record.spanId.startsWith(SYNTH_SPAN_PREFIX)) return record.traceId

  const sessionId = rawSessionId(record)
  if (sessionId === null) return null
  return `${SYNTH_SPAN_PREFIX}${record.harness}:${sessionId}`
}

/**
 * The session id an OTLP-sourced record carries, read from the same
 * attribute the OTLP path groups sessions by (R2.7). Only a non-empty
 * string claims an identity — missing, empty and structured values claim
 * nothing, and the record passes through uncollapsed rather than being
 * forced under a key the telemetry never stated.
 */
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

function groupByKey(records: readonly CanonicalRecord[]): Map<string, CanonicalRecord[]> {
  const groups = new Map<string, CanonicalRecord[]>()
  for (const record of records) {
    const key = deduplicationKeyFor(record)
    if (key === null) continue
    const group = groups.get(key)
    if (group === undefined) groups.set(key, [record])
    else group.push(record)
  }
  return groups
}

/** Records that claimed no session identity; they pass through uncollapsed. */
function keylessRecords(records: readonly CanonicalRecord[]): CanonicalRecord[] {
  return records.filter((record) => deduplicationKeyFor(record) === null)
}

/**
 * Collapse a session observed through both ingest paths into one identity
 * (R3.1, R3.2): returns the records to store — every single-path session
 * unchanged, and duplicate turns joined under D7. A pair is matched by its
 * position in the shared session stream; unmatched turns remain distinct.
 *
 * Keyless records — a synthesized orphan, an OTLP span that carried no
 * `session.id` — claim no session identity and pass through untouched.
 *
 * Disagreements between the paths are persisted to `store` as
 * `DEDUP_DISAGREEMENT` problems while OTLP remains the accounting authority.
 * The caller persists the returned records through the store's idempotent
 * `upsertMany` (R2.5), whose span-id primary key is the same identity
 * scheme this collapse runs on.
 */
export function deduplicate(
  synthRecords: readonly CanonicalRecord[],
  otlpRecords: readonly CanonicalRecord[],
  store: CanonStore,
): CanonicalRecord[] {
  const fileByKey = groupByKey(synthRecords)
  const otlpByKey = groupByKey(otlpRecords)

  const kept: CanonicalRecord[] = []
  const collapsed = new Set<string>()

  for (const [key, fileRecords] of fileByKey) {
    const otlpRecords = otlpByKey.get(key)
    if (otlpRecords === undefined) {
      kept.push(...fileRecords)
      continue
    }
    collapsed.add(key)
    const sharedTurns = Math.min(fileRecords.length, otlpRecords.length)
    for (let index = 0; index < sharedTurns; index += 1) {
      kept.push(joinOtelAndFileTurn(otlpRecords[index]!, fileRecords[index]!))
    }
    kept.push(...fileRecords.slice(sharedTurns), ...otlpRecords.slice(sharedTurns))
    recordDisagreement(key, fileRecords, otlpRecords, 'otlp', store)
  }

  // Sessions only the OTLP path saw: untouched, in arrival order. Keyless
  // records — a synthesized orphan, an OTLP span that carried no
  // `session.id` — claimed no identity and pass through uncollapsed too.
  for (const [key, otlpRecords] of otlpByKey) {
    if (!collapsed.has(key)) kept.push(...otlpRecords)
  }
  kept.push(...keylessRecords(synthRecords), ...keylessRecords(otlpRecords))

  return kept
}
