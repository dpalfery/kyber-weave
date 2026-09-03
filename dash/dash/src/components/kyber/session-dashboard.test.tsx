import { describe, it, expect, beforeEach, vi } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import {
  AgentSessionContent,
  type AgentSessionPayload,
} from '../AgentSessionDashboard'
import {
  SessionInspectorDrawer,
} from '../SessionInspectorDrawer'
import {
  ContextExplorer,
  AgentSessionRow,
  SessionRow,
} from '../ContextExplorer'
import { App, NAV_TABS, type KyberPage } from '../../App'
import type { ContextSessionInfo, ContextTree, ContextSnapshot } from '../../lib/api'
import type { KyberSessionSummary } from '../../lib/kyberApi'

// ---------------------------------------------------------------------------
// Browser Environment Shims in Node
// ---------------------------------------------------------------------------

const listeners: Record<string, Function[]> = {}

if (typeof (globalThis as any).window === 'undefined') {
  ;(globalThis as any).window = {
    matchMedia: () => ({
      matches: false,
      addEventListener: () => {},
      removeEventListener: () => {},
    }),
    localStorage: {
      getItem: () => null,
      setItem: () => {},
      removeItem: () => {},
    },
    addEventListener: (event: string, fn: Function) => {
      listeners[event] = listeners[event] || []
      listeners[event].push(fn)
    },
    removeEventListener: (event: string, fn: Function) => {
      if (listeners[event]) {
        listeners[event] = listeners[event].filter((cb) => cb !== fn)
      }
    },
    dispatchEvent: (event: any) => {
      const cbs = listeners[event.type] || []
      for (const cb of cbs) cb(event)
      return true
    },
  }
} else {
  if (!globalThis.window.addEventListener) {
    ;(globalThis.window as any).addEventListener = (event: string, fn: Function) => {
      listeners[event] = listeners[event] || []
      listeners[event].push(fn)
    }
    ;(globalThis.window as any).removeEventListener = (event: string, fn: Function) => {
      if (listeners[event]) {
        listeners[event] = listeners[event].filter((cb) => cb !== fn)
      }
    }
    ;(globalThis.window as any).dispatchEvent = (event: any) => {
      const cbs = listeners[event.type] || []
      for (const cb of cbs) cb(event)
      return true
    }
  }
}

if (typeof (globalThis as any).KeyboardEvent === 'undefined') {
  ;(globalThis as any).KeyboardEvent = class KeyboardEvent {
    type: string
    key: string
    constructor(type: string, init?: { key?: string }) {
      this.type = type
      this.key = init?.key ?? ''
    }
  }
}

if (typeof (globalThis as any).document === 'undefined') {
  ;(globalThis as any).document = {
    documentElement: {
      classList: {
        contains: () => false,
        toggle: () => false,
        add: () => {},
        remove: () => {},
      },
    },
  }
}

// ---------------------------------------------------------------------------
// React 19 Test Dispatcher & Harness
// ---------------------------------------------------------------------------

let hookStates: unknown[] = []
let hookIndex = 0

function clearHooks() {
  hookStates = []
  hookIndex = 0
}

const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: {
      H?: any
    }
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE

if (reactInternals) {
  reactInternals.H = {
    useState: <T,>(v: T | (() => T)): [T, (val: T | ((prev: T) => T)) => void] => {
      const idx = hookIndex++
      if (idx >= hookStates.length) {
        hookStates.push(typeof v === 'function' ? (v as () => T)() : v)
      }
      const setter = (next: T | ((prev: T) => T)) => {
        hookStates[idx] = typeof next === 'function' ? (next as (prev: T) => T)(hookStates[idx] as T) : next
      }
      return [hookStates[idx] as T, setter]
    },
    useMemo: <T,>(fn: () => T) => fn(),
    useCallback: <T,>(fn: T) => fn,
    useRef: <T,>(v: T) => ({ current: v }),
    useEffect: (fn: () => void | (() => void)) => {
      fn()
    },
    useLayoutEffect: () => {},
    useId: () => 'test-id',
  }
}

function renderHtml(element: React.ReactElement | null | undefined): string {
  if (element == null) return ''
  hookIndex = 0
  return renderToStaticMarkup(element)
}

