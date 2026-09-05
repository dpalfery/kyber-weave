import { readFileSync } from 'node:fs'

import { describe, expect, it } from 'vitest'

import { getMeasurability } from './measurability.js'
import { CanonStore } from './store.js'
import { approximateO200kBase } from './tokens.js'
import { buildSessionRow, buildSessions, mergeMeasurability } from './sessions.js'
import type { AsadSessionPayload } from './sessions.js'
import type { CanonicalRecord, ContentPart } from './types.js'

type SessionPayloadView = AsadSessionPayload & {
  context: AsadSessionPayload['context'] & {
    measurable?: boolean
    unmeasuredTurns?: number
    turns?: Array<{
      toolDefinitionsByServer?: Record<string, number>
      builtinToolDefinitionTokens?: number
      buckets?: Record<string, unknown>
      residual?: { tokens: number }
    }>
    first: {
      buckets: Record<string, { availability?: string; reason?: string } | number>
      reported_input?: unknown
    }
  }
  schema?: { availability?: string; reason?: string }
  timeline?: { availability?: string; reason?: string } | unknown[]
}

function sessionPayload(row: { payload: unknown }): SessionPayloadView {
  return row.payload as SessionPayloadView
}

function unavailableReason(value: unknown, label: string): string {
  expect(value, label).toMatchObject({ availability: 'not_measurable' })
  if (typeof value === 'object' && value !== null && 'reason' in value && typeof value.reason === 'string') {
    return value.reason
  }
  throw new Error(`${label} is missing a reason`)
}

type JsonShape = null | boolean | number | string | JsonShape[] | { [key: string]: JsonShape }

const asadSessionShape = JSON.parse(
  readFileSync(new URL('./fixtures/asad-session-shape.json', import.meta.url), 'utf8'),
) as JsonShape

function expectJsonShape(
  actual: unknown,
  expected: JsonShape,
  path = 'payload',
  governed = false,
): void {
  if (Array.isArray(expected)) {
    expect(Array.isArray(actual), `${path} must be an array`).toBe(true)
    if (expected.length > 0) {
      expect(actual, `${path} must contain a representative item`).not.toHaveLength(0)
      expectJsonShape((actual as unknown[])[0], expected[0]!, `${path}[0]`, true)
    }
    return
  }

  if (expected !== null && typeof expected === 'object') {
    expect(actual, `${path} must be an object`).not.toBeNull()
    expect(typeof actual, `${path} must be an object`).toBe('object')
    if (governed) {
      expect(Object.keys(actual as object).sort(), `${path} must have exactly the governed keys`).toEqual(
        Object.keys(expected).sort(),
      )
    }
    for (const [key, nestedExpected] of Object.entries(expected)) {
      expectJsonShape((actual as Record<string, unknown>)[key], nestedExpected, `${path}.${key}`, governed)
    }
    return
  }

  expect(typeof actual, `${path} must preserve its JSON type`).toBe(typeof expected)
}

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
    const context = sessionPayload(row).context
    const firstTurn = context.turns?.[0]

    expect(context.measurable).toBe(true)
    expect(firstTurn?.toolDefinitionsByServer).toEqual({ context7: 300, codegraph: 200 })
    // A definition naming no server is counted, never guessed into a group.
    expect(firstTurn?.builtinToolDefinitionTokens).toBe(100)
  })

  it('prefers a harness-reported count over tokenizing the text', () => {
    const row = buildSessionRow(
      'sess-1',
      [turn('s1', [{ part: 'system_prompt', text: 'short', tokens: 5800 }])],
      approximateO200kBase,
    )

    expect(sessionPayload(row).context.turns?.[0]?.buckets?.system_prompt).toBe(5800)
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
    const context = sessionPayload(row).context

    expect(context.turns).toHaveLength(1)
    expect(context.unmeasuredTurns).toBe(1)
    expect(context.turns?.[0]?.residual?.tokens).toBeGreaterThanOrEqual(0)
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
    const tools = sessionPayload(row).tools
    expect(tools).toHaveLength(2)
    expect(tools[0].schema_tokens).toBeGreaterThanOrEqual(tools[1].schema_tokens)
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

    expect(sessionPayload(row).tools).toHaveLength(1)
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

  it('serializes capture-off content, schema, and execution structure as unavailable buckets with reasons', () => {
    const source = 'codeburn/claude-code'
    const declared = getMeasurability(source, 'claude-code')
    const row = buildSessionRow(
      'sess-capture-off',
      [
        turn('capture-off', [], {
          source,
          harness: 'claude-code',
          measurability: declared,
        }),
      ],
      approximateO200kBase,
    )
    const payload = JSON.parse(JSON.stringify(row.payload)) as SessionPayloadView

    const bucketDeclarations = {
      system_prompt: 'system_prompt',
      conversation_history: 'conversation_history',
      tool_definitions: 'tool_definitions',
      tool_results: 'tool_result_content',
    } as const
    for (const [bucket, declaration] of Object.entries(bucketDeclarations)) {
      expect(unavailableReason(payload.context.first.buckets[bucket], `${bucket} must not become a zero bucket`)).toContain(
        declaration,
      )
    }
    expect(unavailableReason(payload.schema, 'schema')).toMatch(/claude|session file/i)
    expect(unavailableReason(payload.timeline, 'timeline')).toMatch(/claude|session file/i)
  })

  it('merges missing counter declarations without serializing unavailable totals as zero', () => {
    const unavailableCounter = {
      availability: 'not_measurable' as const,
      reason: 'Cursor hook events do not include input-token counters.',
    }
    const record = turn('missing-counter', [], {
      source: 'cursor-hook',
      harness: 'cursor',
      tokens: tokens({ freshInput: 0, reportedInput: 0 }),
      measurability: { token_usage: unavailableCounter },
    })

    const merged = mergeMeasurability([record]) as Record<string, unknown>
    expect(merged.token_usage).toEqual(unavailableCounter)

    const row = buildSessionRow('sess-missing-counter', [record], approximateO200kBase)
    const payload = JSON.parse(JSON.stringify(row.payload)) as SessionPayloadView

    expect(payload.summary.total_input).toEqual(unavailableCounter)
    expect(payload.context.first.reported_input).toEqual(unavailableCounter)
    expect(JSON.stringify(payload)).not.toContain('"total_input":0')
    expect(JSON.stringify(payload)).not.toContain('"reported_input":0')
  })
})

