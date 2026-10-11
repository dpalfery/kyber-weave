import { describe, it, expect, vi } from 'vitest'
import * as React from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import {
  XML_FOLD_TAGS,
  parseXmlFoldChunks,
  XmlFoldedText,
  ToolCallView,
  ToolResultView,
  KeyValueList,
  InspectorContent,
  SessionInspectorDrawer,
  MessagePartView,
  TURN_CONTENT_SECTION_ORDER,
  isJsonText,
  isRootEncodedJsonText,
  prettyPrintJson,
  prettyPrintJsonLines,
  splitTopLevelArrayItems,
  JsonStringView,
  ReadableContentText,
  TurnContentSections,
  TurnLosslessText,
  TurnLosslessValue,
  prettyPrintJsonNested,
  prettyPrintJsonLinesNested,
  TurnFullContentPanel,
  CopyExactButton,
  isTurnFullContentRequest,
  buildWholeTurnCopyText,
  orphanTurnParts,
} from './SessionInspectorDrawer.js'

import { renderToStaticMarkup } from 'react-dom/server'
import type { KyberSessionContentResult, KyberTurnContentResult } from '../lib/kyberApi.js'
import { fetchKyberSessionContent, fetchTurnContentFull } from '../lib/kyberApi.js'

// Set up React 19 test hook dispatcher so components using hooks can be rendered in tests
const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: {
      H?: Record<string, unknown>
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
    if (props && props.children) {
      const found = findElementByTestId(props.children, testId)
      if (found) return found
    }
  }
  return null
}

function findDetailsNodes(element: React.ReactElement | null | undefined): React.ReactElement[] {
  const matches: React.ReactElement[] = []
  const walk = (node: unknown): void => {
    if (node == null) return
    if (Array.isArray(node)) {
      node.forEach(walk)
      return
    }
    if (React.isValidElement(node)) {
      const el = node as React.ReactElement<{ children?: unknown; className?: string }>
      if (el.type === 'details') {
        matches.push(el)
      }
      const type = el.type as unknown
      if (typeof type === 'function') {
        const result = (type as (p: unknown) => unknown)(el.props)
        walk(result)
        return
      }
      walk((el.props as { children?: unknown }).children)
    }
  }
  walk(element)
  return matches
}

describe('SessionInspectorDrawer: XML Tag Folding Engine', () => {
  it('contains all 13 required XML fold tags', () => {
    const required = [
      'environment_info',
      'workspace_info',
      'instructions',
      'copilot_instructions',
      'context',
      'reminderInstructions',
      'editor_context',
      'tool_use_instructions',
      'notebook_info',
      'system_reminder',
      'file_contents',
      'attachment',
      'environment_details',
    ]
    for (const tag of required) {
      expect(XML_FOLD_TAGS).toContain(tag)
    }
  })

  it('correctly parses plain text and XML tags into chunks', () => {
    const raw = `Prefix text
<instructions>Please adhere to coding conventions.</instructions>
Middle text
<environment_info os="mac">OS: Darwin arm64</environment_info>
Suffix text`
    const chunks = parseXmlFoldChunks(raw)
    expect(chunks.length).toBe(5)
    expect(chunks[0].type).toBe('text')
    expect(chunks[0].content).toContain('Prefix text')
    expect(chunks[1].type).toBe('tag')
    expect(chunks[1].tag).toBe('instructions')
    expect(chunks[1].content).toBe('Please adhere to coding conventions.')
    expect(chunks[2].type).toBe('text')
    expect(chunks[3].type).toBe('tag')
    expect(chunks[3].tag).toBe('environment_info')
    expect(chunks[3].content).toBe('OS: Darwin arm64')
    expect(chunks[4].type).toBe('text')
  })

  it('folds XML tags into details and summary with tag badge and preview', () => {
    const raw = `<instructions>This is an instruction block that will be previewed in the summary element.</instructions>`
    const element = React.createElement(XmlFoldedText, { text: raw })
    const details = findDetailsNodes(element)
    expect(details.length).toBe(1)

    const detailsEl = details[0]
    expect((detailsEl!.props as { className?: string }).className).toContain('group border border-border rounded my-1 bg-card/60')

    const html = renderHtml(element)
    expect(html).toContain('&lt;instructions&gt;')
    expect(html).toContain('This is an instruction block')
  })

  it('previews at most 80 characters of tag text in summary', () => {
    const longText = 'A'.repeat(120)
    const raw = `<workspace_info>${longText}</workspace_info>`
    const element = React.createElement(XmlFoldedText, { text: raw })
    const html = renderHtml(element)

    expect(html).toContain('&lt;workspace_info&gt;')
    // Previews first 80 chars with ellipsis
    expect(html).toContain('A'.repeat(80) + '…')
  })

  it('preserves text outside folded XML tags', () => {
    const raw = `Leading notes
<context>Project context</context>
Trailing notes`
    const element = React.createElement(XmlFoldedText, { text: raw })
    const html = renderHtml(element)
    expect(html).toContain('Leading notes')
    expect(html).toContain('&lt;context&gt;')
    expect(html).toContain('Project context')
    expect(html).toContain('Trailing notes')
  })

  it('correctly handles unclosed XML tags (e.g. <instructions>hello without closing tag)', () => {
    const raw = '<instructions>hello unclosed block'
    const chunks = parseXmlFoldChunks(raw)
    expect(chunks.length).toBe(1)
    expect(chunks[0].type).toBe('tag')
    expect(chunks[0].tag).toBe('instructions')
    expect(chunks[0].content).toBe('hello unclosed block')

    const element = React.createElement(XmlFoldedText, { text: raw })
    const html = renderHtml(element)
    expect(html).toContain('&lt;instructions&gt;')
    expect(html).toContain('hello unclosed block')
    expect(findDetailsNodes(element).length).toBe(1)
  })

  it('correctly handles unclosed XML tag preceded by normal text', () => {
    const raw = 'Before tag <context>some context that is not closed'
    const chunks = parseXmlFoldChunks(raw)
    expect(chunks.length).toBe(2)
    expect(chunks[0].type).toBe('text')
    expect(chunks[0].content).toBe('Before tag ')
    expect(chunks[1].type).toBe('tag')
    expect(chunks[1].tag).toBe('context')
    expect(chunks[1].content).toBe('some context that is not closed')

    const element = React.createElement(XmlFoldedText, { text: raw })
    const html = renderHtml(element)
    expect(html).toContain('Before tag')
    expect(html).toContain('&lt;context&gt;')
    expect(html).toContain('some context that is not closed')
  })
})

describe('SessionInspectorDrawer: Tool Call and Tool Result Formatting', () => {
  it('formats tool call with tool name, wrench icon, and key-value parameters', () => {
    const call = {
      name: 'read_file',
      arguments: JSON.stringify({ path: '/tmp/test.ts', offset: 10 }),
    }
    const element = React.createElement(ToolCallView, { call })
    const html = renderHtml(element)

    expect(html).toContain('🔧')
    expect(html).toContain('tool call')
    expect(html).toContain('read_file')
    expect(html).toContain('path:')
    expect(html).toContain('/tmp/test.ts')
    expect(html).toContain('offset:')
    expect(html).toContain('10')
  })

  it('formats tool result with return icon and formatted output', () => {
    const result = {
      output: JSON.stringify({ success: true, count: 42 }),
    }
    const element = React.createElement(ToolResultView, { result })
    const html = renderHtml(element)

    expect(html).toContain('↩')
    expect(html).toContain('tool result')
    expect(html).toContain('success:')
    expect(html).toContain('true')
    expect(html).toContain('count:')
    expect(html).toContain('42')
  })

  it('formats tool result with XML folded tags when returned string contains them', () => {
    const result = {
      output: `<file_contents path="src/main.ts">console.log("hello world");</file_contents>`,
    }
    const element = React.createElement(ToolResultView, { result })
    const html = renderHtml(element)

    expect(html).toContain('↩')
    expect(html).toContain('&lt;file_contents&gt;')
    expect(html).toContain('console.log(&quot;hello world&quot;);')
  })

  it('handles null, undefined, or invalid JSON string in tool call arguments without throwing', () => {
    expect(() => {
      const elNull = React.createElement(ToolCallView, {
        call: { name: 'test_tool', arguments: null },
      })
      const htmlNull = renderHtml(elNull)
      expect(htmlNull).toContain('test_tool')
      expect(htmlNull).toContain('No parameters')
    }).not.toThrow()

    expect(() => {
      const elUndef = React.createElement(ToolCallView, {
        call: { name: 'test_tool', arguments: undefined },
      })
      const htmlUndef = renderHtml(elUndef)
      expect(htmlUndef).toContain('test_tool')
      expect(htmlUndef).toContain('No parameters')
    }).not.toThrow()

    expect(() => {
      const elInvalid = React.createElement(ToolCallView, {
        call: { name: 'test_tool', arguments: '{ invalid: json without closing brace' },
      })
      const htmlInvalid = renderHtml(elInvalid)
      expect(htmlInvalid).toContain('test_tool')
      expect(htmlInvalid).toContain('{ invalid: json without closing brace')
    }).not.toThrow()

    expect(() => {
      const elMalformed = React.createElement(ToolCallView, {
        call: { name: 'test_tool', arguments: '{ "broken": json' },
      })
      const htmlMalformed = renderHtml(elMalformed)
      expect(htmlMalformed).toContain('test_tool')
    }).not.toThrow()

    expect(() => {
      const elNullCall = React.createElement(ToolCallView, { call: null })
      const htmlNullCall = renderHtml(elNullCall)
      expect(htmlNullCall).toContain('tool_call')
      expect(htmlNullCall).toContain('No parameters')
    }).not.toThrow()
  })

  it('supports fallback to src.args when arguments and parameters are absent', () => {
    const call = {
      name: 'fetch_data',
      args: { url: 'https://api.example.com', timeout: 5000 },
    }
    const element = React.createElement(ToolCallView, { call })
    const html = renderHtml(element)
    expect(html).toContain('fetch_data')
    expect(html).toContain('url:')
    expect(html).toContain('https://api.example.com')
    expect(html).toContain('timeout:')
    expect(html).toContain('5000')
  })

  it('handles null, undefined, or invalid JSON string in tool result output and renders cleanly', () => {
    expect(() => {
      const elNull = React.createElement(ToolResultView, { result: { output: null } })
      const htmlNull = renderHtml(elNull)
      expect(htmlNull).toContain('tool result')
      expect(htmlNull).toContain('empty')
    }).not.toThrow()

    expect(() => {
      const elUndef = React.createElement(ToolResultView, { result: { output: undefined } })
      const htmlUndef = renderHtml(elUndef)
      expect(htmlUndef).toContain('tool result')
      expect(htmlUndef).toContain('empty')
    }).not.toThrow()

    expect(() => {
      const elInvalid = React.createElement(ToolResultView, {
        result: { output: '{ broken json syntax' },
      })
      const htmlInvalid = renderHtml(elInvalid)
      expect(htmlInvalid).toContain('tool result')
      expect(htmlInvalid).toContain('{ broken json syntax')
    }).not.toThrow()

    expect(() => {
      const elNullResult = React.createElement(ToolResultView, { result: null })
      const htmlNullResult = renderHtml(elNullResult)
      expect(htmlNullResult).toContain('tool result')
      expect(htmlNullResult).toContain('empty')
    }).not.toThrow()
  })

  it('renders thinking message part with thinking badge alongside reasoning support', () => {
    const thinkingPart = {
      type: 'thinking',
      content: 'Considering AST parser implementation options...',
    }
    const element = React.createElement(MessagePartView, { part: thinkingPart })
    const html = renderHtml(element)
    expect(html).toContain('thinking')
    expect(html).toContain('Considering AST parser implementation options...')
  })
})