function findComponent(node: unknown, predicate: (props: any) => boolean): React.ReactElement<any> | null {
  if (node == null) return null
  if (Array.isArray(node)) {
    for (const child of node) {
      const found = findComponent(child, predicate)
      if (found) return found
    }
    return null
  }
  if (React.isValidElement(node)) {
    if (predicate(node.props)) {
      return node
    }
    if (node.props && (node.props as any).children) {
      const found = findComponent((node.props as any).children, predicate)
      if (found) return found
    }
    // Descend THROUGH child function components by rendering them, not just
    // through `children`. Without this the walk stops at the first extracted
    // component, so moving markup out of this file into its own component
    // silently breaks every integration test that looks for a test id inside
    // it — the behaviour is intact, the walker just cannot see it. The hook
    // dispatcher installed above is what makes rendering them here safe.
    if (typeof node.type === 'function') {
      try {
        const rendered = (node.type as (props: unknown) => unknown)(node.props)
        const found = findComponent(rendered, predicate)
        if (found) return found
      } catch {
        // A component that cannot render in this harness is not a match;
        // keep walking the rest of the tree rather than failing the search.
      }
    }
  }
  return null
}

function findElementByTestId(node: unknown, testId: string): React.ReactElement<any> | null {
  return findComponent(node, (props) => props?.['data-testid'] === testId)
}

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
    },
  })
}

// ---------------------------------------------------------------------------
// Test Fixtures
// ---------------------------------------------------------------------------

const sampleSessionPayload: AgentSessionPayload = {
  id: 'sess-test-copilot',
  session_id: 'sess-test-copilot',
  harness: 'copilot',
  label: 'Fix parser recursion in AST traversal',
  repo: 'github.com/my-org/kyber-weave',
  branch: 'main',
  agent_name: 'copilot-agent',
  span_count: 12,
  summary: {
    turn_count: 3,
    reported_turn_count: 3,
    request_count: 1,
    total_input: 45000,
    total_output: 3200,
    total_cache_read: 20000,
    total_cache_creation: 5000,
    cache_hit_ratio: 0.44,
    duration_ms: 15400,
    models: ['claude-3-5-sonnet'],
    tool_calls: 4,
    tools_invoked: 1,
    tools_offered: 2,
    schema_tokens_per_turn: 1200,
    unused_schema_per_turn: 400,
    defs_turns: 3,
    cost: {
      usd: 0.185,
      credits: 0.925,
      basis: 'published_rates',
      status: 'ok',
    },
  },
  turns: [
    {
      index: 1,
      spanId: 'turn-span-1',
      model: 'claude-3-5-sonnet',
      durationMs: 4200,
      tokens: {
        fresh_input: 8000,
        cache_read: 5000,
        cache_creation: 2000,
        output: 1200,
      },
      content: {
        prompt_text:
          '<instructions>Analyze the AST recursion bug in ast.ts and provide a patch.</instructions><environment_info>macOS arm64 Darwin 24.0.0 node 22.12.0</environment_info>Investigating recursion depth in parseExpression.',
      },
      has_tool_defs: true,
    },
    {
      index: 2,
      spanId: 'turn-span-2',
      model: 'claude-3-5-sonnet',
      durationMs: 6100,
      tokens: {
        fresh_input: 12000,
        cache_read: 8000,
        cache_creation: 1500,
        output: 1100,
      },
      content: 'Applying patch to limit recursion depth.',
      has_tool_defs: false,
    },
  ],
  context: {
    contextLimit: 200000,
    turns: [
      {
        turn: 1,
        buckets: {
          system_prompt: 1500,
          instruction_context: 2000,
          tool_definitions: 1200,
          conversation_history: 3000,
          tool_result_content: 800,
          residual: 500,
        },
        headroom: 191000,
        pressure: 0.045,
        accumulationRate: 9000,
        freshInput: 9000,
      },
      {
        turn: 2,
        buckets: {
          system_prompt: 1500,
          instruction_context: 2000,
          tool_definitions: 1200,
          conversation_history: 6000,
          tool_result_content: 2500,
          residual: 800,
        },
        headroom: 186000,
        pressure: 0.07,
        accumulationRate: 5000,
        freshInput: 5000,
      },
    ],
  },
  tools: [
    {
      name: 'execute_command',
      server: 'built-in',
      schema_tokens: 800,
      turns_resident: 2,
      total_schema_cost: 1600,
      invocations: 4,
      cost_per_invocation: 400,
      result_tokens: 1500,
      is_mcp: false,
    },
    {
      name: 'unused_helper',
      server: 'custom-mcp',
      schema_tokens: 400,
      turns_resident: 2,
      total_schema_cost: 800,
      invocations: 0,
      cost_per_invocation: 0,
      result_tokens: 0,
      is_mcp: true,
    },
  ],
  timeline: [
    {
      spanId: 'span-root-1',
      name: 'agent:solve_bug',
      op: 'invoke_agent',
      kind: 'agent',
      durationMs: 15400,
      offsetMs: 0,
      attributes: {
        'agent.name': 'copilot-agent',
        'task.description': 'Fix parser recursion',
      },
      children: [
        {
          spanId: 'span-tool-1',
          name: 'execute_tool:execute_command',
          op: 'execute_tool',
          kind: 'tool',
          durationMs: 3200,
          offsetMs: 2000,
          attributes: {
            'tool.name': 'execute_command',
            command: 'npm test',
            exit_code: 0,
          },
        },
      ],
    },
  ],
}

