import { describe, it, expect, vi } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  AgentSessionDashboard,
  AgentSessionContent,
  AgentSessionLoader,
  type AgentSessionPayload,
  type DrawerContent,
  formatDuration,
  formatCredits,
} from './AgentSessionDashboard.js'
import { findContextTurn, findTurnByTransport } from '../lib/kyberApi.js'
import { SessionInspectorDrawer } from './SessionInspectorDrawer.js'
import type { TimelineNode } from './analysis/TimelineView.js'
import type { KyberSessionContext } from '../lib/kyberApi.js'

// Set up React 19 test hook dispatcher so components using hooks can be rendered in tests
type ReactInternals = {
  __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: {
    H?: Record<string, unknown>
  }
}
const internalsOf = (): ReactInternals['__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE'] =>
  (React as unknown as ReactInternals).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE

const reactInternals = internalsOf()

if (reactInternals) {
  reactInternals.H = {
    useState: <T,>(v: T | (() => T)) => [typeof v === 'function' ? (v as () => T)() : v, () => {}],
    useMemo: <T,>(fn: () => T) => fn(),
    useCallback: <T,>(fn: T) => fn,
    useRef: <T,>(v: T) => ({ current: v }),
    useEffect: () => {},
    useLayoutEffect: () => {},
    useId: () => 'test-id',
  }
}

function renderHtml(element: React.ReactElement | null | undefined): string {
  if (element == null) return ''
  return renderToStaticMarkup(element)
}

function findElementByTestId(node: unknown, testId: string): React.ReactElement | null {
  if (node == null) return null
  if (Array.isArray(node)) {
    for (const child of node) {
      const found = findElementByTestId(child, testId)
      if (found) return found
    }
    return null
  }
  if (React.isValidElement(node)) {
    const props = node.props as Record<string, unknown>
    if (props && props['data-testid'] === testId) {
      return node
    }
    const type = node.type as unknown
    if (typeof type === 'function') {
      try {
        const rendered = (type as (p: unknown) => unknown)(node.props)
        const found = findElementByTestId(rendered, testId)
        if (found) return found
      } catch {
        // ignore
      }
    }
    if (props && props.children) {
      const found = findElementByTestId(props.children, testId)
      if (found) return found
    }
  }
  return null
}

// ---------------------------------------------------------------------------
// Test Fixtures
// ---------------------------------------------------------------------------

const sampleSession: AgentSessionPayload = {
  id: 'sess-abc-123',
  session_id: 'sess-abc-123',
  harness: 'copilot',
  label: 'Fix parser AST recursion',
  repo: 'github.com/my-org/kyber-repo',
  branch: 'feature/ast-fix',
  agent_name: 'coder-bot',
  span_count: 48,
  summary: {
    turn_count: 5,
    reported_turn_count: 5,
    request_count: 2,
    total_input: 120500,
    total_output: 6400,
    total_cache_read: 75000,
    total_cache_creation: 18000,
    cache_creation_coverage: 2,
    cache_hit_ratio: 0.622,
    total_reasoning: 1500,
    duration_ms: 38400,
    models: ['claude-3-5-sonnet'],
    tool_calls: 8,
    tools_invoked: 2,
    tools_offered: 3,
    median_ttft_ms: 540,
    unused_schema_per_turn: 900,
    schema_tokens_per_turn: 2800,
    defs_turns: 5,
    schema_waste_cost: {
      usd_low: 0.03,
      usd_high: 0.12,
      credits_low: 0.15,
      credits_high: 0.6,
    },
    cost: {
      usd: 1.25,
      credits: 6.25,
      basis: 'published_rates',
      status: 'ok',
    },
  },
  notes: [
    'Harness does not export raw workspace info files.',
    'Tokens computed with o200k_base tokenizer.',
  ],
  requests: [
    { request: 'Analyze the recursion bug in ast.ts', turns: 3, model: 'claude-3-5-sonnet' },
    { request: 'Apply the patch and verify tests pass', turns: 2, model: 'claude-3-5-sonnet' },
  ],
  reconciliation: [
    {
      request: 'Analyze the recursion bug in ast.ts',
      root_input: 70000,
      sum_chat_input: 70000,
      input_match: true,
      root_output: 3200,
      sum_chat_output: 3200,
      output_match: true,
    },
    {
      request: 'Apply the patch and verify tests pass',
      root_input: 50500,
      sum_chat_input: 50500,
      input_match: true,
      root_output: 3200,
      sum_chat_output: 3200,
      output_match: true,
    },
  ],
  turns: [
    {
      // 0-based payload `index`, matching the served contract (issue #184).
      index: 0,
      spanId: 'turn-span-1',
      model: 'claude-3-5-sonnet',
      durationMs: 4200,
      fresh: 27500,
      cache_read: 0,
      cache_creation: 18000,
      output: 1200,
      has_tool_defs: true,
      content: {
        tool_definitions: [
          { name: 'read_file', description: 'Read file contents', parameters: { path: { type: 'string' } } },
          { name: 'write_file', description: 'Write file contents', parameters: { path: { type: 'string' } } },
        ],
      },
    },
    {
      index: 1,
      spanId: 'turn-span-2',
      model: 'claude-3-5-sonnet',
      durationMs: 5100,
      fresh: 5000,
      cache_read: 42000,
      cache_creation: 0,
      output: 1800,
    },
  ],
  // The fixture carries the context fields this file exercises; the served
  // contract is the full KyberSessionContext.
  context: {
    measurable: true,
    contextLimit: 200000,
    turns: [
      {
        turn: 1,
        reported_input: 45500,
        buckets: {
          system_prompt: 4000,
          tool_definitions: 2800,
          instruction_context: 7200,
          conversation_history: 12000,
          tool_result_content: 18000,
          residual: 1500,
        },
      },
    ],
  } as unknown as KyberSessionContext,
  tools: [
    {
      name: 'read_file',
      server: 'builtin',
      is_mcp: false,
      schema_tokens: 700,
      turns_resident: 5,
      total_schema_cost: 3500,
      invocations: 5,
      cost_per_invocation: 700,
      result_tokens: 12000,
      in_definitions: true,
    },
    {
      name: 'write_file',
      server: 'builtin',
      is_mcp: false,
      schema_tokens: 1200,
      turns_resident: 5,
      total_schema_cost: 6000,
      invocations: 3,
      cost_per_invocation: 2000,
      result_tokens: 3500,
      in_definitions: true,
    },
    {
      name: 'mcp_git_status',
      server: 'git',
      is_mcp: true,
      schema_tokens: 900,
      turns_resident: 5,
      total_schema_cost: 4500,
      invocations: 0, // Unused / 0 calls
      cost_per_invocation: null,
      result_tokens: null,
      in_definitions: true,
    },
  ],
  servers: [
    {
      server: 'builtin',
      is_mcp: false,
      tools: 2,
      schema_tokens: 1900,
      total_schema_cost: 9500,
      invocations: 8,
      unused_tools: 0,
      unused_cost: 0,
    },
    {
      server: 'git',
      is_mcp: true,
      tools: 1,
      schema_tokens: 900,
      total_schema_cost: 4500,
      invocations: 0,
      unused_tools: 1,
      unused_cost: 4500,
    },
  ],
  timeline: [
    {
      spanId: 'root-span-1',
      name: 'invoke_agent',
      op: 'invoke_agent',
      kind: 'agent',
      durationMs: 38400,
      offsetMs: 0,
      status: 'Ok',
      input: 70000,
      output: 3200,
      attributes: { 'agent.name': 'coder-bot' },
      children: [
        {
          spanId: 'chat-span-1',
          name: 'chat claude-3-5-sonnet',
          op: 'chat',
          kind: 'chat',
          durationMs: 4200,
          offsetMs: 250,
          status: 'Ok',
          input: 45500,
          output: 1200,
          attributes: { model: 'claude-3-5-sonnet' },
          children: [],
        },
        {
          spanId: 'tool-span-1',
          name: 'execute_tool read_file',
          op: 'execute_tool',
          kind: 'tool',
          durationMs: 180,
          offsetMs: 4500,
          status: 'Ok',
          tool: 'read_file',
          attributes: { path: 'src/ast.ts' },
          children: [],
        },
      ],
    },
  ],
}

