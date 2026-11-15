import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import {
  describeKiloPluginStatus,
  installKiloPlugin,
  kiloPluginPaths,
  uninstallKiloPlugin,
} from './install.js'
import { KILO_PLUGIN_SOURCE } from './source.js'

const homes: string[] = []
afterEach(() => {
  for (const home of homes.splice(0)) rmSync(home, { recursive: true, force: true })
})
function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kilo-install-'))
  homes.push(home)
  return home
}

describe('kiloPluginPaths', () => {
  it('puts the plugin in Kilo plugin directory and the store under .kyberdash', () => {
    const paths = kiloPluginPaths('/home/me', { platform: 'linux' })
    expect(paths.pluginFile).toBe('/home/me/.config/kilo/plugin/kyberdash-capture.js')
    expect(paths.storeDir).toBe('/home/me/.kyberdash/capture/kilo')
  })
})

describe('installKiloPlugin', () => {
  it('writes the plugin and a receipt holding its digest', () => {
    const home = makeHome()
    const result = installKiloPlugin(home)
    const { pluginFile, receiptFile } = kiloPluginPaths(home)
    expect(result.exitCode).toBe(0)
    expect(readFileSync(pluginFile, 'utf8')).toBe(KILO_PLUGIN_SOURCE)
    expect(JSON.parse(readFileSync(receiptFile, 'utf8'))).toMatchObject({ version: 1, path: pluginFile })
  })

  it('reports and writes nothing on a dry run', () => {
    const home = makeHome()
    const result = installKiloPlugin(home, undefined, { dryRun: true })
    expect(result.exitCode).toBe(0)
    expect(result.out.join('\n')).toMatch(/would install/i)
    expect(existsSync(kiloPluginPaths(home).pluginFile)).toBe(false)
    expect(existsSync(kiloPluginPaths(home).receiptFile)).toBe(false)
  })

  it('is a no-op when the same plugin is already installed', () => {
    const home = makeHome()
    installKiloPlugin(home)
    const again = installKiloPlugin(home)
    expect(again.exitCode).toBe(0)
    expect(again.out.join('\n')).toMatch(/already installed/i)
  })

  it('upgrades its own older plugin, recognised by the marker', () => {
    const home = makeHome()
    const { pluginFile } = kiloPluginPaths(home)
    mkdirSync(join(pluginFile, '..'), { recursive: true })
    writeFileSync(pluginFile, '// kyberdash-capture-plugin v0\nold body\n')
    const result = installKiloPlugin(home)
    expect(result.exitCode).toBe(0)
    expect(readFileSync(pluginFile, 'utf8')).toBe(KILO_PLUGIN_SOURCE)
  })

  it('refuses to overwrite a file it does not own', () => {
    const home = makeHome()
    const { pluginFile } = kiloPluginPaths(home)
    mkdirSync(join(pluginFile, '..'), { recursive: true })
    writeFileSync(pluginFile, 'export default {}\n')
    const result = installKiloPlugin(home)
    expect(result.exitCode).toBe(1)
    expect(result.err.join('\n')).toMatch(/not written by kyberdash/i)
    expect(readFileSync(pluginFile, 'utf8')).toBe('export default {}\n')
  })

  it('creates the plugin directory private to nothing else: it does not touch kilo.jsonc', () => {
    const home = makeHome()
    const config = join(home, '.config', 'kilo')
    mkdirSync(config, { recursive: true })
    writeFileSync(join(config, 'kilo.jsonc'), '{ "owner": "text" }')
    installKiloPlugin(home)
    expect(readFileSync(join(config, 'kilo.jsonc'), 'utf8')).toBe('{ "owner": "text" }')
  })
})

describe('an unreadable plugin path', () => {
  it('install refuses with reason unreadable instead of throwing', () => {
    const home = makeHome()
    mkdirSync(kiloPluginPaths(home).pluginFile, { recursive: true })
    const result = installKiloPlugin(home)
    expect(result).toMatchObject({ exitCode: 1, outcome: 'refused', reason: 'unreadable' })
  })

  it('uninstall refuses with reason unreadable instead of throwing', () => {
    const home = makeHome()
    mkdirSync(kiloPluginPaths(home).pluginFile, { recursive: true })
    const result = uninstallKiloPlugin(home)
    expect(result).toMatchObject({ exitCode: 1, outcome: 'refused', reason: 'unreadable' })
  })
})

