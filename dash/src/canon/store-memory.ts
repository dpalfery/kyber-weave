// Attributing a process's resident memory: the JavaScript heap, or the store's
// own file pages.
//
// WHY this exists: the engine's resident memory was 2.4 GB and the two
// answers needed opposite responses. A heap that holds the memory is a defect
// worth fixing. A store's file pages are the kernel's page cache — reclaimable
// under pressure, and not a defect. Only a measurement tells them apart.
//
// WHY the store's contribution is measured from the *mapping state* and not
// from the resident size: a file's clean pages are the operating system's
// page cache, and the resident size a process is charged for them depends on
// the platform. On macOS they are not counted in the resident size at all — a
// process that mapped and read a 235 MB database reported 20 MB of resident
// memory while `vmmap` counted the same 235 MB. So the resident size cannot
// answer "are the store's pages charged to this process"; the mapping state
// can. A store that is *read* rather than mapped leaves its pages in the
// kernel's cache entirely, and the process's own copy of the bytes is the
// heap's — which is the case this repository is in.
//
// WHY the split is a difference and not an absolute: there is no second
// process to compare this one against, so the store's contribution is the
// resident growth that reading it causes, and the heap's contribution is the
// part of that growth the heap itself explains.

import { statSync } from 'node:fs'
import { createRequire } from 'node:module'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

/** This process's memory, as Node reports it. */
export type MemorySample = {
  /** Resident set size: everything this process holds, heap and buffers alike. */
  rssBytes: number
  /** The JavaScript heap's live objects. */
  heapUsedBytes: number
  /** The JavaScript heap's committed size. */
  heapTotalBytes: number
  /** Memory held outside the JavaScript heap — buffers, and so on. */
  externalBytes: number
}

/** How a stretch of resident growth is accounted for. */
export type MemoryAttribution = {
  /** The sample taken before the measured work. */
  before: MemorySample
  /** The sample taken after it. */
  after: MemorySample
  /** How much the resident memory grew while the work ran. */
  residentGrowthBytes: number
  /** The resident growth the JavaScript heap accounts for. */
  heapBytes: number
  /**
   * The resident growth the JavaScript heap does not account for: the buffers
   * and the native allocations — SQLite's own page cache among them — that
   * the work made.
   */
  nonHeapBytes: number
  /**
   * What the store's own file pages are charging this process. Zero unless
   * the store is mapped into the process.
   */
  fileBackedBytes: number
  /** Which of the two holds the resident memory. */
  holder: 'file' | 'heap'
}

/** The attribution of a store handle, with the store's own numbers beside it. */
export type StoreMemoryAttribution = MemoryAttribution & {
  /** The store that was read. */
  path: string
  /** The store's size on disk, in bytes. */
  storeBytes: number
  /** How many bytes of the store's payload were read. */
  bytesRead: number
  /**
   * SQLite's memory-map limit for the connection that read the store. Zero
   * means the store's pages were read, not mapped: they are the kernel's page
   * cache, and the process's own copy of the bytes is the heap's.
   */
  mmapSizeBytes: number
}

export type StoreMemoryOptions = {
  /**
   * Map the store into this process instead of reading it.
   *
   * Only for a scratch store: it makes the store's file pages this process's
   * own, which is what the mapped branch of the question looks like. The
   * mapping itself writes nothing.
   */
  mmap?: boolean
}

/**
 * The columns that hold the canon store's bulk. Reading them is what makes
 * the store's pages real to the operating system.
 */
const PAYLOAD_COLUMNS: ReadonlyArray<{ table: string; column: string }> = [
  { table: 'records', column: 'raw' },
  { table: 'records', column: 'parts_json' },
  { table: 'quarantine', column: 'raw_payload' },
  { table: 'quarantined_logs', column: 'payload' },
  { table: 'pending_logs', column: 'payload' },
  { table: 'session', column: 'summary_json' },
  { table: 'session', column: 'payload' },
  { table: 'session_turn', column: 'turn_json' },
]

