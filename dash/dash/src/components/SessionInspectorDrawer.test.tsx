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
} from './SessionInspectorDrawer'

import { renderToStaticMarkup } from 'react-dom/server'
import type { KyberSessionContentResult } from '../lib/kyberApi'

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
    if (props && props.children) {
      const found = findElementByTestId(props.children, testId)
      if (found) return found
    }
  }
  return null
}

function findDetailsNodes(element: React.ReactElement | null | undefined): any[] {
  const matches: any[] = []
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
    expect(detailsEl.props.className).toContain('group border border-border rounded my-1 bg-card/60')

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
  onEffect?: (effect: () => void | (() => void), deps?: any[]) => void
) {
  const internals = (React as any).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE
  const prevDispatcher = internals?.H
  try {
    if (internals) {
      internals.H = {
        useEffect: (effect: any, deps: any) => {
          onEffect?.(effect, deps)
        },
        useState: (initial: any) => [typeof initial === 'function' ? initial() : initial, () => {}],
        useMemo: (factory: any) => factory(),
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
    const listeners: Record<string, (e: any) => void> = {}
    const mockWindow = {
      addEventListener: vi.fn((event: string, handler: any) => {
        listeners[event] = handler
      }),
      removeEventListener: vi.fn((event: string, handler: any) => {
        if (listeners[event] === handler) {
          delete listeners[event]
        }
      }),
    }

    try {
      globalThis.window = mockWindow as any
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
        delete (globalThis as any).window
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
      globalThis.window = mockWindow as any
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
        delete (globalThis as any).window
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
    expect(backdrop?.props.onClick).toBeTypeOf('function')

    backdrop?.props.onClick()
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
    expect(closeButton?.props.onClick).toBeTypeOf('function')

    closeButton?.props.onClick()
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
    expect(panel?.props.onClick).toBeTypeOf('function')

    const stopPropagation = vi.fn()
    panel?.props.onClick({ stopPropagation } as any)
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
