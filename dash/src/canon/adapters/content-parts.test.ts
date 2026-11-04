import { describe, expect, it } from 'vitest'

import type { CanonicalRecord } from '../types.js'
import { appendPart } from './content-parts.js'

/** A record with nothing on it, as an adapter's `baseRecord` starts one. */
function emptyRecord(): CanonicalRecord {
  return {
    spanId: 'span-1',
    traceId: 'trace-1',
    parentSpanId: null,
    source: 'test',
    harness: 'test',
    name: 'span',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-11-04T00:00:00.000Z',
    durationMs: 0,
    status: 'unset',
    tokens: {
      freshInput: 0,
      cacheRead: 0,
      cacheCreation: 0,
      output: 0,
      reportedInput: 0,
      reportedOutput: 0,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
  }
}

describe('the shared content-part helper', () => {
  it('files a part after every existing part, and recomputes the content', () => {
    const record = emptyRecord()

    appendPart(record, { part: 'conversation_history', text: 'the first prompt' })
    appendPart(record, { part: 'conversation_history', text: 'the second prompt' })

    expect(record.parts).toEqual([
      { part: 'conversation_history', text: 'the first prompt', order: 0 },
      { part: 'conversation_history', text: 'the second prompt', order: 1 },
    ])
    // Two parts of one bucket join, in order.
    expect(record.content['conversation_history']).toBe('the first prompt\nthe second prompt')
  })

  it('files no part for an empty text, so the parts and the content agree', () => {
    const record = emptyRecord()

    appendPart(record, { part: 'conversation_history', text: '' })

    // An empty part would sit in the parts list and nowhere in the content,
    // and the two readers of a record would disagree.
    expect(record.parts ?? []).toEqual([])
    expect(record.content).toEqual({})
  })
})
