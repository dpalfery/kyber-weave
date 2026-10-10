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

import { projectCanonicalStore } from '../canon/projection.js'
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

export async function runMaintenancePass(store: CanonStore, now: Date): Promise<void> {
  // Purge before projecting: the projection must not cache content that is about to be
  // removed, or the derived views would keep showing it until the next refresh.
  purgeExpiredContent(store, now)
  await projectCanonicalStore(store)
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
