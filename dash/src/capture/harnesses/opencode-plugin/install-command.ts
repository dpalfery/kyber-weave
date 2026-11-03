// `kyberdash kyber capture plugin install --harness opencode` (plan D4, D11, D13).
//
// Contract for the callers (Squad, `kyber-weave update`):
//   stdout  `opencode plugin: <token> <pluginFile>` first, then optionally
//           `opencode consent: <created|would-create|kept> <consentFile>`, then hints.
//           plugin tokens: installed | upgraded | unchanged | kept-edited | kept-newer |
//           kept-foreign | kept-unreadable | skipped-not-installed | would-install | would-upgrade
//   stderr  warnings (`kyberdash capture: warning: ...`) and strict refusals.
//   exit    0 success, including every preserve-mode keep and --if-installed skip;
//           1 strict refusal (foreign file, owner edit, unreadable path) or a failed write;
//           2 usage (missing or unsupported --harness).

import type { PathEnv } from '../../paths.js'
import { opencodePluginPaths, installOpencodePlugin, OPENCODE_CONSENT_REMINDER } from './install.js'
import { readConsentState } from './consent-write.js'

export const PLUGIN_INSTALL_USAGE_EXIT = 2

export type PluginInstallCommandOptions = {
  harness?: string
  preserve?: boolean
  ifInstalled?: boolean
  configDir?: string
  dryRun?: boolean
  homeDir: string
  env?: PathEnv
  /** Injectable; defaults to `process.env` (read only for `OPENCODE_CONFIG_DIR`). */
  processEnv?: NodeJS.ProcessEnv
}

export type PluginInstallCommandResult = { exitCode: number; stdout: string; stderr: string }

function text(lines: readonly string[]): string {
  return lines.length > 0 ? `${lines.join('\n')}\n` : ''
}

export function runPluginInstall(options: PluginInstallCommandOptions): PluginInstallCommandResult {
  if (options.harness !== 'opencode') {
    const given = options.harness === undefined ? 'missing' : `"${options.harness}" is not supported`
    return {
      exitCode: PLUGIN_INSTALL_USAGE_EXIT,
      stdout: '',
      stderr: `kyberdash capture plugin install: --harness ${given}. Expected: opencode.\n`,
    }
  }
  const pluginOptions = {
    mode: options.preserve === true ? ('preserve' as const) : ('strict' as const),
    dryRun: options.dryRun === true,
    ifInstalled: options.ifInstalled === true,
    ...(options.configDir !== undefined ? { configDir: options.configDir } : {}),
    ...(options.processEnv !== undefined ? { processEnv: options.processEnv } : {}),
  }
  let result
  try {
    result = installOpencodePlugin(options.homeDir, options.env, pluginOptions)
  } catch (error) {
    // Reduced to the errno code: a Node fs message names the owner's absolute paths.
    const code = (error as NodeJS.ErrnoException).code ?? 'unknown'
    return {
      exitCode: 1,
      stdout: '',
      stderr: `kyberdash capture plugin install: could not write the OpenCode plugin (${code})\n`,
    }
  }
  const out = [...result.out]
  if (result.consent !== 'none') {
    out.push(`opencode consent: ${result.consent} ${opencodePluginPaths(options.homeDir, options.env, pluginOptions).consentFile}`)
  }
  const pluginPresent = result.disposition !== 'skipped' && result.disposition !== 'refused' && result.disposition !== 'kept-foreign' && result.disposition !== 'kept-unreadable'
  if (
    pluginPresent &&
    readConsentState(opencodePluginPaths(options.homeDir, options.env, pluginOptions).consentFile) !== 'on'
  ) {
    out.push(OPENCODE_CONSENT_REMINDER)
  }
  return { exitCode: result.exitCode, stdout: text(out), stderr: text(result.err) }
}
