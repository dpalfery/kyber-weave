import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { cn } from '../../lib/utils.js'

const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

export interface SlideOutDrawerProps {
  open: boolean
  onClose: () => void
  /** Accessible name for the dialog, rendered as its `aria-label`. */
  label: string
  /**
   * Derives the `data-testid` of every part: `${prefix}-backdrop`,
   * `${prefix}-panel`, `${prefix}-title`, `${prefix}-subtitle`,
   * `${prefix}-close-button` and `${prefix}-body`. Derivation, not a hardcoded
   * id, is what lets two drawers share one shell without sharing test ids.
   */
  testIdPrefix: string
  /**
   * Off by default. On: focus moves into the dialog when it opens (the close
   * button) and back to the element that held focus when it opened once it
   * closes. Off: focus is never moved — the session drawer has never moved
   * focus and must not start now, so it leaves this unset.
   */
  manageFocus?: boolean
  children?: ReactNode
  /** Header text beside the close button; omitted when not given. */
  title?: string
  /** Secondary header line; omitted when not given. */
  subtitle?: string
  /**
   * Wrappers that predate this shell keep their own panel test id (the session
   * drawer is `session-inspector-drawer`) and the classes their markup has
   * always carried, so those are overridable; everything else derives from
   * `testIdPrefix` and the classes above.
   */
  panelTestId?: string
  rootClassName?: string
  backdropClassName?: string
  panelClassName?: string
  headerClassName?: string
  titleClassName?: string
  subtitleClassName?: string
  closeButtonClassName?: string
  bodyClassName?: string
}

/**
 * One shared slide-out shell — backdrop, right slide-in panel, header with
 * close button, scrollable body, Escape handling — so the Settings drawer and
 * the session drawer cannot drift in accessibility behavior: two hand-rolled
 * copies of a modal shell are two places for `role="dialog"`, Escape, and
 * focus-return to silently diverge.
 */
