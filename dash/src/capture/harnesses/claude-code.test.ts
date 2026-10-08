import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { readJsonKeys } from '../edit-json.js'
import { runCapture } from '../index.js'
import { claudeCodeHarness } from './claude-code.js'

const temporaryHomes: string[] = []

/** Env vars KyberDash always sets on enable (P2.2a). Values are strings in strict JSON. */
const CLAUDE_SET_ENV_KEYS = [
  'OTEL_LOGS_EXPORTER',
  'OTEL_LOG_USER_PROMPTS',
  'OTEL_LOG_ASSISTANT_RESPONSES',
  'OTEL_LOG_TOOL_DETAILS',
  'OTEL_LOG_TOOL_CONTENT',
  'OTEL_LOG_RAW_API_BODIES',
  'CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH',
] as const

/** Env vars written only when absent before enable (P2.2a). */
const CLAUDE_ENSURE_IF_ABSENT_ENV_KEYS = [
  'CLAUDE_CODE_ENABLE_TELEMETRY',
  'CLAUDE_CODE_ENHANCED_TELEMETRY_BETA',
  'OTEL_TRACES_EXPORTER',
  'OTEL_EXPORTER_OTLP_PROTOCOL',
  'OTEL_EXPORTER_OTLP_ENDPOINT',
] as const

/** Beta-detailed keys KyberDash must never write (P2.2a). */
const CLAUDE_FORBIDDEN_ENV_KEYS = ['ENABLE_BETA_TRACING_DETAILED', 'BETA_TRACING_ENDPOINT'] as const

const EXPECTED_SET_VALUES: Record<(typeof CLAUDE_SET_ENV_KEYS)[number], string> = {
  OTEL_LOGS_EXPORTER: 'otlp',
  OTEL_LOG_USER_PROMPTS: '1',
  OTEL_LOG_ASSISTANT_RESPONSES: '1',
  OTEL_LOG_TOOL_DETAILS: '1',
  OTEL_LOG_TOOL_CONTENT: '1',
  OTEL_LOG_RAW_API_BODIES: '1',
  CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH: '1048576',
}

const EXPECTED_ENSURE_WHEN_ABSENT: Record<
  (typeof CLAUDE_ENSURE_IF_ABSENT_ENV_KEYS)[number],
  string
> = {
  CLAUDE_CODE_ENABLE_TELEMETRY: '1',
  CLAUDE_CODE_ENHANCED_TELEMETRY_BETA: '1',
  OTEL_TRACES_EXPORTER: 'otlp',
  OTEL_EXPORTER_OTLP_PROTOCOL: 'http/protobuf',
  OTEL_EXPORTER_OTLP_ENDPOINT: 'http://127.0.0.1:4318',
}

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-claude-capture-'))
  temporaryHomes.push(home)
  return home
}

function settingsPath(home: string): string {
  return join(home, '.claude', 'settings.json')
}

function writeStrictJsonSettings(path: string, content: string): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
}

function envDotted(key: string): string {
  return `env.${key}`
}

function readEnv(content: string, key: string) {
  return readJsonKeys(content, [envDotted(key)])[envDotted(key)]
}

/**
 * `//` and `/*` are comments only outside a JSON string. A value such as
 * `http://127.0.0.1:4318` contains those slash pairs as data; rejecting
 * every pair would fail valid strict JSON.
 */
function hasJsonCommentOutsideString(content: string): boolean {
  let inString = false
  let escaped = false
  for (let index = 0; index < content.length; index++) {
    const char = content[index]!
    if (inString) {
      if (escaped) {
        escaped = false
        continue
      }
      if (char === '\\') {
        escaped = true
        continue
      }
      if (char === '"') inString = false
      continue
    }
    if (char === '"') {
      inString = true
      continue
    }
    const next = content[index + 1]
    if (char === '/' && (next === '/' || next === '*')) return true
  }
  return false
}

function expectStrictJsonSettings(content: string): void {
  expect(() => JSON.parse(content)).not.toThrow()
  expect(hasJsonCommentOutsideString(content)).toBe(false)
  const parsed = JSON.parse(content) as { env?: Record<string, unknown> }
  expect(parsed.env).toBeDefined()
  for (const value of Object.values(parsed.env!)) {
    expect(typeof value).toBe('string')
  }
}

function expectSetEnvValues(content: string): void {
  for (const key of CLAUDE_SET_ENV_KEYS) {
    const found = readEnv(content, key)
    expect(found.present, key).toBe(true)
    expect(found.value, key).toBe(EXPECTED_SET_VALUES[key])
    expect(typeof found.value, key).toBe('string')
  }
  const rawBodies = readEnv(content, 'OTEL_LOG_RAW_API_BODIES')
  expect(String(rawBodies.value)).not.toMatch(/^file:/)
}

