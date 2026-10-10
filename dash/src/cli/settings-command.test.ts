// RED tests for `dash settings [show | set <key> <value>] [--json]`
// (docs/archive/plans/2026-10-09-issue-319-folder-import-opt-in.md, A6 / T9).
//
// The CLI is a thin caller over `src/settings/shared-settings.ts`: it owns argument
// parsing, exit codes, and output shape, and nothing else. That split is what these
// tests pin, so they deliberately assert through the injected seams (`write`,
// `writeError`, `createStore`) rather than spawning the CLI — the same wiring the tray
// and the server rely on, and no dependency on this machine's ~/.kyberdash/canon.db.
//
// The command does not exist yet. Every test below fails until the registration lands.

import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { Command, CommanderError } from 'commander'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { SETTING_KEYS, readSetting, type SettingKey } from '../settings/shared-settings.js'
import { registerKyberCommands } from './register.js'

/**
 * The three settings that share the on/off codec, and the one that shares the
 * bounded-integer codec. Spelled out here rather than derived from the codec table so
 * that a change to the codec names cannot quietly shrink the surface this file covers.
 */
const ON_OFF: readonly { short: string; key: SettingKey; other: 'on' | 'off' }[] = [
  { short: 'folder_import.scheduled', key: SETTING_KEYS.folderImportScheduled, other: 'on' },
  { short: 'jobs.paused', key: SETTING_KEYS.jobsPaused, other: 'on' },
  { short: 'receiver.hosted', key: SETTING_KEYS.receiverHosted, other: 'on' },
]
// A loop over an empty array asserts nothing and still reports success, so the
// coverage counts are pinned: these tables only claim to cover what they list here.
describe('dash settings: the coverage tables are not silently empty', () => {
  it('lists the three on/off keys and the four defaults', () => {
    expect(ON_OFF).toHaveLength(3)
    expect(Object.keys(DEFAULTS)).toHaveLength(4)
    expect(DEFAULTS[CADENCE.short]).toBeDefined()
  })
})

const CADENCE: { short: string; key: SettingKey } = {
  short: 'jobs.refresh_cadence_minutes',
  key: SETTING_KEYS.refreshCadenceMinutes,
}
/** Defaults as `shared-settings.ts` defines them: the switch is opt-in. */
const DEFAULTS: Record<string, string | number> = {
  'folder_import.scheduled': 'off',
  'jobs.paused': 'off',
  'jobs.refresh_cadence_minutes': 5,
  'receiver.hosted': 'off',
}

const roots: string[] = []
const opened: CanonStore[] = []

afterEach(() => {
  for (const store of opened.splice(0)) {
    try {
      store.close()
    } catch {
      // The command closes the store it opened; a second close is not an error here.
    }
  }
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true })
})

/**
 * One `kyberdash dash settings ...` invocation against a private canon.db.
 *
 * The injected `createStore` deliberately ignores the path the command resolved — with no
 * `--db` given that is the sandbox home's default canon.db, not this harness's file — and
 * opens the harness's private temp database instead. Without that the command would write
 * one database and every assertion below would read another, so round-trips would appear
 * to lose writes the command had actually made. The file on disk is still the only thing
 * the two invocations in a round-trip test share: no in-memory fake could make a write
 * look durable when it is not.
 */
function createHarness(): {
  db: string
  run: (...args: string[]) => Promise<{ stdout: string; stderr: string; exitCode: number }>
} {
  const root = mkdtempSync(join(tmpdir(), 'kyber-settings-'))
  roots.push(root)
  const db = join(root, 'canon.db')
  const stdout: string[] = []
  const stderr: string[] = []
  const program = new Command()
  program.exitOverride()
  registerKyberCommands(program, {
    write: line => { stdout.push(line) },
    writeError: line => { stderr.push(line) },
    createStore: () => {
      const store = new CanonStore(db)
      opened.push(store)
      return store
    },
  })

  return {
    db,
    async run(...args: string[]) {
      // Each invocation reports only its own output: `set` echoes a confirmation line,
      // and a round-trip test that concatenated it with the following `show --json` would
      // hand the JSON parser something that is not JSON.
      stdout.length = 0
      stderr.length = 0
      const previousExitCode = process.exitCode
      process.exitCode = undefined
      try {
        await program.parseAsync(['node', 'kyberdash', 'dash', 'settings', ...args])
      } catch (error) {
        // A usage error may surface either as commander's thrown `CommanderError` (the
        // path `dash clean` and `dash refresh` use for exit 2) or as a plain
        // `process.exitCode`. Both express the same contract, so normalise them here
        // rather than pinning the test to one mechanism; anything else is a real failure
        // and is rethrown.
        if (!(error instanceof CommanderError)) {
          process.exitCode = previousExitCode
          throw error
        }
        const exitCode = error.exitCode
        process.exitCode = previousExitCode
        return { stdout: stdout.join('\n'), stderr: stderr.join('\n'), exitCode }
      }
      const exitCode = process.exitCode
      process.exitCode = previousExitCode
      return { stdout: stdout.join('\n'), stderr: stderr.join('\n'), exitCode: exitCode ?? 0 }
    },
  }
}

