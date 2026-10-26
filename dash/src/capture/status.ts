// The structured capture status (section 5 of the settings-drawer plan, D9/D11/D19).
//
// WHY it is built from the engine's own primitives (`declaredFiles`,
// `presentKeys`, `statusWarningLines`, the receipt, the revert drift check)
// and not by parsing `runCapture('status')` text or deriving state a second
// way: a second derivation drifts from the CLI, and a stdout parser breaks
// when a line is reworded. status.test.ts pins parity with the text status.
//
// Every report is read-only. It names only the keys KyberDash manages: another
// key in the owner's config — an API key sharing the same `env` object, say —
// is never read into a value that can reach a response.

import {
  declaredFiles,
  exporterResolutionFor,
  fileIdFor,
  loadReceiptLenient,
  presentKeys,
  readTextIfPresent,
  statusWarningLines,
  type ResolvedConfigFile,
} from './index.js'
import { revertBlockEdits } from './edit-block.js'
import { revertJsonEdits } from './edit-json.js'
import {
  defaultExistenceProbe,
  detectInstall,
  dirnameFor,
  guardProbe,
  type ExistenceProbe,
  type InstallDetection,
  type InstallState,
} from './detect.js'
import type { CaptureHarnessWriter, ManagedHarnessWriter } from './harnesses/registry.js'
import { CAPTURE_HARNESS_REGISTRY } from './harnesses/registry.js'
import type { PathEnv } from './paths.js'
import type { CaptureReceipt } from './receipt.js'

export type CaptureHarnessState = 'on' | 'off' | 'partial' | 'unknown' | 'not-applicable'

export type CaptureDriftEntry = { key: string; written: unknown; current: unknown }

export type CaptureFileStatus = {
  id: string
  path: string
  applicable: boolean
  /** null when the file could not be read: unknown is reported as unknown. */
  exists: boolean | null
  expectedKeys: string[]
  presentKeys: string[]
  missingKeys: string[]
  /**
   * Always-written keys that are present but hold a value other than the one
   * `enable` writes. They are why a file reads `partial` with no missing key,
   * so the check can name them instead of saying "something differs".
   */
  mismatchedKeys: string[]
  drift: CaptureDriftEntry[] | null
}

export type CaptureStatusWarning = { code: string; text: string }

export type CaptureManualStep = {
  id: string
  text: string
  env?: { name: string; value: string }
}

export type CaptureHarnessStatus = {
  id: string
  displayName: string
  mode: 'managed' | 'status-only'
  installed: InstallState
  installEvidence: string[]
  state: CaptureHarnessState
  receipt: 'recorded' | 'absent'
  files: CaptureFileStatus[]
  warnings: CaptureStatusWarning[]
  manualSteps: CaptureManualStep[]
  statusLines: string[]
}

export type StatusContext = {
  home: string
  env: PathEnv
  endpoint: string
  /** Defaults to every harness in the registry. */
  writers?: readonly CaptureHarnessWriter[]
  /** Replaces filesystem detection for every harness (a test, or a host that knows better). */
  installProbe?: (harnessId: string) => {
    installed: InstallState
    evidence: readonly string[]
  }
  existence?: ExistenceProbe
}

/**
 * Steps the user must take themselves, which no config key can express. They
 * are text plus the variable to set: the suggested value is the receiver the
 * dashboard listens on, never this server's own environment (D13) — the
 * server cannot see the shell the harness is launched from.
 */
