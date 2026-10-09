// Central clean for KyberDash (issue #312): pause ingestion, wipe the store
// for a harness scope (or all), project the derived caches, re-ingest the
// scope from source logs, and resume ingestion — resuming in a `finally` so a
// failing wipe cannot wedge the collector shut. Thin callers (the `dash
// clean` CLI and `POST /api/kyber/clean`) reach this module; no clean logic
// lives in either surface.
//
// Why ports and not imports: the pause probe talks HTTP to a sibling process,
// the projection and re-ingest are the orchestrator's own pipeline. Injecting
// them keeps this module unit-testable without a receiver or a filesystem,
// and keeps the production wiring in one place (`portsForClean` below).

import { projectCanonicalStore } from '../canon/projection.js'
import { normalizeHarnessName } from '../canon/measurability.js'
import { descriptorFor } from '../refresh/registry.js'
import { refreshHarnessSources } from '../refresh/orchestrator.js'
import type { CanonStore, StoreWipeReport } from '../canon/store.js'
import { pauseReceiver, resumeReceiver, type ReceiverPauseOutcome } from './pause.js'

/** What the caller wants wiped. `reingestWeeks: null` skips re-ingestion. */
export type CleanScope = {
  all?: boolean
  harnesses?: readonly string[]
  reingestWeeks?: number | null
}

/**
 * The HTTP body `POST /api/kyber/clean` accepts. `confirm` is the browser's
 * explicit irreversible-action consent; without it the route answers 400 and
 * nothing is wiped.
 */
export type CleanRequest = CleanScope & {
  confirm?: boolean
}

/** Seams `cleanDatabase` drives; tests inject fakes, production uses `portsForClean`. */
export type CleanPorts = {
  pauseIngestion: () => Promise<ReceiverPauseOutcome>
  resumeIngestion: () => Promise<void>
  project: (store: CanonStore) => Promise<unknown>
  reingest: (input: { harnesses?: readonly string[]; historyWeeks: number }) => Promise<unknown>
}

export type CleanReport = {
  harnesses: string[]
  wipe: StoreWipeReport
  reingested: boolean
  historyWeeks: number | null
}

/** Default re-ingest window: the last 7 days (issue #312). */
export const DEFAULT_CLEAN_REINGEST_WEEKS = 1

/**
 * Upper bound on the re-ingest window: one year. Re-ingestion scans source
 * logs week by week, so an unbounded window turns a typo into a
 * multi-millennia self-inflicted DoS (F6). The route and CLI parsers enforce
 * the same bound so bad input is rejected before anything is wiped.
 */
export const MAX_CLEAN_REINGEST_WEEKS = 52

/**
 * Run one user-initiated clean: pause ingestion, wipe, project, re-ingest,
 * resume. Resume runs in a `finally`; the only path that skips it is a pause
 * that itself failed, in which case nothing was wiped and there is nothing
 * to resume from.
 */
export async function cleanDatabase(
  store: CanonStore,
  scope: CleanScope,
  ports: CleanPorts,
): Promise<CleanReport> {
  const canonical = canonicalScope(scope)
  const historyWeeks = scope.reingestWeeks === undefined
    ? DEFAULT_CLEAN_REINGEST_WEEKS
    : scope.reingestWeeks
  if (historyWeeks !== null && (!Number.isSafeInteger(historyWeeks) || historyWeeks < 1 || historyWeeks > MAX_CLEAN_REINGEST_WEEKS)) {
    throw new Error(`cleanDatabase: reingestWeeks must be a positive integer between 1 and ${MAX_CLEAN_REINGEST_WEEKS}, or null`)
  }

  await ports.pauseIngestion()
  try {
    const wipe = canonical === null ? store.wipeAll() : store.wipeHarnesses(canonical)
    await ports.project(store)
    let reingested = false
    if (historyWeeks !== null) {
      await ports.reingest(
        canonical === null
          ? { historyWeeks }
          : { harnesses: canonical, historyWeeks },
      )
      reingested = true
    }
    return {
      harnesses: canonical ?? ['*'],
      wipe,
      reingested,
      historyWeeks,
    }
  } finally {
    await ports.resumeIngestion()
  }
}

/**
 * Resolve the wipe scope to canonical harness ids, or null for wipe-all.
 * Throws on an empty scope (no `--all`, no `--harness`) so a bare `dash
 * clean` can never wipe by accident, and on an ambiguous scope (`--all`
 * with `--harness`) so a destructive op never fails open to a wider wipe.
 */
function canonicalScope(scope: CleanScope): string[] | null {
  if (scope.all === true) {
    if ((scope.harnesses ?? []).length > 0) {
      throw new Error('cleanDatabase: ambiguous scope — pass --all or --harness, not both')
    }
    return null
  }
  const harnesses = [...new Set((scope.harnesses ?? []).map((h) => normalizeHarnessName(h)))]
  if (harnesses.length === 0) {
    throw new Error('cleanDatabase: scope selects nothing — pass --all or at least one --harness')
  }
  return harnesses
}

/** Production ports: the sibling receiver on 4318 and this process's pipelines. */
export function portsForClean(store: CanonStore): CleanPorts {
  return {
    pauseIngestion: () => pauseReceiver(),
    resumeIngestion: () => resumeReceiver(),
    project: (target) => projectCanonicalStore(target),
    reingest: ({ harnesses, historyWeeks }) =>
      refreshHarnessSources(store, harnesses === undefined ? undefined : {
        getAllProviders: async () => (await import('../synth/provider.js')).getAllProviders(),
        descriptors: harnesses.flatMap((harness) => {
          const descriptor = descriptorFor(harness)
          return descriptor === undefined ? [] : [descriptor]
        }),
      }, { historyWeeks, trigger: 'cli' }),
  }
}
