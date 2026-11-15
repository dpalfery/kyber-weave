import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'

// RED contract for the sessionSummaryFigures column-first conversion (Change A,
// bridge copy). The bridge carries a duplicate copy of the figures'
// payload-extraction SQL for its direct-DB path (used when no store is
// injected), and both copies must be converted together. The contract mirrors
// the store copy in `dash/src/canon/session-summary-figures.test.ts`: a
// materialized row is read from `summary_json`, a legacy row (NULL
// `summary_json`) falls back to its own payload, merge semantics are preserved
// exactly, and the two copies agree on the same corpus.
//
// As there, the writer keeps the column and the payload in agreement, so the
// materialized tests diverge the two sources the way only a test can
// (rewriting the payload while leaving `summary_json` intact) to pin the read
// order. That divergence is a test instrument, never a production state.

const FIGURES_SUMMARY: Record<string, unknown> = {
  turn_count: 7,
  total_input: 1200,
  total_output: 340,
  cost: { basis: 'published', status: 'priced', value: 0.06, currency: 'USD' },
}

const payloadWith = (id: string, summary: unknown = FIGURES_SUMMARY): string =>
  JSON.stringify({ id, harness: 'copilot', summary })

const createFiguresDb = (): DatabaseSync => {
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

const insertFigureRow = (
  db: DatabaseSync,
  row: { id: string; payload: string; summaryJson?: string | null },
): void => {
  db.prepare('INSERT INTO session (session_id, harness, payload, summary_json) VALUES (?, ?, ?, ?)').run(
    row.id,
    'copilot',
    row.payload,
    row.summaryJson ?? null,
  )
}

const tempRoots: string[] = []

afterEach(() => {
  while (tempRoots.length > 0) {
    const root = tempRoots.pop()
    if (root) rmSync(root, { recursive: true, force: true })
  }
})

describe('KyberBridge.sessionSummaryFigures column-first read path (direct-DB copy)', () => {
  it('serves materialized figures from summary_json when the payload no longer carries a summary', () => {
    // RED for the column-first change: the direct-DB copy reads the figures
    // out of the payload with `json_extract`, so once the payload's summary
    // is gone it reports an empty figure set even though the materialized
    // column holds every figure. After the change the populated column wins
    // and this passes.
    const db = createFiguresDb()
    insertFigureRow(db, {
      id: 'sess-m',
      payload: JSON.stringify({ note: 'payload rewritten without a summary' }),
      summaryJson: JSON.stringify(FIGURES_SUMMARY),
    })
    const bridge = new KyberBridge({ canonDb: db })
    try {
      expect(bridge.sessionSummaryFigures(['sess-m']).get('sess-m')).toEqual({
        turnCount: 7,
        totalInput: 1200,
        totalOutput: 340,
        costUsd: 0.06,
      })
    } finally {
      bridge.close()
    }
  })

  it('falls back to the payload for a legacy row whose summary_json is NULL', () => {
    // A row whose `summary_json` is NULL must still read its figures from its
    // own payload. This holds before and after the change (characterization).
    const db = createFiguresDb()
    insertFigureRow(db, { id: 'sess-legacy', payload: payloadWith('sess-legacy') })
    const bridge = new KyberBridge({ canonDb: db })
    try {
      expect(bridge.sessionSummaryFigures(['sess-legacy']).get('sess-legacy')).toEqual({
        turnCount: 7,
        totalInput: 1200,
        totalOutput: 340,
        costUsd: 0.06,
      })
    } finally {
      bridge.close()
    }
  })

  it('applies the fallback per row in a mixed corpus of materialized and legacy rows', () => {
    // RED for the column-first change: the legacy row already reads
    // correctly, but the materialized row with a diverged payload loses its
    // figures until the column is consulted.
    const db = createFiguresDb()
    insertFigureRow(db, {
      id: 'sess-new',
      payload: JSON.stringify({ note: 'payload rewritten without a summary' }),
      summaryJson: JSON.stringify(FIGURES_SUMMARY),
    })
    insertFigureRow(db, { id: 'sess-old', payload: payloadWith('sess-old') })
    const bridge = new KyberBridge({ canonDb: db })
    try {
      const figures = bridge.sessionSummaryFigures(['sess-new', 'sess-old'])
      const expected = { turnCount: 7, totalInput: 1200, totalOutput: 340, costUsd: 0.06 }
      expect(figures.get('sess-new')).toEqual(expected)
      expect(figures.get('sess-old')).toEqual(expected)
    } finally {
      bridge.close()
    }
  })

  it('omits a missing figure instead of zeroing it', () => {
    // Merge semantics the conversion must preserve exactly: a summary without
    // `turn_count` yields a figure set with no `turnCount` key — explicitly
    // not `turnCount: 0`. This holds before and after the change
    // (characterization).
    const db = createFiguresDb()
    insertFigureRow(db, {
      id: 'sess-sparse',
      payload: payloadWith('sess-sparse', { total_input: 1200, total_output: 340 }),
    })
    const bridge = new KyberBridge({ canonDb: db })
    try {
      const figures = bridge.sessionSummaryFigures(['sess-sparse']).get('sess-sparse')
      expect(figures).toBeDefined()
      expect(figures).not.toHaveProperty('turnCount')
      expect(figures).toEqual({ totalInput: 1200, totalOutput: 340 })
    } finally {
      bridge.close()
    }
  })

  it('reports no cost figure when the cost status is not a string', () => {
    // The bridge shape carries no raw `costStatus` — the observable pin is
    // that a non-string `cost.status` must not price: no `costUsd` key. A
    // priced string-status block still prices. This holds before and after
    // the change (characterization).
    const db = createFiguresDb()
    insertFigureRow(db, {
      id: 'sess-numstatus',
      payload: payloadWith('sess-numstatus', {
        turn_count: 1,
        cost: { basis: 'published', status: 42, value: 0.05, currency: 'USD' },
      }),
    })
    insertFigureRow(db, {
      id: 'sess-priced',
      payload: payloadWith('sess-priced', {
        turn_count: 1,
        cost: { basis: 'published', status: 'priced', value: 0.05, currency: 'USD' },
      }),
    })
    const bridge = new KyberBridge({ canonDb: db })
    try {
      const figures = bridge.sessionSummaryFigures(['sess-numstatus', 'sess-priced'])
      const unpriced = figures.get('sess-numstatus')
      expect(unpriced).toBeDefined()
      expect(unpriced).not.toHaveProperty('costUsd')
      expect(unpriced).toEqual({ turnCount: 1 })
      expect(figures.get('sess-priced')).toEqual({ turnCount: 1, costUsd: 0.05 })
    } finally {
      bridge.close()
    }
  })

  it('drops non-finite numbers instead of emitting them as figures', () => {
    // As in the store copy, `JSON.stringify` cannot carry NaN/Infinity, so
    // the row is seeded with raw SQL text carrying `1e999`, which SQLite
    // parses to Inf — the closest observable equivalent of a non-finite
    // figure on this path, and the reason the raw extraction is asserted
    // first. This holds before and after the change (characterization).
    const db = createFiguresDb()
    insertFigureRow(db, {
      id: 'sess-inf',
      payload: '{"summary":{"turn_count":1e999,"total_input":5,"total_output":6}}',
    })
    const extracted = db
      .prepare("SELECT json_extract(payload, '$.summary.turn_count') AS turnCount FROM session WHERE session_id = ?")
      .get('sess-inf') as { turnCount: unknown }
    expect(typeof extracted.turnCount).toBe('number')
    expect(Number.isFinite(extracted.turnCount as number)).toBe(false)
    const bridge = new KyberBridge({ canonDb: db })
    try {
      const figures = bridge.sessionSummaryFigures(['sess-inf']).get('sess-inf')
      expect(figures).toBeDefined()
      expect(figures).not.toHaveProperty('turnCount')
      expect(figures).toEqual({ totalInput: 5, totalOutput: 6 })
    } finally {
      bridge.close()
    }
  })

  it('reads a fully-materialized corpus without naming the payload blob', () => {
    // RED for the column-first change: the direct-DB copy extracts every
    // figure with `json_extract(payload, ...)`, so a fully-materialized
    // corpus still pays for a whole-blob read on every row. After the change
    // (mirroring the sessions-list path) no issued statement names `payload`
    // — unless it is the legacy fallback, which is allowed only when scoped
    // with a `summary_json IS NULL` predicate.
    const db = createFiguresDb()
    insertFigureRow(db, {
      id: 'sess-a',
      payload: payloadWith('sess-a'),
      summaryJson: JSON.stringify(FIGURES_SUMMARY),
    })
    insertFigureRow(db, {
      id: 'sess-b',
      payload: payloadWith('sess-b'),
      summaryJson: JSON.stringify(FIGURES_SUMMARY),
    })
    const prepareSpy = vi.spyOn(db, 'prepare')
    let calls: unknown[][] = []
    const bridge = new KyberBridge({ canonDb: db })
    try {
      expect(bridge.sessionSummaryFigures(['sess-a', 'sess-b']).size).toBe(2)
    } finally {
      calls = [...prepareSpy.mock.calls]
      prepareSpy.mockRestore()
      bridge.close()
    }
    const sessionStatements = calls
      .map((args) => args[0])
      .filter((sql): sql is string => typeof sql === 'string')
      .filter((sql) => sql.includes('FROM session'))
    expect(sessionStatements.length).toBeGreaterThan(0)
    const payloadReads = sessionStatements.filter(
      (sql) => sql.includes('payload') && !sql.includes('summary_json IS NULL'),
    )
    expect(payloadReads).toEqual([])
  })

  it('returns identical figures from the store-injected path and the direct-DB path for the same corpus', () => {
    // The "convert both copies together" pin: the store-injected path
    // (which delegates to `CanonStore.sessionSummaryFigures`) and the
    // direct-DB path (which runs its own SQL) must agree on the same corpus
    // — including the mixed corpus. Today both read the payload, so both
    // agree while both wrong on the diverged row (characterization); if the
    // GREEN phase converted only one copy, this fails.
    const root = mkdtempSync(join(tmpdir(), 'kyber-figures-agree-'))
    tempRoots.push(root)
    const store = new CanonStore(join(root, 'canon.db'))
    try {
      store.upsertSession({
        sessionId: 'sess-new',
        harness: 'copilot',
        label: null,
        isSubagent: false,
        parentSession: null,
        agentName: null,
        repo: null,
        branch: null,
        started: null,
        ended: null,
        payload: { id: 'sess-new', summary: FIGURES_SUMMARY },
      })
      store
        .getDatabase()
        .prepare('UPDATE session SET payload = ? WHERE session_id = ?')
        .run(JSON.stringify({ note: 'payload rewritten without a summary' }), 'sess-new')
      store
        .getDatabase()
        .prepare('INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)')
        .run('sess-old', 'copilot', payloadWith('sess-old'))

      const ids = ['sess-new', 'sess-old']
      const viaStoreBridge = new KyberBridge({ canonPath: ':memory:', store })
      let viaStore: Map<string, object>
      try {
        viaStore = new Map(viaStoreBridge.sessionSummaryFigures(ids).entries())
      } finally {
        viaStoreBridge.close()
      }
      const directDb = new DatabaseSync(join(root, 'canon.db'), { readOnly: true })
      const directBridge = new KyberBridge({ canonDb: directDb })
      let direct: Map<string, object>
      try {
        direct = new Map(directBridge.sessionSummaryFigures(ids).entries())
      } finally {
        directBridge.close()
      }

      expect([...direct.entries()]).toEqual([...viaStore.entries()])
      const expected = { turnCount: 7, totalInput: 1200, totalOutput: 340, costUsd: 0.06 }
      expect(viaStore.get('sess-old')).toEqual(expected)
      expect(direct.get('sess-old')).toEqual(expected)
    } finally {
      store.close()
    }
  })
})
