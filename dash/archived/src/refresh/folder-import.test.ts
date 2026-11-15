// Folder import is the operator's explicit one-off history backfill. Its
// scheduled use is opt-in, so the gate fails closed; the CLI is always
// allowed because a person typed the command. The maintenance pass must
// purge and project without recording a refresh run, so it never looks like
// a source refresh in the audit trail.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { REFRESH_TRIGGERS } from '../canon/refresh-run.js'
import { CONTENT_PURGED_THROUGH_KEY } from '../canon/retention.js'
import { SETTING_KEYS, writeSetting } from '../settings/shared-settings.js'
import { HARNESS_DESCRIPTORS, descriptorFor } from './registry.js'
import {
  folderSourcesAllowed,
  importFolderHistory,
  runMaintenancePass,
} from './folder-import.js'

// The projection is wrapped rather than replaced so the real projection still
// runs. The probe records what the purge had already stamped at the moment
// projection started, which is the only observable proof of the ordering.
const projectionProbe = vi.hoisted(() => ({
  purgedThroughAtProjection: undefined as string | undefined,
  calls: 0,
}))

vi.mock('../canon/projection.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../canon/projection.js')>()
  return {
    ...actual,
    projectCanonicalStore: vi.fn(async (store: CanonStore) => {
      projectionProbe.calls += 1
      projectionProbe.purgedThroughAtProjection = store.getMetadata('content_purged_through')
      return actual.projectCanonicalStore(store)
    }),
  }
})

const openStores: CanonStore[] = []

function openStore(): CanonStore {
  const store = new CanonStore(':memory:')
  openStores.push(store)
  return store
}

afterEach(() => {
  for (const store of openStores.splice(0)) store.close()
  projectionProbe.calls = 0
  projectionProbe.purgedThroughAtProjection = undefined
})

// The fake stands in for refreshHarnessSources, so tests assert on the call
// arguments (store, dependencies, options) rather than on a real ingest.
function fakeRefresh() {
  return vi.fn<(...args: unknown[]) => Promise<unknown>>(async () => ({ exitCode: 0 }))
}

describe('REFRESH_TRIGGERS', () => {
  it('includes web so the web surface can start a refresh', () => {
    expect(REFRESH_TRIGGERS).toContain('web')
  })
})

describe('folderSourcesAllowed', () => {
  it('always allows the cli trigger, even when scheduled folder import is off', () => {
    const store = openStore()
    writeSetting(store, SETTING_KEYS.folderImportScheduled, 'off')

    expect(folderSourcesAllowed('cli', store)).toBe(true)
  })

  it.each(['scheduled', 'tray', 'web'] as const)(
    'denies %s while folder_import.scheduled is off (the default)',
    (trigger) => {
      const store = openStore()

      expect(folderSourcesAllowed(trigger, store)).toBe(false)
    },
  )

  it.each(['scheduled', 'tray', 'web'] as const)(
    'allows %s once folder_import.scheduled is on',
    (trigger) => {
      const store = openStore()
      writeSetting(store, SETTING_KEYS.folderImportScheduled, 'on')

      expect(folderSourcesAllowed(trigger, store)).toBe(true)
    },
  )

  it('fails closed when the stored setting is malformed', () => {
    const store = openStore()
    store.setMetadata('settings.folder_import.scheduled', 'banana')

    expect(folderSourcesAllowed('scheduled', store)).toBe(false)
    expect(folderSourcesAllowed('cli', store)).toBe(true)
  })
})