const sampleAgentSummaries: KyberSessionSummary[] = [
  {
    session_id: 'sess-test-copilot',
    harness: 'copilot',
    label: 'Fix parser recursion in AST traversal',
    is_subagent: false,
    parent_session: null,
    agent_name: 'copilot-agent',
    repo: 'github.com/my-org/kyber-weave',
    branch: 'main',
    started: '2026-03-01T12:00:00.000Z',
    turn_count: 3,
    cost_usd: 0.185,
    models: ['claude-3-5-sonnet'],
  },
]

const sampleCliSession: ContextSessionInfo = {
  provider: 'claude',
  sessionId: 'cli-sess-claude-1',
  project: 'kyber-weave',
  title: 'Refactor CLI command parser',
  mtimeMs: 1772450000000,
  sizeBytes: 10240,
}

const sampleSnapshot: ContextSnapshot = {
  messages: 4,
  tokens: 16000,
  assistant: {
    count: 2,
    tokens: 6000,
    text: { count: 2, tokens: 6000 },
    reasoning: { count: 0, tokens: 0 },
    toolCall: { count: 0, tokens: 0 },
    byTool: [],
  },
  user: {
    count: 2,
    tokens: 10000,
    text: { count: 2, tokens: 10000 },
    image: { count: 0, tokens: 0 },
    compactSummary: { count: 0, tokens: 0 },
    meta: { count: 0, tokens: 0 },
  },
  toolResult: { count: 0, tokens: 0 },
  system: { count: 0, tokens: 0 },
}

const sampleCliTree: ContextTree = {
  session: {
    sessionId: 'cli-sess-claude-1',
    project: 'kyber-weave',
    mtimeMs: 1772450000000,
    sizeBytes: 10240,
  },
  model: 'claude-3-5-sonnet',
  compactions: 0,
  reported: null,
  effective: sampleSnapshot,
  full: sampleSnapshot,
  effectiveRows: [
    {
      label: 'User initial prompt',
      depth: 0,
      count: 1,
      tokens: 5000,
    },
    {
      label: 'Assistant reply',
      depth: 1,
      count: 1,
      tokens: 3000,
    },
  ],
  fullRows: [],
}

// ---------------------------------------------------------------------------
// Test Suites
// ---------------------------------------------------------------------------