function expectEnsureValuesWhenAbsent(content: string): void {
  for (const key of CLAUDE_ENSURE_IF_ABSENT_ENV_KEYS) {
    const found = readEnv(content, key)
    expect(found.present, key).toBe(true)
    expect(found.value, key).toBe(EXPECTED_ENSURE_WHEN_ABSENT[key])
  }
}

function expectForbiddenEnvKeysAbsent(content: string): void {
  for (const key of CLAUDE_FORBIDDEN_ENV_KEYS) {
    const found = readEnv(content, key)
    expect(found.present, key).toBe(false)
  }
}

describe('claude-code capture writer (P2.2a)', () => {
  it.skipIf(process.platform === 'win32')(
    'enable sets managed env keys in strict JSON settings.json',
    async () => {
      const home = makeHome()
      const path = settingsPath(home)
      const initial = JSON.stringify(
        {
          env: {
            OTEL_LOGS_EXPORTER: 'none',
          },
        },
        null,
        2,
      )
      writeStrictJsonSettings(path, `${initial}\n`)

      const result = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['claude-code'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      expect(result.stdout).not.toMatch(/not yet supported/i)

      const written = readFileSync(path, 'utf8')
      expectStrictJsonSettings(written)
      expectSetEnvValues(written)
      expectEnsureValuesWhenAbsent(written)
      expectForbiddenEnvKeysAbsent(written)

      const receiptPath = join(home, '.kyberdash', 'capture-receipt.json')
      expect(existsSync(receiptPath)).toBe(true)
      const receipt = JSON.parse(readFileSync(receiptPath, 'utf8')) as {
        files: Array<{ path: string; keys: Record<string, unknown> }>
      }
      expect(receipt.files.map((file) => file.path)).toEqual([path])
      const receiptKeys = Object.keys(receipt.files[0]!.keys).sort()
      const expectedReceiptKeys = [
        ...CLAUDE_SET_ENV_KEYS,
        ...CLAUDE_ENSURE_IF_ABSENT_ENV_KEYS,
      ].map(envDotted)
      expect(receiptKeys).toEqual([...expectedReceiptKeys].sort())
    },
  )

  it.skipIf(process.platform === 'win32')(
    'enable does not overwrite ensure-if-absent env keys when already present',
    async () => {
      const home = makeHome()
      const path = settingsPath(home)
      const initial = JSON.stringify(
        {
          env: {
            OTEL_LOGS_EXPORTER: 'none',
            CLAUDE_CODE_ENABLE_TELEMETRY: '0',
            CLAUDE_CODE_ENHANCED_TELEMETRY_BETA: '0',
            OTEL_TRACES_EXPORTER: 'console',
            OTEL_EXPORTER_OTLP_PROTOCOL: 'grpc',
            OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost:4318',
          },
        },
        null,
        2,
      )
      writeStrictJsonSettings(path, `${initial}\n`)

      const result = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['claude-code'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      expect(result.stdout).not.toMatch(/not yet supported/i)

      const written = readFileSync(path, 'utf8')
      expectSetEnvValues(written)
      expect(readEnv(written, 'CLAUDE_CODE_ENABLE_TELEMETRY').value).toBe('0')
      expect(readEnv(written, 'CLAUDE_CODE_ENHANCED_TELEMETRY_BETA').value).toBe('0')
      expect(readEnv(written, 'OTEL_TRACES_EXPORTER').value).toBe('console')
      expect(readEnv(written, 'OTEL_EXPORTER_OTLP_PROTOCOL').value).toBe('grpc')
      expect(readEnv(written, 'OTEL_EXPORTER_OTLP_ENDPOINT').value).toBe('http://localhost:4318')
      expectForbiddenEnvKeysAbsent(written)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'disable restores OTEL_LOGS_EXPORTER to its prior none after enable (D11)',
    async () => {
      const home = makeHome()
      const path = settingsPath(home)
      const initial = JSON.stringify(
        {
          env: {
            OTEL_LOGS_EXPORTER: 'none',
          },
        },
        null,
        2,
      )
      writeStrictJsonSettings(path, `${initial}\n`)

      const enabled = await runCapture('enable', {
        homeDir: home,
        harnessIds: ['claude-code'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)
      expect(readEnv(readFileSync(path, 'utf8'), 'OTEL_LOGS_EXPORTER').value).toBe('otlp')

      const disabled = await runCapture('disable', {
        homeDir: home,
        harnessIds: ['claude-code'],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(disabled.exitCode).toBe(0)
      expect(readEnv(readFileSync(path, 'utf8'), 'OTEL_LOGS_EXPORTER').value).toBe('none')
    },
  )

  it('registers claude-code as a capture harness (T8 layout)', () => {
    expect(claudeCodeHarness.id).toBe('claude-code')
    expect(claudeCodeHarness.displayName).toBe('Claude Code')
  })
})
