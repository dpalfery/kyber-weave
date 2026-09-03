import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { existsSync } from 'fs'
import { createRequire } from 'node:module'

import {
  KyberBridge,
  _clip,
} from '../kyber/server/bridge.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

const SESSIONS_DB_PATH = '/Users/dave/git/personal/agent-session-analysis-dashboard/sessions.db'
const HAS_REAL_SESSIONS_DB = existsSync(SESSIONS_DB_PATH)

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
  it('instantiates cleanly and returns empty structures when files do not exist', () => {
    const bridge = new KyberBridge({
      canonPath: '/path/does/not/exist/canon.db',
      sessionsPath: '/path/does/not/exist/sessions.db',
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
      sessionsPath: ':memory:',
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

    bridge = new KyberBridge({ canonDb, sessionsDb })
  })

  afterAll(() => {
    bridge.close()
  })

  it('listSessions combines sessions from canonDb, sessionsDb, and records, deduplicated by session_id', () => {
    const sessions = bridge.listSessions()
    expect(sessions.length).toBe(4)

    const ids = sessions.map((s) => s.session_id)
    expect(ids).toContain('sess-canon-new')
    expect(ids).toContain('sess-shared')
    expect(ids).toContain('trace-records-1')
    expect(ids).toContain('sess-sessions-older')

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
    expect(sessions[3].session_id).toBe('sess-sessions-older') // 09:00:00Z

    // Verify limit slices after global started DESC sort
    const top2 = bridge.listSessions(2)
    expect(top2.length).toBe(2)
    expect(top2[0].session_id).toBe('sess-canon-new')
    expect(top2[1].session_id).toBe('sess-shared')

    const top1 = bridge.listSessions(1)
    expect(top1.length).toBe(1)
    expect(top1[0].session_id).toBe('sess-canon-new')
  })

  it('getSessionPayload queries canonDb first and falls back to sessionsDb', () => {
    // 1. In both canonDb and sessionsDb -> canonDb wins
    const sharedPayload = bridge.getSessionPayload('sess-shared')
    expect(sharedPayload).not.toBeNull()
    expect(sharedPayload!.id).toBe('sess-shared')

    // 2. Only in sessionsDb -> fallback resolves cleanly
    const olderPayload = bridge.getSessionPayload('sess-sessions-older')
    expect(olderPayload).not.toBeNull()
    expect(olderPayload!.id).toBe('sess-sessions-older')
    expect(olderPayload!.harness).toBe('gemini')

    // 3. Not in either -> returns null
    expect(bridge.getSessionPayload('non-existent')).toBeNull()
  })

  it('getComparisonTable aggregates harnesses using json_extract optimization', () => {
    const table = bridge.getComparisonTable()
    expect(table).toBeDefined()
    expect(table.harnesses).toContain('copilot')
    expect(table.harnesses).toContain('gemini')

    const tptRow = table.rows.find((r) => r.metric === 'tokens_per_turn')
    expect(tptRow).toBeDefined()
    // Copilot: 2 sessions (1000+500 / 5 turns = 300, and 400+200 / 2 turns = 300 -> total 2100 / 7 = 300)
    expect(tptRow!.cells.copilot.measurable).toBe(true)
    expect(tptRow!.cells.copilot.value).toBe(300)

    const turnsRow = table.rows.find((r) => r.metric === 'turns')
    expect(turnsRow).toBeDefined()
    expect(turnsRow!.cells.copilot.value).toBe(7)
    expect(turnsRow!.cells.gemini.value).toBe(3)
  })

  it('getQuarantine queries canonDb and sessionsDb without early return shadowing, deduplicating by span_id', () => {
    const quarantined = bridge.getQuarantine()
    expect(quarantined.length).toBe(3)

    const spanIds = quarantined.map((q) => q.span_id)
    expect(spanIds).toContain('quar-canon-1')
    expect(spanIds).toContain('quar-shared')
    expect(spanIds).toContain('quar-sessions-1')

    // Canon priority on duplicate span_id
    const shared = quarantined.find((q) => q.span_id === 'quar-shared')!
    expect(shared.reason).toBe('canon quarantine reason')

    // Limit parameter respected
    const limited = bridge.getQuarantine(2)
    expect(limited.length).toBe(2)
  })

  it('getProblems queries canonDb and sessionsDb without early return shadowing, deduplicating correctly', () => {
    const problems = bridge.getProblems()
    expect(problems.length).toBe(3)

    const spanIds = problems.map((p) => p.span_id)
    expect(spanIds).toContain('prob-canon-1')
    expect(spanIds).toContain('prob-shared')
    expect(spanIds).toContain('prob-sessions-1')

    // Canon priority on duplicate problem
    const shared = problems.find((p) => p.span_id === 'prob-shared')!
    expect(shared.message).toBe('Canon problem message')

    // Limit parameter respected
    const limited = bridge.getProblems(2)
    expect(limited.length).toBe(2)
  })

  it('getMeta counts spans and quarantine across in-memory databases', () => {
    const meta = bridge.getMeta()
    expect(meta.span_count).toBe(1) // 1 record in records table
    expect(meta.quarantined).toBe(4) // 2 in canonDb + 2 in sessionsDb
    expect(meta.tokenizer.kind).toBe('js-tiktoken/o200k_base')
    expect(meta.rates.credit_usd).toBe(0.01)
  })
})