describe('buildSessions', () => {
  it('stores the JSON-safe ASAD payload contract', async () => {
    const store = new CanonStore(':memory:')
    const records = [
      turn('s1', [
        { part: 'system_prompt', text: 'system', tokens: 100 },
        { part: 'tool_definitions', text: '{"name":"search"}', tokens: 100, server: 'mcp' },
      ]),
    ]
    const projected = buildSessionRow('sess-1', records, approximateO200kBase)
    const serializedProjection = JSON.parse(JSON.stringify(projected.payload)) as unknown

    // `analyzeContext` and `rankSchemas` use Maps internally. Validate the
    // projection before SQLite's own JSON round trip could hide a lost map or
    // a discriminated measurability branch.
    expect((serializedProjection as SessionPayloadView).tools).toHaveLength(1)
    expectJsonShape(serializedProjection, asadSessionShape)

    store.upsertMany(records)

    await buildSessions(store)
    const payload = store.getSessionPayload('sess-1')
    const roundTripped = JSON.parse(JSON.stringify(payload)) as unknown

    // The persisted read path is the dashboard contract too.
    expect(roundTripped).toEqual(payload)
    expectJsonShape(roundTripped, asadSessionShape)
    store.close()
  })

  it('builds from the store and is rebuildable', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([turn('s1', [{ part: 'system_prompt', text: 'hello', tokens: 10 }])])

    expect((await buildSessions(store)).built).toBe(1)
    expect(store.sessionCount()).toBe(1)
    // A rebuild is idempotent — the row is a cache over `records`.
    expect((await buildSessions(store)).built).toBe(1)
    expect(store.sessionCount()).toBe(1)
    expect((store.getSessionPayload('sess-1') as SessionPayloadView | undefined)?.harness).toBe('gemini')
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

describe('what counts as a session', () => {
  it('does not build a session from a trace with no model call', async () => {
    // The receiver ingests every span sent to it, including HTTP client and
    // server spans from instrumented libraries. Grouped by trace those looked
    // like 44 sessions in the real corpus, with no tokens and no content.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('http-1', [], {
        sessionId: 'trace-http',
        harness: 'unattributed',
        op: 'unspecified',
        name: 'GET /v1/messages',
        raw: { 'http.request.method': 'GET', 'url.full': 'https://example.invalid/x' },
        tokens: tokens({ freshInput: 0, output: 0, reportedInput: 0, reportedOutput: 0 }),
      }),
    ])

    const report = await buildSessions(store)

    expect(report.built).toBe(0)
    expect(report.skipped).toBe(1)
    store.close()
  })

  it('still builds when a model call is present alongside ambient spans', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('http-1', [], {
        spanId: 'http-1',
        op: 'unspecified',
        tokens: tokens({ freshInput: 0, output: 0, reportedInput: 0, reportedOutput: 0 }),
      }),
      turn('llm-1', [{ part: 'system_prompt', text: 'hello', tokens: 10 }], { spanId: 'llm-1' }),
    ])

    expect((await buildSessions(store)).built).toBe(1)
    store.close()
  })
})
