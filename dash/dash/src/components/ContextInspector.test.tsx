import { describe, it, expect, vi, afterEach } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import {
  ContextInspector,
  BlockRail,
  PartTabs,
  ContentPane,
  CopyButton,
  copyToClipboard,
} from './ContextInspector'
import { SessionInspectorDrawer } from './SessionInspectorDrawer'
import type { KyberTurnContentResult, KyberTurnContentPart } from '../lib/kyberApi'

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

// Set up React 19 test hook dispatcher for static markup and node test runs
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
    useId: () => 'test-context-inspector-id',
  }
}

function renderHtml(element: React.ReactElement | null | undefined, qc = createTestQueryClient()): string {
  if (element == null) return ''
  return renderToStaticMarkup(
    React.createElement(QueryClientProvider, { client: qc }, element)
  )
}

function findNodeByTestId(node: unknown, testId: string): React.ReactElement<any> | null {
  if (node == null) return null
  if (Array.isArray(node)) {
    for (const child of node) {
      const found = findNodeByTestId(child, testId)
      if (found) return found
    }
    return null
  }
  if (React.isValidElement(node)) {
    const props = node.props as Record<string, any>
    if (props && props['data-testid'] === testId) {
      return node
    }
    if (props && props.children) {
      const found = findNodeByTestId(props.children, testId)
      if (found) return found
    }
  }
  return null
}

// Stateful component renderer for testing hook state changes without browser DOM
function renderComponentWithState<P>(
  Component: (props: P) => React.ReactElement | null,
  initialProps: P,
  stateOverrides?: Record<number, any>
): {
  tree: React.ReactElement | null
  state: any[]
  renderWithState: (props: P) => React.ReactElement | null
  setHookState: (index: number, val: any) => void
} {
  const internals = (React as any).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE
  const prevDispatcher = internals?.H
  const state: any[] = []
  if (stateOverrides) {
    Object.entries(stateOverrides).forEach(([k, v]) => {
      state[Number(k)] = v
    })
  }

  let stateCursor = 0

  const dispatcher = {
    useState: (initial: any) => {
      const idx = stateCursor++
      if (!(idx in state)) {
        state[idx] = typeof initial === 'function' ? initial() : initial
      }
      const setter = (next: any) => {
        state[idx] = typeof next === 'function' ? next(state[idx]) : next
      }
      return [state[idx], setter]
    },
    useMemo: (factory: any) => factory(),
    useCallback: (fn: any) => fn,
    useRef: (v: any) => ({ current: v }),
    useEffect: () => {},
    useLayoutEffect: () => {},
    useId: () => 'stateful-id',
  }

  const renderWithState = (props: P) => {
    stateCursor = 0
    if (internals) {
      internals.H = dispatcher
    }
    try {
      return Component(props)
    } finally {
      if (internals) {
        internals.H = prevDispatcher
      }
    }
  }

  const tree = renderWithState(initialProps)

  return {
    tree,
    state,
    renderWithState,
    setHookState: (index: number, val: any) => {
      state[index] = val
    },
  }
}

// ---------------------------------------------------------------------------
// Test Fixtures
// ---------------------------------------------------------------------------

