import * as React from 'react'
import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'

import { fetchKyberSessions, type KyberSessionSummary } from '../lib/kyberApi.js'
import { formatSessionId, formatSessionTitle } from '../lib/labels.js'
import { cn, usd } from '../lib/utils.js'
import { Card } from './ui/card.js'
import { Skeleton } from './ui/skeleton.js'
import { AgentSessionDashboard } from './AgentSessionDashboard.js'
import { formatCostFigure, normalizeCostBlock } from './SessionCostPanel.js'

// Canonical harness IDs are open — the store may report any ID — so this type
// constrains nothing on purpose, and the All sentinel is the one fixed value.
export type ExplorerProvider = string

// The shell's selector speaks `all`; `agent-all` is the spelling that predates
// it, kept so a value handed in from an older caller still reads as unscoped
// rather than as a harness canonically named "agent-all".
export const ALL_PROVIDER = 'agent-all' as const

/** The scope that means every harness, as the shell's selector spells it. */
export const ALL_HARNESS = 'all' as const

// Labels never determine membership or merge canonical harness identities: they
// only name a harness in prose, so anything unrecognised keeps its own id.
const HARNESS_LABELS: Record<string, string> = {
  'claude-code': 'Claude Code',
  'claude-desktop': 'Claude Desktop',
  'claude-cli': 'Claude CLI',
  'codex-desktop': 'Codex Desktop',
  'codex-cli': 'Codex CLI',
  'antigravity-cli': 'Antigravity CLI',
  'antigravity-ide': 'Antigravity IDE',
  'copilot-cli': 'GitHub Copilot CLI',
  'copilot-vscode': 'Copilot (VS Code)',
  'copilot-agent': 'Copilot Agent',
  'cursor-agent': 'Cursor Agent',
  zcode: 'ZCode',
  pi: 'Pi',
  opencode: 'OpenCode',
}

/** How a harness reads in prose; an id this build does not know is its own name. */
export function harnessLabel(harness: string): string {
  return Object.hasOwn(HARNESS_LABELS, harness) ? HARNESS_LABELS[harness]! : harness
}

/**
 * The harness this view is scoped to, or `all` for the whole inventory.
 *
 * An id this build has never seen is returned verbatim rather than widened to
 * all: the scope comes from the store's own harness identities, so resolving an
 * unfamiliar one to "all" would discard the narrowing the user just asked for
 * and hand them another harness's sessions under the name they picked.
 */
export function scopeOf(activeHarness?: string): ExplorerProvider {
  if (!activeHarness || activeHarness === ALL_PROVIDER) return ALL_HARNESS
  return activeHarness
}

export function ago(mtimeMs: number): string {
  const mins = Math.max(0, Math.round((Date.now() - mtimeMs) / 60_000))
  if (mins < 60) return `${mins}m ago`
  if (mins < 60 * 24) return `${Math.round(mins / 60)}h ago`
  return `${Math.round(mins / (60 * 24))}d ago`
}

export function formatSessionTime(started?: string | number | null): string {
  if (!started) return '—'
  const ms = typeof started === 'number' ? started : Date.parse(started)
  if (isNaN(ms)) return String(started)
  return ago(ms)
}

