// Copilot capture writer (P2.1b).
//
// VS Code stores Copilot chat OTel on one settings property whose name
// contains dots, `github.copilot.chat.otel`. Enable writes the five
// KyberDash-owned children on Stable and Insiders. `protocol` is omitted
// from that set so an owner-chosen protocol stays put. `captureIdentity` is
// never a desired key: status only warns when it is already true, and when
// the protocol or exporter is gRPC, because KyberDash receives OTLP/HTTP
// and does not turn identity attributes on. The Copilot CLI is env-only;
// the snippet is read from this process, and shell rc files are never opened.

import { join } from 'node:path'

import type { JsonScalar } from '../edit-json.js'
import type { CaptureConfigFile, CaptureStatusWarning, ManagedHarnessWriter } from './registry.js'

/** The one settings property Copilot uses for chat OTel. Children hang off it. */
const OTEL_PROPERTY = 'github.copilot.chat.otel'

/** Env keys the CLI surface reports. Status prints them; enable never writes them. */
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
    [`${OTEL_PROPERTY}.exporterType`]: 'otlp-http',
    [`${OTEL_PROPERTY}.otlpEndpoint`]: endpoint,
    [`${OTEL_PROPERTY}.captureContent`]: true,
    [`${OTEL_PROPERTY}.maxAttributeSizeChars`]: 0,
  }
}

/**
 * VS Code user settings for one edition, rooted at the HOME capture was given.
 * XDG_CONFIG_HOME and the real Library are ignored so a temporary HOME cannot
 * leak into the user's live settings, and so receipt paths match the files
 * enable wrote.
 */
function vscodeSettingsPath(home: string, edition: 'Code' | 'Code - Insiders'): string {
  if (process.platform === 'darwin') {
    return join(home, 'Library', 'Application Support', edition, 'User', 'settings.json')
  }
  return join(home, '.config', edition, 'User', 'settings.json')
}

function vscodeConfigFile(fileId: string, edition: 'Code' | 'Code - Insiders'): CaptureConfigFile {
  return {
    fileId,
    format: 'jsonc',
    resolvePath: (home: string) => vscodeSettingsPath(home, edition),
    desiredKeys: copilotDesiredKeys,
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
