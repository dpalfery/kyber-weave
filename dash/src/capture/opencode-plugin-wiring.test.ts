// T4 wiring: the OpenCode plugin and its consent file ride the existing capture engine.
// `capture enable|disable --harness opencode` (and so POST /api/kyber/capture, which calls
// the same runEnable/runDisable) install/remove the plugin and flip content consent; status
// (CLI text and structured) reports both; the `capture plugin install` verb is registered.
//
// Every path is under a mkdtemp HOME; the real ~/.config/opencode and ~/.kyberdash are never
// touched, and no test reaches the live receiver (health probes are injected).

import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { Command } from 'commander'
import { afterEach, describe, expect, it } from 'vitest'

import { registerKyberCommands } from '../cli/register.js'
import { callRoute, makeDeps, type RouteDeps } from '../server/testing.js'
import { OPENCODE_PLUGIN_SOURCE } from './harnesses/opencode-plugin/generated/plugin-source.js'
import { runPluginInstall } from './harnesses/opencode-plugin/install-command.js'
import { runCapture } from './index.js'
import { buildCaptureStatus } from './status.js'

const homes: string[] = []
afterEach(() => {
  for (const home of homes.splice(0)) rmSync(home, { recursive: true, force: true })
})
function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-oc-wiring-'))
  homes.push(home)
  return home
}
const NO_LISTENER = async (): Promise<{ status: number; body: string }> => {
  throw new Error('connect ECONNREFUSED 127.0.0.1:4318 (synthetic)')
}
const pluginFile = (home: string): string => join(home, '.config', 'opencode', 'plugins', 'kyberdash-opencode.js')
const consentFile = (home: string): string => join(home, '.kyberdash', 'opencode-plugin.json')
const jsonc = (home: string): string => join(home, '.config', 'opencode', 'opencode.jsonc')
const run = (action: 'enable' | 'disable', home: string, dryRun = false) =>
  runCapture(action, { homeDir: home, harnessIds: ['opencode'], fetchHealth: NO_LISTENER, dryRun })
const consent = (home: string): unknown => JSON.parse(readFileSync(consentFile(home), 'utf8'))

describe('capture enable/disable --harness opencode', () => {
  it('enable writes the JSONC key, installs the plugin and sets content true (creating the file)', async () => {
    const home = makeHome()
    const result = await run('enable', home)
    expect(result.exitCode).toBe(0)
    expect(readFileSync(jsonc(home), 'utf8')).toContain('openTelemetry')
    expect(readFileSync(pluginFile(home), 'utf8')).toBe(OPENCODE_PLUGIN_SOURCE)
    expect(consent(home)).toEqual({ version: 1, content: true })
    expect(result.events).toEqual(
      expect.arrayContaining([expect.objectContaining({ harness: 'opencode', fileId: 'plugin', kind: 'wrote' })]),
    )
  })

  it('enable flips an existing content:false file to true', async () => {
    const home = makeHome()
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    writeFileSync(consentFile(home), '{"version":1,"content":false}')
    await run('enable', home)
    expect(consent(home)).toEqual({ version: 1, content: true })
  })

  it('enable dry run changes nothing under HOME', async () => {
    const home = makeHome()
    const result = await run('enable', home, true)
    expect(result.exitCode).toBe(0)
    expect(existsSync(join(home, '.config'))).toBe(false)
    expect(existsSync(join(home, '.kyberdash'))).toBe(false)
  })

  it('enable refuses a foreign plugin file with exit 1, keeps it, and does not turn consent on', async () => {
    const home = makeHome()
    mkdirSync(join(pluginFile(home), '..'), { recursive: true })
    writeFileSync(pluginFile(home), 'export default {}\n')
    const result = await run('enable', home)
    expect(result.exitCode).toBe(1)
    expect(readFileSync(pluginFile(home), 'utf8')).toBe('export default {}\n')
    expect(existsSync(consentFile(home))).toBe(false)
    expect(result.events).toEqual(
      expect.arrayContaining([expect.objectContaining({ fileId: 'plugin', kind: 'refused', reason: 'conflict' })]),
    )
  })

  it('disable sets content false and removes the plugin and the JSONC key', async () => {
    const home = makeHome()
    await run('enable', home)
    const result = await run('disable', home)
    expect(result.exitCode).toBe(0)
    expect(existsSync(pluginFile(home))).toBe(false)
    expect(consent(home)).toEqual({ version: 1, content: false })
    expect(readFileSync(jsonc(home), 'utf8')).not.toContain('"openTelemetry": true')
  })

  it('disable still turns consent off when the plugin was edited and is kept (exit 1)', async () => {
    const home = makeHome()
    await run('enable', home)
    writeFileSync(pluginFile(home), `${OPENCODE_PLUGIN_SOURCE}// edit\n`)
    const result = await run('disable', home)
    expect(result.exitCode).toBe(1)
    expect(existsSync(pluginFile(home))).toBe(true)
    expect(consent(home)).toEqual({ version: 1, content: false })
  })

  it('disable dry run changes nothing', async () => {
    const home = makeHome()
    await run('enable', home)
    const result = await run('disable', home, true)
    expect(result.exitCode).toBe(0)
    expect(existsSync(pluginFile(home))).toBe(true)
    expect(consent(home)).toEqual({ version: 1, content: true })
  })
})