describe('SessionInspectorDrawer: XSS Sanitization', () => {
  it('escapes HTML tags in XmlFoldedText plain text so <script>alert(1)</script> is escaped as text', () => {
    const malicious = '<script>alert(1)</script>'
    const element = React.createElement(XmlFoldedText, { text: malicious })
    const html = renderHtml(element)
    expect(html).toContain('&lt;script&gt;alert(1)&lt;/script&gt;')
    expect(html).not.toContain('<script>alert(1)</script>')
  })

  it('escapes script tags in ToolCallView parameters', () => {
    const call = {
      name: '<script>alert("xss")</script>',
      arguments: JSON.stringify({ malicious: '<script>alert(2)</script>' }),
    }
    const element = React.createElement(ToolCallView, { call })
    const html = renderHtml(element)
    expect(html).toContain('&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;')
    expect(html).toContain('&lt;script&gt;alert(2)&lt;/script&gt;')
    expect(html).not.toContain('<script>alert("xss")</script>')
    expect(html).not.toContain('<script>alert(2)</script>')
  })

  it('escapes script tags in ToolResultView output', () => {
    const result = {
      output: '<script>alert("payload")</script>',
    }
    const element = React.createElement(ToolResultView, { result })
    const html = renderHtml(element)
    expect(html).toContain('&lt;script&gt;alert(&quot;payload&quot;)&lt;/script&gt;')
    expect(html).not.toContain('<script>alert("payload")</script>')
  })
})

describe('SessionInspectorDrawer: KeyValueList formatting', () => {
  it('renders key-value pairs cleanly', () => {
    const data = { status: 'healthy', turns: 5 }
    const element = React.createElement(KeyValueList, { data })
    const html = renderHtml(element)
    expect(html).toContain('status:')
    expect(html).toContain('healthy')
    expect(html).toContain('turns:')
    expect(html).toContain('5')
  })

  it('handles null/undefined values gracefully', () => {
    const element = React.createElement(KeyValueList, { data: null })
    const html = renderHtml(element)
    expect(html).toContain('empty')
  })
})

describe('SessionInspectorDrawer: InspectorContent', () => {
  it('formats turn object with token spend minibar, model, and duration', () => {
    const turnData = {
      index: 1,
      model: 'claude-3-5-sonnet',
      durationMs: 1420,
      ttft_ms: 320,
      fresh: 1500,
      cache_read: 8000,
      cache_creation: 500,
      visible_output: 250,
      credits: 0.12,
      usd: 0.035,
      fresh_jump_pct: 45,
      content: {
        prompt_text: '<instructions>Write tests</instructions>',
        reasoning_text: 'Let us consider the edge cases...',
      },
    }
    const element = React.createElement(InspectorContent, { data: turnData })
    const html = renderHtml(element)

    expect(html).toContain('Token Spend')
    expect(html).toContain('claude-3-5-sonnet')
    expect(html).toContain('1.4s')
    expect(html).toContain('320ms')
    expect(html).toContain('Fresh input')
    expect(html).toContain('Cache-read input')
    expect(html).toContain('Fresh input jumped')
    expect(html).toContain('+45%')
    expect(html).toContain('&lt;instructions&gt;')
    expect(html).toContain('reasoning')
    expect(html).toContain('Let us consider the edge cases...')
  })

  it('formats timeline span node with attributes and metadata', () => {
    const spanNode = {
      spanId: 'span-abc-123',
      traceId: 'trace-xyz-789',
      name: 'agent.turn',
      kind: 'INTERNAL',
      status: 'OK',
      durationMs: 850,
      isSubagent: true,
      isAuxiliary: false,
      attributes: {
        'gen_ai.system': 'anthropic',
        'gen_ai.request.model': 'claude-3-7-sonnet',
      },
    }
    const element = React.createElement(InspectorContent, { data: spanNode })
    const html = renderHtml(element)

    expect(html).toContain('Span Details')
    expect(html).toContain('agent.turn')
    expect(html).toContain('span-abc-123')
    expect(html).toContain('subagent')
    expect(html).toContain('INTERNAL')
    expect(html).toContain('gen_ai.system:')
    expect(html).toContain('anthropic')
    expect(html).toContain('gen_ai.request.model:')
    expect(html).toContain('claude-3-7-sonnet')
  })

  it('formats raw JSON object when generic data is passed', () => {
    const generic = { customField: 'sample value', count: 99 }
    const element = React.createElement(InspectorContent, { data: generic })
    const html = renderHtml(element)

    expect(html).toContain('customField:')
    expect(html).toContain('sample value')
    expect(html).toContain('count:')
    expect(html).toContain('99')
  })
})

function renderDrawerComponent(
  props: React.ComponentProps<typeof SessionInspectorDrawer>,
  onEffect?: (effect: () => void | (() => void), deps?: unknown[]) => void
) {
  const internals = (React as unknown as { __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: { H?: Record<string, unknown> } }).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE
  const prevDispatcher = internals?.H
  try {
    if (internals) {
      internals.H = {
        useEffect: (effect: () => void | (() => void), deps: unknown) => {
          onEffect?.(effect, deps as unknown[] | undefined)
        },
        useState: (initial: unknown) => [typeof initial === 'function' ? (initial as () => unknown)() : initial, () => {}],
        useMemo: (factory: () => unknown) => factory(),
      }
    }
    return SessionInspectorDrawer(props)
  } finally {
    if (internals) {
      internals.H = prevDispatcher
    }
  }
}