describe('AgentSessionDashboard & SessionInspectorDrawer Integration', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('clicking a turn triggers drawer open with turn token details and folded XML tags', () => {
    clearHooks()
    const tree = AgentSessionContent({ session: sampleSessionPayload })
    expect(tree).toBeDefined()

    // Find spend charts element with onSelectTurn prop
    const spendCharts = findComponent(tree, (p) => typeof p?.onSelectTurn === 'function')
    expect(spendCharts).not.toBeNull()
    expect(spendCharts?.props.onSelectTurn).toBeTypeOf('function')

    // Trigger selecting Turn 1
    spendCharts?.props.onSelectTurn(1)
    expect(hookStates[0]).toBe(true) // drawerOpen
    expect(hookStates[1]).toBe('Turn 1') // drawerTitle
    expect(hookStates[3]).toBeDefined() // drawerContent
    const turnContent = hookStates[3] as any
    expect(turnContent.index).toBe(1)
    expect(turnContent.tokens.fresh_input).toBe(8000)

    // Verify that SessionInspectorDrawer renders the turn and folds the XML tags
    const drawerHtml = renderHtml(
      <SessionInspectorDrawer
        open={true}
        onClose={() => {}}
        title={hookStates[1] as string}
        subtitle={hookStates[2] as string | undefined}
        rawContent={turnContent}
      />
    )

    expect(drawerHtml).toContain('data-testid="session-inspector-drawer"')
    expect(drawerHtml).toContain('Turn 1')
    expect(drawerHtml).toContain('data-testid="xml-folded-content"')
    expect(drawerHtml).toContain('data-testid="folded-tag-instructions"')
    expect(drawerHtml).toContain('data-testid="folded-tag-environment_info"')
    expect(drawerHtml).toContain('&lt;instructions&gt;')
    expect(drawerHtml).toContain('&lt;environment_info&gt;')
    expect(drawerHtml).toContain('Analyze the AST recursion bug in ast.ts and provide a patch.')
    expect(drawerHtml).toContain('macOS arm64 Darwin 24.0.0 node 22.12.0')
  })

  it('clicking a turn bucket in Context Composition triggers bucket-specific drawer inspection', () => {
    clearHooks()
    const tree = AgentSessionContent({ session: sampleSessionPayload })
    const spendCharts = findComponent(tree, (p) => typeof p?.onSelectTurn === 'function')
    expect(spendCharts?.props.onSelectTurn).toBeTypeOf('function')

    // Select turn 1 with bucket 'tool_definitions'
    spendCharts?.props.onSelectTurn(1, 'tool_definitions')
    expect(hookStates[0]).toBe(true) // drawerOpen
    expect(hookStates[1]).toContain('Turn 1 · tool_definitions') // drawerTitle
    expect(hookStates[3]).toBeDefined() // drawerContent
    const bucketContent = hookStates[3] as any
    expect(bucketContent.bucket).toBe('tool_definitions')
    expect(bucketContent.tokens).toBe(1200)

    const drawerHtml = renderHtml(
      <SessionInspectorDrawer
        open={true}
        onClose={() => {}}
        title={hookStates[1] as string}
        rawContent={bucketContent}
      />
    )
    expect(drawerHtml).toContain('Turn 1 · tool_definitions')
    expect(drawerHtml).toContain('1.2K')
  })

  it('clicking a tool in the tool schema ranking table triggers drawer open with tool schema JSON', () => {
    clearHooks()
    const tree = AgentSessionContent({ session: sampleSessionPayload })
    const toolRow = findElementByTestId(tree, 'tool-row-execute_command')
    expect(toolRow).not.toBeNull()
    expect(toolRow?.props.onClick).toBeTypeOf('function')

    toolRow?.props.onClick()
    expect(hookStates[0]).toBe(true) // drawerOpen
    expect(hookStates[1]).toBe('Tool: execute_command') // drawerTitle
    expect(hookStates[2]).toContain('Server: built-in') // drawerSubtitle
    expect(hookStates[2]).toContain('Invocations: 4')
    expect(hookStates[3]).toBeDefined() // drawerContent
    const toolContent = hookStates[3] as any
    expect(toolContent.tool.name).toBe('execute_command')
    expect(toolContent.tool.schema_tokens).toBe(800)

    const drawerHtml = renderHtml(
      <SessionInspectorDrawer
        open={true}
        onClose={() => {}}
        title={hookStates[1] as string}
        subtitle={hookStates[2] as string | undefined}
        rawContent={toolContent}
      />
    )
    expect(drawerHtml).toContain('Tool: execute_command')
    expect(drawerHtml).toContain('execute_command')
    expect(drawerHtml).toContain('800')
  })

  it('clicking a timeline span in duration bars view triggers drawer open with span attributes', () => {
    clearHooks()
    const tree = AgentSessionContent({
      session: sampleSessionPayload,
      initialTimelineTab: 'bars',
    })
    const barRow = findElementByTestId(tree, 'timeline-bar-row')
    expect(barRow).not.toBeNull()
    expect(barRow?.props.onClick).toBeTypeOf('function')

    barRow?.props.onClick()
    expect(hookStates[0]).toBe(true) // drawerOpen
    expect(hookStates[1]).toBe('Span: agent:solve_bug') // drawerTitle
    expect(hookStates[2]).toContain('agent') // drawerSubtitle
    expect(hookStates[2]).toContain('Span ID: span-root-1')
    expect(hookStates[3]).toBeDefined() // drawerContent
    const spanContent = hookStates[3] as any
    expect(spanContent.spanId).toBe('span-root-1')

    const drawerHtml = renderHtml(
      <SessionInspectorDrawer
        open={true}
        onClose={() => {}}
        title={hookStates[1] as string}
        subtitle={hookStates[2] as string | undefined}
        rawContent={spanContent}
      />
    )
    expect(drawerHtml).toContain('Span: agent:solve_bug')
    expect(drawerHtml).toContain('span-root-1')
    expect(drawerHtml).toContain('copilot-agent')
    expect(drawerHtml).toContain('Fix parser recursion')
  })

  it('closes drawer when clicking backdrop', () => {
    let closed = false
    const tree = SessionInspectorDrawer({
      open: true,
      onClose: () => { closed = true },
      title: 'Test Drawer',
    })
    expect(tree).not.toBeNull()

    const backdrop = findElementByTestId(tree, 'drawer-backdrop')
    expect(backdrop).not.toBeNull()
    backdrop?.props.onClick()
    expect(closed).toBe(true)
  })

  it('closes drawer when clicking close button', () => {
    let closed = false
    const tree = SessionInspectorDrawer({
      open: true,
      onClose: () => { closed = true },
      title: 'Test Drawer',
    })
    expect(tree).not.toBeNull()

    const closeBtn = findElementByTestId(tree, 'drawer-close-button')
    expect(closeBtn).not.toBeNull()
    closeBtn?.props.onClick()
    expect(closed).toBe(true)
  })

  it('closes drawer when pressing Escape key', () => {
    const onClose = vi.fn()
    const listenersMap: Record<string, (e: any) => void> = {}
    const origAdd = globalThis.window.addEventListener
    const origRemove = globalThis.window.removeEventListener

    try {
      globalThis.window.addEventListener = vi.fn((event: string, handler: any) => {
        listenersMap[event] = handler
      })
      globalThis.window.removeEventListener = vi.fn()

      const internals = (React as any).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE
      const prevDispatcher = internals?.H
      try {
        if (internals) {
          internals.H = {
            useEffect: (effect: any) => {
              effect()
            },
            useState: (initial: any) => [typeof initial === 'function' ? initial() : initial, () => {}],
            useMemo: (factory: any) => factory(),
            useCallback: (fn: any) => fn,
            useRef: (v: any) => ({ current: v }),
          }
        }
        SessionInspectorDrawer({ open: true, onClose, title: 'Test Drawer' })
      } finally {
        if (internals) {
          internals.H = prevDispatcher
        }
      }

      expect(listenersMap['keydown']).toBeTypeOf('function')
      listenersMap['keydown']({ key: 'Escape' })
      expect(onClose).toHaveBeenCalled()
    } finally {
      globalThis.window.addEventListener = origAdd
      globalThis.window.removeEventListener = origRemove
    }
  })

  it('does not render drawer DOM when open is false', () => {
    const tree = SessionInspectorDrawer({
      open: false,
      onClose: () => {},
      title: 'Hidden Drawer',
    })
    expect(tree).toBeNull()
  })
})

