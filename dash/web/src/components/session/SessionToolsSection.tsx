import { cn, usd, fmtTokens } from '../../lib/utils.js'
import { SchemaCostRanking, type SchemaCostAnalysis, type SchemaCostToolRow } from '../SchemaCostRanking.js'
import { SectionLoading } from './SectionLoading.js'
import { SectionError } from './SectionError.js'
import { formatCredits } from './SessionSummarySection.js'
import { sectionErrorMessage, useSectionPage } from './useSectionPage.js'
import { fetchKyberSessionTools } from '../../lib/kyberApi.js'
import { TOOL_DEFINITION_UNAVAILABLE_KEY, type OpenInspector, type SessionSummaryPayload } from './types.js'

/**
 * Copilot CLI journals carry no tool schemas at all. Its system prompt's
 * `<tools>` section is usage guidance, so the notice says so rather than
 * presenting that section as the offered tool set.
 */
const COPILOT_TOOL_DEFINITIONS_REASON =
  "Copilot CLI session journals carry no tool schemas; the system prompt's <tools> section is usage guidance, not the offered tool set."

function toolDefinitionsUnavailableReason(harness: string | null | undefined): string {
  if (harness === 'copilot' || harness === 'copilot-cli') return COPILOT_TOOL_DEFINITIONS_REASON
  return `${harness || 'This harness'} does not export tool definitions for this session, so only the invocation row is shown.`
}

/**
 * The ranked tool rows, one page at a time, with the aggregate the engine
 * priced over every row. The ranking table and its server rollup are the
 * existing views; the section feeds them the rows and the aggregate, and a
 * tool row click opens the inspector on the span the aggregate named as
 * carrying the definitions — or states honestly that the harness exported no
 * definitions and issues no content request at all.
 */
export interface SessionToolsSectionProps {
  sessionId: string
  /** The summary's harness name, for the notice a definitions-less drawer states. */
  harness?: string | null
  /** The summary record, whose offered/invoked tool lists and waste figures the banners read. */
  summaryRecord?: SessionSummaryPayload | null
  openInspector: OpenInspector
  className?: string
}

