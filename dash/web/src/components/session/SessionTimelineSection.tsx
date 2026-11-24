import { useState } from 'react'
import { useQueries } from '@tanstack/react-query'
import { cn } from '../../lib/utils.js'
import { formatDuration, formatSpanLabel } from '../../lib/labels.js'
import {
  DurationView,
  TimelineView,
  TIMELINE_OP_COLORS,
  type CostBlock,
  type TimelineLevelState,
  type TimelineNode,
} from '../analysis/TimelineView.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { PROJECTION_FAILED_STATUS, projectionFailureMessage, sectionErrorMessage } from './useSectionPage.js'
import {
  fetchKyberSessionTimeline,
  type KyberSessionSummarySection,
  type KyberSessionTimelineNode,
} from '../../lib/kyberApi.js'
import type { DrawerContent, OpenInspector, SessionSummaryPayload } from './types.js'

// The legend and the duration bars share the operation colours with the view.
export { TIMELINE_OP_COLORS }

/** The state-map key of the top level, whose parent the route names as null. */
const TOP_LEVEL = '(top level)'

/** One served timeline node adapted to the call-tree view's node shape. */
function adaptTimelineNode(node: KyberSessionTimelineNode): TimelineNode {
  const rawCost = (node.cost ?? {}) as Partial<CostBlock> & { cost_usd?: number | null }
  const cost: CostBlock = {
    basis: rawCost.basis ?? 'none',
    status: rawCost.status ?? (rawCost.value != null ? 'ok' : '—'),
    value: rawCost.value ?? rawCost.cost_usd ?? undefined,
    currency: rawCost.currency ?? 'USD',
  }

  return {
    spanId: node.spanId,
    parentId: node.parentId,
    // Children are never in the payload: each level pages on demand, and the
    // section fills them in from that level's queries when it is expanded.
    children: [],
    childCount: node.childCount,
    startMs: node.startMs,
    durationMs: node.durationMs,
    kind: node.kind,
    name: formatSpanLabel(node.name),
    status: node.status ?? undefined,
    attributes: node.attributes,
    isSubagent: node.isSubagent,
    isAuxiliary: node.isAuxiliary,
    cost,
  }
}

/** One level's pages, as they are requested: page 1 up front, more on demand. */
type LevelBucket = {
  nodes: KyberSessionTimelineNode[]
  total: number
  loading: boolean
  error: unknown
  refetches: Array<() => void>
}

/**
 * The session's execution timeline, one level at a time. The top level loads
 * at mount; expanding a node loads that node's first page of children, and
 * every level pages past its first 25 on demand. Both views — the call tree
 * and the duration bars — scale by the bounds the route answered, and a span
 * click opens the inspector on the span's own request.
 */
export interface SessionTimelineSectionProps {
  sessionId: string
  /** The summary, whose label, harness and cost stamp the tree's synthetic root. */
  summary?: KyberSessionSummarySection | null
  summaryRecord?: SessionSummaryPayload | null
  /** A navigation can ask to open a particular timeline mode. */
  initialTab?: 'tree' | 'bars'
  openInspector: OpenInspector
  className?: string
}

