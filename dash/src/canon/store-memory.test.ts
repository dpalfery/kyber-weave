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
      expect(report.residentGrowthBytes).toBeLessThan(report.bytesRead / 2)
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
