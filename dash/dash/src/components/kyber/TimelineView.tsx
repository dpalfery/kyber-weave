import * as React from 'react'

import { fetchSpanAttributes } from '../../lib/kyberApi'

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
  startMs: number
  durationMs: number
  kind: string
  name: string
  attributes: Record<string, unknown>
  isSubagent: boolean
  isAuxiliary: boolean
  cost: CostBlock
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

function NodeRow({
  node,
  depth,
  onSelect,
  selectedId,
}: {
  node: TimelineNode
  depth: number
  onSelect?: (n: TimelineNode) => void
  selectedId?: string
}) {
  const isSelected = node.spanId === selectedId
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
        <span className="shrink-0 font-mono text-[11px] text-tertiary-foreground">{node.kind}</span>
        <span className="min-w-0 flex-1 truncate font-medium">{node.name}</span>
        {node.isSubagent && (
          <span className="shrink-0 rounded bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-medium text-amber-700">subagent</span>
        )}
        {node.isAuxiliary && (
          <span className="shrink-0 rounded bg-violet-500/15 px-1.5 py-0.5 text-[10px] font-medium text-violet-700">auxiliary</span>
        )}
        <span className="shrink-0 tabular-nums text-xs text-tertiary-foreground">{node.durationMs}ms</span>
        <CostBadge cost={node.cost} />
      </div>
      {node.children.map((child) => (
        <NodeRow key={child.spanId} node={child} depth={depth + 1} onSelect={onSelect} selectedId={selectedId} />
      ))}
    </>
  )
}

export function TimelineView({
  root,
  onSelectNode,
  selectedId,
}: {
  root: TimelineNode | null | undefined
  onSelectNode?: (node: TimelineNode) => void
  selectedId?: string
}) {
  const [selected, setSelected] = React.useState<TimelineNode | null>(null)
  const handleSelect = (n: TimelineNode) => {
    setSelected(n)
    onSelectNode?.(n)
  }
  const shown = selectedId ? undefined : selected

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
              <NodeRow key={child.spanId} node={child} depth={0} onSelect={handleSelect} selectedId={selectedId ?? selected?.spanId} />
            ))
          )}
        </div>
        <div className="mt-3 flex gap-2 text-xs text-tertiary-foreground">
          <span>Total: {root.durationMs}ms</span>
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
