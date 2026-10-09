// Central clean for KyberDash (issue #312, plan T3 RED): pause ingestion,
// wipe the store for a harness scope (or all), project the derived caches,
// re-ingest the scope from source logs, and resume ingestion — resuming in a
// `finally` so a failing wipe cannot wedge the collector shut. Thin callers
// (the `dash clean` CLI and `POST /api/kyber/clean`) reach this module; no
// clean logic lives in either surface. This module does not exist yet — the
// test below fails to import it until T4 lands the implementation.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { CanonStore } from '../canon/store.js'
import { cleanDatabase } from './clean.js'

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
  it('pauses, wipes, projects, re-ingests the default 7-day window, then resumes', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      const report = await cleanDatabase(store, { harnesses: ['pi'] }, ports)

      expect(report.harnesses).toEqual(['pi'])
      expect(ports.pauseIngestion).toHaveBeenCalledTimes(1)
      expect(ports.reingest).toHaveBeenCalledWith(
        expect.objectContaining({ harnesses: ['pi'], historyWeeks: 1 }),
      )
      expect(ports.project).toHaveBeenCalledTimes(1)
      expect(ports.resumeIngestion).toHaveBeenCalledTimes(1)
      expect(store.getMetadata('last_clean_at')).toBeDefined()
    } finally {
      store.close()
    }
  })

  it('wipes all when asked, and passes no harness filter to re-ingest', async () => {
    const store = temporaryStore()
    try {
      const ports = fakePorts()

      const report = await cleanDatabase(store, { all: true }, ports)

      expect(report.harnesses).toEqual(['*'])
      expect(store.count()).toBe(0)
      expect(ports.reingest).toHaveBeenCalledWith({ historyWeeks: 1 })
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

  it('rejects an empty scope', async () => {
    const store = temporaryStore()
    try {
      await expect(cleanDatabase(store, {}, fakePorts())).rejects.toThrow(/scope/i)
    } finally {
      store.close()
    }
  })
})