describe('ContextExplorer Integration with Agent and CLI Sessions', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('selecting copilot-agent queries agent sessions and clicking a row renders AgentSessionDashboard', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-sessions', 'copilot'], sampleAgentSummaries)
    qc.setQueryData(['kyber-session', 'sess-test-copilot'], sampleSessionPayload)

    // 1. Initial render of ContextExplorer with copilot-agent harness
    const explorerHtml = renderHtml(
      <QueryClientProvider client={qc}>
        <ContextExplorer activeHarness="copilot-agent" />
      </QueryClientProvider>
    )

    expect(explorerHtml).toContain('data-testid="agent-session-row-sess-test-copilot"')
    expect(explorerHtml).toContain('Fix parser recursion in AST traversal')
    expect(explorerHtml).toContain('copilot-agent')

    // 2. Render the agent session row as expanded (open=true)
    const openRowHtml = renderHtml(
      <QueryClientProvider client={qc}>
        <AgentSessionRow
          s={sampleAgentSummaries[0]}
          open={true}
          onToggle={() => {}}
          onSelectSession={() => {}}
        />
      </QueryClientProvider>
    )

    // Verified: AgentSessionDashboard is embedded in place of the CLI TreeTable
    expect(openRowHtml).toContain('data-testid="agent-session-dashboard"')
    expect(openRowHtml).toContain('data-testid="overview-strip-section"')
    expect(openRowHtml).toContain('data-testid="spend-composition-section"')
    expect(openRowHtml).toContain('data-testid="tool-schema-section"')
    expect(openRowHtml).not.toContain('data-testid="tree-table"')
  })

  it('selecting claude queries CLI sessions and renders TreeTable', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['context-sessions', 'claude'], [sampleCliSession])
    qc.setQueryData(['context-tree', 'claude', 'cli-sess-claude-1'], sampleCliTree)

    // 1. Initial render with claude harness
    const explorerHtml = renderHtml(
      <QueryClientProvider client={qc}>
        <ContextExplorer activeHarness="claude" />
      </QueryClientProvider>
    )

    expect(explorerHtml).toContain('data-testid="cli-session-row-cli-sess-claude-1"')
    expect(explorerHtml).toContain('Refactor CLI command parser')

    // 2. Render CLI session row as expanded (open=true)
    const openRowHtml = renderHtml(
      <QueryClientProvider client={qc}>
        <SessionRow
          s={sampleCliSession}
          open={true}
          onToggle={() => {}}
        />
      </QueryClientProvider>
    )

    // Verified: TreeTable is rendered for CLI transcripts, not AgentSessionDashboard
    expect(openRowHtml).toContain('data-testid="tree-table"')
    expect(openRowHtml).toContain('User initial prompt')
    expect(openRowHtml).toContain('Assistant reply')
    expect(openRowHtml).not.toContain('data-testid="agent-session-dashboard"')
  })
})

