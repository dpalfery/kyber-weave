import { describe, it, expect, vi, beforeEach } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import {
  ContextExplorer,
  SessionRow,
  AgentSessionRow,
  SessionDetails,
  SessionDetailsBoundary,
  TreeTable,
  isAgentHarness,
  getAgentHarnessFilter,
  formatSessionTime,
  PROVIDERS,
} from './ContextExplorer'
import type { ContextRow, ContextSessionInfo, ContextTree } from '../lib/api'
import type { KyberSessionSummary } from '../lib/kyberApi'

// ---------------------------------------------------------------------------
// React 19 Test Dispatcher & Hooks Harness
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
    useEffect: () => {},
    useLayoutEffect: () => {},
    useId: () => 'test-id',
  }
}

function renderHtml(element: React.ReactElement | null | undefined): string {
  if (element == null) return ''
  hookIndex = 0
  return renderToStaticMarkup(element)
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

const sampleCliSessions: ContextSessionInfo[] = [
  {
    provider: 'claude',
    sessionId: 'sess-claude-001',
    project: 'kyber-project',
    title: 'Fix AST tokenizer bug',
    mtimeMs: Date.now() - 1000 * 60 * 30, // 30m ago
    sizeBytes: 1024 * 1024 * 1.5,
  },
  {
    provider: 'claude',
    sessionId: 'sess-claude-002',
    project: 'kyber-project',
    title: 'Streaming parser implementation',
    mtimeMs: Date.now() - 1000 * 60 * 180, // 3h ago
    sizeBytes: 1024 * 1024 * 0.8,
  },
]

const sampleCliRows: ContextRow[] = [
  { depth: 0, label: 'System prompt', count: 1, tokens: 4200, bold: true },
  { depth: 0, label: 'Conversation history', count: 8, tokens: 28000, bold: true },
  { depth: 1, label: 'User turns', count: 4, tokens: 16000 },
  { depth: 1, label: 'Assistant turns', count: 4, tokens: 12000 },
  { depth: 0, label: 'Tool call results', count: 3, tokens: 9500, bold: true },
]

const sampleCliTree: ContextTree = {
  session: {
    sessionId: 'sess-claude-001',
    project: 'kyber-project',
    mtimeMs: Date.now() - 1000 * 60 * 30,
    sizeBytes: 1024 * 1024 * 1.5,
  },
  model: 'claude-sonnet-4-6',
  compactions: 2,
  reported: {
    context: 52000,
    window: 200000,
  },
  effective: {
    messages: 8,
    tokens: 41700,
    assistant: {
      count: 4,
      tokens: 18000,
      text: { count: 4, tokens: 12000 },
      reasoning: { count: 1, tokens: 2000 },
      toolCall: { count: 3, tokens: 4000 },
      byTool: [],
    },
    user: {
      count: 4,
      tokens: 23700,
      text: { count: 4, tokens: 23700 },
      image: { count: 0, tokens: 0 },
      compactSummary: { count: 0, tokens: 0 },
      meta: { count: 0, tokens: 0 },
    },
    toolResult: { count: 3, tokens: 9500 },
    system: { count: 1, tokens: 4200 },
  },
  full: {
    messages: 16,
    tokens: 85000,
    assistant: {
      count: 8,
      tokens: 38000,
      text: { count: 8, tokens: 25000 },
      reasoning: { count: 2, tokens: 4000 },
      toolCall: { count: 6, tokens: 9000 },
      byTool: [],
    },
    user: {
      count: 8,
      tokens: 47000,
      text: { count: 8, tokens: 47000 },
      image: { count: 0, tokens: 0 },
      compactSummary: { count: 0, tokens: 0 },
      meta: { count: 0, tokens: 0 },
    },
    toolResult: { count: 6, tokens: 18000 },
    system: { count: 1, tokens: 4200 },
  },
  effectiveRows: sampleCliRows,
  fullRows: sampleCliRows,
}

const sampleAgentSessions: KyberSessionSummary[] = [
  {
    session_id: 'sess-copilot-root',
    harness: 'copilot',
    label: 'Fix parser recursion in AST traversal',
    is_subagent: false,
    parent_session: null,
    agent_name: 'copilot-agent',
    repo: 'github.com/my-org/kyber-weave',
    branch: 'main',
    started: '2026-03-01T12:00:00.000Z',
    turn_count: 10,
    cost_usd: 0.185,
    models: ['claude-3-5-sonnet'],
  },
  {
    session_id: 'sess-copilot-sub',
    harness: 'copilot',
    label: 'Inspect node syntax definitions',
    is_subagent: true,
    parent_session: 'sess-copilot-root',
    agent_name: 'copilot-subagent',
    repo: 'github.com/my-org/kyber-weave',
    branch: 'main',
    started: '2026-03-01T12:05:00.000Z',
    turn_count: 4,
    cost_usd: 0.042,
    models: ['claude-3-5-sonnet'],
  },
  {
    session_id: 'sess-pi-001',
    harness: 'pi',
    label: 'Pi agent autonomous run',
    is_subagent: false,
    parent_session: null,
    started: '2026-03-01T11:00:00.000Z',
    turn_count: 6,
    cost_usd: 0.075,
  },
  {
    session_id: 'sess-gemini-001',
    harness: 'gemini',
    label: 'Antigravity workspace optimization',
    is_subagent: false,
    parent_session: null,
    started: '2026-03-01T10:00:00.000Z',
    turn_count: 12,
    cost_usd: 0.11,
  },
]

// ---------------------------------------------------------------------------
// Test Suites
// ---------------------------------------------------------------------------

describe('ContextExplorer: Helpers & Harness Identification', () => {
  it('correctly classifies agent harnesses vs CLI providers', () => {
    // Agent harnesses
    expect(isAgentHarness('agent-all')).toBe(true)
    expect(isAgentHarness('copilot-agent')).toBe(true)
    expect(isAgentHarness('copilot-vscode')).toBe(true)
    expect(isAgentHarness('copilot')).toBe(true)
    expect(isAgentHarness('pi')).toBe(true)
    expect(isAgentHarness('antigravity')).toBe(true)
    expect(isAgentHarness('gemini')).toBe(true)

    // CLI providers
    expect(isAgentHarness('claude')).toBe(false)
    expect(isAgentHarness('codex')).toBe(false)
    expect(isAgentHarness('copilot-cli')).toBe(false)
    expect(isAgentHarness('opencode')).toBe(false)
    expect(isAgentHarness('kilo-code')).toBe(false)
    expect(isAgentHarness('cursor')).toBe(false)
  })

  it('maps providers to their respective backend harness filters', () => {
    expect(getAgentHarnessFilter('copilot-agent')).toBe('copilot')
    expect(getAgentHarnessFilter('copilot-vscode')).toBe('copilot')
    expect(getAgentHarnessFilter('copilot')).toBe('copilot')
    expect(getAgentHarnessFilter('pi')).toBe('pi')
    expect(getAgentHarnessFilter('antigravity')).toBe('gemini')
    expect(getAgentHarnessFilter('gemini')).toBe('gemini')
    expect(getAgentHarnessFilter('agent-all')).toBeNull()
    expect(getAgentHarnessFilter('claude')).toBeNull()
  })

  it('formats session timestamps safely', () => {
    expect(formatSessionTime(null)).toBe('—')
    expect(formatSessionTime(undefined)).toBe('—')
    expect(formatSessionTime('')).toBe('—')
    // Numeric timestamp
    const fiveMinAgo = Date.now() - 5 * 60 * 1000
    expect(formatSessionTime(fiveMinAgo)).toBe('5m ago')
    // ISO string
    const isoFiveMinAgo = new Date(Date.now() - 10 * 60 * 1000).toISOString()
    expect(formatSessionTime(isoFiveMinAgo)).toBe('10m ago')
  })

  it('contains Agent Sessions (All) in PROVIDERS array', () => {
    const allTab = PROVIDERS.find((p) => p.key === 'agent-all')
    expect(allTab).toBeDefined()
    expect(allTab?.label).toBe('Agent Sessions (All)')
  })
})

describe('ContextExplorer: TreeTable & CLI Details Component', () => {
  it('renders TreeTable with rows, labels, and token counts', () => {
    const html = renderHtml(<TreeTable rows={sampleCliRows} />)
    expect(html).toContain('data-testid="tree-table"')
    expect(html).toContain('System prompt')
    expect(html).toContain('Conversation history')
    expect(html).toContain('User turns')
    expect(html).toContain('Assistant turns')
    expect(html).toContain('Tool call results')
    // Formatted tokens
    expect(html).toContain('4.2K')
    expect(html).toContain('28.0K')
    expect(html).toContain('16.0K')
    expect(html).toContain('9.5K')
  })

  it('renders TreeTable empty message when no rows are available', () => {
    const html = renderHtml(<TreeTable rows={[]} />)
    expect(html).toContain('No context block breakdown available for this session')
  })

  it('renders SessionDetails with metric chips and TreeTable', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['context-tree', 'claude', 'sess-claude-001'], sampleCliTree)

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <SessionDetails provider="claude" id="sess-claude-001" />
      </QueryClientProvider>
    )

    expect(html).toContain('data-testid="cli-session-details"')
    expect(html).toContain('Messages')
    expect(html).toContain('Est. tokens')
    expect(html).toContain('Context (exact)')
    expect(html).toContain('Compactions')
    expect(html).toContain('52.0K / 200.0K')
    expect(html).toContain('Live window')
    expect(html).toContain('Full history')
    expect(html).toContain('data-testid="tree-table"')
  })
})

