// Measurability declarations (R7.6, R8.5, R10.1, R10.2). Each ingest path
// declares, per metric, whether it can measure it at all — availability
// independent of value — and the analyses refuse a declared-unmeasurable
// metric before computing anything, so a file-sourced provider that cannot
// supply tool definitions or message structure is answered in words ("not
// measurable") rather than with an all-zero ranking or a zero-bucket chart
// whose residual would be read as tokenizer drift. An OTLP source with full
// telemetry is measurable, and its gaps come from its adapter's own
// unexported-metric statement, not from a second table.

import { describe, expect, it } from 'vitest'

import type { ParsedProviderCall } from '../../src/providers/types.js'
import { analyzeContext, type ContextAnalysis, type ContextTurn } from '../analysis/context.js'
import {
  TOKEN_RESIDENCY_RATES,
  rankSchemas,
  type SchemaCostAnalysis,
  type ToolDefinition,
} from '../analysis/schema.js'
import { sourceFor, synthesizeCall } from '../synth/synth.js'
import {
  FILE_SOURCE_PREFIX,
  contextCompositionAvailability,
  getMeasurability,
  isFileSource,
  schemaRankingAvailability,
} from './measurability.js'
import { CANONICAL_CONTENT_KEYS } from './types.js'

// ---------------------------------------------------------------------------
// Fixture kit
// ---------------------------------------------------------------------------

/** A file-sourced source name: the synthesizer's namespace plus a provider. */
const FILE_SOURCE = `${FILE_SOURCE_PREFIX}claude-code`

/** Any source name outside the synthesizer's namespace arrived as telemetry. */
const OTLP_SOURCE = 'pi-abc123'

/** The window the context assertions are against (R7.4). */
const LIMIT = 200_000

function call(spec: Partial<ParsedProviderCall> = {}): ParsedProviderCall {
  return {
    provider: 'claude',
    model: 'claude-sonnet-4.5',
    inputTokens: 1_000,
    outputTokens: 240,
    cacheCreationInputTokens: 120,
    cacheReadInputTokens: 3_800,
    cachedInputTokens: 3_800,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0.0123,
    tools: ['Read', 'Bash'],
    bashCommands: [],
    timestamp: '2026-08-29T12:00:00.000Z',
    speed: 'standard',
    deduplicationKey: 'claude:s-1:m-1',
    userMessage: 'run the parity check',
    sessionId: 's-1',
    ...spec,
  }
}

function tool(overrides: Partial<ToolDefinition> = {}): ToolDefinition {
  return { name: 'read', server: 'filesystem', tokens: 600, ...overrides }
}

function turn(overrides: Partial<ContextTurn> = {}): ContextTurn {
  return {
    parts: [{ part: 'system_prompt', text: 'You are a coding agent.' }],
    inputTokens: 5_000,
    freshInput: 5_000,
    ...overrides,
  }
}

/** Narrow a ranking to its measurable variant, failing loudly otherwise. */
function expectMeasurableRanking(result: SchemaCostAnalysis) {
  if (!result.measurable) {
    throw new Error(
      `expected a measurable ranking, got not measurable (${result.invocationCount} invocations)`,
    )
  }
  return result
}

/** Narrow a composition to its measurable variant, failing loudly otherwise. */
function expectMeasurableComposition(result: ContextAnalysis) {
  if (!result.measurable) {
    throw new Error(`expected a measurable composition, got not measurable (${result.reason})`)
  }
  return result
}

// ---------------------------------------------------------------------------
// The declaration itself (R10.1)
// ---------------------------------------------------------------------------

describe('isFileSource — the ingest path a source name carries', () => {
  it('names the synthesizer namespace file-sourced and anything else telemetry', () => {
    expect(isFileSource('codeburn/claude')).toBe(true)
    expect(isFileSource(OTLP_SOURCE)).toBe(false)
  })
})

