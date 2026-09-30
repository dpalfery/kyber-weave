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
// The rule is deliberately conservative: only a file row paired with a
// non-file row is provably the same turn observed twice. Two identical rows
// from the same source kind (two OTLP rows, two file rows) could be genuine
// retries and are always kept — dropping telemetry the rule cannot prove
// duplicated would trade a known over-count for a silent under-count.

/**
 * Maximum timestamp skew for two rows to be the same turn observed twice.
 * Live skew measured under 10 s (collectors flush on different schedules);
 * 60 s bounds the risk of merging genuinely repeated identical turns while
 * tolerating that skew. Named, not inlined, so a wrong merge window is
 * traceable to one assumption.
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

/**
 * Collapse same-turn twin observations within one canonical-harness share.
 * Pure: input order is preserved, no record is mutated. Applied by the
 * derived-layer builders (`buildSessions`, `buildRuns`, `buildFindings`)
 * after shares merge, never at ingest — raw records keep both collectors'
 * rows as provenance.
 */
export function dedupeTwinTurns(records: readonly CanonicalRecord[]): CanonicalRecord[] {
  const anchorByKey = new Map<CounterKey, number>()
  const fileRowsByKey = new Map<CounterKey, CanonicalRecord[]>()
  const otelRowsByKey = new Map<CounterKey, CanonicalRecord[]>()

  for (const record of records) {
    if (record.op !== 'llm.invoke') continue
    const key = counterKey(record.tokens)
    const seen = anchorByKey.get(key)
    const at = timestampMs(record)
    if (seen === undefined) {
      anchorByKey.set(key, at)
    } else if (Math.abs(at - seen) > TWIN_TURN_MAX_SKEW_MS) {
      // Same counters, far apart in time: a repeated identical turn, not a
      // twin observation. Each keeps its own key so neither is dropped.
      const apart = `${key}|${at}`
      anchorByKey.set(apart, at)
      if (isFileSource(record.source)) {
        const list = fileRowsByKey.get(apart) ?? []
        list.push(record)
        fileRowsByKey.set(apart, list)
      } else {
        const list = otelRowsByKey.get(apart) ?? []
        list.push(record)
        otelRowsByKey.set(apart, list)
      }
      continue
    }
    if (isFileSource(record.source)) {
      const list = fileRowsByKey.get(key) ?? []
      list.push(record)
      fileRowsByKey.set(key, list)
    } else {
      const list = otelRowsByKey.get(key) ?? []
      list.push(record)
      otelRowsByKey.set(key, list)
    }
  }

  // A file row is a twin duplicate exactly when its group also holds a
  // non-file row for the same counters within the skew window.
  const dropped = new Set<CanonicalRecord>()
  const transplant = new Map<CanonicalRecord, CanonicalRecord>()
  for (const [key, files] of fileRowsByKey) {
    const otels = otelRowsByKey.get(key)
    if (otels === undefined || otels.length === 0) continue
    const keeper = otels[0]!
    for (const file of files) dropped.add(file)
    if (!hasParts(keeper)) {
      const donor = files.find((file) => hasParts(file))
      if (donor !== undefined) transplant.set(keeper, donor)
    }
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
