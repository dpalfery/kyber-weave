// Pi capture writer (T8): pending discovery.
//
// The OTLP exporter surface for Pi config is not mapped yet. This stub keeps
// the harness in the capture registry so `status` reports it as "not yet
// supported" instead of unknown; a Phase 2 task fills in the managed writer.

import { join } from 'node:path'

import type { PendingHarnessWriter } from './registry.js'

export const piHarness: PendingHarnessWriter = {
  kind: 'pending',
  id: 'pi',
  displayName: 'Pi',
  reason: 'not yet supported',
  resolvePath: (home: string) => join(home, '.pi', 'agent', 'settings.json'),
  format: 'json',
}