function manualStepsFor(writer: CaptureHarnessWriter, endpoint: string): CaptureManualStep[] {
  if (writer.id === 'opencode') {
    return [
      {
        id: 'opencode-otel-endpoint',
        text:
          'OpenCode reads the OTLP endpoint from its process environment (upstream #13438), ' +
          'not from opencode.jsonc; set OTEL_EXPORTER_OTLP_ENDPOINT in the environment OpenCode is launched from',
        env: { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: endpoint },
      },
    ]
  }
  if (writer.id === 'antigravity') {
    return [
      {
        id: 'antigravity-declared-context-window',
        text:
          'Add span.set_attribute("declaredContextWindow", <positive token count>) on the statusline ' +
          "bridge's chat span; the window exists only when the user declares it",
      },
    ]
  }
  if (writer.id === 'kilo') {
    return [
      {
        id: 'kilo-folder-import',
        // No env: a folder import is a setting or a command the user runs,
        // never a variable the drawer could suggest a value for.
        text:
          'Kilo usage and messages come from kilo.db: point Maintenance → Scheduled folder import ' +
          'at the Kilo data folder, or run `kyberdash dash import-history --harness kilo-shared-runtime`',
      },
    ]
  }
  return []
}

/**
 * D19: a multi-file harness (Copilot's two VS Code editions) only touches a
 * file that exists or whose host directory exists. Enable would otherwise
 * create a VS Code Insiders profile directory on a machine without Insiders.
 * A single-file harness is always applicable. A probe that fails is treated as
 * applicable: absence was not proven, and a write would refuse anyway.
 */
function isFileApplicable(
  writer: CaptureHarnessWriter,
  path: string,
  existence: ExistenceProbe,
  platform: NodeJS.Platform,
): boolean {
  if (writer.kind !== 'managed' || (writer.configFiles?.length ?? 0) < 2) return true
  const probe = guardProbe(existence)
  if (probe(path) !== 'absent') return true
  return probe(dirnameFor(platform, path)) !== 'absent'
}

/** The `fileFilter` the capture route hands the engine so enable honors D19. */
export function createApplicabilityFilter(
  writers: readonly CaptureHarnessWriter[],
  existence: ExistenceProbe = defaultExistenceProbe,
  platform: NodeJS.Platform = process.platform,
): (file: { harnessId: string; fileId: string; path: string }) => boolean {
  return (file) => {
    const writer = writers.find((candidate) => candidate.id === file.harnessId)
    return writer === undefined ? true : isFileApplicable(writer, file.path, existence, platform)
  }
}

function isBlockFormat(file: ResolvedConfigFile): boolean {
  return file.format === 'toml' || file.format === 'yaml'
}

function toKeyList(...maps: Array<Record<string, unknown>>): string[] {
  const keys: string[] = []
  for (const map of maps) for (const key of Object.keys(map)) if (!keys.includes(key)) keys.push(key)
  return keys
}

type FileAnalysis = {
  status: CaptureFileStatus
  /** on/off/partial for an applicable, readable, parseable file; unknown otherwise. */
  state: 'on' | 'off' | 'partial' | 'unknown' | undefined
  unreadable?: { path: string; code: string }
  /**
   * The resolved-exporter failure for this file, when its harness declares a
   * resolver and the exporter it will get does not deliver. Key presence alone
   * cannot express this: Copilot's keys can be exactly right while the
   * extension still uses the dev-null file exporter.
   */
  exporterWarning?: CaptureStatusWarning
  /**
   * The line naming the exporter this file's harness will resolve, surfaced
   * even when it raises no warning. A resolver that cannot judge a factor it
   * needs (Copilot's environment override, invisible to the status route)
   * withholds the verdict here rather than inventing one.
   */
  exporterLine?: string
}

