// The one home for the context-window derivation every consumer shares
// (plan docs/plans/2026-09-23-kyberdash-compaction-hazard-window.md, D2).
//
// The session analysis and the compaction-hazard detector must read the same
// reported window under the same rule, or a finding's percentage quietly
// disagrees with the session headroom shown beside it — the detector measuring
// against a fixed default while the session row uses telemetry is exactly the
// over-report this module exists to prevent.
//
// It lives in `canon/` rather than `analysis/` because the import direction is
// one-way: analysis modules may import from `canon/*`, while
// `canon/sessions.ts` must stay importable-free of `analysis/findings.ts`
// (cycle via `canon/findings.ts`).

import type { CanonicalRecord } from './types.js'

/**
 * Default context window, used when nothing on the record says otherwise.
 * Named rather than inlined so a wrong headroom figure is traceable to one
 * assumption instead of looking like a measurement.
 */
export const DEFAULT_CONTEXT_LIMIT = 200_000

/** Attributes a harness may report its context window under. */
export const CONTEXT_LIMIT_KEYS = [
  'contextWindow',
  'gen_ai.request.max_context_tokens',
  'gen_ai.request.context_window',
  'model_context_window',
] as const

/**
 * Where a context window came from: reported by a record's attributes, or the
 * named default because no source reported one.
 */
export type ContextLimitSource = 'reported' | 'default'

/** A context window together with its provenance. */
export type ContextWindow = {
  contextLimit: number
  contextLimitSource: ContextLimitSource
}

/**
 * Read the window attribute a record reports, mirroring the session
 * analysis's key order: the first key holding a non-empty string or a finite
 * number wins, so `Number.isFinite` filtering happens per key, not after.
 */
function reportedLimitOf(record: CanonicalRecord): string | undefined {
  const raw = record.raw
  if (raw === null || typeof raw !== 'object') return undefined
  const attributes = raw as Record<string, unknown>
  for (const key of CONTEXT_LIMIT_KEYS) {
    const value = attributes[key]
    if (typeof value === 'string' && value !== '') return value
    if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  }
  return undefined
}

/**
 * Derive the context window of a record group: the first `llm.invoke` record
 * that names a window wins, falling back to the named default.
 *
 * First-reported is deliberately not latest-reported: a model switch
 * mid-session changes the window, and re-deriving per turn would let one
 * session claim two windows. The session analysis has always worked this way,
 * and the detector adopting any other rule would make a finding disagree with
 * the session row built from the same records.
 */
export function contextLimitOf(records: readonly CanonicalRecord[]): ContextWindow {
  for (const record of records) {
    if (record.op !== 'llm.invoke') continue
    const reported = reportedLimitOf(record)
    if (reported === undefined) continue
    const limit = Number(reported)
    if (Number.isFinite(limit) && limit > 0) {
      return { contextLimit: limit, contextLimitSource: 'reported' }
    }
    // The first report wins even when it is unusable: matching the session
    // analysis, a non-numeric first report falls to the default rather than
    // scanning on to a later record's value.
    return { contextLimit: DEFAULT_CONTEXT_LIMIT, contextLimitSource: 'default' }
  }
  return { contextLimit: DEFAULT_CONTEXT_LIMIT, contextLimitSource: 'default' }
}
