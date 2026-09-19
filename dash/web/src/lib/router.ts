// One router for every dashboard view the CLI and tray can open.
//
// `view-paths.json` is the list. The CLI `--view` flag and the tray's
// `open_view` already match against it. A second copy here would drift, and a
// tray click that no longer matches this router is a dead link with nothing
// to catch it (R5.1). This module reads that file and substitutes params;
// it does not restate the patterns.

import patterns from '../../../src/server/view-paths.json' with { type: 'json' }

export const VIEW_PATH_PATTERNS: readonly string[] = patterns

/**
 * The view the URL identifies. Ancestry that is not in the path (a run's
 * harness, a session's run) is filled in by `resolveAncestry`, not by parsing.
 */
export type ViewLocation = {
  level:
    | 'context-doctor'
    | 'harness'
    | 'run'
    | 'execution'
    | 'turn'
    | 'finding'
    | 'compare'
    | 'quarantine'
    | 'problems'
  harnessId?: string
  runId?: string
  executionId?: string
  turnIndex?: number
  findingId?: string
  compareA?: string
  compareB?: string
}

/** The diagnostic-spine subset App already pushed; alias kept so call sites stay typed. */
export type SpineLocation = ViewLocation

type Split = { path: string; query: string }

function splitQuery(value: string): Split {
  const trimmed = value.trim()
  const q = trimmed.indexOf('?')
  if (q < 0) return { path: trimmed, query: '' }
  return { path: trimmed.slice(0, q), query: trimmed.slice(q + 1) }
}

function normalizePath(path: string): string {
  if (path === '') return ''
  return path.replace(/^\/+/, '').replace(/\/+$/, '')
}

function pathSegments(path: string): string[] {
  const normalized = normalizePath(path)
  return normalized === '' ? [] : normalized.split('/')
}

/**
 * Match one view-paths pattern, capturing `:param` segments and query values.
 * Query placeholders like `a=:runId` bind under the query key (`a`), because
 * both compare sides are `:runId` and would otherwise collide.
 */
function matchPattern(view: string, pattern: string): Record<string, string> | null {
  const asked = splitQuery(view)
  const expected = splitQuery(pattern)
  const viewSegs = pathSegments(asked.path)
  const patSegs = pathSegments(expected.path)
  if (viewSegs.length !== patSegs.length) return null

  const params: Record<string, string> = {}
  for (let i = 0; i < patSegs.length; i++) {
    const slot = patSegs[i]!
    const value = viewSegs[i]!
    if (slot.startsWith(':')) {
      if (value === '') return null
      params[slot.slice(1)] = decodeURIComponent(value)
    } else if (slot !== value) {
      return null
    }
  }

  if (expected.query !== '') {
    const actual = new URLSearchParams(asked.query)
    for (const part of expected.query.split('&')) {
      if (part === '') continue
      const eq = part.indexOf('=')
      const key = eq < 0 ? part : part.slice(0, eq)
      const value = actual.get(key)
      if (value === null || value === '') return null
      params[key] = value
    }
  } else if (asked.query !== '') {
    // A pattern without a query must not silently absorb leftover params;
    // `/` with `?a=1` is not Context Doctor identified by that query.
    return null
  }

  return params
}

function locationFromParams(pattern: string, params: Record<string, string>): ViewLocation | null {
  switch (pattern) {
    case '/':
      return { level: 'context-doctor' }
    case '/harness/:harnessId':
      return { level: 'harness', harnessId: params.harnessId }
    case '/run/:runId':
      return { level: 'run', runId: params.runId }
    case '/session/:sessionId':
      return { level: 'execution', executionId: params.sessionId }
    case '/session/:sessionId/turn/:turnIndex': {
      const turnIndex = Number(params.turnIndex)
      if (!Number.isInteger(turnIndex) || turnIndex < 0) return null
      return { level: 'turn', executionId: params.sessionId, turnIndex }
    }
    case '/finding/:findingId':
      return { level: 'finding', findingId: params.findingId }
    case '/compare?a=:runId&b=:runId':
      return { level: 'compare', compareA: params.a, compareB: params.b }
    case '/quarantine':
      return { level: 'quarantine' }
    case '/problems':
      return { level: 'problems' }
    default:
      return null
  }
}

/** Parse a path+query into the view it names, or null when it is not a view. */
export function locationFromPath(href: string): ViewLocation | null {
  const asked = href.startsWith('/') || href.startsWith('?') ? href : `/${href}`
  for (const pattern of VIEW_PATH_PATTERNS) {
    const params = matchPattern(asked, pattern)
    if (params === null) continue
    const location = locationFromParams(pattern, params)
    if (location !== null) return location
  }
  return null
}

function substitute(pattern: string, params: Record<string, string>): string {
  const split = splitQuery(pattern)
  const path = split.path.replace(/:([A-Za-z][A-Za-z0-9]*)/g, (_all, name: string) => {
    const value = params[name]
    return value === undefined ? `:${name}` : encodeURIComponent(value)
  })
  if (split.query === '') return path === '' ? '/' : path
  const search = new URLSearchParams()
  for (const part of split.query.split('&')) {
    const eq = part.indexOf('=')
    const key = eq < 0 ? part : part.slice(0, eq)
    const raw = eq < 0 ? '' : part.slice(eq + 1)
    const bound = raw.startsWith(':') ? params[key] : raw
    if (bound === undefined || bound === '') return path
    search.set(key, bound)
  }
  const qs = search.toString()
  return qs === '' ? path : `${path}?${qs}`
}

function paramsFromLocation(location: ViewLocation): { pattern: string; params: Record<string, string> } | null {
  switch (location.level) {
    case 'context-doctor':
      return { pattern: '/', params: {} }
    case 'harness':
      if (!location.harnessId) return null
      return { pattern: '/harness/:harnessId', params: { harnessId: location.harnessId } }
    case 'run':
      if (!location.runId) return null
      return { pattern: '/run/:runId', params: { runId: location.runId } }
    case 'execution':
      if (!location.executionId) return null
      return { pattern: '/session/:sessionId', params: { sessionId: location.executionId } }
    case 'turn':
      if (!location.executionId || location.turnIndex === undefined) return null
      return {
        pattern: '/session/:sessionId/turn/:turnIndex',
        params: { sessionId: location.executionId, turnIndex: String(location.turnIndex) },
      }
    case 'finding':
      if (!location.findingId) return null
      return { pattern: '/finding/:findingId', params: { findingId: location.findingId } }
    case 'compare':
      if (!location.compareA || !location.compareB) return null
      return {
        pattern: '/compare?a=:runId&b=:runId',
        params: { a: location.compareA, b: location.compareB },
      }
    case 'quarantine':
      return { pattern: '/quarantine', params: {} }
    case 'problems':
      return { pattern: '/problems', params: {} }
  }
}

/**
 * Encode a view as the path the CLI and tray already open.
 * Null when the location cannot form a pattern in view-paths.json (a compare
 * with only one side, a spine node that has not been identified yet).
 */
export function pathFromLocation(location: ViewLocation): string | null {
  const encoded = paramsFromLocation(location)
  if (encoded === null) return null
  // Refuse to emit a path whose pattern is not in the shared list — that is
  // how a hand-rolled route would sneak in beside the JSON.
  if (!VIEW_PATH_PATTERNS.includes(encoded.pattern)) return null
  return substitute(encoded.pattern, encoded.params)
}
