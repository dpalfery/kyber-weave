import { describe, expect, it } from 'vitest'

import { compactionPressure, toolYield } from '../analysis/signals.js'
import { assembleRollup, coverageFor, digestSessionPayloads } from './harnesses.js'
import { harnessDimensionAvailability } from './measurability.js'
import type { AsadSessionPayload } from './sessions.js'
import { isNotMeasurable, type MetricAvailability } from './types.js'

// Evidence-driven availability (plan "KyberDash: fill the context data gap
// through harness OpenTelemetry capture", T3). A harness's static refusal is
// the fallback for when its recorded sessions carry no evidence; a bucket
// part or a named window in the sessions themselves overrides it. Fixtures
// are synthetic: real key names, invented content.

const CONTENT_DIMENSIONS = ['tool_yield', 'tool_definitions', 'schema_cost'] as const
const PRESSURE_DIMENSIONS = [
  'context_pressure',
  'context_pressure_median',
  'context_pressure_p95',
] as const

function reasonOf(availability: MetricAvailability): string {
  if (!isNotMeasurable(availability)) throw new Error(`expected not_measurable, got ${JSON.stringify(availability)}`)
  return availability.reason
}

function payload(fields: Record<string, unknown>): AsadSessionPayload {
  return { id: 'sess-synthetic', session_id: 'sess-synthetic', ...fields } as unknown as AsadSessionPayload
}

function windowedSession(
  harness: string,
  contextLimitSource: 'reported' | 'declared' | 'default',
  pressures: number[],
): AsadSessionPayload {
  return payload({
    harness,
    context: {
      measurable: true,
      contextLimit: 128_000,
      contextLimitSource,
      turns: pressures.map((pressure) => ({ pressure })),
    },
    summary: { total_input: 1000 },
  })
}

function rollupOf(harness: string, payloads: AsadSessionPayload[]) {
  const digest = digestSessionPayloads(payloads)
  return assembleRollup(harness, digest, {
    sessionCount: digest.count,
    runCount: 0,
    executionCount: 0,
    executions: [],
    tokenTotals: () => undefined,
    scope: { kind: 'harness', harness },
  })
}

describe('harnessDimensionAvailability — evidence overrides a static refusal', () => {
  it('measures OpenCode context pressure when its sessions report a window', () => {
    for (const dim of PRESSURE_DIMENSIONS) {
      expect(isNotMeasurable(harnessDimensionAvailability('opencode', dim))).toBe(true)
      expect(harnessDimensionAvailability('opencode', dim, { windowSources: ['reported'] })).toBe('measured')
    }
  })

  it('makes declared-window pressure derived, never measured (D5)', () => {
    expect(harnessDimensionAvailability('opencode', 'context_pressure', { windowSources: ['declared'] })).toBe(
      'derived',
    )
    expect(harnessDimensionAvailability('aider', 'context_pressure', { windowSources: ['declared'] })).toBe('derived')
    // A statically measurable harness whose only window is declared is still derived.
    expect(harnessDimensionAvailability('copilot', 'context_pressure', { windowSources: ['declared'] })).toBe(
      'derived',
    )
    // A reported window anywhere in the evidence makes the dimension measurable.
    expect(
      harnessDimensionAvailability('opencode', 'context_pressure', { windowSources: ['declared', 'reported'] }),
    ).toBe('measured')
  })

  it('measures Claude Code tool dimensions when its sessions carry tool_definitions parts', () => {
    for (const dim of CONTENT_DIMENSIONS) {
      expect(isNotMeasurable(harnessDimensionAvailability('claude-code', dim))).toBe(true)
      expect(harnessDimensionAvailability('claude-code', dim, { bucketParts: ['tool_definitions'] })).toBe('measured')
    }
    expect(harnessDimensionAvailability('opencode', 'tool_yield', { bucketParts: ['tool_definitions'] })).toBe(
      'measured',
    )
  })
})