describe('ContextExplorer: CLI Provider Sessions Workflow', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('renders CLI provider sessions and displays TreeTable when expanded', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['context-sessions', 'claude'], sampleCliSessions)
    qc.setQueryData(['context-tree', 'claude', 'sess-claude-001'], sampleCliTree)

    // 1. Initial render of ContextExplorer with claude as provider
    const htmlCollapsed = renderHtml(
      <QueryClientProvider client={qc}>
        <ContextExplorer activeHarness="claude" />
      </QueryClientProvider>
    )

    // Verify session rows are rendered
    expect(htmlCollapsed).toContain('Fix AST tokenizer bug')
    expect(htmlCollapsed).toContain('Streaming parser implementation')
    expect(htmlCollapsed).toContain('kyber-project')
    expect(htmlCollapsed).toContain('sess-cl')

    // 2. Render SessionRow directly in open state to verify TreeTable mounts inside SessionDetailsBoundary
    const htmlOpen = renderHtml(
      <QueryClientProvider client={qc}>
        <SessionRow
          s={sampleCliSessions[0]}
          open={true}
          onToggle={() => {}}
        />
      </QueryClientProvider>
    )

    // Verify TreeTable and CLI metric chips are rendered inside open session
    expect(htmlOpen).toContain('data-testid="tree-table"')
    expect(htmlOpen).toContain('data-testid="cli-session-details"')
    expect(htmlOpen).toContain('System prompt')
    expect(htmlOpen).toContain('4.2K')
    expect(htmlOpen).toContain('Context (exact)')
  })
})

