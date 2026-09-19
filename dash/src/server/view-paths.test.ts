// The CLI, the web router and the tray all match against this one list
// (R5.6). Three copies would drift, and a report's `kyberdash web --view`
// command would then open a URL the dashboard did not recognise.

import { describe, expect, it } from 'vitest'

import {
  VIEW_PATH_PATTERNS,
  formatValidViewForms,
  matchesViewPath,
} from './view-paths.js'

describe('view-paths.json (R5.6)', () => {
  it('is the spine route table, so a later surface cannot invent a third copy', () => {
    expect(VIEW_PATH_PATTERNS).toEqual([
      '/',
      '/harness/:harnessId',
      '/run/:runId',
      '/session/:sessionId',
      '/session/:sessionId/turn/:turnIndex',
      '/finding/:findingId',
      '/compare?a=:runId&b=:runId',
      '/quarantine',
      '/problems',
    ])
  })

  it('accepts --view finding/<id> and the other documented forms', () => {
    expect(matchesViewPath('finding/fid-9')).toBe(true)
    expect(matchesViewPath('/finding/fid-9')).toBe(true)
    expect(matchesViewPath('/')).toBe(true)
    expect(matchesViewPath('harness/codex')).toBe(true)
    expect(matchesViewPath('run/r1')).toBe(true)
    expect(matchesViewPath('session/s1')).toBe(true)
    expect(matchesViewPath('session/s1/turn/0')).toBe(true)
    expect(matchesViewPath('compare?a=r1&b=r2')).toBe(true)
    expect(matchesViewPath('quarantine')).toBe(true)
    expect(matchesViewPath('problems')).toBe(true)
  })

  it('rejects a path that is not a view, including a missing compare pair', () => {
    expect(matchesViewPath('not-a-view')).toBe(false)
    expect(matchesViewPath('finding/')).toBe(false)
    expect(matchesViewPath('finding/a/b')).toBe(false)
    expect(matchesViewPath('compare')).toBe(false)
    expect(matchesViewPath('compare?a=r1')).toBe(false)
    expect(matchesViewPath('usage')).toBe(false)
  })

  it('lists the valid --view forms so an unknown view can name them', () => {
    const listed = formatValidViewForms()
    expect(listed).toContain('finding/<findingId>')
    expect(listed).toContain('compare?a=<runId>&b=<runId>')
    expect(listed).toContain('session/<sessionId>/turn/<turnIndex>')
  })
})
