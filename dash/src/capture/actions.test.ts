import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { claudeCodeHarness } from './harnesses/claude-code.js'
import { copilotHarness } from './harnesses/copilot.js'
import { opencodeHarness } from './harnesses/opencode.js'
import type { PathEnv } from './paths.js'
import { runCaptureAction, type CaptureActionRequest } from './actions.js'
import { runCapture } from './index.js'
import { receiptPathForHome } from './receipt.js'
import { CAPTURE_HARNESS_REGISTRY } from './harnesses/registry.js'
import { createApplicabilityFilter } from './status.js'

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-capture-actions-'))
  temporaryHomes.push(home)
  return home
}

function writeConfig(path: string, content: string): void {
  mkdirSync(join(path, '..'), { recursive: true })
  writeFileSync(path, content)
}

const NO_LISTENER = async (): Promise<{ status: number; body: string }> => {
  throw new Error('connect ECONNREFUSED (synthetic)')
}

// Paths come from the writers themselves, on the host's own platform: a Windows runner
// writes real Windows paths, so nothing here hard-codes a darwin layout. appData is always
// injected (a temporary directory) so a Windows run can never resolve into the real APPDATA.
const HOST = process.platform
const hostEnv = (home: string): PathEnv => ({ platform: HOST, appData: join(home, 'AppData', 'Roaming') })
const copilotFile = (home: string, id: string): string =>
  copilotHarness.configFiles!.find((file) => file.fileId === id)!.resolvePath(home, hostEnv(home))
const claudePath = (home: string) => claudeCodeHarness.resolvePath(home, hostEnv(home))
const opencodePath = (home: string) => opencodeHarness.resolvePath(home, hostEnv(home))
const stablePath = (home: string) => copilotFile(home, 'vscode-stable')
const insidersPath = (home: string) => copilotFile(home, 'vscode-insiders')
const insidersRoot = (home: string) => dirname(dirname(insidersPath(home)))

function request(
  home: string,
  harnessId: string,
  action: 'enable' | 'disable',
  overrides: Partial<CaptureActionRequest> = {},
): CaptureActionRequest {
  return {
    action,
    harnessId,
    dryRun: false,
    homeDir: home,
    platform: HOST,
    appDataDir: hostEnv(home).appData,
    fetchHealth: NO_LISTENER,
    ...overrides,
  }
}

describe('runCaptureAction: enable', () => {
  it('previews a dry run as would-write events with before and after, and writes nothing', async () => {
    const home = makeHome()
    writeConfig(
      claudePath(home),
      `${JSON.stringify({ env: { OTEL_LOGS_EXPORTER: 'console', CLAUDE_CODE_ENABLE_TELEMETRY: '1' } }, null, 2)}\n`,
    )
    const before = readFileSync(claudePath(home), 'utf8')

    const result = await runCaptureAction(request(home, 'claude-code', 'enable', { dryRun: true }))

    expect(result.outcome).toBe('previewed')
    expect(result.events).toHaveLength(1)
    const event = result.events[0]!
    expect(event).toMatchObject({
      kind: 'would-write',
      harness: 'claude-code',
      fileId: 'claude-settings',
      path: claudePath(home),
    })
    expect(event.changes?.[0]).toEqual({ key: 'env.OTEL_LOGS_EXPORTER', before: 'console', after: 'otlp' })
    // A key that is absent has no `before`; a present ensure key is not in the plan at all.
    expect(event.changes?.[1]).toEqual({ key: 'env.OTEL_LOG_USER_PROMPTS', after: '1' })
    expect(event.changes?.map((change) => change.key)).not.toContain('env.CLAUDE_CODE_ENABLE_TELEMETRY')
    expect(readFileSync(claudePath(home), 'utf8')).toBe(before)
    expect(existsSync(receiptPathForHome(home))).toBe(false)
  })

  it('writes, then reports noop on the second enable', async () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{}\n')

    const first = await runCaptureAction(request(home, 'opencode', 'enable'))
    expect(first.outcome).toBe('wrote')
    expect(first.events).toEqual([
      {
        kind: 'wrote',
        harness: 'opencode',
        fileId: 'opencode-config',
        path: opencodePath(home),
        changes: [{ key: 'experimental.openTelemetry', after: true }],
      },
    ])
    const written = readFileSync(opencodePath(home), 'utf8')

    const second = await runCaptureAction(request(home, 'opencode', 'enable'))
    expect(second.outcome).toBe('noop')
    expect(second.events).toEqual([
      { kind: 'noop', harness: 'opencode', fileId: 'opencode-config', path: opencodePath(home) },
    ])
    expect(readFileSync(opencodePath(home), 'utf8')).toBe(written)
  })

  it('records a before value that is a JSON boolean as a boolean', async () => {
    const home = makeHome()
    writeConfig(
      stablePath(home),
      JSON.stringify({ github: { copilot: { chat: { otel: { enabled: false } } } } }),
    )
    const result = await runCaptureAction(
      request(home, 'copilot', 'enable', { dryRun: true, fileFilter: createApplicabilityFilter(CAPTURE_HARNESS_REGISTRY) }),
    )
    expect(result.events[0]!.changes![0]).toEqual({
      key: 'github.copilot.chat.otel.enabled',
      before: false,
      after: true,
    })
  })

  it('skips a file the fileFilter rejects, creating no phantom profile directory', async () => {
    const home = makeHome()
    writeConfig(stablePath(home), '{}\n')

    const result = await runCaptureAction(
      request(home, 'copilot', 'enable', { fileFilter: createApplicabilityFilter(CAPTURE_HARNESS_REGISTRY) }),
    )

    expect(result.outcome).toBe('wrote')
    expect(result.events.map((event) => event.fileId)).toEqual(['vscode-stable'])
    expect(existsSync(insidersRoot(home))).toBe(false)
    const receipt = JSON.parse(readFileSync(receiptPathForHome(home), 'utf8')) as { files: unknown[] }
    expect(receipt.files).toHaveLength(1)
  })

  it('without a fileFilter the engine still writes every declared file (the CLI contract)', async () => {
    const home = makeHome()
    writeConfig(stablePath(home), '{}\n')
    await runCaptureAction(request(home, 'copilot', 'enable'))
    expect(existsSync(insidersRoot(home))).toBe(true)
  })

  it('refuses a config that does not parse with a reason and no message', async () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{not json')
    const result = await runCaptureAction(request(home, 'opencode', 'enable'))
    expect(result.outcome).toBe('refused')
    expect(result.reason).toBe('unparseable')
    expect(result.events[0]).toMatchObject({ kind: 'refused', reason: 'unparseable', harness: 'opencode' })
    expect(readFileSync(opencodePath(home), 'utf8')).toBe('{not json')
  })
})

