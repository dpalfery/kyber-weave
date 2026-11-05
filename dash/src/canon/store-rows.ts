// The canonical store's row shapes and the mappers between SQLite rows and the
// domain types. Split from `./store.ts`, which re-exports every name here that
// it exported before.

import { QUARANTINE_UNKNOWN_SOURCES, sourceSpellingsForHarness } from './measurability.js'
import { deflateSync, inflateSync } from 'node:zlib'
import { createHash } from 'node:crypto'
import { contentFromParts } from './types.js'
import type { OtlpMetric, OtlpScope } from '../otel/receiver.js'
import { decompressColumn } from './compressed-column.js'
import { parseJsonObject } from './session-read.js'
import type { CanonicalRecord, ContentPart, CostBlock, ExecutionRow, HarnessRollupRow, Measurability, MetricAvailability, Problem, RunGroupingBasis, RunRow, SessionRow, TokenUsage } from './types.js'
import type { DetectorId, Finding, FindingConfidence, FindingEvidenceLink } from '../analysis/findings.js'
import { type PredictionRecord } from '../analysis/calibration.js'
type Database = import('node:sqlite').DatabaseSync

/** A problem pinned to the record it belongs to (the `problems` table row). */
export type SpanProblem = Problem & {
  spanId: string
  sessionId?: string | null
  harness?: string | null
  timestamp?: string | null
  /**
   * The exception stack when the problem came from a throw (v18+ rows). Null/absent
   * otherwise — never synthesized. A validation problem that was computed, not thrown,
   * has a message but no stack, and must not claim one.
   */
  stack?: string | null
}

/** One entry of the ingest audit log. */
export type IngestLogEntry = {
  source: string
  count: number
  timestamp: string
}

/**
 * One persisted metric point: what a decoded OTLP data point became.
 *
 * A field the point did not carry reads absent -- null, never a zero. A point
 * that reported no value of its own is a fact about the measurement, not a
 * measured zero (docs/rules/honest-unobservability.md).
 */
export type MetricPointRow = {
  /** The metric's name, as the exporter sent it. */
  name: string
  /** The data shape the metric carried: `gauge`, `sum`, `histogram`, … */
  kind: string
  /** The metric's unit; null when the exporter named none. */
  unit: string | null
  /** The aggregation temporality the shape declared (1 delta, 2 cumulative); null when it declared none. */
  aggregationTemporality: number | null
  /** The point's timestamp as an ISO instant; null when the point carried none. */
  timestamp: string | null
  /** The point's timestamp in Unix nanoseconds, verbatim; null when it carried none. */
  timeUnixNano: string | null
  /**
   * The start of the interval the point's measurement covers, in Unix
   * nanoseconds, verbatim; null when the point declared none.
   *
   * The interval is part of the point's IDENTITY: two points that differ only
   * in the window they cover are two measurements, not one measurement seen
   * twice, so the interval travels in the point's digest and lands on the row.
   */
  startTimeUnixNano: string | null
  /** The point's own value, for a gauge or a sum; null when the point carried none. */
  value: number | null
  /** The point's count, for a histogram or a summary; null when the point carried none. */
  count: number | null
  /** The point's sum, for a histogram or a summary; null when the point carried none. */
  sum: number | null
  /** The point's own attributes. */
  attributes: Record<string, unknown>
  /** The resource attributes of the emitting process. */
  resource: Record<string, unknown>
  /** The instrumentation scope that produced the point. */
  scope: OtlpScope
  /** The exporter's `service.name`; null when the exporter named none. */
  source: string | null
  /** Always null: a metric point has no span, so it names no harness. */
  harness: null
  /** Always null: a metric point carries no adapter fingerprint. */
  adapterFingerprint: null
}

/**
 * A metric point on its way to a row: the column values the write statement
 * binds, plus the digest that identifies the point.
 */
export type MetricPointDraft = {
  pointKey: string
  name: string
  kind: string
  unit: string | null
  aggregationTemporality: number | null
  timeUnixNano: string | null
  /**
   * The start of the interval the point's measurement covers; null when the
   * point declared none. It is part of the point's identity, so it travels in
   * the digest and lands on the row.
   */
  startTimeUnixNano: string | null
  timestamp: string | null
  value: number | null
  count: number | null
  sum: number | null
  attributesJson: string
  resourceJson: string
  scopeJson: string
  source: string | null
}

/**
 * A JSON rendering whose object keys are in a stable order.
 *
 * The point's digest is what identifies the point, so two deliveries of the
 * same point must digest the same however the exporter ordered its
 * attributes. Key order carries no meaning in an attribute map, so it is
 * normalized away here rather than trusted.
 */
export function stableJson(value: unknown): string {
  if (value === null || typeof value !== 'object') {
    return JSON.stringify(value) ?? 'null'
  }
  if (Array.isArray(value)) return `[${value.map((item) => stableJson(item)).join(',')}]`
  const entries = Object.entries(value as Record<string, unknown>)
    .sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0))
    .map(([key, item]) => `${JSON.stringify(key)}:${stableJson(item)}`)
  return `{${entries.join(',')}}`
}

/**
 * The instant a metric point's nanosecond timestamp denotes, as an ISO
 * string, or null when the point carried no timestamp or one that is not a
 * nanosecond count.
 *
 * The verbatim nanoseconds are stored beside it, so the sub-millisecond part
 * of the timestamp survives even where the ISO reading cannot carry it.
 */
export function metricInstant(nanos: string | null): string | null {
  if (nanos === null) return null
  let millis: number
  try {
    millis = Number(BigInt(nanos) / 1_000_000n)
  } catch {
    // A timestamp that is not a nanosecond count has no instant to derive.
    // The row keeps the value it arrived with and no reading of it.
    return null
  }
  const instant = new Date(millis)
  return Number.isNaN(instant.getTime()) ? null : instant.toISOString()
}

/**
 * The digest that identifies a metric point: everything the row carries, in
 * the stable rendering above.
 *
 * A metric point has no span id, so there is no exporter-supplied identity to
 * key on. The content digest is the identity: a point re-delivered lands on
 * the row it already occupies instead of beside it, which is what makes the
 * write an upsert rather than an append.
 *
 * The interval the point's measurement covers is part of that digest, and it
 * has to be: the same name, kind, unit, temporality, observation time, value
 * and attributes over a DIFFERENT window is a different measurement, and
 * without the interval in the digest the two collided onto one row and the
 * second upserted onto the first.
 */
export function metricPointKey(draft: Omit<MetricPointDraft, 'pointKey'>): string {
  return createHash('sha256')
    .update(
      stableJson([
        draft.name,
        draft.kind,
        draft.unit,
        draft.aggregationTemporality,
        draft.timeUnixNano,
        draft.startTimeUnixNano,
        draft.value,
        draft.count,
        draft.sum,
        draft.attributesJson,
        draft.resourceJson,
        draft.scopeJson,
        draft.source,
      ]),
      'utf8',
    )
    .digest('hex')
}

