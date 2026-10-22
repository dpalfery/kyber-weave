// Review follow-ups for transcript sync (PR 367): the offset only advances over bytes that
// were read and parsed as whole lines, and the store lock is acquired, not just peeked at.

import { appendFileSync, mkdtempSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import { STORE_LOCK_FILE } from '../refresh/lock.js'
import { runTranscriptSync } from './transcript-sync.js'

const tempDirs: string[] = []
const openStores: CanonStore[] = []

afterEach(() => {
  while (openStores.length > 0) openStores.pop()?.close()
  while (tempDirs.length > 0) rmSync(tempDirs.pop()!, { recursive: true, force: true })
})

function setup(): { dir: string; store: CanonStore } {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-sync-review-'))
  tempDirs.push(dir)
  const store = new CanonStore(join(dir, 'canon.db'))
  openStores.push(store)
  return { dir, store }
}

function record(spanId: string, requestId: string, sessionId: string): CanonicalRecord {
  return {
    spanId,
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    source: 'claude-code',
    harness: 'claude-code',
    sessionId,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-10-14T12:00:00.000Z',
    durationMs: 10,
    status: 'ok',
    tokens: { freshInput: 1, cacheRead: 0, cacheCreation: 0, output: 1, reportedInput: 1, reportedOutput: 1 },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: { request_id: requestId },
  }
}

const line = (requestId: string, content: string): string =>
  JSON.stringify({ type: 'message', requestId, role: 'user', content })

const history = (store: CanonStore, spanId: string): string =>
  String(store.get(spanId)?.content.conversation_history ?? '')

const offsetOf = (store: CanonStore, sessionId: string): number =>
  (store.getDatabase().prepare('SELECT transcript_offset AS o FROM session WHERE session_id = ?').get(sessionId) as {
    o: number
  }).o

describe('transcript sync offsets', () => {
  it('leaves a torn trailing line for the next pass, including one cut inside a multibyte character', async () => {
    const { dir, store } = setup()
    const path = join(dir, 't.jsonl')
    const whole = `${line('req_a', 'first turn')}\n`
    const second = line('req_b', 'second turn 🎉 done')
    const secondBytes = Buffer.from(second, 'utf-8')
    // Cut inside the 4-byte emoji, so the tail is neither a whole line nor valid UTF-8.
    const cut = secondBytes.indexOf(Buffer.from('🎉')) + 2
    writeFileSync(path, Buffer.concat([Buffer.from(whole, 'utf-8'), secondBytes.subarray(0, cut)]))

    store.upsertMany([record('span-a', 'req_a', 'sess-torn'), record('span-b', 'req_b', 'sess-torn')])
    store
      .getDatabase()
      .prepare(
        `INSERT INTO session (session_id, harness, transcript_path, transcript_offset, sync_status, payload)
         VALUES (?, ?, ?, ?, ?, ?)`,
      )
      .run('sess-torn', 'claude-code', path, 0, 'pending', '{}')

    await runTranscriptSync({ store, stateDir: dir, clock: { now: () => 1770000000000 } })

    expect(history(store, 'span-a')).toContain('first turn')
    expect(history(store, 'span-b')).toBe('')
    expect(offsetOf(store, 'sess-torn')).toBe(Buffer.byteLength(whole, 'utf-8'))

    appendFileSync(path, Buffer.concat([secondBytes.subarray(cut), Buffer.from('\n')]))
    await runTranscriptSync({ store, stateDir: dir, clock: { now: () => 1770000060000 } })

    expect(history(store, 'span-b')).toContain('second turn 🎉 done')
    expect(offsetOf(store, 'sess-torn')).toBe(statSync(path).size)
  })
})

describe('transcript sync and the store refresh lock', () => {
  const seed = (store: CanonStore, dir: string, sessionId: string): void => {
    const path = join(dir, `${sessionId}.jsonl`)
    writeFileSync(path, `${line('req_l', 'locked turn')}\n`)
    store.upsertMany([record(`span-${sessionId}`, 'req_l', sessionId)])
    store
      .getDatabase()
      .prepare(
        `INSERT INTO session (session_id, harness, transcript_path, transcript_offset, sync_status, payload)
         VALUES (?, ?, ?, ?, ?, ?)`,
      )
      .run(sessionId, 'claude-code', path, 0, 'pending', '{}')
  }

  it('is busy while a live process holds the lock', async () => {
    const { dir, store } = setup()
    seed(store, dir, 'sess-live')
    // The parent process is alive and is not this one.
    writeFileSync(join(dir, STORE_LOCK_FILE), JSON.stringify({ pid: process.ppid, token: 'x', at: Date.now() }))

    const outcome = await runTranscriptSync({ store, stateDir: dir, clock: { now: () => Date.now() } })

    expect(outcome.outcome).toBe('busy')
    expect(offsetOf(store, 'sess-live')).toBe(0)
  })

  it('is not blocked forever by an abandoned lock', async () => {
    const { dir, store } = setup()
    seed(store, dir, 'sess-stale')
    const lockPath = join(dir, STORE_LOCK_FILE)
    writeFileSync(lockPath, JSON.stringify({ pid: 2_147_483_646, token: 'dead', at: Date.now() - 3_600_000 }))
    const old = new Date(Date.now() - 3_600_000)
    const { utimesSync } = await import('node:fs')
    utimesSync(lockPath, old, old)

    const outcome = await runTranscriptSync({ store, stateDir: dir, clock: { now: () => Date.now() } })

    expect(outcome.outcome).toBe('completed')
    expect(history(store, 'span-sess-stale')).toContain('locked turn')
  })

  it('holds the lock while it syncs and releases it afterwards', async () => {
    const { dir, store } = setup()
    seed(store, dir, 'sess-held')

    await runTranscriptSync({ store, stateDir: dir, clock: { now: () => Date.now() } })

    const { existsSync } = await import('node:fs')
    expect(existsSync(join(dir, STORE_LOCK_FILE))).toBe(false)
  })
})