describe('getMeasurability — the file-sourced path (R7.6, R8.5)', () => {
  it('declares schema ranking and context composition not measurable', () => {
    const measurability = getMeasurability(FILE_SOURCE, 'claude-code')
    for (const metric of ['schema_cost', ...CANONICAL_CONTENT_KEYS]) {
      expect(measurability[metric]).toBe('not_measurable')
    }
    expect(schemaRankingAvailability(measurability)).toBe('not_measurable')
    expect(contextCompositionAvailability(measurability)).toBe('not_measurable')
  })

  it('keeps the counters measurable, so only the impossible metrics are refused', () => {
    const measurability = getMeasurability(FILE_SOURCE, 'claude-code')
    expect(measurability['token_usage']).toBe('measured')
    expect(measurability['cost']).toBe('measured')
  })

  it('declares the R9 hierarchy not measurable — "0 subagents" states a fact session files never carried', () => {
    const measurability = getMeasurability(FILE_SOURCE, 'claude-code')
    expect(measurability['execution_structure']).toBe('not_measurable')
  })

  it('carries the provider-specific gaps (gemini has no cache-creation counter)', () => {
    const measurability = getMeasurability(`${FILE_SOURCE_PREFIX}gemini`, 'gemini')
    expect(measurability['cache_creation']).toBe('not_measurable')
  })
})

describe('getMeasurability — the OTLP path (R10.1)', () => {
  it('declares a full-telemetry harness measurable for ranking and composition', () => {
    const measurability = getMeasurability(OTLP_SOURCE, 'copilot')
    expect(schemaRankingAvailability(measurability)).toBe('measured')
    expect(contextCompositionAvailability(measurability)).toBe('measured')
    expect(measurability['execution_structure']).toBe('measured')
    // Telemetry carries no billing: an OTLP cost figure is computed from
    // tokens and a published table, never read from a counter.
    expect(measurability['cost']).toBe('derived')
    // The adapter's own gap statement rides along.
    expect(measurability['reasoning']).toBe('not_measurable')
  })

  it('agrees with the adapter about what the harness does not export', () => {
    // pi invoked 14 tools across 368 calls while exporting none (R8.5's
    // measured case): the source-level answer refuses the ranking through
    // the adapter's declaration, exactly as its records are stamped.
    const measurability = getMeasurability(OTLP_SOURCE, 'pi')
    expect(measurability['tool_definitions']).toBe('not_measurable')
    expect(schemaRankingAvailability(measurability)).toBe('not_measurable')
    // Structure is pi's telemetry's to supply; only definitions are missing.
    expect(contextCompositionAvailability(measurability)).toBe('measured')
  })
})

// ---------------------------------------------------------------------------
// Propagation — rankSchemas (R8.5)
// ---------------------------------------------------------------------------

describe('rankSchemas under a declaration (R8.5, R10.2)', () => {
  it('reports the file-sourced source not measurable for invocations it cannot rank — never an all-zero ranking', () => {
    const result = rankSchemas(
      [],
      12,
      ['Read', 'Bash'],
      TOKEN_RESIDENCY_RATES,
      getMeasurability(FILE_SOURCE, 'claude-code'),
    )
    expect(result).toEqual({
      measurable: false,
      invocationCount: 2,
      reason: 'declared_not_measurable',
    })
  })

  it('refuses even with no invocations — a source that cannot see offered tools cannot claim "nothing was offered"', () => {
    const declared = rankSchemas(
      [],
      12,
      [],
      TOKEN_RESIDENCY_RATES,
      getMeasurability(FILE_SOURCE, 'claude-code'),
    )
    expect(declared.measurable).toBe(false)
    // The data alone would have called this measurable-and-empty, which is
    // precisely the zero the declaration exists to prevent.
    expect(rankSchemas([], 12, []).measurable).toBe(true)
  })

  it('the declaration wins over definitions that contradicted it', () => {
    const result = rankSchemas(
      [tool()],
      12,
      ['read'],
      TOKEN_RESIDENCY_RATES,
      getMeasurability(FILE_SOURCE, 'claude-code'),
    )
    expect(result.measurable).toBe(false)
  })

  it('an OTLP source with definitions ranks them', () => {
    const result = expectMeasurableRanking(
      rankSchemas(
        [tool({ name: 'read', tokens: 600 }), tool({ name: 'write', tokens: 400 })],
        10,
        ['read'],
        TOKEN_RESIDENCY_RATES,
        getMeasurability(OTLP_SOURCE, 'copilot'),
      ),
    )
    expect(result.ranked.map((entry) => entry.name)).toEqual(['read', 'write'])
    expect(result.ranked.map((entry) => entry.cost)).toEqual([6_000, 4_000])
  })

  it('absent declarations leave the data-driven refusal unchanged', () => {
    expect(rankSchemas([], 12, ['read'])).toEqual({ measurable: false, invocationCount: 1 })
  })
})