describe('SessionInspectorDrawer: Drawer Modal Transitions and Interactions', () => {
  it('renders nothing when open is false', () => {
    const onClose = vi.fn()
    const element = React.createElement(SessionInspectorDrawer, {
      open: false,
      onClose,
      title: 'Inspector Title',
      rawContent: { key: 'val' },
    })
    const html = renderHtml(element)
    expect(html).toBe('')
  })

  it('renders drawer header, close button, and content when open is true', () => {
    const onClose = vi.fn()
    const element = React.createElement(SessionInspectorDrawer, {
      open: true,
      onClose,
      title: 'Turn 3 Details',
      subtitle: 'Session span inspector',
      rawContent: { testAttr: 'testValue' },
    })
    const html = renderHtml(element)

    expect(html).toContain('Turn 3 Details')
    expect(html).toContain('Session span inspector')
    expect(html).toContain('✕')
    expect(html).toContain('testAttr:')
    expect(html).toContain('testValue')
  })

  it('registers keydown listener in SessionInspectorDrawer via useEffect and invokes onClose on Escape', () => {
    let capturedEffect: (() => (() => void) | void) | null = null
    const originalWindow = globalThis.window
    const listeners: Record<string, (e: unknown) => void> = {}
    const mockWindow = {
      addEventListener: vi.fn((event: string, handler: (e: unknown) => void) => {
        listeners[event] = handler
      }),
      removeEventListener: vi.fn((event: string, handler: (e: unknown) => void) => {
        if (listeners[event] === handler) {
          delete listeners[event]
        }
      }),
    }

    try {
      globalThis.window = mockWindow as unknown as Window & typeof globalThis
      const onClose = vi.fn()

      // Execute actual SessionInspectorDrawer component function to trigger its useEffect
      renderDrawerComponent(
        {
          open: true,
          onClose,
          title: 'Inspector Drawer',
        },
        (effect) => {
          capturedEffect = effect
        }
      )

      expect(capturedEffect).toBeTypeOf('function')

      // Run the actual effect returned by SessionInspectorDrawer
      const cleanup = capturedEffect!()

      // Ensure the component registered its keydown listener on window
      expect(mockWindow.addEventListener).toHaveBeenCalledWith('keydown', expect.any(Function))
      expect(listeners['keydown']).toBeDefined()

      // Non-Escape key does not trigger onClose
      listeners['keydown']({ key: 'Tab' })
      expect(onClose).not.toHaveBeenCalled()

      // Escape key triggers onClose
      listeners['keydown']({ key: 'Escape' })
      expect(onClose).toHaveBeenCalledTimes(1)

      // Invoke cleanup to verify removeEventListener was called with the handler
      if (typeof cleanup === 'function') {
        cleanup()
      }
      expect(mockWindow.removeEventListener).toHaveBeenCalledWith('keydown', expect.any(Function))
      expect(listeners['keydown']).toBeUndefined()
    } finally {
      if (originalWindow !== undefined) {
        globalThis.window = originalWindow
      } else {
        const g = globalThis as { window?: unknown }
        delete g.window
      }
    }
  })

  it('does not register keydown listener in SessionInspectorDrawer when open is false', () => {
    let capturedEffect: (() => (() => void) | void) | null = null
    const originalWindow = globalThis.window
    const mockWindow = {
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    }

    try {
      globalThis.window = mockWindow as unknown as Window & typeof globalThis
      const onClose = vi.fn()

      renderDrawerComponent(
        {
          open: false,
          onClose,
          title: 'Closed Drawer',
        },
        (effect) => {
          capturedEffect = effect
        }
      )

      const effectFn = capturedEffect as (() => (() => void) | void) | null
      const cleanup = effectFn ? effectFn() : undefined
      expect(mockWindow.addEventListener).not.toHaveBeenCalled()
      expect(cleanup).toBeUndefined()
    } finally {
      if (originalWindow !== undefined) {
        globalThis.window = originalWindow
      } else {
        const g = globalThis as { window?: unknown }
        delete g.window
      }
    }
  })

  it('calls onClose when clicking the backdrop', () => {
    const onClose = vi.fn()
    const tree = renderDrawerComponent({
      open: true,
      onClose,
      title: 'Backdrop Test Drawer',
    })

    const backdrop = findElementByTestId(tree, 'drawer-backdrop')
    expect(backdrop).not.toBeNull()
    expect((backdrop!.props as { onClick?: unknown }).onClick).toBeTypeOf('function')

    ;(backdrop!.props as { onClick: () => void }).onClick()
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('calls onClose when clicking the close button', () => {
    const onClose = vi.fn()
    const tree = renderDrawerComponent({
      open: true,
      onClose,
      title: 'Close Button Test Drawer',
    })

    const closeButton = findElementByTestId(tree, 'drawer-close-button')
    expect(closeButton).not.toBeNull()
    expect((closeButton!.props as { onClick?: unknown }).onClick).toBeTypeOf('function')

    ;(closeButton!.props as { onClick: () => void }).onClick()
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('calls stopPropagation when clicking the drawer panel and does not call onClose', () => {
    const onClose = vi.fn()
    const tree = renderDrawerComponent({
      open: true,
      onClose,
      title: 'Panel Stop Propagation Test',
    })

    const panel = findElementByTestId(tree, 'session-inspector-drawer')
    expect(panel).not.toBeNull()
    expect((panel!.props as { onClick?: unknown }).onClick).toBeTypeOf('function')

    const stopPropagation = vi.fn()
    ;(panel!.props as { onClick: (e: { stopPropagation: () => void }) => void }).onClick({ stopPropagation })
    expect(stopPropagation).toHaveBeenCalledTimes(1)
    expect(onClose).not.toHaveBeenCalled()
  })
})

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

const CLIPPED_SYSTEM_PROMPT =
  'You are a diagnostic coding agent.\nFollow the repository rules exactly.\n' + 'x'.repeat(80)
const FULL_SYSTEM_PROMPT = CLIPPED_SYSTEM_PROMPT + 'x'.repeat(3000) + '\nUNIQUE_FULL_ENDING'

const bucketRawContent = {
  bucket: 'system_prompt',
  key: 'system_prompt',
  tokens: 5800,
  value: 5800,
  total: 12000,
  label: 'System prompt',
  content: CLIPPED_SYSTEM_PROMPT,
  turn: { spanId: 'span-prompt', index: 1 },
}

const contentRequest = {
  sessionId: 'sess-content-001',
  span: 'span-prompt',
  part: 'system_prompt',
}

const contentQueryKey = [
  'kyber-session-content',
  contentRequest.sessionId,
  contentRequest.span,
  contentRequest.part,
]

function renderDrawerWithQuery(
  qc: QueryClient,
  extra?: Partial<React.ComponentProps<typeof SessionInspectorDrawer>>,
): string {
  return renderHtml(
    <QueryClientProvider client={qc}>
      <SessionInspectorDrawer
        open={true}
        onClose={() => {}}
        title="Turn 1 · system_prompt"
        rawContent={bucketRawContent}
        contentRequest={contentRequest}
        {...extra}
      />
    </QueryClientProvider>
  )
}

describe('SessionInspectorDrawer: Full-content endpoint', () => {
  it('renders unclipped text from the content query instead of the payload stub', () => {
    const qc = createTestQueryClient()
    const body: KyberSessionContentResult = {
      sessionId: contentRequest.sessionId,
      spanId: contentRequest.span,
      parts: [
        {
          spanId: contentRequest.span,
          part: 'system_prompt',
          text: FULL_SYSTEM_PROMPT,
          tokens: 5800,
        },
      ],
    }
    qc.setQueryData(contentQueryKey, body)

    const html = renderDrawerWithQuery(qc)

    expect(html).toContain('UNIQUE_FULL_ENDING')
    expect(html).toContain(FULL_SYSTEM_PROMPT.slice(-40))
    expect(html).toContain('data-testid="drawer-content-scroll"')
    expect(html).toContain('overflow-auto')
    expect(html).toContain('data-testid="context-bucket-inspector"')
    expect(html).not.toContain('data-testid="drawer-content-loading"')
    expect(html).not.toContain('data-testid="drawer-content-fallback-note"')
    expect(html).not.toContain('data-testid="drawer-content-truncated"')
  })

  it('states showing X of Y characters when a part is truncated', () => {
    const qc = createTestQueryClient()
    const shown = 'R'.repeat(120)
    const totalLength = 2_050_000
    const body: KyberSessionContentResult = {
      sessionId: contentRequest.sessionId,
      spanId: contentRequest.span,
      parts: [
        {
          spanId: contentRequest.span,
          part: 'system_prompt',
          text: shown,
          truncated: true,
          totalLength,
        },
      ],
    }
    qc.setQueryData(contentQueryKey, body)

    const html = renderDrawerWithQuery(qc)

    expect(html).toContain('data-testid="drawer-content-truncated"')
    expect(html).toContain(`showing ${shown.length} of ${totalLength} characters`)
    expect(html).toContain(shown)
    expect(html).toContain('data-testid="drawer-content-scroll"')
    expect(html).toContain('overflow-auto')
  })

  it('shows a loading state while the content query has no result', () => {
    const qc = createTestQueryClient()
    const origFetch = globalThis.fetch
    globalThis.fetch = () => new Promise(() => {})

    try {
      const html = renderDrawerWithQuery(qc)
      expect(html).toContain('data-testid="drawer-content-loading"')
      expect(html).toContain('Loading full content')
      expect(html).not.toContain('UNIQUE_FULL_ENDING')
      expect(html).not.toContain('data-testid="drawer-content-fallback-note"')
    } finally {
      globalThis.fetch = origFetch
    }
  })

  it('falls back to clipped payload text with a visible note when the content query fails', () => {
    const qc = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
          gcTime: 0,
          retryOnMount: false,
        },
      },
    })
    const error = new Error('Request failed (500) for /api/kyber/session/sess-content-001/content')
    const query = qc.getQueryCache().build(qc, {
      queryKey: contentQueryKey,
      queryFn: () => Promise.reject(error),
    })
    query.setState({
      status: 'error',
      error,
      fetchStatus: 'idle',
      data: undefined,
      dataUpdatedAt: 0,
      errorUpdatedAt: Date.now(),
      isInvalidated: false,
    })

    const html = renderDrawerWithQuery(qc)

    expect(html).toContain('data-testid="drawer-content-fallback-note"')
    expect(html).toContain('truncated')
    expect(html).toContain(CLIPPED_SYSTEM_PROMPT)
    expect(html).toContain('data-testid="drawer-content-scroll"')
    expect(html).not.toContain('UNIQUE_FULL_ENDING')
    expect(html).not.toContain('data-testid="drawer-content-loading"')
    expect(html).toContain('data-testid="context-bucket-inspector"')
  })
})

