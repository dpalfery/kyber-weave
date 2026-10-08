import { isFileSource } from './measurability.js'
import { contentFromParts } from './types.js'
import type { CanonicalRecord, TokenUsage } from './types.js'

// Same-turn dedupe for twin front-end collectors (issues #181/#182/#231, ADR 0009 D4).
//
// One harness reached through two collectors — Claude Code's OTLP export plus
// its transcript files, Cursor plus Cursor Agent — describes the same model
// call twice under disjoint span ids (live evidence 2026-09-30: byte-identical
// token payloads ~2-6 s apart on one session key). Once `normalizeHarnessName`
// folds the twin surfaces onto one canonical id, both rows land in one share,
// and every sum over the share would count the turn twice.
//
// ADR 0009 D4 states the precedence: counters come from the OTel row and values
// are never summed across sources for the same turn. D10 amends how content
// merges: per canonical bucket, OTel first. A bucket the OTel row carries keeps
// only the OTel row's parts; the file row fills only the buckets the OTel row
// lacks, so no bucket ever holds text from both sources. A union by exact text
// would keep both sources' renderings of one bucket whenever they differ in a
// byte, and count that context twice. File-and-file joins (#231, #232) are one
// source kind and keep their union.
//
// When both rows name the turn, no inference is needed: Claude Code's OTel
// `llm_request` stamps `gen_ai.response.id`, and the transcript row carries
// the same API `message.id` as its native record id. That id join runs first
// and depends on neither counter equality nor the skew window — the two
// collectors are known to disagree on both — so counter matching below is the
// fallback for rows that do not carry the id.
//
// The rule is deliberately conservative: a file row paired with a
// non-file row over identical reported counters is treated as the same turn
// observed twice. Two identical rows from the same source kind (two OTLP
// rows, two file rows) could be genuine retries and are always kept — dropping telemetry the rule cannot prove
// duplicated would trade a known over-count for a silent under-count.
//
// Matching needs to identify a turn, not merely a counter vector. `counterKey`
// therefore carries the session key, because both collectors report the twin
// of one turn under the same session (live evidence: "one session key"), and
// without it two unrelated sessions whose counters happen to agree — and whose
// records share a build share because they share a canonical harness — would
// be merged into one turn. Content is deliberately *not* part of the key: the
// whole point of the pairing is that the OTel row has no content and the file
// row does, so a content fingerprint would separate exactly the rows the rule
// exists to join.
//
// Exact-counter matching covers the Claude OTel+file path (ADR 0009 D4). Issue
// #231 extends the same share with a complementary/overlap join for observations
// whose counters differ: within `TWIN_TURN_MAX_SKEW_MS`, file+file rows under an
// *evidenced* Cursor twin share (session group holding both `cursor` and
// `cursor-agent` collectors) that stand in a complementary or subset relation
// join into one turn. The pass stays inert for every other harness group —
// `isFileSource` alone is not twin evidence (A3 refined, not overturned).
// Joined counters take the per-dimension max (prefer the fuller row as keeper
// identity); they are never summed. Partials (request/response halves) assign
// to at most one complete Cursor Agent observation so union-find cannot
// transitively bridge conflicting completes. A complete Cursor IDE turn is
// itself a full observation — not a half — so each Agent anchor accepts at
// most one best matching complete IDE; conflicting complete IDE turns stay
// separate rather than silently collapsing. Identical same-kind OTLP retries
// stay kept — the overlap join only bridges *different* counter vectors.

/**
 * Maximum timestamp gap for two rows to be the same turn observed twice.
 * Live skew measured under 10 s (collectors flush on different schedules);
 * 60 s bounds the risk of merging genuinely repeated identical turns while
 * tolerating that skew. Named, not inlined, so a wrong merge window is
 * traceable to one assumption instead of looking like a measurement.
 *
 * The gap bounds a cluster's total span, measured from its first row. Rows
 * arrive time-ordered, so that span always covers the neighbour gap and a
 * separate step bound could never fire on its own; bounding only the step
 * would in any case be single-linkage chaining, since rows 50 s apart form
 * an unbounded run that a session re-reporting identical counters every
 * 50 s would collapse into one turn. See the check in `dedupeTwinTurns`.
 */
