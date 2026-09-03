import { useState, Component, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'

import {
  fetchContextSessions,
  fetchContextTree,
  type ContextProvider,
  type ContextRow,
  type ContextSessionInfo,
} from '../lib/api'
import { fetchKyberSessions, type KyberSessionSummary } from '../lib/kyberApi'
import { cn, fmtNum, fmtTokens, label, usd } from '../lib/utils'
import { Card } from './ui/card'
import { Skeleton } from './ui/skeleton'
import { AgentSessionDashboard } from './AgentSessionDashboard'

export type ExplorerProvider = ContextProvider

export const PROVIDERS: Array<{ key: ExplorerProvider; label: string }> = [
  { key: 'agent-all', label: 'Agent Sessions (All)' },
  { key: 'claude', label: 'Claude Code' },
  { key: 'codex', label: 'Codex' },
  { key: 'antigravity', label: 'Antigravity (AGY)' },
  { key: 'copilot-cli', label: 'GitHub Copilot CLI' },
  { key: 'copilot-vscode', label: 'Copilot (VS Code)' },
  { key: 'copilot-agent', label: 'Copilot Agent' },
  { key: 'pi', label: 'Pi' },
  { key: 'opencode', label: 'OpenCode' },
  { key: 'kilo-code', label: 'KiloCode' },
  { key: 'cursor', label: 'Cursor' },
]

export function isAgentHarness(provider: string): boolean {
  return (
    provider === 'agent-all' ||
    provider === 'copilot-agent' ||
    provider === 'copilot-vscode' ||
    provider === 'copilot' ||
    provider === 'pi' ||
    provider === 'antigravity' ||
    provider === 'gemini'
  )
}

export function getAgentHarnessFilter(provider: string): string | null {
  switch (provider) {
    case 'copilot-agent':
    case 'copilot-vscode':
    case 'copilot':
      return 'copilot'
    case 'pi':
      return 'pi'
    case 'antigravity':
    case 'gemini':
      return 'gemini'
    case 'agent-all':
    default:
      return null
  }
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

export class SessionDetailsBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null }
  static getDerivedStateFromError(error: Error) {
    return { error }
  }
  render() {
    if (this.state.error) {
      return (
        <div className="p-4 text-xs text-destructive" data-testid="session-details-error">
          Failed to render session context: {this.state.error.message}
        </div>
      )
    }
    return this.props.children
  }
}

export function TreeTable({ rows }: { rows: ContextRow[] }) {
  const validRows = rows ?? []
  if (validRows.length === 0) {
    return <p className="py-2 text-xs text-tertiary-foreground">No context block breakdown available for this session.</p>
  }
  const nonBold = validRows.filter((r) => !r.bold)
  const max = Math.max(1, ...(nonBold.length > 0 ? nonBold : validRows).map((r) => r.tokens || 0))
  return (
    <div className="flex flex-col" data-testid="tree-table">
      {validRows.map((r, i) => (
        <div key={i} className={cn('relative flex items-center gap-3 rounded-sm px-2 py-[3px]', r.bold && i > 0 && 'mt-2')}>
          {!r.bold && (
            <span
              className="absolute inset-y-[3px] left-0 rounded-sm bg-primary/[0.07]"
              style={{ width: `${Math.max(1, ((r.tokens || 0) / max) * 100)}%` }}
            />
          )}
          <span
            className={cn('relative min-w-0 flex-1 truncate text-[13px]', r.bold ? 'font-semibold text-foreground' : 'text-muted-foreground')}
            style={{ paddingLeft: (r.depth || 0) * 16 }}
          >
            {r.label}
          </span>
          <span className="relative w-16 shrink-0 text-right text-xs tabular-nums text-tertiary-foreground">{fmtNum(r.count || 0)}x</span>
          <span className={cn('relative w-20 shrink-0 text-right text-[13px] tabular-nums', r.bold ? 'font-semibold text-foreground' : 'text-foreground')}>
            {fmtTokens(r.tokens || 0)}
          </span>
        </div>
      ))}
    </div>
  )
}

export function Chip({ label: lbl, value }: { label: string; value: string }) {
  return (
    <div className="rounded-md border border-border bg-interactive-secondary px-3 py-2">
      <div className="text-[10px] font-semibold uppercase tracking-[0.1em] text-tertiary-foreground">{lbl}</div>
      <div className="mt-0.5 text-sm font-medium tabular-nums text-foreground">{value}</div>
    </div>
  )
}

