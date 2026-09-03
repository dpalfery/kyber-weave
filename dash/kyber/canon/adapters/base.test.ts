import { describe, expect, it } from 'vitest'

import { UNTRACED_GROUP, resolveRootByParentage, traceGroup } from './base.js'
import { rawSpan } from './testing.js'

describe('traceGroup', () => {
  it('keys a group by its trace id', () => {
    expect(traceGroup(rawSpan({ spanId: 'a', traceId: 't1' }))).toBe('t1')
  })

  it('files spans with no trace id under the untraced group', () => {
    expect(traceGroup(rawSpan({ spanId: 'a', traceId: null }))).toBe(UNTRACED_GROUP)
  })
})

describe('resolveRootByParentage', () => {
  it('returns the declared root even when a parentless fragment is listed first', () => {
    const spans = [
      rawSpan({ spanId: 'frag', traceId: 't1', parentSpanId: 'dropped' }),
      rawSpan({ spanId: 'root', traceId: 't1', parentSpanId: null }),
      rawSpan({ spanId: 'child', traceId: 't1', parentSpanId: 'root' }),
    ]
    expect(resolveRootByParentage(spans)).toBe('root')
  })

  it('falls back to the first span whose parent was dropped from the group', () => {
    // A receiver can hand a group whose parent span was dropped (the
    // eviction class of R2.7); the fragment that referenced it is the best
    // remaining reconciliation root (R4.5).
    const spans = [
      rawSpan({ spanId: 'child', traceId: 't1', parentSpanId: 'frag' }),
      rawSpan({ spanId: 'frag', traceId: 't1', parentSpanId: 'dropped' }),
    ]
    expect(resolveRootByParentage(spans)).toBe('frag')
  })

  it('returns undefined for an empty group', () => {
    expect(resolveRootByParentage([])).toBeUndefined()
  })

  it('returns undefined for a pure cycle rather than inventing a root', () => {
    const spans = [
      rawSpan({ spanId: 'a', traceId: 't1', parentSpanId: 'b' }),
      rawSpan({ spanId: 'b', traceId: 't1', parentSpanId: 'a' }),
    ]
    expect(resolveRootByParentage(spans)).toBeUndefined()
  })
})
