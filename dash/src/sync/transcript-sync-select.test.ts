import { mkdtempSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { runTranscriptSync } from './transcript-sync.js'

// RED contract for Change B: the sync job's unsettled-rows SELECT must stop
// fetching the `payload` column — a blob of up to 15.3 MB per session that the
// sync loop never reads. The SELECT's row set, the columns it stamps, and the
// values it writes are unchanged; only the unread blob leaves the column list.

const tempDirs: string[] = []
const openStores: CanonStore[] = []

function createTempDir(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  tempDirs.push(dir)
  return dir
}

function createStore(dir: string): CanonStore {
  const store = new CanonStore(join(dir, 'canon.db'))
  openStores.push(store)
  return store
}

afterEach(() => {
  while (openStores.length > 0) {
    const store = openStores.pop()
    store?.close()
  }
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()
    if (dir) rmSync(dir, { recursive: true, force: true })
  }
})

function writeTranscript(dir: string, name: string, lines: unknown[]): string {
  const path = join(dir, name)
  writeFileSync(path, `${lines.map((line) => JSON.stringify(line)).join('\n')}\n`, 'utf-8')
  return path
}

function messageLine(requestId: string, role: string, text: string): unknown {
  return { type: 'message', requestId, role, content: text }
}

function insertSyncRow(
  store: CanonStore,
  row: {
    id: string
    transcriptPath: string | null
    offset?: number
    status?: string | null
    payload?: string
    started?: string | null
    ended?: string | null
  },
): void {
  store
    .getDatabase()
    .prepare(
      `INSERT INTO session (
        session_id, harness, transcript_path, transcript_offset, sync_status, payload, started, ended
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    )
    .run(
      row.id,
      'claude-code',
      row.transcriptPath,
      row.offset ?? 0,
      row.status ?? null,
      row.payload ?? '{}',
      row.started ?? null,
      row.ended ?? null,
    )
}

function sessionStatements(calls: readonly unknown[][]): string[] {
  return calls
    .map((args) => args[0])
    .filter((sql): sql is string => typeof sql === 'string')
    .filter((sql) => sql.includes('FROM session'))
}

function syncRow(store: CanonStore, sessionId: string): Record<string, unknown> {
  return store
    .getDatabase()
    .prepare('SELECT * FROM session WHERE session_id = ?')
    .get(sessionId) as Record<string, unknown>
}

describe('transcript sync unsettled-rows SELECT (payload-independence follow-up)', () => {
  it('selects unsettled sessions without naming the payload blob', async () => {
    // RED for Change B: the SELECT over unsettled rows names the `payload`
    // column today, fetching a blob the loop never reads. After the change no
    // issued `FROM session` statement names it.
    const dir = createTempDir('kyber-sync-select-')
    const store = createStore(dir)
    const transcriptPath = writeTranscript(dir, 't.jsonl', [messageLine('req_1', 'user', 'hello')])
    insertSyncRow(store, { id: 'sess-sync-1', transcriptPath, status: 'pending' })

    const prepareSpy = vi.spyOn(store.getDatabase(), 'prepare')
    const outcome = await runTranscriptSync({ store, stateDir: dir, clock: { now: () => 1770000000000 } })
    const calls = [...prepareSpy.mock.calls]
    prepareSpy.mockRestore()

    expect(outcome.outcome).toBe('completed')
    const statements = sessionStatements(calls)
    expect(statements.length).toBeGreaterThan(0)
    expect(statements.filter((sql) => sql.includes('payload'))).toEqual([])
  })

  it('still stamps the same sync columns and values for a synced row', async () => {
    // The observable outcome the change must not move: a pending row with new
    // transcript bytes advances `transcript_offset` to EOF and is stamped
    // `synced` with the pass timestamp, while every other column — including
    // the now-unfetched `payload` — is byte-identical. Passes before and
    // after the change (characterization guard).
    const dir = createTempDir('kyber-sync-outcome-')
    const store = createStore(dir)
    const transcriptPath = writeTranscript(dir, 't.jsonl', [messageLine('req_1', 'user', 'hello')])
    const payloadText = JSON.stringify({ summary: { turn_count: 3 } })
    insertSyncRow(store, { id: 'sess-sync-2', transcriptPath, status: 'pending', payload: payloadText })

    const now = 1770000000000
    const outcome = await runTranscriptSync({ store, stateDir: dir, clock: { now: () => now } })
    expect(outcome.outcome).toBe('completed')

    const after = syncRow(store, 'sess-sync-2')
    expect(after).toMatchObject({
      session_id: 'sess-sync-2',
      harness: 'claude-code',
      transcript_path: transcriptPath,
      transcript_offset: statSync(transcriptPath).size,
      synced_at: new Date(now).toISOString(),
      sync_status: 'synced',
      payload: payloadText,
      started: null,
      ended: null,
    })
  })

  it('picks up the same rows: pending and NULL statuses sync, settled and unavailable are left alone', async () => {
    // The `sync_status` predicate is untouched by the change: rows with
    // `pending` or NULL status and a readable transcript sync; `settled` and
    // `unavailable` rows are not selected and stay byte-identical; a pending
    // row whose transcript is missing long after its session ended flips to
    // `unavailable`. Passes before and after the change (characterization
    // guard).
    const dir = createTempDir('kyber-sync-selection-')
    const store = createStore(dir)
    const now = 1770000000000
    const transcriptA = writeTranscript(dir, 'a.jsonl', [messageLine('req_a', 'user', 'alpha')])
    const transcriptB = writeTranscript(dir, 'b.jsonl', [messageLine('req_b', 'user', 'beta')])
    const settledTranscript = writeTranscript(dir, 's.jsonl', [messageLine('req_s', 'user', 'done')])
    const settledOffset = statSync(settledTranscript).size
    insertSyncRow(store, { id: 'sess-pending', transcriptPath: transcriptA, status: 'pending' })
    insertSyncRow(store, { id: 'sess-null', transcriptPath: transcriptB, status: null })
    insertSyncRow(store, {
      id: 'sess-settled',
      transcriptPath: settledTranscript,
      offset: settledOffset,
      status: 'settled',
      ended: new Date(now).toISOString(),
    })
    insertSyncRow(store, { id: 'sess-unavailable', transcriptPath: settledTranscript, status: 'unavailable' })
    insertSyncRow(store, {
      id: 'sess-stale',
      transcriptPath: join(dir, 'missing.jsonl'),
      status: 'pending',
      ended: '2020-01-01T00:00:00.000Z',
    })

    const beforeSettled = syncRow(store, 'sess-settled')
    const beforeUnavailable = syncRow(store, 'sess-unavailable')

    const outcome = await runTranscriptSync({ store, stateDir: dir, clock: { now: () => now } })
    expect(outcome.outcome).toBe('completed')

    const iso = new Date(now).toISOString()
    expect(syncRow(store, 'sess-pending')).toMatchObject({
      transcript_offset: statSync(transcriptA).size,
      synced_at: iso,
      sync_status: 'synced',
    })
    expect(syncRow(store, 'sess-null')).toMatchObject({
      transcript_offset: statSync(transcriptB).size,
      synced_at: iso,
      sync_status: 'synced',
    })
    expect(syncRow(store, 'sess-settled')).toEqual(beforeSettled)
    expect(syncRow(store, 'sess-unavailable')).toEqual(beforeUnavailable)
    expect(syncRow(store, 'sess-stale')).toMatchObject({ sync_status: 'unavailable', transcript_offset: 0 })
  })
})
