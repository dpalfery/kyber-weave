// The Factory (Droid) capture plugin commands.
//
// `kyberdash kyber capture plugin install --harness factory` is the one-shot install for the
// callers outside the Settings drawer (Squad, `kyber-weave update`); the OpenCode verb in
// ../opencode-plugin/install-command.ts is its precedent.
//
// Contract for the install:
//   stdout  `factory plugin: <token> <pluginDir>`, then optionally
//           `factory consent: <created|would-create|kept> <consentFile>`, then a reminder.
//   stderr  warnings (`kyberdash capture: warning: ...`) and strict refusals.
//   exit    0 success, including every preserve-mode keep and --if-installed skip;
//           1 strict refusal (a foreign tree, an owner edit, an unreadable path) or a failed
//           write;
//           2 usage (missing or unsupported --harness, or --config-dir).
//
// Contract for the consent:
//   stdout  `factory consent: content <on|off> <consentFile>`.
//   stderr  the usage message.
//   exit    0 when the file was written; 2 usage (a missing or unsupported --harness, or
//           neither or both of --on and --off).
//
// The install never turns the content consent on: it creates the consent file with content
// off, and only the consent verb (or the owner editing that file) records the owner's
// decision to capture the prompt text.
//
// The verb refuses `--config-dir`. That flag selects the OpenCode config root; the Factory
// harness resolves its own root ($FACTORY_DIR, else ~/.factory) — the same tree the Factory FILE
// source reads. A request that carries the flag is a usage error, and a refused request writes
// nothing at all: no plugin tree, no receipt, no consent file.

import { readConsentState, writeContentConsent } from '../../consent-file.js'
import type { PathEnv } from '../../paths.js'
import {
  factoryPluginPaths,
  installFactoryPlugin,
  type FactoryPluginOptions,
} from './install.js'
import type { FactoryHarnessCli } from './marketplace.js'

export const FACTORY_PLUGIN_USAGE_EXIT = 2

export type FactoryPluginCommandResult = { exitCode: number; stdout: string; stderr: string }

export type FactoryPluginInstallCommandOptions = {
  harness?: string
  preserve?: boolean
  ifInstalled?: boolean
  /**
   * `--config-dir`: the OpenCode config root. The Factory harness resolves its own root, so the
   * verb refuses the flag rather than ignoring it.
   */
  configDir?: string
  dryRun?: boolean
  homeDir: string
  env?: PathEnv
  /** Injectable; defaults to `process.env` (read only for `FACTORY_DIR`). */
  processEnv?: NodeJS.ProcessEnv
  /**
   * The harness's CLI, as the install reaches it. A test injects a fake; production
   * resolves `droid` from PATH.
   */
  harnessCli?: FactoryHarnessCli
}

export type FactoryPluginConsentCommandOptions = {
  harness?: string
  on?: boolean
  off?: boolean
  homeDir: string
  env?: PathEnv
}

function text(lines: readonly string[]): string {
  return lines.length > 0 ? `${lines.join('\n')}\n` : ''
}

/** The `--harness` argument as it is reported back to the caller. */
function harnessGiven(harness: string | undefined): string {
  return harness === undefined ? 'missing' : `"${harness}" is not supported`
}

/**
 * The install leaves the content consent off: the plugin is installed, and the owner's
 * decision about the prompt text is theirs to make.
 */
export const FACTORY_CONSENT_REMINDER =
  'factory: the prompt text is captured only after the content consent is turned on: kyberdash kyber capture plugin consent --harness factory --on'