function unescapeHtml(html: string): string {
  return html
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#x27;/g, "'")
    .replace(/&#39;/g, "'")
}

// A Copilot-shaped turn row: the content object carries the flat canonical
// buckets as strings (never input/output messages), including a JSON-encoded
// tool-definitions blob. The raw tool JSON is hand-written so the
// out-of-safe-range integer literal below reaches the renderer byte-for-byte
// instead of being mangled by an intermediate JS number.
const COPILOT_PROMPT_HEAD =
  'You are a diagnostic coding agent operating inside the kyber-weave repository.'
const COPILOT_PROMPT_TAIL = 'UNIQUE_PROMPT_TAIL_9c4e2b'
const COPILOT_LONG_SYSTEM_PROMPT = [
  COPILOT_PROMPT_HEAD,
  'Follow the repository rules exactly; never invent tooling or paths.',
  ...Array.from(
    { length: 60 },
    (_, i) => `Guideline ${String(i + 1).padStart(2, '0')}: preserve every user-visible byte of content.`,
  ),
  '<context>Repository checkout: kyber-weave integration worktree.</context>',
  `Final reminder before the tail marker: ${'restate-the-task '.repeat(120)}`,
  COPILOT_PROMPT_TAIL,
].join('\n')

const COPILOT_BIG_REQUEST_ID = '9007199254740993'
const COPILOT_TOOL_DEFINITIONS_RAW =
  '[{"name": "zeta_tool", "description": "Reads a file from disk.", "request_id": 9007199254740993, "parameters": {"type": "object", "properties": {"path": {"type": "string", "description": "Absolute file path."}, "offset": {"type": "integer", "minimum": 0}}, "required": ["path"]}, "enabled": true, "timeout_ms": null}, {"name": "alpha_tool", "description": "Runs a shell command.", "parameters": {"type": "object", "properties": {"command": {"type": "string"}}}, "enabled": false}]'

const COPILOT_HISTORY_RAW = [
  '{"role": "user", "content": "Investigate the failing refresh gate.", "attachments": []}',
  '{"role": "assistant", "content": "I will start by reading the session row.", "tool_calls": [{"name": "zeta_tool", "arguments": {"path": "/tmp/session.json"}}]}',
].join('\n')

const copilotTurn = {
  index: 2,
  model: 'gpt-5',
  fresh: 9500,
  cache_read: 40000,
  cache_creation: 500,
  output: 700,
  durationMs: 2310,
  content: {
    system_prompt: COPILOT_LONG_SYSTEM_PROMPT,
    instruction_context: 'Org policy: do not exfiltrate secrets.\nSecond line of policy.',
    tool_definitions: COPILOT_TOOL_DEFINITIONS_RAW,
    conversation_history: COPILOT_HISTORY_RAW,
    tool_result_content: 'read_file /tmp/session.json produced 42 lines',
    custom_telemetry_note: 'retained verbatim',
  },
}

describe('SessionInspectorDrawer: Copilot turn readability', () => {
  it('keeps the canonical bucket order with the system prompt first', () => {
    expect([...TURN_CONTENT_SECTION_ORDER]).toEqual([
      'system_prompt',
      'instruction_context',
      'tool_definitions',
      'conversation_history',
      'tool_result_content',
    ])
  })

  it('pretty-prints nested JSON with a whitespace-only transformation', () => {
    expect(prettyPrintJson('{"b":2,"a":[1,{"c":null}]}')).toBe(
      '{\n  "b": 2,\n  "a": [\n    1,\n    {\n      "c": null\n    }\n  ]\n}'
    )
    const once = prettyPrintJson(COPILOT_TOOL_DEFINITIONS_RAW)
    expect(once).not.toBeNull()
    expect(once).toContain(COPILOT_BIG_REQUEST_ID)
    // Formatting twice changes nothing: the first pass is already canonical.
    expect(prettyPrintJson(once!)).toBe(once)
    // Invalid JSON falls back to null so callers keep plain text rendering.
    expect(prettyPrintJson('{oops')).toBeNull()
    expect(prettyPrintJson('just text')).toBeNull()
    expect(prettyPrintJson('')).toBeNull()
  })

  it('splits top-level array items without reordering or re-serializing', () => {
    const items = splitTopLevelArrayItems(COPILOT_TOOL_DEFINITIONS_RAW)
    expect(items).not.toBeNull()
    expect(items!.length).toBe(2)
    expect(items![0]).toContain('"zeta_tool"')
    expect(items![1]).toContain('"alpha_tool"')
    expect(items![0]).toContain(COPILOT_BIG_REQUEST_ID)
    expect(splitTopLevelArrayItems('[]')).toEqual([])
    expect(splitTopLevelArrayItems('{"a":1}')).toBeNull()
    expect(splitTopLevelArrayItems('[1,]')).toBeNull()
  })

  it('detects JSON-shaped text', () => {
    expect(isJsonText('  {"a": 1}  ')).toBe(true)
    expect(isJsonText('[1,2]')).toBe(true)
    expect(isJsonText('plain text')).toBe(false)
    expect(isJsonText('{oops')).toBe(false)
    expect(isJsonText('')).toBe(false)
  })

  it('formats JSON-lines history without losing any line', () => {
    const pretty = prettyPrintJsonLines(COPILOT_HISTORY_RAW)
    expect(pretty).not.toBeNull()
    expect(pretty).toContain('Investigate the failing refresh gate.')
    expect(pretty).toContain('I will start by reading the session row.')
    expect(prettyPrintJsonLines('{"a":1}')).toBeNull()
    expect(prettyPrintJsonLines('{"a":1}\nnot json')).toBeNull()
  })

  it('renders every Copilot content bucket as a labeled section with full content', () => {
    const element = React.createElement(InspectorContent, { data: copilotTurn })
    const html = renderHtml(element)

    // Clear labels and sections for the system prompt and associated data.
    expect(html).toContain('data-testid="turn-content-sections"')
    expect(html).toContain('data-testid="turn-content-section-system_prompt"')
    expect(html).toContain('System prompt')
    expect(html).toContain('Tool definitions')
    expect(html).toContain('Conversation history')
    expect(html).toContain('Instruction context')
    expect(html).toContain('Tool results')
    expect(html).toContain('Custom telemetry note')

    // The long multiline system prompt survives in full, head and tail.
    expect(html).toContain(COPILOT_PROMPT_HEAD)
    expect(html).toContain(COPILOT_PROMPT_TAIL)
    expect(html).toContain('Guideline 01:')
    expect(html).toContain('Guideline 60:')
    expect(html).toContain('Repository checkout: kyber-weave integration worktree.')

    // Structured tool definitions survive with nesting, edge values, order.
    expect(html).toContain('zeta_tool')
    expect(html).toContain('alpha_tool')
    expect(html.indexOf('zeta_tool')).toBeLessThan(html.indexOf('alpha_tool'))
    expect(html).toContain('Absolute file path.')
    expect(html).toContain(COPILOT_BIG_REQUEST_ID)
    expect(html).not.toContain('9007199254740992')

    // History lines, policy text, tool output, and the unknown extra key.
    expect(html).toContain('Investigate the failing refresh gate.')
    expect(html).toContain('I will start by reading the session row.')
    expect(html).toContain('Org policy: do not exfiltrate secrets.')
    expect(html).toContain('produced 42 lines')
    expect(html).toContain('retained verbatim')

    // Structured blocks keep item sections and the exact-raw affordance.
    expect(html).toContain('data-testid="json-string-view"')
    expect(html).toContain('data-testid="json-item-0"')
    expect(html).toContain('data-testid="json-item-1"')
  })

  it('keeps the raw view of a JSON bucket byte-exact', () => {
    const element = React.createElement(JsonStringView, {
      text: COPILOT_TOOL_DEFINITIONS_RAW,
      defaultView: 'raw',
    })
    const html = renderHtml(element)
    expect(unescapeHtml(html)).toContain(COPILOT_TOOL_DEFINITIONS_RAW)

    // The formatted default shows the same literals, only re-indented.
    const formatted = renderHtml(React.createElement(JsonStringView, { text: COPILOT_TOOL_DEFINITIONS_RAW }))
    expect(formatted).toContain(COPILOT_BIG_REQUEST_ID)
    expect(formatted).not.toContain('9007199254740992')
    expect(formatted).toContain('data-testid="json-item-0"')
    expect(formatted).toContain('data-testid="json-item-1"')
  })

  it('routes JSON strings to the structured view and prose to folded text', () => {
    const jsonHtml = renderHtml(
      React.createElement(InspectorContent, { data: '{"role":"system","content":"hello"}' })
    )
    expect(jsonHtml).toContain('data-testid="json-string-view"')
    expect(jsonHtml).toContain('hello')

    const proseHtml = renderHtml(React.createElement(InspectorContent, { data: 'plain prose prompt' }))
    expect(proseHtml).toContain('plain prose prompt')
    expect(proseHtml).not.toContain('data-testid="json-string-view"')
  })

  it('renders real-object tool definitions as named cards and skips message keys', () => {
    const content = {
      prompt_text: 'already rendered above',
      tool_definitions: [
        { name: 'read_file', description: 'Reads a file.' },
        { function: { name: 'wrapped_tool' }, extra: 7 },
      ],
    }
    const html = renderHtml(React.createElement(TurnContentSections, { content }))
    expect(html).toContain('data-testid="turn-content-section-tool_definitions"')
    expect(html).toContain('read_file')
    expect(html).toContain('wrapped_tool')
    expect(html).toContain('Tool 1 of 2')
    expect(html).toContain('Tool 2 of 2')
    expect(html).not.toContain('already rendered above')
    expect(html).not.toContain('turn-content-section-prompt_text')
  })

  it('renders nothing only for absent buckets; explicit empty values get markers', () => {
    // Message-only keys stay rendered elsewhere, so no sections here.
    expect(renderHtml(React.createElement(TurnContentSections, { content: { prompt_text: 'x' } }))).toBe('')
    // Absent top-level content renders nothing; explicit null renders visibly.
    expect(renderHtml(React.createElement(TurnContentSections, { content: undefined }))).toBe('')
    const nullHtml = renderHtml(React.createElement(TurnContentSections, { content: null }))
    expect(nullHtml).toContain('data-testid="turn-content-sections"')
    expect(nullHtml).toContain('data-testid="explicit-null-value"')
    expect(nullHtml).toContain('>null<')
    // A whole empty-string content value is explicit: show a marker section.
    const emptyContentHtml = renderHtml(React.createElement(TurnContentSections, { content: '' }))
    expect(emptyContentHtml).toContain('data-testid="turn-content-sections"')
    expect(emptyContentHtml).toContain('data-testid="turn-content-section-content"')
    expect(emptyContentHtml).toContain('empty string')
    const emptyTextHtml = renderHtml(React.createElement(ReadableContentText, { text: '' }))
    expect(emptyTextHtml).toContain('empty string')
  })

  it('preserves explicit null, empty string, and undefined content values', () => {
    const content = {
      system_prompt: null,
      instruction_context: '',
      conversation_history: undefined,
      tool_result_content: 'ok',
    }
    const html = renderHtml(React.createElement(TurnContentSections, { content }))
    expect(html).toContain('data-testid="turn-content-section-system_prompt"')
    expect(html).toContain('data-testid="explicit-null-value"')
    expect(html).toContain('>null<')
    expect(html).toContain('data-testid="turn-content-section-instruction_context"')
    expect(html).toContain('data-testid="empty-string-value"')
    expect(html).toContain('empty string')
    expect(html).toContain('data-testid="turn-content-section-conversation_history"')
    expect(html).toContain('data-testid="explicit-undefined-value"')
    expect(html).toContain('>undefined<')
    expect(html).toContain('data-testid="turn-content-section-tool_result_content"')
    expect(html).toContain('ok')
    // A key that is not present at all renders no section.
    expect(html).not.toContain('turn-content-section-tool_definitions')
  })

  it('distinguishes false, zero, and empty collections from null and empty string', () => {
    const content = {
      system_prompt: false,
      instruction_context: 0,
      conversation_history: [],
      tool_result_content: {},
    }
    const html = renderHtml(React.createElement(TurnContentSections, { content }))
    expect(html).toContain('data-testid="turn-content-section-system_prompt"')
    expect(html).toContain('false')
    expect(html).toContain('data-testid="turn-content-section-instruction_context"')
    expect(html).toContain('>0<')
    expect(html).toContain('empty list')
    expect(html).toContain('empty object')
    expect(html).not.toContain('explicit-null-value')
    expect(html).not.toContain('empty-string-value')
  })

  it('preserves explicit top-level values instead of dropping them', () => {
    // Explicit top-level null stays visible as "null".
    const nullHtml = renderHtml(React.createElement(TurnContentSections, { content: null }))
    expect(nullHtml).toContain('data-testid="turn-content-sections"')
    expect(nullHtml).toContain('data-testid="turn-content-section-content"')
    expect(nullHtml).toContain('data-testid="explicit-null-value"')
    expect(nullHtml).toContain('>null<')

    // An explicitly empty object stays visible as "empty object".
    const emptyObjectHtml = renderHtml(React.createElement(TurnContentSections, { content: {} }))
    expect(emptyObjectHtml).toContain('data-testid="turn-content-sections"')
    expect(emptyObjectHtml).toContain('empty object')

    // Top-level scalars keep their full value.
    const falseHtml = renderHtml(React.createElement(TurnContentSections, { content: false }))
    expect(falseHtml).toContain('data-testid="turn-content-sections"')
    expect(falseHtml).toContain('false')
    const zeroHtml = renderHtml(React.createElement(TurnContentSections, { content: 0 }))
    expect(zeroHtml).toContain('data-testid="turn-content-sections"')
    expect(zeroHtml).toContain('>0<')

    // Arrays keep order; an empty array stays distinct as "empty list".
    const arrayHtml = renderHtml(
      React.createElement(TurnContentSections, { content: ['zeta', 'alpha'] })
    )
    expect(arrayHtml).toContain('data-testid="turn-content-sections"')
    expect(arrayHtml).toContain('zeta')
    expect(arrayHtml).toContain('alpha')
    expect(arrayHtml.indexOf('zeta')).toBeLessThan(arrayHtml.indexOf('alpha'))
    const emptyArrayHtml = renderHtml(React.createElement(TurnContentSections, { content: [] }))
    expect(emptyArrayHtml).toContain('data-testid="turn-content-sections"')
    expect(emptyArrayHtml).toContain('empty list')

    // Undefined is absent at this component boundary: nothing renders.
    expect(renderHtml(React.createElement(TurnContentSections, { content: undefined }))).toBe('')
  })
})

describe('SessionInspectorDrawer: Copilot turn lossless rendering', () => {
  it('shows a long nested string in full with no preview collapse', () => {
    const tail = 'UNIQUE_NESTED_TAIL_7f3a1c'
    const longNested = `HEAD_${'A'.repeat(5000)}_${tail}`
    const content = {
      system_prompt: { outer: { inner: longNested, sibling: 'second-field-stays' } },
    }
    const html = renderHtml(React.createElement(TurnContentSections, { content }))
    const text = unescapeHtml(html)
    // Every character survives, head through tail, with no preview gate.
    expect(text).toContain(longNested)
    expect(text).toContain(tail)
    expect(text).toContain('second-field-stays')
    expect(html).toContain('data-testid="turn-lossless-text"')
    expect(html).toContain('data-testid="turn-lossless-object"')
    // The generic collapsed preview is scoped out of turn content.
    expect(html).not.toContain('data-testid="key-value-list"')
  })

  it('preserves XML content with leading/trailing whitespace byte-for-byte', () => {
    const padded = '  \n  <context>  padded content with spaces  </context>  \n  '
    const content = { system_prompt: padded }
    const html = renderHtml(React.createElement(TurnContentSections, { content }))
    const text = unescapeHtml(html)
    // Whitespace and markup survive as literal content: nothing trimmed.
    expect(text).toContain(padded)
    expect(text).toContain('  padded content with spaces  ')
    expect(text).toContain('<context>')
    expect(html).toContain('data-testid="turn-lossless-text"')
  })

  it('shows complete long tool-definition fields without clipping', () => {
    const longDescription = `LONG_DESC_HEAD_${'D'.repeat(3000)}_UNIQUE_TOOL_DESC_TAIL_4b9e`
    const longName = `super_long_tool_name_${'N'.repeat(500)}_UNIQUE_TOOL_NAME_TAIL`
    const content = {
      tool_definitions: [
        {
          name: longName,
          description: longDescription,
          parameters: { type: 'object', properties: { path: { type: 'string' } } },
          enabled: true,
        },
      ],
    }
    const html = renderHtml(React.createElement(TurnContentSections, { content }))
    const text = unescapeHtml(html)
    expect(text).toContain(longDescription)
    expect(text).toContain('UNIQUE_TOOL_DESC_TAIL_4b9e')
    expect(text).toContain(longName)
    expect(text).toContain('UNIQUE_TOOL_NAME_TAIL')
    expect(html).toContain('data-testid="tool-definition-0"')
    expect(html).toContain('Tool 1 of 1')
  })

  it('distinguishes nested null/undefined/empty values with visible markers', () => {
    const content = {
      system_prompt: {
        explicitNull: null,
        explicitUndefined: undefined,
        explicitEmpty: '',
        explicitFalse: false,
        explicitZero: 0,
        explicitEmptyList: [] as unknown[],
        explicitEmptyObject: {} as Record<string, unknown>,
      },
    }
    const html = renderHtml(React.createElement(TurnContentSections, { content }))
    expect(html).toContain('data-testid="explicit-null-value"')
    expect(html).toContain('>null<')
    expect(html).toContain('data-testid="explicit-undefined-value"')
    expect(html).toContain('>undefined<')
    expect(html).toContain('data-testid="empty-string-value"')
    expect(html).toContain('empty string')
    expect(html).toContain('false')
    expect(html).toContain('>0<')
    expect(html).toContain('empty list')
    expect(html).toContain('empty object')
  })

  it('keeps nested array and object order without reordering', () => {
    const content = {
      conversation_history: ['zeta-first-1', 'alpha-second-2', 'mid-third-3'],
      system_prompt: { zulu: 'z-value', alpha: 'a-value', mike: 'm-value' },
    }
    const html = renderHtml(React.createElement(TurnContentSections, { content }))
    const zeta = html.indexOf('zeta-first-1')
    const alpha = html.indexOf('alpha-second-2')
    const mid = html.indexOf('mid-third-3')
    expect(zeta).toBeGreaterThanOrEqual(0)
    expect(alpha).toBeGreaterThan(zeta)
    expect(mid).toBeGreaterThan(alpha)
    const zulu = html.indexOf('z-value')
    const aValue = html.indexOf('a-value')
    const mValue = html.indexOf('m-value')
    expect(zulu).toBeGreaterThanOrEqual(0)
    expect(aValue).toBeGreaterThan(zulu)
    expect(mValue).toBeGreaterThan(aValue)
    expect(html).toContain('Item 1 of 3')
    expect(html).toContain('Item 3 of 3')
  })

  it('renders lossless text without trimming or folding', () => {
    const raw = '  leading and trailing spaces stay  '
    const html = renderHtml(React.createElement(TurnLosslessText, { text: raw }))
    expect(unescapeHtml(html)).toContain(raw)
    expect(html).toContain('data-testid="turn-lossless-text"')
  })

  it('routes invalid JSON-shaped strings to lossless text instead of folding', () => {
    const raw = '{"broken": json, <context>markup</context>}'
    const element = React.createElement(TurnLosslessValue, { value: raw })
    const html = renderHtml(element)
    const text = unescapeHtml(html)
    expect(text).toContain(raw)
    expect(text).toContain('<context>')
    expect(html).toContain('data-testid="turn-lossless-text"')
    expect(html).not.toContain('data-testid="json-string-view"')
  })
})

describe('SessionInspectorDrawer: nested JSON readability', () => {
  // Hand-written so the out-of-safe-range literal reaches the renderer
  // byte-for-byte instead of being mangled by an intermediate JS number.
  const NESTED_INNER_RAW =
    '{"inner_key": "INNER_STRUCTURE_9f2", "count": 42, "request_id": 9007199254740993, "list": [1, 2]}'
  const NESTED_OUTER_RAW = `{"context_blob": ${JSON.stringify(NESTED_INNER_RAW)}, "plain": "  hello with spaces  ", "items": [${JSON.stringify(NESTED_INNER_RAW)}, "plain-item"]}`

  it('recurses into JSON-encoded string values in formatted mode', () => {
    const pretty = prettyPrintJsonNested(NESTED_OUTER_RAW)
    expect(pretty).not.toBeNull()
    // The nested structure is exposed, not left as one escaped line.
    expect(pretty!).toContain('"inner_key"')
    expect(pretty!).toContain('INNER_STRUCTURE_9f2')
    expect(pretty!).toContain('"request_id"')
    expect(pretty!).toContain('9007199254740993')
    expect(pretty!).not.toContain('9007199254740992')
    // Formatting stays stable: a second pass changes nothing.
    expect(prettyPrintJsonNested(pretty!)).toBe(pretty)
    // Invalid input still falls back to null.
    expect(prettyPrintJsonNested('{oops')).toBeNull()
    expect(prettyPrintJsonNested('just text')).toBeNull()
  })

  it('exposes nested structure inside objects and arrays via JsonStringView', () => {
    const element = React.createElement(JsonStringView, { text: NESTED_OUTER_RAW })
    const html = renderHtml(element)
    const text = unescapeHtml(html)
    expect(html).toContain('data-testid="json-string-view"')
    expect(text).toContain('INNER_STRUCTURE_9f2')
    expect(text).toContain('inner_key')
    expect(text).toContain('9007199254740993')
    expect(text).not.toContain('9007199254740992')
    expect(text).toContain('hello with spaces')
    expect(text).toContain('plain-item')
  })

  it('keeps the original input string exact in Raw mode and Copy', () => {
    const padded = `  ${NESTED_OUTER_RAW}  \n`
    const rawHtml = renderHtml(React.createElement(JsonStringView, { text: padded, defaultView: 'raw' }))
    // Raw view exposes the exact original bytes, including significant
    // surrounding whitespace and nested escaped values.
    expect(unescapeHtml(rawHtml)).toContain(padded)
    expect(rawHtml).toContain('data-testid="json-raw"')

    // Copy writes the exact original string, not the formatted expansion.
    const written: string[] = []
    const nav = (globalThis as { navigator?: unknown }).navigator
    const hadNavigator = nav !== undefined
    Object.defineProperty(globalThis, 'navigator', {
      value: { clipboard: { writeText: (s: string) => { written.push(s); return Promise.resolve() } } },
      configurable: true,
    })
    try {
      const tree = React.createElement(JsonStringView, { text: padded, defaultView: 'raw' })
      const rendered = (JsonStringView as (p: unknown) => unknown)({ text: padded, defaultView: 'raw' })
      void tree
      const buttons: React.ReactElement[] = []
      const walk = (node: unknown): void => {
        if (node == null) return
        if (Array.isArray(node)) {
          node.forEach(walk)
          return
        }
        if (React.isValidElement(node)) {
          const props = node.props as Record<string, unknown>
          if (node.type === 'button' && props['children'] === 'Copy') buttons.push(node as React.ReactElement)
          walk(props['children'])
        }
      }
      walk(rendered)
      expect(buttons.length).toBeGreaterThan(0)
      ;(buttons[0]!.props as { onClick: () => void }).onClick()
      expect(written).toEqual([padded])
    } finally {
      if (hadNavigator) {
        Object.defineProperty(globalThis, 'navigator', { value: nav, configurable: true })
      } else {
        const g = globalThis as { navigator?: unknown }
        delete g.navigator
      }
    }
  })

  it('formats nested JSON-lines without losing any line', () => {
    const lineInner = '{"deep": "DEEP_LINE_VALUE_77"}'
    const rawLines = `{"role": "user", "blob": ${JSON.stringify(lineInner)}}\n{"role": "assistant", "blob": ${JSON.stringify(lineInner)}}`
    const pretty = prettyPrintJsonLinesNested(rawLines) ?? prettyPrintJsonLines(rawLines)
    expect(pretty).not.toBeNull()
    expect(pretty!).toContain('DEEP_LINE_VALUE_77')
    expect(pretty!).toContain('"deep"')
  })

  it('bounds deeply nested JSON-encoded strings while keeping data visible', () => {
    const marker = 'BOUNDARY_LEAF_MARKER_5c1e'
    let current = `{"leaf": "${marker}"}`
    // One layer past the expansion cap (10): proves the bound stops recursion
    // while the tail stays visible. Sizes double per JSON-string layer, so
    // layers stay just over the cap to avoid a multi-MB fixture.
    const layers = 11
    for (let i = 0; i < layers; i++) {
      current = JSON.stringify({ nested: current })
    }
    const pretty = prettyPrintJsonNested(current)
    expect(pretty).not.toBeNull()
    // Outer layers expand; the marker stays visible either decoded or as an
    // escaped literal once the bound stops further expansion.
    expect(pretty!).toContain('"nested"')
    expect(pretty!).toContain(marker)
    // The bound holds: the tail past the cap remains an escaped string
    // instead of recursing without limit.
    expect(pretty!).toContain('\\"')
    // Shallow nesting still fully expands with no leftover escapes.
    let shallow = `{"leaf": "${marker}"}`
    for (let i = 0; i < 2; i++) {
      shallow = JSON.stringify({ nested: shallow })
    }
    const shallowPretty = prettyPrintJsonNested(shallow)
    expect(shallowPretty).not.toBeNull()
    expect(shallowPretty!).toContain(marker)
    expect(shallowPretty!).not.toContain('\\"')
  })

  it('falls back to visible text for hostile structural nesting', () => {
    const deep = `${'['.repeat(500)}${']'.repeat(500)}`
    const pretty = prettyPrintJsonNested(deep)
    expect(pretty).not.toBeNull()
    expect(pretty!).toContain('[')
    expect(pretty!).toContain(']')
  })
})

describe('SessionInspectorDrawer: whole-turn full context', () => {
  const TURN_SESSION_ID = 'sess-turn-001'
  const TURN_INDEX = 1
  const TURN_KEY = ['kyber-turn-content-full', TURN_SESSION_ID, TURN_INDEX]

  const SHARED_PROMPT = 'SHARED_SESSION_PROMPT_HEAD_8aa1\nFollow every rule.\nSHARED_PROMPT_TAIL_3f9c'
  const OWN_SYSTEM_PROMPT = 'TURN_OWN_SYSTEM_PROMPT_HEAD_51bd\nTurn-specific instructions.\nOWN_PROMPT_TAIL_c742'
  const MYSTERY_TEXT = 'MYSTERY_PAYLOAD_9z7q exact bytes <> & "preserved"'
  const LONG_TEXT = `LONG_HEAD_d4e2_${'L'.repeat(8000)}_LONG_TAIL_9b1f`

  function buildTurnFixture(overrides?: Partial<KyberTurnContentResult>): KyberTurnContentResult {
    const sysPart = { id: 'part-sys-0', spanId: 'span-turn-1', part: 'system_prompt', label: 'System Prompt', text: OWN_SYSTEM_PROMPT, tokens: 150 }
    const toolPart = { id: 'part-tool-1', spanId: 'span-turn-1', part: 'tool_definitions', label: 'Tools', text: '{"name": "zeta_tool"}', tokens: 0 }
    const userPart = { id: 'part-user-2', spanId: 'span-turn-1', part: 'user_messages', label: 'User Messages', text: 'user asks about the failing gate' }
    const asstPart = { id: 'part-asst-3', spanId: 'span-turn-1', part: 'assistant_turns', label: 'Assistant Turns', text: 'assistant starts reading the row' }
    const resPart = { id: 'part-res-4', spanId: 'span-turn-1', part: 'tool_result_content', label: 'Tool Results', text: 'read_file produced 42 lines' }
    const mysteryPart = { id: 'part-other-5', spanId: 'span-turn-1', part: 'mystery_blob', label: 'mystery blob', text: MYSTERY_TEXT }
    const parts = [sysPart, toolPart, userPart, asstPart, resPart, mysteryPart]
    const blocks = [
      { key: 'system_prompt', label: 'System prompt', tokens: 150, parts: [sysPart], text: OWN_SYSTEM_PROMPT },
      { key: 'tool_definitions', label: 'Tools', tokens: 0, parts: [toolPart], text: '{"name": "zeta_tool"}' },
      {
        key: 'conversation_history',
        label: 'Conversation history',
        parts: [userPart, asstPart],
        text: 'user asks about the failing gate\n\nassistant starts reading the row',
      },
      { key: 'tool_result_content', label: 'Tool results', parts: [resPart], text: 'read_file produced 42 lines' },
    ]
    return {
      sessionId: TURN_SESSION_ID,
      turnIndex: TURN_INDEX,
      spanId: 'span-turn-1',
      model: 'gpt-5',
      blocks,
      parts,
      assembledText: [...blocks.map((b) => b.text), MYSTERY_TEXT].join('\n\n'),
      ...overrides,
    } as KyberTurnContentResult
  }

  function seedTurn(
    qc: QueryClient,
    body: KyberTurnContentResult,
    shared: { texts: string[]; seenBefore: boolean } | null = { texts: [SHARED_PROMPT], seenBefore: false },
  ) {
    qc.setQueryData(TURN_KEY, {
      ...body,
      sharedContext: shared
        ? { parts: shared.texts.map((text, i) => ({ text, spanId: `span-turn-0-${i}` })), seenBefore: shared.seenBefore }
        : null,
    } satisfies KyberTurnContentResult)
  }

  function renderPanel(
    qc: QueryClient,
    turn: Record<string, unknown> = { fresh: 250, cache_read: 200, cache_creation: 50 },
    defaultView: 'formatted' | 'raw' = 'formatted',
  ): string {
    return renderHtml(
      <QueryClientProvider client={qc}>
        <TurnFullContentPanel
          request={{ sessionId: TURN_SESSION_ID, turnIndex: TURN_INDEX, full: true }}
          turn={turn as unknown as Parameters<typeof TurnFullContentPanel>[0]['turn']}
          defaultView={defaultView}
        />
      </QueryClientProvider>
    )
  }

  it('recognizes whole-turn requests', () => {
    expect(isTurnFullContentRequest({ sessionId: 's', turnIndex: 1, full: true })).toBe(true)
    expect(isTurnFullContentRequest({ sessionId: 's', span: 'x', part: 'system_prompt' })).toBe(false)
    expect(isTurnFullContentRequest(null)).toBe(false)
  })

  it('renders every block and part including unknown kinds and zero-token blocks', () => {
    const qc = createTestQueryClient()
    seedTurn(qc, buildTurnFixture())
    const html = renderPanel(qc)
    const text = unescapeHtml(html)

    expect(html).toContain('data-testid="turn-content-full"')
    for (const key of ['system_prompt', 'tool_definitions', 'conversation_history', 'tool_result_content']) {
      expect(html).toContain(`data-testid="turn-block-${key}"`)
    }
    // Server order is preserved: system prompt first, tool results last.
    expect(html.indexOf('turn-block-system_prompt')).toBeLessThan(html.indexOf('turn-block-tool_result_content'))
    // Zero-token blocks stay visible with an explicit zero, not hidden.
    expect(html).toContain('data-testid="turn-block-tool_definitions-tokens"')
    expect(html).toContain('0 tokens')
    // Unknown part kinds are not dropped: they render under Additional parts
    // with exact text and ordering metadata. Formatted shows every byte;
    // the exact raw bytes sit one Raw toggle away (raw is not printed
    // twice in the default DOM).
    expect(html).toContain('data-testid="turn-additional-parts"')
    expect(html).toContain('mystery_blob')
    expect(text).toContain(MYSTERY_TEXT)
    expect(html).toContain('data-testid="turn-part-mystery_blob-0-view-raw"')
    expect(html).toContain('data-testid="copy-turn-part-mystery_blob-0"')
    expect(text).toContain(OWN_SYSTEM_PROMPT)
    expect(text).toContain('user asks about the failing gate')
    expect(text).toContain('assistant starts reading the row')
    expect(text).toContain('read_file produced 42 lines')

    // Raw mode exposes the exact source bytes for every part.
    const rawHtml = renderPanel(qc, undefined, 'raw')
    expect(rawHtml).toContain('data-testid="turn-part-mystery_blob-0-raw"')
    expect(unescapeHtml(rawHtml)).toContain(MYSTERY_TEXT)
  })

  it('shows shared session context once and skips exact duplicates', () => {
    const qc = createTestQueryClient()
    seedTurn(qc, buildTurnFixture())
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <TurnFullContentPanel
          request={{ sessionId: TURN_SESSION_ID, turnIndex: TURN_INDEX, full: true }}
          turn={{ fresh: 1, cache_read: 2, cache_creation: 3 }}
        />
      </QueryClientProvider>
    )
    const text = unescapeHtml(html)
    expect(html).toContain('data-testid="shared-session-context"')
    expect(html).toContain('Shared session context')
    expect(html).toContain('Session-level system content')
    expect(text).toContain(SHARED_PROMPT)
    expect(text).toContain('SHARED_PROMPT_TAIL_3f9c')
    // Fresh shared content shows no recurrence badge: without earlier-turn
    // evidence the view must not claim "new" (first capture outside turn
    // spans is unverifiable) — the session-level label stands on its own.
    expect(html).not.toContain('data-testid="shared-session-context-recurrence-seen"')
    expect(html).not.toContain('data-testid="shared-session-context-recurrence-new"')
    expect(html).not.toContain('New this turn')
    // Exact raw bytes stay one toggle away with their own copy action.
    expect(html).toContain('data-testid="shared-session-context-part-0-view-raw"')
    expect(html).toContain('data-testid="copy-shared-session-context-0"')
    const sharedRaw = unescapeHtml(renderHtml(
      <QueryClientProvider client={qc}>
        <TurnFullContentPanel
          request={{ sessionId: TURN_SESSION_ID, turnIndex: TURN_INDEX, full: true }}
          turn={{ fresh: 1, cache_read: 2, cache_creation: 3 }}
          defaultView="raw"
        />
      </QueryClientProvider>
    ))
    expect(sharedRaw).toContain('data-testid="shared-session-context-part-0-raw"')
    expect(sharedRaw).toContain(SHARED_PROMPT)
    // The turn's own prompt still renders in its own block.
    expect(text).toContain(OWN_SYSTEM_PROMPT)

    // An exact duplicate already among the turn's parts is not shown twice.
    const dupQc = createTestQueryClient()
    seedTurn(dupQc, buildTurnFixture(), { texts: [OWN_SYSTEM_PROMPT], seenBefore: true })
    const dupHtml = renderHtml(
      <QueryClientProvider client={dupQc}>
        <TurnFullContentPanel
          request={{ sessionId: TURN_SESSION_ID, turnIndex: TURN_INDEX, full: true }}
          turn={{ fresh: 1, cache_read: 2, cache_creation: 3 }}
        />
      </QueryClientProvider>
    )
    expect(dupHtml).not.toContain('data-testid="shared-session-context"')
    expect(unescapeHtml(dupHtml)).toContain(OWN_SYSTEM_PROMPT)
  })

  it('shows measured turn totals separately with no per-block cache claims', () => {
    const qc = createTestQueryClient()
    seedTurn(qc, buildTurnFixture())
    const html = renderPanel(qc, { fresh: 250, cache_read: 200, cache_creation: 50 })

    expect(html).toContain('data-testid="turn-token-totals"')
    expect(html).toContain('Measured turn totals')
    expect(html).toContain('data-testid="turn-total-fresh"')
    expect(html).toContain('data-testid="turn-total-cache-read"')
    expect(html).toContain('data-testid="turn-total-cache-creation"')
    expect(html).toContain('does not map them to specific blocks')
    // Cache figures appear exactly once — in the totals strip, never as a
    // per-block cached/uncached or new/repeated claim.
    expect(html.match(/Cache read/g)?.length).toBe(1)
    expect(html.match(/Cache creation/g)?.length).toBe(1)
    expect(html).not.toContain('repeated')
    expect(html).not.toContain('cached-block')
    expect(html).not.toContain('uncached-block')

    // Missing figures render honestly instead of synthesizing zeros.
    const absentQc = createTestQueryClient()
    seedTurn(absentQc, buildTurnFixture())
    const absentHtml = renderHtml(
      <QueryClientProvider client={absentQc}>
        <TurnFullContentPanel
          request={{ sessionId: TURN_SESSION_ID, turnIndex: TURN_INDEX, full: true }}
          turn={{}}
        />
      </QueryClientProvider>
    )
    expect(absentHtml).toContain('data-testid="turn-token-totals-absent"')
    expect(absentHtml).toContain('token counts not recorded')
  })

  describe('recurrence labels stay independent of cache', () => {
    function buildLabeledFixture(): KyberTurnContentResult {
      const sysPart = { id: 'r-sys', spanId: 'span-r1', part: 'system_prompt', label: 'System Prompt', text: OWN_SYSTEM_PROMPT, recurrence: { seenBefore: true } }
      const toolPart = { id: 'r-tool', spanId: 'span-r1', part: 'tool_definitions', label: 'Tools', text: '{"name": "zeta_tool"}', recurrence: { seenBefore: false } }
      const userPart = { id: 'r-user', spanId: 'span-r1', part: 'user_messages', label: 'User Messages', text: 'same user line', recurrence: { seenBefore: true } }
      const asstPart = { id: 'r-asst', spanId: 'span-r1', part: 'assistant_turns', label: 'Assistant Turns', text: 'fresh assistant line', recurrence: { seenBefore: false } }
      const mysteryPart = { id: 'r-mys', spanId: 'span-r1', part: 'mystery_blob', label: 'mystery blob', text: MYSTERY_TEXT, recurrence: { seenBefore: true } }
      const parts = [sysPart, toolPart, userPart, asstPart, mysteryPart]
      const blocks = [
        { key: 'system_prompt', label: 'System prompt', parts: [sysPart], text: OWN_SYSTEM_PROMPT, recurrence: { newThisTurn: false, seenBefore: true } },
        { key: 'tool_definitions', label: 'Tools', parts: [toolPart], text: '{"name": "zeta_tool"}', recurrence: { newThisTurn: true, seenBefore: false } },
        {
          key: 'conversation_history',
          label: 'Conversation history',
          parts: [userPart, asstPart],
          text: 'same user line\n\nfresh assistant line',
          recurrence: { newThisTurn: true, seenBefore: true },
        },
      ]
      return {
        sessionId: TURN_SESSION_ID,
        turnIndex: TURN_INDEX,
        spanId: 'span-r1',
        model: 'gpt-5',
        blocks,
        parts,
        assembledText: blocks.map((b) => b.text).join('\n\n'),
        sharedContext: { parts: [{ text: SHARED_PROMPT, spanId: 'span-r0' }], seenBefore: true },
      } as KyberTurnContentResult
    }

    function renderLabeled(): string {
      const qc = createTestQueryClient()
      const body = buildLabeledFixture()
      qc.setQueryData(TURN_KEY, body)
      return renderPanel(qc, { fresh: 250, cache_read: 200, cache_creation: 50 })
    }

    it('shows seen-only, new-only, and both badges per block', () => {
      const html = renderLabeled()
      // Repeated-only block: seen badge, no new badge.
      expect(html).toContain('data-testid="turn-block-system_prompt-recurrence-seen"')
      expect(html).not.toContain('data-testid="turn-block-system_prompt-recurrence-new"')
      // New-only block (kind absent from earlier turns): new badge only.
      expect(html).toContain('data-testid="turn-block-tool_definitions-recurrence-new"')
      expect(html).not.toContain('data-testid="turn-block-tool_definitions-recurrence-seen"')
      // Mixed block: both badges at once.
      expect(html).toContain('data-testid="turn-block-conversation_history-recurrence-new"')
      expect(html).toContain('data-testid="turn-block-conversation_history-recurrence-seen"')
      expect(html).toContain('New this turn')
      expect(html).toContain('Also present in prior captured context')
    })

    it('never converts recurrence into a per-block cache claim', () => {
      const html = renderLabeled()
      const text = unescapeHtml(html)
      for (const key of ['system_prompt', 'tool_definitions', 'conversation_history']) {
        expect(html).toContain(`data-testid="turn-block-${key}-cache-note"`)
      }
      expect(text).toContain('Cache: attribution is not reported for individual blocks.')
      // Seen blocks are not called cached; new blocks are not called uncached.
      expect(html).not.toContain('cached')
      expect(html).not.toContain('uncached')
      expect(html).not.toContain('cache-hit')
      // Measured cache figures still appear exactly once — in the turn totals.
      expect(html.match(/Cache read/g)?.length).toBe(1)
      expect(html.match(/Cache creation/g)?.length).toBe(1)
    })

    it('labels shared context recurrence independently of the session-level tag', () => {
      const html = renderLabeled()
      expect(html).toContain('data-testid="shared-session-context"')
      expect(html).toContain('Shared session context')
      expect(html).toContain('data-testid="shared-session-context-recurrence-seen"')
      expect(html).not.toContain('data-testid="shared-session-context-recurrence-new"')
      // Copy Turn still covers the labeled shared text plus every part.
      const copy = buildWholeTurnCopyText(buildLabeledFixture(), [SHARED_PROMPT])
      expect(copy).toContain('=== SHARED SESSION CONTEXT ===')
      expect(copy).toContain(SHARED_PROMPT)
      expect(copy).toContain(MYSTERY_TEXT)
    })

    it('renders every shared part without a recurrence badge when evidence is absent', () => {
      const qc = createTestQueryClient()
      seedTurn(qc, buildTurnFixture(), { texts: [SHARED_PROMPT, 'SECOND_SHARED_PROMPT_BODY_7c'], seenBefore: false })
      const html = renderPanel(qc)
      const text = unescapeHtml(html)
      expect(html).toContain('data-testid="shared-session-context"')
      expect(html).toContain('data-testid="shared-session-context-part-0"')
      expect(html).toContain('data-testid="shared-session-context-part-1"')
      expect(text).toContain(SHARED_PROMPT)
      expect(text).toContain('SECOND_SHARED_PROMPT_BODY_7c')
      // No badge either way: no evidence, no claim.
      expect(html).not.toContain('shared-session-context-recurrence-seen')
      expect(html).not.toContain('shared-session-context-recurrence-new')
    })
  })

  describe('whole-turn view controls and access', () => {
    it('keeps one toggle and one Copy per section for nested JSON content', () => {
      const qc = createTestQueryClient()
      // buildTurnFixture: 4 blocks (tool_definitions holds JSON) + 1 orphan part.
      seedTurn(qc, buildTurnFixture(), null)
      const html = renderPanel(qc)
      // One outer Formatted/Raw/Copy control set per toggle section…
      expect(html.match(/>Formatted<\/button>/g)?.length).toBe(5)
      expect(html.match(/>Raw<\/button>/g)?.length).toBe(5)
      expect(html.match(/>Copy<\/span>/g)?.length).toBe(5)
      // …with no second copy affordance from the nested JSON view.
      expect(html.match(/>Copy<\/button>/g)).toBeNull()
      // …while the nested JSON view keeps its readable content and descriptor.
      expect(html).toContain('data-testid="json-string-view"')
      expect(html).toContain('zeta_tool')
      // The nested switch is suppressed: no second raw/copy affordance.
      expect(html).not.toContain('data-testid="json-raw"')
    })

    it('labels view-control groups with pressed state and announces copies', () => {
      const qc = createTestQueryClient()
      seedTurn(qc, buildTurnFixture(), null)
      const html = renderPanel(qc)
      // Group label names the section; pressed state marks the active view.
      expect(html).toContain('role="group"')
      expect(html).toContain('formatted or raw view')
      expect(html).toContain('data-testid="turn-block-system_prompt-view-formatted"')
      expect(html).toContain('aria-pressed="true"')
      expect(html).toContain('aria-pressed="false"')
      // Copy success regions announce politely for assistive tech.
      expect(html).toContain('aria-live="polite"')
      // Recurrence badges are text, never color alone.
      expect(html).not.toContain('aria-hidden="true">New this turn')
    })

    it('preserves exact whitespace in whole-turn copy', () => {
      const padded = '  padded content with spaces  '
      const gap = '\n   \n'
      const body = buildTurnFixture()
      const wsPart = { id: 'part-ws-9', spanId: 'span-turn-1', part: 'tool_result_content', label: 'Tool Results', text: padded }
      const gapPart = { id: 'part-gap-10', spanId: 'span-turn-1', part: 'tool_result_content', label: 'Tool Results', text: gap }
      const wsBlock = { key: 'ws_block', label: 'Whitespace block', parts: [wsPart, gapPart], text: `${padded}\n\n${gap}` }
      const withWs = {
        ...body,
        blocks: [...body.blocks, wsBlock],
        parts: [...body.parts, wsPart, gapPart],
      } as KyberTurnContentResult
      const copy = buildWholeTurnCopyText(withWs, [])
      // Leading/trailing and whitespace-only source bytes survive verbatim.
      expect(copy).toContain('=== WHITESPACE BLOCK ===')
      expect(copy).toContain(padded)
      expect(copy).toContain(gap)
      expect(copy).toContain(`=== WHITESPACE BLOCK ===\n${padded}\n\n${gap}`)
    })
  })

  it('keeps long text intact with exact raw and copy access', () => {
    const body = buildTurnFixture()
    const longPart = { id: 'part-long-9', spanId: 'span-turn-1', part: 'user_messages', label: 'User Messages', text: LONG_TEXT }
    const longBlock = { key: 'long_block', label: 'Long block', parts: [longPart], text: LONG_TEXT }
    const full = {
      ...body,
      blocks: [...body.blocks, longBlock],
      parts: [...body.parts, longPart],
      assembledText: `${body.assembledText}\n\n${LONG_TEXT}`,
    } as KyberTurnContentResult
    const qc = createTestQueryClient()
    seedTurn(qc, full)
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <TurnFullContentPanel
          request={{ sessionId: TURN_SESSION_ID, turnIndex: TURN_INDEX, full: true }}
          turn={{ fresh: 1, cache_read: 1, cache_creation: 1 }}
        />
      </QueryClientProvider>
    )
    const text = unescapeHtml(html)
    // Every byte survives in formatted view: head through tail, with raw
    // access and copy affordances alongside (raw prints only in raw mode).
    expect(text).toContain(LONG_TEXT)
    expect(text).toContain('LONG_HEAD_d4e2_')
    expect(text).toContain('LONG_TAIL_9b1f')
    expect(html).toContain('data-testid="turn-block-long_block-view-raw"')
    expect(html).toContain('data-testid="copy-turn-block-long_block"')
    expect(html).toContain('data-testid="copy-turn-button"')
    const longRaw = unescapeHtml(renderHtml(
      <QueryClientProvider client={qc}>
        <TurnFullContentPanel
          request={{ sessionId: TURN_SESSION_ID, turnIndex: TURN_INDEX, full: true }}
          turn={{ fresh: 1, cache_read: 1, cache_creation: 1 }}
          defaultView="raw"
        />
      </QueryClientProvider>
    ))
    expect(longRaw).toContain('data-testid="turn-block-long_block-raw"')
    expect(longRaw).toContain(LONG_TEXT)

    // Copy writes the exact bytes, not the formatted view.
    const written: string[] = []
    const nav = (globalThis as { navigator?: unknown }).navigator
    const hadNavigator = nav !== undefined
    Object.defineProperty(globalThis, 'navigator', {
      value: { clipboard: { writeText: (s: string) => { written.push(s); return Promise.resolve() } } },
      configurable: true,
    })
    try {
      const button = CopyExactButton({ text: LONG_TEXT, testId: 'copy-probe' }) as React.ReactElement
      ;(button.props as { onClick: () => void }).onClick()
      expect(written).toEqual([LONG_TEXT])
    } finally {
      if (hadNavigator) {
        Object.defineProperty(globalThis, 'navigator', { value: nav, configurable: true })
      } else {
        const g = globalThis as { navigator?: unknown }
        delete g.navigator
      }
    }
  })

  it('states truncation exactly while still showing returned bytes', () => {
    const returned = 'TRUNCATED_RETURNED_BYTES_6c2a'
    const body = buildTurnFixture({
      assembledText: returned,
      truncated: true,
      totalLength: 5000,
      blocks: [
        {
          key: 'tool_result_content',
          label: 'Tool results',
          parts: [{ id: 'part-res-4', spanId: 'span-turn-1', part: 'tool_result_content', label: 'Tool Results', text: returned }],
          text: returned,
          truncated: true,
          totalLength: 5000,
        },
      ],
    })
    const qc = createTestQueryClient()
    seedTurn(qc, body, null)
    const html = renderPanel(qc)
    const text = unescapeHtml(html)
    expect(html).toContain('data-testid="turn-content-truncated"')
    expect(html).toContain(`showing ${returned.length} of 5000 characters`)
    expect(html).toContain('data-testid="turn-block-tool_result_content-truncated"')
    // Returned bytes still render; missing source is never pretended present.
    expect(text).toContain(returned)
  })

  it('routes a whole-turn drawer request to the full panel without clipped content', () => {
    const qc = createTestQueryClient()
    seedTurn(qc, buildTurnFixture())
    const html = renderHtml(
      <QueryClientProvider client={qc}>
        <SessionInspectorDrawer
          open={true}
          onClose={() => {}}
          title="Turn 2 · Full context"
          rawContent={{ turnIndex: TURN_INDEX, turn: { fresh: 250, cache_read: 200, cache_creation: 50 } } as unknown as Parameters<typeof SessionInspectorDrawer>[0]['rawContent']}
          contentRequest={{ sessionId: TURN_SESSION_ID, turnIndex: TURN_INDEX, full: true }}
        />
      </QueryClientProvider>
    )
    const text = unescapeHtml(html)
    expect(html).toContain('data-testid="turn-content-full"')
    expect(text).toContain(OWN_SYSTEM_PROMPT)
    expect(text).toContain(SHARED_PROMPT)
    // The clipped payload-row inspector stays out of the lossless view.
    expect(html).not.toContain('data-testid="turn-inspector"')
    expect(html).not.toContain('data-testid="drawer-content-loading"')
  })

  it('builds Copy Turn text covering every returned part plus labeled shared context', () => {
    const body = buildTurnFixture()
    const copy = buildWholeTurnCopyText(body, [SHARED_PROMPT])
    // Shared context first, clearly labeled — never silently omitted.
    expect(copy).toContain('=== SHARED SESSION CONTEXT ===')
    expect(copy.indexOf('=== SHARED SESSION CONTEXT ===')).toBeLessThan(copy.indexOf(OWN_SYSTEM_PROMPT))
    expect(copy).toContain(SHARED_PROMPT)
    // Every block and the orphaned unknown part survive byte-exact.
    expect(copy).toContain(OWN_SYSTEM_PROMPT)
    expect(copy).toContain('{"name": "zeta_tool"}')
    expect(copy).toContain('user asks about the failing gate')
    expect(copy).toContain('read_file produced 42 lines')
    expect(copy).toContain(MYSTERY_TEXT)
    expect(copy).toContain('=== MYSTERY BLOB ===')
    // No shared section when there is no shared text; empty turns copy empty.
    const noShared = buildWholeTurnCopyText(body, [])
    expect(noShared).not.toContain('SHARED SESSION CONTEXT')
    expect(noShared).toContain(MYSTERY_TEXT)
    expect(
      buildWholeTurnCopyText(
        { ...body, blocks: [], parts: [], assembledText: '' } as KyberTurnContentResult,
        [],
      ),
    ).toBe('')
  })

  it('identifies orphan parts by stable id without collapsing repeats', () => {
    const body = buildTurnFixture()
    const orphans = orphanTurnParts(body)
    expect(orphans.map((p) => p.part)).toEqual(['mystery_blob'])
    // A repeated identical unknown part pairs off per occurrence, not once.
    const dup = { ...body.parts[5], id: 'part-other-6' } as KyberTurnContentResult['parts'][number]
    const doubled = { ...body, parts: [...body.parts, dup] } as KyberTurnContentResult
    expect(orphanTurnParts(doubled).map((p) => p.id)).toEqual(['part-other-5', 'part-other-6'])
  })
})

