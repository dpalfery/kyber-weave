// The measurement that decides whether a store handle or the JavaScript heap
// holds a process's resident memory.
//
// WHY this is a test and not a one-off observation: the engine's RSS was
// 2.4 GB and the two answers needed different responses. A heap that holds
// the memory is a defect; a store's file pages are the kernel's page cache,
// which the kernel reclaims under pressure, and are not a defect. Only a
// measurement tells them apart, and a measurement that is not written down
// cannot be repeated when the next person asks the same question.
//
// WHY the diagnostic measures a difference rather than an absolute: there is
// no second process to compare against, so the store's contribution is the
// resident growth that opening and reading the store causes, and the heap's
// contribution is the part of that growth the heap itself explains. What is
// left over is what the store's file pages cost this process.

import { describe, expect, it } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'

import { CanonStore } from './store.js'

/** A scratch store carrying a known number of payload bytes. */
function scratchStore(tag: string, payloadBytes: number): string {
  const path = join(mkdtempSync(join(tmpdir(), `kyberdash-mem-${tag}-`)), 'canon.db')
  const store = new CanonStore(path)
  store.close()
  const db = new DatabaseSync(path)
  db.exec('PRAGMA journal_mode = WAL')
  db.prepare('INSERT INTO quarantine (span_id, namespaces, reason, raw_payload) VALUES (?, ?, ?, ?)').run(
    'span-payload',
    '["gen_ai"]',
    'unclaimed',
    'x'.repeat(payloadBytes),
  )
  db.close()
  return path
}

describe('the store memory attribution', () => {
  it('samples this process\'s memory', async () => {
    const { sampleProcessMemory } = await import('./store-memory.js')

    const sample = sampleProcessMemory()

    expect(sample.rssBytes).toBeGreaterThan(0)
    expect(sample.heapUsedBytes).toBeGreaterThanOrEqual(0)
    expect(sample.heapTotalBytes).toBeGreaterThanOrEqual(0)
    expect(sample.externalBytes).toBeGreaterThanOrEqual(0)
  })

  it('reports the heap-versus-file-backed split for a store handle', async () => {
    const { attributeStoreMemory } = await import('./store-memory.js')
    const path = scratchStore('split', 16 * 1024 * 1024)
    try {
      const report = attributeStoreMemory(path)

      // It read the store's payload.
      expect(report.bytesRead).toBeGreaterThanOrEqual(16 * 1024 * 1024)
      expect(report.storeBytes).toBeGreaterThan(0)

      // The store is not mapped into this process, so its pages are the
      // kernel's page cache rather than this process's resident memory.
      expect(report.mmapSizeBytes).toBe(0)
      expect(report.fileBackedBytes).toBe(0)
      expect(report.holder).toBe('heap')

      // And the measurement says so: the store's bytes were read without
      // becoming resident memory. Reading a store is not what makes a
      // process big.
      //
      // Resident-growth accounting is kernel-specific: darwin leaves clean
      // file pages out of RSS (a 235 MB mapped-and-read store reported 20 MB
      // resident), while linux charges the faulted pages (CI measured 16.5 MB
      // of growth for this 16 MB read). The bound below therefore only holds
      // where the kernel accounts it that way; elsewhere the portable
      // invariants above — the bytes read, the holder, no file-backed charge
      // — are the contract.
      if (process.platform === 'darwin') {
        expect(report.residentGrowthBytes).toBeLessThan(report.bytesRead / 2)
      }
    } finally {
      rmSync(join(path, '..'), { recursive: true, force: true })
    }
  })

  it('reports a mapped store as the holder, so the two cases cannot be confused', async () => {
    const { attributeStoreMemory } = await import('./store-memory.js')
    const path = scratchStore('mapped', 16 * 1024 * 1024)
    try {
      // The same store, mapped into this process instead of read. This is
      // the case the question asks about: a store whose file pages are the
      // process's own.
      const report = attributeStoreMemory(path, { mmap: true })

      expect(report.mmapSizeBytes).toBeGreaterThan(0)
      expect(report.fileBackedBytes).toBe(report.storeBytes)
      expect(report.holder).toBe('file')
    } finally {
      rmSync(join(path, '..'), { recursive: true, force: true })
    }
  })

  it('tells a heap allocation apart from a file-backed one', async () => {
    const { attributeMemory } = await import('./store-memory.js')

    // 24 MB of live JavaScript objects: the heap's growth explains the
    // resident growth, so the split names the heap and not the file.
    const held: number[] = []
    const report = attributeMemory(() => {
      for (let i = 0; i < 24 * 1024 * 1024; i += 1) held.push(i)
    })

    expect(held.length).toBe(24 * 1024 * 1024)
    expect(report.heapBytes).toBeGreaterThan(8 * 1024 * 1024)
    expect(report.fileBackedBytes).toBeLessThan(report.heapBytes)
    expect(report.holder).toBe('heap')
  })
})