/** Read a setting back through the store, from outside the command under test. */
function readBack(db: string, key: SettingKey): unknown {
  const store = new CanonStore(db)
  opened.push(store)
  return readSetting(store, key)
}

/** Read the raw metadata row, so "nothing written" is checked at the storage layer. */
function rawMetadata(db: string, key: SettingKey): string | undefined {
  const store = new CanonStore(db)
  opened.push(store)
  return store.getMetadata(key)
}

/**
 * `--json` output is a flat map from setting name to value. The command's own key
 * spelling is not what these tests are about, so lookup is by substring: the assertions
 * hold whether the command emits the short name or the `settings.`-prefixed storage key.
 */
function jsonEntries(text: string): Record<string, unknown> {
  const parsed: unknown = JSON.parse(text)
  expect(parsed).toBeTypeOf('object')
  expect(parsed).not.toBeNull()
  return parsed as Record<string, unknown>
}

function jsonValueFor(entries: Record<string, unknown>, name: string): unknown {
  // `set` accepts both `jobs.paused` and `settings.jobs.paused`, and the read-back test
  // uses the spelling it wrote. `show --json` emits the short spelling, so the
  // `settings.` prefix is dropped before matching -- the tail segment identifies the
  // setting either way, and the value compared is the one that was written.
  const needle = name.replace(/^settings\./, '')
  const match = Object.entries(entries).find(([key]) => key.includes(needle))
  expect(match, `no entry named ${needle} in ${JSON.stringify(entries)}`).toBeDefined()
  return match![1]
}

describe('dash settings: registration', () => {
  it('is a subcommand of the dash group with show and set, and --json on show', () => {
    const program = new Command()
    registerKyberCommands(program)

    const dash = program.commands.find(command => command.name() === 'dash')
    const settings = dash?.commands.find(command => command.name() === 'settings')
    expect(settings).toBeDefined()
    const subs = settings!.commands.map(command => command.name())
    expect(subs).toContain('show')
    expect(subs).toContain('set')
    const show = settings!.commands.find(command => command.name() === 'show')!
    expect(show.options.map(option => option.long)).toContain('--json')
  })
})

describe('dash settings show', () => {
  it('prints all four keys with their defaults when nothing is stored', async () => {
    const harness = createHarness()

    const result = await harness.run('show')

    expect(result.exitCode).toBe(0)
    for (const [name, value] of Object.entries(DEFAULTS)) {
      // Matched per line rather than against the whole output: the command owns its
      // layout (plain, aligned, or table), and only the pairing of a key with its own
      // value is a contract. A global substring search would pass even if the values
      // were printed in the wrong order, which is exactly the mistake worth catching.
      const line = result.stdout.split('\n').find(text => text.includes(name))
      expect(line, `no line reported ${name}\n${result.stdout}`).toBeDefined()
      expect(line).toContain(String(value))
    }
  })

  it('reports the same four defaults as a JSON object under --json', async () => {
    const harness = createHarness()

    const result = await harness.run('show', '--json')

    expect(result.exitCode).toBe(0)
    const entries = jsonEntries(result.stdout)
    for (const [name, value] of Object.entries(DEFAULTS)) {
      expect(jsonValueFor(entries, name)).toEqual(value)
    }
  })
})

