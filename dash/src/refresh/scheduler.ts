import { availableParallelism } from 'node:os'

/**
 * Bounded job pool. Jobs are queued independently; a rejection never cancels
 * siblings. Default cap matches the plan: max(1, min(4, availableParallelism-1)).
 */
export function defaultJobConcurrency(): number {
  const available = availableParallelism()
  return Math.max(1, Math.min(4, available - 1))
}

export async function runJobsSettled<T, R>(
  jobs: readonly T[],
  concurrency: number,
  run: (job: T, index: number) => Promise<R>,
): Promise<PromiseSettledResult<R>[]> {
  const limit = Math.max(1, concurrency)
  const results: PromiseSettledResult<R>[] = new Array(jobs.length)
  let next = 0

  const worker = async (): Promise<void> => {
    for (;;) {
      const index = next
      next += 1
      if (index >= jobs.length) return
      try {
        const value = await run(jobs[index]!, index)
        results[index] = { status: 'fulfilled', value }
      } catch (reason) {
        results[index] = { status: 'rejected', reason }
      }
    }
  }

  const workers = Math.min(limit, jobs.length)
  if (workers === 0) return results
  await Promise.all(Array.from({ length: workers }, () => worker()))
  return results
}
