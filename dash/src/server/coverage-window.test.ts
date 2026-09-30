import type { AddressInfo } from 'net'
import { DatabaseSync } from 'node:sqlite'
import { describe, expect, it, vi } from 'vitest'

import { runWebDashboard } from '../cli/web.js'
import { KyberBridge } from './bridge.js'

function seedWindowDb(canonDb: DatabaseSync): void {
  canonDb.exec(`
    CREATE TABLE refresh_run (
      id TEXT PRIMARY KEY, started_at TEXT NOT NULL, completed_at TEXT,
      status TEXT NOT NULL, pid INTEGER NOT NULL, trigger TEXT NOT NULL,
      summary TEXT, history_weeks INTEGER
    );
    CREATE TABLE harness_rollup (
      harness TEXT PRIMARY KEY, sample_count INTEGER NOT NULL DEFAULT 0,
      context_pressure_median REAL, context_pressure_p95 REAL,
      cache_hit_rate REAL, tool_yield REAL, delegation_overhead REAL,
      field_coverage REAL, measurability_json TEXT NOT NULL, payload TEXT
    );
    CREATE TABLE session (
      session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, label TEXT,
      is_subagent INTEGER NOT NULL DEFAULT 0, parent_session TEXT,
      agent_name TEXT, repo TEXT, branch TEXT,
      started TEXT, ended TEXT, payload TEXT NOT NULL
    );
  `)
  // Window is [2026-09-16T00:00:00.000Z, 2026-09-30T00:00:00.000Z].
  canonDb
    .prepare(
      'INSERT INTO refresh_run (id, started_at, completed_at, status, pid, trigger, summary, history_weeks) VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
    )
    .run('win-run', '2026-09-30T00:00:00.000Z', '2026-09-30T00:30:00.000Z', 'success', 1234, 'cli', 'ok', 2)
}

function seedRollup(canonDb: DatabaseSync, harness: string, sampleCount: number): void {
  canonDb
    .prepare('INSERT INTO harness_rollup (harness, sample_count, measurability_json, payload) VALUES (?, ?, ?, ?)')
    .run(harness, sampleCount, '{}', JSON.stringify({ sessionCount: sampleCount }))
}

function seedSession(
  canonDb: DatabaseSync,
  id: string,
  harness: string,
  started: string | null,
  ended: string | null,
): void {
  canonDb
    .prepare('INSERT INTO session (session_id, harness, started, ended, payload) VALUES (?, ?, ?, ?, ?)')
    .run(id, harness, started, ended, '{}')
}

async function fetchHarnesses(base: string): Promise<Map<string, Record<string, unknown>>> {
  const res = await fetch(`${base}/api/kyber/harnesses`)
  expect(res.status).toBe(200)
  const body = (await res.json()) as { harnesses: Array<Record<string, unknown>> }
  return new Map(body.harnesses.map((row) => [row.harness as string, row]))
}

describe('coverage window RED (PR #230 review-response)', () => {
  it('thread 4150060203: session end decides the window, matching report sessionAt (ended ?? started)', async () => {
    // Started pre-window but ended in-window: report build.ts sessionAt uses
    // `ended ?? started`, so this harness is covered (null). The routes
    // `started ?? ended` precedence reads it as pre-window-only (wrong).
    const canonDb = new DatabaseSync(':memory:')
    try {
      seedWindowDb(canonDb)
      seedRollup(canonDb, 'ended-in-window', 1)
      seedSession(canonDb, 'sess-1', 'ended-in-window', '2026-08-01T12:00:00.000Z', '2026-09-20T12:00:00.000Z')
      const bridge = new KyberBridge({ canonDb })
      const server = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
      try {
        const base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
        const byId = await fetchHarnesses(base)
        expect(byId.get('ended-in-window')?.noDataReason).toBeNull()
      } finally {
        await new Promise<void>((resolve) => server.close(() => resolve()))
        bridge.close()
      }
    } finally {
      try {
        canonDb.close()
      } catch {
        // The bridge already closed the injected handle.
      }
    }
  })

  it('thread 4150060208: window compare uses epoch ms, so +02:00 offsets sort correctly', async () => {
    // 2026-09-16T01:00:00+02:00 is 2026-09-15T23:00:00Z in epoch — before the
    // 2026-09-16T00:00:00.000Z window floor — but raw string compare sorts it
    // after the floor ("01" > "00") and wrongly reads it as in-window (null).
    const canonDb = new DatabaseSync(':memory:')
    try {
      seedWindowDb(canonDb)
      seedRollup(canonDb, 'offset-harness', 1)
      seedSession(canonDb, 'sess-off', 'offset-harness', '2026-09-16T01:00:00+02:00', '2026-09-16T01:00:00+02:00')
      const bridge = new KyberBridge({ canonDb })
      const server = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
      try {
        const base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
        const byId = await fetchHarnesses(base)
        expect(byId.get('offset-harness')?.noDataReason).toMatch(/no .* coverage window/i)
      } finally {
        await new Promise<void>((resolve) => server.close(() => resolve()))
        bridge.close()
      }
    } finally {
      try {
        canonDb.close()
      } catch {
        // The bridge already closed the injected handle.
      }
    }
  })

  it('thread 4150060213: harness window reads use a capped seam, never uncapped listSessions()', async () => {
    const canonDb = new DatabaseSync(':memory:')
    try {
      seedWindowDb(canonDb)
      seedRollup(canonDb, 'pi', 1)
      seedSession(canonDb, 'sess-pi-1', 'pi', '2026-09-20T12:00:00.000Z', '2026-09-20T12:00:00.000Z')
      const bridge = new KyberBridge({ canonDb })
      const spy = vi.spyOn(bridge, 'listSessions')
      const server = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
      try {
        const base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
        const res = await fetch(`${base}/api/kyber/harnesses`)
        expect(res.status).toBe(200)
        // The window context must not materialize the session table: an
        // uncapped listSessions() call (no limit argument) fails this test.
        const uncapped = spy.mock.calls.filter((args) => args[0] === undefined)
        expect(uncapped).toHaveLength(0)
      } finally {
        await new Promise<void>((resolve) => server.close(() => resolve()))
        spy.mockRestore()
        bridge.close()
      }
    } finally {
      try {
        canonDb.close()
      } catch {
        // The bridge already closed the injected handle.
      }
    }
  })
})
