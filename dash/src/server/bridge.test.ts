import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'

import { CanonStore } from '../canon/store.js'
import {
  KyberBridge,
  SHARE_DROP_WARN_LIMIT,
  type ProblemRow,
  type QuarantineRow,
} from './bridge.js'

type QuarantineRowWithTimestamp = QuarantineRow & { timestamp?: string | null }

interface PagedBridge {
  getQuarantine(limit?: number, offset?: number): QuarantineRowWithTimestamp[]
  getQuarantineCount(): number
  getProblems(limit?: number, offset?: number): ProblemRow[]
  getProblemCount(): number
  close(): void
}

describe('bridge quarantine problems: pagination and metadata contract (Task T3)', () => {
  let tempDir: string
  let dbPath: string
  let store: CanonStore
  let db: DatabaseSync
  let bridge: KyberBridge
  let pagedBridge: PagedBridge

  beforeEach(() => {
    tempDir = mkdtempSync(join(tmpdir(), 'kyber-bridge-t3-'))
    dbPath = join(tempDir, 'canon.db')
    store = new CanonStore(dbPath)
    db = new DatabaseSync(dbPath)
    bridge = new KyberBridge({ canonDb: db })
    pagedBridge = bridge as unknown as PagedBridge
  })

  afterEach(() => {
    bridge.close()
    store.close()
    rmSync(tempDir, { recursive: true, force: true })
  })

  describe('pagination via limit and offset', () => {
    it('supports pagination via limit and offset for getQuarantine', () => {
      // Seed 25 quarantine entries with ordered timestamps
      for (let i = 0; i < 25; i++) {
        const id = String(i).padStart(2, '0')
        store.quarantine(
          `span-quar-${id}`,
          ['copilot'],
          'unmapped namespace',
          {
            source: `copilot:source-${id}`,
            name: `turn-${id}`,
            timestamp: `2026-09-30T10:${id}:00.000Z`,
          },
        )
      }

      const all = pagedBridge.getQuarantine(100, 0)
      expect(all).toHaveLength(25)

      // Query page 1 (items 0 to 9)
      const page1 = pagedBridge.getQuarantine(10, 0)
      expect(page1).toHaveLength(10)
      expect(page1.map((r) => r.span_id)).toEqual(all.slice(0, 10).map((r) => r.span_id))

      // Query page 2 (items 10 to 19)
      const page2 = pagedBridge.getQuarantine(10, 10)
      expect(page2).toHaveLength(10)
      expect(page2.map((r) => r.span_id)).toEqual(all.slice(10, 20).map((r) => r.span_id))
      expect(page2[0].span_id).toBe(all[10].span_id)

      // Query page 3 (items 20 to 24)
      const page3 = pagedBridge.getQuarantine(10, 20)
      expect(page3).toHaveLength(5)
      expect(page3.map((r) => r.span_id)).toEqual(all.slice(20, 25).map((r) => r.span_id))

      // Query beyond total count returns empty
      const pagePastEnd = pagedBridge.getQuarantine(10, 50)
      expect(pagePastEnd).toHaveLength(0)
    })

    it('supports pagination via limit and offset for getProblems', () => {
      // Seed 25 problems
      for (let i = 0; i < 25; i++) {
        const id = String(i).padStart(2, '0')
        store.recordProblem({
          spanId: `span-prob-${id}`,
          sessionId: 'session-pagination-test',
          severity: 'warning',
          code: `TOKEN_LIMIT_${id}`,
          message: `Problem message ${id}`,
          location: `loc-${id}`,
          harness: 'copilot',
          timestamp: `2026-09-30T10:${id}:00.000Z`,
        })
      }

      const all = pagedBridge.getProblems(100, 0)
      expect(all).toHaveLength(25)

      // Query page 1 (items 0 to 9)
      const page1 = pagedBridge.getProblems(10, 0)
      expect(page1).toHaveLength(10)
      expect(page1.map((r) => r.span_id)).toEqual(all.slice(0, 10).map((r) => r.span_id))

      // Query page 2 (items 10 to 19)
      const page2 = pagedBridge.getProblems(10, 10)
      expect(page2).toHaveLength(10)
      expect(page2.map((r) => r.span_id)).toEqual(all.slice(10, 20).map((r) => r.span_id))
      expect(page2[0].span_id).toBe(all[10].span_id)

      // Query page 3 (items 20 to 24)
      const page3 = pagedBridge.getProblems(10, 20)
      expect(page3).toHaveLength(5)
      expect(page3.map((r) => r.span_id)).toEqual(all.slice(20, 25).map((r) => r.span_id))

      // Query beyond total count returns empty
      const pagePastEnd = pagedBridge.getProblems(10, 50)
      expect(pagePastEnd).toHaveLength(0)
    })
  })

  describe('quarantine and problems column mapping and metadata', () => {
    it('returns quarantine rows with source, name, and timestamp populated from database', () => {
      store.quarantine(
        'span-quar-meta-test',
        ['copilot', 'gen_ai'],
        'unmapped namespace',
        {
          source: 'copilot:chat-worker',
          name: 'turn_completion',
          timestamp: '2026-09-30T15:30:00.000Z',
        },
      )

      const quarantined = pagedBridge.getQuarantine()
      const row = quarantined.find((r) => r.span_id === 'span-quar-meta-test')
      expect(row).toBeDefined()
      expect(row?.source).toBe('copilot:chat-worker')
      expect(row?.name).toBe('turn_completion')
      expect(row?.timestamp).toBe('2026-09-30T15:30:00.000Z')
    })

    it('returns problems with actual harness instead of repeating location / spanId', () => {
      const location = 'span-prob-harness-test'
      store.recordProblem({
        spanId: 'span-prob-harness-test',
        sessionId: 'session-problem-test',
        severity: 'error',
        code: 'TOKEN_PARSE_FAILURE',
        message: 'Failed parsing response tokens',
        location,
        harness: 'copilot',
        timestamp: '2026-09-30T15:30:00.000Z',
      })

      const problems = pagedBridge.getProblems()
      const prob = problems.find((p) => p.span_id === 'span-prob-harness-test')
      expect(prob).toBeDefined()
      expect(prob?.harness).toBe('copilot')
      expect(prob?.harness).not.toBe(location)
      expect(prob?.harness).not.toBe(prob?.span_id)
    })

    it('ensures bridge constructed with store shares database handle for rows and counts', () => {
      const storeBridge = new KyberBridge({ store }) as unknown as PagedBridge

      try {
        const quarCount = storeBridge.getQuarantineCount()
        const quarRows = storeBridge.getQuarantine(100, 0)
        expect(quarCount).toBe(quarRows.length)

        const probCount = storeBridge.getProblemCount()
        const probRows = storeBridge.getProblems(100, 0)
        expect(probCount).toBe(probRows.length)
      } finally {
        storeBridge.close()
      }
    })
  })
})

