import { readFileSync } from 'node:fs'

import { describe, expect, it, vi } from 'vitest'

import { tokens, turn } from './fixtures/records.js'
import { getMeasurability, measurabilityFor } from './measurability.js'
import { CanonStore } from './store.js'
import { activeTokenizer, cacheKey, approximateO200kBase } from './tokens.js'
import { buildSessionRow, buildSessions, mergeMeasurability } from './sessions.js'
import type { AsadSessionPayload } from './sessions.js'
import type { CanonicalRecord, SessionRow } from './types.js'

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
  summary: AsadSessionPayload['summary'] & {
    tool_calls?: number
    tools_invoked?: string[]
    tools_offered?: string[]
    [key: string]: unknown
  }
  tools: Array<AsadSessionPayload['tools'][number] & {
    name?: string
    errors?: number
    invocations: number
    [key: string]: unknown
  }>
}

type SessionRowView = SessionRow & {
  summary?: {
    tool_calls?: number
    tools_invoked?: string[]
    tools_offered?: string[]
    [key: string]: unknown
  }
  payload: SessionPayloadView
}

function sessionRow(row: SessionRow): SessionRowView {
  return row as unknown as SessionRowView
}

function sessionPayload(row: { payload: unknown }): SessionPayloadView {
  return row.payload as SessionPayloadView
}

