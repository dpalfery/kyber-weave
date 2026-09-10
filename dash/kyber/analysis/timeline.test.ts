// Execution-structure analysis (R9). The fixture trace carries the three
// populations the criteria name: a primary conversation with tool spans, a
// subagent invocation with tooling of its own, and an auxiliary
// title-generation turn — plus an orphan whose parent never arrived, the
// case design.md routes to attribute grouping. Cost figures in the fixture
// stay on one basis, with tool spans priced at a genuine $0.00 (R5.4: a
// priced zero is a figure, not a missing rate), so the session total is a
// number the assertions can hold to account — including the auxiliary
// spend, which separation must not drop (R9.4).

import { describe, expect, it } from 'vitest'

import {
  AUXILIARY_GROUP_ID,
  SESSION_ROOT_ID,
  auxiliarySpend,
  buildTimeline,
  subagentSessions,
  type TimelineNode,
} from './timeline.js'
import { type CanonicalRecord, type CostBlock, type TokenUsage } from '../canon/types.js'

const T0 = Date.parse('2026-07-14T10:00:00.000Z')

/** ISO timestamp `offsetMs` into the fixture session. */
const at = (offsetMs: number): string => new Date(T0 + offsetMs).toISOString()

function usage(): TokenUsage {
  return {
    freshInput: 0,
    cacheRead: 0,
    cacheCreation: 0,
    output: 0,
    reportedInput: 0,
    reportedOutput: 0,
  }
}

function priced(value: number): CostBlock {
  return { basis: 'harness', status: 'priced', value, currency: 'USD' }
}

function span(spanId: string, name: string, overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-1',
    parentSpanId: null,
    source: 'pi-abc123',
    harness: 'pi',
    name,
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: at(0),
    durationMs: 0,
    status: 'unset',
    tokens: usage(),
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: {},
    ...overrides,
  }
}

/** Depth-first lookup by span id. */
function find(node: TimelineNode, spanId: string): TimelineNode | undefined {
  if (node.spanId === spanId) return node
  for (const child of node.children) {
    const hit = find(child, spanId)
    if (hit !== undefined) return hit
  }
  return undefined
}

function collect(node: TimelineNode, predicate: (node: TimelineNode) => boolean): TimelineNode[] {
  const hits: TimelineNode[] = []
  const walk = (current: TimelineNode): void => {
    if (predicate(current)) hits.push(current)
    for (const child of current.children) walk(child)
  }
  walk(node)
  return hits
}

/**
 * One session: a conversation root with a tool span, a subagent invocation
 * running a tool of its own, an auxiliary title-generation turn, and an
 * orphan span whose parent (`s-missing`) never arrived.
 */
function sessionTrace(): CanonicalRecord[] {
  return [
    span('s-root', 'pi.agent.chat', {
      durationMs: 5000,
      raw: { 'pi.session.id': 'sess-1', 'gen_ai.usage.input_tokens': 1200 },
      cost: priced(0.4),
    }),
    span('s-tool', 'pi.tool.read', {
      parentSpanId: 's-root',
      op: 'tool.invoke',
      timestamp: at(1000),
      durationMs: 400,
      cost: priced(0),
    }),
    span('s-sub', 'pi.agent.subagent', {
      parentSpanId: 's-root',
      timestamp: at(2000),
      durationMs: 1500,
      raw: { 'pi.subagent.agent': 'dotnet-dev' },
      cost: priced(0.25),
    }),
    span('s-sub-tool', 'pi.tool.write', {
      parentSpanId: 's-sub',
      op: 'tool.invoke',
      timestamp: at(2500),
      durationMs: 300,
      cost: priced(0),
    }),
    span('s-title', 'pi.agent.title', {
      parentSpanId: 's-root',
      timestamp: at(4000),
      durationMs: 600,
      raw: { 'pi.request.purpose': 'conversation title' },
      cost: priced(0.01),
    }),
    span('s-orphan', 'pi.tool.scan', {
      parentSpanId: 's-missing',
      op: 'tool.invoke',
      timestamp: at(4500),
      durationMs: 100,
      cost: priced(0),
    }),
  ]
}

describe('buildTimeline — hierarchical rendering (R9.1)', () => {
  const timeline = buildTimeline(sessionTrace())

  it('roots everything under one synthetic session node', () => {
    expect(timeline.spanId).toBe(SESSION_ROOT_ID)
    expect(timeline.parentId).toBeNull()
    expect(timeline.children.map((child) => child.spanId)).toEqual([
      's-root',
      AUXILIARY_GROUP_ID,
      '(orphan:pi:pi.tool.scan)',
    ])
  })

  it('nests tool executions and the subagent invocation under the conversation span', () => {
    const root = find(timeline, 's-root')
    expect(root?.children.map((child) => child.spanId)).toEqual(['s-tool', 's-sub'])
    // The subagent's own tooling hangs under the invocation, not beside it.
    expect(find(timeline, 's-sub')?.children.map((child) => child.spanId)).toEqual(['s-sub-tool'])
  })

  it('places each span at its offset from session start', () => {
    expect(timeline.startMs).toBe(0)
    expect(find(timeline, 's-tool')?.startMs).toBe(1000)
    expect(find(timeline, 's-title')?.startMs).toBe(4000)
    // The session node spans everything under it: 0 → 5000.
    expect(timeline.durationMs).toBe(5000)
    expect(find(timeline, 's-sub')?.durationMs).toBe(1500)
  })

  it('returns an empty session for an empty input rather than guessing one', () => {
    const empty = buildTimeline([])
    expect(empty.spanId).toBe(SESSION_ROOT_ID)
    expect(empty.children).toEqual([])
    expect(empty.durationMs).toBe(0)
    expect(empty.cost.value).toBeUndefined()
  })
})

