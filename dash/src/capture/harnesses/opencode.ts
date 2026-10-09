// OpenCode capture writer (P2.4a).
//
// AI-SDK spans need `experimental.openTelemetry: true` in
// ~/.config/opencode/opencode.jsonc (JSONC). The OTLP endpoint is not a
// config key: OpenCode reads `OTEL_EXPORTER_OTLP_ENDPOINT` from the process
// environment, so status reports that variable and enable never writes it
// or a shell rc. Status also notes upstream #13438 and the non-model span
// volume; those are observations, not keys the receipt restores.

import { join } from 'node:path'

import type { ManagedHarnessWriter } from './registry.js'

/** The only key enable writes. The value stays a JSON boolean. */
const OPENCODE_OTEL_FLAG_KEY = 'experimental.openTelemetry'

/**
 * Reported on status from this process's environment. Omitting it from
 * `desiredKeys` is what keeps enable from writing it into opencode.jsonc.
 */
const STATUS_ENV_KEY = 'OTEL_EXPORTER_OTLP_ENDPOINT'

export const opencodeHarness: ManagedHarnessWriter = {
  kind: 'managed',
  id: 'opencode',
  displayName: 'OpenCode',
  format: 'jsonc',
  resolvePath: (home: string) => join(home, '.config', 'opencode', 'opencode.jsonc'),
  // The endpoint argument is the receiver origin. OpenCode does not store it
  // in opencode.jsonc, so the declaration ignores it.
  desiredKeys: () => ({
    [OPENCODE_OTEL_FLAG_KEY]: true,
  }),
  statusEnvSnippet: {
    label:
      'process env (upstream #13438; non-model spans are emitted with model spans and are not written here)',
    envKeys: [STATUS_ENV_KEY],
  },
}