function analyseManagedFile(
  writer: ManagedHarnessWriter,
  file: ResolvedConfigFile,
  receipt: CaptureReceipt | null,
  ctx: StatusContext,
  existence: ExistenceProbe,
): FileAnalysis {
  const desired = file.desiredKeys(ctx.endpoint)
  const ensure = file.ensureIfAbsentKeys?.(ctx.endpoint) ?? {}
  const expectedKeys = toKeyList(desired, ensure)
  const read = readTextIfPresent(file.path)
  const base: CaptureFileStatus = {
    id: file.id,
    path: file.path,
    applicable: isFileApplicable(writer, file.path, existence, ctx.env.platform),
    exists: read.error !== undefined ? null : read.found,
    expectedKeys,
    presentKeys: [],
    missingKeys: [],
    mismatchedKeys: [],
    drift: null,
  }
  if (!base.applicable) return { status: base, state: undefined }
  if (read.error !== undefined) {
    return {
      status: base,
      state: 'unknown',
      unreadable: { path: file.path, code: read.code ?? 'UNKNOWN' },
    }
  }
  if (!read.found) {
    return { status: { ...base, missingKeys: [...expectedKeys] }, state: 'off' }
  }
  const content = read.content
  if (!isBlockFormat(file) && revertJsonEdits(content, {}, file.literalKeyPrefixes).refused !== undefined) {
    return { status: base, state: 'unknown' }
  }
  // The union view: the CLI text status lists only the always-written keys, the
  // structured status also reports the ensure-if-absent ones.
  const present = presentKeys({ ...file, desiredKeys: () => ({ ...desired, ...ensure }) }, content, ctx.endpoint)
  const presentList = expectedKeys.filter((key) => present[key] !== undefined)
  const missing = expectedKeys.filter((key) => present[key] === undefined)

  const same = (key: string, value: unknown): boolean =>
    isBlockFormat(file) ? String(value) === String(desired[key]) : value === desired[key]
  const mismatched = Object.keys(desired).filter(
    (key) => present[key] !== undefined && !same(key, present[key]),
  )
  const allOn =
    Object.keys(desired).every((key) => present[key] !== undefined && same(key, present[key])) &&
    Object.keys(ensure).every((key) => key in desired || present[key] !== undefined)
  const state = allOn ? 'on' : presentList.length === 0 ? 'off' : 'partial'

  const record = receipt?.files.find((entry) => entry.path === file.path)
  let drift: CaptureDriftEntry[] | null = null
  if (record !== undefined) {
    const entries = isBlockFormat(file)
      ? revertBlockEdits(content, record.keys, file.format as 'toml' | 'yaml').drift
      : revertJsonEdits(content, record.keys, file.literalKeyPrefixes).drift
    if (entries.length > 0) {
      drift = entries.map((entry) => ({
        key: entry.key,
        written: entry.written,
        current: entry.current === undefined ? null : entry.current,
      }))
    }
  }
  // A resolution may withhold a verdict it cannot make (the status route
  // cannot see the editor's environment), and it then carries no warning text:
  // a warning with nothing to say is not a warning.
  const resolution = exporterResolutionFor(file, present)
  const exporterWarning =
    resolution !== undefined && !resolution.delivering && resolution.warning !== ''
      ? { code: 'exporter-not-delivering', text: resolution.warning }
      : undefined
  return {
    status: { ...base, presentKeys: presentList, missingKeys: missing, mismatchedKeys: mismatched, drift },
    state,
    exporterLine: resolution?.line,
    exporterWarning,
  }
}

function combineStates(states: Array<'on' | 'off' | 'partial' | 'unknown' | undefined>): CaptureHarnessState {
  const applicable = states.filter((state) => state !== undefined)
  if (applicable.length === 0) return 'off'
  if (applicable.includes('unknown')) return 'unknown'
  if (applicable.every((state) => state === 'on')) return 'on'
  if (applicable.every((state) => state === 'off')) return 'off'
  return 'partial'
}

/**
 * Ensure-if-absent keys the owner had already set to something else. `enable`
 * keeps such a value by design (it is the owner's, and `disable` must not
 * touch it), so the file still counts its key as "set" — which is exactly how
 * a harness can read Enabled while exporting nowhere. Each kept value that
 * differs is named here:
 *
 * - `endpoint-differs`: the endpoint is not the receiver's. Text unchanged
 *   from the original warning; the CLI and older clients key on it.
 * - `protocol-differs`: the receiver speaks OTLP/HTTP only, so a `grpc`
 *   protocol cannot deliver even to the right host.
 * - `owner-value-differs`: any other kept value (telemetry switched off,
 *   traces exporter `none`). Compared as strings: a JSON `1` and `"1"` are
 *   the same setting to the harness.
 */
