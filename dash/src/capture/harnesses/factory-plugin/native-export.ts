// Factory's native OpenTelemetry export: where its endpoint comes from, and whether it is
// this KyberDash receiver.
//
// Factory (Droid) can export native OTLP telemetry to a customer endpoint. The endpoint is
// configured in one of two places, and the organisation's setting wins:
//
//   1. an organisation-managed `telemetry` block. It OVERRIDES the machine variable: when the
//      organisation pins an endpoint, the machine variable is not consulted at all.
//   2. the machine variable `OTEL_TELEMETRY_ENDPOINT`, with the documented fallback
//      `OTEL_EXPORTER_OTLP_ENDPOINT`.
//
// The organisation's block reaches the machine as the cached view under the Factory root
// (`org-managed-settings.cache.json`, whose `managedSettings.telemetry.endpoint` carries the
// pinned endpoint) and as the system-managed copy (`settings.json`, whose `telemetry.endpoint`
// carries it). Both are read, so a machine that holds only one of them still reports the
// organisation's pin.
//
// An organisation pin is an administrative override the developer does not control, so the
// verdict under one is never "working": the row names the organisation as the cause and the
// plugin path stays the capture path in that deployment (plan D26).
//
// This module is a LEAF of the engine: `node:` built-ins only, no project import and no
// third-party package. It therefore takes the paths it reads and the environment it consults
// as arguments, and it belongs in the leaf list of
// `dash/src/architecture/factory-leaves.test.ts`.

import { readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'

/** The cached organisation settings under the Factory root. */
const ORGANISATION_CACHE_FILE = 'org-managed-settings.cache.json'
/** The system-managed settings file. */
const SYSTEM_SETTINGS_FILE = 'settings.json'
/** The cap on one settings file: the real ones are a few kilobytes. */
const MAX_SETTINGS_BYTES = 1024 * 1024
/** The cap on an echoed endpoint, so one status line stays one line. */
const MAX_ENDPOINT_CHARS = 200

/** The machine variable Factory's native export reads first. */
const TELEMETRY_ENDPOINT_VARIABLE = 'OTEL_TELEMETRY_ENDPOINT'
/** The documented fallback beside it. */
const FALLBACK_TELEMETRY_ENDPOINT_VARIABLE = 'OTEL_EXPORTER_OTLP_ENDPOINT'

/**
 * The port this KyberDash receiver listens on: the OTLP/HTTP standard port, the same one
 * `DEFAULT_CAPTURE_ENDPOINT` names in `dash/src/capture/index.ts`. A leaf may not import the
 * capture core, so the port is stated here and the endpoint is compared against it.
 */
const RECEIVER_PORT = 4318

/**
 * The loopback names that reach the receiver. `localhost` is among them because it is
 * Factory's own documented example (`OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"`), and
 * that address is this receiver.
 */
const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '::1', '[::1]'])

/** The machine variables Factory's native export reads, in the order it reads them. */
export type NativeExportVariable =
  | typeof TELEMETRY_ENDPOINT_VARIABLE
  | typeof FALLBACK_TELEMETRY_ENDPOINT_VARIABLE

/**
 * The native export's state. `available` means the effective endpoint IS this KyberDash
 * receiver; `unavailable` names the cause; `not-configured` means nothing configures the
 * export at all — neither working nor unavailable for a reason.
 *
 * The union is exact: an organisation's pin is never `available`, and a machine variable is
 * never the `organisation-pinned` cause, so the surface cannot word a state the
 * configuration does not hold.
 */
export type NativeExportVerdict =
  | { state: 'available'; endpoint: string; source: NativeExportVariable }
  | {
      state: 'unavailable'
      endpoint: string
      source: 'organisation'
      cause: 'organisation-pinned'
    }
  | {
      state: 'unavailable'
      endpoint: string
      source: NativeExportVariable
      cause: 'endpoint-differs'
    }
  | { state: 'not-configured' }

/** Where the organisation's and the machine's configuration are read from. */
export type NativeExportSources = {
  /** The Factory root: the organisation-settings cache lives under it. */
  factoryRoot: string
  /** The home the system-managed copy lives under. */
  home: string
  /** The platform the system-managed directory is resolved for. */
  platform: NodeJS.Platform
  /** The Windows application-data root, when the caller injected one. */
  appData?: string
}

export type NativeExportOptions = {
  /** Injectable for tests; defaults to `process.env`. */
  processEnv?: NodeJS.ProcessEnv
}

/**
 * The parsed contents of one settings file, or undefined when the file is absent, unreadable
 * or malformed. An absent settings file is an expected state — a machine may pin nothing —
 * so it reads as absent rather than as a failure. A malformed or unreadable file reads the
 * same way: the row then under-reports the native path, which never claims a working path
 * that is not.
 */
function readSettingsFile(path: string): unknown {
  try {
    const stat = statSync(path)
    if (!stat.isFile() || stat.size > MAX_SETTINGS_BYTES) return undefined
    return JSON.parse(readFileSync(path, 'utf8')) as unknown
  } catch {
    return undefined
  }
}