export function SessionDetails({ provider, id }: { provider: ContextProvider; id: string }) {
  const [scope, setScope] = useState<'effective' | 'full'>('effective')
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['context-tree', provider, id],
    queryFn: () => fetchContextTree(provider, id),
    staleTime: 60_000,
  })

  if (isLoading) {
    return (
      <div className="flex flex-col gap-2 px-4 py-4" data-testid="cli-details-loading">
        <Skeleton className="h-14" />
        <Skeleton className="h-40" />
        <p className="text-xs text-tertiary-foreground">Reading the whole transcript, large sessions take a few seconds…</p>
      </div>
    )
  }
  if (isError || !data) {
    return <p className="px-4 py-4 text-sm text-tertiary-foreground" data-testid="cli-details-error">Failed to load: {String((error as Error)?.message ?? 'unknown')}</p>
  }

  const view = (scope === 'full' ? data.full : data.effective) ?? {
    messages: 0,
    tokens: 0,
  }
  const rows = (scope === 'full' ? data.fullRows : data.effectiveRows) ?? []
  const window = data.reported?.window ?? null
  const pct = data.reported && window ? Math.min(100, Math.round((data.reported.context / window) * 100)) : null

  const messagesCount = view?.messages ?? rows.reduce((acc, r) => acc + (r.count || 0), 0)
  const tokensCount = view?.tokens ?? rows.reduce((acc, r) => acc + (r.tokens || 0), 0)

  return (
    <div className="flex flex-col gap-3 px-4 py-4" data-testid="cli-session-details">
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <Chip label="Messages" value={fmtNum(messagesCount)} />
        <Chip label="Est. tokens" value={fmtTokens(tokensCount)} />
        <Chip
          label="Context (exact)"
          value={data.reported ? (window ? `${fmtTokens(data.reported.context)} / ${fmtTokens(window)}` : fmtTokens(data.reported.context)) : '—'}
        />
        <Chip label="Compactions" value={fmtNum(data.compactions ?? 0)} />
      </div>

      {pct !== null && (
        <div>
          <div className="mb-1 flex justify-between text-[11px] text-tertiary-foreground">
            <span>{label(data.model)} · live context window</span>
            <span className="tabular-nums">{pct}%</span>
          </div>
          <div className="h-1.5 overflow-hidden rounded-full bg-interactive-secondary">
            <div className={cn('h-full rounded-full', pct >= 80 ? 'bg-chart-5' : 'bg-primary')} style={{ width: `${pct}%` }} />
          </div>
        </div>
      )}

      <div className="flex items-center justify-between">
        <div className="flex rounded-md border border-border bg-interactive-secondary p-0.5">
          {(['effective', 'full'] as const).map((s) => (
            <button
              key={s}
              type="button"
              onClick={() => setScope(s)}
              className={cn(
                'rounded-[5px] px-2.5 py-1 text-[11px] font-medium transition-colors',
                scope === s ? 'bg-active-primary text-foreground shadow-sm' : 'text-tertiary-foreground hover:text-foreground',
              )}
            >
              {s === 'effective' ? 'Live window' : 'Full history'}
            </button>
          ))}
        </div>
        <span className="text-[11px] text-tertiary-foreground">token counts are estimates; “Context (exact)” comes from API usage</span>
      </div>

      <TreeTable rows={rows} />
    </div>
  )
}

export function SessionRow({ s, open, onToggle }: { s: ContextSessionInfo; open: boolean; onToggle: () => void }) {
  return (
    <div className={cn('border-t border-border first:border-t-0', open && 'bg-interactive-secondary/30')}>
      <button
        type="button"
        onClick={onToggle}
        className="flex w-full items-center gap-3 px-4 py-2.5 text-left transition-colors hover:bg-interactive-secondary/50"
        data-testid={`cli-session-row-${s.sessionId}`}
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
        <span className="shrink-0 font-mono text-xs text-primary">{s.sessionId.slice(0, 8)}</span>
        <span className="min-w-0 flex-1 truncate text-[13px] text-foreground">
          {s.title || <span className="text-tertiary-foreground">untitled session</span>}
        </span>
        <span className="hidden shrink-0 text-xs text-tertiary-foreground sm:block">{s.project}</span>
        <span className="w-16 shrink-0 text-right text-xs tabular-nums text-tertiary-foreground">{ago(s.mtimeMs)}</span>
        <span className="w-16 shrink-0 text-right text-xs tabular-nums text-tertiary-foreground">{(s.sizeBytes / 1024 / 1024).toFixed(1)}MB</span>
      </button>
      {open && (
        <div className="border-t border-border">
          <SessionDetailsBoundary>
            <SessionDetails provider={s.provider} id={s.sessionId} />
          </SessionDetailsBoundary>
        </div>
      )}
    </div>
  )
}

