// F10 regression: repeated service start/close must not retain signal handlers.
// `startOtlpCollectorService` added anonymous SIGINT/SIGTERM handlers it never
// removed, so every in-process restart leaked listeners toward
// MaxListenersExceededWarning. Idempotent close must also hold: closing twice
// must resolve, not throw out of a half-torn-down service.

import { describe, expect, it } from 'vitest'

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
