import type { SessionSource } from '../providers/types.js'

export type { SessionSource }

/** Canonical harness identity persisted on refresh jobs, records, and rollups. */
export type HarnessId = string

/**
 * How a registered upstream `Provider.name` is treated by refresh.
 * Every live `getAllProviders()` entry must have exactly one of these.
 */
export type ProviderDisposition =
  | {
      kind: 'jobs'
      harnessIds: readonly HarnessId[]
    }
  | {
      kind: 'excluded'
      reason: string
    }
  | {
      kind: 'alias-of'
      harnessId: HarnessId
    }

/** Native evidence used to split one provider object into client-surface jobs. */
export type ClassifierKind =
  | 'provider-identity'
  | 'antigravity-root'
  | 'copilot-source-type'
  | 'codex-originator'
  | 'claude-entrypoint'
  | 'kiro-path'
  | 'kilo-root'

export type HarnessSourceDescriptor = {
  harnessId: HarnessId
  label: string
  /** Upstream `Provider.name` that discovers this surface. */
  providerName: string
  classifier: ClassifierKind
  /** Privacy-safe root token for checkpoints and CLI; never an expanded home path. */
  sourceRootLabel: string
  nativeFormat: string
  /**
   * Kyber parser-adapter contract version stored on checkpoints.
   * Changing it invalidates affected checkpoints without deleting canonical rows.
   */
  parserContractVersion: string
}

export type ClassificationInput = {
  source: SessionSource
  /** Codex `session_meta.originator` when already read from the native unit. */
  originator?: string | null
  /** Claude record/session `entrypoint` when already read from the native unit. */
  entrypoint?: string | null
}

export type SessionClassification =
  | { outcome: 'harness'; harnessId: HarnessId }
  | { outcome: 'excluded'; provider: string; reason: string }
  | { outcome: 'indeterminate'; provider: string; reason: string }