/** The rows one decoded metric contributes: one per data point it carried. */
export function metricPointDrafts(metrics: readonly OtlpMetric[]): MetricPointDraft[] {
  const drafts: MetricPointDraft[] = []
  for (const metric of metrics) {
    const resourceJson = stableJson(metric.resource)
    const scopeJson = stableJson(metric.scope)
    const serviceName = metric.resource['service.name']
    // The exporter's service.name is the row's source. It is a source, not an
    // attribution: the row names no harness, because a metric point has no
    // span to attribute.
    const source = typeof serviceName === 'string' && serviceName !== '' ? serviceName : null
    const unit = metric.unit ?? null
    const aggregationTemporality = metric.aggregationTemporality ?? null
    for (const point of metric.dataPoints) {
      const withoutKey: Omit<MetricPointDraft, 'pointKey'> = {
        name: metric.name,
        kind: metric.kind,
        unit,
        aggregationTemporality,
        timeUnixNano: point.timeUnixNano,
        // The interval the measurement covers is part of the point, so it is
        // part of the point's identity. A point that declared none carries
        // none: the absence is the fact.
        startTimeUnixNano: point.startTimeUnixNano,
        timestamp: metricInstant(point.timeUnixNano),
        // A point that reported no value keeps it absent: the absence is the
        // fact, and it must never read as a measured zero.
        value: point.value,
        count: point.count,
        sum: point.sum,
        attributesJson: stableJson(point.attributes),
        resourceJson,
        scopeJson,
        source,
      }
      drafts.push({ ...withoutKey, pointKey: metricPointKey(withoutKey) })
    }
  }
  return drafts
}

/** A stored number, or null when the row carried none. */
export function metricNumber(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null
  if (typeof value === 'bigint') return Number(value)
  return null
}

/** A stored text, or null when the row carried none. */
export function metricText(value: unknown): string | null {
  if (typeof value === 'string') return value
  if (typeof value === 'number' || typeof value === 'bigint') return String(value)
  return null
}

/** The instrumentation scope a row carried, as the receiver's shape. */
export function metricScope(value: unknown): OtlpScope {
  const parsed = parseJsonObject(value)
  if (parsed === undefined) return {}
  const scope: OtlpScope = {}
  if (typeof parsed.name === 'string') scope.name = parsed.name
  if (typeof parsed.version === 'string') scope.version = parsed.version
  return scope
}

/** One stored metric row, as the read-back accessor hands it over. */
export function toMetricPointRow(row: Record<string, unknown>): MetricPointRow {
  return {
    name: String(row.name),
    kind: String(row.kind),
    unit: metricText(row.unit),
    aggregationTemporality: metricNumber(row.aggregation_temporality),
    timestamp: metricText(row.timestamp),
    timeUnixNano: metricText(row.time_unix_nano),
    startTimeUnixNano: metricText(row.start_time_unix_nano),
    value: metricNumber(row.value),
    count: metricNumber(row.point_count),
    sum: metricNumber(row.point_sum),
    attributes: parseJsonObject(row.attributes_json) ?? {},
    resource: parseJsonObject(row.resource_json) ?? {},
    scope: metricScope(row.scope_json),
    source: metricText(row.source),
    // A metric point has no span: it names no harness and carries no adapter
    // fingerprint. The absence is stated rather than left implicit, so a
    // reader cannot mistake an unattributed row for an attributed one.
    harness: null,
    adapterFingerprint: null,
  }
}

/** One quarantined span: held back from the corpus, with the reason why. */
export type QuarantineEntry = {
  spanId: string
  namespaces: string[]
  reason: string
  source?: string
  name?: string
  timestamp?: string
  /** The harness attribution named for the row; absent on rows that predate v19. */
  harness?: string | null
  /**
   * The exact incoming span JSON stored at quarantine time (v18+ rows). Absent for
   * older rows — unknown, never reconstructed from the records table (that row, when
   * one exists, is the normalized record, not the incoming payload, and mislabeling
   * it as the original message is forbidden). The sender's decoded packet, kept
   * beside the audit row as evidence (B7).
   */
  rawPayload?: string | null
}

/**
 * One quarantine row in the shape the detail route serves (plan 2026-10-10,
 * B7). Unlike `QuarantineEntry` every field is always present — the route's
 * contract is explicit null for "row has none", because a dropped key would
 * read as "field unknown" rather than "no evidence". `evidenceAvailable` is
 * the honest answer to "did this row keep a packet", never inferred.
 */
export type QuarantineEntryDetail = {
  spanId: string
  source: string | null
  name: string | null
  reason: string
  timestamp: string | null
  namespaces: string[]
  harness: string | null
  rawPayload: string | null
  evidenceAvailable: boolean
}

/** Audit metadata a quarantine row can carry beyond the reason. */
export type QuarantineMetadata = {
  source?: string
  name?: string
  timestamp?: string
  /** The sender's decoded packet, kept beside the audit row as evidence (B7). */
  rawPayload?: string
  /** The harness attribution named, so a per-harness wipe can scope the row. */
  harness?: string
}

export type QuarantinedLog = {
  logId: string
  reason: string
  log: unknown
  /** The harness tag the row was written with (B3/D-Q3); absent on rows that predate v19. */
  harness?: string
}

/** What the request-body matcher needs of a model call: no content, no raw. */
export type ModelCallCandidate = {
  spanId: string
  timestamp: string
  /** Null only if a legacy row stored none; the matcher treats that as unknown. */
  durationMs: number | null
}

export type CandidateRow = { span_id: unknown; timestamp: unknown; duration_ms: unknown }

export function toCandidate(row: CandidateRow): ModelCallCandidate {
  return {
    spanId: String(row.span_id),
    timestamp: String(row.timestamp),
    durationMs: typeof row.duration_ms === 'number' ? row.duration_ms : null,
  }
}

export type RecordRow = {
  span_id: unknown
  trace_id: unknown
  parent_span_id: unknown
  source: unknown
  harness: unknown
  session_id: unknown
  name: unknown
  op: unknown
  kind: unknown
  timestamp: unknown
  duration_ms: unknown
  status: unknown
  tokens_json: unknown
  content_json: unknown
  cost_json: unknown
  measurability_json: unknown
  parts_json: unknown
  raw: unknown
}

/**
 * Deflate the raw payload into the bytes the store keeps (R12.4). JSON
 * telemetry is highly repetitive — attribute names, prompt scaffolding — so
 * this is where the 78 KB/span floor collapses.
 */
export function compressRaw(raw: unknown): Uint8Array {
  return deflateSync(Buffer.from(JSON.stringify(raw), 'utf8'))
}

/** Inverse of `compressRaw`; the caller parses the JSON. */
export function decompressRaw(blob: Uint8Array): unknown {
  return JSON.parse(inflateSync(Buffer.from(blob)).toString('utf8'))
}

