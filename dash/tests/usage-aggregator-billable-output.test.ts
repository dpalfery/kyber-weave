import { describe, expect, it, beforeAll, vi } from 'vitest'

import { buildMenubarPayloadForRange } from '../src/usage-aggregator.js'
import { getDateRange } from '../src/cli-date.js'
import { loadPricing } from '../src/models.js'
import type { ProjectSummary } from '../src/types.js'

const ts = new Date().toISOString()

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
    savingsUSD: 1,
    savingsBaselineModel: 'gpt-4o',
    tools: [],
    mcpTools: [],
    skills: [],
    subagentTypes: [],
    hasAgentSpawn: false,
    hasPlanMode: false,
    speed: 'standard' as const,
    timestamp: ts,
    bashCommands: [],
    deduplicationKey: `${provider}-sav`,
  }
}

const emptyCat = { turns: 0, costUSD: 0, savingsUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 }

function fixtureProjects(): ProjectSummary[] {
  const grok = makeCall('grok', 10, 3)
  const codex = makeCall('codex', 10, 3)
  return [{
    project: 'proj',
    projectPath: 'proj',
    sessions: [{
      sessionId: 'sess-billable',
      project: 'proj',
      firstTimestamp: ts,
      lastTimestamp: ts,
      totalCostUSD: 0,
      totalSavingsUSD: 2,
      totalInputTokens: 0,
      totalOutputTokens: 20,
      totalReasoningTokens: 6,
      totalCacheReadTokens: 0,
      totalCacheWriteTokens: 0,
      apiCalls: 2,
      turns: [{
        userMessage: 'hi',
        timestamp: ts,
        sessionId: 'sess-billable',
        category: 'coding',
        retries: 0,
        hasEdits: false,
        assistantCalls: [grok, codex],
      }],
      modelBreakdown: {},
      toolBreakdown: {},
      mcpBreakdown: {},
      bashBreakdown: {},
      subagentBreakdown: {},
      categoryBreakdown: { coding: { ...emptyCat, turns: 1, savingsUSD: 2 } },
      skillBreakdown: {},
    }],
    totalCostUSD: 0,
    totalSavingsUSD: 2,
    totalApiCalls: 2,
  }] as unknown as ProjectSummary[]
}

vi.mock('../src/parser.js', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../src/parser.js')>()
  return { ...mod, parseAllSessions: vi.fn(async () => fixtureProjects()) }
})

describe('buildMenubarPayloadForRange: localModelSavings billable output', () => {
  beforeAll(async () => {
    await loadPricing()
  })

  it('writes per-call billableOutputTokens into byModel.outputTokens', async () => {
    const payload = await buildMenubarPayloadForRange(getDateRange('today'), { provider: 'all', optimize: false })
    const byModel = payload.current.localModelSavings.byModel
    const grok = byModel.find(m => m.outputTokens === 13)
    const codex = byModel.find(m => m.outputTokens === 10)
    expect(grok).toBeDefined()
    expect(codex).toBeDefined()
    expect(grok!.outputTokens).toBe(13)
    expect(codex!.outputTokens).toBe(10)
  })
})
