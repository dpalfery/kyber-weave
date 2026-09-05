import { useEffect, useRef, useState, type ReactNode } from 'react'
import { CheckIcon } from './Icons'

/// A bordered footer button that opens a small menu above itself, standing in for the
/// macOS `Menu` with `.bordered` style. Closes on outside click, Escape, or selection.

export type MenuItem = {
  id: string
  label: string
  checked?: boolean
  disabled?: boolean
  danger?: boolean
  separatorBefore?: boolean
}

type Props = {
  label: ReactNode
  title?: string
  items: MenuItem[]
  onSelect: (id: string) => void
  align?: 'left' | 'right'
  className?: string
  /// Optional read-only footer line under the items (version, last update).
  footnote?: string
  /// Lay the items out in a grid (used for the 17-currency picker) instead of one column.
  columns?: number
}

export function DropMenu({ label, title, items, onSelect, align = 'left', className = '', footnote, columns = 1 }: Props) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation()
        setOpen(false)
      }
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey, true)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey, true)
    }
  }, [open])

  return (
    <div className={`dropmenu ${className}`} ref={rootRef}>
      <button
        type="button"
        className={`btn ${open ? 'btn-pressed' : ''}`}
        title={title}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen(o => !o)}
      >
        {label}
      </button>
      {open && (
        <div
          className={`dropmenu-panel dropmenu-${align} ${columns > 1 ? 'dropmenu-grid' : ''}`}
          role="menu"
          style={columns > 1 ? { gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` } : undefined}
        >
          {items.map(item => (
            <div key={item.id} className="dropmenu-item-wrap">
              {item.separatorBefore && <div className="dropmenu-sep" />}
              <button
                type="button"
                role="menuitem"
                className={`dropmenu-item ${item.danger ? 'dropmenu-danger' : ''}`}
                disabled={item.disabled}
                onClick={() => {
                  setOpen(false)
                  onSelect(item.id)
                }}
              >
                <span className="dropmenu-check">{item.checked && <CheckIcon size={11} />}</span>
                <span className="dropmenu-label">{item.label}</span>
              </button>
            </div>
          ))}
          {footnote && <div className="dropmenu-footnote">{footnote}</div>}
        </div>
      )}
    </div>
  )
}