/**
 * Session-scope system-prompt texts hiding in one records row's content
 * columns, without touching anything else on the row.
 *
 * <remarks>
 * Why this shape: the turn-content fallback only needs `system_prompt`
 * texts, so it must not pay for full records. `content_json` is plain text
 * and filters with a parse; `parts_json` is deflated with no expression
 * index into it, so a parts-carrying row still inflates to filter — a true
 * indexed pre-filter would need a schema migration (a generated column over
 * the inflated parts), deliberately out of scope. Semantics match the
 * bridge's `partsFromRow`: parts win when present and non-empty (even when
 * `content_json` carries something else), otherwise `content_json` decides.
 * Both callers (the store method below and the store-less bridge path) read
 * through here so the two seams cannot diverge.
 *
 * The parts are read from either stored form — the deflated one, and the
 * plain JSON a row written before the compression landed holds — so a
 * pre-change row keeps the system prompts it holds instead of falling
 * through to a `content_json` that is '{}' on a parts-carrying row.
 * </remarks>
 */
export function systemPromptTextsFromContentColumns(
  contentJson: unknown,
  partsJson: unknown,
): string[] {
  if (partsJson !== null && partsJson !== undefined) {
    const parts = decompressColumn(partsJson)
    if (Array.isArray(parts) && parts.length > 0) {
      return [...parts]
        .sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
        .filter((part) => part.part === 'system_prompt' && typeof part.text === 'string' && part.text !== '')
        .map((part) => part.text)
    }
  }
  if (typeof contentJson === 'string' && contentJson !== '' && contentJson !== '{}') {
    try {
      const content = JSON.parse(contentJson) as Partial<Record<string, unknown>>
      const text = content.system_prompt
      if (typeof text === 'string' && text !== '') return [text]
    } catch {
      return []
    }
  }
  return []
}

export function text(value: unknown): string {
  return value as string
}

/**
 * How many rows a write statement changed. node:sqlite reports `changes` as
 * a number or a bigint depending on the build; anything else (a driver that
 * reports nothing) reads as zero so a conditional write never claims a win
 * it cannot prove.
 */
export function metadataChangeCount(info: { changes?: unknown }): number {
  if (typeof info.changes === 'number') return info.changes
  if (typeof info.changes === 'bigint') return Number(info.changes)
  return 0
}

/**
 * Map a problems row out of SQLite's column names. `stack` is present only when the row
 * stored one; a computed validation problem has a message and no stack.
 */
export function toSpanProblem(row: {
  span_id: unknown
  severity: unknown
  code: unknown
  message: unknown
  location: unknown
  session_id: unknown
  harness: unknown
  timestamp: unknown
  stack: unknown
}): SpanProblem {
  const p: SpanProblem = {
    spanId: text(row.span_id),
    severity: text(row.severity) as Problem['severity'],
    code: text(row.code),
    message: text(row.message),
  }
  if (row.location !== null && row.location !== undefined) {
    p.location = text(row.location)
  }
  if (row.session_id !== null && row.session_id !== undefined) {
    p.sessionId = text(row.session_id)
  }
  if (row.harness !== null && row.harness !== undefined) {
    p.harness = text(row.harness)
  }
  if (row.timestamp !== null && row.timestamp !== undefined) {
    p.timestamp = text(row.timestamp)
  }
  if (row.stack !== null && row.stack !== undefined) {
    p.stack = text(row.stack)
  }
  return p
}

export type QuarantineRowShape = {
  span_id: unknown
  namespaces: unknown
  reason: unknown
  source: unknown
  name: unknown
  timestamp: unknown
  raw_payload: unknown
}

/**
 * Map a quarantine row out of SQLite's column names. `raw_payload` is present only when
 * the row stored one (v18+ writes); older rows read without it — absent, never invented.
 */
export function toQuarantineEntry(row: QuarantineRowShape): QuarantineEntry {
  const entry: QuarantineEntry = {
    spanId: text(row.span_id),
    namespaces: JSON.parse(text(row.namespaces)) as string[],
    reason: text(row.reason),
  }
  if (row.source !== null && row.source !== undefined) {
    entry.source = text(row.source)
  }
  if (row.name !== null && row.name !== undefined) {
    entry.name = text(row.name)
  }
  if (row.timestamp !== null && row.timestamp !== undefined) {
    entry.timestamp = text(row.timestamp)
  }
  if (row.raw_payload !== null && row.raw_payload !== undefined) {
    entry.rawPayload = text(row.raw_payload)
  }
  return entry
}

/**
 * The `WHERE` fragment scoping quarantine rows to one harness filter, with its bound
 * parameters. `undefined` is unfiltered. `'unknown'` matches unattributed sources —
 * null, blank, or the neutral grouping keys — and a canonical id matches every stored
 * spelling normalizing to it (case-insensitive), so twin front-ends scope together.
 */
export function quarantineHarnessWhere(harness: string | undefined): { where: string; params: Array<string | number> } {
  if (harness === undefined) return { where: '', params: [] }
  if (harness === 'unknown') {
    const keys = QUARANTINE_UNKNOWN_SOURCES.map((key) => key.toLowerCase())
    const placeholders = keys.map(() => '?').join(', ')
    return {
      where: ` WHERE source IS NULL OR TRIM(COALESCE(source, '')) = '' OR LOWER(source) IN (${placeholders})`,
      params: keys,
    }
  }
  const spellings = sourceSpellingsForHarness(harness)
  // An id with no known spelling matches nothing; `IN ()` is not portable SQL.
  if (spellings.length === 0) return { where: ' WHERE 1 = 0', params: [] }
  const placeholders = spellings.map(() => '?').join(', ')
  return { where: ` WHERE LOWER(source) IN (${placeholders})`, params: spellings }
}

/**
 * The `WHERE` fragment scoping problem rows to one harness filter. Problems carry the
 * attributed harness id, matched case-insensitively against the same spelling expansion
 * the quarantine filter uses (so `?harness=copilot-chat` finds the twin front-end's rows
 * in both lists); `unknown` is a null or blank harness.
 */
export function problemsHarnessWhere(harness: string | undefined): { where: string; params: Array<string | number> } {
  if (harness === undefined) return { where: '', params: [] }
  if (harness === 'unknown') {
    return { where: ` WHERE harness IS NULL OR TRIM(COALESCE(harness, '')) = ''`, params: [] }
  }
  const spellings = new Set([harness.toLowerCase(), ...sourceSpellingsForHarness(harness)])
  const placeholders = [...spellings].map(() => '?').join(', ')
  return { where: ` WHERE LOWER(harness) IN (${placeholders})`, params: [...spellings] }
}

export function nullableText(value: unknown): string | null {
  return (value as string | null) ?? null
}

