import { describe, expect, it, vi } from 'vitest'

import { CanonStore } from './store.js'

// RED contract for the sessionSummaryFigures column-first conversion (Change A,
// store copy). The `session` table carries `summary_json` (the serialized
// `$.summary` object) beside `payload`, and this reader must consult the column
// first, falling back to the payload `json_extract` per row only when that
// row's `summary_json` is NULL — the same column-first/fallback shape the
// sessions-list read already uses.
//
// The writer keeps the column and the payload in agreement, so on a natural
// corpus both sources return the same numbers and agreement alone cannot
// observe which source the reader consults. The materialized tests below
// therefore diverge the two sources the way only a test can (rewriting the
// payload while leaving `summary_json` intact) to pin the read order. That
// divergence is a test instrument, never a production state.

const FIGURES_SUMMARY = {
  turn_count: 7,
  total_input: 1200,
  total_output: 340,
  cost: { basis: 'published', status: 'priced', value: 0.06, currency: 'USD' },
}

const EXPECTED_FIGURES_ROW = {
  sessionId: 'sess-materialized',
  turnCount: 7,
  totalInput: 1200,
  totalOutput: 340,
  costStatus: 'priced',
  costValue: 0.06,
  costCurrency: 'USD',
}

function upsertFigures(store: CanonStore, sessionId: string, summary: unknown): void {
  store.upsertSession({
    sessionId,
    harness: 'copilot',
    label: null,
    isSubagent: false,
    parentSession: null,
    agentName: null,
    repo: null,
    branch: null,
    started: null,
    ended: null,
    payload: { id: sessionId, summary },
  })
}

function summaryJsonOf(store: CanonStore, sessionId: string): string | null {
  const row = store
    .getDatabase()
    .prepare('SELECT summary_json AS summaryJson FROM session WHERE session_id = ?')
    .get(sessionId) as { summaryJson: string | null } | undefined
  return row?.summaryJson ?? null
}

type FiguresRow = ReturnType<CanonStore['sessionSummaryFigures']>[number]

function figuresById(store: CanonStore, sessionIds: readonly string[]): Map<string, FiguresRow> {
  return new Map(store.sessionSummaryFigures(sessionIds).map((row) => [row.sessionId, row]))
}

