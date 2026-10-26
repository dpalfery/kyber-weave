import type { AddressInfo } from 'net'
import type { IncomingMessage, Server, ServerResponse } from 'http'
import { tmpdir } from 'os'
import { join } from 'path'
import { DatabaseSync } from 'node:sqlite'
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'

import { runWebDashboard } from '../cli/web.js'
import { KyberBridge } from './bridge.js'
import { handleKyberRequest } from './routes.js'
import { useAbsentLiveReceiver } from '../../tests/setup/live-receiver-stub.js'

useAbsentLiveReceiver()

// RED contract for the bounded timeline read (change B):
// `GET /api/kyber/timeline` serves `payload.timeline` (15.3 MB in, 13.8 KB
// out) by reading the session's entire payload blob. The change serves the
// same bytes without reading the whole payload; the web client is unchanged,
// so the response must stay byte-identical for the same session.
//
// Fixture: one session whose payload carries a small timeline beside hostile
// padding (200 turn rows plus a 50 KB notes blob), so a whole-payload select
// is observable in the SQL tap while the served bytes stay small.

const TIMELINE = {
  spanId: 'span-root-tl',
  name: 'Timeline Session',
  children: [{ spanId: 'span-child-tl', name: 'Child span' }],
  cost: { basis: 'published', value: 0.12 },
}

const SESSION_DDL = `
  CREATE TABLE session (
    session_id TEXT PRIMARY KEY, harness TEXT NOT NULL, label TEXT,
    is_subagent INTEGER DEFAULT 0, parent_session TEXT, agent_name TEXT,
    repo TEXT, branch TEXT, started TEXT, ended TEXT, payload TEXT,
    summary_json TEXT, problems_count INTEGER
  );
`

function hostilePayload(): string {
  return JSON.stringify({
    id: 'sess-timeline-1',
    harness: 'copilot',
    summary: { turn_count: 200, request_count: 200, total_input: 2000, total_output: 1000 },
    problems: [],
    timeline: TIMELINE,
    turns: Array.from({ length: 200 }, (_, index) => ({ index, input: 10, output: 5, model: 'gpt-4o' })),
    notes: 'x'.repeat(50_000),
  })
}

function seedTimelineDb(db: DatabaseSync): void {
  db.exec(SESSION_DDL)
  db.prepare(
    'INSERT INTO session (session_id, harness, label, is_subagent, parent_session, agent_name, ' +
      'repo, branch, started, ended, payload, summary_json, problems_count) ' +
      'VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
  ).run(
    'sess-timeline-1',
    'copilot',
    'Timeline Session',
    0,
    null,
    'tl-agent',
    'kyber-weave',
    'main',
    '2026-09-04T12:00:00.000Z',
    '2026-09-04T12:10:00.000Z',
    hostilePayload(),
    JSON.stringify({ turn_count: 200 }),
    0,
  )
}

describe('GET /api/kyber/timeline (bounded timeline read)', () => {
  let server: Server
  let base: string
  let canonDb: DatabaseSync
  let bridge: KyberBridge

  beforeAll(async () => {
    canonDb = new DatabaseSync(':memory:')
    seedTimelineDb(canonDb)
    bridge = new KyberBridge({ canonDb, ratesPath: join(tmpdir(), 'nonexistent-timeline-rates.json') })
    server = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    bridge.close()
    try {
      canonDb.close()
    } catch {
      // The bridge already closed the injected handle.
    }
  })

  function sessionStatements(calls: unknown[][]): string[] {
    return calls
      .map((args) => args[0])
      .filter((sql): sql is string => typeof sql === 'string')
      .filter((sql) => sql.includes('FROM session'))
  }

  /** A whole-payload select: the bare `payload` column or `*` off `session`. */
  function selectsWholePayload(sql: string): boolean {
    return /SELECT\s+payload\b/i.test(sql) || /SELECT\s+\*/i.test(sql)
  }

  it('serves the timeline byte-identical to the payload timeline', async () => {
    // Characterization: passes today (same blob, same bytes) and must keep
    // passing after GREEN — the client is unchanged, so the bytes are the
    // contract. Raw text, not parsed JSON, because the guarantee is bytes.
    const res = await fetch(`${base}/api/kyber/timeline?id=sess-timeline-1`)
    expect(res.status).toBe(200)
    expect(res.headers.get('content-type')).toContain('application/json')
    expect(res.headers.get('cache-control')).toBe('no-store')
    expect(await res.text()).toBe(JSON.stringify(TIMELINE))
  })

  it('serves the timeline without selecting the session whole payload blob', async () => {
    // RED: the route serves `getSessionPayload`, which issues
    // `SELECT payload FROM session`. After GREEN no `FROM session` statement
    // may return the blob. The pin forbids returning the blob, not any one
    // implementation (a narrow timeline column and a `json_extract` both
    // satisfy it).
    const spy = vi.spyOn(canonDb, 'prepare')
    try {
      const res = await fetch(`${base}/api/kyber/timeline?id=sess-timeline-1`)
      expect(res.status).toBe(200)
    } finally {
      const calls = [...spy.mock.calls]
      spy.mockRestore()
      const probed = sessionStatements(calls)
      expect(probed.length).toBeGreaterThan(0)
      expect(probed.filter(selectsWholePayload)).toEqual([])
    }
  })

  it('still 404s with the same body for a missing session', async () => {
    // Characterization: the route's error behavior for a missing session is
    // unchanged by the read-path swap.
    const res = await fetch(`${base}/api/kyber/timeline?id=sess-does-not-exist`)
    expect(res.status).toBe(404)
    expect(await res.json()).toEqual({ error: 'Session not found' })
  })

  it('still 404s when the store holds no sessions at all', async () => {
    // Characterization: the no-id, no-session branch answers
    // `{ error: 'No sessions available' }` without touching a payload.
    const emptyDb = new DatabaseSync(':memory:')
    const emptyBridge = new KyberBridge({ canonDb: emptyDb })
    try {
      let status = 0
      let body = ''
      const req = { method: 'GET' } as IncomingMessage
      const res = {
        writeHead: (code: number) => {
          status = code
        },
        end: (data: string) => {
          body = data
        },
      } as unknown as ServerResponse
      const handled = handleKyberRequest(
        req,
        res,
        new URL('http://127.0.0.1:4747/api/kyber/timeline'),
        emptyBridge,
      )
      expect(handled).toBe(true)
      expect(status).toBe(404)
      expect(JSON.parse(body)).toEqual({ error: 'No sessions available' })
    } finally {
      emptyBridge.close()
      try {
        emptyDb.close()
      } catch {
        // The bridge already closed the injected handle.
      }
    }
  })
})