/**
 * Install a memoized property computed on first read.
 *
 * Reading the corpus is what a rebuild spends its time on, and each pass over
 * it wants a different handful of fields: grouping spans into runs needs
 * `raw`'s attributes and never looks at the message parts, while the session
 * builder needs the parts and tokenizes them. Inflating `parts_json` for a
 * pass that discards it measured 13 of the 21 seconds each pass cost, and the
 * record objects built around it are what took the rebuild's heap high-water
 * mark to 3.5 GB.
 *
 * The property stays enumerable, configurable and writable, so a record is
 * indistinguishable from the eager object this used to build: spread,
 * `JSON.stringify`, `Object.keys` and deep equality all see the same shape,
 * in the same order, with the same values. Redefining a key the literal
 * already declared keeps its original position in that order.
 *
 * Assigning `undefined` RELEASES the field rather than storing an absent
 * value: the next read derives it again. The two uses are different on
 * purpose. The payload reduction REPLACES the payload with the attributes the
 * derived views read — a defined value, so it stays cached for the record's
 * lifetime. The parts are RELEASED once the readers have counted them: the
 * decompressed text is the session's working set, and a reader that genuinely
 * needs it again (the twin collapse's content transplant, a subagent's task
 * summary) asks for it and inflates it once more.
 */
export function defineLazy<T>(target: object, key: string, compute: () => T): void {
  let value: T
  let computed = false
  Object.defineProperty(target, key, {
    enumerable: true,
    configurable: true,
    get(): T {
      if (!computed) {
        value = compute()
        computed = true
      }
      return value
    },
    set(next: T): void {
      value = next
      computed = next !== undefined
    },
  })
}

/**
 * Drop a record's parts, and the content derived from them.
 *
 * WHY: the parts are a record's largest field, and the projection reads them
 * once — to count them into the composition's bucket totals, the tool
 * catalogue and the skills listing — and then has no further use for the
 * text. `defineLazy` caches what it computed, so the decompressed parts
 * stayed alive for as long as the record, and the record lives as long as the
 * session: on the measured corpus ~10,933 turns x 64 KB of text, about 1.4 GB
 * as UTF-16, which is what the used heap spiked to.
 *
 * The release is a RELEASE, not a destruction: the lazy fields are marked
 * unread, so a later read derives them again from the row. A reader that
 * needs the raw text after the release therefore pays one more inflation, and
 * the projection keeps the reduced value rather than the text.
 */
export function releaseRecordParts(record: CanonicalRecord): void {
  releaseLazyField(record, 'parts')
  releaseLazyField(record, 'content')
}

/**
 * Release one lazy field: drop what it cached and mark it unread, so a later
 * read derives it again.
 *
 * Only a LAZY field is released. A plain value is the record's own data — a
 * record built by hand, or the twin collapse's merged record — and there is
 * nothing to re-derive it from, so releasing it would lose it.
 */
export function releaseLazyField(record: CanonicalRecord, key: 'parts' | 'content'): void {
  const descriptor = Object.getOwnPropertyDescriptor(record, key)
  if (descriptor === undefined) return
  if (descriptor.get === undefined || descriptor.set === undefined) return
  ;(record as unknown as Record<string, unknown>)[key] = undefined
}

export function toRecord(row: RecordRow): CanonicalRecord {
  const record: CanonicalRecord = {
    spanId: text(row.span_id),
    traceId: nullableText(row.trace_id),
    parentSpanId: nullableText(row.parent_span_id),
    source: text(row.source),
    harness: text(row.harness),
    name: text(row.name),
    op: text(row.op),
    kind: text(row.kind),
    // Timestamps normalize to ISO strings on the way in; a Date input and a
    // string input are indistinguishable after a round trip.
    timestamp: text(row.timestamp),
    durationMs: row.duration_ms as number,
    status: text(row.status),
    tokens: JSON.parse(text(row.tokens_json)) as TokenUsage,
    content: JSON.parse(text(row.content_json)) as CanonicalRecord['content'],
    // `content` is redefined below when parts are present; see toRecord.
    cost: JSON.parse(text(row.cost_json)) as CostBlock,
    // Declared here for its place in the key order, then made lazy: only the
    // attribute lookups read it, and they read it from a minority of records.
    raw: undefined,
  }
  defineLazy(record, 'raw', () =>
    row.raw === null || row.raw === undefined ? undefined : decompressRaw(row.raw as Uint8Array),
  )
  if (row.session_id !== null && row.session_id !== undefined) {
    record.sessionId = text(row.session_id)
  }
  if (row.measurability_json !== null) {
    record.measurability = JSON.parse(text(row.measurability_json)) as Measurability
  }
  if (row.parts_json !== null && row.parts_json !== undefined) {
    // Parts are the authority when present, and `content` is derived from
    // them on the way out rather than stored a second time. Storing both
    // measured 166 MB of uncompressed `content_json` against 40 MB for the
    // same text compressed as parts — a 4x store for one copy of the data,
    // which is the shape of the 2.9 GB problem R12.4 exists to prevent.
    //
    // The column holds the deflated parts, and the plain JSON a row written
    // before the compression landed still holds. Both are read, so a
    // pre-change row keeps its parts: falling back to `content_json` would
    // lose them, because that column is '{}' on a parts-carrying row.
    //
    // A column that holds neither form reads as no parts, and the content
    // falls back to `content_json` — one unreadable cell must not take the
    // record's reader down with it.
    defineLazy(record, 'parts', () => decompressColumn(row.parts_json) as ContentPart[] | undefined)
    defineLazy(record, 'content', () => ({
      ...contentFromParts(record.parts ?? []),
      ...(JSON.parse(text(row.content_json)) as CanonicalRecord['content']),
    }))
  }
  return record
}

/**
 * One record of a session, with the attributes the derived views read.
 *
 * The record still carries its payload; the reduced attributes are handed
 * over beside it so the caller can compare the two before adopting them.
 */
export type ProjectedSessionRecord = {
  /** The record, in session order. */
  record: CanonicalRecord
  /** The attributes the derived views read, in place of the payload. */
  attributes: Record<string, unknown>
}

/**
 * The raw attributes the derived views read out of a record's payload.
 *
 * EVERY reader of `record.raw` in the projection path is listed here, because
 * a key missing from this list is a key whose value the projection would
 * silently lose:
 *
 *   - the session analysis (`canon/sessions.ts`): the model, the agent, the
 *     repository and branch, the tool name and status, the operation, the
 *     parent session, the subagent identity and description, and the tool
 *     arguments the task summary reads;
 *   - the twin-collector collapse (`canon/twin-dedupe.ts`): the join keys and
 *     the record's provenance;
 *   - the context-window derivation (`canon/context-window.ts`): the reported
 *     and declared window attributes, and the model identity;
 *   - the skills view (`canon/skills.ts`): the listing and invocation keys;
 *   - the run derivation (`canon/runs.ts`) and its outcome (`canon/outcome.ts`):
 *     the run, working-directory, parent-session and agent attributes, and the
 *     termination attributes;
 *   - the finding detectors (`analysis/findings.ts`, `analysis/compare.ts`,
 *     `analysis/signals.ts`): the tool arguments, the tool result and the
 *     command attributes.
 */
