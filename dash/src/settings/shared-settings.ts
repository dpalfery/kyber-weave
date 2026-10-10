// Shared settings, stored in canon.db metadata.
//
// The CLI, the tray, the web surface, and the receiver all read the same switches. Keeping
// them in the store rather than in a per-surface file means a change made in one place is
// the change every other surface sees, and a wipe-all leaves them alone because they
// describe how the operator wants the tool to behave, not the captured data.
//
// Reads fail closed. A stored value that is not a recognised value reads as the default
// instead of being coerced, so a hand-edited or legacy row cannot quietly switch a feature
// on. Writes are the opposite: an unrecognised value throws before anything is stored,
// so a bad value never reaches a later read to be reinterpreted.

import type { CanonStore } from '../canon/store.js'

export const SETTING_KEYS = {
  folderImportScheduled: 'settings.folder_import.scheduled',
  jobsPaused: 'settings.jobs.paused',
  refreshCadenceMinutes: 'settings.jobs.refresh_cadence_minutes',
  receiverHosted: 'settings.receiver.hosted',
} as const

export type OnOff = 'on' | 'off'

export type SettingValues = {
  readonly [SETTING_KEYS.folderImportScheduled]: OnOff
  readonly [SETTING_KEYS.jobsPaused]: OnOff
  readonly [SETTING_KEYS.refreshCadenceMinutes]: number
  readonly [SETTING_KEYS.receiverHosted]: OnOff
}

export type SettingKey = keyof SettingValues

/** Bounds match the tray's minimum and the one-day ceiling a refresh cadence is useful within. */
const CADENCE_MIN_MINUTES = 1
const CADENCE_MAX_MINUTES = 1440

type SettingCodec<T> = {
  readonly defaultValue: T
  readonly accepts: (value: unknown) => value is T
  readonly encode: (value: T) => string
  readonly decode: (raw: string) => T | undefined
}

const onOffCodec: SettingCodec<OnOff> = {
  defaultValue: 'off',
  accepts: (value): value is OnOff => value === 'on' || value === 'off',
  encode: (value) => value,
  decode: (raw) => (raw === 'on' || raw === 'off' ? raw : undefined),
}

// Digits only: Number() alone would accept '', ' 5', and '1e1', none of which a person
// means as a minute count.
const cadenceCodec: SettingCodec<number> = {
  defaultValue: 5,
  accepts: (value): value is number =>
    typeof value === 'number' &&
    Number.isInteger(value) &&
    value >= CADENCE_MIN_MINUTES &&
    value <= CADENCE_MAX_MINUTES,
  encode: (value) => String(value),
  decode: (raw) => {
    if (!/^\d+$/.test(raw)) return undefined
    const minutes = Number(raw)
    return cadenceCodec.accepts(minutes) ? minutes : undefined
  },
}

const CODECS: { readonly [K in SettingKey]: SettingCodec<SettingValues[K]> } = {
  [SETTING_KEYS.folderImportScheduled]: onOffCodec,
  [SETTING_KEYS.jobsPaused]: onOffCodec,
  [SETTING_KEYS.refreshCadenceMinutes]: cadenceCodec,
  [SETTING_KEYS.receiverHosted]: onOffCodec,
}

export function readSetting<K extends SettingKey>(store: CanonStore, key: K): SettingValues[K] {
  const codec = CODECS[key]
  const raw = store.getMetadata(key)
  if (raw === undefined) return codec.defaultValue
  return codec.decode(raw) ?? codec.defaultValue
}

export function writeSetting<K extends SettingKey>(
  store: CanonStore,
  key: K,
  value: SettingValues[K],
): void {
  const codec = CODECS[key]
  if (!codec.accepts(value)) {
    throw new Error(`setting '${key}' rejected: value is not a recognised setting value`)
  }
  store.setMetadata(key, codec.encode(value))
}
