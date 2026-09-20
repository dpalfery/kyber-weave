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

export type ReceiverStatus =
  | 'reachable'
  | 'not-reachable'
  | 'hosted'
  | 'port-held-by-other'
  | 'unknown'

export type TraySettings = {
  harness: string
  windowDays: number
  refreshMinutes: number
  attentionThreshold: number
  criticalThreshold: number
  launchAtLogin: boolean
  hostReceiver: boolean
}

export type ViewState = {
  phase: Phase
  setup?: SetupInfo
  report: ContextReport | null
  reportFetchedAt: string | null
  error: string | null
  refresh: RefreshInfo
  receiver: ReceiverStatus
  settings: TraySettings
}

/** The commands the popover can invoke, as the design's IPC surface names them. */
export type TrayCommands = {
  refreshNow: () => void
  openView: (view: string) => void
  setSettings: (patch: Partial<TraySettings>) => void
  quit: () => void
}
