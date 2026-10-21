import { describe, expect, it, vi } from 'vitest'

import { CanonStore } from './store.js'

// RED contract for the unknown-window count filter (change D, store copy):
// `countUnknownWindowSessions` runs `COUNT(*)` with
// `json_extract(payload, '$.context.contextLimitSource')` in the `WHERE` —
// aggregate-only, but SQLite still pages every scanned payload blob,
// table-wide when no harness is given. The change filters on a
// `context_limit_source` column instead; schema v22 (landing in parallel,
// see `migration-v22.test.ts`) adds and backfills that column.
//
// Until that migration lands the column does not exist, so this contract is
// honest about both states: with the column absent the count must be right
// via the payload-expression fallback (acceptable until the migration lands);
// with the column present — simulated here by adding and backfilling it the
// way the migration will — the count must be identical AND the issued SQL
// must filter on the column, not on `json_extract(payload, …)`.

function seedUnknownWindowCorpus(store: CanonStore): void {
  const sessions: Array<{ id: string; harness: string; source?: string }> = [
    { id: 'sess-d1', harness: 'copilot', source: 'default' },
    { id: 'sess-d2', harness: 'copilot', source: 'absent' },
    { id: 'sess-d3', harness: 'copilot', source: 'reported' },
    { id: 'sess-d4', harness: 'codex', source: 'default' },
    { id: 'sess-d5', harness: 'copilot' },
  ]
  for (const session of sessions) {
    store.upsertSession({
      sessionId: session.id,
      harness: session.harness,
      payload: {
        id: session.id,
        ...(session.source === undefined ? {} : { context: { contextLimitSource: session.source } }),
      },
    })
  }
  // A legacy payload whose context exists but carries no source key at all.
  store.upsertSession({
    sessionId: 'sess-d6',
    harness: 'copilot',
    payload: { id: 'sess-d6', context: {} },
  })
}

function hasContextLimitSourceColumn(store: CanonStore): boolean {
  const columns = store
    .getDatabase()
    .prepare('PRAGMA table_info(session)')
    .all() as Array<{ name: string }>
  return columns.some((column) => column.name === 'context_limit_source')
}

/** Add and backfill the v22 column when the migration has not landed yet. */
function ensureContextLimitSourceColumn(store: CanonStore): void {
  if (hasContextLimitSourceColumn(store)) return
  const db = store.getDatabase()
  db.exec('ALTER TABLE session ADD COLUMN context_limit_source TEXT')
  db.prepare(
    `UPDATE session SET context_limit_source = json_extract(payload, '$.context.contextLimitSource')`,
  ).run()
}

function countStatements(calls: unknown[][]): string[] {
  return calls
    .map((args) => args[0])
    .filter((sql): sql is string => typeof sql === 'string')
    .filter((sql) => /COUNT\(\*\)/i.test(sql) && sql.includes('FROM session'))
}

describe('countUnknownWindowSessions reads the context_limit_source column (store copy)', () => {
  it('counts the unknown-window sessions with and without a harness scope', () => {
    // Characterization: the numbers are the contract — default/absent count,
    // reported and missing do not, scoped and table-wide alike. Passes today
    // via the payload expression; must stay identical after GREEN.
    const store = new CanonStore(':memory:')
    try {
      seedUnknownWindowCorpus(store)
      expect(store.countUnknownWindowSessions()).toBe(3)
      expect(store.countUnknownWindowSessions('copilot')).toBe(2)
      expect(store.countUnknownWindowSessions('codex')).toBe(1)
      expect(store.countUnknownWindowSessions('claude-code')).toBe(0)
    } finally {
      store.close()
    }
  })

  it('falls back to the payload expression while the column does not exist', () => {
    // Characterization of the explicitly allowed pre-column behavior: until
    // the v22 migration lands there is no column to read, so the query must
    // still work via the payload expression. Skipped (vacuously true) once a
    // migrated store carries the column.
    const store = new CanonStore(':memory:')
    try {
      seedUnknownWindowCorpus(store)
      if (hasContextLimitSourceColumn(store)) return
      const spy = vi.spyOn(store.getDatabase(), 'prepare')
      let calls: unknown[][] = []
      try {
        expect(store.countUnknownWindowSessions()).toBe(3)
      } finally {
        calls = [...spy.mock.calls]
        spy.mockRestore()
      }
      const probed = countStatements(calls)
      expect(probed.length).toBeGreaterThan(0)
      expect(probed.some((sql) => sql.includes('json_extract(payload'))).toBe(true)
    } finally {
      store.close()
    }
  })

  it('counts identically with the column present as without it', () => {
    // Characterization that must hold on both sides of the migration: the
    // same corpus, the same numbers, whether the column exists or not.
    const store = new CanonStore(':memory:')
    try {
      seedUnknownWindowCorpus(store)
      const before = {
        all: store.countUnknownWindowSessions(),
        copilot: store.countUnknownWindowSessions('copilot'),
        codex: store.countUnknownWindowSessions('codex'),
      }
      ensureContextLimitSourceColumn(store)
      expect(store.countUnknownWindowSessions()).toBe(before.all)
      expect(store.countUnknownWindowSessions('copilot')).toBe(before.copilot)
      expect(store.countUnknownWindowSessions('codex')).toBe(before.codex)
      expect(before).toEqual({ all: 3, copilot: 2, codex: 1 })
    } finally {
      store.close()
    }
  })

  it('filters on the context_limit_source column, not on json_extract(payload), when the column exists', () => {
    // RED: even with the column present and backfilled, the query still
    // filters on `json_extract(payload, '$.context.contextLimitSource')`.
    // After GREEN every session `COUNT(*)` statement must name the column
    // and none may reach into `json_extract(payload, …)`.
    const store = new CanonStore(':memory:')
    try {
      seedUnknownWindowCorpus(store)
      ensureContextLimitSourceColumn(store)
      const spy = vi.spyOn(store.getDatabase(), 'prepare')
      try {
        expect(store.countUnknownWindowSessions()).toBe(3)
        expect(store.countUnknownWindowSessions('copilot')).toBe(2)
      } finally {
        const calls = [...spy.mock.calls]
        spy.mockRestore()
        const probed = countStatements(calls)
        expect(probed.length).toBeGreaterThan(0)
        for (const sql of probed) {
          expect(sql).toContain('context_limit_source')
          expect(sql).not.toContain('json_extract(payload')
        }
      }
    } finally {
      store.close()
    }
  })
})
