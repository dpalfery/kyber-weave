import { useQuery } from '@tanstack/react-query'
import { cn, usd, fmtTokens, fmtNum } from '../../lib/utils.js'
import { Card } from '../ui/card.js'
import { Skeleton } from '../ui/skeleton.js'
import { SectionError } from './SectionError.js'
import { formatDuration } from '../../lib/labels.js'
import {
  fetchKyberSessionSummary,
  kyberSessionSectionQueryKey,
} from '../../lib/kyberApi.js'
import type { SessionSummaryPayload } from './types.js'

/**
 * The session view's header strip, banners and overview tiles. Everything here
 * renders from the bounded summary alone: the loader reads it through the
 * client, and no other section's route is waited on. A figure the summary did
 * not carry renders as an honest absence — never as `0` or a default that
 * looks like data.
 */

export function formatCredits(c?: number | null): string {
  if (c == null || !isFinite(c)) return '—'
  return c >= 100 ? c.toFixed(0) : c.toFixed(2)
}

function finiteCost(value: number | null | undefined): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

export interface SessionSummarySectionProps {
  sessionId: string
  onSelectSession?: (sessionId: string) => void
  className?: string
}

export function SessionSummarySection({
  sessionId,
  onSelectSession,
  className,
}: SessionSummarySectionProps) {
  // This section owns the session's one summary query. It is the page's first
  // child, so its request is also the first to leave; the layout re-reads the
  // same key for the sections that need the harness or the cost figures, and
  // TanStack's cache dedupes that read into this single fetch.
  const { data: summary, isPending, isError, error, refetch } = useQuery({
    queryKey: kyberSessionSectionQueryKey(sessionId, 'summary'),
    queryFn: () => fetchKyberSessionSummary(sessionId),
  })

  if (isError) {
    return (
      <SectionError
        testId="session-summary-error"
        label="Session summary"
        message={error instanceof Error ? error.message : String(error)}
        onRetry={() => void refetch()}
        className={className}
      />
    )
  }

  if (isPending || summary === undefined) {
    return (
      <div
        data-testid="session-summary-loading"
        role="status"
        aria-label="Loading session summary"
        className={cn('space-y-4', className)}
      >
        <div className="flex items-center gap-3">
          <Skeleton className="h-7 w-28 rounded-full" />
          <Skeleton className="h-6 w-48 rounded" />
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 gap-3">
          {Array.from({ length: 12 }).map((_, i) => (
            <Skeleton key={i} className="h-20 rounded-lg" />
          ))}
        </div>
      </div>
    )
  }

  // The engine's own summary record, narrowed once at this seam: the tiles and
  // the cost logic below read the fields the record carries, and the section's
  // own counts cover what it does not.
  const u = (summary.summary ?? {}) as unknown as SessionSummaryPayload

  const reportedFigure = finiteCost(u.cost_reported?.value) ?? finiteCost(u.cost_reported?.usd)
  // A harness-reported figure, including a reported zero, is the cost the harness sent.
  // The published block stays beside it and is what the tile shows only when the harness sent none.
  const cost = reportedFigure != null
    ? { ...u.cost_reported, value: reportedFigure, basis: u.cost_reported?.basis || 'harness' }
    : (u.cost || {})
  const costFigure = finiteCost(cost.value) ?? finiteCost(cost.usd)
  const offeredList = Array.isArray(u.tools_offered) ? u.tools_offered : undefined
  const invokedList = Array.isArray(u.tools_invoked) ? u.tools_invoked : undefined
  const toolsOfferedCount = offeredList !== undefined
    ? offeredList.length
    : (typeof u.tools_offered === 'number' ? u.tools_offered : undefined)
  const toolsInvokedCount = invokedList !== undefined
    ? invokedList.length
    : (typeof u.tools_invoked === 'number' ? u.tools_invoked : undefined)
  const invokedSet = invokedList !== undefined ? new Set(invokedList) : null
  const unusedOfferedCount = toolsOfferedCount != null && toolsInvokedCount != null
    ? (offeredList !== undefined && invokedSet !== null
        ? offeredList.filter((name) => !invokedSet.has(name)).length
        : Math.max(0, toolsOfferedCount - toolsInvokedCount))
    : undefined
  const toolCallsMeasured = typeof u.tool_calls === 'number' && Number.isFinite(u.tool_calls)
  const toolCallsReason = `Tool invocation count was not reported by ${summary.harness || 'this adapter'}.`
  const basis = cost.basis || 'none'
  // The engine stamps a harness-reported cost with basis `harness`; `harness_reported` is the
  // legacy spelling, still accepted (SessionCostPanel reads both).
  const isReported = basis === 'harness' || basis === 'harness_reported'
  const reconciliation = summary.reconciliation

  return (
    <div className={cn('space-y-2', className)}>
      {/* Header Strip & Session Meta */}
      <div className="flex flex-col gap-2 pb-2 border-b border-border/80">
        <div className="flex flex-wrap items-center gap-2.5" data-testid="session-summary-header">
          {summary.harness && (
            <span
              className="rounded bg-primary/10 border border-primary/20 px-2.5 py-0.5 font-mono text-xs font-semibold text-primary uppercase tracking-wide"
              data-testid="session-harness-badge"
            >
              {summary.harness}
            </span>
          )}
          <h1 className="text-base sm:text-lg font-semibold text-heading truncate" data-testid="session-label">
            {summary.label || summary.session_id || 'Session'}
          </h1>
          {summary.agent_name && (
            <span className="rounded bg-interactive-secondary border border-border px-2 py-0.5 text-xs text-muted-foreground">
              agent: <span className="font-mono text-foreground">{summary.agent_name}</span>
            </span>
          )}
          {summary.repo && (
            <span className="text-xs text-tertiary-foreground font-mono">
              {summary.branch ? `${summary.branch} · ` : ''}
              {summary.repo.split('/').pop()}
            </span>
          )}
          <span className="ml-auto text-xs text-tertiary-foreground font-mono tabular-nums">
            {fmtNum(summary.span_count)} spans
          </span>
        </div>

        {/* Alerts & Banners: Subagent, Reconciliation, Notes */}
        <div className="flex flex-col gap-2.5 mt-2">
          {/* Subagent Notice */}
          {summary.is_subagent && (
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
                  {summary.agent_name || '?'}
                </span>
                <span>spawned by a <code className="font-mono">runSubagent</code> tool call</span>
                {summary.parent_session && (
                  <>
                    <span>in parent session</span>
                    <button
                      type="button"
                      onClick={() => onSelectSession?.(summary.parent_session!)}
                      className="font-mono font-semibold text-primary underline hover:text-primary/80 transition-colors"
                      data-testid="parent-session-link"
                    >
                      {summary.parent_session.slice(0, 10)}…
                    </button>
                  </>
                )}
                <span className="text-tertiary-foreground">
                  (tokens are counted here and not folded into parent totals).
                </span>
              </div>
            </div>
          )}

          {/* Reconciliation Status Badge, from the counts the summary carries */}
          {reconciliation.total > 0 && (
            <div>
              {reconciliation.mismatched === 0 ? (
                <div
                  className="rounded-md border border-emerald-500/30 bg-emerald-500/10 px-3.5 py-2 text-xs text-emerald-700 dark:text-emerald-300 flex items-center gap-2"
                  data-testid="reconciliation-ok"
                >
                  <span className="font-semibold shrink-0">✓ Reconciliation OK</span>
                  <span className="text-foreground/80">
                    across {reconciliation.total} request{reconciliation.total > 1 ? 's' : ''} — per-turn sums equal each{' '}
                    <code className="font-mono text-foreground">invoke_agent</code> total exactly.
                  </span>
                </div>
              ) : (
                <div
                  className="rounded-md border border-amber-500/30 bg-amber-500/10 px-3.5 py-2.5 text-xs text-amber-800 dark:text-amber-200"
                  data-testid="reconciliation-mismatch"
                >
                  <div className="font-semibold flex items-center gap-1.5">
                    <span>⚠️ Reconciliation MISMATCH</span>
                    <span className="font-normal text-foreground/80">
                      on {reconciliation.mismatched} of {reconciliation.total} request(s):
                    </span>
                  </div>
                  <p className="text-foreground/90 mt-1">
                    The rows below name each request whose chat-turn sums disagree with its{' '}
                    <code className="font-mono text-foreground">invoke_agent</code> total.
                  </p>
                </div>
              )}
            </div>
          )}

          {/* Harness Caveats / Notes Banner */}
          {summary.notes.length > 0 && (
            <div
              className="rounded-md border border-border bg-card/60 px-3.5 py-2.5 text-xs text-foreground/90 space-y-1"
              data-testid="harness-notes"
            >
              <div className="font-semibold text-muted-foreground flex items-center gap-1.5">
                <span>ℹ️ What {summary.harness || 'this harness'} does not export:</span>
              </div>
              <ul className="list-disc list-inside space-y-0.5 text-muted-foreground pl-1">
                {summary.notes.map((note, idx) => (
                  <li key={idx}>{note}</li>
                ))}
              </ul>
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

      {/* Overview Strip (Metric Cards) */}
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
                ? (u.cache_creation_coverage != null
                    // A legacy payload without the count must not print "on 0
                    // turns" — absence is not a measured zero.
                    ? `on ${u.cache_creation_coverage} turns`
                    : 'turn coverage unreported')
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
              {costFigure != null
                ? usd(costFigure)
                : cost.credits != null
                  ? `${formatCredits(cost.credits)} cr`
                  : '—'}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground truncate">
              {costFigure != null && cost.credits != null
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
              {fmtNum(summary.span_count)}
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
              {u.turn_count != null ? fmtNum(u.turn_count) : '—'}
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
              {fmtNum(summary.request_count)}
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
          <Card
            className="px-3.5 py-3"
            data-testid="metric-tool-calls"
            data-measured={String(toolCallsMeasured)}
            title={toolCallsMeasured ? undefined : toolCallsReason}
          >
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Tool Calls
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {fmtNum(u.tool_calls)}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground">
              {toolCallsMeasured
                ? u.tools_invoked != null ? `${toolsInvokedCount} distinct invoked` : 'invocations'
                : toolCallsReason}
            </div>
          </Card>

          {/* 12. Tools Offered */}
          <Card className="px-3.5 py-3">
            <div className="text-[11px] uppercase tracking-wider text-tertiary-foreground">
              Tools Offered
            </div>
            <div className="mt-1 text-xl font-semibold tabular-nums tracking-tight text-foreground">
              {toolsOfferedCount != null ? fmtNum(toolsOfferedCount) : '—'}
            </div>
            <div className="mt-0.5 text-[11px] text-tertiary-foreground truncate">
              {toolsOfferedCount != null
                ? (toolsInvokedCount != null && unusedOfferedCount != null
                    ? `${unusedOfferedCount} never called`
                    : 'invocations not reported')
                : `not exported by ${summary.harness || 'adapter'}`}
            </div>
          </Card>
        </div>
      </div>
    </div>
  )
}