describe('store memory bulk columns and byte sizes', () => {
  it('counts session payload and turn rows in bytesRead', async () => {
    const { attributeStoreMemory } = await import('./store-memory.js')
    const path = join(mkdtempSync(join(tmpdir(), 'kyberdash-mem-bulk-')), 'canon.db')
    const store = new CanonStore(path)
    store.close()
    const bulkPayload = JSON.stringify({ id: 'sess-bulk', bulk: 'x'.repeat(200_000) })
    const bulkTurn = JSON.stringify({ index: 0, text: 'y'.repeat(100_000) })
    const db = new DatabaseSync(path)
    try {
      db.exec(
        `CREATE TABLE IF NOT EXISTS session_turn (
           session_id TEXT NOT NULL,
           turn_index INTEGER NOT NULL,
           turn_json TEXT NOT NULL
         )`,
      )
      db.prepare(
        `INSERT INTO session (session_id, harness, label, payload) VALUES (?, ?, ?, ?)`,
      ).run('sess-bulk', 'claude-code', 'Session sess-bulk', bulkPayload)
      db.prepare(
        `INSERT INTO session_turn (session_id, turn_index, turn_json) VALUES (?, ?, ?)`,
      ).run('sess-bulk', 0, bulkTurn)
    } finally {
      db.close()
    }
    try {
      const report = attributeStoreMemory(path)
      // The session payload and its split turn row are the bulk post-v22;
      // a reader that skips them understates what reading the store faults in.
      expect(report.bytesRead).toBeGreaterThanOrEqual(
        Buffer.byteLength(bulkPayload, 'utf8') + Buffer.byteLength(bulkTurn, 'utf8'),
      )
    } finally {
      rmSync(join(path, '..'), { recursive: true, force: true })
    }
  })

  it('counts multibyte strings in utf8 bytes, not UTF-16 units', async () => {
    const { attributeStoreMemory } = await import('./store-memory.js')
    const path = join(mkdtempSync(join(tmpdir(), 'kyberdash-mem-cjk-')), 'canon.db')
    const store = new CanonStore(path)
    store.close()
    const db = new DatabaseSync(path)
    const cjk = 'あ'.repeat(10_000)
    const expected = Buffer.byteLength(cjk, 'utf8')
    expect(expected).toBeGreaterThan(cjk.length)
    try {
      db.prepare('INSERT INTO quarantine (span_id, namespaces, reason, raw_payload) VALUES (?, ?, ?, ?)').run(
        'span-cjk',
        '["gen_ai"]',
        'unclaimed',
        cjk,
      )
    } finally {
      db.close()
    }
    try {
      const report = attributeStoreMemory(path)
      expect(report.bytesRead).toBeGreaterThanOrEqual(expected)
    } finally {
      rmSync(join(path, '..'), { recursive: true, force: true })
    }
  })
})
