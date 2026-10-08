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

/** Attributes a record may name its model under. Same order as the session header. */
export const MODEL_IDENTITY_KEYS = ['gen_ai.response.model', 'gen_ai.request.model', 'model'] as const

/**
 * The first model id an `llm.invoke` record names, or undefined when none do.
 * Exact string, no trimming and no fuzzy match: a catalog lookup that
 * normalized the id would resolve a different model than the one recorded.
 */
export function modelIdentityOf(records: readonly CanonicalRecord[]): string | undefined {
  for (const record of records) {
    if (record.op !== 'llm.invoke') continue
    const raw = record.raw
    if (raw === null || typeof raw !== 'object') continue
    const attributes = raw as Record<string, unknown>
    for (const key of MODEL_IDENTITY_KEYS) {
      const value = attributes[key]
      if (typeof value === 'string' && value !== '') return value
      if (typeof value === 'number' && Number.isFinite(value)) return String(value)
    }
  }
  return undefined
}

/** Attributes a harness may report its context window under. */
export const CONTEXT_LIMIT_KEYS = [
  'contextWindow',
  'gen_ai.request.max_context_tokens',
  'gen_ai.request.context_window',
  'model_context_window',
] as const

/**
 * The raw key a harness-declared (not harness-reported) window rides under.
 * Deliberately distinct from every `CONTEXT_LIMIT_KEYS` entry: a declared
 * window is provenance of its own, and synthesis must never file it under a
 * reported key. The synthesizer carries `ReaderTurn.declaredContextWindow`
 * here, and nothing else writes it.
 */
export const DECLARED_CONTEXT_LIMIT_KEY = 'declaredContextWindow' as const

/**
 * Where a context window came from: reported by a record's attributes,
 * declared for the session by harness configuration rather than measured
 * telemetry, resolved from the vendor-documented catalog, or the named
 * default because no source named one.
 *
 * `'catalog'` is part of the union so a session row and a compaction finding
 * can name it. `contextLimitOf` never returns it: the catalog lives in the
 * store, and folding that lookup into this function would make the 200K
 * default and a documentation row compete inside every caller that only
 * asked what the records themselves said.
 */
export type ContextLimitSource = 'reported' | 'declared' | 'catalog' | 'default'

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
 * Read the window a record declares for its session: harness configuration
 * rather than measured telemetry, carried under the dedicated declared key
 * alone. The per-key read mirrors the reported path: a non-empty string or
 * a finite number wins, anything else is absence.
 */
function declaredLimitOf(record: CanonicalRecord): string | undefined {
  const raw = record.raw
  if (raw === null || typeof raw !== 'object') return undefined
  const value = (raw as Record<string, unknown>)[DECLARED_CONTEXT_LIMIT_KEY]
  if (typeof value === 'string' && value !== '') return value
  if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  return undefined
}

/** A named window usable as a denominator: finite and positive. */
function usableLimit(named: string): number | undefined {
  const limit = Number(named)
  return Number.isFinite(limit) && limit > 0 ? limit : undefined
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
 *
 * Precedence across classes is reported, then declared, then default: any
 * reported record decides the group even when a declared record comes first,
 * because a measured window beats a configured one. Within each class the
 * first-record rule above is unchanged — the first declared record wins even
 * when it is unusable, and an unusable first report falls to the default
 * rather than scanning on to a later record or dropping to declared.
 */
export function contextLimitOf(records: readonly CanonicalRecord[]): ContextWindow {
  let firstDeclared: string | undefined
  for (const record of records) {
    if (record.op !== 'llm.invoke') continue
    const reported = reportedLimitOf(record)
    if (reported !== undefined) {
      const limit = usableLimit(reported)
      if (limit !== undefined) {
        return { contextLimit: limit, contextLimitSource: 'reported' }
      }
      // The first report wins even when it is unusable: matching the session
      // analysis, a non-numeric first report falls to the default rather than
      // scanning on to a later record's value — or to a declared window.
      return { contextLimit: DEFAULT_CONTEXT_LIMIT, contextLimitSource: 'default' }
    }
    if (firstDeclared === undefined) {
      const declared = declaredLimitOf(record)
      if (declared !== undefined) firstDeclared = declared
    }
  }
  if (firstDeclared !== undefined) {
    const limit = usableLimit(firstDeclared)
    if (limit !== undefined) {
      return { contextLimit: limit, contextLimitSource: 'declared' }
    }
    // Same first-record rule as reported: an unusable first declaration
    // falls to the default rather than scanning on to a later record.
    return { contextLimit: DEFAULT_CONTEXT_LIMIT, contextLimitSource: 'default' }
  }
  return { contextLimit: DEFAULT_CONTEXT_LIMIT, contextLimitSource: 'default' }
}
