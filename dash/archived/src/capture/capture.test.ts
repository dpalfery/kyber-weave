import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, relative } from 'node:path'

import { Command, CommanderError } from 'commander'
import { afterEach, describe, expect, it } from 'vitest'

import { registerKyberCommands } from '../cli/register.js'
import { runCapture } from './index.js'
import type { CaptureHarnessWriter } from './harnesses/registry.js'

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

/** A fresh synthetic HOME; every test resolves all config paths beneath it. */
function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-capture-t8-'))
  temporaryHomes.push(home)
  return home
}

/** A managed JSON harness whose config lives under the given (temporary) HOME. */
function jsonWriter(home: string, id = 'test-json'): CaptureHarnessWriter {
  return {
    kind: 'managed',
    id,
    displayName: 'Test JSON Harness',
    resolvePath: (h: string) => join(h, '.test-capture', `${id}.json`),
    format: 'json',
    desiredKeys: (endpoint: string) => ({ 'otel.endpoint': endpoint }),
  }
}

/** A managed TOML harness whose config lives under the given (temporary) HOME. */
function tomlWriter(home: string, id = 'test-toml'): CaptureHarnessWriter {
  return {
    kind: 'managed',
    id,
    displayName: 'Test TOML Harness',
    resolvePath: (h: string) => join(h, '.test-capture', `${id}.toml`),
    format: 'toml',
    desiredKeys: (endpoint: string) => ({ 'otel.endpoint': endpoint }),
  }
}

function writeConfig(path: string, content: string): void {
  mkdirSync(join(path, '..'), { recursive: true })
  writeFileSync(path, content)
}

/** Every path under `root`, relative and sorted, so a fresh home lists as empty. */
function listTree(root: string): string[] {
  const out: string[] = []
  const walk = (dir: string): void => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name)
      out.push(relative(root, full))
      if (entry.isDirectory()) walk(full)
    }
  }
  walk(root)
  return out.sort()
}

const NO_LISTENER = async (): Promise<{ status: number; body: string }> => {
  throw new Error('connect ECONNREFUSED 127.0.0.1:4318 (synthetic)')
}

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

const FOREIGN_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: '<html><body>synthetic foreign console</body></html>',
})

describe('kyber capture command registration', () => {
  it('registers the capture subgroup with status, enable and disable', () => {
    const program = new Command()
    registerKyberCommands(program)

    const kyber = program.commands.find((command) => command.name() === 'kyber')
    const capture = kyber?.commands.find((command) => command.name() === 'capture')
    expect(capture).toBeDefined()
    const subcommands = (capture?.commands ?? []).map((command) => command.name()).sort()
    expect(subcommands).toEqual(['disable', 'enable', 'plugin', 'status'])
  })

  it('declares --harness, --dry-run and --endpoint on every capture action', () => {
    const program = new Command()
    registerKyberCommands(program)

    const capture = program.commands
      .find((command) => command.name() === 'kyber')
      ?.commands.find((command) => command.name() === 'capture')
    for (const action of ['status', 'enable', 'disable']) {
      const sub = capture?.commands.find((command) => command.name() === action)
      expect(sub).toBeDefined()
      const flags = (sub?.options ?? []).map((option) => option.long)
      expect(flags).toContain('--harness')
      expect(flags).toContain('--endpoint')
    }
    const enable = capture?.commands.find((command) => command.name() === 'enable')
    expect((enable?.options ?? []).map((option) => option.long)).toContain('--dry-run')
  })

  it.each([
    ['unknown harness', ['kyber', 'capture', 'enable', '--harness', 'no-such-harness']],
    ['non-OTLP endpoint', ['kyber', 'capture', 'enable', '--endpoint', 'grpc://127.0.0.1:4317']],
  ])('exits 2 through the CLI for %s', async (_label, args) => {
    const program = new Command()
    program.exitOverride()
    program.configureOutput({ writeOut: () => {}, writeErr: () => {} })
    registerKyberCommands(program)

    const error = await program.parseAsync(['node', 'kyberdash', ...args]).catch((e: unknown) => e)
    expect(error).toBeInstanceOf(CommanderError)
    expect((error as CommanderError).exitCode).toBe(2)
  })
})

