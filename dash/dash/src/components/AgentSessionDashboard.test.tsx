import { describe, it, expect, vi } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import {
  AgentSessionDashboard,
  AgentSessionContent,
  AgentSessionLoader,
  type AgentSessionPayload,
  formatDuration,
  formatCredits,
} from './AgentSessionDashboard'
import { SessionInspectorDrawer } from './SessionInspectorDrawer'

// Set up React 19 test hook dispatcher so components using hooks can be rendered in tests
const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: {
      H?: any
    }
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE

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

function findElementByTestId(node: unknown, testId: string): React.ReactElement<any> | null {
  if (node == null) return null
  if (Array.isArray(node)) {
    for (const child of node) {
      const found = findElementByTestId(child, testId)
      if (found) return found
    }
    return null
  }
  if (React.isValidElement(node)) {
    const props = node.props as Record<string, any>
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
      index: 1,
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
      index: 2,
      spanId: 'turn-span-2',
      model: 'claude-3-5-sonnet',
      durationMs: 5100,
      fresh: 5000,
      cache_read: 42000,
      cache_creation: 0,
      output: 1800,
    },
  ],
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
  },
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
  },
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
    link?.props.onClick()
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
      },
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
    let timelineViewEl: any = null
    const walk = (node: any) => {
      if (!node) return
      if (node.props?.root && node.props?.onSelectNode) {
        timelineViewEl = node
        return
      }
      if (node.props?.children) {
        React.Children.forEach(node.props.children, walk)
      }
    }
    walk(tree)

    expect(timelineViewEl).not.toBeNull()
    const root = timelineViewEl.props.root
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
        } as any,
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
    const internals = (React as any).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE
    const originalUseState = internals?.H?.useState
    const state: any[] = []
    let stateCursor = 0

    const renderWithState = (initialTimelineTab?: 'tree' | 'bars') => {
      stateCursor = 0
      return AgentSessionContent({ session: sampleSession, initialTimelineTab })
    }

    const drawerFrom = (node: unknown): React.ReactElement<any> | null => {
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
      internals.H.useState = (initial: any) => {
        const index = stateCursor++
        if (!(index in state)) {
          state[index] = typeof initial === 'function' ? initial() : initial
        }
        return [
          state[index],
          (next: any) => {
            state[index] = typeof next === 'function' ? next(state[index]) : next
          },
        ]
      }
    }

    try {
      const turnTree = renderWithState()
      let spendCharts: any = null
      const findSpendCharts = (node: any): void => {
        if (!node || spendCharts) return
        if (node.props?.onSelectTurn && node.props?.session) {
          spendCharts = node
          return
        }
        React.Children.forEach(node.props?.children, findSpendCharts)
      }
      findSpendCharts(turnTree)
      expect(spendCharts).not.toBeNull()
      spendCharts.props.onSelectTurn(1, 'tool_definitions')
      expect(drawerFrom(renderWithState())?.props.contentRequest).toEqual({
        sessionId: 'sess-abc-123',
        span: 'turn-span-1',
        part: 'tool_definitions',
      })

      const toolTree = renderWithState()
      findElementByTestId(toolTree, 'tool-row-read_file')?.props.onClick()
      expect(drawerFrom(renderWithState())?.props.contentRequest).toEqual({
        sessionId: 'sess-abc-123',
        span: 'turn-span-1',
        part: 'tool_definitions',
      })

      const spanTree = renderWithState('bars')
      findElementByTestId(spanTree, 'timeline-bar-row')?.props.onClick()
      expect(drawerFrom(renderWithState('bars'))?.props.contentRequest).toEqual({
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
    let capturedContent: any = null

    const reactInternals = (React as any).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE
    const origState = reactInternals?.H?.useState
    if (reactInternals?.H) {
      reactInternals.H.useState = (initial: any) => {
        if (typeof initial === 'boolean') {
          return [capturedOpen, (v: boolean) => { capturedOpen = v }]
        }
        if (initial === '') {
          return [capturedTitle, (t: string) => { capturedTitle = t }]
        }
        return [capturedContent, (c: any) => { capturedContent = c }]
      }
    }

    try {
      const tree = AgentSessionDashboard({ session: sampleSession })
      const toolRow = findElementByTestId(tree, 'tool-row-read_file')
      expect(toolRow).not.toBeNull()
      expect(toolRow?.props.onClick).toBeTypeOf('function')

      toolRow?.props.onClick()
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toBe('Tool: read_file')
      expect(capturedContent).toBeDefined()
      expect(capturedContent.tool.name).toBe('read_file')
    } finally {
      if (reactInternals?.H) {
        reactInternals.H.useState = origState
      }
    }
  })

  it('opens drawer when clicking a timeline span in duration bars view', () => {
    let capturedOpen = false
    let capturedTitle = ''
    let capturedContent: any = null

    const reactInternals = (React as any).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE
    const origState = reactInternals?.H?.useState
    if (reactInternals?.H) {
      reactInternals.H.useState = (initial: any) => {
        if (typeof initial === 'boolean') {
          return [capturedOpen, (v: boolean) => { capturedOpen = v }]
        }
        if (initial === '') {
          return [capturedTitle, (t: string) => { capturedTitle = t }]
        }
        return [capturedContent, (c: any) => { capturedContent = c }]
      }
    }

    try {
      const tree = AgentSessionDashboard({ session: sampleSession, initialTimelineTab: 'bars' })
      const barRow = findElementByTestId(tree, 'timeline-bar-row')
      expect(barRow).not.toBeNull()
      expect(barRow?.props.onClick).toBeTypeOf('function')

      barRow?.props.onClick()
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toContain('Span:')
      expect(capturedContent).toBeDefined()
    } finally {
      if (reactInternals?.H) {
        reactInternals.H.useState = origState
      }
    }
  })

  it('opens drawer for turn when onSelectTurn is invoked on SessionSpendCharts with 1-based index and populates bucket analysis data', () => {
    let capturedOpen = false
    let capturedTitle = ''
    let capturedContent: any = null

    const reactInternals = (React as any).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE
    const origState = reactInternals?.H?.useState
    if (reactInternals?.H) {
      reactInternals.H.useState = (initial: any) => {
        if (typeof initial === 'boolean') {
          return [capturedOpen, (v: boolean) => { capturedOpen = v }]
        }
        if (typeof initial === 'string') {
          return [capturedTitle, (t: string) => { capturedTitle = t }]
        }
        return [capturedContent, (c: any) => { capturedContent = c }]
      }
    }

    try {
      const tree = AgentSessionContent({ session: sampleSession })
      let spendChartsEl: any = null
      const walk = (node: any) => {
        if (!node) return
        if (node.props?.onSelectTurn && node.props?.session) {
          spendChartsEl = node
          return
        }
        if (node.props?.children) {
          React.Children.forEach(node.props.children, walk)
        }
      }
      walk(tree)

      expect(spendChartsEl).not.toBeNull()

      // 1-based turn lookup: Turn 1
      spendChartsEl.props.onSelectTurn(1)
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toBe('Turn 1')
      expect(capturedContent.spanId).toBe('turn-span-1')

      // 1-based turn lookup: Turn 2
      spendChartsEl.props.onSelectTurn(2)
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toBe('Turn 2')
      expect(capturedContent.spanId).toBe('turn-span-2')

      // 1-based turn lookup with bucket: tool_definitions
      spendChartsEl.props.onSelectTurn(1, 'tool_definitions')
      expect(capturedOpen).toBe(true)
      expect(capturedTitle).toBe('Turn 1 · tool_definitions')
      expect(capturedContent).toBeDefined()
      expect(capturedContent.bucket).toBe('tool_definitions')
      expect(capturedContent.tokens).toBe(2800)
      expect(capturedContent.total).toBe(45500)
      expect(capturedContent.label).toBe('Tool definitions')
      expect(capturedContent.content).toEqual(sampleSession.turns![0].content.tool_definitions)

      // Verify ContextBucketInspector properly renders the populated bucket data
      const drawerHtml = renderHtml(
        <SessionInspectorDrawer
          open={true}
          onClose={() => {}}
          title={capturedTitle}
          rawContent={capturedContent}
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
