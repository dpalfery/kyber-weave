// `GET|POST /api/kyber/capture` (task T5 RED): the harness-OTel capture surface the
// Settings drawer drives. The engine already exists under dash/src/capture/ (runCapture,
// the harness writers, the receipt); these tests pin the ROUTE over it and fail until it
// is mounted — today /api/kyber/capture answers the JSON 404 catch-all.
//
// Recorded contract these tests assume (the GREEN task implements; these names are fixed):
//   - dash/src/server/capture-routes.ts exports
//       export type CaptureInstallProbe = (harnessId: string) => {
//         installed: 'detected' | 'not-detected' | 'unknown'
//         evidence: readonly string[]
//       }
//       export type CaptureRouteSettings = {
//         homeDir: string
//         appDataDir?: string
//         platform?: NodeJS.Platform
//         fetchHealth?: HealthFetcher   // from dash/src/capture/probe.ts
//         probeTimeoutMs?: number
//         installProbe?: CaptureInstallProbe
//       }
//       export function handleCaptureRequest(req, res, url, deps): boolean
//     and routes.ts mounts it for /api/kyber/capture (GET and POST only).
//   - KyberRouteDeps (routes.ts) gains: readonly capture?: CaptureRouteSettings.
//   - makeDeps (testing.ts) gains: makeDeps(options?: { capture?: CaptureRouteSettings }).
//   - WebDashboardServices (cli/web.ts) gains capture?: CaptureRouteSettings, forwarded
//     into the route deps (pinned by the appended tests in cli/web.test.ts).
//   - The receiver-hosted setting is written through store.setMetadata
//     (settings/shared-settings.ts writeSetting), so the failing-write scenario is driven
//     with vi.spyOn(store, 'setMetadata') throwing for SETTING_KEYS.receiverHosted — that
//     spy is the "settings-store fake whose receiverHosted write can throw".
//   - The route always targets the default receiver origin http://127.0.0.1:4318 (there is
//     no endpoint field on the settings or in the POST body); pinned via the health URL.
//
// The settings type is mirrored locally so this file compiles against today's tree with no
// missing imports — the same RED discipline settings-route.test.ts used: the failure must
// be the missing route, never a missing module.
//
// Every filesystem touch is under a mkdtemp temporary HOME; the real home and the real
// harness configs are never read or written.
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { HealthFetcher } from '../capture/probe.js'
import { receiptPathForHome } from '../capture/receipt.js'
import { SETTING_KEYS, readSetting, writeSetting } from '../settings/shared-settings.js'
import { callRoute, makeDeps, type RouteDeps } from './testing.js'

const PATH = '/api/kyber/capture'

// -- The locally mirrored recorded contract (see the header note) ------------------------

type CaptureInstallProbe = (harnessId: string) => {
  installed: 'detected' | 'not-detected' | 'unknown'
  evidence: readonly string[]
}

type CaptureSettings = {
  homeDir: string
  appDataDir?: string
  platform?: NodeJS.Platform
  fetchHealth?: HealthFetcher
  probeTimeoutMs?: number
  installProbe?: CaptureInstallProbe
}

// -- Temporary homes and seeds ------------------------------------------------------------

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
  vi.restoreAllMocks()
  vi.unstubAllEnvs()
})

/** A fresh synthetic HOME; every harness config path resolves beneath it. */
function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-capture-route-'))
  temporaryHomes.push(home)
  return home
}

function writeConfig(path: string, content: string): void {
  mkdirSync(join(path, '..'), { recursive: true })
  writeFileSync(path, content)
}

const claudePath = (home: string) => join(home, '.claude', 'settings.json')
const copilotStablePath = (home: string) =>
  join(home, 'Library', 'Application Support', 'Code', 'User', 'settings.json')
const copilotInsidersPath = (home: string) =>
  join(home, 'Library', 'Application Support', 'Code - Insiders', 'User', 'settings.json')
const opencodePath = (home: string) => join(home, '.config', 'opencode', 'opencode.jsonc')
const codexPath = (home: string) => join(home, '.codex', 'config.toml')
const piSettingsPath = (home: string) => join(home, '.pi', 'agent', 'settings.json')
const piObservMePath = (home: string) => join(home, '.pi', 'agent', 'observme.yaml')
const antigravityPath = (home: string) => join(home, '.gemini', 'antigravity-cli', 'settings.json')
const kiloConfigPath = (home: string) => join(home, '.config', 'kilo', 'kilo.jsonc')

/**
 * A Claude settings document whose telemetry block sits exactly at the managed values,
 * with the ensure-if-absent endpoint at a foreign origin — the status-mixed fixture's
 * "on with an endpoint-differs warning, keys set outside KyberDash" shape.
 */
function claudeEnabledConfig(endpoint: string): string {
  return (
    JSON.stringify(
      {
        env: {
          OTEL_LOGS_EXPORTER: 'otlp',
          OTEL_LOG_USER_PROMPTS: '1',
          OTEL_LOG_ASSISTANT_RESPONSES: '1',
          OTEL_LOG_TOOL_DETAILS: '1',
          OTEL_LOG_TOOL_CONTENT: '1',
          OTEL_LOG_RAW_API_BODIES: '1',
          CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH: '1048576',
          CLAUDE_CODE_ENABLE_TELEMETRY: '1',
          CLAUDE_CODE_ENHANCED_TELEMETRY_BETA: '1',
          OTEL_TRACES_EXPORTER: 'otlp',
          OTEL_EXPORTER_OTLP_PROTOCOL: 'http/protobuf',
          OTEL_EXPORTER_OTLP_ENDPOINT: endpoint,
        },
      },
      null,
      2,
    ) + '\n'
  )
}

/** Copilot Stable with three of the five managed keys present — the fixture's "partial". */
function copilotPartialConfig(): string {
  return (
    JSON.stringify(
      {
        github: {
          copilot: {
            chat: {
              otel: {
                enabled: true,
                exporterType: 'otlp-http',
                otlpEndpoint: 'http://127.0.0.1:4318',
              },
            },
          },
        },
      },
      null,
      2,
    ) + '\n'
  )
}

// -- Receiver listeners (same three synthetic states the engine tests use) --------------

const NO_LISTENER: HealthFetcher = async () => {
  throw new Error('connect ECONNREFUSED 127.0.0.1:4318 (synthetic)')
}

