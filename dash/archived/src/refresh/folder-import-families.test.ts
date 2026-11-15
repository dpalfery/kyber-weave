// Family labels in the folder-history import (issue #319 review, item 3).
//
// Two vocabularies meet at the harness boundary and the import used to understand
// only one of them. The registry stores per-front-end ids (`codex-cli`,
// `codex-desktop`, `codex-unclassified`) and `normalizeHarnessName` deliberately does
// NOT fold those onto `codex` - the split is what tells two clients apart in stored
// data (issue #182). Surfaces, however, show and accept the FAMILY label: the native
// tray sends the singular `harness` from its settings preference, which holds `codex`.
//
// `resolveDescriptors` used to call `descriptorFor(id)` directly, so a family label
// found no descriptor and threw - AFTER the route had already answered 202, and
// inside the child process, which then exited 1. The route validated the same name
// with a different resolver (`isImportableHarness`) and let it through.
//
// `refresh/registry.resolveHarnessScope` is the one resolver now, shared by the import,
// the clean wipe scope and the route validation, so these tests pin it at the import
// end: a family expands to ALL of its member descriptors, an unknown name is refused
// before any store work, and nothing ever widens to every harness.
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { HARNESS_DESCRIPTORS, resolveHarnessScope } from './registry.js'
import { importFolderHistory } from './folder-import.js'

const openStores: CanonStore[] = []

function openStore(): CanonStore {
  const store = new CanonStore(':memory:')
  openStores.push(store)
  return store
}

afterEach(() => {
  for (const store of openStores.splice(0)) store.close()
})

/** Stands in for refreshHarnessSources, so the assertions are on the descriptors handed over. */
function fakeRefresh() {
  return vi.fn<(...args: unknown[]) => Promise<unknown>>(async () => ({ exitCode: 0 }))
}

function descriptorsPassedTo(
  refresh: ReturnType<typeof fakeRefresh>,
  index = 0,
): readonly { harnessId: string }[] {
  const dependencies = refresh.mock.calls[index]![1] as {
    descriptors?: readonly { harnessId: string }[]
  }
  return dependencies.descriptors ?? []
}

describe('resolveHarnessScope: one resolver for every surface', () => {
  it('expands a family label to every member descriptor the registry holds', () => {
    const scope = resolveHarnessScope(['codex'])

    expect(scope.known).toEqual(['codex-cli', 'codex-desktop', 'codex-unclassified'])
    expect(scope.descriptors.map((entry) => entry.harnessId)).toEqual(scope.known)
    expect(scope.unknown).toEqual([])
  })

  it('resolves a direct descriptor id to itself', () => {
    const scope = resolveHarnessScope(['pi'])

    expect(scope.known).toEqual(['pi'])
    expect(scope.descriptors.map((entry) => entry.harnessId)).toEqual(['pi'])
  })

  it('reports what it cannot place, keeping the caller own spelling', () => {
    const scope = resolveHarnessScope(['bogus', 'codex'])

    expect(scope.unknown).toEqual(['bogus'])
    expect(scope.known).toEqual(['codex-cli', 'codex-desktop', 'codex-unclassified'])
  })

  it('deduplicates a family and a member named together, in first-seen order', () => {
    const scope = resolveHarnessScope(['codex', 'codex-cli', 'codex', 'pi'])

    expect(scope.known).toEqual(['codex-cli', 'codex-desktop', 'codex-unclassified', 'pi'])
    expect(scope.descriptors).toHaveLength(scope.known.length)
  })
})

describe('importFolderHistory: a family label is importable', () => {
  it('imports every member descriptor of the family, not none and not all', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(
      store,
      { harnesses: ['codex'], trigger: 'cli' },
      { refreshHarnessSources: refresh },
    )

    const ids = descriptorsPassedTo(refresh).map((entry) => entry.harnessId)
    expect(ids).toEqual(['codex-cli', 'codex-desktop', 'codex-unclassified'])
    expect(ids).not.toEqual(HARNESS_DESCRIPTORS.map((entry) => entry.harnessId))
  })

  it('never widens a family label to every harness, which an empty list means', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(
      store,
      { harnesses: ['codex'], trigger: 'cli' },
      { refreshHarnessSources: refresh },
    )

    expect(descriptorsPassedTo(refresh)).toHaveLength(3)
    expect(descriptorsPassedTo(refresh)).not.toContainEqual({ harnessId: 'pi' })
  })

  it('imports a mixed family and descriptor scope as the union, once each', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(
      store,
      { harnesses: ['codex', 'pi'], trigger: 'cli' },
      { refreshHarnessSources: refresh },
    )

    expect(descriptorsPassedTo(refresh).map((entry) => entry.harnessId)).toEqual([
      'codex-cli',
      'codex-desktop',
      'codex-unclassified',
      'pi',
    ])
  })

  it('tolerates the padding a hand-typed or clipboard-pasted name arrives with', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(
      store,
      { harnesses: ['  codex  '], trigger: 'cli' },
      { refreshHarnessSources: refresh },
    )

    expect(descriptorsPassedTo(refresh)).toHaveLength(3)
  })
})

describe('importFolderHistory: what it refuses', () => {
  it('refuses a name the registry cannot place, before any store write', async () => {
    const store = openStore()
    const refresh = fakeRefresh()
    const startRun = vi.spyOn(store, 'startRefreshRun')
    const setMeta = vi.spyOn(store, 'setMetadata')

    await expect(
      importFolderHistory(
        store,
        { harnesses: ['codex', 'bogus'], trigger: 'cli' },
        { refreshHarnessSources: refresh },
      ),
    ).rejects.toThrow(/bogus/)

    expect(refresh).not.toHaveBeenCalled()
    expect(startRun).not.toHaveBeenCalled()
    expect(setMeta).not.toHaveBeenCalled()
    expect(store.listRefreshRuns()).toHaveLength(0)
  })

  it('refuses an empty harness name rather than treating it as everything', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await expect(
      importFolderHistory(
        store,
        { harnesses: ['   '], trigger: 'cli' },
        { refreshHarnessSources: refresh },
      ),
    ).rejects.toThrow()
    expect(refresh).not.toHaveBeenCalled()
  })

  it('still imports every descriptor when no harness is named at all', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(store, { trigger: 'cli' }, { refreshHarnessSources: refresh })

    expect(descriptorsPassedTo(refresh).map((entry) => entry.harnessId)).toEqual(
      HARNESS_DESCRIPTORS.map((entry) => entry.harnessId),
    )
  })
})