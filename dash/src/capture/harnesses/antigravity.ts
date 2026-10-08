// Antigravity capture writer (T8): pending discovery.
//
// The OTLP exporter surface for Antigravity config is not mapped yet. This
// stub keeps the harness in the capture registry so `status` reports it as
// "not yet supported" instead of unknown; a Phase 2 task fills in the
// managed writer.

import { join } from 'node:path'

import type { PendingHarnessWriter } from './registry.js'

export const antigravityHarness: PendingHarnessWriter = {
  kind: 'pending',
  id: 'antigravity',
  displayName: 'Antigravity',
  reason: 'not yet supported',
  resolvePath: (home: string) => join(home, '.gemini', 'antigravity', 'settings.json'),
  format: 'json',
}
