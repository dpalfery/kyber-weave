// Quarantine list + read-only detail inspector.
//
// DISPLAY layer: rows come from `GET /api/kyber/quarantine` (optionally
// `?harness=`), and the inspector reads `GET /api/kyber/quarantine/:spanId`
// when the server serves it. Servers predating the detail route answer 404 —
// the inspector then shows the list row's own reason/namespaces plus an
// explicit "stored evidence unavailable" notice, never a fabricated payload,
// message, or stack. Source labels are never invented: current rows are
// tagged `source=opencode` and render as OpenCode, never as Copilot.

import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'

import { fetchQuarantineDetail } from '../../lib/kyberApi.js'

export type QuarantineEntry = {
  spanId: string
  namespaces: string[]
  reason: string
  /** Stored origin id (e.g. `opencode`); null when the server stored none. */
  source?: string | null
  harness?: string | null
  name?: string | null
  seenAt?: string | null
  timestamp?: string | null
}

function entryHarness(entry: QuarantineEntry): string | null {
  const raw = entry.source ?? entry.harness ?? null
  if (raw === null) return null
  const trimmed = raw.trim()
  return trimmed.length > 0 ? trimmed : null
}

/**
 * The rows visible under a harness scope. `all` (or absent) is every row;
 * `unknown` is rows the server could not attribute; anything else is a
 * case-insensitive match on the row's stored source. The same predicate runs
 * whether or not the server honoured `?harness=`, so the list is truthful
 * against old and new servers alike.
 */
export function quarantineInScope(
  entries: readonly QuarantineEntry[],
  activeHarness: string | undefined,
): QuarantineEntry[] {
  if (activeHarness === undefined || activeHarness === '' || activeHarness === 'all') return [...entries]
  if (activeHarness === 'unknown') return entries.filter((entry) => entryHarness(entry) === null)
  const wanted = activeHarness.toLowerCase()
  return entries.filter((entry) => (entryHarness(entry) ?? '').toLowerCase() === wanted)
}

function textOf(value: unknown): string | null {
  if (typeof value === 'string') {
    const trimmed = value.trim()
    return trimmed.length > 0 ? trimmed : null
  }
  if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  return null
}

/** One list item as text: strings verbatim, structured values as JSON (never `[object Object]`). */
function displayItem(value: unknown): string {
  if (typeof value === 'string') return value
  return JSON.stringify(value) ?? String(value)
}

function stringList(value: unknown): string[] {
  if (Array.isArray(value)) return value.map(displayItem)
  if (typeof value === 'string' && value.trim() !== '') {
    try {
      const parsed: unknown = JSON.parse(value)
      if (Array.isArray(parsed)) return parsed.map(displayItem)
      return [displayItem(parsed)]
    } catch {
      return value.includes(',') ? value.split(',').map((part) => part.trim()).filter((part) => part !== '') : [value.trim()]
    }
  }
  return []
}