export function AgentSessionRow({
  s,
  open,
  onToggle,
  onSelectSession,
}: {
  s: KyberSessionSummary
  open: boolean
  onToggle: () => void
  onSelectSession: (id: string) => void
}) {
  const sessionId = s.session_id || s.sessionId || ''
  const isSubagent = Boolean(s.is_subagent || s.isSubagent)
  const parentSession = s.parent_session || s.parentSession || null
  const turnCount = s.turn_count ?? s.turnCount ?? null
  const costUsd = s.cost_usd ?? s.costUsd ?? null

  return (
    <div className={cn('border-t border-border first:border-t-0', open && 'bg-interactive-secondary/30')}>
      <button
        type="button"
        onClick={onToggle}
        className="flex w-full items-center gap-3 px-4 py-2.5 text-left transition-colors hover:bg-interactive-secondary/50"
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

        <span className="shrink-0 font-mono text-xs text-primary">{sessionId.slice(0, 8)}</span>

        <span className="min-w-0 flex-1 truncate text-[13px] text-foreground flex items-center gap-2">
          <span>{s.label || <span className="text-tertiary-foreground">untitled session</span>}</span>
          {isSubagent && (
            <span className="rounded bg-interactive-secondary px-1.5 py-0.5 text-[10px] text-tertiary-foreground font-mono">
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
            parent: {parentSession.slice(0, 8)}
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

        {/* Cost in USD */}
        <span className="w-20 shrink-0 text-right text-xs tabular-nums text-foreground font-medium">
          {costUsd != null ? usd(costUsd) : '—'}
        </span>

        {/* Timestamp */}
        <span className="w-16 shrink-0 text-right text-xs tabular-nums text-tertiary-foreground">
          {formatSessionTime(s.started)}
        </span>
      </button>

      {open && (
        <div className="border-t border-border">
          <SessionDetailsBoundary>
            <AgentSessionDashboard
              sessionId={sessionId}
              onSelectSession={(selectedId) => onSelectSession(selectedId)}
            />
          </SessionDetailsBoundary>
        </div>
      )}
    </div>
  )
}

export function ContextExplorer({
  activeHarness,
  onHarnessChange,
}: {
  activeHarness?: string
  onHarnessChange?: (h: ContextProvider) => void
} = {}) {
  const [localProvider, setLocalProvider] = useState<ContextProvider>('claude')
  const provider = activeHarness && activeHarness !== 'all' ? (activeHarness as ContextProvider) : localProvider
  const setProvider = (p: ContextProvider) => {
    setLocalProvider(p)
    onHarnessChange?.(p)
  }
  const [openId, setOpenId] = useState<string | null>(null)

  const isAgent = isAgentHarness(provider)
  const agentFilter = getAgentHarnessFilter(provider)

  // Query for CLI sessions (Claude, Codex, etc.)
  const {
    data: cliData,
    isLoading: isCliLoading,
    isError: isCliError,
    error: cliError,
  } = useQuery({
    queryKey: ['context-sessions', provider],
    queryFn: () => fetchContextSessions(provider),
    enabled: !isAgent,
    staleTime: 30_000,
  })

  // Query for Agent sessions (Copilot, Pi, Gemini, All)
  const {
    data: kyberData,
    isLoading: isKyberLoading,
    isError: isKyberError,
    error: kyberError,
  } = useQuery({
    queryKey: ['kyber-sessions', agentFilter],
    queryFn: () => fetchKyberSessions(agentFilter),
    enabled: isAgent,
    staleTime: 30_000,
  })

  const isLoading = isAgent ? isKyberLoading : isCliLoading
  const isError = isAgent ? isKyberError : isCliError
  const error = isAgent ? kyberError : cliError

  return (
    <>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        {PROVIDERS.map((p) => (
          <button
            key={p.key}
            type="button"
            onClick={() => {
              setProvider(p.key)
              setOpenId(null)
            }}
            className={cn(
              'rounded-md border px-3 py-1.5 text-xs font-medium transition-all',
              provider === p.key
                ? 'border-primary/50 bg-primary/15 text-primary font-semibold shadow-xs'
                : 'border-border bg-card text-tertiary-foreground hover:bg-interactive-secondary hover:text-foreground',
            )}
            data-testid={`provider-tab-${p.key}`}
          >
            {p.label}
          </button>
        ))}
        <span className="ml-auto text-xs text-tertiary-foreground max-md:hidden">
          what fills each session’s context window, block by block
        </span>
      </div>

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
            Failed to load sessions: {String((error as Error)?.message)}
          </p>
        )}

        {!isLoading && !isError && isAgent && kyberData && (
          <>
            {kyberData.length === 0 && (
              <p className="px-4 py-6 text-sm text-tertiary-foreground" data-testid="explorer-empty">
                No agent sessions found for this harness.
              </p>
            )}
            {kyberData.map((s) => {
              const sid = s.session_id || s.sessionId || ''
              return (
                <AgentSessionRow
                  key={sid}
                  s={s}
                  open={openId === sid}
                  onToggle={() => setOpenId(openId === sid ? null : sid)}
                  onSelectSession={(id) => setOpenId(id)}
                />
              )
            })}
          </>
        )}

        {!isLoading && !isError && !isAgent && cliData && (
          <>
            {cliData.length === 0 && (
              <p className="px-4 py-6 text-sm text-tertiary-foreground" data-testid="explorer-empty">
                No sessions found for this provider.
              </p>
            )}
            {cliData.map((s) => (
              <SessionRow
                key={s.sessionId}
                s={s}
                open={openId === s.sessionId}
                onToggle={() => setOpenId(openId === s.sessionId ? null : s.sessionId)}
              />
            ))}
          </>
        )}
      </Card>
    </>
  )
}
