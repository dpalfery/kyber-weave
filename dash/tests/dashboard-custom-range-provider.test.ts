import { PassThrough } from 'node:stream'

import React from 'react'
import { render } from 'ink'
import stripAnsi from 'strip-ansi'
import { describe, expect, it, onTestFinished, vi } from 'vitest'

import type { ProjectSummary, SessionSummary } from '../src/types.js'

const { parseAllSessionsMock } = vi.hoisted(() => ({
  parseAllSessionsMock: vi.fn<
    Parameters<typeof import('../src/parser.js').parseAllSessions>,
    ReturnType<typeof import('../src/parser.js').parseAllSessions>
  >(),
}))

vi.mock('../src/parser.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/parser.js')>()
  return { ...actual, parseAllSessions: parseAllSessionsMock }
})

vi.mock('../src/providers/index.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/providers/index.js')>()
  const provider = (name: string) => ({
    name,
    displayName: name,
    modelDisplayName: (model: string) => model,
    toolDisplayName: (tool: string) => tool,
    discoverSessions: async () => [{ provider: name, project: name, path: `/tmp/${name}` }],
    createSessionParser: () => ({ async *parse() {} }),
  })
  return { ...actual, getAllProviders: async () => [provider('beta'), provider('gamma')] }
})

vi.mock('../src/usage-aggregator.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/usage-aggregator.js')>()
  return {
    ...actual,
    buildDurablePeriod: vi.fn(async () => ({
      data: { cost: 7, savingsUSD: 0, calls: 1, sessions: 1, inputTokens: 10, outputTokens: 5, cacheReadTokens: 0, cacheWriteTokens: 0 },
      carriedCostUSD: 0,
    })),
  }
})

vi.mock('../src/plan-usage.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/plan-usage.js')>()
  return { ...actual, getPlanUsages: vi.fn(async () => []) }
})

const BREAKDOWN = {
  coding: { turns: 1, costUSD: 7, savingsUSD: 0, retries: 0, editTurns: 0, oneShotTurns: 0 },
} as SessionSummary['categoryBreakdown']

function project(name: string, provider: string, cost: number): ProjectSummary {
  const timestamp = new Date().toISOString()
  const call = {
    provider, model: `${provider}-model`,
    usage: { inputTokens: 10, outputTokens: 5, cacheCreationInputTokens: 0, cacheReadInputTokens: 0, cachedInputTokens: 0, reasoningTokens: 0, webSearchRequests: 0 },
    costUSD: cost, tools: [], mcpTools: [], skills: [], subagentTypes: [], hasAgentSpawn: false,
    hasPlanMode: false, speed: 'standard' as const, timestamp, bashCommands: [], deduplicationKey: `${provider}:${name}`,
  }
  const session: SessionSummary = {
    sessionId: `${provider}-session`, project: name, firstTimestamp: timestamp, lastTimestamp: timestamp,
    totalCostUSD: cost, totalSavingsUSD: 0, totalInputTokens: 10, totalOutputTokens: 5,
    totalReasoningTokens: 0, totalCacheReadTokens: 0, totalCacheWriteTokens: 0, apiCalls: 1,
    turns: [{ userMessage: 'hi', assistantCalls: [call], timestamp, sessionId: `${provider}-session`, category: 'coding', retries: 0, hasEdits: false }],
    modelBreakdown: { [`${provider}-model`]: { calls: 1, costUSD: cost, savingsUSD: 0, tokens: call.usage } },
    toolBreakdown: {}, mcpBreakdown: {}, bashBreakdown: {}, categoryBreakdown: BREAKDOWN,
    skillBreakdown: {}, subagentBreakdown: {},
  }
  return {
    project: name,
    projectPath: `/repo/${name}`,
    sessions: [session],
    totalCostUSD: cost,
    totalSavingsUSD: 0,
    totalApiCalls: 1,
    totalProxiedCostUSD: 0,
  }
}

function tui() {
  const stdin = new PassThrough() as PassThrough & NodeJS.ReadStream
  const stdout = new PassThrough() as PassThrough & NodeJS.WriteStream
  stdin.isTTY = true
  stdin.setRawMode = () => stdin
  stdin.ref = () => stdin
  stdin.unref = () => stdin
  stdout.isTTY = true
  stdout.columns = 140
  stdout.rows = 50
  return { stdin, stdout }
}

describe('InteractiveDashboard custom-range provider switching', () => {
  it('finishes the provider reload instead of stranding a loading skeleton', async () => {
    const { InteractiveDashboard } = await import('../src/dashboard.js')
    const beta = project('beta-visible', 'beta', 7)
    const gamma = project('gamma-visible', 'gamma', 3)
    parseAllSessionsMock.mockResolvedValue([beta])
    const { stdin, stdout } = tui()
    const frames: string[] = []
    stdout.on('data', chunk => frames.push(stripAnsi(String(chunk))))
    const app = render(React.createElement(InteractiveDashboard, {
      initialProjects: [beta, gamma],
      initialPeriod: 'week',
      initialProvider: 'all',
      refreshSeconds: 0,
      windowColumns: 140,
      customRange: { start: new Date(Date.now() - 7 * 86_400_000), end: new Date() },
      customRangeLabel: 'custom test range',
    }), { stdin, stdout, debug: true, interactive: true, patchConsole: false })
    onTestFinished(() => app.unmount())
    await app.waitUntilRenderFlush()
    await new Promise(resolve => setTimeout(resolve, 20))
    await app.waitUntilRenderFlush()

    frames.length = 0
    stdin.write('p')
    await vi.waitFor(async () => {
      await app.waitUntilRenderFlush()
      const latest = frames.filter(frame => frame.trim()).at(-1) ?? ''
      expect(latest).toContain('$7.00 cost')
      expect(latest).not.toContain('Loading custom test range')
    })

    expect(parseAllSessionsMock).toHaveBeenCalledWith(expect.anything(), 'beta')
  })
})
