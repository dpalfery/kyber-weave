// Regression test for the codex/non-Claude cold-parse restart-loop: a run
// killed mid-provider (app timeout SIGKILL, crash, force-quit) used to lose
// the ENTIRE provider phase, because scanProjectDirs received a saveProgress
// callback for throttled partial-progress persistence but parseProviderSources
// did not — non-Claude providers only persisted at the whole-provider
// boundary. This exercises the callback parseProviderSources now accepts,
// mirroring scanProjectDirs' onFileParsed.
//
// Uses the kiro provider as the multi-file fixture (simple JSON parser, no
// worker-pool indirection) so the test can call parseProviderSources directly
// and count exactly how many files get (re)parsed on each run.

import { describe, it, expect, beforeEach, afterAll, vi } from 'vitest'
import { mkdir, rm, writeFile } from 'fs/promises'
import { join } from 'path'

import { parseProviderSources } from '../src/parser.js'
import { getProvider } from '../src/providers/index.js'
import { CACHE_VERSION, type SessionCache } from '../src/session-cache.js'

// The kiro provider singleton captures homedir() when its module is first
// imported, so HOME must point at the test root before ../src/parser.js is
// evaluated (same constraint as tests/kiro-cache-invalidation.test.ts).
const testRoot = vi.hoisted(() => {
  const root = `${process.env['TMPDIR'] || '/tmp'}/kiro-partial-save-${process.pid}-${Date.now()}`
  process.env['HOME'] = `${root}/home`
  process.env['USERPROFILE'] = `${root}/home`
  return root
})
const HOME = join(testRoot, 'home')

function kiroAgentDir(): string {
  if (process.platform === 'darwin') {
    return join(HOME, 'Library', 'Application Support', 'Kiro', 'User', 'globalStorage', 'kiro.kiroagent')
  }
  if (process.platform === 'win32') {
    return join(HOME, 'AppData', 'Roaming', 'Kiro', 'User', 'globalStorage', 'kiro.kiroagent')
  }
  return join(HOME, '.config', 'Kiro', 'User', 'globalStorage', 'kiro.kiroagent')
}

// Writes `n` independent execution files (same shape as the existing Kiro
// fixture in kiro-cache-invalidation.test.ts), each a self-contained "file"
// parseProviderSources processes and progress-saves individually.
async function seedExecutionFiles(n: number): Promise<void> {
  const dir = join(kiroAgentDir(), 'a'.repeat(32), 'b'.repeat(32))
  await mkdir(dir, { recursive: true })
  for (let i = 0; i < n; i++) {
    await writeFile(join(dir, `exec-${i}`), JSON.stringify({
      executionId: `exec-${i}`,
      workflowType: 'chat-agent',
      status: 'succeed',
      startTime: 1780000000000 + i,
      chatSessionId: `session-${i}`,
      context: {
        messages: [
          { role: 'human', entries: [{ type: 'text', text: `Question number ${i}` }] },
          { role: 'bot', entries: [{ type: 'text', text: `Answer number ${i}, with enough content to yield a non-zero token estimate.` }] },
        ],
      },
    }))
  }
}

function callCount(projects: Awaited<ReturnType<typeof parseProviderSources>>): number {
  return projects.flatMap(p => p.sessions).flatMap(s => s.turns).flatMap(t => t.assistantCalls).length
}

beforeEach(async () => {
  await rm(testRoot, { recursive: true, force: true })
})
afterAll(async () => {
  await rm(testRoot, { recursive: true, force: true })
})

describe('parseProviderSources partial-progress threading', () => {
  it('a kill right after a mid-provider save resumes without re-parsing already-completed files', async () => {
    await seedExecutionFiles(4)
    const provider = await getProvider('kiro')
    if (!provider) throw new Error('kiro provider unavailable in this environment')
    const sources = await provider.discoverSessions()
    expect(sources).toHaveLength(4)

    // Reference: an uninterrupted cold parse of all 4 files.
    const reference = await parseProviderSources(
      'kiro', sources, new Set(), { version: CACHE_VERSION, providers: {} }, undefined, undefined, false,
    )
    expect(callCount(reference)).toBe(4)

    // Simulated kill: the process dies the instant the 2nd file's cache entry
    // is durable — `snapshot` is exactly what a throttled disk save would have
    // persisted at that moment. Throwing from the callback (which fires AFTER
    // the file's entry lands, outside the per-file try/catch) propagates out of
    // parseProviderSources uncaught, the same as a real interruption: nothing
    // after this point ever reaches disk.
    const KILLED = new Error('simulated kill mid-provider')
    const killDiskCache: SessionCache = { version: CACHE_VERSION, providers: {} }
    let killCalls = 0
    let snapshot: SessionCache | undefined
    const onFileParsed = async () => {
      killCalls++
      if (killCalls === 2) {
        snapshot = structuredClone(killDiskCache)
        throw KILLED
      }
    }
    await expect(
      parseProviderSources('kiro', sources, new Set(), killDiskCache, undefined, onFileParsed, false),
    ).rejects.toBe(KILLED)
    expect(killCalls).toBe(2)
    expect(Object.keys(snapshot?.providers['kiro']?.files ?? {})).toHaveLength(2)

    // Resume: re-run against exactly what the kill left behind. Only the 2
    // files the kill never reached should get (re)parsed.
    let resumeParses = 0
    const resumed = await parseProviderSources(
      'kiro', sources, new Set(), snapshot!, undefined, async () => { resumeParses++ }, false,
    )
    expect(resumeParses).toBe(2)

    // Convergence: the resumed run's totals match the never-interrupted reference.
    expect(callCount(resumed)).toBe(callCount(reference))
  })
})
