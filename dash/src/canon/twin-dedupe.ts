import { isFileSource } from './measurability.js'
import type { CanonicalRecord } from './types.js'

// Same-turn dedupe for twin front-end collectors (issues #181/#182, ADR 0009 D4).
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
// Matching is exact-counter only: same-turn observations whose counters
// differ (e.g. the cursor twin's overlapping output figures, issue #231) are
// left alone, as are turns that reported no usage at all — every such turn
// stamps the same all-zero key, which identifies nothing, so those rows are
// never bucketed for matching in the first place. The canonical architecture
// states this boundary where the contract is described.

/**
 * Maximum timestamp gap for two rows to be the same turn observed twice.
 * Live skew measured under 10 s (collectors flush on different schedules);
 * 60 s bounds the risk of merging genuinely repeated identical turns while
 * tolerating that skew. Named, not inlined, so a wrong merge window is
 * traceable to one assumption instead of looking like a measurement.
 *
 * The gap bounds a cluster's total span as well as each step between
 * neighbours. Bounding only the step would be single-linkage chaining: rows
 * 50 s apart form an unbounded run that a session re-reporting identical
 * counters every 50 s would collapse into one turn.
 */
export const TWIN_TURN_MAX_SKEW_MS = 60_000

type CounterKey = string

function counterKey(record: CanonicalRecord): CounterKey {
  const { tokens } = record
  return [
    record.sessionId ?? '',
    tokens.freshInput,
    tokens.cacheRead,
    tokens.cacheCreation,
    tokens.output,
    tokens.reportedInput,
    tokens.reportedOutput,
  ].join(':')
}

function timestampMs(record: CanonicalRecord): number {
  return Date.parse(typeof record.timestamp === 'string' ? record.timestamp : record.timestamp.toISOString())
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

/**
 * Collapse same-turn twin observations within one canonical-harness share.
 * Pure: input order is preserved, no record is mutated. Rows sharing exact
 * counters cluster by timestamp proximity (consecutive gaps within
 * `TWIN_TURN_MAX_SKEW_MS`); a cluster holding both source kinds is one turn
 * observed twice, so its file rows drop and its content transplants pairwise
 * onto the content-less OTel rows nearest in time. Applied by the
 * derived-layer builders (`buildSessions`, `buildRuns`, `buildFindings`)
 * after shares merge, never at ingest — raw records keep both collectors'
 * rows as provenance.
 */
export function dedupeTwinTurns(records: readonly CanonicalRecord[]): CanonicalRecord[] {
  const byCounter = new Map<CounterKey, CanonicalRecord[]>()
  for (const record of records) {
    if (record.op !== 'llm.invoke') continue
    // An all-zero row shares one key with every other all-zero row in the
    // share, which identifies nothing. Excluding them here means they are never
    // clustered at all, rather than relying on a later guard to un-merge them.
    if (isUnreportedCounters(record.tokens)) continue
    const key = counterKey(record)
    const list = byCounter.get(key) ?? []
    list.push(record)
    byCounter.set(key, list)
  }

  const dropped = new Set<CanonicalRecord>()
  const transplant = new Map<CanonicalRecord, CanonicalRecord>()
  for (const turns of byCounter.values()) {
    const ordered = [...turns].sort((a, b) => timestampMs(a) - timestampMs(b))
    let cluster: CanonicalRecord[] = []
    const closeCluster = () => {
      collapseCluster(cluster, dropped, transplant)
      cluster = []
    }
    for (const record of ordered) {
      // Close on either bound: the gap to the neighbour, and the span from the
      // cluster's first row. The second is what stops adjacent pairs from
      // chaining into an arbitrarily long run.
      const previous = cluster[cluster.length - 1]
      const first = cluster[0]
      const exceedsStep = previous !== undefined && Math.abs(timestampMs(record) - timestampMs(previous)) > TWIN_TURN_MAX_SKEW_MS
      const exceedsSpan = first !== undefined && timestampMs(record) - timestampMs(first) > TWIN_TURN_MAX_SKEW_MS
      if (exceedsStep || exceedsSpan) closeCluster()
      cluster.push(record)
    }
    closeCluster()
  }

  return records.flatMap((record) => {
    if (dropped.has(record)) return []
    const donor = transplant.get(record)
    if (donor === undefined) return [record]
    // ADR 0009 D4: the OTel row's counters stand; the file row's content
    // fills only what the OTel row did not carry — keeper parts first, then
    // donor parts not already present, so a paired non-needy keeper never
    // loses its own content to its twin's (review M2).
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
        ...((donor.parts !== undefined && donor.parts.length > 0) || extraParts.length > 0
          ? { parts: [...(record.parts ?? []), ...extraParts] }
          : {}),
        content: { ...donor.content, ...record.content },
      },
    ]
  })
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
    .sort((a, b) => timestampMs(a) - timestampMs(b))
  const free = new Set(donors)
  const nearestFree = (moment: number): CanonicalRecord | undefined => {
    let best: CanonicalRecord | undefined
    for (const donor of donors) {
      if (!free.has(donor)) continue
      if (best === undefined || Math.abs(timestampMs(donor) - moment) < Math.abs(timestampMs(best) - moment)) {
        best = donor
      }
    }
    return best
  }
  const keepers = [
    ...otels.filter((otel) => !hasParts(otel)).sort((a, b) => timestampMs(a) - timestampMs(b)),
    ...otels.filter((otel) => hasParts(otel)).sort((a, b) => timestampMs(a) - timestampMs(b)),
  ]
  for (const keeper of keepers) {
    const donor = nearestFree(timestampMs(keeper))
    if (donor === undefined) break
    free.delete(donor)
    transplant.set(keeper, donor)
  }
  // Surplus donors (the 2x-per-turn shape) still drop — their counters are
  // the same turn — but merge into the keeper nearest to them in time rather
  // than vanishing with the duplicate.
  for (const donor of [...free].sort((a, b) => timestampMs(a) - timestampMs(b))) {
    let nearest = otels[0]!
    for (const keeper of otels) {
      if (Math.abs(timestampMs(keeper) - timestampMs(donor)) < Math.abs(timestampMs(nearest) - timestampMs(donor))) {
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
  return {
    ...first,
    parts: [...firstParts, ...secondParts],
    content: { ...second.content, ...first.content },
  }
}