describe('importFolderHistory: window validation', () => {
  it('defaults to a one-week window', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(store, { trigger: 'cli' }, { refreshHarnessSources: refresh })

    expect(refresh).toHaveBeenCalledTimes(1)
    expect(refresh.mock.calls[0]![2]).toMatchObject({ historyWeeks: 1 })
  })

  it.each([1, 52])('accepts the %i-week boundary', async (weeks) => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(store, { weeks, trigger: 'cli' }, { refreshHarnessSources: refresh })

    expect(refresh.mock.calls[0]![2]).toMatchObject({ historyWeeks: weeks })
  })

  it.each([0, 53, -1, 1.5, Number.NaN, Number.POSITIVE_INFINITY])(
    'rejects weeks=%s before any store write or refresh',
    async (weeks) => {
      const store = openStore()
      const refresh = fakeRefresh()
      const startRun = vi.spyOn(store, 'startRefreshRun')
      const setMeta = vi.spyOn(store, 'setMetadata')

      await expect(
        importFolderHistory(store, { weeks, trigger: 'cli' }, { refreshHarnessSources: refresh }),
      ).rejects.toThrow()

      expect(refresh).not.toHaveBeenCalled()
      expect(startRun).not.toHaveBeenCalled()
      expect(setMeta).not.toHaveBeenCalled()
      expect(store.listRefreshRuns()).toHaveLength(0)
    },
  )
})

describe('importFolderHistory: harness narrowing', () => {
  it('passes only the requested harness descriptors to refreshHarnessSources', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(
      store,
      { harnesses: ['pi'], trigger: 'cli' },
      { refreshHarnessSources: refresh },
    )

    const dependencies = refresh.mock.calls[0]![1] as { descriptors?: readonly { harnessId: string }[] }
    expect(dependencies.descriptors?.map((d) => d.harnessId)).toEqual(['pi'])
    expect(dependencies.descriptors?.[0]).toEqual(descriptorFor('pi'))
  })

  it('passes every registry descriptor when no harness is named', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(store, { trigger: 'cli' }, { refreshHarnessSources: refresh })

    const dependencies = refresh.mock.calls[0]![1] as { descriptors?: readonly { harnessId: string }[] }
    expect(dependencies.descriptors?.map((d) => d.harnessId)).toEqual(
      HARNESS_DESCRIPTORS.map((d) => d.harnessId),
    )
  })

  it('hands the store to refreshHarnessSources unchanged', async () => {
    const store = openStore()
    const refresh = fakeRefresh()

    await importFolderHistory(store, { trigger: 'cli' }, { refreshHarnessSources: refresh })

    expect(refresh.mock.calls[0]![0]).toBe(store)
  })
})

describe('importFolderHistory: settings are never changed', () => {
  it.each(['on', 'off'] as const)(
    'leaves folder_import.scheduled at %s after an import',
    async (value) => {
      const store = openStore()
      writeSetting(store, SETTING_KEYS.folderImportScheduled, value)
      const refresh = fakeRefresh()

      await importFolderHistory(store, { trigger: 'cli' }, { refreshHarnessSources: refresh })

      expect(store.getMetadata('settings.folder_import.scheduled')).toBe(value)
    },
  )
})

describe('runMaintenancePass', () => {
  it('purges expired content before projecting, using the supplied clock', async () => {
    const store = openStore()
    const now = new Date('2026-10-10T12:00:00.000Z')

    await runMaintenancePass(store, now)

    expect(projectionProbe.calls).toBe(1)
    expect(projectionProbe.purgedThroughAtProjection).toBe('2026-09-26T12:00:00.000Z')
    expect(store.getMetadata(CONTENT_PURGED_THROUGH_KEY)).toBe('2026-09-26T12:00:00.000Z')
  })

  it('writes no refresh_run row, including when a run already exists', async () => {
    const store = openStore()
    store.startRefreshRun({
      id: 'existing-run',
      startedAt: '2026-10-09T00:00:00.000Z',
      pid: 1,
      trigger: 'cli',
    })
    const before = store.listRefreshRuns()

    await runMaintenancePass(store, new Date('2026-10-10T12:00:00.000Z'))

    expect(store.listRefreshRuns()).toEqual(before)
    expect(store.listRefreshRuns()).toHaveLength(1)
  })

  it('does not start a refresh run on an empty store', async () => {
    const store = openStore()
    const startRun = vi.spyOn(store, 'startRefreshRun')

    await runMaintenancePass(store, new Date('2026-10-10T12:00:00.000Z'))

    expect(startRun).not.toHaveBeenCalled()
    expect(store.latestRefreshRun('success')).toBeUndefined()
    expect(store.latestRefreshRun('running')).toBeUndefined()
  })
})
