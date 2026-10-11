// Harness-agnostic capture command core for D3, D9 and D11 (T8).
//
// `kyberdash kyber capture status | enable | disable` points the six D6
// harnesses' OTLP exporters at the KyberDash receiver. Only OTLP/HTTP
// endpoints may be written. macOS, Linux and Windows are all supported:
// since D14 every path resolves through the platform-aware seam in paths.ts,
// so Windows runs in-process with no script shipped (ADR 0031 amendment).
// Every config path resolves from the HOME passed at call time — never at
// module load — so tests run entirely under a temporary HOME.
//
// A managed declaration may name several config files. JSON and JSONC values
// keep their JSON types; TOML and YAML managed blocks stay strings. An
// ensure-if-absent key shares that file with the always-written keys: the
// core reads the document first, inserts the key only when it is absent, and
// leaves a present one out of the receipt. The harness stays a declaration,
// not a second editor. Status can report declared warnings and a process-env
// snippet without writing either. The registry is mixed: managed writers are
// written on enable and restored on disable, while pending ones (codex,
// antigravity) report their own reason and are never written. Callers (and
// tests) may inject managed writers;
// `enable`/`disable`/`status` treat both kinds uniformly.

import { mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname } from 'node:path'

import { applyBlockEdits, readBlockKeys, revertBlockEdits } from './edit-block.js'
import { applyJsonEdits, readJsonKeys, revertJsonEdits, type JsonScalar } from './edit-json.js'
import {
  probeReceiver,
  RECEIVER_DOWN_REMEDY,
  type HealthFetcher,
} from './probe.js'
import {
  loadCaptureReceipt,
  receiptPathForHome,
  saveCaptureReceipt,
  sha256Hex,
  type CaptureReceipt,
  type ReceiptFileRecord,
} from './receipt.js'
import {
  CAPTURE_HARNESS_REGISTRY,
  SUPPORTED_CAPTURE_HARNESS_IDS,
  type CaptureFileFormat,
  type CaptureHarnessWriter,
  type ManagedHarnessWriter,
} from './harnesses/registry.js'
import { classifyFsError, type FsErrorReason } from './errno.js'
import type { PathEnv } from './paths.js'

export { SUPPORTED_CAPTURE_HARNESS_IDS }
export type { CaptureHarnessWriter }

/** The default OTLP/HTTP receiver origin. */
export const DEFAULT_CAPTURE_ENDPOINT = 'http://127.0.0.1:4318'

export type CaptureAction = 'status' | 'enable' | 'disable'

export type CaptureRunOptions = {
  /** Harness ids to act on; defaults to the six D6 registry harnesses. */
  harnessIds?: string[]
  /** OTLP receiver origin. Only OTLP/HTTP (http/https) may be written. */
  endpoint?: string
  /** Print the exact per-file change and write nothing. */
  dryRun?: boolean
  /** HOME to resolve every path from; defaults to the real one at call time. */
  homeDir?: string
  /** Platform override; defaults to `process.platform` at call time. */
  platform?: NodeJS.Platform
  /**
   * Windows application-data root for the VS Code settings seam (D14).
   * Injected so tests point win32 resolution at a temp directory instead of
   * the real APPDATA; a real run defaults to APPDATA, then
   * `<home>\AppData\Roaming`. Ignored on every other platform.
   */
  appDataDir?: string
  /** Injectable health fetch so tests pin liveness without a socket. */
  fetchHealth?: HealthFetcher
  /** Writer set override; defaults to the pending-discovery registry. */
  writers?: CaptureHarnessWriter[]
  /** Deadline for the receiver liveness probe; defaults to the probe's own 5s. */
  probeTimeoutMs?: number
  /**
   * Decides, per declared file, whether this call touches it (D19). The CLI
   * passes none and so keeps writing every declared file; the settings route
   * passes one so enable never creates a VS Code Insiders profile directory on
   * a machine without Insiders. On disable it only silences the "no record"
   * noise for a file that was never receipted: a receipted file is always
   * restored, so a profile removed since enable cannot strand its keys.
   */
  fileFilter?: CaptureFileFilter
}

export type CaptureFileFilter = (file: {
  harnessId: string
  fileId: string
  path: string
}) => boolean

/** What happened to one key, for a preview or a report. `before` is absent when the key was. */
export type CaptureEventChange = { key: string; before?: unknown; after?: unknown }

export type CaptureEventKind =
  | 'would-write'
  | 'wrote'
  | 'noop'
  | 'would-restore'
  | 'restored'
  | 'refused'
  | 'error'

/**
 * One structured per-file outcome, built beside the CLI text and never parsed
 * from it (D11). A host error is reduced to its errno `code`: the message of a
 * Node fs error names the owner's absolute paths and must not cross a wire.
 */
