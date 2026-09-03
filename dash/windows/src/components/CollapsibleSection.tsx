import { useState, type ReactNode } from 'react'
import { ChevronRight } from './Icons'

/// The macOS CollapsibleSection shell: 3px brand dot + caption, trailing column headers,
/// a chevron that rotates 90 degrees when open. Same component for Activity and Models so
/// the two headers can never drift apart again.

type Props = {
  caption: string
  columns?: Array<{ label: string; width: number }>
  defaultExpanded?: boolean
  children: ReactNode
}

export function CollapsibleSection({ caption, columns = [], defaultExpanded = true, children }: Props) {
  const [expanded, setExpanded] = useState(defaultExpanded)
  return (
    <section className="collapsible">
      <button
        type="button"
        className="collapsible-header"
        aria-expanded={expanded}
        onClick={() => setExpanded(e => !e)}
      >
        <SectionCaption text={caption} />
        <span className="collapsible-spacer" />
        {expanded && columns.map(c => (
          <span key={c.label} className="col-header" style={{ minWidth: c.width }}>{c.label}</span>
        ))}
        <ChevronRight size={9} className={`chevron ${expanded ? 'chevron-open' : ''}`} />
      </button>
      {expanded && <div className="collapsible-body">{children}</div>}
    </section>
  )
}

export function SectionCaption({ text, muted = true }: { text: string; muted?: boolean }) {
  return (
    <span className={`section-caption ${muted ? '' : 'section-caption-strong'}`}>
      <span className="section-dot" />
      {text}
    </span>
  )
}
