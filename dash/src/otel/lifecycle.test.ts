// F10 regression: repeated service start/close must not retain signal handlers.
// `startOtlpCollectorService` added anonymous SIGINT/SIGTERM handlers it never
// removed, so every in-process restart leaked listeners toward
// MaxListenersExceededWarning. Idempotent close must also hold: closing twice
// must resolve, not throw out of a half-torn-down service.

import { describe, expect, it, vi } from 'vitest'

import { startOtlpCollectorService } from './service.js'
import { attachShutdownSignals } from '../cli/mcp.js'

function signalCounts(): Record<'SIGINT' | 'SIGTERM', number> {
  return {
    SIGINT: process.listenerCount('SIGINT'),
    SIGTERM: process.listenerCount('SIGTERM'),
  }
}

describe('F10 collector service lifecycle', () => {
  it('returns SIGINT/SIGTERM listener counts to baseline across repeated start/close cycles', async () => {
    const before = signalCounts()

    for (let cycle = 0; cycle < 3; cycle++) {
      const service = await startOtlpCollectorService({ port: 0, dbPath: ':memory:' })
      expect(process.listenerCount('SIGINT')).toBeGreaterThan(before.SIGINT)
      expect(process.listenerCount('SIGTERM')).toBeGreaterThan(before.SIGTERM)
      await service.close()
    }

    expect(signalCounts()).toEqual(before)
  })

  it('tolerates close() called twice without throwing', async () => {
    const service = await startOtlpCollectorService({ port: 0, dbPath: ':memory:' })
    await service.close()
    await expect(service.close()).resolves.toBeUndefined()
  })

  it('joins concurrent close() calls into one teardown instead of resolving the second early', async () => {
    const service = await startOtlpCollectorService({ port: 0, dbPath: ':memory:' })
    // Hold the writer's stop behind a gate: no teardown completes until the
    // test releases it, so an early-resolving second close is observable.
    let releaseStop!: () => void
    const stopGate = new Promise<void>((resolve) => {
      releaseStop = resolve
    })
    const realStop = service.writer.stop.bind(service.writer)
    const stopSpy = vi.spyOn(service.writer, 'stop').mockImplementation(() => stopGate.then(() => realStop()))
    try {
      let secondSettled = false
      const first = service.close()
      const second = service.close().then(() => {
        secondSettled = true
      })
      // Both closes are in flight and the gate is still shut: the second
      // close must not have resolved from a guard while teardown runs.
      await new Promise((resolve) => setImmediate(resolve))
      expect(secondSettled).toBe(false)
      // One teardown only: the concurrent close joined it, not restarted it.
      expect(stopSpy).toHaveBeenCalledTimes(1)

      releaseStop()
      await Promise.all([first, second])
      expect(secondSettled).toBe(true)
    } finally {
      stopSpy.mockRestore()
    }
  })
})

describe('F10 attachShutdownSignals', () => {
  it('registers named handlers and removes them on detach, idempotently', () => {
    const before = signalCounts()
    let shutdowns = 0
    const detach = attachShutdownSignals(() => {
      shutdowns += 1
    })

    expect(process.listenerCount('SIGINT')).toBe(before.SIGINT + 1)
    expect(process.listenerCount('SIGTERM')).toBe(before.SIGTERM + 1)

    detach()
    expect(signalCounts()).toEqual(before)

    // Detaching twice removes nothing that is not ours.
    detach()
    expect(signalCounts()).toEqual(before)
    expect(shutdowns).toBe(0)
  })
})
