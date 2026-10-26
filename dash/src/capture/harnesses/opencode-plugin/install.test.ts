import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { sha256Hex } from '../../receipt.js'
import { readConsentState } from './consent-write.js'
import {
  describeOpencodePluginStatus,
  installOpencodePlugin,
  opencodePluginPaths,
  opencodePluginState,
  setOpencodeContentConsent,
  uninstallOpencodePlugin,
} from './install.js'
import { OPENCODE_PLUGIN_MARKER, OPENCODE_PLUGIN_SOURCE } from './generated/plugin-source.js'

const dirs: string[] = []
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})
function temp(prefix: string): string {
  const dir = mkdtempSync(join(tmpdir(), prefix))
  dirs.push(dir)
  return dir
}
/** No OPENCODE_CONFIG_DIR unless a test injects one: tests never read the real process env. */
const NO_ENV: NodeJS.ProcessEnv = {}

describe('opencodePluginPaths', () => {
  it('defaults to ~/.config/opencode/plugins and keeps receipt and consent under ~/.kyberdash', () => {
    const paths = opencodePluginPaths('/home/me', { platform: 'linux' }, { processEnv: NO_ENV })
    expect(paths.pluginFile).toBe('/home/me/.config/opencode/plugins/kyberdash-opencode.js')
    expect(paths.receiptFile).toBe('/home/me/.kyberdash/opencode-plugin-receipt.json')
    expect(paths.consentFile).toBe('/home/me/.kyberdash/opencode-plugin.json')
  })

  it('prefers --config-dir over $OPENCODE_CONFIG_DIR over the default', () => {
    const env = { OPENCODE_CONFIG_DIR: '/env/oc' }
    const linux = { platform: 'linux' } as const
    expect(opencodePluginPaths('/h', linux, { processEnv: env }).pluginFile).toBe('/env/oc/plugins/kyberdash-opencode.js')
    expect(opencodePluginPaths('/h', linux, { processEnv: env, configDir: '/flag/oc' }).pluginFile).toBe(
      '/flag/oc/plugins/kyberdash-opencode.js',
    )
    expect(opencodePluginPaths('/h', linux, { processEnv: { OPENCODE_CONFIG_DIR: '' } }).pluginFile).toBe(
      '/h/.config/opencode/plugins/kyberdash-opencode.js',
    )
  })
})

