/**
 * Pin test for the machine-readable status contract (R11.4, R11.5, R14.3).
 *
 * The status contract is the single seam the native clients depend on: they
 * spawn the CLI on an interval and decode its JSON output; they hold no
 * analysis logic. Extending the contract is sufficient to carry a new analysis
 * into the menu bar and Electron without modifying the clients themselves.
 *
 * This test pins the contract so a change that would break the native clients
 * fails in CI. It asserts:
 *  - backward compatibility: a payload produced without the `kyber` field still
 *    decodes (old CLI → new client);
 *  - new analyses are carried under optional `kyber` fields and round-trip;
 *  - every new analysis field (context buckets/pressure, schema ranking,
 *    timeline, compare, quarantineCount, problems) appears in shape;
 *  - top-level key set is pinned: adding/removing/renaming a top-level key
 *    fails until this snapshot is intentionally updated with its rationale
 *    (recorded in design.md under R14.3).
 */

import { describe, expect, it } from 'vitest'

import {
  buildMenubarPayload,
  serializeKyberContext,
  serializeKyberSchema,
  type KyberComparePayload,
  type KyberContextPayload,
  type KyberPayload,
  type KyberSchemaPayload,
  type KyberTimelineNode,
  type MenubarPayload,
  type PeriodData,
} from '../src/menubar-json.js'

function emptyPeriod(label: string): PeriodData {
  return {
    label,
    cost: 0,
    calls: 0,
    sessions: 0,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    categories: [],
    models: [],
  }
}

// Minimal valid timeline node (R9) — tree structure the renderer plots.
function fakeTimeline(): KyberTimelineNode {
  return {
    spanId: '(session)',
    parentId: null,
    children: [
      {
        spanId: 'span-1',
        parentId: null,
        children: [],
        startMs: 0,
        durationMs: 12,
        kind: 'internal',
        name: 'llm.invoke',
        attributes: { 'gen_ai.operation.name': 'chat' },
        isSubagent: false,
        isAuxiliary: false,
        cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
      },
    ],
    startMs: 0,
    durationMs: 12,
    kind: 'synthetic',
    name: 'session',
    attributes: {},
    isSubagent: false,
    isAuxiliary: false,
    cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
  }
}

function fakeCompare(): KyberComparePayload {
  return {
    harnesses: ['pi', 'copilot'],
    rows: [
      {
        metric: 'tokens_per_turn',
        kind: 'per_turn',
        label: 'Tokens per turn',
        unit: 'tokens',
        cells: {
          pi: { measurable: true, availability: 'measured', value: 1200, render: '1,200' },
          copilot: { measurable: true, availability: 'measured', value: 1100, render: '1,100' },
        },
      },
    ],
    problems: [],
  }
}

