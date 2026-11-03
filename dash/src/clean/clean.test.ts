// Central clean for KyberDash (issue #312, plan T3 RED): pause ingestion,
// wipe the store for a harness scope (or all), project the derived caches,
// re-ingest the scope from source logs, and resume ingestion — resuming in a
// `finally` so a failing wipe cannot wedge the collector shut. Thin callers
// (the `dash clean` CLI and `POST /api/kyber/clean`) reach this module; no
// clean logic lives in either surface. This module does not exist yet — the
// test below fails to import it until T4 lands the implementation.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { projectCanonicalStore } from '../canon/projection.js'
import { CanonStore } from '../canon/store.js'
import { importFolderHistory } from '../refresh/folder-import.js'
import { refreshHarnessSources } from '../refresh/orchestrator.js'
import { SETTING_KEYS } from '../settings/shared-settings.js'
import { cleanDatabase, portsForClean } from './clean.js'
import { pauseReceiver, resumeReceiver } from './pause.js'

vi.mock('./pause.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./pause.js')>()),
  pauseReceiver: vi.fn(async () => ({ paused: true, resumed: false })),
  resumeReceiver: vi.fn(async () => {}),
}))

vi.mock('../canon/projection.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../canon/projection.js')>()),
  projectCanonicalStore: vi.fn(async () => {}),
}))

// Self-contained factory, no `importOriginal`: the real folder-import.ts imports
// MAX_CLEAN_REINGEST_WEEKS from clean/clean.js, and clean.ts imports
// importFolderHistory from folder-import — spreading the original module back in
// would resolve that cycle through the mock. Only importFolderHistory is exercised
// here; folder-import.test.ts covers the module itself.
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
  const root = mkdtempSync(join(tmpdir(), 'kyber-clean-'))
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

describe('cleanDatabase (issue #312)', () => {
  it('does not re-ingest when reingestWeeks is omitted', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      const report = await cleanDatabase(store, { harnesses: ['pi'] }, ports)

      expect(report.harnesses).toEqual(['pi'])
      expect(ports.pauseIngestion).toHaveBeenCalledTimes(1)
      expect(ports.reingest).not.toHaveBeenCalled()
      expect(report.reingested).toBe(false)
      expect(report.historyWeeks).toBeNull()
      expect(ports.project).toHaveBeenCalledTimes(1)
      expect(ports.resumeIngestion).toHaveBeenCalledTimes(1)
      expect(store.getMetadata('last_clean_at')).toBeDefined()
    } finally {
      store.close()
    }
  })

  it('wipes all when asked, and passes no harness filter to re-ingest when a window is given', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      const report = await cleanDatabase(store, { all: true, reingestWeeks: 1 }, ports)

      expect(report.harnesses).toEqual(['*'])
      expect(store.count()).toBe(0)
      expect(ports.reingest).toHaveBeenCalledWith({ historyWeeks: 1 })
    } finally {
      store.close()
    }
  })

  it('wipes all without re-ingest when no window is given', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      const report = await cleanDatabase(store, { all: true }, ports)

      expect(report.harnesses).toEqual(['*'])
      expect(store.count()).toBe(0)
      expect(ports.reingest).not.toHaveBeenCalled()
    } finally {
      store.close()
    }
  })

  it('resumes ingestion even when the wipe throws', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts({
        project: vi.fn(async () => {
          throw new Error('projection blew up')
        }),
      })

      await expect(cleanDatabase(store, { harnesses: ['pi'] }, ports)).rejects.toThrow(
        'projection blew up',
      )
      expect(ports.resumeIngestion).toHaveBeenCalledTimes(1)
    } finally {
      store.close()
    }
  })

  it('fails closed when our receiver is up and cannot be paused', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts({
        pauseIngestion: vi.fn(async () => {
          throw new Error('pause refused')
        }),
      })

      await expect(cleanDatabase(store, { harnesses: ['pi'] }, ports)).rejects.toThrow(
        'pause refused',
      )
      expect(ports.resumeIngestion).not.toHaveBeenCalled()
      expect(store.getMetadata('last_clean_at')).toBeUndefined()
    } finally {
      store.close()
    }
  })

  it('skips re-ingest when asked', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      await cleanDatabase(store, { harnesses: ['pi'], reingestWeeks: null }, ports)

      expect(ports.reingest).not.toHaveBeenCalled()
      expect(ports.project).toHaveBeenCalledTimes(1)
      expect(ports.resumeIngestion).toHaveBeenCalledTimes(1)
    } finally {
      store.close()
    }
  })

  it('passes an explicit deeper window through to re-ingest', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      await cleanDatabase(store, { harnesses: ['pi'], reingestWeeks: 4 }, ports)

      expect(ports.reingest).toHaveBeenCalledWith(
        expect.objectContaining({ harnesses: ['pi'], historyWeeks: 4 }),
      )
    } finally {
      store.close()
    }
  })

  it('rejects an ambiguous scope combining --all with --harness instead of wiping all', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      await expect(
        cleanDatabase(store, { all: true, harnesses: ['pi'] }, ports),
      ).rejects.toThrow(/ambiguous/i)
      expect(ports.pauseIngestion).not.toHaveBeenCalled()
      expect(ports.resumeIngestion).not.toHaveBeenCalled()
    } finally {
      store.close()
    }
  })

  it('rejects an empty scope', async () => {
    const store = temporaryStore()
    try {
      await expect(cleanDatabase(store, {}, fakePorts())).rejects.toThrow(/scope/i)
    } finally {
      store.close()
    }
  })

  it.each([53, Number.MAX_SAFE_INTEGER])('rejects a reingest window of %p weeks without pausing (F6)', async (reingestWeeks) => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      await expect(cleanDatabase(store, { harnesses: ['pi'], reingestWeeks }, ports)).rejects.toThrow(
        /reingestWeeks/i,
      )
      expect(ports.pauseIngestion).not.toHaveBeenCalled()
      expect(ports.resumeIngestion).not.toHaveBeenCalled()
    } finally {
      store.close()
    }
  })

  it('accepts a reingest window of exactly one year', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      await cleanDatabase(store, { harnesses: ['pi'], reingestWeeks: 52 }, ports)

      expect(ports.reingest).toHaveBeenCalledWith(
        expect.objectContaining({ harnesses: ['pi'], historyWeeks: 52 }),
      )
    } finally {
      store.close()
    }
  })
})

