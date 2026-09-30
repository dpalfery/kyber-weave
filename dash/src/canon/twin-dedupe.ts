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
// Matching is exact-counter only: same-turn observations whose counters
// differ (e.g. the cursor twin's overlapping output figures, issue #231) are
// left alone, as are turns that reported no usage at all (their shared
// all-zero key identifies nothing). The canonical architecture states this
// boundary where the contract is described.

/**
 * Maximum timestamp span for rows to be the same turn observed twice.
 * Live skew measured under 10 s (collectors flush on different schedules);
 * 60 s bounds the risk of merging genuinely repeated identical turns while
 * tolerating that skew. The bound applies to the cluster's span from its
 * first row, not just adjacency (review): a chain of near-identical turns
 * must not merge into one unbounded cluster. Named, not inlined, so a wrong
 * merge window is traceable to one assumption instead of looking like a
 * measurement.
 */
export const TWIN_TURN_MAX_SKEW_MS = 60_000

type CounterKey = string

function counterKey(tokens: CanonicalRecord['tokens']): CounterKey {
  return [
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
 * counters cluster by timestamp proximity within a bounded span; a cluster
 * holding both source kinds is one turn observed twice, so its file rows
 * drop and its content transplants pairwise onto the content-less OTel rows
 * nearest in time, with surplus rows' unique content keys merged rather
 * than lost. Applied by the derived-layer builders (`buildSessions`,
 * `buildRuns`, `buildFindings`) after shares merge, never at ingest — raw
 * records keep both collectors' rows as provenance.
 */
export function dedupeTwinTurns(records: readonly CanonicalRecord[]): CanonicalRecord[] {
  const byCounter = new Map<CounterKey, CanonicalRecord[]>()
  for (const record of records) {
    if (record.op !== 'llm.invoke') continue
    const list = byCounter.get(counterKey(record.tokens)) ?? []
    list.push(record)
    byCounter.set(counterKey(record.tokens), list)
  }

  const dropped = new Set<CanonicalRecord>()
  const transplant = new Map<CanonicalRecord, CanonicalRecord>()
  const surplusContent = new Map<CanonicalRecord, CanonicalRecord[]>()
  for (const turns of byCounter.values()) {
    const ordered = [...turns].sort((a, b) => timestampMs(a) - timestampMs(b))
    // Clusters are bounded by span, not just adjacency (review): rows join
    // while they stay within the skew of the cluster's first row. A chain of
    // near-identical turns must not merge into one hour-long cluster — a row
    // that only touches the cluster through other file rows is kept.
    let cluster: CanonicalRecord[] = []
    let start = 0
    const closeCluster = () => {
      collapseCluster(cluster, dropped, transplant, surplusContent)
      cluster = []
    }
    for (const record of ordered) {
      const at = timestampMs(record)
      if (cluster.length > 0 && at - start > TWIN_TURN_MAX_SKEW_MS) {
        closeCluster()
      }
      if (cluster.length === 0) start = at
      cluster.push(record)
    }
    closeCluster()
  }

  return records.flatMap((record) => {
    if (dropped.has(record)) return []
    const donor = transplant.get(record)
    const surplus = surplusContent.get(record) ?? []
    if (donor === undefined && surplus.length === 0) return [record]
    // ADR 0009 D4: the OTel row's counters stand; file content fills only
    // what the OTel row did not carry — paired parts first, then content
    // keys unique to surplus rows (review), never a duplicated part.
    const seen = new Set(
      [...(donor?.parts ?? []), ...(record.parts ?? [])].map((part) => `${part.part}\u0000${part.text}`),
    )
    const extraParts = surplus
      .flatMap((row) => [...(row.parts ?? [])])
      .filter((part) => {
        const key = `${part.part}\u0000${part.text}`
        if (seen.has(key)) return false
        seen.add(key)
        return true
      })
    const mergedContent: Record<string, unknown> = { ...record.content }
    for (const row of [...(donor !== undefined ? [donor] : []), ...surplus]) {
      for (const [key, value] of Object.entries(row.content)) {
        if (!(key in mergedContent)) mergedContent[key] = value
      }
    }
    return [
      {
        ...record,
        ...((donor?.parts !== undefined && donor.parts.length > 0) || extraParts.length > 0
          ? { parts: [...(donor?.parts ?? []), ...extraParts] }
          : {}),
        content: { ...mergedContent, ...record.content } as CanonicalRecord['content'],
      },
    ]
  })
}

/**
 * Collapse one proximity cluster: when it holds both source kinds, every
 * file row is the same turn observed twice and drops; content transplants
 * one-to-one onto the nearest content-less OTel rows so no turn's file
 * content is silently discarded with a sibling's duplicate.
 */
function collapseCluster(
  cluster: readonly CanonicalRecord[],
  dropped: Set<CanonicalRecord>,
  transplant: Map<CanonicalRecord, CanonicalRecord>,
  surplusContent: Map<CanonicalRecord, CanonicalRecord[]>,
): void {
  if (cluster.length === 0) return
  // Zero-counter rows share one key by construction (`0:0:0:0:0:0`) across
  // unrelated turns: with nothing to match on, the whole cluster is kept.
  if (cluster.every((record) => isUnreportedCounters(record.tokens))) return
  const otels = cluster.filter((record) => !isFileSource(record.source))
  const files = cluster.filter((record) => isFileSource(record.source))
  if (otels.length === 0 || files.length === 0) return
  for (const file of files) dropped.add(file)
  const needy = otels.filter((otel) => !hasParts(otel))
  const donors = files.filter((file) => hasParts(file))
  for (let i = 0; i < Math.min(needy.length, donors.length); i++) {
    transplant.set(needy[i]!, donors[i]!)
  }
  // Surplus file rows (the 2×-per-turn shape of issue #232) still drop —
  // their counters are the same turn — but content keys unique to them merge
  // onto the first keeper instead of vanishing with the duplicate (review).
  const paired = new Set(transplant.values())
  const surplus = donors.filter((donor) => !paired.has(donor))
  if (surplus.length > 0) {
    const keeper = otels[0]!
    const existing = surplusContent.get(keeper) ?? []
    surplusContent.set(keeper, [...existing, ...surplus])
  }
}