const KYBERDASH_LISTENER: HealthFetcher = async () => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

const FOREIGN_LISTENER: HealthFetcher = async () => ({
  status: 200,
  body: '<html><body>synthetic foreign console</body></html>',
})

// -- Fixture loading and comparison -------------------------------------------------------

type FixtureName =
  | 'status-mixed'
  | 'status-unknown'
  | 'post-preview-enable'
  | 'post-wrote-enable'
  | 'post-refused-drift'
  | 'post-disable-last'

/** The shared capture-api fixtures, consumed unmodified (their author pinned the shapes). */
function loadFixture(name: FixtureName): unknown {
  return JSON.parse(readFileSync(new URL(`./fixtures/capture-api/${name}.json`, import.meta.url), 'utf8'))
}

/**
 * Replace the volatile parts of a capture payload so it can be compared against a shared
 * fixture: `path` values name this machine's temp HOME, and `installEvidence` lines embed
 * those paths. Everything else — key sets, ordering, and every non-path value — must match
 * the fixture exactly, which is what "response key sets match the shared fixtures" means
 * for values that are not machine-specific.
 */
function fixtureComparable(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(fixtureComparable)
  if (value === null || typeof value !== 'object') return value
  let record = value as Record<string, unknown>
  if (typeof record['path'] === 'string') record = { ...record, path: '<path>' }
  if (Array.isArray(record['installEvidence'])) {
    record = { ...record, installEvidence: record['installEvidence'].map(() => '<evidence>') }
  }
  const out: Record<string, unknown> = {}
  for (const [key, entry] of Object.entries(record)) out[key] = fixtureComparable(entry)
  return out
}

// -- Response views ----------------------------------------------------------------------

type CaptureFileView = {
  id: unknown
  path: unknown
  applicable: unknown
  exists: unknown
  expectedKeys: unknown
  presentKeys: unknown
  missingKeys: unknown
  drift: unknown
}

type CaptureHarnessView = {
  id: unknown
  displayName: unknown
  mode: unknown
  installed: unknown
  installEvidence: unknown
  state: unknown
  receipt: unknown
  files: CaptureFileView[]
  warnings: Array<Record<string, unknown>>
  manualSteps: Array<Record<string, unknown>>
  statusLines: unknown[]
}

type CaptureBodyView = {
  platform?: unknown
  action?: unknown
  harness?: unknown
  dryRun?: unknown
  outcome?: unknown
  reason?: unknown
  events?: Array<Record<string, unknown>>
  harnessState?: CaptureHarnessView
  receiver?: Record<string, unknown>
  harnesses?: CaptureHarnessView[]
}

const asBody = (value: unknown): CaptureBodyView => value as CaptureBodyView

function harnessById(body: unknown, id: string): CaptureHarnessView {
  const found = (asBody(body).harnesses ?? []).find((harness) => harness.id === id)
  expect(found, `harness ${id} must be in the response`).toBeDefined()
  return found!
}

const ownKeys = (value: unknown): string[] => Object.keys(value as Record<string, unknown>).sort()

// -- Deps construction -------------------------------------------------------------------

/**
 * Build route deps carrying the capture settings. The makeDeps OPTION is part of the
 * recorded contract; the double cast keeps this file compiling today, when makeDeps
 * takes no arguments, exactly like the RED casts testing.ts and web.test.ts used for
 * seams that did not exist yet.
 */
function makeCaptureDeps(settings: CaptureSettings): RouteDeps {
  return (makeDeps as unknown as (options?: { capture?: CaptureSettings }) => RouteDeps)({ capture: settings })
}

function captureSettings(home: string, overrides: Partial<CaptureSettings> = {}): CaptureSettings {
  return { homeDir: home, platform: 'darwin', fetchHealth: NO_LISTENER, ...overrides }
}

// =========================================================================================
// GET /api/kyber/capture
// =========================================================================================