export function SlideOutDrawer({
  open,
  onClose,
  label,
  testIdPrefix,
  manageFocus = false,
  children,
  title,
  subtitle,
  panelTestId,
  rootClassName,
  backdropClassName,
  panelClassName,
  headerClassName,
  titleClassName,
  subtitleClassName,
  closeButtonClassName,
  bodyClassName,
}: SlideOutDrawerProps) {
  // This shell is limited to useState/useEffect on purpose: the session
  // drawer's characterization harnesses render this component (through the
  // wrapper) as a plain function under a test hook dispatcher that provides
  // only useEffect/useState/useMemo, so a useRef here would crash them.
  const [trackedOpen, setTrackedOpen] = useState(open)
  const [panelWidth, setPanelWidth] = useState<number | null>(null)
  const [restoreFocusTo, setRestoreFocusTo] = useState<HTMLElement | null>(null)

  // Capture what holds focus during the render that opens the drawer, not in
  // an effect: the close button's `autoFocus` moves focus during the commit,
  // so by the time an effect ran the opener would already have lost focus.
  // This is React's "adjust state when a prop changes" pattern — the immediate
  // re-render is never committed, and a single static render never enters the
  // branch, so SSR and the characterization harnesses are unaffected.
  if (open !== trackedOpen) {
    setTrackedOpen(open)
    if (open && manageFocus) {
      setRestoreFocusTo(
        typeof document !== 'undefined' && document.activeElement instanceof HTMLElement
          ? document.activeElement
          : null,
      )
    }
  }

  useEffect(() => {
    if (open || !manageFocus || restoreFocusTo === null) return
    // The dialog just unmounted; return focus to the element that held it
    // before the drawer opened, so keyboard users are not dropped on <body>.
    restoreFocusTo.focus()
    setRestoreFocusTo(null)
  }, [open, manageFocus, restoreFocusTo])

  // Drag the left edge to widen the panel so long text stays readable. A plain
  // function, not useCallback: the characterization harnesses provide no
  // other hooks than useEffect/useState/useMemo.
  const startResize = (e: { preventDefault: () => void }) => {
    e.preventDefault()
    const onMove = (ev: MouseEvent) => {
      setPanelWidth(Math.min(window.innerWidth, Math.max(320, window.innerWidth - ev.clientX)))
    }
    const onUp = () => {
      window.removeEventListener('mousemove', onMove)
      window.removeEventListener('mouseup', onUp)
    }
    window.addEventListener('mousemove', onMove)
    window.addEventListener('mouseup', onUp)
  }

  // Registered last on purpose: the characterization harnesses invoke this
  // component as a plain function, keep the last effect, and run it to assert
  // the window keydown listener; a later effect would displace it.
  useEffect(() => {
    if (!open || typeof window === 'undefined') return
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose()
        return
      }
      // aria-modal promises keyboard confinement: with manageFocus on, Tab cycles inside
      // the panel. Off, focus is never touched (the session drawer's contract).
      if (e.key === 'Tab' && manageFocus && typeof document !== 'undefined') {
        const panel = document.querySelector<HTMLElement>(
          `[data-testid="${panelTestId ?? `${testIdPrefix}-panel`}"]`,
        )
        if (panel === null) return
        const focusable = Array.from(
          panel.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR),
        )
        const first = focusable[0]
        const last = focusable[focusable.length - 1]
        if (first === undefined || last === undefined) {
          e.preventDefault()
          return
        }
        const active = document.activeElement
        if (!panel.contains(active)) {
          e.preventDefault()
          ;(e.shiftKey ? last : first).focus()
        } else if (e.shiftKey && active === first) {
          e.preventDefault()
          last.focus()
        } else if (!e.shiftKey && active === last) {
          e.preventDefault()
          first.focus()
        }
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [open, onClose, manageFocus, panelTestId, testIdPrefix])

  if (!open) return null

  return (
    <div className={cn('relative z-50', rootClassName)}>
      {/* Dark backdrop */}
      <div
        className={cn('fixed inset-0 bg-black/60 backdrop-blur-[1px] transition-opacity', backdropClassName)}
        onClick={onClose}
        aria-hidden="true"
        data-testid={`${testIdPrefix}-backdrop`}
      />

      {/* Drawer panel sliding in from right */}
      <div
        role="dialog"
        aria-modal="true"
        aria-label={label}
        className={cn(
          'fixed top-0 right-0 bottom-0 z-50 flex flex-col',
          'w-full max-w-full sm:max-w-xl md:max-w-2xl',
          'bg-card border-l border-border shadow-2xl',
          'animate-in slide-in-from-right duration-200 ease-in-out',
          panelClassName,
        )}
        style={panelWidth === null ? undefined : { width: panelWidth, maxWidth: '100vw' }}
        onClick={(e) => e.stopPropagation()}
        data-testid={panelTestId ?? `${testIdPrefix}-panel`}
      >
        <div
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize panel"
          onMouseDown={startResize}
          className="absolute left-0 top-0 bottom-0 w-1.5 -translate-x-1/2 cursor-col-resize hover:bg-border active:bg-border z-10"
          data-testid={`${testIdPrefix}-resize-handle`}
        />
        {/* Header */}
        <div
          className={cn('flex items-center justify-between border-b border-border px-5 py-3.5 shrink-0 bg-card', headerClassName)}
        >
          <div className="min-w-0 flex-1 pr-3">
            {title !== undefined && (
              <h2
                className={cn('text-sm font-semibold text-foreground truncate', titleClassName)}
                data-testid={`${testIdPrefix}-title`}
              >
                {title}
              </h2>
            )}
            {subtitle && (
              <p
                className={cn('text-xs text-muted-foreground truncate mt-0.5', subtitleClassName)}
                data-testid={`${testIdPrefix}-subtitle`}
              >
                {subtitle}
              </p>
            )}
          </div>
          <div className="flex items-center gap-2 shrink-0">
            {/* The close button is the dialog's focus target when manageFocus
                moves focus in; `autoFocus={false}` never moves focus. */}
            <button
              type="button"
              onClick={onClose}
              aria-label="Close"
              autoFocus={manageFocus}
              className={cn(
                'flex h-8 w-8 items-center justify-center rounded-md border border-border text-tertiary-foreground transition-colors hover:bg-interactive-secondary hover:text-foreground',
                closeButtonClassName,
              )}
              data-testid={`${testIdPrefix}-close-button`}
            >
              ✕
            </button>
          </div>
        </div>

        {/* Scrollable Body */}
        <div
          className={cn('flex-1 overflow-y-auto p-5 space-y-4 text-xs', bodyClassName)}
          data-testid={`${testIdPrefix}-body`}
        >
          {children}
        </div>
      </div>
    </div>
  )
}
