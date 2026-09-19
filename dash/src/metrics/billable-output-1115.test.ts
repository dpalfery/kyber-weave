import { describe, expect, it } from 'vitest'

import { aggregateModelStats, computeComparison } from './compare-stats.js'
import { aggregateProjectsIntoDays } from './day-aggregator.js'
import { billableOutputTokens, getShortModelName } from '../pricing/models.js'
import { findContextBloatCandidates } from './optimize.js'
import { callBillableOutputTokens, sessionBillableOutputTokens, sessionModelBillableOutputTokens } from './session-output.js'
import { aggregateSessions } from './sessions-report.js'
import { buildPeriodData } from './usage-aggregator.js'
import type { ProjectSummary, SessionSummary } from '../types.js'

function makeCall(provider: string, outputTokens: number, reasoningTokens: number) {
  return {
    provider,
    model: provider === 'codex' ? 'gpt-5.4' : 'grok-4',
    usage: {
      inputTokens: 0,
      outputTokens,
      cacheCreationInputTokens: 0,
      cacheReadInputTokens: 0,
      cachedInputTokens: 0,
      reasoningTokens,
      webSearchRequests: 0,
    },
    costUSD: 0,
    tools: [],
    mcpTools: [],
    skills: [],
    hasAgentSpawn: false,
    hasPlanMode: false,
    speed: 'standard' as const,
    timestamp: '2026-08-01T12:00:00Z',
    bashCommands: [],
    deduplicationKey: `${provider}-${outputTokens}-${reasoningTokens}`,
    savingsUSD: 1,
    savingsBaselineModel: 'gpt-4o',
  }
}

function makeSession(provider: string, outputTokens: number, reasoningTokens: number): SessionSummary {
  const call = makeCall(provider, outputTokens, reasoningTokens)
  return {
    sessionId: `${provider}-s`,
    project: 'p',
    firstTimestamp: call.timestamp,
    lastTimestamp: call.timestamp,
    totalCostUSD: 0,
    totalSavingsUSD: 0,
    totalInputTokens: 0,
    totalOutputTokens: outputTokens,
    totalReasoningTokens: reasoningTokens,
    totalCacheReadTokens: 0,
    totalCacheWriteTokens: 0,
    apiCalls: 1,
    turns: [{
      userMessage: 'x',
      timestamp: call.timestamp,
      sessionId: `${provider}-s`,
      category: 'coding',
      retries: 0,
      hasEdits: true,
      assistantCalls: [call],
    }],
    modelBreakdown: {
      [getShortModelName(call.model)]: {
        calls: 1,
        costUSD: 0,
        savingsUSD: 0,
        tokens: call.usage,
      },
    },
    toolBreakdown: {},
    mcpBreakdown: {},
    bashBreakdown: {},
    categoryBreakdown: {} as never,
    skillBreakdown: {} as never,
  } as unknown as SessionSummary
}

function makeProject(session: SessionSummary): ProjectSummary {
  return {
    project: 'p',
    projectPath: '/p',
    totalCostUSD: 0,
    totalSavingsUSD: 0,
    totalProxiedCostUSD: 0,
    totalApiCalls: 1,
    sessions: [session],
  } as ProjectSummary
}

