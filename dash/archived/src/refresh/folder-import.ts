// Folder import: the one-off, operator-driven history backfill from local harness folders.
//
// It is the same ingest a refresh runs, narrowed by harness and widened by history window,
// so it stays a separate entry point rather than a flag on refresh. The gate on reading
// folders belongs to scheduled and automatic refresh (folderSourcesAllowed), because those
// runs read folders no one asked for at that moment. The explicit import is a user opt-in
// and is always allowed, whatever the trigger and whatever settings.folder_import.scheduled
// says.
//
// The maintenance pass exists so retention and projection can run on a timer without
// writing a refresh_run row. A refresh row says "sources were read"; a maintenance pass
// reads none, and recording it as a refresh would make the audit trail claim work that
// never happened.

import { projectCanonicalStore, projectionIsCurrent } from '../canon/projection.js'
import {
  SHARED_PROJECTION_MIN_INTERVAL_MS,
  sharedProjectionDue,
  startLeaseHeartbeat,
  releaseProjectionLease,
  tryClaimProjectionLease,
} from '../canon/projection-lease.js'
import type { RefreshTrigger } from '../canon/refresh-run.js'
import { purgeExpiredContent } from '../canon/retention.js'
import type { CanonStore } from '../canon/store.js'

import { SETTING_KEYS, readSetting } from '../settings/shared-settings.js'

import { MAX_CLEAN_REINGEST_WEEKS } from './limits.js'
import { HARNESS_DESCRIPTORS, resolveHarnessScope } from './registry.js'
import { refreshHarnessSources, type RefreshDependencies } from './orchestrator.js'
import type { HarnessSourceDescriptor } from './types.js'

export const DEFAULT_FOLDER_IMPORT_WEEKS = 1

export type FolderImportOptions = {
  weeks?: number
  harnesses?: readonly string[]
  trigger: RefreshTrigger
}

export type FolderImportDependencies = {
  refreshHarnessSources: (
    store: CanonStore,
    dependencies: RefreshDependencies,
    options: { historyWeeks: number; trigger: RefreshTrigger },
  ) => Promise<unknown>
}

const productionFolderImportDependencies: FolderImportDependencies = {
  refreshHarnessSources,
}

export function folderSourcesAllowed(trigger: RefreshTrigger, store: CanonStore): boolean {
  if (trigger === 'cli') return true
  return readSetting(store, SETTING_KEYS.folderImportScheduled) === 'on'
}

// The explicit, user-initiated history import. It is always allowed for any trigger and
// never reads or changes settings.folder_import.scheduled; that setting gates only the
// scheduled and automatic refresh runs (see folderSourcesAllowed). Argument checks run
// before the first store write, so a rejected request leaves no refresh row behind.
export async function importFolderHistory(
  store: CanonStore,
  options: FolderImportOptions,
  dependencies: FolderImportDependencies = productionFolderImportDependencies,
): Promise<void> {
  const weeks = options.weeks ?? DEFAULT_FOLDER_IMPORT_WEEKS
  if (!Number.isSafeInteger(weeks) || weeks < 1 || weeks > MAX_CLEAN_REINGEST_WEEKS) {
    throw new Error(
      `importFolderHistory: weeks must be a whole number between 1 and ${MAX_CLEAN_REINGEST_WEEKS}`,
    )
  }
  const descriptors = resolveDescriptors(options.harnesses)

  await dependencies.refreshHarnessSources(
    store,
    {
      getAllProviders: async () => (await import('../synth/provider.js')).getAllProviders(),
      descriptors,
    },
    { historyWeeks: weeks, trigger: options.trigger },
  )
}

/**
 * The periodic pass: retention, then the projection.
 *
 * The purge keeps its own time-based cadence — its trigger is a retention
 * deadline, not a change in the corpus — so it runs on every pass and only
 * the projection is gated. The purge runs FIRST so a projection that does
 * happen never caches content that is about to be removed.
 *
 * The gate is the projection's own: it compares the corpus against what the
 * last projection saw (the records-write marker, the detector version and the
 * parser contracts) and skips the rebuild when nothing moved. A rebuild over a
 * real corpus is ~26 s of CPU, and running it on a timer whether or not
 * anything changed is what starved the dashboard's HTTP handlers.
 */
export async function runMaintenancePass(store: CanonStore, now: Date): Promise<void> {
  // Purge before projecting: the projection must not cache content that is about to be
  // removed, or the derived views would keep showing it until the next refresh.
  purgeExpiredContent(store, now)

  if (projectionIsCurrent(store)) return
  const nowMs = now.getTime()
  // The shared floor (F1): automatic projections are spaced at least
  // SHARED_PROJECTION_MIN_INTERVAL_MS apart no matter which process ran the
  // last one, so a 30 s maintenance tick cannot bypass the receiver's
  // 10-minute cadence by projecting whenever the corpus merely looks dirty.
  if (!sharedProjectionDue(store, nowMs, SHARED_PROJECTION_MIN_INTERVAL_MS)) return
  // The shared lease (F1): at most one automatic projection runs at a time
  // across processes. Losing is routine contention — the next tick retries —
  // so a held lease skips quietly rather than projecting over a live pass.
  const claim = tryClaimProjectionLease(store, { owner: 'maintenance', nowMs })
  if (claim === null) return
  const heartbeat = startLeaseHeartbeat(store, claim)
  try {
    await projectCanonicalStore(store)
  } finally {
    heartbeat.stop()
    releaseProjectionLease(store, heartbeat.current())
  }
}

function resolveDescriptors(harnesses: readonly string[] | undefined): readonly HarnessSourceDescriptor[] {
  if (harnesses === undefined || harnesses.length === 0) return HARNESS_DESCRIPTORS
  // One resolution for every caller (registry.resolveHarnessScope), so a family label the
  // tray sends (`codex`) imports the members the registry actually stores rather than
  // throwing inside the child after the route already answered 202.
  const scope = resolveHarnessScope(harnesses)
  if (scope.unknown.length > 0) {
    throw new Error(`importFolderHistory: unknown harness '${scope.unknown[0]}'`)
  }
  if (scope.descriptors.length === 0) {
    throw new Error(`importFolderHistory: no folder source for harness '${harnesses[0]}'`)
  }
  return scope.descriptors
}
