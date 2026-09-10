import { describe, expect, it, beforeAll, vi } from 'vitest'

import { buildMenubarPayloadForRange } from '../src/usage-aggregator.js'
import { getDateRange } from '../src/cli-date.js'
import { loadPricing } from '../src/models.js'
import type { ProjectSummary } from '../src/types.js'

// The savings block counts REQUESTS: a copilot supplementary accounting call
// (rollup / paired store row) can carry configured model-savings too — its
// saved dollars must be kept while its call weight stays zero.
const ts = new Date().toISOString()

function makeCall(savingsUSD: number, supplementary: boolean) {
  return {
    provider: 'copilot',
    model: 'llama3.1:8b',
    usage: {
      inputTokens: 10,
      outputTokens: supplementary ? 0 : 20,
      cacheCreationInputTokens: 0,
      cacheReadInputTokens: 0,
      cachedInputTokens: 0,
      reasoningTokens: 0,
      webSearchRequests: 0,
    },
    costUSD: 0.01,
    savingsUSD,
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
    deduplicationKey: supplementary ? 'sav-supp' : 'sav-real',
    ...(supplementary ? { supplementaryAccounting: true } : {}),
  }
}

const emptyCat = { turns: 0, costUSD: 0, savingsUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 }

function fixtureProjects(): ProjectSummary[] {
  return [{
    project: 'proj',
    projectPath: 'proj',
    sessions: [{
      sessionId: 'sess-sav',
      project: 'proj',
      firstTimestamp: ts,
      lastTimestamp: ts,
      totalCostUSD: 0.02,
      totalSavingsUSD: 7,
      totalInputTokens: 20,
      totalOutputTokens: 20,
      totalCacheReadTokens: 0,
      totalCacheWriteTokens: 0,
      apiCalls: 1,
      turns: [{
        userMessage: 'hi',
        timestamp: ts,
        sessionId: 'sess-sav',
        category: 'coding',
        retries: 0,
        hasEdits: false,
        assistantCalls: [makeCall(5, false), makeCall(2, true)],
      }],
      modelBreakdown: {},
      toolBreakdown: {},
      mcpBreakdown: {},
      bashBreakdown: {},
      subagentBreakdown: {},
      categoryBreakdown: { coding: { ...emptyCat, turns: 1, costUSD: 0.02, savingsUSD: 7 } },
      skillBreakdown: {},
    }],
    totalCostUSD: 0.02,
    totalSavingsUSD: 7,
    totalApiCalls: 1,
  }] as unknown as ProjectSummary[]
}

vi.mock('../src/parser.js', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../src/parser.js')>()
  return { ...mod, parseAllSessions: vi.fn(async () => fixtureProjects()) }
})

describe('buildMenubarPayloadForRange: supplementary savings weight', () => {
  beforeAll(async () => {
    await loadPricing()
  })

  it('keeps supplementary saved dollars but counts only behavioral calls', async () => {
    const payload = await buildMenubarPayloadForRange(getDateRange('today'), { provider: 'all', optimize: false })
    const savings = payload.current.localModelSavings!

    expect(savings.totalUSD).toBeCloseTo(7, 10)
    expect(savings.calls).toBe(1)
    const byModel = savings.byModel.find(m => m.name.includes('llama'))!
    expect(byModel.savingsUSD).toBeCloseTo(7, 10)
    expect(byModel.calls).toBe(1)
    const byProvider = savings.byProvider.find(p => p.name === 'copilot')!
    expect(byProvider.savingsUSD).toBeCloseTo(7, 10)
    expect(byProvider.calls).toBe(1)
  })
})
