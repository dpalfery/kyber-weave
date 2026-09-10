// @vitest-environment node
// Windows is the majority of the desktop fleet and has no other orphan escape:
// no `ps`, and after an app crash nothing closes the child's stdin. These pin
// the win32 half of the reap, which needs its own file because it mocks
// `node:os` and `node:child_process` wholesale.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

// vi.mock factories are hoisted above the module body, so the spies they close
// over have to be hoisted with them.
const { platformMock, execFileSyncMock } = vi.hoisted(() => ({
  platformMock: vi.fn(() => 'win32'),
  execFileSyncMock: vi.fn(),
}))

vi.mock('node:os', async orig => {
  const actual = await orig<typeof import('node:os')>()
  return { ...actual, platform: platformMock }
})
vi.mock('node:child_process', async orig => {
  const actual = await orig<typeof import('node:child_process')>()
  return { ...actual, execFileSync: execFileSyncMock }
})

const { reapOrphanServe, serveCommandMatches } = await import('./cli')

// What Electron's spawn of the bundled CLI records, and what Windows reports
// back for it: same argv, different quoting, and a path with a space in it.
const RECORDED = 'C:\\Program Files\\CodeBurn\\CodeBurn.exe C:\\Program Files\\CodeBurn\\resources\\cli\\dist\\launch.js serve --stdio'
const OBSERVED = '"C:\\Program Files\\CodeBurn\\CodeBurn.exe" "C:\\Program Files\\CodeBurn\\resources\\cli\\dist\\launch.js" serve --stdio'

let dir: string
// process.kill is swapped rather than spied: Electron's process typings do not
// expose it to vi.spyOn's key constraint.
let signalled: Array<[number, string | number | undefined]>
let originalKill: typeof process.kill

function pidFileWith(pid: number, cmd: string): string {
  const p = join(dir, `pid-${pid}-${Math.random()}.json`)
  writeFileSync(p, JSON.stringify({ pid, cmd }))
  return p
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'codeburn-win32-reap-'))
  platformMock.mockReturnValue('win32')
  execFileSyncMock.mockReset()
  signalled = []
  originalKill = process.kill
  process.kill = ((pid: number, signal?: string | number) => {
    signalled.push([pid, signal])
    return true
  }) as typeof process.kill
})

afterEach(() => {
  process.kill = originalKill
  rmSync(dir, { recursive: true, force: true })
})

describe('reapOrphanServe on Windows', () => {
  it('asks CIM for the command line (tasklist never reports argv) and reaps an exact match', () => {
    execFileSyncMock.mockReturnValue(OBSERVED + '\r\n')

    reapOrphanServe(pidFileWith(4242, RECORDED))

    const [bin, args] = execFileSyncMock.mock.calls[0] as [string, string[]]
    expect(bin).toBe('powershell.exe')
    expect(args.join(' ')).toContain('Win32_Process')
    expect(args.join(' ')).toContain('ProcessId=4242')
    expect(signalled).toEqual([[4242, 'SIGTERM']])
  })

  it('never signals a recycled pid running something else', () => {
    execFileSyncMock.mockReturnValue('"C:\\Windows\\System32\\notepad.exe"\r\n')

    reapOrphanServe(pidFileWith(4242, RECORDED))

    expect(signalled).toEqual([])
  })

  it('never signals when the command line cannot be established', () => {
    execFileSyncMock.mockImplementation(() => { throw new Error('powershell unavailable') })

    reapOrphanServe(pidFileWith(4242, RECORDED))

    expect(signalled).toEqual([])
  })

  it('never signals on an empty CommandLine (CIM returns nothing for a dead pid)', () => {
    execFileSyncMock.mockReturnValue('\r\n')

    reapOrphanServe(pidFileWith(4242, RECORDED))

    expect(signalled).toEqual([])
  })

  it('still uses ps off Windows', () => {
    platformMock.mockReturnValue('darwin')
    execFileSyncMock.mockReturnValue('/usr/local/bin/codeburn serve --stdio\n')

    reapOrphanServe(pidFileWith(4242, '/usr/local/bin/codeburn serve --stdio'))

    expect((execFileSyncMock.mock.calls[0] as [string, string[]])[0]).toBe('ps')
    expect(signalled).toEqual([[4242, 'SIGTERM']])
  })
})

describe('serveCommandMatches', () => {
  it('accepts the same argv across the two platforms\' quoting conventions', () => {
    expect(serveCommandMatches(RECORDED, OBSERVED)).toBe(true)
    expect(serveCommandMatches(RECORDED, RECORDED)).toBe(true)
  })

  it('rejects a prefix, a suffix, and a different argv', () => {
    expect(serveCommandMatches(RECORDED, OBSERVED.replace(' --stdio', ''))).toBe(false)
    expect(serveCommandMatches(RECORDED, OBSERVED + ' --extra')).toBe(false)
    expect(serveCommandMatches(RECORDED, 'node C:\\other\\cli.js serve --stdio')).toBe(false)
  })

  it('rejects an absent command line and an empty record', () => {
    expect(serveCommandMatches(RECORDED, null)).toBe(false)
    expect(serveCommandMatches('', '')).toBe(false)
    expect(serveCommandMatches('   ', '   ')).toBe(false)
  })
})