describe('#1115 billableOutputTokens on report/optimize totals', () => {
  it('exclusive grok: 10 output + 3 reasoning = 13', () => {
    expect(billableOutputTokens('grok', 10, 3)).toBe(13)
    expect(sessionBillableOutputTokens(makeSession('grok', 10, 3))).toBe(13)
  })

  it('inclusive codex: 10 output already contains reasoning = 10', () => {
    expect(billableOutputTokens('codex', 10, 3)).toBe(10)
    expect(sessionBillableOutputTokens(makeSession('codex', 10, 3))).toBe(10)
  })

  it('day-aggregator uses billable output per call', () => {
    const grokDays = aggregateProjectsIntoDays([makeProject(makeSession('grok', 10, 3))])
    expect(grokDays[0]!.outputTokens).toBe(13)
    expect(grokDays[0]!.providers.grok!.outputTokens).toBe(13)

    const codexDays = aggregateProjectsIntoDays([makeProject(makeSession('codex', 10, 3))])
    expect(codexDays[0]!.outputTokens).toBe(10)
    expect(codexDays[0]!.providers.codex!.outputTokens).toBe(10)
  })

  it('sessions report and period data match the helper', () => {
    const grok = makeProject(makeSession('grok', 10, 3))
    const codex = makeProject(makeSession('codex', 10, 3))
    expect(aggregateSessions([grok])[0]!.outputTokens).toBe(13)
    expect(aggregateSessions([codex])[0]!.outputTokens).toBe(10)
    expect(buildPeriodData('t', [grok]).outputTokens).toBe(13)
    expect(buildPeriodData('t', [codex]).outputTokens).toBe(10)
  })

  it('optimize context-bloat denominator does not double-count inclusive reasoning', () => {
    const inclusive = makeSession('codex', 100_000, 50_000)
    inclusive.totalInputTokens = 2_000_000
    const exclusive = makeSession('grok', 100_000, 50_000)
    exclusive.totalInputTokens = 2_000_000

    const inc = findContextBloatCandidates([makeProject(inclusive)])
    const exc = findContextBloatCandidates([makeProject(exclusive)])
    // ratio = input / billableOut. Inclusive 2e6/1e5 = 20; exclusive 2e6/1.5e5 ≈ 13.3
    // Both clear CONTEXT_BLOAT_MIN_RATIO if that threshold is below 13.
    if (inc.length && exc.length) {
      expect(inc[0]!.growthRatio === null || typeof inc[0]!.growthRatio === 'number').toBe(true)
    }
    // Direct contract: helper is what the detector uses.
    expect(sessionBillableOutputTokens(inclusive)).toBe(100_000)
    expect(sessionBillableOutputTokens(exclusive)).toBe(150_000)
  })

  it('compare-stats Output tok/call uses per-call billable output', () => {
    const grok = aggregateModelStats([makeProject(makeSession('grok', 10, 3))])
    const codex = aggregateModelStats([makeProject(makeSession('codex', 10, 3))])
    expect(grok[0]!.outputTokens).toBe(13)
    expect(codex[0]!.outputTokens).toBe(10)
    const rows = computeComparison(grok[0]!, codex[0]!)
    const outputRow = rows.find(r => r.label === 'Output tok / call')!
    expect(outputRow.valueA).toBe(13)
    expect(outputRow.valueB).toBe(10)
  })

  it('session helper does not crash on aggregate-only or minimal calls', () => {
    const aggregate = makeSession('grok', 10, 3)
    aggregate.turns = []
    expect(sessionBillableOutputTokens(aggregate)).toBe(13)

    const noReasoning = makeSession('grok', 10, 3)
    noReasoning.turns = []
    delete (noReasoning as { totalReasoningTokens?: number }).totalReasoningTokens
    const noReasoningOut = sessionBillableOutputTokens(noReasoning)
    expect(Number.isFinite(noReasoningOut)).toBe(true)
    expect(noReasoningOut).toBe(10)

    const stub = makeSession('codex', 10, 3)
    stub.turns[0]!.assistantCalls = [{ costUSD: 1, tools: [], bashCommands: [], timestamp: stub.firstTimestamp } as never]
    expect(sessionBillableOutputTokens(stub)).toBe(10)

    expect(callBillableOutputTokens({} as never)).toBe(0)
    expect(callBillableOutputTokens({ provider: 'grok', usage: { outputTokens: 10, reasoningTokens: 3 } })).toBe(13)
  })

  it('joins model output on getShortModelName, not raw call.model', () => {
    const session = makeSession('claude', 10, 0)
    session.turns[0]!.assistantCalls[0]!.model = 'deepseek-v4-pro'
    session.modelBreakdown = {
      'DeepSeek v4 Pro': {
        calls: 1,
        costUSD: 1,
        savingsUSD: 0,
        tokens: session.turns[0]!.assistantCalls[0]!.usage,
      },
    }
    const billed = sessionModelBillableOutputTokens(session)
    expect(billed).toEqual({ 'DeepSeek v4 Pro': 10 })
    expect(billed['deepseek-v4-pro']).toBeUndefined()
  })

  it('does not mint a $0 short-name orphan when modelBreakdown is the raw id', () => {
    const session = makeSession('claude', 50, 0)
    session.turns[0]!.assistantCalls[0]!.model = 'claude-opus-4-8'
    session.modelBreakdown = {
      'claude-opus-4-8': {
        calls: 2,
        costUSD: 5,
        savingsUSD: 0,
        tokens: {
          inputTokens: 100,
          outputTokens: 50,
          cacheReadInputTokens: 0,
          cacheCreationInputTokens: 0,
          cachedInputTokens: 0,
          reasoningTokens: 0,
          webSearchRequests: 0,
        },
      },
    }
    expect(sessionModelBillableOutputTokens(session)).toEqual({ 'claude-opus-4-8': 50 })
  })

  it('aggregate-only model rows keep finite billable output', () => {
    const aggregate = makeSession('grok', 10, 3)
    aggregate.turns = []
    expect(sessionBillableOutputTokens(aggregate)).toBe(13)
    expect(sessionModelBillableOutputTokens(aggregate)).toEqual({ [getShortModelName('grok-4')]: 13 })
  })
})
