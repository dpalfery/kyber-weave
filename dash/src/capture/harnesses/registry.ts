// The capture harness registry (T8): the six D6 harnesses plus Kilo Code (P3),
// in one place.
//
// Every writer module is a "pending discovery" stub until its Phase 2 task
// maps the harness's real OTLP exporter surface and turns it into a managed
// writer. Phase 2 tasks edit only their own writer file; the registry stays
// a plain list. Config paths resolve from the HOME passed at call time —
// never at module load — so tests run entirely under a temporary HOME. Since
// D14 each resolvePath also takes an optional path environment: the platform
// picks the joining rules and carries the Windows appData root, so win32
// resolution is testable on any host while every one-argument caller stays
// valid.
//
// The managed-writer fields below are the shared declaration seam. Multi-file
// paths, JSON scalar types, status warnings and the env snippet live here so
// a harness module cannot grow a second writer that bypasses D11.

import type { JsonScalar } from '../edit-json.js'
import type { KiloPluginResult, KiloPluginState } from '../kilo-plugin/install.js'
import type { PathEnv } from '../paths.js'
import { antigravityHarness } from './antigravity.js'
import { claudeCodeHarness } from './claude-code.js'
import { codexHarness } from './codex.js'
import { copilotHarness } from './copilot.js'
import { kiloHarness } from './kilo.js'
import { opencodeHarness } from './opencode.js'
import { piHarness } from './pi.js'

export type CaptureFileFormat = 'json' | 'jsonc' | 'toml' | 'yaml'

/** A harness whose exporter surface is not mapped yet: report, do not write. */
export type PendingHarnessWriter = {
  kind: 'pending'
  id: string
  displayName: string
  reason: string
  resolvePath: (home: string, env?: PathEnv) => string
  format: CaptureFileFormat
  /**
   * Home-dependent status lines. When set and non-empty, `status` prints
   * these instead of the static `reason`. Pi names the loaded extension;
   * Antigravity reports the statusline bridge; Kilo states the fixed
   * ingest-boundary facts. `enable` and `disable` do not call this and
   * never write config.
   */
  /**
   * `redact` is set by the structured (API) status: it must not echo text copied
   * from config the owner wrote. The CLI text omits it and is unchanged.
   */
  describeStatus?: (home: string, options?: { redact?: boolean }) => readonly string[]
  /**
   * A harness that owns one whole file (a plugin) instead of config keys. The
   * file carries its own receipt, so `enable` and `disable` call these and write
   * nothing to the key receipt. The Settings route stays read-only for it.
   */
  filePlugin?: {
    install: (home: string, env: PathEnv | undefined, options: { dryRun: boolean }) => KiloPluginResult
    uninstall: (home: string, env: PathEnv | undefined, options: { dryRun: boolean }) => KiloPluginResult
    path: (home: string, env: PathEnv | undefined) => string
    state: (home: string, env: PathEnv | undefined) => KiloPluginState
  }
}

/**
 * The live state of one declared file, handed to a harness's exporter
 * resolver. `observed` carries only the keys the harness declares, mapped to
 * their live value (absent keys are `undefined`); `processEnv` is the
 * environment the *caller* can legitimately vouch for, and is omitted when the
 * caller cannot see the one that decides — the server-side status route cannot
 * see a GUI-launched editor's environment, so it passes nothing and the
 * resolution says so instead of reporting an override as absent.
 */
export type CaptureExporterInput = {
  observed: Readonly<Record<string, unknown>>
  processEnv?: Readonly<Record<string, string | undefined>>
}

/**
 * The exporter a harness will actually use, resolved from its config state
 * and the process environment. WHY this exists beside `statusWarnings`: a
 * warning keys on one config value, but a harness's exporter can be decided by
 * a conjunction — for Copilot the settings keys *and* an environment override
 * the GUI never inherits from a shell. Keys can be present and correct while
 * the pipeline still resolves to the dev-null file exporter, which is a
 * failure, not a pass.
 */
export type CaptureExporterResolution = {
  /** The exporter the harness will resolve, e.g. `otlp-http` or `file`. */
  exporter: string
  /** True only when records would reach the receiver: the pipeline is on. */
  delivering: boolean
  /** One status line naming the resolved exporter. */
  line: string
  /** The precise, actionable warning shown when `delivering` is false. */
  warning: string
}

/**
 * One config file a managed harness owns. Each file is its own D11 unit:
 * its path, format and desired values are independent of the others.
 */
export type CaptureConfigFile = {
  fileId: string
  format: CaptureFileFormat
  resolvePath: (home: string, env?: PathEnv) => string
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
  /**
   * Dotted prefixes whose keys are ONE literal property, never a nested tree
   * (VS Code settings: `github.copilot.chat.otel`). Reading, writing and
   * restoring then address the flat property, and `enable` refuses a
   * pre-existing nested block instead of writing beside it.
   */
  literalKeyPrefixes?: readonly string[]
  /** Status-only: the exporter this file's harness will actually resolve. */
  exporterResolution?: (input: CaptureExporterInput) => CaptureExporterResolution
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
  resolvePath: (home: string, env?: PathEnv) => string
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
  /**
   * Literal dotted prefixes for the single-file surface; see
   * `CaptureConfigFile.literalKeyPrefixes`. When `configFiles` is set, each
   * file's own declaration is what the core reads instead.
   */
  literalKeyPrefixes?: readonly string[]
  /** Status-only exporter resolver for the single-file surface. */
  exporterResolution?: (input: CaptureExporterInput) => CaptureExporterResolution
  /** When set, each entry is written, receipted and restored on its own. */
  configFiles?: readonly CaptureConfigFile[]
  statusWarnings?: readonly CaptureStatusWarning[]
  statusEnvSnippet?: CaptureStatusEnvSnippet
}

export type CaptureHarnessWriter = PendingHarnessWriter | ManagedHarnessWriter

/** The seven harnesses, the default `--harness` set. Kilo is appended last. */
export const CAPTURE_HARNESS_REGISTRY: readonly CaptureHarnessWriter[] = [
  copilotHarness,
  claudeCodeHarness,
  codexHarness,
  opencodeHarness,
  piHarness,
  antigravityHarness,
  kiloHarness,
]

export const SUPPORTED_CAPTURE_HARNESS_IDS: readonly string[] = CAPTURE_HARNESS_REGISTRY.map(
  (writer) => writer.id,
)

/** Find a registered writer by harness id. */
export function harnessWriterFor(id: string): CaptureHarnessWriter | undefined {
  return CAPTURE_HARNESS_REGISTRY.find((writer) => writer.id === id)
}
