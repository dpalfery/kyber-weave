// Codex capture writer (P2.3): status-only (G1-Q3 = (a)).
//
// Codex OTel stays off. The harness remains pending so `enable` and `disable`
// write nothing: they must not create or modify `~/.codex/config.toml`, and
// they must not add an `[otel]` table. `status` reports that contract through
// `reason` — coverage comes from session files, not from an exporter we turn on.

import { join } from 'node:path'

import type { PendingHarnessWriter } from './registry.js'

export const codexHarness: PendingHarnessWriter = {
  kind: 'pending',
  id: 'codex',
  displayName: 'Codex',
  reason: 'no [otel] table is present; coverage comes from session files',
  resolvePath: (home: string) => join(home, '.codex', 'config.toml'),
  format: 'toml',
}
