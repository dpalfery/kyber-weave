import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'

import { type ContextProvider } from '../lib/api'
import { fetchKyberSessions, type KyberSessionSummary } from '../lib/kyberApi'
import { cn, usd } from '../lib/utils'
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
      return null
    default:
      return provider
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
          <AgentSessionDashboard
            sessionId={sessionId}
            onSelectSession={(selectedId) => onSelectSession(selectedId)}
          />
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

  const agentFilter = getAgentHarnessFilter(provider)

  // All providers are represented by canonical ASAD sessions and expand through
  // AgentSessionDashboard, regardless of their original transcript format.
  const {
    data: kyberData,
    isLoading: isKyberLoading,
    isError: isKyberError,
    error: kyberError,
  } = useQuery({
    queryKey: ['kyber-sessions', agentFilter],
    queryFn: () => fetchKyberSessions(agentFilter),
    staleTime: 30_000,
  })

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
        {isKyberLoading && (
          <div className="flex flex-col gap-2 p-4" data-testid="explorer-loading">
            {Array.from({ length: 6 }).map((_, i) => (
              <Skeleton key={i} className="h-9" />
            ))}
          </div>
        )}

        {isKyberError && (
          <p className="px-4 py-6 text-sm text-tertiary-foreground" data-testid="explorer-error">
            Failed to load sessions: {String((kyberError as Error)?.message)}
          </p>
        )}

        {!isKyberLoading && !isKyberError && kyberData && (
          <>
            {kyberData.length === 0 && (
              <p className="px-4 py-6 text-sm text-tertiary-foreground" data-testid="explorer-empty">
                No sessions found for this harness.
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
      </Card>
    </>
  )
}