export type CaptureEvent = {
  kind: CaptureEventKind
  harness: string
  fileId: string
  path: string
  changes?: CaptureEventChange[]
  reason?: FsErrorReason
  drift?: Array<{ key: string; written: unknown; current: unknown }>
  code?: string
}

export type CaptureRunResult = {
  exitCode: number
  stdout: string
  stderr: string
  /** Additive structured outcomes; the text above is unchanged by their presence. */
  events: CaptureEvent[]
}

/** Usage errors exit 2; operational refusals (drift, conflict) exit 1. */
const USAGE_EXIT = 2
const REFUSAL_EXIT = 1

function isOtlpHttpEndpoint(endpoint: string): boolean {
  if (!/^https?:\/\//i.test(endpoint)) return false
  try {
    const url = new URL(endpoint)
    return url.protocol === 'http:' || url.protocol === 'https:'
  } catch {
    return false
  }
}

export function readTextIfPresent(path: string): {
  found: boolean
  content: string
  error?: string
  /** The errno code of a failed read, when the host supplied one. */
  code?: string
  /** The thrown value of a failed read, for callers that classify it; never serialized. */
  thrown?: unknown
} {
  // One read, not exists-then-read: the check and the bytes are the same
  // syscall, so a path that vanishes between them cannot throw a stack out
  // of the command. Other failures become a message the caller prints.
  try {
    return { found: true, content: readFileSync(path, 'utf8') }
  } catch (error) {
    const code =
      error !== null && typeof error === 'object' && 'code' in error
        ? (error as { code?: unknown }).code
        : undefined
    if (code === 'ENOENT') return { found: false, content: '' }
    const message = error instanceof Error ? error.message : String(error)
    return {
      found: false,
      content: '',
      error: message,
      ...(typeof code === 'string' ? { code } : {}),
      thrown: error,
    }
  }
}

function writeText(path: string, content: string): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
}

/** One config file resolved for this call. Paths are not resolved at load. */
export type ResolvedConfigFile = {
  /** Stable file id the structured status and events name this file by. */
  id: string
  path: string
  format: CaptureFileFormat
  desiredKeys: (endpoint: string) => Record<string, JsonScalar>
  ensureIfAbsentKeys?: (endpoint: string) => Record<string, JsonScalar>
}

/**
 * The stable id of a harness's single config file. Multi-file writers carry
 * their own `fileId`s; the single-file and status-only writers predate the
 * field, so the ids the structured status and events use live here once.
 */
const SINGLE_FILE_IDS: Readonly<Record<string, string>> = {
  'claude-code': 'claude-settings',
  opencode: 'opencode-config',
  codex: 'codex-config',
  pi: 'pi-agent-settings',
  antigravity: 'antigravity-settings',
}

export function fileIdFor(harnessId: string): string {
  return SINGLE_FILE_IDS[harnessId] ?? `${harnessId}-config`
}

/**
 * Files this declaration owns. `configFiles` wins when it is set: the
 * top-level `desiredKeys` on a multi-file writer is the legacy string
 * surface, and using it would turn boolean `true` into the string `"true"`
 * and would skip every file after the first.
 */
export function declaredFiles(
  writer: ManagedHarnessWriter,
  home: string,
  env: PathEnv,
): ResolvedConfigFile[] {
  if (writer.configFiles !== undefined && writer.configFiles.length > 0) {
    return writer.configFiles.map((file) => ({
      id: file.fileId,
      path: file.resolvePath(home, env),
      format: file.format,
      desiredKeys: file.desiredKeys,
      ensureIfAbsentKeys: file.ensureIfAbsentKeys,
    }))
  }
  return [
    {
      id: fileIdFor(writer.id),
      path: writer.resolvePath(home, env),
      format: writer.format,
      desiredKeys: writer.desiredKeys,
      ensureIfAbsentKeys: writer.ensureIfAbsentKeys,
    },
  ]
}

function jsonDesiredMatches(
  current: { present: boolean; value?: unknown } | undefined,
  desired: JsonScalar,
): boolean {
  return current?.present === true && current.value === desired
}

function isJsonScalar(value: unknown): value is JsonScalar {
  return (
    value === null ||
    typeof value === 'string' ||
    typeof value === 'boolean' ||
    typeof value === 'number'
  )
}

/**
 * Always-written keys plus ensure-if-absent keys the document does not
 * already contain. Presence comes from the file just read: a present ensure
 * key keeps the owner's value and stays out of this map, so the existing
 * JSON editor never replaces it and the receipt never records it. Disable
 * restores every receipted key; recording an owner key would put the
 * candidate back or call the owner's value drift. A name that is in both
 * maps is always-written.
 */
function jsonWritesAfterRead(
  content: string,
  always: Record<string, JsonScalar>,
  ensure: Record<string, JsonScalar>,
): Record<string, JsonScalar> {
  const writes: Record<string, JsonScalar> = { ...always }
  const candidates: string[] = []
  for (const key of Object.keys(ensure)) {
    if (!(key in always)) candidates.push(key)
  }
  if (candidates.length === 0) return writes
  const found = readJsonKeys(content, candidates)
  for (const key of candidates) {
    if (!found[key]?.present) writes[key] = ensure[key]!
  }
  return writes
}