function toolInvoke(
  spanId: string,
  parentSpanId: string,
  name: string,
  over: Partial<CanonicalRecord> = {},
): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-1',
    parentSpanId,
    source: 'claude-code',
    harness: 'claude-code',
    sessionId: 'sess-1',
    name,
    op: 'tool.invoke',
    kind: 'internal',
    timestamp: '2026-09-03T10:00:01.000Z',
    durationMs: 50,
    status: 'ok',
    tokens: tokens({ freshInput: 0, output: 0, reportedInput: 0, reportedOutput: 0 }),
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: {
      'gen_ai.tool.name': name,
      ...((over.raw as Record<string, unknown> | undefined) ?? {}),
    },
    ...over,
  }
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

  it('keeps unparseable tool definitions out of the schema ranking without losing their residence', () => {
    // Neither blob yields a name. Keying the ranking by name collapses both
    // into one blank-named row carrying the first blob's cost; instead the
    // residence stays counted in the context bucket and no blank row ships.
    const row = buildSessionRow(
      'sess-1',
      [
        turn('s1', [
          { part: 'tool_definitions', text: 'tools:\n  - name: [unclosed', tokens: 300 },
          { part: 'tool_definitions', text: 'just some truncated prose, not a catalogue', tokens: 200 },
        ]),
        toolInvoke('t1', 's1', 'Read', { harness: 'antigravity', source: 'antigravity' }),
      ],
      approximateO200kBase,
    )
    const payload = sessionPayload(row)
    expect(payload.tools.some((tool) => tool.name === '')).toBe(false)
    expect(payload.tools.some((tool) => tool.name === 'Read')).toBe(true)
    expect(payload.context.turns?.[0]?.builtinToolDefinitionTokens).toBe(500)
    expect(payload.summary.tools_offered).toEqual([])
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

  // Issue #185: the summary never carried `cache_hit_ratio` or
  // `cache_creation_coverage`, so the session tiles rendered '—' and "on 0 turns"
  // beside measured totals. Both are emitted from measured figures only.
  it('emits measured cache_hit_ratio and cache_creation_coverage in the summary', () => {
    const row = buildSessionRow(
      'sess-cache',
      [
        turn('c1', [{ part: 'system_prompt', text: 'a'.repeat(400) }], {
          harness: 'copilot',
          tokens: tokens({ freshInput: 99000, cacheRead: 1700000, cacheCreation: 1000, reportedInput: 1800000 }),
        }),
        turn('c2', [{ part: 'system_prompt', text: 'b'.repeat(400) }], {
          harness: 'copilot',
          tokens: tokens({ freshInput: 9000, cacheRead: 86000, cacheCreation: 5000, reportedInput: 100000 }),
        }),
        turn('c3', [{ part: 'system_prompt', text: 'c'.repeat(400) }], {
          harness: 'copilot',
          tokens: tokens({ freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 50, reportedInput: 0 }),
        }),
      ],
      approximateO200kBase,
    )
    const summary = sessionPayload(row).summary as Record<string, unknown>
    expect(summary.total_input).toBe(1900000)
    expect(summary.total_cache_read).toBe(1786000)
    expect(summary.cache_hit_ratio as number).toBeCloseTo(0.94, 4)
    expect(summary.cache_creation_coverage).toBe(2)
  })

  it('omits cache_hit_ratio when input is unmeasurable, never zero', () => {
    const unavailableCounter = {
      availability: 'not_measurable' as const,
      reason: 'Cursor hook events do not include input-token counters.',
    }
    const row = buildSessionRow(
      'sess-cache-unmeasurable',
      [
        turn('cu1', [], {
          source: 'cursor-hook',
          harness: 'cursor',
          tokens: tokens({ freshInput: 0, reportedInput: 0 }),
          measurability: { token_usage: unavailableCounter },
        }),
      ],
      approximateO200kBase,
    )
    const summary = sessionPayload(row).summary as Record<string, unknown>
    expect(summary.total_input).toEqual(unavailableCounter)
    expect('cache_hit_ratio' in summary).toBe(false)
    // Review (Kilo K1 / Copilot C3): coverage with it — turns existing is not
    // a measured counter.
    expect('cache_creation_coverage' in summary).toBe(false)
    expect(JSON.stringify(summary)).not.toContain('"cache_hit_ratio":0')
  })

  // Review re-review #2 (Kilo 7): OpenCode is catalogued `not_measurable`
  // with documented confidence — a verified negative, not an uncatalogued
  // gap — so its cache figures stay absent like any unsupported harness.
  it('omits cache figures for a documented-unmeasurable harness', () => {
    const row = buildSessionRow(
      'sess-opencode-cache',
      [
        turn('o1', [{ part: 'system_prompt', text: 'a'.repeat(400) }], {
          harness: 'opencode',
          tokens: tokens({ freshInput: 1200, cacheRead: 0, cacheCreation: 0, reportedInput: 1200 }),
        }),
      ],
      approximateO200kBase,
    )
    const summary = sessionPayload(row).summary as Record<string, unknown>
    expect(summary.total_input).toBe(1200)
    expect('cache_hit_ratio' in summary).toBe(false)
    expect('cache_creation_coverage' in summary).toBe(false)
  })

  // Review re-review (Kilo 1): Codex is `supported` yet exports no
  // cache-creation counter, so coverage stays absent while the read ratio
  // (which Codex does export) is still emitted.
  it('omits creation coverage where the harness vocabulary holds no such counter', () => {
    const row = buildSessionRow(
      'sess-codex-cache',
      [
        turn('x1', [{ part: 'system_prompt', text: 'a'.repeat(400) }], {
          harness: 'codex',
          tokens: tokens({ freshInput: 900, cacheRead: 300, cacheCreation: 0, reportedInput: 1200 }),
        }),
      ],
      approximateO200kBase,
    )
    const summary = sessionPayload(row).summary as Record<string, unknown>
    expect(summary.total_input).toBe(1200)
    expect(summary.cache_hit_ratio as number).toBeCloseTo(0.25, 5)
    expect('cache_creation_coverage' in summary).toBe(false)
  })

  // Review re-review (Kilo 2): the ratio beside coverage already requires
  // input > 0; a measured zero input emits neither key.
  it('omits cache figures when the measured input is zero', () => {
    const row = buildSessionRow(
      'sess-zero-input',
      [
        turn('z1', [{ part: 'system_prompt', text: 'a'.repeat(400) }], {
          harness: 'copilot',
          tokens: tokens({ freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0 }),
        }),
      ],
      approximateO200kBase,
    )
    const summary = sessionPayload(row).summary as Record<string, unknown>
    expect(summary.total_input).toBe(0)
    expect('cache_hit_ratio' in summary).toBe(false)
    expect('cache_creation_coverage' in summary).toBe(false)
  })

  // Review (Copilot C2/C3): Cursor declares no cache counters, so even with a
  // measured input the cache figures are absent — never a 0% ratio or an
  // "on 0 turns" count beside stored zeros.
  it('omits cache figures for a harness that exports no cache counters', () => {
    const row = buildSessionRow(
      'sess-cursor-cache',
      [
        turn('cc1', [{ part: 'system_prompt', text: 'a'.repeat(400) }], {
          source: 'cursor-hook',
          harness: 'cursor',
          tokens: tokens({ freshInput: 1200, cacheRead: 0, cacheCreation: 0, reportedInput: 1200 }),
        }),
      ],
      approximateO200kBase,
    )
    const summary = sessionPayload(row).summary as Record<string, unknown>
    expect(summary.total_input).toBe(1200)
    expect('cache_hit_ratio' in summary).toBe(false)
    expect('cache_creation_coverage' in summary).toBe(false)
  })

  // Issue #187: the engine's measured per-turn input never reached the wire —
  // `TurnPressure` carries no input figure — so the composition view fell back to
  // `bucketedTokens` and reported residual 0.0% for unattributed input.
  it('carries the measured per-turn input on serialized context turns', () => {
    const row = buildSessionRow(
      'sess-reported',
      [
        turn('r1', [{ part: 'system_prompt', text: 'short', tokens: 124 }], {
          tokens: tokens({ freshInput: 66600, cacheRead: 0, cacheCreation: 0, reportedInput: 66600 }),
        }),
      ],
      approximateO200kBase,
    )
    const context = sessionPayload(row).context
    const wire = JSON.parse(JSON.stringify(context)) as {
      turns: Array<{ reported_input?: unknown; residual?: { tokens: number } }>
    }
    expect(wire.turns).toHaveLength(1)
    expect(wire.turns[0]!.reported_input).toBe(66600)
    expect(wire.turns[0]!.residual?.tokens).toBe(66600 - 124)
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
    expect(row.harness).toBe('antigravity')
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

  it('preserves unavailable bucket reasons over measured turns in partially measurable sessions', () => {
    const unavailableSystem = {
      availability: 'not_measurable' as const,
      reason: 'Copilot VS Code does not persist system prompts.',
    }
    const unavailableToolResult = {
      availability: 'not_measurable' as const,
      reason: 'Copilot VS Code does not persist tool result content.',
    }
    const record = turn(
      'copilot-partially-measurable',
      [{ part: 'conversation_history', text: 'user turn', tokens: 150 }],
      {
        source: 'copilot-vscode',
        harness: 'copilot-vscode',
        tokens: tokens({ freshInput: 150, reportedInput: 150 }),
        measurability: { system_prompt: unavailableSystem, tool_result_content: unavailableToolResult },
      },
    )

    const row = buildSessionRow('sess-partial', [record], approximateO200kBase)
    const payload = JSON.parse(JSON.stringify(row.payload)) as SessionPayloadView

    // conversation_history should be measured tokens
    expect(payload.context.first.buckets['conversation_history']).toBe(150)
    // system_prompt must retain its unavailable reason, NOT 0
    expect(unavailableReason(payload.context.first.buckets['system_prompt'], 'system_prompt')).toContain(
      'Copilot VS Code',
    )
    expect(payload.context.first.buckets['tool_result_content']).toEqual(unavailableToolResult)
    expect(payload.context.last.buckets['tool_result_content']).toEqual(unavailableToolResult)
  })

  it('keeps an observed bucket count over a session-wide unavailable declaration', () => {
    const unavailableToolResult = {
      availability: 'not_measurable' as const,
      reason: 'Earlier request did not persist tool result content.',
    }
    const earlier = turn(
      'mixed-earlier',
      [{ part: 'conversation_history', text: 'user turn', tokens: 150 }],
      {
        tokens: tokens({ freshInput: 150, reportedInput: 150 }),
        measurability: { tool_result_content: unavailableToolResult },
      },
    )
    const later = turn(
      'mixed-later',
      [
        { part: 'conversation_history', text: 'user turn', tokens: 150 },
        { part: 'tool_result_content', text: 'tool output', tokens: 40 },
      ],
      {
        timestamp: '2026-09-03T10:05:00.000Z',
        tokens: tokens({ freshInput: 190, reportedInput: 190 }),
      },
    )

    const row = buildSessionRow('sess-mixed', [earlier, later], approximateO200kBase)
    const payload = JSON.parse(JSON.stringify(row.payload)) as SessionPayloadView

    expect(payload.context.first.buckets['tool_result_content']).toEqual(unavailableToolResult)
    expect(payload.context.last.buckets['tool_result_content']).toBe(40)
  })

  it('populates tool metrics in session summary when tool invocations are present', () => {
    const parent = turn('turn-1', [{ part: 'system_prompt', text: 'help' }])
    const tool1 = toolInvoke('tool-1', 'turn-1', 'Bash', {
      raw: { 'gen_ai.tool.name': 'Bash', arguments: '{"command":"echo test"}' },
      status: 'ok',
    })
    const tool2 = toolInvoke('tool-2', 'turn-1', 'Read', {
      raw: { 'gen_ai.tool.name': 'Read', arguments: '{"path":"file.txt"}' },
      status: 'error',
    })

    const session = sessionRow(buildSessionRow('sess-tools', [parent, tool1, tool2], approximateO200kBase))

    // 1. Tool metrics populated in summary:
    // - session.summary.tool_calls is equal to the number of tool invocations (2)
    expect(session.summary?.tool_calls).toBe(2)
    // - session.summary.tools_invoked is an array of distinct or all invoked tool names (['Bash', 'Read'])
    expect(session.summary?.tools_invoked).toEqual(['Bash', 'Read'])

    // - session.payload.summary.tool_calls matches session.summary.tool_calls
    expect(session.payload.summary.tool_calls).toBe(session.summary?.tool_calls)
    expect(session.payload.summary.tool_calls).toBe(2)

    // - session.payload.summary.tools_invoked matches session.summary.tools_invoked
    expect(session.payload.summary.tools_invoked).toEqual(session.summary?.tools_invoked)
    expect(session.payload.summary.tools_invoked).toEqual(['Bash', 'Read'])

    // - session.payload.tools contains entries for invoked tools with invocation counts and error counts
    expect(session.payload.tools).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ name: 'Bash', invocations: 1, errors: 0 }),
        expect.objectContaining({ name: 'Read', invocations: 1, errors: 1 }),
      ]),
    )
  })

  it('preserves honest unobservability by keeping tools_offered undefined when unobserved', () => {
    const parent = turn('turn-1', [{ part: 'system_prompt', text: 'help' }])
    const tool1 = toolInvoke('tool-1', 'turn-1', 'Bash')

    // Records do NOT contain any tool_definition or tool_definitions parts (unobserved)
    const session = sessionRow(buildSessionRow('sess-unobserved', [parent, tool1], approximateO200kBase))

    // Must be undefined (not an empty array or static harness defaults)
    expect(session.summary?.tools_offered).toBeUndefined()
    expect(session.payload.summary.tools_offered).toBeUndefined()
    expect(session.summary?.tools_offered).not.toEqual([])
    expect(session.payload.summary.tools_offered).not.toEqual([])
  })

  it('populates tools_offered when tool definition parts are observed in records', () => {
    const parentWithDefs = turn('turn-defs', [
      {
        part: 'tool_definitions',
        text: JSON.stringify([{ name: 'Bash' }, { name: 'Read' }, { name: 'Glob' }]),
        tokens: 300,
      },
    ])
    const tool1 = toolInvoke('tool-1', 'turn-defs', 'Bash')

    const session = sessionRow(buildSessionRow('sess-observed', [parentWithDefs, tool1], approximateO200kBase))

    expect(session.summary?.tools_offered).toEqual(expect.arrayContaining(['Bash', 'Read', 'Glob']))
    expect(session.payload.summary.tools_offered).toEqual(session.summary?.tools_offered)
  })
  it("does not evaluate lazy record.content getter when record.parts is provided", () => {
    const contentGetterSpy = vi.fn().mockReturnValue({})
    const record = turn("turn-lazy-check", [
      {
        part: "tool_definitions",
        text: JSON.stringify([{ name: "Bash" }]),
        tokens: 100,
      },
    ])
    Object.defineProperty(record, "content", {
      get: contentGetterSpy,
      configurable: true,
      enumerable: true,
    })

    buildSessionRow("sess-lazy", [record], approximateO200kBase)

    expect(contentGetterSpy).not.toHaveBeenCalled()
  })


  it('projects 0 tool calls when Claude session has zero tool records, while unmeasured harnesses remain undefined (Thread 3)', () => {
    // Claude session where tool calls are measurable, but zero were invoked:
    const claudeTurnNoTools = turn('claude-turn-0', [{ part: 'system_prompt', text: 'hello' }], {
      source: 'codeburn/claude',
      harness: 'claude',
      measurability: measurabilityFor('claude'),
    })
    const claudeSession = sessionRow(
      buildSessionRow('sess-claude-no-tools', [claudeTurnNoTools], approximateO200kBase),
    )
    expect(claudeSession.summary?.tool_calls).toBe(0)
    expect(claudeSession.payload.summary.tool_calls).toBe(0)
    expect(claudeSession.summary?.tools_invoked).toEqual([])
    expect(claudeSession.payload.summary.tools_invoked).toEqual([])

    // Unmeasured harness session (e.g. kilo, opencode) where tool calls are not supported:
    const unmeasuredTurn = turn('kilo-turn-0', [{ part: 'system_prompt', text: 'hello' }], {
      source: 'codeburn/kilo',
      harness: 'kilo',
      measurability: measurabilityFor('kilo'),
    })
    const unmeasuredHarnessSession = sessionRow(
      buildSessionRow('sess-kilo-no-tools', [unmeasuredTurn], approximateO200kBase),
    )
    expect(unmeasuredHarnessSession.summary?.tool_calls).toBeUndefined()
    expect(unmeasuredHarnessSession.payload.summary.tool_calls).toBeUndefined()
    expect(unmeasuredHarnessSession.summary?.tools_invoked).toBeUndefined()
    expect(unmeasuredHarnessSession.payload.summary.tools_invoked).toBeUndefined()
  })

  it('preserves empty array tools_offered when empty tool definitions were observed (Thread 4)', () => {
    // Observed empty tool catalogue:
    const turnWithEmptyCatalog = turn('turn-empty-cat', [
      { part: 'system_prompt', text: 'hi' },
      { part: 'tool_definitions', text: '[]' },
    ])
    const sessionWithEmptyCatalog = sessionRow(
      buildSessionRow('sess-empty-cat', [turnWithEmptyCatalog], approximateO200kBase),
    )
    expect(sessionWithEmptyCatalog.summary?.tools_offered).toEqual([])
    expect(sessionWithEmptyCatalog.payload.summary.tools_offered).toEqual([])

    // Unobserved tool definitions (no tool_definitions parts):
    const turnNoDefinitions = turn('turn-no-defs', [{ part: 'system_prompt', text: 'hi' }])
    const sessionNoDefinitions = sessionRow(
      buildSessionRow('sess-no-defs', [turnNoDefinitions], approximateO200kBase),
    )
    expect(sessionNoDefinitions.summary?.tools_offered).toBeUndefined()
    expect(sessionNoDefinitions.payload.summary.tools_offered).toBeUndefined()
  })

  it('distinguishes unmeasured tool calls from zero tool calls when harness does not report tools', () => {
    // When no tool records are present and harness did not report tools
    const parentWithoutTools = turn('turn-no-tools', [{ part: 'system_prompt', text: 'plain conversation' }], {
      source: 'claude-code',
      harness: 'claude-code',
      measurability: {
        tool_calls: {
          availability: 'not_measurable',
          reason: 'Tool invocation count was not reported by claude-code.',
        },
      },
    })

    const unmeasuredSession = sessionRow(
      buildSessionRow('sess-no-tools', [parentWithoutTools], approximateO200kBase),
    )

    // tool_calls must be undefined (unmeasured), NEVER coerced to 0
    expect(unmeasuredSession.summary?.tool_calls).toBeUndefined()
    expect(unmeasuredSession.payload.summary.tool_calls).toBeUndefined()
    expect(unmeasuredSession.summary?.tools_invoked).toBeUndefined()
    expect(unmeasuredSession.payload.summary.tools_invoked).toBeUndefined()

    // Conversely, when tools ARE present and measured, tool_calls must be a valid count
    const parentWithTool = turn('turn-with-tool', [{ part: 'system_prompt', text: 'help' }])
    const tool = toolInvoke('tool-1', 'turn-with-tool', 'Bash')
    const measuredSession = sessionRow(
      buildSessionRow('sess-measured-tool', [parentWithTool, tool], approximateO200kBase),
    )

    expect(measuredSession.summary?.tool_calls).toBe(1)
    expect(measuredSession.payload.summary.tool_calls).toBe(1)
    expect(measuredSession.summary?.tools_invoked).toEqual(['Bash'])
    expect(measuredSession.payload.summary.tools_invoked).toEqual(['Bash'])
  })

  it('populates total_schema_cost on named tools rows in session payload when tools are invoked (Thread 1)', () => {
    const parentWithDefs = turn('turn-defs-cost', [
      { part: 'tool_definitions', text: JSON.stringify([{ name: 'Bash', description: 'Execute shell command' }]) },
    ])
    const tool = toolInvoke('tool-cost-1', 'turn-defs-cost', 'Bash')
    const session = sessionRow(
      buildSessionRow('sess-cost-check', [parentWithDefs, tool], approximateO200kBase),
    )

    expect(session.payload.tools).toBeDefined()
    const bashTool = session.payload.tools?.find((t) => t.name === 'Bash')
    expect(bashTool).toBeDefined()
    expect(bashTool?.name).toBe('Bash')
    expect(bashTool?.total_schema_cost).toBeDefined()
    expect(bashTool?.total_schema_cost).toBeGreaterThan(0)
  })

  it('extracts tool definitions wrapped in {"tools":[...]} consistently (Thread 2)', () => {
    const wrappedDefs = JSON.stringify({
      tools: [
        { name: 'ToolA', description: 'First tool' },
        { name: 'ToolB', description: 'Second tool' },
      ],
    })
    const parent = turn('turn-wrapped-defs', [
      { part: 'tool_definitions', text: wrappedDefs },
    ])
    const tool = toolInvoke('tool-wrapped-1', 'turn-wrapped-defs', 'ToolA')
    const session = sessionRow(
      buildSessionRow('sess-wrapped-defs', [parent, tool], approximateO200kBase),
    )

    expect(session.summary?.tools_offered).toEqual(['ToolA', 'ToolB'])
    const toolNames = session.payload.tools?.map((t) => t.name)
    expect(toolNames).toContain('ToolA')
    expect(toolNames).toContain('ToolB')
    expect(toolNames?.some((n) => n && n.includes('{'))).toBe(false)
  })

  it('does not promote unparseable / non-JSON tool definitions text into tool names (Thread 3)', () => {
    const badPart = turn('turn-bad-defs', [
      { part: 'tool_definitions', text: '--- non-json YAML or raw catalogue ---' },
    ])
    const session = sessionRow(
      buildSessionRow('sess-bad-defs', [badPart], approximateO200kBase),
    )

    expect(session.summary?.tools_offered).toEqual([])
    expect(session.payload.tools?.some((t) => t.name?.includes('non-json'))).toBe(false)
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
    expect((store.getSessionPayload('sess-1') as SessionPayloadView | undefined)?.harness).toBe('antigravity')
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
    store.upsertSession({ sessionId: 'stale', harness: 'antigravity', payload: {} })
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

describe('buildSessions derived-table completeness', () => {
  // Regression: `kyber build` used to rebuild sessions and runs but not the
  // tables downstream of them. The dashboard then listed runs it could not
  // score, because `/api/kyber/harness/:id` 404s on a missing rollup and the
  // scorecard rendered that as "telemetry missing" — a measurement claim about
  // telemetry that had in fact been collected.
  it('rebuilds harness rollups alongside sessions and runs', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('s1', [{ part: 'system_prompt', text: 'hello', tokens: 10 }]),
    ])

    const report = await buildSessions(store)

    expect(report.rollups).toBeGreaterThan(0)
    expect(store.getHarnessRollup('antigravity')).toBeDefined()
    expect(store.getHarnessRollup('antigravity')?.sampleCount).toBeGreaterThan(0)
    store.close()
  })

  it('runs the finding detectors so an unexamined corpus never reads as clean', async () => {
    const store = new CanonStore(':memory:')
    // A tool definition resident across turns without ever being invoked is the
    // dormant-tool-schema detector's deterministic case.
    store.upsertMany([
      turn('s1', [
        { part: 'tool_definitions', text: '{"name":"search"}', tokens: 4000, server: 'mcp' },
        { part: 'system_prompt', text: 'system', tokens: 100 },
      ]),
      turn('s2', [
        { part: 'tool_definitions', text: '{"name":"search"}', tokens: 4000, server: 'mcp' },
        { part: 'system_prompt', text: 'system', tokens: 100 },
      ], { timestamp: '2026-09-03T10:05:00.000Z' }),
    ])

    const report = await buildSessions(store)

    expect(report.findings).toBe(store.listFindings().length)
    // Every persisted finding carries the harness that produced it, so the
    // dashboard can scope findings to one harness tab.
    for (const finding of store.listFindings()) {
      expect((finding as { harness?: string }).harness).toBe('antigravity')
      expect(finding.runId).toBeTruthy()
    }
    store.close()
  })

  it('prunes findings whose evidence no longer builds', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('s1', [
        { part: 'tool_definitions', text: '{"name":"search"}', tokens: 4000, server: 'mcp' },
        { part: 'system_prompt', text: 'system', tokens: 100 },
      ]),
    ])
    await buildSessions(store)

    store.upsertFinding({
      id: 'stale-finding',
      detectorId: 'duplicate-tool-call',
      title: 'Stale',
      mechanism: 'Left by an earlier build',
      evidenceLinks: [],
      confidence: 'deterministic',
      estimatedWasteTokens: 1,
      recommendation: 'none',
      errorBar: { lower: 0, upper: 2 },
      outcomeRiskCaveat: 'none',
      runId: 'run-that-no-longer-exists',
    })
    expect(store.getFinding('stale-finding')).toBeDefined()

    // A rebuild is authoritative for `finding` exactly as it is for `run`.
    await buildSessions(store)
    expect(store.getFinding('stale-finding')).toBeUndefined()
    store.close()
  })
})