export function AgentSessionRow({
  s,
  open,
  onToggle,
  onSelectSession,
  subagentsExpanded = false,
  onToggleSubagents,
  isChild = false,
}: {
  s: KyberSessionSummary
  open: boolean
  onToggle: () => void
  onSelectSession: (id: string) => void
  subagentsExpanded?: boolean
  onToggleSubagents?: (e?: React.MouseEvent) => void
  isChild?: boolean
}) {
  const sessionId = s.session_id || s.sessionId || ''
  const isSubagent = Boolean(s.is_subagent || s.isSubagent || isChild)
  const parentSession = s.parent_session || s.parentSession || null
  const turnCount = (!isChild && s.rollup?.turn_count != null) ? s.rollup.turn_count : (s.turn_count ?? s.turnCount ?? null)
  const effectiveCost = (!isChild && s.rollup?.cost) ? s.rollup.cost : s.cost
  const effectiveCostUsd = (!isChild && s.rollup?.cost_usd != null) ? s.rollup.cost_usd : (s.cost_usd ?? s.costUsd ?? null)
  const hasSubagents = Boolean(s.subagents && s.subagents.length > 0)

  return (
    <div className={cn('border-t border-border first:border-t-0', open && 'bg-interactive-secondary/30')}>
      <button
        type="button"
        onClick={onToggle}
        className={cn(
          'flex w-full items-center gap-3 px-4 py-2.5 text-left transition-colors hover:bg-interactive-secondary/50',
          isChild && 'pl-8'
        )}
        data-testid={`agent-session-row-${sessionId}`}
      >
        <svg
          viewBox="0 0 16 16"
          width="10"
          height="10"
          className={cn('shrink-0 text-tertiary-foreground transition-transform', open && 'rotate-90')}
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <path d="M6 3l5 5-5 5" />
        </svg>

        {hasSubagents && (
          <button
            type="button"
            data-testid={`subagent-toggle-${sessionId}`}
            aria-label={`Toggle subagents for ${sessionId}`}
            onClick={(e) => {
              e.stopPropagation()
              onToggleSubagents?.(e)
            }}
            className="p-1 -ml-1 text-tertiary-foreground hover:text-foreground transition-colors shrink-0"
          >
            <svg
              viewBox="0 0 16 16"
              width="10"
              height="10"
              className={cn('transition-transform', subagentsExpanded && 'rotate-90')}
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              <path d="M6 3l5 5-5 5" />
            </svg>
          </button>
        )}

        <span className="shrink-0 font-mono text-xs text-primary" title={sessionId}>
          {formatSessionId(sessionId)}
        </span>

        <span className="min-w-0 flex-1 truncate text-[13px] text-foreground flex items-center gap-2">
          <span title={sessionId}>
            {formatSessionTitle({
              label: s.label,
              sessionId,
              agentName: s.agent_name ?? s.agentName,
              taskSummary: s.task_summary ?? s.taskSummary,
              harness: s.harness,
            }) || <span className="text-tertiary-foreground">untitled session</span>}
          </span>
          {isSubagent && (
            <span
              data-testid="subagent-indicator"
              className="rounded bg-interactive-secondary px-1.5 py-0.5 text-[10px] text-tertiary-foreground font-mono"
            >
              subagent
            </span>
          )}
        </span>

        {parentSession && (
          <button
            type="button"
            data-testid="parent-session-link"
            onClick={(e) => {
              e.stopPropagation()
              onSelectSession(parentSession)
            }}
            className="shrink-0 text-xs text-primary hover:underline font-mono"
            title={`Parent session: ${parentSession}`}
          >
            parent: {formatSessionId(parentSession)}
          </button>
        )}

        {/* Harness badge */}
        <span
          className={cn(
            'shrink-0 rounded px-1.5 py-0.5 text-[11px] font-medium border capitalize',
            s.harness === 'copilot'
              ? 'bg-blue-500/10 text-blue-500 border-blue-500/20'
              : s.harness === 'gemini'
                ? 'bg-purple-500/10 text-purple-500 border-purple-500/20'
                : s.harness === 'pi'
                  ? 'bg-amber-500/10 text-amber-500 border-amber-500/20'
                  : 'bg-interactive-secondary text-foreground border-border'
          )}
          data-testid="agent-harness-badge"
        >
          {s.harness}
        </span>

        {/* Turn count */}
        <span className="w-16 shrink-0 text-right text-xs tabular-nums text-tertiary-foreground">
          {turnCount != null ? `${turnCount} turns` : '—'}
        </span>

        {/* Cost: word statuses from formatCostFigure need ~97px at text-xs; w-20 (80px) wraps. */}
        <span
          data-testid="agent-session-cost"
          title={effectiveCost ? `Cost basis: ${effectiveCost.basis}` : undefined}
          className="w-28 shrink-0 whitespace-nowrap text-right text-xs tabular-nums text-foreground font-medium"
        >
          {effectiveCost ? formatCostFigure(normalizeCostBlock(effectiveCost)) : effectiveCostUsd != null ? usd(effectiveCostUsd) : '—'}
        </span>

        {/* Timestamp */}
        <span className="w-16 shrink-0 text-right text-xs tabular-nums text-tertiary-foreground">
          {formatSessionTime(s.started)}
        </span>
      </button>

      {open && (
        <div className="border-t border-border">
          <AgentSessionDashboard
            sessionId={sessionId}
            onSelectSession={(selectedId) => onSelectSession(selectedId)}
          />
        </div>
      )}
    </div>
  )
}

export interface ContextExplorerProps {
  /**
   * The harness scope held by the shell's one selector. Omitted, `all` or
   * `agent-all` means every harness; any other value scopes to exactly that
   * canonical id.
   */
  activeHarness?: string
  /**
   * Reports the session the user opened here so the shell can carry it to
   * Context Doctor, and `undefined` when they close it again — a session the
   * user has just closed must not stay selected on the other side.
   */
  onSelectSession?: (sessionId: string | undefined) => void
  /** Optional initial session list used by testing or custom embeddings. */
  sessions?: KyberSessionSummary[]
}

