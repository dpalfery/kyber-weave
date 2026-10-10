// Characterization tests for scope resolution in cleanDatabase (clean.ts,
// canonicalScope). Additive and separate from clean.test.ts, which is an
// approved, closed contract — these pin the ordering guarantee that makes a
// descriptor-less harness safe: canonicalScope runs before the receiver pause,
// so an unusable scope is a no-op rather than a wipe followed by a throw.
//
// Reuses the same module mocks and fake-ports construction as clean.test.ts:
// the folder-import mock in particular is unavoidable, because the real
// folder-import.ts imports MAX_CLEAN_REINGEST_WEEKS from clean/clean.js and
// spreading the original module would resolve that import cycle through the
// mock.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { CanonStore } from '../canon/store.js'
import { cleanDatabase } from './clean.js'

vi.mock('./pause.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./pause.js')>()),
  pauseReceiver: vi.fn(async () => ({ paused: true, resumed: false })),
  resumeReceiver: vi.fn(async () => {}),
}))

vi.mock('../canon/projection.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../canon/projection.js')>()),
  projectCanonicalStore: vi.fn(async () => {}),
}))

vi.mock('../refresh/folder-import.js', () => ({
  importFolderHistory: vi.fn(async () => {}),
}))

vi.mock('../refresh/orchestrator.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../refresh/orchestrator.js')>()),
  refreshHarnessSources: vi.fn(async () => ({})),
}))

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
  vi.restoreAllMocks()
})

function temporaryStore(): CanonStore {
  const root = mkdtempSync(join(tmpdir(), 'kyber-clean-scope-'))
  temporaryRoots.push(root)
  return new CanonStore(join(root, 'canon.db'))
}

function fakePorts(overrides: Partial<Parameters<typeof cleanDatabase>[2]> = {}) {
  return {
    pauseIngestion: vi.fn(async () => ({ paused: false as boolean, resumed: true as boolean })),
    resumeIngestion: vi.fn(async () => {}),
    project: vi.fn(async () => {}),
    reingest: vi.fn(async () => {}),
    ...overrides,
  }
}

describe('cleanDatabase scope resolution (canonicalScope)', () => {
  it('rejects a scope of only unknown harness ids before anything runs', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()
      // Spied on the store, not on the ports: the contract is that neither wipe
      // path is reached at all, and the wipe methods are what actually destroy
      // data. Counting port calls would only show the pipeline stayed idle.
      const wipeAll = vi.spyOn(store, 'wipeAll')
      const wipeHarnesses = vi.spyOn(store, 'wipeHarnesses')

      await expect(
        cleanDatabase(store, { harnesses: ['bogus-nonexistent'], reingestWeeks: 4 }, ports),
      ).rejects.toThrow(/scope selects no known harness/)

      expect(ports.pauseIngestion).toHaveBeenCalledTimes(0)
      expect(ports.resumeIngestion).toHaveBeenCalledTimes(0)
      expect(ports.project).toHaveBeenCalledTimes(0)
      expect(ports.reingest).toHaveBeenCalledTimes(0)
      expect(wipeAll).not.toHaveBeenCalled()
      expect(wipeHarnesses).not.toHaveBeenCalled()
    } finally {
      store.close()
    }
  })

  it('rejects the same unknown-only scope with no reingest window (the throw precedes any import)', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()
      const wipeAll = vi.spyOn(store, 'wipeAll')
      const wipeHarnesses = vi.spyOn(store, 'wipeHarnesses')

      // Why this case separately: with no window there is no import to fail on,
      // so the rejection has to come from scope resolution alone. That is what
      // makes the throw independent of an import — an empty harness list must
      // never be passed downstream, where it reads as "every harness".
      await expect(cleanDatabase(store, { harnesses: ['bogus-nonexistent'] }, ports)).rejects.toThrow(
        /scope selects no known harness/,
      )

      expect(ports.pauseIngestion).toHaveBeenCalledTimes(0)
      expect(ports.resumeIngestion).toHaveBeenCalledTimes(0)
      expect(ports.project).toHaveBeenCalledTimes(0)
      expect(ports.reingest).toHaveBeenCalledTimes(0)
      expect(wipeAll).not.toHaveBeenCalled()
      expect(wipeHarnesses).not.toHaveBeenCalled()
    } finally {
      store.close()
    }
  })

  it('drops the unknown id from a mixed scope and wipes, reports and re-ingests only the known one', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()
      const wipeAll = vi.spyOn(store, 'wipeAll')
      const wipeHarnesses = vi.spyOn(store, 'wipeHarnesses')

      const report = await cleanDatabase(
        store,
        { harnesses: ['bogus', 'pi'], reingestWeeks: 2 },
        ports,
      )

      // One list, three consumers: the wipe, the report and the re-ingest must
      // all agree on the resolvable set, or a clean silently wipes a different
      // scope than the one it reports.
      expect(wipeAll).not.toHaveBeenCalled()
      expect(wipeHarnesses).toHaveBeenCalledTimes(1)
      expect(wipeHarnesses).toHaveBeenCalledWith(['pi'])
      expect(report.harnesses).toEqual(['pi'])
      expect(ports.reingest).toHaveBeenCalledWith({ harnesses: ['pi'], historyWeeks: 2 })
    } finally {
      store.close()
    }
  })

  it('still wipes all and reports "*" for an all scope', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()
      const wipeAll = vi.spyOn(store, 'wipeAll')
      const wipeHarnesses = vi.spyOn(store, 'wipeHarnesses')

      // Control: filtering must not narrow an explicit --all, whose report
      // value is the literal '*' rather than a harness id.
      const report = await cleanDatabase(store, { all: true }, ports)

      expect(wipeAll).toHaveBeenCalledTimes(1)
      expect(wipeHarnesses).not.toHaveBeenCalled()
      expect(report.harnesses).toEqual(['*'])
    } finally {
      store.close()
    }
  })
})