// ---------------------------------------------------------------------------
// Propagation — analyzeContext (R7.6)
// ---------------------------------------------------------------------------

describe('analyzeContext under a declaration (R7.6, R10.2)', () => {
  it('reports the file-sourced source not measurable even when turns carry parts', () => {
    // The parts are the trap: fragments a file source happens to carry would
    // otherwise bucket into a composition chart whose residual is read as
    // tokenizer drift (R7.6). The declaration answers before the data.
    const result = analyzeContext([turn(), turn({ inputTokens: 6_000, freshInput: 1_000 })], {
      contextLimit: LIMIT,
      measurability: getMeasurability(FILE_SOURCE, 'claude-code'),
    })
    expect(result).toEqual({
      measurable: false,
      reason: 'declared_not_measurable',
      turns: 2,
      contextLimit: LIMIT,
    })
  })

  it('the refusal carries no buckets and no residual to chart', () => {
    const result = analyzeContext([turn()], {
      contextLimit: LIMIT,
      measurability: getMeasurability(FILE_SOURCE, 'claude-code'),
    })
    expect(result.measurable).toBe(false)
    expect('buckets' in result).toBe(false)
    expect('residual' in result).toBe(false)
  })

  it('an OTLP source with structure composes', () => {
    const result = expectMeasurableComposition(
      analyzeContext(
        [
          turn(),
          turn({
            parts: [{ part: 'conversation_history', text: 'run the parity check' }],
            inputTokens: 6_000,
            freshInput: 1_000,
          }),
        ],
        { contextLimit: LIMIT, measurability: getMeasurability(OTLP_SOURCE, 'copilot') },
      ),
    )
    expect(result.turns).toHaveLength(2)
    expect(result.turns[0].buckets.system_prompt).toBeGreaterThan(0)
  })

  it('absent declarations leave the data-driven answer in charge', () => {
    expect(analyzeContext([turn({ parts: [] })], { contextLimit: LIMIT })).toEqual({
      measurable: false,
      reason: 'no_message_structure',
      turns: 1,
      contextLimit: LIMIT,
    })
  })
})

// ---------------------------------------------------------------------------
// One table, two readers — the synthesizer's stamps and the source-level
// answer must never disagree about a limitation (R10.1)
// ---------------------------------------------------------------------------

describe('agreement between record stamps and the source-level declaration', () => {
  it('every limitation a synthesized record stamps is declared at the source level', () => {
    for (const provider of ['claude', 'gemini']) {
      const parsed = call({ provider, deduplicationKey: `${provider}:s-1:m-1` })
      const record = synthesizeCall(parsed)

      const stamped = Object.entries(record.measurability ?? {})
        .filter(([, availability]) => availability === 'not_measurable')
        .map(([metric]) => metric)
        .sort()
      expect(stamped.length).toBeGreaterThan(0)

      const declared = Object.entries(getMeasurability(sourceFor(parsed), provider))
        .filter(([, availability]) => availability === 'not_measurable')
        .map(([metric]) => metric)
        .sort()

      for (const metric of stamped) {
        expect(declared).toContain(metric)
      }
    }
  })
})
