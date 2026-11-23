import * as React from 'react'

import { formatDuration, formatSpanLabel } from '../../lib/labels.js'
import { fetchSpanAttributes } from '../../lib/kyberApi.js'
import { cn } from '../../lib/utils.js'

export type CostBlock = {
  basis: string
  status: string
  value?: number
  currency?: string
}

export type TimelineNode = {
  spanId: string
  parentId: string | null
  children: TimelineNode[]
  childCount: number
  startMs: number
  durationMs: number
  kind: string
  name: string
  status?: string
  attributes: Record<string, unknown>
  isSubagent: boolean
  isAuxiliary: boolean
  cost: CostBlock
}

/**
 * One level's load state, as the section's level queries answered it. The
 * tree renders it under the node whose children are loading.
 */
export type TimelineLevelState = {
  /** A page of this level is still in flight. */
  loading: boolean
  /** The level's failure in the words the route answered; null while it stands. */
  error: string | null
  canLoadMore: boolean
  loadedCount: number
  total: number
}

/** Operation colour for the duration bars and the legend, keyed by span kind. */
export const TIMELINE_OP_COLORS: Record<string, string> = {
  invoke_agent: '#3b82f6', // blue
  agent: '#3b82f6',
  chat: '#f59e0b', // amber
  execute_tool: '#10b981', // emerald
  tool: '#10b981',
  'tool.invoke': '#10b981',
  execute_hook: '#ec4899', // pink
  hook: '#ec4899',
  embeddings: '#8b5cf6', // purple
}

function CostBadge({ cost }: { cost: CostBlock }) {
  if (cost.value === undefined || cost.value === null) {
    return <span className="text-xs text-tertiary-foreground">{cost.status}</span>
  }
  const v = cost.value as number
  const cur = cost.currency ?? 'USD'
  try {
    return (
      <span className="text-xs tabular-nums">
        {new Intl.NumberFormat('en-US', { style: 'currency', currency: cur }).format(v)}
      </span>
    )
  } catch {
    return <span className="text-xs tabular-nums">${v.toFixed(2)}</span>
  }
}

type LevelControls = {
  expandedIds?: readonly string[]
  levelState?: (parent: string | null) => TimelineLevelState | undefined
  onToggleExpand?: (node: TimelineNode) => void
  onLoadMore?: (parent: string | null) => void
  onRetryLevel?: (parent: string | null) => void
}

/** The rows that state one level's progress beneath the node expanded to it. */
function LevelRows({ parent, state, onRetryLevel, onLoadMore }: {
  parent: string | null
  state: TimelineLevelState
  onRetryLevel?: (parent: string | null) => void
  onLoadMore?: (parent: string | null) => void
}) {
  if (state.loading) {
    return (
      <div data-testid="timeline-level-loading" role="status" aria-live="polite" className="px-2 py-1 text-xs text-tertiary-foreground">
        Loading…
      </div>
    )
  }
  if (state.error !== null) {
    return (
      <div data-testid="timeline-level-error" role="status" aria-live="polite" className="flex items-center gap-2 px-2 py-1 text-xs text-destructive-foreground">
        <span>{state.error}</span>
        <button type="button" data-testid="timeline-level-retry" onClick={() => onRetryLevel?.(parent)} className="underline">
          Retry
        </button>
      </div>
    )
  }
  if (state.canLoadMore) {
    return (
      <button
        type="button"
        data-testid="timeline-level-more"
        onClick={() => onLoadMore?.(parent)}
        className="text-xs text-muted-foreground underline"
      >
        Load more ({state.total - state.loadedCount} more)
      </button>
    )
  }
  if (state.loadedCount === 0) {
    return <div data-testid="timeline-level-empty" className="px-2 py-1 text-xs text-tertiary-foreground">No children.</div>
  }
  return null
}

