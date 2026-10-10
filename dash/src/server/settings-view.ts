// The typed view of the shared settings over the ONE API (architecture rule R1).
//
// The tray, the web UI and the CLI all read and write the same four switches through
// `settings/shared-settings.ts`, which owns the storage and the on/off encoding. This
// module is only the JSON shape: JSON booleans in, `on`/`off` metadata rows out. Keeping
// the mapping here means the route holds no settings knowledge of its own.

import type { CanonStore } from '../canon/store.js'
import type { SettingKey } from '../settings/shared-settings.js'
import { SETTING_KEYS, readSetting, writeSetting } from '../settings/shared-settings.js'

export type SettingsView = {
  folderImportScheduled: boolean
  jobsPaused: boolean
  refreshCadenceMinutes: number
  receiverHosted: boolean
}

type SettingKeyName = keyof SettingsView

/** The JSON key ↔ metadata key pairing, in the order a client reads them. */
const KEYS: ReadonlyArray<readonly [SettingKeyName, SettingKey]> = [
  ['folderImportScheduled', SETTING_KEYS.folderImportScheduled],
  ['jobsPaused', SETTING_KEYS.jobsPaused],
  ['refreshCadenceMinutes', SETTING_KEYS.refreshCadenceMinutes],
  ['receiverHosted', SETTING_KEYS.receiverHosted],
]

/**
 * The cadence bounds mirror `shared-settings.ts` (the tray's minimum, and one day as the
 * ceiling). They are repeated rather than imported because the codec is private to the
 * settings module; a value that slips past this check is still refused by `writeSetting`
 * itself, so the route can never store something the codec does not recognise.
 */
const CADENCE_MIN_MINUTES = 1
const CADENCE_MAX_MINUTES = 1440

export function readSettings(store: CanonStore): SettingsView {
  // Read per key rather than through the KEYS table so each read keeps the type its own
  // codec returns: a `SettingKey`-wide read is `on | off | number`, which is the storage
  // encoding, not this view.
  return {
    folderImportScheduled: readSetting(store, SETTING_KEYS.folderImportScheduled) === 'on',
    jobsPaused: readSetting(store, SETTING_KEYS.jobsPaused) === 'on',
    refreshCadenceMinutes: readSetting(store, SETTING_KEYS.refreshCadenceMinutes),
    receiverHosted: readSetting(store, SETTING_KEYS.receiverHosted) === 'on',
  }
}

/**
 * Validate a whole patch before applying any of it. Returns undefined when the patch
 * carries an unknown key or a value of the wrong type, in which case nothing is written:
 * a partial application would leave a client believing it set every switch it asked for.
 */
export function parseSettingsPatch(body: Readonly<Record<string, unknown>>): SettingsView | undefined {
  const patch: SettingsView = {
    folderImportScheduled: false,
    jobsPaused: false,
    refreshCadenceMinutes: 0,
    receiverHosted: false,
  }
  for (const [name, raw] of Object.entries(body)) {
    if (name === 'refreshCadenceMinutes') {
      if (
        typeof raw !== 'number' ||
        !Number.isInteger(raw) ||
        raw < CADENCE_MIN_MINUTES ||
        raw > CADENCE_MAX_MINUTES
      ) {
        return undefined
      }
      patch.refreshCadenceMinutes = raw
      continue
    }
    if ((name === 'folderImportScheduled' || name === 'jobsPaused' || name === 'receiverHosted') && typeof raw === 'boolean') {
      patch[name] = raw
      continue
    }
    return undefined
  }
  return patch
}

/**
 * Apply only the keys the body actually carried, then answer the full object. Separate
 * from `parseSettingsPatch` because a PUT is partial: a body naming one switch must leave
 * the other three exactly as they were.
 */
export function applyPartialSettings(
  store: CanonStore,
  body: Readonly<Record<string, unknown>>,
  validated: SettingsView,
): SettingsView {
  for (const [name, key] of KEYS) {
    if (!(name in body)) continue
    if (name === 'refreshCadenceMinutes') {
      writeSetting(store, key, validated.refreshCadenceMinutes)
    } else {
      writeSetting(store, key, validated[name] ? 'on' : 'off')
    }
  }
  return readSettings(store)
}