/**
 * TOML and YAML blocks store strings. A string is kept as itself so the
 * block renderer does not quote it twice; any other JSON scalar is coerced.
 */
function stringsForBlock(desired: Record<string, JsonScalar>): Record<string, string> {
  const out: Record<string, string> = {}
  for (const [key, value] of Object.entries(desired)) {
    out[key] = typeof value === 'string' ? value : String(value)
  }
  return out
}

/**
 * Run one capture action. All output is returned (the CLI writes it); the
 * exit code follows the command contract: 0 on success, 2 on usage errors,
 * non-zero on drift or conflict refusals.
 */
export async function runCapture(
  action: CaptureAction,
  options: CaptureRunOptions = {},
): Promise<CaptureRunResult> {
  // Resolved at call time, never at module load, so a temporary HOME stays
  // temporary for the whole call.
  const home = options.homeDir ?? homedir()
  const platform = options.platform ?? process.platform
  // D14 (ADR 0031 amendment): Windows is supported in-process — every harness
  // resolves its config through the platform-aware seam in paths.ts, and no
  // script is shipped — so the win32 refusal that used to stand here is gone.
  // Status stays read-only: a win32 platform injected on another host changes
  // only the strings it prints, never the filesystem.
  const pathEnv: PathEnv = { platform, appData: options.appDataDir }
  const out: string[] = []
  const err: string[] = []

  const endpoint = options.endpoint ?? DEFAULT_CAPTURE_ENDPOINT
  if (!isOtlpHttpEndpoint(endpoint)) {
    err.push(
      `kyberdash capture: --endpoint must be an OTLP/HTTP URL (http:// or https://); got "${endpoint}". ` +
        'Only OTLP/HTTP protocols may be written.',
    )
    return { exitCode: USAGE_EXIT, stdout: '', stderr: `${err.join('\n')}\n`, events: [] }
  }

  const writers = options.writers ?? [...CAPTURE_HARNESS_REGISTRY]
  const requested =
    options.harnessIds !== undefined && options.harnessIds.length > 0
      ? options.harnessIds
      : writers.map((writer) => writer.id)
  for (const id of requested) {
    if (!writers.some((writer) => writer.id === id)) {
      err.push(
        `kyberdash capture: unknown harness "${id}". ` +
          `Expected one of: ${writers.map((writer) => writer.id).join(', ')}.`,
      )
      return { exitCode: USAGE_EXIT, stdout: '', stderr: `${err.join('\n')}\n`, events: [] }
    }
  }
  const selected = requested
    .map((id) => writers.find((writer) => writer.id === id))
    .filter((writer): writer is CaptureHarnessWriter => writer !== undefined)

  const probe = await probeReceiver(endpoint, options.fetchHealth, options.probeTimeoutMs)

  if (action === 'status') {
    out.push(...statusLines(selected, home, pathEnv, endpoint))
    out.push(`receiver: ${probe.state}`, `  ${probe.detail}`)
    if (probe.state === 'none') out.push(RECEIVER_DOWN_REMEDY)
    return { exitCode: 0, stdout: `${out.join('\n')}\n`, stderr: '', events: [] }
  }

  if (action === 'enable') {
    return runEnable(
      selected,
      home,
      pathEnv,
      endpoint,
      probe.state,
      probe.detail,
      options.dryRun ?? false,
      options.fileFilter,
    )
  }

  return runDisable(selected, home, pathEnv, options.dryRun ?? false, options.fileFilter)
}

function statusLines(
  writers: readonly CaptureHarnessWriter[],
  home: string,
  env: PathEnv,
  endpoint: string,
): string[] {
  const lines: string[] = []
  const receipt = loadReceiptLenient(home)
  for (const writer of writers) {
    lines.push(`harness ${writer.id} (${writer.displayName})`)
    if (writer.kind === 'pending') {
      const path = writer.resolvePath(home, env)
      const read = readTextIfPresent(path)
      lines.push(
        read.error !== undefined
          ? `  config: ${path} (unreadable: ${read.error})`
          : `  config: ${path} (${read.found ? 'found' : 'missing'})`,
      )
      const described = writer.describeStatus?.(home)
      if (described !== undefined && described.length > 0) {
        // Replaces the static reason. Pi returns lines that are already
        // indented; Antigravity returns bare lines, which get the same
        // two-space prefix as the rest of status. enable and disable never
        // call this, so a status-only route cannot grow a receipt.
        for (const line of described) {
          lines.push(line.startsWith('  ') ? line : `  ${line}`)
        }
      } else {
        lines.push(`  ${writer.reason}`)
      }
      continue
    }
    for (const file of declaredFiles(writer, home, env)) {
      appendFileStatus(lines, file, endpoint, receipt)
    }
    // Warnings and the env snippet are status text only. They are not keys
    // `enable` writes, so they never enter the receipt.
    lines.push(...statusWarningLines(writer, home, env))
    lines.push(...statusEnvSnippetLines(writer))
  }
  return lines
}

