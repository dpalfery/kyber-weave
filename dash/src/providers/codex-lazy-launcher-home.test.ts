// Regression test for D1 decision: launcher-home detection must run only on explicit request,
// not automatically at provider construction time.
//
// When createCodexProvider() is called with no arguments (the module-level singleton),
// it must not probe ~/.codex or ~/.buzz. Detection defers to the first call to
// probeRoots() or discoverSessions(), and is memoized thereafter.
//
// Detection is measured by spy call count on isNestedLauncherCodexHome and sameCodexHome,
// which are mocked to return deterministic values without touching the real filesystem.
// HOME and CODEX_HOME are redirected to temp directories to prevent real home access
// during module initialization or environment variable defaults.

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mkdtemp, rm } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'

let detectionRunCount = 0
let tempHome: string
let tempCodexHome: string

// Mock the launcher-homes module with spies that return false/deterministic values,
// preventing any real filesystem access or home directory probing.
vi.mock('../ingest/launcher-homes.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../ingest/launcher-homes.js')>()
  return {
    ...actual,
    sameCodexHome: vi.fn((_a: string, _b: string) => {
      detectionRunCount++
      return false // not the same
    }),
    isNestedLauncherCodexHome: vi.fn(() => {
      detectionRunCount++
      return false // not nested
    }),
    defaultBilledCodexHome: vi.fn(() => tempCodexHome),
    defaultLauncherRoots: vi.fn(() => [join(tempHome, '.buzz')]),
  }
})

describe('Codex lazy launcher-home detection', () => {
  beforeEach(async () => {
    detectionRunCount = 0
    tempHome = await mkdtemp(join(tmpdir(), 'codex-test-home-'))
    tempCodexHome = join(tempHome, '.codex')
    // Save and override HOME/CODEX_HOME to temp directories
    process.env.HOME = tempHome
    if (process.env.CODEX_HOME) delete process.env.CODEX_HOME
  })

  afterEach(async () => {
    await rm(tempHome, { recursive: true, force: true })
    // Restore original environment
    delete process.env.HOME
    vi.resetModules()
  })

  it('createCodexProvider() with no arguments does not invoke detection', async () => {
    const { createCodexProvider } = await import('../providers/codex.js')
    const beforeCreation = detectionRunCount
    createCodexProvider()
    // Detection should not have run during construction
    expect(detectionRunCount).toBe(beforeCreation)
  })

  it('first call to probeRoots() invokes detection', async () => {
    const { createCodexProvider } = await import('../providers/codex.js')
    const provider = createCodexProvider()
    const beforeProbe = detectionRunCount
    await provider.probeRoots?.()
    // Detection should have run at least once during first probeRoots
    expect(detectionRunCount).toBeGreaterThan(beforeProbe)
  })

  it('second call to probeRoots() does not invoke detection again (memoized)', async () => {
    const { createCodexProvider } = await import('../providers/codex.js')
    const provider = createCodexProvider()
    await provider.probeRoots?.()
    const afterFirst = detectionRunCount
    await provider.probeRoots?.()
    // Detection should not run again; count should remain the same
    expect(detectionRunCount).toBe(afterFirst)
  })

  it('first call to discoverSessions() invokes detection', async () => {
    const { createCodexProvider } = await import('../providers/codex.js')
    const provider = createCodexProvider()
    const beforeDiscover = detectionRunCount
    await provider.discoverSessions()
    // Detection should have run at least once during first discoverSessions
    expect(detectionRunCount).toBeGreaterThan(beforeDiscover)
  })

  it('second call to discoverSessions() does not invoke detection again (memoized)', async () => {
    const { createCodexProvider } = await import('../providers/codex.js')
    const provider = createCodexProvider()
    await provider.discoverSessions()
    const afterFirst = detectionRunCount
    await provider.discoverSessions()
    // Detection should not run again; count should remain the same
    expect(detectionRunCount).toBe(afterFirst)
  })

  it('probeRoots() and discoverSessions() share memoized detection state', async () => {
    const { createCodexProvider } = await import('../providers/codex.js')
    const provider = createCodexProvider()
    await provider.probeRoots?.()
    const afterProbe = detectionRunCount
    await provider.discoverSessions()
    // discoverSessions should not re-run detection if probeRoots already did
    expect(detectionRunCount).toBe(afterProbe)
  })
})
