import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { cn } from '../lib/utils.js'

/** Which rendering of a content body is showing. */
export type ContentViewMode = 'formatted' | 'raw'

/** Upper bound on highlighted matches per body; past it the count reads "N+". */
const MAX_SEARCH_MATCHES = 1000

/** Fallback marking attributes, set on elements that hold a match when the CSS Highlight API is absent. */
const HIT_ATTR = 'data-search-hit'
const ACTIVE_ATTR = 'data-search-active'

/** Escapes a user query so it matches literally inside a RegExp. */
function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

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
        const clipboard = typeof navigator === 'undefined' ? undefined : navigator.clipboard
        if (!clipboard || typeof clipboard.writeText !== 'function') return
        let pending: Promise<void>
        try {
          pending = Promise.resolve(clipboard.writeText(resolveCopySource(text)))
        } catch {
          return
        }
        // A rejected write (permission denied, no focus) must not surface as an
        // unhandled rejection, and must not claim success.
        pending.then(
          () => {
            setCopied(true)
            setTimeout(() => setCopied(false), 2000)
          },
          () => {},
        )
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
    const ranges: Range[] = []
    let overflow = false
    if (root && debouncedQuery) {
      // Match against the original text, never a lowercased copy: some
      // characters change length when lowercased (e.g. U+0130), which would
      // shift every offset that followed. A case-insensitive RegExp keeps the
      // offsets in the same string the Range is set on.
      const pattern = new RegExp(escapeRegExp(debouncedQuery), 'gi')
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT)
      scan: for (let n = walker.nextNode(); n; n = walker.nextNode()) {
        const hay = n.nodeValue ?? ''
        pattern.lastIndex = 0
        for (let m = pattern.exec(hay); m !== null; m = pattern.exec(hay)) {
          if (m[0].length === 0) {
            pattern.lastIndex += 1
            continue
          }
          if (ranges.length >= MAX_SEARCH_MATCHES) {
            overflow = true
            break scan
          }
          const r = document.createRange()
          r.setStart(n, m.index)
          r.setEnd(n, m.index + m[0].length)
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
    const active = ranges[current]
    const registry = (globalThis as { CSS?: { highlights?: Map<string, unknown> } }).CSS?.highlights
    const HighlightCtor = (globalThis as { Highlight?: new (...r: Range[]) => unknown }).Highlight
    const marked = new Set<HTMLElement>()
    const activeEl = active?.startContainer.parentElement ?? null
    if (registry && HighlightCtor) {
      registry.set(matchName, new HighlightCtor(...ranges))
      if (active) registry.set(currentName, new HighlightCtor(active))
      else registry.delete(currentName)
    } else {
      // No CSS Custom Highlight API (older browsers): mark the element holding
      // each match with a data attribute, styled by highlightCss. Attributes
      // leave React-rendered text nodes untouched.
      for (const r of ranges) {
        const el = r.startContainer.parentElement
        if (el && !marked.has(el)) {
          el.setAttribute(HIT_ATTR, searchId)
          marked.add(el)
        }
      }
      if (activeEl) {
        activeEl.setAttribute(ACTIVE_ATTR, searchId)
        marked.add(activeEl)
      }
    }
    if (active) {
      // Reveal the match if it sits inside a collapsed fold, then bring it into
      // view. scrollIntoView scrolls every scrolling ancestor as needed, so the
      // match's nearest bounded box (the drawer's content scroller, a panel, or
      // the page) moves to show it.
      for (let el = activeEl; el && el !== bodyRef.current; el = el.parentElement) {
        if (el instanceof HTMLDetailsElement && !el.open) el.open = true
      }
      activeEl?.scrollIntoView?.({ block: 'center', inline: 'nearest' })
    }
    return () => {
      registry?.delete(matchName)
      registry?.delete(currentName)
      for (const el of marked) {
        el.removeAttribute(HIT_ATTR)
        el.removeAttribute(ACTIVE_ATTR)
      }
    }
  }, [enabled, matchCount, current, debouncedQuery, view, text, matchName, currentName, searchId])

  const step = (delta: number) => {
    if (matchCount === 0) return
    setCurrent((c) => (c + delta + matchCount) % matchCount)
  }

  // The ::highlight rules serve browsers with the CSS Custom Highlight API; the
  // attribute rules are the fallback marking for everywhere else.
  const highlightCss = [
    `::highlight(${matchName}){background-color:rgb(250 204 21 / 0.35);color:inherit}`,
    `::highlight(${currentName}){background-color:rgb(249 115 22 / 0.65);color:inherit}`,
    `[${HIT_ATTR}="${searchId}"]{background-color:rgb(250 204 21 / 0.35)}`,
    `[${ACTIVE_ATTR}="${searchId}"]{background-color:rgb(249 115 22 / 0.45);outline:2px solid rgb(249 115 22);outline-offset:1px}`,
  ].join('')

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
      className="flex items-center justify-between gap-2 flex-wrap min-w-0 max-w-full"
      role={showToggle ? 'group' : undefined}
      aria-label={showToggle ? groupLabel : undefined}
    >
      {leading ? <div className="flex items-center gap-2 min-w-0">{leading}</div> : null}
      {showRight ? (
        <div className="flex flex-wrap items-center justify-end gap-1.5 min-w-0 max-w-full ml-auto">
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
        className="h-6 w-28 min-w-[5rem] shrink rounded border border-border bg-card px-2 text-xs text-foreground placeholder:text-muted-foreground"
      />
      {/* Always rendered, so the position of the count never depends on the query. */}
      <span
        aria-live="polite"
        className="shrink-0 whitespace-nowrap min-w-[3rem] text-right text-[11px] font-mono text-foreground tabular-nums"
        data-testid={`${viewPrefix}-search-count`}
      >
        {matchCount === 0 ? '0/0' : `${current + 1}/${matchCount}${capped ? '+' : ''}`}
      </span>
      <button type="button" onClick={() => step(-1)} disabled={matchCount === 0} aria-label="Previous match" className="shrink-0 px-1 text-xs text-muted-foreground hover:text-foreground disabled:opacity-40">↑</button>
      <button type="button" onClick={() => step(1)} disabled={matchCount === 0} aria-label="Next match" className="shrink-0 px-1 text-xs text-muted-foreground hover:text-foreground disabled:opacity-40">↓</button>
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
