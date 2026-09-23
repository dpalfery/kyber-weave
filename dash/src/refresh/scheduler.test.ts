import { describe, expect, it } from 'vitest'

import { defaultJobConcurrency, runJobsSettled } from './scheduler.js'

describe('runJobsSettled', () => {
  it('never runs more workers than the injected concurrency cap', async () => {
    let inFlight = 0
    let peak = 0
    const jobs = [1, 2, 3, 4, 5]
    const results = await runJobsSettled(jobs, 2, async (job) => {
      inFlight += 1
      peak = Math.max(peak, inFlight)
      await new Promise((resolve) => setTimeout(resolve, 20))
      inFlight -= 1
      return job
    })
    expect(peak).toBeLessThanOrEqual(2)
    expect(results.map((result) => (result.status === 'fulfilled' ? result.value : null))).toEqual(jobs)
  })

  it('lets remaining jobs finish when one job rejects', async () => {
    const finished: number[] = []
    const results = await runJobsSettled([1, 2, 3], 2, async (job) => {
      if (job === 1) {
        await new Promise((resolve) => setTimeout(resolve, 10))
        throw new Error('harness 1 failed')
      }
      await new Promise((resolve) => setTimeout(resolve, 30))
      finished.push(job)
      return job
    })
    expect(finished.sort()).toEqual([2, 3])
    expect(results[0]).toMatchObject({ status: 'rejected' })
    expect(results[1]).toMatchObject({ status: 'fulfilled', value: 2 })
    expect(results[2]).toMatchObject({ status: 'fulfilled', value: 3 })
  })

  it('caps default concurrency between 1 and 4', () => {
    expect(defaultJobConcurrency()).toBeGreaterThanOrEqual(1)
    expect(defaultJobConcurrency()).toBeLessThanOrEqual(4)
  })
})
