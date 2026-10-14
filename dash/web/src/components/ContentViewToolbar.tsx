import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { cn } from '../lib/utils.js'

/** Which rendering of a content body is showing. */
export type ContentViewMode = 'formatted' | 'raw'

/** Upper bound on highlighted matches per body; past it the count reads "N+". */
const MAX_SEARCH_MATCHES = 1000

/** Copy source: the exact text, or a thunk so large values stringify only on click. */
export type CopySource = string | (() => string)

function resolveCopySource(source: CopySource): string {
  return typeof source === 'function' ? source() : source
}

/**
 * Copies the exact source string and confirms with a short "Copied!" label.
 * Every formatted/raw surface uses it, so raw access never depends on the
 * formatted view it sits beside.
 */
export function CopyExactButton({ text, testId, label = 'Copy' }: { text: CopySource; testId: string; label?: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <button
      type="button"
      onClick={() => {
        try {
          void navigator.clipboard.writeText(resolveCopySource(text))
          setCopied(true)
          setTimeout(() => setCopied(false), 2000)
        } catch {
          // ignore
        }
      }}
      className="rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:text-foreground hover:bg-interactive-secondary transition-colors"
      data-testid={testId}
    >
      <span aria-live="polite">{copied ? 'Copied!' : label}</span>
    </button>
  )
}

/** Search state the toolbar renders; `null` where search is not offered. */
export interface ContentSearch {
  query: string
  onQueryChange: (query: string) => void
  matchCount: number
  capped: boolean
  current: number
  step: (delta: number) => void
}

/**
 * Searchable body hook. Highlights matches in whatever text is currently
 * rendered (formatted or raw) through the CSS Custom Highlight API, so the
 * rendering itself is never rewritten. Attach `bodyRef` to the element that
 * wraps the rendered body. `enabled: false` turns the whole mechanism off.
 */
export function useContentSearch(view: ContentViewMode, text: string, enabled: boolean) {
  const bodyRef = useRef<HTMLDivElement>(null)
  const [query, setQuery] = useState('')
  const [debouncedQuery, setDebouncedQuery] = useState('')
  const [matchCount, setMatchCount] = useState(0)
  const [capped, setCapped] = useState(false)
  const [current, setCurrent] = useState(0)
  const rangesRef = useRef<Range[]>([])
  // Several instances coexist in one drawer, so highlight names must be unique per instance.
  const searchId = useId().replace(/[^a-zA-Z0-9_-]/g, '')
  const matchName = `search-match-${searchId}`
  const currentName = `search-current-${searchId}`

  useEffect(() => {
    const t = setTimeout(() => setDebouncedQuery(query), query ? 150 : 0)
    return () => clearTimeout(t)
  }, [query])

  useEffect(() => {
    if (!enabled) return
    const root = bodyRef.current
    const needle = debouncedQuery.toLowerCase()
    const ranges: Range[] = []
    let overflow = false
    if (root && needle) {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT)
      scan: for (let n = walker.nextNode(); n; n = walker.nextNode()) {
        const hay = (n.nodeValue ?? '').toLowerCase()
        for (let i = hay.indexOf(needle); i !== -1; i = hay.indexOf(needle, i + needle.length)) {
          if (ranges.length >= MAX_SEARCH_MATCHES) {
            overflow = true
            break scan
          }
          const r = document.createRange()
          r.setStart(n, i)
          r.setEnd(n, i + needle.length)
          ranges.push(r)
        }
      }
    }
    rangesRef.current = ranges
    setMatchCount(ranges.length)
    setCapped(overflow)
    setCurrent(0)
  }, [enabled, debouncedQuery, view, text])

  useEffect(() => {
    if (!enabled) return
    const ranges = rangesRef.current
    const registry = (globalThis as { CSS?: { highlights?: Map<string, unknown> } }).CSS?.highlights
    const HighlightCtor = (globalThis as { Highlight?: new (...r: Range[]) => unknown }).Highlight
    if (registry && HighlightCtor) {
      registry.set(matchName, new HighlightCtor(...ranges))
      const active = ranges[current]
      if (active) registry.set(currentName, new HighlightCtor(active))
      else registry.delete(currentName)
    }
    const active = ranges[current]
    if (active) {
      // Reveal the match if it sits inside a collapsed fold.
      for (let el = active.startContainer.parentElement; el && el !== bodyRef.current; el = el.parentElement) {
        if (el instanceof HTMLDetailsElement && !el.open) el.open = true
      }
      active.startContainer.parentElement?.scrollIntoView?.({ block: 'center' })
    }
    return () => {
      registry?.delete(matchName)
      registry?.delete(currentName)
    }
  }, [enabled, matchCount, current, debouncedQuery, view, text, matchName, currentName])

  const step = (delta: number) => {
    if (matchCount === 0) return
    setCurrent((c) => (c + delta + matchCount) % matchCount)
  }

  const highlightCss = `::highlight(${matchName}){background-color:rgb(250 204 21 / 0.35);color:inherit}::highlight(${currentName}){background-color:rgb(249 115 22 / 0.65);color:inherit}`

  return {
    bodyRef,
    highlightCss,
    search: enabled
      ? ({ query, onQueryChange: setQuery, matchCount, capped, current, step } satisfies ContentSearch)
      : null,
  }
}

export interface ContentViewToolbarProps {
  /** Shown at the left of the row: a label, a descriptor, a character count. */
  leading?: ReactNode
  /** Accessible and test-id prefix shared by every control in the row. */
  viewPrefix: string
  /** Names the content in aria labels, e.g. "System prompt". */
  label?: string
  /** Present when the toolbar owns the Formatted/Raw switch. */
  view?: ContentViewMode
  onViewChange?: (view: ContentViewMode) => void
  /** Present when the toolbar offers search. */
  search?: ContentSearch | null
  /** Present when the toolbar offers an exact Copy. */
  copy?: { text: CopySource; testId: string } | null
}