describe('uninstallKiloPlugin', () => {
  it('removes the plugin and the receipt but keeps captured data', () => {
    const home = makeHome()
    installKiloPlugin(home)
    const { pluginFile, receiptFile, storeDir } = kiloPluginPaths(home)
    mkdirSync(storeDir, { recursive: true })
    writeFileSync(join(storeDir, 'index.jsonl'), '{}\n')
    const result = uninstallKiloPlugin(home)
    expect(result.exitCode).toBe(0)
    expect(existsSync(pluginFile)).toBe(false)
    expect(existsSync(receiptFile)).toBe(false)
    expect(existsSync(join(storeDir, 'index.jsonl'))).toBe(true)
  })

  it('leaves a plugin the owner edited and says so', () => {
    const home = makeHome()
    installKiloPlugin(home)
    const { pluginFile } = kiloPluginPaths(home)
    writeFileSync(pluginFile, `${KILO_PLUGIN_SOURCE}// owner edit\n`)
    const result = uninstallKiloPlugin(home)
    expect(result.exitCode).toBe(1)
    expect(result.err.join('\n')).toMatch(/changed since install/i)
    expect(existsSync(pluginFile)).toBe(true)
  })

  it('is a quiet no-op when nothing was installed', () => {
    const home = makeHome()
    const result = uninstallKiloPlugin(home)
    expect(result.exitCode).toBe(0)
    expect(result.out.join('\n')).toMatch(/not installed/i)
  })

  it('reports what it would remove on a dry run and removes nothing', () => {
    const home = makeHome()
    installKiloPlugin(home)
    const result = uninstallKiloPlugin(home, undefined, { dryRun: true })
    expect(result.out.join('\n')).toMatch(/would remove/i)
    expect(existsSync(kiloPluginPaths(home).pluginFile)).toBe(true)
  })
})

describe('describeKiloPluginStatus', () => {
  it('says missing before install', () => {
    const lines = describeKiloPluginStatus(makeHome()).join('\n')
    expect(lines).toMatch(/plugin: not installed/)
  })

  it('says installed with no events until Kilo has made a call', () => {
    const home = makeHome()
    installKiloPlugin(home)
    const lines = describeKiloPluginStatus(home).join('\n')
    expect(lines).toMatch(/plugin: installed/)
    expect(lines).toMatch(/captured calls: none yet/)
  })

  it('counts captured calls and sessions from the store', () => {
    const home = makeHome()
    installKiloPlugin(home)
    const { storeDir } = kiloPluginPaths(home)
    mkdirSync(storeDir, { recursive: true })
    writeFileSync(
      join(storeDir, 'index.jsonl'),
      [
        { ts: 1000, sessionID: 'a' },
        { ts: 2000, sessionID: 'a' },
        { ts: 3000, sessionID: 'b' },
      ]
        .map((row) => JSON.stringify(row))
        .join('\n') + '\n',
    )
    const lines = describeKiloPluginStatus(home).join('\n')
    expect(lines).toMatch(/captured calls: 3 across 2 sessions/)
    expect(lines).toMatch(/last call: 1970-01-01T00:00:03/)
  })

  it('flags an edited plugin as modified without echoing its text', () => {
    const home = makeHome()
    installKiloPlugin(home)
    writeFileSync(kiloPluginPaths(home).pluginFile, 'secret owner text')
    const lines = describeKiloPluginStatus(home).join('\n')
    expect(lines).toMatch(/modified/)
    expect(lines).not.toMatch(/secret owner text/)
  })

  it('ignores an index line with a non-finite timestamp instead of crashing', () => {
    const home = makeHome()
    installKiloPlugin(home)
    const { storeDir } = kiloPluginPaths(home)
    mkdirSync(storeDir, { recursive: true })
    writeFileSync(
      join(storeDir, 'index.jsonl'),
      '{"ts":1e999,"sessionID":"a"}\n{"ts":2000,"sessionID":"b"}\n',
    )
    const lines = describeKiloPluginStatus(home).join('\n')
    expect(lines).toMatch(/captured calls: 1 across 1 sessions/)
    expect(lines).toMatch(/last call: 1970-01-01T00:00:02/)
  })

  it('reports unreadable, without throwing, when the plugin path is not a readable file', () => {
    const home = makeHome()
    mkdirSync(kiloPluginPaths(home).pluginFile, { recursive: true })
    const lines = describeKiloPluginStatus(home).join('\n')
    expect(lines).toMatch(/plugin: unreadable/)
  })

  it('reports an unreadable index as unreadable, not as no calls', () => {
    const home = makeHome()
    installKiloPlugin(home)
    mkdirSync(join(kiloPluginPaths(home).storeDir, 'index.jsonl'), { recursive: true })
    const lines = describeKiloPluginStatus(home).join('\n')
    expect(lines).toMatch(/captured calls: unreadable/)
    expect(lines).not.toMatch(/none yet/)
  })

  it('never throws on a corrupt index', () => {
    const home = makeHome()
    installKiloPlugin(home)
    const { storeDir } = kiloPluginPaths(home)
    mkdirSync(storeDir, { recursive: true })
    writeFileSync(join(storeDir, 'index.jsonl'), 'not json\n{bad')
    expect(() => describeKiloPluginStatus(home)).not.toThrow()
    expect(statSync(storeDir).isDirectory()).toBe(true)
  })
})