function appendFileStatus(
  lines: string[],
  file: ResolvedConfigFile,
  endpoint: string,
  receipt: CaptureReceipt | null,
): void {
  const path = file.path
  const { found, content, error } = readTextIfPresent(path)
  if (error !== undefined) {
    lines.push(`  config: ${path} (unreadable: ${error})`)
    return
  }
  lines.push(`  config: ${path} (${found ? 'found' : 'missing'})`)
  const desired = file.desiredKeys(endpoint)
  const keys = Object.keys(desired)
  if (!found) {
    lines.push(`  expected keys: ${keys.join(', ')}`)
    lines.push('  present keys: none (config missing)')
    return
  }
  const present = presentKeys(file, content, endpoint)
  const missing = keys.filter((key) => present[key] === undefined)
  const unexpected = Object.keys(present).filter(
    (key) => !keys.includes(key) && key.startsWith('otel.'),
  )
  lines.push(`  expected keys: ${keys.join(', ')}`)
  lines.push(
    missing.length === 0
      ? `  present keys: ${keys.join(', ')}`
      : `  present keys: ${keys.filter((key) => !missing.includes(key)).join(', ') || 'none'} (missing: ${missing.join(', ')})`,
  )
  if (unexpected.length > 0) lines.push(`  extra otel keys: ${unexpected.join(', ')}`)
  const record = receipt?.files.find((entry) => entry.path === path)
  if (record === undefined) {
    lines.push('  receipt: no record')
    return
  }
  const currentSha = sha256Hex(content)
  if (currentSha !== record.postSha256) {
    lines.push(`  drift: file changed since enable (expected ${record.postSha256}, found ${currentSha})`)
    for (const [key, entry] of Object.entries(record.keys)) {
      const value = present[key]
      if (value === undefined || value !== entry.written) {
        lines.push(
          `  drift: key "${key}": written "${String(entry.written)}", current "${value === undefined ? '(absent)' : String(value)}"`,
        )
      }
    }
  } else {
    lines.push('  drift: none')
  }
}

export function presentKeys(
  file: ResolvedConfigFile,
  content: string,
  endpoint: string,
): Record<string, unknown> {
  if (file.format === 'json' || file.format === 'jsonc') {
    const keys = Object.keys(file.desiredKeys(endpoint))
    const found = readJsonKeys(content, keys)
    const result: Record<string, unknown> = {}
    for (const [key, prior] of Object.entries(found)) {
      if (prior.present) result[key] = prior.value
    }
    return result
  }
  return readBlockKeys(content)
}

export function statusWarningLines(writer: ManagedHarnessWriter, home: string, env: PathEnv): string[] {
  const warnings = writer.statusWarnings
  if (warnings === undefined || warnings.length === 0) return []
  const files = writer.configFiles ?? []
  const lines: string[] = []
  for (const warning of warnings) {
    const file = files.find((candidate) => candidate.fileId === warning.fileId)
    if (file === undefined) continue
    const { found, content, error } = readTextIfPresent(file.resolvePath(home, env))
    if (error !== undefined || !found) continue
    const observed = observedConfigValue(file.format, content, warning.key)
    if (observed.present && observed.value === warning.whenValue) {
      lines.push(`  warn: ${warning.warnText}`)
    }
  }
  return lines
}

/** The live value of a warning key. JSON keeps its type; block values are strings. */
function observedConfigValue(
  format: CaptureFileFormat,
  content: string,
  key: string,
): { present: boolean; value?: unknown } {
  if (format === 'json' || format === 'jsonc') {
    return readJsonKeys(content, [key])[key] ?? { present: false }
  }
  const entries = readBlockKeys(content)
  if (!(key in entries)) return { present: false }
  return { present: true, value: entries[key] }
}

function statusEnvSnippetLines(writer: ManagedHarnessWriter): string[] {
  const snippet = writer.statusEnvSnippet
  if (snippet === undefined) return []
  // process.env is the capture command's own environment. Reading a shell
  // rc here would both touch the user's files and report values this
  // process was not actually started with.
  const lines = [`  ${snippet.label}:`]
  for (const key of snippet.envKeys) {
    const value = process.env[key]
    lines.push(`    ${key}=${value ?? ''}`)
  }
  return lines
}

function eventFor(
  kind: CaptureEventKind,
  writer: { id: string },
  file: { id: string; path: string },
  extra: Partial<Pick<CaptureEvent, 'changes' | 'reason' | 'drift' | 'code'>> = {},
): CaptureEvent {
  return { kind, harness: writer.id, fileId: file.id, path: file.path, ...extra }
}

