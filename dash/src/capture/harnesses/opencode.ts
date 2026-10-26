// OpenCode capture writer (P2.4a, T6 ownership).
//
// AI-SDK spans need `experimental.openTelemetry: true` in
// ~/.config/opencode/opencode.jsonc (JSONC). The OTLP endpoint is not a
// config key: OpenCode reads `OTEL_EXPORTER_OTLP_ENDPOINT` from the process
// environment, so status reports that variable and enable never writes it
// or a shell rc. Status also notes upstream #13438 and the non-model span
// volume; those are observations, not keys the receipt restores.
//
// T6 owns the dash-CLI-managed plugin contract here beside the flag: the
// plugin id and its `plugins` config keys are DECLARED (re-exported) so
// capture owns the contract, but they are never written — local file
// discovery installs the plugin by file, and `desiredKeys` stays the single
// OTel flag. The endpoint likewise stays process-env-only.

import { envJoin, type PathEnv } from '../paths.js'

import {
  describeOpencodePluginStatus,
  installOpencodePlugin,
  opencodePluginPaths,
  opencodePluginState,
  setOpencodeContentConsent,
  uninstallOpencodePlugin,
  type OpencodePluginResult,
} from './opencode-plugin/install.js'
import type { ManagedFilePlugin, ManagedHarnessWriter } from './registry.js'

/** Strict mode only ever kept-newer/noop/refused/wrote; fold the preserve-only outcomes away. */
function narrow(result: OpencodePluginResult) {
  const outcome = result.outcome === 'kept' || result.outcome === 'skipped' ? 'noop' : result.outcome
  return {
    exitCode: result.exitCode,
    outcome,
    ...(result.reason !== undefined ? { reason: result.reason } : {}),
    out: result.out,
    err: result.err,
  } as const
}

const opencodeFilePlugin: ManagedFilePlugin = {
  install: (home, env, options) => narrow(installOpencodePlugin(home, env, { mode: 'strict', dryRun: options.dryRun })),
  uninstall: (home, env, options) => narrow(uninstallOpencodePlugin(home, env, { dryRun: options.dryRun })),
  path: (home, env) => opencodePluginPaths(home, env).pluginFile,
  state: (home, env) => opencodePluginState(home, env),
  describeStatus: (home, env) => describeOpencodePluginStatus(home, env),
  consent: {
    set: (home, env, content, options) => setOpencodeContentConsent(home, content, { dryRun: options.dryRun, env }),
  },
}

/**
 * The plugin capture contract, declared here and never written (T6
 * declare-only). The dash CLI installs the plugin file into opencode's own
 * plugin directories (see `resolveOpencodePluginPaths`); capture manages
 * only the OTel flag below. Re-exported so the writer module is the single
 * ownership point for the plugin's capture keys.
 */
export { OPENCODE_PLUGIN_CONFIG_KEYS, OPENCODE_PLUGIN_ID } from './opencode-plugin/plugin.js'

/** The only key enable writes. The value stays a JSON boolean. */
const OPENCODE_OTEL_FLAG_KEY = 'experimental.openTelemetry'

/**
 * Reported on status from this process's environment. Omitting it from
 * `desiredKeys` is what keeps enable from writing it into opencode.jsonc.
 */
const STATUS_ENV_KEY = 'OTEL_EXPORTER_OTLP_ENDPOINT'

/**
 * D5 consent (T3): sender-owned, off by default. Content (system prompt,
 * tool definitions, skills listing) rides only when this is `1`.
 * Declare-only: reported on status like the endpoint above, never written
 * or receipted by enable — the `OTEL_EXPORTER_OTLP_ENDPOINT` pattern.
 */
const CONTEXT_CONSENT_ENV_KEY = 'KYBERDASH_OPENCODE_CONTEXT'

export const opencodeHarness: ManagedHarnessWriter = {
  kind: 'managed',
  id: 'opencode',
  displayName: 'OpenCode',
  format: 'jsonc',
  // D14: win32 keeps the .config root — the seam changes the separators,
  // not the root, because OpenCode reads this location on every OS.
  resolvePath: (home: string, env?: PathEnv) =>
    envJoin(env, home, '.config', 'opencode', 'opencode.jsonc'),
  // The endpoint argument is the receiver origin. OpenCode does not store it
  // in opencode.jsonc, so the declaration ignores it.
  desiredKeys: () => ({
    [OPENCODE_OTEL_FLAG_KEY]: true,
  }),
  filePlugin: opencodeFilePlugin,
  statusEnvSnippet: {
    label:
      'process env (upstream #13438; non-model spans are emitted with model spans and are not written here)',
    envKeys: [STATUS_ENV_KEY, CONTEXT_CONSENT_ENV_KEY],
  },
}