/**
 * Synthetic, content-free B1 ASAD wire payload. It intentionally contains
 * analysis output only: no prompt text, tool schemas, or tool-result content.
 * The dashboard must consume this server shape directly, without an adapter
 * recreating the retired intermediate rows.
 */
const b1Session = {
  id: 'b1-asad-session',
  session_id: 'b1-asad-session',
  harness: 'claude-code',
  label: 'B1 fixture session',
  agent_name: 'test-agent',
  repo: 'acme/fixture',
  branch: 'main',
  span_count: 3,
  summary: {
    turn_count: 2,
    request_count: 1,
    total_input: 4200,
    total_output: 600,
    total_cache_read: 1200,
    total_cache_creation: 300,
    duration_ms: 1800,
    models: ['claude-test'],
    cost: { basis: 'published', status: 'priced', value: 0.42, currency: 'USD' },
  },
  context: {
    measurable: true,
    contextLimit: 200000,
    turns: [
      {
        index: 0,
        buckets: {
          system_prompt: 400,
          instruction_context: 200,
          tool_definitions: 300,
          conversation_history: 1800,
          tool_result_content: 1200,
          residual: 300,
        },
        reported_input: 4200,
      },
    ],
    first: { buckets: { system_prompt: 400 }, reported_input: 4200 },
    last: { buckets: { system_prompt: 400 }, reported_input: 4200 },
  } as unknown as KyberSessionContext,
  // B1 serializes tool metadata in `schema`; tools is deliberately free of
  // content fields such as descriptions or definitions.
  tools: [{ schema_tokens: 300, invocations: 0, turns_resident: 2 }],
  schema: {
    measurable: true,
    byServer: { filesystem: 600 },
    neverInvoked: [{ name: 'read_file', server: 'filesystem', cost: 600, invoked: false }],
    unusedRange: { tokenResidencies: 600, floor: 0, ceiling: 600 },
    turns: 2,
  },
  turns: [
    { index: 0, spanId: 'b1-turn-1', model: 'claude-test', input: 4200, fresh: 2700, cache_read: 1200, cache_creation: 300, output: 600 },
  ],
  timeline: [
    {
      spanId: 'b1-turn-1',
      parentId: null,
      name: 'llm.invoke',
      kind: 'client',
      startMs: 0,
      durationMs: 1800,
      attributes: {},
      isSubagent: false,
      isAuxiliary: false,
      cost: { basis: 'published', status: 'priced', value: 0.42, currency: 'USD' },
      children: [],
    },
  ],
  requests: [{ request: 'b1-request', turns: 1, model: 'claude-test' }],
  servers: [{ server: 'filesystem', is_mcp: true, tools: 1, schema_tokens: 300, invocations: 0, unused_tools: 1, unused_cost: 600 }],
  coverage: { schema: 1, context: 1 },
  problems: [],
  reconciliation: [],
  subagents: [],
  auxiliary: [],
  measurability: {},
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('AgentSessionDashboard: Formatters and Helpers', () => {
  it('formats durations properly', () => {
    expect(formatDuration(null)).toBe('—')
    expect(formatDuration(undefined)).toBe('—')
    expect(formatDuration(450)).toBe('450ms')
    expect(formatDuration(2500)).toBe('2.5s')
    expect(formatDuration(65000)).toBe('1m 5s')
  })

  it('formats credits properly', () => {
    expect(formatCredits(null)).toBe('—')
    expect(formatCredits(undefined)).toBe('—')
    expect(formatCredits(12.345)).toBe('12.35')
    expect(formatCredits(150)).toBe('150')
  })
})

describe('AgentSessionDashboard: Assembly and Subpanels', () => {
  it('renders all six ASAD views from the content-free B1 payload', () => {
    const html = renderHtml(React.createElement(AgentSessionDashboard, { session: b1Session }))

    // 1. Session overview
    expect(html).toContain('Session Overview')
    expect(html).toContain('4.2K')
    // 2. Per-turn token spend
    expect(html).toContain('Token Spend per Turn')
    expect(html).toContain('2.7K')
    // 3. Context composition
    expect(html).toContain('Context Composition')
    expect(html).toContain('Conversation history')
    // 4. Tool/schema cost
    expect(html).toContain('read_file')
    expect(html).toContain('By MCP server')
    // 5. Execution timeline
    expect(html).toContain('Execution Timeline')
    expect(html).toContain('llm.invoke')
    // 6. Session cost/token accounting
    expect(html).toContain('Session Cost &amp; Token Accounting')
    expect(html).toContain('$0.42')

    expect(html.indexOf('Session Overview')).toBeLessThan(html.indexOf('Token Spend per Turn'))
    expect(html.indexOf('Token Spend per Turn')).toBeLessThan(html.indexOf('Context Composition'))
    expect(html.indexOf('Context Composition')).toBeLessThan(
      html.indexOf('3 · Tool &amp; Schema Cost Ranking')
    )
    expect(html.indexOf('3 · Tool &amp; Schema Cost Ranking')).toBeLessThan(
      html.indexOf('Execution Timeline')
    )
    expect(html.indexOf('Execution Timeline')).toBeLessThan(
      html.indexOf('Session Cost &amp; Token Accounting')
    )
  })

  it('renders overview strip with metrics (spans, turns, tokens, cost, cache hit ratio)', () => {
    const html = renderHtml(<AgentSessionDashboard session={sampleSession} />)

    // Header metadata
    expect(html).toContain('copilot')
    expect(html).toContain('Fix parser AST recursion')
    expect(html).toContain('coder-bot')
    expect(html).toContain('48 spans')

    // Metric cards
    expect(html).toContain('Total Input')
    expect(html).toContain('120.5K') // 120,500 formatted as tokens
    expect(html).toContain('75.0K from cache')

    expect(html).toContain('Cache Read')
    expect(html).toContain('75.0K')
    expect(html).toContain('62.2% hit ratio')

    expect(html).toContain('Cache Creation')
    expect(html).toContain('18.0K')
    expect(html).toContain('on 2 turns')

    expect(html).toContain('Total Output')
    expect(html).toContain('6.4K')
    expect(html).toContain('1.5K reasoning')

    expect(html).toContain('Cost')
    expect(html).toContain('$1.25')
    expect(html).toContain('published')

    expect(html).toContain('Cache Hit Ratio')
    expect(html).toContain('62.2%')

    expect(html).toContain('Spans')
    expect(html).toContain('48')

    expect(html).toContain('Turns')
    expect(html).toContain('5')

    expect(html).toContain('Requests')
    expect(html).toContain('2')

    expect(html).toContain('Duration')
    expect(html).toContain('38.4s')

    expect(html).toContain('Tool Calls')
    expect(html).toContain('8')

    expect(html).toContain('Tools Offered')
    expect(html).toContain('3')
    expect(html).toContain('1 never called')
  })

  it('marks an unreported tool-call counter unavailable instead of rendering zero', () => {
    const unavailableToolCalls: AgentSessionPayload = {
      ...sampleSession,
      summary: {
        ...sampleSession.summary,
        tool_calls: undefined,
        tools_invoked: undefined,
      },
    }

    const tree = AgentSessionDashboard({ session: unavailableToolCalls })
    const metric = findElementByTestId(tree, 'metric-tool-calls')
    const html = renderHtml(metric)
    expect(html).toContain('data-measured="false"')
    expect(html).toContain('>—<')
    expect(html).not.toContain('>0<')
    expect(html).toContain('Tool invocation count was not reported by copilot.')
  })

  it('preserves a recorded zero tool-call counter as measured', () => {
    const zeroToolCalls: AgentSessionPayload = {
      ...sampleSession,
      summary: {
        ...sampleSession.summary,
        tool_calls: 0,
      },
    }

    const tree = AgentSessionDashboard({ session: zeroToolCalls })
    const metric = findElementByTestId(tree, 'metric-tool-calls')
    const html = renderHtml(metric)
    expect(html).toContain('data-measured="true"')
    expect(html).toContain('>0<')
  })

  it('renders reconciliation OK badge when root inputs match sum of chat inputs', () => {
    const html = renderHtml(<AgentSessionDashboard session={sampleSession} />)
    expect(html).toContain('data-testid="reconciliation-ok"')
    expect(html).toContain('Reconciliation OK')
    expect(html).toContain('across 2 requests')
  })

  it('renders reconciliation MISMATCH warning when root inputs mismatch', () => {
    const mismatchSession: AgentSessionPayload = {
      ...sampleSession,
      reconciliation: [
        {
          request: 'First request',
          root_input: 50000,
          sum_chat_input: 42000,
          input_match: false,
          root_output: 1000,
          sum_chat_output: 1000,
          output_match: true,
        },
      ],
    }
    const html = renderHtml(<AgentSessionDashboard session={mismatchSession} />)
    expect(html).toContain('data-testid="reconciliation-mismatch"')
    expect(html).toContain('Reconciliation MISMATCH')
    expect(html).toContain('on 1 of 1 request(s):')
    expect(html).toContain('chat sum 42.0K vs root 50.0K')
  })

  it('renders subagent notice with parent session link and agent name when is_subagent is true', () => {
    const subagentSession: AgentSessionPayload = {
      ...sampleSession,
      is_subagent: true,
      parent_session: 'parent-sess-999999',
      agent_name: 'subagent-worker',
    }
    const html = renderHtml(<AgentSessionDashboard session={subagentSession} />)
    expect(html).toContain('data-testid="subagent-notice"')
    expect(html).toContain('Subagent session')
    expect(html).toContain('subagent-worker')
    expect(html).toContain('parent-sess-999999'.slice(0, 10))
  })

  it('invokes onSelectSession callback when clicking parent session link', () => {
    const onSelectSession = vi.fn()
    const subagentSession: AgentSessionPayload = {
      ...sampleSession,
      is_subagent: true,
      parent_session: 'parent-sess-888888',
      agent_name: 'subagent-worker',
    }
    const tree = AgentSessionDashboard({
      session: subagentSession,
      onSelectSession,
    })
    const link = findElementByTestId(tree, 'parent-session-link')
    expect(link).not.toBeNull()
    ;(link!.props as { onClick: () => void }).onClick()
    expect(onSelectSession).toHaveBeenCalledWith('parent-sess-888888')
  })

  it('renders harness caveats and notes banner', () => {
    const html = renderHtml(<AgentSessionDashboard session={sampleSession} />)
    expect(html).toContain('data-testid="harness-notes"')
    expect(html).toContain('What copilot does not export:')
    expect(html).toContain('Harness does not export raw workspace info files.')
  })

  it('keeps a B3 unavailable-context reason verbatim in the dashboard banner', () => {
    const reason = 'Claude Code session files record tool invocations, not tool definitions.'
    const unavailableBucketSession = {
      ...b1Session,
      context: {
        measurable: false,
        reason,
      } as unknown as KyberSessionContext,
    }

    const html = renderHtml(<AgentSessionDashboard session={unavailableBucketSession} />)
    expect(html).toContain('data-testid="context-composition-not-measurable"')
    expect(html).toContain(reason)
  })

  it('renders multiple user requests list', () => {
    const html = renderHtml(<AgentSessionDashboard session={sampleSession} />)
    expect(html).toContain('data-testid="user-requests"')
    expect(html).toContain('2 user requests in this session:')
    expect(html).toContain('Analyze the recursion bug in ast.ts')
    expect(html).toContain('Apply the patch and verify tests pass')
  })

  it('renders auxiliary chat calls banner when aux_chat_calls is present', () => {
    const auxSession: AgentSessionPayload = {
      ...sampleSession,
      summary: {
        ...sampleSession.summary,
        aux_chat_calls: 2,
        aux_models: ['gpt-4o-mini'],
        aux_input: 1200,
        aux_output: 80,
      },
    }
    const html = renderHtml(<AgentSessionDashboard session={auxSession} />)
    expect(html).toContain('data-testid="aux-calls-banner"')
    expect(html).toContain('2 auxiliary gpt-4o-mini call(s)')
  })

  it('renders Spend and Context Composition section with SessionSpendCharts', () => {
    const html = renderHtml(<AgentSessionDashboard session={sampleSession} />)
    expect(html).toContain('data-testid="spend-composition-section"')
    expect(html).toContain('data-testid="session-spend-charts"')
    expect(html).toContain('data-testid="turn-spend-chart"')
    expect(html).toContain('data-testid="context-composition-chart"')
  })

  it('renders Tool and Schema Cost Ranking table with MCP grouping and waste range', () => {
    const html = renderHtml(<AgentSessionDashboard session={sampleSession} />)
    expect(html).toContain('data-testid="tool-schema-section"')
    expect(html).toContain('3 · Tool &amp; Schema Cost Ranking')

    // Waste callout banner & range
    expect(html).toContain('data-testid="schema-waste-banner"')
    expect(html).toContain('1 of 3 tools were never called')
    expect(html).toContain('900 tokens/turn')
    expect(html).toContain('data-testid="schema-waste-range"')
    expect(html).toContain('Unused waste range: $0.030 – $0.120')

    // Server groups and tools
    expect(html).toContain('data-testid="tools-ranking-table"')
    expect(html).toContain('builtin')
    expect(html).toContain('git')
    expect(html).toContain('MCP')
    expect(html).toContain('read_file')
    expect(html).toContain('write_file')
    expect(html).toContain('mcp_git_status')
    expect(html).toContain('0 calls')
    expect(html).toContain('never called')
  })

  it('renders fallback banner when harness does not export tool schemas', () => {
    const piSession: AgentSessionPayload = {
      ...sampleSession,
      harness: 'pi',
      tools: [
        {
          name: 'read',
          invocations: 4,
          in_definitions: false,
        },
      ],
    }
    const html = renderHtml(<AgentSessionDashboard session={piSession} />)
    expect(html).toContain('data-testid="schemas-not-exported-banner"')
    expect(html).toContain('pi does not export tool definitions')
  })

  it('renders Execution Timeline with call tree and duration bars toggle', () => {
    const html = renderHtml(<AgentSessionDashboard session={sampleSession} />)
    expect(html).toContain('data-testid="execution-timeline-section"')
    expect(html).toContain('4 · Execution Timeline &amp; Call Tree')
    expect(html).toContain('data-testid="timeline-tab-tree"')
    expect(html).toContain('data-testid="timeline-tab-bars"')
    expect(html).toContain('data-testid="timeline-tree-view"')
    expect(html).toContain('invoke_agent')
    expect(html).toContain('chat claude-3-5-sonnet')
    expect(html).toContain('execute_tool read_file')
  })

  it('renders Duration Timeline bars when bars tab is selected', () => {
    const html = renderHtml(<AgentSessionDashboard session={sampleSession} initialTimelineTab="bars" />)
    expect(html).toContain('data-testid="timeline-bars-view"')
    expect(html).toContain('data-testid="timeline-bar-row"')
  })

  it('renders empty state when session is null', () => {
    const html = renderHtml(<AgentSessionDashboard session={null} />)
    expect(html).toContain('data-testid="agent-dashboard-empty"')
    expect(html).toContain('No agent session selected')
  })

  it('supports harness reported cost basis', () => {
    const piSession: AgentSessionPayload = {
      ...sampleSession,
      harness: 'pi',
      summary: {
        ...sampleSession.summary,
        cost: {
          usd: 0.85,
          basis: 'harness_reported',
          status: 'ok',
        },
      },
    }
    const html = renderHtml(<AgentSessionDashboard session={piSession} />)
    expect(html).toContain('data-testid="metric-cost"')
    expect(html).toContain('$0.85')
    expect(html).toContain('reported')
    expect(html).toContain('reported by harness')
  })

  it('keeps session.timeline[0] in children so invoke_agent is not swallowed when timeline has 1 element', () => {
    const singleSpanTimeline = [
      {
        spanId: 'root-span-1',
        name: 'invoke_agent',
        op: 'invoke_agent',
        kind: 'agent',
        durationMs: 38400,
        children: [
          {
            spanId: 'child-span-1',
            name: 'chat claude-3-5-sonnet',
            op: 'chat',
            children: [],
          },
        ],
      },
    ]
    const singleTimelineSession: AgentSessionPayload = {
      ...sampleSession,
      timeline: singleSpanTimeline,
    }
    const tree = AgentSessionContent({ session: singleTimelineSession })
    let timelineViewEl: React.ReactElement | null = null
    const walk = (node: unknown): void => {
      if (!React.isValidElement(node)) return
      const props = node.props as Record<string, unknown> | undefined
      if (props?.root && props?.onSelectNode) {
        timelineViewEl = node
        return
      }
      if (props?.children) {
        React.Children.forEach(props.children, walk)
      }
    }
    walk(tree)

    expect(timelineViewEl).not.toBeNull()
    const root = (timelineViewEl!.props as { root: TimelineNode }).root
    expect(root.spanId).toBe('session-root')
    expect(root.children).toHaveLength(1)
    expect(root.children[0].spanId).toBe('root-span-1')
    expect(root.children[0].name).toBe('invoke_agent')
    expect(root.children[0].children).toHaveLength(1)
    expect(root.children[0].children[0].spanId).toBe('child-span-1')
  })

  it('renders empty table row when toolRows is empty', () => {
    const emptyToolsSession: AgentSessionPayload = {
      ...sampleSession,
      tools: [],
    }
    const html = renderHtml(<AgentSessionDashboard session={emptyToolsSession} />)
    expect(html).toContain('No tools recorded for this session.')
    expect(html).toMatch(/<td[^>]*colSpan="9"[^>]*>No tools recorded for this session\.<\/td>/i)
  })

  it('sorts tools within each server in the schema ranking table by total_schema_cost descending', () => {
    const sessionUnsortedTools: AgentSessionPayload = {
      ...sampleSession,
      tools: [
        {
          name: 'tool_cheap',
          server: 'custom_server',
          is_mcp: false,
          schema_tokens: 50,
          turns_resident: 1,
          total_schema_cost: 50,
          invocations: 1,
        },
        {
          name: 'tool_expensive',
          server: 'custom_server',
          is_mcp: false,
          schema_tokens: 500,
          turns_resident: 10,
          total_schema_cost: 5000,
          invocations: 2,
        },
      ],
    }
    const html = renderHtml(<AgentSessionDashboard session={sessionUnsortedTools} />)
    const expIdx = html.indexOf('tool_expensive')
    const cheapIdx = html.indexOf('tool_cheap')
    expect(expIdx).toBeGreaterThan(-1)
    expect(cheapIdx).toBeGreaterThan(-1)
    expect(expIdx).toBeLessThan(cheapIdx)
  })

  it('falls back to node.op || node.kind before indexing TIMELINE_OP_COLORS in duration bars', () => {
    const sessionWithKindOnly: AgentSessionPayload = {
      ...sampleSession,
      timeline: [
        {
          spanId: 'span-kind-only',
          name: 'kind_span',
          kind: 'tool',
          durationMs: 500,
          offsetMs: 0,
        },
      ],
    }
    const html = renderHtml(<AgentSessionDashboard session={sessionWithKindOnly} initialTimelineTab="bars" />)
    expect(html).toContain('data-testid="timeline-bars-view"')
    expect(html).toContain('kind_span')
    // TIMELINE_OP_COLORS['tool'] is #10b981
    expect(html).toContain('style="left:0%;width:100%;background-color:#10b981"')
  })
})

describe('AgentSessionDashboard: Interaction & Drawer Integration', () => {
  it('routes turn bands, tool rows, and timeline spans through full-content requests', () => {
    const internals = internalsOf()
    const originalUseState = internals?.H?.useState
    const state: unknown[] = []
    let stateCursor = 0

    const renderWithState = (initialTimelineTab?: 'tree' | 'bars') => {
      stateCursor = 0
      return AgentSessionContent({ session: sampleSession, initialTimelineTab })
    }

    const drawerFrom = (node: unknown): React.ReactElement | null => {
      if (node == null) return null
      if (Array.isArray(node)) {
        for (const child of node) {
          const found = drawerFrom(child)
          if (found) return found
        }
        return null
      }
      if (!React.isValidElement(node)) return null
      if (node.type === SessionInspectorDrawer) return node
      return drawerFrom((node.props as { children?: unknown }).children)
    }

    if (internals?.H) {
      internals.H.useState = (initial: unknown) => {
        const index = stateCursor++
        if (!(index in state)) {
          state[index] = typeof initial === 'function' ? (initial as () => unknown)() : initial
        }
        return [
          state[index],
          (next: unknown) => {
            state[index] = typeof next === 'function' ? (next as (s: unknown) => unknown)(state[index]) : next
          },
        ]
      }
    }

    try {
      const turnTree = renderWithState()
      let spendCharts: React.ReactElement | null = null
      const findSpendCharts = (node: unknown): void => {
        if (!React.isValidElement(node) || spendCharts) return
        const props = node.props as Record<string, unknown> | undefined
        if (props?.onSelectTurn && props?.session) {
          spendCharts = node
          return
        }
        if (props?.children) {
          React.Children.forEach(props.children, findSpendCharts)
        }
      }
      findSpendCharts(turnTree)
      expect(spendCharts).not.toBeNull()
      // 0-based transport: the first turn is index 0 (issue #184).
      ;(spendCharts!.props as { onSelectTurn: (i: number, b?: string) => void }).onSelectTurn(0, 'tool_definitions')
      expect((drawerFrom(renderWithState())!.props as { contentRequest?: unknown }).contentRequest).toEqual({
        sessionId: 'sess-abc-123',
        span: 'turn-span-1',
        part: 'tool_definitions',
      })

      const toolTree = renderWithState()
      ;(findElementByTestId(toolTree, 'tool-row-read_file')!.props as { onClick: () => void }).onClick()
      expect((drawerFrom(renderWithState())!.props as { contentRequest?: unknown }).contentRequest).toEqual({
        sessionId: 'sess-abc-123',
        span: 'turn-span-1',
        part: 'tool_definitions',
      })

      const spanTree = renderWithState('bars')
      ;(findElementByTestId(spanTree, 'timeline-bar-row')!.props as { onClick: () => void }).onClick()
      expect((drawerFrom(renderWithState('bars'))!.props as { contentRequest?: unknown }).contentRequest).toEqual({
        sessionId: 'sess-abc-123',
        span: 'root-span-1',
      })
    } finally {
      if (internals?.H) {
        internals.H.useState = originalUseState
      }
    }
  })

  it('opens drawer when clicking a tool row', () => {
    let capturedOpen = false
    let capturedTitle = ''
    let capturedContent: unknown = null

    const reactInternals = internalsOf()
    const origState = reactInternals?.H?.useState
    if (reactInternals?.H) {
      reactInternals.H.useState = (initial: unknown) => {
        if (typeof initial === 'boolean') {
          return [capturedOpen, (v: boolean) => { capturedOpen = v }]
        }
        if (initial === '') {
          return [capturedTitle, (t: string) => { capturedTitle = t }]
        }
        return [capturedContent, (c: unknown) => { capturedContent = c }]
      }
    }

    try {
      const tree = AgentSessionDashboard({ session: sampleSession })
      const toolRow = findElementByTestId(tree, 'tool-row-read_file')
      expect(toolRow).not.toBeNull()
      expect((toolRow!.props as { onClick?: unknown }).onClick).toBeTypeOf('function')

      ;(toolRow!.props as { onClick: () => void }).onClick()
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toBe('Tool: read_file')
      expect(capturedContent).toBeDefined()
      expect((capturedContent as { tool: { name: string } }).tool.name).toBe('read_file')
    } finally {
      if (reactInternals?.H) {
        reactInternals.H.useState = origState
      }
    }
  })

  it('opens drawer when clicking a timeline span in duration bars view', () => {
    let capturedOpen = false
    let capturedTitle = ''
    let capturedContent: unknown = null

    const reactInternals = internalsOf()
    const origState = reactInternals?.H?.useState
    if (reactInternals?.H) {
      reactInternals.H.useState = (initial: unknown) => {
        if (typeof initial === 'boolean') {
          return [capturedOpen, (v: boolean) => { capturedOpen = v }]
        }
        if (initial === '') {
          return [capturedTitle, (t: string) => { capturedTitle = t }]
        }
        return [capturedContent, (c: unknown) => { capturedContent = c }]
      }
    }

    try {
      const tree = AgentSessionDashboard({ session: sampleSession, initialTimelineTab: 'bars' })
      const barRow = findElementByTestId(tree, 'timeline-bar-row')
      expect(barRow).not.toBeNull()
      expect((barRow!.props as { onClick?: unknown }).onClick).toBeTypeOf('function')

      ;(barRow!.props as { onClick: () => void }).onClick()
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toContain('Span:')
      expect(capturedContent).toBeDefined()
    } finally {
      if (reactInternals?.H) {
        reactInternals.H.useState = origState
      }
    }
  })

  it('opens drawer for turn when onSelectTurn is invoked on SessionSpendCharts with a 0-based index and populates bucket analysis data', () => {
    let capturedOpen = false
    let capturedTitle = ''
    let capturedContent: unknown = null

    const reactInternals = internalsOf()
    const origState = reactInternals?.H?.useState
    if (reactInternals?.H) {
      reactInternals.H.useState = (initial: unknown) => {
        if (typeof initial === 'boolean') {
          return [capturedOpen, (v: boolean) => { capturedOpen = v }]
        }
        if (typeof initial === 'string') {
          return [capturedTitle, (t: string) => { capturedTitle = t }]
        }
        return [capturedContent, (c: unknown) => { capturedContent = c }]
      }
    }

    try {
      const tree = AgentSessionContent({ session: sampleSession })
      let spendChartsEl: React.ReactElement | null = null
      const walk = (node: unknown): void => {
        if (!React.isValidElement(node)) return
        const props = node.props as Record<string, unknown> | undefined
        if (props?.onSelectTurn && props?.session) {
          spendChartsEl = node
          return
        }
        if (props?.children) {
          React.Children.forEach(props.children, walk)
        }
      }
      walk(tree)

      expect(spendChartsEl).not.toBeNull()

      // 0-based transport lookup: index 0 is the human-facing Turn 1 (issue #184).
      ;(spendChartsEl!.props as { onSelectTurn: (i: number, b?: string) => void }).onSelectTurn(0)
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toBe('Turn 1')
      expect((capturedContent as { spanId?: string }).spanId).toBe('turn-span-1')

      // 0-based transport lookup: index 1 is Turn 2.
      ;(spendChartsEl!.props as { onSelectTurn: (i: number, b?: string) => void }).onSelectTurn(1)
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toBe('Turn 2')
      expect((capturedContent as { spanId?: string }).spanId).toBe('turn-span-2')

      // 0-based turn lookup with bucket: tool_definitions
      ;(spendChartsEl!.props as { onSelectTurn: (i: number, b?: string) => void }).onSelectTurn(0, 'tool_definitions')
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toBe('Turn 1 · tool_definitions')
      expect(capturedContent).toBeDefined()
      const bucketData = capturedContent as {
        bucket?: string
        tokens?: number
        total?: number
        label?: string
        content?: unknown
      }
      expect(bucketData.bucket).toBe('tool_definitions')
      expect(bucketData.tokens).toBe(2800)
      expect(bucketData.total).toBe(45500)
      expect(bucketData.label).toBe('Tool definitions')
      expect(bucketData.content).toEqual(
        (sampleSession.turns![0].content as { tool_definitions?: unknown }).tool_definitions
      )

      // Verify ContextBucketInspector properly renders the populated bucket data
      const drawerHtml = renderHtml(
        <SessionInspectorDrawer
          open={true}
          onClose={() => {}}
          title={capturedTitle}
          rawContent={capturedContent as DrawerContent}
        />
      )
      expect(drawerHtml).toContain('data-testid="context-bucket-inspector"')
      expect(drawerHtml).toContain('Tool definitions')
      expect(drawerHtml).toContain('2.8K')
      expect(drawerHtml).toContain('45.5K')
      expect(drawerHtml).toContain('6.2%')
    } finally {
      if (reactInternals?.H) {
        reactInternals.H.useState = origState
      }
    }
  })

  it('shows the first-turn edge counts, not the last, for Turn 1 when context.turns is empty (issue #184 review)', () => {
    const edgeSession: AgentSessionPayload = {
      ...sampleSession,
      turns: [
        {
          index: 0,
          spanId: 'turn-span-1',
          model: 'claude-3-5-sonnet',
          buckets: { tool_definitions: 100 },
        },
      ],
      context: {
        measurable: true,
        contextLimit: 200000,
        turns: [],
        residualTotal: 0,
        derivedCounts: false,
        freshJumpFactor: 2,
        flaggedTurns: [],
        sessionAccumulationRate: 0,
        unmeasuredTurns: 0,
        first: {
          buckets: { tool_definitions: 111 },
          reported_input: 111,
        },
        last: {
          buckets: { tool_definitions: 999 },
          reported_input: 999,
        },
      },
    }

    let capturedTitle = ''
    let capturedContent: unknown = null

    const reactInternals = internalsOf()
    const origState = reactInternals?.H?.useState
    if (reactInternals?.H) {
      reactInternals.H.useState = (initial: unknown) => {
        if (typeof initial === 'boolean') {
          return [false, () => {}]
        }
        if (typeof initial === 'string') {
          return [capturedTitle, (t: string) => { capturedTitle = t }]
        }
        return [capturedContent, (c: unknown) => { capturedContent = c }]
      }
    }

    try {
      const tree = AgentSessionContent({ session: edgeSession })
      let spendChartsEl: React.ReactElement | null = null
      const walk = (node: unknown): void => {
        if (!React.isValidElement(node)) return
        const props = node.props as Record<string, unknown> | undefined
        if (props?.onSelectTurn && props?.session) {
          spendChartsEl = node
          return
        }
        if (props?.children) {
          React.Children.forEach(props.children, walk)
        }
      }
      walk(tree)
      expect(spendChartsEl).not.toBeNull()

      ;(spendChartsEl!.props as { onSelectTurn: (i: number, b?: string) => void }).onSelectTurn(0, 'tool_definitions')
      expect(capturedTitle).toBe('Turn 1 · tool_definitions')
      const bucketData = capturedContent as { tokens?: number; total?: number }
      expect(bucketData.tokens).toBe(111)
      expect(bucketData.total).toBe(111)
    } finally {
      if (reactInternals?.H) {
        reactInternals.H.useState = origState
      }
    }
  })
})

describe('AgentSessionDashboard: Remote Fetch States', () => {
  it('renders loading skeleton when sessionId is provided and query is pending', () => {
    const qc = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    })
    const origFetch = globalThis.fetch
    globalThis.fetch = () => new Promise(() => {})

    try {
      const html = renderHtml(
        <QueryClientProvider client={qc}>
          <AgentSessionDashboard sessionId="remote-sess-123" />
        </QueryClientProvider>
      )
      expect(html).toContain('data-testid="agent-dashboard-loading"')
    } finally {
      globalThis.fetch = origFetch
    }
  })

  it('renders error state when error prop is present', () => {
    const html = renderHtml(
      <AgentSessionDashboard error={new Error('Network connection failed')} />
    )
    expect(html).toContain('data-testid="agent-dashboard-error"')
    expect(html).toContain('Failed to load session')
    expect(html).toContain('Network connection failed')
  })

  it('renders loading skeleton when isLoading prop is true', () => {
    const html = renderHtml(<AgentSessionDashboard isLoading={true} />)
    expect(html).toContain('data-testid="agent-dashboard-loading"')
  })

  it('forwards initialTimelineTab through AgentSessionLoader to AgentSessionContent', () => {
    const qc = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    })
    qc.setQueryData(['kyber-session', 'test-sess-1'], sampleSession)
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <AgentSessionLoader sessionId="test-sess-1" initialTimelineTab="bars" />
      </QueryClientProvider>
    )
    expect(html).toContain('data-testid="timeline-bars-view"')
  })
})

