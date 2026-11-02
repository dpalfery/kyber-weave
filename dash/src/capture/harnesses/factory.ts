// Factory (Droid) capture writer: plugin-only.
//
// Factory reaches KyberDash through a capture plugin DIRECTORY in its own root
// (`<factory root>/plugins/kyberdash-factory`), not through a configuration key. The row
// therefore declares no key at all: `enable` installs the plugin tree and `disable` removes
// it. The owner's own `~/.factory/settings.json` is never written, merged or receipted —
// `resolvePath` only names where Factory keeps its settings, which is where the harness is
// found.
//
// The CONTENT consent is the owner's own decision, and it is deliberately NOT the Settings
// switch: `enable` installs the capture and leaves that decision exactly as it was, and only
// the consent verb (`kyberdash kyber capture plugin consent --harness factory --on`) or the
// owner editing the consent file turns the prompt text on. `disable` turns it off. This
// differs from the OpenCode writer, whose Settings switch does set the consent.
//
// The Factory root is `$FACTORY_DIR`, else `~/.factory`: the resolution the Factory file
// provider already uses (`dash/src/providers/droid.ts`) and the one the installer uses.

import {
  createConsentIfAbsent,
  readConsentState,
  writeContentConsent,
  type ConsentState,
} from '../consent-file.js'
import { envJoin, type PathEnv } from '../paths.js'

import {
  describeFactoryPluginStatus,
  factoryPluginPaths,
  factoryPluginState,
  installFactoryPlugin,
  uninstallFactoryPlugin,
  type FactoryPluginResult,
} from './factory-plugin/install.js'
import {
  readNativeExport,
  type NativeExportShape,
  type NativeExportShapeSource,
  type NativeExportVerdict,
} from './factory-plugin/native-export.js'
import { resolveFactoryHarnessCli } from './factory-plugin/marketplace.js'
import type { ManagedFilePlugin, ManagedHarnessWriter } from './registry.js'

const FACTORY_HARNESS_ID = 'factory'

/** What the enable path says when the content consent is off: the switch never records the prompt text. */
const OFF_CONSENT = 'harness factory: content consent off (the capture switch does not record the prompt text)'

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
 * The native OpenTelemetry export's verdict, read from the Factory root's cached
 * organisation settings and the system-managed copy beside it. The owner's own settings file
 * is never read for it.
 */
function nativeExportVerdict(home: string, env: PathEnv | undefined): NativeExportVerdict {
  const paths = factoryPluginPaths(home, env)
  return readNativeExport({
    factoryRoot: paths.root,
    home,
    platform: env?.platform ?? process.platform,
    ...(env?.appData !== undefined ? { appData: env.appData } : {}),
  })
}

/**
 * The native OpenTelemetry export's line: whether Factory's own export is pointed at this
 * KyberDash receiver, and the cause when it is not.
 *
 * The native path is never reported as working when it is not. An organisation's pinned
 * endpoint OVERRIDES the machine variable, so a developer whose variable points at this
 * receiver is told the native path is unavailable and that the organisation is the cause —
 * not that the native path works, when the organisation's endpoint is what receives the
 * traffic.
 */
function nativeExportLine(verdict: NativeExportVerdict): string {
  switch (verdict.state) {
    case 'available':
      return `native OpenTelemetry export: available (${verdict.source} points at ${verdict.endpoint}, this KyberDash receiver)`
    case 'unavailable':
      return verdict.source === 'organisation'
        ? `native OpenTelemetry export: unavailable (an organisation pins the telemetry endpoint ${verdict.endpoint} in its managed settings, and that endpoint overrides the machine variable)`
        : `native OpenTelemetry export: unavailable (${verdict.source} points at ${verdict.endpoint}, not this KyberDash receiver)`
    case 'not-configured':
      return 'native OpenTelemetry export: not configured (neither OTEL_TELEMETRY_ENDPOINT nor OTEL_EXPORTER_OTLP_ENDPOINT is set, and no organisation pins a telemetry endpoint)'
  }
}

/** The place a resolved switch came from, in the words the row prints. */
function shapeSource(source: NativeExportShapeSource): string {
  switch (source) {
    case 'organisation':
      return "the organisation's telemetry block"
    case 'granularity':
      return 'the aggregate granularity overrides every content opt-in'
    case 'default':
      return 'the documented default'
    default:
      return source
  }
}