/**
 * The session list behind the shell's harness scope.
 *
 * There is no selector here: the shell owns one harness scope for Context
 * Doctor and this list, and a second strip over the same store would put the
 * same choice on screen twice, each able to disagree with the other.
 */
export function ContextExplorer({ activeHarness, onSelectSession, sessions: initialSessions }: ContextExplorerProps = {}) {
  const scope = scopeOf(activeHarness)
  // Expansion is remembered against the scope that opened it, and dropped when
  // the shell moves to another: a session chosen under one harness must never
  // still read as open — or still reportable — under the next. Resetting here
  // rather than only on the way out means returning to a harness starts clean,
  // instead of resurrecting a session the user closed by leaving.
  const [renderedScope, setRenderedScope] = useState<ExplorerProvider>(scope)
  const [expanded, setExpanded] = useState<{ scope: ExplorerProvider; id: string | null }>({
    scope,
    id: null,
  })
  const [expandedSubagents, setExpandedSubagents] = useState<Record<string, boolean>>({})

  if (scope !== renderedScope) {
    setRenderedScope(scope)
    setExpanded({ scope, id: null })
  }
  const openId = expanded.scope === scope ? expanded.id : null
  const selectSession = (id: string | null) => {
    setExpanded({ scope, id })
    onSelectSession?.(id ?? undefined)
  }

  const toggleSubagents = (id: string) => {
    setExpandedSubagents((prev) => ({
      ...prev,
      [id]: !prev[id],
    }))
  }

  // One unfiltered read backs every scope, so a harness this machine has never
  // collected simply narrows to nothing instead of minting a request filter.
  const {
    data: kyberData,
    isLoading: isKyberLoading,
    isError: isKyberError,
    error: kyberError,
  } = useQuery({
    queryKey: ['kyber-sessions'],
    queryFn: () => fetchKyberSessions(),
    staleTime: 30_000,
  })

  const sourceData = initialSessions ?? kyberData
  const sessions =
    scope === ALL_HARNESS ? sourceData : sourceData?.filter((s) => s.harness === scope)

  const isLoading = initialSessions ? false : isKyberLoading
  const isError = initialSessions ? false : isKyberError

  return (
    <Card className="overflow-hidden">
      {isLoading && (
        <div className="flex flex-col gap-2 p-4" data-testid="explorer-loading">
          {Array.from({ length: 6 }).map((_, i) => (
            <Skeleton key={i} className="h-9" />
          ))}
        </div>
      )}

      {isError && (
        <p className="px-4 py-6 text-sm text-tertiary-foreground" data-testid="explorer-error">
          Failed to load sessions: {String((kyberError as Error)?.message)}
        </p>
      )}

      {!isLoading && !isError && sourceData && (
        <>
          {sessions?.length === 0 && (
            <p className="px-4 py-6 text-sm text-tertiary-foreground" data-testid="explorer-empty">
              {/* The empty state names the scope it is empty for. Answering with
                  "all" here would reply to a question about one harness with
                  another's inventory. */}
              {scope === ALL_HARNESS ? 'No sessions found.' : `No sessions for ${harnessLabel(scope)}.`}
            </p>
          )}
          {sessions?.map((s) => {
            const sid = s.session_id || s.sessionId || ''
            const hasSubagents = Boolean(s.subagents && s.subagents.length > 0)
            const isSubagentsExpanded = Boolean(expandedSubagents[sid])
            return (
              <React.Fragment key={sid}>
                <AgentSessionRow
                  s={s}
                  open={openId === sid}
                  onToggle={() => selectSession(openId === sid ? null : sid)}
                  onSelectSession={(id) => selectSession(id)}
                  subagentsExpanded={isSubagentsExpanded}
                  onToggleSubagents={() => toggleSubagents(sid)}
                />
                {hasSubagents && isSubagentsExpanded && (
                  <div data-testid="subagent-children" className="pl-6 border-l border-border/50">
                    {s.subagents!.map((child) => {
                      const childSid = child.session_id || child.sessionId || ''
                      return (
                        <AgentSessionRow
                          key={childSid}
                          s={child}
                          open={openId === childSid}
                          onToggle={() => selectSession(openId === childSid ? null : childSid)}
                          onSelectSession={(id) => selectSession(id)}
                          isChild
                        />
                      )
                    })}
                  </div>
                )}
              </React.Fragment>
            )
          })}
        </>
      )}
    </Card>
  )
}
