import { appendFileSync, mkdtempSync, rmSync, unlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import { STORE_LOCK_FILE } from '../refresh/lock.js'
import { runTranscriptSync } from './transcript-sync.js'

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

afterEach(async () => {
  while (openStores.length > 0) {
    const store = openStores.pop()
    store?.close()
  }
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()
    if (dir) rmSync(dir, { recursive: true, force: true })
  }
})

describe('transcript synchronization engine job (dash/src/sync/transcript-sync.ts)', () => {
  it('increments transcript offset to EOF and stamps synced status and timestamp on delta parse', async () => {
    const dir = createTempDir('kyber-sync-tail-')
    const store = createStore(dir)
    const transcriptPath = join(dir, 'transcript-tail.jsonl')

    const firstChunk =
      JSON.stringify({
        type: 'message',
        id: 'msg_001',
        requestId: 'req_001',
        role: 'user',
        content: 'Initial user prompt',
      }) +
      '\n' +
      JSON.stringify({
        type: 'message',
        id: 'msg_002',
        requestId: 'req_001',
        role: 'assistant',
        content: 'Initial model response',
      }) +
      '\n'
    writeFileSync(transcriptPath, firstChunk, 'utf-8')
    const firstSize = Buffer.byteLength(firstChunk, 'utf-8')

    const db = store.getDatabase()
    db.prepare(
      `INSERT INTO session (
        session_id, harness, transcript_path, transcript_offset, sync_status, payload
      ) VALUES (?, ?, ?, ?, ?, ?)`,
    ).run('sess-tail-1', 'claude-code', transcriptPath, 0, 'pending', '{}')

    const t1 = 1770000000000
    const pass1 = await runTranscriptSync({
      store,
      stateDir: dir,
      clock: { now: () => t1 },
    })

    expect(pass1.outcome).toBe('completed')

    const rowAfterPass1 = db
      .prepare(
        `SELECT session_id, transcript_offset, synced_at, sync_status FROM session WHERE session_id = ?`,
      )
      .get('sess-tail-1') as {
        session_id: string
        transcript_offset: number
        synced_at: string | null
        sync_status: string
      }

    expect(rowAfterPass1.transcript_offset).toBe(firstSize)
    expect(rowAfterPass1.synced_at).toBe(new Date(t1).toISOString())
    expect(rowAfterPass1.sync_status).toBe('synced')

    // Append subsequent delta bytes to transcript file
    const secondChunk =
      JSON.stringify({
        type: 'message',
        id: 'msg_003',
        requestId: 'req_002',
        role: 'user',
        content: 'Follow-up turn question',
      }) + '\n'
    appendFileSync(transcriptPath, secondChunk, 'utf-8')
    const totalSize = firstSize + Buffer.byteLength(secondChunk, 'utf-8')

    const t2 = t1 + 60_000
    const pass2 = await runTranscriptSync({
      store,
      stateDir: dir,
      clock: { now: () => t2 },
    })

    expect(pass2.outcome).toBe('completed')

    const rowAfterPass2 = db
      .prepare(
        `SELECT session_id, transcript_offset, synced_at, sync_status FROM session WHERE session_id = ?`,
      )
      .get('sess-tail-1') as {
        session_id: string
        transcript_offset: number
        synced_at: string | null
        sync_status: string
      }

    expect(rowAfterPass2.transcript_offset).toBe(totalSize)
    expect(rowAfterPass2.synced_at).toBe(new Date(t2).toISOString())
    expect(rowAfterPass2.sync_status).toBe('synced')
  })

  it('enriches session records with transcript content via request_id twin-join', async () => {
    const dir = createTempDir('kyber-sync-twin-')
    const store = createStore(dir)
    const transcriptPath = join(dir, 'transcript-twin.jsonl')

    const sharedRequestId = 'req_twin_join_alpha_99'
    const transcriptLines =
      JSON.stringify({
        type: 'message',
        id: 'msg_join_1',
        requestId: sharedRequestId,
        role: 'user',
        content: 'Calculate Fibonacci(10)',
      }) +
      '\n' +
      JSON.stringify({
        type: 'message',
        id: 'msg_join_2',
        requestId: sharedRequestId,
        role: 'assistant',
        content: 'Fibonacci(10) is 55.',
      }) +
      '\n'
    writeFileSync(transcriptPath, transcriptLines, 'utf-8')

    const otelRecord: CanonicalRecord = {
      spanId: 'span-otel-twin-join-1',
      traceId: 'trace-join-1234567890abcdef',
      parentSpanId: null,
      source: 'claude-code',
      harness: 'claude-code',
      sessionId: 'sess-join-42',
      name: 'llm_request',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-10-14T12:00:00.000Z',
      durationMs: 850,
      status: 'ok',
      tokens: {
        freshInput: 40,
        cacheRead: 10,
        cacheCreation: 0,
        output: 15,
        reportedInput: 50,
        reportedOutput: 15,
      },
      content: {},
      cost: { basis: 'unknown', status: 'no_rate' },
      raw: {
        'session.id': 'sess-join-42',
        request_id: sharedRequestId,
      },
    }
    store.upsert(otelRecord)

    const db = store.getDatabase()
    db.prepare(
      `INSERT INTO session (
        session_id, harness, transcript_path, transcript_offset, sync_status, payload
      ) VALUES (?, ?, ?, ?, ?, ?)`,
    ).run('sess-join-42', 'claude-code', transcriptPath, 0, 'pending', '{}')

    const syncTime = new Date('2026-10-14T12:05:00.000Z').getTime()
    const outcome = await runTranscriptSync({
      store,
      stateDir: dir,
      clock: { now: () => syncTime },
    })

    expect(outcome.outcome).toBe('completed')

    const enrichedRecord = store.get('span-otel-twin-join-1')
    expect(enrichedRecord).toBeDefined()
    const hasConversationHistory =
      Boolean(enrichedRecord?.content.conversation_history) ||
      (enrichedRecord?.parts !== undefined && enrichedRecord.parts.length > 0)
    expect(hasConversationHistory).toBe(true)

    const sessionRow = db
      .prepare(`SELECT sync_status, synced_at FROM session WHERE session_id = ?`)
      .get('sess-join-42') as { sync_status: string; synced_at: string | null }
    expect(sessionRow.sync_status).toBe('synced')
    expect(sessionRow.synced_at).toBe(new Date(syncTime).toISOString())
  })

  it('transitions inactive unchanged sessions to settled with timestamp after two consecutive passes', async () => {
    const dir = createTempDir('kyber-sync-settle-')
    const store = createStore(dir)
    const transcriptPath = join(dir, 'transcript-settle.jsonl')

    writeFileSync(
      transcriptPath,
      JSON.stringify({
        type: 'message',
        id: 'msg_settle_01',
        requestId: 'req_settle_01',
        role: 'user',
        content: 'Final prompt before inactivity',
      }) + '\n',
      'utf-8',
    )

    const lastActivityTime = new Date('2026-10-14T10:00:00.000Z').getTime()
    const db = store.getDatabase()
    db.prepare(
      `INSERT INTO session (
        session_id, harness, transcript_path, transcript_offset, sync_status, ended, payload
      ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      'sess-settle-1',
      'claude-code',
      transcriptPath,
      0,
      'pending',
      new Date(lastActivityTime).toISOString(),
      '{}',
    )

    // Pass 1: Inactive for > 30 min (31 minutes later). First pass processes transcript.
    const tPass1 = lastActivityTime + 31 * 60_000
    const outcome1 = await runTranscriptSync({
      store,
      stateDir: dir,
      clock: { now: () => tPass1 },
    })
    expect(outcome1.outcome).toBe('completed')

    const rowPass1 = db
      .prepare(`SELECT sync_status, settled_at FROM session WHERE session_id = ?`)
      .get('sess-settle-1') as { sync_status: string; settled_at: string | null }
    expect(rowPass1.sync_status).toBe('synced')
    expect(rowPass1.settled_at).toBeNull()

    // Pass 2: Inactive for > 30 min and unchanged for 2 consecutive passes -> transitions to 'settled'.
    const tPass2 = tPass1 + 60_000
    const outcome2 = await runTranscriptSync({
      store,
      stateDir: dir,
      clock: { now: () => tPass2 },
    })
    expect(outcome2.outcome).toBe('completed')

    const rowPass2 = db
      .prepare(`SELECT sync_status, settled_at FROM session WHERE session_id = ?`)
      .get('sess-settle-1') as { sync_status: string; settled_at: string | null }
    expect(rowPass2.sync_status).toBe('settled')
    expect(rowPass2.settled_at).toBe(new Date(tPass2).toISOString())
  })

  it('transitions sessions with missing transcript files to unavailable after 24 hours', async () => {
    const dir = createTempDir('kyber-sync-missing-')
    const store = createStore(dir)
    const missingTranscriptPath = join(dir, 'does-not-exist.jsonl')

    const sessionEndedTime = new Date('2026-10-10T08:00:00.000Z').getTime()
    const db = store.getDatabase()
    db.prepare(
      `INSERT INTO session (
        session_id, harness, transcript_path, transcript_offset, sync_status, ended, payload
      ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      'sess-missing-1',
      'claude-code',
      missingTranscriptPath,
      0,
      'pending',
      new Date(sessionEndedTime).toISOString(),
      '{}',
    )

    // Clock is > 24 hours past session end (25 hours later)
    const tMissing = sessionEndedTime + 25 * 3600_000
    const outcome = await runTranscriptSync({
      store,
      stateDir: dir,
      clock: { now: () => tMissing },
    })
    expect(outcome.outcome).toBe('completed')

    const rowMissing = db
      .prepare(`SELECT sync_status, settled_at FROM session WHERE session_id = ?`)
      .get('sess-missing-1') as { sync_status: string; settled_at: string | null }
    expect(rowMissing.sync_status).toBe('unavailable')
  })

  it('yields and leaves pending state untouched when store refresh lock is held', async () => {
    const dir = createTempDir('kyber-sync-lock-')
    const store = createStore(dir)
    const transcriptPath = join(dir, 'transcript-locked.jsonl')

    writeFileSync(
      transcriptPath,
      JSON.stringify({
        type: 'message',
        id: 'msg_locked_1',
        requestId: 'req_locked_1',
        role: 'user',
        content: 'Content waiting for sync',
      }) + '\n',
      'utf-8',
    )

    const db = store.getDatabase()
    db.prepare(
      `INSERT INTO session (
        session_id, harness, transcript_path, transcript_offset, sync_status, payload
      ) VALUES (?, ?, ?, ?, ?, ?)`,
    ).run('sess-locked-1', 'claude-code', transcriptPath, 0, 'pending', '{}')

    // Another live process (the parent) holds the store refresh lock. A holder in this very
    // process would queue the sync behind it rather than report busy, so it is not used here.
    const lockPath = join(dir, STORE_LOCK_FILE)
    writeFileSync(lockPath, JSON.stringify({ pid: process.ppid, token: 'other', at: Date.now() }))

    try {
      // Sync engine job runs while store refresh lock is held -> must yield
      const syncResult = await runTranscriptSync({
        store,
        stateDir: dir,
        clock: { now: () => Date.now() },
      })

      expect(syncResult.outcome).toBe('busy')

      // Pending state must be completely untouched
      const rowWhileLocked = db
        .prepare(
          `SELECT transcript_offset, sync_status, synced_at FROM session WHERE session_id = ?`,
        )
        .get('sess-locked-1') as {
          transcript_offset: number
          sync_status: string
          synced_at: string | null
        }
      expect(rowWhileLocked.transcript_offset).toBe(0)
      expect(rowWhileLocked.sync_status).toBe('pending')
      expect(rowWhileLocked.synced_at).toBeNull()
    } finally {
      unlinkSync(lockPath)
    }

    // Now that lock is released, sync engine job proceeds cleanly
    const postReleaseSync = await runTranscriptSync({
      store,
      stateDir: dir,
      clock: { now: () => Date.now() },
    })

    expect(postReleaseSync.outcome).toBe('completed')

    const rowAfterRelease = db
      .prepare(
        `SELECT transcript_offset, sync_status, synced_at FROM session WHERE session_id = ?`,
      )
      .get('sess-locked-1') as {
        transcript_offset: number
        sync_status: string
        synced_at: string | null
      }
    expect(rowAfterRelease.transcript_offset).toBeGreaterThan(0)
    expect(rowAfterRelease.sync_status).toBe('synced')
    expect(rowAfterRelease.synced_at).not.toBeNull()
  })
})