describe('installOpencodePlugin: preserve mode', () => {
  const preserve = (home: string, extra: Record<string, unknown> = {}) =>
    installOpencodePlugin(home, undefined, { mode: 'preserve', processEnv: NO_ENV, ...extra })

  it('writes the plugin, a receipt holding its digest, and a consent file with content off', () => {
    const home = temp('oc-install-')
    const result = preserve(home)
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('installed')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe(OPENCODE_PLUGIN_SOURCE)
    expect(JSON.parse(readFileSync(paths.receiptFile, 'utf8'))).toEqual({
      version: 1,
      path: paths.pluginFile,
      sha256: sha256Hex(OPENCODE_PLUGIN_SOURCE),
    })
    expect(JSON.parse(readFileSync(paths.consentFile, 'utf8'))).toEqual({ version: 1, content: false })
    expect(result.consent).toBe('created')
  })

  it('never rewrites an existing consent file, whichever value it holds', () => {
    for (const body of ['{"version":1,"content":true}\n', '{"version":1,"content":false}', 'not json at all']) {
      const home = temp('oc-consent-')
      const { consentFile } = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
      mkdirSync(join(home, '.kyberdash'), { recursive: true })
      writeFileSync(consentFile, body)
      const result = preserve(home)
      expect(result.consent).toBe('kept')
      expect(readFileSync(consentFile, 'utf8')).toBe(body)
    }
  })

  it('is a noop when the same plugin is installed', () => {
    const home = temp('oc-noop-')
    preserve(home)
    const again = preserve(home)
    expect(again.exitCode).toBe(0)
    expect(again.disposition).toBe('unchanged')
    expect(again.outcome).toBe('noop')
  })

  it('upgrades our unmodified older plugin', () => {
    const home = temp('oc-upgrade-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    const older = `// kyberdash-opencode-plugin v0\nexport default {}\n`
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, older)
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    writeFileSync(paths.receiptFile, JSON.stringify({ version: 1, path: paths.pluginFile, sha256: sha256Hex(older) }))
    const result = preserve(home)
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('upgraded')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe(OPENCODE_PLUGIN_SOURCE)
    expect(result.out.join('\n')).toMatch(/restart OpenCode/i)
  })

  it('keeps a plugin of ours that the owner edited, warns, and exits 0', () => {
    const home = temp('oc-edited-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    const original = `// kyberdash-opencode-plugin v0\nexport default {}\n`
    const edited = `${original}// my tweak\n`
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, edited)
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    writeFileSync(paths.receiptFile, JSON.stringify({ version: 1, path: paths.pluginFile, sha256: sha256Hex(original) }))
    const result = preserve(home)
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('kept-edited')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe(edited)
    expect(result.err.join('\n')).toMatch(/edited/i)
  })

  it('keeps an older marker file when there is no receipt (origin unknown), warns, exits 0', () => {
    const home = temp('oc-norcpt-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    const unknown = `// kyberdash-opencode-plugin v0\nexport default {}\n// owner tweak\n`
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, unknown)
    const result = preserve(home)
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('kept-edited')
    expect(result.outcome).toBe('kept')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe(unknown)
    expect(existsSync(paths.receiptFile)).toBe(false)
    expect(result.err.join('\n')).toMatch(/no receipt/i)
  })

  it('keeps an older marker file when the receipt is for another path', () => {
    const home = temp('oc-otherrcpt-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    const older = `// kyberdash-opencode-plugin v0\nexport default {}\n`
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, older)
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    const receipt = JSON.stringify({ version: 1, path: join(home, 'elsewhere', 'kyberdash-opencode.js'), sha256: sha256Hex(older) })
    writeFileSync(paths.receiptFile, receipt)
    const result = preserve(home)
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('kept-edited')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe(older)
    expect(readFileSync(paths.receiptFile, 'utf8')).toBe(receipt)
  })

  it('strict mode still upgrades an older marker file with no receipt', () => {
    const home = temp('oc-strict-norcpt-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, '// kyberdash-opencode-plugin v0\n')
    const result = installOpencodePlugin(home, undefined, { mode: 'strict', processEnv: NO_ENV })
    expect(result.disposition).toBe('upgraded')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe(OPENCODE_PLUGIN_SOURCE)
  })

  it('keeps a plugin that is not ours, warns, and exits 0 without touching consent', () => {
    const home = temp('oc-foreign-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, 'export default { id: "someone-else" }\n')
    const result = preserve(home)
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('kept-foreign')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe('export default { id: "someone-else" }\n')
    expect(existsSync(paths.receiptFile)).toBe(false)
    expect(existsSync(paths.consentFile)).toBe(false)
    expect(result.err.join('\n')).toMatch(/not written by KyberDash/i)
  })

  it('keeps a newer plugin of ours instead of downgrading it', () => {
    const home = temp('oc-newer-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, '// kyberdash-opencode-plugin v999\nexport default {}\n')
    const result = preserve(home)
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('kept-newer')
    expect(readFileSync(paths.pluginFile, 'utf8')).toContain('v999')
  })

  it('treats a path that is not a regular file as kept-unreadable with exit 0', () => {
    const home = temp('oc-dir-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(paths.pluginFile, { recursive: true })
    const result = preserve(home)
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('kept-unreadable')
  })

  it('never touches opencode.jsonc', () => {
    const home = temp('oc-jsonc-')
    const config = join(home, '.config', 'opencode', 'opencode.jsonc')
    mkdirSync(join(config, '..'), { recursive: true })
    writeFileSync(config, '{\n  // mine\n  "model": "x"\n}\n')
    preserve(home)
    expect(readFileSync(config, 'utf8')).toBe('{\n  // mine\n  "model": "x"\n}\n')
  })

  it('--if-installed with nothing installed writes nothing at all', () => {
    const home = temp('oc-ifinst-')
    const result = preserve(home, { ifInstalled: true })
    expect(result.exitCode).toBe(0)
    expect(result.disposition).toBe('skipped')
    expect(existsSync(join(home, '.kyberdash'))).toBe(false)
    expect(existsSync(join(home, '.config'))).toBe(false)
  })

  it('--if-installed upgrades an installed older plugin', () => {
    const home = temp('oc-ifinst2-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    const older = '// kyberdash-opencode-plugin v0\n'
    writeFileSync(paths.pluginFile, older)
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    writeFileSync(paths.receiptFile, JSON.stringify({ version: 1, path: paths.pluginFile, sha256: sha256Hex(older) }))
    expect(preserve(home, { ifInstalled: true }).disposition).toBe('upgraded')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe(OPENCODE_PLUGIN_SOURCE)
  })

  it('--config-dir selects the root', () => {
    const home = temp('oc-cfgdir-')
    const root = temp('oc-root-')
    const result = preserve(home, { configDir: root })
    expect(result.exitCode).toBe(0)
    expect(readFileSync(join(root, 'plugins', 'kyberdash-opencode.js'), 'utf8')).toBe(OPENCODE_PLUGIN_SOURCE)
    expect(existsSync(join(home, '.config'))).toBe(false)
  })

  it('$OPENCODE_CONFIG_DIR selects the root when no --config-dir is given', () => {
    const home = temp('oc-envdir-')
    const root = temp('oc-root2-')
    preserve(home, { processEnv: { OPENCODE_CONFIG_DIR: root } })
    expect(existsSync(join(root, 'plugins', 'kyberdash-opencode.js'))).toBe(true)
  })

  it('--dry-run writes nothing and says what it would do', () => {
    const home = temp('oc-dry-')
    const result = preserve(home, { dryRun: true })
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('would-write')
    expect(result.out.join('\n')).toMatch(/would/i)
    expect(existsSync(join(home, '.kyberdash'))).toBe(false)
    expect(existsSync(join(home, '.config'))).toBe(false)
  })
})

describe('installOpencodePlugin: strict mode', () => {
  const strict = (home: string, extra: Record<string, unknown> = {}) =>
    installOpencodePlugin(home, undefined, { mode: 'strict', processEnv: NO_ENV, ...extra })

  it('installs the plugin and receipt but leaves the consent file to the caller', () => {
    const home = temp('oc-strict-')
    const result = strict(home)
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('wrote')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe(OPENCODE_PLUGIN_SOURCE)
    expect(existsSync(paths.consentFile)).toBe(false)
  })

  it('refuses a plugin that is not ours with exit 1 (conflict)', () => {
    const home = temp('oc-strict-foreign-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, 'export default {}\n')
    const result = strict(home)
    expect(result.exitCode).toBe(1)
    expect(result.reason).toBe('conflict')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe('export default {}\n')
  })

  it('refuses our plugin after an owner edit with exit 1 (drift)', () => {
    const home = temp('oc-strict-drift-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    const original = '// kyberdash-opencode-plugin v0\n'
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, `${original}// edit\n`)
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    writeFileSync(paths.receiptFile, JSON.stringify({ version: 1, path: paths.pluginFile, sha256: sha256Hex(original) }))
    const result = strict(home)
    expect(result.exitCode).toBe(1)
    expect(result.reason).toBe('drift')
    expect(readFileSync(paths.pluginFile, 'utf8')).toBe(`${original}// edit\n`)
  })

  it('refuses a path that is not a regular file with exit 1 (unreadable)', () => {
    const home = temp('oc-strict-dir-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(paths.pluginFile, { recursive: true })
    const result = strict(home)
    expect(result.exitCode).toBe(1)
    expect(result.reason).toBe('unreadable')
  })

  it('upgrades our unmodified older plugin and is a noop for the current one', () => {
    const home = temp('oc-strict-up-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, '// kyberdash-opencode-plugin v0\n')
    expect(strict(home).outcome).toBe('wrote')
    expect(strict(home).outcome).toBe('noop')
  })

  it('writes nothing on a dry run', () => {
    const home = temp('oc-strict-dry-')
    const result = strict(home, { dryRun: true })
    expect(result.outcome).toBe('would-write')
    expect(existsSync(join(home, '.config'))).toBe(false)
    expect(existsSync(join(home, '.kyberdash'))).toBe(false)
  })
})

describe('uninstallOpencodePlugin', () => {
  it('removes the plugin and receipt while its digest still matches', () => {
    const home = temp('oc-rm-')
    installOpencodePlugin(home, undefined, { mode: 'strict', processEnv: NO_ENV })
    const result = uninstallOpencodePlugin(home, undefined, { processEnv: NO_ENV })
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('restored')
    expect(existsSync(paths.pluginFile)).toBe(false)
    expect(existsSync(paths.receiptFile)).toBe(false)
  })

  it('leaves an edited plugin in place and exits 1', () => {
    const home = temp('oc-rm-edit-')
    installOpencodePlugin(home, undefined, { mode: 'strict', processEnv: NO_ENV })
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    writeFileSync(paths.pluginFile, `${OPENCODE_PLUGIN_SOURCE}// edit\n`)
    const result = uninstallOpencodePlugin(home, undefined, { processEnv: NO_ENV })
    expect(result.exitCode).toBe(1)
    expect(result.reason).toBe('drift')
    expect(existsSync(paths.pluginFile)).toBe(true)
  })

  it('leaves a plugin that is not ours alone with exit 0', () => {
    const home = temp('oc-rm-foreign-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(join(paths.pluginFile, '..'), { recursive: true })
    writeFileSync(paths.pluginFile, 'export default {}\n')
    const result = uninstallOpencodePlugin(home, undefined, { processEnv: NO_ENV })
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('noop')
    expect(existsSync(paths.pluginFile)).toBe(true)
  })

  it('is a noop when nothing is installed, and a dry run removes nothing', () => {
    const home = temp('oc-rm-none-')
    expect(uninstallOpencodePlugin(home, undefined, { processEnv: NO_ENV }).outcome).toBe('noop')
    installOpencodePlugin(home, undefined, { mode: 'strict', processEnv: NO_ENV })
    const dry = uninstallOpencodePlugin(home, undefined, { processEnv: NO_ENV, dryRun: true })
    expect(dry.outcome).toBe('would-restore')
    expect(existsSync(opencodePluginPaths(home, undefined, { processEnv: NO_ENV }).pluginFile)).toBe(true)
  })
})

describe('setOpencodeContentConsent', () => {
  it('creates the file when absent and flips it both ways', () => {
    const home = temp('oc-set-')
    const { consentFile } = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    setOpencodeContentConsent(home, true)
    expect(JSON.parse(readFileSync(consentFile, 'utf8'))).toEqual({ version: 1, content: true })
    setOpencodeContentConsent(home, false)
    expect(JSON.parse(readFileSync(consentFile, 'utf8'))).toEqual({ version: 1, content: false })
  })

  it('replaces a malformed file and writes nothing on a dry run', () => {
    const home = temp('oc-set2-')
    const { consentFile } = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    writeFileSync(consentFile, 'garbage')
    setOpencodeContentConsent(home, true, { dryRun: true })
    expect(readFileSync(consentFile, 'utf8')).toBe('garbage')
    setOpencodeContentConsent(home, true)
    expect(readConsentState(consentFile)).toBe('on')
  })
})

describe('opencodePluginState and describeOpencodePluginStatus', () => {
  it('reports off / on / partial / unknown for the plugin file', () => {
    const home = temp('oc-state-')
    const paths = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    expect(opencodePluginState(home, undefined, { processEnv: NO_ENV }).state).toBe('off')
    installOpencodePlugin(home, undefined, { mode: 'strict', processEnv: NO_ENV })
    expect(opencodePluginState(home, undefined, { processEnv: NO_ENV })).toMatchObject({ state: 'on', exists: true, receipt: true })
    writeFileSync(paths.pluginFile, `${OPENCODE_PLUGIN_MARKER.replace(/v\d+$/, 'v0')}\n`)
    rmSync(paths.receiptFile)
    expect(opencodePluginState(home, undefined, { processEnv: NO_ENV }).state).toBe('partial')
    writeFileSync(paths.pluginFile, 'export default {}\n')
    expect(opencodePluginState(home, undefined, { processEnv: NO_ENV }).state).toBe('unknown')
  })

  it('states the consent in fixed text for absent, off and on, and never echoes file text', () => {
    const home = temp('oc-desc-')
    const { consentFile } = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    const lines = (): string => describeOpencodePluginStatus(home, undefined, { processEnv: NO_ENV }).join('\n')
    expect(lines()).toMatch(/plugin: not installed/)
    expect(lines()).toMatch(/content consent: not set/)
    setOpencodeContentConsent(home, false)
    expect(lines()).toMatch(/content consent: off/)
    setOpencodeContentConsent(home, true)
    expect(lines()).toMatch(/content consent: on/)
    writeFileSync(consentFile, 'SECRET-OWNER-TEXT')
    expect(lines()).toMatch(/content consent: unreadable or malformed/)
    expect(lines()).not.toContain('SECRET-OWNER-TEXT')
  })
})

describe('receipt fallback for status and uninstall (a custom-root install is not invisible)', () => {
  /** Installed into a custom root, as Squad does; the later caller has no flag and no env. */
  function installedInCustomRoot(prefix: string): { home: string; root: string; pluginFile: string } {
    const home = temp(prefix)
    const root = temp(`${prefix}root-`)
    installOpencodePlugin(home, undefined, { mode: 'preserve', configDir: root, processEnv: NO_ENV })
    return { home, root, pluginFile: join(root, 'plugins', 'kyberdash-opencode.js') }
  }

  it('opencodePluginState finds the plugin at the receipt path when the resolved root has none', () => {
    const { home } = installedInCustomRoot('oc-fb-state-')
    expect(opencodePluginState(home, undefined, { processEnv: NO_ENV })).toMatchObject({ state: 'on', exists: true, receipt: true })
    expect(describeOpencodePluginStatus(home, undefined, { processEnv: NO_ENV })[0]).toBe('plugin: installed')
  })

  it('uninstall removes the plugin recorded in the receipt and then the receipt', () => {
    const { home, pluginFile } = installedInCustomRoot('oc-fb-rm-')
    const result = uninstallOpencodePlugin(home, undefined, { processEnv: NO_ENV })
    expect(result.exitCode).toBe(0)
    expect(result.outcome).toBe('restored')
    expect(existsSync(pluginFile)).toBe(false)
    expect(existsSync(opencodePluginPaths(home, undefined, { processEnv: NO_ENV }).receiptFile)).toBe(false)
  })

  it('an explicit --config-dir keeps its own root and does not borrow the receipt path', () => {
    const { home, pluginFile } = installedInCustomRoot('oc-fb-flag-')
    const elsewhere = temp('oc-fb-else-')
    expect(opencodePluginState(home, undefined, { processEnv: NO_ENV, configDir: elsewhere }).state).toBe('off')
    expect(uninstallOpencodePlugin(home, undefined, { processEnv: NO_ENV, configDir: elsewhere }).outcome).toBe('noop')
    expect(existsSync(pluginFile)).toBe(true)
  })

  it('a plugin at the resolved root wins over the receipt path', () => {
    const { home } = installedInCustomRoot('oc-fb-win-')
    const envRoot = temp('oc-fb-env-')
    const env = { OPENCODE_CONFIG_DIR: envRoot }
    // An env root with no plugin falls back to the receipt path ...
    expect(opencodePluginState(home, undefined, { processEnv: env }).state).toBe('on')
    // ... but a file at the resolved root is what status reports.
    mkdirSync(join(envRoot, 'plugins'), { recursive: true })
    writeFileSync(join(envRoot, 'plugins', 'kyberdash-opencode.js'), 'export default {}\n')
    expect(opencodePluginState(home, undefined, { processEnv: env }).state).toBe('unknown')
  })

  it('ignores a receipt whose path is not a KyberDash plugin file name', () => {
    const home = temp('oc-fb-bad-')
    const victim = join(temp('oc-fb-victim-'), 'other.js')
    writeFileSync(victim, OPENCODE_PLUGIN_SOURCE)
    const { receiptFile } = opencodePluginPaths(home, undefined, { processEnv: NO_ENV })
    mkdirSync(join(receiptFile, '..'), { recursive: true })
    writeFileSync(receiptFile, JSON.stringify({ version: 1, path: victim, sha256: sha256Hex(OPENCODE_PLUGIN_SOURCE) }))
    expect(opencodePluginState(home, undefined, { processEnv: NO_ENV }).state).toBe('off')
    expect(uninstallOpencodePlugin(home, undefined, { processEnv: NO_ENV }).outcome).toBe('noop')
    expect(existsSync(victim)).toBe(true)
  })
})