export function runFactoryPluginInstall(
  options: FactoryPluginInstallCommandOptions,
): FactoryPluginCommandResult {
  // Checked before the harness, so the refusal holds whatever else the request carries: the flag
  // names the OpenCode config root, and the Factory harness resolves its own root.
  if (options.configDir !== undefined) {
    return {
      exitCode: FACTORY_PLUGIN_USAGE_EXIT,
      stdout: '',
      stderr:
        'kyberdash capture plugin install: --config-dir selects the OpenCode config root; the Factory harness resolves its own root ($FACTORY_DIR, else ~/.factory). Pass --harness opencode to use it.\n',
    }
  }
  if (options.harness !== 'factory') {
    return {
      exitCode: FACTORY_PLUGIN_USAGE_EXIT,
      stdout: '',
      stderr: `kyberdash capture plugin install: --harness ${harnessGiven(options.harness)}. Expected: factory.\n`,
    }
  }
  const pluginOptions: FactoryPluginOptions = {
    mode: options.preserve === true ? 'preserve' : 'strict',
    dryRun: options.dryRun === true,
    ifInstalled: options.ifInstalled === true,
    ...(options.processEnv !== undefined ? { processEnv: options.processEnv } : {}),
    ...(options.harnessCli !== undefined ? { harnessCli: options.harnessCli } : {}),
  }
  let result
  try {
    result = installFactoryPlugin(options.homeDir, options.env, pluginOptions)
  } catch (error) {
    // Reduced to the errno code: a Node fs message names the owner's absolute paths. The
    // code is read through the optional chain, so a failure that is not an object at all
    // is reported as unknown instead of raising a second failure over the first.
    const code = (error as NodeJS.ErrnoException | undefined)?.code ?? 'unknown'
    return {
      exitCode: 1,
      stdout: '',
      stderr: `kyberdash capture plugin install: could not write the Factory capture plugin (${code})\n`,
    }
  }
  const paths = factoryPluginPaths(options.homeDir, options.env, pluginOptions)
  const out = [...result.out]
  if (result.consent !== 'none') {
    out.push(`factory consent: ${result.consent} ${paths.consentFile}`)
  }
  const pluginPresent =
    result.disposition !== 'skipped' &&
    result.disposition !== 'refused' &&
    result.disposition !== 'kept-foreign' &&
    result.disposition !== 'kept-unreadable'
  if (pluginPresent && readConsentState(paths.consentFile) !== 'on') {
    out.push(FACTORY_CONSENT_REMINDER)
  }
  return { exitCode: result.exitCode, stdout: text(out), stderr: text(result.err) }
}

/**
 * The owner's decision about the content the plugin may record: the consent file is
 * `{ "version": 1, "content": <boolean> }`, and this is the only path that turns content on.
 */
export function runFactoryPluginConsent(
  options: FactoryPluginConsentCommandOptions,
): FactoryPluginCommandResult {
  if (options.harness !== 'factory') {
    return {
      exitCode: FACTORY_PLUGIN_USAGE_EXIT,
      stdout: '',
      stderr: `kyberdash capture plugin consent: --harness ${harnessGiven(options.harness)}. Expected: factory.\n`,
    }
  }
  if (options.on === true && options.off === true) {
    return {
      exitCode: FACTORY_PLUGIN_USAGE_EXIT,
      stdout: '',
      stderr: 'kyberdash capture plugin consent: --on and --off contradict each other; pass exactly one.\n',
    }
  }
  if (options.on !== true && options.off !== true) {
    return {
      exitCode: FACTORY_PLUGIN_USAGE_EXIT,
      stdout: '',
      stderr: 'kyberdash capture plugin consent: exactly one of --on or --off is required.\n',
    }
  }
  const content = options.on === true
  const { consentFile } = factoryPluginPaths(options.homeDir, options.env)
  try {
    writeContentConsent(consentFile, content)
  } catch (error) {
    // The errno code is read through the optional chain: a failure that is not an object
    // at all has no code, and reading one anyway raises a second failure over the first.
    const code = (error as NodeJS.ErrnoException | undefined)?.code ?? 'unknown'
    return {
      exitCode: 1,
      stdout: '',
      stderr: `kyberdash capture plugin consent: could not write the consent file (${code})\n`,
    }
  }
  return {
    exitCode: 0,
    stdout: `factory consent: content ${content ? 'on' : 'off'} ${consentFile}\n`,
    stderr: '',
  }
}
