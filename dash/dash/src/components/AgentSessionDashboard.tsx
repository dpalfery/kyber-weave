import { useState, useMemo, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { cn, usd, fmtTokens, fmtNum } from '../lib/utils'
import { Card } from './ui/card'
import { Skeleton } from './ui/skeleton'
import { SessionInspectorDrawer } from './SessionInspectorDrawer'
import { SessionSpendCharts, CONTEXT_BUCKET_LABELS } from './SessionSpendCharts'
import { SchemaCostRanking, type SchemaCostAnalysis } from './SchemaCostRanking'
import { TimelineView, type TimelineNode, type CostBlock } from './kyber/TimelineView'
import { SessionCostPanel } from './SessionCostPanel'

// ---------------------------------------------------------------------------
// Types & Contracts
// ---------------------------------------------------------------------------

export interface SessionSummaryCost {
  value?: number | null
  currency?: string | null
  usd?: number | null
  credits?: number | null
  basis?: 'published_rates' | 'harness_reported' | string
  status?: string
  priced_turns?: number
  unpriced_turns?: number
  by_model?: Array<{
    model: string
    turns: number
    input: number
    output: number
    credits?: number | null
    usd?: number | null
    status?: string
  }>
}

export interface SessionWasteCost {
  usd_low?: number | null
  usd_high?: number | null
  credits_low?: number | null
  credits_high?: number | null
}

export interface SessionSummaryPayload {
  turn_count?: number | null
  reported_turn_count?: number | null
  request_count?: number | null
  total_input?: number | null
  total_output?: number | null
  total_cache_read?: number | null
  cache_hit_ratio?: number | null
  total_cache_creation?: number | null
  cache_creation_coverage?: number | null
  total_reasoning?: number | null
  duration_ms?: number | null
  models?: string[]
  tool_calls?: number | null
  tools_invoked?: number | null
  tools_offered?: number | null
  error_count?: number | null
  median_ttft_ms?: number | null
  aux_chat_calls?: number | null
  aux_models?: string[]
  aux_input?: number | null
  aux_output?: number | null
  unused_schema_per_turn?: number | null
  schema_tokens_per_turn?: number | null
  defs_turns?: number | null
  schema_waste_cost?: SessionWasteCost | null
  cost?: SessionSummaryCost | null
  span_count?: number | null
}

export interface ReconciliationRow {
  request: string
  root_input?: number | null
  sum_chat_input?: number | null
  input_match: boolean
  root_output?: number | null
  sum_chat_output?: number | null
  output_match: boolean
}

export interface ToolRow {
  name: string
  server?: string
  is_mcp?: boolean
  schema_tokens?: number | null
  turns_resident?: number | null
  total_schema_cost?: number | null
  invocations: number
  cost_per_invocation?: number | null
  result_tokens?: number | null
  in_definitions?: boolean
  results_not_recorded?: number | null
}

export interface ServerRow {
  server: string
  is_mcp?: boolean
  tools: number
  schema_tokens?: number | null
  total_schema_cost?: number | null
  invocations: number
  unused_tools: number
  unused_cost: number
}

export interface SessionTimelineNode {
  spanId: string
  traceId?: string
  parentId?: string | null
  name: string
  op?: string
  kind?: string
  durationMs?: number
  offsetMs?: number
  startMs?: number
  status?: string
  input?: number | null
  output?: number | null
  tool?: string | null
  content?: Record<string, unknown>
  attributes?: Record<string, unknown>
  isSubagent?: boolean
  isAuxiliary?: boolean
  cost?: CostBlock | any
  children?: SessionTimelineNode[]
}

export interface SessionRequestItem {
  request: string
  timestamp?: string
  turns?: number
  model?: string
}

export interface AgentSessionPayload {
  id?: string
  session_id?: string
  harness?: string
  label?: string | null
  traceId?: string | null
  repo?: string | null
  branch?: string | null
  commit?: string | null
  agent_type?: string | null
  agent_name?: string | null
  is_subagent?: boolean
  parent_session?: string | null
  span_count?: number
  summary?: SessionSummaryPayload
  turns?: any[]
  tools?: ToolRow[]
  servers?: ServerRow[]
  context?: any
  coverage?: Record<string, number>
  notes?: string[]
  timeline?: SessionTimelineNode[]
  reconciliation?: ReconciliationRow[]
  requests?: SessionRequestItem[]
}

export interface AgentSessionDashboardProps {
  session?: AgentSessionPayload | any
  sessionId?: string
  isLoading?: boolean
  error?: Error | string | null
  onSelectSession?: (sessionId: string) => void
  initialTimelineTab?: 'tree' | 'bars'
  className?: string
}

// ---------------------------------------------------------------------------
// Helpers & Formatters
// ---------------------------------------------------------------------------

export function formatDuration(ms?: number | null): string {
  if (ms == null || !isFinite(ms)) return '—'
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  const minutes = Math.floor(ms / 60000)
  const seconds = Math.round((ms % 60000) / 1000)
  return `${minutes}m ${seconds}s`
}

export function formatCredits(c?: number | null): string {
  if (c == null || !isFinite(c)) return '—'
  return c >= 100 ? c.toFixed(0) : c.toFixed(2)
}

export const TIMELINE_OP_COLORS: Record<string, string> = {
  invoke_agent: '#3b82f6', // blue
  agent: '#3b82f6',
  chat: '#f59e0b', // amber
  execute_tool: '#10b981', // emerald
  tool: '#10b981',
  execute_hook: '#ec4899', // pink
  hook: '#ec4899',
  embeddings: '#8b5cf6', // purple
}

function adaptTimelineNode(node: any, parentId: string | null = null): TimelineNode {
  const rawCost = node.cost ?? {}
  const cost: CostBlock = {
    basis: rawCost.basis ?? 'none',
    status: rawCost.status ?? (rawCost.value != null ? 'ok' : '—'),
    value: rawCost.value ?? node.cost_usd ?? undefined,
    currency: rawCost.currency ?? 'USD',
  }

  return {
    spanId: node.spanId || node.id || 'span',
    parentId: parentId ?? node.parentId ?? null,
    children: Array.isArray(node.children)
      ? node.children.map((c: any) => adaptTimelineNode(c, node.spanId))
      : [],
    startMs: node.offsetMs ?? node.startMs ?? 0,
    durationMs: node.durationMs ?? 0,
    kind: node.kind ?? node.op ?? 'span',
    name: node.name ?? 'unnamed',
    attributes: node.attributes ?? node.raw_attributes ?? {},
    isSubagent: Boolean(node.isSubagent || node.attributes?.['subagent.session_id']),
    isAuxiliary: Boolean(node.isAuxiliary || node.attributes?.['kyber.auxiliary']),
    cost,
  }
}

// ---------------------------------------------------------------------------
// Main Component: AgentSessionDashboard
// ---------------------------------------------------------------------------

export interface AgentSessionLoaderProps {
  sessionId: string
  onSelectSession?: (sessionId: string) => void
  initialTimelineTab?: 'tree' | 'bars'
  className?: string
}

export function AgentSessionLoader({
  sessionId,
  onSelectSession,
  initialTimelineTab,
  className,
}: AgentSessionLoaderProps) {
  const {
    data: fetchedSession,
    isLoading,
    isError,
    error,
    refetch,
  } = useQuery({
    queryKey: ['kyber-session', sessionId],
    queryFn: async () => {
      const res = await fetch(`/api/kyber/session/${encodeURIComponent(sessionId)}`)
      if (!res.ok) {
        const err = await res.json().catch(() => null)
        throw new Error(err?.error || `Failed to fetch session: HTTP ${res.status}`)
      }
      return res.json()
    },
  })

  if (isError) {
    return (
      <Card
        className={cn('m-6 border-red-500/30 bg-red-500/10 p-6', className)}
        data-testid="agent-dashboard-error"
      >
        <div className="flex items-start gap-3">
          <span className="text-xl" role="img" aria-label="error">
            ⚠️
          </span>
          <div className="space-y-2">
            <h3 className="font-semibold text-red-600 dark:text-red-400">Failed to load session</h3>
            <p className="text-xs text-foreground/80">
              {error instanceof Error ? error.message : String(error)}
            </p>
            <button
              type="button"
              onClick={() => refetch()}
              className="mt-2 rounded bg-interactive-secondary px-3 py-1 text-xs font-medium text-foreground hover:bg-card transition-colors border border-border"
            >
              Retry
            </button>
          </div>
        </div>
      </Card>
    )
  }

  if (isLoading) {
    return (
      <div className={cn('flex flex-col gap-6 p-6', className)} data-testid="agent-dashboard-loading">
        <div className="flex items-center gap-3">
          <Skeleton className="h-7 w-28 rounded-full" />
          <Skeleton className="h-6 w-48 rounded" />
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-3">
          {Array.from({ length: 12 }).map((_, i) => (
            <Skeleton key={i} className="h-20 rounded-lg" />
          ))}
        </div>
        <Skeleton className="h-72 rounded-lg" />
        <Skeleton className="h-64 rounded-lg" />
      </div>
    )
  }

  return (
    <AgentSessionContent
      session={fetchedSession ?? null}
      onSelectSession={onSelectSession}
      initialTimelineTab={initialTimelineTab}
      className={className}
    />
  )
}

export function AgentSessionContent({
  session,
  onSelectSession,
  initialTimelineTab,
  className,
}: {
  session: AgentSessionPayload | null
  onSelectSession?: (sessionId: string) => void
  initialTimelineTab?: 'tree' | 'bars'
  className?: string
}) {

  // 2. State for slide-out inspector drawer
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [drawerTitle, setDrawerTitle] = useState('')
  const [drawerSubtitle, setDrawerSubtitle] = useState<string | undefined>(undefined)
  const [drawerContent, setDrawerContent] = useState<any>(null)

  // 3. State for timeline view mode ('tree' vs 'bars')
  const [timelineTab, setTimelineTab] = useState<'tree' | 'bars'>(initialTimelineTab ?? 'tree')
  const [timelineTabSelected, setTimelineTabSelected] = useState(false)
  const [selectedSpanId, setSelectedSpanId] = useState<string | undefined>(undefined)
  // A navigation can ask to open a particular timeline mode. Once a viewer
  // chooses a tab, preserve that choice for the rest of this session.
  const activeTimelineTab = timelineTabSelected ? timelineTab : (initialTimelineTab ?? timelineTab)

  // The payload's timeline is a SINGLE root node — that is what buildTimeline
  // returns and what the payload shape documents — but both consumers below
  // treated it as an array. On a real session that threw "nodes is not
  // iterable" and the error boundary replaced the entire expanded view, while
  // every unit test passed because the fixtures happened to use an array.
  // Normalise once here so neither consumer has to care.
  const timelineNodes = useMemo(() => {
    const timeline = session?.timeline
    if (!timeline) return []
    return Array.isArray(timeline) ? timeline : [timeline]
  }, [session?.timeline])

  // 4. Map of spanId -> full timeline node for quick lookup on click
  const spanMap = useMemo(() => {
    const map = new Map<string, any>()
    const walk = (nodes: any[]) => {
      for (const node of nodes) {
        if (node?.spanId) {
          map.set(node.spanId, node)
        }
        if (Array.isArray(node?.children)) {
          walk(node.children)
        }
      }
    }
    walk(timelineNodes)
    return map
  }, [timelineNodes])

  // 5. Adapt session timeline into a single root TimelineNode for TimelineView
  const timelineRoot: TimelineNode | null = useMemo(() => {
    if (!session || timelineNodes.length === 0) return null
    return {
      spanId: 'session-root',
      parentId: null,
      children: timelineNodes.map((n: any) => adaptTimelineNode(n, 'session-root')),
      startMs: 0,
      durationMs: session.summary?.duration_ms ?? 0,
      kind: 'session',
      name: session.label || `${session.harness || 'Agent'} Session`,
      attributes: {},
      isSubagent: Boolean(session.is_subagent),
      isAuxiliary: false,
      cost: {
        basis: session.summary?.cost?.basis ?? 'none',
        status: session.summary?.cost?.status ?? 'ok',
        value: session.summary?.cost?.value ?? undefined,
        currency: session.summary?.cost?.currency ?? 'USD',
      },
    }
  }, [session])

  // 6. Flattened timeline spans for horizontal duration bars view
  const flattenedTimeline = useMemo(() => {
    const list: Array<{ node: any; depth: number }> = []
    const walk = (nodes: any[], depth = 0) => {
      for (const n of nodes) {
        list.push({ node: n, depth })
        if (Array.isArray(n.children)) {
          walk(n.children, depth + 1)
        }
      }
    }
    walk(timelineNodes, 0)
    return list
  }, [timelineNodes])

  const maxTimelineSpan = useMemo(() => {
    if (flattenedTimeline.length === 0) return 1
    return (
      Math.max(
        ...flattenedTimeline.map(
          ({ node }) => (node.offsetMs ?? node.startMs ?? 0) + (node.durationMs ?? 0)
        )
      ) || 1
    )
  }, [flattenedTimeline])

  // 7. Drawer trigger handlers
  const openDrawerForTurn = useCallback(
    (turnIndex: number, bucketName?: string) => {
      setSelectedSpanId(undefined)
      const turns = session?.turns || []
      const turn = turns.find(
        (t: any, i: number) =>
          t.index === turnIndex ||
          t.turn === turnIndex ||
          i === turnIndex ||
          i + 1 === turnIndex
      )
      if (!turn) return

      const turnNum = turn.index ?? turn.turn ?? turnIndex
      if (bucketName) {
        const contextTurns = session?.context?.turns || []
        const ctxTurn =
          contextTurns.find(
            (ct: any, i: number) =>
              ct.turn === turnNum ||
              ct.index === turnNum ||
              i === turnNum ||
              i + 1 === turnNum
          ) ||
          (turnNum === 1 ? session?.context?.first : undefined) ||
          session?.context?.last

        const rawBuckets = (ctxTurn?.buckets ?? (turn as any)?.buckets ?? {}) as Record<string, any>
        const tokens = Number(rawBuckets[bucketName] ?? (turn as any)?.[bucketName] ?? 0)
        const bucketSum = Object.values(rawBuckets).reduce(
          (sum: number, v: any) => sum + (typeof v === 'number' ? v : 0),
          0
        )
        const reported = Number(
          ctxTurn?.reported_input ??
            turn.cumulative_input ??
            turn.input ??
            turn.fresh ??
            bucketSum ??
            0
        )
        const total = Math.max(reported, bucketSum)
        const label =
          (CONTEXT_BUCKET_LABELS as Record<string, string>)[bucketName] || bucketName
        const content =
          turn.content && typeof turn.content === 'object' && bucketName in turn.content
            ? turn.content[bucketName]
            : ((turn as any)[bucketName] ?? (typeof turn.content === 'string' ? turn.content : turn))

        setDrawerTitle(`Turn ${turnNum} · ${bucketName}`)
        setDrawerSubtitle(
          `Bucket analysis · ${turn.model ? `Model: ${turn.model} · ` : ''}${fmtTokens(total || turn.cumulative_input || turn.input)} total tokens`
        )
        setDrawerContent({
          turnIndex,
          bucket: bucketName,
          key: bucketName,
          tokens,
          value: tokens,
          total,
          label,
          content,
          turn,
          context: session?.context,
        })
      } else {
        setDrawerTitle(`Turn ${turnNum}`)
        setDrawerSubtitle(
          `${turn.model ? `Model: ${turn.model} · ` : ''}${formatDuration(turn.durationMs)}`
        )
        setDrawerContent(turn)
      }
      setDrawerOpen(true)
    },
    [session]
  )

  const openDrawerForSpan = useCallback(
    (nodeOrId: any) => {
      const spanId = typeof nodeOrId === 'string' ? nodeOrId : nodeOrId?.spanId
      const fullSpan = (spanId ? spanMap.get(spanId) : null) ?? nodeOrId
      if (!fullSpan) return

      setSelectedSpanId(spanId)
      setDrawerTitle(`Span: ${fullSpan.name || fullSpan.spanId || 'Inspection'}`)
      setDrawerSubtitle(
        `${fullSpan.kind || fullSpan.op || 'span'} · ${formatDuration(fullSpan.durationMs)} · Span ID: ${fullSpan.spanId || '—'}`
      )
      setDrawerContent(fullSpan)
      setDrawerOpen(true)
    },
    [spanMap]
  )

  const openDrawerForTool = useCallback(
    (tool: ToolRow) => {
      setSelectedSpanId(undefined)
      setDrawerTitle(`Tool: ${tool.name}`)
      setDrawerSubtitle(
        `Server: ${tool.server || 'built-in'}${tool.is_mcp ? ' (MCP)' : ''} · Invocations: ${tool.invocations}`
      )

      // Find tool definition from turns if available
      const turnWithDefs = (session?.turns || []).find(
        (t: any) => t.has_tool_defs || t.content?.tool_definitions
      )
      const defs = turnWithDefs?.content?.tool_definitions || []
      const def = defs.find(
        (d: any) => (d.name || d.function?.name) === tool.name
      )

      setDrawerContent({
        tool,
        definition: def ?? null,
        contentRequest: {
          span: typeof turnWithDefs?.spanId === 'string' ? turnWithDefs.spanId : undefined,
          part: 'tool_definitions',
        },
      })
      setDrawerOpen(true)
    },
    [session]
  )

  // -------------------------------------------------------------------------
  // Render: Empty State
  // -------------------------------------------------------------------------

  if (!session) {
    return (
      <div
        className={cn('flex flex-col items-center justify-center p-12 text-center', className)}
        data-testid="agent-dashboard-empty"
      >
        <p className="text-sm font-medium text-muted-foreground">No agent session selected</p>
        <p className="mt-1 text-xs text-tertiary-foreground">
          Select a session from the list above or provide a valid sessionId.
        </p>
      </div>
    )
  }

  const u = session.summary || {}
  const rows = session.reconciliation || []
  const badReconciliation = rows.filter((r) => !(r.input_match && r.output_match))
  const cost = u.cost || {}
  const basis = cost.basis || 'none'
  const isReported = basis === 'harness_reported'

  // Tool definitions grouping
  const toolRows = session.tools || []
  const schemaOffered = toolRows.filter((t) => t.in_definitions)
  const isSchemaMeasurable = schemaOffered.length > 0

  return (
    <div
      className={cn('flex flex-col gap-6 p-4 sm:p-6 text-foreground', className)}
      data-testid="agent-session-dashboard"
    >
      {/* ---------------------------------------------------------------------
          Header Strip & Session Meta
      --------------------------------------------------------------------- */}
      <div className="flex flex-col gap-2 pb-2 border-b border-border/80">
        <div className="flex flex-wrap items-center gap-2.5">
          {session.harness && (
            <span
              className="rounded bg-primary/10 border border-primary/20 px-2.5 py-0.5 font-mono text-xs font-semibold text-primary uppercase tracking-wide"
              data-testid="session-harness-badge"
            >
              {session.harness}
            </span>
          )}
          <h1 className="text-base sm:text-lg font-semibold text-heading truncate" data-testid="session-label">
            {session.label || session.id || session.session_id || 'Session'}
          </h1>
          {session.agent_name && (
            <span className="rounded bg-interactive-secondary border border-border px-2 py-0.5 text-xs text-muted-foreground">
              agent: <span className="font-mono text-foreground">{session.agent_name}</span>
            </span>
          )}
          {session.repo && (
            <span className="text-xs text-tertiary-foreground font-mono">
              {session.branch ? `${session.branch} · ` : ''}
              {session.repo.split('/').pop()}
            </span>
          )}
          <span className="ml-auto text-xs text-tertiary-foreground font-mono tabular-nums">
            {fmtNum(session.span_count ?? u.span_count ?? 0)} spans
          </span>
        </div>

        {/* ---------------------------------------------------------------------
            Alerts & Banners: Subagent, Reconciliation, Caveats, Requests
        --------------------------------------------------------------------- */}
        <div className="flex flex-col gap-2.5 mt-2">
          {/* Subagent Notice */}
          {session.is_subagent && (
            <div
              className="rounded-md border border-amber-500/30 bg-amber-500/10 px-3.5 py-2.5 text-xs text-foreground/90"
              data-testid="subagent-notice"
            >
              <div className="flex flex-wrap items-center gap-1.5">
                <span className="font-semibold text-amber-600 dark:text-amber-400">
                  Subagent session
                </span>
                <span>— agent</span>
                <span className="font-mono font-medium text-foreground">
                  {session.agent_name || '?'}
                </span>
                <span>spawned by a <code className="font-mono">runSubagent</code> tool call</span>
                {session.parent_session && (
                  <>
                    <span>in parent session</span>
                    <button
                      type="button"
                      onClick={() => onSelectSession?.(session.parent_session!)}
                      className="font-mono font-semibold text-primary underline hover:text-primary/80 transition-colors"
                      data-testid="parent-session-link"
                    >
                      {session.parent_session.slice(0, 10)}…
                    </button>
                  </>
                )}
                <span className="text-tertiary-foreground">
                  (tokens are counted here and not folded into parent totals).
                </span>
              </div>
            </div>
          )}

          {/* Reconciliation Status Badge */}
          {rows.length > 0 && (
            <div>
              {badReconciliation.length === 0 ? (
                <div
                  className="rounded-md border border-emerald-500/30 bg-emerald-500/10 px-3.5 py-2 text-xs text-emerald-700 dark:text-emerald-300 flex items-center gap-2"
                  data-testid="reconciliation-ok"
                >
                  <span className="font-semibold shrink-0">✓ Reconciliation OK</span>
                  <span className="text-foreground/80">
                    across {rows.length} request{rows.length > 1 ? 's' : ''} — per-turn sums equal each{' '}
                    <code className="font-mono text-foreground">invoke_agent</code> total exactly (
                    {rows.map((r) => fmtTokens(r.root_input)).join(' + ')} tokens in).
                  </span>
                </div>
              ) : (
                <div
                  className="rounded-md border border-amber-500/30 bg-amber-500/10 px-3.5 py-2.5 text-xs text-amber-800 dark:text-amber-200 space-y-1"
                  data-testid="reconciliation-mismatch"
                >
                  <div className="font-semibold flex items-center gap-1.5">
                    <span>⚠️ Reconciliation MISMATCH</span>
                    <span className="font-normal text-foreground/80">
                      on {badReconciliation.length} of {rows.length} request(s):
                    </span>
                  </div>
                  <ul className="list-disc list-inside space-y-0.5 text-foreground/90 pl-1">
                    {badReconciliation.map((r, i) => {
                      const diff = (r.sum_chat_input || 0) - (r.root_input || 0)
                      return (
                        <li key={i} className="truncate">
                          <em>{r.request || `Request ${i + 1}`}</em> — chat sum {fmtTokens(r.sum_chat_input)} vs
                          root {fmtTokens(r.root_input)} ({diff > 0 ? '+' : ''}
                          {fmtTokens(diff)})
                        </li>
                      )
                    })}
                  </ul>
                </div>
              )}
            </div>
          )}

          {/* Harness Caveats / Notes Banner */}
          {session.notes && session.notes.length > 0 && (
            <div
              className="rounded-md border border-border bg-card/60 px-3.5 py-2.5 text-xs text-foreground/90 space-y-1"
              data-testid="harness-notes"
            >
              <div className="font-semibold text-muted-foreground flex items-center gap-1.5">
                <span>ℹ️ What {session.harness || 'this harness'} does not export:</span>
              </div>
              <ul className="list-disc list-inside space-y-0.5 text-muted-foreground pl-1">
                {session.notes.map((note, idx) => (
                  <li key={idx}>{note}</li>
                ))}
              </ul>
            </div>
          )}

          {/* User Request List (if multiple) */}
          {session.requests && session.requests.length > 1 && (
            <div
              className="rounded-md border border-border bg-card/60 px-3.5 py-2 text-xs text-muted-foreground"
              data-testid="user-requests"
            >
              <span className="font-semibold text-foreground mr-1.5">
                {session.requests.length} user requests in this session:
              </span>
              <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1">
                {session.requests.map((r, i) => (
                  <span key={i} className="inline-flex items-center gap-1">
                    <span className="font-mono text-tertiary-foreground">{i + 1}.</span>
                    <span className="text-foreground max-w-xs truncate" title={r.request}>
                      {r.request}
                    </span>
                    {r.turns !== undefined && (
                      <em className="text-tertiary-foreground text-[11px]">({r.turns} turns)</em>
                    )}
                  </span>
                ))}
              </div>
            </div>
          )}

          {/* Auxiliary chat calls banner */}
          {u.aux_chat_calls ? (
            <div
              className="rounded-md border border-border bg-interactive-secondary/40 px-3.5 py-2 text-xs text-muted-foreground"
              data-testid="aux-calls-banner"
            >
              <span className="font-semibold text-foreground mr-1">
                {u.aux_chat_calls} auxiliary {u.aux_models?.join(', ') || ''} call(s)
              </span>
              in this trace (e.g. conversation title generation) are excluded from the per-turn
              charts but cost {fmtTokens(u.aux_input)} in / {fmtTokens(u.aux_output)} out.
            </div>
          ) : null}
        </div>
      </div>

      {/* ---------------------------------------------------------------------
          Section 1: Overview Strip (Metric Cards)
      --------------------------------------------------------------------- */}
      <div className="space-y-2" data-testid="overview-strip-section">
        <h2 className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
          Session Overview
        </h2>
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-3">
          {/* 1. Total Input */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Total Input
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {u.total_input != null ? fmtTokens(u.total_input) : '—'}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground truncate">
              {u.total_cache_read ? `${fmtTokens(u.total_cache_read)} from cache` : 'reported tokens'}
            </div>
          </Card>

          {/* 2. Cache Read */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Cache Read
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-chart-2">
              {u.total_cache_read != null ? fmtTokens(u.total_cache_read) : '—'}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground">
              {u.cache_hit_ratio != null ? `${(u.cache_hit_ratio * 100).toFixed(1)}% hit ratio` : 'cache read'}
            </div>
          </Card>

          {/* 3. Cache Creation */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Cache Creation
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-chart-6">
              {u.total_cache_creation != null ? fmtTokens(u.total_cache_creation) : '—'}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground truncate">
              {u.total_cache_creation != null
                ? `on ${u.cache_creation_coverage ?? 0} turns`
                : 'not emitted'}
            </div>
          </Card>

          {/* 4. Total Output */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Total Output
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-chart-5">
              {u.total_output != null ? fmtTokens(u.total_output) : '—'}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground truncate">
              {u.total_reasoning ? `${fmtTokens(u.total_reasoning)} reasoning` : 'generated output'}
            </div>
          </Card>

          {/* 5. Cost & Basis */}
          <Card className="px-3.5 py-3" data-testid="metric-cost">
            <div className="flex items-center justify-between">
              <span className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
                Cost
              </span>
              {basis && basis !== 'none' && (
                <span
                  className={cn(
                    'rounded px-1.5 py-0.2 font-mono text-[9px] uppercase font-semibold',
                    isReported
                      ? 'bg-primary/15 text-primary'
                      : 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400'
                  )}
                  title={`Cost basis: ${basis}`}
                >
                  {isReported ? 'reported' : 'published'}
                </span>
              )}
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {cost.value != null
                ? usd(cost.value)
                : cost.credits != null
                  ? `${formatCredits(cost.credits)} cr`
                  : '—'}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground truncate">
              {cost.value != null && cost.credits != null
                ? `${formatCredits(cost.credits)} credits`
                : isReported
                  ? 'reported by harness'
                  : 'published rates'}
            </div>
          </Card>

          {/* 6. Cache Hit Ratio */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Cache Hit Ratio
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {u.cache_hit_ratio != null ? `${(u.cache_hit_ratio * 100).toFixed(1)}%` : '—'}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground">
              cache_read ÷ input
            </div>
          </Card>

          {/* 7. Spans */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Spans
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {fmtNum(session.span_count ?? u.span_count ?? 0)}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground">
              OTel recorded spans
            </div>
          </Card>

          {/* 8. Turns */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Turns
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {fmtNum(u.turn_count ?? session.turns?.length ?? 0)}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground">
              {u.reported_turn_count != null ? `reported: ${u.reported_turn_count}` : 'interaction turns'}
            </div>
          </Card>

          {/* 9. Requests */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Requests
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {fmtNum(u.request_count ?? (session.requests?.length || 1))}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground">
              invoke_agent roots
            </div>
          </Card>

          {/* 10. Wall Clock */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Duration
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {formatDuration(u.duration_ms)}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground">
              {u.median_ttft_ms != null ? `TTFT: ${Math.round(u.median_ttft_ms)}ms` : 'wall clock'}
            </div>
          </Card>

          {/* 11. Tool Calls */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Tool Calls
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {fmtNum(u.tool_calls ?? 0)}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground">
              {u.tools_invoked != null ? `${u.tools_invoked} distinct invoked` : 'invocations'}
            </div>
          </Card>

          {/* 12. Tools Offered */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Tools Offered
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {u.tools_offered != null ? fmtNum(u.tools_offered) : '—'}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground truncate">
              {u.tools_offered != null
                ? `${(u.tools_offered - (u.tools_invoked ?? 0))} never called`
                : `not exported by ${session.harness || 'adapter'}`}
            </div>
          </Card>
        </div>
      </div>

      {/* ---------------------------------------------------------------------
          Section 2: Spend & Context Composition Charts
      --------------------------------------------------------------------- */}
      <div className="space-y-2" data-testid="spend-composition-section">
        <SessionSpendCharts
          session={session}
          onSelectTurn={(turnIdx, bucket) => openDrawerForTurn(turnIdx, bucket)}
        />
      </div>

      {/* ---------------------------------------------------------------------
          Section 3: Tool & Schema Cost Table
      --------------------------------------------------------------------- */}
      <div className="space-y-3" data-testid="tool-schema-section">
        <div className="flex flex-col sm:flex-row sm:items-baseline justify-between gap-1">
          <div>
            <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">
              3 · Tool & Schema Cost Ranking
            </h2>
            <p className="text-xs text-muted-foreground mt-0.5">
              Ranked by resident cost (schema size tokens × turns resident). Red indicates tools offered but never called.
            </p>
          </div>
          {u.schema_waste_cost && u.schema_waste_cost.usd_low != null && (
            <span
              className="font-mono text-xs text-amber-600 dark:text-amber-400 font-semibold"
              data-testid="schema-waste-range"
            >
              Unused waste range: {usd(u.schema_waste_cost.usd_low)} – {usd(u.schema_waste_cost.usd_high)}
              {u.schema_waste_cost.credits_low != null
                ? ` (${formatCredits(u.schema_waste_cost.credits_low)} – ${formatCredits(u.schema_waste_cost.credits_high)} credits)`
                : ''}
            </span>
          )}
        </div>

        {/* Waste Callout Banner */}
        {u.tools_offered != null && (u.tools_offered - (u.tools_invoked ?? 0)) > 0 && (
          <div
            className="rounded-md border border-amber-500/30 bg-amber-500/10 p-3 text-xs text-foreground/90 space-y-1"
            data-testid="schema-waste-banner"
          >
            <div className="font-semibold text-amber-700 dark:text-amber-300">
              {u.tools_offered - (u.tools_invoked ?? 0)} of {u.tools_offered} tools were never called.
            </div>
            <p className="text-muted-foreground leading-relaxed">
              That represents{' '}
              <strong className="text-foreground">{fmtTokens(u.unused_schema_per_turn)} tokens/turn</strong>{' '}
              ({u.schema_tokens_per_turn ? ((u.unused_schema_per_turn || 0) / u.schema_tokens_per_turn * 100).toFixed(1) : '0'}% of the{' '}
              {fmtTokens(u.schema_tokens_per_turn)}-token schema block), resident across{' '}
              <strong className="text-foreground">{u.defs_turns ?? 0} turns</strong> — totaling{' '}
              <strong className="text-foreground">
                {fmtTokens((u.unused_schema_per_turn || 0) * (u.defs_turns || 0))} tokens
              </strong>{' '}
              in overhead for uncalled tools.
            </p>
          </div>
        )}

        {/* Non-measurable schema warning (e.g. Pi harness) */}
        {!isSchemaMeasurable && (
          <div
            className="rounded-md border border-border bg-card p-3 text-xs text-tertiary-foreground"
            data-testid="schemas-not-exported-banner"
          >
            <span className="font-medium text-foreground">
              {session.harness || 'Harness'} does not export tool definitions.
            </span>{' '}
            Ranking below lists invocations only.
          </div>
        )}

        {/* Tools Ranking */}
        <SchemaCostRanking
          schema={(session as { schema?: SchemaCostAnalysis }).schema}
          tools={toolRows}
          onSelectTool={openDrawerForTool}
        />
      </div>

      {/* ---------------------------------------------------------------------
          Section 4: Execution Timeline / Call Tree
      --------------------------------------------------------------------- */}
      <div className="space-y-3" data-testid="execution-timeline-section">
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2">
          <div>
            <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">
              4 · Execution Timeline & Call Tree
            </h2>
            <p className="text-xs text-muted-foreground mt-0.5">
              Hierarchical span execution tree. Click any span node or timeline bar to inspect its full attributes and content.
            </p>
          </div>

          {/* View Mode Toggle */}
          <div className="flex rounded border border-border bg-interactive-secondary p-0.5 text-xs self-start sm:self-auto">
            <button
              type="button"
              onClick={() => {
                setTimelineTab('tree')
                setTimelineTabSelected(true)
              }}
              className={cn(
                'px-2.5 py-1 rounded-[4px] font-medium transition-colors',
                activeTimelineTab === 'tree'
                  ? 'bg-card text-foreground shadow-sm'
                  : 'text-muted-foreground hover:text-foreground'
              )}
              data-testid="timeline-tab-tree"
            >
              Call Tree
            </button>
            <button
              type="button"
              onClick={() => {
                setTimelineTab('bars')
                setTimelineTabSelected(true)
              }}
              className={cn(
                'px-2.5 py-1 rounded-[4px] font-medium transition-colors',
                activeTimelineTab === 'bars'
                  ? 'bg-card text-foreground shadow-sm'
                  : 'text-muted-foreground hover:text-foreground'
              )}
              data-testid="timeline-tab-bars"
            >
              Duration Timeline
            </button>
          </div>
        </div>

        {/* Legend for Operations */}
        <div className="flex flex-wrap items-center gap-3 text-xs text-tertiary-foreground">
          {Object.entries(TIMELINE_OP_COLORS).map(([op, color]) => (
            <div key={op} className="flex items-center gap-1.5">
              <span className="h-2 w-2 rounded-full shrink-0" style={{ backgroundColor: color }} />
              <span className="font-mono text-[11px]">{op}</span>
            </div>
          ))}
        </div>

        {/* Call Tree View */}
        {activeTimelineTab === 'tree' ? (
          <div data-testid="timeline-tree-view">
            <TimelineView
              root={timelineRoot}
              selectedId={selectedSpanId}
              onSelectNode={(node) => openDrawerForSpan(node)}
            />
          </div>
        ) : (
          /* Duration Bars Timeline View */
          <Card className="p-4 space-y-2 border border-border" data-testid="timeline-bars-view">
            {flattenedTimeline.length === 0 ? (
              <p className="py-6 text-center text-xs text-tertiary-foreground italic">
                No timeline spans recorded.
              </p>
            ) : (
              <div className="space-y-1.5 overflow-x-auto max-h-[500px]">
                {flattenedTimeline.map(({ node, depth }, idx) => {
                  const offset = node.offsetMs ?? node.startMs ?? 0
                  const leftPct = (offset / maxTimelineSpan) * 100
                  const widthPct = Math.max(((node.durationMs ?? 0) / maxTimelineSpan) * 100, 0.7)
                  const opKey = node.op || node.kind || ''
                  const opColor = TIMELINE_OP_COLORS[opKey] || '#64748b'
                  const isSelected = selectedSpanId === node.spanId

                  return (
                    <div
                      key={node.spanId || idx}
                      onClick={() => openDrawerForSpan(node)}
                      className={cn(
                        'group flex items-center gap-2 rounded px-2 py-1 text-xs font-mono cursor-pointer transition-colors',
                        isSelected
                          ? 'bg-interactive-secondary ring-1 ring-primary/40'
                          : 'hover:bg-interactive-secondary/60'
                      )}
                      data-testid="timeline-bar-row"
                    >
                      {/* Name with depth indent */}
                      <span
                        className="truncate text-foreground font-medium shrink-0"
                        style={{ width: `${Math.max(240 - depth * 12, 100)}px`, paddingLeft: `${depth * 12}px` }}
                        title={node.name}
                      >
                        {node.name || 'unnamed'}
                      </span>

                      {/* Horizontal Duration Bar Track */}
                      <div className="relative flex-1 h-3 rounded bg-muted/60 overflow-hidden">
                        <div
                          className="absolute top-0 bottom-0 rounded-[2px] transition-all opacity-80 group-hover:opacity-100"
                          style={{
                            left: `${leftPct}%`,
                            width: `${widthPct}%`,
                            backgroundColor: opColor,
                          }}
                          title={`${node.name} · ${node.durationMs}ms`}
                        />
                      </div>

                      {/* Metadata */}
                      <span className="w-16 text-right tabular-nums text-tertiary-foreground text-[11px] shrink-0">
                        {formatDuration(node.durationMs)}
                      </span>
                      <span className="w-24 text-right tabular-nums text-tertiary-foreground text-[10px] truncate shrink-0">
                        {node.input != null ? `in: ${fmtTokens(node.input)}` : ''}
                      </span>
                    </div>
                  )
                })}
              </div>
            )}
          </Card>
        )}
      </div>

      {/* ---------------------------------------------------------------------
          Section 5: Session Cost & Token Accounting
      --------------------------------------------------------------------- */}
      <SessionCostPanel session={session} />

      {/* ---------------------------------------------------------------------
          Section 6: Slide-out Inspector Drawer
      --------------------------------------------------------------------- */}
      <SessionInspectorDrawer
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        title={drawerTitle}
        subtitle={drawerSubtitle}
        rawContent={drawerContent}
        contentRequest={
          (session.id ?? session.session_id) && selectedSpanId
            ? {
                sessionId: String(session.id ?? session.session_id),
                span: selectedSpanId,
              }
            : (session.id ?? session.session_id) && typeof drawerContent?.contentRequest === 'object'
            ? {
                sessionId: String(session.id ?? session.session_id),
                ...drawerContent.contentRequest,
              }
            : typeof drawerContent?.bucket === 'string' && (session.id ?? session.session_id)
              ? {
                  sessionId: String(session.id ?? session.session_id),
                  span:
                    typeof drawerContent.turn?.spanId === 'string' && drawerContent.turn.spanId
                      ? drawerContent.turn.spanId
                      : undefined,
                  part: drawerContent.bucket,
                }
              : (session.id ?? session.session_id) && typeof drawerContent?.spanId === 'string'
                ? {
                    sessionId: String(session.id ?? session.session_id),
                    span: drawerContent.spanId,
                  }
                : undefined
        }
      />
    </div>
  )
}

export function AgentSessionDashboard(props: AgentSessionDashboardProps) {
  if (props.error) {
    return (
      <Card
        className={cn('m-6 border-red-500/30 bg-red-500/10 p-6', props.className)}
        data-testid="agent-dashboard-error"
      >
        <div className="flex items-start gap-3">
          <span className="text-xl" role="img" aria-label="error">
            ⚠️
          </span>
          <div className="space-y-2">
            <h3 className="font-semibold text-red-600 dark:text-red-400">Failed to load session</h3>
            <p className="text-xs text-foreground/80">
              {props.error instanceof Error ? props.error.message : String(props.error)}
            </p>
          </div>
        </div>
      </Card>
    )
  }

  if (props.isLoading) {
    return (
      <div className={cn('flex flex-col gap-6 p-6', props.className)} data-testid="agent-dashboard-loading">
        <div className="flex items-center gap-3">
          <Skeleton className="h-7 w-28 rounded-full" />
          <Skeleton className="h-6 w-48 rounded" />
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-3">
          {Array.from({ length: 12 }).map((_, i) => (
            <Skeleton key={i} className="h-20 rounded-lg" />
          ))}
        </div>
        <Skeleton className="h-72 rounded-lg" />
        <Skeleton className="h-64 rounded-lg" />
      </div>
    )
  }

  if (props.session) {
    return (
      <AgentSessionContent
        session={props.session}
        onSelectSession={props.onSelectSession}
        initialTimelineTab={props.initialTimelineTab}
        className={props.className}
      />
    )
  }
  if (props.sessionId) {
    return (
      <AgentSessionLoader
        sessionId={props.sessionId}
        onSelectSession={props.onSelectSession}
        initialTimelineTab={props.initialTimelineTab}
        className={props.className}
      />
    )
  }
  return (
    <AgentSessionContent
      session={null}
      onSelectSession={props.onSelectSession}
      initialTimelineTab={props.initialTimelineTab}
      className={props.className}
    />
  )
}

export default AgentSessionDashboard
