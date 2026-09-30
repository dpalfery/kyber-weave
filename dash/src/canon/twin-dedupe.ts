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
 * Maximum timestamp gap for two rows to be the same turn observed twice.
 * Live skew measured under 10 s (collectors flush on different schedules);
 * 60 s bounds the risk of merging genuinely repeated identical turns while
 * tolerating that skew. Named, not inlined, so a wrong merge window is
 * traceable to one assumption instead of looking like a measurement.
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
    const list = byCounter.get(counterKey(record.tokens)) ?? []
    list.push(record)
    byCounter.set(counterKey(record.tokens), list)
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
      const previous = cluster[cluster.length - 1]
      if (previous !== undefined && Math.abs(timestampMs(record) - timestampMs(previous)) > TWIN_TURN_MAX_SKEW_MS) {
        closeCluster()
      }
      cluster.push(record)
    }
    closeCluster()
  }

  return records.flatMap((record) => {
    if (dropped.has(record)) return []
    const donor = transplant.get(record)
    if (donor === undefined) return [record]
    // ADR 0009 D4: the OTel row's counters stand; the file row's content
    // fills only what the OTel row did not carry.
    return [
      {
        ...record,
        ...(donor.parts !== undefined && donor.parts.length > 0 ? { parts: donor.parts } : {}),
        content: { ...donor.content, ...record.content },
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
}