describe('buildSessions token cache', () => {
  // `token_cache` is provisioned by R4.6 and was reached by nothing: the build
  // passed the bare encoder, so every rebuild re-tokenized the whole corpus
  // and the table had never held a row. These tests hold the wire in place.

  // A single named definition in canonical spaceless JSON, long enough to
  // engage the token cache (>= MIN_CACHED_TEXT_LENGTH): the item text the
  // ranker counts is byte-identical to the blob, so the poisoned cache key
  // hits. (Unnamed blobs are excluded from the schema ranking, so a
  // schemaless blob can no longer carry the cache assertion.)
  const definition = `{"name":"search","description":"${'does something useful; '.repeat(40)}"}`

  function cacheRows(store: CanonStore): number {
    return (
      store.tokenCache().prepare('SELECT COUNT(*) AS n FROM token_cache').get() as { n: number }
    ).n
  }

  it('populates the cache on a first build', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([turn('s1', [{ part: 'tool_definitions', text: definition }])])

    expect(cacheRows(store)).toBe(0)
    await buildSessions(store)

    expect(cacheRows(store)).toBeGreaterThan(0)
    store.close()
  })

  it('reads the cache on a second build instead of re-tokenizing', async () => {
    // Poisoning one row is the only way to observe a hit from outside: if the
    // build consults the table, the planted count reaches the payload; if it
    // re-tokenizes, the real count does.
    const store = new CanonStore(':memory:')
    store.upsertMany([turn('s1', [{ part: 'tool_definitions', text: definition }])])

    await buildSessions(store)
    const honest = sessionPayload({ payload: store.getSessionPayload('sess-1') }).tools[0]?.schema_tokens
    expect(honest).toBeGreaterThan(0)

    store
      .tokenCache()
      .prepare('INSERT OR REPLACE INTO token_cache (hash, count, model) VALUES (?, ?, ?)')
      .run(cacheKey(definition, await activeTokenizer()), 999_999, await activeTokenizer())

    await buildSessions(store)

    expect(sessionPayload({ payload: store.getSessionPayload('sess-1') }).tools[0]?.schema_tokens).toBe(
      999_999,
    )
    store.close()
  })

  it('leaves the built payload unchanged between a cold and a warm build', async () => {
    // The memo may change what a rebuild costs, never what it produces.
    const cold = new CanonStore(':memory:')
    cold.upsertMany([turn('s1', [{ part: 'tool_definitions', text: definition }])])
    await buildSessions(cold)
    const first = JSON.stringify(cold.getSessionPayload('sess-1'))

    await buildSessions(cold)
    const second = JSON.stringify(cold.getSessionPayload('sess-1'))

    expect(second).toBe(first)
    cold.close()
  })
})