export const TWIN_TURN_MAX_SKEW_MS = 60_000

type CounterKey = string

const TOKEN_DIMS = [
  'freshInput',
  'cacheRead',
  'cacheCreation',
  'output',
  'reportedInput',
  'reportedOutput',
] as const satisfies readonly (keyof TokenUsage)[]

function counterKey(record: CanonicalRecord, shareKey?: string): CounterKey {
  const { tokens } = record
  // A share routinely mixes rows with session_id set and rows where only
  // the trace carried the key (review): an omitted attribute must resolve
  // to the share's key, never to a bucket of its own — otherwise one
  // collector's half of a twin lands in '' and the turn counts twice.
  return [
    record.sessionId ?? shareKey ?? '',
    tokens.freshInput,
    tokens.cacheRead,
    tokens.cacheCreation,
    tokens.output,
    tokens.reportedInput,
    tokens.reportedOutput,
  ].join(':')
}

function sessionKeyOf(record: CanonicalRecord, shareKey?: string): string {
  return record.sessionId ?? shareKey ?? ''
}

function timestampMs(record: CanonicalRecord): number {
  return Date.parse(typeof record.timestamp === 'string' ? record.timestamp : record.timestamp.toISOString())
}

/** Parsed timestamps memoized across a run (council review): sorts, span
 * checks and nearest-pairing otherwise re-parse the same stamps per
 * comparison. Records are garbage-collected with their entries. */
const parsedTimestamps = new WeakMap<CanonicalRecord, number>()

function cachedTimestampMs(record: CanonicalRecord): number {
  const known = parsedTimestamps.get(record)
  if (known !== undefined) return known
  const ms = timestampMs(record)
  parsedTimestamps.set(record, ms)
  return ms
}

/** The API response id an OTel row reports; Claude Code stamps the message id here. */
function otelResponseIdOf(record: CanonicalRecord): string | undefined {
  const raw = record.raw
  if (typeof raw !== 'object' || raw === null) return undefined
  const value = (raw as Record<string, unknown>)['gen_ai.response.id']
  return typeof value === 'string' && value !== '' ? value : undefined
}

/**
 * The harness's own record id on a synthesized file row (`message.id` for a
 * Claude transcript). A digest fallback is stamped when the transcript names
 * no id; it identifies nothing an OTel row could carry, so it never joins.
 */
function fileNativeIdOf(record: CanonicalRecord): string | undefined {
  const raw = record.raw
  if (typeof raw !== 'object' || raw === null) return undefined
  const provenance = (raw as { provenance?: { nativeRecordId?: unknown; recordDigest?: unknown } }).provenance
  if (provenance === undefined || provenance.recordDigest !== undefined) return undefined
  const value = provenance.nativeRecordId
  return typeof value === 'string' && value !== '' ? value : undefined
}

function hasParts(record: CanonicalRecord): boolean {
  return (record.parts !== undefined && record.parts.length > 0) || Object.keys(record.content).length > 0
}

/** True when a turn reported no usage at all: identical for every such turn, so nothing to match on. */
function isUnreportedCounters(tokens: CanonicalRecord['tokens']): boolean {
  return (
    tokens.freshInput === 0 &&
    tokens.cacheRead === 0 &&
    tokens.cacheCreation === 0 &&
    tokens.output === 0 &&
    tokens.reportedInput === 0 &&
    tokens.reportedOutput === 0
  )
}

function tokenDims(tokens: TokenUsage): readonly number[] {
  return TOKEN_DIMS.map((dim) => tokens[dim] ?? 0)
}

/** Componentwise ≤ — every field of `a` is covered by `b`. */
function isSubsetDims(a: readonly number[], b: readonly number[]): boolean {
  return a.every((value, i) => value <= (b[i] ?? 0))
}