/**
 * The export's EFFECTIVE SHAPE: the format, the granularity and the message-content switch,
 * each with the source it came from.
 *
 * The endpoint says where the export sends; the shape says what it sends. The format picks
 * the metric catalog, the granularity decides whether the legacy format produces spans at
 * all, and the content switch decides whether content-bearing spans are produced — so a row
 * that named only the endpoint could read as "the native path works" while the export is
 * pinned to a shape KyberDash does not read.
 */
function nativeExportShapeLine(shape: NativeExportShape): string {
  return `native OpenTelemetry export shape: format ${shape.format.value} (${shapeSource(shape.format.source)}), granularity ${shape.granularity.value} (${shapeSource(shape.granularity.source)}), message content ${shape.content.value ? 'on' : 'off'} (${shapeSource(shape.content.source)})`
}

/**
 * What KyberDash does with the export, in fixed words: the metrics are persisted, no native
 * span is claimed, and the content consequence follows the consent file.
 *
 * The consent is KyberDash's own (`~/.kyberdash/factory-plugin.json`) and it is READ here,
 * not inferred from the shape: the export's own content switch says what Factory sends, and
 * the consent says what KyberDash is allowed to keep.
 */
function nativeExportConsequenceLine(consent: ConsentState): string {
  const content =
    consent === 'on'
      ? "Factory content is stored while KyberDash's consent is on"
      : "no Factory content is stored while KyberDash's consent is off"
  return `native OpenTelemetry export: Factory's native metrics are persisted; Factory's native spans are not claimed; ${content}`
}

/**
 * The status lines for the Factory row: the plugin state, the directory the plugin
 * resolves to, the receipt and the content consent. The consent lines come from the
 * installer, which owns the consent file's shape; the directory and the receipt are added
 * here because the Factory plugin is a TREE and the row has to say where it lives. The
 * native export lines are added here too: they are the Factory row's own facts about the
 * second capture path, and they are the same lines the printed status, the structured row
 * and the API route carry.
 */
function factoryStatusLines(home: string, env: PathEnv | undefined): string[] {
  const paths = factoryPluginPaths(home, env)
  const state = factoryPluginState(home, env)
  const described = describeFactoryPluginStatus(home, env)
  const plugin = described[0] ?? 'plugin: unknown'
  const verdict = nativeExportVerdict(home, env)
  return [
    plugin,
    `plugin directory: ${paths.pluginDir}`,
    `plugin receipt: ${state.receipt ? 'recorded' : 'absent'}`,
    ...described.slice(1),
    nativeExportLine(verdict),
    nativeExportShapeLine(verdict.shape),
    nativeExportConsequenceLine(readConsentState(paths.consentFile)),
  ]
}

const factoryFilePlugin: ManagedFilePlugin = {
  install: (home, env, options) =>
    narrow(
      installFactoryPlugin(home, env, {
        mode: 'strict',
        dryRun: options.dryRun,
        harnessCli: options.harnessCli ?? resolveFactoryHarnessCli(),
      }),
    ),
  uninstall: (home, env, options) =>
    narrow(
      uninstallFactoryPlugin(home, env, {
        dryRun: options.dryRun,
        harnessCli: options.harnessCli ?? resolveFactoryHarnessCli(),
      }),
    ),
  path: (home, env) => factoryPluginPaths(home, env).pluginDir,
  state: (home, env) => factoryPluginState(home, env),
  describeStatus: (home, env) => factoryStatusLines(home, env),
  consent: {
    // The Settings switch starts the capture; it never opts the prompt text in. The content
    // consent is the owner's own decision, so `enable` leaves the file exactly as it is and
    // creates it with content off only when it is absent; `disable` turns content off.
    set: (home, env, content, options) => {
      const { consentFile } = factoryPluginPaths(home, env)
      if (!content) {
        if (options.dryRun === true) return { out: ['harness factory: would set content consent off'] }
        writeContentConsent(consentFile, false)
        return { out: ['harness factory: content consent off'] }
      }
      const state = readConsentState(consentFile)
      if (state === 'absent') {
        if (options.dryRun === true) {
          return { out: ['harness factory: would create the content consent with content off'] }
        }
        createConsentIfAbsent(consentFile)
        return { out: [OFF_CONSENT] }
      }
      if (state === 'on') {
        return { out: ['harness factory: content consent on (left as the owner set it)'] }
      }
      if (state === 'off') {
        return { out: [OFF_CONSENT] }
      }
      // A malformed or unreadable file is the owner's to fix: it reads off either way.
      return { out: ['harness factory: content consent left as it is'] }
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