describe('canonical harness on derived sessions', () => {
  // `run`, `execution` and `listHarnesses` are all keyed by the canonical
  // harness. Storing the raw provider entry on the session meant the rollup's
  // `listSessions(harness)` never matched those sessions: on the measured
  // corpus the claude-code scorecard computed context pressure, cache hit rate
  // and tool yield from 1 session while 58 more sat under `claude`, and still
  // reported 59 samples because that count comes from runs.
  // Cursor lost 10 sessions the same way.

  function aliased(spanId: string, harness: string, sessionId: string): CanonicalRecord {
    return {
      spanId,
      traceId: 'trace-1',
      parentSpanId: null,
      source: harness,
      harness,
      sessionId,
      name: 'llm_request',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-03T10:00:00.000Z',
      durationMs: 10,
      status: 'ok',
      tokens: {
        reportedInput: 100,
        reportedOutput: 10,
        freshInput: 100,
        cacheRead: 0,
        cacheCreation: 0,
        output: 10,
        input: 100,
      } as CanonicalRecord['tokens'],
      content: { system_prompt: 'hello' },
      cost: { basis: 'unknown', status: 'no_rate' },
    }
  }

  it('stamps the canonical harness, not the front-end name', () => {
    // Issue #182: cursor-agent folds onto cursor at the derived layer.
    const row = buildSessionRow('s1', [aliased('a1', 'cursor-agent', 's1')], approximateO200kBase)

    expect(row.harness).toBe('cursor')
    expect((row.payload as { harness: string }).harness).toBe('cursor')
  })

  it('maps a generic Claude client to unclassified rather than guessing CLI', () => {
    const row = buildSessionRow('s1', [aliased('a1', 'claude', 's1')], approximateO200kBase)

    expect(row.harness).toBe('claude-unclassified')
  })

  it('leaves a session findable by the harness its runs are keyed under', async () => {
    // The end-to-end shape of the bug: build from records that arrived under an
    // alias, then look the session up the way the rollup does.
    const store = new CanonStore(':memory:')
    store.upsertMany([aliased('a1', 'cursor-agent', 'sess-alias')])

    await buildSessions(store)

    expect(store.listSessions('cursor')).toHaveLength(1)
    expect(store.listSessions('cursor-agent')).toHaveLength(0)
    store.close()
  })

  it('merges twin front-ends into one session and one run', async () => {
    // Issue #182: one native id on cursor + cursor-agent is one session.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      aliased('a1', 'cursor', 'sess-alias'),
      { ...aliased('a2', 'cursor-agent', 'sess-alias'), timestamp: '2026-09-03T10:00:05.000Z' },
    ])

    const report = await buildSessions(store)

    expect(report.built).toBe(1)
    expect(store.sessionCount()).toBe(1)
    expect(store.listSessions('cursor')).toHaveLength(1)
    expect(store.listSessions('cursor-agent')).toHaveLength(0)
    const runs = store.listRuns()
    expect(runs).toHaveLength(1)
    for (const run of runs) {
      expect(store.listExecutions(run.runId).length).toBeGreaterThan(0)
    }
    store.close()
  })

  it('counts #231 cursor twin overlap output once on session totals', async () => {
    // Live evidence (issue #231, key 0f659701-…): cursor file halves
    // (17007/0, 0/45) plus cursor-agent (125/45) within skew. After T2's
    // complementary join, buildSessions must sum the overlapped 45 once
    // (~45, not ~90) — rebuild-only; raw dual rows stay in the store.
    const sessionKey = 'test-session-key-0001'
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('synth:cursor:req', [], {
        source: 'codeburn/cursor',
        harness: 'cursor',
        sessionId: sessionKey,
        timestamp: '2026-09-30T19:52:28.000Z',
        tokens: tokens({
          freshInput: 17007,
          cacheRead: 0,
          cacheCreation: 0,
          output: 0,
          reportedInput: 17007,
          reportedOutput: 0,
        }),
      }),
      turn('synth:cursor:res', [], {
        source: 'codeburn/cursor',
        harness: 'cursor',
        sessionId: sessionKey,
        timestamp: '2026-09-30T19:52:30.000Z',
        tokens: tokens({
          freshInput: 0,
          cacheRead: 0,
          cacheCreation: 0,
          output: 45,
          reportedInput: 0,
          reportedOutput: 45,
        }),
      }),
      turn('synth:cursor-agent:0', [], {
        source: 'codeburn/cursor-agent',
        harness: 'cursor-agent',
        sessionId: sessionKey,
        timestamp: '2026-09-30T19:52:33.181Z',
        tokens: tokens({
          freshInput: 125,
          cacheRead: 0,
          cacheCreation: 0,
          output: 45,
          reportedInput: 125,
          reportedOutput: 45,
        }),
      }),
    ])

    const report = await buildSessions(store)
    expect(report.built).toBe(1)

    const sessions = store.listSessions('cursor')
    expect(sessions).toHaveLength(1)
    const payload = sessionPayload({ payload: store.getSessionPayload(sessions[0]!.sessionId) })
    expect(payload.summary.total_output).toBe(45)
    expect(store.sessionTokenTotals(sessions[0]!.sessionId)?.output).toBe(45)
    // Without the join the three raw rows would sum to 90; prove the store
    // still holds all three provenance rows while the derived total is once.
    expect(store.recordsForSession(sessionKey)).toHaveLength(3)
    store.close()
  })
})

