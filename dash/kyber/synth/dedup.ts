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
// What "collapse" means (R3.1, R3.3): a session whose key arrives from both
// paths keeps ONE side's records — the richer source's — and the other
// side's description of that session is not stored, so its turns, tokens and
// cost are never counted twice. Richness is populated information: token
// fields, canonical content keys, priced cost, raw payload size — compared
// in that order so the judgement is a total order and deterministic. On any
// disagreement of value between the two paths — turn counts, token class
// sums, cost — a `DEDUP_DISAGREEMENT` problem recording both sides' figures
// is persisted before the loser is discarded (R3.3): surfaced, not silently
// dropped. A session seen by only one path passes through untouched, and
// records within one path are never collapsed against each other — a
// session's turns are distinct work, not duplicates.

import type { CanonStore, SpanProblem } from '../canon/store.js'
import type { CanonicalRecord, TokenUsage } from '../canon/types.js'
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
// Richness (R3.3: prefer the richer source)
// ---------------------------------------------------------------------------

/**
 * How much information one side's description of a session carries, summed
 * over its records and compared as a tuple on the axes R3.3 names: populated
 * token fields, then populated canonical content keys, then priced cost,
 * then raw payload bytes. The first axis that differs decides; a tie keeps
 * the file path's records — the upstream-native accounting — which also
 * makes the outcome deterministic.
 */
type Richness = {
  tokenFields: number
  contentKeys: number
  pricedCost: number
  rawBytes: number
}

function richnessOf(records: readonly CanonicalRecord[]): Richness {
  const total = { tokenFields: 0, contentKeys: 0, pricedCost: 0, rawBytes: 0 }
  for (const record of records) {
    total.tokenFields += populatedTokenFields(record.tokens)
    total.contentKeys += populatedContentKeys(record.content)
    total.pricedCost += record.cost.status === 'priced' ? 1 : 0
    total.rawBytes += rawBytesOf(record.raw)
  }
  return total
}

/** Populated token counters: each non-zero class and reported total, plus reasoning. */
function populatedTokenFields(tokens: TokenUsage): number {
  let fields = 0
  for (const value of [
    tokens.freshInput,
    tokens.cacheRead,
    tokens.cacheCreation,
    tokens.output,
    tokens.reportedInput,
    tokens.reportedOutput,
  ]) {
    if (value > 0) fields += 1
  }
  if (tokens.reasoning !== undefined && tokens.reasoning > 0) fields += 1
  return fields
}

function populatedContentKeys(content: CanonicalRecord['content']): number {
  return Object.values(content).filter(
    (value) => typeof value === 'string' && value.length > 0,
  ).length
}

function rawBytesOf(raw: unknown): number {
  return raw === undefined ? 0 : JSON.stringify(raw).length
}

/** Positive when `a` is the richer side, negative when `b` is, zero when tied. */
function compareRichness(a: Richness, b: Richness): number {
  return (
    a.tokenFields - b.tokenFields ||
    a.contentKeys - b.contentKeys ||
    a.pricedCost - b.pricedCost ||
    a.rawBytes - b.rawBytes
  )
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
  winner: 'file' | 'otlp',
  store: CanonStore,
): void {
  const file = summarize(fileRecords)
  const otlp = summarize(otlpRecords)
  const what = firstDisagreement(file, otlp)
  if (what === null) return

  const kept = winner === 'file' ? file : otlp
  const problem: SpanProblem = {
    spanId: kept.firstSpanId,
    severity: 'warning',
    code: DEDUP_DISAGREEMENT,
    message:
      `session ${key} arrived through both ingest paths and they disagree on ${what}. ` +
      `${describeSide('file', file)}. ${describeSide('OTLP', otlp)}. ` +
      `kept the richer source (${winner === 'file' ? 'file' : 'OTLP'} path, ` +
      `${kept.turns} record(s), first span ${kept.firstSpanId}); ` +
      `the other path's figures are recorded here rather than stored`,
    location: key,
  }
  store.recordProblem(problem)
}

// ---------------------------------------------------------------------------
// The collapse
// ---------------------------------------------------------------------------

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
 * (R3.1, R3.2): returns the records to store — every record of a
 * single-path session unchanged, and for a both-paths session exactly one
 * side's records, the richer source's (R3.3). Within one path nothing ever
 * collapses: a session's turns are distinct work and must each count once.
 *
 * Keyless records — a synthesized orphan, an OTLP span that carried no
 * `session.id` — claim no session identity and pass through untouched.
 *
 * Disagreements between the paths are persisted to `store` as
 * `DEDUP_DISAGREEMENT` problems before the poorer side is discarded (R3.3).
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
    const winner =
      compareRichness(richnessOf(fileRecords), richnessOf(otlpRecords)) >= 0 ? 'file' : 'otlp'
    kept.push(...(winner === 'file' ? fileRecords : otlpRecords))
    recordDisagreement(key, fileRecords, otlpRecords, winner, store)
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