describe('status reports plugin and consent', () => {
  it('CLI text and structured status carry the same fixed lines', async () => {
    const home = makeHome()
    const before = buildCaptureStatus({ home, env: { platform: process.platform }, endpoint: 'http://127.0.0.1:4318' })
    const off = before.find((entry) => entry.id === 'opencode')!
    expect(off.statusLines).toEqual(
      expect.arrayContaining(['plugin: not installed', expect.stringMatching(/^content consent: not set/)]),
    )
    await run('enable', home)
    const after = buildCaptureStatus({ home, env: { platform: process.platform }, endpoint: 'http://127.0.0.1:4318' })
    const on = after.find((entry) => entry.id === 'opencode')!
    expect(on.statusLines).toEqual(
      expect.arrayContaining(['plugin: installed', expect.stringMatching(/^content consent: on/)]),
    )
    const text = await runCapture('status', { homeDir: home, harnessIds: ['opencode'], fetchHealth: NO_LISTENER })
    expect(text.stdout).toContain('plugin: installed')
    expect(text.stdout).toContain('content consent: on')
  })
})

describe('POST /api/kyber/capture for opencode', () => {
  it('enable installs the plugin, sets consent, and the response status shows both', async () => {
    const home = makeHome()
    const deps: RouteDeps = (makeDeps as unknown as (options?: unknown) => RouteDeps)({
      capture: { homeDir: home, platform: process.platform, fetchHealth: NO_LISTENER },
    })
    const { status, body } = await callRoute('POST', '/api/kyber/capture', {
      deps,
      payload: { action: 'enable', harness: 'opencode', dryRun: false },
    })
    expect(status).toBe(200)
    expect(consent(home)).toEqual({ version: 1, content: true })
    expect(existsSync(pluginFile(home))).toBe(true)
    const lines = (body as { harnessState: { statusLines: string[] } }).harnessState.statusLines
    expect(lines).toEqual(expect.arrayContaining(['plugin: installed', expect.stringMatching(/^content consent: on/)]))

    const off = await callRoute('POST', '/api/kyber/capture', {
      deps,
      payload: { action: 'disable', harness: 'opencode', dryRun: false },
    })
    expect(off.status).toBe(200)
    expect(consent(home)).toEqual({ version: 1, content: false })
    expect(existsSync(pluginFile(home))).toBe(false)
  })
})

describe('capture plugin install verb', () => {
  it('is registered under kyber capture with its flags', () => {
    const program = new Command()
    registerKyberCommands(program)
    const capture = program.commands.find((c) => c.name() === 'kyber')?.commands.find((c) => c.name() === 'capture')
    const plugin = capture?.commands.find((c) => c.name() === 'plugin')
    const install = plugin?.commands.find((c) => c.name() === 'install')
    expect(install).toBeDefined()
    expect(install!.options.map((o) => o.long).sort()).toEqual(
      ['--config-dir', '--dry-run', '--harness', '--if-installed', '--preserve'].sort(),
    )
  })

  const base = (home: string, extra: Record<string, unknown> = {}) =>
    runPluginInstall({ harness: 'opencode', homeDir: home, processEnv: {}, ...extra })

  it('exits 2 for a missing or unsupported harness', () => {
    const home = makeHome()
    expect(runPluginInstall({ homeDir: home, processEnv: {} }).exitCode).toBe(2)
    expect(runPluginInstall({ harness: 'kilo', homeDir: home, processEnv: {} }).exitCode).toBe(2)
    expect(existsSync(join(home, '.kyberdash'))).toBe(false)
  })

  it('preserve install prints the contract lines and keeps consent off', () => {
    const home = makeHome()
    const root = join(home, 'oc-root')
    const result = base(home, { preserve: true, configDir: root })
    expect(result.exitCode).toBe(0)
    const lines = result.stdout.trim().split('\n')
    expect(lines[0]).toBe(`opencode plugin: installed ${join(root, 'plugins', 'kyberdash-opencode.js')}`)
    expect(lines).toContain(`opencode consent: created ${consentFile(home)}`)
    expect(result.stdout).toMatch(/turned on in KyberDash Settings/)
    expect(consent(home)).toEqual({ version: 1, content: false })
  })

  it('preserve keeps a foreign file: warning on stderr, exit 0', () => {
    const home = makeHome()
    mkdirSync(join(pluginFile(home), '..'), { recursive: true })
    writeFileSync(pluginFile(home), 'export default {}\n')
    const result = base(home, { preserve: true })
    expect(result.exitCode).toBe(0)
    expect(result.stdout.split('\n')[0]).toMatch(/^opencode plugin: kept-foreign /)
    expect(result.stderr).toMatch(/warning/)
  })

  it('strict (no --preserve) exits 1 on a foreign file', () => {
    const home = makeHome()
    mkdirSync(join(pluginFile(home), '..'), { recursive: true })
    writeFileSync(pluginFile(home), 'export default {}\n')
    const result = base(home)
    expect(result.exitCode).toBe(1)
    expect(result.stderr).toMatch(/not written by KyberDash/)
  })

  it('--if-installed with nothing installed prints skipped-not-installed and writes nothing', () => {
    const home = makeHome()
    const result = base(home, { preserve: true, ifInstalled: true })
    expect(result.exitCode).toBe(0)
    expect(result.stdout.split('\n')[0]).toMatch(/^opencode plugin: skipped-not-installed /)
    expect(existsSync(join(home, '.kyberdash'))).toBe(false)
  })

  it('--dry-run prints would-install and writes nothing', () => {
    const home = makeHome()
    const result = base(home, { preserve: true, dryRun: true })
    expect(result.stdout.split('\n')[0]).toMatch(/^opencode plugin: would-install /)
    expect(existsSync(join(home, '.config'))).toBe(false)
    expect(existsSync(join(home, '.kyberdash'))).toBe(false)
  })
})