describe('timeline attributes are not re-stored in the payload', () => {
  // Each timeline node carried the record's whole raw span payload. That made
  // the derived cache a second, uncompressed copy of the corpus: 266 MB of one
  // 264 MB session payload, and nearly all of the 995 MB the session table
  // held on the measured corpus. The attributes stay reachable per span
  // through `CanonStore.spanAttributes` (R9.2); they are simply no longer
  // copied into every session row.

  const RAW = { 'gen_ai.request.model': 'claude-opus-5', 'kyber.prompt': 'y'.repeat(5_000) }

  function spanRecord(spanId: string, parentSpanId: string | null): CanonicalRecord {
    return {
      spanId,
      traceId: 'trace-tl',
      parentSpanId,
      source: 'claude-code',
      harness: 'claude-code',
      sessionId: 'sess-tl',
      name: 'llm_request',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-03T10:00:00.000Z',
      durationMs: 10,
      status: 'ok',
      tokens: {
        freshInput: 100,
        cacheRead: 0,
        cacheCreation: 0,
        output: 10,
        reportedInput: 100,
        reportedOutput: 10,
      } as CanonicalRecord['tokens'],
      content: { system_prompt: 'hello' },
      cost: { basis: 'unknown', status: 'no_rate' },
      raw: RAW,
    }
  }

  type TimelineShape = { spanId: string; attributes: Record<string, unknown>; children: TimelineShape[] }

  function flatten(nodes: TimelineShape[]): TimelineShape[] {
    return nodes.flatMap((node) => [node, ...flatten(node.children)])
  }

  it('leaves every timeline node with an empty attribute map', () => {
    const row = buildSessionRow(
      'sess-tl',
      [spanRecord('root', null), spanRecord('child', 'root')],
      approximateO200kBase,
    )

    const timeline = (row.payload as { timeline: TimelineShape[] }).timeline
    const nodes = flatten(timeline)
    expect(nodes.length).toBeGreaterThan(0)
    for (const node of nodes) {
      expect(node.attributes).toEqual({})
    }
  })

  it('keeps the prompt out of the serialized payload entirely', () => {
    const row = buildSessionRow('sess-tl', [spanRecord('root', null)], approximateO200kBase)

    expect(JSON.stringify(row.payload)).not.toContain('y'.repeat(5_000))
  })

  it('still preserves the node structure the renderer needs', () => {
    const row = buildSessionRow(
      'sess-tl',
      [spanRecord('root', null), spanRecord('child', 'root')],
      approximateO200kBase,
    )

    const nodes = flatten((row.payload as { timeline: TimelineShape[] }).timeline)
    expect(nodes.map((node) => node.spanId)).toContain('root')
    expect(nodes.map((node) => node.spanId)).toContain('child')
  })

  it('serves the same attributes back per span from the store', async () => {
    // The capability R9.2 asks for, relocated rather than removed.
    const store = new CanonStore(':memory:')
    store.upsertMany([spanRecord('root', null)])
    await buildSessions(store)

    expect(store.spanAttributes('root')).toEqual(RAW)
    expect(store.spanAttributes('absent')).toBeUndefined()
    store.close()
  })
})

