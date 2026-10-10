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
import { importFolderHistory } from '../refresh/folder-import.js'
import { MAX_CLEAN_REINGEST_WEEKS } from '../refresh/limits.js'
import { resolveHarnessScope } from '../refresh/registry.js'
import type { CanonStore, StoreWipeReport } from '../canon/store.js'
import { pauseReceiver, resumeReceiver, type ReceiverPauseOutcome } from './pause.js'

/**
 * What the caller wants wiped, and whether a folder-history backfill follows.
 *
 * `reingestWeeks` undefined or null means no import at all: a clean is a
 * destructive op, so the default is the conservative one and re-ingestion is
 * something the caller has to ask for by name. A number n (1..MAX_CLEAN_REINGEST_WEEKS)
 * imports the last n weeks of source history for the cleaned scope.
 */
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

// Re-exported so callers (the CLI, the route) keep importing the bound from
// here; the declaration itself lives in the neutral `refresh/limits` leaf so
// the folder-history import the clean pipeline calls back into does not have to
// import it from here, which would close an import cycle.
export { MAX_CLEAN_REINGEST_WEEKS }

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
  // Undefined and null both mean "do not import". Defaulting to no import is the
  // safe choice for a destructive op: re-ingestion rescans source logs and can
  // take minutes, which nobody should inherit by omitting a flag.
  const historyWeeks = scope.reingestWeeks ?? null
  if (historyWeeks !== null && (!Number.isSafeInteger(historyWeeks) || historyWeeks < 1 || historyWeeks > MAX_CLEAN_REINGEST_WEEKS)) {
    throw new Error(`cleanDatabase: reingestWeeks must be a positive integer between 1 and ${MAX_CLEAN_REINGEST_WEEKS}, or null`)
  }

  await ports.pauseIngestion()
  try {
    const wipe = canonical === null ? store.wipeAll() : store.wipeHarnesses(canonical)
    await ports.project(store)
    let reingested = false
    // A scope the wipe understands but no folder exists for has nothing to import.
    // Skipped, and reported as not re-ingested — never widened to every harness, which is
    // what an empty harness list means to the folder import. (Unreachable through
    // canonicalScope today, since every known id comes from a descriptor; kept because
    // the two properties are separate and this one is the destructive one.)
    const importable =
      canonical === null || resolveHarnessScope(canonical).descriptors.length > 0
    if (historyWeeks !== null && importable) {
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
 * clean` can never wipe by accident, on an ambiguous scope (`--all`
 * with `--harness`) so a destructive op never fails open to a wider wipe, and
 * when no requested harness is known to the registry — so a wipe, a history
 * import and the report always share one non-empty list for a harness scope.
 *
 * <remarks>
 * Resolution is `refresh/registry.resolveHarnessScope`, the same one the folder
 * import and the route validation use, so a name a caller can type cannot mean
 * three different things depending on which surface it arrived through. A family
 * label (`codex`, what the tray's harness preference holds) expands to its member
 * ids here: the wipe matches stored raw names against the requested set and their
 * normalized forms, and `normalizeHarnessName('codex-cli')` is still `codex-cli`,
 * so wiping the bare family label would delete nothing while reporting success.
 * </remarks>
 */
function canonicalScope(scope: CleanScope): string[] | null {
  if (scope.all === true) {
    if ((scope.harnesses ?? []).length > 0) {
      throw new Error('cleanDatabase: ambiguous scope — pass --all or --harness, not both')
    }
    return null
  }
  const requested = [...new Set((scope.harnesses ?? []).map((h) => h.trim()))]
  if (requested.length === 0) {
    throw new Error('cleanDatabase: scope selects nothing — pass --all or at least one --harness')
  }
  // Drop harnesses the registry cannot place, before anything is wiped: the
  // folder-history import rejects an unknown harness, and rejecting after the
  // wipe would leave a clean that destroys data and then throws. Filtering here
  // means the wipe, the import, and the report all see the same resolvable list.
  const { known } = resolveHarnessScope(requested)
  // Why this must throw rather than proceed with nothing: the folder-history
  // import reads an empty harness list as "every harness", so a typo'd
  // `--harness bogus` would wipe nothing, re-import the whole store's history,
  // and report `harnesses: []`. Throwing here lands before the receiver pause
  // and before any wipe, so an unusable scope is a no-op, not a surprise
  // full-history import.
  if (known.length === 0) {
    throw new Error('cleanDatabase: scope selects no known harness')
  }
  return known
}

/** Production ports: the sibling receiver on 4318 and this process's pipelines. */
export function portsForClean(store: CanonStore): CleanPorts {
  return {
    pauseIngestion: () => pauseReceiver(),
    resumeIngestion: () => resumeReceiver(),
    project: (target) => projectCanonicalStore(target),
    // Delegate to the folder-history import rather than driving the refresh
    // pipeline here: that is the one entry point that knows how to turn a window
    // and a harness list into a source scan, and going through it keeps one
    // implementation of "read the last n weeks of logs".
    reingest: ({ harnesses, historyWeeks }) =>
      importFolderHistory(store, { weeks: historyWeeks, harnesses, trigger: 'cli' }),
  }
}