describe('SessionInspectorDrawer: root-encoded JSON strings', () => {
  const ROOT_ENCODED = '"[{\\"type\\":\\"text\\",\\"text\\":\\"prompt\\"}]"'

  it('detects root-encoded JSON text', () => {
    expect(isRootEncodedJsonText(ROOT_ENCODED)).toBe(true)
    expect(isRootEncodedJsonText('  ' + ROOT_ENCODED + '  ')).toBe(true)
    expect(isRootEncodedJsonText('plain prose prompt')).toBe(false)
    expect(isRootEncodedJsonText('{"a": 1}')).toBe(false)
    expect(isRootEncodedJsonText('"just a string"')).toBe(false)
    expect(isRootEncodedJsonText('"123"')).toBe(false)
    expect(isRootEncodedJsonText('"{oops}"')).toBe(false)
    expect(isRootEncodedJsonText('')).toBe(false)
  })

  it('expands root-encoded strings in the nested formatter', () => {
    const pretty = prettyPrintJsonNested(ROOT_ENCODED)
    expect(pretty).not.toBeNull()
    // Readable structure, not one escaped line.
    expect(pretty!).toContain('"type"')
    expect(pretty!).toContain('prompt')
    expect(pretty!).not.toContain('\\"')
    // Expanding twice changes nothing.
    expect(prettyPrintJsonNested(pretty!)).toBe(pretty)
  })

  it('renders root-encoded text readable in Formatted with exact Raw and Copy', () => {
    const formatted = renderHtml(React.createElement(JsonStringView, { text: ROOT_ENCODED }))
    const formattedText = unescapeHtml(formatted)
    expect(formatted).toContain('data-testid="json-string-view"')
    expect(formattedText).toContain('"type"')
    expect(formattedText).toContain('prompt')

    const rawHtml = renderHtml(React.createElement(JsonStringView, { text: ROOT_ENCODED, defaultView: 'raw' }))
    // Raw keeps the exact original outer bytes, escapes and all.
    expect(unescapeHtml(rawHtml)).toContain(ROOT_ENCODED)
    expect(rawHtml).toContain('data-testid="json-raw"')

    // Copy writes the exact original outer text, not the expansion.
    const written: string[] = []
    const nav = (globalThis as { navigator?: unknown }).navigator
    const hadNavigator = nav !== undefined
    Object.defineProperty(globalThis, 'navigator', {
      value: { clipboard: { writeText: (s: string) => { written.push(s); return Promise.resolve() } } },
      configurable: true,
    })
    try {
      const rendered = (JsonStringView as (p: unknown) => unknown)({ text: ROOT_ENCODED, defaultView: 'raw' })
      const buttons: React.ReactElement[] = []
      const walk = (node: unknown): void => {
        if (node == null) return
        if (Array.isArray(node)) {
          node.forEach(walk)
          return
        }
        if (React.isValidElement(node)) {
          const props = node.props as Record<string, unknown>
          if (node.type === 'button' && props['children'] === 'Copy') buttons.push(node as React.ReactElement)
          walk(props['children'])
        }
      }
      walk(rendered)
      expect(buttons.length).toBeGreaterThan(0)
      ;(buttons[0]!.props as { onClick: () => void }).onClick()
      expect(written).toEqual([ROOT_ENCODED])
    } finally {
      if (hadNavigator) {
        Object.defineProperty(globalThis, 'navigator', { value: nav, configurable: true })
      } else {
        const g = globalThis as { navigator?: unknown }
        delete g.navigator
      }
    }
  })

  it('routes root-encoded strings to the structured view, prose stays folded', () => {
    const jsonHtml = renderHtml(React.createElement(ReadableContentText, { text: ROOT_ENCODED }))
    expect(jsonHtml).toContain('data-testid="json-string-view"')
    const proseHtml = renderHtml(React.createElement(ReadableContentText, { text: '"just a string"' }))
    expect(proseHtml).not.toContain('data-testid="json-string-view"')
  })
})

