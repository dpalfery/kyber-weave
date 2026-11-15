import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { claudeCodeHarness } from './harnesses/claude-code.js'
import { DEFAULT_CAPTURE_ENDPOINT } from './index.js'
import { assessCapture, assessHarness, type CaptureReceiverView } from './health.js'
import type { PathEnv } from './paths.js'
import { buildCaptureStatus, type CaptureHarnessStatus } from './status.js'

const homes: string[] = []
afterEach(() => {
  for (const home of homes.splice(0)) rmSync(home, { recursive: true, force: true })
})

const HOST = process.platform
const envFor = (home: string): PathEnv => ({ platform: HOST, appData: join(home, 'AppData', 'Roaming') })
const LIVE: CaptureReceiverView = { hosted: true, liveness: 'kyberdash', detail: 'listening' }

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-capture-health-'))
  homes.push(home)
  return home
}

/** Every key enable writes, at enable's values, plus any overrides. */
function claudeSettings(overrides: Record<string, unknown> = {}): string {
  const env: Record<string, unknown> = {}
  for (const [key, value] of Object.entries({
    ...claudeCodeHarness.desiredKeys(DEFAULT_CAPTURE_ENDPOINT),
    ...claudeCodeHarness.ensureIfAbsentKeys!(DEFAULT_CAPTURE_ENDPOINT),
  })) {
    env[key.replace(/^env\./, '')] = value
  }
  return JSON.stringify({ env: { ...env, ...overrides } }, null, 2)
}

function claudeStatus(content: string): CaptureHarnessStatus {
  const home = makeHome()
  const path = claudeCodeHarness.resolvePath(home, envFor(home))
  mkdirSync(join(path, '..'), { recursive: true })
  writeFileSync(path, content)
  const status = buildCaptureStatus({ home, env: envFor(home), endpoint: DEFAULT_CAPTURE_ENDPOINT }).find(
    (entry) => entry.id === 'claude-code',
  )
  if (status === undefined) throw new Error('no claude-code status')
  return status
}