describe('sessionSummaryFigures reads the materialized summary column', () => {
  it('serves materialized figures from summary_json when the payload no longer carries a summary', () => {
    // RED for the column-first change: the current implementation reads the
    // figures out of the payload with `json_extract`, so once the payload's
    // summary is gone it reports a bare `{ sessionId }` even though the
    // materialized column holds every figure. After the change the populated
    // column wins and this passes.
    const store = new CanonStore(':memory:')
    try {
      upsertFigures(store, 'sess-materialized', FIGURES_SUMMARY)
      store
        .getDatabase()
        .prepare('UPDATE session SET payload = ? WHERE session_id = ?')
        .run(JSON.stringify({ note: 'payload rewritten without a summary' }), 'sess-materialized')

      expect(summaryJsonOf(store, 'sess-materialized')).not.toBeNull()
      expect(store.sessionSummaryFigures(['sess-materialized'])).toEqual([EXPECTED_FIGURES_ROW])
    } finally {
      store.close()
    }
  })

  it('falls back to the payload for a legacy row whose summary_json is NULL', () => {
    // A row written before materialization existed carries NULL, never an
    // empty object: the reader must derive the figures from that row's
    // payload. This holds before and after the change (characterization).
    const store = new CanonStore(':memory:')
    try {
      store
        .getDatabase()
        .prepare('INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)')
        .run('sess-legacy', 'copilot', JSON.stringify({ summary: FIGURES_SUMMARY }))

      expect(summaryJsonOf(store, 'sess-legacy')).toBeNull()
      expect(store.sessionSummaryFigures(['sess-legacy'])).toEqual([
        { ...EXPECTED_FIGURES_ROW, sessionId: 'sess-legacy' },
      ])
    } finally {
      store.close()
    }
  })

  it('applies the fallback per row in a mixed corpus of materialized and legacy rows', () => {
    // RED for the column-first change: the legacy row already reads
    // correctly, but the materialized row with a diverged payload loses its
    // figures until the column is consulted.
    const store = new CanonStore(':memory:')
    try {
      upsertFigures(store, 'sess-new', FIGURES_SUMMARY)
      store
        .getDatabase()
        .prepare('UPDATE session SET payload = ? WHERE session_id = ?')
        .run(JSON.stringify({ note: 'payload rewritten without a summary' }), 'sess-new')
      store
        .getDatabase()
        .prepare('INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)')
        .run('sess-old', 'copilot', JSON.stringify({ summary: FIGURES_SUMMARY }))

      expect(summaryJsonOf(store, 'sess-old')).toBeNull()
      const byId = figuresById(store, ['sess-new', 'sess-old'])
      expect(byId.get('sess-new')).toEqual({ ...EXPECTED_FIGURES_ROW, sessionId: 'sess-new' })
      expect(byId.get('sess-old')).toEqual({ ...EXPECTED_FIGURES_ROW, sessionId: 'sess-old' })
    } finally {
      store.close()
    }
  })

  it('omits a missing figure instead of zeroing it', () => {
    // Merge semantics the conversion must preserve exactly: a summary without
    // `turn_count` yields a result with no `turnCount` key — explicitly not
    // `turnCount: 0`. This holds before and after the change
    // (characterization).
    const store = new CanonStore(':memory:')
    try {
      upsertFigures(store, 'sess-sparse', { total_input: 1200, total_output: 340 })
      const [row] = store.sessionSummaryFigures(['sess-sparse'])
      expect(row).toBeDefined()
      expect('turnCount' in row!).toBe(false)
      expect(row!.turnCount).toBeUndefined()
      expect(row).toMatchObject({ sessionId: 'sess-sparse', totalInput: 1200, totalOutput: 340 })
    } finally {
      store.close()
    }
  })

  it('takes cost status and currency only when they are strings', () => {
    // Merge semantics the conversion must preserve exactly: a `cost.status`
    // (or `cost.currency`) that is not a string must not appear as
    // `costStatus` (or `costCurrency`); a string one must. This holds before
    // and after the change (characterization).
    const store = new CanonStore(':memory:')
    try {
      upsertFigures(store, 'sess-numstatus', {
        turn_count: 1,
        cost: { basis: 'published', status: 42, value: 0.05, currency: 'USD' },
      })
      upsertFigures(store, 'sess-numcurrency', {
        turn_count: 1,
        cost: { basis: 'published', status: 'priced', value: 0.05, currency: 123 },
      })
      const byId = figuresById(store, ['sess-numstatus', 'sess-numcurrency'])
      expect(byId.get('sess-numstatus')).toBeDefined()
      expect(byId.get('sess-numstatus')).not.toHaveProperty('costStatus')
      expect(byId.get('sess-numstatus')).toMatchObject({ costValue: 0.05, costCurrency: 'USD' })
      expect(byId.get('sess-numcurrency')).toBeDefined()
      expect(byId.get('sess-numcurrency')).toMatchObject({ costStatus: 'priced', costValue: 0.05 })
      expect(byId.get('sess-numcurrency')).not.toHaveProperty('costCurrency')
    } finally {
      store.close()
    }
  })

  it('drops non-finite numbers instead of emitting them as figures', () => {
    // Merge semantics the conversion must preserve exactly: a non-finite
    // `turn_count` is not returned as a figure. `JSON.stringify` cannot carry
    // NaN/Infinity (it writes null), and neither can the store's own writer,
    // so the rows below are seeded with raw SQL text carrying `1e999`, which
    // SQLite parses to Inf — the closest observable equivalent of a
    // non-finite figure on this path, and the reason the raw extraction is
    // asserted first. This holds before and after the change
    // (characterization): the guard lives in the shared merge, whichever
    // source the reader consults.
    const store = new CanonStore(':memory:')
    try {
      store
        .getDatabase()
        .prepare('INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)')
        .run(
          'sess-inf',
          'copilot',
          '{"summary":{"turn_count":1e999,"total_input":5,"total_output":6}}',
        )
      const extracted = store
        .getDatabase()
        .prepare("SELECT json_extract(payload, '$.summary.turn_count') AS turnCount FROM session WHERE session_id = ?")
        .get('sess-inf') as { turnCount: unknown }
      expect(typeof extracted.turnCount).toBe('number')
      expect(Number.isFinite(extracted.turnCount as number)).toBe(false)

      upsertFigures(store, 'sess-inf-mat', FIGURES_SUMMARY)
      // Both sources carry the identical non-finite text, so whichever source
      // the reader consults it meets the same guard: `turn_count` is dropped,
      // the finite totals are kept.
      const nonFiniteText = '{"turn_count":1e999,"total_input":5,"total_output":6}'
      store
        .getDatabase()
        .prepare('UPDATE session SET payload = ?, summary_json = ? WHERE session_id = ?')
        .run(`{"summary":${nonFiniteText}}`, nonFiniteText, 'sess-inf-mat')

      const byId = figuresById(store, ['sess-inf', 'sess-inf-mat'])
      for (const sessionId of ['sess-inf', 'sess-inf-mat']) {
        const row = byId.get(sessionId)
        expect(row).toBeDefined()
        expect(row).not.toHaveProperty('turnCount')
        expect(row).toMatchObject({ sessionId, totalInput: 5, totalOutput: 6 })
      }
    } finally {
      store.close()
    }
  })

  it('reads a fully-materialized corpus without naming the payload blob', () => {
    // RED for the column-first change: the current statement extracts every
    // figure with `json_extract(payload, ...)`, so a fully-materialized
    // corpus still pays for a whole-blob read on every row. After the change
    // (mirroring the sessions-list path) no issued statement names `payload`
    // — unless it is the legacy fallback, which is allowed only when scoped
    // with a `summary_json IS NULL` predicate.
    const store = new CanonStore(':memory:')
    try {
      upsertFigures(store, 'sess-a', FIGURES_SUMMARY)
      upsertFigures(store, 'sess-b', FIGURES_SUMMARY)
      const db = store.getDatabase()
      const prepareSpy = vi.spyOn(db, 'prepare')
      let calls: unknown[][] = []
      try {
        expect(store.sessionSummaryFigures(['sess-a', 'sess-b'])).toHaveLength(2)
      } finally {
        calls = [...prepareSpy.mock.calls]
        prepareSpy.mockRestore()
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
    } finally {
      store.close()
    }
  })
})