function NodeRow({
  node,
  depth,
  onSelect,
  selectedId,
  controls,
}: {
  node: TimelineNode
  depth: number
  onSelect?: (n: TimelineNode) => void
  selectedId?: string
  controls: LevelControls
}) {
  const isSelected = node.spanId === selectedId
  const expandable = node.childCount > 0
  const expanded = expandable && (controls.expandedIds?.includes(node.spanId) ?? false)
  const level = expanded ? controls.levelState?.(node.spanId) : undefined
  return (
    <>
      <div
        data-testid="timeline-node"
        className={`flex items-center gap-2 rounded px-2 py-1.5 text-sm hover:bg-interactive-secondary ${isSelected ? 'bg-interactive-secondary ring-1 ring-border' : ''} ${node.isAuxiliary ? 'opacity-60' : ''}`}
        style={{ paddingLeft: `${depth * 16 + 8}px` }}
        onClick={() => onSelect?.(node)}
        role={onSelect ? 'button' : undefined}
        tabIndex={onSelect ? 0 : undefined}
      >
        {expandable && (
          <button
            type="button"
            data-testid="timeline-node-expand"
            aria-expanded={expanded}
            aria-label={`${expanded ? 'Collapse' : 'Expand'} ${node.name} children`}
            className="shrink-0 text-xs text-tertiary-foreground"
            onClick={(e) => {
              e.stopPropagation()
              controls.onToggleExpand?.(node)
            }}
          >
            {expanded ? '▾' : '▸'}
          </button>
        )}
        <span className="shrink-0 font-mono text-[11px] text-tertiary-foreground">{node.kind}</span>
        <span className="min-w-0 flex-1 truncate font-medium">{formatSpanLabel(node.name)}</span>
        {node.isSubagent && (
          <span className="shrink-0 rounded bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-medium text-amber-700">subagent</span>
        )}
        {expandable && !expanded && (
          <span className="shrink-0 text-[10px] text-tertiary-foreground" data-testid="timeline-node-hidden">{node.childCount} hidden</span>
        )}
        {node.isAuxiliary && (
          <span className="shrink-0 rounded bg-violet-500/15 px-1.5 py-0.5 text-[10px] font-medium text-violet-700">auxiliary</span>
        )}
        {node.status && (() => {
          const s = typeof node.status === 'string' ? node.status.toLowerCase() : ''
          if (!s) return null
          const badgeClass =
            s === 'error' || s === 'failure' || s === 'fatal'
              ? 'bg-red-500/15 text-red-700 dark:text-red-400'
              : s === 'ok' || s === 'success'
                ? 'bg-emerald-500/15 text-emerald-700 dark:text-emerald-400'
                : 'bg-muted text-muted-foreground'
          return (
            <span
              data-testid="status-badge"
              className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium ${badgeClass}`}
            >
              {node.status}
            </span>
          )
        })()}
        <span className="shrink-0 tabular-nums text-xs text-tertiary-foreground">{formatDuration(node.durationMs)}</span>
        <CostBadge cost={node.cost} />
      </div>
      {expanded && (
        <>
          {node.children.map((child) => (
            <NodeRow key={child.spanId} node={child} depth={depth + 1} onSelect={onSelect} selectedId={selectedId} controls={controls} />
          ))}
          {level && <LevelRows parent={node.spanId} state={level} onRetryLevel={controls.onRetryLevel} onLoadMore={controls.onLoadMore} />}
        </>
      )}
    </>
  )
}

export interface TimelineViewProps extends LevelControls {
  root: TimelineNode | null | undefined
  onSelectNode?: (node: TimelineNode) => void
  selectedId?: string
}

export function TimelineView({
  root,
  expandedIds,
  levelState,
  onToggleExpand,
  onLoadMore,
  onRetryLevel,
  onSelectNode,
  selectedId,
}: TimelineViewProps) {
  const [selected, setSelected] = React.useState<TimelineNode | null>(null)
  const handleSelect = (n: TimelineNode) => {
    setSelected(n)
    onSelectNode?.(n)
  }
  const shown = selectedId ? undefined : selected
  const controls: LevelControls = { expandedIds, levelState, onToggleExpand, onLoadMore, onRetryLevel }
  const rootLevel = levelState?.(null)

  // Session payloads no longer carry each node's attribute map — it is the raw
  // span payload, and embedding it made every session row a second
  // uncompressed copy of the corpus. A node that still has them inline (an
  // older payload, or a caller passing them directly) renders them as before;
  // otherwise the one span on screen is fetched from compressed storage.
  const inlineAttributes = shown?.attributes ?? {}
  const hasInlineAttributes = Object.keys(inlineAttributes).length > 0
  const shownSpanId = shown?.spanId
  const [loadedAttributes, setLoadedAttributes] = React.useState<Record<string, unknown> | null>(null)
  const [attributesError, setAttributesError] = React.useState<string | null>(null)
  const [attributesLoading, setAttributesLoading] = React.useState(false)

  React.useEffect(() => {
    setLoadedAttributes(null)
    setAttributesError(null)
    if (shownSpanId === undefined || hasInlineAttributes) {
      setAttributesLoading(false)
      return
    }
    let cancelled = false
    setAttributesLoading(true)
    fetchSpanAttributes(shownSpanId)
      .then((result) => {
        if (!cancelled) setLoadedAttributes(result.attributes)
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setAttributesError(err instanceof Error ? err.message : 'Unable to load span attributes.')
        }
      })
      .finally(() => {
        if (!cancelled) setAttributesLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [shownSpanId, hasInlineAttributes])

  const attributes = hasInlineAttributes ? inlineAttributes : loadedAttributes

  if (!root) {
    return <div className="py-8 text-center text-sm text-tertiary-foreground">No timeline data.</div>
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="rounded-md border border-border bg-card p-3">
        <h3 className="mb-2 text-xs font-semibold uppercase tracking-wider text-heading">Call tree</h3>
        <div className="flex flex-col">
          {root.children.length === 0 ? (
            <p className="py-4 text-center text-sm text-tertiary-foreground">Empty session.</p>
          ) : (
            root.children.map((child) => (
              <NodeRow key={child.spanId} node={child} depth={0} onSelect={handleSelect} selectedId={selectedId ?? selected?.spanId} controls={controls} />
            ))
          )}
        </div>
        {rootLevel && <LevelRows parent={null} state={rootLevel} onRetryLevel={onRetryLevel} onLoadMore={onLoadMore} />}
        <div className="mt-3 flex gap-2 text-xs text-tertiary-foreground">
          <span>Total: {formatDuration(root.durationMs)}</span>
          <span>·</span>
          <CostBadge cost={root.cost} />
        </div>
      </div>

      {shown && (
        <div data-testid="timeline-attributes" className="rounded-md border border-border bg-card p-4">
          <h3 className="mb-2 text-xs font-semibold uppercase tracking-wider text-heading">Attributes: {shown.name}</h3>
          {attributesLoading ? (
            <p className="text-sm text-tertiary-foreground">Loading attributes…</p>
          ) : attributesError !== null ? (
            <p className="text-sm text-destructive-foreground">{attributesError}</p>
          ) : attributes === null || Object.keys(attributes).length === 0 ? (
            <p className="text-sm text-tertiary-foreground">No attributes.</p>
          ) : (
            <pre className="overflow-auto rounded bg-interactive-secondary p-3 text-xs">{JSON.stringify(attributes, null, 2)}</pre>
          )}
          <div className="mt-2 text-xs text-tertiary-foreground">
            <div>spanId: {shown.spanId}</div>
            <div>parentId: {shown.parentId ?? '—'}</div>
          </div>
        </div>
      )}
    </div>
  )
}

/** The duration bars: every node loaded so far, scaled to the whole tree's bounds. */
export function DurationView({
  nodes,
  bounds,
  onSelectNode,
  selectedId,
}: {
  nodes: TimelineNode[]
  bounds: { startMs: number; endMs: number }
  onSelectNode?: (node: TimelineNode) => void
  selectedId?: string
}) {
  const span = Math.max(1, bounds.endMs - bounds.startMs)
  return (
    <div className="space-y-2 rounded-md border border-border bg-card p-4" data-testid="timeline-bars-view">
      <div className="max-h-[500px] space-y-1.5 overflow-x-auto">
        {nodes.map((node) => {
          const offset = node.startMs - bounds.startMs
          const leftPct = (offset / span) * 100
          const measuredDuration = typeof node.durationMs === 'number' && node.durationMs > 0
          const widthPct = measuredDuration
            ? Math.max((node.durationMs / span) * 100, 0.7)
            : 0
          const opColor = TIMELINE_OP_COLORS[node.kind] || '#64748b'
          const isSelected = selectedId === node.spanId

          return (
            <div
              key={node.spanId}
              onClick={() => onSelectNode?.(node)}
              className={cn(
                'group flex cursor-pointer items-center gap-2 rounded px-2 py-1 font-mono text-xs transition-colors',
                isSelected ? 'bg-interactive-secondary ring-1 ring-primary/40' : 'hover:bg-interactive-secondary/60',
              )}
              data-testid="timeline-bar-row"
            >
              <span
                className="shrink-0 truncate font-medium text-foreground"
                style={{ width: '240px' }}
                title={node.name}
              >
                {node.name}
              </span>

              <div className="relative h-3 flex-1 overflow-hidden rounded bg-muted/60">
                <div
                  data-testid={`timeline-bar-${node.spanId}`}
                  className="absolute bottom-0 top-0 rounded-[2px] opacity-80 transition-all group-hover:opacity-100"
                  style={{
                    left: `${leftPct}%`,
                    width: `${widthPct}%`,
                    backgroundColor: opColor,
                  }}
                  title={`${node.name} · ${formatDuration(node.durationMs)}`}
                />
              </div>

              <span className="w-16 shrink-0 text-right text-[11px] tabular-nums text-tertiary-foreground">
                {formatDuration(node.durationMs)}
              </span>
              <span className="w-24 shrink-0 truncate text-right text-[10px] tabular-nums text-tertiary-foreground">
                {node.childCount > 0 ? `${node.childCount} child${node.childCount === 1 ? '' : 'ren'}` : ''}
              </span>
            </div>
          )
        })}
      </div>
    </div>
  )
}
