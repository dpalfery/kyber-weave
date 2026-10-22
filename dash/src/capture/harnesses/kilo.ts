// Kilo Code capture writer.
//
// Kilo's OTLP stream cannot carry what KyberDash needs, so capture does not use it. Kilo
// (an OpenCode fork) loads plugins from ~/.config/kilo/plugin/, and its plugin hooks see the
// assembled system prompt, each tool definition and the model of every call. `enable`
// installs one plugin file that records those to ~/.kyberdash/capture/kilo; `disable`
// removes it. Usage and message content still come from kilo.db through folder import.
//
// The plugin file owns itself (digest receipt in kilo-plugin/install.ts), so this writer is
// not a key writer: it stays out of the Settings route's writable set, and `status` never
// echoes text the owner wrote in kilo.jsonc.

import { describeKiloPluginStatus, installKiloPlugin, kiloPluginPaths, uninstallKiloPlugin } from '../kilo-plugin/install.js'
import { envJoin, type PathEnv } from '../paths.js'

import type { PendingHarnessWriter } from './registry.js'

/** Fixed facts plus the plugin's own state. Nothing in the owner's config is read. */
function describeKiloStatus(home: string): readonly string[] {
  return [
    ...describeKiloPluginStatus(home),
    'captures: system prompt, tool definitions and skills catalog, from the capture plugin',
    'usage source: Kilo usage and message content are collected from kilo.db through folder import',
  ]
}

export const kiloHarness: PendingHarnessWriter = {
  kind: 'pending',
  id: 'kilo',
  displayName: 'Kilo Code',
  reason: 'file plugin: `enable` installs a capture plugin into the Kilo plugin directory',
  // D14: resolves on win32 through the same segments so `status` reports
  // instead of throwing.
  resolvePath: (home: string, env?: PathEnv) =>
    envJoin(env, home, '.config', 'kilo', 'kilo.jsonc'),
  format: 'jsonc',
  describeStatus: describeKiloStatus,
  filePlugin: {
    install: (home, env, options) => installKiloPlugin(home, env, options),
    uninstall: (home, env, options) => uninstallKiloPlugin(home, env, options),
    path: (home, env) => kiloPluginPaths(home, env).pluginFile,
  },
}
