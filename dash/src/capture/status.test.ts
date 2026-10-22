import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

import { claudeCodeHarness } from './harnesses/claude-code.js'
import { copilotHarness } from './harnesses/copilot.js'
import { opencodeHarness } from './harnesses/opencode.js'
import { pjoin, type PathEnv } from './paths.js'
import { DEFAULT_CAPTURE_ENDPOINT, runCapture } from './index.js'
import { buildCaptureStatus, buildHarnessStatus, createApplicabilityFilter } from './status.js'
import type { CaptureHarnessStatus, StatusContext } from './status.js'
import { CAPTURE_HARNESS_REGISTRY } from './harnesses/registry.js'
import { KILO_PLUGIN_SOURCE } from './kilo-plugin/source.js'

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
  vi.unstubAllEnvs()
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-capture-status-'))
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

function ctxFor(home: string, overrides: Partial<StatusContext> = {}): StatusContext {
  return { home, env: hostEnv(home), endpoint: DEFAULT_CAPTURE_ENDPOINT, ...overrides }
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
const codexPath = (home: string) => pjoin(HOST, home, '.codex', 'config.toml')
const piSettingsPath = (home: string) => pjoin(HOST, home, '.pi', 'agent', 'settings.json')

/**
 * The VS Code Stable user-data root, following the same seam Copilot's settings path
 * uses: win32 roots at the (injected) application-data directory, darwin at
 * `~/Library/Application Support`, linux at `~/.config`.
 */
function vscodeUserDataRoot(home: string): string {
  const { appData } = hostEnv(home)
  if (HOST === 'win32') return pjoin(HOST, appData ?? home, 'Code')
  if (HOST === 'darwin') return pjoin(HOST, home, 'Library', 'Application Support', 'Code')
  return pjoin(HOST, home, '.config', 'Code')
}

const kiloConfigDir = (home: string) => pjoin(HOST, home, '.config', 'kilo')
const kiloConfigFile = (home: string) => pjoin(HOST, kiloConfigDir(home), 'kilo.jsonc')
const kiloSharedRuntimeDb = (home: string) => pjoin(HOST, home, '.local', 'share', 'kilo', 'kilo.db')
const kiloVscodeGlobalStorage = (home: string) =>
  pjoin(HOST, vscodeUserDataRoot(home), 'User', 'globalStorage', 'kilocode.kilo-code')

/** Create one of the places a detection probe looks for, so the real filesystem answers. */
function createProbeSource(path: string, kind: 'file' | 'directory'): void {
  if (kind === 'file') {
    mkdirSync(dirname(path), { recursive: true })
    writeFileSync(path, '')
    return
  }
  mkdirSync(path, { recursive: true })
}

function byId(statuses: readonly CaptureHarnessStatus[], id: string): CaptureHarnessStatus {
  const found = statuses.find((status) => status.id === id)
  if (found === undefined) throw new Error(`no status for ${id}`)
  return found
}

async function enableViaEngine(home: string, harnessIds: string[]): Promise<void> {
  const result = await runCapture('enable', {
    homeDir: home,
    platform: HOST,
    appDataDir: hostEnv(home).appData,
    harnessIds,
    fetchHealth: NO_LISTENER,
  })
  expect(result.exitCode).toBe(0)
}

describe('buildCaptureStatus over a temporary home', () => {
  it('reports nothing installed for an empty home, in registry order', () => {
    const statuses = buildCaptureStatus(ctxFor(makeHome()))
    expect(statuses.map((status) => status.id)).toEqual(
      CAPTURE_HARNESS_REGISTRY.map((writer) => writer.id),
    )
    for (const status of statuses) {
      expect(status.installed).toBe('not-detected')
      expect(status.receipt).toBe('absent')
    }
    const copilot = byId(statuses, 'copilot')
    expect(copilot.files.map((file) => file.applicable)).toEqual([false, false])
    expect(copilot.state).toBe('off')
    const claude = byId(statuses, 'claude-code')
    expect(claude.state).toBe('off')
    expect(claude.files[0]).toMatchObject({ id: 'claude-settings', applicable: true, exists: false })
    expect(claude.files[0]!.presentKeys).toEqual([])
    expect(claude.files[0]!.missingKeys).toEqual(claude.files[0]!.expectedKeys)
    expect(byId(statuses, 'codex')).toMatchObject({ mode: 'status-only', state: 'not-applicable' })
  })

  it('marks managed and status-only modes', () => {
    const statuses = buildCaptureStatus(ctxFor(makeHome()))
    expect(statuses.map((status) => [status.id, status.mode])).toEqual([
      ['copilot', 'managed'],
      ['claude-code', 'managed'],
      ['codex', 'status-only'],
      ['opencode', 'managed'],
      ['pi', 'status-only'],
      ['antigravity', 'status-only'],
      ['kilo', 'managed'],
    ])
  })

  it('reports on with a recorded receipt and no drift after an engine enable', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    await enableViaEngine(home, ['claude-code'])

    const claude = byId(buildCaptureStatus(ctxFor(home)), 'claude-code')
    expect(claude.state).toBe('on')
    expect(claude.receipt).toBe('recorded')
    expect(claude.installed).toBe('detected')
    expect(claude.files[0]!.drift).toBeNull()
    expect(claude.files[0]!.missingKeys).toEqual([])
    expect(claude.files[0]!.expectedKeys).toHaveLength(12)
    expect(claude.warnings).toEqual([])
  })

  it('reports a drifted key as partial with the written and current values', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    await enableViaEngine(home, ['claude-code'])
    const doc = JSON.parse((await import('node:fs')).readFileSync(claudePath(home), 'utf8')) as {
      env: Record<string, unknown>
    }
    doc.env['OTEL_LOG_USER_PROMPTS'] = '0'
    delete doc.env['OTEL_LOG_RAW_API_BODIES']
    writeFileSync(claudePath(home), JSON.stringify(doc, null, 2))

    const claude = byId(buildCaptureStatus(ctxFor(home)), 'claude-code')
    expect(claude.state).toBe('partial')
    expect(claude.receipt).toBe('recorded')
    expect(claude.files[0]!.drift).toEqual([
      { key: 'env.OTEL_LOG_USER_PROMPTS', written: '1', current: '0' },
      { key: 'env.OTEL_LOG_RAW_API_BODIES', written: '1', current: null },
    ])
  })

  it('reports keys set outside KyberDash as on with an absent receipt', () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{ "experimental": { "openTelemetry": true } }\n')

    const opencode = byId(buildCaptureStatus(ctxFor(home)), 'opencode')
    expect(opencode.state).toBe('on')
    expect(opencode.receipt).toBe('absent')
    expect(opencode.files[0]!.drift).toBeNull()
  })

  it('does not call Copilot Stable-only partial: the unused Insiders file is not applicable', async () => {
    const home = makeHome()
    writeConfig(stablePath(home), '{}\n')
    await enableViaEngine(home, ['copilot'])
    // enable via the engine wrote both editions; remove Insiders again to model "Stable only".
    rmSync(dirname(dirname(insidersPath(home))), { recursive: true })

    const copilot = byId(buildCaptureStatus(ctxFor(home)), 'copilot')
    expect(copilot.files.map((file) => [file.id, file.applicable, file.exists])).toEqual([
      ['vscode-stable', true, true],
      ['vscode-insiders', false, false],
    ])
    expect(copilot.files[1]!.presentKeys).toEqual([])
    expect(copilot.files[1]!.missingKeys).toEqual([])
    expect(copilot.state).toBe('on')
    expect(copilot.installed).toBe('detected')
  })

  it('treats an Insiders host directory as applicable even before its settings file exists', () => {
    const home = makeHome()
    mkdirSync(dirname(insidersPath(home)), { recursive: true })
    const copilot = byId(buildCaptureStatus(ctxFor(home)), 'copilot')
    expect(copilot.files.map((file) => file.applicable)).toEqual([false, true])
    expect(copilot.files[1]!.exists).toBe(false)
  })

  it('reports partial when only some managed keys are present', () => {
    const home = makeHome()
    // The flat dotted property VS Code actually reads, with only `enabled` set.
    writeConfig(
      stablePath(home),
      JSON.stringify({ 'github.copilot.chat.otel': { enabled: true } }),
    )
    const copilot = byId(buildCaptureStatus(ctxFor(home)), 'copilot')
    expect(copilot.state).toBe('partial')
    expect(copilot.files[0]!.presentKeys).toEqual(['github.copilot.chat.otel.enabled'])
    expect(copilot.files[0]!.missingKeys).toHaveLength(5)
  })

  it('ignores a nested github.copilot.chat.otel block, exactly as VS Code does', () => {
    const home = makeHome()
    // The extension reads one property named `github.copilot.chat.otel`; a nested tree is
    // never read, so its keys are absent — not a pass. This is the shape that made Copilot
    // log `exporter=file` while every key looked present.
    writeConfig(
      stablePath(home),
      JSON.stringify({ github: { copilot: { chat: { otel: { enabled: true, exporterType: 'otlp-http' } } } } }),
    )
    const copilot = byId(buildCaptureStatus(ctxFor(home)), 'copilot')
    expect(copilot.files[0]!.presentKeys).toEqual([])
    expect(copilot.files[0]!.missingKeys).toHaveLength(6)
    expect(copilot.state).toBe('off')
  })

  it("does not judge the editor's environment: the settings resolve, the override is withheld", async () => {
    const home = makeHome()
    writeConfig(stablePath(home), '{}\n')
    await enableViaEngine(home, ['copilot'])
    // The status route runs under the web process, not the editor's, so this
    // value must not change the answer: neither an empty override nor a set one
    // is visible here, and warning about the one this process was never started
    // with flagged GUI launches that were configured correctly.
    vi.stubEnv('COPILOT_OTEL_ENABLED', '')

    const copilot = byId(buildCaptureStatus(ctxFor(home)), 'copilot')
    expect(copilot.files[0]!.missingKeys).toEqual([])
    expect(copilot.warnings.map((warning) => warning.code)).not.toContain('exporter-not-delivering')
    // The resolved exporter is still reported — on the line, not as a warning.
    expect(copilot.statusLines.join('\n')).toMatch(/exporter: otlp-http/)
    expect(copilot.statusLines.join('\n')).toMatch(/not visible from here/)

    vi.stubEnv('COPILOT_OTEL_ENABLED', 'true')
    const withOverride = byId(buildCaptureStatus(ctxFor(home)), 'copilot')
    expect(withOverride.warnings).toEqual([])
    expect(withOverride.statusLines).toEqual(copilot.statusLines)
  })

  it('reports unknown for an unreadable config and an unparseable one, and downgrades install', () => {
    const home = makeHome()
    // A directory where the settings file belongs: every read fails without chmod tricks.
    mkdirSync(claudePath(home), { recursive: true })
    writeConfig(opencodePath(home), '{not json')

    const statuses = buildCaptureStatus(ctxFor(home))
    const claude = byId(statuses, 'claude-code')
    expect(claude.state).toBe('unknown')
    expect(claude.files[0]).toMatchObject({ exists: null, applicable: true, presentKeys: [], missingKeys: [] })
    expect(claude.installed).toBe('unknown')
    expect(claude.installEvidence[0]).toContain('could not inspect')
    expect(claude.installEvidence.join('\n')).toContain('EISDIR')

    const opencode = byId(statuses, 'opencode')
    expect(opencode.state).toBe('unknown')
    expect(opencode.files[0]!.exists).toBe(true)
  })

  it('warns when an existing ensure-if-absent endpoint points somewhere else', () => {
    const home = makeHome()
    writeConfig(
      claudePath(home),
      JSON.stringify({ env: { OTEL_EXPORTER_OTLP_ENDPOINT: 'http://127.0.0.1:4319' } }),
    )
    const claude = byId(buildCaptureStatus(ctxFor(home)), 'claude-code')
    expect(claude.warnings).toEqual([
      {
        code: 'endpoint-differs',
        text: 'env.OTEL_EXPORTER_OTLP_ENDPOINT is http://127.0.0.1:4319; the KyberDash receiver listens at http://127.0.0.1:4318',
      },
    ])
  })

  it('surfaces declared status warnings as config-warning entries', () => {
    const home = makeHome()
    writeConfig(
      stablePath(home),
      JSON.stringify({ 'github.copilot.chat.otel': { captureIdentity: true } }),
    )
    const copilot = byId(buildCaptureStatus(ctxFor(home)), 'copilot')
    expect(copilot.warnings.map((warning) => warning.code)).toContain('config-warning')
    expect(copilot.warnings.find((warning) => warning.code === 'config-warning')!.text).toContain(
      'captureIdentity is true',
    )
  })

  it('reports status-only harnesses with their lines and no keys', () => {
    const home = makeHome()
    writeConfig(codexPath(home), '# synthetic\n')
    writeConfig(
      piSettingsPath(home),
      JSON.stringify({ packages: ['@dpalfery/pi-statusline'] }),
    )

    const statuses = buildCaptureStatus(ctxFor(home))
    const codex = byId(statuses, 'codex')
    expect(codex.installed).toBe('detected')
    expect(codex.statusLines).toEqual(['no [otel] table is present; coverage comes from session files'])
    expect(codex.files).toEqual([
      {
        id: 'codex-config',
        path: codexPath(home),
        applicable: false,
        exists: true,
        expectedKeys: [],
        presentKeys: [],
        missingKeys: [],
        mismatchedKeys: [],
        drift: null,
      },
    ])
    const pi = byId(statuses, 'pi')
    expect(pi.statusLines).toEqual(['extension: collector (@dpalfery/pi-statusline)'])
    expect(byId(statuses, 'antigravity').statusLines.at(-1)).toContain('declaredContextWindow snippet:')
  })

  it('names the OpenCode endpoint variable with the suggested value, never the process value', () => {
    vi.stubEnv('OTEL_EXPORTER_OTLP_ENDPOINT', 'http://localhost:9999')
    const opencode = byId(buildCaptureStatus(ctxFor(makeHome())), 'opencode')
    expect(opencode.manualSteps).toHaveLength(1)
    expect(opencode.manualSteps[0]).toMatchObject({
      id: 'opencode-otel-endpoint',
      env: { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: 'http://127.0.0.1:4318' },
    })
    expect(JSON.stringify(opencode)).not.toContain('9999')
    expect(byId(buildCaptureStatus(ctxFor(makeHome())), 'copilot').manualSteps).toEqual([])
  })

  it('lets an injected install probe win over the filesystem', () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    const statuses = buildCaptureStatus(
      ctxFor(home, {
        installProbe: (id) => ({ installed: 'not-detected', evidence: [`synthetic ${id}`] }),
      }),
    )
    for (const status of statuses) {
      expect(status.installed).toBe('not-detected')
      expect(status.installEvidence).toEqual([`synthetic ${status.id}`])
    }
  })

  it('never puts a key KyberDash does not manage into the report', () => {
    const home = makeHome()
    const secret = 'sk-ant-synth-NEVER-LEAK-0123456789'
    writeConfig(
      claudePath(home),
      JSON.stringify({ env: { ANTHROPIC_API_KEY: secret, OTEL_LOGS_EXPORTER: 'console' } }),
    )
    const claude = byId(buildCaptureStatus(ctxFor(home)), 'claude-code')
    const json = JSON.stringify(claude)
    expect(json).not.toContain(secret)
    expect(json).not.toContain('ANTHROPIC_API_KEY')
    expect(claude.files[0]!.presentKeys).toEqual(['env.OTEL_LOGS_EXPORTER'])
  })

  it('resolves win32 paths from the injected app-data root, read-only', () => {
    const home = makeHome()
    const appData = makeHome()
    const statuses = buildCaptureStatus({
      home,
      env: { platform: 'win32', appData },
      endpoint: DEFAULT_CAPTURE_ENDPOINT,
    })
    expect(byId(statuses, 'copilot').files.map((file) => file.path)).toEqual([
      `${appData}\\Code\\User\\settings.json`,
      `${appData}\\Code - Insiders\\User\\settings.json`,
    ])
  })
})