describe('harnessDimensionAvailability — no evidence returns the static reason verbatim', () => {
  const harnesses = ['opencode', 'aider', 'claude-code', 'codex', 'cursor', 'windsurf', 'pi', 'copilot']
  const dimensions = [...CONTENT_DIMENSIONS, ...PRESSURE_DIMENSIONS, 'cache_hit_rate', 'delegation_overhead']

  it('treats absent, empty and irrelevant evidence as no evidence', () => {
    for (const harness of harnesses) {
      for (const dim of dimensions) {
        const fallback = harnessDimensionAvailability(harness, dim)
        expect(harnessDimensionAvailability(harness, dim, {})).toEqual(fallback)
        expect(harnessDimensionAvailability(harness, dim, { bucketParts: [], windowSources: [] })).toEqual(fallback)
        // A guessed default window is not evidence of a window (#181).
        expect(harnessDimensionAvailability(harness, dim, { windowSources: ['default'] })).toEqual(fallback)
        // A content part other than tool_definitions says nothing about tool schemas.
        expect(harnessDimensionAvailability(harness, dim, { bucketParts: ['conversation_history'] })).toEqual(
          fallback,
        )
      }
    }
  })

  it('keeps the exact static wording', () => {
    expect(reasonOf(harnessDimensionAvailability('opencode', 'context_pressure', { windowSources: ['default'] }))).toBe(
      'OpenCode is installed with experimental OpenTelemetry disabled and exports no supported session files.',
    )
    expect(reasonOf(harnessDimensionAvailability('claude-code', 'tool_yield', { bucketParts: [] }))).toBe(
      'Claude Code session files record tool invocations, not tool definition schemas; raw API body export is required.',
    )
  })

  it('does not let window evidence unlock tool dimensions, nor tool evidence unlock pressure', () => {
    expect(
      harnessDimensionAvailability('claude-code', 'tool_yield', { windowSources: ['reported'] }),
    ).toEqual(harnessDimensionAvailability('claude-code', 'tool_yield'))
    expect(
      harnessDimensionAvailability('opencode', 'context_pressure', { bucketParts: ['tool_definitions'] }),
    ).toEqual(harnessDimensionAvailability('opencode', 'context_pressure'))
  })
})

describe('coverageFor — evidence raises field coverage', () => {
  it('counts an evidenced dimension as covered and falls back without evidence', () => {
    expect(coverageFor('opencode', {})).toBe(coverageFor('opencode'))
    expect(coverageFor('opencode', { windowSources: ['reported'] })).toBeGreaterThan(coverageFor('opencode'))
    expect(coverageFor('claude-code', { bucketParts: ['tool_definitions'] })).toBeGreaterThan(
      coverageFor('claude-code'),
    )
  })
})

describe('harness rollup — evidence from recorded sessions', () => {
  it('reports OpenCode pressure as measured from sessions with a reported window', () => {
    const rollup = rollupOf('opencode', [windowedSession('opencode', 'reported', [0.2, 0.4])])
    expect(rollup.contextPressureMedian).toBe(0.4)
    expect(rollup.measurability['context_pressure']).toBe('measured')
    expect(rollup.measurability['context_pressure_median']).toBe('measured')
    expect(rollup.measurability['context_pressure_p95']).toBe('measured')
  })

  it('tags pressure from a declared window as derived, never measured (D5)', () => {
    for (const harness of ['copilot', 'opencode']) {
      const rollup = rollupOf(harness, [windowedSession(harness, 'declared', [0.3, 0.6])])
      expect(rollup.contextPressureMedian).toBe(0.6)
      expect(rollup.measurability['context_pressure']).toBe('derived')
      expect(rollup.measurability['context_pressure_median']).toBe('derived')
      expect(rollup.measurability['context_pressure_p95']).toBe('derived')
    }
  })

  it('tags mixed reported and declared sessions as derived', () => {
    const rollup = rollupOf('copilot', [
      windowedSession('copilot', 'reported', [0.1]),
      windowedSession('copilot', 'declared', [0.5]),
    ])
    expect(rollup.measurability['context_pressure']).toBe('derived')
  })

  it('keeps default-window sessions as unknownWindowSessions with the #181 refusal', () => {
    const rollup = rollupOf('copilot', [windowedSession('copilot', 'default', [0.95])])
    expect(rollup.contextPressureMedian).toBeNull()
    expect(reasonOf(rollup.measurability['context_pressure']!)).toContain('No source reported a context window for 1')
    expect((rollup.payload as { unknownWindowSessions?: number }).unknownWindowSessions).toBe(1)
  })

  it('returns the static OpenCode reason verbatim when every session has a default window', () => {
    const rollup = rollupOf('opencode', [windowedSession('opencode', 'default', [0.95])])
    expect(rollup.contextPressureMedian).toBeNull()
    expect(rollup.measurability['context_pressure']).toEqual(harnessDimensionAvailability('opencode', 'context_pressure'))
    expect((rollup.payload as { unknownWindowSessions?: number }).unknownWindowSessions).toBe(1)
  })

  it('measures Claude Code tool yield from sessions carrying tool_definitions', () => {
    const rollup = rollupOf('claude-code', [
      payload({
        harness: 'claude-code',
        context: {
          measurable: true,
          contextLimitSource: 'reported',
          turns: [],
          first: { buckets: { tool_definitions: 1200, conversation_history: 300 }, reported_input: 1500 },
          last: { buckets: { tool_definitions: 1200, conversation_history: 900 }, reported_input: 2100 },
        },
        summary: {
          total_input: 2100,
          tools_offered: ['Read', 'Edit', 'Grep', 'Bash'],
          tools_invoked: ['Read', 'Grep'],
        },
      }),
    ])
    expect(rollup.toolYield).toBe(0.5)
    expect(rollup.measurability['tool_yield']).toBe('measured')
  })

  it('keeps the static Claude Code tool_yield reason when sessions carry no tool_definitions', () => {
    const rollup = rollupOf('claude-code', [
      payload({
        harness: 'claude-code',
        context: { measurable: true, contextLimitSource: 'reported', turns: [] },
        summary: { total_input: 100, tools_invoked: ['Read'] },
      }),
    ])
    expect(rollup.toolYield).toBeNull()
    expect(rollup.measurability['tool_yield']).toEqual(harnessDimensionAvailability('claude-code', 'tool_yield'))
  })

  it('keeps Copilot sessions without observed invocations out of tool_yield (#235)', () => {
    const rollup = rollupOf('copilot', [
      payload({
        harness: 'copilot',
        context: {
          measurable: true,
          contextLimitSource: 'reported',
          turns: [],
          first: { buckets: { tool_definitions: 200 }, reported_input: 200 },
          last: { buckets: { tool_definitions: 200 }, reported_input: 200 },
        },
        summary: { total_input: 200, tools_offered: ['ToolA', 'ToolB'] },
      }),
    ])
    expect(rollup.toolYield).toBeNull()
    expect(isNotMeasurable(rollup.measurability['tool_yield'])).toBe(true)
  })
})

