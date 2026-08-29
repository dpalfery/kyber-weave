import { PassThrough } from 'node:stream'

import React, { useEffect } from 'react'
import { Text } from 'ink'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { RESIZE_DEBOUNCE_MS, createDebouncedResizeStream, renderDebouncedInteractive } from '../src/dashboard.js'
import { BSU, ESU, stripSyncUpdateEscapes } from '../src/ink-win.js'

function makeTerminal(columns = 100, rows = 24): PassThrough & NodeJS.WriteStream {
  const terminal = new PassThrough() as PassThrough & NodeJS.WriteStream
  terminal.isTTY = true
  terminal.columns = columns
  terminal.rows = rows
  return terminal
}

function paintedFrames(writes: string[]): string[] {
  return writes
    .map(chunk => stripSyncUpdateEscapes(chunk))
    .flatMap(chunk => chunk.match(/FRAME:[^\r\n]*/g) ?? [])
}

const CLEAR_ALTERNATE_SCREEN = '\u001B[2J\u001B[H'

describe('interactive dashboard resize stream', () => {
  afterEach(() => vi.useRealTimers())

  it('publishes one settled paint after a resize burst', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal()
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    const app = renderDebouncedInteractive(terminal, size => (
      React.createElement(Text, null, `FRAME:${size.columns}x${size.rows}`)
    ), {
      interactive: true,
      patchConsole: false,
      alternateScreen: true,
    })
    await vi.advanceTimersByTimeAsync(100)
    writes.length = 0

    terminal.columns = 99
    terminal.emit('resize')
    await vi.advanceTimersByTimeAsync(50)
    terminal.columns = 98
    terminal.emit('resize')
    await vi.advanceTimersByTimeAsync(50)
    terminal.columns = 97
    terminal.rows = 30
    terminal.emit('resize')

    await vi.advanceTimersByTimeAsync(RESIZE_DEBOUNCE_MS + 100)

    const frames = paintedFrames(writes)
    expect(frames.filter(frame => frame !== 'FRAME:97x30'), 'a resize burst must not paint intermediate sizes').toEqual([])
    expect(frames).toContain('FRAME:97x30')

    app.unmount()
    app.dispose()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()
  })

  it('keeps the clear on the settled repaint despite a synchronized interloper', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal(80, 100)
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    let interactive: ReturnType<typeof renderDebouncedInteractive> | undefined
    const app = renderDebouncedInteractive(terminal, size => {
      if (size.columns === 180) {
        interactive?.stdout.write(`${BSU}INTERLOPER${ESU}`)
      }
      return React.createElement(Text, null, `FRAME:${size.columns}x${size.rows}`)
    }, {
      interactive: true,
      patchConsole: false,
      alternateScreen: true,
    })
    interactive = app
    await vi.advanceTimersByTimeAsync(100)
    writes.length = 0

    terminal.columns = 120
    terminal.emit('resize')
    await vi.advanceTimersByTimeAsync(50)
    terminal.columns = 180
    terminal.rows = 70
    terminal.emit('resize')

    await vi.advanceTimersByTimeAsync(RESIZE_DEBOUNCE_MS + 100)

    const output = stripSyncUpdateEscapes(writes.join(''))
    const rawOutput = writes.join('')
    const clearAt = output.indexOf(CLEAR_ALTERNATE_SCREEN)
    const settledFrameAt = output.indexOf('FRAME:180x70')
    expect(output.split(CLEAR_ALTERNATE_SCREEN)).toHaveLength(3)
    expect(clearAt).toBeGreaterThanOrEqual(0)
    expect(settledFrameAt).toBeGreaterThan(clearAt)
    const rawClearAt = rawOutput.indexOf(CLEAR_ALTERNATE_SCREEN)
    const rawFrameAt = rawOutput.indexOf('FRAME:180x70')
    const targetFrameStart = rawOutput.lastIndexOf(BSU, rawFrameAt)
    const targetFrameEnd = rawOutput.indexOf(ESU, rawFrameAt)
    const targetFrame = rawOutput.slice(targetFrameStart, targetFrameEnd + ESU.length)
    expect(rawOutput.lastIndexOf(BSU, rawClearAt)).toBeGreaterThanOrEqual(0)
    expect(targetFrameEnd).toBeGreaterThan(rawFrameAt)
    expect(targetFrame).toContain(CLEAR_ALTERNATE_SCREEN)

    app.unmount()
    app.dispose()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()
  })

  it('keeps a narrowing resize free of intermediate frames', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal(180, 70)
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    const app = renderDebouncedInteractive(terminal, size => (
      React.createElement(Text, null, `FRAME:${size.columns}x${size.rows}`)
    ), {
      interactive: true,
      patchConsole: false,
      alternateScreen: true,
    })
    await vi.advanceTimersByTimeAsync(100)
    writes.length = 0

    terminal.columns = 80
    terminal.emit('resize')
    await vi.advanceTimersByTimeAsync(RESIZE_DEBOUNCE_MS + 100)

    const output = stripSyncUpdateEscapes(writes.join(''))
    expect(new Set(paintedFrames(writes))).toEqual(new Set(['FRAME:80x70']))
    expect(output).toContain(CLEAR_ALTERNATE_SCREEN)

    app.unmount()
    app.dispose()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()
  })

  it('does not blank the alternate screen when the settled frame is unchanged', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal(280, 60)
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    const app = renderDebouncedInteractive(terminal, () => (
      React.createElement(Text, null, 'FRAME:stable')
    ), {
      interactive: true,
      patchConsole: false,
      alternateScreen: true,
    })
    await vi.advanceTimersByTimeAsync(100)
    writes.length = 0

    terminal.columns = 300
    terminal.emit('resize')
    await vi.advanceTimersByTimeAsync(RESIZE_DEBOUNCE_MS + 100)

    const output = writes.join('')
    expect(stripSyncUpdateEscapes(output)).not.toContain(CLEAR_ALTERNATE_SCREEN)
    expect(output).not.toContain(BSU)
    expect(output).not.toContain(ESU)

    app.unmount()
    app.dispose()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()
  })

  it('does not clear an unchanged alternate screen in screen-reader mode', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal(280, 60)
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    const app = renderDebouncedInteractive(terminal, () => (
      React.createElement(Text, null, 'FRAME:stable')
    ), {
      interactive: true,
      patchConsole: false,
      alternateScreen: true,
      isScreenReaderEnabled: true,
    })
    await vi.advanceTimersByTimeAsync(100)
    writes.length = 0

    terminal.columns = 300
    terminal.emit('resize')
    await vi.advanceTimersByTimeAsync(RESIZE_DEBOUNCE_MS + 100)

    expect(stripSyncUpdateEscapes(writes.join(''))).not.toContain(CLEAR_ALTERNATE_SCREEN)

    app.unmount()
    app.dispose()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()
  })

  it('inserts the reset only inside an Ink-owned synchronized repaint', () => {
    const terminal = makeTerminal()
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    const stdout = createDebouncedResizeStream(terminal, RESIZE_DEBOUNCE_MS)
    const redraw = stdout.armAlternateScreenClear()

    stdout.write(`${BSU}FRAME:ink-owned`)
    stdout.write(ESU)
    redraw.cancel()

    expect(writes.join('')).toBe(`${BSU}${CLEAR_ALTERNATE_SCREEN}FRAME:ink-owned${ESU}`)
    stdout.dispose()
  })

  it('does not let an unsynchronized write consume or nest the pending reset', () => {
    const terminal = makeTerminal()
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    const stdout = createDebouncedResizeStream(terminal, RESIZE_DEBOUNCE_MS)
    const redraw = stdout.armAlternateScreenClear()

    stdout.write('incidental-log-clear')
    stdout.write(`${BSU}FRAME:ink-owned`)
    stdout.write(ESU)
    redraw.cancel()

    expect(writes.join('')).toBe(`incidental-log-clear${BSU}${CLEAR_ALTERNATE_SCREEN}FRAME:ink-owned${ESU}`)
    expect(writes.join('').split(BSU)).toHaveLength(2)
    expect(writes.join('').split(ESU)).toHaveLength(2)
    stdout.dispose()
  })

  it('keeps a replacement clear from being cancelled by a superseded resize', () => {
    const terminal = makeTerminal()
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    const stdout = createDebouncedResizeStream(terminal, RESIZE_DEBOUNCE_MS)
    const superseded = stdout.armAlternateScreenClear()
    const current = stdout.armAlternateScreenClear()

    superseded.cancel()
    stdout.write(`${BSU}FRAME:current${ESU}`)
    current.cancel()

    expect(writes.join('')).toBe(`${BSU}${CLEAR_ALTERNATE_SCREEN}FRAME:current${ESU}`)
    stdout.dispose()
  })

  it('preserves the reset and frame when a Windows-style sink strips sync escapes', () => {
    const terminal = makeTerminal()
    const writes: string[] = []
    const originalWrite = terminal.write.bind(terminal)
    terminal.write = ((chunk: unknown, ...args: unknown[]) => {
      const stripped = typeof chunk === 'string' ? stripSyncUpdateEscapes(chunk) : chunk
      return (originalWrite as (...values: unknown[]) => boolean)(stripped, ...args)
    }) as typeof terminal.write
    terminal.on('data', chunk => writes.push(String(chunk)))
    const stdout = createDebouncedResizeStream(terminal, RESIZE_DEBOUNCE_MS)
    const redraw = stdout.armAlternateScreenClear()

    stdout.write(`${BSU}FRAME:windows${ESU}`)
    redraw.cancel()

    expect(writes.join('')).toBe(`${CLEAR_ALTERNATE_SCREEN}FRAME:windows`)
    stdout.dispose()
  })

  it('does not erase the primary screen when alternate-screen rendering is disabled', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal(80, 40)
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    const app = renderDebouncedInteractive(terminal, size => (
      React.createElement(Text, null, `FRAME:${size.columns}x${size.rows}`)
    ), {
      interactive: true,
      patchConsole: false,
      alternateScreen: false,
    })
    await vi.advanceTimersByTimeAsync(100)
    writes.length = 0

    terminal.columns = 120
    terminal.emit('resize')
    await vi.advanceTimersByTimeAsync(RESIZE_DEBOUNCE_MS + 100)

    const output = stripSyncUpdateEscapes(writes.join(''))
    expect(output).not.toContain(CLEAR_ALTERNATE_SCREEN)
    expect(output).toContain('FRAME:120x40')

    app.unmount()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()
  })

  it('paints a mid-burst state update when the burst nets to no size change', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal()
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    let updateVisibleState = () => {}
    const StatefulProbe = ({ size }: { size: { columns: number; rows: number } }) => {
      const [revision, setRevision] = React.useState(0)
      updateVisibleState = () => setRevision(value => value + 1)
      return React.createElement(Text, null, `FRAME:revision=${revision}:size=${size.columns}x${size.rows}`)
    }
    const app = renderDebouncedInteractive(terminal, size => React.createElement(StatefulProbe, { size }), {
      interactive: true,
      patchConsole: false,
      alternateScreen: true,
    })
    await vi.advanceTimersByTimeAsync(100)
    writes.length = 0

    terminal.columns = 80
    terminal.emit('resize')
    updateVisibleState()
    terminal.columns = 100
    terminal.emit('resize')

    await vi.advanceTimersByTimeAsync(RESIZE_DEBOUNCE_MS + 100)

    expect(paintedFrames(writes).some(frame => frame.includes('revision=1')), 'a mid-burst state update must reach the terminal even when net size is unchanged').toBe(true)

    app.unmount()
    app.dispose()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()
  })

  it('paints a state update after a spurious identical-dimension SIGWINCH', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal()
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    let updateVisibleState = () => {}
    const StatefulProbe = ({ size }: { size: { columns: number; rows: number } }) => {
      const [revision, setRevision] = React.useState(0)
      updateVisibleState = () => setRevision(value => value + 1)
      return React.createElement(Text, null, `FRAME:revision=${revision}:size=${size.columns}x${size.rows}`)
    }
    const app = renderDebouncedInteractive(terminal, size => React.createElement(StatefulProbe, { size }), {
      interactive: true,
      patchConsole: false,
      alternateScreen: true,
    })
    await vi.advanceTimersByTimeAsync(100)
    writes.length = 0

    terminal.emit('resize')
    updateVisibleState()

    await vi.advanceTimersByTimeAsync(RESIZE_DEBOUNCE_MS + 100)

    expect(paintedFrames(writes).some(frame => frame.includes('revision=1')), 'a state update must still paint after a no-op SIGWINCH').toBe(true)

    app.unmount()
    app.dispose()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()
  })

  it('removes the source relay and cancels pending resize delivery on unmount', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal()
    const renderedSizes: Array<{ columns: number; rows: number }> = []
    const Probe = ({ size }: { size: { columns: number; rows: number } }) => {
      useEffect(() => {
        renderedSizes.push(size)
      }, [size])
      return React.createElement(Text, null, `FRAME:${size.columns}x${size.rows}`)
    }
    const app = renderDebouncedInteractive(terminal, size => React.createElement(Probe, { size }), {
      interactive: true,
      patchConsole: false,
    })
    await vi.advanceTimersByTimeAsync(100)
    renderedSizes.length = 0

    terminal.columns = 90
    terminal.emit('resize')
    app.unmount()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()

    await vi.advanceTimersByTimeAsync(RESIZE_DEBOUNCE_MS)
    expect(renderedSizes).toEqual([])
    expect(terminal.listenerCount('resize')).toBe(0)
  })

  it('cancels an armed clear when unmounted', async () => {
    vi.useFakeTimers()
    const terminal = makeTerminal()
    const writes: string[] = []
    terminal.on('data', chunk => writes.push(String(chunk)))
    const app = renderDebouncedInteractive(terminal, size => (
      React.createElement(Text, null, `FRAME:${size.columns}x${size.rows}`)
    ), {
      interactive: true,
      patchConsole: false,
      alternateScreen: true,
    })
    await vi.advanceTimersByTimeAsync(100)
    writes.length = 0

    app.stdout.armAlternateScreenClear()
    app.unmount()
    await vi.runAllTimersAsync()
    await app.waitUntilExit()
    writes.length = 0
    app.stdout.write(`${BSU}AFTER-UNMOUNT${ESU}`)

    expect(writes.join('')).toBe(`${BSU}AFTER-UNMOUNT${ESU}`)
    expect(terminal.listenerCount('resize')).toBe(0)
  })

  it('disposes a stream that never rendered', () => {
    const terminal = makeTerminal()
    const stdout = createDebouncedResizeStream(terminal, RESIZE_DEBOUNCE_MS)
    expect(terminal.listenerCount('resize')).toBe(1)
    stdout.dispose()
    expect(terminal.listenerCount('resize')).toBe(0)

    const resize = vi.fn()
    stdout.on('resize', resize)
    terminal.emit('resize')
    expect(resize).not.toHaveBeenCalled()
    expect(terminal.listenerCount('resize')).toBe(0)
  })
})
