import { describe, it, expect, afterEach, vi } from 'vitest'

import { PROGRESS_LINE_PREFIX, startProgressKeepalive, stopProgressKeepalive } from '../src/parser.js'

// A cold parse goes genuinely silent between providers (a measured 31.6s on a
// large corpus, in the inter-provider cache save), and the desktop app reads
// silence as a dead child. These pin the heartbeat that makes silence mean
// stopped rather than slow.
describe('scan-progress keepalive', () => {
  const original = process.env['CODEBURN_PROGRESS']

  /** Collects the progress lines written to stderr while `fn` drives the clock. */
  function captureKeepalives(fn: () => void): string[] {
    const written: string[] = []
    const spy = vi.spyOn(process.stderr, 'write').mockImplementation(((chunk: unknown) => {
      written.push(String(chunk))
      return true
    }) as typeof process.stderr.write)
    try { fn() } finally { spy.mockRestore() }
    return written.filter(line => line.startsWith(PROGRESS_LINE_PREFIX) && line.includes('"keepalive"'))
  }

  afterEach(() => {
    stopProgressKeepalive()
    stopProgressKeepalive()
    vi.useRealTimers()
    if (original === undefined) delete process.env['CODEBURN_PROGRESS']
    else process.env['CODEBURN_PROGRESS'] = original
  })

  it('beats through a silent stretch far longer than the app watchdog window', () => {
    process.env['CODEBURN_PROGRESS'] = '1'
    vi.useFakeTimers()
    // 90s of a parse doing nothing observable — three times the measured save
    // stall, and twice the app's 45s silence window.
    const beats = captureKeepalives(() => {
      startProgressKeepalive()
      vi.advanceTimersByTime(90_000)
    })
    expect(beats.length).toBeGreaterThanOrEqual(9)
    // No silent gap anywhere near the window the app kills on.
    expect(90_000 / beats.length).toBeLessThan(45_000)
  })

  it('stops when the parse ends, so an idle process never chatters', () => {
    process.env['CODEBURN_PROGRESS'] = '1'
    vi.useFakeTimers()
    const afterStop = captureKeepalives(() => {
      startProgressKeepalive()
      vi.advanceTimersByTime(25_000)
      stopProgressKeepalive()
    })
    expect(afterStop.length).toBeGreaterThan(0)
    expect(captureKeepalives(() => vi.advanceTimersByTime(60_000))).toEqual([])
  })

  it('keeps beating until the outermost parse finishes', () => {
    process.env['CODEBURN_PROGRESS'] = '1'
    vi.useFakeTimers()
    startProgressKeepalive()
    startProgressKeepalive()
    stopProgressKeepalive() // an inner parse returned; the outer one is still running
    expect(captureKeepalives(() => vi.advanceTimersByTime(30_000)).length).toBeGreaterThan(0)
    stopProgressKeepalive()
    expect(captureKeepalives(() => vi.advanceTimersByTime(30_000))).toEqual([])
  })

  it('emits nothing for a plain CLI run (no CODEBURN_PROGRESS)', () => {
    delete process.env['CODEBURN_PROGRESS']
    vi.useFakeTimers()
    expect(captureKeepalives(() => {
      startProgressKeepalive()
      vi.advanceTimersByTime(60_000)
    })).toEqual([])
  })
})
