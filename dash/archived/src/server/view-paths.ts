// One route table for every surface that opens a dashboard view.
//
// The CLI (`--view`), the web router and the tray's `open_view` all have to
// accept the same paths. Three copies would drift, and a report's
// `kyberdash web --view finding/<id>` command would then open a URL the
// dashboard (or the tray) refused. This file is the list; each surface
// matches against it rather than restating it.

import patterns from './view-paths.json' with { type: 'json' }

export const VIEW_PATH_PATTERNS: readonly string[] = patterns

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function splitQuery(value: string): { path: string; query: string } {
  const trimmed = value.trim()
  const q = trimmed.indexOf('?')
  if (q < 0) return { path: trimmed, query: '' }
  return { path: trimmed.slice(0, q), query: trimmed.slice(q + 1) }
}

function normalizePath(path: string): string {
  return path.replace(/^\/+/, '').replace(/\/+$/, '')
}

function pathMatches(viewPath: string, patternPath: string): boolean {
  const view = normalizePath(viewPath)
  const pattern = normalizePath(patternPath)
  if (pattern === '') return view === ''
  const source = pattern
    .split('/')
    .map((segment) => (segment.startsWith(':') ? '[^/]+' : escapeRegex(segment)))
    .join('/')
  return new RegExp(`^${source}$`).test(view)
}

function queryMatches(viewQuery: string, patternQuery: string): boolean {
  if (patternQuery === '') return true
  const actual = new URLSearchParams(viewQuery)
  for (const part of patternQuery.split('&')) {
    if (part === '') continue
    const eq = part.indexOf('=')
    const key = eq < 0 ? part : part.slice(0, eq)
    const value = actual.get(key)
    if (value === null || value === '') return false
  }
  return true
}

export function matchesViewPath(view: string): boolean {
  const asked = splitQuery(view)
  return VIEW_PATH_PATTERNS.some((pattern) => {
    const expected = splitQuery(pattern)
    return pathMatches(asked.path, expected.path) && queryMatches(asked.query, expected.query)
  })
}

/** The `--view` forms named when an unknown view exits 2. */
export function formatValidViewForms(): string {
  return VIEW_PATH_PATTERNS.map((pattern) => {
    const form = pattern.replace(/^\//, '').replace(/:([A-Za-z][A-Za-z0-9]*)/g, '<$1>')
    return form === '' ? '/' : form
  }).join(', ')
}
