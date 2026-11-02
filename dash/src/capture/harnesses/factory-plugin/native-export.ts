// Factory's native OpenTelemetry export: where its endpoint comes from, whether it is this
// KyberDash receiver, and what its EFFECTIVE SHAPE is.
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
// (`org-managed-settings.cache.json`, whose `managedSettings.telemetry` carries the pinned
// values) and as the system-managed copy (`settings.json`, whose `telemetry` carries them).
// Both are read, so a machine that holds only one of them still reports the organisation's
// pin.
//
// An organisation pin is an administrative override the developer does not control, so the
// verdict under one is never "working": the row names the organisation as the cause and the
// plugin path stays the capture path in that deployment (plan D26).
//
// The endpoint does not say what the export SENDS. That is the export's shape, and it is
// configured in the same two places — but NOT by one rule:
//
//   - the ENABLED switch follows precedence. The organisation's `telemetry.enabled` wins
//     outright: where it is set at all the machine's `OTEL_CUSTOMER_ENABLED` is not read,
//     so an organisation can keep the customer pipeline on (or off) whatever the machine
//     says. Where the organisation sets nothing, the machine's variable decides — and
//     where the machine says nothing either the pipeline is ON, because Factory's own
//     documentation makes that variable an opt-OUT.
//   - the FORMAT follows precedence. The organisation's `telemetry.format` wins, and where it
//     is set the machine's `OTEL_TELEMETRY_FORMAT` is not read at all. Anything
//     unrecognised resolves to `legacy`.
//   - the GRANULARITY follows most-restrictive-wins, not precedence: `aggregate` beats
//     `user`, whichever level each of them came from. A value that CANNOT BE READ
//     resolves to `aggregate` — that is Factory's own published privacy rule, and it is
//     the restrictive direction. A value that was never set is a different case: it takes
//     the documented default, which is `user`.
//   - the MESSAGE-CONTENT switch follows precedence for the organisation's
//     `telemetry.logMessageContent` — when it is set at all the machine's
//     `OTEL_LOG_MESSAGE_CONTENT` does not decide — and the granularity overrides both:
//     `aggregate` overrides every content opt-in.
//
// The shape decides what arrives: the format picks the metric catalog (`droid.*` under
// `legacy`, `gen_ai.*` under `genai` — the two replace each other and do not stack), the
// granularity decides whether the legacy format produces spans at all, and the content
// switch decides whether content-bearing spans are produced.
//
// This module is a LEAF of the engine: `node:` built-ins only, no project import and no
// third-party package. It therefore takes the paths it reads and the environment it consults
// as arguments, and it belongs in the leaf list of
// `dash/src/architecture/factory-leaves.test.ts`.

import { existsSync, readFileSync, statSync } from 'node:fs'
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
/** The machine variable that carries the export's format. */
const TELEMETRY_FORMAT_VARIABLE = 'OTEL_TELEMETRY_FORMAT'
/** The machine variable that carries the export's granularity. */
const TELEMETRY_GRANULARITY_VARIABLE = 'OTEL_TELEMETRY_GRANULARITY'
/** The machine variable that carries the message-content switch. */
const LOG_MESSAGE_CONTENT_VARIABLE = 'OTEL_LOG_MESSAGE_CONTENT'
/**
 * The machine variable that carries the export's master switch. Factory's own
 * documentation makes it an opt-OUT: setting it to `false` takes the machine out of the
 * customer telemetry pipeline, and leaving it unset does not.
 */
const CUSTOMER_ENABLED_VARIABLE = 'OTEL_CUSTOMER_ENABLED'

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

/** The machine variables Factory's native export reads for its endpoint, in the order it reads them. */
export type NativeExportVariable =
  | typeof TELEMETRY_ENDPOINT_VARIABLE
  | typeof FALLBACK_TELEMETRY_ENDPOINT_VARIABLE

/** The format the native export sends: `legacy` or `genai`. */
export type NativeExportFormat = 'legacy' | 'genai'

/** The granularity the native export sends: per-individual (`user`) or aggregate. */
export type NativeExportGranularity = 'user' | 'aggregate'

/**
 * Where a resolved switch came from: the organisation's managed `telemetry` block, one of
 * the machine variables, the documented default, or the granularity — which overrides every
 * content opt-in.
 */
