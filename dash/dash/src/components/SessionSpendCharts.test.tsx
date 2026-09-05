import { describe, it, expect, vi, beforeEach } from 'vitest'
import * as React from 'react'

import {
  ContextCompositionChart,
  SessionSpendCharts,
  TurnSpendChart,
  contextSegments,
  extractNormalizedContextTurns,
  normalizeContextBuckets,
  type ContextCompositionData,
  type TurnSpendItem,
} from './SessionSpendCharts'

// Set up React 19 test hook dispatcher so components using hooks can be rendered in tests
let hookStates: unknown[] = []
let hookIndex = 0

function clearHooks() {
  hookStates = []
  hookIndex = 0
}

const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: {
      H?: unknown
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

beforeEach(() => {
  clearHooks()
})

// Helper to walk a React element tree and extract text
function renderText(element: React.ReactElement): string {
  const walk = (node: unknown): string => {
    if (node == null || typeof node === 'boolean') return ''
    if (typeof node === 'string' || typeof node === 'number') return String(node)
    if (Array.isArray(node)) return node.map(walk).join(' ')
    if (React.isValidElement(node)) {
      const el = node as React.ReactElement<{ children?: unknown }>
      const type = el.type as unknown
      if (typeof type === 'function') {
        hookIndex = 0
        const result = (type as (p: unknown) => unknown)(el.props)
        return walk(result)
      }
      return walk((el.props as { children?: unknown }).children)
    }
    return ''
  }
  return walk(element)
}

function normalizedRenderText(element: React.ReactElement): string {
  return renderText(element).replace(/\s+/g, ' ').trim()
}

// Helper to find all elements matching a predicate in the rendered tree
function findAllElements(
  node: unknown,
  predicate: (el: React.ReactElement<Record<string, unknown>>) => boolean,
): Array<React.ReactElement<Record<string, unknown>>> {
  const results: Array<React.ReactElement<Record<string, unknown>>> = []
  const walk = (curr: unknown) => {
    if (!curr || typeof curr !== 'object') return
    if (Array.isArray(curr)) {
      curr.forEach(walk)
      return
    }
    if (React.isValidElement(curr)) {
      const el = curr as React.ReactElement<Record<string, unknown>>
      if (predicate(el)) results.push(el)

      const type = el.type as unknown
      if (typeof type === 'function') {
        try {
          hookIndex = 0
          const rendered = (type as (p: unknown) => unknown)(el.props)
          walk(rendered)
        } catch {
          // ignore hook/state issues in plain walk
        }
      }
      if (el.props && 'children' in el.props) {
        walk(el.props.children)
      }
    }
  }
  walk(node)
  return results
}

describe('SessionSpendCharts: normalizeContextBuckets', () => {
  it('normalizes snake_case canonical keys', () => {
    const raw = {
      system_prompt: 1200,
      instruction_context: 800,
      tool_definitions: 3500,
      conversation_history: 4100,
      tool_result_content: 2200,
      residual: 300,
    }
    const result = normalizeContextBuckets(raw)
    expect(result.system_prompt).toBe(1200)
    expect(result.instruction_context).toBe(800)
    expect(result.tool_definitions).toBe(3500)
    expect(result.conversation_history).toBe(4100)
    expect(result.tool_result_content).toBe(2200)
    expect(result.residual).toBe(300)
  })

  it('normalizes human-readable agentdash keys into canonical buckets', () => {
    const raw = {
      'System prompt': 572,
      'built-in tools': 1800,
      'mcp: filesystem': 1200,
      'Instruction files / workspace context': 640,
      'Conversation history': 4500,
      'File contents via tool results': 3100,
    }
    const result = normalizeContextBuckets(raw, 12000)
    expect(result.system_prompt).toBe(572)
    expect(result.tool_definitions).toBe(3000) // 1800 + 1200
    expect(result.instruction_context).toBe(640)
    expect(result.conversation_history).toBe(4500)
    expect(result.tool_result_content).toBe(3100)
    // Residual calculated from reported input: 12000 - (572 + 3000 + 640 + 4500 + 3100) = 188
    expect(result.residual).toBe(188)
  })

  it('handles empty or null buckets gracefully', () => {
    const result = normalizeContextBuckets({})
    expect(result.system_prompt).toBe(0)
    expect(result.residual).toBe(0)
  })
})

describe('SessionSpendCharts: extractNormalizedContextTurns', () => {
  it('extracts turns when turns array is provided', () => {
    const data: ContextCompositionData = {
      measurable: true,
      contextLimit: 200000,
      turns: [
        {
          index: 1,
          buckets: { system_prompt: 1000, conversation_history: 2000 },
          reported_input: 3500,
        },
        {
          index: 2,
          buckets: { system_prompt: 1000, conversation_history: 5000 },
          reported_input: 6500,
        },
      ],
    }
    const rows = extractNormalizedContextTurns(data)
    expect(rows.length).toBe(2)
    expect(rows[0]!.turnIndex).toBe(1)
    expect(rows[0]!.buckets.system_prompt).toBe(1000)
    expect(rows[0]!.buckets.conversation_history).toBe(2000)
    expect(rows[0]!.buckets.residual).toBe(500) // 3500 - 3000
    expect(rows[1]!.turnIndex).toBe(2)
  })

  it('extracts first and last turns when first/last snapshot is provided', () => {
    const data: ContextCompositionData = {
      measurable: true,
      first: {
        turn: 1,
        buckets: { 'System prompt': 500, 'built-in tools': 1000 },
        reported_input: 1600,
      },
      last: {
        turn: 10,
        buckets: { 'System prompt': 500, 'built-in tools': 1000, 'Conversation history': 8000 },
        reported_input: 9800,
      },
    }
    const rows = extractNormalizedContextTurns(data)
    expect(rows.length).toBe(2)
    expect(rows[0]!.label).toContain('First turn')
    expect(rows[0]!.turnIndex).toBe(1)
    expect(rows[0]!.buckets.system_prompt).toBe(500)
    expect(rows[0]!.buckets.tool_definitions).toBe(1000)
    expect(rows[0]!.buckets.residual).toBe(100)

    expect(rows[1]!.label).toContain('Last turn')
    expect(rows[1]!.turnIndex).toBe(10)
    expect(rows[1]!.buckets.conversation_history).toBe(8000)
  })

  it('handles single-turn snapshot when last is "__single__"', () => {
    const data: ContextCompositionData = {
      measurable: true,
      first: {
        turn: 1,
        buckets: {
          system_prompt: 1200,
          instruction_context: 600,
          conversation_history: 2500,
        },
        reported_input: 4500,
      },
      last: '__single__',
    }
    const rows = extractNormalizedContextTurns(data)
    expect(rows.length).toBe(1)
    expect(rows[0]!.turnIndex).toBe(1)
    expect(rows[0]!.label).toContain('First turn (#1)')
    expect(rows[0]!.buckets.system_prompt).toBe(1200)
    expect(rows[0]!.buckets.instruction_context).toBe(600)
    expect(rows[0]!.buckets.conversation_history).toBe(2500)
    expect(rows[0]!.buckets.residual).toBe(200) // 4500 - 4300

    // Component rendering with single-turn snapshot
    const text = renderText(React.createElement(ContextCompositionChart, { context: data }))
    expect(text).toContain('First turn (#1)')
    expect(text).not.toContain('Last turn')
  })

  it('returns empty array when measurable is false', () => {
    const data: ContextCompositionData = {
      measurable: false,
      reason: 'no_message_structure',
    }
    const rows = extractNormalizedContextTurns(data)
    expect(rows).toEqual([])
  })
})

describe('TurnSpendChart', () => {
  const sampleTurns: TurnSpendItem[] = [
    {
      index: 1,
      fresh_input: 1000,
      cache_read: 0,
      cache_creation: 500,
      output: 250,
      input: 1500,
    },
    {
      index: 2,
      fresh_input: 200,
      cache_read: 1300,
      cache_creation: 0,
      output: 300,
      input: 1500,
    },
    {
      index: 3,
      fresh_input: 800, // jump > 25% from 200
      cache_read: 1500,
      cache_creation: null,
      output: 400,
      input: 2300,
    },
  ]

  it('renders empty fallback when turns is empty or undefined', () => {
    const textEmpty = renderText(React.createElement(TurnSpendChart, { turns: [] }))
    expect(textEmpty).toContain('No agent turns recorded')

    const textNull = renderText(React.createElement(TurnSpendChart, { turns: undefined }))
    expect(textNull).toContain('No agent turns recorded')
  })

  it('renders turn progression and legend labels', () => {
    const text = renderText(React.createElement(TurnSpendChart, { turns: sampleTurns }))
    expect(text).toContain('Token Spend per Turn')
    expect(text).toContain('Fresh input')
    expect(text).toContain('Cache-read input')
    expect(text).toContain('Output tokens')
    expect(text).toContain('Cumulative input')
  })

  it('detects and displays fresh input jumps > 25%', () => {
    const text = renderText(React.createElement(TurnSpendChart, { turns: sampleTurns }))
    expect(text).toContain('Fresh-input jump >25%')
    expect(text).toContain('#3')
  })

  it('handles turn click callback', () => {
    const onSelect = vi.fn()
    const element = React.createElement(TurnSpendChart, {
      turns: sampleTurns,
      onSelectTurn: onSelect,
    })
    const bars = findAllElements(
      element,
      (el) => typeof el.props['data-testid'] === 'string' && el.props['data-testid'].startsWith('turn-bar-'),
    )

    expect(bars.length).toBe(3)
    const firstBar = bars[0]!
    expect(firstBar.props['data-testid']).toBe('turn-bar-1')

    if (typeof firstBar.props.onClick === 'function') {
      firstBar.props.onClick({ stopPropagation: vi.fn() })
      expect(onSelect).toHaveBeenCalledWith(1)
    }
  })

  it('verifies reasoning tokens in turns and selection styling', () => {
    const onSelect = vi.fn()
    const turnsWithReasoning: TurnSpendItem[] = [
      {
        index: 1,
        fresh_input: 500,
        cache_read: 300,
        output: 400, // visible_output not provided: output should guard to Math.max(0, 400 - 150) = 250
        reasoning: 150,
        input: 800,
      },
      {
        index: 2,
        fresh_input: 200,
        cache_read: 800,
        visible_output: 100, // visible_output is precalculated
        output: 350,
        reasoning: 250,
        input: 1000,
      },
    ]

    const element = React.createElement(TurnSpendChart, {
      turns: turnsWithReasoning,
      selectedTurnIndex: 1,
      onSelectTurn: onSelect,
    })

    // Legend includes Reasoning output
    const text = renderText(element)
    expect(text).toContain('Reasoning output')

    // SVG minWidth styling for clean responsive scroll without w-full
    const svgs = findAllElements(element, (el) => el.type === 'svg')
    expect(svgs.length).toBe(1)
    const svgEl = svgs[0]!
    expect(svgEl.props.style).toBeDefined()
    expect((svgEl.props.style as React.CSSProperties)?.minWidth).toBeDefined()
    expect(String(svgEl.props.className)).not.toContain('w-full')

    // Interactive bars have role="button", tabIndex={0}, and onKeyDown
    const bars = findAllElements(
      element,
      (el) => typeof el.props['data-testid'] === 'string' && el.props['data-testid'].startsWith('turn-bar-'),
    )
    expect(bars.length).toBe(2)

    const bar1 = bars[0]!
    expect(bar1.props['role']).toBe('button')
    expect(bar1.props['tabIndex']).toBe(0)

    // Selection styling on bar 1
    const bar1Rects = findAllElements(bar1, (el) => el.type === 'rect')
    const bgRect1 = bar1Rects.find(
      (r) => r.props.fill === 'var(--color-interactive-secondary-hover)',
    )
    expect(bgRect1).toBeDefined()

    // Highlight border stroke for selected turn 1
    const highlightBorder = bar1Rects.find(
      (r) => r.props.stroke === 'var(--color-foreground)',
    )
    expect(highlightBorder).toBeDefined()

    // Bar 2 unselected styling
    const bar2 = bars[1]!
    const bar2Rects = findAllElements(bar2, (el) => el.type === 'rect')
    const bgRect2 = bar2Rects.find((r) => r.props.fill === 'transparent')
    expect(bgRect2).toBeDefined()
    const unselectedBorder = bar2Rects.find((r) => r.props.stroke === 'var(--color-foreground)')
    expect(unselectedBorder).toBeUndefined()

    // Keyboard handlers (Enter and Space) on bar 2
    if (typeof bar2.props.onKeyDown === 'function') {
      const preventDefaultEnter = vi.fn()
      bar2.props.onKeyDown({ key: 'Enter', preventDefault: preventDefaultEnter })
      expect(preventDefaultEnter).toHaveBeenCalled()
      expect(onSelect).toHaveBeenCalledWith(2)

      const preventDefaultSpace = vi.fn()
      bar2.props.onKeyDown({ key: ' ', preventDefault: preventDefaultSpace })
      expect(preventDefaultSpace).toHaveBeenCalled()
      expect(onSelect).toHaveBeenCalledWith(2)
    }
  })
})

describe('ContextCompositionChart', () => {
  const sampleContext: ContextCompositionData = {
    measurable: true,
    contextLimit: 128000,
    turns: [
      {
        index: 1,
        buckets: {
          system_prompt: 1500,
          instruction_context: 500,
          tool_definitions: 2000,
          conversation_history: 1000,
          tool_result_content: 800,
          residual: 200,
        },
        reported_input: 6000,
      },
      {
        index: 2,
        buckets: {
          system_prompt: 1500,
          instruction_context: 500,
          tool_definitions: 2000,
          conversation_history: 4000,
          tool_result_content: 1500,
          residual: 500,
        },
        reported_input: 10000,
      },
    ],
  }

  it('renders not measurable fallback state when measurable is false', () => {
    const notMeasurableData: ContextCompositionData = {
      measurable: false,
      reason: 'no_message_structure',
    }
    const text = renderText(React.createElement(ContextCompositionChart, { context: notMeasurableData }))
    expect(text).toContain('Not Measurable')
    expect(text).toContain('Context Composition Unavailable')
  })

  it('renders empty fallback when context is null or empty', () => {
    const text = renderText(React.createElement(ContextCompositionChart, { context: null }))
    expect(text).toContain('No context data available')
  })

  it('renders semantic bucket legend items', () => {
    const text = renderText(React.createElement(ContextCompositionChart, { context: sampleContext }))
    expect(text).toContain('Context Composition')
    expect(text).toContain('System prompt')
    expect(text).toContain('Instruction context')
    expect(text).toContain('Tool definitions')
    expect(text).toContain('Conversation history')
    expect(text).toContain('Tool results')
    expect(text).toContain('Residual')
  })

  it('handles segment click calling onSelectTurn with turn index and bucket key', () => {
    const onSelect = vi.fn()
    const element = React.createElement(ContextCompositionChart, {
      context: sampleContext,
      onSelectTurn: onSelect,
    })

    const segments = findAllElements(
      element,
      (el) =>
        typeof el.props['data-testid'] === 'string' &&
        el.props['data-testid'].startsWith('context-segment-'),
    )

    expect(segments.length).toBeGreaterThan(0)
    const sysSegment = segments.find((s) => s.props['data-testid'] === 'context-segment-system_prompt')
    expect(sysSegment).toBeDefined()

    if (sysSegment && typeof sysSegment.props.onClick === 'function') {
      sysSegment.props.onClick({ stopPropagation: vi.fn() })
      expect(onSelect).toHaveBeenCalledWith(1, 'system_prompt')
    }
  })

  it('covers heatmap view mode toggle and rendering', () => {
    clearHooks()
    // Initial render in bars mode
    hookIndex = 0
    const initialTree = (ContextCompositionChart as (p: any) => React.ReactElement)({
      context: sampleContext,
    })
    const barViews = findAllElements(
      initialTree,
      (el) => el.props['data-testid'] === 'context-stacked-bars',
    )
    expect(barViews.length).toBe(1)

    // Find tab button for heatmap
    const heatmapTabs = findAllElements(
      initialTree,
      (el) => el.props['data-testid'] === 'tab-heatmap',
    )
    expect(heatmapTabs.length).toBe(1)

    // Click heatmap tab
    const tabBtn = heatmapTabs[0]!
    if (typeof tabBtn.props.onClick === 'function') {
      tabBtn.props.onClick({ stopPropagation: vi.fn() } as any)
    }

    // Re-render after state change
    hookIndex = 0
    const heatmapTree = (ContextCompositionChart as (p: any) => React.ReactElement)({
      context: sampleContext,
    })
    const heatmapViews = findAllElements(
      heatmapTree,
      (el) => el.props['data-testid'] === 'context-heatmap',
    )
    expect(heatmapViews.length).toBe(1)

    const text = renderText(heatmapTree)
    expect(text).toContain('Total Input')
    expect(text).toContain('Pressure')
    expect(text).toContain('Turn')

    // Toggle back to stacked bars
    const barTabs = findAllElements(
      heatmapTree,
      (el) => el.props['data-testid'] === 'tab-stacked-bars',
    )
    expect(barTabs.length).toBe(1)
    if (typeof barTabs[0]!.props.onClick === 'function') {
      barTabs[0]!.props.onClick({ stopPropagation: vi.fn() } as any)
    }

    hookIndex = 0
    const toggledBackTree = (ContextCompositionChart as (p: any) => React.ReactElement)({
      context: sampleContext,
    })
    const backToBars = findAllElements(
      toggledBackTree,
      (el) => el.props['data-testid'] === 'context-stacked-bars',
    )
    expect(backToBars.length).toBe(1)
  })

  it('preserves selectedTurnIndex on legend bucket click', () => {
    const onSelect = vi.fn()
    const element = React.createElement(ContextCompositionChart, {
      context: sampleContext,
      selectedTurnIndex: 2,
      onSelectTurn: onSelect,
    })

    const legendItems = findAllElements(
      element,
      (el) =>
        el.props['role'] === 'button' &&
        typeof el.props.children === 'object' &&
        el.props.tabIndex === 0,
    )
    expect(legendItems.length).toBeGreaterThan(0)

    const toolDefLegend = legendItems.find((item) => renderText(item).includes('Tool definitions'))
    expect(toolDefLegend).toBeDefined()

    if (toolDefLegend && typeof toolDefLegend.props.onClick === 'function') {
      toolDefLegend.props.onClick({ stopPropagation: vi.fn() } as any)
      expect(onSelect).toHaveBeenCalledWith(2, 'tool_definitions')
    }
  })

  it('supports keyboard navigation (Enter/Space) on segments', () => {
    const onSelect = vi.fn()
    const element = React.createElement(ContextCompositionChart, {
      context: sampleContext,
      onSelectTurn: onSelect,
    })

    const sysSegment = findAllElements(
      element,
      (el) => el.props['data-testid'] === 'context-segment-system_prompt',
    )[0]
    expect(sysSegment).toBeDefined()
    expect(sysSegment.props.tabIndex).toBe(0)
    expect(sysSegment.props.role).toBe('button')

    if (typeof sysSegment.props.onKeyDown === 'function') {
      const preventDefault = vi.fn()
      const stopPropagation = vi.fn()
      sysSegment.props.onKeyDown({ key: 'Enter', preventDefault, stopPropagation })
      expect(preventDefault).toHaveBeenCalled()
      expect(stopPropagation).toHaveBeenCalled()
      expect(onSelect).toHaveBeenCalledWith(1, 'system_prompt')
    }
  })

  it('uses high contrast drop-shadow text styling on context segments', () => {
    const element = React.createElement(ContextCompositionChart, { context: sampleContext })
    const segments = findAllElements(
      element,
      (el) =>
        typeof el.props['data-testid'] === 'string' &&
        el.props['data-testid'].startsWith('context-segment-'),
    )
    expect(segments.length).toBeGreaterThan(0)
    const firstSegment = segments[0]!
    expect(String(firstSegment.props.className)).toContain('text-white')
    expect(String(firstSegment.props.className)).toContain('drop-shadow-[0_1px_1px_rgba(0,0,0,0.8)]')
  })

  /** True when the rendered tree contains an element with this test id. */
  const hasTestId = (element: React.ReactElement, id: string) =>
    findAllElements(element, (el) => el.props['data-testid'] === id).length > 0

  it('says counts are derived, and names the tokenizer, when they are', () => {
    const element = React.createElement(ContextCompositionChart, {
      context: { ...sampleContext, derivedCounts: true, derivedModel: 'o200k_base' },
    })

    expect(hasTestId(element, 'context-caveat-derived')).toBe(true)
    expect(hasTestId(element, 'context-caveat-measured')).toBe(false)
  })

  it('does not warn about a proxy tokenizer when the harness reported the counts', () => {
    // The previous note fired on `derivedCounts` alone, so it appeared even
    // for harnesses reporting exact per-bucket totals, where it is false.
    const element = React.createElement(ContextCompositionChart, {
      context: { ...sampleContext, derivedCounts: false },
    })

    expect(hasTestId(element, 'context-caveat-measured')).toBe(true)
    expect(hasTestId(element, 'context-caveat-derived')).toBe(false)
  })

  it('uses the ASAD derived-count caveat and escalates only above a 15% residual', () => {
    const atThreshold = React.createElement(ContextCompositionChart, {
      context: {
        measurable: true,
        derivedCounts: true,
        derivedModel: 'o200k_base',
        turns: [
          {
            index: 1,
            reported_input: 1_000,
            buckets: { system_prompt: 850, residual: 150 },
          },
        ],
      },
    })
    const aboveThreshold = React.createElement(ContextCompositionChart, {
      context: {
        measurable: true,
        derivedCounts: true,
        derivedModel: 'o200k_base',
        turns: [
          {
            index: 1,
            reported_input: 1_000,
            buckets: { system_prompt: 840, residual: 160 },
          },
        ],
      },
    })

    const atThresholdText = normalizedRenderText(atThreshold)
    expect(atThresholdText).toMatch(
      /Bucket sizes are derived by tokenizing content with o200k_base\s*, a proxy for models that do not publish their tokenizer, so each is a lower bound\./,
    )
    expect(atThresholdText).toMatch(
      /The residual of 15\.0\s*% is the gap between the buckets and the model's own reported input\./,
    )
    expect(atThresholdText).not.toContain('Treat this session\'s bucket sizes as a lower bound.')

    const aboveThresholdText = normalizedRenderText(aboveThreshold)
    expect(aboveThresholdText).toMatch(
      /The residual of 16\.0\s*% is the gap between the buckets and the model's own reported input\./,
    )
    expect(aboveThresholdText).toContain('Treat this session\'s bucket sizes as a lower bound.')
  })

  it('uses the ASAD harness-reported caveat without suppressing the residual', () => {
    const element = React.createElement(ContextCompositionChart, {
      context: {
        measurable: true,
        derivedCounts: false,
        turns: [
          {
            index: 1,
            reported_input: 1_000,
            buckets: { system_prompt: 840, residual: 160 },
          },
        ],
      },
    })

    const text = normalizedRenderText(element)
    expect(text).toMatch(/Bucket sizes are reported by the harness\s*, not estimated\./)
    expect(text).toMatch(
      /The residual of 16\.0\s*% is input the harness did not attribute to any bucket — chat framing and role delimiters — rather than tokenizer drift\./,
    )
    expect(text).not.toContain('Treat this session\'s bucket sizes as a lower bound.')
  })

  it('renders the B3 unavailable-bucket reason verbatim', () => {
    const reason = 'Claude Code session files do not store the runtime system prompt.'
    const text = normalizedRenderText(
      React.createElement(ContextCompositionChart, {
        context: {
          measurable: false,
          reason,
        },
      }),
    )

    expect(text).toContain(reason)
  })

  it('states plainly when tool definitions carry no server attribution', () => {
    const element = React.createElement(ContextCompositionChart, { context: sampleContext })

    expect(hasTestId(element, 'context-caveat-no-servers')).toBe(true)
  })
})

describe('SessionSpendCharts (composite)', () => {
  it('renders both turn spend and context composition panels', () => {
    const turns: TurnSpendItem[] = [
      { index: 1, fresh_input: 500, cache_read: 0, output: 100, input: 500 },
    ]
    const context: ContextCompositionData = {
      measurable: true,
      turns: [
        { index: 1, buckets: { system_prompt: 400, residual: 100 }, reported_input: 500 },
      ],
    }
    const text = renderText(
      React.createElement(SessionSpendCharts, { turns, context }),
    )
    expect(text).toContain('Token Spend per Turn')
    expect(text).toContain('Context Composition')
  })
})

describe('contextSegments — per-MCP-server bands', () => {
  const row = {
    buckets: {
      system_prompt: 5800,
      instruction_context: 3411,
      tool_definitions: 4200,
      conversation_history: 2000,
      tool_result_content: 0,
      residual: 500,
    },
    servers: { context7: 2000, codegraph: 1200 },
    builtinToolTokens: 1000,
  }

  it('splits tool definitions into one band per server, plus built-ins', () => {
    // A single "Tool definitions: 4.2k" bar says schemas are expensive. The
    // per-server split says which server to disconnect, which is the whole
    // reason the ground-truth server field is collected.
    const ids = contextSegments(row).map((s) => s.id)

    expect(ids).toContain('mcp:context7')
    expect(ids).toContain('mcp:codegraph')
    expect(ids).toContain('tool_definitions:builtin')
    expect(ids).not.toContain('tool_definitions')
  })

  it('orders servers by descending cost and gives each a distinct colour', () => {
    const servers = contextSegments(row).filter((s) => s.server !== undefined)

    expect(servers.map((s) => s.server)).toEqual(['context7', 'codegraph'])
    expect(servers[0]!.color).not.toBe(servers[1]!.color)
  })

  it('preserves the bucket total across the split', () => {
    const toolSegments = contextSegments(row).filter((s) => s.key === 'tool_definitions')
    const summed = toolSegments.reduce((sum, s) => sum + s.tokens, 0)

    expect(summed).toBe(row.buckets.tool_definitions)
  })

  it('every server band still reports against its bucket, so inspection works', () => {
    const context7 = contextSegments(row).find((s) => s.id === 'mcp:context7')

    expect(context7?.key).toBe('tool_definitions')
  })

  it('falls back to a single band when no server attribution exists', () => {
    // Antigravity sends tool names only. Those tokens are built-in as far as
    // this system is concerned; a server is never invented for them.
    const ids = contextSegments({ buckets: row.buckets }).map((s) => s.id)

    expect(ids).toContain('tool_definitions')
    expect(ids.some((id) => id.startsWith('mcp:'))).toBe(false)
  })

  it('omits empty buckets but keeps the residual', () => {
    const ids = contextSegments(row).map((s) => s.id)

    expect(ids).not.toContain('tool_result_content')
    expect(ids).toContain('residual')
  })
})

describe('extractNormalizedContextTurns — server attribution', () => {
  it('carries toolDefinitionsByServer through from the analysis', () => {
    const rows = extractNormalizedContextTurns({
      measurable: true,
      turns: [
        {
          index: 1,
          reported_input: 20000,
          buckets: { system_prompt: 5800, tool_definitions: 4200 },
          toolDefinitionsByServer: { context7: 2000, codegraph: 1200 },
          builtinToolDefinitionTokens: 1000,
        } as never,
      ],
    })

    expect(rows[0]?.servers).toEqual({ context7: 2000, codegraph: 1200 })
    expect(rows[0]?.builtinToolTokens).toBe(1000)
  })

  it('accepts a Map, which is what the analysis layer produces natively', () => {
    const rows = extractNormalizedContextTurns({
      measurable: true,
      turns: [
        {
          index: 1,
          reported_input: 10000,
          buckets: { tool_definitions: 3200 },
          toolDefinitionsByServer: new Map([['context7', 2000]]),
        } as never,
      ],
    })

    expect(rows[0]?.servers).toEqual({ context7: 2000 })
  })
})
