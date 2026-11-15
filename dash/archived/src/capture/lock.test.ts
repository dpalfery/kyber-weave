import { describe, expect, it } from 'vitest'

import { createMutex, withCaptureLock } from './lock.js'

describe('createMutex', () => {
  it('runs tasks one at a time, in submission order', async () => {
    const mutex = createMutex()
    const order: string[] = []
    let running = 0
    let peak = 0
    const task = (name: string, delayMs: number) =>
      mutex.run(async () => {
        running += 1
        peak = Math.max(peak, running)
        order.push(`start ${name}`)
        await new Promise((resolve) => setTimeout(resolve, delayMs))
        order.push(`end ${name}`)
        running -= 1
        return name
      })

    const results = await Promise.all([task('a', 15), task('b', 1), task('c', 1)])

    expect(results).toEqual(['a', 'b', 'c'])
    expect(peak).toBe(1)
    expect(order).toEqual(['start a', 'end a', 'start b', 'end b', 'start c', 'end c'])
  })

  it('releases on a rejected task and still runs the next one', async () => {
    const mutex = createMutex()
    const failing = mutex.run(async () => {
      throw new Error('boom')
    })
    const next = mutex.run(async () => 'after')

    await expect(failing).rejects.toThrow('boom')
    await expect(next).resolves.toBe('after')
  })

  it('releases on a synchronous throw and accepts a plain return value', async () => {
    const mutex = createMutex()
    await expect(
      mutex.run(() => {
        throw new Error('sync boom')
      }),
    ).rejects.toThrow('sync boom')
    await expect(mutex.run(() => 7)).resolves.toBe(7)
  })
})

describe('withCaptureLock', () => {
  it('shares one process-wide mutex across callers', async () => {
    let running = 0
    let peak = 0
    const task = () =>
      withCaptureLock(async () => {
        running += 1
        peak = Math.max(peak, running)
        await new Promise((resolve) => setTimeout(resolve, 3))
        running -= 1
      })
    await Promise.all([task(), task(), task()])
    expect(peak).toBe(1)
  })
})