/**
 * No conflicting non-zeros: each dimension is zero on at least one side, or
 * both sides agree. Request/response halves (`17007/0` + `0/45`) match this;
 * disagreeing inputs (`17007` vs `125`) do not.
 */
function isComplementaryDims(a: readonly number[], b: readonly number[]): boolean {
  return a.every((value, i) => {
    const other = b[i] ?? 0
    return value === 0 || other === 0 || value === other
  })
}

/**
 * Issue #231 join predicate for distinct counter vectors: complementary or
 * subset. Identical vectors stay on the exact-counter path so same-kind
 * retries are never collapsed here.
 */
function canJoinDistinctCounters(a: TokenUsage, b: TokenUsage): boolean {
  const da = tokenDims(a)
  const db = tokenDims(b)
  if (da.every((value, i) => value === (db[i] ?? 0))) return false
  return isComplementaryDims(da, db) || isSubsetDims(da, db) || isSubsetDims(db, da)
}

/** Per-dimension max — joined counters are never summed (A3 / ADR 0009 D4). */
function maxTokens(a: TokenUsage, b: TokenUsage): TokenUsage {
  const merged: TokenUsage = {
    freshInput: Math.max(a.freshInput, b.freshInput),
    cacheRead: Math.max(a.cacheRead, b.cacheRead),
    cacheCreation: Math.max(a.cacheCreation, b.cacheCreation),
    output: Math.max(a.output, b.output),
    reportedInput: Math.max(a.reportedInput, b.reportedInput),
    reportedOutput: Math.max(a.reportedOutput, b.reportedOutput),
  }
  const reasoning = Math.max(a.reasoning ?? 0, b.reasoning ?? 0)
  if (a.reasoning !== undefined || b.reasoning !== undefined) {
    merged.reasoning = reasoning
  }
  return merged
}

/** Fuller vector = more non-zero dimensions, then larger magnitude. */
function counterFullness(tokens: TokenUsage): { nonZero: number; magnitude: number } {
  const dims = tokenDims(tokens)
  let nonZero = 0
  let magnitude = 0
  for (const value of dims) {
    if (value > 0) nonZero++
    magnitude += value
  }
  return { nonZero, magnitude }
}

function preferFullerKeeper(a: CanonicalRecord, b: CanonicalRecord): CanonicalRecord {
  const fa = counterFullness(a.tokens)
  const fb = counterFullness(b.tokens)
  if (fa.nonZero !== fb.nonZero) return fa.nonZero > fb.nonZero ? a : b
  if (fa.magnitude !== fb.magnitude) return fa.magnitude > fb.magnitude ? a : b
  // Stable: earlier stamp wins when fullness ties.
  return cachedTimestampMs(a) <= cachedTimestampMs(b) ? a : b
}

/**
 * Collapse same-turn twin observations within one canonical-harness share.
 * Pure: input order is preserved, no record is mutated. An OTel row whose
 * `gen_ai.response.id` equals a file row's native id in the same session is
 * that turn, whatever its counters or timestamp. The remaining rows sharing
 * exact counters cluster by timestamp proximity (span within
 * `TWIN_TURN_MAX_SKEW_MS`); a cluster holding both source kinds is one turn
 * observed twice, so its file rows drop and its content transplants pairwise
 * onto the OTel rows nearest in time. Every OTel-and-file merge is per bucket,
 * OTel first (D10). A further pass (#231) joins file+file rows under an
 * evidenced Cursor twin share whose counters differ but stand in a
 * complementary/subset relation under the same skew window, merging by
 * per-dimension max onto the fuller keeper (partials assign to at most one
 * complete Cursor Agent turn; each such turn accepts at most one complete
 * Cursor IDE match). Applied by the derived-layer builders (`buildSessions`,
 * `buildRuns`, `buildFindings`) after shares merge, never at ingest — raw
 * records keep both collectors' rows as provenance.
 */