describe('timeline shape from the canonical store', () => {
  it('renders when timeline is a single root node, not an array', () => {
    // buildTimeline() returns ONE root node, and the payload shape documents
    // it that way — but three consumers here iterated it as an array. Every
    // unit test passed because the fixtures used an array, while a real
    // session threw "nodes is not iterable" and the error boundary replaced
    // the entire expanded view. This pins the real shape.
    const session = {
      ...sampleSession,
      timeline: {
        spanId: 'root-1',
        parentId: null,
        name: 'session',
        kind: 'session',
        startMs: 0,
        durationMs: 100,
        attributes: {},
        isSubagent: false,
        isAuxiliary: false,
        cost: { basis: 'unknown', status: 'no_rate' },
        children: [
          {
            spanId: 'child-1',
            parentId: 'root-1',
            name: 'llm_request',
            kind: 'client',
            startMs: 1,
            durationMs: 10,
            attributes: {},
            isSubagent: false,
            isAuxiliary: false,
            cost: { basis: 'unknown', status: 'no_rate' },
            children: [],
          },
        ],
      },
    }

    const html = renderHtml(React.createElement(AgentSessionContent as never, { session } as never))

    expect(html).toContain('execution-timeline-section')
    expect(html).toContain('spend-composition-section')
  })
})