export function QuarantineView({
  entries,
  activeHarness,
}: {
  entries: QuarantineEntry[] | null | undefined
  activeHarness?: string
}) {
  const [selected, setSelected] = useState<string | null>(null)
  const scoped = quarantineInScope(entries ?? [], activeHarness)
  const selectedEntry = scoped.find((entry) => entry.spanId === selected) ?? null

  const detail = useQuery({
    queryKey: ['kyber-quarantine-detail', selected],
    queryFn: () => fetchQuarantineDetail(selected as string),
    enabled: selected !== null,
    retry: false,
  })

  if (!entries || entries.length === 0) {
    return <div className="py-8 text-center text-sm text-tertiary-foreground">No quarantined spans.</div>
  }

  return (
    <div className="rounded-md border border-border bg-card p-4">
      <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">
        Quarantine — {scoped.length} span{scoped.length === 1 ? '' : 's'}
      </h3>
      {activeHarness !== undefined && activeHarness !== 'all' && (
        <p data-testid="quarantine-scope-note" className="mb-2 text-xs text-muted-foreground">
          Showing {scoped.length} of {entries.length} for {activeHarness === 'unknown' ? 'unknown harness' : activeHarness}. Select All to clear the scope.
        </p>
      )}
      {scoped.length === 0 ? (
        <p data-testid="quarantine-empty-scope" className="py-4 text-center text-sm text-tertiary-foreground">
          No quarantined spans in this harness scope.
        </p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr>
                <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Span ID</th>
                <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Namespaces</th>
                <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Reason</th>
              </tr>
            </thead>
            <tbody>
              {scoped.map((e) => (
                <tr key={e.spanId} className="border-t border-border" data-testid="quarantine-row">
                  <td className="py-2 font-mono text-xs">
                    <button
                      type="button"
                      data-testid="quarantine-row-button"
                      data-span-id={e.spanId}
                      aria-label={`Inspect quarantined span ${e.spanId}`}
                      className="font-mono text-xs text-primary underline-offset-2 hover:underline focus-visible:outline-2"
                      onClick={() => setSelected(e.spanId)}
                    >
                      {e.spanId}
                    </button>
                  </td>
                  <td className="py-2 font-mono text-xs">{e.namespaces.join(', ')}</td>
                  <td className="py-2 text-xs">{e.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {selectedEntry !== null && (
        <QuarantineDetailPanel
          entry={selectedEntry}
          detail={detail.data ?? null}
          isLoading={detail.isLoading}
          isError={detail.isError}
          onRetry={() => void detail.refetch()}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  )
}

function QuarantineDetailPanel({
  entry,
  detail,
  isLoading,
  isError,
  onRetry,
  onClose,
}: {
  entry: QuarantineEntry
  detail: Record<string, unknown> | null
  isLoading: boolean
  isError: boolean
  onRetry: () => void
  onClose: () => void
}) {
  const body = (detail ?? {}) as Record<string, unknown>
  const reason = textOf(body.reason) ?? entry.reason
  const namespaces = body.namespaces !== undefined ? stringList(body.namespaces) : entry.namespaces
  const source = textOf(body.source ?? body.harness) ?? entryHarness(entry) ?? 'unknown'
  const name = textOf(body.name) ?? entry.name ?? null
  const seenAt = textOf(body.seen_at ?? body.timestamp ?? body.at) ?? entry.seenAt ?? entry.timestamp ?? null
  // WHY rawPayload first: the detail route serves the stored span JSON as
  // `rawPayload` with `evidenceAvailable`; `payload`/`attributes` are legacy
  // keys. Reading only the legacy keys reported "no payload" for rows that
  // had stored evidence.
  const payload = body.rawPayload ?? body.payload ?? body.attributes ?? null
  const hasPayload = payload !== null && payload !== undefined

  return (
    <section
      data-testid="quarantine-detail"
      aria-label={`Quarantined span ${entry.spanId}`}
      className="mt-4 rounded border border-border p-3"
    >
      <div className="flex items-center justify-between gap-2">
        <h4 className="font-mono text-xs font-semibold">Span {entry.spanId}</h4>
        <button
          type="button"
          data-testid="quarantine-detail-close"
          aria-label="Close quarantine detail"
          onClick={onClose}
          className="rounded border border-border px-2 py-1 text-xs"
        >
          Close
        </button>
      </div>
      <dl className="mt-2 flex flex-col gap-1 text-xs">
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">Why quarantined:</dt>
          <dd data-testid="quarantine-detail-reason">{reason === '' ? 'reason not recorded' : reason}</dd>
        </div>
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">Source:</dt>
          <dd data-testid="quarantine-detail-source">{source}</dd>
        </div>
        {name !== null && (
          <div className="flex flex-wrap gap-2">
            <dt className="text-muted-foreground">Name:</dt>
            <dd data-testid="quarantine-detail-name">{name}</dd>
          </div>
        )}
        <div className="flex flex-wrap gap-2">
          <dt className="text-muted-foreground">Namespaces:</dt>
          <dd data-testid="quarantine-detail-namespaces" className="font-mono">
            {namespaces.length > 0 ? namespaces.join(', ') : 'none recorded'}
          </dd>
        </div>
        {seenAt !== null && (
          <div className="flex flex-wrap gap-2">
            <dt className="text-muted-foreground">Seen:</dt>
            <dd data-testid="quarantine-detail-seen">{seenAt}</dd>
          </div>
        )}
      </dl>
      {isLoading && <p data-testid="quarantine-detail-loading" className="mt-2 text-xs text-muted-foreground">Reading stored evidence…</p>}
      {!isLoading && isError && (
        <div data-testid="quarantine-detail-unavailable" role="status" className="mt-2 text-xs text-muted-foreground">
          <p>
            Stored evidence for this row is unavailable — this server does not serve quarantine detail, or the row
            predates stored payloads. What is shown above is the list row itself, not a retrieved payload.
          </p>
          <button type="button" data-testid="quarantine-detail-retry" onClick={onRetry} className="mt-1 rounded border border-border px-2 py-1 text-xs">
            Retry
          </button>
        </div>
      )}
      {!isLoading && !isError && hasPayload && (
        <div className="mt-2">
          <h5 className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Original payload</h5>
          <pre data-testid="quarantine-detail-payload" className="mt-1 max-h-64 overflow-auto rounded bg-interactive-secondary p-2 font-mono text-[11px]">
            {typeof payload === 'string' ? payload : JSON.stringify(payload, null, 2)}
          </pre>
        </div>
      )}
      {!isLoading && !isError && !hasPayload && detail !== null && (
        <p data-testid="quarantine-detail-no-payload" role="status" className="mt-2 text-xs text-muted-foreground">
          No original payload was stored for this row. Older rows lack stored evidence; nothing is fabricated here.
        </p>
      )}
    </section>
  )
}
