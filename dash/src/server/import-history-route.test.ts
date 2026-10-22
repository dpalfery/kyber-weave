// `POST /api/kyber/import-history` (issue #319 T11 RED, rule R1): an explicit, one-off
// folder-history import requested by the tray or the web UI. The server hands it to the
// JobHost as an `import` job. It must NEVER change the scheduled folder-import setting:
// "import this once" and "keep importing" are different decisions. Assumed host call:
// `jobHost.runJob('import', args)` with `--weeks <n>` and one `--harness <id>` per harness.
import { describe, expect, it } from 'vitest'

import { SETTING_KEYS, readSetting, writeSetting } from '../settings/shared-settings.js'
import { callRoute, makeDeps } from './testing.js'

const PATH = '/api/kyber/import-history'

describe('POST /api/kyber/import-history', () => {
  it('accepts weeks and harnesses with 202 and runs an import job', async () => {
    const deps = makeDeps()
    const { status, handled } = await callRoute('POST', PATH, { deps, payload: { weeks: 4, harnesses: ['pi'] } })
    expect(handled).toBe(true)
    expect(status).toBe(202)
    expect(deps.jobHost.runJobCalls).toHaveLength(1)
    const call = deps.jobHost.runJobCalls[0]!
    expect(call.kind).toBe('import')
    // Index-based, not a substring: `--weeks 4` also appears in `--weeks 40` or
    // `--weeks 4x`, so the substring assertion would pass on a wrong argv.
    expect(call.args[call.args.indexOf('--weeks') + 1]).toBe('4')
    expect(call.args.join(' ')).toContain('--harness pi')
  })

  it('accepts an empty body (all harnesses, default window) with 202', async () => {
    const deps = makeDeps()
    const { status } = await callRoute('POST', PATH, { deps, payload: {} })
    expect(status).toBe(202)
    expect(deps.jobHost.runJobCalls.map((c) => c.kind)).toEqual(['import'])
  })

  it.each([1, 52])('accepts the boundary weeks=%i', async (weeks) => {
    const { status } = await callRoute('POST', PATH, { deps: makeDeps(), payload: { weeks } })
    expect(status).toBe(202)
  })

  it.each([
    ['weeks of zero', { weeks: 0 }],
    ['weeks above a year', { weeks: 53 }],
    ['fractional weeks', { weeks: 1.5 }],
    ['string weeks', { weeks: '4' }],
    ['negative weeks', { weeks: -1 }],
    ['an unknown harness', { harnesses: ['no-such-harness'] }],
    ['a non-array harnesses', { harnesses: 'pi' }],
    ['a non-string harness', { harnesses: [7] }],
    ['an empty harness name', { harnesses: [''] }],
    ['an array body', [1]],
    ['malformed JSON', '{not json'],
  ])('rejects %s with 400 and starts nothing', async (_label, payload) => {
    const deps = makeDeps()
    const { status, body } = await callRoute('POST', PATH, { deps, payload })
    expect(status).toBe(400)
    expect(body).toHaveProperty('error')
    expect(deps.jobHost.runJobCalls).toEqual([])
  })

  // The native tray sends a SINGULAR string `harness` (runtime.rs); the route accepts both
  // `harness` (string) and `harnesses` (string array). Ignoring the singular key would make
  // "import codex only" silently import every harness.
  describe('singular `harness` (native tray payload)', () => {
    it('accepts {weeks, harness} with 202 and passes --harness <id> and --weeks <n> to the import job', async () => {
      const deps = makeDeps()
      const { status } = await callRoute('POST', PATH, { deps, payload: { weeks: 12, harness: 'codex' } })
      expect(status).toBe(202)
      expect(deps.jobHost.runJobCalls).toHaveLength(1)
      const call = deps.jobHost.runJobCalls[0]!
      expect(call.kind).toBe('import')
      expect(call.args[call.args.indexOf('--harness') + 1]).toBe('codex')
      expect(call.args).toContain('--harness')
      expect(call.args[call.args.indexOf('--weeks') + 1]).toBe('12')
      expect(call.args).toContain('--weeks')
      expect(call.args.filter((a) => a === '--harness')).toHaveLength(1)
    })

    it.each([
      ['an unknown harness', { weeks: 2, harness: 'no-such-harness' }],
      ['a non-string harness', { weeks: 2, harness: 123 }],
      ['both harness and harnesses (ambiguous)', { weeks: 2, harness: 'codex', harnesses: ['codex'] }],
    ])('rejects %s with 400 and starts nothing', async (_label, payload) => {
      const deps = makeDeps()
      const { status, body } = await callRoute('POST', PATH, { deps, payload })
      expect(status).toBe(400)
      expect(body).toHaveProperty('error')
      expect(deps.jobHost.runJobCalls).toEqual([])
    })

    it.each([false, true])('never changes the scheduled folder-import setting (was %s)', async (scheduled) => {
      const deps = makeDeps()
      writeSetting(deps.store, SETTING_KEYS.folderImportScheduled, scheduled ? 'on' : 'off')
      const accepted = await callRoute('POST', PATH, { deps, payload: { weeks: 3, harness: 'codex' } })
      expect(accepted.status).toBe(202)
      expect(readSetting(deps.store, SETTING_KEYS.folderImportScheduled)).toBe(scheduled ? 'on' : 'off')
    })
  })

  it('answers 409 when the host already runs a job', async () => {
    const deps = makeDeps()
    deps.jobHost.status = { ...deps.jobHost.status, state: 'running' }
    const { status } = await callRoute('POST', PATH, { deps, payload: { weeks: 2 } })
    expect(status).toBe(409)
    expect(deps.jobHost.runJobCalls).toEqual([])
  })

  it.each(['declined', 'busy', 'hosted-elsewhere'] as const)('answers 409 when the host answers %s', async (outcome) => {
    const deps = makeDeps()
    deps.jobHost.nextOutcome = Promise.resolve({ outcome })
    const { status } = await callRoute('POST', PATH, { deps, payload: { weeks: 2 } })
    expect(status).toBe(409)
  })

  it.each([false, true])('never changes the scheduled folder-import setting (was %s)', async (scheduled) => {
    const deps = makeDeps()
    writeSetting(deps.store, SETTING_KEYS.folderImportScheduled, scheduled ? 'on' : 'off')
    const accepted = await callRoute('POST', PATH, { deps, payload: { weeks: 3, harnesses: ['pi'] } })
    const rejected = await callRoute('POST', PATH, { deps, payload: { weeks: 99 } }) // a rejected request must not either
    expect([accepted.status, rejected.status]).toEqual([202, 400])
    expect(readSetting(deps.store, SETTING_KEYS.folderImportScheduled)).toBe(scheduled ? 'on' : 'off')
  })

  it.each(['GET', 'PUT', 'DELETE'])('rejects %s with 405', async (method) => {
    const { status } = await callRoute(method, PATH, { deps: makeDeps() })
    expect(status).toBe(405)
  })

  it('rejects a non-JSON content type with 415', async () => {
    const deps = makeDeps()
    const { status } = await callRoute('POST', PATH, { deps, payload: { weeks: 2 }, contentType: 'text/plain' })
    expect(status).toBe(415)
    expect(deps.jobHost.runJobCalls).toEqual([])
  })

  it('rejects an oversized body with 413', async () => {
    const deps = makeDeps()
    const { status, destroyed } = await callRoute('POST', PATH, {
      deps,
      chunks: ['x'.repeat(40_000), 'x'.repeat(40_000)],
    })
    expect(status).toBe(413)
    expect(destroyed).toBe(true)
  })

  it('never puts a host error string in the response body', async () => {
    const deps = makeDeps()
    deps.jobHost.nextOutcome = () => Promise.reject(new Error('spawn ENOENT /Users/secret/path/kyberdash'))
    const { raw, status } = await callRoute('POST', PATH, { deps, payload: { weeks: 2 } })
    expect([202, 500]).toContain(status)
    expect(raw).not.toContain('secret')
    expect(raw).not.toContain('ENOENT')
  })
})