export const PROJECTED_ATTRIBUTE_KEYS: ReadonlySet<string> = new Set([
  // The session analysis (`canon/sessions.ts`).
  'gen_ai.response.model',
  'gen_ai.request.model',
  'model',
  'gen_ai.agent.name',
  'agent.name',
  'vcs.repository.name',
  'repo',
  'vcs.ref.head.name',
  'branch',
  'gen_ai.operation.name',
  'gen_ai.tool.name',
  'tool.name',
  'gen_ai.tool.status',
  'status',
  'is_error',
  'isError',
  'gen_ai.is_subagent',
  'is_subagent',
  'parent_session',
  'parent_session_id',
  'gen_ai.parent_session_id',
  'parentSession',
  'parentSessionId',
  'gen_ai.prompt',
  'prompt',
  'agent_id',
  'agentId',
  'subagent_id',
  'subagentId',
  'agent.id',
  'gen_ai.agent.id',
  'agent_type',
  'agentType',
  'gen_ai.agent.type',
  'agent_name',
  'agentName',
  'agent',
  'description',
  'task_description',
  'taskDescription',
  'task',
  'gen_ai.agent.description',
  'summary',
  'arguments',
  'gen_ai.tool.arguments',
  'tool.arguments',
  'tool_arguments',
  'parameters',
  'gen_ai.agent.arguments',
  'task.description',
  // The context-window derivation (`canon/context-window.ts`).
  'contextWindow',
  'gen_ai.request.max_context_tokens',
  'gen_ai.request.context_window',
  'model_context_window',
  'declaredContextWindow',
  // The twin-collector collapse (`canon/twin-dedupe.ts`).
  'gen_ai.response.id',
  'request_id',
  'requestId',
  'provenance',
  // The skills view (`canon/skills.ts`).
  'kyber.skills.listed',
  'kyber.skills.invoked',
  'github.copilot.context.skills',
  'gen_ai.skills',
  'gen_ai.usage.skill_tokens',
  // The run derivation (`canon/runs.ts`).
  'process.working_directory',
  'working_directory',
  'cwd',
  'workspace.root',
  'project.path',
  'gen_ai.run.id',
  'run.id',
  'run_id',
  'runId',
  'gen_ai.workflow.id',
  'workflow.id',
  'workflow_id',
  'task.id',
  'task_id',
  // The run outcome (`canon/outcome.ts`).
  'exitCode',
  'exit_code',
  'status_code',
  'terminationReason',
  'termination_reason',
  'stop_reason',
  'finish_reason',
  'type',
  'event',
  'error',
  'exception',
  'editFailed',
  'isCorrection',
  'correctionRule',
  'userMessage',
  // The finding detectors (`analysis/findings.ts`, `analysis/compare.ts`,
  // `analysis/signals.ts`).
  'gen_ai.tool.arguments_hash',
  'gen_ai.tool.result_bytes',
  'result',
  'output',
  'tool_name',
  'tool',
  'tools',
  'command',
  'cmd',
])

/**
 * The length above which a string value counts as payload rather than as an
 * attribute: a harness's prompt, conversation or catalogue runs to tens of
 * kilobytes, and an attribute the readers look up is a name, a path or a
 * count. Values under the limit are kept whatever their key, so an attribute
 * nobody listed still survives when it is small.
 */
export const PROJECTED_VALUE_LIMIT = 4096

/**
 * A record's payload, reduced to the attributes the derived views read.
 *
 * Every key survives: the timeline's evidence scan reads the key names, and a
 * key that names the evidence IS evidence. The values are what the projection
 * cannot afford — the harness's prompt, conversation and catalogue — under
 * keys no reader asks for. A value under `PROJECTED_VALUE_LIMIT` is kept
 * whatever its key; a longer one is kept only when a reader asks for it by
 * name.
 */
export function projectedAttributes(payload: unknown): Record<string, unknown> {
  if (payload === null || typeof payload !== 'object' || Array.isArray(payload)) return {}
  const attributes: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(payload as Record<string, unknown>)) {
    if (typeof value === 'string') {
      if (value.length <= PROJECTED_VALUE_LIMIT || PROJECTED_ATTRIBUTE_KEYS.has(key)) {
        attributes[key] = value
      }
      continue
    }
    if (value !== null && typeof value === 'object') {
      // An object or an array is payload unless a reader asks for it by name
      // (`provenance`, the skills listing, the detector's tool list).
      if (PROJECTED_ATTRIBUTE_KEYS.has(key)) attributes[key] = value
      continue
    }
    attributes[key] = value
  }
  return attributes
}

/**
 * Replace every record's payload with the attributes the derived views read.
 *
 * The payload is read and released one record at a time, so a session's
 * payloads are never all live at once — which is the whole point: the
 * measured corpus held 1.5 GB of them at once, and the heap spiked to 3.1 GB
 * every projection.
 */
export function reduceRecordPayloads(records: readonly CanonicalRecord[]): void {
  for (const record of records) record.raw = projectedAttributes(record.raw)
}

export type SessionDbRow = {
  session_id: unknown
  harness: unknown
  label: unknown
  is_subagent: unknown
  parent_session: unknown
  agent_name: unknown
  repo: unknown
  branch: unknown
  started: unknown
  ended: unknown
  payload: unknown
}

export function toSessionRow(row: SessionDbRow): SessionRow {
  let payload: unknown = undefined
  if (row.payload !== null && row.payload !== undefined) {
    try {
      payload = JSON.parse(text(row.payload))
    } catch {
      payload = row.payload
    }
  }
  return {
    sessionId: text(row.session_id),
    harness: text(row.harness),
    label: nullableText(row.label),
    isSubagent: Boolean(row.is_subagent),
    parentSession: nullableText(row.parent_session),
    agentName: nullableText(row.agent_name),
    repo: nullableText(row.repo),
    branch: nullableText(row.branch),
    started: nullableText(row.started),
    ended: nullableText(row.ended),
    summary:
      payload !== null && typeof payload === "object" && "summary" in (payload as Record<string, unknown>)
        ? ((payload as Record<string, unknown>).summary as Record<string, unknown>)
        : undefined,
    payload,
  }
}

/**
 * The two materialized summary columns of one session payload, derived from
 * the payload's own object.
 *
 * `summary_json` is the serialized `$.summary` object; `problems_count` is the
 * length of the `$.problems` array. A payload carrying no such value writes
 * NULL for that column: NULL is the "not materialized" sentinel, so the reader
 * derives the value from the payload and lands on the answer it would have
 * derived anyway. A fabricated `{}` or `0` would instead claim an empty
 * summary, and a measured zero problem count, for a payload that was never
 * measured.
 *
 * WHY this is the rule and `materializeSessionSummary` is its entry point:
 * the rule has to be in ONE place, because two places is how a migrated
 * corpus and a freshly written one end up describing the same session
 * differently. The write path derives the columns from the serialized payload
 * the row stores (`materializeSessionSummary`); the migration derives them
 * from the object it has already parsed (`sessionSummaryOf`, this function).
 * The context-limit column is shared the same way -- `contextLimitSourceOf`
 * is the rule, `materializeContextLimitSource` is the write path's entry
 * point -- so the two columns of a migrated row and a written row come from
 * one derivation each.
 */