/**
 * The endpoint as it is echoed in a status line. A control character is replaced, so a value
 * out of a settings file or an environment variable cannot break the line-based status
 * surface, and the value is capped so one line stays one line.
 */
function displayEndpoint(value: string): string | undefined {
  const cleaned = value.replace(/[\u0000-\u001f\u007f]/g, ' ').trim()
  if (cleaned === '') return undefined
  return cleaned.length > MAX_ENDPOINT_CHARS ? `${cleaned.slice(0, MAX_ENDPOINT_CHARS)}…` : cleaned
}

/** A record view of a parsed document, or undefined when it is not an object. */
function asRecord(value: unknown): Record<string, unknown> | undefined {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return undefined
  return value as Record<string, unknown>
}

/** The `telemetry.endpoint` of a settings document, when it carries one. */
function telemetryEndpoint(value: unknown): string | undefined {
  const endpoint = asRecord(asRecord(value)?.['telemetry'])?.['endpoint']
  if (typeof endpoint !== 'string') return undefined
  return displayEndpoint(endpoint)
}

/** The `managedSettings.telemetry.endpoint` of the cached organisation settings. */
function cachedOrganisationEndpoint(value: unknown): string | undefined {
  return telemetryEndpoint(asRecord(value)?.['managedSettings'])
}

/**
 * The directory the system-managed organisation settings live in: the application-data root
 * on Windows, the application-support directory on macOS, and the machine-wide `etc`
 * directory on Linux. It is resolved under the given home, so the reader never reaches the
 * real machine's own settings file.
 */
function systemManagedDir(sources: NativeExportSources): string {
  if (sources.platform === 'win32') {
    return join(sources.appData ?? join(sources.home, 'AppData', 'Roaming'), 'Factory')
  }
  if (sources.platform === 'linux') return join(sources.home, 'etc', 'factory')
  return join(sources.home, 'Library', 'Application Support', 'Factory')
}

/**
 * The endpoint an organisation pins, when one pins it. The cached view under the Factory
 * root is read first — it is the view Factory itself reads — and the system-managed copy is
 * the fallback.
 */
function organisationEndpoint(sources: NativeExportSources): string | undefined {
  const cached = cachedOrganisationEndpoint(
    readSettingsFile(join(sources.factoryRoot, ORGANISATION_CACHE_FILE)),
  )
  if (cached !== undefined) return cached
  return telemetryEndpoint(readSettingsFile(join(systemManagedDir(sources), SYSTEM_SETTINGS_FILE)))
}

/** The machine variable that names the endpoint, and the endpoint it names. */
function machineEndpoint(
  processEnv: NodeJS.ProcessEnv,
): { endpoint: string; source: NativeExportVariable } | undefined {
  const variables: readonly NativeExportVariable[] = [
    TELEMETRY_ENDPOINT_VARIABLE,
    FALLBACK_TELEMETRY_ENDPOINT_VARIABLE,
  ]
  for (const variable of variables) {
    const value = processEnv[variable]
    if (value === undefined) continue
    const endpoint = displayEndpoint(value)
    if (endpoint === undefined) continue
    return { endpoint, source: variable }
  }
  return undefined
}

/**
 * Whether the endpoint points at this KyberDash receiver: the loopback host on the OTLP/HTTP
 * port, over plain HTTP — the receiver speaks OTLP/HTTP, not TLS.
 */
function pointsAtReceiver(endpoint: string): boolean {
  let url: URL
  try {
    url = new URL(endpoint)
  } catch {
    return false
  }
  if (url.protocol !== 'http:') return false
  if (!LOOPBACK_HOSTS.has(url.hostname)) return false
  return url.port === String(RECEIVER_PORT)
}

/**
 * The native export's effective endpoint and state.
 *
 * The organisation's pinned endpoint wins outright: when one is pinned, the machine variable
 * is not consulted at all, and the verdict is `unavailable` with the organisation named as
 * the cause — a developer whose machine variable points at this receiver must not be told
 * the native path works when the organisation's endpoint is what receives the traffic.
 */
export function readNativeExport(
  sources: NativeExportSources,
  options: NativeExportOptions = {},
): NativeExportVerdict {
  const processEnv = options.processEnv ?? process.env
  const organisation = organisationEndpoint(sources)
  if (organisation !== undefined) {
    return {
      state: 'unavailable',
      endpoint: organisation,
      source: 'organisation',
      cause: 'organisation-pinned',
    }
  }
  const machine = machineEndpoint(processEnv)
  if (machine === undefined) return { state: 'not-configured' }
  return pointsAtReceiver(machine.endpoint)
    ? { state: 'available', endpoint: machine.endpoint, source: machine.source }
    : {
        state: 'unavailable',
        endpoint: machine.endpoint,
        source: machine.source,
        cause: 'endpoint-differs',
      }
}
