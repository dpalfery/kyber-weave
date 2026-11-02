// Factory (Droid) capture writer: plugin-only.
//
// Factory reaches KyberDash through a capture plugin DIRECTORY in its own root
// (`<factory root>/plugins/kyberdash-factory`), not through a configuration key. The row
// therefore declares no key at all: `enable` installs the plugin tree and turns the capture
// consent on, `disable` turns the consent off and removes the tree. The owner's own
// `~/.factory/settings.json` is never written, merged or receipted — `resolvePath` only
// names where Factory keeps its settings, which is where the harness is found.
//
// The Factory root is `$FACTORY_DIR`, else `~/.factory`: the resolution the Factory file
// provider already uses (`dash/src/providers/droid.ts`) and the one the installer uses.

import { writeContentConsent } from '../consent-file.js'
import { envJoin, type PathEnv } from '../paths.js'

import {
  describeFactoryPluginStatus,
  factoryPluginPaths,
  factoryPluginState,
  installFactoryPlugin,
  uninstallFactoryPlugin,
  type FactoryPluginResult,
} from './factory-plugin/install.js'
import type { ManagedFilePlugin, ManagedHarnessWriter } from './registry.js'

const FACTORY_HARNESS_ID = 'factory'

/**
 * The engine knows one vocabulary for a plugin outcome; the installer's preserve-only
 * outcomes (a kept tree, a skipped install) are folded into it.
 */
function narrow(result: FactoryPluginResult) {
  const outcome = result.outcome === 'kept' || result.outcome === 'skipped' ? 'noop' : result.outcome
  return {
    exitCode: result.exitCode,
    outcome,
    ...(result.reason !== undefined ? { reason: result.reason } : {}),
    out: result.out,
    err: result.err,
  } as const
}

/**
 * Where Factory keeps its own settings. Named, never written: KyberDash installs a plugin,
 * not a key, so this path is the harness's place and nothing more.
 */
function factorySettingsPath(home: string, env?: PathEnv): string {
  return envJoin(env, factoryPluginPaths(home, env).root, 'settings.json')
}

/**
 * The status lines for the Factory row: the plugin state, the directory the plugin
 * resolves to, the receipt and the content consent. The consent lines come from the
 * installer, which owns the consent file's shape; the directory and the receipt are added
 * here because the Factory plugin is a TREE and the row has to say where it lives.
 */
function factoryStatusLines(home: string, env: PathEnv | undefined): string[] {
  const paths = factoryPluginPaths(home, env)
  const state = factoryPluginState(home, env)
  const described = describeFactoryPluginStatus(home, env)
  const plugin = described[0] ?? 'plugin: unknown'
  return [
    plugin,
    `plugin directory: ${paths.pluginDir}`,
    `plugin receipt: ${state.receipt ? 'recorded' : 'absent'}`,
    ...described.slice(1),
  ]
}

const factoryFilePlugin: ManagedFilePlugin = {
  install: (home, env, options) =>
    narrow(installFactoryPlugin(home, env, { mode: 'strict', dryRun: options.dryRun })),
  uninstall: (home, env, options) =>
    narrow(uninstallFactoryPlugin(home, env, { dryRun: options.dryRun })),
  path: (home, env) => factoryPluginPaths(home, env).pluginDir,
  state: (home, env) => factoryPluginState(home, env),
  describeStatus: (home, env) => factoryStatusLines(home, env),
  consent: {
    // The Settings switch is the consent: on for `enable`, off for `disable`. The installer
    // creates the file with content off; this is the only writer that turns it on.
    set: (home, env, content, options) => {
      const { consentFile } = factoryPluginPaths(home, env)
      const word = content ? 'on' : 'off'
      if (options.dryRun === true) return { out: [`harness factory: would set content consent ${word}`] }
      writeContentConsent(consentFile, content)
      return { out: [`harness factory: content consent ${word}`] }
    },
  },
}

export const factoryHarness: ManagedHarnessWriter = {
  kind: 'managed',
  id: FACTORY_HARNESS_ID,
  displayName: 'Factory (Droid)',
  format: 'json',
  resolvePath: factorySettingsPath,
  // Plugin-only: no config key is declared, so the engine reads, writes and receipts none.
  desiredKeys: () => ({}),
  filePlugin: factoryFilePlugin,
}