describe('AgentSessionDashboard: Issue #180 Task 8 Dashboard UI Verification', () => {
  it('renders tool-call count when summary.tool_calls = 5 in Overview card and not a dash', () => {
    const sessionWith5ToolCalls: AgentSessionPayload = {
      ...sampleSession,
      summary: {
        ...sampleSession.summary,
        tool_calls: 5,
        tools_invoked: 3,
      },
    }

    const tree = AgentSessionDashboard({ session: sessionWith5ToolCalls })
    const metric = findElementByTestId(tree, 'metric-tool-calls')
    const html = renderHtml(metric)
    expect(html).toContain('data-measured="true"')
    expect(html).toContain('>5<')
    expect(html).not.toContain('>—<')
    expect(html).toContain('3 distinct invoked')
  })

  it('renders dash with honest unmeasured tooltip when summary.tool_calls is undefined for legacy session or unmeasured harness', () => {
    const legacySession: AgentSessionPayload = {
      ...sampleSession,
      harness: 'claude-code',
      summary: {
        ...sampleSession.summary,
        tool_calls: undefined,
        tools_invoked: undefined,
      },
    }

    const tree = AgentSessionDashboard({ session: legacySession })
    const metric = findElementByTestId(tree, 'metric-tool-calls')
    const html = renderHtml(metric)
    expect(html).toContain('data-measured="false"')
    expect(html).toContain('>—<')
    expect(html).not.toContain('>0<')
    expect(html).toContain('Tool invocation count was not reported by claude-code.')
  })

  it('renders tool invocations with name and status badge in timeline call tree when session contains tool.invoke records', () => {
    const sessionWithToolInvokes: AgentSessionPayload = {
      ...sampleSession,
      timeline: [
        {
          spanId: 'root-span-1',
          name: 'invoke_agent',
          op: 'invoke_agent',
          kind: 'agent',
          durationMs: 5000,
          offsetMs: 0,
          children: [
            {
              spanId: 'tool-span-1',
              parentId: 'root-span-1',
              name: 'Bash',
              op: 'tool.invoke',
              kind: 'tool',
              durationMs: 350,
              offsetMs: 100,
              status: 'ok',
              attributes: {
                'gen_ai.tool.name': 'Bash',
                'gen_ai.tool.status': 'ok',
                'gen_ai.tool.call_id': 'call_1',
              },
              children: [],
            },
            {
              spanId: 'tool-span-2',
              parentId: 'root-span-1',
              name: 'Read',
              op: 'tool.invoke',
              kind: 'tool',
              durationMs: 120,
              offsetMs: 500,
              status: 'error',
              attributes: {
                'gen_ai.tool.name': 'Read',
                'gen_ai.tool.status': 'error',
                'gen_ai.tool.call_id': 'call_2',
              },
              children: [],
            },
          ],
        },
      ],
    }

    const html = renderHtml(<AgentSessionDashboard session={sessionWithToolInvokes} />)
    const treeView = html.slice(html.indexOf('data-testid="timeline-tree-view"'))

    // Verify tool invocation names and durations render in call tree
    expect(treeView).toContain('Bash')
    expect(treeView).toContain('350ms')
    expect(treeView).toContain('Read')
    expect(treeView).toContain('120ms')

    // Verify status badges render for tool invocations
    expect(treeView).toMatch(/data-testid="status-badge"[^>]*>\s*ok\s*</i)
    expect(treeView).toMatch(/data-testid="status-badge"[^>]*>\s*error\s*</i)
  })
  it("formats tools_offered and tools_invoked properly when provided as string arrays", () => {
    const sessionWithArrayTools: AgentSessionPayload = {
      ...sampleSession,
      summary: {
        ...sampleSession.summary,
        tool_calls: 3,
        tools_offered: ["toolA", "toolB"],
        tools_invoked: ["toolA"],
      },
    }

    const html = renderHtml(<AgentSessionDashboard session={sessionWithArrayTools} />)
    const idx = html.indexOf("Tools Offered");
    const offeredMetric = html.slice(idx, idx + 400);

    // Card value should format as count 2, not "—"
    expect(offeredMetric).toContain(">2<")
    // Subtitle should format as "1 never called", not "NaN never called"
    expect(offeredMetric).toContain("1 never called")
    expect(offeredMetric).not.toContain("NaN")
  })

  it('does NOT render schema-waste-banner and reports unmeasured invocations when tools_invoked is undefined', () => {
    const sessionWithUnmeasuredInvocations: AgentSessionPayload = {
      ...sampleSession,
      summary: {
        ...sampleSession.summary,
        tools_offered: ['toolA', 'toolB'],
        tools_invoked: undefined,
      },
    }

    const html = renderHtml(<AgentSessionDashboard session={sessionWithUnmeasuredInvocations} />)
    expect(html).not.toContain('data-testid="schema-waste-banner"')
    expect(html).not.toContain('tools were never called')
    const offeredCard = html.slice(html.indexOf('Tools Offered'), html.indexOf('Tools Offered') + 300)
    expect(offeredCard).toContain('invocations not reported')
    expect(offeredCard).not.toContain('never called')
  })

  it('safely handles non-string node.status in timeline without throwing', () => {
    const sessionWithMalformedStatus: AgentSessionPayload = {
      ...sampleSession,
      timeline: [
        {
          spanId: 'span-bad-status',
          name: 'tool_call',
          op: 'tool.invoke',
          kind: 'tool',
          durationMs: 100,
          offsetMs: 0,
          status: true as unknown as string,
          attributes: {},
          children: [],
        },
      ],
    }

    expect(() => {
      renderHtml(<AgentSessionDashboard session={sessionWithMalformedStatus} />)
    }).not.toThrow()
  })

  it("computes unusedOfferedCount via set difference and renders waste banner for string arrays (Threads 6 & 7)", () => {
    const sessionWithArrayTools: AgentSessionPayload = {
      ...sampleSession,
      summary: {
        ...sampleSession.summary,
        tool_calls: 3,
        tools_offered: ["toolA", "toolB"],
        tools_invoked: ["toolA", "toolX"],
        unused_schema_per_turn: 500,
        schema_tokens_per_turn: 2000,
      },
    }

    const html = renderHtml(<AgentSessionDashboard session={sessionWithArrayTools} />)
    const idx = html.indexOf("Tools Offered")
    const offeredMetric = html.slice(idx, idx + 400)

    // Card subtitle should say "1 never called", NOT "0 never called"
    expect(offeredMetric).toContain("1 never called")
    expect(offeredMetric).not.toContain("0 never called")

    // Waste banner MUST be rendered because 1 offered tool was never called!
    expect(html).toContain('data-testid="schema-waste-banner"')
    expect(html).toContain("1 of 2 tools were never called.")
  })

  it("styles timeline status badges according to failure/success/neutral classifications (Thread 8)", () => {
    const sessionWithStatuses: AgentSessionPayload = {
      ...sampleSession,
      timeline: [
        {
          spanId: "root-span-1",
          parentId: null,
          name: "assistant",
          op: "llm.turn",
          kind: "turn",
          durationMs: 1000,
          children: [
            { spanId: "c1", parentId: "root-span-1", name: "t1", op: "tool.invoke", kind: "tool", durationMs: 10, status: "error", children: [] },
            { spanId: "c2", parentId: "root-span-1", name: "t2", op: "tool.invoke", kind: "tool", durationMs: 10, status: "failure", children: [] },
            { spanId: "c3", parentId: "root-span-1", name: "t3", op: "tool.invoke", kind: "tool", durationMs: 10, status: "fatal", children: [] },
            { spanId: "c4", parentId: "root-span-1", name: "t4", op: "tool.invoke", kind: "tool", durationMs: 10, status: "ok", children: [] },
            { spanId: "c5", parentId: "root-span-1", name: "t5", op: "tool.invoke", kind: "tool", durationMs: 10, status: "success", children: [] },
            { spanId: "c6", parentId: "root-span-1", name: "t6", op: "tool.invoke", kind: "tool", durationMs: 10, status: "unset", children: [] },
            { spanId: "c7", parentId: "root-span-1", name: "t7", op: "tool.invoke", kind: "tool", durationMs: 10, status: "unknown", children: [] },
          ],
        },
      ],
    }

    const html = renderHtml(<AgentSessionDashboard session={sessionWithStatuses} />)
    const treeView = html.slice(html.indexOf('data-testid="timeline-tree-view"'))

    // error, failure, fatal must have red classes
    expect(treeView).toMatch(/data-testid="status-badge"[^>]*text-red-[^>]*>\s*error\s*</i)
    expect(treeView).toMatch(/data-testid="status-badge"[^>]*text-red-[^>]*>\s*failure\s*</i)
    expect(treeView).toMatch(/data-testid="status-badge"[^>]*text-red-[^>]*>\s*fatal\s*</i)

    // ok, success must have green (emerald) classes
    expect(treeView).toMatch(/data-testid="status-badge"[^>]*text-emerald-[^>]*>\s*ok\s*</i)
    expect(treeView).toMatch(/data-testid="status-badge"[^>]*text-emerald-[^>]*>\s*success\s*</i)

    // unset, unknown must have neutral muted classes (NOT emerald or red)
    expect(treeView).toMatch(/data-testid="status-badge"[^>]*text-muted-foreground[^>]*>\s*unset\s*</i)
    expect(treeView).toMatch(/data-testid="status-badge"[^>]*text-muted-foreground[^>]*>\s*unknown\s*</i)
  })
});

