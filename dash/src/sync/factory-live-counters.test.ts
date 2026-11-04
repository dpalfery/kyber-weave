import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { buildSessions } from '../canon/sessions.js'
import type { CanonicalRecord } from '../canon/types.js'
import { syncFactoryLiveCounters } from './factory-live-counters.js'

const tempDirs: string[] = []
const openStores: CanonStore[] = []

function createTempDir(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  tempDirs.push(dir)
  return dir
}

function createStore(dir: string): CanonStore {
  const store = new CanonStore(join(dir, 'canon.db'))
  openStores.push(store)
  return store
}

afterEach(() => {
  while (openStores.length > 0) {
    const store = openStores.pop()
    store?.close()
  }
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()
    if (dir) rmSync(dir, { recursive: true, force: true })
  }
})

const SESSION_ID = '11111111-2222-3333-4444-555555555555'
const WORKSPACE = '-Users-test-workspace'
const WORKING_DIRECTORY = '/Users/test/workspace'
const NOW_ISO = '2026-11-04T06:30:00.000Z'
const NOW_MS = Date.parse(NOW_ISO)

/**
 * A Factory root with one session: its transcript and the companion file that
 * holds the counters the plugin cannot carry.
 */
function createFactoryRoot(dir: string, options: { counters?: boolean } = {}): string {
  const factoryRoot = join(dir, 'factory')
  const sessionDir = join(factoryRoot, 'sessions', WORKSPACE)
  mkdirSync(sessionDir, { recursive: true })

  const transcript = [
    JSON.stringify({
      type: 'session_start',
      id: SESSION_ID,
      cwd: WORKING_DIRECTORY,
      timestamp: NOW_ISO,
    }),
    JSON.stringify({
      type: 'message',
      id: 'msg-user-1',
      timestamp: NOW_ISO,
      message: { role: 'user', content: [{ type: 'text', text: 'the first prompt' }] },
    }),
    JSON.stringify({
      type: 'message',
      id: 'msg-assistant-1',
      timestamp: NOW_ISO,
      message: { role: 'assistant', content: [{ type: 'text', text: 'the first answer' }] },
    }),
  ].join('\n')
  writeFileSync(join(sessionDir, `${SESSION_ID}.jsonl`), transcript, 'utf-8')

  if (options.counters !== false) {
    writeFileSync(
      join(sessionDir, `${SESSION_ID}.settings.json`),
      JSON.stringify({
        model: 'claude-sonnet-4-5',
        tokenUsage: {
          inputTokens: 1000,
          outputTokens: 200,
          cacheCreationTokens: 50,
          cacheReadTokens: 4000,
          thinkingTokens: 0,
        },
      }),
      'utf-8',
    )
  }

  return factoryRoot
}

/** The plugin's own record for the session: the turns, and no counters. */
function pluginRecord(): CanonicalRecord {
  return {
    spanId: 'plugin-span-1',
    traceId: 'plugin-trace-1',
    parentSpanId: null,
    source: 'kyberdash-factory-plugin',
    harness: 'droid',
    sessionId: SESSION_ID,
    name: 'factory.session',
    op: 'agent.invoke',
    kind: 'internal',
    timestamp: NOW_ISO,
    durationMs: 0,
    status: 'unset',
    tokens: {
      freshInput: 0,
      cacheRead: 0,
      cacheCreation: 0,
      output: 0,
      reportedInput: 0,
      reportedOutput: 0,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: {
      'factory.contract_version': '1',
      'factory.session.id': SESSION_ID,
      'factory.cwd': WORKING_DIRECTORY,
    },
  }
}

describe('the live Factory counter sync (dash/src/sync/factory-live-counters.ts)', () => {
  it('reads the counters of a captured session from the companion file the plugin cannot carry', async () => {
    const dir = createTempDir('kyber-factory-counters-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir)
    store.upsert(pluginRecord())

    const outcome = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS,
      readStates: new Map(),
    })

    expect(outcome).toMatchObject({ outcome: 'completed', sessions: 1 })

    const counters = store
      .recordsForHarnessSession('droid', SESSION_ID)
      .filter((record) => record.source === 'codeburn/droid')
    expect(counters).toHaveLength(1)
    expect(counters[0]?.tokens.freshInput).toBe(1000)
    expect(counters[0]?.tokens.output).toBe(200)
    expect(counters[0]?.tokens.cacheRead).toBe(4000)
    expect(counters[0]?.tokens.cacheCreation).toBe(50)
  })

  it('pairs the counters with the plugin turns, so the session reports measured tokens', async () => {
    const dir = createTempDir('kyber-factory-pairing-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir)
    store.upsert(pluginRecord())

    await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS,
      readStates: new Map(),
    })

    await buildSessions(store)

    const row = store
      .getDatabase()
      .prepare('SELECT payload FROM session WHERE session_id = ? AND harness = ?')
      .get(SESSION_ID, 'droid') as { payload: string } | undefined
    expect(row).toBeDefined()
    const payload = JSON.parse(row!.payload) as {
      summary: {
        total_input: unknown
        total_output: unknown
        total_cache_read: unknown
        models: string[]
      }
    }
    // The session's input is the fresh input plus the cache it read: the
    // counters the companion file reported, counted the way a session counts
    // them.
    expect(payload.summary.total_input).toBe(5050)
    expect(payload.summary.total_output).toBe(200)
    expect(payload.summary.total_cache_read).toBe(4000)
    expect(payload.summary.models).toEqual(['claude-sonnet-4'])
  })

  it('writes nothing for a captured session the harness has no companion file for', async () => {
    const dir = createTempDir('kyber-factory-no-settings-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir, { counters: false })
    store.upsert(pluginRecord())

    const outcome = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS,
      readStates: new Map(),
    })

    expect(outcome).toMatchObject({ outcome: 'completed', sessions: 0, records: 0 })
    const counters = store
      .recordsForHarnessSession('droid', SESSION_ID)
      .filter((record) => record.source === 'codeburn/droid')
    expect(counters).toHaveLength(0)
  })

  it('reads no session the plugin did not capture', async () => {
    const dir = createTempDir('kyber-factory-uncaptured-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir)

    const outcome = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS,
      readStates: new Map(),
    })

    expect(outcome).toMatchObject({ outcome: 'completed', sessions: 0, records: 0 })
    expect(store.recordsForHarnessSession('droid', SESSION_ID)).toHaveLength(0)
  })

  it('re-reads a session only when its plugin records advanced', async () => {
    const dir = createTempDir('kyber-factory-throttle-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir)
    store.upsert(pluginRecord())
    const readStates = new Map()

    const first = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS,
      readStates,
    })
    expect(first).toMatchObject({ outcome: 'completed', sessions: 1 })

    const second = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS + 1_000,
      readStates,
    })
    expect(second).toMatchObject({ outcome: 'completed', sessions: 0, records: 0 })

    const advanced = pluginRecord()
    advanced.spanId = 'plugin-span-2'
    advanced.timestamp = '2026-11-04T06:31:00.000Z'
    store.upsert(advanced)

    const third = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS + 2_000,
      readStates,
    })
    expect(third).toMatchObject({ outcome: 'completed', sessions: 1 })
  })
})
