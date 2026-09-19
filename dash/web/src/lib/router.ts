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

export type SpineAction =
  | { type: 'push'; location: ViewLocation }
  | { type: 'pop' }
  | { type: 'replace'; location: ViewLocation }
  | { type: 'goTo'; location: ViewLocation }
  | { type: 'reset'; stack: ViewLocation[] }

function sameFrame(a: ViewLocation, b: ViewLocation): boolean {
  return (
    a.level === b.level &&
    a.harnessId === b.harnessId &&
    a.runId === b.runId &&
    a.executionId === b.executionId &&
    a.turnIndex === b.turnIndex &&
    a.findingId === b.findingId &&
    a.compareA === b.compareA &&
    a.compareB === b.compareB
  )
}

/** The spine reducer App uses; exported so ancestry tests can replay a click path. */
export function applySpineAction(stack: ViewLocation[], action: SpineAction): ViewLocation[] {
  switch (action.type) {
    case 'push':
      return [...stack, action.location]
    case 'pop':
      return stack.length > 1 ? stack.slice(0, -1) : stack
    case 'replace':
      return [...stack.slice(0, -1), action.location]
    case 'reset':
      return action.stack.length > 0 ? action.stack : [{ level: 'context-doctor' }]
    case 'goTo': {
      let matchingIndex = -1
      for (let i = stack.length - 1; i >= 0; i--) {
        if (sameFrame(stack[i]!, action.location)) {
          matchingIndex = i
          break
        }
      }
      const root = stack[0] ?? { level: 'context-doctor' as const }
      return matchingIndex >= 0 ? stack.slice(0, matchingIndex + 1) : [root, action.location]
    }
  }
}

export type HistoryWrite = Pick<History, 'pushState' | 'replaceState'>

function browserHistory(): HistoryWrite | null {
  if (typeof window === 'undefined' || !window.history) return null
  return window.history
}

export function pushView(
  path: string,
  stack: ViewLocation[],
  history: Pick<History, 'pushState'> | null = browserHistory(),
): void {
  history?.pushState({ stack }, '', path)
}

export function replaceView(
  path: string,
  stack: ViewLocation[],
  history: Pick<History, 'replaceState'> | null = browserHistory(),
): void {
  history?.replaceState({ stack }, '', path)
}

function isStoredStack(state: unknown): state is { stack: ViewLocation[] } {
  return (
    typeof state === 'object' &&
    state !== null &&
    'stack' in state &&
    Array.isArray((state as { stack: unknown }).stack)
  )
}

/**
 * popstate carries the stack we pushed. A direct load (no state) only has the
 * URL, so the caller runs resolveAncestry to rebuild the same stack.
 */
export function restoreFromPopState(
  state: unknown,
  href: string,
): ViewLocation[] | { needsAncestry: ViewLocation } {
  if (isStoredStack(state)) return state.stack
  const location = locationFromPath(href)
  return location === null ? [{ level: 'context-doctor' }] : { needsAncestry: location }
}

export type AncestryApi = {
  fetchHarness: (id: string) => Promise<{ harness: string }>
  fetchRun: (id: string) => Promise<{ runId: string; harness: string }>
  fetchSession: (
    id: string,
  ) => Promise<{ id: string; harness: string; runId?: string; executionId?: string }>
  fetchFinding: (
    id: string,
  ) => Promise<{
    id: string
    runId?: string
    sessionId?: string
    harness?: string
    executionId?: string
  }>
}

export type AncestryResult =
  | { status: 'ok'; stack: ViewLocation[] }
  | { status: 'not-found'; id: string }

function isNotFound(error: unknown): boolean {
  if (typeof error === 'object' && error !== null && 'status' in error) {
    return (error as { status: unknown }).status === 404
  }
  return error instanceof Error && /\(404\)/.test(error.message)
}

function notFoundId(location: ViewLocation): string {
  return (
    location.findingId ??
    location.executionId ??
    location.runId ??
    location.harnessId ??
    location.compareA ??
    location.compareB ??
    ''
  )
}

function doctor(): ViewLocation {
  return { level: 'context-doctor' }
}