export function dedupeTwinTurns(records: readonly CanonicalRecord[], shareKey?: string): CanonicalRecord[] {
  const dropped = new Set<CanonicalRecord>()
  const transplant = new Map<CanonicalRecord, CanonicalRecord>()
  const idJoined = collapseIdJoins(records, shareKey, dropped, transplant)

  const byCounter = new Map<CounterKey, CanonicalRecord[]>()
  for (const record of records) {
    if (record.op !== 'llm.invoke') continue
    // A row already paired by id has its twin; counter matching could only
    // hand it a second, different turn's file row.
    if (idJoined.has(record)) continue
    // An all-zero row shares one key with every other all-zero row in the
    // share, which identifies nothing. Excluding them here means they are never
    // clustered at all, rather than relying on a later guard to un-merge them.
    if (isUnreportedCounters(record.tokens)) continue
    const key = counterKey(record, shareKey)
    const list = byCounter.get(key) ?? []
    list.push(record)
    byCounter.set(key, list)
  }

  for (const turns of byCounter.values()) {
    const ordered = [...turns].sort((a, b) => cachedTimestampMs(a) - cachedTimestampMs(b))
    let cluster: CanonicalRecord[] = []
    const closeCluster = () => {
      collapseCluster(cluster, dropped, transplant)
      cluster = []
    }
    for (const record of ordered) {
      // Close when the span from the cluster's first row exceeds the window.
      // Rows arrive time-ordered, so the span always covers the neighbour
      // gap — a separate step bound could never fire on its own (review).
      const first = cluster[0]
      const exceedsSpan = first !== undefined && cachedTimestampMs(record) - cachedTimestampMs(first) > TWIN_TURN_MAX_SKEW_MS
      if (exceedsSpan) closeCluster()
      cluster.push(record)
    }
    closeCluster()
  }

  // #231: complementary / overlap join for distinct counter vectors (file+file).
  const tokenOverride = new Map<CanonicalRecord, TokenUsage>()
  collapseOverlapFileJoins(records, shareKey, dropped, transplant, tokenOverride)

  return records.flatMap((record) => {
    if (dropped.has(record)) return []
    const donor = transplant.get(record)
    const tokens = tokenOverride.get(record) ?? record.tokens
    if (donor === undefined) {
      return tokens === record.tokens ? [record] : [{ ...record, tokens }]
    }
    if (!isFileSource(record.source)) return [mergeOtelFirst(record, donor)]
    // File+file joins (#231, #232): keeper parts first, then donor parts not
    // already present, so the keeper never loses its own content to its
    // twin's (review M2). #231 keepers carry maxed counters via `tokenOverride`.
    const seen = new Set((record.parts ?? []).map((part) => `${part.part}\u0000${part.text}`))
    const extraParts = (donor.parts ?? []).filter((part) => {
      const key = `${part.part}\u0000${part.text}`
      if (seen.has(key)) return false
      seen.add(key)
      return true
    })
    const newParts = [...(record.parts ?? []), ...extraParts]
    return [
      {
        ...record,
        tokens,
        ...((donor.parts !== undefined && donor.parts.length > 0) || extraParts.length > 0
          ? { parts: newParts }
          : {}),
        content: {
          ...donor.content,
          ...record.content,
          ...(newParts.length > 0 ? contentFromParts(newParts) : {}),
        },
      },
    ]
  })
}

/**
 * Pair OTel rows with file rows that name the same turn: the OTel row's
 * `gen_ai.response.id` equals the file row's native id under one session key.
 * Matched file rows drop and become content donors; the OTel row keeps its
 * counters. Should two OTel rows report one id, the earliest takes the file
 * row and the other stays — same-kind rows are never dropped. Returns every
 * row the pass paired, so the counter pass leaves them alone.
 */
