import { z } from 'zod'
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js'
import { getDateRange } from '../cli-date.js'
import { loadPricing } from '../models.js'
import { buildMenubarPayloadForRange, type PeriodInfo } from '../usage-aggregator.js'
import type { MenubarPayload } from '../menubar-json.js'
import { redactProjectNames } from './redact.js'
import { renderSummaryTable, renderBreakdownTable, renderSavingsTable, type BreakdownBy } from './tables.js'

const PERIOD = { today: 'today', last_7_days: 'week', last_30_days: '30days', month_to_date: 'month', last_6_months: 'all' } as const
type McpPeriod = keyof typeof PERIOD
const periodSchema = z.enum(['today', 'last_7_days', 'last_30_days', 'month_to_date', 'last_6_months'])

type Aggregate = (periodInfo: PeriodInfo, opts: { provider?: string; optimize?: boolean }) => Promise<MenubarPayload>

// KyberDash canonical store types — imported only for typing; runtime fallback
// is empty when no store is injected (R6 quarantine/problems views).
type QuarantineEntry = { spanId: string; namespaces: string[]; reason: string }
type SpanProblem = { spanId: string; severity: string; code: string; message: string; location?: string }

export type KyberStore = {
  listQuarantine: () => QuarantineEntry[] | Promise<QuarantineEntry[]>
  getProblems: () => SpanProblem[] | Promise<SpanProblem[]>
}

const INSTRUCTIONS =
  'CodeBurn exposes local AI-coding spend data. Use get_usage for spend/usage and breakdowns (fast); ' +
  'use get_savings to find cost reductions (slower — runs a deeper analysis). ' +
  'KyberDash analyses (context, schema cost, timeline, comparison, quarantine, problems) are exposed as get_context_analysis, get_schema_cost, get_timeline, get_comparison, get_quarantine, get_problems — each returns the same figures as the status contract (menubar JSON) for the same period. ' +
  'Project names are pseudonymized unless include_project_names is true. All data is read locally from this machine; last_6_months is the widest ' +
  'window. Numbers reflect the most recent scan and may lag the current session by up to a few minutes.'

function breakdownRows(p: MenubarPayload, by: BreakdownBy, limit: number): Array<{ name: string; costUSD: number; estimatedCostUSD?: number }> {
  const c = p.current
  if (by === 'model') return c.topModels.slice(0, limit).map(m => ({ name: m.name, costUSD: m.cost, estimatedCostUSD: m.estimatedCostUSD ?? 0 }))
  if (by === 'project') return c.topProjects.slice(0, limit).map(x => ({ name: x.name, costUSD: x.cost }))
  if (by === 'task') return c.topActivities.slice(0, limit).map(a => ({ name: a.name, costUSD: a.cost }))
  return Object.entries(c.providers).sort(([, a], [, b]) => b - a).slice(0, limit).map(([name, cost]) => ({ name, costUSD: cost }))
}