describe('kilo plugin capture status', () => {
  const kiloPluginFile = (home: string) => pjoin(HOST, kiloConfigDir(home), 'plugin', 'kyberdash-capture.js')

  it('lists kilo as a managed harness, off until the plugin is installed, with the plugin as its one file', () => {
    const home = makeHome()
    const kilo = byId(buildCaptureStatus(ctxFor(home)), 'kilo')
    expect(kilo).toMatchObject({
      id: 'kilo',
      displayName: 'Kilo Code',
      mode: 'managed',
      installed: 'not-detected',
      state: 'off',
      receipt: 'absent',
      warnings: [],
    })
    // The one file is the plugin itself: no config keys are declared or written.
    expect(kilo.files).toHaveLength(1)
    expect(kilo.files[0]).toMatchObject({
      id: 'plugin',
      path: kiloPluginFile(home),
      applicable: true,
      exists: false,
      expectedKeys: [],
      presentKeys: [],
      missingKeys: [],
      drift: null,
    })
  })

  it('reports on with a recorded receipt after the plugin is installed', async () => {
    const home = makeHome()
    await runCapture('enable', { homeDir: home, harnessIds: ['kilo'], fetchHealth: NO_LISTENER })
    const kilo = byId(buildCaptureStatus(ctxFor(home)), 'kilo')
    expect(kilo).toMatchObject({ mode: 'managed', state: 'on', receipt: 'recorded' })
    expect(kilo.files[0]).toMatchObject({ exists: true })
  })

  it('reports partial for an older plugin of its own, which Enable upgrades', () => {
    const home = makeHome()
    writeConfig(kiloPluginFile(home), '// kyberdash-capture-plugin v0\nold\n')
    expect(byId(buildCaptureStatus(ctxFor(home)), 'kilo').state).toBe('partial')
  })

  it.each([
    ['a file KyberDash did not write', 'export default {}\n'],
  ])('reports unknown for %s, so the row offers no action', (_name, content) => {
    const home = makeHome()
    writeConfig(kiloPluginFile(home), content)
    expect(byId(buildCaptureStatus(ctxFor(home)), 'kilo').state).toBe('unknown')
  })

  it('reports unknown instead of throwing when the plugin path is a directory', () => {
    const home = makeHome()
    mkdirSync(kiloPluginFile(home), { recursive: true })
    const kilo = byId(buildCaptureStatus(ctxFor(home)), 'kilo')
    expect(kilo.state).toBe('unknown')
    expect(kilo.statusLines.join('\n')).toMatch(/plugin: unreadable/)
  })

  it('reports unknown for a plugin the owner edited since install', async () => {
    const home = makeHome()
    await runCapture('enable', { homeDir: home, harnessIds: ['kilo'], fetchHealth: NO_LISTENER })
    writeConfig(kiloPluginFile(home), `${KILO_PLUGIN_SOURCE}// owner edit\n`)
    expect(byId(buildCaptureStatus(ctxFor(home)), 'kilo').state).toBe('unknown')
  })

  it.each([
    ['the config directory', kiloConfigDir, 'directory'],
    ['the shared-runtime database', kiloSharedRuntimeDb, 'file'],
    ['the VS Code Stable globalStorage directory', kiloVscodeGlobalStorage, 'directory'],
  ] as const)('is detected when %s exists, with one evidence line', (_label, sourceOf, kind) => {
    const home = makeHome()
    const source = sourceOf(home)
    createProbeSource(source, kind)

    const kilo = byId(buildCaptureStatus(ctxFor(home)), 'kilo')
    expect(kilo.installed).toBe('detected')
    // One question with three alternatives, so a detected harness carries one line
    // naming the place that answered rather than three.
    expect(kilo.installEvidence).toHaveLength(1)
    expect(kilo.installEvidence[0]).toContain(source)
  })

  it('is not-detected when none of the three places exists', () => {
    const kilo = byId(buildCaptureStatus(ctxFor(makeHome())), 'kilo')
    expect(kilo.installed).toBe('not-detected')
    expect(kilo.installEvidence).toHaveLength(1)
  })

  it('states the plugin state, what it captures and where usage comes from, with no OTLP framing', () => {
    const kilo = byId(buildCaptureStatus(ctxFor(makeHome())), 'kilo')
    const lines = kilo.statusLines.join('\n')
    expect(lines.trim()).not.toBe('')
    expect(lines).toMatch(/plugin: not installed/)
    expect(lines).toMatch(/system prompt, tool definitions and skills/)
    expect(lines).toMatch(/kilo\.db/)
    expect(lines).toMatch(/folder import/i)
    expect(lines).not.toMatch(/OTLP/)
  })

  it('never echoes text from kilo.jsonc', () => {
    const home = makeHome()
    const sentinel = 'synthetic-kilo-config-NEVER-ECHO-0123456789'
    writeConfig(kiloConfigFile(home), `{ "model": "${sentinel}" }\n`)

    const kilo = byId(buildCaptureStatus(ctxFor(home)), 'kilo')
    expect(JSON.stringify(kilo)).not.toContain(sentinel)
  })

  it('names exactly one manual step, the folder import, with no environment variable', () => {
    const kilo = byId(buildCaptureStatus(ctxFor(makeHome())), 'kilo')
    expect(kilo.manualSteps).toHaveLength(1)
    const step = kilo.manualSteps[0]!
    expect(step.id).toBe('kilo-folder-import')
    // The plugin has its own Enable button; the folder import is a setting or command the
    // user runs, never an env var the drawer could set for them.
    expect(step.env).toBeUndefined()
    expect(step.text).toMatch(/Scheduled folder import|import-history --harness kilo-shared-runtime/)
  })
})