describe('ContextExplorer: Agent Harness Workflow & AgentSessionDashboard', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('fetches from /api/kyber/sessions and renders AgentSessionRows with harness badge, turns, cost, and timestamp', () => {
    const qc = createTestQueryClient()
    // Pre-populate kyber-sessions for copilot harness
    qc.setQueryData(['kyber-sessions', 'copilot'], [sampleAgentSessions[0], sampleAgentSessions[1]])

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <ContextExplorer activeHarness="copilot-agent" />
      </QueryClientProvider>
    )

    // Harness badge
    expect(html).toContain('data-testid="agent-harness-badge"')
    expect(html).toContain('copilot')

    // Labels
    expect(html).toContain('Fix parser recursion in AST traversal')
    expect(html).toContain('Inspect node syntax definitions')

    // Subagent tag
    expect(html).toContain('subagent')

    // Parent session link
    expect(html).toContain('data-testid="parent-session-link"')
    expect(html).toContain('parent: sess-cop')

    // Turns
    expect(html).toContain('10 turns')
    expect(html).toContain('4 turns')

    // Cost in USD
    expect(html).toContain('$0.185')
    expect(html).toContain('$0.042')
  })

  it('queries all agent sessions when agent-all is selected', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-sessions', null], sampleAgentSessions)

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <ContextExplorer activeHarness="agent-all" />
      </QueryClientProvider>
    )

    expect(html).toContain('Fix parser recursion in AST traversal')
    expect(html).toContain('Pi agent autonomous run')
    expect(html).toContain('Antigravity workspace optimization')
    expect(html).toContain('pi')
    expect(html).toContain('gemini')
    expect(html).toContain('copilot')
  })

  it('expanding an agent session mounts AgentSessionDashboard inside SessionDetailsBoundary', () => {
    const qc = createTestQueryClient()
    const sessionToOpen = sampleAgentSessions[0]

    // Pre-populate both query keys
    qc.setQueryData(['kyber-sessions', 'copilot'], [sessionToOpen])
    qc.setQueryData(['kyber-session', sessionToOpen.session_id], {
      id: sessionToOpen.session_id,
      session_id: sessionToOpen.session_id,
      harness: sessionToOpen.harness,
      label: sessionToOpen.label,
      summary: {
        turn_count: 10,
        total_input: 50000,
        total_output: 5000,
        cost: { usd: 0.185, basis: 'published_rates', status: 'ok' },
        duration_ms: 25000,
      },
      turns: [],
      tools: [],
      timeline: [],
    })

    // Render AgentSessionRow open
    const htmlOpen = renderHtml(
      <QueryClientProvider client={qc}>
        <AgentSessionRow
          s={sessionToOpen}
          open={true}
          onToggle={() => {}}
          onSelectSession={() => {}}
        />
      </QueryClientProvider>
    )

    // Verify AgentSessionDashboard is mounted
    expect(htmlOpen).toContain('data-testid="agent-session-dashboard"')
    expect(htmlOpen).toContain('Fix parser recursion in AST traversal')
    expect(htmlOpen).toContain('data-testid="overview-strip-section"')
  })

  it('renders empty message when no agent sessions exist for harness', () => {
    const qc = createTestQueryClient()
    qc.setQueryData(['kyber-sessions', 'pi'], [])

    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <ContextExplorer activeHarness="pi" />
      </QueryClientProvider>
    )

    expect(html).toContain('No agent sessions found for this harness')
  })
})