/** A refused event for a failed read: the errno code and a stable reason, never the message. */
function readRefusal(
  writer: { id: string },
  file: { id: string; path: string },
  thrown: unknown,
): CaptureEvent {
  const { code, reason } = classifyFsError(thrown)
  return eventFor('refused', writer, file, { reason, code })
}

function sameValue(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b)
}

/** Only the keys that would actually change: a key already at its desired value is no change. */
function changesFor(
  desired: Record<string, JsonScalar>,
  prior: Record<string, { present: boolean; value?: unknown }>,
): CaptureEventChange[] {
  const changes: CaptureEventChange[] = []
  for (const [key, after] of Object.entries(desired)) {
    const previous = prior[key]
    if (previous?.present === true && sameValue(previous.value, after)) continue
    changes.push(
      previous?.present === true ? { key, before: previous.value, after } : { key, after },
    )
  }
  return changes
}

type EnableWrite = {
  desired: Record<string, JsonScalar>
  prior: Record<string, { present: boolean; value?: unknown }>
  preSha: string
  postSha: string
}

type EnableFileResult = {
  exitCode: number
  out: string[]
  err: string[]
  events: CaptureEvent[]
  record?: EnableWrite
}

function enableFile(
  writer: ManagedHarnessWriter,
  file: ResolvedConfigFile,
  endpoint: string,
  dryRun: boolean,
): EnableFileResult {
  const path = file.path
  const desired = file.desiredKeys(endpoint)
  const read = readTextIfPresent(path)
  const { found, content, error } = read
  const out: string[] = []
  const err: string[] = []
  const events: CaptureEvent[] = []
  if (error !== undefined) {
    err.push(`harness ${writer.id}: cannot read ${path}: ${error}`)
    events.push(readRefusal(writer, file, read.thrown))
    return { exitCode: REFUSAL_EXIT, out, err, events }
  }

  if (file.format === 'json' || file.format === 'jsonc') {
    // Absence is decided from the bytes just read. Present ensure keys are
    // omitted here so applyJsonEdits — the only JSON editor — never sees them.
    const desiredWrites = jsonWritesAfterRead(
      content,
      desired,
      file.ensureIfAbsentKeys?.(endpoint) ?? {},
    )
    const current = readJsonKeys(content, Object.keys(desiredWrites))
    const upToDate = Object.entries(desiredWrites).every(([key, value]) =>
      jsonDesiredMatches(current[key], value),
    )
    if (upToDate) {
      out.push(`harness ${writer.id}: already enabled (no-op): ${path}`)
      events.push(eventFor('noop', writer, file))
      return { exitCode: 0, out, err, events }
    }
    const applied = applyJsonEdits(content, desiredWrites)
    if (applied.refused !== undefined) {
      err.push(`harness ${writer.id}: ${path}: ${applied.refused}`)
      events.push(eventFor('refused', writer, file, { reason: 'unparseable' }))
      return { exitCode: REFUSAL_EXIT, out, err, events }
    }
    if (dryRun) {
      out.push(...dryRunLines(path, desiredWrites, applied.prior))
      events.push(
        eventFor('would-write', writer, file, { changes: changesFor(desiredWrites, applied.prior) }),
      )
      return { exitCode: 0, out, err, events }
    }
    const preSha = sha256Hex(content)
    writeText(path, applied.content)
    out.push(`harness ${writer.id}: enabled: ${path} (${found ? 'updated' : 'created'})`)
    events.push(eventFor('wrote', writer, file, { changes: changesFor(desiredWrites, applied.prior) }))
    return {
      exitCode: 0,
      out,
      err,
      events,
      record: {
        desired: desiredWrites,
        prior: applied.prior,
        preSha,
        postSha: sha256Hex(applied.content),
      },
    }
  }

  const blockDesired = stringsForBlock(desired)
  const applied = applyBlockEdits(content, blockDesired, file.format)
  if (applied.conflict !== undefined) {
    err.push(
      `harness ${writer.id}: refusing ${path}: conflicting key "${applied.conflict.key}" outside the managed block`,
    )
    err.push(applied.conflict.snippet)
    events.push(eventFor('refused', writer, file, { reason: 'conflict' }))
    return { exitCode: REFUSAL_EXIT, out, err, events }
  }
  if (!applied.changed) {
    out.push(`harness ${writer.id}: already enabled (no-op): ${path}`)
    events.push(eventFor('noop', writer, file))
    return { exitCode: 0, out, err, events }
  }
  if (dryRun) {
    out.push(...dryRunLines(path, blockDesired, applied.prior))
    events.push(
      eventFor('would-write', writer, file, { changes: changesFor(blockDesired, applied.prior) }),
    )
    return { exitCode: 0, out, err, events }
  }
  const preSha = sha256Hex(content)
  writeText(path, applied.content)
  out.push(`harness ${writer.id}: enabled: ${path} (${found ? 'updated' : 'created'})`)
  events.push(eventFor('wrote', writer, file, { changes: changesFor(blockDesired, applied.prior) }))
  return {
    exitCode: 0,
    out,
    err,
    events,
    record: {
      desired: blockDesired,
      prior: applied.prior,
      preSha,
      postSha: sha256Hex(applied.content),
    },
  }
}