const sampleTurnContent: KyberTurnContentResult = {
  sessionId: 'test-session-123',
  turnIndex: 2,
  model: 'claude-3-7-sonnet-20250219',
  measurability: 'full',
  blocks: [
    {
      key: 'system_prompt',
      label: 'System Prompt',
      tokens: 1200,
      text: '<system>You are Claude, a helpful AI assistant.</system>',
      parts: [
        {
          part: 'system_prompt',
          label: 'System Prompt',
          tokens: 1200,
          text: '<system>You are Claude, a helpful AI assistant.</system>',
        },
      ],
    },
    {
      key: 'tool_definitions',
      label: 'Tool Definitions',
      tokens: 450,
      text: 'name: read_file\nname: write_file',
      parts: [
        {
          part: 'tool_definitions',
          id: 'tool-read_file',
          label: 'Tool: read_file',
          tokens: 225,
          server: 'filesystem',
          text: 'name: read_file\ndescription: Read content of a file.',
        },
        {
          part: 'tool_definitions',
          id: 'tool-write_file',
          label: 'Tool: write_file',
          tokens: 225,
          server: 'filesystem',
          text: 'name: write_file\ndescription: Write content to a file.',
        },
      ],
    },
    {
      key: 'conversation_history',
      label: 'Conversation History',
      tokens: 800,
      text: 'User: Inspect context.\nAssistant: Certainly, opening unclipped inspector.',
      parts: [
        {
          part: 'user_messages',
          id: 'user-turn-1',
          label: 'User Messages',
          tokens: 300,
          text: 'User: Inspect context.',
        },
        {
          part: 'assistant_turns',
          id: 'asst-turn-1',
          label: 'Assistant Turns',
          tokens: 500,
          text: 'Assistant: Certainly, opening unclipped inspector.',
        },
      ],
    },
  ],
  parts: [
    {
      part: 'system_prompt',
      label: 'System Prompt',
      tokens: 1200,
      text: '<system>You are Claude, a helpful AI assistant.</system>',
    },
    {
      part: 'tool_definitions',
      label: 'Tool Definitions',
      tokens: 450,
      text: 'name: read_file\nname: write_file',
    },
    {
      part: 'user_messages',
      id: 'user-turn-1',
      label: 'User Messages',
      tokens: 300,
      text: 'User: Inspect context.',
    },
    {
      part: 'assistant_turns',
      id: 'asst-turn-1',
      label: 'Assistant Turns',
      tokens: 500,
      text: 'Assistant: Certainly, opening unclipped inspector.',
    },
  ],
  assembledText:
    '[SYSTEM PROMPT]\n<system>You are Claude, a helpful AI assistant.</system>\n\n[TOOL DEFINITIONS]\nname: read_file\nname: write_file\n\n[CONVERSATION HISTORY]\nUser: Inspect context.\nAssistant: Certainly, opening unclipped inspector.',
  totalLength: 280,
  truncated: false,
}

const sampleTruncatedTurnContent: KyberTurnContentResult = {
  sessionId: 'test-session-456',
  turnIndex: 5,
  model: 'gpt-4o',
  measurability: 'full',
  blocks: [
    {
      key: 'conversation_history',
      label: 'Conversation History',
      tokens: 50000,
      text: 'Truncated conversation history slice...',
      truncated: true,
      totalLength: 150000,
      parts: [
        {
          part: 'conversation_history',
          label: 'Conversation History',
          tokens: 50000,
          text: 'Truncated conversation history slice...',
          truncated: true,
          totalLength: 150000,
        },
      ],
    },
  ],
  parts: [
    {
      part: 'conversation_history',
      label: 'Conversation History',
      tokens: 50000,
      text: 'Truncated conversation history slice...',
      truncated: true,
      totalLength: 150000,
    },
  ],
  assembledText: '[CONVERSATION HISTORY]\nTruncated conversation history slice...',
  truncated: true,
  totalLength: 150000,
}

const sampleNotMeasurableTurnContent: KyberTurnContentResult = {
  sessionId: 'test-session-789',
  turnIndex: 0,
  model: 'closed-proxy-model',
  measurability: 'not_measurable',
  notMeasurable: {
    reason: 'Upstream gateway hides system prompt and tool definitions.',
  },
  blocks: [
    {
      key: 'system_prompt',
      label: 'System Prompt',
      notMeasurable: {
        reason: 'System prompt not measurable: upstream gateway hides raw system prompt.',
      },
      tokens: 0,
      text: '',
      parts: [],
    },
  ],
  parts: [],
  assembledText: '',
  truncated: false,
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('copyToClipboard & Clipboard Mocking', () => {
  const originalClipboardDesc = Object.getOwnPropertyDescriptor(globalThis.navigator, 'clipboard')
  const originalDocumentDesc = Object.getOwnPropertyDescriptor(globalThis, 'document')

  afterEach(() => {
    if (originalClipboardDesc) {
      Object.defineProperty(globalThis.navigator, 'clipboard', originalClipboardDesc)
    } else {
      delete (globalThis.navigator as any).clipboard
    }
    if (originalDocumentDesc) {
      Object.defineProperty(globalThis, 'document', originalDocumentDesc)
    } else {
      delete (globalThis as any).document
    }
    vi.restoreAllMocks()
  })

  it('copies text via navigator.clipboard.writeText when available', async () => {
    const writeTextMock = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      value: { writeText: writeTextMock },
      configurable: true,
      writable: true,
    })

    const success = await copyToClipboard('Hello, unclipped context!')
    expect(success).toBe(true)
    expect(writeTextMock).toHaveBeenCalledWith('Hello, unclipped context!')
  })

  it('falls back to document.execCommand when navigator.clipboard throws', async () => {
    const writeTextMock = vi.fn().mockRejectedValue(new Error('Permission denied'))
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      value: { writeText: writeTextMock },
      configurable: true,
      writable: true,
    })

    const appendChildMock = vi.fn()
    const removeChildMock = vi.fn()
    const execCommandMock = vi.fn().mockReturnValue(true)

    const mockTextarea = {
      value: '',
      style: {},
      focus: vi.fn(),
      select: vi.fn(),
    }

    Object.defineProperty(globalThis, 'document', {
      value: {
        createElement: vi.fn().mockReturnValue(mockTextarea),
        body: {
          appendChild: appendChildMock,
          removeChild: removeChildMock,
        },
        execCommand: execCommandMock,
      },
      configurable: true,
      writable: true,
    })

    const success = await copyToClipboard('Fallback context block text')
    expect(success).toBe(true)
    expect(mockTextarea.value).toBe('Fallback context block text')
    expect(appendChildMock).toHaveBeenCalledWith(mockTextarea)
    expect(mockTextarea.select).toHaveBeenCalled()
    expect(execCommandMock).toHaveBeenCalledWith('copy')
    expect(removeChildMock).toHaveBeenCalledWith(mockTextarea)
  })

  it('returns false when text is empty without calling clipboard API', async () => {
    const writeTextMock = vi.fn()
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      value: { writeText: writeTextMock },
      configurable: true,
      writable: true,
    })

    const success = await copyToClipboard('')
    expect(success).toBe(false)
    expect(writeTextMock).not.toHaveBeenCalled()
  })

  it('returns false when neither clipboard API nor document is available', async () => {
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      value: undefined,
      configurable: true,
      writable: true,
    })
    Object.defineProperty(globalThis, 'document', {
      value: undefined,
      configurable: true,
      writable: true,
    })

    const success = await copyToClipboard('Text without environment')
    expect(success).toBe(false)
  })
})