export type NativeExportShapeSource =
  | 'organisation'
  | 'default'
  | 'granularity'
  | typeof TELEMETRY_FORMAT_VARIABLE
  | typeof TELEMETRY_GRANULARITY_VARIABLE
  | typeof LOG_MESSAGE_CONTENT_VARIABLE
  | typeof CUSTOMER_ENABLED_VARIABLE

/** One resolved switch: its value, and the place it came from. */
export type NativeExportSwitch<T> = {
  value: T
  source: NativeExportShapeSource
  /**
   * Set when the source carried a value that could not be read, so the restrictive value
   * applied instead of the value the source named. The source still names the level that
   * carried it, because the source alone would read as that level having pinned the
   * value the row reports.
   */
  unreadable?: true
}

/**
 * The native export's effective shape: the enabled switch, the format, the granularity and
 * the message-content switch, each with the source it came from.
 *
 * The shape is not the endpoint. The enabled switch decides whether the export runs at
 * all, the format decides which metric catalog arrives, the granularity decides whether
 * the legacy format produces spans at all, and the content switch decides whether
 * content-bearing spans are produced.
 */
export type NativeExportShape = {
  enabled: NativeExportSwitch<boolean>
  format: NativeExportSwitch<NativeExportFormat>
  granularity: NativeExportSwitch<NativeExportGranularity>
  content: NativeExportSwitch<boolean>
}

/**
 * The native export's state. `available` means the effective endpoint IS this KyberDash
 * receiver; `unavailable` names the cause; `not-configured` means nothing configures the
 * export at all — neither working nor unavailable for a reason.
 *
 * The union is exact: an organisation's pin is never `available`, and a machine variable is
 * never the `organisation-pinned` cause, so the surface cannot word a state the
 * configuration does not hold.
 *
 * Every state carries the export's SHAPE, because the endpoint does not say what the export
 * sends: a verdict without the shape would let the row read as "the native path works" when
 * the export is pinned to a shape that sends nothing KyberDash reads.
 */
export type NativeExportVerdict =
  | { state: 'available'; endpoint: string; source: NativeExportVariable; shape: NativeExportShape }
  | {
      state: 'unavailable'
      endpoint: string
      source: 'organisation'
      cause: 'organisation-pinned'
      shape: NativeExportShape
    }
  | {
      state: 'unavailable'
      endpoint: string
      source: NativeExportVariable
      cause: 'endpoint-differs'
      shape: NativeExportShape
    }
  | { state: 'not-configured'; shape: NativeExportShape }

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
 * One settings file, as the reader sees it.
 *
 * An ABSENT file is an expected state — a machine may pin nothing — so it reads as absent
 * rather than as a failure. A file that EXISTS and cannot be read is a different state:
 * it is a pin this reader cannot see, and reading it as "nothing is pinned" would report
 * the export as more open than it is.
 */
type SettingsFile =
  | { state: 'absent' }
  | { state: 'readable'; document: unknown }
  | { state: 'unreadable' }

/**
 * The parsed contents of one settings file, or the state it could not be read in.
 */
