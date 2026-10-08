// Harness-agnostic capture command core for D3, D9 and D11 (T8).
//
// `kyberdash kyber capture status | enable | disable` points the six D6
// harnesses' OTLP exporters at the KyberDash receiver. Only OTLP/HTTP
// endpoints may be written; macOS and Linux are supported, Windows reports
// unsupported. Every config path resolves from the HOME passed at call time
// — never at module load — so tests run entirely under a temporary HOME.
//
// The six registry writers are pending-discovery stubs: they report "not
// yet supported" and are never written. Callers (and tests) may inject
// managed writers; `enable`/`disable`/`status` treat both kinds uniformly.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname } from 'node:path'

import { applyBlockEdits, readBlockKeys, revertBlockEdits } from './edit-block.js'
import { applyJsonEdits, readJsonKeys, revertJsonEdits } from './edit-json.js'
import {
  probeReceiver,
  RECEIVER_DOWN_REMEDY,
  type HealthFetcher,
} from './probe.js'
import {
  loadCaptureReceipt,
  saveCaptureReceipt,
  sha256Hex,
  type CaptureReceipt,
  type ReceiptFileRecord,
} from './receipt.js'
import {
  CAPTURE_HARNESS_REGISTRY,
  SUPPORTED_CAPTURE_HARNESS_IDS,
  type CaptureHarnessWriter,
} from './harnesses/registry.js'

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
  /** Injectable health fetch so tests pin liveness without a socket. */
  fetchHealth?: HealthFetcher
  /** Writer set override; defaults to the pending-discovery registry. */
  writers?: CaptureHarnessWriter[]
}

export type CaptureRunResult = {
  exitCode: number
  stdout: string
  stderr: string
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

function readTextIfPresent(path: string): { found: boolean; content: string } {
  if (!existsSync(path)) return { found: false, content: '' }
  return { found: true, content: readFileSync(path, 'utf8') }
}

function writeText(path: string, content: string): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
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
  const out: string[] = []
  const err: string[] = []

  if (platform === 'win32') {
    err.push(
      'kyberdash capture: Windows is unsupported; capture runs on macOS and Linux only.',
    )
    return { exitCode: REFUSAL_EXIT, stdout: '', stderr: `${err.join('\n')}\n` }
  }

  const endpoint = options.endpoint ?? DEFAULT_CAPTURE_ENDPOINT
  if (!isOtlpHttpEndpoint(endpoint)) {
    err.push(
      `kyberdash capture: --endpoint must be an OTLP/HTTP URL (http:// or https://); got "${endpoint}". ` +
        'Only OTLP/HTTP protocols may be written.',
    )
    return { exitCode: USAGE_EXIT, stdout: '', stderr: `${err.join('\n')}\n` }
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
      return { exitCode: USAGE_EXIT, stdout: '', stderr: `${err.join('\n')}\n` }
    }
  }
  const selected = requested
    .map((id) => writers.find((writer) => writer.id === id))
    .filter((writer): writer is CaptureHarnessWriter => writer !== undefined)

  const probe = await probeReceiver(endpoint, options.fetchHealth)

  if (action === 'status') {
    out.push(...statusLines(selected, home, endpoint))
    out.push(`receiver: ${probe.state}`, `  ${probe.detail}`)
    if (probe.state === 'none') out.push(RECEIVER_DOWN_REMEDY)
    return { exitCode: 0, stdout: `${out.join('\n')}\n`, stderr: '' }
  }

  if (action === 'enable') {
    return runEnable(selected, home, endpoint, probe.state, probe.detail, options.dryRun ?? false)
  }

  return runDisable(selected, home, options.dryRun ?? false)
}