function collapseIdJoins(
  records: readonly CanonicalRecord[],
  shareKey: string | undefined,
  dropped: Set<CanonicalRecord>,
  transplant: Map<CanonicalRecord, CanonicalRecord>,
): Set<CanonicalRecord> {
  const joined = new Set<CanonicalRecord>()
  const keeperById = new Map<string, CanonicalRecord>()
  for (const record of records) {
    if (record.op !== 'llm.invoke' || isFileSource(record.source)) continue
    const id = otelResponseIdOf(record)
    if (id === undefined) continue
    const key = `${sessionKeyOf(record, shareKey)}\u0000${id}`
    const known = keeperById.get(key)
    if (known === undefined || cachedTimestampMs(record) < cachedTimestampMs(known)) keeperById.set(key, record)
  }
  if (keeperById.size === 0) return joined

  for (const record of records) {
    if (record.op !== 'llm.invoke' || !isFileSource(record.source)) continue
    const id = fileNativeIdOf(record)
    if (id === undefined) continue
    const keeper = keeperById.get(`${sessionKeyOf(record, shareKey)}\u0000${id}`)
    if (keeper === undefined) continue
    dropped.add(record)
    joined.add(record)
    joined.add(keeper)
    if (!hasParts(record)) continue
    const existing = transplant.get(keeper)
    transplant.set(keeper, existing === undefined ? record : mergeDonors(existing, record))
  }
  return joined
}

/**
 * D10: the OTel row's counters and every bucket it carries stand; the file
 * donor fills only the buckets the OTel row lacks. A bucket counts as carried
 * whether the OTel row holds it as parts or only as flat content.
 */
function mergeOtelFirst(record: CanonicalRecord, donor: CanonicalRecord): CanonicalRecord {
  const held = new Set<string>([...(record.parts ?? []).map((part) => part.part), ...Object.keys(record.content)])
  const fillParts = (donor.parts ?? []).filter((part) => !held.has(part.part))
  const fillContent = Object.fromEntries(
    Object.entries(donor.content).filter(([bucket]) => !held.has(bucket)),
  ) as CanonicalRecord['content']
  const parts = [...(record.parts ?? []), ...fillParts]
  return {
    ...record,
    ...(fillParts.length > 0 ? { parts } : {}),
    content: {
      ...fillContent,
      ...record.content,
      ...(fillParts.length > 0 ? contentFromParts(parts) : {}),
    },
  }
}

/** Cursor IDE file collector — not `cursor-agent` (harness or `codeburn/` source). */
function isCursorCollector(record: CanonicalRecord): boolean {
  if (record.harness === 'cursor-agent') return false
  if (record.harness === 'cursor') return true
  // Exact source stamp; reject `…/cursor-agent` which also starts with codeburn/cursor.
  return record.source === 'codeburn/cursor'
}

/** Cursor Agent file collector. */
function isCursorAgentCollector(record: CanonicalRecord): boolean {
  return record.harness === 'cursor-agent' || record.source.includes('cursor-agent')
}

/**
 * A3 overlap join only runs when the session group evidences both Cursor twin
 * collectors. `buildSessions` calls `dedupeTwinTurns` for every harness share;
 * without this gate, ordinary file-backed rows elsewhere would collapse on
 * complementary counters alone.
 */
function hasEvidencedCursorTwinShare(records: readonly CanonicalRecord[]): boolean {
  let cursor = false
  let agent = false
  for (const record of records) {
    if (isCursorAgentCollector(record)) agent = true
    else if (isCursorCollector(record)) cursor = true
    if (cursor && agent) return true
  }
  return false
}

/** Both input and output counters present — a full turn observation, not a half. */
function hasCompleteCounters(record: CanonicalRecord): boolean {
  const hasInput = record.tokens.freshInput > 0 || record.tokens.reportedInput > 0
  const hasOutput = record.tokens.output > 0 || record.tokens.reportedOutput > 0
  return hasInput && hasOutput
}

/**
 * Complete Cursor Agent observation: both input and output counters present.
 * Partials (request/response halves, incomplete agent rows) assign to at most
 * one such complete so they cannot transitively bridge conflicting turns.
 */
