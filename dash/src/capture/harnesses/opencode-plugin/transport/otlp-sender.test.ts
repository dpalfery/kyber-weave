// T4 (plan 2026-10-23 §6-T4 RED): the wire body must carry the envelope v2
// context attributes. A `session.context` hook event with content fields
// lands on the OTLP span as `opencode.context.system/.tools/.skills` plus
// model markers when named; absent fields stay absent (no empty padding).

import { describe, expect, it } from 'vitest'

import type { OpencodeHookEvent } from '../plugin.js'
import { buildOtlpTracesBody } from './otlp-sender.js'

const CONTEXT_EVENT: OpencodeHookEvent = {
  kind: 'session.context',
  sessionId: 'ses_t4_context',
  timestamp: '2026-10-23T00:00:00.000Z',
  systemPrompt: 'T4 assembled system prompt',
  toolsText: '[{"name":"read","description":"read a file"}]',
  skills: ['plan', 'review'],
  model: 'claude-opus-4-6',
  agent: 'build',
}

function attributeMap(event: OpencodeHookEvent): Map<string, unknown> {
  const body = buildOtlpTracesBody([event])
  const span = body.resourceSpans[0]!.scopeSpans[0]!.spans[0]!
  return new Map(span.attributes.map((attribute) => [attribute.key, attribute.value]))
}

describe('OTLP sender carries T4 context fields onto the wire span', () => {
  it('includes opencode.context.* attributes on a session.context event', () => {
    const attributes = attributeMap(CONTEXT_EVENT)

    expect(attributes.get('opencode.context.system')).toEqual({
      stringValue: 'T4 assembled system prompt',
    })
    expect(attributes.get('opencode.context.tools')).toEqual({
      stringValue: '[{"name":"read","description":"read a file"}]',
    })
    expect(attributes.get('opencode.context.skills')).toEqual({
      stringValue: '["plan","review"]',
    })
  })

  it('includes model and agent markers when the event names them', () => {
    const attributes = attributeMap(CONTEXT_EVENT)

    expect(attributes.get('opencode.model.id')).toEqual({ stringValue: 'claude-opus-4-6' })
    expect(attributes.get('opencode.agent')).toEqual({ stringValue: 'build' })
  })

  it('drops non-string content values instead of emitting invalid OTLP (review PR #425 finding 2, RED)', () => {
    const attributes = attributeMap({
      kind: 'session.context',
      sessionId: 'ses_t4_coerce',
      timestamp: '2026-10-23T00:00:00.000Z',
      systemPrompt: 42 as unknown as string,
      toolsText: { name: 'read' } as unknown as string,
      model: true as unknown as string,
    })
    expect(attributes.has('opencode.context.system')).toBe(false)
    expect(attributes.has('opencode.context.tools')).toBe(false)
    expect(attributes.has('opencode.model.id')).toBe(false)
    expect(attributes.get('session.id')).toEqual({ stringValue: 'ses_t4_coerce' })
  })

  it('omits context attributes when the event carries no content', () => {
    const attributes = attributeMap({
      kind: 'session.start',
      sessionId: 'ses_t4_bare',
      timestamp: '2026-10-23T00:00:00.000Z',
    })

    expect(attributes.has('opencode.context.system')).toBe(false)
    expect(attributes.has('opencode.context.tools')).toBe(false)
    expect(attributes.has('opencode.context.skills')).toBe(false)
    expect(attributes.has('opencode.model.id')).toBe(false)
  })
})