describe('assessHarness over real Claude Code settings', () => {
  it('passes a fully configured harness with a live receiver', () => {
    const status = claudeStatus(claudeSettings())
    expect(status.state).toBe('on')
    expect(assessHarness(status, LIVE, DEFAULT_CAPTURE_ENDPOINT)).toEqual({ health: 'ok', issues: [] })
  })

  it('flags the gRPC-port endpoint and grpc protocol that state alone reports as on', () => {
    // The reported case: every key present, so `state` is `on`, but the kept endpoint is the
    // OTLP gRPC port and nothing reaches the receiver.
    const status = claudeStatus(
      claudeSettings({ OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost:4317', OTEL_EXPORTER_OTLP_PROTOCOL: 'grpc' }),
    )
    expect(status.state).toBe('on')

    const { health, issues } = assessHarness(status, LIVE, DEFAULT_CAPTURE_ENDPOINT)
    expect(health).toBe('attention')
    expect(issues.map((issue) => [issue.code, issue.severity])).toEqual([
      ['endpoint-differs', 'blocking'],
      ['protocol-differs', 'blocking'],
    ])
    expect(issues[0]!.text).toBe(
      'env.OTEL_EXPORTER_OTLP_ENDPOINT is http://localhost:4317; the KyberDash receiver listens at http://127.0.0.1:4318',
    )
    expect(issues[0]!.remedy).toContain(`Set it to ${DEFAULT_CAPTURE_ENDPOINT}`)
    expect(issues[0]!.path).toBe(status.files[0]!.path)
    expect(issues[1]!.text).toContain('accepts OTLP/HTTP only')
  })

  it('names any other kept value that differs, comparing as strings', () => {
    const status = claudeStatus(
      claudeSettings({ CLAUDE_CODE_ENABLE_TELEMETRY: '0', CLAUDE_CODE_ENHANCED_TELEMETRY_BETA: 1 }),
    )
    const { issues } = assessHarness(status, LIVE, DEFAULT_CAPTURE_ENDPOINT)
    // The JSON number 1 is the same setting as "1"; only the switched-off telemetry is named.
    expect(issues.map((issue) => issue.code)).toEqual(['owner-value-differs'])
    expect(issues[0]!.text).toBe(
      'env.CLAUDE_CODE_ENABLE_TELEMETRY is 0; KyberDash expects 1 (the existing value was kept on enable)',
    )
  })

  it('names missing and mismatched managed keys on a partial file', () => {
    const status = claudeStatus(JSON.stringify({ env: { OTEL_LOGS_EXPORTER: 'console' } }))
    expect(status.state).toBe('partial')
    expect(status.files[0]!.mismatchedKeys).toEqual(['env.OTEL_LOGS_EXPORTER'])

    const { health, issues } = assessHarness(status, LIVE, DEFAULT_CAPTURE_ENDPOINT)
    expect(health).toBe('attention')
    expect(issues.map((issue) => issue.code)).toEqual(['missing-keys', 'value-mismatch'])
    expect(issues[0]!.text).toContain('env.OTEL_LOG_RAW_API_BODIES')
  })
})

describe('assessHarness: receiver and non-managed states', () => {
  const on = (): CaptureHarnessStatus => claudeStatus(claudeSettings())

  it.each([
    [{ hosted: false, liveness: 'unknown', detail: '' }, 'receiver-not-hosted'],
    [{ hosted: true, liveness: 'none', detail: 'nothing is listening' }, 'receiver-down'],
    [{ hosted: true, liveness: 'foreign', detail: 'something foreign' }, 'receiver-foreign'],
  ] as const)('blocks an enabled harness when the receiver is %o', (receiver, code) => {
    const { health, issues } = assessHarness(on(), receiver, DEFAULT_CAPTURE_ENDPOINT)
    expect(health).toBe('attention')
    expect(issues).toEqual([expect.objectContaining({ code, severity: 'blocking' })])
  })

  it('does not judge the receiver for a harness that is off', () => {
    const status = claudeStatus('{}')
    expect(status.state).toBe('off')
    expect(assessHarness(status, { hosted: false, liveness: 'unknown', detail: '' }, DEFAULT_CAPTURE_ENDPOINT)).toEqual(
      { health: 'off', issues: [] },
    )
  })

  it('reports an unreadable config as needing attention, never as off', () => {
    const status = claudeStatus('{not json')
    expect(status.state).toBe('unknown')
    const { health, issues } = assessHarness(status, LIVE, DEFAULT_CAPTURE_ENDPOINT)
    expect(health).toBe('attention')
    expect(issues.map((issue) => issue.code)).toEqual(['config-unreadable'])
  })

  it('leaves status-only harnesses read-only with no issues', () => {
    const home = makeHome()
    const statuses = buildCaptureStatus({ home, env: envFor(home), endpoint: DEFAULT_CAPTURE_ENDPOINT })
    for (const status of statuses.filter((entry) => entry.mode === 'status-only')) {
      expect(assessHarness(status, LIVE, DEFAULT_CAPTURE_ENDPOINT)).toEqual({ health: 'read-only', issues: [] })
    }
  })
})

describe('assessCapture', () => {
  it('is deterministic: the same inputs give the same verdict', () => {
    const statuses = [claudeStatus(claudeSettings({ OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost:4317' }))]
    const at = new Date('2026-10-18T12:00:00.000Z')
    expect(assessCapture(statuses, LIVE, DEFAULT_CAPTURE_ENDPOINT, at)).toEqual(
      assessCapture(statuses, LIVE, DEFAULT_CAPTURE_ENDPOINT, at),
    )
  })

  it('summarises attention, ok and off', () => {
    const at = new Date('2026-10-18T12:00:00.000Z')
    const healthy = claudeStatus(claudeSettings())
    const broken = claudeStatus(claudeSettings({ OTEL_EXPORTER_OTLP_ENDPOINT: 'http://localhost:4317' }))
    const off = claudeStatus('{}')

    expect(assessCapture([healthy, broken], LIVE, DEFAULT_CAPTURE_ENDPOINT, at).check).toEqual({
      checkedAt: '2026-10-18T12:00:00.000Z',
      health: 'attention',
      enabled: 2,
      attention: 1,
      issueCount: 1,
    })
    expect(assessCapture([healthy], LIVE, DEFAULT_CAPTURE_ENDPOINT, at).check.health).toBe('ok')
    expect(assessCapture([off], LIVE, DEFAULT_CAPTURE_ENDPOINT, at).check.health).toBe('off')
  })
})
