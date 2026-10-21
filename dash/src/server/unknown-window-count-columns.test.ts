import { DatabaseSync } from 'node:sqlite'
import { describe, expect, it, vi } from 'vitest'

import { KyberBridge } from './bridge.js'

// RED contract for the unknown-window count filter (change D, bridge copy):
// the bridge's direct-DB fallback (`countUnknownWindowSessions` with no
// store) runs `COUNT(*)` with `json_extract(payload,
// '$.context.contextLimitSource')` in the `WHERE`, paging every scanned
// payload blob table-wide when no harness is given. The change filters on a
// `context_limit_source` column instead; schema v22 (landing in parallel,
// see `migration-v22.test.ts`) adds and backfills that column.
//
// Same honesty rule as the store copy in
// `dash/src/canon/unknown-window-count-columns.test.ts`: with the column
// absent the payload-expression fallback must still work and count right
// (acceptable until the migration lands); with the column present the count
// must be identical AND the SQL must name the column, not
// `json_extract(payload, …)`. The harness/session/run-scoped single-row
// payload reads are out of scope — they touch one row, not the table.

const SESSION_DDL = `
  CREATE TABLE session (
    session_id TEXT PRIMARY KEY,
    harness TEXT NOT NULL,
    payload TEXT NOT NULL
  );
`

function seedBridgeCorpus(db: DatabaseSync): void {
  db.exec(SESSION_DDL)
  const insert = db.prepare('INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)')
  const rows: Array<{ id: string; harness: string; source?: string }> = [
    { id: 'sess-b1', harness: 'copilot', source: 'default' },
    { id: 'sess-b2', harness: 'copilot', source: 'absent' },
    { id: 'sess-b3', harness: 'copilot', source: 'reported' },
    { id: 'sess-b4', harness: 'codex', source: 'default' },
    { id: 'sess-b5', harness: 'copilot' },
  ]
  for (const row of rows) {
    insert.run(
      row.id,
      row.harness,
      JSON.stringify({
        id: row.id,
        ...(row.source === undefined ? {} : { context: { contextLimitSource: row.source } }),
      }),
    )
  }
}

function hasContextLimitSourceColumn(db: DatabaseSync): boolean {
  const columns = db.prepare('PRAGMA table_info(session)').all() as Array<{ name: string }>
  return columns.some((column) => column.name === 'context_limit_source')
}

/** Add and backfill the v22 column when the migration has not landed yet. */
function ensureContextLimitSourceColumn(db: DatabaseSync): void {
  if (hasContextLimitSourceColumn(db)) return
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

function closeBridge(bridge: KyberBridge, db: DatabaseSync): void {
  bridge.close()
  try {
    db.close()
  } catch {
    // The bridge already closed the injected handle.
  }
}

describe('KyberBridge.countUnknownWindowSessions reads the context_limit_source column (direct-DB copy)', () => {
  it('counts the unknown-window sessions with and without a harness scope', () => {
    // Characterization: the numbers are the contract — default/absent count,
    // reported and missing do not, scoped and table-wide alike. Passes today
    // via the payload expression; must stay identical after GREEN.
    const db = new DatabaseSync(':memory:')
    seedBridgeCorpus(db)
    const bridge = new KyberBridge({ canonDb: db })
    try {
      expect(bridge.countUnknownWindowSessions()).toBe(3)
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot' })).toBe(2)
      expect(bridge.countUnknownWindowSessions({ harness: 'codex' })).toBe(1)
      expect(bridge.countUnknownWindowSessions({ harness: 'claude-code' })).toBe(0)
    } finally {
      closeBridge(bridge, db)
    }
  })

  it('falls back to the payload expression while the column does not exist', () => {
    // Characterization of the explicitly allowed pre-column behavior: until
    // the v22 migration lands there is no column to read, so the query must
    // still work via the payload expression. Skipped (vacuously true) once a
    // migrated store carries the column.
    const db = new DatabaseSync(':memory:')
    seedBridgeCorpus(db)
    if (hasContextLimitSourceColumn(db)) return
    const bridge = new KyberBridge({ canonDb: db })
    const spy = vi.spyOn(db, 'prepare')
    let calls: unknown[][] = []
    try {
      expect(bridge.countUnknownWindowSessions()).toBe(3)
    } finally {
      calls = [...spy.mock.calls]
      spy.mockRestore()
      closeBridge(bridge, db)
    }
    const probed = countStatements(calls)
    expect(probed.length).toBeGreaterThan(0)
    expect(probed.some((sql) => sql.includes('json_extract(payload'))).toBe(true)
  })

  it('counts identically with the column present as without it', () => {
    // Characterization that must hold on both sides of the migration: the
    // same corpus, the same numbers, whether the column exists or not.
    const db = new DatabaseSync(':memory:')
    seedBridgeCorpus(db)
    const bridge = new KyberBridge({ canonDb: db })
    try {
      const before = {
        all: bridge.countUnknownWindowSessions(),
        copilot: bridge.countUnknownWindowSessions({ harness: 'copilot' }),
        codex: bridge.countUnknownWindowSessions({ harness: 'codex' }),
      }
      ensureContextLimitSourceColumn(db)
      expect(bridge.countUnknownWindowSessions()).toBe(before.all)
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot' })).toBe(before.copilot)
      expect(bridge.countUnknownWindowSessions({ harness: 'codex' })).toBe(before.codex)
      expect(before).toEqual({ all: 3, copilot: 2, codex: 1 })
    } finally {
      closeBridge(bridge, db)
    }
  })

  it('filters on the context_limit_source column, not on json_extract(payload), when the column exists', () => {
    // RED: even with the column present and backfilled, the query still
    // filters on `json_extract(payload, '$.context.contextLimitSource')`.
    // After GREEN every session `COUNT(*)` statement must name the column
    // and none may reach into `json_extract(payload, …)`.
    const db = new DatabaseSync(':memory:')
    seedBridgeCorpus(db)
    ensureContextLimitSourceColumn(db)
    const bridge = new KyberBridge({ canonDb: db })
    const spy = vi.spyOn(db, 'prepare')
    let calls: unknown[][] = []
    try {
      expect(bridge.countUnknownWindowSessions()).toBe(3)
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot' })).toBe(2)
    } finally {
      calls = [...spy.mock.calls]
      spy.mockRestore()
      closeBridge(bridge, db)
    }
    const probed = countStatements(calls)
    expect(probed.length).toBeGreaterThan(0)
    for (const sql of probed) {
      expect(sql).toContain('context_limit_source')
      expect(sql).not.toContain('json_extract(payload')
    }
  })

  it('keeps answering the session-scoped single-row read', () => {
    // Characterization: the session scope reads one row's payload, never a
    // table scan, so change D leaves it alone — it must keep working.
    const db = new DatabaseSync(':memory:')
    seedBridgeCorpus(db)
    const bridge = new KyberBridge({ canonDb: db })
    try {
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-b1' })).toBe(1)
      expect(bridge.countUnknownWindowSessions({ harness: 'copilot', sessionId: 'sess-b3' })).toBe(0)
    } finally {
      closeBridge(bridge, db)
    }
  })
})