describe('GET /api/kyber/capture', () => {
  it('answers 200 over a seeded temporary HOME with the key structure of the status-mixed fixture', async () => {
    const home = makeHome()
    // status-mixed's claude-code: fully on, receipt absent (set outside KyberDash), and an
    // endpoint that does not point at the receiver.
    writeConfig(claudePath(home), claudeEnabledConfig('http://127.0.0.1:4319'))
    writeConfig(copilotStablePath(home), copilotPartialConfig())
    writeConfig(opencodePath(home), '{\n  // synthetic opencode fixture\n}\n')
    writeConfig(codexPath(home), '# synthetic codex config\n')
    writeConfig(kiloConfigPath(home), '{}\n')
    writeConfig(piSettingsPath(home), `${JSON.stringify({ packages: ['@dpalfery/pi-statusline'] }, null, 2)}\n`)
    writeConfig(piObservMePath(home), '# synthetic ObservMe fixture\nendpoint: http://127.0.0.1:4318/v1/traces\n')
    const bridgeScript = join(home, 'bin', 'antigravity-statusline-bridge.sh')
    writeConfig(bridgeScript, '#!/bin/sh\ncurl --silent -X POST http://127.0.0.1:4318/v1/traces\n')
    writeConfig(
      antigravityPath(home),
      `${JSON.stringify({ statusLine: { command: bridgeScript } }, null, 2)}\n`,
    )

    // The manual-step env value is the SUGGESTED origin, never this process's own value —
    // so give the process a different one and require the suggestion anyway.
    vi.stubEnv('OTEL_EXPORTER_OTLP_ENDPOINT', 'http://localhost:9999')

    const fetchUrls: string[] = []
    const recordingListener: HealthFetcher = async (url) => {
      fetchUrls.push(url)
      return KYBERDASH_LISTENER(url)
    }
    const deps = makeCaptureDeps(captureSettings(home, { fetchHealth: recordingListener }))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const { status, handled, body } = await callRoute('GET', PATH, { deps })
    expect(handled).toBe(true)
    expect(status).toBe(200)

    const doc = asBody(body)
    expect(doc.platform).toBe('darwin')
    expect(ownKeys(doc)).toEqual(['harnesses', 'platform', 'receiver'])
    expect(doc.receiver).toEqual({
      hosted: true,
      liveness: 'kyberdash',
      detail: 'KyberDash receiver is listening at http://127.0.0.1:4318/healthz',
    })
    // The route targets the default receiver origin; there is no endpoint knob to misuse.
    expect(fetchUrls).toContain('http://127.0.0.1:4318/healthz')

    expect((doc.harnesses ?? []).map((harness) => harness.id)).toEqual([
      'copilot',
      'claude-code',
      'codex',
      'opencode',
      'pi',
      'antigravity',
      'kilo',
    ])

    const fixtureHarnesses = (loadFixture('status-mixed') as CaptureBodyView).harnesses ?? []
    const fixtureById = new Map(fixtureHarnesses.map((harness) => [String(harness.id), harness]))
    // The shared fixture predates kilo; its six rows are still compared field by field.
    for (const id of ['copilot', 'claude-code', 'codex', 'opencode', 'pi', 'antigravity']) {
      const harness = harnessById(body, id)
      const fixtureHarness = fixtureById.get(id)
      expect(fixtureHarness, `fixture harness ${id}`).toBeDefined()
      expect(ownKeys(harness), `key set of harness ${id}`).toEqual(ownKeys(fixtureHarness))
      expect(harness.files.map((file) => file.id)).toEqual(fixtureHarness!.files.map((file) => file.id))
      for (let index = 0; index < harness.files.length; index += 1) {
        const file = harness.files[index]!
        const fixtureFile = fixtureHarness!.files[index]!
        expect(ownKeys(file), `key set of ${id} file ${index}`).toEqual(ownKeys(fixtureFile))
        expect(file.expectedKeys).toEqual(fixtureFile.expectedKeys)
        expect(file.applicable).toEqual(fixtureFile.applicable)
        expect(file.exists).toEqual(fixtureFile.exists)
        expect(file.presentKeys).toEqual(fixtureFile.presentKeys)
        expect(file.missingKeys).toEqual(fixtureFile.missingKeys)
        expect(file.drift).toEqual(fixtureFile.drift)
        // Paths resolve under the injected HOME — the safety property that makes the
        // route testable without touching a real config.
        expect(String(file.path)).toContain(home)
      }
      // installEvidence lines embed machine paths, so structure rather than text is pinned.
      const evidence = harness.installEvidence as unknown[]
      expect(evidence.length, `installEvidence of ${id}`).toBeGreaterThan(0)
      for (const line of evidence) expect(typeof line).toBe('string')
    }

    expect(harnessById(body, 'copilot')).toMatchObject({
      mode: 'managed',
      installed: 'detected',
      state: 'partial',
      receipt: 'absent',
      warnings: [],
      manualSteps: [],
      statusLines: [],
    })
    expect(String((harnessById(body, 'copilot').installEvidence as unknown[]).join('\n'))).toContain('not found')

    const claude = harnessById(body, 'claude-code')
    expect(claude).toMatchObject({ mode: 'managed', installed: 'detected', state: 'on', receipt: 'absent' })
    expect(claude.warnings).toEqual([
      {
        code: 'endpoint-differs',
        text: expect.stringContaining('http://127.0.0.1:4319'),
      },
    ])
    expect(String(claude.warnings[0]!['text'])).toContain('http://127.0.0.1:4318')
    expect(claude.manualSteps).toEqual([])
    expect(claude.statusLines).toEqual([])

    expect(harnessById(body, 'opencode')).toMatchObject({
      mode: 'managed',
      installed: 'detected',
      state: 'off',
      receipt: 'absent',
      warnings: [],
      statusLines: [],
    })
    expect(harnessById(body, 'opencode').manualSteps).toEqual([
      {
        id: 'opencode-otel-endpoint',
        text: 'OpenCode reads the OTLP endpoint from its process environment (upstream #13438), not from opencode.jsonc; set OTEL_EXPORTER_OTLP_ENDPOINT in the environment OpenCode is launched from',
        env: { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: 'http://127.0.0.1:4318' },
      },
    ])

    expect(harnessById(body, 'codex').statusLines).toEqual([
      'no [otel] table is present; coverage comes from session files',
    ])
    expect(harnessById(body, 'pi').statusLines).toEqual([
      'extension: collector (@dpalfery/pi-statusline)',
      'extension: ObservMe (observme.yaml)',
      'warn: both the collector and ObservMe are loaded',
    ])

    const antigravity = harnessById(body, 'antigravity')
    expect(antigravity.statusLines).toHaveLength(3)
    expect(String(antigravity.statusLines[0])).toContain('statusline bridge: ')
    expect(antigravity.statusLines[1]).toBe('bridge route: the statusline script posts to 4318')
    expect(antigravity.statusLines[2]).toBe(
      'declaredContextWindow snippet: span.set_attribute("declaredContextWindow", <positive token count>)',
    )
    expect(antigravity.manualSteps).toEqual([
      {
        id: 'antigravity-declared-context-window',
        text: 'Add span.set_attribute("declaredContextWindow", <positive token count>) on the statusline bridge\'s chat span; the window exists only when the user declares it',
      },
    ])
    for (const id of ['codex', 'pi', 'antigravity', 'kilo']) {
      expect(harnessById(body, id)).toMatchObject({
        mode: 'status-only',
        installed: 'detected',
        state: 'not-applicable',
        receipt: 'absent',
        warnings: [],
      })
    }

    // Kilo is reported honestly: the drawer shows the plugin state and the two steps the
    // user owns, and no environment variable to set.
    const kilo = harnessById(body, 'kilo')
    expect(kilo.displayName).toBe('Kilo Code')
    expect((kilo.statusLines as unknown[]).length).toBeGreaterThan(0)
    expect(kilo.manualSteps).toHaveLength(2)
    expect(kilo.manualSteps[0]).toMatchObject({ id: 'kilo-capture-plugin' })
    expect(kilo.manualSteps[1]).toMatchObject({ id: 'kilo-folder-import' })
    expect(kilo.manualSteps[1]!['env']).toBeUndefined()
  })

  it('reports drift for a receipted file the owner changed since enable', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const enable = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'claude-code', dryRun: false },
    })
    expect(enable.status).toBe(200)

    // The owner moves one managed key off the value KyberDash wrote.
    const written = JSON.parse(readFileSync(claudePath(home), 'utf8')) as { env: Record<string, unknown> }
    written.env['OTEL_LOG_USER_PROMPTS'] = '0'
    writeFileSync(claudePath(home), `${JSON.stringify(written, null, 2)}\n`)

    const { status, body } = await callRoute('GET', PATH, { deps })
    expect(status).toBe(200)
    const claude = harnessById(body, 'claude-code')
    expect(claude.state).toBe('partial')
    expect(claude.receipt).toBe('recorded')
    expect(claude.files[0]!.drift).toEqual([
      { key: 'env.OTEL_LOG_USER_PROMPTS', written: '1', current: '0' },
    ])
  })

  it('reports unknown for a harness whose config cannot be read, and does not probe liveness while the receiver is not hosted', async () => {
    const home = makeHome()
    // A DIRECTORY where the settings file would be: every read fails, on every platform,
    // without chmod tricks — an unreadable config is exists:null, never a fabricated zero.
    mkdirSync(claudePath(home), { recursive: true })
    const deps = makeCaptureDeps(captureSettings(home))

    const { status, body } = await callRoute('GET', PATH, { deps })
    expect(status).toBe(200)

    const claude = harnessById(body, 'claude-code')
    expect(claude.installed).toBe('unknown')
    expect((claude.installEvidence as unknown[]).length).toBeGreaterThan(0)
    expect(String((claude.installEvidence as unknown[])[0])).toContain('could not inspect')
    expect(claude.state).toBe('unknown')
    const file = claude.files[0]!
    expect(file.exists).toBe(null)
    expect(file.applicable).toBe(true)
    expect(file.presentKeys).toEqual([])
    expect(file.missingKeys).toEqual([])

    // A server that does not host the receiver does not probe it (status-unknown fixture).
    expect(asBody(body).receiver).toEqual({
      hosted: false,
      liveness: 'unknown',
      detail: 'this KyberDash server does not host the OTLP receiver; liveness was not probed',
    })
  })

  it('passes an injected install probe through, per harness', async () => {
    const home = makeHome()
    // The file exists; the probe's answer must still win, which is what makes the seam a
    // seam rather than a hint.
    writeConfig(claudePath(home), '{}\n')
    const probe: CaptureInstallProbe = (harnessId) => ({
      installed: harnessId === 'claude-code' ? 'unknown' : 'not-detected',
      evidence: [`synthetic ${harnessId} evidence`],
    })
    const deps = makeCaptureDeps(captureSettings(home, { installProbe: probe }))

    const { status, body } = await callRoute('GET', PATH, { deps })
    expect(status).toBe(200)
    for (const id of ['copilot', 'claude-code', 'codex', 'opencode', 'pi', 'antigravity', 'kilo']) {
      const harness = harnessById(body, id)
      expect(harness.installed).toBe(id === 'claude-code' ? 'unknown' : 'not-detected')
      expect(harness.installEvidence).toEqual([`synthetic ${id} evidence`])
    }
  })

  it.each([
    ['kyberdash', KYBERDASH_LISTENER, 'KyberDash receiver is listening at'],
    ['foreign', FOREIGN_LISTENER, 'something foreign is listening at'],
    ['none', NO_LISTENER, 'nothing is listening at'],
  ])('reports liveness %s when the health probe observes it', async (liveness, fetchHealth, detailFragment) => {
    const deps = makeCaptureDeps(captureSettings(makeHome(), { fetchHealth }))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const { status, body } = await callRoute('GET', PATH, { deps })
    expect(status).toBe(200)
    expect(asBody(body).receiver).toMatchObject({
      hosted: true,
      liveness,
      detail: expect.stringContaining(detailFragment),
    })
  })

  it('bounds a hanging health probe by the injected probe timeout', async () => {
    // A fetch that never settles, with no timer behind it: only the route's own probe
    // timeout can decide this request, and the answer must still land inside the test
    // window rather than riding the probe forever.
    const never: HealthFetcher = () => new Promise(() => {})
    const deps = makeCaptureDeps(captureSettings(makeHome(), { fetchHealth: never, probeTimeoutMs: 15 }))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const { status, body } = await callRoute('GET', PATH, { deps })
    expect(status).toBe(200)
    expect(body).toBeDefined()
    expect(asBody(body).receiver).toMatchObject({ hosted: true, liveness: 'none' })
  })

  it('resolves win32 config paths through the injected app-data root', async () => {
    const home = makeHome()
    const appData = makeHome()
    const deps = makeCaptureDeps(captureSettings(home, { platform: 'win32', appDataDir: appData }))

    const { status, body } = await callRoute('GET', PATH, { deps })
    expect(status).toBe(200)
    expect(asBody(body).platform).toBe('win32')
    const winJoin = (...parts: string[]) => parts.join('\\')
    expect(harnessById(body, 'copilot').files.map((file) => file.path)).toEqual([
      winJoin(appData, 'Code', 'User', 'settings.json'),
      winJoin(appData, 'Code - Insiders', 'User', 'settings.json'),
    ])
    expect(harnessById(body, 'claude-code').files[0]!.path).toBe(winJoin(home, '.claude', 'settings.json'))
  })

  it('answers 404 when no store is mounted', async () => {
    // The capture route needs the shared settings store (the receiver-hosted switch); a
    // caller that reaches it without one gets the JSON 404 catch-all's answer, never a crash.
    const { status, handled, body } = await callRoute('GET', PATH, {
      deps: undefined as unknown as RouteDeps,
    })
    expect(handled).toBe(true)
    expect(status).toBe(404)
    expect(body).toEqual({ error: 'Not found' })
  })
})

