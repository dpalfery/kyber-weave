import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { createRequire } from 'node:module'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import {
  KyberBridge,
  buildTransportIndex,
  dedupedRunSessions,
  lookupTransport,
  sumSessionFigures,
  type SessionSummaryFigures,
  _clip,
} from './bridge.js'
import { CanonStore } from '../canon/store.js'
import { buildSessions } from '../canon/sessions.js'
import { buildRuns } from '../canon/runs.js'
import type { CanonicalRecord } from '../canon/types.js'
import { buildContextReport } from '../analysis/report/build.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

describe('KyberBridge: _clip helper', () => {
  it('leaves short strings unchanged', () => {
    expect(_clip('hello world')).toBe('hello world')
    expect(_clip('')).toBe('')
  })

  it('truncates strings exceeding maxLen and appends truncated message', () => {
    const longString = 'a'.repeat(2500)
    const clipped = _clip(longString, 2000)
    expect(clipped.length).toBe(2000 + '... [truncated, 2500 chars]'.length)
    expect(clipped.startsWith('a'.repeat(2000))).toBe(true)
    expect(clipped).toContain('... [truncated, 2500 chars]')
  })

  it('recursively clips string leaves in objects and arrays preserving structure', () => {
    const data = {
      id: 123,
      name: 'short',
      longLeaf: 'x'.repeat(2100),
      items: [
        'normal',
        'y'.repeat(2050),
        { nested: 'z'.repeat(2010), count: 42 },
      ],
    }

    const clipped = _clip(data)
    expect(clipped.id).toBe(123)
    expect(clipped.name).toBe('short')
    expect(clipped.longLeaf).toContain('... [truncated, 2100 chars]')
    expect(clipped.items[0]).toBe('normal')
    expect(clipped.items[1]).toContain('... [truncated, 2050 chars]')
    const nestedItem = clipped.items[2] as { nested: string; count: number }
    expect(nestedItem.nested).toContain('... [truncated, 2010 chars]')
    expect(nestedItem.count).toBe(42)
  })

  it('stops recursion beyond depth 8', () => {
    let deep: unknown = 'deep-leaf'
    for (let i = 0; i < 10; i++) {
      deep = { next: deep }
    }
    const clipped = _clip(deep)
    expect(clipped).toBeDefined()
  })

  it('handles primitives, null, and undefined unchanged', () => {
    expect(_clip(null)).toBeNull()
    expect(_clip(undefined)).toBeUndefined()
    expect(_clip(42)).toBe(42)
    expect(_clip(true)).toBe(true)
  })
})