describe('SessionInspectorDrawer: lossless request builders', () => {
  function mockFetch(captured: string[]) {
    const origFetch = globalThis.fetch
    globalThis.fetch = ((input: unknown) => {
      captured.push(String(input))
      return Promise.resolve({ ok: true, json: () => Promise.resolve({}) })
    }) as typeof fetch
    return () => {
      globalThis.fetch = origFetch
    }
  }

  it('requests the explicit lossless turn path instead of a large-number budget', async () => {
    const urls: string[] = []
    const restore = mockFetch(urls)
    try {
      await fetchTurnContentFull('sess-x', 2)
      expect(urls).toHaveLength(1)
      expect(urls[0]).toBe('/api/kyber/session/sess-x/turn/2/content?full=true')
      expect(urls[0]).not.toContain('budget')
    } finally {
      restore()
    }
  })

  it('passes the explicit lossless flag for session content', async () => {
    const urls: string[] = []
    const restore = mockFetch(urls)
    try {
      await fetchKyberSessionContent('sess-x', { part: 'system_prompt', full: true })
      expect(urls).toHaveLength(1)
      expect(urls[0]).toContain('/api/kyber/session/sess-x/content')
      expect(urls[0]).toContain('part=system_prompt')
      expect(urls[0]).toContain('full=true')
    } finally {
      restore()
    }
  })
})