describe('bridge compareRuns: empty execution keys fall back to the run id (issue #190)', () => {
  let tempDir: string
  let dbPath: string
  let store: CanonStore
  let db: DatabaseSync
  let bridge: KyberBridge

  beforeEach(() => {
    tempDir = mkdtempSync(join(tmpdir(), 'kyber-bridge-compare-'))
    dbPath = join(tempDir, 'canon.db')
    store = new CanonStore(dbPath)
    db = new DatabaseSync(dbPath)
    bridge = new KyberBridge({ canonDb: db })
  })

  afterEach(() => {
    // Spies die with the test whatever its verdict — a failed assertion must
    // not leave console.warn or db.prepare mocked for later tests.
    vi.restoreAllMocks()
    bridge.close()
    store.close()
    rmSync(tempDir, { recursive: true, force: true })
  })

  it('compares records keyed by the run id when every execution key is empty', () => {
    // Run A's only execution selects no session key: both its session id
    // and its execution id are empty. Run B's execution resolves normally.
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-empty-keys-a', 'cursor', 'derived')
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-empty-keys-b', 'cursor', 'derived')
    db.prepare(
      `INSERT INTO execution
       (execution_id, run_id, session_id, harness, is_root, parent_linkage_json)
       VALUES ('', ?, NULL, ?, 1, ?)`,
    ).run('run-empty-keys-a', 'cursor', JSON.stringify('measured'))
    db.prepare(
      `INSERT INTO execution
       (execution_id, run_id, session_id, harness, is_root, parent_linkage_json)
       VALUES (?, ?, ?, ?, 1, ?)`,
    ).run('exec-b', 'run-empty-keys-b', 'run-empty-keys-b', 'cursor', JSON.stringify('measured'))
    for (const runId of ['run-empty-keys-a', 'run-empty-keys-b']) {
      db.prepare(
        `INSERT INTO records
         (span_id, source, harness, session_id, name, op, kind, timestamp,
          duration_ms, status, tokens_json, content_json, cost_json)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      ).run(
        `span-${runId}`,
        'otel',
        'cursor',
        runId,
        'llm',
        'llm.invoke',
        'model',
        '2026-10-01T00:00:00.000Z',
        10,
        'success',
        JSON.stringify({
          freshInput: 100,
          cacheRead: 0,
          cacheCreation: 0,
          output: 10,
          reportedInput: 100,
          reportedOutput: 10,
        }),
        JSON.stringify({}),
        JSON.stringify({}),
      )
    }

    const summary = bridge.compareRuns('run-empty-keys-a', 'run-empty-keys-b')

    expect(summary).not.toBeNull()
    // The run id is the only session key Run A's execution offers; the
    // records stored under it must reach the comparison, not be omitted.
    expect(summary!.runA.turnCount).toBe(1)
    expect(summary!.runB.turnCount).toBe(1)
  })

  it('uses executionId when sessionId is an empty string', () => {
    // sessionId ?? executionId keeps "", so the filter drops that key and
    // the run falls back to runId — omitting records keyed by executionId.
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-blank-session-a', 'cursor', 'derived')
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-blank-session-b', 'cursor', 'derived')
    db.prepare(
      `INSERT INTO execution
       (execution_id, run_id, session_id, harness, is_root, parent_linkage_json)
       VALUES (?, ?, ?, ?, 1, ?)`,
    ).run(
      'exec-blank-session-a',
      'run-blank-session-a',
      '',
      'cursor',
      JSON.stringify('measured'),
    )
    db.prepare(
      `INSERT INTO execution
       (execution_id, run_id, session_id, harness, is_root, parent_linkage_json)
       VALUES (?, ?, ?, ?, 1, ?)`,
    ).run(
      'exec-blank-session-b',
      'run-blank-session-b',
      'exec-blank-session-b',
      'cursor',
      JSON.stringify('measured'),
    )
    for (const [runId, sessionId] of [
      ['run-blank-session-a', 'exec-blank-session-a'],
      ['run-blank-session-b', 'exec-blank-session-b'],
    ] as const) {
      db.prepare(
        `INSERT INTO records
         (span_id, source, harness, session_id, name, op, kind, timestamp,
          duration_ms, status, tokens_json, content_json, cost_json)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      ).run(
        `span-${runId}`,
        'otel',
        'cursor',
        sessionId,
        'llm',
        'llm.invoke',
        'model',
        '2026-10-01T00:00:00.000Z',
        10,
        'success',
        JSON.stringify({
          freshInput: 100,
          cacheRead: 0,
          cacheCreation: 0,
          output: 10,
          reportedInput: 100,
          reportedOutput: 10,
        }),
        JSON.stringify({}),
        JSON.stringify({}),
      )
    }

    const summary = bridge.compareRuns('run-blank-session-a', 'run-blank-session-b')

    expect(summary).not.toBeNull()
    expect(summary!.runA.turnCount).toBe(1)
    expect(summary!.runB.turnCount).toBe(1)
  })

  it('scopes twin-dedupe to the execution harness when share resolution misses', () => {
    // Bare session key with two harnesses sharing near-identical counters —
    // unscoped dedupeTwinTurns would collapse them; harness scoping keeps both
    // runs' own turns.
    const sessionKey = 'shared-bare-session'
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-cursor-share', 'cursor', 'derived')
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-copilot-share', 'copilot-cli', 'derived')
    db.prepare(
      `INSERT INTO execution
       (execution_id, run_id, session_id, harness, is_root, parent_linkage_json)
       VALUES (?, ?, ?, ?, 1, ?)`,
    ).run('exec-cursor-share', 'run-cursor-share', sessionKey, 'cursor', JSON.stringify('measured'))
    db.prepare(
      `INSERT INTO execution
       (execution_id, run_id, session_id, harness, is_root, parent_linkage_json)
       VALUES (?, ?, ?, ?, 1, ?)`,
    ).run(
      'exec-copilot-share',
      'run-copilot-share',
      sessionKey,
      'copilot-cli',
      JSON.stringify('measured'),
    )
    for (const [spanId, harness] of [
      ['span-cursor-share', 'cursor'],
      ['span-copilot-share', 'copilot-cli'],
    ] as const) {
      db.prepare(
        `INSERT INTO records
         (span_id, source, harness, session_id, name, op, kind, timestamp,
          duration_ms, status, tokens_json, content_json, cost_json)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      ).run(
        spanId,
        'otel',
        harness,
        sessionKey,
        'llm',
        'llm.invoke',
        'model',
        '2026-10-01T00:00:00.000Z',
        10,
        'success',
        JSON.stringify({
          freshInput: 100,
          cacheRead: 0,
          cacheCreation: 0,
          output: 10,
          reportedInput: 100,
          reportedOutput: 10,
        }),
        JSON.stringify({}),
        JSON.stringify({}),
      )
    }

    const summary = bridge.compareRuns('run-cursor-share', 'run-copilot-share')

    expect(summary).not.toBeNull()
    expect(summary!.runA.turnCount).toBe(1)
    expect(summary!.runB.turnCount).toBe(1)
  })

  it('warns when an excluded harness identity drops all share records', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    const sessionKey = 'gemini-excluded-session'
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-gemini-excl', 'gemini', 'derived')
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-cursor-peer', 'cursor', 'derived')
    db.prepare(
      `INSERT INTO execution
       (execution_id, run_id, session_id, harness, is_root, parent_linkage_json)
       VALUES (?, ?, ?, ?, 1, ?)`,
    ).run('exec-gemini-excl', 'run-gemini-excl', sessionKey, 'gemini', JSON.stringify('measured'))
    db.prepare(
      `INSERT INTO execution
       (execution_id, run_id, session_id, harness, is_root, parent_linkage_json)
       VALUES (?, ?, ?, ?, 1, ?)`,
    ).run('exec-cursor-peer', 'run-cursor-peer', 'cursor-peer-session', 'cursor', JSON.stringify('measured'))
    db.prepare(
      `INSERT INTO records
       (span_id, source, harness, session_id, name, op, kind, timestamp,
        duration_ms, status, tokens_json, content_json, cost_json)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      'span-gemini-excl',
      'otel',
      'gemini',
      sessionKey,
      'llm',
      'llm.invoke',
      'model',
      '2026-10-01T00:00:00.000Z',
      10,
      'success',
      JSON.stringify({
        freshInput: 100,
        cacheRead: 0,
        cacheCreation: 0,
        output: 10,
        reportedInput: 100,
        reportedOutput: 10,
      }),
      JSON.stringify({}),
      JSON.stringify({}),
    )
    db.prepare(
      `INSERT INTO records
       (span_id, source, harness, session_id, name, op, kind, timestamp,
        duration_ms, status, tokens_json, content_json, cost_json)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      'span-cursor-peer',
      'otel',
      'cursor',
      'cursor-peer-session',
      'llm',
      'llm.invoke',
      'model',
      '2026-10-01T00:00:00.000Z',
      10,
      'success',
      JSON.stringify({
        freshInput: 100,
        cacheRead: 0,
        cacheCreation: 0,
        output: 10,
        reportedInput: 100,
        reportedOutput: 10,
      }),
      JSON.stringify({}),
      JSON.stringify({}),
    )

    const summary = bridge.compareRuns('run-gemini-excl', 'run-cursor-peer')

    expect(summary).not.toBeNull()
    expect(summary!.runA.availability).toBe('unavailable')
    const shareDropWarnings = () =>
      warn.mock.calls.filter(
        (args) =>
          typeof args[0] === 'string' &&
          args[0].includes('[KyberBridge]') &&
          args[0].includes('excluded') &&
          args[0].includes('gemini'),
      ).length
    expect(shareDropWarnings()).toBe(1)
    // The empty side explains the drop in its own reason, not only the log.
    if (summary!.runA.availability === 'unavailable') {
      expect(summary!.runA.metricsReason).toContain('excluded identity')
      expect(summary!.runA.metricsReason).toContain('dropped 1 record(s)')
    }

    // A second Compare click on the same empty share does not re-warn.
    expect(bridge.compareRuns('run-gemini-excl', 'run-cursor-peer')).not.toBeNull()
    expect(shareDropWarnings()).toBe(1)
  })

  it('evicts the oldest share-drop warn key at the bound instead of clearing all', () => {
    // Pre-fill the private dedupe set to the module bound, then drive one more
    // distinct drop through recordsForShare so overflow is exercised without
    // inserting SHARE_DROP_WARN_LIMIT + 1 full compare fixtures.
    type ShareDropInternals = {
      warnedShareDrops: Set<string>
      recordsForShare(
        key: string,
        harness: string,
      ): { records: unknown[]; dropNote?: string }
    }
    const internals = bridge as unknown as ShareDropInternals
    const limit = SHARE_DROP_WARN_LIMIT
    const oldestKey = 'cursor\0sess-oldest'
    const newestSession = 'sess-newest'
    const newestKey = `cursor\0${newestSession}`

    const insertDropRecord = (sessionId: string, spanId: string) => {
      db.prepare(
        `INSERT INTO records
         (span_id, source, harness, session_id, name, op, kind, timestamp,
          duration_ms, status, tokens_json, content_json, cost_json)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      ).run(
        spanId,
        'otel',
        'copilot-cli',
        sessionId,
        'llm',
        'llm.invoke',
        'model',
        '2026-10-01T00:00:00.000Z',
        10,
        'success',
        JSON.stringify({
          freshInput: 100,
          cacheRead: 0,
          cacheCreation: 0,
          output: 10,
          reportedInput: 100,
          reportedOutput: 10,
        }),
        JSON.stringify({}),
        JSON.stringify({}),
      )
    }
    insertDropRecord('sess-oldest', 'span-drop-oldest')
    insertDropRecord(newestSession, 'span-drop-newest')

    internals.warnedShareDrops.add(oldestKey)
    for (let i = 0; i < limit - 1; i++) {
      internals.warnedShareDrops.add(`cursor\0fill-${i}`)
    }
    expect(internals.warnedShareDrops.size).toBe(limit)

    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {})
    const shareDropWarnings = () =>
      warn.mock.calls.filter(
        (args) => typeof args[0] === 'string' && args[0].includes('[KyberBridge] Compare share:'),
      ).length

    expect(internals.recordsForShare(newestSession, 'cursor').dropNote).toBeDefined()
    expect(shareDropWarnings()).toBe(1)
    // Bound held; only the oldest entry was removed — not a mass clear.
    expect(internals.warnedShareDrops.size).toBe(limit)
    expect(internals.warnedShareDrops.has(newestKey)).toBe(true)
    expect(internals.warnedShareDrops.has(oldestKey)).toBe(false)
    expect(internals.warnedShareDrops.has('cursor\0fill-0')).toBe(true)

    // The evicted oldest key may warn again; the newest key stays silenced.
    expect(internals.recordsForShare('sess-oldest', 'cursor').dropNote).toBeDefined()
    expect(shareDropWarnings()).toBe(2)
    expect(internals.recordsForShare(newestSession, 'cursor').dropNote).toBeDefined()
    expect(shareDropWarnings()).toBe(2)
    expect(internals.warnedShareDrops.size).toBe(limit)

    warn.mockRestore()
  })

  it('memoizes session identities across compareRuns until records change', () => {
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-memo-a', 'cursor', 'derived')
    db.prepare(
      'INSERT INTO run (run_id, harness, grouping_basis) VALUES (?, ?, ?)',
    ).run('run-memo-b', 'cursor', 'derived')
    for (const [runId, sessionId] of [
      ['run-memo-a', 'sess-memo-a'],
      ['run-memo-b', 'sess-memo-b'],
    ] as const) {
      db.prepare(
        `INSERT INTO execution
         (execution_id, run_id, session_id, harness, is_root, parent_linkage_json)
         VALUES (?, ?, ?, ?, 1, ?)`,
      ).run(`exec-${runId}`, runId, sessionId, 'cursor', JSON.stringify('measured'))
      db.prepare(
        `INSERT INTO records
         (span_id, source, harness, session_id, name, op, kind, timestamp,
          duration_ms, status, tokens_json, content_json, cost_json)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      ).run(
        `span-${runId}`,
        'otel',
        'cursor',
        sessionId,
        'llm',
        'llm.invoke',
        'model',
        '2026-10-01T00:00:00.000Z',
        10,
        'success',
        JSON.stringify({
          freshInput: 100,
          cacheRead: 0,
          cacheCreation: 0,
          output: 10,
          reportedInput: 100,
          reportedOutput: 10,
        }),
        JSON.stringify({}),
        JSON.stringify({}),
      )
    }

    const prepareSpy = vi.spyOn(db, 'prepare')
    expect(bridge.compareRuns('run-memo-a', 'run-memo-b')).not.toBeNull()
    const firstDistinctCalls = prepareSpy.mock.calls.filter(
      (args) => typeof args[0] === 'string' && args[0].includes('SELECT DISTINCT COALESCE(session_id'),
    ).length
    expect(firstDistinctCalls).toBe(1)

    expect(bridge.compareRuns('run-memo-a', 'run-memo-b')).not.toBeNull()
    const secondDistinctCalls = prepareSpy.mock.calls.filter(
      (args) => typeof args[0] === 'string' && args[0].includes('SELECT DISTINCT COALESCE(session_id'),
    ).length
    // Memoized: second compare must not re-issue the full-table DISTINCT.
    expect(secondDistinctCalls).toBe(1)

    // Inserting a record changes the generation fingerprint — cache must miss.
    db.prepare(
      `INSERT INTO records
       (span_id, source, harness, session_id, name, op, kind, timestamp,
        duration_ms, status, tokens_json, content_json, cost_json)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      'span-memo-extra',
      'otel',
      'cursor',
      'sess-memo-a',
      'llm',
      'llm.invoke',
      'model',
      '2026-10-01T00:00:01.000Z',
      10,
      'success',
      JSON.stringify({
        freshInput: 50,
        cacheRead: 0,
        cacheCreation: 0,
        output: 5,
        reportedInput: 50,
        reportedOutput: 5,
      }),
      JSON.stringify({}),
      JSON.stringify({}),
    )
    expect(bridge.compareRuns('run-memo-a', 'run-memo-b')).not.toBeNull()
    const afterChangeCalls = prepareSpy.mock.calls.filter(
      (args) => typeof args[0] === 'string' && args[0].includes('SELECT DISTINCT COALESCE(session_id'),
    ).length
    expect(afterChangeCalls).toBe(2)

    const distinctCalls = () =>
      prepareSpy.mock.calls.filter(
        (args) => typeof args[0] === 'string' && args[0].includes('SELECT DISTINCT COALESCE(session_id'),
      ).length

    // In-place UPDATE from another connection (backfill's setSessionId): row
    // count and max rowid are unchanged, but data_version moves — must miss.
    store.setSessionId('span-memo-extra', 'sess-memo-moved')
    expect(bridge.compareRuns('run-memo-a', 'run-memo-b')).not.toBeNull()
    expect(distinctCalls()).toBe(3)

    // In-place UPDATE on the bridge's own connection: total_changes() moves.
    db.prepare('UPDATE records SET session_id = ? WHERE span_id = ?').run('sess-memo-a', 'span-memo-extra')
    expect(bridge.compareRuns('run-memo-a', 'run-memo-b')).not.toBeNull()
    expect(distinctCalls()).toBe(4)

    // No writes since: warm memo again.
    expect(bridge.compareRuns('run-memo-a', 'run-memo-b')).not.toBeNull()
    expect(distinctCalls()).toBe(4)
  })
})

