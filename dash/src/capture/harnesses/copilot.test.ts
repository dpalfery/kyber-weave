import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { snapshotFile } from '../testing.js'

import { readJsonKeys } from '../edit-json.js'
import { runCapture } from '../index.js'
import { copilotHarness } from './copilot.js'

const temporaryHomes: string[] = []

/** Keys KyberDash owns for Copilot VS Code OTel capture (P2.1b). */
const COPILOT_OTEL_MANAGED_KEYS = [
  'github.copilot.chat.otel.enabled',
  'github.copilot.chat.otel.exporterType',
  'github.copilot.chat.otel.otlpEndpoint',
  'github.copilot.chat.otel.captureContent',
  'github.copilot.chat.otel.maxAttributeSizeChars',
] as const

const PROTOCOL_KEY = 'github.copilot.chat.otel.protocol'
const CAPTURE_IDENTITY_KEY = 'github.copilot.chat.otel.captureIdentity'

/** Env vars the Copilot CLI surface reports in capture status (status-only). */
const COPILOT_CLI_ENV_KEYS = [
  'COPILOT_OTEL_ENABLED',
  'OTEL_EXPORTER_OTLP_ENDPOINT',
  'OTEL_EXPORTER_OTLP_PROTOCOL',
  'OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT',
  'COPILOT_OTEL_CAPTURE_CONTENT',
  'COPILOT_OTEL_EXPORTER_TYPE',
  'COPILOT_OTEL_MAX_ATTRIBUTE_SIZE_CHARS',
] as const

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
  vi.unstubAllEnvs()
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-copilot-capture-'))
  temporaryHomes.push(home)
  return home
}

function vscodeUserSettingsPaths(home: string): { stable: string; insiders: string } {
  if (process.platform === 'darwin') {
    return {
      stable: join(home, 'Library', 'Application Support', 'Code', 'User', 'settings.json'),
      insiders: join(
        home,
        'Library',
        'Application Support',
        'Code - Insiders',
        'User',
        'settings.json',
      ),
    }
  }
  return {
    stable: join(home, '.config', 'Code', 'User', 'settings.json'),
    insiders: join(home, '.config', 'Code - Insiders', 'User', 'settings.json'),
  }
}

function writeJsoncConfig(path: string, content: string): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
}

function seedBothVscodeSettings(home: string): { stable: string; insiders: string; stableInitial: string; insidersInitial: string } {
  const paths = vscodeUserSettingsPaths(home)
  const stableInitial = `{
  // synthetic stable VS Code fixture
  "github.copilot.chat.otel": {
    "protocol": "http/protobuf",
    "enabled": false
  },
  "editor.fontSize": 14
}
`
  const insidersInitial = `{
  // synthetic insiders VS Code fixture
  "github.copilot.chat.otel": {
    "protocol": "http/protobuf",
    "exporterType": "console"
  },
  "workbench.colorTheme": "Dark+"
}
`
  writeJsoncConfig(paths.stable, stableInitial)
  writeJsoncConfig(paths.insiders, insidersInitial)
  return { ...paths, stableInitial, insidersInitial }
}

function expectManagedOtelValues(content: string): void {
  const found = readJsonKeys(content, [...COPILOT_OTEL_MANAGED_KEYS])
  expect(found['github.copilot.chat.otel.enabled'].present).toBe(true)
  expect(found['github.copilot.chat.otel.enabled'].value).toBe(true)
  expect(found['github.copilot.chat.otel.exporterType'].value).toBe('otlp-http')
  expect(found['github.copilot.chat.otel.otlpEndpoint'].value).toBe('http://127.0.0.1:4318')
  expect(found['github.copilot.chat.otel.captureContent'].value).toBe(true)
  expect(found['github.copilot.chat.otel.maxAttributeSizeChars'].value).toBe(0)
}

function expectProtocolUntouched(content: string, expectedProtocol: string): void {
  const found = readJsonKeys(content, [PROTOCOL_KEY])
  expect(found[PROTOCOL_KEY].present).toBe(true)
  expect(found[PROTOCOL_KEY].value).toBe(expectedProtocol)
}

function expectCaptureIdentityNeverSet(content: string): void {
  const found = readJsonKeys(content, [CAPTURE_IDENTITY_KEY])
  if (!found[CAPTURE_IDENTITY_KEY].present) return
  expect(found[CAPTURE_IDENTITY_KEY].value).not.toBe(true)
}

