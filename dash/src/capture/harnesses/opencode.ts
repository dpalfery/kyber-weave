// OpenCode capture writer (T8): pending discovery.
//
// The OTLP exporter surface for OpenCode config is not mapped yet. This stub
// keeps the harness in the capture registry so `status` reports it as
// "not yet supported" instead of unknown; a Phase 2 task fills in the
// managed writer.

import { join } from 'node:path'

import type { PendingHarnessWriter } from './registry.js'

export const opencodeHarness: PendingHarnessWriter = {
  kind: 'pending',
  id: 'opencode',
  displayName: 'OpenCode',
  reason: 'not yet supported',
  resolvePath: (home: string) => join(home, '.config', 'opencode', 'opencode.json'),
  format: 'json',
}
