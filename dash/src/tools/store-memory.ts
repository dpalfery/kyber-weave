// Operator tool: where does a process's resident memory go when it holds the
// canonical store?
//
// WHY: the engine's resident memory was 2.4 GB and there were two candidate
// explanations with opposite consequences. A JavaScript heap that holds the
// memory is a defect. A store's file pages are the kernel's page cache, which
// the kernel takes back under pressure, and are not a defect. Guessing between
// them is how a real problem goes unnoticed and a non-problem costs a
// refactor, so the split is measured and the measurement is runnable.
//
// WHAT it reports, and why each number is there:
//   - the store's page counters: what the store holds.
//   - `mmap_size`: whether the store's pages are mapped into this process.
//     A store that is *read* has its pages in the kernel's cache and charges
//     the process nothing; a store that is *mapped* has them as the process's
//     own, and the kernel can still reclaim them.
//   - the heap's own growth: what the JavaScript heap accounts for.
//
// Usage:
//   npm run memory:store -- [--db <path>] [--mmap]
//
//   --db   the store to measure. Defaults to the shared resolver: an explicit
//          path, then KYBER_CANON_DB, then ~/.kyberdash/canon.db.
//   --mmap map the store into this process instead of reading it. Only for a
//          scratch store: it is the other branch of the question, and the
//          mapping writes nothing.

import { fileURLToPath } from 'node:url'

import { resolveCanonDbPath } from '../canon/paths.js'
import { attributeStoreMemory } from '../canon/store-memory.js'

/** Parse the tool's own arguments. Kept here so the tool stays a tool. */
export function parseArgs(argv: readonly string[]): { path: string; mmap: boolean } {
  let path: string | undefined
  let mmap = false
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index]
    if (arg === '--db') {
      const value = argv[index + 1]
      if (value === undefined || value.startsWith('-')) {
        throw new Error('--db requires a value')
      }
      path = value
      index += 1
      continue
    }
    if (arg === '--mmap') {
      mmap = true
      continue
    }
    throw new Error(`unknown argument: ${arg}`)
  }
  return { path: resolveCanonDbPath(path), mmap }
}

/** Render the attribution as terminal lines. */
export function formatStoreMemoryReport(report: ReturnType<typeof attributeStoreMemory>): string {
  const mb = (bytes: number): string => `${(bytes / 1048576).toFixed(1)} MB`
  return [
    `Store:            ${report.path}`,
    `Store size:       ${mb(report.storeBytes)}`,
    `Payload read:     ${mb(report.bytesRead)}`,
    `Memory-mapped:    ${report.mmapSizeBytes > 0 ? `yes (limit ${mb(report.mmapSizeBytes)})` : 'no'}`,
    `File-backed:      ${mb(report.fileBackedBytes)}`,
    `Resident growth:  ${mb(report.residentGrowthBytes)}`,
    `JavaScript heap:  ${mb(report.heapBytes)}`,
    `Not the heap:     ${mb(report.nonHeapBytes)}`,
    `Holder:           ${report.holder}`,
  ].join('\n')
}

const isMain = process.argv[1] !== undefined
  && fileURLToPath(import.meta.url) === process.argv[1]
if (isMain) {
  const { path, mmap } = parseArgs(process.argv.slice(2))
  process.stdout.write(`${formatStoreMemoryReport(attributeStoreMemory(path, { mmap }))}\n`)
}
