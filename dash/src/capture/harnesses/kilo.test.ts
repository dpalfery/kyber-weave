// Kilo Code capture writer: a file plugin. `enable` installs one plugin file into the Kilo
// plugin directory, `disable` removes it, and `status` reports its state without echoing
// text the owner wrote in kilo.jsonc.

import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { runCapture } from '../index.js'
import { kiloHarness } from './kilo.js'

const temporaryHomes: string[] = []

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-kilo-capture-'))
  temporaryHomes.push(home)
  return home
}

/** Kilo's config surface under the HOME capture was given. */
function kiloConfigDir(home: string): string {
  return join(home, '.config', 'kilo')
}

function writeKiloConfig(home: string, content: string): string {
  mkdirSync(kiloConfigDir(home), { recursive: true })
  const path = join(kiloConfigDir(home), 'kilo.jsonc')
  writeFileSync(path, content)
  return path
}

describe('kilo capture writer declaration', () => {
  it('is a pending file-plugin writer named Kilo Code', () => {
    expect(kiloHarness.kind).toBe('pending')
    expect(kiloHarness.id).toBe('kilo')
    expect(kiloHarness.displayName).toBe('Kilo Code')
  })

  it('resolves its config surface under the HOME it is given, on every platform', () => {
    // D14: the win32 path is a pure string computation, so a Windows status can be
    // reported from any host. Either the config directory or kilo.jsonc inside it
    // satisfies this: the decision pinned is that the path is Kilo's own and under HOME.
    expect(kiloHarness.resolvePath('/home/me', { platform: 'linux' }).startsWith('/home/me/.config/kilo')).toBe(true)
    const win32Path = kiloHarness.resolvePath('C:\\Users\\me', { platform: 'win32' })
    expect(win32Path.startsWith('C:\\Users\\me\\')).toBe(true)
    expect(win32Path).not.toContain('/')
    // A caller that never injects a path environment keeps the host's own join.
    expect(kiloHarness.resolvePath('/home/me').startsWith(join('/home/me'))).toBe(true)
  })
})

describe('kilo status text', () => {
  it('reads the plugin where the injected path environment installed it', async () => {
    const home = makeHome()
    const env = { platform: process.platform }
    kiloHarness.filePlugin!.install(home, env, { dryRun: false })
    expect(kiloHarness.describeStatus!(home, { env }).join('\n')).toMatch(/plugin: installed/)
  })

  it('states the plugin state, what it captures and where usage comes from, with no OTLP framing', async () => {
    const home = makeHome()
    writeKiloConfig(home, '{}\n')

    const result = await runCapture('status', {
      homeDir: home,
      harnessIds: ['kilo'],
      fetchHealth: KYBERDASH_LISTENER,
    })

    expect(result.exitCode).toBe(0)
    const text = `${result.stdout}\n${result.stderr}`
    expect(text).toContain('harness kilo (Kilo Code)')
    expect(text).not.toMatch(/not yet supported/i)
    expect(text).toMatch(/plugin: not installed/)
    expect(text).toMatch(/system prompt, tool definitions and skills/)
    expect(text).toMatch(/kilo\.db/)
    expect(text).toMatch(/folder import/i)
    expect(text).not.toMatch(/OTLP/)
  })

  it('reports the plugin as installed after enable', async () => {
    const home = makeHome()
    await runCapture('enable', { homeDir: home, harnessIds: ['kilo'], fetchHealth: KYBERDASH_LISTENER })
    const result = await runCapture('status', {
      homeDir: home,
      harnessIds: ['kilo'],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(result.stdout).toMatch(/plugin: installed/)
  })

  it('never echoes text from kilo.jsonc', async () => {
    const home = makeHome()
    const sentinel = 'synthetic-kilo-config-NEVER-ECHO-0123456789'
    writeKiloConfig(home, `{ "model": "${sentinel}" }\n`)

    const result = await runCapture('status', {
      homeDir: home,
      harnessIds: ['kilo'],
      fetchHealth: KYBERDASH_LISTENER,
    })

    expect(result.exitCode).toBe(0)
    const text = `${result.stdout}\n${result.stderr}`
    expect(text).not.toContain(sentinel)
  })
})