describe('CopyButton Component', () => {
  it('renders default button with label "Copy" and clipboard emoji', () => {
    const element = React.createElement(CopyButton, { text: 'Some text to copy' })
    const html = renderHtml(element)

    expect(html).toContain('Copy')
    expect(html).toContain('📋')
    expect(html).toContain('data-testid="copy-button"')
  })

  it('renders custom label and custom testId', () => {
    const element = React.createElement(CopyButton, {
      text: 'Block content',
      label: 'Copy System Prompt',
      testId: 'copy-custom-button',
      title: 'Click to copy system prompt',
    })
    const html = renderHtml(element)

    expect(html).toContain('Copy System Prompt')
    expect(html).toContain('data-testid="copy-custom-button"')
    expect(html).toContain('title="Click to copy system prompt"')
  })

  it('renders disabled styling and disabled attribute when disabled or text is empty', () => {
    const disabledElement = React.createElement(CopyButton, {
      text: 'Some text',
      disabled: true,
      testId: 'copy-disabled',
    })
    const htmlDisabled = renderHtml(disabledElement)
    expect(htmlDisabled).toContain('disabled=""')
    expect(htmlDisabled).toContain('opacity-50')

    const emptyTextElement = React.createElement(CopyButton, {
      text: '',
      testId: 'copy-empty',
    })
    const htmlEmpty = renderHtml(emptyTextElement)
    expect(htmlEmpty).toContain('disabled=""')
  })

  it('transitions to Copied! state and displays checkmark feedback', () => {
    // Render with copied state = true (state[0] = true)
    const { tree } = renderComponentWithState(CopyButton, {
      text: 'Assembled turn text',
      testId: 'copy-feedback-test',
    }, { 0: true })

    const html = renderHtml(tree)
    expect(html).toContain('Copied!')
    expect(html).toContain('✓')
    expect(html).toContain('text-emerald-500')
  })

  it('fires onClick callback and triggers copyToClipboard on button click', async () => {
    const writeTextMock = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      value: { writeText: writeTextMock },
      configurable: true,
      writable: true,
    })

    try {
      const onClickMock = vi.fn()
      const tree = CopyButton({
        text: 'Text to copy',
        onClick: onClickMock,
        testId: 'copy-interactive-button',
      })

      const button = findNodeByTestId(tree, 'copy-interactive-button')
      expect(button).not.toBeNull()

      const fakeEvent = { stopPropagation: vi.fn() } as any
      await button!.props.onClick(fakeEvent)

      expect(fakeEvent.stopPropagation).toHaveBeenCalled()
      expect(writeTextMock).toHaveBeenCalledWith('Text to copy')
      expect(onClickMock).toHaveBeenCalled()
    } finally {
      delete (globalThis.navigator as any).clipboard
    }
  })
})

