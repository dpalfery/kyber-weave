// The right-side slide-in panel the header tools open.
//
// It is a dialog and is treated as one: it names itself, it can be closed
// three ways (the close button, Escape, the backdrop), it takes focus when it
// opens and hands it back to the control that opened it when it closes, and
// Tab stays inside it while it is up. A panel that leaves focus on the page
// behind it reads to a keyboard or screen-reader user as a page they never
// left.
//
// Sizing is phone-first (`w-full`, its own scroll, a viewport-height cap):
// the dashboard is read on a phone, and a fixed desktop width is a control
// strip that cannot be reached at all.

import { useEffect, useRef } from 'react'

export interface SlideInPanelProps {
  /** Accessible name — the same words as the button that opens it. */
  title: string
  onClose: () => void
  /**
   * The control to hand focus back to on close. Callers pass the button that
   * opened the panel: `document.activeElement` is not that button (a pointer
   * click does not focus it), and restoring focus to `<body>` drops the
   * operator at the top of a long page.
   */
  restoreFocusTo?: HTMLElement | null
  children: React.ReactNode
  testId?: string
}

/** Every element Tab can land on inside the panel, in document order. */
function focusableIn(root: HTMLElement): HTMLElement[] {
  const selector =
    'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
  return [...root.querySelectorAll<HTMLElement>(selector)].filter(
    (node) => node.offsetParent !== null || node === document.activeElement,
  )
}

export function SlideInPanel({
  title,
  onClose,
  restoreFocusTo,
  children,
  testId = 'dashboard-tools-panel',
}: SlideInPanelProps) {
  const panelRef = useRef<HTMLDivElement | null>(null)
  // Whatever opened the panel gets focus back on close: the operator's place
  // in the header, not the top of the document.
  const opener = useRef<HTMLElement | null>(restoreFocusTo ?? null)

  useEffect(() => {
    opener.current = restoreFocusTo ?? opener.current
    const panel = panelRef.current
    if (panel === null) return
    const first = focusableIn(panel)[0] ?? panel
    first.focus()
    return () => {
      const target = opener.current
      if (target instanceof HTMLElement) target.focus()
    }
    // Status and Maintenance swap by re-rendering this same instance, so the effect
    // re-runs when the title or the opener changes: focus moves into the new panel and
    // close restores to the control that opened it, not the one that opened the last.
  }, [title, restoreFocusTo])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      const panel = panelRef.current
      if (panel === null) return
      if (event.key === 'Escape') {
        event.stopPropagation()
        onClose()
        return
      }
      if (event.key !== 'Tab') return
      // Containment: Tab from the last control wraps to the first, and
      // Shift+Tab from the first wraps back, so the page behind stays out of
      // reach while the panel is up.
      const items = focusableIn(panel)
      if (items.length === 0) {
        event.preventDefault()
        panel.focus()
        return
      }
      const first = items[0]!
      const last = items[items.length - 1]!
      const active = document.activeElement
      if (event.shiftKey && (active === first || active === panel)) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && active === last) {
        event.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  return (
    <div className="fixed inset-0 z-50 flex justify-end" data-testid={`${testId}-root`}>
      <div
        data-testid={`${testId}-backdrop`}
        aria-hidden="true"
        className="absolute inset-0 bg-black/40"
        onClick={onClose}
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        tabIndex={-1}
        data-testid={testId}
        className="relative flex h-full max-h-full w-full max-w-md flex-col gap-density-cluster overflow-y-auto border-l border-border bg-background p-chrome shadow-2xl"
      >
        <div className="flex items-center justify-between gap-density-cluster">
          <h2
            data-testid={`${testId}-title`}
            className="font-display text-density-lg font-semibold text-foreground"
          >
            {title}
          </h2>
          <button
            type="button"
            data-testid={`${testId}-close`}
            aria-label={`Close ${title}`}
            className="flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground hover:bg-interactive-secondary hover:text-foreground"
            onClick={onClose}
          >
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>
        <div className="flex min-h-0 flex-1 flex-col gap-density-stack">{children}</div>
      </div>
    </div>
  )
}