/**
 * Fetch the leaf entity once and build the stack navigating there would have
 * pushed (R5.2). A 404 becomes `{ status: 'not-found', id }` for the shell.
 */
export async function resolveAncestry(
  location: ViewLocation,
  api: AncestryApi,
): Promise<AncestryResult> {
  try {
    switch (location.level) {
      case 'context-doctor':
      case 'quarantine':
      case 'problems':
        return { status: 'ok', stack: [location] }
      case 'compare': {
        if (location.compareA) await api.fetchRun(location.compareA)
        if (location.compareB) await api.fetchRun(location.compareB)
        return { status: 'ok', stack: [doctor(), location] }
      }
      case 'harness': {
        if (!location.harnessId) return { status: 'not-found', id: '' }
        const harness = await api.fetchHarness(location.harnessId)
        return {
          status: 'ok',
          stack: [doctor(), { level: 'harness', harnessId: harness.harness }],
        }
      }
      case 'run': {
        if (!location.runId) return { status: 'not-found', id: '' }
        const run = await api.fetchRun(location.runId)
        return {
          status: 'ok',
          stack: [
            doctor(),
            { level: 'harness', harnessId: run.harness },
            { level: 'run', runId: run.runId, harnessId: run.harness },
          ],
        }
      }
      case 'execution':
      case 'turn': {
        const sessionId = location.executionId
        if (!sessionId) return { status: 'not-found', id: '' }
        const session = await api.fetchSession(sessionId)
        const executionId = session.executionId ?? session.id
        const runId = session.runId
        const harnessId = session.harness
        const stack: ViewLocation[] = [doctor(), { level: 'harness', harnessId }]
        if (runId) stack.push({ level: 'run', runId, harnessId })
        stack.push({ level: 'execution', executionId, runId, harnessId })
        if (location.level === 'turn') {
          stack.push({
            level: 'turn',
            executionId,
            runId,
            harnessId,
            turnIndex: location.turnIndex,
          })
        }
        return { status: 'ok', stack }
      }
      case 'finding': {
        if (!location.findingId) return { status: 'not-found', id: '' }
        const finding = await api.fetchFinding(location.findingId)
        let harnessId = finding.harness
        let runId = finding.runId
        if (runId && !harnessId) {
          const run = await api.fetchRun(runId)
          harnessId = run.harness
          runId = run.runId
        }
        if (!runId && finding.sessionId) {
          const session = await api.fetchSession(finding.sessionId)
          harnessId = harnessId ?? session.harness
          runId = session.runId
        }
        const stack: ViewLocation[] = [doctor()]
        if (harnessId) stack.push({ level: 'harness', harnessId })
        if (runId) stack.push({ level: 'run', runId, harnessId })
        const findingLoc: ViewLocation = {
          level: 'finding',
          findingId: finding.id,
          runId,
          harnessId,
        }
        if (finding.executionId) findingLoc.executionId = finding.executionId
        stack.push(findingLoc)
        return { status: 'ok', stack }
      }
    }
  } catch (error) {
    if (isNotFound(error)) return { status: 'not-found', id: notFoundId(location) }
    throw error
  }
}

export async function createAncestryApi(): Promise<AncestryApi> {
  const {
    fetchFinding,
    fetchHarness,
    fetchKyberSession,
    fetchRun,
  } = await import('./kyberApi.js')
  return {
    async fetchHarness(id) {
      const row = await fetchHarness(id)
      return { harness: row.harness }
    },
    async fetchRun(id) {
      const run = await fetchRun(id)
      return { runId: run.runId, harness: run.harness }
    },
    async fetchSession(id) {
      const session = await fetchKyberSession(id)
      return {
        id: session.id ?? session.session_id ?? id,
        harness: session.harness,
        runId: session.runId ?? session.run_id,
        executionId: session.executionId ?? session.execution_id ?? id,
      }
    },
    async fetchFinding(id) {
      const finding = await fetchFinding(id)
      return {
        id: finding.id,
        runId: finding.runId,
        sessionId: finding.sessionId,
        harness: finding.harness,
        executionId: finding.executionId,
      }
    },
  }
}
