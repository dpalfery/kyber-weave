// The discovery sweep (provider walks + per-file stats) now issues its metadata
// syscalls concurrently instead of one at a time. Nothing downstream may notice:
// source order still follows the registry / readdir order, and nothing that was
// discovered serially may be dropped.

import { mkdtemp, mkdir, rm, symlink, writeFile } from 'fs/promises'
import { tmpdir } from 'os'
import { basename, join } from 'path'

import { describe, it, expect, beforeEach, afterEach } from 'vitest'

import { FS_SCAN_CONCURRENCY, mapWithConcurrency } from '../src/fs-utils.js'
import { collectJsonlFiles } from '../src/parser.js'
import { discoverAllSessions } from '../src/providers/index.js'
import type { Provider, SessionSource } from '../src/providers/types.js'

let root: string
beforeEach(async () => { root = await mkdtemp(join(tmpdir(), 'codeburn-disc-')) })
afterEach(async () => { await rm(root, { recursive: true, force: true }) })

const tick = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms))

describe('mapWithConcurrency', () => {
  it('returns results in input order even when they settle out of order', async () => {
    const items = [40, 0, 20, 0, 10]
    const out = await mapWithConcurrency(items, 8, async (ms, i) => {
      await tick(ms)
      return i
    })
    expect(out).toEqual([0, 1, 2, 3, 4])
  })

  it('never exceeds the requested number of in-flight workers', async () => {
    let inFlight = 0
    let peak = 0
    await mapWithConcurrency(Array.from({ length: 50 }, (_, i) => i), 4, async () => {
      inFlight++
      peak = Math.max(peak, inFlight)
      await tick(1)
      inFlight--
    })
    expect(peak).toBe(4)
  })

  it('handles an empty list without spawning workers', async () => {
    await expect(mapWithConcurrency([], FS_SCAN_CONCURRENCY, async () => 1)).resolves.toEqual([])
  })
})

describe('collectJsonlFiles', () => {
  // The walk skips the subagents/ probe for entries readdir reports as plain
  // files. A symlink is NOT a plain file, so a symlinked session directory must
  // still be probed — dropping it would lose that session's subagent spend.
  it('still probes subagents under a symlinked session directory', async () => {
    const real = join(root, 'real-session')
    await mkdir(join(real, 'subagents'), { recursive: true })
    await writeFile(join(real, 'subagents', 'agent-linked.jsonl'), '{}\n')
    await symlink(real, join(root, 'linked-session'), 'dir')
    // A plain sibling file is the case the skip exists for: no probe, no effect.
    await writeFile(join(root, 'notes.txt'), 'x')

    const found = (await collectJsonlFiles(root)).map(f => basename(f))
    expect(found).toContain('agent-linked.jsonl')
    expect(found).not.toContain('notes.txt')
  })
})

function fakeProvider(name: string, delayMs: number, paths: string[]): Provider {
  return {
    name,
    displayName: name,
    modelDisplayName: (m: string) => m,
    toolDisplayName: (t: string) => t,
    async discoverSessions(): Promise<SessionSource[]> {
      await tick(delayMs)
      return paths.map(path => ({ path, project: name, provider: name }))
    },
    createSessionParser() { throw new Error('not used') },
  }
}

describe('discoverAllSessions', () => {
  it('concatenates providers in registry order regardless of which finishes first', async () => {
    const providers = [
      fakeProvider('slow', 30, ['/s1', '/s2']),
      fakeProvider('fast', 0, ['/f1']),
      fakeProvider('mid', 10, ['/m1']),
    ]
    const sources = await discoverAllSessions('all', providers)
    expect(sources.map(s => s.path)).toEqual(['/s1', '/s2', '/f1', '/m1'])
  })

  it('keeps a throwing provider isolated without dropping the others', async () => {
    const boom = fakeProvider('boom', 0, [])
    boom.discoverSessions = async () => { throw new Error('nope') }
    const sources = await discoverAllSessions('all', [
      fakeProvider('a', 5, ['/a1']),
      boom,
      fakeProvider('b', 0, ['/b1']),
    ])
    expect(sources.map(s => s.path)).toEqual(['/a1', '/b1'])
  })

  it('honours a provider filter', async () => {
    const sources = await discoverAllSessions('b', [
      fakeProvider('a', 0, ['/a1']),
      fakeProvider('b', 0, ['/b1']),
    ])
    expect(sources.map(s => s.path)).toEqual(['/b1'])
  })
})
