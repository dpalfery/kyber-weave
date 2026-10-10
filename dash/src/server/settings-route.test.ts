// `GET|PUT /api/kyber/settings` (issue #319 T11 RED, architecture rule R1): the shared
// settings the tray and the web UI both read and write over ONE API. Values live in
// canon.db metadata (shared-settings.ts); the route is a typed view over them. These fail
// until the route exists: today /api/kyber/settings answers the JSON 404 catch-all.
import { describe, expect, it } from 'vitest'

import { SETTING_KEYS, readSetting, writeSetting } from '../settings/shared-settings.js'
import { callRoute, makeDeps } from './testing.js'

const DEFAULTS = {
  folderImportScheduled: false,
  jobsPaused: false,
  refreshCadenceMinutes: 5,
  receiverHosted: false,
}

describe('GET /api/kyber/settings', () => {
  it('returns the documented defaults for a fresh store', async () => {
    const deps = makeDeps()
    const { status, body } = await callRoute('GET', '/api/kyber/settings', { deps })
    expect(status).toBe(200)
    expect(body).toEqual(DEFAULTS)
  })

  it('reflects values written through the shared-settings module', async () => {
    const deps = makeDeps()
    writeSetting(deps.store, SETTING_KEYS.jobsPaused, 'on')
    writeSetting(deps.store, SETTING_KEYS.refreshCadenceMinutes, 30)
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')
    writeSetting(deps.store, SETTING_KEYS.folderImportScheduled, 'on')
    const { body } = await callRoute('GET', '/api/kyber/settings', { deps })
    expect(body).toEqual({
      folderImportScheduled: true,
      jobsPaused: true,
      refreshCadenceMinutes: 30,
      receiverHosted: true,
    })
  })
})

describe('PUT /api/kyber/settings', () => {
  it('applies a partial body and answers the full object', async () => {
    const deps = makeDeps()
    const { status, body } = await callRoute('PUT', '/api/kyber/settings', {
      deps,
      payload: { folderImportScheduled: true },
    })
    expect(status).toBe(200)
    expect(body).toEqual({ ...DEFAULTS, folderImportScheduled: true })
    expect(readSetting(deps.store, SETTING_KEYS.folderImportScheduled)).toBe('on')
    expect(readSetting(deps.store, SETTING_KEYS.jobsPaused)).toBe('off')
  })

  it('accepts every key in one body and persists each', async () => {
    const deps = makeDeps()
    const next = {
      folderImportScheduled: true,
      jobsPaused: true,
      refreshCadenceMinutes: 1440,
      receiverHosted: true,
    }
    const { status, body } = await callRoute('PUT', '/api/kyber/settings', { deps, payload: next })
    expect(status).toBe(200)
    expect(body).toEqual(next)
    const again = await callRoute('GET', '/api/kyber/settings', { deps })
    expect(again.body).toEqual(next)
  })

  it('can turn a setting back off', async () => {
    const deps = makeDeps()
    writeSetting(deps.store, SETTING_KEYS.jobsPaused, 'on')
    const { body } = await callRoute('PUT', '/api/kyber/settings', { deps, payload: { jobsPaused: false } })
    expect(body).toMatchObject({ jobsPaused: false })
    expect(readSetting(deps.store, SETTING_KEYS.jobsPaused)).toBe('off')
  })

  it.each([
    ['an unknown key', { folderImport: true }],
    ['a string for a boolean', { jobsPaused: 'true' }],
    ['a number for a boolean', { receiverHosted: 1 }],
    ['a string cadence', { refreshCadenceMinutes: '5' }],
    ['cadence below the minimum', { refreshCadenceMinutes: 0 }],
    ['cadence above a day', { refreshCadenceMinutes: 1441 }],
    ['a fractional cadence', { refreshCadenceMinutes: 2.5 }],
    ['a negative cadence', { refreshCadenceMinutes: -5 }],
    ['an array body', [{ jobsPaused: true }]],
    ['malformed JSON', '{not json'],
  ])('rejects %s with 400 and writes nothing', async (_label, payload) => {
    const deps = makeDeps()
    const { status, body } = await callRoute('PUT', '/api/kyber/settings', { deps, payload })
    expect(status).toBe(400)
    expect(body).toHaveProperty('error')
    expect(readSetting(deps.store, SETTING_KEYS.jobsPaused)).toBe('off')
    expect(readSetting(deps.store, SETTING_KEYS.refreshCadenceMinutes)).toBe(5)
  })

  it('applies none of a body when one key in it is invalid (all-or-nothing)', async () => {
    const deps = makeDeps()
    const { status } = await callRoute('PUT', '/api/kyber/settings', {
      deps,
      payload: { jobsPaused: true, refreshCadenceMinutes: 0 },
    })
    expect(status).toBe(400)
    expect(readSetting(deps.store, SETTING_KEYS.jobsPaused)).toBe('off')
  })

  it.each(['POST', 'DELETE', 'PATCH'])('rejects %s with 405', async (method) => {
    const { status, handled } = await callRoute(method, '/api/kyber/settings', { deps: makeDeps(), payload: {} })
    expect(handled).toBe(true)
    expect(status).toBe(405)
  })

  it('rejects a non-JSON content type with 415 and writes nothing', async () => {
    const deps = makeDeps()
    const { status } = await callRoute('PUT', '/api/kyber/settings', {
      deps,
      payload: { jobsPaused: true },
      contentType: 'text/plain',
    })
    expect(status).toBe(415)
    expect(readSetting(deps.store, SETTING_KEYS.jobsPaused)).toBe('off')
  })

  it('rejects an oversized body with 413, destroys the request, and writes nothing', async () => {
    const deps = makeDeps()
    const { status, destroyed } = await callRoute('PUT', '/api/kyber/settings', {
      deps,
      chunks: ['x'.repeat(40_000), 'x'.repeat(40_000)],
    })
    expect(status).toBe(413)
    expect(destroyed).toBe(true)
    expect(readSetting(deps.store, SETTING_KEYS.jobsPaused)).toBe('off')
  })

  it('never echoes internals in a failure body', async () => {
    const deps = makeDeps()
    const { raw, status } = await callRoute('PUT', '/api/kyber/settings', { deps, payload: { jobsPaused: 'x' } })
    expect(status).toBe(400)
    expect(raw).not.toContain('settings.jobs.paused')
    expect(raw).not.toMatch(/\/Users\/|node_modules|at .*\(.*:\d+:\d+\)/)
  })
})