describe('session cost totals (issue #186)', () => {
  const priced = (value: number): CanonicalRecord['cost'] => ({
    basis: 'published',
    status: 'priced',
    value,
    currency: 'USD',
    byModel: { 'claude-opus-5': value },
  })
  const toolSpan = (spanId: string): CanonicalRecord =>
    turn(spanId, [], {
      op: 'tool.execute',
      name: 'tool_call',
      parentSpanId: 's1',
      cost: { basis: 'unknown', status: 'no_rate' },
    })
  const costOf = (row: { payload: unknown }) =>
    (row.payload as { summary: { cost: unknown } }).summary.cost
  const problemsOf = (row: { payload: unknown }) =>
    (row.payload as { problems: Array<{ code?: string }> }).problems

  it('totals one priced turn, one unpriced published turn and a non-turn span as partial', () => {
    const row = buildSessionRow(
      'sess-1',
      [
        turn('s1', [], { cost: priced(0.25) }),
        turn('s2', [], { cost: { basis: 'published', status: 'no_rate' } }),
        toolSpan('t1'),
      ],
      approximateO200kBase,
    )

    expect(costOf(row)).toMatchObject({ basis: 'published', status: 'partial', value: 0.25 })
  })

  it('makes no cost claim for a non-turn span', () => {
    const row = buildSessionRow(
      'sess-1',
      [turn('s1', [], { cost: priced(0.25) }), toolSpan('t1')],
      approximateO200kBase,
    )

    expect(costOf(row)).toMatchObject({ basis: 'published', status: 'priced', value: 0.25 })
  })

  it('records COST_BASIS_MISMATCH in the payload when harness and published turns mix', () => {
    const row = buildSessionRow(
      'sess-1',
      [
        turn('s1', [], { cost: priced(0.25) }),
        turn('s2', [], {
          cost: { basis: 'harness', status: 'priced', value: 0.5, currency: 'USD' },
        }),
      ],
      approximateO200kBase,
    )

    expect(problemsOf(row).map((p) => p.code)).toContain('COST_BASIS_MISMATCH')
  })
})