function dryRunLines(
  path: string,
  desired: Record<string, JsonScalar>,
  prior: Record<string, { present: boolean; value?: unknown }>,
): string[] {
  const lines = [`dry-run: ${path} would write ${Object.keys(desired).join(', ')}`]
  for (const [key, value] of Object.entries(desired)) {
    const previous = prior[key]!
    lines.push(
      `  ${key}: ${previous.present ? JSON.stringify(previous.value) : '(absent)'} -> ${JSON.stringify(value)}`,
    )
  }
  return lines
}

async function runEnable(
  writers: readonly CaptureHarnessWriter[],
  home: string,
  env: PathEnv,
  endpoint: string,
  probeState: string,
  probeDetail: string,
  dryRun: boolean,
  fileFilter: CaptureFileFilter | undefined,
): Promise<CaptureRunResult> {
  const out: string[] = []
  const err: string[] = []
  const events: CaptureEvent[] = []
  let exitCode = 0
  const loaded = loadReceiptState(home)
  if (loaded.corrupt) {
    // Replacing an unreadable receipt would lose the prior values `disable`
    // restores from. Refuse and leave it for the owner to inspect.
    const harness = writers[0]?.id ?? ''
    err.push(`kyberdash capture: refusing to enable: the capture receipt is unparseable (${receiptPathForHome(home)})`)
    return {
      exitCode: REFUSAL_EXIT,
      stdout: '',
      stderr: `${err.join('\n')}\n`,
      events: [{ kind: 'refused', harness, fileId: 'receipt', path: receiptPathForHome(home), reason: 'unparseable' }],
    }
  }
  const receipt = loaded.receipt ?? { version: 1 as const, files: [] }
  let wroteAny = false

  try {
    for (const writer of writers) {
      if (writer.kind === 'pending') {
        out.push(`harness ${writer.id}: ${writer.reason} (pending discovery); skipped`)
        continue
      }
      for (const file of declaredFiles(writer, home, env)) {
        if (fileFilter?.({ harnessId: writer.id, fileId: file.id, path: file.path }) === false) continue
        const enabled = enableFile(writer, file, endpoint, dryRun)
        out.push(...enabled.out)
        err.push(...enabled.err)
        events.push(...enabled.events)
        if (enabled.exitCode !== 0) exitCode = enabled.exitCode
        if (enabled.record === undefined) continue
        const existing = receipt.files.find((entry) => entry.path === file.path)
        const ensure =
          file.format === 'json' || file.format === 'jsonc'
            ? (file.ensureIfAbsentKeys?.(endpoint) ?? {})
            : {}
        upsertReceipt(
          receipt,
          file.path,
          file.format,
          writer.id,
          retainWrittenEnsureKeys(enabled.record.desired, ensure, existing),
          enabled.record.prior,
          enabled.record.preSha,
          enabled.record.postSha,
        )
        wroteAny = true
      }
    }

  } finally {
    // A write that throws on a later file must not strand earlier files without a record.
    if (wroteAny && !dryRun) saveCaptureReceipt(home, receipt)
  }

  out.push(`receiver: ${probeState}`)
  out.push(`  ${probeDetail}`)
  if (probeState === 'none') out.push(RECEIVER_DOWN_REMEDY)
  return {
    exitCode,
    stdout: out.length > 0 ? `${out.join('\n')}\n` : '',
    stderr: err.length > 0 ? `${err.join('\n')}\n` : '',
    events,
  }
}

/**
 * A later enable omits an ensure key that is already present, but disable
 * still has to remove the copy an earlier enable inserted. Rebuilding the
 * receipt from only this write would forget that key. Always-written keys
 * are already in `written`. Writers with no ensure map are unchanged.
 */
function retainWrittenEnsureKeys(
  written: Record<string, JsonScalar>,
  ensure: Record<string, JsonScalar>,
  existing: ReceiptFileRecord | undefined,
): Record<string, JsonScalar> {
  if (existing === undefined || Object.keys(ensure).length === 0) return written
  const merged: Record<string, JsonScalar> = { ...written }
  for (const key of Object.keys(ensure)) {
    if (key in merged) continue
    const recorded = existing.keys[key]
    if (recorded === undefined || !isJsonScalar(recorded.written)) continue
    merged[key] = recorded.written
  }
  return merged
}