function ownerValueWarnings(
  files: readonly ResolvedConfigFile[],
  analyses: readonly FileAnalysis[],
  ctx: StatusContext,
): CaptureStatusWarning[] {
  // The endpoint leads: it is the one that decides whether anything arrives at all.
  const endpointFirst: CaptureStatusWarning[] = []
  const warnings: CaptureStatusWarning[] = []
  files.forEach((file, index) => {
    const analysis = analyses[index]!
    if (!analysis.status.applicable || analysis.status.exists !== true || isBlockFormat(file)) return
    const ensure = file.ensureIfAbsentKeys?.(ctx.endpoint) ?? {}
    const desired = file.desiredKeys(ctx.endpoint)
    const read = readTextIfPresent(file.path)
    const present = presentKeys({ ...file, desiredKeys: () => ensure }, read.content, ctx.endpoint)
    for (const [key, wanted] of Object.entries(ensure)) {
      // A key `enable` also always writes is judged by mismatchedKeys instead.
      if (key in desired || present[key] === undefined) continue
      const current = present[key]
      if (wanted === ctx.endpoint) {
        if (current !== ctx.endpoint) {
          endpointFirst.push({
            code: 'endpoint-differs',
            text: `${key} is ${String(current)}; the KyberDash receiver listens at ${ctx.endpoint}`,
          })
        }
        continue
      }
      if (String(current) === String(wanted)) continue
      if (/OTLP_PROTOCOL$/.test(key)) {
        warnings.push({
          code: 'protocol-differs',
          text: `${key} is ${String(current)}; the KyberDash receiver accepts OTLP/HTTP only (${String(wanted)})`,
        })
        continue
      }
      warnings.push({
        code: 'owner-value-differs',
        text: `${key} is ${String(current)}; KyberDash expects ${String(wanted)} (the existing value was kept on enable)`,
      })
    }
  })
  return [...endpointFirst, ...warnings]
}