describe('buildTimeline — attribute inspection (R9.2)', () => {
  const timeline = buildTimeline(sessionTrace())

  it('exposes the attributes each record preserved', () => {
    expect(find(timeline, 's-root')?.attributes).toEqual({
      'pi.session.id': 'sess-1',
      'gen_ai.usage.input_tokens': 1200,
    })
    expect(find(timeline, 's-sub')?.attributes).toEqual({ 'pi.subagent.agent': 'dotnet-dev' })
    expect(find(timeline, 's-tool')?.attributes).toEqual({})
  })

  it('exposes nothing behind a synthetic container — there is no span to inspect', () => {
    expect(timeline.attributes).toEqual({})
    const bucket = find(timeline, AUXILIARY_GROUP_ID)
    expect(bucket?.attributes).toEqual({})
  })
})

describe('subagentSessions — subagent identification (R9.3)', () => {
  it('identifies the subagent session by name evidence and names its parent', () => {
    const timeline = buildTimeline(sessionTrace())
    expect(subagentSessions(timeline)).toEqual([
      { spanId: 's-sub', parentSpanId: 's-root', name: 'pi.agent.subagent' },
    ])
    expect(find(timeline, 's-sub')?.isSubagent).toBe(true)
    expect(find(timeline, 's-sub')?.parentId).toBe('s-root')
  })

  it('also honors harness-specific attribute evidence, and names a parent that never arrived', () => {
    const timeline = buildTimeline([
      span('s-main', 'pi.agent.chat', { durationMs: 1000 }),
      span('s-attr-sub', 'pi.agent.chat', {
        parentSpanId: 's-main',
        timestamp: at(100),
        raw: { 'pi.agent.kind': 'subagent' },
      }),
      span('s-loose-sub', 'pi.agent.chat', {
        parentSpanId: 's-gone',
        timestamp: at(200),
        raw: { 'gen_ai.operation.name': 'subagent' },
      }),
    ])
    expect(find(timeline, 's-attr-sub')?.isSubagent).toBe(true)
    // The loose invocation is still identified, with its own parent claim —
    // identification does not require the parent to have arrived.
    expect(subagentSessions(timeline)).toEqual([
      { spanId: 's-attr-sub', parentSpanId: 's-main', name: 'pi.agent.chat' },
      { spanId: 's-loose-sub', parentSpanId: 's-gone', name: 'pi.agent.chat' },
    ])
  })
})

describe('auxiliary separation with spend still reported (R9.4)', () => {
  const timeline = buildTimeline(sessionTrace())

  it('separates the title turn from the primary conversation', () => {
    const title = find(timeline, 's-title')
    expect(title?.isAuxiliary).toBe(true)
    // It lives under the auxiliary group, not under the conversation span.
    const bucket = timeline.children.find((child) => child.spanId === AUXILIARY_GROUP_ID)
    expect(bucket?.children.map((child) => child.spanId)).toEqual(['s-title'])
    expect(find(timeline, 's-root')?.children.map((child) => child.spanId)).toEqual([
      's-tool',
      's-sub',
    ])
    // The primary conversation contains no auxiliary activity.
    const primary = find(timeline, 's-root')
    expect(primary && collect(primary, (node) => node.isAuxiliary)).toEqual([])
  })

  it('still reports the auxiliary spend, apart and in the session total', () => {
    expect(auxiliarySpend(timeline)).toEqual({
      basis: 'harness',
      status: 'priced',
      value: 0.01,
      currency: 'USD',
    })
    // 0.40 conversation + 0.25 subagent + 0.01 title: separation dropped nothing.
    expect(timeline.cost.basis).toBe('harness')
    expect(timeline.cost.status).toBe('priced')
    expect(timeline.cost.value).toBeCloseTo(0.66, 10)
    expect(timeline.cost.currency).toBe('USD')
  })

  it('reports no auxiliary figure — not $0.00 — when there is no auxiliary activity', () => {
    const spend = auxiliarySpend(buildTimeline([span('s-a', 'pi.agent.chat')]))
    expect(spend).toEqual({ basis: 'unknown', status: 'no_rate' })
  })
})

describe('orphans and cycles group by attribute, not invented ancestry (design.md)', () => {
  const timeline = buildTimeline(sessionTrace())

  it('groups a span whose parent never arrived under its harness and name', () => {
    const group = find(timeline, '(orphan:pi:pi.tool.scan)')
    expect(group?.children.map((child) => child.spanId)).toEqual(['s-orphan'])
    // The record's own parent claim is preserved verbatim.
    expect(find(timeline, 's-orphan')?.parentId).toBe('s-missing')
  })

  it('does not nest a parentage cycle under itself', () => {
    const cyclic = buildTimeline([
      span('s-a', 'pi.tool.loop', { parentSpanId: 's-b' }),
      span('s-b', 'pi.tool.loop', { parentSpanId: 's-a' }),
    ])
    const group = find(cyclic, '(orphan:pi:pi.tool.loop)')
    expect(group?.children.map((child) => child.spanId)).toEqual(['s-a', 's-b'])
    expect(group?.parentId).toBeNull()
  })

  it('carries no figure on a total whose bases differ rather than blending them (R5.1)', () => {
    const mixed = buildTimeline([
      span('s-priced', 'pi.agent.chat', { cost: priced(0.4) }),
      span('s-unpriced', 'pi.tool.read', { parentSpanId: 's-priced' }),
    ])
    expect(mixed.cost).toEqual({ basis: 'unknown', status: 'no_rate' })
    // The per-span blocks survive unblended either way.
    expect(find(mixed, 's-priced')?.cost.value).toBe(0.4)
    expect(find(mixed, 's-unpriced')?.cost.value).toBeUndefined()
  })
})
