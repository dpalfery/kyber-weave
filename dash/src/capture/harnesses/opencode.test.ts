import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

import { readJsonKeys } from '../edit-json.js'
import { runCapture } from '../index.js'
import { runCaptureAction } from '../actions.js'
import {
  receiptPathForHome,
  saveCaptureReceipt,
  sha256Hex,
  loadCaptureReceipt,
  type ReceiptPrior,
} from '../receipt.js'
import { readContentConsent, resolveConsentPath } from './opencode-plugin/consent.js'
import {
  legacyOpencodeFlagRecord,
  migrateLegacyOpencodeFlag,
  opencodeHarness,
  OPENCODE_PLUGIN_CONFIG_KEYS,
  OPENCODE_PLUGIN_ID,
} from './opencode.js'

const temporaryHomes: string[] = []

/**
 * The key older KyberDash builds wrote into opencode.jsonc. Enable no longer writes it. Enable and
 * disable only put back the value a capture receipt recorded for it.
 */
const OPENCODE_OTEL_FLAG_KEY = 'experimental.openTelemetry'

/** The OTLP endpoint variable. Status no longer reports it: OpenCode does not read it from opencode.jsonc. */
const STATUS_ENV_KEY = 'OTEL_EXPORTER_OTLP_ENDPOINT' as const

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
  vi.unstubAllEnvs()
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-opencode-capture-'))
  temporaryHomes.push(home)
  return home
}

function opencodeConfigPath(home: string): string {
  return join(home, '.config', 'opencode', 'opencode.jsonc')
}

function pluginFilePath(home: string): string {
  return join(home, '.config', 'opencode', 'plugins', 'kyberdash-opencode.js')
}

function writeOpencodeJsonc(path: string, content: string): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
}

function readOtelFlag(content: string) {
  return readJsonKeys(content, [OPENCODE_OTEL_FLAG_KEY])[OPENCODE_OTEL_FLAG_KEY]
}

/** Receipt keys KyberDash still holds for opencode.jsonc. */
function opencodeReceiptKeys(home: string): string[] {
  const receipt = loadCaptureReceipt(home)
  return (receipt?.files ?? [])
    .filter((file) => file.harnessId === 'opencode')
    .flatMap((file) => Object.keys(file.keys))
}

/**
 * Records the flag the way an older KyberDash did: it wrote `true` over `before`, and the receipt
 * keeps the `prior` value so a later enable or disable can put it back.
 */
function recordLegacyFlag(
  home: string,
  path: string,
  before: string,
  written: string,
  prior: ReceiptPrior,
): void {
  saveCaptureReceipt(home, {
    version: 1,
    files: [
      {
        path,
        format: 'jsonc',
        harnessId: 'opencode',
        keys: { [OPENCODE_OTEL_FLAG_KEY]: { written: true, prior } },
        preSha256: sha256Hex(before),
        postSha256: sha256Hex(written),
      },
    ],
  })
}

/** The owner's file: a comment, the flag set to `false` by hand, nothing KyberDash wrote. */
function seedOpencodeJsonc(home: string): { path: string; initial: string } {
  const path = opencodeConfigPath(home)
  const initial = `{
  // opencode capture fixture
  "theme": "dark",
  "experimental": {
    "openTelemetry": false
  }
}
`
  writeOpencodeJsonc(path, initial)
  return { path, initial }
}