function isCompleteCursorAgentObservation(record: CanonicalRecord): boolean {
  return isCursorAgentCollector(record) && hasCompleteCounters(record)
}

/**
 * Complete Cursor IDE observation: a full turn from the IDE collector, not a
 * request/response half. Conflicting completes must not all subset-join one
 * Agent anchor (review): each anchor takes at most one best IDE match.
 */
function isCompleteCursorIdeObservation(record: CanonicalRecord): boolean {
  return isCursorCollector(record) && hasCompleteCounters(record)
}

/**
 * Issue #231: within one evidenced Cursor twin session and skew window, join
 * file-sourced rows whose counters differ but are complementary or subset-
 * related. Each partial assigns to at most one complete Cursor Agent turn;
 * each Agent turn accepts at most one complete Cursor IDE match. Components
 * collapse to the fuller keeper with per-dimension max counters. Content from
 * dropped siblings transplants like the exact-counter donor path. Identical
 * vectors are excluded so same-kind retries stay intact.
 */
function collapseOverlapFileJoins(
  records: readonly CanonicalRecord[],
  shareKey: string | undefined,
  dropped: Set<CanonicalRecord>,
  transplant: Map<CanonicalRecord, CanonicalRecord>,
  tokenOverride: Map<CanonicalRecord, TokenUsage>,
): void {
  const bySession = new Map<string, CanonicalRecord[]>()
  for (const record of records) {
    if (dropped.has(record)) continue
    if (record.op !== 'llm.invoke') continue
    if (!isFileSource(record.source)) continue
    if (isUnreportedCounters(record.tokens)) continue
    const key = sessionKeyOf(record, shareKey)
    const list = bySession.get(key) ?? []
    list.push(record)
    bySession.set(key, list)
  }

  for (const turns of bySession.values()) {
    // Inert unless both Cursor collectors appear in this session group.
    if (!hasEvidencedCursorTwinShare(turns)) continue
    const ordered = [...turns].sort((a, b) => cachedTimestampMs(a) - cachedTimestampMs(b))
    let cluster: CanonicalRecord[] = []
    const closeCluster = () => {
      collapseOverlapCluster(cluster, dropped, transplant, tokenOverride)
      cluster = []
    }
    for (const record of ordered) {
      const first = cluster[0]
      const exceedsSpan = first !== undefined && cachedTimestampMs(record) - cachedTimestampMs(first) > TWIN_TURN_MAX_SKEW_MS
      if (exceedsSpan) closeCluster()
      cluster.push(record)
    }
    closeCluster()
  }
}

/**
 * Bounded turn matching: each complete Cursor Agent observation is a turn
 * anchor. Request/response halves join at most one anchor they can
 * complement/subset with (nearest in time). Complete Cursor IDE observations
 * are full turns, not halves: each anchor accepts at most its nearest
 * joinable IDE match; any other conflicting complete IDE stays its own turn.
 * Partials never union two Agent completes — the join relation is not
 * transitive, and union-find would treat it as if it were.
 */
