import {
  KYBER_SECTION_PAGE_LIMITS,
  type KyberSectionPageLimit,
  type KyberSectionPageOrder,
} from '../../lib/kyberApi.js'

/**
 * The one pager every paged list shares: previous, next, page numbers, and
 * optional page-size and sort-order controls, computed from the window the
 * server answered under.
 *
 * Nothing renders when the list is empty (`total <= 0`), or when every row fits
 * one page and neither page-size nor sort controls are provided. Every control
 * disables while the page it points at is loading, so one press cannot answer
 * twice. The controls are real buttons and selects, so they are reachable and
 * operable by keyboard.
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
  /** Available page-size options when the pager renders a size selector. */
  limitOptions?: readonly number[]
  /** Reports a newly selected page size. */
  onLimitChange?: (limit: KyberSectionPageLimit) => void
  /** Current sort order when the pager renders a sort toggle. */
  order?: KyberSectionPageOrder
  /** Reports a toggled sort order. */
  onOrderChange?: (order: KyberSectionPageOrder) => void
  /** Custom labels for the sort toggle button (`desc` and `asc`). */
  orderLabels?: { desc: string; asc: string }
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
  limitOptions,
  onLimitChange,
  order = 'desc',
  onOrderChange,
  orderLabels = { desc: 'Newest first', asc: 'Oldest first' },
  loading = false,
  ariaLabel = 'Pagination',
}: PagerProps) {
  const showPageButtons = limit > 0 && total > limit
  const resolvedLimitOptions = limitOptions ?? KYBER_SECTION_PAGE_LIMITS
  const showLimitSelect = onLimitChange !== undefined && resolvedLimitOptions.length > 0
  const showOrderToggle = onOrderChange !== undefined
  const hasControls = showLimitSelect || showOrderToggle

  if (limit <= 0 || total <= 0) return null
  if (!showPageButtons && !hasControls) return null

  const pageCount = Math.max(1, Math.ceil(total / limit))
  const current = Math.min(Math.max(page, 1), pageCount)
  const nextOrder: KyberSectionPageOrder = order === 'desc' ? 'asc' : 'desc'

  return (
    <nav
      aria-label={ariaLabel}
      data-testid="list-pager"
      className="flex flex-wrap items-center justify-center gap-1.5 border-t border-border px-4 py-2"
    >
      {showPageButtons && (
        <>
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
        </>
      )}
      {showLimitSelect && (
        <select
          aria-label="Rows per page"
          data-testid="pager-limit"
          value={String(limit)}
          disabled={loading}
          onChange={(event) => {
            const parsed = Number(event.target.value)
            if (parsed === 25 || parsed === 50 || parsed === 100) {
              onLimitChange(parsed)
            }
          }}
          className="rounded border border-border bg-card px-2 py-1 text-xs text-foreground transition-colors hover:bg-interactive-secondary disabled:cursor-not-allowed disabled:opacity-40"
        >
          {resolvedLimitOptions.map((option) => (
            <option key={option} value={String(option)}>
              {option} / page
            </option>
          ))}
        </select>
      )}
      {showOrderToggle && (
        <button
          type="button"
          data-testid="pager-order"
          disabled={loading}
          onClick={() => onOrderChange(nextOrder)}
          className="rounded border border-border bg-card px-2 py-1 text-xs text-tertiary-foreground transition-colors hover:bg-interactive-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-40"
        >
          {orderLabels[order]}
        </button>
      )}
    </nav>
  )
}
