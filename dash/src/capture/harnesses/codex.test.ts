import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { runCapture } from '../index.js'
import { codexHarness } from './codex.js'

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-codex-capture-'))
  temporaryHomes.push(home)
  return home
}

function codexConfigPath(home: string): string {
  return join(home, '.codex', 'config.toml')
}

function writeCodexConfig(home: string, content: string): string {
  const path = codexConfigPath(home)
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
  return path
}

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

/** Config fixture without a Codex OTel table (G1-Q3 = (a)). */
const CONFIG_WITHOUT_OTEL = `# synthetic codex config — no [otel] table
[features]
experimental = false
`

describe('codex capture writer (P2.3, status-only)', () => {
  it.skipIf(process.platform === 'win32')(
    'status reports no [otel] table and session-file coverage',
    async () => {
      const home = makeHome()
      const configPath = writeCodexConfig(home, CONFIG_WITHOUT_OTEL)

      const result = await runCapture('status', {
        homeDir: home,
        harnessIds: ['codex'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      const combined = `${result.stdout}\n${result.stderr}`
      expect(combined).not.toMatch(/not yet supported/i)
      expect(combined).toContain(configPath)
      expect(combined.toLowerCase()).toMatch(/\[otel\]/)
      expect(combined.toLowerCase()).toMatch(/session file/)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'enable writes nothing under a temporary HOME (missing or existing config without [otel])',
    async () => {
      const homeMissing = makeHome()
      const missingPath = codexConfigPath(homeMissing)
      expect(existsSync(missingPath)).toBe(false)

      const enabledMissing = await runCapture('enable', {
        homeDir: homeMissing,
        harnessIds: ['codex'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabledMissing.exitCode).toBe(0)
      expect(enabledMissing.stdout).not.toMatch(/not yet supported/i)
      expect(existsSync(missingPath)).toBe(false)

      const homeExisting = makeHome()
      const existingPath = writeCodexConfig(homeExisting, CONFIG_WITHOUT_OTEL)
      const before = readFileSync(existingPath, 'utf8')
      const mtimeBefore = statSync(existingPath).mtimeMs

      const enabledExisting = await runCapture('enable', {
        homeDir: homeExisting,
        harnessIds: ['codex'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabledExisting.exitCode).toBe(0)
      expect(enabledExisting.stdout).not.toMatch(/not yet supported/i)
      expect(readFileSync(existingPath, 'utf8')).toBe(before)
      expect(statSync(existingPath).mtimeMs).toBe(mtimeBefore)

      const receiptPath = join(homeExisting, '.kyberdash', 'capture-receipt.json')
      expect(existsSync(receiptPath)).toBe(false)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'disable writes nothing to ~/.codex/config.toml under a temporary HOME',
    async () => {
      const home = makeHome()
      const configPath = writeCodexConfig(home, CONFIG_WITHOUT_OTEL)
      const before = readFileSync(configPath, 'utf8')
      const mtimeBefore = statSync(configPath).mtimeMs

      const disabled = await runCapture('disable', {
        homeDir: home,
        harnessIds: ['codex'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(disabled.exitCode).toBe(0)
      expect(disabled.stdout).not.toMatch(/not yet supported/i)
      expect(readFileSync(configPath, 'utf8')).toBe(before)
      expect(statSync(configPath).mtimeMs).toBe(mtimeBefore)
    },
  )

  it('registers codex as a capture harness (T8 layout)', () => {
    expect(codexHarness.id).toBe('codex')
    expect(codexHarness.displayName).toBe('Codex')
    expect(codexHarness.resolvePath('/tmp/synthetic-home')).toBe(
      join('/tmp/synthetic-home', '.codex', 'config.toml'),
    )
  })
})