describe('BlockRail Component', () => {
  it('renders "Context Blocks" header and "Whole Turn" all-button', () => {
    const onSelectBlock = vi.fn()
    const element = React.createElement(BlockRail, {
      blocks: sampleTurnContent.blocks,
      activeBlockKey: 'all',
      onSelectBlock,
    })
    const html = renderHtml(element)

    expect(html).toContain('Context Blocks')
    expect(html).toContain('Whole Turn')
    expect(html).toContain('data-testid="block-item-all"')
    // Active 'all' button styling
    expect(html).toContain('bg-primary/15')
  })

  it('renders canonical blocks with labels and token counts', () => {
    const onSelectBlock = vi.fn()
    const element = React.createElement(BlockRail, {
      blocks: sampleTurnContent.blocks,
      activeBlockKey: 'system_prompt',
      onSelectBlock,
    })
    const html = renderHtml(element)

    expect(html).toContain('System Prompt')
    expect(html).toContain('Tool Definitions')
    expect(html).toContain('Conversation History')

    // Formatted token badges
    expect(html).toContain('1.2K') // 1200 tokens
    expect(html).toContain('450')  // 450 tokens
    expect(html).toContain('800')  // 800 tokens
  })

  it('displays "n/a" badge for notMeasurable blocks', () => {
    const onSelectBlock = vi.fn()
    const element = React.createElement(BlockRail, {
      blocks: sampleNotMeasurableTurnContent.blocks,
      activeBlockKey: 'all',
      onSelectBlock,
    })
    const html = renderHtml(element)

    expect(html).toContain('System Prompt')
    expect(html).toContain('n/a')
  })

  it('invokes onSelectBlock callback when block item button is clicked', () => {
    const onSelectBlock = vi.fn()
    const tree = BlockRail({
      blocks: sampleTurnContent.blocks,
      activeBlockKey: 'all',
      onSelectBlock,
    })

    const toolDefButton = findNodeByTestId(tree, 'block-item-tool_definitions')
    expect(toolDefButton).not.toBeNull()

    toolDefButton!.props.onClick()
    expect(onSelectBlock).toHaveBeenCalledWith('tool_definitions')

    const allButton = findNodeByTestId(tree, 'block-item-all')
    expect(allButton).not.toBeNull()
    allButton!.props.onClick()
    expect(onSelectBlock).toHaveBeenCalledWith('all')
  })
})

describe('PartTabs Component', () => {
  it('returns null when parts list has 0 or 1 item', () => {
    const onSelectPart = vi.fn()
    const emptyElement = React.createElement(PartTabs, {
      parts: [],
      activePartId: 'all',
      onSelectPart,
    })
    expect(renderHtml(emptyElement)).toBe('')

    const singleElement = React.createElement(PartTabs, {
      parts: [sampleTurnContent.parts[0]],
      activePartId: 'all',
      onSelectPart,
    })
    expect(renderHtml(singleElement)).toBe('')
  })

  it('renders "All (N)" tab and part buttons when multiple parts exist', () => {
    const onSelectPart = vi.fn()
    const element = React.createElement(PartTabs, {
      parts: sampleTurnContent.parts,
      activePartId: 'all',
      onSelectPart,
    })
    const html = renderHtml(element)

    expect(html).toContain('All (4)')
    expect(html).toContain('System Prompt')
    expect(html).toContain('Tool Definitions')
    expect(html).toContain('User Messages')
    expect(html).toContain('Assistant Turns')
  })

  it('renders MCP server badge when part has server attribute', () => {
    const toolParts: KyberTurnContentPart[] = [
      {
        part: 'tool_definitions',
        id: 'tool-read_file',
        label: 'read_file',
        server: 'filesystem-server',
        text: 'tool definition',
      },
      {
        part: 'tool_definitions',
        id: 'tool-fetch',
        label: 'fetch',
        server: 'web-server',
        text: 'tool definition',
      },
    ]

    const element = React.createElement(PartTabs, {
      parts: toolParts,
      activePartId: 'tool-read_file',
      onSelectPart: vi.fn(),
    })
    const html = renderHtml(element)

    expect(html).toContain('filesystem-server')
    expect(html).toContain('web-server')
  })

  it('invokes onSelectPart with part id when tab is clicked', () => {
    const onSelectPart = vi.fn()
    const tree = PartTabs({
      parts: sampleTurnContent.parts,
      activePartId: 'all',
      onSelectPart,
    })

    const userMsgTab = findNodeByTestId(tree, 'part-tab-user_messages')
    expect(userMsgTab).not.toBeNull()

    userMsgTab!.props.onClick()
    expect(onSelectPart).toHaveBeenCalledWith('user-turn-1')
  })
})

