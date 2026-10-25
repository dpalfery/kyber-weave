/**
 * Scope admission: defaults an absent scope to the previous seven days from
 * the engine clock, and rejects a malformed explicit scope as
 * `invalid_request` — never a silent default.
 *
 * <remarks>
 * Falling back to a default when the caller sent `from >= to` would answer
 * a different question than the one asked and count it as measured. An
 * absent scope is the only case that defaults; anything the caller wrote
 * down must parse.
 * </remarks>
 */

import { DEFAULT_DAYS } from './catalog.js'
import { isRecord, type AnalyticsScope, type OperationError } from './types.js'

const ALLOWED_KEYS = new Set(['from', 'to', 'timezone', 'grain', 'harness', 'repository', 'branch', 'runId', 'sessionId'])

export function isValidDate(value: unknown): value is string {
  return typeof value === 'string' && Number.isFinite(Date.parse(value))
}

/** The seven-day default window the engine clock anchors. */
export function defaultScope(clock: () => Date): AnalyticsScope {
  const now = clock()
  return {
    from: new Date(now.getTime() - DEFAULT_DAYS * 86_400_000).toISOString(),
    to: now.toISOString(),
    timezone: 'UTC',
  }
}

export type ScopeResult =
  | { ok: true; scope: AnalyticsScope }
  | { ok: false; error: OperationError }

/**
 * Admit one request scope. `undefined` (no `scope` key) defaults; any value
 * the caller supplied must be a strict object with known keys, a parseable
 * `from < to` range, and a string timezone.
 */
export function parseScope(rawScope: unknown, clock: () => Date): ScopeResult {
  if (rawScope === undefined) return { ok: true, scope: defaultScope(clock) }
  const obj = isRecord(rawScope)
  if (obj === null) {
    return { ok: false, error: { code: 'invalid_request', message: 'The scope must be an object.', hint: 'Send a scope with from, to and identity filters.' } }
  }
  if (Object.keys(obj).some((key) => !ALLOWED_KEYS.has(key))) {
    return { ok: false, error: { code: 'invalid_request', message: 'The scope has unsupported keys.', hint: 'Use only from, to, timezone, grain and identity filters.' } }
  }
  const fallback = defaultScope(clock)
  const from = obj.from === undefined ? fallback.from : obj.from
  const to = obj.to === undefined ? fallback.to : obj.to
  if (!isValidDate(from) || !isValidDate(to)) {
    return { ok: false, error: { code: 'invalid_request', message: 'The scope time range is malformed.', hint: 'Send parseable from and to timestamps with from earlier than to.' } }
  }
  if (Date.parse(from) >= Date.parse(to)) {
    return { ok: false, error: { code: 'invalid_request', message: 'The scope time range is malformed.', hint: 'Send a from timestamp earlier than to.' } }
  }
  if (obj.timezone !== undefined && typeof obj.timezone !== 'string') {
    return { ok: false, error: { code: 'invalid_request', message: 'The scope timezone is malformed.', hint: 'Send the timezone as a string.' } }
  }
  const scope = { ...obj, from, to, timezone: typeof obj.timezone === 'string' ? obj.timezone : 'UTC' } as AnalyticsScope
  return { ok: true, scope }
}