describe('parity with the CLI text status', () => {
  it('agrees with `capture status` on present and missing keys', async () => {
    const home = makeHome()
    writeConfig(
      stablePath(home),
      JSON.stringify({
        'github.copilot.chat.otel': { enabled: true, exporterType: 'otlp-http' },
      }),
    )
    const text = (
      await runCapture('status', {
        homeDir: home,
        platform: HOST,
    appDataDir: hostEnv(home).appData,
        harnessIds: ['copilot'],
        fetchHealth: NO_LISTENER,
      })
    ).stdout
    const present = /present keys: (.*?)(?: \(missing: (.*)\))?$/m.exec(
      text.split('\n').filter((line) => line.includes('present keys'))[0]!,
    )!
    const textPresent = present[1]!.split(', ')
    const textMissing = (present[2] ?? '').split(', ').filter((key) => key !== '')

    const copilot = byId(buildCaptureStatus(ctxFor(home)), 'copilot')
    expect(copilot.files[0]!.presentKeys).toEqual(textPresent)
    expect(copilot.files[0]!.missingKeys).toEqual(textMissing)
  })

  it('agrees on missing config: the text says config missing, the report says exists false', async () => {
    const home = makeHome()
    const text = (
      await runCapture('status', {
        homeDir: home,
        platform: HOST,
    appDataDir: hostEnv(home).appData,
        harnessIds: ['opencode'],
        fetchHealth: NO_LISTENER,
      })
    ).stdout
    expect(text).toContain('(missing)')
    const opencode = byId(buildCaptureStatus(ctxFor(home)), 'opencode')
    expect(opencode.files[0]).toMatchObject({ exists: false, presentKeys: [] })
  })
})