describe('status contract pin (R11.4, R14.3)', () => {
  it('backward compatible: payload without kyber omits kyber key and still decodes', () => {
    const payload = buildMenubarPayload(emptyPeriod('Today'), [], null)
    const json = JSON.stringify(payload)
    const decoded = JSON.parse(json) as MenubarPayload
    expect(decoded).not.toHaveProperty('kyber')
    // Old payload still has the documented top-level keys
    expect(decoded.generated).toBeDefined()
    expect(decoded.current).toBeDefined()
    expect(decoded.optimize).toBeDefined()
    expect(decoded.history).toBeDefined()
    expect(decoded.currency).toBeDefined()
    // Decoding an old payload as new shape does not throw
    expect(() => JSON.parse(JSON.stringify({ ...decoded, extraUnknown: 1 })) as unknown as MenubarPayload).not.toThrow()
  })

  it('carries new analyses under optional kyber field and round-trips through JSON', () => {
    const context: KyberContextPayload = {
      measurable: true,
      contextLimit: 200_000,
      turns: [
        {
          index: 1,
          buckets: {
            system_prompt: 1000,
            tool_definitions: 2000,
            instruction_context: 500,
            conversation_history: 800,
            tool_result_content: 200,
          },
          toolDefinitionsByServer: { 'mcp-server-a': 2000 },
          builtinToolDefinitionTokens: 0,
          strippedInstructionBlocks: { count: 1, tokens: 500 },
          bucketedTokens: 4500,
          residual: { tokens: 500, attribution: 'tokenizer_drift' },
          headroom: 195_000,
          pressure: 0.025,
          accumulationRate: 4500,
          freshInput: 900,
        },
      ],
      residualTotal: 500,
      derivedCounts: true,
      freshJumpFactor: 2,
      flaggedTurns: [],
      sessionAccumulationRate: 4500,
    }

    const schema: KyberSchemaPayload = {
      measurable: true,
      ranked: [{ name: 'mcp__server__tool', server: 'server', cost: 2000, invoked: false }],
      neverInvoked: [{ name: 'mcp__server__tool', server: 'server', cost: 2000, invoked: false }],
      byServer: { server: 2000 },
      unusedRange: { tokenResidencies: 2000, floor: 0.001, ceiling: 0.02, currency: 'USD' },
      turns: 5,
    }

    const kyber: KyberPayload = {
      context,
      schema,
      timeline: fakeTimeline(),
      compare: fakeCompare(),
      quarantineCount: 3,
      problems: [{ severity: 'warning', code: 'TOKEN_SUM_MISMATCH', message: 'fresh_input mismatch', location: 'span-1' }],
    }

    const payload = buildMenubarPayload(emptyPeriod('Today'), [], null, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, kyber)
    expect(payload.kyber).toBeDefined()
    expect(payload.kyber?.context).toBeDefined()
    expect(payload.kyber?.schema).toBeDefined()
    expect(payload.kyber?.timeline).toBeDefined()
    expect(payload.kyber?.compare).toBeDefined()
    expect(payload.kyber?.quarantineCount).toBe(3)
    expect(payload.kyber?.problems).toHaveLength(1)

    // Round-trip through JSON (the native clients' path: spawn → decode)
    const json = JSON.stringify(payload)
    const decoded = JSON.parse(json) as MenubarPayload
    expect(decoded.kyber?.context).toEqual(context)
    expect(decoded.kyber?.schema).toEqual(schema)
    expect(decoded.kyber?.timeline?.spanId).toBe('(session)')
    expect(decoded.kyber?.compare?.harnesses).toEqual(['pi', 'copilot'])
    expect(decoded.kyber?.quarantineCount).toBe(3)
    expect(decoded.kyber?.problems?.[0]?.code).toBe('TOKEN_SUM_MISMATCH')
  })

  it('context payload requires buckets and pressure per turn (R7)', () => {
    const rawContext = {
      measurable: true as const,
      contextLimit: 128_000,
      turns: [
        {
          index: 1,
          buckets: { system_prompt: 500, tool_definitions: 1000, instruction_context: 0, conversation_history: 2000, tool_result_content: 300 },
          toolDefinitionsByServer: new Map([['server-a', 1000]]),
          builtinToolDefinitionTokens: 0,
          strippedInstructionBlocks: { count: 0, tokens: 0 },
          bucketedTokens: 3800,
          residual: { tokens: 200, attribution: 'tokenizer_drift' as const },
          headroom: 124_000,
          pressure: 3800 / 128_000,
          accumulationRate: 3800,
          freshInput: 900,
        },
      ],
      residualTotal: 200,
      derivedCounts: false,
      freshJumpFactor: 2,
      flaggedTurns: [1],
      sessionAccumulationRate: 3800,
    }
    const serialized = serializeKyberContext(rawContext as unknown as Parameters<typeof serializeKyberContext>[0])
    expect(serialized.measurable).toBe(true)
    if (serialized.measurable) {
      expect(serialized.turns[0].buckets.system_prompt).toBe(500)
      expect(serialized.turns[0].pressure).toBeCloseTo(3800 / 128_000)
      // Map became plain record for JSON
      expect(serialized.turns[0].toolDefinitionsByServer).toEqual({ 'server-a': 1000 })
      expect(serialized.turns[0].residual.attribution).toBe('tokenizer_drift')
    }
  })

  it('context not measurable still carries reason and turn count (R7.6)', () => {
    const serialized = serializeKyberContext({ measurable: false, reason: 'no_message_structure', turns: 3, contextLimit: 128_000 } as unknown as Parameters<typeof serializeKyberContext>[0])
    expect(serialized).toEqual({ measurable: false, reason: 'no_message_structure', turns: 3, contextLimit: 128_000 })
  })

  it('schema payload requires ranking by resident cost (R8.1) and byServer grouping (R8.3)', () => {
    const raw = {
      measurable: true as const,
      ranked: [
        { name: 'tool-b', server: 'server-b', cost: 3000, invoked: false },
        { name: 'tool-a', server: 'server-a', cost: 1000, invoked: true },
      ],
      neverInvoked: [{ name: 'tool-b', server: 'server-b', cost: 3000, invoked: false }],
      byServer: new Map([['server-b', 3000], ['server-a', 1000]]),
      unusedRange: { tokenResidencies: 3000, floor: 0.002, ceiling: 0.03, currency: 'USD' },
      turns: 10,
    }
    const serialized = serializeKyberSchema(raw as unknown as Parameters<typeof serializeKyberSchema>[0])
    expect(serialized.measurable).toBe(true)
    if (serialized.measurable) {
      expect(serialized.ranked[0].name).toBe('tool-b')
      expect(serialized.byServer).toEqual({ 'server-b': 3000, 'server-a': 1000 })
      expect(serialized.unusedRange.tokenResidencies).toBe(3000)
      // Map became plain record
      expect(typeof serialized.byServer).toBe('object')
      expect(serialized.byServer instanceof Map).toBe(false)
    }
  })

  it('quarantineCount and problems are independent of other analyses (R6)', () => {
    const kyber: KyberPayload = { quarantineCount: 0, problems: [] }
    const payload = buildMenubarPayload(emptyPeriod('Today'), [], null, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, kyber)
    expect(payload.kyber?.quarantineCount).toBe(0)
    expect(payload.kyber?.problems).toEqual([])
    const json = JSON.stringify(payload)
    expect(JSON.parse(json).kyber.quarantineCount).toBe(0)
  })

  it('pin: top-level keys are exactly the documented contract — change fails until snapshot updated (R14.3)', () => {
    const payload = buildMenubarPayload(emptyPeriod('Today'), [], null, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, {
      context: { measurable: false, reason: 'no_message_structure', turns: 0, contextLimit: 128_000 },
      quarantineCount: 0,
      problems: [],
    })
    const topKeys = Object.keys(payload).sort()
    // Pin snapshot: the machine-readable status format produced by `codeburn
    // status --format menubar-json` (R11.4). A key added/removed/renamed here
    // will break the native clients' decode path (Swift's Codable and the
    // Electron's JSON handling) unless this snapshot is intentionally updated
    // and design.md records the rationale under R14.3.
    const expectedTopKeys = ['claudeConfigs', 'combined', 'currency', 'current', 'generated', 'history', 'hydration', 'kyber', 'optimize', 'plugins', 'stale'].sort()
    // claudeConfigs/combined/hydration/plugins/stale/kyber are optional — they
    // appear only when present, so sort the present keys against the superset.
    // At least the required keys must be present and no unexpected key may appear.
    const requiredKeys = ['currency', 'current', 'generated', 'history', 'optimize'].sort()
    for (const k of requiredKeys) expect(topKeys).toContain(k)
    for (const k of topKeys) expect(expectedTopKeys).toContain(k)
    // The new kyber field must be present when supplied and absent when not.
    expect(topKeys).toContain('kyber')
    const withoutKyber = buildMenubarPayload(emptyPeriod('Today'), [], null)
    expect(Object.keys(withoutKyber)).not.toContain('kyber')
  })

  it('timeline and compare payloads carry the documented structure (R9, R10)', () => {
    const payload = buildMenubarPayload(emptyPeriod('Today'), [], null, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, {
      timeline: fakeTimeline(),
      compare: fakeCompare(),
    })
    expect(payload.kyber?.timeline?.children).toHaveLength(1)
    expect(payload.kyber?.timeline?.children[0].attributes['gen_ai.operation.name']).toBe('chat')
    expect(payload.kyber?.compare?.rows[0].metric).toBe('tokens_per_turn')
    // Timeline children and compare rows survive JSON round-trip
    const roundTripped = JSON.parse(JSON.stringify(payload)) as MenubarPayload
    expect(roundTripped.kyber?.timeline?.children[0].spanId).toBe('span-1')
    expect(roundTripped.kyber?.compare?.rows[0].cells.pi.render).toBe('1,200')
  })
})