function statusLines(
  writers: readonly CaptureHarnessWriter[],
  home: string,
  endpoint: string,
): string[] {
  const lines: string[] = []
  const receipt = loadReceiptLenient(home)
  for (const writer of writers) {
    const path = writer.resolvePath(home)
    const { found, content } = readTextIfPresent(path)
    lines.push(`harness ${writer.id} (${writer.displayName})`)
    lines.push(`  config: ${path} (${found ? 'found' : 'missing'})`)
    if (writer.kind === 'pending') {
      lines.push(`  ${writer.reason}`)
      continue
    }
    const desired = writer.desiredKeys(endpoint)
    const keys = Object.keys(desired)
    if (!found) {
      lines.push(`  expected keys: ${keys.join(', ')}`)
      lines.push('  present keys: none (config missing)')
      continue
    }
    const present = presentKeys(writer, content, endpoint)
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
    const record = receipt?.files.find((file) => file.path === path)
    if (record === undefined) {
      lines.push('  receipt: no record')
      continue
    }
    const currentSha = sha256Hex(content)
    if (currentSha !== record.postSha256) {
      lines.push(`  drift: file changed since enable (expected ${record.postSha256}, found ${currentSha})`)
      for (const [key, entry] of Object.entries(record.keys)) {
        const value = present[key]
        if (value === undefined || value !== String(entry.written)) {
          lines.push(
            `  drift: key "${key}": written "${String(entry.written)}", current "${value ?? '(absent)'}"`,
          )
        }
      }
    } else {
      lines.push('  drift: none')
    }
  }
  return lines
}

function presentKeys(
  writer: CaptureHarnessWriter,
  content: string,
  endpoint: string,
): Record<string, string> {
  if (writer.kind !== 'managed') return {}
  if (writer.format === 'json' || writer.format === 'jsonc') {
    const keys = Object.keys(writer.desiredKeys(endpoint))
    const found = readJsonKeys(content, keys)
    const result: Record<string, string> = {}
    for (const [key, prior] of Object.entries(found)) {
      if (prior.present && prior.value !== undefined) result[key] = String(prior.value)
    }
    return result
  }
  return readBlockKeys(content)
}

async function runEnable(
  writers: readonly CaptureHarnessWriter[],
  home: string,
  endpoint: string,
  probeState: string,
  probeDetail: string,
  dryRun: boolean,
): Promise<CaptureRunResult> {
  const out: string[] = []
  const err: string[] = []
  let exitCode = 0
  const receipt = loadReceiptLenient(home) ?? { version: 1 as const, files: [] }
  let wroteAny = false

  for (const writer of writers) {
    const path = writer.resolvePath(home)
    if (writer.kind === 'pending') {
      out.push(`harness ${writer.id}: ${writer.reason} (pending discovery); skipped`)
      continue
    }
    const desired = writer.desiredKeys(endpoint)
    const { found, content } = readTextIfPresent(path)

    if (writer.format === 'json' || writer.format === 'jsonc') {
      const current = readJsonKeys(content, Object.keys(desired))
      const upToDate = Object.entries(desired).every(
        ([key, value]) => current[key]?.present === true && String(current[key]?.value) === value,
      )
      if (upToDate) {
        out.push(`harness ${writer.id}: already enabled (no-op): ${path}`)
        continue
      }
      const applied = applyJsonEdits(content, desired)
      if (dryRun) {
        out.push(`dry-run: ${path} would write ${Object.keys(desired).join(', ')}`)
        for (const [key, value] of Object.entries(desired)) {
          const prior = applied.prior[key]!
          out.push(
            `  ${key}: ${prior.present ? JSON.stringify(prior.value) : '(absent)'} -> ${JSON.stringify(value)}`,
          )
        }
        continue
      }
      const preSha = sha256Hex(content)
      writeText(path, applied.content)
      const postSha = sha256Hex(applied.content)
      upsertReceipt(receipt, path, writer, desired, applied.prior, preSha, postSha)
      wroteAny = true
      out.push(`harness ${writer.id}: enabled: ${path} (${found ? 'updated' : 'created'})`)
      continue
    }

    const applied = applyBlockEdits(content, desired, writer.format)
    if (applied.conflict !== undefined) {
      exitCode = REFUSAL_EXIT
      err.push(`harness ${writer.id}: refusing ${path}: conflicting key "${applied.conflict.key}" outside the managed block`)
      err.push(applied.conflict.snippet)
      continue
    }
    if (!applied.changed) {
      out.push(`harness ${writer.id}: already enabled (no-op): ${path}`)
      continue
    }
    if (dryRun) {
      out.push(`dry-run: ${path} would write ${Object.keys(desired).join(', ')}`)
      for (const [key, value] of Object.entries(desired)) {
        const prior = applied.prior[key]!
        out.push(
          `  ${key}: ${prior.present ? JSON.stringify(prior.value) : '(absent)'} -> ${JSON.stringify(value)}`,
        )
      }
      continue
    }
    const preSha = sha256Hex(content)
    writeText(path, applied.content)
    const postSha = sha256Hex(applied.content)
    upsertReceipt(receipt, path, writer, desired, applied.prior, preSha, postSha)
    wroteAny = true
    out.push(`harness ${writer.id}: enabled: ${path} (${found ? 'updated' : 'created'})`)
  }

  if (wroteAny && !dryRun) saveCaptureReceipt(home, receipt)

  out.push(`receiver: ${probeState}`)
  out.push(`  ${probeDetail}`)
  if (probeState === 'none') out.push(RECEIVER_DOWN_REMEDY)
  return {
    exitCode,
    stdout: out.length > 0 ? `${out.join('\n')}\n` : '',
    stderr: err.length > 0 ? `${err.join('\n')}\n` : '',
  }
}