describe('ContentPane Component (Decision D4 & D14 Compliance)', () => {
  it('renders unclipped text and character count label', () => {
    const element = React.createElement(ContentPane, {
      text: '<system>You are Claude.</system>',
      label: 'System Prompt',
    })
    const html = renderHtml(element)

    expect(html).toContain('System Prompt · 32 chars')
    expect(html).toContain('You are Claude.')
    expect(html).toContain('data-testid="context-content-pane"')
  })

  it('renders empty content state when text is empty', () => {
    const element = React.createElement(ContentPane, {
      text: '',
      label: 'Empty Block',
    })
    const html = renderHtml(element)

    expect(html).toContain('No content recorded for this block.')
    expect(html).toContain('data-testid="empty-content-pane"')
  })

  it('Decision D4 compliance: renders not-measurable pane stating reason and no fake placeholder', () => {
    const reasonText = 'System prompt not measurable: upstream gateway hides raw system prompt.'
    const element = React.createElement(ContentPane, {
      text: '',
      notMeasurable: { reason: reasonText },
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="not-measurable-pane"')
    expect(html).toContain('Not Measurable')
    expect(html).toContain(reasonText)
    // Verify no misleading placeholder text is present
    expect(html).not.toContain('No content recorded')
    expect(html).not.toContain('0 tokens')
  })

  it('Decision D14 compliance: renders visibly labelled budget truncation banner when truncated is true', () => {
    const element = React.createElement(ContentPane, {
      text: 'Truncated prompt context text',
      truncated: true,
      totalLength: 150000,
      label: 'Conversation History',
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="budget-truncated-banner"')
    expect(html).toContain('D14 Budget')
    expect(html).toContain('Showing 29 of 150,000 characters (length limit applied)')
  })

  it('toggles between formatted view and raw view', () => {
    // Default formatted view: renders XmlFoldedText
    const { tree: formattedTree } = renderComponentWithState(ContentPane, {
      text: '<custom_tag>Some inner content</custom_tag>',
      label: 'Custom Tag Test',
    }, { 0: 'formatted' })

    const formattedHtml = renderHtml(formattedTree)
    expect(formattedHtml).toContain('custom_tag')

    // Raw view: renders <pre> block
    const { tree: rawTree } = renderComponentWithState(ContentPane, {
      text: '<custom_tag>Some inner content</custom_tag>',
      label: 'Custom Tag Test',
    }, { 0: 'raw' })

    const rawHtml = renderHtml(rawTree)
    expect(rawHtml).toContain('<pre')
    expect(rawHtml).toContain('&lt;custom_tag&gt;Some inner content&lt;/custom_tag&gt;')
  })
})

describe('ContextInspector Component', () => {
  it('renders turn index, model badge, block rail, and assembled content', () => {
    const element = React.createElement(ContextInspector, {
      data: sampleTurnContent,
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="context-inspector"')
    expect(html).toContain('Turn 2')
    expect(html).toContain('claude-3-7-sonnet-20250219')
    expect(html).toContain('Whole Turn')
    expect(html).toContain('System Prompt')
    expect(html).toContain('Tool Definitions')
    expect(html).toContain('Conversation History')
  })

  it('provides turn-level copy button containing whole turn assembled plain text', () => {
    const element = React.createElement(ContextInspector, {
      data: sampleTurnContent,
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="copy-turn-button"')
    expect(html).toContain('Copy Turn')
  })

  it('provides block-level copy button containing active block text', () => {
    // When selectedBlockKey is 'all', block copy text defaults to assembledText
    const allHtml = renderHtml(
      React.createElement(ContextInspector, {
        data: sampleTurnContent,
        initialBlockKey: 'all',
      })
    )
    expect(allHtml).toContain('data-testid="copy-block-button"')
    expect(allHtml).toContain('Copy Block')

    // When selectedBlockKey is 'system_prompt', block copy text is the system prompt text
    const sysHtml = renderHtml(
      React.createElement(ContextInspector, {
        data: sampleTurnContent,
        initialBlockKey: 'system_prompt',
      })
    )
    expect(sysHtml).toContain('data-testid="copy-block-button"')
    expect(sysHtml).toContain('Copy System Prompt')
  })

  it('disables block-level copy button when block is notMeasurable', () => {
    const html = renderHtml(
      React.createElement(ContextInspector, {
        data: sampleNotMeasurableTurnContent,
        initialBlockKey: 'system_prompt',
      })
    )

    expect(html).toContain('data-testid="copy-block-button"')
    expect(html).toContain('disabled=""')
  })

  it('switches displayed content when block is selected', () => {
    const element = React.createElement(ContextInspector, {
      data: sampleTurnContent,
      initialBlockKey: 'tool_definitions',
    })

    const html = renderHtml(element)
    expect(html).toContain('name: read_file')
    expect(html).toContain('name: write_file')
    // Shows part tabs for tools
    expect(html).toContain('Tool: read_file')
    expect(html).toContain('Tool: write_file')
  })

  it('switches displayed content when individual part is selected in part tabs', () => {
    const element = React.createElement(ContextInspector, {
      data: sampleTurnContent,
      initialBlockKey: 'tool_definitions',
      initialPart: 'tool-read_file',
    })

    const html = renderHtml(element)
    expect(html).toContain('Tool: read_file')
    expect(html).toContain('Read content of a file.')
  })

  it('initializes to initialBlockKey when passed', () => {
    const element = React.createElement(ContextInspector, {
      data: sampleTurnContent,
      initialBlockKey: 'conversation_history',
    })

    const html = renderHtml(element)
    expect(html).toContain('Conversation History')
    expect(html).toContain('Inspect context.')
  })

  it('renders D14 budget truncation banner when turn data has truncated: true', () => {
    const element = React.createElement(ContextInspector, {
      data: sampleTruncatedTurnContent,
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="budget-truncated-banner"')
    expect(html).toContain('D14 Budget')
    expect(html).toContain('length limit applied')
  })

  it('renders close button when onClose is provided', () => {
    const onClose = vi.fn()
    const element = React.createElement(ContextInspector, {
      data: sampleTurnContent,
      onClose,
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="context-inspector-close-button"')
    expect(html).toContain('✕')
  })

  it('renders error state when data is null and no session to query', () => {
    const element = React.createElement(ContextInspector, {
      data: undefined,
      sessionId: undefined,
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="context-inspector-error"')
    expect(html).toContain('Unable to load unclipped context.')
  })
})

describe('SessionInspectorDrawer Integration with ContextInspector', () => {
  it('renders "Context Inspector" toggle button in drawer header when sessionId is available', () => {
    const element = React.createElement(SessionInspectorDrawer, {
      open: true,
      onClose: vi.fn(),
      title: 'Turn 2 Inspection',
      rawContent: {
        sessionId: 'session-xyz',
        turnIndex: 2,
        tokens: 2450,
      },
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="toggle-context-inspector-button"')
    expect(html).toContain('Context Inspector')
  })

  it('toggles to ContextInspector view when inspectContext is true', () => {
    const element = React.createElement(SessionInspectorDrawer, {
      open: true,
      onClose: vi.fn(),
      title: 'Turn 2 Inspection',
      inspectContext: true,
      rawContent: {
        sessionId: 'session-xyz',
        turnIndex: 2,
      },
    })

    const html = renderHtml(element)
    expect(html).toContain('data-testid="drawer-context-inspector-view"')
    expect(html).toContain('Show Overview')
  })

  it('renders "Open in Context Inspector" button in TurnInspector view', () => {
    const turnData = {
      sessionId: 'session-xyz',
      fresh: 1000,
      cache_read: 2000,
      turn: 3,
      index: 3,
      model: 'claude-3-5-sonnet',
    }

    const element = React.createElement(SessionInspectorDrawer, {
      open: true,
      onClose: vi.fn(),
      title: 'Turn 3',
      rawContent: turnData,
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="open-context-inspector-button"')
    expect(html).toContain('Open in Context Inspector')
  })

  it('renders "Open in Context Inspector" button in ContextBucketInspector view', () => {
    const bucketData = {
      sessionId: 'session-xyz',
      bucket: 'system_prompt',
      tokens: 4500,
    }

    const element = React.createElement(SessionInspectorDrawer, {
      open: true,
      onClose: vi.fn(),
      title: 'System Prompt Bucket',
      rawContent: bucketData,
    })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="open-context-inspector-button"')
    expect(html).toContain('Open in Context Inspector')
  })
})
