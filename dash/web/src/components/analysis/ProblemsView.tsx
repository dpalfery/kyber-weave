// Problems list + read-only detail inspector.
//
// DISPLAY layer: rows come from `GET /api/kyber/problems` (optionally
// `?harness=`), and the inspector reads `GET /api/kyber/problem/:id` when
// the server serves it. A 404 (servers predating the route) or a row that
// never stored its evidence renders an explicit unavailable notice — the
// code/message from the list row is shown as the list row, and no message,
// payload, or stack trace is ever invented.

import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'

import { fetchProblemDetail } from '../../lib/kyberApi.js'

export type ProblemEntry = {
  id?: string | number
  severity: 'error' | 'warning'
  code: string
  message: string
  location?: string
  spanId?: string
  harness?: string | null
  at?: string | null
}

function entryHarness(problem: ProblemEntry): string | null {
  const raw = problem.harness ?? problem.location ?? null
  // `location` doubles as the harness alias in older list mappings (App.tsx
  // mapped `p.harness` into `location`), so only treat it as a harness when
  // the row carries no dedicated harness. A path-like location is not a
  // harness and must not filter rows away.
  if (problem.harness !== undefined && problem.harness !== null) {
    const trimmed = String(problem.harness).trim()
    return trimmed.length > 0 ? trimmed : null
  }
  if (typeof raw === 'string' && raw.trim() !== '' && !raw.includes('/') && !raw.includes(':')) {
    return raw.trim()
  }
  return null
}

/**
 * The rows visible under a harness scope. Same sentinel contract as the
 * quarantine list: `all`/absent is everything, `unknown` is unattributed
 * rows, otherwise a case-insensitive harness match. Runs locally so old
 * servers that ignore `?harness=` still show a truthful list.
 */
export function problemsInScope(
  problems: readonly ProblemEntry[],
  activeHarness: string | undefined,
): ProblemEntry[] {
  if (activeHarness === undefined || activeHarness === '' || activeHarness === 'all') return [...problems]
  if (activeHarness === 'unknown') return problems.filter((problem) => entryHarness(problem) === null)
  const wanted = activeHarness.toLowerCase()
  return problems.filter((problem) => (entryHarness(problem) ?? '').toLowerCase() === wanted)
}

function textOf(value: unknown): string | null {
  if (typeof value === 'string') {
    const trimmed = value.trim()
    return trimmed.length > 0 ? trimmed : null
  }
  if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  return null
}