// =========================================================================================
// POST /api/kyber/capture
// =========================================================================================

describe('POST /api/kyber/capture', () => {
  const VALID = { action: 'enable', harness: 'opencode', dryRun: false }

  it('answers 404 when no store is mounted', async () => {
    const { status, body } = await callRoute('POST', PATH, {
      deps: undefined as unknown as RouteDeps,
      payload: VALID,
    })
    expect(status).toBe(404)
    expect(body).toEqual({ error: 'Not found' })
  })

  it.each([
    ['malformed JSON', '{not json'],
    ['an array body', [{ action: 'enable', harness: 'opencode', dryRun: false }]],
    ['a missing action', { harness: 'opencode', dryRun: false }],
    ['an unknown action', { action: 'status', harness: 'opencode', dryRun: false }],
    ['a non-string action', { action: 1, harness: 'opencode', dryRun: false }],
    ['a missing harness', { action: 'enable', dryRun: false }],
    ['an unknown harness', { action: 'enable', harness: 'no-such-harness', dryRun: false }],
    ['a non-string harness', { action: 'enable', harness: ['opencode'], dryRun: false }],
    ['a missing dryRun', { action: 'enable', harness: 'opencode' }],
    ['a string dryRun', { action: 'enable', harness: 'opencode', dryRun: 'false' }],
    ['a null dryRun', { action: 'enable', harness: 'opencode', dryRun: null }],
    ['a number dryRun', { action: 'disable', harness: 'opencode', dryRun: 0 }],
    ['an extra key beyond the documented set', { action: 'enable', harness: 'opencode', dryRun: false, endpoint: 'http://127.0.0.1:4318' }],
    ['the opt-in on enable', { action: 'enable', harness: 'opencode', dryRun: false, removeUnreceiptedKeys: true }],
    ['a non-boolean opt-in', { action: 'disable', harness: 'opencode', dryRun: false, removeUnreceiptedKeys: 'yes' }],
    ['a confirmed manual-key removal without its preview', { action: 'disable', harness: 'opencode', dryRun: false, removeUnreceiptedKeys: true }],
  ])('rejects %s with 400 and writes nothing', async (_label, payload) => {
    const home = makeHome()
    const configPath = opencodePath(home)
    writeConfig(configPath, '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))

    const { status, body } = await callRoute('POST', PATH, { deps, payload })
    expect(status).toBe(400)
    expect(ownKeys(body)).toEqual(['error'])
    expect(readFileSync(configPath, 'utf8')).toBe('{}\n')
    expect(existsSync(receiptPathForHome(home))).toBe(false)
  })

  it.each([
    ['codex', codexPath],
    ['pi', piSettingsPath],
    ['antigravity', antigravityPath],
    ['kilo', kiloConfigPath],
  ])('rejects a status-only harness (%s) with 400 and writes nothing', async (harness, pathFor) => {
    const home = makeHome()
    const configPath = pathFor(home)
    writeConfig(configPath, '# synthetic status-only config\n')
    const deps = makeCaptureDeps(captureSettings(home))

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness, dryRun: false },
    })
    expect(status).toBe(400)
    expect(ownKeys(body)).toEqual(['error'])
    expect(readFileSync(configPath, 'utf8')).toBe('# synthetic status-only config\n')
    expect(existsSync(receiptPathForHome(home))).toBe(false)
  })

  it('rejects a status-only harness as a dry run too: a pending writer is never managed', async () => {
    // `dryRun: true` is the drawer's preview path. A pending harness has no managed key to
    // preview, so the request is malformed rather than an empty preview.
    const home = makeHome()
    const configPath = kiloConfigPath(home)
    writeConfig(configPath, '{ "model": "synthetic" }\n')
    const deps = makeCaptureDeps(captureSettings(home))

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'kilo', dryRun: true },
    })
    expect(status).toBe(400)
    expect(ownKeys(body)).toEqual(['error'])
    expect(readFileSync(configPath, 'utf8')).toBe('{ "model": "synthetic" }\n')
    expect(existsSync(receiptPathForHome(home))).toBe(false)
  })

  it('rejects a confirm whose echo carries more changes than the client keeps with 400', async () => {
    // The client caps each event at 64 changes (CAPTURE_MAX_CHANGES). A larger bound here
    // would accept an echo the client cannot produce, so the bound must match.
    const home = makeHome()
    const configPath = claudePath(home)
    writeConfig(configPath, '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))
    const event = {
      kind: 'would-restore',
      harness: 'claude-code',
      fileId: 'claude-settings',
      path: configPath,
      changes: Array.from({ length: 65 }, (_, index) => ({ key: `env.K${index}`, after: index })),
    }

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: {
        action: 'disable',
        harness: 'claude-code',
        dryRun: false,
        removeUnreceiptedKeys: true,
        expectedPreviewEvents: [event],
      },
    })
    expect(status).toBe(400)
    expect(ownKeys(body)).toEqual(['error'])
  })

  it('rejects a non-JSON content type with 415 and writes nothing', async () => {
    const home = makeHome()
    const configPath = opencodePath(home)
    writeConfig(configPath, '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: VALID,
      contentType: 'text/plain',
    })
    expect(status).toBe(415)
    expect(body).toEqual({ error: 'Content-Type must be application/json' })
    expect(readFileSync(configPath, 'utf8')).toBe('{}\n')
    expect(existsSync(receiptPathForHome(home))).toBe(false)
  })

  it('rejects an oversized body with 413, destroys the request, and writes nothing', async () => {
    const home = makeHome()
    const configPath = opencodePath(home)
    writeConfig(configPath, '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))

    const { status, body, destroyed } = await callRoute('POST', PATH, {
      deps,
      chunks: ['x'.repeat(40_000), 'x'.repeat(40_000)],
    })
    expect(status).toBe(413)
    expect((body as { error?: unknown }).error).toEqual(expect.stringContaining('exceeds'))
    expect(destroyed).toBe(true)
    expect(readFileSync(configPath, 'utf8')).toBe('{}\n')
    expect(existsSync(receiptPathForHome(home))).toBe(false)
  })

  it.each(['PUT', 'DELETE', 'PATCH'])('rejects %s with 405', async (method) => {
    const { status, handled, body } = await callRoute(method, PATH, {
      deps: makeCaptureDeps(captureSettings(makeHome())),
      payload: VALID,
    })
    expect(handled).toBe(true)
    expect(status).toBe(405)
    expect(body).toEqual({ error: 'Method Not Allowed' })
  })

  it('previews a dry-run enable exactly as the post-preview-enable fixture, writing nothing', async () => {
    const home = makeHome()
    // The fixture's claude-code: logs exporter on "console", telemetry enable already set —
    // eleven keys would change, the present ensure key would not.
    writeConfig(
      claudePath(home),
      `${JSON.stringify({ env: { OTEL_LOGS_EXPORTER: 'console', CLAUDE_CODE_ENABLE_TELEMETRY: '1' } }, null, 2)}\n`,
    )
    const deps = makeCaptureDeps(captureSettings(home))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')
    const before = readFileSync(claudePath(home), 'utf8')

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'claude-code', dryRun: true },
    })
    expect(status).toBe(200)
    expect(fixtureComparable(body)).toEqual(fixtureComparable(loadFixture('post-preview-enable')))
    expect(readFileSync(claudePath(home), 'utf8')).toBe(before)
    expect(existsSync(receiptPathForHome(home))).toBe(false)
    // A dry run turns nothing on — not the config keys, not the receiver setting.
    expect(readSetting(deps.store, SETTING_KEYS.receiverHosted)).toBe('on')
  })

  it('does not turn the receiver setting on for a dry run', async () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'opencode', dryRun: true },
    })
    expect(status).toBe(200)
    expect(asBody(body).outcome).toBe('previewed')
    expect(readSetting(deps.store, SETTING_KEYS.receiverHosted)).toBe('off')
    expect(readFileSync(opencodePath(home), 'utf8')).toBe('{}\n')
  })

  it('writes a real enable exactly as the post-wrote-enable fixture, and never creates a not-applicable file', async () => {
    const home = makeHome()
    // The fixture's copilot: Stable exists with enabled:false; Insiders does not exist, so
    // it is not applicable — no event, and no file created on its account.
    writeConfig(copilotStablePath(home), copilotStableBeforeSeed())
    const fetchUrls: string[] = []
    const deps = makeCaptureDeps(
      captureSettings(home, {
        fetchHealth: async (url) => {
          fetchUrls.push(url)
          return KYBERDASH_LISTENER(url)
        },
      }),
    )
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'copilot', dryRun: false },
    })
    expect(status).toBe(200)
    expect(fixtureComparable(body)).toEqual(fixtureComparable(loadFixture('post-wrote-enable')))
    expect(fetchUrls).toContain('http://127.0.0.1:4318/healthz')

    // Only the applicable file was written; Insiders is untouched and still absent.
    expect(existsSync(copilotInsidersPath(home))).toBe(false)
    const written = JSON.parse(readFileSync(copilotStablePath(home), 'utf8')) as {
      github?: { copilot?: { chat?: { otel?: Record<string, unknown> } } }
    }
    expect(written.github?.copilot?.chat?.otel).toEqual({
      'enabled': true,
      'exporterType': 'otlp-http',
      'otlpEndpoint': 'http://127.0.0.1:4318',
      'captureContent': true,
      'maxAttributeSizeChars': 0,
    })

    const receipt = JSON.parse(readFileSync(receiptPathForHome(home), 'utf8')) as {
      files: Array<{ path: string; format: string; keys: Record<string, { written: unknown }> }>
    }
    expect(receipt.files).toHaveLength(1)
    expect(receipt.files[0]!.path).toBe(copilotStablePath(home))
    expect(receipt.files[0]!.format).toBe('jsonc')
    expect(receipt.files[0]!.keys['github.copilot.chat.otel.enabled']?.written).toBe(true)
    expect(receipt.files[0]!.keys['github.copilot.chat.otel.maxAttributeSizeChars']?.written).toBe(0)
  })

  it('turns the receiver-hosted setting on when a real enable finds it off', async () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))

    const { status, body } = await callRoute('POST', PATH, { deps, payload: VALID })
    expect(status).toBe(200)
    expect(asBody(body).outcome).toBe('wrote')
    expect(asBody(body).receiver).toMatchObject({ enableFailed: false })
    expect(readSetting(deps.store, SETTING_KEYS.receiverHosted)).toBe('on')
    expect(readFileSync(opencodePath(home), 'utf8')).toContain('"openTelemetry": true')
  })

  it('answers 200 with enableFailed when the receiver-setting write fails AFTER the config write', async () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))
    // The receiver-hosted write is the only store write the route makes; failing it after
    // the config write is the recorded ordering, so the config stands and the failure is
    // reported in the receiver block rather than as a 5xx.
    const original = deps.store.setMetadata.bind(deps.store)
    vi.spyOn(deps.store, 'setMetadata').mockImplementation((key: string, value: string) => {
      if (key === SETTING_KEYS.receiverHosted) {
        throw new Error('SQLITE_BUSY: database is locked (/Users/secret/canon.db)')
      }
      original(key, value)
    })
    vi.spyOn(console, 'error').mockImplementation(() => {})

    const { status, body, raw } = await callRoute('POST', PATH, { deps, payload: VALID })
    expect(status).toBe(200)
    const doc = asBody(body)
    expect(doc.outcome).toBe('wrote')
    expect(doc.receiver).toMatchObject({
      hosted: false,
      enabled: false,
      enableFailed: true,
      lastManagedHarnessDisabled: false,
    })
    expect(typeof doc.receiver?.['willEnable']).toBe('boolean')
    // The config write happened before the setting write, so both stand recorded.
    expect(readFileSync(opencodePath(home), 'utf8')).toContain('"openTelemetry": true')
    expect(existsSync(receiptPathForHome(home))).toBe(true)
    // The sqlite message names a real path on this machine; it stays off the wire.
    expect(raw).not.toContain('/Users/secret')
    expect(raw).not.toContain('SQLITE_BUSY')
    expect(readSetting(deps.store, SETTING_KEYS.receiverHosted)).toBe('off')
  })

  it('answers noop for a second enable of an already-enabled harness', async () => {
    const home = makeHome()
    writeConfig(opencodePath(home), '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))

    const first = await callRoute('POST', PATH, { deps, payload: VALID })
    expect(first.status).toBe(200)
    expect(asBody(first.body).outcome).toBe('wrote')
    const afterFirst = readFileSync(opencodePath(home), 'utf8')

    const second = await callRoute('POST', PATH, { deps, payload: VALID })
    expect(second.status).toBe(200)
    expect(asBody(second.body).outcome).toBe('noop')
    const events = asBody(second.body).events ?? []
    expect(events).toHaveLength(1)
    expect(events[0]).toMatchObject({ kind: 'noop', harness: 'opencode', fileId: 'opencode-config' })
    expect(readFileSync(opencodePath(home), 'utf8')).toBe(afterFirst)
  })

  it('disables the last managed harness exactly as the post-disable-last fixture and leaves the receiver hosted', async () => {
    const home = makeHome()
    const seed = '{\n  // synthetic opencode fixture: the comment must survive the round trip\n}\n'
    writeConfig(opencodePath(home), seed)
    const deps = makeCaptureDeps(captureSettings(home))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const enable = await callRoute('POST', PATH, { deps, payload: VALID })
    expect(enable.status).toBe(200)

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'disable', harness: 'opencode', dryRun: false },
    })
    expect(status).toBe(200)
    expect(fixtureComparable(body)).toEqual(fixtureComparable(loadFixture('post-disable-last')))
    // The owner's bytes come back exactly, comment and all.
    expect(readFileSync(opencodePath(home), 'utf8')).toBe(seed)
    // Disabling the last managed harness never stops the receiver.
    expect(readSetting(deps.store, SETTING_KEYS.receiverHosted)).toBe('on')
    const receipt = JSON.parse(readFileSync(receiptPathForHome(home), 'utf8')) as {
      files: Array<{ path: string }>
    }
    expect(receipt.files.some((file) => file.path === opencodePath(home))).toBe(false)
  })

  it('refuses a drifted disable with 409 exactly as the post-refused-drift fixture, touching nothing', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const enable = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'claude-code', dryRun: false },
    })
    expect(enable.status).toBe(200)
    const receiptBefore = readFileSync(receiptPathForHome(home), 'utf8')

    // The fixture's two drifts: one key moved off the written value, one removed outright.
    const written = JSON.parse(readFileSync(claudePath(home), 'utf8')) as { env: Record<string, unknown> }
    written.env['OTEL_LOG_USER_PROMPTS'] = '0'
    delete written.env['OTEL_LOG_RAW_API_BODIES']
    writeFileSync(claudePath(home), `${JSON.stringify(written, null, 2)}\n`)
    const tampered = readFileSync(claudePath(home), 'utf8')

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'disable', harness: 'claude-code', dryRun: false },
    })
    expect(status).toBe(409)
    expect(fixtureComparable(body)).toEqual(fixtureComparable(loadFixture('post-refused-drift')))
    expect(readFileSync(claudePath(home), 'utf8')).toBe(tampered)
    expect(readFileSync(receiptPathForHome(home), 'utf8')).toBe(receiptBefore)
  })

  it('refuses a disable whose config has become unreadable with 409 reason unreadable', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const enable = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'claude-code', dryRun: false },
    })
    expect(enable.status).toBe(200)

    // A directory in place of the config: every read fails with an errno (EISDIR here),
    // and a host error is reduced to its errno code only.
    rmSync(claudePath(home))
    mkdirSync(claudePath(home), { recursive: true })

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'disable', harness: 'claude-code', dryRun: false },
    })
    expect(status).toBe(409)
    const doc = asBody(body)
    expect(doc.outcome).toBe('refused')
    expect(ownKeys(doc)).toEqual(['error', 'events', 'outcome', 'reason'])
    expect(doc.reason).toBe('unreadable')
    expect((doc.events ?? [])[0]).toMatchObject({ kind: 'refused', harness: 'claude-code', code: 'EISDIR' })
  })

  it('refuses a disable whose config no longer parses with 409 reason unparseable', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    const deps = makeCaptureDeps(captureSettings(home))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const enable = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'claude-code', dryRun: false },
    })
    expect(enable.status).toBe(200)

    writeFileSync(claudePath(home), '{not json')

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'disable', harness: 'claude-code', dryRun: false },
    })
    expect(status).toBe(409)
    const doc = asBody(body)
    expect(doc.outcome).toBe('refused')
    expect(doc.reason).toBe('unparseable')
    expect(ownKeys(doc)).toEqual(['error', 'events', 'outcome', 'reason'])
    expect((doc.events ?? [])[0]).toMatchObject({ kind: 'refused', harness: 'claude-code' })
  })

  it('removes only declared keys on a Settings disable with the opt-in and no receipt, preserving the rest', async () => {
    const home = makeHome()
    const seed =
      `${JSON.stringify({ env: { OTEL_LOGS_EXPORTER: 'otlp', UNRELATED_OWNER_KEY: 'keep' } }, null, 2)}\n`
    writeConfig(claudePath(home), seed)
    const deps = makeCaptureDeps(captureSettings(home))

    const preview = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'disable', harness: 'claude-code', dryRun: true, removeUnreceiptedKeys: true },
    })
    expect(preview.status).toBe(200)
    const previewDoc = asBody(preview.body)
    expect(previewDoc.outcome).toBe('previewed')
    expect(readFileSync(claudePath(home), 'utf8')).toBe(seed)

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: {
        action: 'disable',
        harness: 'claude-code',
        dryRun: false,
        removeUnreceiptedKeys: true,
        expectedPreviewEvents: previewDoc['events'],
      },
    })
    expect(status).toBe(200)
    expect(asBody(body).outcome).toBe('wrote')
    const doc = JSON.parse(readFileSync(claudePath(home), 'utf8')) as { env: Record<string, unknown> }
    expect(doc.env['OTEL_LOGS_EXPORTER']).toBeUndefined()
    expect(doc.env['UNRELATED_OWNER_KEY']).toBe('keep')
    expect(existsSync(receiptPathForHome(home))).toBe(false)
  })

  it('refuses a manual-key removal when a value changed after the Settings preview', async () => {
    const home = makeHome()
    const seed = `${JSON.stringify({ env: { OTEL_LOGS_EXPORTER: 'otlp', UNRELATED_OWNER_KEY: 'keep' } }, null, 2)}\n`
    writeConfig(claudePath(home), seed)
    const deps = makeCaptureDeps(captureSettings(home))

    const preview = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'disable', harness: 'claude-code', dryRun: true, removeUnreceiptedKeys: true },
    })
    const previewDoc = asBody(preview.body)
    const changed = `${JSON.stringify({ env: { OTEL_LOGS_EXPORTER: 'console', UNRELATED_OWNER_KEY: 'keep' } }, null, 2)}\n`
    writeConfig(claudePath(home), changed)

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: {
        action: 'disable',
        harness: 'claude-code',
        dryRun: false,
        removeUnreceiptedKeys: true,
        expectedPreviewEvents: previewDoc['events'],
      },
    })

    expect(status).toBe(409)
    expect((asBody(body) as Record<string, unknown>)['error']).toMatch(/changed since preview/i)
    expect(asBody(body).reason).toBe('drift')
    expect(readFileSync(claudePath(home), 'utf8')).toBe(changed)
  })

  it('refuses a Settings disable with the opt-in when the receipt is corrupt, writing nothing', async () => {
    const home = makeHome()
    const seed = `${JSON.stringify({ env: { OTEL_LOGS_EXPORTER: 'otlp' } }, null, 2)}\n`
    writeConfig(claudePath(home), seed)
    writeConfig(receiptPathForHome(home), '{not json')
    const deps = makeCaptureDeps(captureSettings(home))

    const { status, body } = await callRoute('POST', PATH, {
      deps,
      payload: {
        action: 'disable',
        harness: 'claude-code',
        dryRun: false,
        removeUnreceiptedKeys: true,
        expectedPreviewEvents: [],
      },
    })
    expect(status).toBe(409)
    expect(asBody(body).reason).toBe('unparseable')
    expect(readFileSync(claudePath(home), 'utf8')).toBe(seed)
  })

  it('answers the fixed 500 body for a thrown engine failure, on GET and POST, and keeps serving', async () => {
    // The install probe is injectable, so an unexpected engine defect is reachable the
    // only honest way: injected. The route must survive it with the fixed body.
    const exploding: CaptureInstallProbe = () => {
      throw new Error('install probe exploded (/Users/secret/home)')
    }
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const deps = makeCaptureDeps(captureSettings(makeHome(), { installProbe: exploding }))

    const got = await callRoute('GET', PATH, { deps })
    expect(got.status).toBe(500)
    expect(got.body).toEqual({ error: 'Capture failed' })
    expect(got.raw).not.toContain('/Users/secret')
    expect(got.raw).not.toContain('exploded')

    const posted = await callRoute('POST', PATH, { deps, payload: VALID })
    expect(posted.status).toBe(500)
    expect(posted.body).toEqual({ error: 'Capture failed' })
    expect(posted.raw).not.toContain('/Users/secret')

    // The process is still serving: the next request on the same route is answered again.
    const again = await callRoute('GET', PATH, { deps })
    expect(again.status).toBe(500)
    expect(again.body).toEqual({ error: 'Capture failed' })
  })

  it('never leaks a key KyberDash does not manage, in any response', async () => {
    const secret = 'sk-ant-synth-NEVER-LEAK-0123456789abcdef'
    const home = makeHome()
    // An owner key that is none of KyberDash's business, sitting in the same document the
    // route reads and rewrites.
    writeConfig(
      claudePath(home),
      `${JSON.stringify({ env: { ANTHROPIC_API_KEY: secret, OTEL_LOGS_EXPORTER: 'console' } }, null, 2)}\n`,
    )
    const deps = makeCaptureDeps(captureSettings(home))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const got = await callRoute('GET', PATH, { deps })
    expect(got.status).toBe(200)
    // Status reports only managed keys — never another key's name, never a value.
    expect(harnessById(got.body, 'claude-code').files[0]!.presentKeys).toEqual(['env.OTEL_LOGS_EXPORTER'])

    const preview = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'claude-code', dryRun: true },
    })
    expect(preview.status).toBe(200)

    const wrote = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'enable', harness: 'claude-code', dryRun: false },
    })
    expect(wrote.status).toBe(200)

    const disabled = await callRoute('POST', PATH, {
      deps,
      payload: { action: 'disable', harness: 'claude-code', dryRun: false },
    })
    expect(disabled.status).toBe(200)

    for (const response of [got, preview, wrote, disabled]) {
      expect(response.raw).not.toContain(secret)
      expect(response.raw).not.toContain('ANTHROPIC_API_KEY')
    }
    // The owner's key survives the whole round trip untouched.
    expect(readFileSync(claudePath(home), 'utf8')).toContain(secret)
  })

  it('serializes concurrent POSTs: one write, and the receipt stays one valid document', async () => {
    const home = makeHome()
    writeConfig(claudePath(home), '{}\n')
    // A probe that settles on a real timer widens the in-flight window so the two
    // requests genuinely overlap instead of finishing in the same microtask.
    const slow: HealthFetcher = async () => {
      await new Promise((resolve) => setTimeout(resolve, 5))
      return { status: 200, body: JSON.stringify({ service: 'kyberdash-otlp' }) }
    }
    const deps = makeCaptureDeps(captureSettings(home, { fetchHealth: slow }))
    writeSetting(deps.store, SETTING_KEYS.receiverHosted, 'on')

    const both = await Promise.all([
      callRoute('POST', PATH, { deps, payload: { action: 'enable', harness: 'claude-code', dryRun: false } }),
      callRoute('POST', PATH, { deps, payload: { action: 'enable', harness: 'claude-code', dryRun: false } }),
    ])

    for (const response of both) expect([200, 409]).toContain(response.status)
    const outcomes = both
      .filter((response) => response.status === 200)
      .map((response) => asBody(response.body).outcome)
    const refusals = both
      .filter((response) => response.status === 409)
      .map((response) => asBody(response.body).reason)
    // Sequential (wrote then noop) or refused-as-busy: either is fine, but exactly one
    // request did the write — interleaved writers would both claim it.
    expect(outcomes).toContain('wrote')
    expect(outcomes.filter((outcome) => outcome === 'wrote')).toHaveLength(1)
    for (const outcome of outcomes) expect(['wrote', 'noop']).toContain(outcome)
    for (const reason of refusals) expect(reason).toBe('busy')

    // Whatever the interleaving, the receipt is one parseable document with one entry,
    // and the config is one parseable document holding the managed keys.
    const receipt = JSON.parse(readFileSync(receiptPathForHome(home), 'utf8')) as {
      files: Array<{ path: string }>
    }
    expect(receipt.files).toHaveLength(1)
    expect(receipt.files[0]!.path).toBe(claudePath(home))
    const config = JSON.parse(readFileSync(claudePath(home), 'utf8')) as { env?: Record<string, unknown> }
    expect(config.env?.['OTEL_LOGS_EXPORTER']).toBe('otlp')
    expect(config.env?.['OTEL_EXPORTER_OTLP_ENDPOINT']).toBe('http://127.0.0.1:4318')
  })
})

/** Copilot Stable seeded with only `enabled: false` — the fixture's before value. */
function copilotStableBeforeSeed(): string {
  return `${JSON.stringify({ github: { copilot: { chat: { otel: { enabled: false } } } } }, null, 2)}\n`
}
