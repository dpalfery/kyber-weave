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
// ADR 0009 D4 states the precedence: counters come from the OTel row, content
// comes from the file row when the OTel row carries no parts, and values are
// never summed across sources for the same turn.
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
// whose counters differ: within `TWIN_TURN_MAX_SKEW_MS`, file+file rows under a
// folded twin share that stand in a complementary or subset relation join into
// one turn. Joined counters take the per-dimension max (prefer the fuller row as
// keeper identity); they are never summed. Identical same-kind OTLP retries stay
// kept — the overlap join only bridges *different* counter vectors.

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
 * Pure: input order is preserved, no record is mutated. Rows sharing exact
 * counters cluster by timestamp proximity (span within `TWIN_TURN_MAX_SKEW_MS`);
 * a cluster holding both source kinds is one turn observed twice, so its file
 * rows drop and its content transplants pairwise onto the content-less OTel
 * rows nearest in time. A second pass (#231) joins file+file rows whose
 * counters differ but stand in a complementary/subset relation under the same
 * skew window, merging by per-dimension max onto the fuller keeper. Applied by
 * the derived-layer builders (`buildSessions`, `buildRuns`, `buildFindings`)
 * after shares merge, never at ingest — raw records keep both collectors'
 * rows as provenance.
 */
export function dedupeTwinTurns(records: readonly CanonicalRecord[], shareKey?: string): CanonicalRecord[] {
  const byCounter = new Map<CounterKey, CanonicalRecord[]>()
  for (const record of records) {
    if (record.op !== 'llm.invoke') continue
    // An all-zero row shares one key with every other all-zero row in the
    // share, which identifies nothing. Excluding them here means they are never
    // clustered at all, rather than relying on a later guard to un-merge them.
    if (isUnreportedCounters(record.tokens)) continue
    const key = counterKey(record, shareKey)
    const list = byCounter.get(key) ?? []
    list.push(record)
    byCounter.set(key, list)
  }

  const dropped = new Set<CanonicalRecord>()
  const transplant = new Map<CanonicalRecord, CanonicalRecord>()
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
    // ADR 0009 D4: the OTel row's counters stand; the file row's content
    // fills only what the OTel row did not carry — keeper parts first, then
    // donor parts not already present, so a paired non-needy keeper never
    // loses its own content to its twin's (review M2). For #231 file+file
    // joins the keeper already carries maxed counters via `tokenOverride`.
    const seen = new Set((record.parts ?? []).map((part) => `${part.part}\u0000${part.text}`))
    const extraParts = (donor.parts ?? []).filter((part) => {
      const key = `${part.part}\u0000${part.text}`
      if (seen.has(key)) return false
      seen.add(key)
      return true
    })
    return [
      {
        ...record,
        tokens,
        ...((donor.parts !== undefined && donor.parts.length > 0) || extraParts.length > 0
          ? { parts: [...(record.parts ?? []), ...extraParts] }
          : {}),
        content: { ...donor.content, ...record.content },
      },
    ]
  })
}

/**
 * Issue #231: within one session and skew window, join file-sourced rows whose
 * counters differ but are complementary or subset-related. Connected components
 * collapse to the fuller keeper with per-dimension max counters; content from
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

function collapseOverlapCluster(
  cluster: readonly CanonicalRecord[],
  dropped: Set<CanonicalRecord>,
  transplant: Map<CanonicalRecord, CanonicalRecord>,
  tokenOverride: Map<CanonicalRecord, TokenUsage>,
): void {
  if (cluster.length < 2) return

  // Union-find over complementary/subset edges (transitive via a bridging half).
  const parent = new Map<CanonicalRecord, CanonicalRecord>()
  const find = (record: CanonicalRecord): CanonicalRecord => {
    let root = record
    while (parent.get(root) !== undefined && parent.get(root) !== root) root = parent.get(root)!
    let walk = record
    while (walk !== root) {
      const next = parent.get(walk) ?? walk
      parent.set(walk, root)
      walk = next
    }
    return root
  }
  const union = (a: CanonicalRecord, b: CanonicalRecord): void => {
    const ra = find(a)
    const rb = find(b)
    if (ra === rb) return
    parent.set(ra, rb)
  }
  for (const record of cluster) parent.set(record, record)

  for (let i = 0; i < cluster.length; i++) {
    for (let j = i + 1; j < cluster.length; j++) {
      const left = cluster[i]!
      const right = cluster[j]!
      if (canJoinDistinctCounters(left.tokens, right.tokens)) union(left, right)
    }
  }

  const components = new Map<CanonicalRecord, CanonicalRecord[]>()
  for (const record of cluster) {
    const root = find(record)
    const list = components.get(root) ?? []
    list.push(record)
    components.set(root, list)
  }

  for (const members of components.values()) {
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
    // Only stamp an override when the max vector differs from the keeper's own.
    if (tokenDims(mergedTokens).some((value, i) => value !== (tokenDims(keeper.tokens)[i] ?? 0))) {
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
  if (otels.length === 0 || files.length === 0) return
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
    content: contentFromParts(parts),
  }
}
