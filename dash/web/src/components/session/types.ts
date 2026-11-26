import type {
  KyberContextTurn,
  KyberSectionPageLimit,
  KyberSectionPageOrder,
  KyberSessionContext,
  KyberSessionTurnRow,
  KyberSkillRow,
  KyberToolDefinitionShadow,
  KyberUnmeasurable,
} from '../../lib/kyberApi.js'
import type { DrawerContentRequest } from '../SessionInspectorDrawer.js'
import type { CostBlock } from '../analysis/TimelineView.js'

export type { KyberSectionPageLimit, KyberSectionPageOrder }
export type SectionPageLimit = KyberSectionPageLimit
export type SectionPageOrder = KyberSectionPageOrder

/**
 * The session view's shared shapes. They were the payload-era dashboard's own
 * types; the sectioned view keeps them for the components and drawers that
 * still read a whole row (the inspector's dispatch is over these shapes), and
 * the dashboard re-exports them so no import site has to move.
 */

export interface SessionSummaryCost {
  value?: number | null
  currency?: string | null
  usd?: number | null
  credits?: number | null
  basis?: 'published_rates' | 'harness_reported' | string
  status?: string
  priced_turns?: number
  unpriced_turns?: number
  by_model?: Array<{
    model: string
    turns: number
    input: number
    output: number
    credits?: number | null
    usd?: number | null
    status?: string
  }>
}

export interface SessionWasteCost {
  usd_low?: number | null
  usd_high?: number | null
  credits_low?: number | null
  credits_high?: number | null
}

export interface SessionSummaryPayload {
  turn_count?: number | null
  reported_turn_count?: number | null
  request_count?: number | null
  total_input?: number | null
  total_output?: number | null
  total_cache_read?: number | null
  cache_hit_ratio?: number | null
  total_cache_creation?: number | null
  cache_creation_coverage?: number | null
  total_reasoning?: number | null
  duration_ms?: number | null
  models?: string[]
  tool_calls?: number | null
  tools_invoked?: string[] | number | null
  tools_offered?: string[] | number | KyberUnmeasurable | null
  /** Skill names listed to the agent; absent when not measured. */
  skills_listed?: string[] | null
  /** Skill names invoked at least once; absent when not measured. */
  skills_invoked?: string[] | null
  error_count?: number | null
  median_ttft_ms?: number | null
  aux_chat_calls?: number | null
  aux_models?: string[]
  aux_input?: number | null
  aux_output?: number | null
  unused_schema_per_turn?: number | null
  schema_tokens_per_turn?: number | null
  defs_turns?: number | null
  schema_waste_cost?: SessionWasteCost | null
  cost?: SessionSummaryCost | null
  /** Harness-reported cost. A priced value, including zero, is the figure the harness sent. */
  cost_reported?: SessionSummaryCost | null
  span_count?: number | null
}

export interface ReconciliationRow {
  request: string
  root_input?: number | null
  sum_chat_input?: number | null
  input_match: boolean
  root_output?: number | null
  sum_chat_output?: number | null
  output_match: boolean
}

export interface ToolRow {
  // The served tools rows carry no name today (AsadTool); the table labels
  // from schema ranking where it can.
  name?: string
  server?: string
  is_mcp?: boolean
  schema_tokens?: number | null
  turns_resident?: number | null
  total_schema_cost?: number | null
  invocations: number
  cost_per_invocation?: number | null
  result_tokens?: number | null
  in_definitions?: boolean
  results_not_recorded?: number | null
  /** The invocation's arguments, as the engine previewed them. Absent when not carried. */
  arguments_preview?: string | null
  /** The invocation's result, as the engine previewed them. Absent when not carried. */
  result_preview?: string | null
}

export interface ServerRow {
  server: string
  is_mcp?: boolean
  tools: number
  schema_tokens?: number | null
  total_schema_cost?: number | null
  invocations: number
  unused_tools: number
  unused_cost: number
}

export interface SessionTimelineNode {
  spanId: string
  id?: string
  traceId?: string
  parentId?: string | null
  name: string
  op?: string
  kind?: string
  durationMs?: number
  offsetMs?: number
  startMs?: number
  status?: string
  input?: number | null
  output?: number | null
  tool?: string | null
  content?: Record<string, unknown>
  attributes?: Record<string, unknown>
  raw_attributes?: Record<string, unknown>
  cost?: CostBlock | Record<string, unknown>
  cost_usd?: number | null
  isSubagent?: boolean
  isAuxiliary?: boolean
  children?: SessionTimelineNode[]
}