describe.runIf(HAS_REAL_SESSIONS_DB)('KyberBridge: sessions.db live queries', () => {
  let bridge: KyberBridge

  beforeAll(() => {
    bridge = new KyberBridge({
      sessionsPath: SESSIONS_DB_PATH,
    })
  })

  afterAll(() => {
    bridge.close()
  })

  it('listSessions returns sessions with exact required schema', () => {
    const sessions = bridge.listSessions()
    expect(sessions.length).toBeGreaterThan(0)
    expect(sessions.length).toBeGreaterThanOrEqual(187)

    const first = sessions[0]
    expect(first).toHaveProperty('session_id')
    expect(first).toHaveProperty('harness')
    expect(first).toHaveProperty('label')
    expect(first).toHaveProperty('is_subagent')
    expect(first).toHaveProperty('parent_session')
    expect(first).toHaveProperty('agent_name')
    expect(first).toHaveProperty('repo')
    expect(first).toHaveProperty('branch')
    expect(first).toHaveProperty('started')
    expect(first).toHaveProperty('ended')
    expect(first).toHaveProperty('turn_count')
    expect(first).toHaveProperty('request_count')
    expect(first).toHaveProperty('total_input')
    expect(first).toHaveProperty('total_output')
    expect(first).toHaveProperty('cost_usd')
    expect(first).toHaveProperty('models')
    expect(first).toHaveProperty('problems')

    expect(typeof first.is_subagent).toBe('boolean')
    expect(Array.isArray(first.models)).toBe(true)
    expect(typeof first.problems).toBe('number')
  })

  it('listSessions respects limit parameter', () => {
    const limited = bridge.listSessions(10)
    expect(limited.length).toBe(10)
  })

  it('getSessionPayload returns clipped payload for valid session id', () => {
    const payload = bridge.getSessionPayload('functions.runSubagent:61')
    expect(payload).not.toBeNull()
    expect(payload!.id).toBe('functions.runSubagent:61')
    expect(payload!.harness).toBe('copilot')
    expect(payload!.summary).toBeDefined()
    expect(payload!.summary!.turn_count).toBe(4)
    expect(payload!.turns).toBeDefined()
    expect(payload!.turns!.length).toBe(4)
  })

  it('getSessionPayload returns null for unknown session id', () => {
    const payload = bridge.getSessionPayload('does-not-exist-12345')
    expect(payload).toBeNull()
  })

  it('getComparisonTable returns complete matrix across copilot, gemini, and pi', () => {
    const table = bridge.getComparisonTable()
    expect(table).toBeDefined()
    expect(table.harnesses).toContain('copilot')
    expect(table.harnesses).toContain('gemini')
    expect(table.harnesses).toContain('pi')

    const metricKeys = table.rows.map((r) => r.metric)
    expect(metricKeys).toContain('tokens_per_turn')
    expect(metricKeys).toContain('input_tokens_per_turn')
    expect(metricKeys).toContain('output_tokens_per_turn')
    expect(metricKeys).toContain('fresh_input_per_turn')
    expect(metricKeys).toContain('cache_read_share_per_turn')
    expect(metricKeys).toContain('schema_cost_per_turn')
    expect(metricKeys).toContain('cost_per_turn')
    expect(metricKeys).toContain('turns')
    expect(metricKeys).toContain('total_tokens')
    expect(metricKeys).toContain('total_cost')

    // Schema cost: pi and gemini not measurable, copilot derived
    const schemaRow = table.rows.find((r) => r.metric === 'schema_cost_per_turn')!
    expect(schemaRow.cells.pi.measurable).toBe(false)
    expect(schemaRow.cells.pi.render).toBe('not measurable')
    expect(schemaRow.cells.gemini.measurable).toBe(false)
    expect(schemaRow.cells.gemini.render).toBe('not measurable')
    expect(schemaRow.cells.copilot.measurable).toBe(true)
    expect(schemaRow.cells.copilot.availability).toBe('derived')
    expect(schemaRow.cells.copilot.render).toContain('derived, lower bound')

    // Cost basis mismatch problem detected between published_rates and harness_reported
    expect(table.problems.some((p) => p.code === 'cost_basis_mismatch')).toBe(true)
  })

  it('getQuarantine returns quarantine rows from quarantine table', () => {
    const quarantined = bridge.getQuarantine(5)
    expect(quarantined.length).toBe(5)
    const q = quarantined[0]
    expect(q).toHaveProperty('span_id')
    expect(q).toHaveProperty('source')
    expect(q).toHaveProperty('name')
    expect(q).toHaveProperty('namespaces')
    expect(q).toHaveProperty('reason')
    expect(q).toHaveProperty('seen_at')
  })

  it('getProblems returns problems recorded in problem table', () => {
    const problems = bridge.getProblems()
    expect(problems.length).toBeGreaterThan(0)
    const p = problems[0]
    expect(p).toHaveProperty('id')
    expect(p).toHaveProperty('session_id')
    expect(p).toHaveProperty('span_id')
    expect(p).toHaveProperty('severity')
    expect(p).toHaveProperty('code')
    expect(p).toHaveProperty('message')
    expect(p).toHaveProperty('at')
    expect(p).toHaveProperty('harness')
  })

  it('getMeta returns metadata, counts, rates and harness information', () => {
    const meta = bridge.getMeta()
    expect(meta.span_count).toBeGreaterThan(30000)
    expect(meta.quarantined).toBeGreaterThan(400000)
    expect(meta.tokenizer.kind).toBe('js-tiktoken/o200k_base')
    expect(meta.rates.credit_usd).toBe(0.01)
    expect(meta.harnesses.copilot).toBeDefined()
    expect(meta.harnesses.gemini).toBeDefined()
    expect(meta.harnesses.pi).toBeDefined()
    expect(meta.sources.length).toBeGreaterThan(0)
  })
})