export function sessionSummaryOf(record: Record<string, unknown>): {
  summary_json: string | null
  problems_count: number | null
} {
  const summary = record.summary
  const problems = record.problems
  return {
    // A payload that carries no summary, or a null one, has no summary to
    // materialize: NULL, never the string "null" and never an empty object.
    summary_json: summary === undefined || summary === null ? null : JSON.stringify(summary),
    // Only an array has a length to count. A missing or non-array `problems`
    // is "never measured", not a count of zero.
    problems_count: Array.isArray(problems) ? problems.length : null,
  }
}

/**
 * The two materialized summary columns of one session row, derived from the
 * serialized payload the row stores.
 *
 * Derived from the serialized payload rather than from the caller's object, so
 * the columns are exactly the `$.summary` and `$.problems` the stored payload
 * holds -- the two cannot disagree. The rule itself is `sessionSummaryOf`,
 * which the migration uses over the object it parses.
 */
export function materializeSessionSummary(payloadJson: string): {
  summary_json: string | null
  problems_count: number | null
} {
  let parsed: unknown
  try {
    parsed = JSON.parse(payloadJson)
  } catch {
    return { summary_json: null, problems_count: null }
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return { summary_json: null, problems_count: null }
  }
  return sessionSummaryOf(parsed as Record<string, unknown>)
}

/**
 * The measured figures a session's summary carries.
 *
 * Every field is optional because the summary is optional: a session that
 * measured nothing carries none of them, and a figure that is missing stays
 * missing. A zero would claim a measured zero for a session that measured
 * nothing.
 */
export type SessionSummaryFigureFields = {
  turnCount?: number
  totalInput?: number
  totalOutput?: number
  totalCacheRead?: number
  totalCacheCreation?: number
  durationMs?: number
  costBasis?: string
  costStatus?: string
  costValue?: number
  costCurrency?: string
  costUsdLegacy?: number
  includedInParent?: boolean
}

/** One session's measured figures, with the session they belong to. */
export type SessionSummaryFigureRow = SessionSummaryFigureFields & { sessionId: string }

/**
 * The figures a summary carries, and where each one lives inside it.
 *
 * One list, two renderings: the materialized column holds the `$.summary`
 * object itself, so its paths are `$.<path>`, while a payload holds that same
 * object under `$.summary`, so its paths are `$.summary.<path>`. Deriving
 * both statements from this one list is what stops the materialized read and
 * the legacy read from drifting apart on which figure is which.
 */
export const SESSION_FIGURE_FIELDS = [
  { alias: 'turn_count', path: 'turn_count' },
  { alias: 'total_input', path: 'total_input' },
  { alias: 'total_output', path: 'total_output' },
  { alias: 'total_cache_read', path: 'total_cache_read' },
  { alias: 'total_cache_creation', path: 'total_cache_creation' },
  { alias: 'duration_ms', path: 'duration_ms' },
  { alias: 'cost_basis', path: 'cost.basis' },
  { alias: 'cost_status', path: 'cost.status' },
  { alias: 'cost_value', path: 'cost.value' },
  { alias: 'cost_currency', path: 'cost.currency' },
  { alias: 'cost_usd', path: 'cost.usd' },
  { alias: 'included_in_parent', path: 'included_in_parent' },
] as const

/**
 * The figures of a set of sessions, read out of the materialized column.
 *
 * `summary_json` holds the serialized `$.summary` object, so every figure is
 * one JSON path inside it. The statement names no payload: naming `payload`
 * at all, even inside `json_extract`, makes SQLite read and parse the whole
 * blob of every row it touches, which is the cost this read exists to avoid.
 * `summary_json` itself is selected beside the figures because its NULL is
 * the writer's "not materialized" sentinel -- the only thing that says a row
 * still has to be read out of its payload.
 */
export function sessionFiguresSelect(placeholders: string): string {
  const columns = SESSION_FIGURE_FIELDS.map(
    (field) => `json_extract(summary_json, '$.${field.path}') AS ${field.alias}`,
  ).join(', ')
  return `SELECT session_id, summary_json, ${columns} FROM session WHERE session_id IN (${placeholders})`
}

/**
 * The same figures, read out of a legacy row's own payload.
 *
 * The scope is the load-bearing part: `summary_json IS NULL` confines the
 * payload read to the rows written before the materialization existed, so a
 * materialized corpus never reads a byte of payload. A database whose schema
 * predates the column has no materialized rows to scope to, and the scope is
 * dropped for it -- every row there is a legacy row.
 */
export function sessionFiguresLegacySelect(placeholders: string, scoped: boolean): string {
  const columns = SESSION_FIGURE_FIELDS.map(
    (field) => `json_extract(payload, '$.summary.${field.path}') AS ${field.alias}`,
  ).join(', ')
  const scope = scoped ? ' AND summary_json IS NULL' : ''
  return `SELECT session_id, ${columns} FROM session WHERE session_id IN (${placeholders})${scope}`
}

/** One row of the figures read, with the aliases the field list names. */
export type SessionFigureDbRow = {
  session_id: unknown
  summary_json?: unknown
  turn_count?: unknown
  total_input?: unknown
  total_output?: unknown
  total_cache_read?: unknown
  total_cache_creation?: unknown
  duration_ms?: unknown
  cost_basis?: unknown
  cost_status?: unknown
  cost_value?: unknown
  cost_currency?: unknown
  cost_usd?: unknown
  included_in_parent?: unknown
}

/**
 * Whether a statement failed because the table has no such column.
 *
 * The one failure the reader recovers from: a database written before the
 * materialized columns existed has no `summary_json` to read, and every row
 * in it is a legacy row.
 */
export function isMissingColumnError(err: unknown): boolean {
  return err instanceof Error && /no such column/i.test(err.message)
}

/**
 * The figures one row carries, with the guards this read has always applied.
 *
 * A figure the session did not measure stays missing: a summary without
 * `turn_count` yields no turn count, never a zero. A status or a currency
 * that is not a string is not a status or a currency. A number that is not
 * finite is not a figure: a JSON document carrying `1e999` parses to Infinity,
 * and an infinite total would poison every sum it enters.
 */