// Issue #184: the drawer resolves strictly 0-based transport through the shared
// helper — explicit identity first, array position only for rows that carry
// neither `index` nor `turn`. Never a neighbor.
describe('findTurnByTransport (shared drawer lookup)', () => {
  it('matches a 0-based index row and the positional fallback', () => {
    expect(findTurnByTransport([{ index: 4 }], 4)).toEqual({ index: 4 })
    expect(findTurnByTransport([{}, {}], 1)).toEqual({})
    expect(findTurnByTransport([{ index: 3 }], 4)).toBeUndefined()
  })

  it('matches a legacy 1-based turn row via turn - 1 only', () => {
    expect(findTurnByTransport([{ turn: 5 }], 4)).toEqual({ turn: 5 })
    expect(findTurnByTransport([{ turn: 5 }], 5)).toBeUndefined()
  })

  it('prefers explicit identity over an earlier positional match (issue #184 review)', () => {
    const rows = [{ turn: 2, spanId: 'span-a' }, { turn: 1, spanId: 'span-b' }]
    expect(findTurnByTransport(rows, 0)).toEqual({ turn: 1, spanId: 'span-b' })
    expect(findTurnByTransport(rows, 1)).toEqual({ turn: 2, spanId: 'span-a' })
  })

  it('never serves a neighboring identified row positionally', () => {
    expect(findTurnByTransport([{ index: 7 }, { index: 8 }], 0)).toBeUndefined()
  })
})