describe('KyberBridge: Missing / Empty Database Fallbacks', () => {
  it('ignores legacy database environment variables', () => {
    const directory = mkdtempSync(join(tmpdir(), 'kyber-legacy-db-'))
    const legacyPath = join(directory, 'sessions.db')
    const previousAgentdashDb = process.env.AGENTDASH_DB
    const previousKyberDb = process.env.KYBER_DB
    const legacyDb = new DatabaseSync(legacyPath)
    legacyDb.exec(`
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY,
        harness TEXT NOT NULL,
        label TEXT,
        is_subagent INTEGER,
        parent_session TEXT,
        agent_name TEXT,
        repo TEXT,
        branch TEXT,
        started TEXT,
        ended TEXT,
        payload TEXT,
        summary_json TEXT,
        problems_count INTEGER
      );
    `)
    legacyDb
      .prepare('INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
      .run(
        'legacy-only-session',
        'legacy',
        'Legacy session',
        0,
        null,
        null,
        null,
        null,
        '2026-09-04T00:00:00.000Z',
        null,
        JSON.stringify({ id: 'legacy-only-session' }),
      )
    legacyDb.close()
    process.env.AGENTDASH_DB = legacyPath
    process.env.KYBER_DB = legacyPath

    const bridge = new KyberBridge({ canonPath: ':memory:' })
    try {
      expect(bridge.listSessions()).not.toContainEqual(
        expect.objectContaining({ session_id: 'legacy-only-session' }),
      )
      expect(bridge.getSessionPayload('legacy-only-session')).toBeNull()
    } finally {
      bridge.close()
      if (previousAgentdashDb === undefined) delete process.env.AGENTDASH_DB
      else process.env.AGENTDASH_DB = previousAgentdashDb
      if (previousKyberDb === undefined) delete process.env.KYBER_DB
      else process.env.KYBER_DB = previousKyberDb
      rmSync(directory, { recursive: true, force: true })
    }
  })

  it('instantiates cleanly and returns empty structures when files do not exist', () => {
    const bridge = new KyberBridge({
      canonPath: '/path/does/not/exist/canon.db',
      ratesPath: '/path/does/not/exist/rates.json',
    })

    try {
      expect(bridge.listSessions()).toEqual([])
      expect(bridge.getSessionPayload('nonexistent')).toBeNull()
      expect(bridge.getComparisonTable()).toEqual({
        harnesses: [],
        rows: [],
        problems: [],
      })
      expect(bridge.getQuarantine()).toEqual([])
      expect(bridge.getProblems()).toEqual([])

      const meta = bridge.getMeta()
      expect(meta.span_count).toBe(0)
      expect(meta.quarantined).toBe(0)
      expect(meta.tokenizer.kind).toBe('js-tiktoken/o200k_base')
      expect(meta.rates.credit_usd).toBe(0.01)
      expect(meta.harnesses).toEqual({})
      expect(meta.sources).toEqual([])
    } finally {
      bridge.close()
    }
  })

  it('handles in-memory sqlite database gracefully', () => {
    const bridge = new KyberBridge({
      canonPath: ':memory:',
    })
    try {
      expect(bridge.listSessions()).toEqual([])
      expect(bridge.getSessionPayload('test')).toBeNull()
      expect(bridge.getComparisonTable()).toEqual({
        harnesses: [],
        rows: [],
        problems: [],
      })
    } finally {
      bridge.close()
    }
  })
})

describe('KyberBridge: in-memory minimal tables fixture (CI verified)', () => {
  let canonDb: import('node:sqlite').DatabaseSync
  let sessionsDb: import('node:sqlite').DatabaseSync
  let bridge: KyberBridge

  beforeAll(() => {
    canonDb = new DatabaseSync(':memory:')
    sessionsDb = new DatabaseSync(':memory:')

    // Create minimal tables in canonDb: session, records, quarantine, problem
    canonDb.exec(`
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY,
        harness TEXT NOT NULL,
        label TEXT,
        is_subagent INTEGER DEFAULT 0,
        parent_session TEXT,
        agent_name TEXT,
        repo TEXT,
        branch TEXT,
        started TEXT,
        ended TEXT,
        payload TEXT,
        summary_json TEXT,
        problems_count INTEGER
      );
      CREATE TABLE records (
        span_id TEXT PRIMARY KEY,
        trace_id TEXT,
        parent_span_id TEXT,
        harness TEXT,
        source TEXT,
        name TEXT,
        timestamp TEXT,
        op TEXT
      );
      CREATE TABLE quarantine (
        span_id TEXT PRIMARY KEY,
        source TEXT,
        name TEXT,
        namespaces TEXT,
        reason TEXT,
        seen_at INTEGER
      );
      CREATE TABLE problem (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT,
        span_id TEXT,
        severity TEXT,
        code TEXT,
        message TEXT,
        at INTEGER,
        harness TEXT
      );
    `)

    // Create minimal tables in sessionsDb: session, quarantine, problem
    sessionsDb.exec(`
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY,
        harness TEXT NOT NULL,
        label TEXT,
        is_subagent INTEGER DEFAULT 0,
        parent_session TEXT,
        agent_name TEXT,
        repo TEXT,
        branch TEXT,
        started TEXT,
        ended TEXT,
        payload TEXT,
        summary_json TEXT,
        problems_count INTEGER
      );
      CREATE TABLE quarantine (
        span_id TEXT PRIMARY KEY,
        source TEXT,
        name TEXT,
        namespaces TEXT,
        reason TEXT,
        seen_at INTEGER
      );
      CREATE TABLE problem (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT,
        span_id TEXT,
        severity TEXT,
        code TEXT,
        message TEXT,
        at INTEGER,
        harness TEXT
      );
    `)

    // Insert mock data into canonDb
    const canonPayloadNew = JSON.stringify({
      id: 'sess-canon-new',
      harness: 'copilot',
      summary: {
        turn_count: 5,
        request_count: 5,
        total_input: 1000,
        total_output: 500,
        total_cache_read: 200,
        total_cache_creation: 100,
        schema_tokens_per_turn: 50,
        cost: { usd: 0.05, basis: 'published_rates', status: 'priced', value: 0.05, currency: 'USD' },
        models: ['gpt-4o'],
      },
      problems: ['prob-1'],
    })

    const canonPayloadShared = JSON.stringify({
      id: 'sess-shared',
      harness: 'copilot',
      summary: {
        turn_count: 2,
        request_count: 2,
        total_input: 400,
        total_output: 200,
        cost: { usd: 0.02, basis: 'published_rates', status: 'priced', value: 0.02, currency: 'USD' },
        models: ['gpt-4o'],
      },
    })

    canonDb
      .prepare(
        'INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'
      )
      .run(
        'sess-canon-new',
        'copilot',
        'Canon New Session',
        0,
        null,
        'canon-agent',
        'repo1',
        'main',
        '2026-09-03T12:00:00Z',
        '2026-09-03T12:05:00Z',
        canonPayloadNew
      )

    canonDb
      .prepare(
        'INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'
      )
      .run(
        'sess-shared',
        'copilot',
        'Shared Session (Canon)',
        0,
        null,
        'canon-agent-wins',
        'repo1',
        'main',
        '2026-09-03T11:00:00Z',
        '2026-09-03T11:02:00Z',
        canonPayloadShared
      )

    // Raw trace in canon records table
    canonDb
      .prepare(
        'INSERT INTO records VALUES (?, ?, ?, ?, ?, ?, ?, ?)'
      )
      .run(
        'span-trace-1',
        'trace-records-1',
        null,
        'pi',
        'pi-agent',
        'Pi Trace Session',
        '2026-09-03T10:30:00Z',
        'llm.invoke'
      )

    // Quarantine in canonDb
    canonDb
      .prepare('INSERT INTO quarantine VALUES (?, ?, ?, ?, ?, ?)')
      .run('quar-canon-1', 'copilot', 'test_span', '["custom.attr"]', 'unmapped namespace', 1725360000)
    canonDb
      .prepare('INSERT INTO quarantine VALUES (?, ?, ?, ?, ?, ?)')
      .run('quar-shared', 'copilot', 'shared_span', '["shared.attr"]', 'canon quarantine reason', 1725360001)

    // Problem in canonDb
    canonDb
      .prepare('INSERT INTO problem (session_id, span_id, severity, code, message, at, harness) VALUES (?, ?, ?, ?, ?, ?, ?)')
      .run('sess-canon-new', 'prob-canon-1', 'error', 'invalid_tokens', 'Token count mismatch', 1725360000, 'copilot')
    canonDb
      .prepare('INSERT INTO problem (session_id, span_id, severity, code, message, at, harness) VALUES (?, ?, ?, ?, ?, ?, ?)')
      .run('sess-shared', 'prob-shared', 'warning', 'basis_diff', 'Canon problem message', 1725360001, 'copilot')

    // Insert mock data into sessionsDb
    const sessionsPayloadOlder = JSON.stringify({
      id: 'sess-sessions-older',
      harness: 'gemini',
      summary: {
        turn_count: 3,
        request_count: 3,
        total_input: 600,
        total_output: 300,
        total_cache_read: 0,
        total_cache_creation: 0,
        cost: { usd: 0.03, basis: 'published_rates' },
        models: ['gemini-1.5-pro'],
      },
    })

    const sessionsPayloadShared = JSON.stringify({
      id: 'sess-shared',
      harness: 'copilot',
      summary: {
        turn_count: 2,
        request_count: 2,
        total_input: 400,
        total_output: 200,
        cost: { usd: 0.02, basis: 'published_rates', status: 'priced', value: 0.02, currency: 'USD' },
        models: ['gpt-4o'],
      },
    })

    sessionsDb
      .prepare(
        'INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'
      )
      .run(
        'sess-sessions-older',
        'gemini',
        'Sessions Older Session',
        0,
        null,
        'sessions-agent',
        'repo2',
        'dev',
        '2026-09-03T09:00:00Z',
        '2026-09-03T09:05:00Z',
        sessionsPayloadOlder
      )

    sessionsDb
      .prepare(
        'INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'
      )
      .run(
        'sess-shared',
        'copilot',
        'Shared Session (Sessions)',
        0,
        null,
        'sessions-agent-shadowed',
        'repo1',
        'main',
        '2026-09-03T11:00:00Z',
        '2026-09-03T11:02:00Z',
        sessionsPayloadShared
      )

    // Quarantine in sessionsDb
    sessionsDb
      .prepare('INSERT INTO quarantine VALUES (?, ?, ?, ?, ?, ?)')
      .run('quar-sessions-1', 'gemini', 'gemini_span', '["gemini.attr"]', 'gemini quarantine reason', 1725350000)
    sessionsDb
      .prepare('INSERT INTO quarantine VALUES (?, ?, ?, ?, ?, ?)')
      .run('quar-shared', 'copilot', 'shared_span', '["shared.attr"]', 'sessions quarantine reason shadowed', 1725350001)

    // Problem in sessionsDb
    sessionsDb
      .prepare('INSERT INTO problem (session_id, span_id, severity, code, message, at, harness) VALUES (?, ?, ?, ?, ?, ?, ?)')
      .run('sess-sessions-older', 'prob-sessions-1', 'warning', 'unknown_field', 'Sessions problem message', 1725350000, 'gemini')
    sessionsDb
      .prepare('INSERT INTO problem (session_id, span_id, severity, code, message, at, harness) VALUES (?, ?, ?, ?, ?, ?, ?)')
      .run('sess-shared', 'prob-shared', 'warning', 'basis_diff', 'Sessions problem shadowed', 1725350001, 'copilot')

    bridge = new KyberBridge({ canonDb })
  })

  afterAll(() => {
    bridge.close()
  })

  it('listSessions reports only canonical derived sessions, never record-only groups', () => {
    const sessions = bridge.listSessions()
    const ids = sessions.map((s) => s.session_id)

    // The raw `records` row keys a session that has no derived `session` row.
    // It has no authoritative persisted payload, so it must not be synthesized
    // into a reported session.
    expect(ids).not.toContain('trace-records-1')
    expect(sessions.length).toBe(2)
    expect(ids).toContain('sess-canon-new')
    expect(ids).toContain('sess-shared')
    expect(ids).not.toContain('sess-sessions-older')

    // A record-only id consequently has no session payload either.
    expect(bridge.getSessionPayload('trace-records-1')).toBeNull()

    // Verify deduplication: canonDb session took priority
    const shared = sessions.find((s) => s.session_id === 'sess-shared')!
    expect(shared.agent_name).toBe('canon-agent-wins')
    expect(shared.label).toBe('Shared Session (Canon)')

    // Derived rows still parse their persisted summary payload.
    const newest = sessions.find((s) => s.session_id === 'sess-canon-new')!
    expect(newest.turn_count).toBe(5)
    expect(newest.request_count).toBe(5)
    expect(newest.total_input).toBe(1000)
    expect(newest.total_output).toBe(500)
    expect(newest.cost_usd).toBe(0.05)
    expect(newest.models).toEqual(['gpt-4o'])
    expect(newest.problems).toBe(1)
  })

  it('listSessions sorts derived sessions strictly by started DESC before applying limit', () => {
    const sessions = bridge.listSessions()
    expect(sessions.map((s) => s.session_id)).toEqual(['sess-canon-new', 'sess-shared']) // 12:00:00Z, 11:00:00Z

    // Verify limit slices after global started DESC sort over derived rows
    const top2 = bridge.listSessions(2)
    expect(top2.length).toBe(2)
    expect(top2.map((s) => s.session_id)).toEqual(['sess-canon-new', 'sess-shared'])

    const top1 = bridge.listSessions(1)
    expect(top1.length).toBe(1)
    expect(top1[0].session_id).toBe('sess-canon-new')
  })

  it('getSessionPayload reads only canonDb', () => {
    const sharedPayload = bridge.getSessionPayload('sess-shared')
    expect(sharedPayload).not.toBeNull()
    expect(sharedPayload!.id).toBe('sess-shared')

    const olderPayload = bridge.getSessionPayload('sess-sessions-older')
    expect(olderPayload).toBeNull()

    expect(bridge.getSessionPayload('non-existent')).toBeNull()
  })

  it('getComparisonTable does not derive rows from legacy session tables', () => {
    const table = bridge.getComparisonTable()
    expect(table).toEqual({
      harnesses: [],
      rows: [],
      problems: [],
    })
  })

  it('getQuarantine reads canonical entries', () => {
    const quarantined = bridge.getQuarantine()
    expect(quarantined.length).toBe(2)

    const spanIds = quarantined.map((q) => q.span_id)
    expect(spanIds).toContain('quar-canon-1')
    expect(spanIds).toContain('quar-shared')
    expect(spanIds).not.toContain('quar-sessions-1')

    // Canon priority on duplicate span_id
    const shared = quarantined.find((q) => q.span_id === 'quar-shared')!
    expect(shared.reason).toBe('canon quarantine reason')

    // Limit parameter respected
    const limited = bridge.getQuarantine(2)
    expect(limited.length).toBe(2)
  })

  it('getProblems reads canonical diagnostics', () => {
    const problems = bridge.getProblems()
    expect(problems.length).toBe(2)

    const spanIds = problems.map((p) => p.span_id)
    expect(spanIds).toContain('prob-canon-1')
    expect(spanIds).toContain('prob-shared')
    expect(spanIds).not.toContain('prob-sessions-1')

    // Canon priority on duplicate problem
    const shared = problems.find((p) => p.span_id === 'prob-shared')!
    expect(shared.message).toBe('Canon problem message')

    // Limit parameter respected
    const limited = bridge.getProblems(2)
    expect(limited.length).toBe(2)
  })

  it('getMeta counts canonical spans and quarantine entries', () => {
    const meta = bridge.getMeta()
    expect(meta.span_count).toBe(1) // 1 record in records table
    expect(meta.quarantined).toBe(2)
    expect(meta.tokenizer.kind).toBe('js-tiktoken/o200k_base')
    expect(meta.rates.credit_usd).toBe(0.01)
  })
})

describe('KyberBridge: canonical-store comparison', () => {
  it('derives comparison rows from injected canonical records with AGENTDASH_DB unset', () => {
    const previousAgentdashDb = process.env.AGENTDASH_DB
    delete process.env.AGENTDASH_DB

    const store = new CanonStore(':memory:')
    const records: CanonicalRecord[] = [
      {
        spanId: 'copilot-turn',
        traceId: 'trace-copilot',
        parentSpanId: null,
        sessionId: 'canon-copilot',
        source: 'synthetic',
        harness: 'copilot',
        name: 'synthetic copilot turn',
        op: 'llm.invoke',
        kind: 'client',
        timestamp: '2026-09-04T12:00:00.000Z',
        durationMs: 100,
        status: 'ok',
        tokens: { freshInput: 80, cacheRead: 20, cacheCreation: 0, output: 40, reportedInput: 100, reportedOutput: 40 },
        content: {},
        cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
      },
      {
        spanId: 'gemini-turn',
        traceId: 'trace-gemini',
        parentSpanId: null,
        sessionId: 'canon-gemini',
        source: 'synthetic',
        harness: 'gemini',
        name: 'synthetic gemini turn',
        op: 'llm.invoke',
        kind: 'client',
        timestamp: '2026-09-04T12:01:00.000Z',
        durationMs: 100,
        status: 'ok',
        tokens: { freshInput: 50, cacheRead: 0, cacheCreation: 0, output: 25, reportedInput: 50, reportedOutput: 25 },
        content: {},
        cost: { basis: 'published', status: 'priced', value: 0.005, currency: 'USD' },
      },
    ]
    store.upsertMany(records)
    const bridge = new KyberBridge({ canonPath: ':memory:', store })

    try {
      const table = bridge.getComparisonTable()
      expect(process.env.AGENTDASH_DB).toBeUndefined()
      expect(table.harnesses).toEqual(['copilot', 'gemini'])
      expect(table.rows.find((row) => row.metric === 'turns')?.cells.copilot.value).toBe(1)
      expect(table.rows.find((row) => row.metric === 'turns')?.cells.gemini.value).toBe(1)
    } finally {
      bridge.close()
      store.close()
      if (previousAgentdashDb === undefined) delete process.env.AGENTDASH_DB
      else process.env.AGENTDASH_DB = previousAgentdashDb
    }
  })
})

describe('KyberBridge: DB-backed report facts', () => {
  it('propagates injected-store cost lookup failures', () => {
    const brokenStore = {
      costContributionsForSessions: () => {
        throw new Error('store cost lookup failed')
      },
    } as unknown as CanonStore
    const bridge = new KyberBridge({ canonPath: ':memory:', store: brokenStore })
    try {
      expect(() => bridge.getSessionCostContributions(['session-1'])).toThrow('store cost lookup failed')
    } finally {
      bridge.close()
    }
  })

  it('propagates a later cost chunk failure instead of returning a partial total', () => {
    let costQueries = 0
    const db = {
      exec: () => {},
      prepare: (sql: string) => {
        if (sql.includes('sqlite_master')) return { get: () => ({}) }
        if (sql.includes('cost_json')) {
          costQueries += 1
          if (costQueries === 2) throw new Error('second cost chunk failed')
          return { all: () => [{ session_key: 'session-0', cost_json: '{"basis":"published","status":"priced","value":2}' }] }
        }
        throw new Error(`unexpected query: ${sql}`)
      },
      close: () => {},
    } as unknown as import('node:sqlite').DatabaseSync
    const bridge = new KyberBridge({ canonDb: db })
    const ids = Array.from({ length: 901 }, (_, index) => `session-${index}`)
    try {
      expect(() => bridge.getSessionCostContributions(ids)).toThrow('second cost chunk failed')
      expect(costQueries).toBe(2)
    } finally {
      bridge.close()
    }
  })

  it('reports uncapped diagnostics, refresh state, and scoped priced cost from canon.db', () => {
    const directory = mkdtempSync(join(tmpdir(), 'kyber-report-db-'))
    const dbPath = join(directory, 'canon.db')
    const store = new CanonStore(dbPath)
    const sessionId = 'sess-report-db'
    const now = new Date('2026-09-19T12:00:00.000Z')

    try {
      store.upsertSession({
        sessionId,
        harness: 'cursor',
        repo: 'kyber-weave',
        started: '2026-09-19T10:00:00.000Z',
        ended: '2026-09-19T11:00:00.000Z',
        payload: {
          summary: { turn_count: 1, total_input: 1_000, total_output: 20 },
          context: {
            measurable: true,
            contextLimit: 200_000,
            turns: [{
              index: 1,
              pressure: 0.005,
              buckets: {
                system_prompt: 500,
                tool_definitions: 200,
                instruction_context: 100,
                conversation_history: 150,
                tool_result_content: 50,
              },
              residual: { tokens: 0 },
              toolDefinitionsByServer: {},
            }],
          },
        },
      })

      const records: CanonicalRecord[] = Array.from({ length: 250 }, (_, index) => ({
        spanId: `priced-harness-${index}`,
        traceId: `trace-${index}`,
        parentSpanId: null,
        source: 'synthetic',
        harness: 'cursor',
        sessionId,
        name: `priced record ${index}`,
        op: 'llm.invoke',
        kind: 'client',
        timestamp: `2026-09-19T10:${String(index % 60).padStart(2, '0')}:00.000Z`,
        durationMs: 10,
        status: 'ok',
        tokens: {
          freshInput: 10,
          cacheRead: 0,
          cacheCreation: 0,
          output: 2,
          reportedInput: 10,
          reportedOutput: 2,
        },
        content: {},
        cost: { basis: 'harness', status: 'priced', value: 0.25, currency: 'USD' },
      }))
      records.push({
        ...records[0]!,
        spanId: 'priced-published-1',
        traceId: 'trace-published-1',
        cost: { basis: 'published', status: 'priced', value: 0.25, currency: 'USD' },
      })
      records.push({
        ...records[0]!,
        spanId: 'priced-published-2',
        traceId: 'trace-published-2',
        cost: { basis: 'published', status: 'priced', value: 0.75, currency: 'USD' },
      })
      store.upsertMany(records)

      for (let index = 0; index < 251; index += 1) {
        store.quarantine(`quarantined-${index}`, ['synthetic'], 'fixture quarantine')
      }
      for (let index = 0; index < 307; index += 1) {
        store.recordProblem({
          spanId: `problem-${index}`,
          severity: 'warning',
          code: 'FIXTURE_PROBLEM',
          message: 'fixture problem',
          location: `fixture-${index}`,
        })
      }

      store.startRefreshRun({
        id: 'refresh-success',
        startedAt: '2026-09-19T10:30:00.000Z',
        pid: process.pid,
        trigger: 'cli',
      })
      store.completeRefreshRun(
        'refresh-success',
        'success',
        '2026-09-19T10:45:00.000Z',
        'fixture refresh succeeded',
      )
      store.startRefreshRun({
        id: 'refresh-failure',
        startedAt: '2026-09-19T11:30:00.000Z',
        pid: process.pid,
        trigger: 'scheduled',
      })
      store.completeRefreshRun(
        'refresh-failure',
        'failure',
        '2026-09-19T11:45:00.000Z',
        'fixture refresh failed',
      )
      const dbBridge = new KyberBridge({ canonPath: dbPath })
      const injectedBridge = new KyberBridge({ canonPath: dbPath, store })
      try {
        const dbReport = buildContextReport(dbBridge, { days: 7 }, { now, storePath: dbPath })
        const injectedReport = buildContextReport(injectedBridge, { days: 7 }, { now, storePath: dbPath })

        expect(dbReport).toEqual(injectedReport)
        expect(dbReport.coverage).toMatchObject({
          quarantineCount: 251,
          problemCount: 307,
          refresh: {
            lastSuccessAt: '2026-09-19T10:45:00.000Z',
            lastFailure: {
              at: '2026-09-19T11:45:00.000Z',
              summary: 'fixture refresh failed',
            },
            inProgress: null,
          },
        })
        expect(dbReport.cost).toEqual([
          { basis: 'harness', amountUsd: { value: 62.5, unit: 'USD' } },
          { basis: 'published', amountUsd: { value: 1, unit: 'USD' } },
        ])
      } finally {
        dbBridge.close()
        injectedBridge.close()
      }
    } finally {
      try {
        store.close()
      } finally {
        rmSync(directory, { recursive: true, force: true })
      }
    }
  })

  it('lists distinct-identity diagnostics separately, matching the problem count', () => {
    const directory = mkdtempSync(join(tmpdir(), 'kyber-problems-db-'))
    const dbPath = join(directory, 'canon.db')
    const store = new CanonStore(dbPath)
    try {
      // Distinct locations, empty versus absent location, and delimiters inside ids.
      const identities: Array<{ spanId: string; code: string; location?: string }> = [
        { spanId: 'span-shared', code: 'FIXTURE_PROBLEM', location: 'file-a.ts' },
        { spanId: 'span-shared', code: 'FIXTURE_PROBLEM', location: 'file-b.ts' },
        { spanId: 'span-shared', code: 'FIXTURE_PROBLEM', location: '' },
        { spanId: 'span-shared', code: 'FIXTURE_PROBLEM' },
        { spanId: 'a:b', code: 'c' },
        { spanId: 'a', code: 'b:c' },
      ]
      for (const identity of identities) {
        store.recordProblem({ ...identity, severity: 'warning', message: 'fixture problem' })
      }
      const bridge = new KyberBridge({ canonPath: dbPath })
      try {
        expect(bridge.getProblems()).toHaveLength(identities.length)
        expect(bridge.getProblemCount()).toBe(identities.length)
      } finally {
        bridge.close()
      }
    } finally {
      try {
        store.close()
      } finally {
        rmSync(directory, { recursive: true, force: true })
      }
    }
  })

  it('surfaces errors from getProblemCount and getQuarantineCount when queries fail', () => {
    const brokenStore = {
      countQuarantine: () => {
        throw new Error('store quarantine count error')
      },
      countProblems: () => {
        throw new Error('store problem count error')
      },
    } as unknown as CanonStore

    const bridge = new KyberBridge({ canonPath: ':memory:', store: brokenStore })
    try {
      expect(() => bridge.getQuarantineCount()).toThrow('store quarantine count error')
      expect(() => bridge.getProblemCount()).toThrow('store problem count error')
    } finally {
      bridge.close()
    }
  })

  it('returns exact zero when problem and quarantine tables are clean', () => {
    const cleanStore = {
      countQuarantine: () => 0,
      countProblems: () => 0,
    } as unknown as CanonStore

    const bridge = new KyberBridge({ canonPath: ':memory:', store: cleanStore })
    try {
      expect(bridge.getQuarantineCount()).toBe(0)
      expect(bridge.getProblemCount()).toBe(0)
    } finally {
      bridge.close()
    }
  })
})

describe('KyberBridge: T4 coverage read seam (plan docs/plans/2026-09-30-issues-189-198-199, D2)', () => {
  const WEEK_MS = 7 * 24 * 60 * 60 * 1000

  function recordFor(overrides: Partial<CanonicalRecord>): CanonicalRecord {
    return {
      spanId: 'span-t4',
      traceId: 'trace-t4',
      parentSpanId: null,
      source: 'synthetic',
      harness: 'pi',
      name: 't4 record',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-10T12:00:00.000Z',
      durationMs: 10,
      status: 'ok',
      tokens: { freshInput: 10, cacheRead: 0, cacheCreation: 0, output: 2, reportedInput: 10, reportedOutput: 2 },
      content: {},
      cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
      ...overrides,
    }
  }

  it('exposes the refresh coverage window with derived bounds on the last success', () => {
    const store = new CanonStore(':memory:')
    try {
      store.startRefreshRun({
        id: 't4-windowed',
        startedAt: '2026-09-12T00:00:00.000Z',
        pid: process.pid,
        trigger: 'cli',
        historyWeeks: 6,
      })
      store.completeRefreshRun('t4-windowed', 'success', '2026-09-12T00:30:00.000Z', 't4 ok')
      const bridge = new KyberBridge({ canonPath: ':memory:', store })
      try {
        const state = bridge.getRefreshState()
        expect(state.lastSuccessAt).toBe('2026-09-12T00:30:00.000Z')
        expect(state.historyWeeks).toBe(6)
        expect(state.coveredThrough).toBe('2026-09-12T00:00:00.000Z')
        expect(state.coveredFrom).toBe(
          new Date(Date.parse('2026-09-12T00:00:00.000Z') - 6 * WEEK_MS).toISOString(),
        )
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('reads pre-migration refresh rows as unknown window, never zero', () => {
    const store = new CanonStore(':memory:')
    try {
      store.startRefreshRun({
        id: 't4-legacy',
        startedAt: '2026-09-12T00:00:00.000Z',
        pid: process.pid,
        trigger: 'cli',
      })
      store.completeRefreshRun('t4-legacy', 'success', '2026-09-12T00:30:00.000Z', 't4 legacy ok')
      const bridge = new KyberBridge({ canonPath: ':memory:', store })
      try {
        const state = bridge.getRefreshState()
        expect(state.historyWeeks).toBeNull()
        expect(state.coveredFrom).toBeNull()
        expect(state.coveredThrough).toBeNull()
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('reads databases without the history_weeks column as unknown rather than throwing', () => {
    const canonDb = new DatabaseSync(':memory:')
    try {
      canonDb.exec(`
        CREATE TABLE refresh_run (
          id TEXT PRIMARY KEY,
          started_at TEXT NOT NULL,
          completed_at TEXT,
          status TEXT NOT NULL,
          pid INTEGER NOT NULL,
          trigger TEXT NOT NULL,
          summary TEXT
        );
      `)
      canonDb
        .prepare(
          'INSERT INTO refresh_run (id, started_at, completed_at, status, pid, trigger, summary) VALUES (?, ?, ?, ?, ?, ?, ?)',
        )
        .run('old-row', '2026-09-12T00:00:00.000Z', '2026-09-12T00:30:00.000Z', 'success', process.pid, 'cli', 'old')
      const bridge = new KyberBridge({ canonDb })
      try {
        const state = bridge.getRefreshState()
        expect(state.lastSuccessAt).toBe('2026-09-12T00:30:00.000Z')
        expect(state.historyWeeks).toBeNull()
        expect(state.coveredFrom).toBeNull()
        expect(state.coveredThrough).toBeNull()
      } finally {
        bridge.close()
      }
    } finally {
      try {
        canonDb.close()
      } catch {
        // KyberBridge.close() already closed the injected handle.
      }
    }
  })

  it('derives per-source ingest activity without inventing receiver status', () => {
    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([
        recordFor({ spanId: 't4-s1', source: 'codeburn/pi', harness: 'pi' }),
        recordFor({ spanId: 't4-s2', source: 'codeburn/pi', harness: 'pi' }),
        recordFor({ spanId: 't4-s3', source: 'agy', harness: 'antigravity' }),
        recordFor({ spanId: 't4-s4', source: 'unattributed', harness: 'pi' }),
      ])
      store.logIngest('agy', 5)
      store.logIngest('agy', 3)
      const recordsBefore = store.count()
      const logBefore = store.getIngestLog().length
      const bridge = new KyberBridge({ canonPath: ':memory:', store })
      try {
        const activity = bridge.getIngestActivity()
        expect(activity.status).toBe('known')
        expect(activity.lastReceivedAt).not.toBeNull()
        const bySource = new Map(activity.sources.map((entry) => [entry.source, entry]))
        expect(bySource.get('codeburn/pi')?.recordCount).toBe(2)
        expect(bySource.get('codeburn/pi')?.display).toBe('pi')
        expect(bySource.get('codeburn/pi')?.kind).toBe('local-file')
        expect(bySource.get('codeburn/pi')?.ingestedCount).toBe(0)
        expect(bySource.get('codeburn/pi')?.lastReceivedAt).toBeNull()
        expect(bySource.get('agy')?.recordCount).toBe(1)
        expect(bySource.get('agy')?.ingestedCount).toBe(8)
        expect(bySource.get('agy')?.lastReceivedAt).toBe(activity.lastReceivedAt)
        expect(bySource.get('unattributed')?.recordCount).toBe(1)
        expect(bySource.get('unattributed')?.kind).toBe('legacy-unattributed')
        // Read-only: no rows added by the read.
        expect(store.count()).toBe(recordsBefore)
        expect(store.getIngestLog()).toHaveLength(logBefore)
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('reports unknown receiver status when nothing was ever recorded, never zero or running', () => {
    const store = new CanonStore(':memory:')
    try {
      const bridge = new KyberBridge({ canonPath: ':memory:', store })
      try {
        const activity = bridge.getIngestActivity()
        expect(activity.status).toBe('unknown')
        expect(activity.sources).toEqual([])
        expect(activity.lastReceivedAt).toBeNull()
        if (activity.status === 'unknown') {
          expect(activity.reason).toMatch(/no receiver activity recorded/)
        }
        expect(activity.status).not.toBe('running')
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('exposes source checkpoint statuses read-only, including partial with zero records', () => {
    const store = new CanonStore(':memory:')
    try {
      const base = {
        providerId: 'pi',
        parserId: 'pi-jsonl',
        parserContractVersion: '1',
        format: 'jsonl',
        sourceRootLabel: '~/.pi/agent/sessions',
        revisionToken: 'rev-1',
        coveredFromUtc: '2026-08-29T00:00:00.000Z',
        coveredThroughUtc: '2026-09-12T00:00:00.000Z',
        lastAttemptUtc: '2026-09-12T00:00:00.000Z',
        lastSuccessUtc: '2026-09-12T00:00:01.000Z',
        lastErrorCode: null,
        unitCount: 1,
      } as const
      store.commitSourceUnit({
        records: [],
        provenance: [],
        checkpoint: {
          ...base,
          harnessId: 'pi',
          sourceKey: 'session:ok-unit',
          lastStatus: 'ok',
          recordCount: 3,
        },
      })
      store.commitSourceUnit({
        records: [],
        provenance: [],
        checkpoint: {
          ...base,
          harnessId: 'pi',
          sourceKey: 'session:partial-unit',
          lastStatus: 'partial',
          lastErrorCode: 'PARSE_WARN',
          recordCount: 0,
        },
      })
      const before = store.listSourceCheckpoints().length
      const bridge = new KyberBridge({ canonPath: ':memory:', store })
      try {
        const statuses = bridge.getSourceCheckpointStatuses()
        expect(statuses).toHaveLength(2)
        const partial = statuses?.find((entry) => entry.sourceKey === 'session:partial-unit')
        expect(partial?.lastStatus).toBe('partial')
        expect(partial?.recordCount).toBe(0)
        expect(bridge.getSourceCheckpointStatuses('pi')).toHaveLength(2)
        expect(bridge.getSourceCheckpointStatuses('cursor')).toEqual([])
        // Read-only: the read added no checkpoints.
        expect(store.listSourceCheckpoints()).toHaveLength(before)
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('reads a non-numeric file-backed history_weeks as unknown, never NaN', () => {
    // Review PR #230 (kilo nux5R): Number('junk') is NaN, and NaN ?? null is
    // still NaN — the file branch must use the shared normalizer so a bad
    // value reads as unknown, matching the store mapper and the contract.
    const canonDb = new DatabaseSync(':memory:')
    try {
      canonDb.exec(`
        CREATE TABLE refresh_run (
          id TEXT PRIMARY KEY,
          started_at TEXT NOT NULL,
          completed_at TEXT,
          status TEXT NOT NULL,
          pid INTEGER NOT NULL,
          trigger TEXT NOT NULL,
          summary TEXT,
          history_weeks TEXT
        );
      `)
      canonDb
        .prepare(
          'INSERT INTO refresh_run (id, started_at, completed_at, status, pid, trigger, summary, history_weeks) VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
        )
        .run('bad-weeks', '2026-09-12T00:00:00.000Z', '2026-09-12T00:30:00.000Z', 'success', process.pid, 'cli', 'ok', 'junk')
      const bridge = new KyberBridge({ canonDb })
      try {
        const state = bridge.getRefreshState()
        expect(state.historyWeeks).toBeNull()
        expect(state.coveredFrom).toBeNull()
        expect(state.coveredThrough).toBeNull()
      } finally {
        bridge.close()
      }
    } finally {
      try {
        canonDb.close()
      } catch {
        // KyberBridge.close() already closed the injected handle.
      }
    }
  })

  it('degrades to unknown receiver status when the injected store read throws', () => {
    // Review PR #230 (kilo nux5H): the file branch documents reads as no
    // receiver activity, not a throw — the store branch must degrade
    // identically instead of turning /coverage into a 500.
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    store.close()
    try {
      const activity = bridge.getIngestActivity()
      expect(activity.status).toBe('unknown')
    } finally {
      bridge.close()
    }
  })

  it('returns null checkpoint statuses when the checkpoint table is absent or unreadable', () => {
    // Review PR #230 (kilo nux5Z): [] must mean "read fine, no units" — a
    // failed or impossible read is null (unknown) so routes never fabricate
    // {ok: 0, partial: 0, failed: 0, unavailable: 0} for unreadable coverage.
    const canonDb = new DatabaseSync(':memory:')
    try {
      const bridge = new KyberBridge({ canonDb })
      try {
        expect(bridge.getSourceCheckpointStatuses()).toBeNull()
      } finally {
        bridge.close()
      }
    } finally {
      try {
        canonDb.close()
      } catch {
        // KyberBridge.close() already closed the injected handle.
      }
    }
  })

  it('tallies quarantine reasons with GROUP BY on both the store and file handles', () => {
    // Review PR #230 (copilot numrV / kilo nux5e): the coverage endpoint must
    // not materialize the quarantine table to count reasons.
    const store = new CanonStore(':memory:')
    try {
      store.quarantine('span-q1', ['pi'], 'unclaimed')
      store.quarantine('span-q2', ['pi'], 'unclaimed')
      store.quarantine('span-q3', ['copilot'], 'non-model span: health check')
      const bridge = new KyberBridge({ canonPath: ':memory:', store })
      try {
        expect(bridge.getQuarantineCountsByReason()).toEqual([
          { reason: 'unclaimed', count: 2 },
          { reason: 'non-model span: health check', count: 1 },
        ])
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })
})

describe('KyberBridge run figures review follow-ups', () => {
  // Copilot C4: a priced non-USD block is omitted, never served as dollars.
  // The legacy `usd` shape predates currency and names dollars.
  it('serves costUsd only for USD-priced figures', () => {
    const store = new CanonStore(':memory:')
    const session = (id: string, cost: unknown): void => {
      store.upsertSession({
        sessionId: id,
        harness: 'copilot',
        payload: {
          id,
          summary: { turn_count: 1, total_input: 100, total_output: 10, cost },
        },
      })
    }
    session('sess-usd', { basis: 'published', status: 'priced', value: 0.05, currency: 'USD' })
    session('sess-eur', { basis: 'published', status: 'priced', value: 0.05, currency: 'EUR' })
    session('sess-legacy', { usd: 0.12, basis: 'published_rates', status: 'ok' })
    session('sess-norate', { basis: 'unknown', status: 'no_rate' })
    // Re-review Kilo 5: a `partial` block still carries a priced figure.
    session('sess-partial', { basis: 'published', status: 'partial', value: 0.03, currency: 'USD' })

    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      const figures = bridge.sessionSummaryFigures([
        'sess-usd',
        'sess-eur',
        'sess-legacy',
        'sess-norate',
        'sess-partial',
      ])
      expect(figures.get('sess-usd')?.costUsd).toBe(0.05)
      expect(figures.get('sess-eur')?.costUsd).toBeUndefined()
      expect(figures.get('sess-legacy')?.costUsd).toBe(0.12)
      expect(figures.get('sess-norate')?.costUsd).toBeUndefined()
      expect(figures.get('sess-partial')?.costUsd).toBe(0.03)
    } finally {
      bridge.close()
      store.close()
    }
  })

  // Kilo K3 / Copilot C5: a priced figure beside an unpriced session is a
  // partial sum wearing a total's suit — mark it, or omit when nothing priced.
  it('marks partial run costs and omits unpriced ones', () => {
    const store = new CanonStore(':memory:')
    const session = (id: string, cost: unknown): void => {
      store.upsertSession({
        sessionId: id,
        harness: 'copilot',
        payload: { id, summary: { turn_count: 1, cost } },
      })
    }
    session('sess-p1', { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' })
    session('sess-p2', { basis: 'unknown', status: 'no_rate' })

    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      const summaries = bridge.sessionSummaryFigures(['sess-p1', 'sess-p2'])
      expect(sumSessionFigures(summaries, ['sess-p1', 'sess-p2'])).toEqual({
        turnCount: 2,
        costUsd: 0.01,
        costStatus: 'partial',
        partial: true,
        partialFields: ['costUsd'],
      })
      // Every session priced: complete, no marker.
      expect(
        sumSessionFigures(summaries, ['sess-p1']),
      ).toEqual({ turnCount: 1, costUsd: 0.01 })
      // Nothing priced: absent, never $0.
      expect(sumSessionFigures(summaries, ['sess-p2'])).toEqual({ turnCount: 1 })
    } finally {
      bridge.close()
      store.close()
    }
  })

  // Re-review #2 (Kilo B): when every session's cost block is partial, the
  // priced count equals the linked count, so neither marker fires and the
  // total looks complete. A per-session partial flag marks it.
  it('marks cost partial when every session is partial', () => {
    const store = new CanonStore(':memory:')
    const session = (id: string): void => {
      store.upsertSession({
        sessionId: id,
        harness: 'copilot',
        payload: {
          id,
          summary: {
            turn_count: 1,
            cost: { basis: 'published', status: 'partial', value: 0.02, currency: 'USD' },
          },
        },
      })
    }
    session('sess-ap1')
    session('sess-ap2')
    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      const summaries = bridge.sessionSummaryFigures(['sess-ap1', 'sess-ap2'])
      expect(summaries.get('sess-ap1')?.costUsd).toBe(0.02)
      // costStatus marks the partial cost; no general `partial` flag — both
      // sessions are otherwise fully accounted for.
      expect(sumSessionFigures(summaries, ['sess-ap1', 'sess-ap2'])).toEqual({
        turnCount: 2,
        costUsd: 0.04,
        costStatus: 'partial',
      })
    } finally {
      bridge.close()
      store.close()
    }
  })

  // Re-review #2 (Kilo 4): a field present in some summaries and missing in
  // others names exactly which cells are subtotals.
  it('names partially-covered fields', () => {
    const summaries: SessionSummaryFigures = new Map([
      ['s-a', { turnCount: 2, totalInput: 500, totalOutput: 50 }],
      ['s-b', { turnCount: 3 }],
    ])
    expect(sumSessionFigures(summaries, ['s-a', 's-b'])).toEqual({
      turnCount: 5,
      totalInput: 500,
      totalOutput: 50,
      partial: true,
      partialFields: ['totalInput', 'totalOutput'],
    })
  })

  // Re-review Kilo 5: turn/input/output totals are subtotals when a linked
  // session contributes no figures — marked, not complete-looking.
  it('marks non-cost totals partial when a linked session is missing', () => {
    const store = new CanonStore(':memory:')
    store.upsertSession({
      sessionId: 'sess-only',
      harness: 'copilot',
      payload: { id: 'sess-only', summary: { turn_count: 2, total_input: 500 } },
    })
    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      const summaries = bridge.sessionSummaryFigures(['sess-only'])
      // A linked session with no summary row at all: subtotal.
      expect(sumSessionFigures(summaries, ['sess-only', 'sess-gone'])).toEqual({
        turnCount: 2,
        totalInput: 500,
        partial: true,
        partialFields: ['turnCount', 'totalInput'],
      })
      // Every linked session accounted for: complete, no marker.
      expect(sumSessionFigures(summaries, ['sess-only'])).toEqual({
        turnCount: 2,
        totalInput: 500,
      })
    } finally {
      bridge.close()
      store.close()
    }
  })

  // Re-review Kilo 7: in direct-DB mode json_extract yields null (not
  // undefined) for a missing currency — a priced block without one must
  // still read as USD, exactly as store mode treats it.
  it('keeps a priced block without currency in direct-DB mode', () => {
    const canonDb = new DatabaseSync(':memory:')
    canonDb.exec(`
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, label TEXT,
        is_subagent INTEGER DEFAULT 0, parent_session TEXT, agent_name TEXT,
        repo TEXT, branch TEXT, started TEXT, ended TEXT, payload TEXT,
        summary_json TEXT, problems_count INTEGER
      );
    `)
    canonDb
      .prepare('INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
      .run(
        'sess-nocur',
        'copilot',
        'no currency',
        0,
        null,
        null,
        null,
        null,
        '2026-09-04T00:00:00.000Z',
        null,
        JSON.stringify({
          id: 'sess-nocur',
          summary: {
            turn_count: 1,
            cost: { basis: 'published', status: 'priced', value: 0.07 },
          },
        }),
      )
    const bridge = new KyberBridge({ canonDb })
    try {
      expect(bridge.sessionSummaryFigures(['sess-nocur']).get('sess-nocur')?.costUsd).toBe(0.07)
    } finally {
      // Bridge owns the passed database handle.
      bridge.close()
    }
  })
})

describe('KyberBridge run session streaming (re-review #2 Kilo 4/5)', () => {
  // The generator is lazy: pulling one session parses exactly one payload.
  // An eager shared Map would parse every session up front.
  it('streams one payload at a time', async () => {
    const store = new CanonStore(':memory:')
    const rec = (spanId: string, sessionId: string): import('../canon/types.js').CanonicalRecord => ({
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      sessionId,
      source: 'synthetic',
      harness: 'copilot',
      name: 'canonical run turn',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-04T12:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: {
        freshInput: 800,
        cacheRead: 200,
        cacheCreation: 0,
        output: 100,
        reportedInput: 1000,
        reportedOutput: 100,
      },
      content: {},
      parts: [{ part: 'system_prompt', text: 'sys', tokens: 600 }],
      cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
      raw: { model: 'gpt-4o', cwd: '/repo' },
    })
    store.upsertMany([rec('st-t1', 'stream-a'), rec('st-t2', 'stream-b')])
    await buildSessions(store)
    await buildRuns(store)
    const runId = store.listRuns('copilot')[0]!.runId

    let payloadReads = 0
    class CountingStore extends CanonStore {
      override getSessionPayload(sessionId: string): unknown | undefined {
        payloadReads += 1
        return super.getSessionPayload(sessionId)
      }
    }
    const counting = new CountingStore(':memory:')
    counting.upsertMany([rec('st-t1', 'stream-a'), rec('st-t2', 'stream-b')])
    await buildSessions(counting)
    await buildRuns(counting)

    const bridge = new KyberBridge({ canonPath: ':memory:', store: counting })
    try {
      const executions = counting.listExecutions(runId)
      expect(executions).toHaveLength(2)
      const stream = bridge.streamRunSessionPayloads(executions)
      const first = stream.next()
      expect(first.done).toBe(false)
      // One pull, one parse — the second session is untouched.
      expect(payloadReads).toBe(1)
    } finally {
      bridge.close()
      store.close()
      counting.close()
    }
  })

  // Two executions sharing one session digest it once, not twice.
  it('deduplicates shared sessions for the digest', () => {
    const exec = (id: string): import('../canon/types.js').ExecutionRow => ({
      executionId: id,
      runId: 'run-dedup',
      sessionId: 'sess-shared',
      parentExecutionId: null,
      harness: 'copilot',
      agentName: null,
      isRoot: true,
      started: null,
      ended: null,
      parentLinkage: 'measured',
    })
    function* source(): Generator<{
      execution: import('../canon/types.js').ExecutionRow
      payload: Record<string, unknown> & { context?: unknown }
    }> {
      const payload = { id: 'sess-shared' }
      yield { execution: exec('exec-1'), payload }
      yield { execution: exec('exec-2'), payload }
    }
    const seen = [...dedupedRunSessions(source())]
    expect(seen).toHaveLength(1)
    expect(seen[0]!.execution.executionId).toBe('exec-1')
  })
})


describe('KyberBridge.listSessions cost mapping (issue #186)', () => {
  let db: InstanceType<typeof DatabaseSync>
  let bridge: KyberBridge

  const insert = (id: string, started: string, cost: Record<string, unknown> | undefined) => {
    const summary: Record<string, unknown> = { turn_count: 1, request_count: 1, models: ['m'] }
    if (cost) summary.cost = cost
    db.prepare('INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)').run(
      id, 'claude-code', id, 0, null, 'agent', 'repo', 'main', started, started,
      JSON.stringify({ id, harness: 'claude-code', summary }),
    )
  }

  beforeAll(() => {
    db = new DatabaseSync(':memory:')
    db.exec(`
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, label TEXT,
        is_subagent INTEGER DEFAULT 0, parent_session TEXT, agent_name TEXT,
        repo TEXT, branch TEXT, started TEXT, ended TEXT, payload TEXT,
        summary_json TEXT, problems_count INTEGER
      );
      CREATE TABLE records (
        span_id TEXT PRIMARY KEY, trace_id TEXT, parent_span_id TEXT, harness TEXT,
        source TEXT, name TEXT, timestamp TEXT, op TEXT
      );
      CREATE TABLE quarantine (
        span_id TEXT PRIMARY KEY, source TEXT, name TEXT, namespaces TEXT, reason TEXT, seen_at INTEGER
      );
      CREATE TABLE problem (
        id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT, span_id TEXT,
        severity TEXT, code TEXT, message TEXT, at INTEGER, harness TEXT
      );
    `)
    insert('s-priced', '2026-09-30T05:00:00Z', { basis: 'published', status: 'priced', value: 0.05, currency: 'USD' })
    insert('s-norate', '2026-09-30T04:00:00Z', { basis: 'published', status: 'no_rate' })
    insert('s-partial', '2026-09-30T03:00:00Z', { basis: 'published', status: 'partial', value: 0.02, currency: 'USD' })
    insert('s-nonusd', '2026-09-30T02:00:00Z', { basis: 'harness', status: 'priced', value: 7, currency: 'EUR' })
    insert('s-none', '2026-09-30T01:00:00Z', undefined)
    bridge = new KyberBridge({ canonDb: db })
  })

  afterAll(() => {
    bridge.close()
  })

  const find = (id: string) => bridge.listSessions().find((s) => s.session_id === id)!

  it('maps a priced CostBlock to a cost figure and exposes the block', () => {
    const row = find('s-priced')
    expect(row.cost_usd).toBe(0.05)
    expect(row.cost).toEqual({ basis: 'published', status: 'priced', value: 0.05, currency: 'USD' })
  })

  it('lists a no_rate block with no figure and its status', () => {
    const row = find('s-norate')
    expect(row.cost_usd).toBeNull()
    expect(row.cost.status).toBe('no_rate')
    expect(row.cost.basis).toBe('published')
  })

  it('lists a partial block with no figure and partial status', () => {
    const row = find('s-partial')
    expect(row.cost_usd).toBeNull()
    expect(row.cost.status).toBe('partial')
  })

  it('does not report a non-USD priced value as cost_usd', () => {
    const row = find('s-nonusd')
    expect(row.cost_usd).toBeNull()
    expect(row.cost).toMatchObject({ basis: 'harness', status: 'priced', currency: 'EUR' })
  })

  it('defaults a row with no summary cost to unknown/no_rate', () => {
    const row = find('s-none')
    expect(row.cost_usd).toBeNull()
    expect(row.cost).toEqual({ basis: 'unknown', status: 'no_rate' })
  })
})

describe('KyberBridge.listSessions column-only read path (sessions-list conversion)', () => {
  // RED contract for the sessions-list read-path conversion. The primary list
  // statement must be column-only (no `payload` blob, no `json_extract(payload`);
  // a `WHERE summary_json IS NULL OR problems_count IS NULL` fallback covers
  // rows missing a materialized value only; the observable list is identical
  // across the materialized, legacy-fallback, and no-JSON-function paths;
  // payload turns/problems regions never affect the list; and `limit` still
  // slices after nesting and rollup.
  const SUMMARY: Record<string, unknown> = {
    turn_count: 4,
    request_count: 4,
    total_input: 800,
    total_output: 400,
    cost: { basis: 'published', status: 'priced', value: 0.04, currency: 'USD' },
    models: ['gpt-4o'],
  }
  const SUMMARY_JSON = JSON.stringify(SUMMARY)

  const payloadWith = (
    id: string,
    problems: unknown[] = [],
    turns: unknown[] = [],
    summary: Record<string, unknown> = SUMMARY,
  ): string => JSON.stringify({ id, harness: 'copilot', summary, problems, turns })

  const createListDb = (): import('node:sqlite').DatabaseSync => {
    const db = new DatabaseSync(':memory:')
    db.exec(`
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, label TEXT,
        is_subagent INTEGER DEFAULT 0, parent_session TEXT, agent_name TEXT,
        repo TEXT, branch TEXT, started TEXT, ended TEXT, payload TEXT,
        summary_json TEXT, problems_count INTEGER
      );
    `)
    return db
  }

  const insertListRow = (
    db: import('node:sqlite').DatabaseSync,
    row: {
      id: string
      started: string
      payload: string
      summaryJson?: string | null
      problemsCount?: number | null
      parentSession?: string | null
      isSubagent?: number
    },
  ): void => {
    db.prepare(
      `INSERT INTO session (session_id, harness, label, is_subagent, parent_session,
        agent_name, repo, branch, started, ended, payload, summary_json, problems_count)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      row.id,
      'copilot',
      `${row.id} label`,
      row.isSubagent ?? 0,
      row.parentSession ?? null,
      'agent',
      'repo',
      'main',
      row.started,
      row.started,
      row.payload,
      row.summaryJson ?? null,
      row.problemsCount ?? null,
    )
  }

  // Interception reuses the file's established "unexpected query" mechanism: a
  // `prepare` wrapper that records the SQL the bridge actually issues instead
  // of retyping it, so a change to the bridge's own statement text flips these
  // assertions.
  const tapSessionStatements = (db: import('node:sqlite').DatabaseSync): string[] => {
    const statements: string[] = []
    const originalPrepare = db.prepare.bind(db)
    db.prepare = ((sql: string) => {
      statements.push(sql)
      return (originalPrepare as (sql: string) => unknown)(sql)
    }) as typeof db.prepare
    return statements
  }

  const sessionStatements = (statements: string[]): string[] =>
    statements.filter((sql) => sql.includes('FROM session'))

  const readSessions = (db: import('node:sqlite').DatabaseSync) => {
    const bridge = new KyberBridge({ canonDb: db })
    try {
      return bridge.listSessions()
    } finally {
      bridge.close()
    }
  }

  it('issues a primary list statement that never touches the payload blob', () => {
    const db = createListDb()
    insertListRow(db, {
      id: 'sess-a',
      started: '2026-09-04T12:00:00.000Z',
      payload: payloadWith('sess-a', ['p1', 'p2']),
      summaryJson: SUMMARY_JSON,
      problemsCount: 2,
    })
    insertListRow(db, {
      id: 'sess-b',
      started: '2026-09-04T11:00:00.000Z',
      payload: payloadWith('sess-b'),
      summaryJson: SUMMARY_JSON,
      problemsCount: 0,
    })
    const statements = tapSessionStatements(db)
    const bridge = new KyberBridge({ canonDb: db })
    try {
      expect(bridge.listSessions()).toHaveLength(2)
      const lists = sessionStatements(statements)
      expect(lists.length).toBeGreaterThan(0)
      const primary = lists[0]!
      expect(primary).not.toContain('payload')
      expect(primary).not.toContain('json_extract(payload')
    } finally {
      bridge.close()
    }
  })

  it('issues exactly one payload-free statement when every row is materialized', () => {
    const db = createListDb()
    insertListRow(db, {
      id: 'sess-a',
      started: '2026-09-04T12:00:00.000Z',
      payload: payloadWith('sess-a', ['p1', 'p2']),
      summaryJson: SUMMARY_JSON,
      problemsCount: 2,
    })
    const statements = tapSessionStatements(db)
    const bridge = new KyberBridge({ canonDb: db })
    try {
      expect(bridge.listSessions()).toHaveLength(1)
      const lists = sessionStatements(statements)
      expect(lists).toHaveLength(1)
      expect(lists[0]).not.toContain('payload')
    } finally {
      bridge.close()
    }
  })

  it('issues the legacy fallback only for rows missing a materialized value', () => {
    const db = createListDb()
    insertListRow(db, {
      id: 'sess-new',
      started: '2026-09-04T12:00:00.000Z',
      payload: payloadWith('sess-new', ['p1']),
      summaryJson: SUMMARY_JSON,
      problemsCount: 1,
    })
    insertListRow(db, {
      id: 'sess-legacy',
      started: '2026-09-04T11:00:00.000Z',
      payload: payloadWith('sess-legacy', ['p1', 'p2', 'p3']),
      summaryJson: null,
      problemsCount: null,
    })
    const statements = tapSessionStatements(db)
    const bridge = new KyberBridge({ canonDb: db })
    try {
      const sessions = bridge.listSessions()
      expect(sessions.map((session) => session.session_id).sort()).toEqual(['sess-legacy', 'sess-new'])
      const lists = sessionStatements(statements)
      expect(lists).toHaveLength(2)
      const fallback = lists[1]!
      // The fallback legitimately reads payload-derived JSON, but only for
      // the rows missing a materialized value — a row whose summary_json or
      // problems_count is NULL — that the primary could not serve.
      expect(fallback).toContain('payload')
      expect(fallback).toContain('summary_json IS NULL')
      expect(fallback).toContain('problems_count IS NULL')
    } finally {
      bridge.close()
    }
  })

  it('returns byte-identical lists for materialized, legacy-fallback, and no-JSON-function reads', () => {
    const seed = (legacyId: string | null, noJsonFunctions: boolean): import('node:sqlite').DatabaseSync => {
      const db = createListDb()
      const rows = [
        { id: 'sess-a', problems: ['p1', 'p2'] as unknown[], count: 2 as number | null, started: '2026-09-04T12:00:00.000Z', hasSummary: true, hasProblems: true },
        { id: 'sess-b', problems: [] as unknown[], count: 0 as number | null, started: '2026-09-04T11:00:00.000Z', hasSummary: true, hasProblems: true },
        // A payload with a problems array and no summary. The writer stores a
        // NULL summary beside a real count for it, so the count is a fact of
        // its own and must not be gated on the summary's presence.
        { id: 'sess-c', problems: ['p3', 'p4', 'p5'] as unknown[], count: 3 as number | null, started: '2026-09-04T10:00:00.000Z', hasSummary: false, hasProblems: true },
        // A payload with a summary and no problems array. The writer stores a
        // NULL count beside a real summary for it, and the count is not
        // measured.
        { id: 'sess-d', problems: [] as unknown[], count: null as number | null, started: '2026-09-04T09:00:00.000Z', hasSummary: true, hasProblems: false },
      ]
      for (const row of rows) {
        const legacy = legacyId === row.id
        insertListRow(db, {
          id: row.id,
          started: row.started,
          payload: JSON.stringify({
            id: row.id,
            harness: 'copilot',
            ...(row.hasSummary ? { summary: SUMMARY } : {}),
            ...(row.hasProblems ? { problems: row.problems } : {}),
            turns: [],
          }),
          summaryJson: legacy || !row.hasSummary ? null : SUMMARY_JSON,
          problemsCount: legacy ? null : row.count,
        })
      }
      if (noJsonFunctions) {
        const originalPrepare = db.prepare.bind(db)
        db.prepare = ((sql: string) => {
          if (sql.includes('json_extract')) throw new Error('no such function: json_extract')
          return (originalPrepare as (sql: string) => unknown)(sql)
        }) as typeof db.prepare
      }
      return db
    }
    const materialized = readSessions(seed(null, false))
    const viaFallback = readSessions(seed('sess-b', false))
    const lastResort = readSessions(seed(null, true))
    expect(JSON.stringify(viaFallback)).toBe(JSON.stringify(materialized))
    expect(JSON.stringify(lastResort)).toBe(JSON.stringify(materialized))

    // The problems count is a fact of its own: a session whose payload carries
    // a problems array and no summary still reports its count, in every read
    // path. A count that only reaches the reader alongside a summary is a
    // count the list drops.
    for (const list of [materialized, viaFallback, lastResort]) {
      expect(list.find((session) => session.session_id === 'sess-c')?.problems).toBe(3)
    }
    // A payload with no problems array carries no measured count, and the
    // reader has always reported none for it; the materialized column's NULL
    // does not change that answer.
    for (const list of [materialized, viaFallback, lastResort]) {
      expect(list.find((session) => session.session_id === 'sess-d')?.problems).toBe(0)
    }

    // The audit gap: a legacy row (NULL summary_json) on a SQLite build with
    // no JSON functions. The primary reads, the legacy fallback throws, and
    // the payload read serves both rows — observably identical to the
    // materialized list.
    const legacyDb = seed('sess-b', true)
    const legacyStatements = tapSessionStatements(legacyDb)
    const legacyLastResort = readSessions(legacyDb)
    expect(JSON.stringify(legacyLastResort)).toBe(JSON.stringify(materialized))
    const legacyPaths = sessionStatements(legacyStatements)
    expect(legacyPaths).toHaveLength(3)
    expect(legacyPaths[1]!).toContain('json_extract')
  })

  it('ignores hostile payload turns and problems regions the list no longer reads', () => {
    // The turns array is already ignored by every read path, and the problems
    // array must stop mattering once the count comes from `problems_count`:
    // both variants carry identical materialized columns, so only a reader
    // that still derives output from the payload can tell them apart.
    const hostileTurns = Array.from({ length: 2000 }, (_, index) => ({
      index,
      blob: 'x'.repeat(500),
      nested: { depth: [index, { marker: `hostile-${index}` }] },
    }))
    const hostileProblems = Array.from({ length: 500 }, (_, index) => ({
      code: `HOSTILE_${index}`,
      message: 'y'.repeat(200),
    }))
    const openVariant = (hostile: boolean): import('node:sqlite').DatabaseSync => {
      const db = createListDb()
      insertListRow(db, {
        id: 'sess-v',
        started: '2026-09-04T12:00:00.000Z',
        payload: payloadWith('sess-v', hostile ? hostileProblems : [], hostile ? hostileTurns : []),
        summaryJson: SUMMARY_JSON,
        problemsCount: 3,
      })
      return db
    }
    const calm = readSessions(openVariant(false))
    const hostile = readSessions(openVariant(true))
    expect(JSON.stringify(hostile)).toBe(JSON.stringify(calm))
  })

  it('still applies limit after nesting and rollup instead of pushing it into SQL', () => {
    // The newest *rows* are subagents of sess-p; a SQL-side LIMIT 1 would
    // return sess-c2 alone. The observable contract is the opposite: the
    // newest *root* with its children and their rolled-up counters.
    const summaryFor = (turns: number): Record<string, unknown> => ({
      ...SUMMARY,
      turn_count: turns,
      request_count: turns,
    })
    const db = createListDb()
    const rows = [
      { id: 'sess-p', started: '2026-09-04T10:00:00.000Z', turns: 1 },
      { id: 'sess-c1', started: '2026-09-04T12:00:00.000Z', turns: 2, parentSession: 'sess-p', isSubagent: 1 },
      { id: 'sess-c2', started: '2026-09-04T13:00:00.000Z', turns: 3, parentSession: 'sess-p', isSubagent: 1 },
      { id: 'sess-q', started: '2026-09-04T09:00:00.000Z', turns: 5 },
    ]
    for (const row of rows) {
      const summary = summaryFor(row.turns)
      insertListRow(db, {
        id: row.id,
        started: row.started,
        payload: payloadWith(row.id, [], [], summary),
        summaryJson: JSON.stringify(summary),
        problemsCount: 0,
        parentSession: row.parentSession,
        isSubagent: row.isSubagent,
      })
    }
    const bridge = new KyberBridge({ canonDb: db })
    try {
      const all = bridge.listSessions()
      expect(all.map((session) => session.session_id)).toEqual(['sess-p', 'sess-q'])
      expect(all[0]!.subagents).toHaveLength(2)
      const top1 = bridge.listSessions(1)
      expect(top1).toHaveLength(1)
      expect(top1[0]!.session_id).toBe('sess-p')
      expect(top1[0]!.subagents).toHaveLength(2)
      expect(top1[0]!.rollup?.turn_count).toBe(6)
    } finally {
      bridge.close()
    }
  })
})

describe('KyberBridge.listSessions covering index (session_denorm_idx contract)', () => {
  // RED-phase contract for the covering index and its planner statistics
  // (GREEN lands in store.ts: MIGRATIONS[20] and SCHEMA_SQL gain
  // `session_denorm_idx`, and both creation paths run `ANALYZE session`).
  // The primary list statement the bridge actually issues — intercepted from
  // the bridge, never retyped — must be servable as an index-only scan over
  // that index, and dropping the index must change performance, never output.
  // Until GREEN lands, the EXPLAIN assertion below fails with a plain table
  // scan (the index does not exist yet and there are no `sqlite_stat1` rows);
  // the byte-identical parity assertion already passes, trivially, with no
  // index in play. Neither test touches application code.

  /**
   * The covering index GREEN must create, in the exact column order the
   * migration ships: `started DESC` first so the scan answers
   * `ORDER BY started DESC` with no sort step, then the reader's select list
   * in reader order. Kept here only to build the index-present fixture for
   * the parity test; the definition itself is pinned in
   * `migration-v21.test.ts` against `sqlite_master`.
   */
  const COVERING_INDEX_DDL = `
    CREATE INDEX IF NOT EXISTS session_denorm_idx
      ON session (started DESC, session_id, harness, label, is_subagent,
                  parent_session, agent_name, repo, branch, ended,
                  summary_json, problems_count);
  `

  const tapStatements = (db: import('node:sqlite').DatabaseSync): string[] => {
    const statements: string[] = []
    const originalPrepare = db.prepare.bind(db)
    db.prepare = ((sql: string) => {
      statements.push(sql)
      return (originalPrepare as (sql: string) => unknown)(sql)
    }) as typeof db.prepare
    return statements
  }

  const sessionStatements = (statements: string[]): string[] =>
    statements.filter((sql) => sql.includes('FROM session'))

  it('serves the intercepted primary list statement from the covering index', () => {
    // A fresh store takes the GREEN shape from SCHEMA_SQL plus the
    // fresh-create `ANALYZE session`, so the planner must choose an
    // index-only scan for the bridge's own primary statement. RED now: no
    // index exists and `sqlite_stat1` carries no session row, so the plan is
    // a plain scan and the pinned index name is absent.
    const store = new CanonStore(':memory:')
    try {
      const summary = {
        turn_count: 4,
        request_count: 4,
        total_input: 800,
        total_output: 400,
        cost: { basis: 'published', status: 'priced', value: 0.04, currency: 'USD' },
        models: ['gpt-4o'],
      }
      for (const [id, started] of [
        ['sess-cover-new', '2026-09-04T12:00:00.000Z'],
        ['sess-cover-old', '2026-09-04T11:00:00.000Z'],
      ] as const) {
        store.upsertSession({
          sessionId: id,
          harness: 'copilot',
          label: `${id} label`,
          agentName: 'agent',
          repo: 'repo',
          branch: 'main',
          started,
          ended: started,
          payload: { id, harness: 'copilot', summary, problems: [{ code: 'p1' }] },
        })
      }
      const db = store.getDatabase()
      const statements = tapStatements(db)
      const bridge = new KyberBridge({ canonPath: ':memory:', store })
      try {
        // Fully materialized rows, so the bridge issues the primary alone.
        expect(bridge.listSessions()).toHaveLength(2)
        const lists = sessionStatements(statements)
        expect(lists.length).toBeGreaterThan(0)
        const primary = lists[0]!

        const plan = db
          .prepare(`EXPLAIN QUERY PLAN ${primary}`)
          .all() as Array<{ detail: string }>
        const text = plan.map((row) => row.detail).join('\n')
        // The index name is pinned, so a silently different plan fails.
        expect(text).toContain('USING COVERING INDEX session_denorm_idx')
        expect(text).not.toContain('payload')
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('returns byte-identical lists with the covering index present, dropped, and re-created', () => {
    // The index changes performance, never output: the same bridge over the
    // same rows serializes identically whether or not the planner can use
    // the index. Self-contained fixture — the index is created here, not by
    // the store — so this pins the invariant rather than the landing, and
    // passes before GREEN too.
    const db = new DatabaseSync(':memory:')
    db.exec(`
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, label TEXT,
        is_subagent INTEGER DEFAULT 0, parent_session TEXT, agent_name TEXT,
        repo TEXT, branch TEXT, started TEXT, ended TEXT, payload TEXT,
        summary_json TEXT, problems_count INTEGER
      );
    `)
    const summaryJson = JSON.stringify({ turn_count: 1, models: ['m'] })
    const insert = (
      id: string,
      started: string,
      summary: string | null,
      count: number | null,
      problems: unknown,
    ): void => {
      db.prepare(
        `INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name,
          repo, branch, started, ended, payload, summary_json, problems_count)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      ).run(
        id,
        'copilot',
        `${id} label`,
        0,
        null,
        'agent',
        'repo',
        'main',
        started,
        started,
        JSON.stringify({
          id,
          harness: 'copilot',
          ...(summary === null ? {} : { summary: JSON.parse(summary) }),
          problems,
        }),
        summary,
        count,
      )
    }
    insert('sess-p1', '2026-09-04T12:00:00.000Z', summaryJson, 2, [{ code: 'a' }, { code: 'b' }])
    insert('sess-p2', '2026-09-04T11:00:00.000Z', summaryJson, 0, [])
    // A legacy row (no materialized values) exercises the fallback under
    // every index state below.
    insert('sess-legacy', '2026-09-04T10:00:00.000Z', null, null, [{ code: 'c' }])
    db.exec(COVERING_INDEX_DDL)
    db.exec('ANALYZE session')
    const created = db
      .prepare("SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'session_denorm_idx'")
      .get() as { name: string } | undefined
    expect(created?.name).toBe('session_denorm_idx')

    const bridge = new KyberBridge({ canonDb: db })
    try {
      const withIndex = JSON.stringify(bridge.listSessions())
      db.exec('DROP INDEX IF EXISTS session_denorm_idx')
      const withoutIndex = JSON.stringify(bridge.listSessions())
      db.exec(COVERING_INDEX_DDL)
      const recreated = JSON.stringify(bridge.listSessions())
      expect(withoutIndex).toBe(withIndex)
      expect(recreated).toBe(withIndex)
    } finally {
      bridge.close()
    }
  })
})

describe('KyberBridge: getMeta().rates names both rate tables (issue #186)', () => {
  const bridge = new KyberBridge({
    canonPath: '/path/does/not/exist/canon.db',
    ratesPath: '/path/does/not/exist/rates.json',
  })
  afterAll(() => bridge.close())

  type RateTable = {
    id: string
    source: string
    retrieved: string
    applies_to: string[]
    credit_usd?: number
  }
  const tables = (): RateTable[] =>
    (bridge.getMeta().rates as unknown as { tables: RateTable[] }).tables

  it('keeps the legacy flat rates fields (additive extension)', () => {
    const { rates } = bridge.getMeta()
    expect(rates.credit_usd).toBe(0.01)
    expect(rates.source).toBe(
      'https://docs.github.com/copilot/reference/copilot-billing/models-and-pricing',
    )
  })

  it('lists exactly the published and copilot_credits tables', () => {
    expect(Array.isArray(tables())).toBe(true)
    expect(tables().map((t) => t.id).sort()).toEqual(['copilot_credits', 'published'])
  })

  it('names the published-rate table (LiteLLM snapshot) for Claude Code and Codex', () => {
    const t = tables().find((x) => x.id === 'published')
    expect(t).toBeDefined()
    expect(t!.source.toLowerCase()).toContain('litellm')
    expect(t!.retrieved).toBe('2026-09-30')
    expect([...t!.applies_to].sort()).toEqual(['claude-code', 'codex'])
  })

  it('names the Copilot credits table with source, retrieved date, credit value, and Copilot-only applicability', () => {
    const t = tables().find((x) => x.id === 'copilot_credits')
    expect(t).toBeDefined()
    expect(t!.source).toBe(
      'https://docs.github.com/copilot/reference/copilot-billing/models-and-pricing',
    )
    expect(t!.retrieved).toBe('2026-09-30')
    expect(t!.credit_usd).toBe(0.01)
    expect(t!.applies_to).toEqual(['copilot'])
  })
})

describe('KyberBridge: unknown-window session owner semantics (thread-4)', () => {
  // `sessionHarnessRaw` is private to bridge.ts (absent row -> undefined,
  // failed read -> null), so the absent-vs-failed distinction is pinned
  // through the observable `countUnknownWindowSessions` envelope below, on
  // both the store and the raw-DB paths.
  const payloadWithSource = (source: string) => ({ context: { contextLimitSource: source } })

  const seedRawSession = (
    db: import('node:sqlite').DatabaseSync,
    sessionId: string,
    harness: string,
    source: string,
  ): void => {
    db.exec(`
      CREATE TABLE IF NOT EXISTS session (
        session_id TEXT PRIMARY KEY,
        harness TEXT NOT NULL,
        payload TEXT NOT NULL
      );
    `)
    db.prepare('INSERT OR REPLACE INTO session (session_id, harness, payload) VALUES (?, ?, ?)').run(
      sessionId,
      harness,
      JSON.stringify(payloadWithSource(source)),
    )
  }

  it('reports 0 for an absent session row under a harness scope (store path)', () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-absent' })).toBe(0)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('reports 0 for an absent session row under a harness scope (raw-DB path)', () => {
    const canonDb = new DatabaseSync(':memory:')
    const bridge = new KyberBridge({ canonDb })
    try {
      canonDb.exec(
        'CREATE TABLE session (session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, payload TEXT NOT NULL)',
      )
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-absent' })).toBe(0)
    } finally {
      bridge.close()
      try {
        canonDb.close()
      } catch {
        // The bridge already closed the injected handle.
      }
    }
  })

  it('reports 0 on a normalized-harness mismatch even when the payload window is unknown (store path)', () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      store.upsertSession({
        sessionId: 'sess-other-owner',
        harness: 'copilot',
        payload: payloadWithSource('default'),
      })
      expect(bridge.countUnknownWindowSessions({ harness: 'codex', sessionId: 'sess-other-owner' })).toBe(0)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('reports 0 on a normalized-harness mismatch even when the payload window is unknown (raw-DB path)', () => {
    const canonDb = new DatabaseSync(':memory:')
    const bridge = new KyberBridge({ canonDb })
    try {
      seedRawSession(canonDb, 'sess-other-owner', 'copilot', 'default')
      expect(bridge.countUnknownWindowSessions({ harness: 'codex', sessionId: 'sess-other-owner' })).toBe(0)
    } finally {
      bridge.close()
      try {
        canonDb.close()
      } catch {
        // The bridge already closed the injected handle.
      }
    }
  })

  it('falls through to the payload read when the harness owns the session (store path)', () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      store.upsertSession({ sessionId: 'sess-unknown', harness: 'copilot', payload: payloadWithSource('default') })
      store.upsertSession({ sessionId: 'sess-known', harness: 'copilot', payload: payloadWithSource('reported') })
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-unknown' })).toBe(1)
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-known' })).toBe(0)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('falls through to the payload read when the harness owns the session (raw-DB path)', () => {
    const canonDb = new DatabaseSync(':memory:')
    const bridge = new KyberBridge({ canonDb })
    try {
      seedRawSession(canonDb, 'sess-unknown', 'copilot', 'default')
      seedRawSession(canonDb, 'sess-known', 'copilot', 'reported')
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-unknown' })).toBe(1)
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-known' })).toBe(0)
    } finally {
      bridge.close()
      try {
        canonDb.close()
      } catch {
        // The bridge already closed the injected handle.
      }
    }
  })

  it('falls back to the payload read when the store harness lookup throws', () => {
    const bridgeFor = (source: string): KyberBridge => {
      const throwingStore = {
        sessionHarness: () => {
          throw new Error('harness column unreadable')
        },
        getSessionPayload: () => payloadWithSource(source),
      } as unknown as CanonStore
      return new KyberBridge({ canonPath: ':memory:', store: throwingStore })
    }
    const unknownBridge = bridgeFor('default')
    try {
      expect(unknownBridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-1' })).toBe(1)
    } finally {
      unknownBridge.close()
    }
    const knownBridge = bridgeFor('reported')
    try {
      expect(knownBridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-1' })).toBe(0)
    } finally {
      knownBridge.close()
    }
  })

  it('falls back to the payload read when the raw-DB harness lookup throws', () => {
    const bridgeFor = (source: string): KyberBridge => {
      const fakeDb = {
        exec: () => {},
        close: () => {},
        prepare: (sql: string) => {
          if (sql.includes('sqlite_master')) return { get: () => ({ '1': 1 }) }
          if (sql.includes('SELECT harness')) throw new Error('harness column unreadable')
          if (sql.includes('SELECT payload')) {
            return { get: () => ({ payload: JSON.stringify(payloadWithSource(source)) }) }
          }
          throw new Error(`unexpected query: ${sql}`)
        },
      } as unknown as import('node:sqlite').DatabaseSync
      return new KyberBridge({ canonDb: fakeDb })
    }
    const unknownBridge = bridgeFor('default')
    try {
      expect(unknownBridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-1' })).toBe(1)
    } finally {
      unknownBridge.close()
    }
    const knownBridge = bridgeFor('reported')
    try {
      expect(knownBridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-1' })).toBe(0)
    } finally {
      knownBridge.close()
    }
  })
})

describe('KyberBridge.compareRuns split-share identity (issue #190 / D1)', () => {
  // Executions store claimed ids (`cursor:<rawKey>`) when a session key is
  // split across harnesses. Compare must resolve via shareOf → recordsForShare
  // the way findings/runs already do; recordsForSession(claimedId) is empty
  // because records stay under the raw key.
  function turnRecord(
    spanId: string,
    harness: string,
    sessionId: string,
    timestamp: string,
  ): CanonicalRecord {
    return {
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      sessionId,
      source: 'synthetic',
      harness,
      name: 'canonical compare turn',
      op: 'llm.invoke',
      kind: 'client',
      timestamp,
      durationMs: 100,
      status: 'ok',
      tokens: {
        freshInput: 800,
        cacheRead: 200,
        cacheCreation: 0,
        output: 100,
        reportedInput: 1000,
        reportedOutput: 100,
      },
      content: { system_prompt: 'hello' },
      parts: [{ part: 'system_prompt', text: 'sys', tokens: 600 }],
      cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
      raw: { model: 'gpt-4o', cwd: '/repo' },
    }
  }

  it('compares split-share runs that have records under the raw key (and bare-key runs still compare)', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turnRecord('split-cur', 'cursor', 'k-split', '2026-09-03T10:00:00.000Z'),
      turnRecord('split-vs', 'copilot-chat', 'k-split', '2026-09-03T10:01:00.000Z'),
      turnRecord('bare-a', 'cursor', 'solo-a', '2026-09-03T11:00:00.000Z'),
      turnRecord('bare-b', 'copilot', 'solo-b', '2026-09-03T11:01:00.000Z'),
    ])
    await buildSessions(store)
    await buildRuns(store)

    const cursorSplitRun = store.listRuns('cursor').find((run) =>
      store.listExecutions(run.runId).some((execution) => execution.sessionId === 'cursor:k-split'),
    )
    const vscodeSplitRun = store.listRuns('copilot-vscode').find((run) =>
      store
        .listExecutions(run.runId)
        .some((execution) => execution.sessionId === 'copilot-vscode:k-split'),
    )
    const bareCursorRun = store.listRuns('cursor').find((run) =>
      store.listExecutions(run.runId).some((execution) => execution.sessionId === 'solo-a'),
    )
    const bareCopilotRun = store.listRuns('copilot').find((run) =>
      store.listExecutions(run.runId).some((execution) => execution.sessionId === 'solo-b'),
    )

    expect(cursorSplitRun).toBeDefined()
    expect(vscodeSplitRun).toBeDefined()
    expect(bareCursorRun).toBeDefined()
    expect(bareCopilotRun).toBeDefined()
    // Precondition: claimed ids, empty under recordsForSession, populated via share.
    const identities = store.sessionIdentities()
    for (const sessionId of ['cursor:k-split', 'copilot-vscode:k-split'] as const) {
      const share = identities.shareOf(sessionId)
      expect(share).toEqual({ key: 'k-split', harness: sessionId.split(':')[0] })
      expect(store.recordsForSession(sessionId)).toHaveLength(0)
      expect(store.recordsForShare(share!.key, share!.harness).length).toBeGreaterThan(0)
    }

    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      const bare = bridge.compareRuns(bareCursorRun!.runId, bareCopilotRun!.runId)
      expect(bare).not.toBeNull()
      expect(bare!.runA.turnCount).toBeGreaterThan(0)
      expect(bare!.runB.turnCount).toBeGreaterThan(0)
      expect(bare!.pairs.length).toBeGreaterThan(0)

      const split = bridge.compareRuns(cursorSplitRun!.runId, vscodeSplitRun!.runId)
      expect(split).not.toBeNull()
      expect(split!.runA.turnCount).toBeGreaterThan(0)
      expect(split!.runB.turnCount).toBeGreaterThan(0)
      expect(split!.pairs.length).toBeGreaterThan(0)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('resolves split-share the same way on the direct-DB path (no injected store)', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turnRecord('split-cur', 'cursor', 'k-split', '2026-09-03T10:00:00.000Z'),
      turnRecord('split-vs', 'copilot-chat', 'k-split', '2026-09-03T10:01:00.000Z'),
    ])
    await buildSessions(store)
    await buildRuns(store)

    const cursorSplitRun = store.listRuns('cursor').find((run) =>
      store.listExecutions(run.runId).some((execution) => execution.sessionId === 'cursor:k-split'),
    )
    const vscodeSplitRun = store.listRuns('copilot-vscode').find((run) =>
      store
        .listExecutions(run.runId)
        .some((execution) => execution.sessionId === 'copilot-vscode:k-split'),
    )
    expect(cursorSplitRun).toBeDefined()
    expect(vscodeSplitRun).toBeDefined()

    // Direct-DB: same sqlite handle, no CanonStore — must rebuild SessionIdentities.
    const bridge = new KyberBridge({ canonDb: store.getDatabase() })
    try {
      const split = bridge.compareRuns(cursorSplitRun!.runId, vscodeSplitRun!.runId)
      expect(split).not.toBeNull()
      expect(split!.runA.turnCount).toBeGreaterThan(0)
      expect(split!.runB.turnCount).toBeGreaterThan(0)
      expect(split!.pairs.length).toBeGreaterThan(0)
      expect(bridge.compareRuns('missing-a', cursorSplitRun!.runId)).toBeNull()
    } finally {
      // Direct-DB: bridge was given the store's handle without the store, so
      // bridge.close() would close it. Close the owning store first.
      store.close()
      bridge.close()
    }
  })

  it('rebuilds SessionIdentities once when compareRuns loads two executed sides', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turnRecord('split-cur', 'cursor', 'k-split', '2026-09-03T10:00:00.000Z'),
      turnRecord('split-vs', 'copilot-chat', 'k-split', '2026-09-03T10:01:00.000Z'),
    ])
    await buildSessions(store)
    await buildRuns(store)

    const cursorSplitRun = store.listRuns('cursor').find((run) =>
      store.listExecutions(run.runId).some((execution) => execution.sessionId === 'cursor:k-split'),
    )
    const vscodeSplitRun = store.listRuns('copilot-vscode').find((run) =>
      store
        .listExecutions(run.runId)
        .some((execution) => execution.sessionId === 'copilot-vscode:k-split'),
    )
    expect(cursorSplitRun).toBeDefined()
    expect(vscodeSplitRun).toBeDefined()

    const db = store.getDatabase()
    const originalPrepare = db.prepare.bind(db)
    let identityScans = 0
    db.prepare = ((sql: string) => {
      if (sql.includes('SELECT DISTINCT COALESCE(session_id, trace_id)')) identityScans += 1
      return originalPrepare(sql)
    }) as typeof db.prepare

    const bridge = new KyberBridge({ canonDb: db })
    try {
      const split = bridge.compareRuns(cursorSplitRun!.runId, vscodeSplitRun!.runId)
      expect(split).not.toBeNull()
      expect(split!.runA.turnCount).toBeGreaterThan(0)
      expect(split!.runB.turnCount).toBeGreaterThan(0)
      expect(identityScans).toBe(1)
    } finally {
      // Direct-DB: bridge was given the store's handle without the store, so
      // bridge.close() would close it. Close the owning store first.
      store.close()
      bridge.close()
    }
  })

  it('twin-dedupes compare turnCount per execution like findings (not raw undeduped rows)', async () => {
    // Live twin shape (issue #182 / ADR 0009 D4): one session key under
    // claude-code (OTLP) and claude-desktop (file) with byte-identical
    // counters within the skew window. Two such pairs five minutes apart
    // are two genuine turns — four raw rows, two after dedupeTwinTurns.
    // Dropping the per-execution dedupe in recordsForRun would make
    // turnCount 4 and this assertion fail.
    const twinKey = 'twin-cmp'
    const sameTokens = {
      freshInput: 2,
      cacheRead: 39096,
      cacheCreation: 24977,
      output: 563,
      reportedInput: 64075,
      reportedOutput: 563,
    }
    const otel = (spanId: string, timestamp: string): CanonicalRecord => ({
      ...turnRecord(spanId, 'claude-code', twinKey, timestamp),
      source: 'claude-code-desktop',
      tokens: { ...sameTokens },
    })
    const file = (spanId: string, timestamp: string): CanonicalRecord => ({
      ...turnRecord(spanId, 'claude-desktop', twinKey, timestamp),
      source: 'codeburn/claude-desktop',
      tokens: { ...sameTokens },
    })

    const store = new CanonStore(':memory:')
    store.upsertMany([
      otel('otel-1', '2026-09-23T22:43:53.540Z'),
      file('synth:aaa', '2026-09-23T22:43:58.783Z'),
      otel('otel-2', '2026-09-23T22:48:53.540Z'),
      file('synth:bbb', '2026-09-23T22:48:58.783Z'),
      turnRecord('bare-b', 'copilot', 'solo-b', '2026-09-23T23:00:00.000Z'),
    ])
    await buildSessions(store)
    await buildRuns(store)

    // Precondition: raw provenance keeps all four twin rows under the folded share.
    expect(store.recordsForShare(twinKey, 'claude-code')).toHaveLength(4)

    const twinRun = store.listRuns('claude-code').find((run) =>
      store.listExecutions(run.runId).some((execution) => execution.sessionId === twinKey),
    )
    const bareRun = store.listRuns('copilot').find((run) =>
      store.listExecutions(run.runId).some((execution) => execution.sessionId === 'solo-b'),
    )
    expect(twinRun).toBeDefined()
    expect(bareRun).toBeDefined()

    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      const cmp = bridge.compareRuns(twinRun!.runId, bareRun!.runId)
      expect(cmp).not.toBeNull()
      // Deduped cardinality: 2 twin pairs → 2 turns (not 4 raw rows).
      expect(cmp!.runA.turnCount).toBe(2)
      expect(cmp!.runB.turnCount).toBe(1)
      expect(cmp!.pairs.length).toBeGreaterThan(0)
    } finally {
      bridge.close()
      store.close()
    }
  })
})

describe('KyberBridge: transport index lookup (single-pass identity map)', () => {
  type Row = { span: string; index?: number; turn?: number }

  const identityOf = (r: Row): number | undefined => {
    if (typeof r.index === 'number' && Number.isFinite(r.index)) return r.index
    if (typeof r.turn === 'number' && Number.isFinite(r.turn)) return r.turn - 1
    return undefined
  }

  it('prefers explicit identity over array position', () => {
    // Payload order is [{turn: 2}, {turn: 1}]: transport 0 is the second row.
    const rows: Row[] = [
      { span: 'span-pa', turn: 2 },
      { span: 'span-pb', turn: 1 },
    ]
    const byIdentity = buildTransportIndex(rows, identityOf)
    expect(lookupTransport(rows, byIdentity, identityOf, 0)?.span).toBe('span-pb')
    expect(lookupTransport(rows, byIdentity, identityOf, 1)?.span).toBe('span-pa')
  })

  it('falls back to array position only for identity-free rows', () => {
    const rows: Row[] = [{ span: 'span-q0' }, { span: 'span-q1' }]
    const byIdentity = buildTransportIndex(rows, identityOf)
    expect(byIdentity.size).toBe(0)
    expect(lookupTransport(rows, byIdentity, identityOf, 0)?.span).toBe('span-q0')
    expect(lookupTransport(rows, byIdentity, identityOf, 1)?.span).toBe('span-q1')
    expect(lookupTransport(rows, byIdentity, identityOf, 2)).toBeUndefined()
  })

  it('never resolves a neighbor when identities disagree with position', () => {
    const rows: Row[] = [
      { span: 'span-x0', index: 0 },
      { span: 'span-x1', index: 1 },
    ]
    const byIdentity = buildTransportIndex(rows, identityOf)
    // Transport 2 matches no identity and row 2 does not exist.
    expect(lookupTransport(rows, byIdentity, identityOf, 2)).toBeUndefined()
    expect(lookupTransport(rows, byIdentity, identityOf, -1)).toBeUndefined()
  })

  it('keeps the first row on duplicate identities, matching find order', () => {
    const rows: Row[] = [
      { span: 'span-first', index: 0 },
      { span: 'span-second', index: 0 },
    ]
    const byIdentity = buildTransportIndex(rows, identityOf)
    expect(lookupTransport(rows, byIdentity, identityOf, 0)?.span).toBe('span-first')
  })
})