export function sessionFigureFields(row: SessionFigureDbRow): SessionSummaryFigureFields {
  const figure = (value: unknown): number | undefined =>
    typeof value === 'number' && Number.isFinite(value) ? value : undefined
  const turnCount = figure(row.turn_count)
  const totalInput = figure(row.total_input)
  const totalOutput = figure(row.total_output)
  const totalCacheRead = figure(row.total_cache_read)
  const totalCacheCreation = figure(row.total_cache_creation)
  const durationMs = figure(row.duration_ms)
  const costValue = figure(row.cost_value)
  const costUsdLegacy = figure(row.cost_usd)
  return {
    ...(turnCount !== undefined ? { turnCount } : {}),
    ...(totalInput !== undefined ? { totalInput } : {}),
    ...(totalOutput !== undefined ? { totalOutput } : {}),
    ...(totalCacheRead !== undefined ? { totalCacheRead } : {}),
    ...(totalCacheCreation !== undefined ? { totalCacheCreation } : {}),
    ...(durationMs !== undefined ? { durationMs } : {}),
    ...(typeof row.cost_basis === 'string' ? { costBasis: row.cost_basis } : {}),
    ...(typeof row.cost_status === 'string' ? { costStatus: row.cost_status } : {}),
    ...(costValue !== undefined ? { costValue } : {}),
    ...(typeof row.cost_currency === 'string' ? { costCurrency: row.cost_currency } : {}),
    ...(costUsdLegacy !== undefined ? { costUsdLegacy } : {}),
    ...(row.included_in_parent === true ? { includedInParent: true as const } : {}),
  }
}

/**
 * The measured figures of a set of sessions.
 *
 * <remarks>
 * One reader for both copies of this read: the store's own accessor and the
 * bridge's direct-DB path differ only in which handle they hand over, so the
 * two cannot disagree about what a session's figures are.
 *
 * The materialized column answers first. Only a row whose `summary_json` is
 * NULL -- a row written before the materialization existed -- falls back to
 * its own payload, and it falls back one row at a time, so a mixed corpus
 * leaves the materialized rows alone and reads each legacy row's payload
 * once.
 * </remarks>
 */
export function readSessionSummaryFigures(
  db: Database,
  sessionIds: readonly string[],
): SessionSummaryFigureRow[] {
  const uniqueIds = [...new Set(sessionIds)].filter((id) => id.length > 0)
  const out: SessionSummaryFigureRow[] = []
  const chunkSize = 900

  for (let offset = 0; offset < uniqueIds.length; offset += chunkSize) {
    const chunk = uniqueIds.slice(offset, offset + chunkSize)
    for (const row of readSessionFigureRows(db, chunk)) {
      if (typeof row.session_id !== 'string') continue
      out.push({ sessionId: row.session_id, ...sessionFigureFields(row) })
    }
  }

  return out
}

/**
 * One chunk of the figures read: the materialized column, with the payload
 * read for the rows the column could not serve.
 */
export function readSessionFigureRows(db: Database, ids: string[]): SessionFigureDbRow[] {
  const placeholders = ids.map(() => '?').join(', ')
  let primary: SessionFigureDbRow[]
  try {
    primary = db.prepare(sessionFiguresSelect(placeholders)).all(...ids) as SessionFigureDbRow[]
  } catch (err) {
    if (!isMissingColumnError(err)) throw err
    // A schema that predates the materialized column: every row in it is a
    // legacy row, so the payload read serves the whole chunk.
    return db.prepare(sessionFiguresLegacySelect(placeholders, false)).all(...ids) as SessionFigureDbRow[]
  }

  // NULL is the writer's "not materialized" sentinel, so these are exactly
  // the rows whose figures still live in their payload.
  const legacyIds = primary
    .filter((row) => row.summary_json === null || row.summary_json === undefined)
    .map((row) => row.session_id)
    .filter((id): id is string => typeof id === 'string')
  if (legacyIds.length === 0) return primary

  const legacyPlaceholders = legacyIds.map(() => '?').join(', ')
  const legacy = db
    .prepare(sessionFiguresLegacySelect(legacyPlaceholders, true))
    .all(...legacyIds) as SessionFigureDbRow[]
  const byId = new Map<unknown, SessionFigureDbRow>(legacy.map((row) => [row.session_id, row]))
  return primary.map((row) => {
    if (row.summary_json !== null && row.summary_json !== undefined) return row
    // The payload fills what the column could not serve. A row with no
    // materialized summary has no materialized figures either, so nothing
    // that was materialized is overwritten.
    return byId.get(row.session_id) ?? row
  })
}

export type RunDbRow = {
  run_id: unknown
  harness: unknown
  label: unknown
  grouping_basis: unknown
  grouping_rule: unknown
  working_directory: unknown
  started: unknown
  ended: unknown
  execution_count: unknown
  payload: unknown
}

export function toRunRow(row: RunDbRow): RunRow {
  const payload = row.payload === null || row.payload === undefined ? undefined : JSON.parse(text(row.payload))
  const outcome =
    payload !== null && typeof payload === 'object' && !Array.isArray(payload) && 'outcome' in payload
      ? (payload as Record<string, unknown>)['outcome']
      : undefined
  return {
    runId: text(row.run_id),
    harness: text(row.harness),
    label: nullableText(row.label),
    groupingBasis: text(row.grouping_basis) as RunGroupingBasis,
    groupingRule: nullableText(row.grouping_rule),
    workingDirectory: nullableText(row.working_directory),
    started: nullableText(row.started),
    ended: nullableText(row.ended),
    executionCount: Number(row.execution_count ?? 0),
    ...(outcome !== undefined ? { outcome: outcome as RunRow['outcome'] } : {}),
    payload,
  }
}

export type ExecutionDbRow = {
  execution_id: unknown
  run_id: unknown
  session_id: unknown
  parent_execution_id: unknown
  harness: unknown
  agent_name: unknown
  is_root: unknown
  started: unknown
  ended: unknown
  parent_linkage_json: unknown
  payload: unknown
}

export function toExecutionRow(row: ExecutionDbRow): ExecutionRow {
  return {
    executionId: text(row.execution_id),
    runId: text(row.run_id),
    sessionId: nullableText(row.session_id),
    parentExecutionId: nullableText(row.parent_execution_id),
    harness: text(row.harness),
    agentName: nullableText(row.agent_name),
    isRoot: Boolean(row.is_root),
    started: nullableText(row.started),
    ended: nullableText(row.ended),
    parentLinkage: JSON.parse(text(row.parent_linkage_json)) as MetricAvailability,
    payload: row.payload === null || row.payload === undefined ? undefined : JSON.parse(text(row.payload)),
  }
}

export type HarnessRollupDbRow = {
  harness: unknown
  sample_count: unknown
  context_pressure_median: unknown
  context_pressure_p95: unknown
  cache_hit_rate: unknown
  tool_yield: unknown
  delegation_overhead: unknown
  field_coverage: unknown
  measurability_json: unknown
  payload: unknown
}

export function nullableNumber(value: unknown): number | null {
  if (value === null || value === undefined) return null
  const num = Number(value)
  return Number.isFinite(num) ? num : null
}