function collapseOverlapCluster(
  cluster: readonly CanonicalRecord[],
  dropped: Set<CanonicalRecord>,
  transplant: Map<CanonicalRecord, CanonicalRecord>,
  tokenOverride: Map<CanonicalRecord, TokenUsage>,
): void {
  if (cluster.length < 2) return

  const completes = cluster.filter(isCompleteCursorAgentObservation)
  if (completes.length === 0) return

  const groups = new Map<CanonicalRecord, CanonicalRecord[]>()
  for (const complete of completes) groups.set(complete, [complete])

  // Halves and incomplete rows only — a complete IDE turn is not a partial.
  const partials = cluster
    .filter((record) => !isCompleteCursorAgentObservation(record) && !isCompleteCursorIdeObservation(record))
    .sort((a, b) => cachedTimestampMs(a) - cachedTimestampMs(b))

  for (const partial of partials) {
    let best: CanonicalRecord | undefined
    let bestDist = Number.POSITIVE_INFINITY
    for (const complete of completes) {
      if (!canJoinDistinctCounters(partial.tokens, complete.tokens)) continue
      const dist = Math.abs(cachedTimestampMs(partial) - cachedTimestampMs(complete))
      if (best === undefined || dist < bestDist) {
        best = complete
        bestDist = dist
        continue
      }
      if (dist > bestDist) continue
      // Time tie: prefer the earlier complete for stable assignment.
      if (cachedTimestampMs(complete) < cachedTimestampMs(best)) best = complete
    }
    if (best === undefined) continue
    groups.get(best)!.push(partial)
  }

  // At most one complete IDE per Agent anchor (nearest joinable). Remaining
  // complete IDE turns stay separate — never silently dropped into the max.
  const availableIdes = new Set(cluster.filter(isCompleteCursorIdeObservation))
  const anchorsByTime = [...completes].sort((a, b) => cachedTimestampMs(a) - cachedTimestampMs(b))
  for (const complete of anchorsByTime) {
    let best: CanonicalRecord | undefined
    let bestDist = Number.POSITIVE_INFINITY
    for (const ide of availableIdes) {
      if (!canJoinDistinctCounters(ide.tokens, complete.tokens)) continue
      const dist = Math.abs(cachedTimestampMs(ide) - cachedTimestampMs(complete))
      if (best === undefined || dist < bestDist) {
        best = ide
        bestDist = dist
        continue
      }
      if (dist > bestDist) continue
      if (cachedTimestampMs(ide) < cachedTimestampMs(best)) best = ide
    }
    if (best === undefined) continue
    availableIdes.delete(best)
    groups.get(complete)!.push(best)
  }

  for (const members of groups.values()) {
    if (members.length < 2) continue
    let keeper = members[0]!
    for (const candidate of members.slice(1)) {
      keeper = preferFullerKeeper(keeper, candidate)
    }
    let mergedTokens = keeper.tokens
    for (const member of members) {
      if (member === keeper) continue
      mergedTokens = maxTokens(mergedTokens, member.tokens)
      dropped.add(member)
      if (!hasParts(member)) continue
      const existing = transplant.get(keeper)
      transplant.set(keeper, existing === undefined ? member : mergeDonors(existing, member))
    }
    // Only stamp an override when the max vector differs from the keeper's own
    // (TOKEN_DIMS or reasoning — maxTokens merges reasoning too).
    if (
      tokenDims(mergedTokens).some((value, i) => value !== (tokenDims(keeper.tokens)[i] ?? 0)) ||
      (mergedTokens.reasoning ?? 0) !== (keeper.tokens.reasoning ?? 0)
    ) {
      tokenOverride.set(keeper, mergedTokens)
    }
  }
}

/**
 * Collapse one proximity cluster: when it holds both source kinds, each
 * file row is treated as the same turn observed twice and drops; content
 * transplants onto keepers paired nearest-in-time (review M2) so no turn's
 * file content is silently discarded with a sibling's duplicate.
 *
 * In a file-only session (no OTLP rows), duplicate file rows for the same
 * turn with identical counters are also collapsed onto the earliest file row
 * so file-only sessions never double-count turns or tokens (#232).
 *
 * Issue #232 reports file rows arriving 2x per turn, so donors can outnumber
 * the rows that receive them. Those surplus rows are not provably duplicates of
 * anything — they carry content — so their content is merged into the keeper
 * nearest in time rather than dropped with the row. Dropping the row itself is
 * still correct and is what keeps the counters counted once: precedence gives
 * counters to the OTel row, so a retained file row would count them twice.
 */
