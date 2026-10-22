// Copilot capture writer (P2.1b).
//
// VS Code stores Copilot chat OTel on one settings property whose name
// contains dots, `github.copilot.chat.otel`. Enable writes six
// KyberDash-owned children on Stable and Insiders, and the property is
// declared a *literal* prefix so those children land in that one property
// rather than a nested `github` > `copilot` > `chat` > `otel` tree VS Code
// never reads (the live defect: keys present, exporter=file, every span
// discarded). `protocol` is omitted from the set so an owner-chosen protocol
// stays put. `captureIdentity` is never a desired key: status only warns when
// it is already true, and when the protocol or exporter is gRPC, because
// KyberDash receives OTLP/HTTP and does not turn identity attributes on.
//
// The Copilot CLI and the extension both also read an environment override,
// `COPILOT_OTEL_ENABLED`. KyberDash does not write environment: every writer
// here edits a config file it can receipt per key and restore byte-faithfully,
// and a GUI session's environment is neither (a shell rc file is not read by
// Finder-launched VS Code, and `launchctl setenv` is a login-scoped system
// mutation with no per-key inverse). So the decision is deliberate — enable
// writes the settings the extension reads, and status *resolves* the exporter
// it will actually get, warning with the exact `launchctl setenv` remedy when
// the override is missing. Environment stays the owner's to set.

import type { JsonScalar } from '../edit-json.js'
import { pjoin, type PathEnv } from '../paths.js'
import type {
  CaptureConfigFile,
  CaptureExporterInput,
  CaptureExporterResolution,
  CaptureStatusWarning,
  ManagedHarnessWriter,
} from './registry.js'

/** The one settings property Copilot uses for chat OTel. Children hang off it. */
const OTEL_PROPERTY = 'github.copilot.chat.otel'

/** VS Code reads these keys only as children of that one dotted property. */
const OTEL_LITERAL_PREFIXES = [OTEL_PROPERTY] as const

/**
 * Env keys the CLI surface reports, and the keys `status` reads to resolve the
 * exporter. `enable` writes none of them: see the module comment for why the
 * environment is left to the owner.
 */
const COPILOT_CLI_ENV_KEYS = [
  'COPILOT_OTEL_ENABLED',
  'OTEL_EXPORTER_OTLP_ENDPOINT',
  'OTEL_EXPORTER_OTLP_PROTOCOL',
  'OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT',
  'COPILOT_OTEL_CAPTURE_CONTENT',
  'COPILOT_OTEL_EXPORTER_TYPE',
  'COPILOT_OTEL_MAX_ATTRIBUTE_SIZE_CHARS',
] as const

function copilotDesiredKeys(endpoint: string): Record<string, JsonScalar> {
  return {
    [`${OTEL_PROPERTY}.enabled`]: true,
    // Forces the pipeline on even when the extension's own `enabledExplicitly`
    // computes false. Without it Copilot logs `exporter=file` in every session
    // and hands the SDK a file exporter pointed at os.devNull.
    [`${OTEL_PROPERTY}.dbSpanExporter.enabled`]: true,
    [`${OTEL_PROPERTY}.exporterType`]: 'otlp-http',
    [`${OTEL_PROPERTY}.otlpEndpoint`]: endpoint,
    [`${OTEL_PROPERTY}.captureContent`]: true,
    [`${OTEL_PROPERTY}.maxAttributeSizeChars`]: 0,
  }
}

/** A boolean override is on unless it is blank, `0` or `false`. */
function envTruthy(value: string | undefined): boolean {
  if (value === undefined) return false
  const normalised = value.trim().toLowerCase()
  return normalised !== '' && normalised !== '0' && normalised !== 'false'
}

/**
 * The exporter VS Code Copilot will actually use, from the values `status`
 * observes plus the environment the caller can vouch for. The extension keeps
 * its OTel pipeline off — feeding the SDK a file exporter pointed at os.devNull
 * — unless the settings keys and the `COPILOT_OTEL_ENABLED` override are all in
 * place. This is the check the old status lacked: it reported key presence, so
 * "keys present but the pipeline is off" read as a pass.
 *
 * `processEnv` is omitted when the caller cannot see the environment that
 * decides. The server-side status route runs under this machine's web process,
 * not the editor's, so it reports the override as unverified rather than
 * absent: reading `process.env` unconditionally made the API warn about a GUI
 * launch that was actually configured correctly.
 */