describe('signals — the inputs are the evidence', () => {
  it('computes tool yield for Claude Code when definitions are supplied', () => {
    const result = toolYield({ harness: 'claude-code', definedTools: ['Read', 'Edit'], invokedTools: ['Read'] })
    expect(result.status).toBe('measured')
    if (result.status !== 'not_measurable') expect(result.value).toBe(0.5)
  })

  it('returns the static Claude Code reason when no definitions are supplied', () => {
    const result = toolYield({ harness: 'claude-code', invokedTools: ['Read'] })
    expect(result).toEqual({
      status: 'not_measurable',
      reason: reasonOf(harnessDimensionAvailability('claude-code', 'tool_yield')),
    })
  })

  it('computes OpenCode compaction pressure against a reported window as measured', () => {
    const result = compactionPressure({
      harness: 'opencode',
      peakInputTokens: 64_000,
      contextLimit: 128_000,
      contextLimitSource: 'reported',
    })
    expect(result.status).toBe('measured')
    if (result.status !== 'not_measurable') expect(result.value).toBe(0.5)
  })

  it('computes compaction pressure against a declared window as derived, never measured (D5)', () => {
    for (const harness of ['opencode', 'copilot']) {
      const result = compactionPressure({
        harness,
        peakInputTokens: 64_000,
        contextLimit: 128_000,
        contextLimitSource: 'declared',
      })
      expect(result.status).toBe('derived')
      if (result.status !== 'not_measurable') expect(result.measurementClass).not.toBe('deterministic')
    }
  })

  it('refuses compaction pressure against a default window (#181)', () => {
    const result = compactionPressure({
      harness: 'copilot',
      peakInputTokens: 190_000,
      contextLimit: 200_000,
      contextLimitSource: 'default',
    })
    expect(result.status).toBe('not_measurable')
  })

  it('refuses compaction pressure when a window source names no limit', () => {
    for (const harness of ['opencode', 'copilot']) {
      for (const contextLimitSource of ['reported', 'declared'] as const) {
        const result = compactionPressure({ harness, peakInputTokens: 100_000, contextLimitSource })
        expect(result.status, `${harness}/${contextLimitSource}`).toBe('not_measurable')
      }
    }
  })

  it('returns the static OpenCode reason when no window is named', () => {
    const result = compactionPressure({ harness: 'opencode', peakInputTokens: 64_000, contextLimit: 128_000 })
    expect(result).toEqual({
      status: 'not_measurable',
      reason: reasonOf(harnessDimensionAvailability('opencode', 'context_pressure')),
    })
  })
})