export function SessionToolsSection({
  sessionId,
  harness,
  summaryRecord,
  openInspector,
  className,
}: SessionToolsSectionProps) {
  const tools = useSectionPage(sessionId, 'tools', fetchKyberSessionTools)
  const data = tools.data

  if (tools.isError && data === undefined) {
    return (
      <SectionError
        testId="section-error-tools"
        label="Tool ranking"
        message={sectionErrorMessage(tools.error)}
        onRetry={tools.refetch}
        className={className}
      />
    )
  }

  if (data === undefined) {
    return <SectionLoading testId="session-tools-loading" label="tool ranking" />
  }

  const rows = data.entries
  const aggregate = data.aggregate
  const u = summaryRecord ?? {}

  // The engine's verdicts, read from the aggregate it priced over every row
  // before paging — never from which rows happen to be on the page. An empty
  // page whose aggregate names no definitions span is the engine's own "not
  // measurable" verdict — the route answers no rows at all when no tool
  // figures exist. Invocation-only rows (calls measured, no definitions)
  // render as the plain ranking, exactly as they always have.
  const measurable = aggregate.maxCost != null || aggregate.definitionsSpanId != null
  const schema: SchemaCostAnalysis | undefined =
    !measurable && rows.length === 0 ? { measurable: false, invocationCount: aggregate.invocationCount } : undefined

  const offeredList = Array.isArray(u.tools_offered) ? u.tools_offered : undefined
  const invokedList = Array.isArray(u.tools_invoked) ? u.tools_invoked : undefined
  const toolsOfferedCount = offeredList !== undefined
    ? offeredList.length
    : (typeof u.tools_offered === 'number' ? u.tools_offered : undefined)
  const toolsInvokedCount = invokedList !== undefined
    ? invokedList.length
    : (typeof u.tools_invoked === 'number' ? u.tools_invoked : undefined)
  const invokedSet = invokedList !== undefined ? new Set(invokedList) : null
  const unusedOfferedCount = toolsOfferedCount != null && toolsInvokedCount != null
    ? (offeredList !== undefined && invokedSet !== null
        ? offeredList.filter((name) => !invokedSet.has(name)).length
        : Math.max(0, toolsOfferedCount - toolsInvokedCount))
    : undefined

  const openToolInspector = (tool: {
    name: string
    server?: string | null
    is_mcp?: boolean
    invocations: number
    total_schema_cost?: number | null
    invoked?: boolean
    in_definitions?: boolean
  }) => {
    const title = `Tool: ${tool.name}`
    const subtitle = `Server: ${tool.server || 'built-in'}${tool.is_mcp ? ' (MCP)' : ''} · Invocations: ${tool.invocations}`
    // No span carried definitions and the summary lists no offered tools: the
    // harness exported none. Say so instead of issuing a content request that
    // can only come back empty and read as a load failure.
    if (aggregate.definitionsSpanId == null && !Array.isArray(u.tools_offered)) {
      openInspector({
        title,
        subtitle,
        content: {
          tool,
          definition: null,
          [TOOL_DEFINITION_UNAVAILABLE_KEY]: toolDefinitionsUnavailableReason(harness),
        },
      })
      return
    }

    const contentRequest = {
      sessionId,
      span: aggregate.definitionsSpanId ?? undefined,
      part: 'tool_definitions',
      toolName: tool.name,
    }
    openInspector({
      title,
      subtitle,
      content: { tool, definition: null, contentRequest },
      contentRequest,
    })
  }

  return (
    <div className={cn('space-y-3', className)} data-testid="tool-schema-section">
      <div className="flex flex-col sm:flex-row sm:items-baseline justify-between gap-1">
        <div>
          <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">
            3 · Tool &amp; Schema Cost Ranking
          </h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Ranked by resident cost (schema size tokens × turns resident). Red indicates tools offered but never called.
          </p>
        </div>
        {u.schema_waste_cost && u.schema_waste_cost.usd_low != null && (
          <span
            className="font-mono text-xs text-amber-600 dark:text-amber-400 font-semibold"
            data-testid="schema-waste-range"
          >
            Unused waste range: {usd(u.schema_waste_cost.usd_low)} – {usd(u.schema_waste_cost.usd_high)}
            {u.schema_waste_cost.credits_low != null
              ? ` (${formatCredits(u.schema_waste_cost.credits_low)} – ${formatCredits(u.schema_waste_cost.credits_high)} credits)`
              : ''}
          </span>
        )}
      </div>

      {/* Waste Callout Banner */}
      {toolsOfferedCount != null && toolsInvokedCount != null && unusedOfferedCount != null && unusedOfferedCount > 0 && (
        <div
          className="rounded-md border border-amber-500/30 bg-amber-500/10 p-3 text-xs text-foreground/90 space-y-1"
          data-testid="schema-waste-banner"
        >
          <div className="font-semibold text-amber-700 dark:text-amber-300">
            {unusedOfferedCount} of {toolsOfferedCount} tools were never called.
          </div>
          <p className="text-muted-foreground leading-relaxed">
            That represents{' '}
            <strong className="text-foreground">{fmtTokens(u.unused_schema_per_turn)} tokens/turn</strong>{' '}
            ({u.schema_tokens_per_turn ? ((u.unused_schema_per_turn || 0) / u.schema_tokens_per_turn * 100).toFixed(1) : '0'}% of the{' '}
            {fmtTokens(u.schema_tokens_per_turn)}-token schema block), resident across{' '}
            <strong className="text-foreground">{u.defs_turns ?? 0} turns</strong> — totaling{' '}
            <strong className="text-foreground">
              {fmtTokens((u.unused_schema_per_turn || 0) * (u.defs_turns || 0))} tokens
            </strong>{' '}
            in overhead for uncalled tools.
          </p>
        </div>
      )}

      {/* Non-measurable schema warning (e.g. Pi harness) */}
      {!measurable && (
        <div
          className="rounded-md border border-border bg-card p-3 text-xs text-tertiary-foreground"
          data-testid="schemas-not-exported-banner"
        >
          <span className="font-medium text-foreground">
            {harness || 'Harness'} does not export tool definitions.
          </span>{' '}
          Ranking below lists invocations only.
        </div>
      )}

      {/* Tools Ranking */}
      <SchemaCostRanking
        schema={schema}
        // The payload's tools rows carried names and total_schema_cost when available;
        // the ranking table renders them or falls back where absent.
        tools={rows as unknown as SchemaCostToolRow[]}
        // The whole-ranking figures travel in the aggregate the route priced
        // over every row before paging, so the scale, rollup, waste range and
        // counts are whole-session values with only this page loaded. The
        // offered count is the summary's own figure when it measured one.
        aggregate={{
          maxCost: aggregate.maxCost,
          unusedCount: aggregate.unusedCount,
          offeredCount: toolsOfferedCount ?? data.total,
          unusedRange: aggregate.unusedRange
            ? {
                tokenResidencies: aggregate.unusedRange.tokenResidencies,
                floor: aggregate.unusedRange.floor,
                ceiling: aggregate.unusedRange.ceiling,
                ...(aggregate.unusedRange.currency != null ? { currency: aggregate.unusedRange.currency } : {}),
              }
            : null,
          byServer: aggregate.byServer,
        }}
        pager={{
          page: tools.page,
          total: data.total,
          limit: data.limit,
          loading: tools.isFetching,
          onPageChange: tools.setPage,
        }}
        onSelectTool={openToolInspector}
      />
    </div>
  )
}
