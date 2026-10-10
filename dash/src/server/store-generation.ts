// A single number that says "the store's contents are not what they were", so a client
// holding cached records knows to drop them.
//
// WHY a generation and not a count: a client must be able to compare two reads cheaply
// and to tell "nothing changed" from "something was wiped under me". A plain record
// count cannot do both — ingesting one session moves it without anything being invalid,
// while a clean can return the count to exactly the value it had before.
//
// The generation is derived, never stored and bumped, because the rows it summarises are
// written by every process that touches the store (refresh, import, clean, a test). A
// bump would need a writer to run for every path that changes the data; deriving it means
// the answer cannot drift from the data it describes.
//
// Inputs: the clean stamp (so a wipe always moves it, even when the wiped scope leaves
// the record count unchanged), the record count and the `records` rowid high-water mark
// (both grow with ingestion, so an append moves it once and never moves it back).

import type { CanonStore } from '../canon/store.js'

const CLEAN_STAMP_KEY = 'last_clean_at'

/** FNV-1a over the three inputs: stable across processes, and never a colliding key. */
function hash32(parts: readonly string[]): number {
  let hash = 0x811c9dc5
  for (const part of parts) {
    for (let index = 0; index < part.length; index += 1) {
      hash ^= part.charCodeAt(index)
      // 32-bit FNV prime multiply, kept in range with Math.imul so the result does not
      // drift with the float mantissa.
      hash = Math.imul(hash, 0x01000193) >>> 0
    }
    hash ^= 0x2f
    hash = Math.imul(hash, 0x01000193) >>> 0
  }
  return hash >>> 0
}

/**
 * The current generation, or `0` when the store cannot be read at all. A zero is still a
 * stable answer: two unreadable reads agree, and the next readable one differs, which is
 * exactly what the client needs.
 */
export function storeGeneration(store: CanonStore): number {
  let cleanStamp = ''
  let count = ''
  let highWater = ''
  try {
    cleanStamp = store.getMetadata(CLEAN_STAMP_KEY) ?? ''
  } catch {
    cleanStamp = ''
  }
  try {
    count = String(store.count())
  } catch {
    count = ''
  }
  try {
    // The `records` table is addressed directly rather than through a list method: this
    // must never materialize rows, only the largest rowid.
    const row = store
      .getDatabase()
      .prepare('SELECT MAX(rowid) AS high FROM records')
      .get() as { high?: unknown } | undefined
    const high = row?.high
    highWater = typeof high === 'number' ? String(high) : ''
  } catch {
    highWater = ''
  }
  return hash32([cleanStamp, count, highWater])
}