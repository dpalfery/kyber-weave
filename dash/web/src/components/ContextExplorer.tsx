import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'

import { fetchKyberSessions, type KyberSessionSummary } from '../lib/kyberApi.js'
import { cn, usd } from '../lib/utils.js'
import { Card } from './ui/card.js'
import { Skeleton } from './ui/skeleton.js'
import { AgentSessionDashboard } from './AgentSessionDashboard.js'
import { formatCostFigure, normalizeCostBlock } from './SessionCostPanel.js'

// Canonical harness IDs are open — the store may report any ID — so this type
// constrains nothing on purpose, and the All sentinel is the one fixed value.
export type ExplorerProvider = string

// The All callback sentinel is compatible with the established provider contract,
// and doubles as the only inbound spelling of All: a parent that captures
// `onHarnessChange` and feeds it back needs no translation. A harness ID that is
// not in the inventory falls back to All, so an unknown inbound value degrades
// to All rather than to a phantom empty tab.
export const ALL_PROVIDER = 'agent-all' as const

// Labels never determine membership or merge canonical harness identities.
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
        <span
          data-testid="agent-session-cost"
          title={s.cost ? `Cost basis: ${s.cost.basis}` : undefined}
          className="w-20 shrink-0 text-right text-xs tabular-nums text-foreground font-medium"
        >
          {s.cost ? formatCostFigure(normalizeCostBlock(s.cost)) : costUsd != null ? usd(costUsd) : '—'}
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
  onHarnessChange?: (h: ExplorerProvider) => void
} = {}) {
  const [localProvider, setLocalProvider] = useState<ExplorerProvider>(ALL_PROVIDER)
  const setProvider = (p: ExplorerProvider) => {
    setLocalProvider(p)
    onHarnessChange?.(p)
  }
  const [openId, setOpenId] = useState<string | null>(null)

  // Tabs and rows share the unfiltered canonical inventory, so a selected subset
  // cannot hide other harnesses or turn provider aliases into request filters.
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

  // Sort so the tab strip is stable: the store returns started DESC, which would
  // otherwise slide every tab right as a new session lands, moving the tab a user
  // is mid-click on. Excluding the sentinel keeps the inventory provably disjoint
  // from the All tab, so no session can mint a second tab sharing its key.
  const harnesses = [...new Set(
    kyberData
      ?.map((s) => s.harness)
      .filter((h): h is string => Boolean(h) && h !== ALL_PROVIDER),
  )].sort()
  const providers = [
    { key: ALL_PROVIDER, label: 'Agent Sessions (All)' },
    ...harnesses.map((key) => ({ key, label: Object.hasOwn(HARNESS_LABELS, key) ? HARNESS_LABELS[key] : key })),
  ]
  const selectedProvider = activeHarness ?? localProvider
  const provider = harnesses.includes(selectedProvider) ? selectedProvider : ALL_PROVIDER
  const sessions = provider === ALL_PROVIDER ? kyberData : kyberData?.filter((s) => s.harness === provider)

  return (
    <>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        {providers.map((p) => (
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
            {sessions?.length === 0 && (
              <p className="px-4 py-6 text-sm text-tertiary-foreground" data-testid="explorer-empty">
                {/* The empty state can only be All: `harnesses` is derived from these rows
                    by a non-empty filter, so any selected subset already has a session. */}
                No sessions found.
              </p>
            )}
            {sessions?.map((s) => {
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
