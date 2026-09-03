import { describe, expect, it } from 'vitest'

import { CanonStore } from './store.js'
import { approximateO200kBase } from './tokens.js'
import { buildSessionRow, buildSessions } from './sessions.js'
import type { CanonicalRecord, ContentPart } from './types.js'

// The analysis layer these tests exercise was written, tested and then called
// by nothing for the whole life of the feature. These tests are about the
// wire: that a stored record reaches `analyzeContext` with its server
// attribution and harness-reported counts intact, and that the payload the
// dashboard reads is the analysis output rather than a reconstruction of it.

const tokens = (over: Partial<CanonicalRecord['tokens']> = {}) => ({
  freshInput: 1000,
  cacheRead: 0,
  cacheCreation: 0,
  output: 100,
  reportedInput: 1000,
  reportedOutput: 100,
  ...over,
})

function turn(spanId: string, parts: ContentPart[], over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-1',
    parentSpanId: null,
    source: 'antigravity',
    harness: 'gemini',
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

describe('buildSessionRow', () => {
  it('carries ground-truth MCP servers through to the per-server bands', () => {
    // This is the whole point of the structured parts. The endpoint it
    // replaces hard-coded `toolDefinitionsByServer: {}`.
    const row = buildSessionRow(
      'sess-1',
      [
        turn('s1', [
          { part: 'tool_definitions', text: '{"name":"search"}', tokens: 300, server: 'context7' },
          { part: 'tool_definitions', text: '{"name":"explore"}', tokens: 200, server: 'codegraph' },
          { part: 'tool_definitions', text: '{"name":"read"}', tokens: 100 },
          { part: 'system_prompt', text: 'you are a helpful assistant', tokens: 400 },
        ]),
      ],
      approximateO200kBase,
    )
    const context = (row.payload as any).context

    expect(context.measurable).toBe(true)
    expect(context.turns[0].toolDefinitionsByServer).toEqual({ context7: 300, codegraph: 200 })
    // A definition naming no server is counted, never guessed into a group.
    expect(context.turns[0].builtinToolDefinitionTokens).toBe(100)
  })

  it('prefers a harness-reported count over tokenizing the text', () => {
    const row = buildSessionRow(
      'sess-1',
      [turn('s1', [{ part: 'system_prompt', text: 'short', tokens: 5800 }])],
      approximateO200kBase,
    )

    expect((row.payload as any).context.turns[0].buckets.system_prompt).toBe(5800)
  })

  it('excludes turns with no measured input, and says how many', () => {
    // A turn carrying content but no counters yields a negative residual —
    // a fact about the absent counter, not about the model's context.
    const row = buildSessionRow(
      'sess-1',
      [
        turn('s1', [{ part: 'system_prompt', text: 'a'.repeat(400) }]),
        turn('s2', [{ part: 'system_prompt', text: 'a'.repeat(400) }], {
          tokens: tokens({ freshInput: 0, reportedInput: 0 }),
        }),
      ],
      approximateO200kBase,
    )
    const context = (row.payload as any).context

    expect(context.turns).toHaveLength(1)
    expect(context.unmeasuredTurns).toBe(1)
    expect(context.turns[0].residual.tokens).toBeGreaterThanOrEqual(0)
  })

  it('ranks each tool in an aggregate catalogue, not the catalogue as one tool', () => {
    // Harnesses send the whole tool list as a single JSON array. Left
    // unsplit it ranks as one tool whose name is the entire blob.
    const row = buildSessionRow(
      'sess-1',
      [
        turn('s1', [
          {
            part: 'tool_definitions',
            text: JSON.stringify([{ name: 'view_file' }, { name: 'run_command' }]),
            tokens: 4200,
          },
        ]),
      ],
      approximateO200kBase,
    )

    // Ranked by descending resident cost (R8.1), not by the order they
    // arrived in — so assert membership, and that the ranking is sorted.
    const tools = (row.payload as any).tools
    expect(tools.map((t: any) => t.name).sort()).toEqual(['run_command', 'view_file'])
    expect(tools[0].total_schema_cost).toBeGreaterThanOrEqual(tools[1].total_schema_cost)
  })

  it('reads a name out of an OpenAI-shaped definition', () => {
    const row = buildSessionRow(
      'sess-1',
      [
        turn('s1', [
          {
            part: 'tool_definitions',
            text: JSON.stringify([{ type: 'function', function: { name: 'get_weather' } }]),
          },
        ]),
      ],
      approximateO200kBase,
    )

    expect((row.payload as any).tools[0].name).toBe('get_weather')
  })

  it('reports the session header the list view shows', () => {
    const row = buildSessionRow(
      'sess-1',
      [
        turn('s1', [{ part: 'system_prompt', text: 'x' }], {
          raw: {
            'gen_ai.agent.name': 'antigravity',
            'vcs.repository.name': 'kyber-weave',
            'vcs.ref.head.name': 'main',
          },
        }),
      ],
      approximateO200kBase,
    )

    expect(row.agentName).toBe('antigravity')
    expect(row.repo).toBe('kyber-weave')
    expect(row.branch).toBe('main')
    expect(row.harness).toBe('gemini')
  })
})

describe('buildSessions', () => {
  it('builds from the store and is rebuildable', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([turn('s1', [{ part: 'system_prompt', text: 'hello', tokens: 10 }])])

    expect((await buildSessions(store)).built).toBe(1)
    expect(store.sessionCount()).toBe(1)
    // A rebuild is idempotent — the row is a cache over `records`.
    expect((await buildSessions(store)).built).toBe(1)
    expect(store.sessionCount()).toBe(1)
    expect((store.getSessionPayload('sess-1') as any).harness).toBe('gemini')
    store.close()
  })

  it('skips records that carry no evidence at all', async () => {
    // The live receiver stores every span it receives, including ones with
    // no attributes and no counters, all stamped `op: llm.invoke`. Those
    // must not become sessions.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('empty-1', [], {
        sessionId: 'sess-empty',
        harness: 'unattributed',
        tokens: tokens({ freshInput: 0, output: 0, reportedInput: 0, reportedOutput: 0 }),
      }),
    ])

    const report = await buildSessions(store)

    expect(report.built).toBe(0)
    expect(report.skipped).toBe(1)
    store.close()
  })

  it('prunes rows whose session no longer builds', async () => {
    const store = new CanonStore(':memory:')
    store.upsertSession({ sessionId: 'stale', harness: 'gemini', payload: {} })
    store.upsertMany([turn('s1', [{ part: 'system_prompt', text: 'hello', tokens: 10 }])])

    const report = await buildSessions(store)

    expect(report.pruned).toBe(1)
    expect(store.builtSessionIds()).toEqual(['sess-1'])
    store.close()
  })
})