describe('cleanDatabase folder-history import through the production ports (T5)', () => {
  beforeEach(() => {
    vi.mocked(pauseReceiver).mockClear()
    vi.mocked(resumeReceiver).mockClear()
    vi.mocked(projectCanonicalStore).mockClear()
    vi.mocked(importFolderHistory).mockClear()
    vi.mocked(refreshHarnessSources).mockClear()
  })

  it('does not import folder history when reingestWeeks is omitted', async () => {
    const store = temporaryStore()
    try {
      const report = await cleanDatabase(store, { harnesses: ['pi'] }, portsForClean(store))

      expect(report.reingested).toBe(false)
      expect(report.historyWeeks).toBeNull()
      expect(importFolderHistory).not.toHaveBeenCalled()
    } finally {
      store.close()
    }
  })

  it('imports folder history for an explicit window narrowed to the cleaned harnesses', async () => {
    const store = temporaryStore()
    try {
      const report = await cleanDatabase(store, { harnesses: ['pi'], reingestWeeks: 3 }, portsForClean(store))

      expect(report.reingested).toBe(true)
      expect(report.historyWeeks).toBe(3)
      expect(importFolderHistory).toHaveBeenCalledTimes(1)
      expect(vi.mocked(importFolderHistory).mock.calls[0]?.[0]).toBe(store)
      expect(vi.mocked(importFolderHistory).mock.calls[0]?.[1]).toEqual(
        expect.objectContaining({ weeks: 3, harnesses: ['pi'] }),
      )
    } finally {
      store.close()
    }
  })

  it('imports folder history for every harness, at the explicit window, when the clean wipes all', async () => {
    const store = temporaryStore()
    try {
      await cleanDatabase(store, { all: true, reingestWeeks: 2 }, portsForClean(store))

      expect(importFolderHistory).toHaveBeenCalledTimes(1)
      const options = vi.mocked(importFolderHistory).mock.calls[0]?.[1]
      expect(options?.weeks).toBe(2)
      expect(options?.harnesses ?? []).toHaveLength(0)
    } finally {
      store.close()
    }
  })

  it('drops harnesses with no descriptor before the wipe and import, so the clean resolves', async () => {
    const store = temporaryStore()
    try {
      // The wipe is spied, not the import, so the harness list that reaches
      // store.wipeHarnesses is observed directly: a descriptor-less harness must
      // be dropped before the wipe rather than passed through to it.
      const wipe = vi.spyOn(store, 'wipeHarnesses')

      const pending = cleanDatabase(
        store,
        { harnesses: ['pi', 'no-such-harness'], reingestWeeks: 1 },
        portsForClean(store),
      )
      await expect(pending).resolves.toBeDefined()

      expect(wipe).toHaveBeenCalledTimes(1)
      expect(wipe).toHaveBeenCalledWith(['pi'])
      expect((await pending).harnesses).toEqual(['pi'])
      expect(importFolderHistory).toHaveBeenCalledTimes(1)
      expect(vi.mocked(importFolderHistory).mock.calls[0]?.[1]).toEqual(
        expect.objectContaining({ harnesses: ['pi'] }),
      )

      // Narrowed scope, then the import — never the reverse.
      const first = (order: readonly number[]) => order[0] ?? Number.POSITIVE_INFINITY
      expect(first(wipe.mock.invocationCallOrder)).toBeLessThan(
        first(vi.mocked(importFolderHistory).mock.invocationCallOrder),
      )
    } finally {
      store.close()
    }
  })

  // Why this lives here: it guards clean.ts only. importFolderHistory is mocked
  // in this suite, so folder-import.test.ts is what covers the module's own
  // read/write of the setting; what matters here is that a clean neither reads
  // it into a decision nor writes it back.
  it('leaves the shared folder-import setting unchanged across a scoped clean with re-import', async () => {
    const store = temporaryStore()
    try {
      store.setMetadata(SETTING_KEYS.folderImportScheduled, 'on')

      await cleanDatabase(store, { harnesses: ['pi'], reingestWeeks: 2 }, portsForClean(store))

      expect(store.getMetadata(SETTING_KEYS.folderImportScheduled)).toBe('on')
      expect(importFolderHistory).toHaveBeenCalledTimes(1)
    } finally {
      store.close()
    }
  })

  it('leaves the shared folder-import setting unchanged after a scoped clean with no window', async () => {
    const store = temporaryStore()
    try {
      store.setMetadata(SETTING_KEYS.folderImportScheduled, 'on')

      await cleanDatabase(store, { harnesses: ['pi'] }, portsForClean(store))

      // No window means no import, so the setting must survive untouched.
      expect(store.getMetadata(SETTING_KEYS.folderImportScheduled)).toBe('on')
      expect(importFolderHistory).not.toHaveBeenCalled()
    } finally {
      store.close()
    }
  })

  it('pauses, wipes, projects, imports, then resumes, in that order', async () => {
    const store = temporaryStore()
    try {
      const wipe = vi.spyOn(store, 'wipeHarnesses')

      await cleanDatabase(store, { harnesses: ['pi'], reingestWeeks: 2 }, portsForClean(store))

      const first = (order: readonly number[]) => order[0] ?? Number.POSITIVE_INFINITY
      const pause = first(vi.mocked(pauseReceiver).mock.invocationCallOrder)
      const wiped = first(wipe.mock.invocationCallOrder)
      const projected = first(vi.mocked(projectCanonicalStore).mock.invocationCallOrder)
      const imported = first(vi.mocked(importFolderHistory).mock.invocationCallOrder)
      const resumed = first(vi.mocked(resumeReceiver).mock.invocationCallOrder)

      expect(pause).toBeLessThan(wiped)
      expect(wiped).toBeLessThan(projected)
      expect(projected).toBeLessThan(imported)
      expect(imported).toBeLessThan(resumed)
    } finally {
      store.close()
    }
  })
})