function upsertReceipt(
  receipt: CaptureReceipt,
  path: string,
  writer: CaptureHarnessWriter,
  desired: Record<string, string>,
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
    format: writer.format,
    harnessId: writer.id,
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

async function runDisable(
  writers: readonly CaptureHarnessWriter[],
  home: string,
  dryRun: boolean,
): Promise<CaptureRunResult> {
  const out: string[] = []
  const err: string[] = []
  let exitCode = 0
  const receipt = loadReceiptLenient(home)
  const revertedPaths: string[] = []

  for (const writer of writers) {
    const path = writer.resolvePath(home)
    if (writer.kind === 'pending') {
      out.push(`harness ${writer.id}: ${writer.reason} (pending discovery); skipped`)
      continue
    }
    const record = receipt?.files.find((file) => file.path === path)
    if (record === undefined) {
      out.push(`harness ${writer.id}: no record for ${path}; nothing to do`)
      continue
    }
    const { found, content } = readTextIfPresent(path)
    if (!found) {
      exitCode = REFUSAL_EXIT
      err.push(`harness ${writer.id}: drift: ${path} is missing since enable; leaving receipt unchanged`)
      continue
    }

    if (writer.format === 'json' || writer.format === 'jsonc') {
      const reverted = revertJsonEdits(content, record.keys)
      if (reverted.drift.length > 0) {
        exitCode = REFUSAL_EXIT
        for (const entry of reverted.drift) {
          err.push(
            `harness ${writer.id}: drift: ${path} key "${entry.key}": written "${String(entry.written)}", current "${entry.current === undefined ? '(absent)' : String(entry.current)}"; leaving file unchanged`,
          )
        }
        continue
      }
      if (!reverted.changed) {
        out.push(`harness ${writer.id}: already disabled (no-op): ${path}`)
        revertedPaths.push(path)
        continue
      }
      if (dryRun) {
        out.push(`dry-run: ${path} would restore ${Object.keys(record.keys).join(', ')}`)
        continue
      }
      writeText(path, reverted.content)
      revertedPaths.push(path)
      out.push(`harness ${writer.id}: disabled: ${path} (restored)`)
      continue
    }

    const reverted = revertBlockEdits(content, record.keys, writer.format)
    if (reverted.drift.length > 0) {
      exitCode = REFUSAL_EXIT
      for (const entry of reverted.drift) {
        err.push(
          `harness ${writer.id}: drift: ${path} key "${entry.key}": written "${String(entry.written)}", current "${entry.current === undefined ? '(absent)' : String(entry.current)}"; leaving file unchanged`,
        )
      }
      continue
    }
    if (!reverted.changed) {
      out.push(`harness ${writer.id}: already disabled (no-op): ${path}`)
      revertedPaths.push(path)
      continue
    }
    if (dryRun) {
      out.push(`dry-run: ${path} would restore ${Object.keys(record.keys).join(', ')}`)
      continue
    }
    writeText(path, reverted.content)
    revertedPaths.push(path)
    out.push(`harness ${writer.id}: disabled: ${path} (restored)`)
  }

  if (!dryRun && receipt !== null && revertedPaths.length > 0) {
    receipt.files = receipt.files.filter((file) => !revertedPaths.includes(file.path))
    saveCaptureReceipt(home, receipt)
  }

  return {
    exitCode,
    stdout: out.length > 0 ? `${out.join('\n')}\n` : '',
    stderr: err.length > 0 ? `${err.join('\n')}\n` : '',
  }
}

function loadReceiptLenient(home: string): CaptureReceipt | null {
  try {
    return loadCaptureReceipt(home)
  } catch {
    return null
  }
}
