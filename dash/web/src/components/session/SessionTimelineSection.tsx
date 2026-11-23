import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { cn } from '../../lib/utils.js'
import { formatDuration, formatSpanLabel } from '../../lib/labels.js'
import { Card } from '../ui/card.js'
import { TimelineView, type CostBlock, type TimelineNode } from '../analysis/TimelineView.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { sectionErrorMessage } from './useSectionPage.js'
import {
  fetchKyberSessionTimeline,
  kyberSessionSectionQueryKey,
  type KyberSessionSummarySection,
  type KyberSessionTimelineNode,
} from '../../lib/kyberApi.js'
import type { DrawerContent, OpenInspector, SessionSummaryPayload } from './types.js'

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
    // Deeper levels page on expand (T11–T13); the top level renders as leaves.
    children: [],
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

/**
 * The session's execution timeline, one level at a time. T10 pins the top
 * level; deeper levels page on expand in T11–T13. Both views — the call tree
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
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: kyberSessionSectionQueryKey(sessionId, 'timeline'),
    queryFn: () => fetchKyberSessionTimeline(sessionId, { parent: null, page: 1 }),
  })

  // View mode state ('tree' vs 'bars'). Once a viewer chooses a tab, preserve
  // that choice for the rest of this session.
  const [timelineTab, setTimelineTab] = useState<'tree' | 'bars'>(initialTab ?? 'tree')
  const [timelineTabSelected, setTimelineTabSelected] = useState(false)
  const [selectedSpanId, setSelectedSpanId] = useState<string | undefined>(undefined)
  const activeTimelineTab = timelineTabSelected ? timelineTab : (initialTab ?? timelineTab)

  const timelineRoot: TimelineNode | null = useMemo(() => {
    if (!data || data.nodes.length === 0) return null
    const cost = summaryRecord?.cost
    return {
      spanId: 'session-root',
      parentId: null,
      children: data.nodes.map(adaptTimelineNode),
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
  }, [data, summary, summaryRecord])

  const flattened = useMemo(() => data?.nodes ?? [], [data])
  // The bars scale by the whole tree's bounds, as the route answered them.
  const maxTimelineSpan = useMemo(() => {
    if (!data) return 1
    return Math.max(1, data.bounds.endMs - data.bounds.startMs)
  }, [data])

  const openSpanInspector = (node: { spanId: string; name?: string; kind?: string; durationMs?: number }) => {
    setSelectedSpanId(node.spanId)
    openInspector({
      title: `Span: ${node.name || node.spanId || 'Inspection'}`,
      subtitle: `${node.kind || 'span'} · ${formatDuration(node.durationMs)} · Span ID: ${node.spanId || '—'}`,
      content: node as unknown as DrawerContent,
      contentRequest: node.spanId ? { sessionId, span: node.spanId } : undefined,
    })
  }

  if (isError) {
    return (
      <SectionError
        testId="section-error-timeline"
        label="Execution timeline"
        message={sectionErrorMessage(error)}
        onRetry={() => void refetch()}
        className={className}
      />
    )
  }

  if (isPending || data === undefined) {
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

      {data.nodes.length === 0 ? (
        <p data-testid="timeline-empty" className="py-6 text-center text-xs text-tertiary-foreground italic">
          No timeline spans recorded.
        </p>
      ) : activeTimelineTab === 'tree' ? (
        <div data-testid="timeline-tree-view">
          <TimelineView
            root={timelineRoot}
            selectedId={selectedSpanId}
            onSelectNode={(node) => openSpanInspector(node)}
          />
        </div>
      ) : (
        /* Duration Bars Timeline View */
        <Card className="p-4 space-y-2 border border-border" data-testid="timeline-bars-view">
          <div className="space-y-1.5 overflow-x-auto max-h-[500px]">
            {flattened.map((node) => {
              const offset = node.startMs - data.bounds.startMs
              const leftPct = (offset / maxTimelineSpan) * 100
              const measuredDuration = typeof node.durationMs === 'number' && node.durationMs > 0
              const widthPct = measuredDuration
                ? Math.max((node.durationMs / maxTimelineSpan) * 100, 0.7)
                : 0
              const opKey = node.kind || ''
              const opColor = TIMELINE_OP_COLORS[opKey] || '#64748b'
              const isSelected = selectedSpanId === node.spanId

              return (
                <div
                  key={node.spanId}
                  onClick={() => openSpanInspector(node)}
                  className={cn(
                    'group flex items-center gap-2 rounded px-2 py-1 text-xs font-mono cursor-pointer transition-colors',
                    isSelected
                      ? 'bg-interactive-secondary ring-1 ring-primary/40'
                      : 'hover:bg-interactive-secondary/60',
                  )}
                  data-testid="timeline-bar-row"
                >
                  {/* Name with depth indent */}
                  <span
                    className="truncate text-foreground font-medium shrink-0"
                    style={{ width: '240px' }}
                    title={formatSpanLabel(node.name)}
                  >
                    {formatSpanLabel(node.name)}
                  </span>

                  {/* Horizontal Duration Bar Track */}
                  <div className="relative flex-1 h-3 rounded bg-muted/60 overflow-hidden">
                    <div
                      data-testid={`timeline-bar-${node.spanId}`}
                      className="absolute top-0 bottom-0 rounded-[2px] transition-all opacity-80 group-hover:opacity-100"
                      style={{
                        left: `${leftPct}%`,
                        width: `${widthPct}%`,
                        backgroundColor: opColor,
                      }}
                      title={`${formatSpanLabel(node.name)} · ${formatDuration(node.durationMs)}`}
                    />
                  </div>

                  {/* Metadata */}
                  <span className="w-16 text-right tabular-nums text-tertiary-foreground text-[11px] shrink-0">
                    {formatDuration(node.durationMs)}
                  </span>
                  <span className="w-24 text-right tabular-nums text-tertiary-foreground text-[10px] truncate shrink-0">
                    {node.childCount > 0 ? `${node.childCount} child${node.childCount === 1 ? '' : 'ren'}` : ''}
                  </span>
                </div>
              )
            })}
          </div>
        </Card>
      )}
    </div>
  )
}