describe('findContextTurn (shared 1-based context lookup)', () => {
  it('matches engine 1-based index and legacy turn, then position', () => {
    expect(findContextTurn([{ index: 2 }], 2)).toEqual({ index: 2 })
    expect(findContextTurn([{ turn: 2 }], 2)).toEqual({ turn: 2 })
    expect(findContextTurn([{}, {}], 2)).toEqual({})
  })

  it('prefers explicit identity over an earlier positional match (issue #184 review)', () => {
    const rows = [{ turn: 2 }, { turn: 1 }]
    expect(findContextTurn(rows, 1)).toEqual({ turn: 1 })
    expect(findContextTurn(rows, 2)).toEqual({ turn: 2 })
  })
})

// Issue #185: a legacy payload carrying cache creation but no coverage count
// must not print "on 0 turns" — absence is not a measured zero.
describe('AgentSessionDashboard cache tiles (issue #185)', () => {
  it('renders honest absence instead of "on 0 turns" when coverage is unreported', () => {
    const { summary, ...rest } = sampleSession
    const legacy = {
      ...rest,
      summary: {
        ...summary,
        total_cache_creation: 10800000,
        cache_creation_coverage: undefined,
        cache_hit_ratio: undefined,
      },
    }
    const html = renderHtml(<AgentSessionDashboard session={legacy} />)

    expect(html).toContain('Cache Creation')
    expect(html).not.toContain('on 0 turns')
    // The ratio tile falls back to its honest dash without a served ratio.
    expect(html).toContain('Cache Hit Ratio')
  })
})
