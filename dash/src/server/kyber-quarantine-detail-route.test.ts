import type { AddressInfo } from 'node:net'
import type { Server } from 'node:http'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { runWebDashboard } from '../cli/web.js'
import { KyberBridge } from './bridge.js'
import { CanonStore } from '../canon/store.js'

// Plan 2026-10-10, B7: the turn view links a Claude turn to the quarantined
// interaction span that kept the sender's raw packet. The quarantine list stays
// light and carries no payloads; this detail route is the only surface that
// serves one, and it answers an explicit null rather than inventing evidence for
// a row written before the column existed.
type QuarantineDetail = {
  spanId: string
  source: string | null
  name: string | null
  reason: string
  timestamp: string | null
  namespaces: string[]
  harness: string | null
  rawPayload: string | null
  evidenceAvailable: boolean
}

describe('GET /api/kyber/quarantine/:spanId', () => {
  let server: Server
  let base: string
  let store: CanonStore

  const PACKET = JSON.stringify({
    name: 'claude_code.interaction',
    attributes: { 'span.type': 'interaction', user_prompt: 'why is the Claude turn inspector empty?' },
  })

  beforeAll(async () => {
    store = new CanonStore(':memory:')

    // A v19 row that kept its packet: the detail route serves the evidence.
    // Assigned to a variable so the metadata travels structurally; the options
    // object gains rawPayload/harness with T14.
    const withPayload = {
      source: 'claude-code',
      name: 'claude_code.interaction',
      timestamp: '2026-10-10T10:00:00.000Z',
      rawPayload: PACKET,
      harness: 'claude-code',
    }
    store.quarantineAndDelete('span-quarantine-with-payload', ['claude_code'], 'non-model span', withPayload)

    // A v19 audit row with no packet: the row exists, the evidence does not.
    store.quarantine('span-quarantine-no-payload', ['claude_code'], 'non-model span', {
      source: 'claude-code',
      name: 'claude_code.interaction',
      timestamp: '2026-10-10T10:01:00.000Z',
    })

    // No separate canonDb: the bridge shares the store's handle, so the list and
    // detail routes read the rows this test seeded.
    server = await runWebDashboard({
      port: 0,
      open: false,
      writeStdout: () => {},
      kyberBridge: new KyberBridge({
        store,
        ratesPath: join(tmpdir(), 'nonexistent-quarantine-detail-rates.json'),
      }),
    })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    store.close()
  })

  it('returns the B7 shape for a quarantined span that kept its raw packet', async () => {
    const res = await fetch(`${base}/api/kyber/quarantine/span-quarantine-with-payload`)
    expect(res.status).toBe(200)

    const body = (await res.json()) as QuarantineDetail
    expect(body).toEqual({
      spanId: 'span-quarantine-with-payload',
      source: 'claude-code',
      name: 'claude_code.interaction',
      reason: 'non-model span',
      timestamp: '2026-10-10T10:00:00.000Z',
      namespaces: ['claude_code'],
      harness: 'claude-code',
      rawPayload: PACKET,
      evidenceAvailable: true,
    })
  })

  it('returns the whole B7 shape with no evidence for an audit row that stored no packet', async () => {
    const res = await fetch(`${base}/api/kyber/quarantine/span-quarantine-no-payload`)
    expect(res.status).toBe(200)

    const body = (await res.json()) as QuarantineDetail
    expect(body).toEqual({
      spanId: 'span-quarantine-no-payload',
      source: 'claude-code',
      name: 'claude_code.interaction',
      reason: 'non-model span',
      timestamp: '2026-10-10T10:01:00.000Z',
      namespaces: ['claude_code'],
      harness: null,
      rawPayload: null,
      evidenceAvailable: false,
    })
  })

  it('404s for an id that names no quarantine row', async () => {
    const res = await fetch(`${base}/api/kyber/quarantine/span-does-not-exist`)
    expect(res.status).toBe(404)
  })

  it('405s for a non-GET method', async () => {
    const res = await fetch(`${base}/api/kyber/quarantine/span-quarantine-with-payload`, { method: 'POST' })
    expect(res.status).toBe(405)
  })

  it('400s with the missing-id JSON error for an empty or whitespace id, never the catch-all 404', async () => {
    // B7: an empty id is a caller error, not an unknown row. The precedence
    // guard at routes.ts:1693 answers every unhandled /api/kyber/* path with
    // `{"error":"Not found"}` (404), so asserting the body — not just the
    // status — proves the detail route owns this answer rather than the
    // catch-all. Same shape as the span-attributes route's 'Missing span id'.
    for (const path of ['/api/kyber/quarantine/', '/api/kyber/quarantine/%20']) {
      const res = await fetch(`${base}${path}`)
      expect(res.status).toBe(400)
      const body = (await res.json()) as { error: string }
      expect(body).toEqual({ error: 'Missing span id' })
    }
  })

  it('leaves the list route light: entries carry no payload', async () => {
    const res = await fetch(`${base}/api/kyber/quarantine`)
    expect(res.status).toBe(200)

    const body = (await res.json()) as { entries: Array<Record<string, unknown>>; total: number }
    expect(body.total).toBe(2)
    expect(body.entries).toHaveLength(2)
    // B7: the list stays light — no payload crosses this route.
    expect(JSON.stringify(body)).not.toContain(PACKET)
    expect(body.entries[0]).not.toHaveProperty('rawPayload')
  })
})