/**
 * The one Formatted/Raw toolbar: view switch, search with match count and
 * prev/next, and exact Copy. Any control can be omitted where an outer
 * toolbar already supplies it.
 */
export function ContentViewToolbar({
  leading,
  viewPrefix,
  label,
  view,
  onViewChange,
  search,
  copy,
}: ContentViewToolbarProps) {
  const showToggle = view !== undefined && onViewChange !== undefined
  const groupLabel = `${label ?? 'Content'}: formatted or raw view`
  const showRight = showToggle || Boolean(search) || Boolean(copy)
  return (
    <div
      className="flex items-center justify-between gap-2 flex-wrap"
      role={showToggle ? 'group' : undefined}
      aria-label={showToggle ? groupLabel : undefined}
    >
      {leading ? <div className="flex items-center gap-2 min-w-0">{leading}</div> : null}
      {showRight ? (
        <div className="flex items-center gap-1.5 ml-auto">
          {showToggle ? (
            <div className="flex rounded border border-border bg-interactive-secondary p-0.5 text-xs">
              <button
                type="button"
                onClick={() => onViewChange?.('formatted')}
                data-testid={`${viewPrefix}-view-formatted`}
                aria-pressed={view === 'formatted'}
                className={cn(
                  'px-2 py-0.5 rounded-[4px] font-medium transition-colors',
                  view === 'formatted' ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
                )}
              >
                Formatted
              </button>
              <button
                type="button"
                onClick={() => onViewChange?.('raw')}
                data-testid={`${viewPrefix}-view-raw`}
                aria-pressed={view === 'raw'}
                className={cn(
                  'px-2 py-0.5 rounded-[4px] font-medium transition-colors',
                  view === 'raw' ? 'bg-card text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
                )}
              >
                Raw
              </button>
            </div>
          ) : null}
          {search ? <SearchControls search={search} viewPrefix={viewPrefix} label={label} /> : null}
          {copy ? <CopyExactButton text={copy.text} testId={copy.testId} label="Copy" /> : null}
        </div>
      ) : null}
    </div>
  )
}

function SearchControls({ search, viewPrefix, label }: { search: ContentSearch; viewPrefix: string; label?: string }) {
  const { query, onQueryChange, matchCount, capped, current, step } = search
  return (
    <>
      <input
        type="search"
        value={query}
        onChange={(e) => onQueryChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault()
            step(e.shiftKey ? -1 : 1)
          }
        }}
        placeholder="Search…"
        aria-label={`${label ?? 'Content'}: search text`}
        data-testid={`${viewPrefix}-search`}
        className="h-6 w-32 rounded border border-border bg-card px-2 text-xs text-foreground placeholder:text-muted-foreground"
      />
      {query ? (
        <>
          <span aria-live="polite" className="text-[11px] font-mono text-tertiary-foreground tabular-nums" data-testid={`${viewPrefix}-search-count`}>
            {matchCount === 0 ? '0/0' : `${current + 1}/${matchCount}${capped ? '+' : ''}`}
          </span>
          <button type="button" onClick={() => step(-1)} disabled={matchCount === 0} aria-label="Previous match" className="px-1 text-xs text-muted-foreground hover:text-foreground disabled:opacity-40">↑</button>
          <button type="button" onClick={() => step(1)} disabled={matchCount === 0} aria-label="Next match" className="px-1 text-xs text-muted-foreground hover:text-foreground disabled:opacity-40">↓</button>
        </>
      ) : null}
    </>
  )
}

export interface SearchableContentViewProps {
  view: ContentViewMode
  onViewChange?: (view: ContentViewMode) => void
  /** The exact text being shown: the search re-scans when it changes. */
  text: string
  label?: string
  viewPrefix: string
  leading?: ReactNode
  formatted: ReactNode
  raw: ReactNode
  /**
   * `full` (default) renders the toolbar with toggle, search and Copy.
   * `minimal` renders only the body, for embeddings whose outer view already
   * provides the toggle, search and exact Copy.
   */
  controls?: 'full' | 'minimal'
  /** Copy source; defaults to the exact `text`. */
  copy?: { text: CopySource; testId: string }
  bodyClassName?: string
}

/**
 * Toolbar plus searchable body. Owns the search state and the highlight
 * registration; the caller owns which view is showing.
 */
export function SearchableContentView({
  view,
  onViewChange,
  text,
  label,
  viewPrefix,
  leading,
  formatted,
  raw,
  controls = 'full',
  copy,
  bodyClassName,
}: SearchableContentViewProps) {
  const full = controls === 'full'
  const { bodyRef, highlightCss, search } = useContentSearch(view, text, full)
  const showToggle = full && onViewChange !== undefined
  const showToolbar = full || leading !== undefined
  return (
    <div className="space-y-2">
      {full ? <style>{highlightCss}</style> : null}
      {showToolbar ? (
        <ContentViewToolbar
          leading={leading}
          viewPrefix={viewPrefix}
          label={label}
          view={showToggle ? view : undefined}
          onViewChange={showToggle ? onViewChange : undefined}
          search={search}
          copy={full ? (copy ?? { text, testId: `${viewPrefix}-copy` }) : null}
        />
      ) : null}
      <div ref={bodyRef} className={bodyClassName}>
        {view === 'formatted' ? formatted : raw}
      </div>
    </div>
  )
}