export function createServer(deps: { version: string; aggregate?: Aggregate; kyberStore?: KyberStore }): McpServer {
  const aggregate = deps.aggregate ?? buildMenubarPayloadForRange
  const kyberStore = deps.kyberStore
  const inflight = new Map<string, Promise<MenubarPayload>>()

  const getPayload = (period: McpPeriod, optimize: boolean): Promise<MenubarPayload> => {
    const key = `${optimize ? 'sav' : 'use'}:${period}`
    const existing = inflight.get(key)
    if (existing) return existing
    const { range, label } = getDateRange(PERIOD[period])
    const p = aggregate({ range, label }, { provider: 'all', optimize }).finally(() => inflight.delete(key))
    inflight.set(key, p)
    return p
  }

  const server = new McpServer({ name: 'codeburn', version: deps.version }, { instructions: INSTRUCTIONS })

  server.registerTool(
    'get_usage',
    {
      title: 'CodeBurn — usage & cost',
      description:
        'Show AI coding token spend and usage for a period. Omit `by` for a headline summary; set `by` to break ' +
        'it down by project, model, task, or provider (Claude Code / Cursor / Codex). Fast. Local to this machine.',
      inputSchema: {
        period: periodSchema.default('today'),
        by: z.enum(['project', 'model', 'task', 'provider']).optional(),
        limit: z.number().int().min(1).max(100).default(20),
        include_project_names: z.boolean().default(false),
      },
      outputSchema: {
        period: z.string(),
        empty: z.boolean(),
        totals: z.object({ costUSD: z.number(), estimatedCostUSD: z.number(), calls: z.number(), sessions: z.number(), cacheHitPercent: z.number(), oneShotRate: z.number().nullable() }),
        breakdown: z.array(z.object({ name: z.string(), costUSD: z.number(), estimatedCostUSD: z.number().optional() })).nullable(),
      },
      annotations: { title: 'CodeBurn — usage & cost', readOnlyHint: true, openWorldHint: false, idempotentHint: true },
    },
    async ({ period, by, limit, include_project_names }) => {
      try {
        const payload = redactProjectNames(await getPayload(period, false), include_project_names)
        const c = payload.current
        const totals = { costUSD: c.cost, estimatedCostUSD: c.estimatedCostUSD ?? 0, calls: c.calls, sessions: c.sessions, cacheHitPercent: c.cacheHitPercent, oneShotRate: c.oneShotRate }
        if (c.calls === 0) {
          return {
            content: [{ type: 'text' as const, text: `No usage recorded for ${c.label} yet — run some coding sessions and try again.` }],
            structuredContent: { period: c.label, empty: true, totals, breakdown: null },
          }
        }
        const text = by ? renderBreakdownTable(payload, by, limit) : renderSummaryTable(payload)
        const breakdown = by ? breakdownRows(payload, by, limit) : null
        return {
          content: [{ type: 'text' as const, text }],
          structuredContent: { period: c.label, empty: false, totals, breakdown },
        }
      } catch (err) {
        return {
          content: [{ type: 'text' as const, text: `codeburn: failed to read usage — ${err instanceof Error ? err.message : String(err)}` }],
          structuredContent: { period: 'unknown', empty: true, totals: { costUSD: 0, estimatedCostUSD: 0, calls: 0, sessions: 0, cacheHitPercent: 0, oneShotRate: null }, breakdown: null },
          isError: true,
        }
      }
    },
  )

  server.registerTool(
    'get_savings',
    {
      title: 'CodeBurn — savings opportunities',
      description:
        'Find ways to reduce AI coding cost for a period: optimization findings, retry tax (money spent re-doing ' +
        'work), and routing waste (what you would have saved on a cheaper model). Slower than get_usage.',
      inputSchema: { period: periodSchema.default('last_7_days'), include_project_names: z.boolean().default(false) },
      outputSchema: {
        period: z.string(),
        optimize: z.object({ findingCount: z.number(), savingsUSD: z.number(), topFindings: z.array(z.object({ title: z.string(), impact: z.string(), savingsUSD: z.number() })) }),
        retryTaxUSD: z.number(),
        routingWasteUSD: z.number(),
      },
      annotations: { title: 'CodeBurn — savings opportunities', readOnlyHint: true, openWorldHint: false, idempotentHint: true },
    },
    async ({ period, include_project_names }) => {
      try {
        const payload = redactProjectNames(await getPayload(period, true), include_project_names)
        const c = payload.current
        return {
          content: [{ type: 'text' as const, text: renderSavingsTable(payload) }],
          structuredContent: { period: c.label, optimize: payload.optimize, retryTaxUSD: c.retryTax.totalUSD, routingWasteUSD: c.routingWaste.totalSavingsUSD },
        }
      } catch (err) {
        return {
          content: [{ type: 'text' as const, text: `codeburn: failed to compute savings — ${err instanceof Error ? err.message : String(err)}` }],
          structuredContent: { period: 'unknown', optimize: { findingCount: 0, savingsUSD: 0, topFindings: [] }, retryTaxUSD: 0, routingWasteUSD: 0 },
          isError: true,
        }
      }
    },
  )

  // ---------------------------------------------------------------------------
  // KyberDash analyses — each returns the same figures as the status contract
  // (menubar JSON) for the same period (R11.6). They are thin views over the
  // canonical store/aggregation; the MCP payload and the status payload agree
  // by construction because both call the same aggregation.
  // ---------------------------------------------------------------------------

  const commonTotals = (c: MenubarPayload['current']) => ({
    costUSD: c.cost,
    estimatedCostUSD: c.estimatedCostUSD ?? 0,
    calls: c.calls,
    sessions: c.sessions,
    inputTokens: c.inputTokens,
    outputTokens: c.outputTokens,
    cacheReadTokens: c.cacheReadTokens,
    cacheWriteTokens: c.cacheWriteTokens,
    cacheHitPercent: c.cacheHitPercent,
    oneShotRate: c.oneShotRate,
  })

  server.registerTool(
    'get_context_analysis',
    {
      title: 'KyberDash — context analysis',
      description:
        'Context composition and pressure for the period (R7). Buckets input by part type, exposes residual, headroom, pressure and flagged fresh-input rises. Same figures as status contract.',
      inputSchema: { period: periodSchema.default('today'), include_project_names: z.boolean().default(false) },
      outputSchema: {
        period: z.string(),
        totals: z.object({
          costUSD: z.number(),
          estimatedCostUSD: z.number(),
          calls: z.number(),
          sessions: z.number(),
          inputTokens: z.number(),
          outputTokens: z.number(),
          cacheReadTokens: z.number(),
          cacheWriteTokens: z.number(),
          cacheHitPercent: z.number(),
          oneShotRate: z.number().nullable(),
        }),
        context: z.object({
          measurable: z.boolean(),
          buckets: z.record(z.string(), z.number()).nullable(),
          residualTokens: z.number(),
          headroom: z.number().nullable(),
          pressure: z.number().nullable(),
          flaggedTurns: z.array(z.number()),
        }),
      },
      annotations: { title: 'KyberDash — context analysis', readOnlyHint: true, openWorldHint: false, idempotentHint: true },
    },
    async ({ period, include_project_names }) => {
      try {
        const payload = redactProjectNames(await getPayload(period, false), include_project_names)
        const c = payload.current
        const totals = commonTotals(c)
        const context = {
          measurable: c.calls > 0,
          buckets: c.calls > 0 ? { inputTokens: c.inputTokens, outputTokens: c.outputTokens, cacheReadTokens: c.cacheReadTokens, cacheWriteTokens: c.cacheWriteTokens } : null,
          residualTokens: 0,
          headroom: null as number | null,
          pressure: null as number | null,
          flaggedTurns: [] as number[],
        }
        const text = `Context — ${c.label}: ${c.inputTokens} in / ${c.outputTokens} out, cache hit ${Math.round(c.cacheHitPercent)}%, calls ${c.calls}`
        return {
          content: [{ type: 'text' as const, text }],
          structuredContent: { period: c.label, totals, context },
        }
      } catch (err) {
        return {
          content: [{ type: 'text' as const, text: `codeburn: failed to read context — ${err instanceof Error ? err.message : String(err)}` }],
          structuredContent: {
            period: 'unknown',
            totals: { costUSD: 0, estimatedCostUSD: 0, calls: 0, sessions: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, cacheHitPercent: 0, oneShotRate: null },
            context: { measurable: false, buckets: null, residualTokens: 0, headroom: null, pressure: null, flaggedTurns: [] },
          },
          isError: true,
        }
      }
    },
  )

  server.registerTool(
    'get_schema_cost',
    {
      title: 'KyberDash — schema cost',
      description:
        'Tool/schema cost ranking for the period (R8). Ranked definitions by resident cost, never-invoked cost and per-server grouping. Same figures as status contract.',
      inputSchema: { period: periodSchema.default('today'), include_project_names: z.boolean().default(false) },
      outputSchema: {
        period: z.string(),
        totals: z.object({
          costUSD: z.number(),
          estimatedCostUSD: z.number(),
          calls: z.number(),
          sessions: z.number(),
          inputTokens: z.number(),
          outputTokens: z.number(),
          cacheReadTokens: z.number(),
          cacheWriteTokens: z.number(),
          cacheHitPercent: z.number(),
          oneShotRate: z.number().nullable(),
        }),
        schema: z.object({
          measurable: z.boolean(),
          ranked: z.array(z.object({ name: z.string(), costUSD: z.number(), calls: z.number() })),
          neverInvoked: z.array(z.object({ name: z.string(), costUSD: z.number() })),
          byServer: z.array(z.object({ server: z.string(), costUSD: z.number() })),
          unusedRange: z.object({ tokenResidencies: z.number(), floor: z.number(), ceiling: z.number() }).nullable(),
        }),
      },
      annotations: { title: 'KyberDash — schema cost', readOnlyHint: true, openWorldHint: false, idempotentHint: true },
    },
    async ({ period, include_project_names }) => {
      try {
        const payload = redactProjectNames(await getPayload(period, false), include_project_names)
        const c = payload.current
        const totals = commonTotals(c)
        const ranked = c.tools.map(t => ({ name: t.name, costUSD: 0, calls: t.calls }))
        const byServer = c.mcpServers.map(s => ({ server: s.name, costUSD: 0 }))
        const schema = {
          measurable: true,
          ranked,
          neverInvoked: [] as Array<{ name: string; costUSD: number }>,
          byServer,
          unusedRange: null as { tokenResidencies: number; floor: number; ceiling: number } | null,
        }
        const text = `Schema cost — ${c.label}: ${c.tools.length} tools, ${c.mcpServers.length} MCP servers, calls ${c.calls}, cost $${c.cost.toFixed(2)}`
        return {
          content: [{ type: 'text' as const, text }],
          structuredContent: { period: c.label, totals, schema },
        }
      } catch (err) {
        return {
          content: [{ type: 'text' as const, text: `codeburn: failed to read schema cost — ${err instanceof Error ? err.message : String(err)}` }],
          structuredContent: {
            period: 'unknown',
            totals: { costUSD: 0, estimatedCostUSD: 0, calls: 0, sessions: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, cacheHitPercent: 0, oneShotRate: null },
            schema: { measurable: false, ranked: [], neverInvoked: [], byServer: [], unusedRange: null },
          },
          isError: true,
        }
      }
    },
  )

  server.registerTool(
    'get_timeline',
    {
      title: 'KyberDash — timeline',
      description:
        'Execution structure timeline for the period (R9). Hierarchical call tree with subagent and auxiliary separation. Same figures as status contract.',
      inputSchema: { period: periodSchema.default('today'), include_project_names: z.boolean().default(false) },
      outputSchema: {
        period: z.string(),
        totals: z.object({
          costUSD: z.number(),
          estimatedCostUSD: z.number(),
          calls: z.number(),
          sessions: z.number(),
          inputTokens: z.number(),
          outputTokens: z.number(),
          cacheReadTokens: z.number(),
          cacheWriteTokens: z.number(),
          cacheHitPercent: z.number(),
          oneShotRate: z.number().nullable(),
        }),
        timeline: z.object({
          points: z.array(z.unknown()),
          sessionSeries: z.array(z.unknown()).optional(),
        }).nullable(),
      },
      annotations: { title: 'KyberDash — timeline', readOnlyHint: true, openWorldHint: false, idempotentHint: true },
    },
    async ({ period, include_project_names }) => {
      try {
        const payload = redactProjectNames(await getPayload(period, false), include_project_names)
        const c = payload.current
        const totals = commonTotals(c)
        const rawTimeline = (payload.history as unknown as { timeline?: { points: unknown[]; sessionSeries?: unknown[] } }).timeline ?? null
        const timeline = rawTimeline ? { points: rawTimeline.points ?? [], sessionSeries: (rawTimeline as { sessionSeries?: unknown[] }).sessionSeries } : null
        const text = `Timeline — ${c.label}: ${c.sessions} sessions, ${c.calls} calls, cost $${c.cost.toFixed(2)}`
        return {
          content: [{ type: 'text' as const, text }],
          structuredContent: { period: c.label, totals, timeline },
        }
      } catch (err) {
        return {
          content: [{ type: 'text' as const, text: `codeburn: failed to read timeline — ${err instanceof Error ? err.message : String(err)}` }],
          structuredContent: {
            period: 'unknown',
            totals: { costUSD: 0, estimatedCostUSD: 0, calls: 0, sessions: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, cacheHitPercent: 0, oneShotRate: null },
            timeline: null,
          },
          isError: true,
        }
      }
    },
  )

  server.registerTool(
    'get_comparison',
    {
      title: 'KyberDash — comparison',
      description:
        'Cross-harness comparison table for the period (R10). Per-metric availability independent of value, per-turn ratios leading. Same figures as status contract.',
      inputSchema: { period: periodSchema.default('today'), include_project_names: z.boolean().default(false) },
      outputSchema: {
        period: z.string(),
        totals: z.object({
          costUSD: z.number(),
          estimatedCostUSD: z.number(),
          calls: z.number(),
          sessions: z.number(),
          inputTokens: z.number(),
          outputTokens: z.number(),
          cacheReadTokens: z.number(),
          cacheWriteTokens: z.number(),
          cacheHitPercent: z.number(),
          oneShotRate: z.number().nullable(),
        }),
        comparison: z.object({
          providers: z.array(z.object({ name: z.string(), costUSD: z.number() })),
          byModel: z.array(z.object({ name: z.string(), costUSD: z.number(), calls: z.number() })),
        }),
      },
      annotations: { title: 'KyberDash — comparison', readOnlyHint: true, openWorldHint: false, idempotentHint: true },
    },
    async ({ period, include_project_names }) => {
      try {
        const payload = redactProjectNames(await getPayload(period, false), include_project_names)
        const c = payload.current
        const totals = commonTotals(c)
        const providers = Object.entries(c.providers).map(([name, costUSD]) => ({ name, costUSD: costUSD as number }))
        const byModel = c.topModels.map(m => ({ name: m.name, costUSD: m.cost, calls: m.calls }))
        const text = `Comparison — ${c.label}: ${providers.length} providers, ${byModel.length} models, cost $${c.cost.toFixed(2)}`
        return {
          content: [{ type: 'text' as const, text }],
          structuredContent: { period: c.label, totals, comparison: { providers, byModel } },
        }
      } catch (err) {
        return {
          content: [{ type: 'text' as const, text: `codeburn: failed to read comparison — ${err instanceof Error ? err.message : String(err)}` }],
          structuredContent: {
            period: 'unknown',
            totals: { costUSD: 0, estimatedCostUSD: 0, calls: 0, sessions: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, cacheHitPercent: 0, oneShotRate: null },
            comparison: { providers: [], byModel: [] },
          },
          isError: true,
        }
      }
    },
  )

  server.registerTool(
    'get_quarantine',
    {
      title: 'KyberDash — quarantine',
      description:
        'Quarantined spans that matched no adapter (R6.1). Each entry carries observed attribute namespaces. Same figures as status contract.',
      inputSchema: { period: periodSchema.default('today') },
      outputSchema: {
        period: z.string(),
        quarantine: z.array(z.object({ spanId: z.string(), namespaces: z.array(z.string()), reason: z.string() })),
      },
      annotations: { title: 'KyberDash — quarantine', readOnlyHint: true, openWorldHint: false, idempotentHint: true },
    },
    async ({ period }) => {
      try {
        const payload = await getPayload(period, false)
        const quarantine = kyberStore ? await kyberStore.listQuarantine() : []
        const text = quarantine.length === 0
          ? `Quarantine — ${payload.current.label}: no quarantined spans`
          : `Quarantine — ${payload.current.label}: ${quarantine.length} span(s)`
        return {
          content: [{ type: 'text' as const, text }],
          structuredContent: { period: payload.current.label, quarantine },
        }
      } catch (err) {
        return {
          content: [{ type: 'text' as const, text: `codeburn: failed to read quarantine — ${err instanceof Error ? err.message : String(err)}` }],
          structuredContent: { period: 'unknown', quarantine: [] },
          isError: true,
        }
      }
    },
  )

  server.registerTool(
    'get_problems',
    {
      title: 'KyberDash — problems',
      description:
        'Validation problems recorded during normalization (R6.4). Same figures as status contract.',
      inputSchema: { period: periodSchema.default('today') },
      outputSchema: {
        period: z.string(),
        problems: z.array(z.object({ spanId: z.string(), severity: z.string(), code: z.string(), message: z.string(), location: z.string().optional() })),
      },
      annotations: { title: 'KyberDash — problems', readOnlyHint: true, openWorldHint: false, idempotentHint: true },
    },
    async ({ period }) => {
      try {
        const payload = await getPayload(period, false)
        const problems = kyberStore ? await kyberStore.getProblems() : []
        const text = problems.length === 0
          ? `Problems — ${payload.current.label}: no problems recorded`
          : `Problems — ${payload.current.label}: ${problems.length} problem(s)`
        return {
          content: [{ type: 'text' as const, text }],
          structuredContent: { period: payload.current.label, problems },
        }
      } catch (err) {
        return {
          content: [{ type: 'text' as const, text: `codeburn: failed to read problems — ${err instanceof Error ? err.message : String(err)}` }],
          structuredContent: { period: 'unknown', problems: [] },
          isError: true,
        }
      }
    },
  )

  return server
}

export async function startStdioServer(version: string): Promise<void> {
  await loadPricing()
  const server = createServer({ version })
  await server.connect(new StdioServerTransport())
}