export function SessionTimelineSection({
  sessionId,
  summary,
  summaryRecord,
  initialTab,
  openInspector,
  className,
}: SessionTimelineSectionProps) {
  // Expansion and paging are viewer state; the queries below derive from them.
  // A session change collapses both: the previous session's expanded nodes and
  // per-level pages are not where a viewer opening another session is. Adjusted
  // during render (the React state-when-props-change pattern), so no request
  // leaves under the new session's id carrying the previous session's levels.
  const [prevSessionId, setPrevSessionId] = useState(sessionId)
  const [expandedIds, setExpandedIds] = useState<string[]>([])
  const [levelPages, setLevelPages] = useState<Record<string, number>>({})
  if (prevSessionId !== sessionId) {
    setPrevSessionId(sessionId)
    setExpandedIds([])
    setLevelPages({})
  }

  const levelRequests: Array<{ parent: string | null; page: number; levelKey: string }> = []
  const topPages = levelPages[TOP_LEVEL] ?? 1
  for (let page = 1; page <= topPages; page++) {
    levelRequests.push({ parent: null, page, levelKey: TOP_LEVEL })
  }
  for (const parentId of expandedIds) {
    const pages = levelPages[parentId] ?? 1
    for (let page = 1; page <= pages; page++) {
      levelRequests.push({ parent: parentId, page, levelKey: parentId })
    }
  }

  const levelQueries = useQueries({
    queries: levelRequests.map((request) => ({
      queryKey: ['kyber-session', sessionId, 'timeline', request.parent, request.page],
      queryFn: () => fetchKyberSessionTimeline(sessionId, { parent: request.parent, page: request.page }),
      retry: false,
    })),
  })

  const levels = new Map<string, LevelBucket>()
  levelRequests.forEach((request, index) => {
    const query = levelQueries[index]
    const bucket = levels.get(request.levelKey) ?? { nodes: [], total: 0, loading: false, error: null, refetches: [] }
    if (query.data) {
      // Pages concatenate in request order, so a level's rows keep their order.
      bucket.nodes.push(...query.data.nodes)
      bucket.total = query.data.total
    }
    if (query.isPending) bucket.loading = true
    if (query.isError) bucket.error = query.error
    bucket.refetches.push(() => void query.refetch())
    levels.set(request.levelKey, bucket)
  })

  // View mode state ('tree' vs 'bars'). Once a viewer chooses a tab, preserve
  // that choice for the rest of this session.
  const [timelineTab, setTimelineTab] = useState<'tree' | 'bars'>(initialTab ?? 'tree')
  const [timelineTabSelected, setTimelineTabSelected] = useState(false)
  const [selectedSpanId, setSelectedSpanId] = useState<string | undefined>(undefined)
  const activeTimelineTab = timelineTabSelected ? timelineTab : (initialTab ?? timelineTab)

  const childrenByParent = new Map<string, KyberSessionTimelineNode[]>()
  for (const [levelKey, bucket] of levels) {
    if (levelKey !== TOP_LEVEL) childrenByParent.set(levelKey, bucket.nodes)
  }

  const buildNode = (raw: KyberSessionTimelineNode): TimelineNode => {
    const expanded = expandedIds.includes(raw.spanId)
    return {
      ...adaptTimelineNode(raw),
      children: expanded ? (childrenByParent.get(raw.spanId) ?? []).map(buildNode) : [],
    }
  }

  const topNodes = (levels.get(TOP_LEVEL)?.nodes ?? []).map(buildNode)
  const loadedNodes: TimelineNode[] = []
  const walk = (nodes: TimelineNode[]) => {
    for (const node of nodes) {
      loadedNodes.push(node)
      walk(node.children)
    }
  }
  walk(topNodes)

  const levelState = (parent: string | null): TimelineLevelState | undefined => {
    const bucket = levels.get(parent ?? TOP_LEVEL)
    if (!bucket) return undefined
    return {
      loading: bucket.loading,
      error: bucket.error === null ? null : sectionErrorMessage(bucket.error),
      canLoadMore: bucket.total > bucket.nodes.length,
      loadedCount: bucket.nodes.length,
      total: bucket.total,
    }
  }

  const toggleExpand = (node: TimelineNode) => {
    setExpandedIds((ids) =>
      ids.includes(node.spanId) ? ids.filter((id) => id !== node.spanId) : [...ids, node.spanId],
    )
  }
  const loadMore = (parent: string | null) => {
    const key = parent ?? TOP_LEVEL
    setLevelPages((pages) => ({ ...pages, [key]: (pages[key] ?? 1) + 1 }))
  }
  const retryLevel = (parent: string | null) => {
    const bucket = levels.get(parent ?? TOP_LEVEL)
    for (const refetch of bucket?.refetches ?? []) refetch()
  }

  // requests[0] is always the top level's first page, so it carries the
  // section's own window: the bounds every view scales by.
  const rootQuery = levelQueries[0]
  const data = rootQuery?.data

  const timelineRoot: TimelineNode | null =
    data && topNodes.length > 0
      ? (() => {
          const cost = summaryRecord?.cost
          return {
            spanId: 'session-root',
            parentId: null,
            children: topNodes,
            childCount: 0,
            startMs: data.bounds.startMs,
            durationMs: Math.max(0, data.bounds.endMs - data.bounds.startMs),
            kind: 'session',
            name: summary?.label || `${summary?.harness || 'Agent'} Session`,
            attributes: {},
            isSubagent: Boolean(summary?.is_subagent),
            isAuxiliary: false,
            cost: {
              basis: cost?.basis ?? 'none',
              status: cost?.status ?? (cost?.value != null ? 'ok' : '—'),
              value: cost?.value ?? undefined,
              currency: cost?.currency ?? 'USD',
            },
          }
        })()
      : null

  const openSpanInspector = (node: { spanId: string; name?: string; kind?: string; durationMs?: number }) => {
    setSelectedSpanId(node.spanId)
    openInspector({
      title: `Span: ${node.name || node.spanId || 'Inspection'}`,
      subtitle: `${node.kind || 'span'} · ${formatDuration(node.durationMs)} · Span ID: ${node.spanId || '—'}`,
      content: node as unknown as DrawerContent,
      contentRequest: node.spanId ? { sessionId, span: node.spanId } : undefined,
    })
  }

  // A faulted projection is an error state, not an empty tree: the top level's
  // payload names the fault for the whole session, so no level renders as
  // "no spans recorded".
  if (rootQuery?.isError || data?.projection_status === PROJECTION_FAILED_STATUS) {
    return (
      <SectionError
        testId="section-error-timeline"
        label="Execution timeline"
        message={
          data?.projection_status === PROJECTION_FAILED_STATUS
            ? projectionFailureMessage(data)
            : sectionErrorMessage(rootQuery?.error)
        }
        onRetry={() => void rootQuery.refetch()}
        className={className}
      />
    )
  }

  if (rootQuery?.isPending || data === undefined) {
    return <SectionLoading testId="session-timeline-loading" label="execution timeline" />
  }

  return (
    <div className={cn('space-y-3', className)} data-testid="execution-timeline-section">
      <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2">
        <div>
          <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">
            5 · Execution Timeline &amp; Call Tree
          </h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Hierarchical span execution tree. Click any span node or timeline bar to inspect its full attributes and content.
          </p>
        </div>

        {/* View Mode Toggle */}
        <div className="flex rounded border border-border bg-interactive-secondary p-0.5 text-xs self-start sm:self-auto">
          <button
            type="button"
            onClick={() => {
              setTimelineTab('tree')
              setTimelineTabSelected(true)
            }}
            className={cn(
              'px-2.5 py-1 rounded-[4px] font-medium transition-colors',
              activeTimelineTab === 'tree'
                ? 'bg-card text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground',
            )}
            data-testid="timeline-tab-tree"
          >
            Call Tree
          </button>
          <button
            type="button"
            onClick={() => {
              setTimelineTab('bars')
              setTimelineTabSelected(true)
            }}
            className={cn(
              'px-2.5 py-1 rounded-[4px] font-medium transition-colors',
              activeTimelineTab === 'bars'
                ? 'bg-card text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground',
            )}
            data-testid="timeline-tab-bars"
          >
            Duration Timeline
          </button>
        </div>
      </div>

      {/* Legend for Operations */}
      <div className="flex flex-wrap items-center gap-3 text-xs text-tertiary-foreground">
        {Object.entries(TIMELINE_OP_COLORS).map(([op, color]) => (
          <div key={op} className="flex items-center gap-1.5">
            <span className="h-2 w-2 rounded-full shrink-0" style={{ backgroundColor: color }} />
            <span className="font-mono text-[11px]">{op}</span>
          </div>
        ))}
      </div>

      {topNodes.length === 0 ? (
        <p data-testid="timeline-empty" className="py-6 text-center text-xs text-tertiary-foreground italic">
          No timeline spans recorded.
        </p>
      ) : activeTimelineTab === 'tree' ? (
        <div data-testid="timeline-tree-view">
          <TimelineView
            root={timelineRoot}
            expandedIds={expandedIds}
            levelState={levelState}
            onToggleExpand={toggleExpand}
            onLoadMore={loadMore}
            onRetryLevel={retryLevel}
            selectedId={selectedSpanId}
            onSelectNode={(node) => openSpanInspector(node)}
          />
        </div>
      ) : (
        <DurationView
          nodes={loadedNodes}
          bounds={data.bounds}
          selectedId={selectedSpanId}
          onSelectNode={(node) => openSpanInspector(node)}
        />
      )}
    </div>
  )
}