export function toHarnessRollupRow(row: HarnessRollupDbRow): HarnessRollupRow {
  return {
    harness: text(row.harness),
    sampleCount: Number(row.sample_count ?? 0),
    contextPressureMedian: nullableNumber(row.context_pressure_median),
    contextPressureP95: nullableNumber(row.context_pressure_p95),
    cacheHitRate: nullableNumber(row.cache_hit_rate),
    toolYield: nullableNumber(row.tool_yield),
    delegationOverhead: nullableNumber(row.delegation_overhead),
    fieldCoverage: nullableNumber(row.field_coverage),
    measurability: JSON.parse(text(row.measurability_json)) as Record<string, MetricAvailability>,
    payload: row.payload === null || row.payload === undefined ? undefined : JSON.parse(text(row.payload)),
  }
}

export type FindingDbRow = {
  id: unknown
  detector_id: unknown
  title: unknown
  mechanism: unknown
  confidence: unknown
  estimated_waste_tokens: unknown
  recommendation: unknown
  error_bar_json: unknown
  evidence_links_json: unknown
  outcome_risk_caveat: unknown
  run_id: unknown
  session_id: unknown
  rank_score: unknown
  payload: unknown
}

export function toFinding(row: FindingDbRow): Finding {
  const parsedErrorBar = JSON.parse(text(row.error_bar_json)) as { lower: number; upper: number } | null
  const evidenceLinks = JSON.parse(text(row.evidence_links_json)) as FindingEvidenceLink[]
  const finding: Finding = {
    id: text(row.id),
    detectorId: text(row.detector_id) as DetectorId,
    title: text(row.title),
    mechanism: text(row.mechanism),
    evidenceLinks,
    confidence: text(row.confidence) as FindingConfidence,
    // A NULL waste column is an unmeasured (coverage-gap) finding: it
    // round-trips as absent, never as zero waste.
    ...(row.estimated_waste_tokens === null || row.estimated_waste_tokens === undefined
      ? {}
      : { estimatedWasteTokens: Number(row.estimated_waste_tokens) }),
    recommendation: text(row.recommendation),
    ...(parsedErrorBar === null || parsedErrorBar === undefined ? {} : { errorBar: parsedErrorBar }),
    outcomeRiskCaveat: text(row.outcome_risk_caveat),
    runId: nullableText(row.run_id) ?? undefined,
    sessionId: nullableText(row.session_id) ?? undefined,
    rankScore: Number(row.rank_score ?? 0),
  }
  if (row.payload !== null && row.payload !== undefined) {
    try {
      const extra = JSON.parse(text(row.payload)) as Record<string, unknown>
      Object.assign(finding, extra)
    } catch {}
  }
  return finding
}

export type PredictionDbRow = {
  id: unknown
  finding_id: unknown
  run_id: unknown
  predicted_waste_tokens: unknown
  confidence: unknown
  confidence_tier: unknown
  error_bar_json: unknown
  created_at: unknown
  comparison_run_id: unknown
  observed_delta_tokens: unknown
  calibration_score: unknown
  payload: unknown
}

export function toPrediction(row: PredictionDbRow): PredictionRecord {
  const errorBar = row.error_bar_json
    ? (JSON.parse(text(row.error_bar_json)) as { lower: number; upper: number })
    : undefined
  const createdAt = text(row.created_at)
  const prediction: PredictionRecord = {
    id: text(row.id),
    findingId: text(row.finding_id),
    runId: text(row.run_id),
    predictedWasteTokens: Number(row.predicted_waste_tokens ?? 0),
    confidence: Number(row.confidence ?? 0),
    confidenceTier: row.confidence_tier ? (text(row.confidence_tier) as FindingConfidence) : undefined,
    errorBar,
    createdAt,
    timestamp: createdAt,
    comparisonRunId: nullableText(row.comparison_run_id) ?? undefined,
    observedDeltaTokens:
      row.observed_delta_tokens !== null && row.observed_delta_tokens !== undefined
        ? Number(row.observed_delta_tokens)
        : undefined,
    calibrationScore:
      row.calibration_score !== null && row.calibration_score !== undefined
        ? Number(row.calibration_score)
        : undefined,
    status:
      row.comparison_run_id !== null && row.comparison_run_id !== undefined
        ? 'scored'
        : 'pending',
  }
  if (row.payload !== null && row.payload !== undefined) {
    try {
      prediction.payload = JSON.parse(text(row.payload)) as Record<string, unknown>
    } catch {}
  }
  return prediction
}


export const UPSERT_RECORD_SQL = `
INSERT OR REPLACE INTO records (
  span_id, trace_id, parent_span_id, source, harness, session_id, name, op, kind,
  timestamp, duration_ms, status, tokens_json, content_json, cost_json,
  measurability_json, parts_json, raw
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
`

/**
 * The metric-point write, keyed on the point's content digest.
 *
 * The conflict arm restates the row rather than replacing it: a replacement
 * would delete the row and insert it again, which moves it to the end of the
 * table's order and loses the position the point was first seen at.
 */
export const UPSERT_METRIC_POINT_SQL = `
INSERT INTO metric_point (
  point_key, name, kind, unit, aggregation_temporality, time_unix_nano,
  start_time_unix_nano, timestamp, value, point_count, point_sum,
  attributes_json, resource_json, scope_json, source
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
ON CONFLICT(point_key) DO UPDATE SET
  name = excluded.name,
  kind = excluded.kind,
  unit = excluded.unit,
  aggregation_temporality = excluded.aggregation_temporality,
  time_unix_nano = excluded.time_unix_nano,
  start_time_unix_nano = excluded.start_time_unix_nano,
  timestamp = excluded.timestamp,
  value = excluded.value,
  point_count = excluded.point_count,
  point_sum = excluded.point_sum,
  attributes_json = excluded.attributes_json,
  resource_json = excluded.resource_json,
  scope_json = excluded.scope_json,
  source = excluded.source
`

/** Server-side finding filters (issue #191): detector and harness narrow the
 * set. Paging lives in the bridge (`listFindingsPage`), which slices the
 * narrowed set once `total` is known — one owner, not two (review). */
export type FindingsListOptions = {
  detector?: string
  harness?: string
}

/** Payload bytes one quarantine page may hold; bounds startup memory for the relink pass. */
export const DEFAULT_QUARANTINE_PAGE_BYTES = 16 * 1024 * 1024

/** Payload bytes one pending-log replay page may hold; bounds working memory for the replay pass. */
export const DEFAULT_PENDING_PAGE_BYTES = 4 * 1024 * 1024

/** One `pending_logs` row without its payload (F8): the metadata-only read. */
export type PendingLogMetadata = {
  logId: string
  harness: string | null
  bytes: number
  pendingSince: number | null
  traceId: string | null
  spanId: string | null
  sessionId: string | null
}

/** An expired pending log's parsed payload with the harness tag surfaced (F8). */
export type PendingLogEntry = Record<string, unknown> & {
  logId: string
  harness?: string
  pendingSince?: number
  attributes?: Record<string, unknown>
}

/** A parked interaction prompt's correlation fields without its payload (F8). */
export type PendingInteractionPromptRow = {
  logId: string
  traceId: string | null
  spanId: string | null
  pendingSince?: number
  prompt: unknown
}
