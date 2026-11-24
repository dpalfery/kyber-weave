import { useCallback, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { cn } from '../lib/utils.js'
import { Card } from './ui/card.js'
import { Skeleton } from './ui/skeleton.js'
import { SessionInspectorDrawer } from './SessionInspectorDrawer.js'
import { StoredFieldsPanel } from './StoredFieldsPanel.js'
import { SessionSummarySection } from './session/SessionSummarySection.js'
import { SessionChartsSection } from './session/SessionChartsSection.js'
import { SessionToolsSection } from './session/SessionToolsSection.js'
import { SessionServersSection } from './session/SessionServersSection.js'
import { SessionSkillsSection } from './session/SessionSkillsSection.js'
import { SessionTimelineSection } from './session/SessionTimelineSection.js'
import { SessionRequestsSection } from './session/SessionRequestsSection.js'
import { SessionReconciliationSection } from './session/SessionReconciliationSection.js'
import { SessionProblemsSection } from './session/SessionProblemsSection.js'
import { SessionCostSection } from './session/SessionCostSection.js'
import {
  drawerNoticeOf,
  stripNotice,
  type DrawerContent,
  type OpenInspectorRequest,
  type SessionSummaryPayload,
} from './session/types.js'
import type { DrawerContentRequest } from './SessionInspectorDrawer.js'
import {
  fetchKyberSessionSummary,
  kyberSessionSectionQueryKey,
} from '../lib/kyberApi.js'

// The shapes the drawer and the charts still read under their payload-era
// names; they live in the session module now and no import site has to move.
export type {
  AgentSessionPayload,
  DrawerContent,
  ReconciliationRow,
  ServerRow,
  SessionRequestItem,
  SessionSummaryCost,
  SessionSummaryPayload,
  SessionTimelineNode,
  SessionWasteCost,
  ToolRow,
} from './session/types.js'

/**
 * The session view, sectioned: one request per section, issued at mount in
 * page order, each section rendering its own states. The first section reads
 * the bounded summary through the client; every other section owns its own
 * query, so one slow route never blanks the page, and no content route is
 * requested until something is clicked.
 */
export interface AgentSessionLoaderProps {
  sessionId: string
  onSelectSession?: (sessionId: string) => void
  initialTimelineTab?: 'tree' | 'bars'
  className?: string
}

export function AgentSessionLoader({
  sessionId,
  onSelectSession,
  initialTimelineTab,
  className,
}: AgentSessionLoaderProps) {
  return (
    <AgentSessionContent
      sessionId={sessionId}
      onSelectSession={onSelectSession}
      initialTimelineTab={initialTimelineTab}
      className={className}
    />
  )
}

export interface AgentSessionContentProps {
  sessionId: string | null
  onSelectSession?: (sessionId: string) => void
  initialTimelineTab?: 'tree' | 'bars'
  className?: string
}

export function AgentSessionContent({
  sessionId,
  onSelectSession,
  initialTimelineTab,
  className,
}: AgentSessionContentProps) {
  // The summary the first section fetches, re-read here for the sections
  // that need the harness name or the cost figures. The key is the section's
  // own, so this observer never issues a second request.
  const summaryQuery = useQuery({
    queryKey: kyberSessionSectionQueryKey(sessionId ?? 'none', 'summary'),
    queryFn: () => fetchKyberSessionSummary(sessionId ?? 'none'),
    enabled: sessionId !== null,
  })
  const summary = summaryQuery.data ?? null
  const summaryLoading = sessionId !== null && summaryQuery.isPending
  const summaryError = summaryQuery.isError
    ? summaryQuery.error instanceof Error
      ? summaryQuery.error.message
      : String(summaryQuery.error)
    : null

  // The one piece of state the whole page shares: the inspector drawer. Each
  // section computes its own request from the data it holds and hands it to
  // this one primitive; nothing else crosses a section boundary.
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [drawerTitle, setDrawerTitle] = useState('')
  const [drawerSubtitle, setDrawerSubtitle] = useState<string | undefined>(undefined)
  const [drawerContent, setDrawerContent] = useState<DrawerContent>(null)
  const [drawerContentRequest, setDrawerContentRequest] = useState<DrawerContentRequest | undefined>(undefined)

  const openInspector = useCallback((request: OpenInspectorRequest) => {
    setDrawerTitle(request.title)
    setDrawerSubtitle(request.subtitle)
    setDrawerContent(request.content)
    setDrawerContentRequest(request.contentRequest)
    setDrawerOpen(true)
  }, [])

  const drawerNotice = drawerNoticeOf(drawerContent)
  // The summary's own record, narrowed once for the sections that read its
  // cost and tool figures.
  const summaryRecord = (summary?.summary ?? null) as SessionSummaryPayload | null

  if (!sessionId) {
    return (
      <div
        className={cn('flex flex-col items-center justify-center p-12 text-center', className)}
        data-testid="agent-dashboard-empty"
      >
        <p className="text-sm font-medium text-muted-foreground">No agent session selected</p>
        <p className="mt-1 text-xs text-tertiary-foreground">
          Select a session from the list above or provide a valid sessionId.
        </p>
      </div>
    )
  }

  return (
    <div
      className={cn('flex flex-col gap-6 p-4 sm:p-6 text-foreground', className)}
      data-testid="agent-session-dashboard"
    >
      {/* 1 · Header, banners and overview — from /summary alone. */}
      <SessionSummarySection sessionId={sessionId} onSelectSession={onSelectSession} />

      {/* 2 · Spend and context composition charts. */}
      <SessionChartsSection sessionId={sessionId} openInspector={openInspector} />

      {/* 3 · Tool and schema cost ranking, with the per-server rollup. */}
      <SessionToolsSection
        sessionId={sessionId}
        harness={summary?.harness}
        summaryRecord={summaryRecord}
        openInspector={openInspector}
      />
      <SessionServersSection sessionId={sessionId} />

      {/* 4 · Skills. */}
      <SessionSkillsSection
        sessionId={sessionId}
        summaryRecord={summaryRecord}
        openInspector={openInspector}
      />

      {/* 5 · Execution timeline and call tree (top level; deeper levels page on expand). */}
      <SessionTimelineSection
        sessionId={sessionId}
        summary={summary ?? null}
        summaryRecord={summaryRecord}
        initialTab={initialTimelineTab}
        openInspector={openInspector}
      />

      {/* Session cost and token accounting, from the summary the page read. */}
      <SessionCostSection
        summary={summary}
        summaryRecord={summaryRecord}
        loading={summaryLoading}
        error={summaryError}
        onRetry={() => void summaryQuery.refetch()}
      />

      {/* The session's user requests, paged. */}
      <SessionRequestsSection sessionId={sessionId} />

      {/* The reconciliation rows, paged. */}
      <SessionReconciliationSection sessionId={sessionId} />

      {/* The stored ingest problems, paged. */}
      <SessionProblemsSection sessionId={sessionId} />

      {/* Stored fields: what the relational store persisted for this session. */}
      <div className="space-y-2" data-testid="stored-fields-section">
        <StoredFieldsPanel sessionKey={sessionId} />
      </div>

      {/* Slide-out Inspector Drawer. */}
      <SessionInspectorDrawer
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        title={drawerTitle}
        subtitle={drawerSubtitle}
        rawContent={drawerNotice === undefined ? drawerContent : stripNotice(drawerContent)}
        contentRequest={drawerContentRequest}
      >
        {drawerNotice !== undefined && (
          <p
            className="rounded border border-border bg-card px-3 py-2 text-xs text-tertiary-foreground"
            data-testid="tool-definition-unavailable"
          >
            {drawerNotice}
          </p>
        )}
      </SessionInspectorDrawer>
    </div>
  )
}

export interface AgentSessionDashboardProps {
  sessionId?: string
  error?: Error | string | null
  isLoading?: boolean
  onSelectSession?: (sessionId: string) => void
  initialTimelineTab?: 'tree' | 'bars'
  className?: string
}

export function AgentSessionDashboard(props: AgentSessionDashboardProps) {
  if (props.error) {
    return (
      <Card
        className={cn('m-6 border-red-500/30 bg-red-500/10 p-6', props.className)}
        data-testid="agent-dashboard-error"
      >
        <div className="flex items-start gap-3">
          <span className="text-xl" role="img" aria-label="error">
            ⚠️
          </span>
          <div className="space-y-2">
            <h3 className="font-semibold text-red-600 dark:text-red-400">Failed to load session</h3>
            <p className="text-xs text-foreground/80">
              {props.error instanceof Error ? props.error.message : String(props.error)}
            </p>
          </div>
        </div>
      </Card>
    )
  }

  if (props.isLoading) {
    return (
      <div className={cn('flex flex-col gap-6 p-6', props.className)} data-testid="agent-dashboard-loading">
        <div className="flex items-center gap-3">
          <Skeleton className="h-7 w-28 rounded-full" />
          <Skeleton className="h-6 w-48 rounded" />
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 md:grid-cols-6 gap-3">
          {Array.from({ length: 12 }).map((_, i) => (
            <Skeleton key={i} className="h-20 rounded-lg" />
          ))}
        </div>
        <Skeleton className="h-72 rounded-lg" />
        <Skeleton className="h-64 rounded-lg" />
      </div>
    )
  }

  if (props.sessionId) {
    return (
      <AgentSessionLoader
        sessionId={props.sessionId}
        onSelectSession={props.onSelectSession}
        initialTimelineTab={props.initialTimelineTab}
        className={props.className}
      />
    )
  }

  return (
    <AgentSessionContent
      sessionId={null}
      onSelectSession={props.onSelectSession}
      initialTimelineTab={props.initialTimelineTab}
      className={props.className}
    />
  )
}

export default AgentSessionDashboard