describe('App: Global Top Navigation Integration', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('switches smoothly between [Usage], [Context], [Compare], [Quarantine], [Problems] tabs without errors', () => {
    const qc = createTestQueryClient()

    // Pre-populate global endpoint data
    qc.setQueryData(['kyber-compare'], {
      harnesses: ['copilot', 'pi', 'gemini'],
      rows: [
        {
          metric: 'tokens_per_turn',
          kind: 'per_turn',
          label: 'Tokens per turn',
          unit: 'tokens',
          cells: {
            copilot: { measurable: true, availability: 'measured', value: 1250, render: '1,250' },
            pi: { measurable: false, availability: 'not_measurable', render: 'not measurable' },
            gemini: { measurable: true, availability: 'measured', value: 980, render: '980' },
          },
        },
      ],
      problems: [],
    })
    qc.setQueryData(['kyber-quarantine'], {
      entries: [
        { spanId: 'quar-101', namespaces: ['unclaimed.ns'], reason: 'No adapter handler' },
      ],
    })
    qc.setQueryData(['kyber-problems'], {
      problems: [
        { severity: 'error', code: 'token_mismatch', message: 'Reconciliation difference', spanId: 'span-err-1' },
      ],
    })

    const tabs: Array<{ page: KyberPage; expectedSnippet: string }> = [
      { page: 'usage', expectedSnippet: 'data-testid="nav-tab-usage"' },
      { page: 'context', expectedSnippet: 'Context' },
      { page: 'compare', expectedSnippet: 'Tokens per turn' },
      { page: 'quarantine', expectedSnippet: 'quar-101' },
      { page: 'problems', expectedSnippet: 'token_mismatch' },
    ]

    for (const { page, expectedSnippet } of tabs) {
      clearHooks()
      const html = renderHtml(
        <QueryClientProvider client={qc}>
          <App initialPage={page} />
        </QueryClientProvider>
      )

      // Verified: Header navigation contains all 5 tabs
      for (const tab of NAV_TABS) {
        expect(html).toContain(`data-testid="nav-tab-${tab.key}"`)
      }

      // Verified: Active tab receives active highlight class
      const activeBtn = html.match(new RegExp(`<button[^>]*data-testid="nav-tab-${page}"[^>]*>`))?.[0]
      expect(activeBtn).toContain('bg-active-primary')

      // Verified: Panel renders corresponding content without blank screen
      expect(html).toContain(expectedSnippet)
    }
  })
})