describe('opencode capture writer: plugin-only', () => {
  it.skipIf(process.platform === 'win32')(
    'enable writes no opencode.jsonc key and leaves the owner file byte-identical',
    async () => {
      const home = makeHome()
      const { path, initial } = seedOpencodeJsonc(home)

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(enabled.exitCode).toBe(0)
      expect(readFileSync(path, 'utf8')).toBe(initial)
      expect(opencodeReceiptKeys(home)).toEqual([])
    },
  )

  it.skipIf(process.platform === 'win32')(
    'enable installs the capture plugin and turns content consent on',
    async () => {
      const home = makeHome()
      seedOpencodeJsonc(home)

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(enabled.exitCode).toBe(0)
      expect(existsSync(pluginFilePath(home))).toBe(true)
      expect(readContentConsent(resolveConsentPath(home))).toBe(true)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'enable puts back the flag value a receipt recorded, keeps the owner comment, and drops the receipt entry',
    async () => {
      const home = makeHome()
      const path = opencodeConfigPath(home)
      const before = '{\n  // owner comment\n  "experimental": {\n    "openTelemetry": false\n  }\n}\n'
      const written = '{\n  // owner comment\n  "experimental": {\n    "openTelemetry": true\n  }\n}\n'
      writeOpencodeJsonc(path, written)
      recordLegacyFlag(home, path, before, written, { present: true, value: false })

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(enabled.exitCode).toBe(0)
      const restored = readFileSync(path, 'utf8')
      expect(readOtelFlag(restored)).toMatchObject({ present: true, value: false })
      expect(restored).toContain('// owner comment')
      expect(opencodeReceiptKeys(home)).not.toContain(OPENCODE_OTEL_FLAG_KEY)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'enable removes the flag when the receipt says KyberDash added it over an absent key',
    async () => {
      const home = makeHome()
      const path = opencodeConfigPath(home)
      const before = '{\n  "theme": "dark"\n}\n'
      const written = '{\n  "theme": "dark",\n  "experimental": {\n    "openTelemetry": true\n  }\n}\n'
      writeOpencodeJsonc(path, written)
      recordLegacyFlag(home, path, before, written, { present: false })

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(enabled.exitCode).toBe(0)
      expect(readOtelFlag(readFileSync(path, 'utf8')).present).toBe(false)
      expect(opencodeReceiptKeys(home)).not.toContain(OPENCODE_OTEL_FLAG_KEY)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'enable keeps a flag the owner changed after KyberDash wrote it, with a warning',
    async () => {
      const home = makeHome()
      const path = opencodeConfigPath(home)
      const before = '{\n  "theme": "dark"\n}\n'
      const written = '{\n  "theme": "dark",\n  "experimental": {\n    "openTelemetry": true\n  }\n}\n'
      const owner = '{\n  "theme": "dark",\n  "experimental": {\n    "openTelemetry": false\n  }\n}\n'
      writeOpencodeJsonc(path, owner)
      recordLegacyFlag(home, path, before, written, { present: false })

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(readOtelFlag(readFileSync(path, 'utf8'))).toMatchObject({ present: true, value: false })
      const output = `${enabled.stdout}\n${enabled.stderr}`
      expect(output).toMatch(/warning/i)
      expect(output).toContain(OPENCODE_OTEL_FLAG_KEY)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'disable puts back the receipted flag value, keeps the owner comment, and drops the receipt entry',
    async () => {
      const home = makeHome()
      const path = opencodeConfigPath(home)
      const before = '{\n  // owner comment\n  "experimental": {\n    "openTelemetry": false\n  }\n}\n'
      const written = '{\n  // owner comment\n  "experimental": {\n    "openTelemetry": true\n  }\n}\n'
      writeOpencodeJsonc(path, written)
      recordLegacyFlag(home, path, before, written, { present: true, value: false })

      const disabled = await runCapture('disable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(disabled.exitCode).toBe(0)
      const restored = readFileSync(path, 'utf8')
      expect(readOtelFlag(restored)).toMatchObject({ present: true, value: false })
      expect(restored).toContain('// owner comment')
      expect(opencodeReceiptKeys(home)).not.toContain(OPENCODE_OTEL_FLAG_KEY)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'disable keeps a flag the owner changed after KyberDash wrote it, with a warning',
    async () => {
      const home = makeHome()
      const path = opencodeConfigPath(home)
      const before = '{\n  "theme": "dark"\n}\n'
      const written = '{\n  "theme": "dark",\n  "experimental": {\n    "openTelemetry": true\n  }\n}\n'
      const owner = '{\n  "theme": "dark",\n  "experimental": {\n    "openTelemetry": false\n  }\n}\n'
      writeOpencodeJsonc(path, owner)
      recordLegacyFlag(home, path, before, written, { present: false })

      const disabled = await runCapture('disable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(readOtelFlag(readFileSync(path, 'utf8'))).toMatchObject({ present: true, value: false })
      const output = `${disabled.stdout}\n${disabled.stderr}`
      expect(output).toMatch(/warning/i)
      expect(output).toContain(OPENCODE_OTEL_FLAG_KEY)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'disable removes the capture plugin and turns content consent off, leaving opencode.jsonc alone',
    async () => {
      const home = makeHome()
      const { path, initial } = seedOpencodeJsonc(home)
      await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(existsSync(pluginFilePath(home))).toBe(true)

      const disabled = await runCapture('disable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(disabled.exitCode).toBe(0)
      expect(existsSync(pluginFilePath(home))).toBe(false)
      expect(readContentConsent(resolveConsentPath(home))).toBe(false)
      expect(readFileSync(path, 'utf8')).toBe(initial)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'status reports plugin state, content consent and receiver liveness, with no OTLP env snippet',
    async () => {
      vi.stubEnv(STATUS_ENV_KEY, 'http://192.0.2.17:4318')

      const home = makeHome()
      seedOpencodeJsonc(home)
      await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      const result = await runCapture('status', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      const combined = `${result.stdout}\n${result.stderr}`
      expect(combined).toContain('plugin: installed')
      expect(combined).toContain('content consent: on')
      expect(combined).toMatch(/^receiver: /m)
      expect(combined).not.toContain(STATUS_ENV_KEY)
      expect(combined).not.toContain('KYBERDASH_OPENCODE_CONTEXT')
      expect(combined).not.toContain('http://192.0.2.17:4318')
      expect(combined).not.toContain('13438')
      expect(combined.toLowerCase()).not.toContain('process environment')
    },
  )

  it.skipIf(process.platform === 'win32')(
    'dry-run previews only the plugin install and writes nothing',
    async () => {
      const home = makeHome()
      const { path, initial } = seedOpencodeJsonc(home)

      const result = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        dryRun: true,
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      expect(result.events).toHaveLength(1)
      expect(result.events[0]).toMatchObject({
        kind: 'would-write',
        harness: 'opencode',
        fileId: 'plugin',
        path: pluginFilePath(home),
      })
      expect(readFileSync(path, 'utf8')).toBe(initial)
      expect(existsSync(pluginFilePath(home))).toBe(false)
      expect(readContentConsent(resolveConsentPath(home))).toBe(false)
      expect(existsSync(receiptPathForHome(home))).toBe(false)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'CLI and structured action agree on the dry-run events (CLI↔POST parity)',
    async () => {
      const home = makeHome()
      seedOpencodeJsonc(home)

      const cli = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        dryRun: true,
        fetchHealth: KYBERDASH_LISTENER,
      })
      const structured = await runCaptureAction({
        action: 'enable',
        harnessId: 'opencode',
        dryRun: true,
        homeDir: home,
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(cli.exitCode).toBe(0)
      expect(structured.outcome).toBe('previewed')
      expect(structured.events).toEqual(cli.events)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'enable leaves an unparseable opencode.jsonc byte-identical',
    async () => {
      const home = makeHome()
      const configPath = opencodeConfigPath(home)
      writeOpencodeJsonc(configPath, '{ this is not jsonc\n')

      await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(readFileSync(configPath, 'utf8')).toBe('{ this is not jsonc\n')
    },
  )

  it('resolves and reports the win32 config path with win32 separators (D14 seam)', async () => {
    const home = makeHome()
    expect(opencodeHarness.resolvePath(home, { platform: 'win32' })).toBe(
      `${home}\\.config\\opencode\\opencode.jsonc`,
    )

    const result = await runCapture('status', {
      homeDir: home,
      platform: 'win32',
      harnessIds: ['opencode'],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(result.exitCode).toBe(0)
    expect(result.stdout).toContain(`${home}\\.config\\opencode\\opencode.jsonc`)
  })
})

describe('opencode legacy flag migration: hostile receipts and dry runs', () => {
  const malformedEntries: readonly [string, unknown][] = [
    ['null entry', null],
    ['string entry', 'not-an-entry'],
    ['entry without keys', { harnessId: 'opencode', path: '<path>' }],
    ['entry with null keys', { harnessId: 'opencode', path: '<path>', keys: null }],
    ['entry with a non-object keys', { harnessId: 'opencode', path: '<path>', keys: 'x' }],
    ['entry with an array keys', { harnessId: 'opencode', path: '<path>', keys: [OPENCODE_OTEL_FLAG_KEY] }],
    ['entry whose flag record is null', { harnessId: 'opencode', path: '<path>', keys: { [OPENCODE_OTEL_FLAG_KEY]: null } }],
    [
      'entry whose flag record has no prior',
      { harnessId: 'opencode', path: '<path>', keys: { [OPENCODE_OTEL_FLAG_KEY]: { written: true } } },
    ],
  ]

  it.each(malformedEntries)('ignores a malformed receipt: %s', (_name, entry) => {
    const home = makeHome()
    const path = opencodeConfigPath(home)
    const concrete =
      entry !== null && typeof entry === 'object' && 'path' in entry ? { ...(entry as object), path } : entry
    const receipt = { version: 1, files: [concrete] } as unknown as Parameters<typeof legacyOpencodeFlagRecord>[0]

    expect(() => legacyOpencodeFlagRecord(receipt, path)).not.toThrow()
    expect(legacyOpencodeFlagRecord(receipt, path)).toBeUndefined()
    const migration = migrateLegacyOpencodeFlag(home, { platform: process.platform }, receipt, false)
    expect(migration).toEqual({ out: [], err: [], events: [], receiptChanged: false })
  })

  it('still finds a well-formed entry beside malformed ones', () => {
    const home = makeHome()
    const path = opencodeConfigPath(home)
    const good = {
      path,
      format: 'jsonc',
      harnessId: 'opencode',
      keys: { [OPENCODE_OTEL_FLAG_KEY]: { written: true, prior: { present: false } } },
      preSha256: 'a',
      postSha256: 'b',
    }
    const receipt = { version: 1, files: [null, { harnessId: 'opencode', path, keys: null }, good] } as unknown as Parameters<
      typeof legacyOpencodeFlagRecord
    >[0]
    expect(legacyOpencodeFlagRecord(receipt, path)).toBe(good)
  })

  it('a malformed receipt does not stop enable or status', async () => {
    const home = makeHome()
    mkdirSync(dirname(receiptPathForHome(home)), { recursive: true })
    writeFileSync(receiptPathForHome(home), JSON.stringify({ version: 1, files: [null, { harnessId: 'opencode', keys: null }] }))

    const status = await runCapture('status', { homeDir: home, harnessIds: ['opencode'], fetchHealth: KYBERDASH_LISTENER })
    expect(status.exitCode).toBe(0)
  })

  it.skipIf(process.platform === 'win32')('a dry run does not claim a missing file was dropped', () => {
    const home = makeHome()
    const path = opencodeConfigPath(home)
    const receipt = {
      version: 1 as const,
      files: [
        {
          path,
          format: 'jsonc',
          harnessId: 'opencode',
          keys: { [OPENCODE_OTEL_FLAG_KEY]: { written: true, prior: { present: false } } },
          preSha256: 'a',
          postSha256: 'b',
        },
      ],
    }

    const dry = migrateLegacyOpencodeFlag(home, { platform: process.platform }, receipt, true)
    expect(dry.receiptChanged).toBe(false)
    expect(receipt.files).toHaveLength(1)
    expect(dry.out.join('\n')).toContain('would drop')
    expect(dry.out.join('\n')).not.toMatch(/\bdropped\b/)

    const real = migrateLegacyOpencodeFlag(home, { platform: process.platform }, receipt, false)
    expect(real.receiptChanged).toBe(true)
    expect(real.out.join('\n')).toMatch(/\bdropped\b/)
  })

  it.skipIf(process.platform === 'win32')('a dry run does not claim it stopped tracking a changed flag', () => {
    const home = makeHome()
    const path = opencodeConfigPath(home)
    writeOpencodeJsonc(path, '{\n  "experimental": {\n    "openTelemetry": false\n  }\n}\n')
    const receipt = {
      version: 1 as const,
      files: [
        {
          path,
          format: 'jsonc',
          harnessId: 'opencode',
          keys: { [OPENCODE_OTEL_FLAG_KEY]: { written: true, prior: { present: false } } },
          preSha256: 'a',
          postSha256: 'b',
        },
      ],
    }

    const dry = migrateLegacyOpencodeFlag(home, { platform: process.platform }, receipt, true)
    expect(dry.receiptChanged).toBe(false)
    expect(dry.err.join('\n')).toContain('would no longer track')
    expect(dry.err.join('\n')).not.toContain('and no longer tracking it')

    const real = migrateLegacyOpencodeFlag(home, { platform: process.platform }, receipt, false)
    expect(real.receiptChanged).toBe(true)
    expect(real.err.join('\n')).toContain('no longer tracking it')
  })
})

describe('opencode capture: plugin-only ownership', () => {
  it('keeps the plugin id and the plugins config key declared, not written', () => {
    expect(OPENCODE_PLUGIN_ID).toBe('kyberdash-opencode')
    expect(OPENCODE_PLUGIN_CONFIG_KEYS).toEqual({ plugins: 'plugins' })
  })

  it.skipIf(process.platform === 'win32')(
    'enable never registers the plugin in opencode.jsonc',
    async () => {
      const home = makeHome()
      const { path } = seedOpencodeJsonc(home)

      await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      const written = readFileSync(path, 'utf8')
      expect(readJsonKeys(written, [OPENCODE_PLUGIN_CONFIG_KEYS.plugins])[OPENCODE_PLUGIN_CONFIG_KEYS.plugins]!.present).toBe(false)
    },
  )

  it('registers opencode against ~/.config/opencode/opencode.jsonc, the migration target', () => {
    expect(opencodeHarness.id).toBe('opencode')
    expect(opencodeHarness.displayName).toBe('OpenCode')
    expect(opencodeHarness.resolvePath('/tmp/synthetic-home')).toBe(
      join('/tmp/synthetic-home', '.config', 'opencode', 'opencode.jsonc'),
    )
  })
})