function readSettingsFile(path: string): SettingsFile {
  if (!existsSync(path)) return { state: 'absent' }
  try {
    const stat = statSync(path)
    if (!stat.isFile() || stat.size > MAX_SETTINGS_BYTES) return { state: 'unreadable' }
    return { state: 'readable', document: JSON.parse(readFileSync(path, 'utf8')) as unknown }
  } catch {
    return { state: 'unreadable' }
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

/** The `telemetry` block of a settings document, when it carries one. */
function telemetryBlock(value: unknown): Record<string, unknown> | undefined {
  return asRecord(asRecord(value)?.['telemetry'])
}

/** The `managedSettings.telemetry` block of the cached organisation settings. */
function cachedOrganisationTelemetry(value: unknown): Record<string, unknown> | undefined {
  return telemetryBlock(asRecord(value)?.['managedSettings'])
}

/** A string key of a telemetry block, when it carries one. */
function blockString(block: Record<string, unknown> | undefined, key: string): string | undefined {
  const value = block?.[key]
  return typeof value === 'string' ? value : undefined
}

/** A boolean key of a telemetry block, when it carries one. */
function blockBoolean(block: Record<string, unknown> | undefined, key: string): boolean | undefined {
  const value = block?.[key]
  return typeof value === 'boolean' ? value : undefined
}

/** The `telemetry.endpoint` of a telemetry block, as it is echoed in a status line. */
function blockEndpoint(block: Record<string, unknown> | undefined): string | undefined {
  const value = block?.['endpoint']
  if (typeof value !== 'string') return undefined
  return displayEndpoint(value)
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
 * The organisation's telemetry block, as the reader resolves it. A value the organisation
 * does not pin is absent, not a default: the default belongs to the level that did not pin
 * it.
 */
type OrganisationTelemetry = {
  /** The endpoint the organisation pins, when it pins one. */
  endpoint: string | undefined
  /** The format the organisation pins, when it pins one. */
  format: string | undefined
  /** The granularity the organisation pins, when it pins one the export can act on. */
  granularity: NativeExportGranularity | undefined
  /** The organisation pins a granularity whose value cannot be read. */
  granularityUnreadable: boolean
  /** The message-content switch the organisation pins, when it pins one. */
  logMessageContent: boolean | undefined
  /** The enabled switch the organisation pins, when it pins one. */
  enabled: boolean | undefined
  /** The organisation pins an enabled switch whose value cannot be read. */
  enabledUnreadable: boolean
}

/**
 * One of the organisation's two views of its managed settings: the `telemetry` block the
 * view carries, and whether the view's file exists and could not be read.
 */
type OrganisationView = {
  /** The `telemetry` block the view carries, when it carries a readable one. */
  block: Record<string, unknown> | undefined
  /** The view's file exists and could not be read, so its pins are invisible here. */
  unreadable: boolean
}

/**
 * One of the organisation's two views of its managed settings, read from its file. The
 * cached view under the Factory root is read first — it is the view Factory itself reads —
 * and the system-managed copy is the fallback for every key the cached view does not
 * carry.
 */
function organisationView(
  path: string,
  select: (document: unknown) => Record<string, unknown> | undefined,
): OrganisationView {
  const file = readSettingsFile(path)
  if (file.state === 'absent') return { block: undefined, unreadable: false }
  if (file.state === 'unreadable') return { block: undefined, unreadable: true }
  return { block: select(file.document), unreadable: false }
}

/**
 * Whether a telemetry block carries a key at all. A key that is present but carries a
 * value of the wrong type is present: the reader can see that the organisation pinned
 * something it cannot read, which is not the same as the organisation pinning nothing.
 */
function blockHasKey(block: Record<string, unknown> | undefined, key: string): boolean {
  return block !== undefined && block[key] !== undefined
}

/**
 * The organisation's granularity, and whether it could be read.
 *
 * The first view that carries a readable value wins. A key that is present but carries
 * anything the export cannot act on — a name that is neither `aggregate` nor `user`, a
 * value of the wrong type — and a settings file that exists and cannot be read at all, are
 * values that CANNOT BE READ: Factory's own published rule resolves those to the
 * restrictive granularity, so the reader reports them as unreadable rather than as nothing
 * pinned.
 */
function organisationGranularity(
  views: readonly OrganisationView[],
): { value: NativeExportGranularity | undefined; unreadable: boolean } {
  for (const view of views) {
    const value = blockString(view.block, 'granularity')
    if (value === 'aggregate' || value === 'user') return { value, unreadable: false }
  }
  for (const view of views) {
    if (blockHasKey(view.block, 'granularity')) return { value: undefined, unreadable: true }
  }
  if (views.some((view) => view.unreadable)) return { value: undefined, unreadable: true }
  return { value: undefined, unreadable: false }
}

/**
 * The organisation's telemetry block, resolved key by key. The endpoint resolves that way
 * too, so the verdict under an organisation's pin is unchanged by reading the shape beside
 * it.
 */
function organisationTelemetry(sources: NativeExportSources): OrganisationTelemetry {
  const cached = organisationView(
    join(sources.factoryRoot, ORGANISATION_CACHE_FILE),
    cachedOrganisationTelemetry,
  )
  const system = organisationView(
    join(systemManagedDir(sources), SYSTEM_SETTINGS_FILE),
    telemetryBlock,
  )
  const views = [cached, system]
  const granularity = organisationGranularity(views)
  const enabled = blockBoolean(cached.block, 'enabled') ?? blockBoolean(system.block, 'enabled')
  return {
    endpoint: blockEndpoint(cached.block) ?? blockEndpoint(system.block),
    format: blockString(cached.block, 'format') ?? blockString(system.block, 'format'),
    granularity: granularity.value,
    granularityUnreadable: granularity.unreadable,
    logMessageContent:
      blockBoolean(cached.block, 'logMessageContent') ??
      blockBoolean(system.block, 'logMessageContent'),
    enabled,
    enabledUnreadable:
      enabled === undefined &&
      views.some((view) => blockHasKey(view.block, 'enabled')),
  }
}

/**
 * The export's master switch.
 *
 * The organisation's `telemetry.enabled` wins outright: where it is set at all the
 * machine's `OTEL_CUSTOMER_ENABLED` is not read, so an organisation can keep the customer
 * pipeline on (or off) whatever the machine says. Where the organisation sets nothing the
 * machine's variable decides, and where the machine says nothing either the pipeline is
 * ON — Factory's own documentation makes that variable an opt-OUT, so a machine that says
 * nothing has not opted out.
 *
 * A value that cannot be read falls to off. The switch cannot be honoured when it cannot
 * be read, and off is the direction that never reports an export that is not running.
 */
function resolveEnabled(
  organisation: OrganisationTelemetry,
  processEnv: NodeJS.ProcessEnv,
): NativeExportSwitch<boolean> {
  if (organisation.enabled !== undefined) {
    return { value: organisation.enabled, source: 'organisation' }
  }
  if (organisation.enabledUnreadable) {
    return { value: false, source: 'organisation', unreadable: true }
  }
  const machine = processEnv[CUSTOMER_ENABLED_VARIABLE]
  if (machine === 'false' || machine === '0') {
    return { value: false, source: CUSTOMER_ENABLED_VARIABLE }
  }
  if (machine === 'true' || machine === '1') {
    return { value: true, source: CUSTOMER_ENABLED_VARIABLE }
  }
  if (machine !== undefined) {
    return { value: false, source: CUSTOMER_ENABLED_VARIABLE, unreadable: true }
  }
  return { value: true, source: 'default' }
}

/**
 * The format the export sends. The organisation's block wins by precedence: where it pins
 * the format the machine's variable is not read at all, and anything unrecognised resolves
 * to `legacy`.
 */
function resolveFormat(
  organisation: OrganisationTelemetry,
  processEnv: NodeJS.ProcessEnv,
): NativeExportSwitch<NativeExportFormat> {
  if (organisation.format !== undefined) {
    return { value: organisation.format === 'genai' ? 'genai' : 'legacy', source: 'organisation' }
  }
  const machine = processEnv[TELEMETRY_FORMAT_VARIABLE]
  if (machine === 'legacy' || machine === 'genai') {
    return { value: machine, source: TELEMETRY_FORMAT_VARIABLE }
  }
  return { value: 'legacy', source: 'default' }
}

/**
 * The granularity the export sends. Most-restrictive-wins, not precedence: `aggregate`
 * beats `user` whichever level each of them came from. A tie is the organisation's, because
 * that is the level that pinned it.
 *
 * A value that CANNOT BE READ resolves to `aggregate`, which is Factory's own published
 * privacy rule and the restrictive direction: a value that is present but is neither
 * `aggregate` nor `user`, a value of the wrong type, and a settings file that exists and
 * cannot be read at all. A value that was never set is a different case and takes the
 * documented default, which is `user`.
 */
function resolveGranularity(
  organisation: OrganisationTelemetry,
  processEnv: NodeJS.ProcessEnv,
): NativeExportSwitch<NativeExportGranularity> {
  const organisationValue = organisation.granularity
  const raw = processEnv[TELEMETRY_GRANULARITY_VARIABLE]
  const machineValue = raw === 'aggregate' || raw === 'user' ? raw : undefined
  if (organisation.granularityUnreadable) {
    return { value: 'aggregate', source: 'organisation', unreadable: true }
  }
  if (organisationValue === 'aggregate' || machineValue === 'aggregate') {
    return {
      value: 'aggregate',
      source: organisationValue === 'aggregate' ? 'organisation' : TELEMETRY_GRANULARITY_VARIABLE,
    }
  }
  if (organisationValue === 'user' || machineValue === 'user') {
    return {
      value: 'user',
      source: organisationValue === 'user' ? 'organisation' : TELEMETRY_GRANULARITY_VARIABLE,
    }
  }
  if (raw !== undefined) {
    return { value: 'aggregate', source: TELEMETRY_GRANULARITY_VARIABLE, unreadable: true }
  }
  return { value: 'user', source: 'default' }
}

/**
 * The message-content switch. The organisation's value wins when it is set at all, and the
 * aggregate granularity overrides every content opt-in: `aggregate` carries no
 * per-individual content, so an opt-in that would have turned content on is overridden.
 *
 * The source names where the VALUE came from, so the granularity is named only when it
 * changed the outcome: with no opt-in at all, the switch is off because the default is off,
 * and the aggregate granularity agrees with it rather than overriding it.
 */
function resolveContent(
  organisation: OrganisationTelemetry,
  processEnv: NodeJS.ProcessEnv,
  granularity: NativeExportSwitch<NativeExportGranularity>,
): NativeExportSwitch<boolean> {
  const organisationValue = organisation.logMessageContent
  if (organisationValue !== undefined) {
    if (granularity.value === 'aggregate' && organisationValue) {
      return { value: false, source: 'granularity' }
    }
    return { value: organisationValue, source: 'organisation' }
  }
  const machine = processEnv[LOG_MESSAGE_CONTENT_VARIABLE]
  if (machine === 'true' || machine === '1') {
    if (granularity.value === 'aggregate') return { value: false, source: 'granularity' }
    return { value: true, source: LOG_MESSAGE_CONTENT_VARIABLE }
  }
  if (machine === 'false' || machine === '0') {
    return { value: false, source: LOG_MESSAGE_CONTENT_VARIABLE }
  }
  return { value: false, source: 'default' }
}

/** The export's effective shape: the enabled switch, the format, the granularity and the content switch. */
function resolveShape(
  organisation: OrganisationTelemetry,
  processEnv: NodeJS.ProcessEnv,
): NativeExportShape {
  const enabled = resolveEnabled(organisation, processEnv)
  const format = resolveFormat(organisation, processEnv)
  const granularity = resolveGranularity(organisation, processEnv)
  const content = resolveContent(organisation, processEnv, granularity)
  return { enabled, format, granularity, content }
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
 * The native export's effective endpoint, state and shape.
 *
 * The organisation's pinned endpoint wins outright: when one is pinned, the machine variable
 * is not consulted at all, and the verdict is `unavailable` with the organisation named as
 * the cause — a developer whose machine variable points at this receiver must not be told
 * the native path works when the organisation's endpoint is what receives the traffic.
 *
 * The SHAPE travels with the verdict. It is resolved from the same configuration as the
 * endpoint — the organisation's managed block and the machine's variables — but it follows
 * Factory's own precedence rules, which are not one rule (see the header).
 *
 * The verdict's own vocabulary is about the ENDPOINT and stays exact: `available`,
 * `unavailable` or `not-configured`. The organisation's enabled switch does not add a
 * verdict of its own, because it does not change where the endpoint points — it decides
 * whether the export runs at all, and that travels in the shape beside the verdict, where
 * a reader looks for what the export is.
 */
export function readNativeExport(
  sources: NativeExportSources,
  options: NativeExportOptions = {},
): NativeExportVerdict {
  const processEnv = options.processEnv ?? process.env
  const organisation = organisationTelemetry(sources)
  const shape = resolveShape(organisation, processEnv)
  if (organisation.endpoint !== undefined) {
    return {
      state: 'unavailable',
      endpoint: organisation.endpoint,
      source: 'organisation',
      cause: 'organisation-pinned',
      shape,
    }
  }
  const machine = machineEndpoint(processEnv)
  if (machine === undefined) return { state: 'not-configured', shape }
  return pointsAtReceiver(machine.endpoint)
    ? { state: 'available', endpoint: machine.endpoint, source: machine.source, shape }
    : {
        state: 'unavailable',
        endpoint: machine.endpoint,
        source: machine.source,
        cause: 'endpoint-differs',
        shape,
      }
}
