// Claude Code capture writer (T8): pending discovery.
//
// The OTLP exporter surface for Claude Code config is not mapped yet. This
// stub keeps the harness in the capture registry so `status` reports it as
// "not yet supported" instead of unknown; a Phase 2 task fills in the
// managed writer.

import { join } from 'node:path'

import type { PendingHarnessWriter } from './registry.js'

export const claudeCodeHarness: PendingHarnessWriter = {
  kind: 'pending',
  id: 'claude-code',
  displayName: 'Claude Code',
  reason: 'not yet supported',
  resolvePath: (home: string) => join(home, '.claude', 'settings.json'),
  format: 'jsonc',
}