/** Sample this process's memory. */
export function sampleProcessMemory(): MemorySample {
  const usage = process.memoryUsage()
  return {
    rssBytes: usage.rss,
    heapUsedBytes: usage.heapUsed,
    heapTotalBytes: usage.heapTotal,
    externalBytes: usage.external,
  }
}

/**
 * Attribute the resident growth of `work` between the JavaScript heap and
 * everything else the process holds.
 *
 * Nothing is mapped into the process here, so nothing of the work can be
 * charged to a file's pages: `fileBackedBytes` is zero, and whatever the work
 * grew is the heap's or the buffers'.
 */
export function attributeMemory(work: () => void): MemoryAttribution {
  const before = sampleProcessMemory()
  work()
  const after = sampleProcessMemory()
  return attribute(before, after, 0)
}

/**
 * Attribute the resident memory a store handle costs this process.
 *
 * The store is opened read-only and its payload is read, so the store's pages
 * are faulted in the way a reader faults them in — which is the whole
 * question: whether the store's bytes become this process's resident memory
 * or stay the kernel's.
 */
export function attributeStoreMemory(
  path: string,
  options: StoreMemoryOptions = {},
): StoreMemoryAttribution {
  const storeBytes = statSync(path).size
  const before = sampleProcessMemory()
  const db = new DatabaseSync(path, { readOnly: true })
  let bytesRead = 0
  let mmapSizeBytes = 0
  try {
    if (options.mmap === true) db.exec(`PRAGMA mmap_size = ${storeBytes}`)
    const mmap = db.prepare('PRAGMA mmap_size').get() as { mmap_size?: number } | undefined
    mmapSizeBytes = mmap?.mmap_size ?? 0
    for (const { table, column } of PAYLOAD_COLUMNS) {
      if (!hasColumn(db, table, column)) continue
      for (const row of db.prepare(`SELECT ${column} FROM ${table}`).iterate()) {
        bytesRead += byteLength(row[column])
      }
    }
  } finally {
    db.close()
  }
  const after = sampleProcessMemory()
  // A mapped store's pages are the process's own; a store that is read has
  // its pages in the kernel's cache and charges this process nothing.
  const fileBackedBytes = mmapSizeBytes > 0 ? storeBytes : 0
  return {
    path,
    storeBytes,
    bytesRead,
    mmapSizeBytes,
    ...attribute(before, after, fileBackedBytes),
  }
}

/**
 * Split a resident growth between the heap and everything else.
 *
 * The heap's own growth is measured; what is left of the resident growth is
 * the buffers and the native allocations the work made — and, when the work
 * mapped a file, the file's own pages.
 */
function attribute(before: MemorySample, after: MemorySample, fileBackedBytes: number): MemoryAttribution {
  const heapBytes = Math.max(0, after.heapUsedBytes - before.heapUsedBytes)
  const residentGrowthBytes = Math.max(0, after.rssBytes - before.rssBytes)
  const nonHeapBytes = Math.max(0, residentGrowthBytes - heapBytes)
  return {
    before,
    after,
    residentGrowthBytes,
    heapBytes,
    nonHeapBytes,
    fileBackedBytes,
    holder: fileBackedBytes > heapBytes ? 'file' : 'heap',
  }
}

/** Whether the table has the column, so a store from another version is skipped rather than fatal. */
function hasColumn(db: InstanceType<typeof DatabaseSync>, table: string, column: string): boolean {
  const exists = db
    .prepare("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?")
    .get(table)
  if (exists === undefined) return false
  const columns = db.prepare(`PRAGMA table_info(${table})`).all() as { name: string }[]
  return columns.some(entry => entry.name === column)
}

/** The size of one stored value, whether SQLite returned it as text or as bytes. */
function byteLength(value: unknown): number {
  if (value === null || value === undefined) return 0
  if (typeof value === 'string') return Buffer.byteLength(value, 'utf8')
  if (value instanceof Uint8Array) return value.byteLength
  return 0
}
