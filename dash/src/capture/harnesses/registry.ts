// The capture harness registry (T8): the six D6 harnesses in one place.
//
// Every writer module is a "pending discovery" stub until its Phase 2 task
// maps the harness's real OTLP exporter surface and turns it into a managed
// writer. Phase 2 tasks edit only their own writer file; the registry stays
// a plain list. Config paths resolve from the HOME passed at call time —
// never at module load — so tests run entirely under a temporary HOME.

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
}

/** A harness with a mapped exporter surface: the keys `enable` writes. */
export type ManagedHarnessWriter = {
  kind: 'managed'
  id: string
  displayName: string
  resolvePath: (home: string) => string
  format: CaptureFileFormat
  /** The exact keys (and OTLP/HTTP values) `enable` writes for an endpoint. */
  desiredKeys: (endpoint: string) => Record<string, string>
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
