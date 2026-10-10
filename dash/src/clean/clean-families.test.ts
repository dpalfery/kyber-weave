// Family labels in the clean wipe scope (issue #319 review, item 3).
//
// `dash clean --harness codex` is a command a person types, and the tray's clean
// control posts the harness preference it holds - which is a FAMILY label (`codex`),
// not a registry descriptor id. `canonicalScope` used to filter through `descriptorFor`,
// which finds nothing for a family, and threw 'scope selects no known harness': a
// regression on the CLI path for the one harness family with three stored front-ends.
//
// The subtle half is what a fix must NOT do. `CanonStore.wipeHarnesses` normalizes the
// names it is given and then matches them against the raw names actually present in the
// stored rows. `normalizeHarnessName('codex')` is still `codex`, and no stored row is
// called `codex` - they are `codex-cli`, `codex-desktop` and `codex-unclassified`. So a
// scope handed straight to the wipe deletes NOTHING and reports a successful clean.
// These tests run against a real store file so that silent no-op is observable rather
// than assumed, and so `reingested` cannot be earned by a wipe that wiped nothing.
//
// `clean-scope.test.ts` remains the approved contract for unknown scopes (mixed
// ['bogus','pi'] wipes ['pi'] only, all-unknown throws); nothing here changes it.
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

// The clean pipeline reaches the folder import through this seam, and the real module
// closes an import cycle with clean.js, so it is mocked rather than spread - the same
// construction clean-scope.test.ts uses.
vi.mock('../refresh/folder-import.js', () => ({
  importFolderHistory: vi.fn(async () => {}),
}))

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
  vi.restoreAllMocks()
})

function temporaryStore(): CanonStore {
  const root = mkdtempSync(join(tmpdir(), 'kyber-clean-families-'))
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

function seedRecord(store: CanonStore, spanId: string, harness: string): void {
  store.upsert({
    spanId,
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    source: 'test:clean-families',
    harness,
    sessionId: `${harness}-session`,
    name: 'probe turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-10-01T12:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: {
      freshInput: 10,
      cacheRead: 0,
      cacheCreation: 0,
      output: 5,
      reasoning: 0,
      reportedInput: 10,
      reportedOutput: 5,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
  })
}

/**
 * The harnesses still readable in the store, one row per distinct value. Read through the
 * store's own API rather than its private handle: this is what a later query would see.
 */
function storedHarnesses(store: CanonStore): string[] {
  const every = [
    'codex-cli',
    'codex-desktop',
    'codex-unclassified',
    'pi',
    'cursor',
  ]
  return every.filter((harness) => store.listRecordsByHarness([harness], 1).length > 0).sort()
}

describe('cleanDatabase: a family label is a wipe scope, not an unknown', () => {
  it('expands codex to its three stored front-ends and deletes those rows', async () => {
    const store = temporaryStore()
    seedRecord(store, 'span-codex-cli', 'codex-cli')
    seedRecord(store, 'span-codex-desktop', 'codex-desktop')
    seedRecord(store, 'span-codex-unclassified', 'codex-unclassified')
    seedRecord(store, 'span-pi', 'pi')
    const wipeHarnesses = vi.spyOn(store, 'wipeHarnesses')

    const report = await cleanDatabase(store, { harnesses: ['codex'] }, fakePorts())

    expect(wipeHarnesses).toHaveBeenCalledWith(['codex-cli', 'codex-desktop', 'codex-unclassified'])
    expect(report.harnesses).toEqual(['codex-cli', 'codex-desktop', 'codex-unclassified'])
    // The proof the scope was not silently empty: every codex row is gone and pi is not.
    expect(storedHarnesses(store)).toEqual(['pi'])
    expect(report.wipe.records).toBe(3)
  })

  it('reports the member ids, never the bare family label, so the report is checkable', async () => {
    const store = temporaryStore()
    seedRecord(store, 'span-codex-cli', 'codex-cli')

    const report = await cleanDatabase(store, { harnesses: ['codex'] }, fakePorts())

    expect(report.harnesses).not.toContain('codex')
    expect(report.harnesses.every((id) => id.startsWith('codex-'))).toBe(true)
  })

  it('wipes a family and a named member named together exactly once', async () => {
    const store = temporaryStore()
    seedRecord(store, 'span-codex-cli', 'codex-cli')
    seedRecord(store, 'span-pi', 'pi')

    const report = await cleanDatabase(
      store,
      { harnesses: ['codex', 'codex-cli', 'pi'] },
      fakePorts(),
    )

    expect(report.harnesses).toEqual(['codex-cli', 'codex-desktop', 'codex-unclassified', 'pi'])
    expect(storedHarnesses(store)).toEqual([])
  })

  it('silently drops an unknown name beside a family label, and still wipes the family', async () => {
    // The mixed-scope silent drop is approved behaviour and stays: a name nobody can
    // place must not widen the scope to everything. What is pinned here is that the
    // family label beside it is NOT treated as unplaceable - it still wipes.
    const store = temporaryStore()
    seedRecord(store, 'span-codex-cli', 'codex-cli')
    seedRecord(store, 'span-pi', 'pi')
    const wipeAll = vi.spyOn(store, 'wipeAll')

    const report = await cleanDatabase(
      store,
      { harnesses: ['codex', 'bogus-nonexistent'] },
      fakePorts(),
    )

    expect(wipeAll).not.toHaveBeenCalled()
    expect(report.harnesses).toEqual(['codex-cli', 'codex-desktop', 'codex-unclassified'])
    expect(report.harnesses).not.toContain('bogus-nonexistent')
    expect(storedHarnesses(store)).toEqual(['pi'])
  })
})

describe('cleanDatabase: re-ingest follows the same expansion', () => {
  it('imports the expanded member ids, not the label and not every harness', async () => {
    const store = temporaryStore()
    seedRecord(store, 'span-codex-cli', 'codex-cli')
    const ports = fakePorts()

    const report = await cleanDatabase(store, { harnesses: ['codex'], reingestWeeks: 2 }, ports)

    expect(ports.reingest).toHaveBeenCalledWith({
      harnesses: ['codex-cli', 'codex-desktop', 'codex-unclassified'],
      historyWeeks: 2,
    })
    expect(report.reingested).toBe(true)
  })

  it('never earns `reingested` from a scope that wiped nothing', async () => {
    const store = temporaryStore()
    seedRecord(store, 'span-codex-cli', 'codex-cli')
    seedRecord(store, 'span-pi', 'pi')
    const imported: Array<{ harnesses?: readonly string[] }> = []
    const ports = fakePorts({
      reingest: async (input: { harnesses?: readonly string[]; historyWeeks: number }) => {
        imported.push(input)
      },
    })

    const report = await cleanDatabase(store, { harnesses: ['codex'], reingestWeeks: 1 }, ports)

    // A re-ingest narrower than the wipe would silently undelete... nothing: the wipe is
    // what decides the scope, so the import must be at least as wide.
    expect(imported).toHaveLength(1)
    expect(imported[0]!.harnesses).toEqual(report.harnesses)
  })

  it('does not import when no window was asked for, expanded or not', async () => {
    const store = temporaryStore()
    seedRecord(store, 'span-codex-cli', 'codex-cli')
    const ports = fakePorts()

    const report = await cleanDatabase(store, { harnesses: ['codex'] }, ports)

    expect(ports.reingest).not.toHaveBeenCalled()
    expect(report.reingested).toBe(false)
    expect(report.historyWeeks).toBeNull()
  })
})