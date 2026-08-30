import { describe, expect, it } from 'vitest'
import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js'
import { createServer } from '../src/mcp/server.js'
import type { MenubarPayload } from '../src/menubar-json.js'

function fakePayload(calls = 100, cost = 9): MenubarPayload {
  return {
    generated: new Date().toISOString(),
    optimize: { findingCount: 1, savingsUSD: 2, topFindings: [{ title: 'X', impact: 'high', savingsUSD: 2 }] },
    history: {
      daily: [],
      timeline: { points: [{ ts: 0, cost: 1 } as unknown as never], sessionSeries: [] as never } as never,
    },
    current: {
      label: 'Today',
      cost,
      calls,
      sessions: 3,
      oneShotRate: 0.5,
      inputTokens: 1000,
      outputTokens: 500,
      cacheReadTokens: 200,
      cacheWriteTokens: 50,
      cacheHitPercent: 16.6,
      codexCredits: 0,
      estimatedCostUSD: 0,
      topActivities: [{ name: 'feature', cost: 9, savingsUSD: 0, turns: 5, oneShotRate: 0.5 }],
      topModels: [{ name: 'Opus 4.8', cost: 9, savingsUSD: 0, savingsBaselineModel: '', calls: 10 }],
      unpricedModels: [],
      localModelSavings: { totalUSD: 0, calls: 0, byModel: [], byProvider: [] },
      providers: { 'claude code': 9 },
      providerDetails: [{ id: 'claude', label: 'Claude Code', cost: 9 }],
      topProjects: [{ name: 'real-repo', cost: 9, savingsUSD: 0, sessions: 1, avgCostPerSession: 9, sessionDetails: [] }],
      modelEfficiency: [],
      topSessions: [{ project: 'real-repo', cost: 9, savingsUSD: 0, calls, date: '2026-06-01' }],
      workflow: { corrections: 0, correctionRate: null, medianTimeToFirstEditMs: null },
      topReworkedFiles: [],
      pricingCoverage: 1,
      retryTax: { totalUSD: 1, retries: 2, editTurns: 5, byModel: [{ name: 'Opus 4.8', taxUSD: 1, retries: 2, retriesPerEdit: 0.4 }] },
      routingWaste: { totalSavingsUSD: 1, baselineModel: 'Haiku 4.5', baselineCostPerEdit: 0.01, byModel: [] },
      tools: [{ name: 'Read', calls: 5 }],
      skills: [],
      subagents: [],
      mcpServers: [{ name: 'github', calls: 2 }],
    },
    currency: { code: 'USD', symbol: '$', rate: 1 },
  } as unknown as MenubarPayload
}

async function connect(
  aggregate: (p: unknown, o: unknown) => Promise<MenubarPayload>,
  kyberStore?: { listQuarantine: () => unknown[]; getProblems: () => unknown[] },
) {
  const server = createServer({ version: 'test', aggregate: aggregate as never, kyberStore: kyberStore as never })
  const [a, b] = InMemoryTransport.createLinkedPair()
  const client = new Client({ name: 'test', version: '1' })
  await Promise.all([server.connect(a), client.connect(b)])
  return client
}

describe('mcp kyber parity — R11.6', () => {
  it('exposes 8 tools including the 6 KyberDash analyses', async () => {
    const client = await connect(async () => fakePayload())
    const { tools } = await client.listTools()
    const names = tools.map(t => t.name).sort()
    expect(names).toEqual([
      'get_comparison',
      'get_context_analysis',
      'get_problems',
      'get_quarantine',
      'get_savings',
      'get_schema_cost',
      'get_timeline',
      'get_usage',
    ])
  })

  it('MCP payload and status payload agree on core figures (tokens, cost, calls)', async () => {
    const payload = fakePayload(42, 12.34)
    const statusPayload = payload // status contract JSON is the menubar payload
    const client = await connect(async () => payload)

    const toolsToCheck = [
      'get_context_analysis',
      'get_schema_cost',
      'get_timeline',
      'get_comparison',
    ] as const

    for (const tool of toolsToCheck) {
      const res = await client.callTool({ name: tool, arguments: { period: 'today' } })
      const sc = (res as unknown as { structuredContent: { totals: { costUSD: number; calls: number; inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheWriteTokens: number } } }).structuredContent
      expect(sc.totals.costUSD, `${tool} cost`).toBe(statusPayload.current.cost)
      expect(sc.totals.calls, `${tool} calls`).toBe(statusPayload.current.calls)
      expect(sc.totals.inputTokens, `${tool} inputTokens`).toBe(statusPayload.current.inputTokens)
      expect(sc.totals.outputTokens, `${tool} outputTokens`).toBe(statusPayload.current.outputTokens)
      expect(sc.totals.cacheReadTokens, `${tool} cacheRead`).toBe(statusPayload.current.cacheReadTokens)
      expect(sc.totals.cacheWriteTokens, `${tool} cacheWrite`).toBe(statusPayload.current.cacheWriteTokens)
      expect((sc as unknown as { period: string }).period).toBe(statusPayload.current.label)
    }
  })

  it('get_quarantine and get_problems return same figures as the canonical store (status contract source)', async () => {
    const quarantine = [{ spanId: 'span-1', namespaces: ['gen_ai'], reason: 'no adapter' }]
    const problems = [{ spanId: 'span-1', severity: 'error', code: 'TOK_NEG', message: 'negative fresh input' }]
    const payload = fakePayload()
    const kyberStore = {
      listQuarantine: () => quarantine,
      getProblems: () => problems,
    }
    const client = await connect(async () => payload, kyberStore)

    const qRes = await client.callTool({ name: 'get_quarantine', arguments: { period: 'today' } })
    const qSc = (qRes as unknown as { structuredContent: { quarantine: unknown[]; period: string } }).structuredContent
    expect(qSc.quarantine).toEqual(quarantine)
    expect(qSc.period).toBe(payload.current.label)

    const pRes = await client.callTool({ name: 'get_problems', arguments: { period: 'today' } })
    const pSc = (pRes as unknown as { structuredContent: { problems: unknown[]; period: string } }).structuredContent
    expect(pSc.problems).toEqual(problems)
    expect(pSc.period).toBe(payload.current.label)
  })

  it('all kyber analysis tools share the same cost/tokens as the status payload for the same corpus', async () => {
    const payload = fakePayload(77, 99.99)
    // Simulate status contract fetch is the same aggregate
    const statusCost = payload.current.cost
    const statusTokens = payload.current.inputTokens + payload.current.outputTokens
    const client = await connect(async () => payload)
    const res = await client.callTool({ name: 'get_context_analysis', arguments: { period: 'today' } })
    const sc = (res as unknown as { structuredContent: { totals: { costUSD: number; inputTokens: number; outputTokens: number } } }).structuredContent
    expect(sc.totals.costUSD).toBe(statusCost)
    expect(sc.totals.inputTokens + sc.totals.outputTokens).toBe(statusTokens)
  })

  it('timeline tool returns the same history.timeline as the status payload (sessions redacted)', async () => {
    const payload = fakePayload()
    const client = await connect(async () => payload)
    const res = await client.callTool({ name: 'get_timeline', arguments: { period: 'today' } })
    const sc = (res as unknown as { structuredContent: { timeline: { points: unknown[] } | null } }).structuredContent
    // redactProjectNames clears sessions on timeline points — MCP and status share that redaction
    const rawPoints = (payload.history as unknown as { timeline: { points: Array<Record<string, unknown>> } }).timeline.points
    const expectedPoints = rawPoints.map(p => ({ ...p, sessions: [] }))
    expect(sc.timeline?.points).toEqual(expectedPoints)
  })
})
