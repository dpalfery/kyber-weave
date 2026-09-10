import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { createRequire } from 'node:module'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import {
  KyberBridge,
  _clip,
} from '../kyber/server/bridge.js'
import { CanonStore } from '../kyber/canon/store.js'
import type { CanonicalRecord } from '../kyber/canon/types.js'

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
    expect(clipped.items[2].nested).toContain('... [truncated, 2010 chars]')
    expect(clipped.items[2].count).toBe(42)
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
        payload TEXT
      );
    `)
    legacyDb
      .prepare('INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
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
        payload TEXT
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
        payload TEXT
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
        cost: { usd: 0.05, basis: 'published_rates' },
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
        cost: { usd: 0.02, basis: 'published_rates' },
        models: ['gpt-4o'],
      },
    })

    canonDb
      .prepare(
        'INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'
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
        'INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'
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
        cost: { usd: 0.02, basis: 'published_rates' },
        models: ['gpt-4o'],
      },
    })

    sessionsDb
      .prepare(
        'INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'
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
        'INSERT INTO session VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)'
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

  it('listSessions combines canonical derived sessions and records', () => {
    const sessions = bridge.listSessions()
    expect(sessions.length).toBe(3)

    const ids = sessions.map((s) => s.session_id)
    expect(ids).toContain('sess-canon-new')
    expect(ids).toContain('sess-shared')
    expect(ids).toContain('trace-records-1')
    expect(ids).not.toContain('sess-sessions-older')

    // Verify deduplication: canonDb session took priority
    const shared = sessions.find((s) => s.session_id === 'sess-shared')!
    expect(shared.agent_name).toBe('canon-agent-wins')
    expect(shared.label).toBe('Shared Session (Canon)')
  })

  it('listSessions sorts sessions strictly by started DESC before applying limit', () => {
    const sessions = bridge.listSessions()
    expect(sessions[0].session_id).toBe('sess-canon-new') // 12:00:00Z
    expect(sessions[1].session_id).toBe('sess-shared') // 11:00:00Z
    expect(sessions[2].session_id).toBe('trace-records-1') // 10:30:00Z

    // Verify limit slices after global started DESC sort
    const top2 = bridge.listSessions(2)
    expect(top2.length).toBe(2)
    expect(top2[0].session_id).toBe('sess-canon-new')
    expect(top2[1].session_id).toBe('sess-shared')

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
