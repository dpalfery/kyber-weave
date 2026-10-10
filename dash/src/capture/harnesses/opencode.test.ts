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
import { opencodeHarness } from './opencode.js'

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
