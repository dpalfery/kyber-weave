// The capture harness registry (T8): the six D6 harnesses in one place.
//
// Every writer module is a "pending discovery" stub until its Phase 2 task
// maps the harness's real OTLP exporter surface and turns it into a managed
// writer. Phase 2 tasks edit only their own writer file; the registry stays
// a plain list. Config paths resolve from the HOME passed at call time —
// never at module load — so tests run entirely under a temporary HOME.
//
// The managed-writer fields below are the shared declaration seam. Multi-file
// paths, JSON scalar types, status warnings and the env snippet live here so
// a harness module cannot grow a second writer that bypasses D11.

import type { JsonScalar } from '../edit-json.js'
import { antigravityHarness } from './antigravity.js'
import { claudeCodeHarness } from './claude-code.js'
import { codexHarness } from './codex.js'
import { copilotHarness } from './copilot.js'
import { opencodeHarness } from './opencode.js'
import { piHarness } from './pi.js'

export type CaptureFileFormat = 'json' | 'jsonc' | 'toml' | 'yaml'

/** A harness whose exporter surface is not mapped yet: report, do not write. */
export type PendingHarnessWriter = {
  kind: 'pending'
  id: string
  displayName: string
  reason: string
  resolvePath: (home: string) => string
  format: CaptureFileFormat
  /**
   * Home-dependent status lines. When set and non-empty, `status` prints
   * these instead of the static `reason`. Pi names the loaded extension;
   * Antigravity reports the statusline bridge. `enable` and `disable` do
   * not call this and never write config.
   */
  describeStatus?: (home: string) => readonly string[]
}

/**
 * One config file a managed harness owns. Each file is its own D11 unit:
 * its path, format and desired values are independent of the others.
 */
export type CaptureConfigFile = {
  fileId: string
  format: CaptureFileFormat
  resolvePath: (home: string) => string
  /**
   * Keys `enable` writes. JSON and JSONC keep JSON types (string, boolean,
   * number, null). TOML and YAML are still written as strings by the core.
   */
  desiredKeys: (endpoint: string) => Record<string, JsonScalar>
  /**
   * Keys inserted only when this file does not already contain them. A
   * present key keeps the owner's value and is left out of the receipt, so
   * `disable` cannot restore or overwrite it. The core decides absence after
   * it reads the file; the harness does not edit.
   */
  ensureIfAbsentKeys?: (endpoint: string) => Record<string, JsonScalar>
}

/**
 * A status-only warning. The core prints it when the key currently holds
 * `whenValue`; it never writes the key, so the owner's value stays put.
 */
export type CaptureStatusWarning = {
  fileId: string
  key: string
  whenValue: JsonScalar
  warnText: string
}

/**
 * Optional status text built from the capture process environment. The core
 * reads `process.env` only — shell rc files are the user's and are never
 * opened.
 */
export type CaptureStatusEnvSnippet = {
  label: string
  envKeys: readonly string[]
}

/** A harness with a mapped exporter surface: the keys `enable` writes. */
export type ManagedHarnessWriter = {
  kind: 'managed'
  id: string
  displayName: string
  resolvePath: (home: string) => string
  format: CaptureFileFormat
  /**
   * The single-file surface. Strings remain valid. JSON scalars are accepted
   * so a one-file declaration can write a boolean or number without a second
   * shape. When `configFiles` is set, those files are what `enable` writes.
   */
  desiredKeys: (endpoint: string) => Record<string, JsonScalar>
  /**
   * Single-file ensure-if-absent keys. When `configFiles` is set, each
   * file's own `ensureIfAbsentKeys` is what `enable` reads instead.
   */
  ensureIfAbsentKeys?: (endpoint: string) => Record<string, JsonScalar>
  /** When set, each entry is written, receipted and restored on its own. */
  configFiles?: readonly CaptureConfigFile[]
  statusWarnings?: readonly CaptureStatusWarning[]
  statusEnvSnippet?: CaptureStatusEnvSnippet
}

export type CaptureHarnessWriter = PendingHarnessWriter | ManagedHarnessWriter

/** The six D6 harnesses, the default `--harness` set. */
export const CAPTURE_HARNESS_REGISTRY: readonly CaptureHarnessWriter[] = [
  copilotHarness,
  claudeCodeHarness,
  codexHarness,
  opencodeHarness,
  piHarness,
  antigravityHarness,
]

export const SUPPORTED_CAPTURE_HARNESS_IDS: readonly string[] = CAPTURE_HARNESS_REGISTRY.map(
  (writer) => writer.id,
)

/** Find a registered writer by harness id. */
export function harnessWriterFor(id: string): CaptureHarnessWriter | undefined {
  return CAPTURE_HARNESS_REGISTRY.find((writer) => writer.id === id)
}