export interface SessionRequestItem {
  /** The request's span id. Identity only; it is never the request's label. */
  request: string
  /**
   * Preview of the user's prompt, at most 200 characters. Null or a not_measurable object when
   * the prompt was not recorded (content capture off); the label falls back to the request's position.
   */
  prompt?: string | KyberUnmeasurable | null
  timestamp?: string
  turns?: number
  model?: string
}

export interface AgentSessionPayload {
  id?: string
  session_id?: string
  harness?: string
  label?: string | null
  traceId?: string | null
  repo?: string | null
  branch?: string | null
  commit?: string | null
  agent_type?: string | null
  agent_name?: string | null
  is_subagent?: boolean
  parent_session?: string | null
  span_count?: number
  summary?: SessionSummaryPayload
  turns?: KyberSessionTurnRow[]
  /** Absent on a payload that predates the skills projection. */
  skills?: KyberSkillRow[] | Record<string, unknown> | null
  tools?: ToolRow[]
  servers?: ServerRow[]
  context?: KyberSessionContext
  coverage?: Record<string, number>
  notes?: string[]
  timeline?: SessionTimelineNode[]
  reconciliation?: ReconciliationRow[]
  requests?: SessionRequestItem[]
}

/**
 * What the inspector drawer is currently showing. The drawer's panes
 * runtime-dispatch on the value's shape (see `InspectorContent`), so the
 * union names the producers rather than gating the consumers: a turn row, a
 * payload timeline node, the light span reference the timeline view hands
 * back on click, a tool with its definition row, or one context-bucket
 * drill-down.
 */
export type DrawerContent =
  | KyberSessionTurnRow
  | SessionTimelineNode
  | { spanId?: string; name?: string; kind?: string; op?: string; durationMs?: number }
  | {
      tool: ToolRow
      definition: KyberToolDefinitionShadow | null
      contentRequest: { span?: string; part: string; toolName?: string }
    }
  | { skill: KyberSkillRow }
  | string
  // One context-bucket drill-down's key pair (D9): the drawer fetches the
  // turn widened with its context and derives figures and content itself.
  | {
      turnIndex: number
      bucket: string
    }
  // A whole-turn open's key: the drawer fetches the row and its lossless
  // content itself (D9); no turn row rides the payload.
  | {
      turnIndex: number
    }
  // Anything else InspectorContent's shape dispatch can render — tests hand
  // the drawer bare records, and the runtime dispatch is the contract.
  | Record<string, unknown>
  | null

/** Drawer-content key carrying the "definitions not exported" notice; never rendered as data. */
export const TOOL_DEFINITION_UNAVAILABLE_KEY = 'definitionUnavailableNotice'

export function drawerNoticeOf(content: unknown): string | undefined {
  if (content === null || typeof content !== 'object') return undefined
  const notice = (content as Record<string, unknown>)[TOOL_DEFINITION_UNAVAILABLE_KEY]
  return typeof notice === 'string' ? notice : undefined
}

export function stripNotice<T>(content: T): T {
  if (content === null || typeof content !== 'object') return content
  const { [TOOL_DEFINITION_UNAVAILABLE_KEY]: _notice, ...rest } = content as Record<string, unknown>
  return rest as T
}

/**
 * The paging window one section list's rows arrived under, with the page turn
 * the pager drives. The section reads it off its route's envelope; the list
 * renders it as the shared pager.
 */
export interface PagedWindow {
  /** The one-based page these rows came from. */
  page: number
  /** How many rows the whole list holds, across every page. */
  total: number
  /** Rows per page the server answered under. */
  limit: number
  /** Available page-size options when the pager exposes a size selector. */
  limitOptions?: readonly number[]
  /** Reports a newly chosen page size. */
  onLimitChange?: (limit: KyberSectionPageLimit) => void
  /** Current sort order when the pager exposes a sort toggle. */
  order?: KyberSectionPageOrder
  /** Reports a newly chosen sort order. */
  onOrderChange?: (order: KyberSectionPageOrder) => void
  /** Custom labels for the sort toggle button (`desc` and `asc`). */
  orderLabels?: { desc: string; asc: string }
  /** True while the page being turned to is in flight. */
  loading?: boolean
  /** Accessible label for the pager navigation landmark. */
  ariaLabel?: string
  /** Reports the page the viewer navigated to. */
  onPageChange: (page: number) => void
}

/**
 * One request to open the inspector drawer, as a section computes it from its
 * own data. The content request is the section's to build: it knows the span
 * or turn its rows name.
 */
export interface OpenInspectorRequest {
  title: string
  subtitle?: string
  content: DrawerContent
  contentRequest?: DrawerContentRequest
}

export type OpenInspector = (request: OpenInspectorRequest) => void

export type { KyberContextTurn }
