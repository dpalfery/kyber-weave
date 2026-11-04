import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { buildSessions } from '../canon/sessions.js'
import type { CanonicalRecord } from '../canon/types.js'
import { factoryRootOf, syncFactoryLiveCounters } from './factory-live-counters.js'

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
function pluginRecord(sessionId: string = SESSION_ID): CanonicalRecord {
  return {
    spanId: 'plugin-span-1',
    traceId: 'plugin-trace-1',
    parentSpanId: null,
    source: 'kyberdash-factory-plugin',
    harness: 'droid',
    sessionId,
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
      'factory.session.id': sessionId,
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

  it('reads no session whose id would name a file outside the sessions root', async () => {
    const dir = createTempDir('kyber-factory-traversal-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir)

    // The session id comes out of the plugin's own span, so it is data and
    // never a path: a value carrying a separator must not name a file
    // outside the sessions root.
    const secret = [
      JSON.stringify({ type: 'session_start', id: 'escaped', cwd: WORKING_DIRECTORY }),
      JSON.stringify({
        type: 'message',
        id: 'msg-assistant-1',
        timestamp: NOW_ISO,
        message: { role: 'assistant', content: [{ type: 'text', text: 'the answer' }] },
      }),
    ].join('\n')
    writeFileSync(join(factoryRoot, 'escaped.jsonl'), secret, 'utf-8')
    writeFileSync(
      join(factoryRoot, 'escaped.settings.json'),
      JSON.stringify({
        model: 'claude-sonnet-4-5',
        tokenUsage: { inputTokens: 1000, outputTokens: 200, cacheCreationTokens: 0, cacheReadTokens: 0 },
      }),
      'utf-8',
    )
    store.upsert(pluginRecord('../../escaped'))

    const outcome = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS,
      readStates: new Map(),
    })

    expect(outcome).toMatchObject({ outcome: 'completed', sessions: 0, records: 0 })
    // Nothing was read outside the sessions root, so no counter record was
    // written at all — not under the session id the span named, and not
    // under the id the file outside the root carried.
    const written = store
      .getDatabase()
      .prepare(`SELECT session_id FROM records WHERE source = 'codeburn/droid'`)
      .all() as Array<{ session_id: string }>
    expect(written).toHaveLength(0)
  })

  it('fails the pass, rather than reporting no sessions, when the sessions root cannot be read', async () => {
    const dir = createTempDir('kyber-factory-unreadable-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir)
    store.upsert(pluginRecord())

    const sessionsRoot = join(factoryRoot, 'sessions')
    chmodSync(sessionsRoot, 0o000)
    try {
      const outcome = await syncFactoryLiveCounters({
        store,
        stateDir: dir,
        factoryRoot,
        now: () => NOW_MS,
        readStates: new Map(),
      })

      // "No Factory sessions" and "the sessions root cannot be read" are
      // different statements, and only one of them is true here.
      expect(outcome.outcome).toBe('failed')
      expect(outcome.outcome === 'failed' ? outcome.reason : '').toContain(sessionsRoot)
    } finally {
      chmodSync(sessionsRoot, 0o755)
    }
  })

  it('reads a transcript whose timestamp is not a string, rather than aborting the pass', async () => {
    const dir = createTempDir('kyber-factory-numeric-timestamp-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir)
    store.upsert(pluginRecord())

    // A transcript is data: its timestamps are whatever the harness wrote.
    const sessionDir = join(factoryRoot, 'sessions', WORKSPACE)
    const transcriptPath = join(sessionDir, `${SESSION_ID}.jsonl`)
    const lines = readFileSync(transcriptPath, 'utf-8').split('\n')
    const numeric = JSON.parse(lines[2] ?? '{}') as Record<string, unknown>
    numeric['timestamp'] = 1764832800000
    writeFileSync(transcriptPath, [lines[0], lines[1], JSON.stringify(numeric)].join('\n'), 'utf-8')

    const outcome = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS,
      readStates: new Map(),
    })

    expect(outcome.outcome).toBe('completed')
  })

  it('reads no relative root when FACTORY_DIR is empty', () => {
    const previous = process.env['FACTORY_DIR']
    process.env['FACTORY_DIR'] = ''
    try {
      // An empty root is an absent root: a relative `sessions` path would read
      // whatever happens to sit beside the process.
      expect(factoryRootOf(undefined)).toBe(join(homedir(), '.factory'))
    } finally {
      if (previous === undefined) delete process.env['FACTORY_DIR']
      else process.env['FACTORY_DIR'] = previous
    }
  })

  it('reads the default number of sessions when the caller asks for none', async () => {
    const dir = createTempDir('kyber-factory-unbounded-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir)
    store.upsert(pluginRecord())

    // A count that is not a positive integer reaches a SQL LIMIT as it is.
    const outcome = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS,
      readStates: new Map(),
      maxSessions: 0,
    })

    expect(outcome).toMatchObject({ outcome: 'completed', sessions: 1 })
  })

  it('does not re-read a session whose records advanced within the same instant', async () => {
    const dir = createTempDir('kyber-factory-same-instant-')
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

    // The same instant, written two ways. The throttle compares instants, so
    // this is not a new read.
    const later = pluginRecord()
    later.spanId = 'plugin-span-2'
    later.timestamp = '2026-11-04T06:30:00.000+00:00'
    store.upsert(later)

    const second = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS + 1_000,
      readStates,
    })
    expect(second).toMatchObject({ outcome: 'completed', sessions: 0, records: 0 })
  })

  it('does not scan a session with nothing to read again on every tick', async () => {
    const dir = createTempDir('kyber-factory-negative-throttle-')
    const store = createStore(dir)
    // The session has a transcript but no companion file, so it has no
    // counters to read.
    const factoryRoot = createFactoryRoot(dir, { counters: false })
    store.upsert(pluginRecord())
    const readStates = new Map()

    const first = await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS,
      readStates,
    })
    expect(first).toMatchObject({ outcome: 'completed', sessions: 0, records: 0 })
    const readAtMs = readStates.get(SESSION_ID)?.readAtMs
    expect(readAtMs).toBe(NOW_MS)

    await syncFactoryLiveCounters({
      store,
      stateDir: dir,
      factoryRoot,
      now: () => NOW_MS + 1_000,
      readStates,
    })

    // The session was not scanned again: the session is read again when its
    // own records advance, which is when a companion file can have appeared.
    expect(readStates.get(SESSION_ID)?.readAtMs).toBe(readAtMs)
  })

  it('names the lock directory when the lock cannot be taken', async () => {
    const dir = createTempDir('kyber-factory-lock-')
    const store = createStore(dir)
    const factoryRoot = createFactoryRoot(dir)
    store.upsert(pluginRecord())

    // A state directory under a file cannot be created, so the lock cannot be
    // taken — and the reason must name where.
    const blocker = join(dir, 'blocker')
    writeFileSync(blocker, 'not a directory', 'utf-8')
    const stateDir = join(blocker, 'state')

    const outcome = await syncFactoryLiveCounters({
      store,
      stateDir,
      factoryRoot,
      now: () => NOW_MS,
      readStates: new Map(),
    })

    expect(outcome.outcome).toBe('failed')
    expect(outcome.outcome === 'failed' ? outcome.reason : '').toContain(stateDir)
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
