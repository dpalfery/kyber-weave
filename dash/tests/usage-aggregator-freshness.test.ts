import { describe, expect, it, beforeAll, vi } from 'vitest'

import { buildMenubarPayloadForRange } from '../src/usage-aggregator.js'
import { getDateRange } from '../src/cli-date.js'
import { loadPricing } from '../src/models.js'
import type { ProjectSummary } from '../src/types.js'

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

function sessionFor(sourceId: string, sourceLabel: string, sourcePath: string) {
  return {
    sessionId: `sess-${sourceId}`,
    project: `proj-${sourceId}`,
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
      sessionId: `sess-${sourceId}`,
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
    source: { id: sourceId, label: sourceLabel, path: sourcePath, kind: 'claude-config' },
  }
}

function fixtureProjects(): ProjectSummary[] {
  return [{
    project: 'proj-a',
    projectPath: 'proj-a',
    sessions: [sessionFor('claude-config:a', 'A', '/a')],
    totalCostUSD: 0.02,
    totalSavingsUSD: 7,
    totalApiCalls: 1,
  }, {
    project: 'proj-b',
    projectPath: 'proj-b',
    sessions: [sessionFor('claude-config:b', 'B', '/b')],
    totalCostUSD: 0.02,
    totalSavingsUSD: 7,
    totalApiCalls: 1,
  }] as unknown as ProjectSummary[]
}

vi.mock('../src/parser.js', async (importOriginal) => {
  const mod = await importOriginal<typeof import('../src/parser.js')>()
  let parseCalls = 0
  return {
    ...mod,
    parseAllSessions: vi.fn(async () => {
      parseCalls++
      return fixtureProjects()
    }),
    isSessionHydrationComplete: vi.fn(() => parseCalls === 1),
    sessionHydrationSnapshot: vi.fn(() => ({
      complete: parseCalls === 1,
      deferredForFirstPaint: false,
      indexedFiles: parseCalls,
      pendingFiles: 0,
    })),
  }
})

describe('buildMenubarPayloadForRange: hydration freshness marker', () => {
  beforeAll(async () => {
    await loadPricing()
  })

  it('reflects the primary parse hydration, not a later bystander parse', async () => {
    // Two parseAllSessions calls happen on the scoped branch: the primary one
    // (claude-only) and a second one for the 365-day history block. The mock
    // makes the primary complete (parseCalls === 1 -> true) and the second
    // incomplete (parseCalls === 2 -> false). The payload must trust the
    // primary outcome, captured immediately after it resolves.
    const payload = await buildMenubarPayloadForRange(getDateRange('today'), {
      provider: 'all',
      optimize: false,
      claudeConfigSourceId: 'claude-config:a',
    })
    expect(payload.stale).toBeUndefined()
  })
})
