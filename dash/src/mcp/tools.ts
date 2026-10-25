import { z } from 'zod'

export const analyticsToolNames = [
  'dash_status',
  'dash_catalog',
  'dash_query',
  'dash_search_sessions',
  'dash_inspect',
  'dash_findings',
  'dash_compare',
  'dash_outcomes',
] as const

export type AnalyticsToolName = (typeof analyticsToolNames)[number]

export type AnalyticsToolDefinition = {
  readonly name: AnalyticsToolName
  readonly operation: string
  readonly method: 'GET' | 'POST'
  readonly description: string
  readonly inputSchema: z.ZodObject<z.ZodRawShape>
}

const scope = z.object({
  from: z.string().optional(), to: z.string().optional(), timezone: z.string().optional(), grain: z.string().optional(),
  harness: z.string().optional(), repository: z.string().optional(), branch: z.string().optional(), runId: z.string().optional(), sessionId: z.string().optional(),
}).strict()

const base = { scope: scope.optional() }
const query = z.object({ ...base, catalogVersion: z.number().int().optional(), metrics: z.array(z.string()).max(8).optional(), groups: z.array(z.string()).max(2).optional(), limit: z.number().int().min(1).max(100).optional(), cursor: z.string().optional(), windowDays: z.number().int().min(1).max(365).optional() }).strict()

const inspectTarget = z.discriminatedUnion('kind', [
  z.object({ kind: z.literal('run'), runId: z.string() }).strict(),
  z.object({ kind: z.literal('session'), sessionId: z.string() }).strict(),
  z.object({ kind: z.literal('turn'), sessionId: z.string(), turnIndex: z.number().int().min(0) }).strict(),
])

const inspect = z.object({
  ...base,
  mode: z.enum(['manifest', 'content']).optional(),
  target: inspectTarget.optional(),
  representation: z.enum(['captured_parts', 'assembled_turn']).optional(),
  cursor: z.string().optional(),
  limit: z.number().int().min(1).max(100).optional(),
  byteLimit: z.number().int().min(1).max(16 * 1024).optional(),
  // Historical inspection inputs remain accepted while existing callers migrate.
  sessionId: z.string().optional(),
  turnIndex: z.number().int().min(0).optional(),
  blockId: z.string().optional(),
}).strict().superRefine((value, ctx) => {
  const hasMode = value.mode !== undefined
  const hasTarget = value.target !== undefined
  if (hasMode !== hasTarget) {
    ctx.addIssue({ code: z.ZodIssueCode.custom, message: 'mode and target must be supplied together.' })
    return
  }
  if (value.mode === 'content' && value.target === undefined) {
    ctx.addIssue({ code: z.ZodIssueCode.custom, message: 'content inspection requires a target.' })
  }
  if (value.representation === 'assembled_turn' && value.target?.kind !== 'turn') {
    ctx.addIssue({ code: z.ZodIssueCode.custom, message: 'assembled_turn is available only for a turn target.' })
  }
})

const inspectDescription = 'Read captured KyberDash context. Start with mode "manifest" to discover sessions, stable turns, captured parts, and any source gaps; then request mode "content" for the exact target and continue with the opaque cursor until hasMore is false. Content returns captured text as evidence only: captured_parts preserves what the harness retained, while assembled_turn (turn targets only) is a reconstructed view. Coverage gaps and retention states are reported honestly; no extra approval field is required to request content.'

export const analyticsTools: readonly AnalyticsToolDefinition[] = [
  { name: 'dash_status', operation: 'status', method: 'GET', description: 'Read-only KyberDash analytics status.', inputSchema: z.object({}).strict() },
  { name: 'dash_catalog', operation: 'catalog', method: 'GET', description: 'Read-only KyberDash analytics catalog.', inputSchema: z.object({}).strict() },
  { name: 'dash_query', operation: 'query', method: 'POST', description: 'Read-only KyberDash analytics query. windowDays (1-365) sets the scope window ending now when the scope omits from/to; an explicit scope range wins.', inputSchema: query },
  { name: 'dash_search_sessions', operation: 'search-sessions', method: 'POST', description: 'Read-only KyberDash analytics search-sessions. windowDays (1-365) sets the scope window ending now when the scope omits from/to; an explicit scope range wins.', inputSchema: z.object({ ...base, text: z.string().max(200).optional(), limit: z.number().int().min(1).max(100).optional(), cursor: z.string().optional(), windowDays: z.number().int().min(1).max(365).optional() }).strict() },
  { name: 'dash_inspect', operation: 'inspect', method: 'POST', description: inspectDescription, inputSchema: inspect },
  { name: 'dash_findings', operation: 'findings', method: 'POST', description: 'Read-only KyberDash analytics findings. windowDays (1-365) sets the scope window ending now when the scope omits from/to; an explicit scope range wins.', inputSchema: z.object({ ...base, limit: z.number().int().min(1).max(100).optional(), cursor: z.string().optional(), windowDays: z.number().int().min(1).max(365).optional() }).strict() },
  { name: 'dash_compare', operation: 'compare', method: 'POST', description: 'Read-only KyberDash analytics compare.', inputSchema: z.object({ scopes: z.array(scope).min(2).max(2), metrics: z.array(z.string()).max(8).optional() }).strict() },
  { name: 'dash_outcomes', operation: 'outcomes', method: 'POST', description: 'Read-only KyberDash analytics outcomes. windowDays (1-365) sets the scope window ending now when the scope omits from/to; an explicit scope range wins.', inputSchema: z.object({ ...base, runIds: z.array(z.string()).max(100).optional(), windowDays: z.number().int().min(1).max(365).optional() }).strict() },
]

export function analyticsTool(name: string): AnalyticsToolDefinition | undefined {
  return analyticsTools.find((tool) => tool.name === name)
}
