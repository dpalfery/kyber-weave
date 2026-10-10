// Shared settings live in canon.db metadata so every surface (CLI, tray, web,
// receiver) reads one source. These tests pin the storage keys, the defaults,
// the validation bounds, and the fail-closed read of malformed stored values.

import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import {
  SETTING_KEYS,
  readSetting,
  writeSetting,
} from './shared-settings.js'

const openStores: CanonStore[] = []

function openStore(): CanonStore {
  const store = new CanonStore(':memory:')
  openStores.push(store)
  return store
}

afterEach(() => {
  for (const store of openStores.splice(0)) store.close()
})

describe('shared settings: storage keys', () => {
  it('stores each setting under its documented metadata key', () => {
    const store = openStore()

    writeSetting(store, SETTING_KEYS.folderImportScheduled, 'on')
    writeSetting(store, SETTING_KEYS.jobsPaused, 'on')
    writeSetting(store, SETTING_KEYS.refreshCadenceMinutes, 30)
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')

    expect(store.getMetadata('settings.folder_import.scheduled')).toBe('on')
    expect(store.getMetadata('settings.jobs.paused')).toBe('on')
    expect(store.getMetadata('settings.jobs.refresh_cadence_minutes')).toBe('30')
    expect(store.getMetadata('settings.receiver.hosted')).toBe('on')
  })
})

describe('shared settings: defaults', () => {
  it('reads every setting as its default when nothing is stored', () => {
    const store = openStore()

    expect(readSetting(store, SETTING_KEYS.folderImportScheduled)).toBe('off')
    expect(readSetting(store, SETTING_KEYS.jobsPaused)).toBe('off')
    expect(readSetting(store, SETTING_KEYS.refreshCadenceMinutes)).toBe(5)
    expect(readSetting(store, SETTING_KEYS.receiverHosted)).toBe('off')
  })
})

describe('shared settings: round trip', () => {
  it.each([
    [SETTING_KEYS.folderImportScheduled, 'on'],
    [SETTING_KEYS.jobsPaused, 'on'],
    [SETTING_KEYS.receiverHosted, 'on'],
  ] as const)('round-trips a valid on/off value for %s', (key, value) => {
    const store = openStore()

    writeSetting(store, key, value)

    expect(readSetting(store, key)).toBe(value)
  })

  it('round-trips a cadence at both ends of its range', () => {
    const store = openStore()

    writeSetting(store, SETTING_KEYS.refreshCadenceMinutes, 1)
    expect(readSetting(store, SETTING_KEYS.refreshCadenceMinutes)).toBe(1)

    writeSetting(store, SETTING_KEYS.refreshCadenceMinutes, 1440)
    expect(readSetting(store, SETTING_KEYS.refreshCadenceMinutes)).toBe(1440)
  })
})

describe('shared settings: validation on write', () => {
  it.each([0, -5, 1441, 1.5, Number.NaN, Number.POSITIVE_INFINITY])(
    'rejects cadence %s and leaves the stored value unchanged',
    (minutes) => {
      const store = openStore()
      writeSetting(store, SETTING_KEYS.refreshCadenceMinutes, 15)

      expect(() => writeSetting(store, SETTING_KEYS.refreshCadenceMinutes, minutes)).toThrow()

      expect(readSetting(store, SETTING_KEYS.refreshCadenceMinutes)).toBe(15)
    },
  )

  it('rejects an unrecognised on/off value without storing it', () => {
    const store = openStore()

    expect(() => writeSetting(store, SETTING_KEYS.folderImportScheduled, 'yes' as never)).toThrow()

    expect(store.getMetadata('settings.folder_import.scheduled')).toBeUndefined()
    expect(readSetting(store, SETTING_KEYS.folderImportScheduled)).toBe('off')
  })

  // Every on/off key shares one validator; each must refuse an unrecognised
  // value rather than persisting it for a later read to reinterpret.
  it.each([
    [SETTING_KEYS.jobsPaused, 'settings.jobs.paused'],
    [SETTING_KEYS.receiverHosted, 'settings.receiver.hosted'],
  ] as const)('rejects an unrecognised on/off value for %s without storing it', (key, metadataKey) => {
    const store = openStore()

    expect(() => writeSetting(store, key, 'yes' as never)).toThrow()

    expect(store.getMetadata(metadataKey)).toBeUndefined()
    expect(readSetting(store, key)).toBe('off')
  })
})

describe('shared settings: fail-closed reads of malformed stored values', () => {
  // A stored value outside 'on'/'off' must read as the default, never be
  // coerced, so a hand-edited or legacy row cannot switch a feature on.
  const onOffKeys = [
    [SETTING_KEYS.folderImportScheduled, 'settings.folder_import.scheduled'],
    [SETTING_KEYS.jobsPaused, 'settings.jobs.paused'],
    [SETTING_KEYS.receiverHosted, 'settings.receiver.hosted'],
  ] as const
  const malformedOnOff = ['yes', 'ON', 'true', '', ' on']

  it.each(
    onOffKeys.flatMap(([key, metadataKey]) =>
      malformedOnOff.map((raw) => [key, metadataKey, raw] as const),
    ),
  )('reads a stored value of %j under %s as off', (key, metadataKey, raw) => {
    const store = openStore()
    store.setMetadata(metadataKey, raw)

    expect(readSetting(store, key)).toBe('off')
  })

  it.each(['0', '1441', '5.5', 'five', '', 'NaN'])(
    'reads a stored cadence of %j as the default',
    (raw) => {
      const store = openStore()
      store.setMetadata('settings.jobs.refresh_cadence_minutes', raw)

      expect(readSetting(store, SETTING_KEYS.refreshCadenceMinutes)).toBe(5)
    },
  )
})

describe('shared settings: survive a wipe-all', () => {
  it('keeps every stored value across store.wipeAll()', () => {
    const store = openStore()
    writeSetting(store, SETTING_KEYS.folderImportScheduled, 'on')
    writeSetting(store, SETTING_KEYS.jobsPaused, 'on')
    writeSetting(store, SETTING_KEYS.refreshCadenceMinutes, 60)
    writeSetting(store, SETTING_KEYS.receiverHosted, 'on')

    store.wipeAll()

    expect(readSetting(store, SETTING_KEYS.folderImportScheduled)).toBe('on')
    expect(readSetting(store, SETTING_KEYS.jobsPaused)).toBe('on')
    expect(readSetting(store, SETTING_KEYS.refreshCadenceMinutes)).toBe(60)
    expect(readSetting(store, SETTING_KEYS.receiverHosted)).toBe('on')
  })
})
