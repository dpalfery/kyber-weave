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
import { opencodeHarness, OPENCODE_PLUGIN_CONFIG_KEYS, OPENCODE_PLUGIN_ID } from './opencode.js'

const temporaryHomes: string[] = []

/** Managed config key (P2.4a). */
const OPENCODE_OTEL_FLAG_KEY = 'experimental.openTelemetry'

/** Process env reported on status only; never written into opencode.jsonc (P2.4a). */
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

function writeOpencodeJsonc(path: string, content: string): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
}

function readOtelFlag(content: string) {
  return readJsonKeys(content, [OPENCODE_OTEL_FLAG_KEY])[OPENCODE_OTEL_FLAG_KEY]
}

function seedOpencodeJsonc(home: string): { path: string; initial: string } {
  const path = opencodeConfigPath(home)
  const initial = `{
  // opencode capture fixture (P2.4a)
  "theme": "dark",
  "experimental": {
    "openTelemetry": false
  }
}
`
  writeOpencodeJsonc(path, initial)
  return { path, initial }
}

describe('opencode capture writer (P2.4a)', () => {
  it.skipIf(process.platform === 'win32')(
    'enable sets experimental.openTelemetry true in JSONC with comments preserved; disable restores false',
    async () => {
      const home = makeHome()
      const { path, initial } = seedOpencodeJsonc(home)

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)
      expect(enabled.stdout).not.toMatch(/not yet supported/i)

      const afterEnable = readFileSync(path, 'utf8')
      expect(afterEnable).toContain('// opencode capture fixture (P2.4a)')
      expect(afterEnable).not.toBe(initial)
      const flagEnabled = readOtelFlag(afterEnable)
      expect(flagEnabled.present).toBe(true)
      expect(flagEnabled.value).toBe(true)
      expect(typeof flagEnabled.value).toBe('boolean')

      const receiptPath = join(home, '.kyberdash', 'capture-receipt.json')
      expect(existsSync(receiptPath)).toBe(true)

      const disabled = await runCapture('disable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(disabled.exitCode).toBe(0)
      expect(readFileSync(path, 'utf8')).toBe(initial)
      expect(readOtelFlag(readFileSync(path, 'utf8')).value).toBe(false)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'status reports OTEL_EXPORTER_OTLP_ENDPOINT from the capture process env and notes upstream #13438 and non-model span volume',
    async () => {
      vi.stubEnv(STATUS_ENV_KEY, 'http://192.0.2.17:4318')

      const home = makeHome()
      seedOpencodeJsonc(home)

      const result = await runCapture('status', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      const combined = `${result.stdout}\n${result.stderr}`
      expect(combined).not.toMatch(/not yet supported/i)
      expect(combined).toContain(STATUS_ENV_KEY)
      expect(combined).toContain('http://192.0.2.17:4318')
      expect(combined).toMatch(/13438/)
      expect(combined.toLowerCase()).toMatch(/non-model/)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'enable writes only experimental.openTelemetry in opencode.jsonc (never process env keys)',
    async () => {
      vi.stubEnv(STATUS_ENV_KEY, 'http://192.0.2.17:4318')

      const home = makeHome()
      const { path, initial } = seedOpencodeJsonc(home)

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)
      expect(enabled.stdout).not.toMatch(/not yet supported/i)

      const written = readFileSync(path, 'utf8')
      expect(readOtelFlag(written).value).toBe(true)
      const envInConfig = readJsonKeys(written, [
        STATUS_ENV_KEY,
        `env.${STATUS_ENV_KEY}`,
        'OTEL_TRACES_EXPORTER',
        'OTEL_EXPORTER_OTLP_PROTOCOL',
      ])
      for (const entry of Object.values(envInConfig)) {
        expect(entry.present).toBe(false)
      }
      expect(written).not.toContain('http://192.0.2.17:4318')
      expect(existsSync(join(home, '.profile'))).toBe(false)
      expect(existsSync(join(home, '.bashrc'))).toBe(false)
      expect(readFileSync(path, 'utf8')).not.toBe(initial)
    },
  )

  it('registers opencode against ~/.config/opencode/opencode.jsonc (T8 layout)', () => {
    expect(opencodeHarness.id).toBe('opencode')
    expect(opencodeHarness.displayName).toBe('OpenCode')
    expect(opencodeHarness.kind).toBe('managed')
    expect(opencodeHarness.format).toBe('jsonc')
    expect(opencodeHarness.resolvePath('/tmp/synthetic-home')).toBe(
      join('/tmp/synthetic-home', '.config', 'opencode', 'opencode.jsonc'),
    )
  })
})

describe('opencode capture ownership (T6)', () => {
  it('declares the plugin capture keys as declare-only: the OTel flag is the only written key', () => {
    // The plugin registration (`plugins`) is declared on the writer module so
    // capture owns the contract, but it is never written: local file discovery
    // installs the plugin by file. The OTLP endpoint stays process-env-only
    // for the same reason — OpenCode never reads it from opencode.jsonc.
    for (const endpoint of ['http://127.0.0.1:4318', 'https://receiver.example:4318']) {
      expect(opencodeHarness.desiredKeys(endpoint)).toEqual({ [OPENCODE_OTEL_FLAG_KEY]: true })
    }
    expect(OPENCODE_PLUGIN_ID).toBe('kyberdash-opencode')
    expect(OPENCODE_PLUGIN_CONFIG_KEYS).toEqual({ plugins: 'plugins' })
    expect(opencodeHarness.desiredKeys('http://127.0.0.1:4318')).not.toHaveProperty('plugins')
    expect(opencodeHarness.desiredKeys('http://127.0.0.1:4318')).not.toHaveProperty(STATUS_ENV_KEY)
    expect(opencodeHarness.ensureIfAbsentKeys).toBeUndefined()
    expect(opencodeHarness.configFiles).toBeUndefined()
  })

  it.skipIf(process.platform === 'win32')(
    'dry-run previews the exact flag change and writes nothing (CLI side)',
    async () => {
      const home = makeHome()
      const { path } = seedOpencodeJsonc(home)

      const result = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        dryRun: true,
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      // The config file, then the plugin file the strict install would write (T4).
      expect(result.events).toHaveLength(2)
      expect(result.events[1]).toMatchObject({ kind: 'would-write', harness: 'opencode', fileId: 'plugin' })
      expect(result.events[0]).toMatchObject({
        kind: 'would-write',
        harness: 'opencode',
        fileId: 'opencode-config',
        path,
      })
      expect(result.events[0]?.changes).toEqual([
        { key: OPENCODE_OTEL_FLAG_KEY, before: false, after: true },
      ])
      expect(readFileSync(path, 'utf8')).toContain('"openTelemetry": false')
      expect(existsSync(join(home, '.kyberdash', 'capture-receipt.json'))).toBe(false)
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
    'a drifted flag refuses the disable without writes (409-class refusal)',
    async () => {
      const home = makeHome()
      const { path } = seedOpencodeJsonc(home)

      const enabled = await runCaptureAction({
        action: 'enable',
        harnessId: 'opencode',
        dryRun: false,
        homeDir: home,
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.outcome).toBe('wrote')

      writeOpencodeJsonc(path, readFileSync(path, 'utf8').replace('"openTelemetry": true', '"openTelemetry": false'))
      const drifted = readFileSync(path, 'utf8')

      const refused = await runCaptureAction({
        action: 'disable',
        harnessId: 'opencode',
        dryRun: false,
        homeDir: home,
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(refused.outcome).toBe('refused')
      expect(refused.reason).toBe('drift')
      expect(readFileSync(path, 'utf8')).toBe(drifted)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'an unparseable opencode.jsonc refuses the enable without writes',
    async () => {
      const home = makeHome()
      const configPath = opencodeConfigPath(home)
      writeOpencodeJsonc(configPath, '{ this is not jsonc\n')

      const result = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['opencode'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(1)
      expect(result.events.some((event) => event.kind === 'refused')).toBe(true)
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