describe('createApplicabilityFilter', () => {
  it('skips an absent Insiders profile and keeps every single-file harness file', () => {
    const home = makeHome()
    writeConfig(stablePath(home), '{}\n')
    const filter = createApplicabilityFilter(CAPTURE_HARNESS_REGISTRY)
    expect(filter({ harnessId: 'copilot', fileId: 'vscode-stable', path: stablePath(home) })).toBe(true)
    expect(filter({ harnessId: 'copilot', fileId: 'vscode-insiders', path: insidersPath(home) })).toBe(false)
    expect(filter({ harnessId: 'claude-code', fileId: 'claude-settings', path: claudePath(home) })).toBe(true)
  })
})

describe('createApplicabilityFilter platform', () => {
  it('takes the host directory of a win32 path with win32 rules on any host', () => {
    const file = 'C:\\Users\\x\\AppData\\Roaming\\Code - Insiders\\User\\settings.json'
    const dir = 'C:\\Users\\x\\AppData\\Roaming\\Code - Insiders\\User'
    const probe = (path: string) => (path === dir ? ('present' as const) : ('absent' as const))
    const filter = createApplicabilityFilter(CAPTURE_HARNESS_REGISTRY, probe, 'win32')
    expect(filter({ harnessId: 'copilot', fileId: 'vscode-insiders', path: file })).toBe(true)
  })
})

describe('buildHarnessStatus', () => {
  it('builds one harness on its own', () => {
    const writer = CAPTURE_HARNESS_REGISTRY.find((candidate) => candidate.id === 'opencode')!
    expect(buildHarnessStatus(writer, ctxFor(makeHome())).id).toBe('opencode')
  })
})
