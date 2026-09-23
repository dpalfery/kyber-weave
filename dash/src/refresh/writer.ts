import type { CanonicalRecord } from '../canon/types.js'
import type { RecordProvenance, SourceCheckpoint } from '../canon/source-state.js'

export type SourceUnitCommit = {
  records: readonly CanonicalRecord[]
  provenance: readonly RecordProvenance[]
  checkpoint: SourceCheckpoint
}

export type CanonicalWriter = {
  enqueue: (item: SourceUnitCommit) => Promise<void>
  drain: () => Promise<void>
}

type QueuedCommit = {
  item: SourceUnitCommit
  resolve: () => void
  reject: (reason: unknown) => void
}

/**
 * Single store writer with a bounded queue. Callers block on enqueue when the
 * queue is full so parsed units cannot grow without bound in memory.
 */
export function createCanonicalWriter(options: {
  capacity: number
  commit: (item: SourceUnitCommit) => void
}): CanonicalWriter {
  const capacity = Math.max(1, options.capacity)
  const queue: QueuedCommit[] = []
  const waiting: Array<() => void> = []
  let processing: Promise<void> = Promise.resolve()
  let running = false

  const wake = (): void => {
    const next = waiting.shift()
    if (next) next()
  }

  const pump = (): void => {
    if (running) return
    running = true
    processing = (async () => {
      try {
        while (queue.length > 0) {
          const entry = queue.shift()!
          wake()
          try {
            options.commit(entry.item)
            entry.resolve()
          } catch (error) {
            entry.reject(error)
          }
          await Promise.resolve()
        }
      } finally {
        running = false
      }
    })()
  }

  return {
    async enqueue(item) {
      while (queue.length >= capacity) {
        await new Promise<void>((resolve) => waiting.push(resolve))
      }
      const settled = new Promise<void>((resolve, reject) => {
        queue.push({ item, resolve, reject })
      })
      pump()
      await settled
    },
    async drain() {
      try {
        await processing
      } catch {
        // Commit failures are already rejected on the responsible enqueue.
      }
      while (running || queue.length > 0) {
        pump()
        try {
          await processing
        } catch {
          // Same: drain waits for idle without rethrowing attributed errors.
        }
      }
    },
  }
}