describe('dash settings set', () => {
  // Both spellings are accepted because the short form is what an operator types and the
  // prefixed form is what `SETTING_KEYS` exposes — the tray and the HTTP API both see the
  // prefixed keys, and a value pasted from there must not be rejected.
  const ON_OFF_CASES = ON_OFF.flatMap(entry => [
    [entry.short, entry.key, 'on', 'on'] as const,
    [entry.short, entry.key, 'off', 'off'] as const,
    [entry.key, entry.key, 'on', 'on'] as const,
    [entry.key, entry.key, 'off', 'off'] as const,
  ])

  it.each(ON_OFF_CASES)('stores %s = %s', async (name, key, value, expected) => {
    const harness = createHarness()

    const result = await harness.run('set', name, value)

    expect(result.exitCode).toBe(0)
    expect(readBack(harness.db, key)).toBe(expected)
  })

  it.each([1, 5, 60, 1440])('stores %s = %d', async (value) => {
    const harness = createHarness()

    const result = await harness.run('set', CADENCE.short, String(value))

    expect(result.exitCode).toBe(0)
    expect(readBack(harness.db, CADENCE.key)).toBe(value)
  })

  it.each([1, 60, 1440])('stores the prefixed cadence key %s = %d', async (value) => {
    const harness = createHarness()

    const result = await harness.run('set', CADENCE.key, String(value))

    expect(result.exitCode).toBe(0)
    expect(readBack(harness.db, CADENCE.key)).toBe(value)
  })

  it.each(ON_OFF_CASES)('reads %s = %s back through show --json', async (name, _key, value) => {
    const harness = createHarness()
    expect((await harness.run('set', name, value)).exitCode).toBe(0)

    const entries = jsonEntries((await harness.run('show', '--json')).stdout)

    expect(jsonValueFor(entries, name)).toBe(value)
  })

  it('leaves the other three settings untouched when one is set', async () => {
    const harness = createHarness()

    await harness.run('set', 'jobs.paused', 'on')

    const entries = jsonEntries((await harness.run('show', '--json')).stdout)
    expect(jsonValueFor(entries, 'jobs.paused')).toBe('on')
    for (const entry of ON_OFF) {
      if (entry.short !== 'jobs.paused') expect(jsonValueFor(entries, entry.short)).toBe(DEFAULTS[entry.short])
    }
    expect(jsonValueFor(entries, CADENCE.short)).toBe(DEFAULTS[CADENCE.short])
  })
})

describe('dash settings: rejected input exits 2 and writes nothing', () => {
  // Exit 2, not 1: an unknown key or a bad value is a usage error, and the tray and the
  // HTTP layer both treat 2 as "the caller asked for something impossible" rather than
  // "something went wrong". Nothing may be written — a partially applied bad value would
  // be reinterpreted by every later read.
  it.each([
    ['settings.nonexistent', 'on'],
    ['nonexistent', 'on'],
    ['folder_import.scheduled_typo', 'on'],
    ['SETTINGS.FOLDER_IMPORT.SCHEDULED', 'on'],
  ])('rejects the unknown key %s', async (name) => {
    const harness = createHarness()

    const result = await harness.run('set', name, 'on')

    expect(result.exitCode).toBe(2)
    for (const entry of [...ON_OFF, CADENCE]) {
      expect(rawMetadata(harness.db, entry.key), entry.short).toBeUndefined()
    }
  })

  it.each(['0', '1441', 'abc', '-5', '1.5', ''])(
    'rejects the cadence value %j without overwriting a stored one',
    async (value) => {
      const harness = createHarness()
      // Seeded first so the assertion is "unchanged", not "absent": a rejected write that
      // clobbered an existing value would otherwise look identical to a clean rejection.
      // The seed is checked through the store rather than through its exit code so that a
      // failure here names the value that did not land.
      await harness.run('set', CADENCE.short, '30')
      expect(readBack(harness.db, CADENCE.key)).toBe(30)

      const result = await harness.run('set', CADENCE.short, value)

      expect(result.exitCode).toBe(2)
      expect(readBack(harness.db, CADENCE.key)).toBe(30)
      expect(rawMetadata(harness.db, CADENCE.key)).toBe('30')
    },
  )

  it.each(ON_OFF.flatMap(_entry => ['yes', 'true', '1', 'ON', 'onoff', ' off'] as const))
    ('rejects %j for every on/off key without overwriting a stored one', async value => {
      const harness = createHarness()
      for (const entry of ON_OFF) {
        await harness.run('set', entry.short, 'on')
        expect(readBack(harness.db, entry.key), entry.short).toBe('on')
      }

      for (const entry of ON_OFF) {
        const result = await harness.run('set', entry.short, value)

        expect(result.exitCode, `${entry.short} ${value}`).toBe(2)
        expect(readBack(harness.db, entry.key), `${entry.short} ${value}`).toBe('on')
      }
    })
})