export function ProblemsView({
  problems,
  activeHarness,
}: {
  problems: ProblemEntry[] | null | undefined
  activeHarness?: string
}) {
  const [selected, setSelected] = useState<string | null>(null)
  const scoped = problemsInScope(problems ?? [], activeHarness)
  // Same (problem, index) key the rows render with, so a duplicate or id-less row
  // opens the row that was clicked rather than the first one sharing its code.
  const selectedProblem = scoped.find((problem, i) => problemKey(problem, i) === selected) ?? null
  // Only a row the server stored has an id to ask for; a synthetic row key would be a
  // guaranteed 404, so no request is made and the list row itself is shown.
  const serverId = selectedProblem?.id !== undefined ? String(selectedProblem.id) : null

  const detail = useQuery({
    queryKey: ['kyber-problem-detail', serverId],
    queryFn: () => fetchProblemDetail(serverId as string),
    enabled: serverId !== null,
    retry: false,
  })

  if (!problems || problems.length === 0) {
    return <div className="py-8 text-center text-sm text-tertiary-foreground">No problems.</div>
  }

  return (
    <div className="rounded-md border border-border bg-card p-4">
      <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">
        Problems — {scoped.length}
      </h3>
      {activeHarness !== undefined && activeHarness !== 'all' && (
        <p data-testid="problems-scope-note" className="mb-2 text-xs text-muted-foreground">
          Showing {scoped.length} of {problems.length} for {activeHarness === 'unknown' ? 'unknown harness' : activeHarness}. Select All to clear the scope.
        </p>
      )}
      {scoped.length === 0 ? (
        <p data-testid="problems-empty-scope" className="py-4 text-center text-sm text-tertiary-foreground">
          No problems in this harness scope.
        </p>
      ) : (
        <div className="flex flex-col gap-2">
          {scoped.map((p, i) => {
            const key = problemKey(p, i)
            return (
              <div
                key={key}
                data-testid="problem-row"
                className={`rounded border px-3 py-2 text-sm ${p.severity === 'error' ? 'border-red-200 bg-red-50 dark:border-red-800 dark:bg-red-950/30' : 'border-amber-200 bg-amber-50 dark:border-amber-800 dark:bg-amber-950/30'}`}
              >
                <div className="flex items-center gap-2">
                  <span
                    className={`rounded px-1.5 py-0.5 text-[10px] font-medium uppercase ${p.severity === 'error' ? 'bg-red-200 text-red-800 dark:bg-red-800 dark:text-red-100' : 'bg-amber-200 text-amber-800 dark:bg-amber-800 dark:text-amber-100'}`}
                  >
                    {p.severity}
                  </span>
                  <span className="font-mono text-xs font-medium">{p.code}</span>
                  {p.location && <span className="text-xs text-tertiary-foreground">at {p.location}</span>}
                  {p.spanId && <span className="font-mono text-xs text-tertiary-foreground">{p.spanId}</span>}
                </div>
                <p className="mt-1 text-xs">{p.message}</p>
                <button
                  type="button"
                  data-testid="problem-row-button"
                  aria-label={`Inspect problem ${p.code}`}
                  className="mt-1 text-xs text-primary underline-offset-2 hover:underline focus-visible:outline-2"
                  onClick={() => setSelected(key)}
                >
                  Inspect
                </button>
              </div>
            )
          })}
        </div>
      )}
      {selectedProblem !== null && (
        <ProblemDetailPanel
          problem={selectedProblem}
          detail={detail.data ?? null}
          isLoading={serverId !== null && detail.isLoading}
          isError={serverId === null || detail.isError}
          canRetry={serverId !== null}
          onRetry={() => void detail.refetch()}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  )
}

/** A row's identity in the list: the stored id, else code + span + position (always unique). */
function problemKey(problem: ProblemEntry, index: number): string {
  if (problem.id !== undefined) return String(problem.id)
  return `${problem.code}:${problem.spanId ?? ''}:${index}`
}

function ProblemDetailPanel({
  problem,
  detail,
  isLoading,
  isError,
  canRetry,
  onRetry,
  onClose,
}: {
  problem: ProblemEntry
  detail: Record<string, unknown> | null
  isLoading: boolean
  isError: boolean
  canRetry: boolean
  onRetry: () => void
  onClose: () => void
}) {
  const body = (detail ?? {}) as Record<string, unknown>
  const code = textOf(body.code) ?? problem.code
  const message = textOf(body.message) ?? (problem.message !== '' ? problem.message : null)
  const incoming = body.payload ?? body.message ?? null
  const stack = body.stack ?? body.stackTrace ?? null
  const spanId = textOf(body.span_id ?? body.spanId) ?? problem.spanId ?? null
  const at = textOf(body.at ?? body.timestamp) ?? problem.at ?? null

  return (
    <section
      data-testid="problem-detail"
      aria-label={`Problem ${code}`}
      className="mt-4 rounded border border-border p-3"
    >
      <div className="flex items-center justify-between gap-2">
        <h4 className="font-mono text-xs font-semibold">{code}</h4>
        <button
          type="button"
          data-testid="problem-detail-close"
          aria-label="Close problem detail"
          onClick={onClose}
          className="rounded border border-border px-2 py-1 text-xs"
        >
          Close
        </button>
      </div>
      <dl className="mt-2 flex flex-col gap-1 text-xs">
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">Message:</dt>
          <dd data-testid="problem-detail-message">{message ?? 'message not recorded'}</dd>
        </div>
        {spanId !== null && (
          <div className="flex flex-wrap gap-2">
            <dt className="text-muted-foreground">Span:</dt>
            <dd data-testid="problem-detail-span" className="font-mono">{spanId}</dd>
          </div>
        )}
        {at !== null && (
          <div className="flex flex-wrap gap-2">
            <dt className="text-muted-foreground">At:</dt>
            <dd data-testid="problem-detail-at">{at}</dd>
          </div>
        )}
      </dl>
      {isLoading && <p data-testid="problem-detail-loading" className="mt-2 text-xs text-muted-foreground">Reading stored evidence…</p>}
      {!isLoading && isError && (
        <div data-testid="problem-detail-unavailable" role="status" className="mt-2 text-xs text-muted-foreground">
          <p>
            Stored evidence for this row is unavailable — this server does not serve problem detail, or the row
            predates stored payloads. What is shown above is the list row itself; no message or stack is fabricated.
          </p>
          {canRetry && (
            <button type="button" data-testid="problem-detail-retry" onClick={onRetry} className="mt-1 rounded border border-border px-2 py-1 text-xs">
              Retry
            </button>
          )}
        </div>
      )}
      {!isLoading && !isError && incoming !== null && incoming !== undefined && (
        <div className="mt-2">
          <h5 className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Exact incoming message / payload</h5>
          <pre data-testid="problem-detail-payload" className="mt-1 max-h-64 overflow-auto rounded bg-interactive-secondary p-2 font-mono text-[11px]">
            {typeof incoming === 'string' ? incoming : JSON.stringify(incoming, null, 2)}
          </pre>
        </div>
      )}
      {!isLoading && !isError && stack !== null && stack !== undefined && (
        <div className="mt-2">
          <h5 className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Stack trace</h5>
          <pre data-testid="problem-detail-stack" className="mt-1 max-h-64 overflow-auto rounded bg-interactive-secondary p-2 font-mono text-[11px]">
            {typeof stack === 'string' ? stack : JSON.stringify(stack, null, 2)}
          </pre>
        </div>
      )}
      {!isLoading && !isError && detail !== null && (incoming === null || incoming === undefined) && (stack === null || stack === undefined) && (
        <p data-testid="problem-detail-no-evidence" role="status" className="mt-2 text-xs text-muted-foreground">
          No incoming message, payload, or stack trace was stored for this row. Older rows lack stored evidence;
          nothing is fabricated here.
        </p>
      )}
    </section>
  )
}
