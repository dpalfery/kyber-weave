/**
 * The one pager every paged list shares: previous, next and page numbers,
 * computed from the window the server answered under.
 *
 * Nothing renders while every row fits one page — a list the server served
 * whole has nothing to page — and every control disables while the page it
 * points at is loading, so one press cannot answer twice. The controls are
 * real buttons, so they are reachable and operable by keyboard.
 */
export interface PagerProps {
  /** The one-based page the list is showing. */
  page: number
  /** Rows per page the server answered under. */
  limit: number
  /** How many rows the whole list holds. */
  total: number
  /** Reports the page the user navigated to. */
  onPageChange: (page: number) => void
  /** True while the page this pager points at is loading. */
  loading?: boolean
  /** What this pager pages; names the nav for screen readers. */
  ariaLabel?: string
}

/** Page numbers and the gaps between them, for one rendered pager. */
type PagerSlot = { kind: 'page'; page: number } | { kind: 'gap' }

/**
 * The page numbers one pager shows: every page while they fit, otherwise the
 * first, a window around the current one, and the last, with gaps between.
 */
export function pagerSlots(page: number, pageCount: number, windowSize = 3): PagerSlot[] {
  if (pageCount <= windowSize + 4) {
    return Array.from({ length: pageCount }, (_, index) => ({ kind: 'page' as const, page: index + 1 }))
  }
  const first = 1
  const last = pageCount
  const windowStart = Math.min(Math.max(page - Math.floor(windowSize / 2), first + 1), last - windowSize)
  const windowEnd = windowStart + windowSize - 1
  const slots: PagerSlot[] = [{ kind: 'page', page: first }, { kind: 'gap' }]
  for (let candidate = windowStart; candidate <= windowEnd; candidate += 1) {
    slots.push({ kind: 'page', page: candidate })
  }
  slots.push({ kind: 'gap' }, { kind: 'page', page: last })
  return slots
}

export function Pager({
  page,
  limit,
  total,
  onPageChange,
  loading = false,
  ariaLabel = 'Pagination',
}: PagerProps) {
  if (limit <= 0 || total <= limit) return null
  const pageCount = Math.max(1, Math.ceil(total / limit))
  const current = Math.min(Math.max(page, 1), pageCount)

  return (
    <nav
      aria-label={ariaLabel}
      data-testid="list-pager"
      className="flex items-center justify-center gap-1.5 border-t border-border px-4 py-2"
    >
      <button
        type="button"
        data-testid="pager-prev"
        aria-label="Previous page"
        disabled={current <= 1 || loading}
        onClick={() => onPageChange(current - 1)}
        className="rounded px-2 py-1 text-xs text-tertiary-foreground transition-colors hover:bg-interactive-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
      >
        Previous
      </button>
      {pagerSlots(current, pageCount).map((slot, index) =>
        slot.kind === 'gap' ? (
          <span key={`gap-${index}`} aria-hidden="true" className="px-1 text-xs text-tertiary-foreground">
            …
          </span>
        ) : (
          <button
            key={`page-${slot.page}`}
            type="button"
            data-testid={`pager-page-${slot.page}`}
            aria-label={`Page ${slot.page}`}
            aria-current={slot.page === current ? 'page' : undefined}
            disabled={loading && slot.page !== current}
            onClick={() => onPageChange(slot.page)}
            className={
              slot.page === current
                ? 'rounded bg-interactive-secondary px-2 py-1 text-xs font-semibold text-foreground'
                : 'rounded px-2 py-1 text-xs text-tertiary-foreground transition-colors hover:bg-interactive-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40'
            }
          >
            {slot.page}
          </button>
        ),
      )}
      <button
        type="button"
        data-testid="pager-next"
        aria-label="Next page"
        disabled={current >= pageCount || loading}
        onClick={() => onPageChange(current + 1)}
        className="rounded px-2 py-1 text-xs text-tertiary-foreground transition-colors hover:bg-interactive-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
      >
        Next
      </button>
    </nav>
  )
}