describe('ContextExplorer: Parent Session Navigation', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('clicking parent session link calls onSelectSession with parentSessionId', () => {
    const subagentSession = sampleAgentSessions[1] // sess-copilot-sub, parent is sess-copilot-root
    const onSelectSessionMock = vi.fn()

    // 1. Verify AgentSessionRow has the parent session button with parent ID
    const tree = AgentSessionRow({
      s: subagentSession,
      open: false,
      onToggle: () => {},
      onSelectSession: onSelectSessionMock,
    })

    // Walk React element to locate parent-session-link
    let parentBtn: any = null
    const walk = (node: any) => {
      if (!node) return
      if (node.props?.['data-testid'] === 'parent-session-link') {
        parentBtn = node
        return
      }
      if (node.props?.children) {
        React.Children.forEach(node.props.children, walk)
      }
    }
    walk(tree)

    expect(parentBtn).not.toBeNull()
    expect(parentBtn.props.title).toContain('Parent session: sess-copilot-root')

    // Simulate click on parent session link
    const stopPropagationMock = vi.fn()
    parentBtn.props.onClick({ stopPropagation: stopPropagationMock })

    expect(stopPropagationMock).toHaveBeenCalled()
    expect(onSelectSessionMock).toHaveBeenCalledWith('sess-copilot-root')
  })

  it('navigates to parent session when parent link is activated in ContextExplorer', () => {
    const qc = createTestQueryClient()
    const parentSession = sampleAgentSessions[0] // sess-copilot-root
    const subSession = sampleAgentSessions[1] // sess-copilot-sub

    qc.setQueryData(['kyber-sessions', 'copilot'], [parentSession, subSession])
    qc.setQueryData(['kyber-session', parentSession.session_id], {
      id: parentSession.session_id,
      session_id: parentSession.session_id,
      harness: 'copilot',
      label: parentSession.label,
      summary: { turn_count: 10, cost: { usd: 0.185 } },
      turns: [],
    })
    qc.setQueryData(['kyber-session', subSession.session_id], {
      id: subSession.session_id,
      session_id: subSession.session_id,
      harness: 'copilot',
      label: subSession.label,
      summary: { turn_count: 4, cost: { usd: 0.042 } },
      turns: [],
    })

    // Render subagent open directly
    const subHtml = renderHtml(
      <QueryClientProvider client={qc}>
        <AgentSessionRow
          s={subSession}
          open={true}
          onToggle={() => {}}
          onSelectSession={() => {}}
        />
      </QueryClientProvider>
    )

    // Subagent row is open, showing its dashboard
    expect(subHtml).toContain('data-testid="agent-session-dashboard"')
    expect(subHtml).toContain(subSession.label)

    // Render parent session open
    const parentHtml = renderHtml(
      <QueryClientProvider client={qc}>
        <AgentSessionRow
          s={parentSession}
          open={true}
          onToggle={() => {}}
          onSelectSession={() => {}}
        />
      </QueryClientProvider>
    )

    // Parent session is now open, showing parent dashboard
    expect(parentHtml).toContain('data-testid="agent-session-dashboard"')
    expect(parentHtml).toContain(parentSession.label)
  })
})

describe('ContextExplorer: Boundary and Error Handling', () => {
  it('catches render error gracefully in SessionDetailsBoundary', () => {
    const boundary = new SessionDetailsBoundary({ children: null })
    boundary.state = { error: new Error('Exploded in child component') }
    const html = renderHtml(boundary.render() as React.ReactElement)

    expect(html).toContain('Failed to render session context: Exploded in child component')
    expect(html).toContain('data-testid="session-details-error"')
  })
})