describe('runCaptureAction: disable', () => {
  it('previews a would-restore on a dry run, then restores key-level and byte-exact', async () => {
    const home = makeHome()
    const seed = '{\n  // owner comment\n}\n'
    writeConfig(opencodePath(home), seed)
    await runCaptureAction(request(home, 'opencode', 'enable'))
    const enabled = readFileSync(opencodePath(home), 'utf8')

    const preview = await runCaptureAction(request(home, 'opencode', 'disable', { dryRun: true }))
    expect(preview.outcome).toBe('previewed')
    expect(preview.events).toEqual([
      {
        kind: 'would-restore',
        harness: 'opencode',
        fileId: 'opencode-config',
        path: opencodePath(home),
        changes: [{ key: 'experimental.openTelemetry', before: true }],
      },
    ])
    expect(readFileSync(opencodePath(home), 'utf8')).toBe(enabled)

    const restored = await runCaptureAction(request(home, 'opencode', 'disable'))
    expect(restored.outcome).toBe('wrote')
    expect(restored.events[0]).toMatchObject({ kind: 'restored', fileId: 'opencode-config' })
    expect(readFileSync(opencodePath(home), 'utf8')).toBe(seed)
  })

  it('reports noop when nothing was ever enabled', async () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{}\n')
    const result = await runCaptureAction(request(home, 'opencode', 'disable'))
    expect(result.outcome).toBe('noop')
    expect(result.events).toEqual([
      { kind: 'noop', harness: 'opencode', fileId: 'opencode-config', path: opencodePath(home) },
    ])
  })

  it('refuses on drift with the entries, leaving every byte untouched', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    await runCaptureAction(request(home, 'claude-code', 'enable'))
    const doc = JSON.parse(readFileSync(claudePath(home), 'utf8')) as { env: Record<string, unknown> }
    doc.env['OTEL_LOG_USER_PROMPTS'] = '0'
    delete doc.env['OTEL_LOG_RAW_API_BODIES']
    writeFileSync(claudePath(home), `${JSON.stringify(doc, null, 2)}\n`)
    const tampered = readFileSync(claudePath(home), 'utf8')
    const receiptBefore = readFileSync(receiptPathForHome(home), 'utf8')

    const result = await runCaptureAction(request(home, 'claude-code', 'disable'))

    expect(result.outcome).toBe('refused')
    expect(result.reason).toBe('drift')
    expect(result.events).toEqual([
      {
        kind: 'refused',
        harness: 'claude-code',
        fileId: 'claude-settings',
        path: claudePath(home),
        reason: 'drift',
        drift: [
          { key: 'env.OTEL_LOG_USER_PROMPTS', written: '1', current: '0' },
          { key: 'env.OTEL_LOG_RAW_API_BODIES', written: '1', current: null },
        ],
      },
    ])
    expect(readFileSync(claudePath(home), 'utf8')).toBe(tampered)
    expect(readFileSync(receiptPathForHome(home), 'utf8')).toBe(receiptBefore)
  })

  it('refuses an unreadable config with its errno code only', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    await runCaptureAction(request(home, 'claude-code', 'enable'))
    rmSync(claudePath(home))
    mkdirSync(claudePath(home), { recursive: true })

    const result = await runCaptureAction(request(home, 'claude-code', 'disable'))

    expect(result.outcome).toBe('refused')
    expect(result.reason).toBe('unreadable')
    expect(result.events[0]).toEqual({
      kind: 'refused',
      harness: 'claude-code',
      fileId: 'claude-settings',
      path: claudePath(home),
      reason: 'unreadable',
      code: 'EISDIR',
    })
  })

  it('refuses a config that stopped parsing', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    await runCaptureAction(request(home, 'claude-code', 'enable'))
    writeFileSync(claudePath(home), '{not json')

    const result = await runCaptureAction(request(home, 'claude-code', 'disable'))
    expect(result.outcome).toBe('refused')
    expect(result.reason).toBe('unparseable')
    expect(result.events[0]).toMatchObject({ kind: 'refused', reason: 'unparseable' })
    expect(readFileSync(claudePath(home), 'utf8')).toBe('{not json')
  })
})