function buildOne(
  writer: CaptureHarnessWriter,
  ctx: StatusContext,
  receipt: CaptureReceipt | null,
): CaptureHarnessStatus {
  const existence = ctx.existence ?? defaultExistenceProbe
  const injected = ctx.installProbe?.(writer.id)
  let detection: InstallDetection = injected
    ? { installed: injected.installed, evidence: [...injected.evidence] }
    : detectInstall(writer, ctx.home, ctx.env, existence)

  if (writer.kind === 'pending' && writer.filePlugin !== undefined) {
    // A file plugin is switchable from Settings, so it reports as managed: the one
    // "file" is the plugin and no config key is declared, written or drift-checked.
    const plugin = writer.filePlugin.state(ctx.home, ctx.env)
    const lines = (writer.describeStatus?.(ctx.home, { redact: true, env: ctx.env }) ?? [])
      .map((line) => line.trim())
      .filter((line) => line !== '')
    return {
      id: writer.id,
      displayName: writer.displayName,
      mode: 'managed',
      installed: detection.installed,
      installEvidence: [...detection.evidence],
      state: plugin.state,
      receipt: plugin.receipt ? 'recorded' : 'absent',
      files: [
        {
          id: 'plugin',
          path: writer.filePlugin.path(ctx.home, ctx.env),
          applicable: true,
          exists: plugin.exists,
          expectedKeys: [],
          presentKeys: [],
          missingKeys: [],
          mismatchedKeys: [],
          drift: null,
        },
      ],
      warnings: [],
      manualSteps: manualStepsFor(writer, ctx.endpoint),
      statusLines: lines,
    }
  }

  if (writer.kind === 'pending') {
    const path = writer.resolvePath(ctx.home, ctx.env)
    const read = readTextIfPresent(path)
    const described = writer.describeStatus?.(ctx.home, { redact: true, env: ctx.env })
    const lines = (described ?? []).map((line) => line.trim()).filter((line) => line !== '')
    return {
      id: writer.id,
      displayName: writer.displayName,
      mode: 'status-only',
      installed: detection.installed,
      installEvidence: [...detection.evidence],
      state: 'not-applicable',
      receipt: receipt?.files.some((entry) => entry.harnessId === writer.id) ? 'recorded' : 'absent',
      files: [
        {
          id: fileIdFor(writer.id),
          path,
          applicable: false,
          exists: read.error !== undefined ? null : read.found,
          expectedKeys: [],
          presentKeys: [],
          missingKeys: [],
          mismatchedKeys: [],
          drift: null,
        },
      ],
      warnings: [],
      manualSteps: manualStepsFor(writer, ctx.endpoint),
      statusLines: lines.length > 0 ? lines : [writer.reason],
    }
  }

  const files = declaredFiles(writer, ctx.home, ctx.env)
  const analyses = files.map((file) => analyseManagedFile(writer, file, receipt, ctx, existence))

  // A config we cannot read means we cannot honestly say the harness can be
  // switched: install evidence from the directory alone is downgraded to
  // unknown, with the failed inspection named first. An injected probe is the
  // caller's own judgement and is left alone.
  if (injected === undefined && detection.installed !== 'unknown') {
    const unreadable = analyses.find((analysis) => analysis.unreadable !== undefined)?.unreadable
    if (unreadable !== undefined) {
      detection = {
        installed: 'unknown',
        evidence: [`could not inspect ${unreadable.path} (${unreadable.code})`, ...detection.evidence],
      }
    }
  }

  const paths = files.map((file) => file.path)
  const recorded =
    receipt?.files.some((entry) => paths.includes(entry.path) || entry.harnessId === writer.id) ?? false
  const configWarnings: CaptureStatusWarning[] = statusWarningLines(writer, ctx.home, ctx.env).map(
    (line) => ({ code: 'config-warning', text: line.replace(/^\s*warn: /, '') }),
  )
  // Both editions can resolve the same failure; the report names it once.
  const exporterWarnings = analyses
    .flatMap((analysis) => (analysis.exporterWarning === undefined ? [] : [analysis.exporterWarning]))
    .filter(
      (warning, index, all) => all.findIndex((other) => other.text === warning.text) === index,
    )
  // The resolved exporter belongs on the line even when it raises no warning:
  // a resolver that withholds a verdict it cannot make (the status route
  // cannot see the editor's environment) still has to say what it resolved, or
  // the dashboard would show nothing about the exporter at all.
  const exporterLines = analyses
    .flatMap((analysis) => (analysis.exporterLine === undefined ? [] : [analysis.exporterLine]))
    .filter((line, index, all) => all.indexOf(line) === index)
  return {
    id: writer.id,
    displayName: writer.displayName,
    mode: 'managed',
    installed: detection.installed,
    installEvidence: [...detection.evidence],
    state: combineStates(analyses.map((analysis) => analysis.state)),
    receipt: recorded ? 'recorded' : 'absent',
    files: analyses.map((analysis) => analysis.status),
    warnings: [...ownerValueWarnings(files, analyses, ctx), ...configWarnings, ...exporterWarnings],
    manualSteps: manualStepsFor(writer, ctx.endpoint),
    // The plugin and consent state are decided here, in the engine; the drawer only renders them.
    statusLines: [...exporterLines, ...(writer.filePlugin?.describeStatus(ctx.home, ctx.env) ?? [])],
  }
}

/** One harness's structured status. */
export function buildHarnessStatus(
  writer: CaptureHarnessWriter,
  ctx: StatusContext,
): CaptureHarnessStatus {
  return buildOne(writer, ctx, loadReceiptLenient(ctx.home))
}

/** Every harness's structured status, in registry order. */
export function buildCaptureStatus(ctx: StatusContext): CaptureHarnessStatus[] {
  const receipt = loadReceiptLenient(ctx.home)
  const writers = ctx.writers ?? CAPTURE_HARNESS_REGISTRY
  return writers.map((writer) => buildOne(writer, ctx, receipt))
}