function upsertReceipt(
  receipt: CaptureReceipt,
  path: string,
  format: string,
  harnessId: string,
  desired: Record<string, JsonScalar>,
  prior: Record<string, { present: boolean; value?: unknown }>,
  preSha: string,
  postSha: string,
): void {
  const existing = receipt.files.find((file) => file.path === path)
  const keys: ReceiptFileRecord['keys'] = {}
  for (const [key, value] of Object.entries(desired)) {
    // A re-enable keeps the original prior: the receipt remembers what was
    // there before KyberDash ever wrote, not the intermediate value.
    const originalPrior = existing?.keys[key]?.prior ?? prior[key]!
    keys[key] = { written: value, prior: originalPrior }
  }
  const record: ReceiptFileRecord = {
    path,
    format,
    harnessId,
    keys,
    // The pre-edit digest is the digest before KyberDash ever wrote; a
    // re-enable keeps the first one so `status` compares against the true
    // baseline.
    preSha256: existing?.preSha256 ?? preSha,
    postSha256: postSha,
  }
  if (existing === undefined) receipt.files.push(record)
  else receipt.files[receipt.files.indexOf(existing)] = record
}

/** The `current` of a drift entry on the wire: an absent key is null, never `undefined`. */
function driftEntries(
  drift: ReadonlyArray<{ key: string; written: unknown; current: unknown }>,
): Array<{ key: string; written: unknown; current: unknown }> {
  return drift.map((entry) => ({
    key: entry.key,
    written: entry.written,
    current: entry.current === undefined ? null : entry.current,
  }))
}

function driftLinesFor(
  writer: ManagedHarnessWriter,
  files: readonly ResolvedConfigFile[],
  receipt: CaptureReceipt | null,
): { lines: string[]; events: CaptureEvent[] } {
  const lines: string[] = []
  const events: CaptureEvent[] = []
  for (const file of files) {
    const record = receipt?.files.find((entry) => entry.path === file.path)
    if (record === undefined) continue
    const read = readTextIfPresent(file.path)
    if (read.error !== undefined) {
      lines.push(
        `harness ${writer.id}: cannot read ${file.path}: ${read.error}; leaving file unchanged`,
      )
      events.push(readRefusal(writer, file, read.thrown))
      continue
    }
    if (!read.found) continue
    const content = read.content
    if (file.format === 'json' || file.format === 'jsonc') {
      const reverted = revertJsonEdits(content, record.keys)
      if (reverted.refused !== undefined) {
        lines.push(
          `harness ${writer.id}: ${file.path}: ${reverted.refused}; leaving file unchanged`,
        )
        events.push(eventFor('refused', writer, file, { reason: 'unparseable' }))
        continue
      }
      for (const entry of reverted.drift) {
        lines.push(
          `harness ${writer.id}: drift: ${file.path} key "${entry.key}": written "${String(entry.written)}", current "${entry.current === undefined ? '(absent)' : String(entry.current)}"; leaving file unchanged`,
        )
      }
      if (reverted.drift.length > 0) {
        events.push(
          eventFor('refused', writer, file, { reason: 'drift', drift: driftEntries(reverted.drift) }),
        )
      }
      continue
    }
    const drift = revertBlockEdits(content, record.keys, file.format).drift
    for (const entry of drift) {
      lines.push(
        `harness ${writer.id}: drift: ${file.path} key "${entry.key}": written "${String(entry.written)}", current "${entry.current === undefined ? '(absent)' : String(entry.current)}"; leaving file unchanged`,
      )
    }
    if (drift.length > 0) {
      events.push(eventFor('refused', writer, file, { reason: 'drift', drift: driftEntries(drift) }))
    }
  }
  return { lines, events }
}