describe('runCaptureAction: shape', () => {
  it('rejects an unknown harness id instead of acting', async () => {
    await expect(runCaptureAction(request(makeHome(), 'no-such', 'enable'))).rejects.toThrow(/unknown harness/)
  })

  it('serializes two concurrent enables: one writes, the receipt stays one valid document', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    const slow = async (): Promise<{ status: number; body: string }> => {
      await new Promise((resolve) => setTimeout(resolve, 5))
      return { status: 200, body: JSON.stringify({ service: 'kyberdash-otlp' }) }
    }

    const [a, b] = await Promise.all([
      runCaptureAction(request(home, 'claude-code', 'enable', { fetchHealth: slow })),
      runCaptureAction(request(home, 'claude-code', 'enable', { fetchHealth: slow })),
    ])

    expect([a.outcome, b.outcome].sort()).toEqual(['noop', 'wrote'])
    const receipt = JSON.parse(readFileSync(receiptPathForHome(home), 'utf8')) as { files: unknown[] }
    expect(receipt.files).toHaveLength(1)
  })

  it('returns within the probe timeout when the health fetch never settles', async () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{}\n')
    const never = () => new Promise<{ status: number; body: string }>(() => {})
    const started = Date.now()

    const result = await runCaptureAction(
      request(home, 'opencode', 'enable', { fetchHealth: never, probeTimeoutMs: 20 }),
    )

    expect(result.outcome).toBe('wrote')
    expect(Date.now() - started).toBeLessThan(2000)
  })
})

describe('runCapture: structured events beside the unchanged text', () => {
  it('returns an events array and the same text the CLI always printed', async () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{}\n')
    const result = await runCapture('enable', {
      homeDir: home,
      platform: HOST,
      appDataDir: hostEnv(home).appData,
      harnessIds: ['opencode'],
      fetchHealth: NO_LISTENER,
    })
    expect(result.stdout).toContain(`harness opencode: enabled: ${opencodePath(home)} (updated)`)
    expect(result.events.map((event) => event.kind)).toEqual(['wrote'])
  })

  it('bounds the receiver probe by probeTimeoutMs', async () => {
    const home = makeHome()
    const result = await runCapture('status', {
      homeDir: home,
      platform: HOST,
      appDataDir: hostEnv(home).appData,
      harnessIds: ['opencode'],
      fetchHealth: () => new Promise(() => {}),
      probeTimeoutMs: 20,
    })
    expect(result.stdout).toContain('receiver: none')
    expect(result.stdout).toContain('timed out')
  })

  it('reports a usage failure (bad endpoint) as refused, not noop', async () => {
    const home = makeHome()
    const result = await runCaptureAction(
      request(home, 'claude-code', 'enable', { endpoint: 'ftp://not-otlp.example' }),
    )
    expect(result.outcome).toBe('refused')
    expect(result.reason).toBe('unreadable')
    expect(result.events).toEqual([])
  })
})
