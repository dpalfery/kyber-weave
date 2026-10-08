// OpenCode harness adapter slot (Seam 3).
//
// Placeholder only: it detects nothing and claims no span, so registering it
// leaves every existing ingest vote unchanged. The Phase 2 OpenCode task
// fills in the fingerprint, relevance, normalization and validation here and
// edits no other adapter file to do it.

import type { HarnessAdapter, RawSpan } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import { baseRecord, validateRecordTokens } from './copilot.js'

/** The harness identity a future OpenCode adapter will stamp on its records. */
const OPENCODE_HARNESS = 'opencode'

export const opencodeAdapter: HarnessAdapter = {
  name: OPENCODE_HARNESS,
  namespaces: ['gen_ai', 'opencode'],

  detect(_span: RawSpan): number {
    return 0
  },

  relevance(_span: RawSpan): number {
    return 0
  },

  normalize(raw: RawSpan) {
    return baseRecord(this, raw)
  },

  group: traceGroup,
  resolveRoot: resolveRootByParentage,
  validate: validateRecordTokens,

  unexportedMetrics() {
    return []
  },
}
