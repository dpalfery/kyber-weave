// Persist deterministic waste findings for every derived run (Task E2, ADR 0008, D6).
//
// The detectors in `../analysis/findings.js` are pure: they read canonical records and
// return ranked findings. This module is the build stage that runs them over the store
// and writes the `finding` table. Without it the detector suite never executes and the
// dashboard reports "no findings detected" for a corpus that was never examined — the
// silence-as-health failure ADR 0011 forbids.
//
// `finding` is a rebuildable cache over `records` in the same sense as `run` and
// `execution`: a rebuild is authoritative, and rows the detectors no longer emit are pruned.

import { detectFindings } from '../analysis/findings.js'
import { catalogWindowForRecords } from './model-window-catalog.js'
import { dedupeTwinTurns } from './twin-dedupe.js'
import { CanonStore, reduceRecordPayloads } from './store.js'
import type { CanonicalRecord } from './types.js'

export type BuildFindingsReport = {
  /** Runs the detector suite examined. */
  runsExamined: number
  /** Findings written to the `finding` table. */
  findingsBuilt: number
  /** Stale findings removed because no detector re-emitted them. */
  pruned: number
}

/**
 * Run every finding detector over each derived run and persist the ranked results.
 *
 * Records are gathered per run through its executions' sessions, so a finding's evidence
 * links stay inside the run that produced them. The owning harness is carried in the
 * finding payload — it round-trips through `toFinding`, and it is what lets the dashboard
 * scope findings to one harness instead of showing every run's findings on every tab.
 */
export function buildFindings(store: CanonStore): BuildFindingsReport {
  const steps = buildFindingsSteps(store)
  let step = steps.next()
  while (!step.done) step = steps.next()
  return step.value
}

/**
 * The same build, handing the event loop back between runs.
 *
 * WHY two shapes: the projection over a real corpus spends seconds in the
 * detector sweep, and a rebuild that holds the event loop starves every
 * pending HTTP handler. The build itself is one implementation — a generator
 * that pauses between runs — and the two drivers below decide whether a pause
 * is a loop turn (here) or nothing at all (the synchronous `buildFindings`).
 */
export async function buildFindingsYielding(
  store: CanonStore,
  yieldToLoop: () => Promise<void>,
): Promise<BuildFindingsReport> {
  const steps = buildFindingsSteps(store)
  let step = steps.next()
  while (!step.done) {
    await yieldToLoop()
    step = steps.next()
  }
  return step.value
}

/**
 * The findings build as a sequence of steps, pausing between runs.
 *
 * The pause point is the generator's `yield`: the synchronous driver steps
 * over it, the yielding driver awaits a loop turn there.
 */
function* buildFindingsSteps(store: CanonStore): Generator<void, BuildFindingsReport> {
  const report: BuildFindingsReport = { runsExamined: 0, findingsBuilt: 0, pruned: 0 }
  const builtIds = new Set<string>()
  const identities = store.sessionIdentities()

  for (const run of store.listRuns()) {
    const records: CanonicalRecord[] = []
    for (const execution of store.listExecutions(run.runId)) {
      const sessionId = execution.sessionId ?? execution.executionId
      const share = identities.shareOf(sessionId)
      const shareRecords =
        share === undefined ? store.recordsForSession(sessionId) : store.recordsForShare(share.key, share.harness)
      // The detectors read a few attributes out of each payload, and a
      // session's decompressed payloads are the projection's working set — the
      // measured corpus held 1.5 GB of them at once. The payloads are reduced
      // to the attributes the readers look up before the twin collapse reads
      // them, and released one record at a time.
      reduceRecordPayloads(shareRecords)
      // Twin collectors describe the same turns twice (issue #182, ADR 0009
      // D4): detectors that count or sum across turns (duplicate-tool-call,
      // unbounded-delegation) would read one conversation as two. Scoped per
      // execution, never across them: two sessions' identical turns are two
      // genuine turns, and the detector regroups by session below.
      records.push(...dedupeTwinTurns(shareRecords, share?.key ?? sessionId))
    }
    if (records.length === 0) continue

    report.runsExamined += 1

    const findings = detectFindings({
      runId: run.runId,
      records,
      ...(run.outcome === undefined ? {} : { outcome: run.outcome }),
      // Runs are grouped per harness, so a run holds one share of a split key
      // and its records alone cannot say the key was split, nor which id the
      // share was given. The detector names its sessions from the same table.
      sessionIdentities: identities,
      // Catalog only. A reported or declared window is already decided inside
      // the detector; returning those here would let a second derivation
      // disagree with `contextLimitOf`.
      resolveWindow: (groupRecords) => catalogWindowForRecords(groupRecords, store),
    })

    for (const finding of findings) {
      // `upsertFinding` only derives a payload when the finding carries none, so the
      // explanatory fields have to be restated here alongside the harness.
      store.upsertFinding({
        ...finding,
        payload: {
          ...(finding.payload ?? {}),
          ...(finding.measurementClass === undefined ? {} : { measurementClass: finding.measurementClass }),
          ...(finding.confidenceBasis === undefined ? {} : { confidenceBasis: finding.confidenceBasis }),
          ...(finding.whatWouldRaiseIt === undefined ? {} : { whatWouldRaiseIt: finding.whatWouldRaiseIt }),
          harness: run.harness,
        },
      })
      builtIds.add(finding.id)
      report.findingsBuilt += 1
    }
    yield
  }

  // A rebuild is authoritative. Anything the detectors did not re-emit is stale.
  for (const existing of store.listFindings()) {
    if (builtIds.has(existing.id)) continue
    store.deleteFinding(existing.id)
    report.pruned += 1
  }

  return report
}