describe('capture enable --dry-run', () => {
  it('writes nothing: config digests are unchanged and no receipt is recorded', async () => {
    const home = makeHome()
    // Synthetic JSONC fixture: real key names and structure, invented content.
    const initial = '{\n  // synthetic fixture comment\n  "theme": "dark"\n}\n'
    const writer = jsonWriter(home)
    const configPath = writer.kind === 'managed' ? writer.resolvePath(home) : ''
    writeConfig(configPath, initial)
    const before = readFileSync(configPath, 'utf8')

    const result = await runCapture('enable', {
      homeDir: home,
      writers: [writer],
      dryRun: true,
      fetchHealth: NO_LISTENER,
    })

    expect(result.exitCode).toBe(0)
    expect(result.stdout).toMatch(/dry-run/i)
    expect(result.stdout).toContain(configPath)
    expect(readFileSync(configPath, 'utf8')).toBe(before)
    expect(existsSync(join(home, '.kyberdash', 'capture-receipt.json'))).toBe(false)
  })
})

describe('capture enable and disable', () => {
  it('enable writes and records the receipt, and a second enable is a no-op', async () => {
    const home = makeHome()
    const initial = '{\n  "theme": "dark"\n}\n'
    const writer = jsonWriter(home)
    if (writer.kind !== 'managed') throw new Error('test writer must be managed')
    const configPath = writer.resolvePath(home)
    writeConfig(configPath, initial)

    const first = await runCapture('enable', {
      homeDir: home,
      writers: [writer],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(first.exitCode).toBe(0)
    const written = readFileSync(configPath, 'utf8')
    expect(written).toContain('http://127.0.0.1:4318')
    expect(written).not.toBe(initial)

    const receiptPath = join(home, '.kyberdash', 'capture-receipt.json')
    expect(existsSync(receiptPath)).toBe(true)
    const receipt = JSON.parse(readFileSync(receiptPath, 'utf8')) as {
      files: Array<{
        path: string
        format: string
        keys: Record<string, { written: unknown; prior: { present: boolean; value?: unknown } }>
        preSha256: string
        postSha256: string
      }>
    }
    expect(receipt.files).toHaveLength(1)
    expect(receipt.files[0]?.path).toBe(configPath)
    expect(receipt.files[0]?.format).toBe('json')
    expect(receipt.files[0]?.keys['otel.endpoint']?.written).toBe('http://127.0.0.1:4318')
    expect(receipt.files[0]?.keys['otel.endpoint']?.prior).toEqual({ present: false })
    expect(receipt.files[0]?.preSha256).toMatch(/^[0-9a-f]{64}$/)
    expect(receipt.files[0]?.postSha256).toMatch(/^[0-9a-f]{64}$/)
    expect(receipt.files[0]?.preSha256).not.toBe(receipt.files[0]?.postSha256)

    const second = await runCapture('enable', {
      homeDir: home,
      writers: [writer],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(second.exitCode).toBe(0)
    expect(second.stdout).toMatch(/no-op/i)
    expect(readFileSync(configPath, 'utf8')).toBe(written)
    expect(readFileSync(receiptPath, 'utf8')).toBe(JSON.stringify(receipt, null, 2) + '\n')
  })

  it('disable on an untouched file restores it byte-for-byte', async () => {
    const home = makeHome()
    const initial = '{\n  "theme": "dark"\n}\n'
    const writer = tomlWriter(home)
    if (writer.kind !== 'managed') throw new Error('test writer must be managed')
    const configPath = writer.resolvePath(home)
    writeConfig(configPath, 'theme = "dark"\n')

    const enabled = await runCapture('enable', {
      homeDir: home,
      writers: [writer],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(enabled.exitCode).toBe(0)
    expect(readFileSync(configPath, 'utf8')).not.toBe(initial)

    const disabled = await runCapture('disable', {
      homeDir: home,
      writers: [writer],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(disabled.exitCode).toBe(0)
    expect(readFileSync(configPath, 'utf8')).toBe('theme = "dark"\n')
  })

  it('disable refuses when a written key drifted: file untouched, drift printed, non-zero exit', async () => {
    const home = makeHome()
    const writer = jsonWriter(home)
    if (writer.kind !== 'managed') throw new Error('test writer must be managed')
    const configPath = writer.resolvePath(home)
    writeConfig(configPath, '{\n  "theme": "dark"\n}\n')

    const enabled = await runCapture('enable', {
      homeDir: home,
      writers: [writer],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(enabled.exitCode).toBe(0)

    // A foreign edit after enable: the harness owner changed the endpoint.
    const drifted = readFileSync(configPath, 'utf8').replace(
      'http://127.0.0.1:4318',
      'http://192.0.2.9:4318',
    )
    writeFileSync(configPath, drifted)

    const disabled = await runCapture('disable', {
      homeDir: home,
      writers: [writer],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(disabled.exitCode).not.toBe(0)
    // The file is left untouched: no per-key restore, never a whole-file restore.
    expect(readFileSync(configPath, 'utf8')).toBe(drifted)
    const combined = `${disabled.stdout}\n${disabled.stderr}`
    expect(combined).toContain('otel.endpoint')
    expect(combined).toContain('http://127.0.0.1:4318')
    expect(combined).toContain('http://192.0.2.9:4318')
  })
})

describe('capture enable and disable on the Kilo plugin harness', () => {
  const kiloConfigPath = (home: string): string => join(home, '.config', 'kilo', 'kilo.jsonc')
  const pluginPath = (home: string): string => join(home, '.config', 'kilo', 'plugin', 'kyberdash-capture.js')
  const run = (action: 'enable' | 'disable', home: string, dryRun = false) =>
    runCapture(action, { homeDir: home, harnessIds: ['kilo'], fetchHealth: NO_LISTENER, dryRun })

  it('enable installs the plugin and never writes the key receipt', async () => {
    const home = makeHome()
    const result = await run('enable', home)
    expect(result.exitCode).toBe(0)
    expect(existsSync(pluginPath(home))).toBe(true)
    expect(existsSync(join(home, '.kyberdash', 'kilo-plugin-receipt.json'))).toBe(true)
    expect(existsSync(join(home, '.kyberdash', 'capture-receipt.json'))).toBe(false)
    expect(result.events).toMatchObject([{ kind: 'wrote', harness: 'kilo', fileId: 'plugin' }])
  })

  it('enable dry run writes nothing under HOME', async () => {
    const home = makeHome()
    const before = listTree(home)
    const result = await run('enable', home, true)
    expect(result.exitCode).toBe(0)
    expect(listTree(home)).toEqual(before)
    expect(result.events).toMatchObject([{ kind: 'would-write' }])
  })

  it('disable removes what enable installed and leaves captured data', async () => {
    const home = makeHome()
    await run('enable', home)
    const store = join(home, '.kyberdash', 'capture', 'kilo')
    mkdirSync(store, { recursive: true })
    writeFileSync(join(store, 'index.jsonl'), '{}\n')
    const result = await run('disable', home)
    expect(result.exitCode).toBe(0)
    expect(existsSync(pluginPath(home))).toBe(false)
    expect(existsSync(join(store, 'index.jsonl'))).toBe(true)
  })

  it('disable on a machine that never enabled writes nothing', async () => {
    const home = makeHome()
    const before = listTree(home)
    const result = await run('disable', home)
    expect(result.exitCode).toBe(0)
    expect(listTree(home)).toEqual(before)
  })

  it.each(['enable', 'disable'] as const)('%s leaves an existing kilo.jsonc byte-for-byte unchanged', async (action) => {
    const home = makeHome()
    const configPath = kiloConfigPath(home)
    writeConfig(configPath, '{\n  // synthetic kilo fixture\n  "model": "synthetic"\n}\n')
    const before = readFileSync(configPath, 'utf8')
    const result = await run(action, home)
    expect(result.exitCode).toBe(0)
    expect(readFileSync(configPath, 'utf8')).toBe(before)
  })

  it.each(['enable', 'disable'] as const)(
    '%s works for kilo alone when the shared key receipt is corrupt, because the plugin keeps its own',
    async (action) => {
      const home = makeHome()
      mkdirSync(join(home, '.kyberdash'), { recursive: true })
      writeFileSync(join(home, '.kyberdash', 'capture-receipt.json'), '{ not json')
      if (action === 'disable') await run('enable', home)
      const result = await run(action, home)
      expect(result.exitCode).toBe(0)
      expect(readFileSync(join(home, '.kyberdash', 'capture-receipt.json'), 'utf8')).toBe('{ not json')
    },
  )

  it('enable refuses to overwrite a plugin file the owner wrote', async () => {
    const home = makeHome()
    mkdirSync(join(home, '.config', 'kilo', 'plugin'), { recursive: true })
    writeFileSync(pluginPath(home), 'export default {}\n')
    const result = await run('enable', home)
    expect(result.exitCode).toBe(1)
    expect(result.stderr).toMatch(/not written by KyberDash/)
    expect(readFileSync(pluginPath(home), 'utf8')).toBe('export default {}\n')
  })
})

describe('capture status and receiver liveness', () => {
  it('reports per-harness state and tells no listener apart', async () => {
    const home = makeHome()
    const result = await runCapture('status', { homeDir: home, fetchHealth: NO_LISTENER })
    expect(result.exitCode).toBe(0)
    expect(result.stdout).toMatch(/receiver:\s*none/i)
    expect(result.stdout).toContain(home)
  })

  it('tells a foreign listener apart from the KyberDash receiver', async () => {
    const home = makeHome()
    const foreign = await runCapture('status', { homeDir: home, fetchHealth: FOREIGN_LISTENER })
    expect(foreign.stdout).toMatch(/receiver:\s*foreign/i)

    const own = await runCapture('status', { homeDir: home, fetchHealth: KYBERDASH_LISTENER })
    expect(own.stdout).toMatch(/receiver:\s*kyberdash/i)
  })

  it('names the remedy when the receiver is down and installs nothing', async () => {
    const home = makeHome()
    const result = await runCapture('enable', {
      homeDir: home,
      writers: [jsonWriter(home)],
      fetchHealth: NO_LISTENER,
    })
    expect(result.exitCode).toBe(0)
    const combined = `${result.stdout}\n${result.stderr}`
    expect(combined).toContain('host receiver')
    expect(combined).toContain('launch at login')
    expect(combined).toContain('kyberdash kyber otel')
    // D9: the command never installs a process manager.
    expect(existsSync(join(home, 'Library', 'LaunchAgents'))).toBe(false)
  })

  it('reports concrete registry status instead of a "not yet supported" stub', async () => {
    const home = makeHome()
    const result = await runCapture('status', { homeDir: home, fetchHealth: NO_LISTENER })
    expect(result.exitCode).toBe(0)
    expect(result.stdout).not.toMatch(/not yet supported/i)
    expect(result.stdout).toContain('harness copilot (Copilot)')
    expect(result.stdout).toContain('harness claude-code (Claude Code)')
    expect(result.stdout).toContain('harness codex (Codex)')
    expect(result.stdout).toContain('harness opencode (OpenCode)')
    expect(result.stdout).toContain('harness pi (Pi)')
    expect(result.stdout).toContain('harness antigravity (Antigravity)')
    expect(result.stdout).toContain('harness kilo (Kilo Code)')
    expect(result.stdout).toContain('no [otel] table is present; coverage comes from session files')
    expect(result.stdout).toContain('statusline bridge')
    expect(result.stdout).toContain('declaredContextWindow')
  })
})

describe('capture usage errors and platform', () => {
  it('exits 2 for an unknown --harness id', async () => {
    const home = makeHome()
    const result = await runCapture('status', {
      homeDir: home,
      harnessIds: ['no-such-harness'],
      fetchHealth: NO_LISTENER,
    })
    expect(result.exitCode).toBe(2)
    expect(`${result.stdout}\n${result.stderr}`).toMatch(/unknown harness/i)
  })

  it('exits 2 when the endpoint is not OTLP/HTTP', async () => {
    const home = makeHome()
    const result = await runCapture('enable', {
      homeDir: home,
      writers: [jsonWriter(home)],
      endpoint: 'grpc://127.0.0.1:4317',
      fetchHealth: NO_LISTENER,
    })
    expect(result.exitCode).toBe(2)
    expect(`${result.stdout}\n${result.stderr}`).toMatch(/otlp\/http/i)
  })

  it('reports status under an injected win32 platform instead of refusing Windows (D14)', async () => {
    const home = makeHome()
    // A temporary application-data directory keeps the win32 VS Code seam off
    // the real APPDATA root; status is read-only, so win32 paths are resolved
    // and existence-checked, never written, on any host.
    const appData = mkdtempSync(join(tmpdir(), 'kyber-capture-t8-appdata-'))
    temporaryHomes.push(appData)
    const result = await runCapture('status', {
      homeDir: home,
      appDataDir: appData,
      platform: 'win32',
      fetchHealth: NO_LISTENER,
    })
    expect(result.exitCode).toBe(0)
    expect(`${result.stdout}\n${result.stderr}`).not.toMatch(/unsupported/i)
    // The injected application-data directory is the VS Code settings root on
    // win32, so both D14 edition paths appear with win32 separators.
    expect(result.stdout).toContain(`${appData}\\Code\\User\\settings.json`)
    expect(result.stdout).toContain(`${appData}\\Code - Insiders\\User\\settings.json`)
  })
})