describe('copilot capture writer (P2.1b)', () => {
  it.skipIf(process.platform === 'win32')(
    'enable sets the five VS Code OTel keys on Stable and Insiders and preserves JSONC comments',
    async () => {
      const home = makeHome()
      const { stable, insiders, stableInitial, insidersInitial } = seedBothVscodeSettings(home)

      const result = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['copilot'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      expect(result.stdout).not.toMatch(/not yet supported/i)

      const stableWritten = readFileSync(stable, 'utf8')
      const insidersWritten = readFileSync(insiders, 'utf8')

      expect(stableWritten).toContain('// synthetic stable VS Code fixture')
      expect(insidersWritten).toContain('// synthetic insiders VS Code fixture')
      expect(stableWritten).not.toBe(stableInitial)
      expect(insidersWritten).not.toBe(insidersInitial)

      expectManagedOtelValues(stableWritten)
      expectManagedOtelValues(insidersWritten)
      expectProtocolUntouched(stableWritten, 'http/protobuf')
      expectProtocolUntouched(insidersWritten, 'http/protobuf')
      expectCaptureIdentityNeverSet(stableWritten)
      expectCaptureIdentityNeverSet(insidersWritten)

      const receiptPath = join(home, '.kyberdash', 'capture-receipt.json')
      expect(existsSync(receiptPath)).toBe(true)
      const receipt = JSON.parse(readFileSync(receiptPath, 'utf8')) as {
        files: Array<{ path: string; keys: Record<string, unknown> }>
      }
      const paths = receipt.files.map((file) => file.path).sort()
      expect(paths).toEqual([insiders, stable].sort())
      for (const file of receipt.files) {
        expect(Object.keys(file.keys).sort()).toEqual([...COPILOT_OTEL_MANAGED_KEYS].sort())
      }
    },
  )

  it.skipIf(process.platform === 'win32')(
    'disable restores both VS Code settings byte-for-byte after enable (D11)',
    async () => {
      const home = makeHome()
      const { stable, insiders, stableInitial, insidersInitial } = seedBothVscodeSettings(home)

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['copilot'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)
      expect(readFileSync(stable, 'utf8')).not.toBe(stableInitial)

      const disabled = await runCapture('disable', {
        homeDir: home,
        harnessIds: ['copilot'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(disabled.exitCode).toBe(0)
      expect(readFileSync(stable, 'utf8')).toBe(stableInitial)
      expect(readFileSync(insiders, 'utf8')).toBe(insidersInitial)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'status warns when captureIdentity is true in VS Code settings',
    async () => {
      const home = makeHome()
      const { stable, insiders } = vscodeUserSettingsPaths(home)
      const jsonc = `{
  // identity-on fixture
  "github.copilot.chat.otel": {
    "captureIdentity": true,
    "protocol": "http/protobuf"
  }
}
`
      writeJsoncConfig(stable, jsonc)
      writeJsoncConfig(insiders, jsonc)

      const result = await runCapture('status', {
        homeDir: home,
        harnessIds: ['copilot'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      const combined = `${result.stdout}\n${result.stderr}`.toLowerCase()
      expect(combined).toMatch(/warn/)
      expect(combined).toMatch(/captureidentity/)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'status warns when VS Code OTel protocol or exporter uses grpc',
    async () => {
      const home = makeHome()
      const { stable } = vscodeUserSettingsPaths(home)
      writeJsoncConfig(
        stable,
        `{
  // grpc fixture
  "github.copilot.chat.otel": {
    "protocol": "grpc",
    "exporterType": "otlp-grpc"
  }
}
`,
      )

      const result = await runCapture('status', {
        homeDir: home,
        harnessIds: ['copilot'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      const combined = `${result.stdout}\n${result.stderr}`.toLowerCase()
      expect(combined).toMatch(/warn/)
      expect(combined).toMatch(/grpc/)
    },
  )

  describe('Copilot CLI (status-only env snippet)', () => {
    const snippetEnv: Record<(typeof COPILOT_CLI_ENV_KEYS)[number], string> = {
      COPILOT_OTEL_ENABLED: '1',
      OTEL_EXPORTER_OTLP_ENDPOINT: 'http://127.0.0.1:4318',
      OTEL_EXPORTER_OTLP_PROTOCOL: 'http/protobuf',
      OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT: 'true',
      COPILOT_OTEL_CAPTURE_CONTENT: 'true',
      COPILOT_OTEL_EXPORTER_TYPE: 'otlp-http',
      COPILOT_OTEL_MAX_ATTRIBUTE_SIZE_CHARS: '0',
    }

    beforeEach(() => {
      for (const [key, value] of Object.entries(snippetEnv)) vi.stubEnv(key, value)
    })

    it('status prints the expected env snippet from the command process env', async () => {
      const home = makeHome()
      seedBothVscodeSettings(home)

      const result = await runCapture('status', {
        homeDir: home,
        harnessIds: ['copilot'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      const combined = `${result.stdout}\n${result.stderr}`
      expect(combined).toMatch(/copilot cli/i)
      for (const key of COPILOT_CLI_ENV_KEYS) {
        expect(combined).toContain(key)
        expect(combined).toContain(snippetEnv[key])
      }
    })

    it('enable and disable never edit shell rc files under HOME', async () => {
      const home = makeHome()
      seedBothVscodeSettings(home)
      const zshrc = join(home, '.zshrc')
      const zshenv = join(home, '.zshenv')
      const rcInitial = '# synthetic shell rc — must stay untouched\n'
      writeFileSync(zshrc, rcInitial)
      writeFileSync(zshenv, rcInitial)
      const zshrcBefore = snapshotFile(zshrc)
      const zshenvBefore = snapshotFile(zshenv)

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['copilot'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)

      const disabled = await runCapture('disable', {
        homeDir: home,
        harnessIds: ['copilot'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(disabled.exitCode).toBe(0)

      const zshrcAfter = snapshotFile(zshrc)
      const zshenvAfter = snapshotFile(zshenv)
      expect(zshrcAfter.text).toBe(rcInitial)
      expect(zshenvAfter.text).toBe(rcInitial)
      expect(zshrcAfter.mtimeMs).toBe(zshrcBefore.mtimeMs)
      expect(zshenvAfter.mtimeMs).toBe(zshenvBefore.mtimeMs)
    })
  })

  it('registers copilot as a capture harness (T8 layout)', () => {
    expect(copilotHarness.id).toBe('copilot')
    expect(copilotHarness.displayName).toBe('Copilot')
  })
})