export function resolveCopilotExporter(input: CaptureExporterInput): CaptureExporterResolution {
  const value = (leaf: string): unknown => input.observed[`${OTEL_PROPERTY}.${leaf}`]
  const protocol = value('exporterType')
  const envKnown = input.processEnv !== undefined
  const envReady = envTruthy(input.processEnv?.['COPILOT_OTEL_ENABLED'])

  // A protocol Copilot is set to use that KyberDash does not receive. This is
  // not the dev-null case — spans do leave the editor, they just do not arrive
  // here — so naming the exporter is the honest answer. The existing
  // `statusWarningsFor` gRPC entries already say this about this value.
  if (typeof protocol === 'string' && protocol !== '' && protocol !== 'otlp-http') {
    return {
      exporter: protocol,
      delivering: false,
      line: `exporter: ${protocol} — not KyberDash's OTLP/HTTP receiver`,
      warning:
        `Copilot is configured to export over "${protocol}"; KyberDash receives OTLP/HTTP only. ` +
        'Run "kyberdash kyber capture enable" for copilot to write the values KyberDash reads.',
    }
  }

  const settingsReady =
    value('enabled') === true &&
    value('dbSpanExporter.enabled') === true &&
    protocol === 'otlp-http'
  if (settingsReady && envReady) {
    return {
      exporter: 'otlp-http',
      delivering: true,
      line: 'exporter: otlp-http (Copilot OTLP pipeline on)',
      warning: '',
    }
  }

  const remedy =
    'Run "kyberdash kyber capture enable" for copilot, and on macOS hand a GUI-launched VS Code the override with ' +
    '"launchctl setenv COPILOT_OTEL_ENABLED true" (and OTEL_EXPORTER_OTLP_ENDPOINT), then fully quit and relaunch it.'

  // Settings in order, environment not this process's to read. The status
  // route runs under the web process, not the editor's, so it cannot judge the
  // override: it withholds the verdict and says so on its line rather than
  // warning about an environment it never saw. `delivering` stays false —
  // delivery is unconfirmed, not confirmed — but an empty warning raises
  // nothing, and the CLI, which can see a shell's environment, still diagnoses
  // the override by name.
  if (settingsReady && !envKnown) {
    return {
      exporter: 'otlp-http',
      delivering: false,
      line: "exporter: otlp-http (settings at the values enable writes; the editor's environment is not visible from here)",
      warning: '',
    }
  }

  const reasons: string[] = []
  if (!settingsReady) {
    reasons.push('the github.copilot.chat.otel settings keys are not all at the values enable writes')
  }
  // Only a caller that can see the environment may report the override missing.
  if (envKnown && !envReady) {
    reasons.push('COPILOT_OTEL_ENABLED is missing from the environment this command sees')
  }
  return {
    exporter: 'file',
    delivering: false,
    line: 'exporter: file (dev-null) — Copilot discards every span',
    warning: `exporter resolves to file (dev-null): ${reasons.join('; ')}. ${remedy}`,
  }
}

/**
 * VS Code user settings for one edition, rooted at the HOME capture was given;
 * win32 roots at the application-data directory instead, because that is
 * where VS Code reads User/settings.json on Windows. XDG_CONFIG_HOME and the
 * real Library are ignored so a temporary HOME cannot leak into the user's
 * live settings, and so receipt paths match the files enable wrote.
 */
function vscodeSettingsPath(
  home: string,
  edition: 'Code' | 'Code - Insiders',
  env?: PathEnv,
): string {
  const platform = env?.platform ?? process.platform
  if (platform === 'win32') {
    // WHY this precedence: an injected appData (a test's temp directory) must
    // beat the real APPDATA so a synthetic run never resolves into the
    // user's live settings; APPDATA beats the HOME guess because Windows
    // itself exports it; <home>\AppData\Roaming is the documented default
    // when neither is available. An injected appData is ignored on darwin
    // and linux, whose strings D14 leaves unchanged.
    const fallbackRoaming = pjoin(platform, home, 'AppData', 'Roaming')
    const appData = env?.appData ?? process.env.APPDATA ?? fallbackRoaming
    return pjoin(platform, appData, edition, 'User', 'settings.json')
  }
  if (platform === 'darwin') {
    return pjoin(platform, home, 'Library', 'Application Support', edition, 'User', 'settings.json')
  }
  return pjoin(platform, home, '.config', edition, 'User', 'settings.json')
}

function vscodeConfigFile(fileId: string, edition: 'Code' | 'Code - Insiders'): CaptureConfigFile {
  return {
    fileId,
    format: 'jsonc',
    resolvePath: (home: string, env?: PathEnv) => vscodeSettingsPath(home, edition, env),
    desiredKeys: copilotDesiredKeys,
    literalKeyPrefixes: OTEL_LITERAL_PREFIXES,
    exporterResolution: resolveCopilotExporter,
  }
}

const vscodeStable = vscodeConfigFile('vscode-stable', 'Code')
const vscodeInsiders = vscodeConfigFile('vscode-insiders', 'Code - Insiders')

/**
 * Status-only. These keys are not in `desiredKeys`, so enable never writes
 * them; the warning fires only when the file already holds the value.
 */
function statusWarningsFor(fileId: string): CaptureStatusWarning[] {
  return [
    {
      fileId,
      key: `${OTEL_PROPERTY}.captureIdentity`,
      whenValue: true,
      warnText:
        'captureIdentity is true; Copilot will export user and host identity, which KyberDash does not enable',
    },
    {
      fileId,
      key: `${OTEL_PROPERTY}.protocol`,
      whenValue: 'grpc',
      warnText: 'protocol is grpc; KyberDash receives OTLP/HTTP only and leaves protocol untouched',
    },
    {
      fileId,
      key: `${OTEL_PROPERTY}.exporterType`,
      whenValue: 'otlp-grpc',
      warnText: 'exporterType is otlp-grpc; KyberDash receives OTLP/HTTP only',
    },
  ]
}

export const copilotHarness: ManagedHarnessWriter = {
  kind: 'managed',
  id: 'copilot',
  displayName: 'Copilot',
  // configFiles is what enable writes. The top-level fields stay the Stable
  // file so a single-file reader still sees a real Copilot surface.
  resolvePath: vscodeStable.resolvePath,
  format: 'jsonc',
  desiredKeys: copilotDesiredKeys,
  configFiles: [vscodeStable, vscodeInsiders],
  statusWarnings: [
    ...statusWarningsFor(vscodeStable.fileId),
    ...statusWarningsFor(vscodeInsiders.fileId),
  ],
  statusEnvSnippet: {
    label: 'Copilot CLI',
    envKeys: COPILOT_CLI_ENV_KEYS,
  },
}
