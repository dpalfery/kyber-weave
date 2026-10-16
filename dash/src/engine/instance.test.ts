import { spawn } from 'child_process'
import { mkdtempSync, rmSync, writeFileSync, existsSync } from 'fs'
import { createServer, type Socket } from 'net'
import { tmpdir } from 'os'
import { join } from 'path'

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { acquireEngine, enginePipePath, queryEngine, readEngineRecordFile, requestEngineShutdown } from './instance.js'
import type { EngineOwner } from './instance.js'

let stateDir: string
const owners: EngineOwner[] = []

beforeEach(() => {
  stateDir = mkdtempSync(join(tmpdir(), 'kd-engine-'))
})

afterEach(async () => {
  for (const owner of owners.splice(0)) await owner.release()
  rmSync(stateDir, { recursive: true, force: true })
})

async function own(version: string, onShutdownRequested?: () => void): Promise<EngineOwner> {
  const taken = await acquireEngine({ stateDir, version, ...(onShutdownRequested === undefined ? {} : { onShutdownRequested }) })
  if (taken.kind !== 'owner') throw new Error('expected to own the engine')
  owners.push(taken)
  return taken
}

describe('engine instance lock', () => {
  it('lets exactly one process own the engine and tells the second who holds it', async () => {
    const first = await own('1.0.0')
    first.update({ url: 'http://127.0.0.1:4747' })

    const second = await acquireEngine({ stateDir, version: '1.0.0' })

    expect(second.kind).toBe('attached')
    if (second.kind === 'attached') {
      expect(second.record.pid).toBe(process.pid)
      expect(second.record.version).toBe('1.0.0')
      expect(second.record.url).toBe('http://127.0.0.1:4747')
    }
  })

  it('publishes a record naming pid, executable and version for the holder', async () => {
    await own('2.3.4')

    const record = readEngineRecordFile(stateDir)

    expect(record).toMatchObject({ pid: process.pid, exe: process.execPath, version: '2.3.4' })
  })

  it('answers queryEngine from the live holder, not from the file', async () => {
    const owner = await own('1.0.0')
    owner.update({ url: 'http://127.0.0.1:1' })
    writeFileSync(join(stateDir, 'engine.json'), JSON.stringify({ pid: 1, exe: 'x', version: 'liar', startedAt: '' }))

    const live = await queryEngine(stateDir)

    expect(live).not.toBeNull()
    expect(live).not.toBe('unresponsive')
    expect((live as { version: string }).version).toBe('1.0.0')
  })

  it('reports no engine when nothing holds the endpoint', async () => {
    expect(await queryEngine(stateDir)).toBeNull()
  })

  it('releases the endpoint and the record on release so the next start can own it', async () => {
    const first = await own('1.0.0')
    await first.release()
    owners.length = 0

    expect(readEngineRecordFile(stateDir)).toBeNull()
    const next = await own('1.0.1')
    expect(next.record.version).toBe('1.0.1')
  })

  it('reclaims a leftover socket file that no process listens on', async () => {
    if (process.platform === 'win32') return
    const path = enginePipePath(stateDir)
    // A real engine killed outright leaves its socket file behind; reproduce exactly that.
    const child = spawn(process.execPath, ['-e', `require('net').createServer().listen(${JSON.stringify(path)}, () => console.log('up'))`], { stdio: ['ignore', 'pipe', 'ignore'] })
    await new Promise<void>((resolve) => { child.stdout.once('data', () => resolve()) })
    child.kill('SIGKILL')
    await new Promise<void>((resolve) => { child.once('exit', () => resolve()) })
    expect(existsSync(path)).toBe(true)

    const taken = await acquireEngine({ stateDir, version: '1.0.0' })

    expect(taken.kind).toBe('owner')
    if (taken.kind === 'owner') owners.push(taken)
  })

  it('does not bind over a holder that accepts connections but never answers', async () => {
    if (process.platform === 'win32') return
    const path = enginePipePath(stateDir)
    const held: Socket[] = []
    const mute = createServer((socket) => { held.push(socket); socket.on('error', () => {}) })
    await new Promise<void>((resolve) => mute.listen(path, resolve))
    try {
      const result = await acquireEngine({ stateDir, version: '1.0.0' })
      expect(result.kind).toBe('attached')
    } finally {
      for (const socket of held) socket.destroy()
      await new Promise<void>((resolve) => mute.close(() => resolve()))
    }
  })

  it('delivers a shutdown request to the holder', async () => {
    const stop = vi.fn()
    await own('1.0.0', stop)

    const acknowledged = await requestEngineShutdown(stateDir)

    expect(acknowledged).toBe(true)
    expect(stop).toHaveBeenCalledTimes(1)
  })

  it('keeps long state-dir paths inside the socket path limit', () => {
    const long = join(tmpdir(), 'x'.repeat(120))

    const path = enginePipePath(long, 'darwin')

    expect(path.length).toBeLessThanOrEqual(104)
  })

  it('uses a named pipe on Windows', () => {
    expect(enginePipePath('C:\\Users\\me\\.kyberdash', 'win32')).toMatch(/^\\\\\.\\pipe\\kyberdash-engine-[0-9a-f]{16}$/)
  })
})