async function runDisable(
  writers: readonly CaptureHarnessWriter[],
  home: string,
  env: PathEnv,
  dryRun: boolean,
  fileFilter: CaptureFileFilter | undefined,
): Promise<CaptureRunResult> {
  const out: string[] = []
  const err: string[] = []
  const events: CaptureEvent[] = []
  let exitCode = 0
  const receipt = loadReceiptLenient(home)
  const revertedPaths: string[] = []

  for (const writer of writers) {
    if (writer.kind === 'pending') {
      out.push(`harness ${writer.id}: ${writer.reason} (pending discovery); skipped`)
      continue
    }
    const files = declaredFiles(writer, home, env)
    // Drift is decided for every file before any revert is written. One
    // drifted file leaves the others in place so the receipt still describes
    // the whole enable; a later disable can restore every file once the
    // drift is gone.
    const drifted = driftLinesFor(writer, files, receipt)
    if (drifted.lines.length > 0) {
      exitCode = REFUSAL_EXIT
      err.push(...drifted.lines)
      events.push(...drifted.events)
      continue
    }
    for (const file of files) {
      const path = file.path
      const record = receipt?.files.find((entry) => entry.path === path)
      if (record === undefined) {
        // A file this harness never wrote and the filter rules out (an absent
        // Insiders profile) is not worth a "nothing to do" line in a report.
        if (fileFilter?.({ harnessId: writer.id, fileId: file.id, path }) !== false) {
          out.push(`harness ${writer.id}: no record for ${path}; nothing to do`)
          events.push(eventFor('noop', writer, file))
        }
        continue
      }
      const read = readTextIfPresent(path)
      const { found, content, error } = read
      if (error !== undefined) {
        exitCode = REFUSAL_EXIT
        err.push(`harness ${writer.id}: cannot read ${path}: ${error}; leaving file unchanged`)
        events.push(readRefusal(writer, file, read.thrown))
        continue
      }
      if (!found) {
        exitCode = REFUSAL_EXIT
        err.push(`harness ${writer.id}: drift: ${path} is missing since enable; leaving receipt unchanged`)
        // ENOENT is drift in this domain (errno.ts): the file left since enable.
        events.push(eventFor('refused', writer, file, { reason: 'drift', code: 'ENOENT' }))
        continue
      }

      const restoreChanges = (): CaptureEventChange[] =>
        Object.entries(record.keys).map(([key, entry]) => ({
          key,
          before: entry.written,
          ...(entry.prior.present ? { after: entry.prior.value } : {}),
        }))

      if (file.format === 'json' || file.format === 'jsonc') {
        const reverted = revertJsonEdits(content, record.keys)
        if (reverted.refused !== undefined) {
          exitCode = REFUSAL_EXIT
          err.push(`harness ${writer.id}: ${path}: ${reverted.refused}; leaving file unchanged`)
          events.push(eventFor('refused', writer, file, { reason: 'unparseable' }))
          continue
        }
        if (reverted.drift.length > 0) {
          exitCode = REFUSAL_EXIT
          for (const entry of reverted.drift) {
            err.push(
              `harness ${writer.id}: drift: ${path} key "${entry.key}": written "${String(entry.written)}", current "${entry.current === undefined ? '(absent)' : String(entry.current)}"; leaving file unchanged`,
            )
          }
          events.push(
            eventFor('refused', writer, file, { reason: 'drift', drift: driftEntries(reverted.drift) }),
          )
          continue
        }
        if (!reverted.changed) {
          out.push(`harness ${writer.id}: already disabled (no-op): ${path}`)
          events.push(eventFor('noop', writer, file))
          revertedPaths.push(path)
          continue
        }
        if (dryRun) {
          out.push(`dry-run: ${path} would restore ${Object.keys(record.keys).join(', ')}`)
          events.push(eventFor('would-restore', writer, file, { changes: restoreChanges() }))
          continue
        }
        writeText(path, reverted.content)
        revertedPaths.push(path)
        out.push(`harness ${writer.id}: disabled: ${path} (restored)`)
        events.push(eventFor('restored', writer, file, { changes: restoreChanges() }))
        continue
      }

      const reverted = revertBlockEdits(content, record.keys, file.format)
      if (reverted.drift.length > 0) {
        exitCode = REFUSAL_EXIT
        for (const entry of reverted.drift) {
          err.push(
            `harness ${writer.id}: drift: ${path} key "${entry.key}": written "${String(entry.written)}", current "${entry.current === undefined ? '(absent)' : String(entry.current)}"; leaving file unchanged`,
          )
        }
        events.push(
          eventFor('refused', writer, file, { reason: 'drift', drift: driftEntries(reverted.drift) }),
        )
        continue
      }
      if (!reverted.changed) {
        out.push(`harness ${writer.id}: already disabled (no-op): ${path}`)
        events.push(eventFor('noop', writer, file))
        revertedPaths.push(path)
        continue
      }
      if (dryRun) {
        out.push(`dry-run: ${path} would restore ${Object.keys(record.keys).join(', ')}`)
        events.push(eventFor('would-restore', writer, file, { changes: restoreChanges() }))
        continue
      }
      writeText(path, reverted.content)
      revertedPaths.push(path)
      out.push(`harness ${writer.id}: disabled: ${path} (restored)`)
      events.push(eventFor('restored', writer, file, { changes: restoreChanges() }))
    }
  }

  if (!dryRun && receipt !== null && revertedPaths.length > 0) {
    receipt.files = receipt.files.filter((file) => !revertedPaths.includes(file.path))
    saveCaptureReceipt(home, receipt)
  }

  return {
    exitCode,
    stdout: out.length > 0 ? `${out.join('\n')}\n` : '',
    stderr: err.length > 0 ? `${err.join('\n')}\n` : '',
    events,
  }
}

/** Distinguishes "no receipt" from "a receipt that exists but cannot be parsed". */
export function loadReceiptState(home: string): { receipt: CaptureReceipt | null; corrupt: boolean } {
  try {
    return { receipt: loadCaptureReceipt(home), corrupt: false }
  } catch {
    return { receipt: null, corrupt: true }
  }
}

export function loadReceiptLenient(home: string): CaptureReceipt | null {
  try {
    return loadCaptureReceipt(home)
  } catch {
    return null
  }
}
