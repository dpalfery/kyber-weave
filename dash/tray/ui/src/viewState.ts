/**
 * What the Rust side sends the popover.
 *
 * The report's own types come from the engine, type-only, so the UI and the
 * engine cannot drift (design C10). The wrapper around it is the tray's, and
 * mirrors `ViewState` in the design's Data Models — field for field, because
 * the Rust structs serialize into exactly this.
 */

import type { ContextReport } from '../../../src/analysis/report/types.ts'

export type Phase = 'setup' | 'starting' | 'ready' | 'stale'

export type SetupReason = 'not-found' | 'too-old'

export type SetupInfo = {
  probed: string[]
  remedy: string
  reason: SetupReason
}

export type RefreshState = 'idle' | 'running' | 'running-elsewhere' | 'failed'

export type RefreshInfo = {
  state: RefreshState
  lastSuccessAt: string | null
  lastFailure: string | null
}

/**
 * The server's job state (`GET /api/kyber/jobs`), forwarded verbatim by the
 * Rust core. The tray renders it and never derives a schedule from it (R1).
 */
export type JobsInfo = {
  refresh: RefreshInfo & { nextDueAt: string | null }
  /** True only for scheduled jobs; manual actions, OTLP ingest and retention continue. */
  paused: boolean
  storeGeneration: number
  hostedElsewhere?: boolean
}

/** The server-owned settings document (`GET /api/kyber/settings`). */
export type SharedSettings = {
  folderImportScheduled: boolean
  jobsPaused: boolean
  refreshCadenceMinutes: number
  receiverHosted: boolean
}

export type ReceiverStatus =
  | 'reachable'
  | 'not-reachable'
  | 'hosted'
  | 'off'
  | 'port-held-by-other'
  | 'unknown'

/**
 * Tray-local display preferences. The refresh cadence and the receiver live on
 * the server now (`SharedSettings`), so this document carries neither: a
 * required field here would be one the Rust side no longer sends.
 */
export type TraySettings = {
  harness: string
  windowDays: number
  attentionThreshold: number
  criticalThreshold: number
  launchAtLogin: boolean
}

export type ViewState = {
  phase: Phase
  setup?: SetupInfo
  report: ContextReport | null
  reportFetchedAt: string | null
  error: string | null
  /**
   * `null` or absent means the core could not read it: unknown, never a
   * default. Optional so a payload that omits the key is also "unknown".
   */
  jobs?: JobsInfo | null
  sharedSettings?: SharedSettings | null
  /**
   * Legacy: the Rust core no longer serializes these two (the server owns the
   * refresh and receiver state now, see `jobs` / `sharedSettings`). They stay
   * optional only so older fixtures type-check; the UI treats absence as unknown.
   */
  refresh?: RefreshInfo
  receiver?: ReceiverStatus
  /** Tray-local display preferences. */
  settings: TraySettings
}

/** The scope a `clean_database` popover command may wipe (issue #312). */
export type CleanDatabaseScope = 'all' | { harness: string }

/** The commands the popover can invoke, as the design's IPC surface names them. */
export type TrayCommands = {
  refreshNow: () => void
  openView: (view: string) => void
  setSettings: (patch: Partial<TraySettings>) => void
  quit: () => void
  /** `importWeeks` is present only when the user opted into a folder import after the wipe. */
  cleanDatabase: (scope: CleanDatabaseScope, importWeeks?: number) => void
  importFolderHistory: (request: ImportFolderHistoryRequest) => void
  setSharedSettings: (patch: Partial<SharedSettings>) => void
}

export type ImportFolderHistoryRequest = { weeks: number; harness?: string }