function collapseCluster(
  cluster: readonly CanonicalRecord[],
  dropped: Set<CanonicalRecord>,
  transplant: Map<CanonicalRecord, CanonicalRecord>,
): void {
  if (cluster.length === 0) return
  const otels = cluster.filter((record) => !isFileSource(record.source))
  const files = cluster.filter((record) => isFileSource(record.source))
  if (otels.length === 0) {
    if (files.length <= 1) return
    // Only collapse duplicate file rows for Claude Desktop where request/response
    // pairs produce twin file records for one turn (#232). For other file sources,
    // identical counters within 60 s could be genuine retries and must be preserved
    // per ADR 0009 D4.
    if (!files.every((file) => file.source === 'codeburn/claude-desktop' || file.harness === 'claude-desktop')) {
      return
    }
    const sortedFiles = [...files].sort((a, b) => cachedTimestampMs(a) - cachedTimestampMs(b))
    const keeper = sortedFiles[0]!
    let donor: CanonicalRecord | undefined
    for (let i = 1; i < sortedFiles.length; i++) {
      const file = sortedFiles[i]!
      dropped.add(file)
      if (hasParts(file)) {
        donor = donor === undefined ? file : mergeDonors(donor, file)
      }
    }
    if (donor !== undefined) {
      transplant.set(keeper, donor)
    }
    return
  }
  if (files.length === 0) return
  for (const file of files) dropped.add(file)
  // Pairing is OTel-centric in time order, needy keepers first (review M2).
  // Donor-centric pairing ("each file takes its nearest free OTel") misfires
  // on the #232 shape: fA2's true mate otelA is already taken, so it would
  // pair otelB and hand the other turn's parts across. Letting each keeper in
  // turn take its nearest free donor keeps fA1/fA2 on otelA and fB1/fB2 on
  // otelB. Needy keepers choose first so a content-less turn is never left
  // bare while a content-carrying neighbour absorbs its donor.
  const donors = files
    .filter((file) => hasParts(file))
    .sort((a, b) => cachedTimestampMs(a) - cachedTimestampMs(b))
  const free = new Set(donors)
  const nearestFree = (moment: number): CanonicalRecord | undefined => {
    let best: CanonicalRecord | undefined
    for (const donor of donors) {
      if (!free.has(donor)) continue
      if (best === undefined || Math.abs(cachedTimestampMs(donor) - moment) < Math.abs(cachedTimestampMs(best) - moment)) {
        best = donor
      }
    }
    return best
  }
  const keepers = [
    ...otels.filter((otel) => !hasParts(otel)).sort((a, b) => cachedTimestampMs(a) - cachedTimestampMs(b)),
    ...otels.filter((otel) => hasParts(otel)).sort((a, b) => cachedTimestampMs(a) - cachedTimestampMs(b)),
  ]
  for (const keeper of keepers) {
    const donor = nearestFree(cachedTimestampMs(keeper))
    if (donor === undefined) break
    free.delete(donor)
    transplant.set(keeper, donor)
  }
  // Surplus donors (the 2x-per-turn shape) still drop — their counters are
  // the same turn — but merge into the keeper nearest to them in time rather
  // than vanishing with the duplicate.
  for (const donor of [...free].sort((a, b) => cachedTimestampMs(a) - cachedTimestampMs(b))) {
    let nearest = otels[0]!
    for (const keeper of otels) {
      if (Math.abs(cachedTimestampMs(keeper) - cachedTimestampMs(donor)) < Math.abs(cachedTimestampMs(nearest) - cachedTimestampMs(donor))) {
        nearest = keeper
      }
    }
    const existing = transplant.get(nearest)
    transplant.set(nearest, existing === undefined ? donor : mergeDonors(existing, donor))
  }
}

/** Two file rows of one turn, fused into the single donor the keeper receives. */
function mergeDonors(first: CanonicalRecord, second: CanonicalRecord): CanonicalRecord {
  const firstParts = first.parts ?? []
  const secondParts = second.parts ?? []
  const parts = [...firstParts, ...secondParts]
  // Content is the collapsed form of exactly the parts (review): derive it
  // from the fused parts rather than first-wins spreading, which drops the
  // second donor's text whenever both share a bucket key.
  return {
    ...first,
    parts,
    content: {
      ...first.content,
      ...second.content,
      ...(parts.length > 0 ? contentFromParts(parts) : {}),
    },
  }
}
