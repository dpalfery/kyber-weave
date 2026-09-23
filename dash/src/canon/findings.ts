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
import { CanonStore } from './store.js'
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
  const report: BuildFindingsReport = { runsExamined: 0, findingsBuilt: 0, pruned: 0 }
  const builtIds = new Set<string>()

  for (const run of store.listRuns()) {
    const records: CanonicalRecord[] = []
    for (const execution of store.listExecutions(run.runId)) {
      records.push(...store.recordsForSession(execution.sessionId ?? execution.executionId))
    }
    if (records.length === 0) continue

    report.runsExamined += 1

    const findings = detectFindings({
      runId: run.runId,
      records,
      ...(run.outcome === undefined ? {} : { outcome: run.outcome }),
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
  }

  // A rebuild is authoritative. Anything the detectors did not re-emit is stale.
  for (const existing of store.listFindings()) {
    if (builtIds.has(existing.id)) continue
    store.deleteFinding(existing.id)
    report.pruned += 1
  }

  return report
}
