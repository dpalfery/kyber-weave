/**
 * Nothing in scope yet.
 *
 * Distinct from the stale state: there is no old document being passed off as
 * current, there is simply nothing to show. R8.10 puts Refresh now here,
 * because reading local harness history is exactly what would fill it.
 */

type Props = {
  onRefreshNow: () => void
  busy: boolean
}

export function EmptyState({ onRefreshNow, busy }: Props) {
  return (
    <section className="empty" data-testid="empty-state">
      <p className="empty__line">No session in this window.</p>
      <p className="empty__hint">
        A refresh reads local harness history into the store.
      </p>
      <button type="button" onClick={onRefreshNow} disabled={busy} data-testid="refresh-now">
        {busy ? 'Refreshing…' : 'Refresh now'}
      </button>
    </section>
  )
}