// A pre-v18 row is not a v19 row with a null option — it is a row that lived in
// a store whose quarantine table had neither column. Only a store that predates
// the migration can prove the route reads such a row as `harness: null,
// rawPayload: null, evidenceAvailable: false` instead of inventing evidence.
describe('GET /api/kyber/quarantine/:spanId (store written before v18)', () => {
  let server: Server
  let base: string
  let store: CanonStore
  const dirs: string[] = []

  /** A frozen v17 quarantine table: no `raw_payload`, no `harness`. */
  const V17_QUARANTINE_SQL = `
CREATE TABLE quarantine (
  span_id TEXT PRIMARY KEY,
  namespaces TEXT NOT NULL,
  reason TEXT NOT NULL,
  source TEXT,
  name TEXT,
  timestamp TEXT
);
`

  function frozenV17StoreAt(): string {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-quarantine-detail-v17-'))
    dirs.push(dir)
    const path = join(dir, 'canon.db')
    new CanonStore(path).close()

    const db = new DatabaseSync(path)
    db.exec('DROP TABLE quarantine')
    db.exec(V17_QUARANTINE_SQL)
    db.prepare('UPDATE metadata SET value = ? WHERE key = ?').run('17', 'schema_version')
    db.prepare(
      `INSERT INTO quarantine (span_id, namespaces, reason, source, name, timestamp)
       VALUES (?, ?, ?, ?, ?, ?)`,
    ).run(
      'span-pre-v18',
      JSON.stringify(['pi']),
      'unresolved parent span',
      'pi',
      'llm_request',
      '2026-09-01T00:00:00.000Z',
    )
    db.close()
    return path
  }

  beforeAll(async () => {
    store = new CanonStore(frozenV17StoreAt())

    server = await runWebDashboard({
      port: 0,
      open: false,
      writeStdout: () => {},
      kyberBridge: new KyberBridge({
        store,
        ratesPath: join(tmpdir(), 'nonexistent-quarantine-detail-v17-rates.json'),
      }),
    })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    store.close()
    while (dirs.length > 0) rmSync(dirs.pop()!, { recursive: true, force: true })
  })

  it('reads a pre-v18 row as the whole B7 shape with harness null and no evidence', async () => {
    const res = await fetch(`${base}/api/kyber/quarantine/span-pre-v18`)
    expect(res.status).toBe(200)

    const body = (await res.json()) as QuarantineDetail
    expect(body).toEqual({
      spanId: 'span-pre-v18',
      source: 'pi',
      name: 'llm_request',
      reason: 'unresolved parent span',
      timestamp: '2026-09-01T00:00:00.000Z',
      namespaces: ['pi'],
      harness: null,
      rawPayload: null,
      evidenceAvailable: false,
    })
  })
})
