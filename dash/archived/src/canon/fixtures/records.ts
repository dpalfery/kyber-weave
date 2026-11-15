// Shared CanonicalRecord fixture builders for the canon suites. These were
// duplicated byte-for-byte in findings.test.ts and sessions.test.ts until
// issue #129; one definition here keeps a CanonicalRecord shape change from
// having to be applied in every copy, where the copies could drift. The
// bodies are deliberately left exactly as the duplicates were, so consuming
// the module is a pure import swap with zero assertion edits.

import type { CanonicalRecord, ContentPart } from '../types.js'

export const tokens = (over: Partial<CanonicalRecord['tokens']> = {}) => ({
  freshInput: 1000,
  cacheRead: 0,
  cacheCreation: 0,
  output: 100,
  reportedInput: 1000,
  reportedOutput: 100,
  ...over,
})

export function turn(spanId: string, parts: ContentPart[], over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-1',
    parentSpanId: null,
    source: 'antigravity',
    harness: 'antigravity',
    sessionId: 'sess-1',
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-09-03T10:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: tokens(),
    content: {},
    parts,
    cost: { basis: 'unknown', status: 'no_rate' },
    ...over,
  }
}
