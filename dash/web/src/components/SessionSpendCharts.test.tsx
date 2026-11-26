import { describe, it, expect, vi, beforeEach } from 'vitest'
import * as React from 'react'
import { readFileSync } from 'node:fs'

import {
  ContextCompositionChart,
  MCP_SERVER_COLORS,
  SessionSpendCharts,
  TurnSpendChart,
  contextSegments,
  extractNormalizedContextTurns,
  normalizeContextBuckets,
  type ContextCompositionData,
  type TurnSpendItem,
} from './SessionSpendCharts.js'

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

  // A turn with no measured counter: every token field is null, as the
  // /charts section serves it for a harness that reports no per-turn tokens.
  const unmeasuredTurn = (index: number): TurnSpendItem =>
    ({
      index,
      input: null,
      output: null,
      cache_read: null,
      cache_creation: null,
      fresh: null,
      fresh_input: null,
      reasoning: null,
      visible_output: null,
    }) as unknown as TurnSpendItem

  const turnBars = (element: React.ReactElement) =>
    findAllElements(
      element,
      (el) => typeof el.props['data-testid'] === 'string' && el.props['data-testid'].startsWith('turn-bar-'),
    )

  it('renders the not-measurable state, with no bars, chips or zero figure, when no turn carries a counter', () => {
    const turns = [unmeasuredTurn(1), unmeasuredTurn(2)]
    const element = React.createElement(TurnSpendChart, { turns })
    const text = normalizedRenderText(element)

    expect(text).toContain('Token Spend per Turn')
    expect(text).toContain('2 turns')
    expect(text).toContain(
      'This harness does not report token counts per turn. Session totals, where reported, are in the overview.',
    )
    expect(text).not.toContain('Fresh:')
    expect(text).not.toContain('Cache Hit:')
    expect(text).not.toContain('Output:')
    expect(text).not.toMatch(/\b0\b/)
    expect(findAllElements(element, (el) => el.props['data-testid'] === 'turn-spend-not-measurable')).toHaveLength(1)
    expect(findAllElements(element, (el) => el.props['data-testid'] === 'turn-spend-chart')).toHaveLength(0)
    expect(turnBars(element)).toHaveLength(0)
  })

  it('draws every turn, leaves an unmeasured turn bare, and sums chips over measured turns only', () => {
    const element = React.createElement(TurnSpendChart, {
      turns: [sampleTurns[0]!, unmeasuredTurn(2), sampleTurns[2]!],
    })
    const bars = turnBars(element)

    expect(bars.map((b) => b.props['data-testid'])).toEqual(['turn-bar-1', 'turn-bar-2', 'turn-bar-3'])
    expect(bars[0]!.props['data-measured']).toBe('true')
    expect(bars[1]!.props['data-measured']).toBe('false')
    expect(bars[1]!.props['aria-label']).toBe('Turn 2, tokens not measured')
    expect(bars[2]!.props['data-measured']).toBe('true')

    // Segments are the only <title> children of a bar: none for the unmeasured turn.
    expect(findAllElements(bars[1]!, (el) => el.type === 'title')).toHaveLength(0)
    expect(findAllElements(bars[0]!, (el) => el.type === 'title').length).toBeGreaterThan(0)

    // Chips equal the figures for measured turns 1 and 3 alone:
    // fresh 1000 + 800 = 1.8K; cache read 1500 of 3800 input = 39.5%; output 250 + 400 = 650.
    const text = normalizedRenderText(element)
    expect(text).toContain('Fresh: 1.8K')
    expect(text).toMatch(/Cache Hit: 39\.5 ?%/)
    expect(text).toContain('Output: 650')
  })

  it('keeps a turn whose only counter is a measured zero as measured', () => {
    const element = React.createElement(TurnSpendChart, {
      turns: [{ index: 1, output: 0 } as TurnSpendItem],
    })
    const bars = turnBars(element)

    expect(bars).toHaveLength(1)
    expect(bars[0]!.props['data-measured']).toBe('true')
    expect(findAllElements(element, (el) => el.props['data-testid'] === 'turn-spend-not-measurable')).toHaveLength(0)
  })

  it('marks every fully measured turn as measured and keeps the chart shell', () => {
    const element = React.createElement(TurnSpendChart, { turns: sampleTurns })
    const bars = turnBars(element)

    expect(bars).toHaveLength(3)
    for (const bar of bars) expect(bar.props['data-measured']).toBe('true')
    expect(findAllElements(element, (el) => el.props['data-testid'] === 'turn-spend-chart')).toHaveLength(1)
  })

  it('words a fresh-input jump against the immediately preceding turn as "vs previous turn"', () => {
    // Turn 3 (fresh 800) follows turn 2 (fresh 200): +300%, adjacent.
    const element = React.createElement(TurnSpendChart, { turns: sampleTurns })
    const text = normalizedRenderText(element)

    expect(text).toContain('#3 (+300% vs previous turn)')
    expect(text).not.toContain('vs previous measured turn')
  })

  it('names the compared turn when an unmeasured turn sits between the jump and its baseline', () => {
    // Turn 1 is measured (fresh 200); turn 2 has no counter; turn 3 (fresh 800)
    // is compared against turn 1, the previous measured turn: +300%.
    const turns: TurnSpendItem[] = [
      { index: 1, fresh_input: 200, cache_read: 0, output: 50, input: 200 },
      unmeasuredTurn(2),
      { index: 3, fresh_input: 800, cache_read: 0, output: 50, input: 800 },
    ]
    const element = React.createElement(TurnSpendChart, { turns })
    const text = normalizedRenderText(element)

    expect(text).toContain('#3 (+300% vs previous measured turn (turn 1))')
    expect(text).not.toContain('#3 (+300% vs previous turn)')
  })

  it('labels a server-provided jump "vs earlier turn", since the API does not name its baseline turn', () => {
    // The server's fresh_jump_pct is not measured against a turn the API names,
    // so the label must not claim "previous turn" or name one.
    const turns: TurnSpendItem[] = [
      { index: 1, fresh_input: 200, cache_read: 0, output: 50, input: 200 },
      { index: 2, fresh_input: 800, cache_read: 0, output: 50, input: 800, fresh_jump_pct: 300 },
    ]
    const element = React.createElement(TurnSpendChart, { turns })
    const text = normalizedRenderText(element)

    expect(text).toContain('#2 (+300% vs earlier turn)')
    expect(text).not.toContain('vs previous turn')
    expect(text).not.toContain('vs previous measured turn')
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
      // Row label is the 1-based `Turn #1`; transport is 0-based (issue #184).
      expect(onSelect).toHaveBeenCalledWith(0, 'system_prompt')
    }
  })

  it('covers heatmap view mode toggle and rendering', () => {
    clearHooks()
    // Initial render in bars mode
    hookIndex = 0
    const initialTree = (ContextCompositionChart as (p: Record<string, unknown>) => React.ReactElement)({
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
      tabBtn.props.onClick({ stopPropagation: vi.fn() } as { stopPropagation: () => void })
    }

    // Re-render after state change
    hookIndex = 0
    const heatmapTree = (ContextCompositionChart as (p: Record<string, unknown>) => React.ReactElement)({
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
      barTabs[0]!.props.onClick({ stopPropagation: vi.fn() } as { stopPropagation: () => void })
    }

    hookIndex = 0
    const toggledBackTree = (ContextCompositionChart as (p: Record<string, unknown>) => React.ReactElement)({
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
      // 0-based transport index for the second turn (issue #184).
      selectedTurnIndex: 1,
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
      toolDefLegend.props.onClick({ stopPropagation: vi.fn() } as { stopPropagation: () => void })
      expect(onSelect).toHaveBeenCalledWith(1, 'tool_definitions')
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
      expect(onSelect).toHaveBeenCalledWith(0, 'system_prompt')
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

  it('names the model a stand-in tokenizer is not the own of', () => {
    const element = React.createElement(ContextCompositionChart, {
      context: {
        ...sampleContext,
        derivedCounts: true,
        tokenizer: 'o200k_base',
        tokenizers: [{ name: 'o200k_base', isModelOwn: false }],
        tokenizerIsModelOwn: false,
      },
      turns: [{ index: 1, model: 'claude-opus-4' }],
    })

    const text = normalizedRenderText(element)
    expect(text).toContain('o200k_base, standard tokenizer — not Claude')
    expect(text).not.toMatch(/estimated/i)
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
      /Bucket sizes are derived by tokenizing content with o200k_base\s*, standard tokenizer — not the model's own\s*,\s*so each is a lower bound\./,
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
    expect(text).toMatch(/Bucket sizes are reported by the harness\s*, measured directly\./)
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

  it('emits a 0-based transport index when a 1-based row label is clicked (issue #184)', () => {
    const onSelect = vi.fn()
    const element = React.createElement(ContextCompositionChart, {
      context: sampleContext,
      onSelectTurn: onSelect,
    })

    const rowLabel = findAllElements(
      element,
      (el) => el.props['role'] === 'button' && renderText(el) === 'Turn #2',
    )[0]
    expect(rowLabel).toBeDefined()
    expect(renderText(element)).toContain('Turn #2')

    if (rowLabel && typeof rowLabel.props.onClick === 'function') {
      rowLabel.props.onClick()
      expect(onSelect).toHaveBeenCalledWith(1)
    }
  })

  it('defaults a legend click to the first row as a 0-based index (issue #184)', () => {
    const onSelect = vi.fn()
    const element = React.createElement(ContextCompositionChart, {
      context: sampleContext,
      onSelectTurn: onSelect,
    })

    const legendItems = findAllElements(
      element,
      (el) =>
        el.props['role'] === 'button' &&
        typeof el.props.children === 'object' &&
        el.props.tabIndex === 0,
    )
    const toolDefLegend = legendItems.find((item) => renderText(item).includes('Tool definitions'))
    expect(toolDefLegend).toBeDefined()

    if (toolDefLegend && typeof toolDefLegend.props.onClick === 'function') {
      toolDefLegend.props.onClick({ stopPropagation: vi.fn() } as { stopPropagation: () => void })
      expect(onSelect).toHaveBeenCalledWith(0, 'tool_definitions')
    }
  })

  it('highlights the row matching a 0-based selectedTurnIndex (issue #184)', () => {
    const element = React.createElement(ContextCompositionChart, {
      context: sampleContext,
      selectedTurnIndex: 1,
      onSelectTurn: vi.fn(),
    })

    const selected = findAllElements(
      element,
      (el) => el.props['data-testid'] === 'context-turn-2',
    )[0]
    const unselected = findAllElements(
      element,
      (el) => el.props['data-testid'] === 'context-turn-1',
    )[0]
    expect(selected).toBeDefined()
    expect(String(selected!.props.className)).toContain('ring-1')
    expect(String(unselected!.props.className)).not.toContain('ring-1')
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

describe('SessionSpendCharts: fed by the /charts section', () => {
  // The shapes `GET /api/kyber/session/:id/charts` answers: the detail
  // payload's turns and context, without its other reads.
  const sectionTurns: TurnSpendItem[] = [
    { index: 0, fresh_input: 500, cache_read: 0, output: 100, input: 500 },
    { index: 1, fresh_input: 200, cache_read: 300, output: 150, input: 650 },
  ]
  const sectionContext: ContextCompositionData = {
    measurable: true,
    turns: [{ index: 1, buckets: { system_prompt: 400, residual: 100 }, reported_input: 500 }],
  }

  it('renders both charts from the section data the /charts route serves', () => {
    const text = renderText(
      React.createElement(SessionSpendCharts, { turns: sectionTurns, context: sectionContext }),
    )
    expect(text).toContain('Token Spend per Turn')
    expect(text).toContain('Context Composition')
  })

  it('renders its empty states while the section data has not loaded', () => {
    const text = renderText(React.createElement(SessionSpendCharts, {}))
    expect(text).toContain('No agent turns recorded')
    expect(text).toContain('No context data available')
  })

  it('consumes the section data only: no monolithic session payload prop remains', () => {
    // The charts once fell back to `session.turns` / `session.context` off the
    // whole-session payload. The section's own route answers their data now, so
    // the payload shape is out of the module entirely — read as text because
    // the rule is about the module's inputs, not about any one render.
    const source = readFileSync(new URL('./SessionSpendCharts.tsx', import.meta.url), 'utf8')
    expect(source).not.toContain('AgentSessionPayload')
    expect(source).not.toContain('session?.turns')
    expect(source).not.toContain('session?.context')
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

  it('uses a whole-session server colour map so legend and row agree (dbId 4704550866)', () => {
    // observedServers is ordinal (alphabetical); row order is token-descending.
    // Without a shared map, alpha is legend [0] but drawn [1].
    const observedServers = ['alpha', 'beta']
    const serverColors = Object.fromEntries(
      observedServers.map((server, index) => [server, MCP_SERVER_COLORS[index % MCP_SERVER_COLORS.length]!]),
    )
    const row = {
      buckets: {
        system_prompt: 0,
        instruction_context: 0,
        tool_definitions: 6000,
        conversation_history: 0,
        tool_result_content: 0,
        residual: 0,
      },
      servers: { beta: 5000, alpha: 1000 },
    }

    const segments = contextSegments(row, serverColors)
    const alpha = segments.find((s) => s.id === 'mcp:alpha')
    const beta = segments.find((s) => s.id === 'mcp:beta')

    expect(alpha?.color).toBe(MCP_SERVER_COLORS[0])
    expect(beta?.color).toBe(MCP_SERVER_COLORS[1])
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

// Issue #187: a turn whose measured input dwarfs its buckets must show the
// gap as residual — never "Residual: 0.0%" — and a composition with no
// measured input basis must say so instead of printing a zero.
describe('SessionSpendCharts residual honesty (issue #187)', () => {
  it('attributes the engine residual instead of reporting 0.0%', () => {
    const data: ContextCompositionData = {
      measurable: true,
      contextLimit: 200000,
      turns: [
        {
          index: 1,
          buckets: { system_prompt: 124 },
          reported_input: 66600,
          residual: { tokens: 66476, attribution: 'tokenizer_drift' },
        },
      ],
    }
    const rows = extractNormalizedContextTurns(data)
    expect(rows).toHaveLength(1)
    expect(rows[0]!.buckets.residual).toBe(66476)
    expect(rows[0]!.hasMeasuredInput).toBe(true)

    const text = normalizedRenderText(React.createElement(ContextCompositionChart, { context: data }))
    expect(text).toContain('Residual: 99.8')
    expect(text).not.toContain('Residual: 0.0')
  })

  it('renders not measurable instead of 0.0% with no measured input basis', () => {
    const data: ContextCompositionData = {
      measurable: true,
      contextLimit: 200000,
      turns: [{ index: 1, buckets: { system_prompt: 124 } }],
    }
    const rows = extractNormalizedContextTurns(data)
    expect(rows).toHaveLength(1)
    expect(rows[0]!.hasMeasuredInput).toBe(false)

    const text = normalizedRenderText(React.createElement(ContextCompositionChart, { context: data }))
    expect(text).toContain('not measurable')
    expect(text).not.toContain('0.0%')
  })

  it('keeps a fully-bucketed turn at a measured 0.0%', () => {
    const data: ContextCompositionData = {
      measurable: true,
      contextLimit: 200000,
      turns: [
        {
          index: 1,
          buckets: { system_prompt: 124 },
          reported_input: 124,
          residual: { tokens: 0, attribution: 'tokenizer_drift' },
        },
      ],
    }
    const rows = extractNormalizedContextTurns(data)
    expect(rows[0]!.buckets.residual).toBe(0)
    expect(rows[0]!.hasMeasuredInput).toBe(true)

    const text = normalizedRenderText(React.createElement(ContextCompositionChart, { context: data }))
    expect(text).toContain('Residual: 0.0')
    expect(text).not.toContain('not measurable')
  })
})

describe('ContextCompositionChart whole-session scaling, legend attribution, and Claude residual copy', () => {
  it('builds legendSegments and hasServerAttribution from observedBuckets and observedServers when present', () => {
    const context: ContextCompositionData = {
      measurable: true,
      contextLimit: 200000,
      observedBuckets: ['system_prompt', 'instruction_context', 'tool_definitions', 'conversation_history'],
      observedServers: ['context7', 'codegraph'],
      residualTotal: 500,
      measuredInputTotal: 5000,
      turns: [
        {
          index: 1,
          reported_input: 5000,
          buckets: { conversation_history: 4500, residual: 500 },
        },
      ],
    }

    const element = React.createElement(ContextCompositionChart, { context })
    const legendIds = findAllElements(
      element,
      (el) => typeof el.props['data-testid'] === 'string' && el.props['data-testid'].startsWith('context-legend-'),
    ).map((el) => el.props['data-testid'])

    expect(legendIds).toContain('context-legend-system_prompt')
    expect(legendIds).toContain('context-legend-instruction_context')
    expect(legendIds).toContain('context-legend-mcp:context7')
    expect(legendIds).toContain('context-legend-mcp:codegraph')
    expect(legendIds).toContain('context-legend-conversation_history')
    expect(legendIds).toContain('context-legend-residual')
    // Whole-session observedServers suppresses the missing-server-attribution caveat
    expect(findAllElements(element, (el) => el.props['data-testid'] === 'context-caveat-no-servers')).toHaveLength(0)
  })

  it('shows no-server-attribution caveat when observedServers is explicitly empty', () => {
    const context: ContextCompositionData = {
      measurable: true,
      observedBuckets: ['system_prompt', 'tool_definitions'],
      observedServers: [],
      turns: [
        {
          index: 1,
          reported_input: 2000,
          buckets: { system_prompt: 1000, tool_definitions: 1000 },
        },
      ],
    }

    const element = React.createElement(ContextCompositionChart, { context })
    expect(findAllElements(element, (el) => el.props['data-testid'] === 'context-caveat-no-servers')).toHaveLength(1)
  })

  it('sizes Residual % from whole-session measuredInputTotal and residualTotal across different pages', () => {
    const page1Context: ContextCompositionData = {
      measurable: true,
      measuredInputTotal: 10000,
      residualTotal: 3000,
      turns: [{ index: 1, reported_input: 5000, buckets: { system_prompt: 4500, residual: 500 } }],
    }
    const page2Context: ContextCompositionData = {
      measurable: true,
      measuredInputTotal: 10000,
      residualTotal: 3000,
      turns: [{ index: 2, reported_input: 5000, buckets: { system_prompt: 2500, residual: 2500 } }],
    }

    const page1Text = normalizedRenderText(React.createElement(ContextCompositionChart, { context: page1Context }))
    const page2Text = normalizedRenderText(React.createElement(ContextCompositionChart, { context: page2Context }))
    expect(page1Text).toMatch(/Residual: 30\.0\s*%/)
    expect(page2Text).toMatch(/Residual: 30\.0\s*%/)
  })

  it('scales heatmap cell intensity by whole-session maxBucketTokens when present', () => {
    clearHooks()
    hookIndex = 0
    const context: ContextCompositionData = {
      measurable: true,
      maxBucketTokens: 10000,
      turns: [{ index: 1, reported_input: 2000, buckets: { system_prompt: 2000 } }],
    }
    const barsTree = (ContextCompositionChart as (p: Record<string, unknown>) => React.ReactElement)({ context })
    const heatmapTab = findAllElements(barsTree, (el) => el.props['data-testid'] === 'tab-heatmap')[0]!
    if (typeof heatmapTab.props.onClick === 'function') {
      heatmapTab.props.onClick({ stopPropagation: vi.fn() } as { stopPropagation: () => void })
    }
    hookIndex = 0
    const heatmapTree = (ContextCompositionChart as (p: Record<string, unknown>) => React.ReactElement)({ context })
    const heatmapCells = findAllElements(
      heatmapTree,
      (el) =>
        el.type === 'span' &&
        typeof el.props.style === 'object' &&
        el.props.style !== null &&
        typeof (el.props.style as { backgroundColor?: string }).backgroundColor === 'string' &&
        (el.props.style as { backgroundColor: string }).backgroundColor.includes('color-mix'),
    )
    // 2000 / 10000 = 0.2 -> Math.round(0.2 * 75 + 10) = 25%
    expect(heatmapCells.length).toBeGreaterThan(0)
    expect((heatmapCells[0]!.props.style as { backgroundColor: string }).backgroundColor).toContain('25%')
  })

  it('suppresses heavyDrift warning and explains omitted Claude system prompt & tool schemas in caveat and footer', () => {
    const context: ContextCompositionData = {
      measurable: true,
      derivedCounts: true,
      tokenizer: 'o200k_base',
      tokenizers: [{ name: 'o200k_base', isModelOwn: false }],
      tokenizerIsModelOwn: false,
      measuredInputTotal: 10000,
      residualTotal: 4500,
      missingBuckets: ['system_prompt', 'tool_definitions'],
      residualReasons: ['Claude Code transcripts omit the runtime system prompt and tool schemas.'],
      turns: [
        {
          index: 1,
          reported_input: 10000,
          buckets: { conversation_history: 5500, residual: 4500 },
        },
      ],
    }

    const element = React.createElement(ContextCompositionChart, { context })
    const caveat = findAllElements(element, (el) => el.props['data-testid'] === 'context-caveat')[0]!
    const footer = findAllElements(element, (el) => el.props['data-testid'] === 'context-composition-footer')[0]!
    const caveatText = normalizedRenderText(caveat)
    const footerText = normalizedRenderText(footer)

    expect(String(caveat.props.className)).not.toContain('border-amber-500/40')
    expect(caveatText).not.toContain('proxy is a poor fit')
    expect(caveatText).not.toContain("Treat this session's bucket sizes as a lower bound.")
    expect(caveatText).toContain('Claude Code transcripts omit the runtime system prompt and tool schemas.')
    expect(caveatText).toMatch(/system prompt/i)
    expect(caveatText).toMatch(/tool definitions|tool schemas/i)
    expect(footerText).toMatch(/system prompt/i)
    expect(footerText).toMatch(/tool schemas|tool definitions/i)
    expect(footerText).not.toContain('Lower residual values indicate higher precision message token tracking.')
  })

  it('renders shared contextPager across both Stacked Bars and Heatmap Matrix views', () => {
    clearHooks()
    hookIndex = 0
    const context: ContextCompositionData = {
      measurable: true,
      turns: [{ index: 1, reported_input: 1000, buckets: { system_prompt: 900, residual: 100 } }],
    }
    const contextPager = {
      page: 1,
      limit: 25 as const,
      total: 50,
      onPageChange: vi.fn(),
      limitOptions: [25, 50, 100] as const,
      onLimitChange: vi.fn(),
      order: 'desc' as const,
      onOrderChange: vi.fn(),
    }

    const barsTree = (SessionSpendCharts as (p: Record<string, unknown>) => React.ReactElement)({
      turns: [{ index: 0, fresh_input: 900, output: 100 }],
      context,
      contextPager,
    })
    expect(findAllElements(barsTree, (el) => el.props['data-testid'] === 'list-pager')).toHaveLength(1)

    const contextEl = findAllElements(barsTree, (el) => el.type === ContextCompositionChart)[0]!
    hookIndex = 0
    const innerBars = (ContextCompositionChart as (p: Record<string, unknown>) => React.ReactElement)(contextEl.props)
    const heatmapTab = findAllElements(innerBars, (el) => el.props['data-testid'] === 'tab-heatmap')[0]!
    if (typeof heatmapTab.props.onClick === 'function') {
      heatmapTab.props.onClick({ stopPropagation: vi.fn() } as { stopPropagation: () => void })
    }
    hookIndex = 0
    const innerHeatmap = (ContextCompositionChart as (p: Record<string, unknown>) => React.ReactElement)(contextEl.props)
    expect(findAllElements(innerHeatmap, (el) => el.props['data-testid'] === 'context-heatmap')).toHaveLength(1)
    expect(findAllElements(innerHeatmap, (el) => el.props['data-testid'] === 'list-pager')).toHaveLength(1)
  })

  it('keeps Pager rendered with an empty-page message when turns is empty on a past-end page with total > 0', () => {
    const context: ContextCompositionData = {
      measurable: true,
      contextLimit: 200000,
      turns: [],
    }
    const contextPager = {
      page: 4,
      limit: 50 as const,
      total: 128,
      onPageChange: vi.fn(),
      limitOptions: [25, 50, 100] as const,
      onLimitChange: vi.fn(),
      order: 'desc' as const,
      onOrderChange: vi.fn(),
    }

    const element = React.createElement(ContextCompositionChart, { context, contextPager })
    const text = normalizedRenderText(element)
    expect(text).toContain('No turns on this page.')
    expect(text).not.toContain('Context composition telemetry was not captured for this session.')
    expect(findAllElements(element, (el) => el.props['data-testid'] === 'list-pager')).toHaveLength(1)
    expect(findAllElements(element, (el) => el.props['data-testid'] === 'pager-prev')).toHaveLength(1)
  })

  it('preserves heavyDrift warning and standard drift footer when residualReasons only contains tokenizer_drift', () => {
    const context: ContextCompositionData = {
      measurable: true,
      derivedCounts: true,
      tokenizer: 'o200k_base',
      tokenizers: [{ name: 'o200k_base', isModelOwn: false }],
      tokenizerIsModelOwn: false,
      measuredInputTotal: 10000,
      residualTotal: 4500,
      residualReasons: ['tokenizer_drift'],
      turns: [
        {
          index: 1,
          reported_input: 10000,
          buckets: { conversation_history: 5500, residual: 4500 },
        },
      ],
    }

    const element = React.createElement(ContextCompositionChart, { context })
    const caveat = findAllElements(element, (el) => el.props['data-testid'] === 'context-caveat')[0]!
    const footer = findAllElements(element, (el) => el.props['data-testid'] === 'context-composition-footer')[0]!
    const caveatText = normalizedRenderText(caveat)
    const footerText = normalizedRenderText(footer)

    expect(String(caveat.props.className)).toContain('border-amber-500/40')
    expect(caveatText).toContain('proxy is a poor fit')
    expect(caveatText).toContain("Treat this session's bucket sizes as a lower bound.")
    expect(findAllElements(element, (el) => el.props['data-testid'] === 'context-caveat-omissions')).toHaveLength(0)
    expect(footerText).toContain('Residual accounts for tokenizer drift, role delimiters, and unmeasured chat framing.')
    expect(footerText).toContain('Lower residual values indicate higher precision message token tracking.')
  })

  it('handles measuredInputTotal null by falling back to page measured rows and 0 as not measurable', () => {
    const nullTotalContext: ContextCompositionData = {
      measurable: true,
      measuredInputTotal: null,
      residualTotal: null as unknown as undefined,
      turns: [
        {
          index: 1,
          reported_input: 5000,
          buckets: { system_prompt: 4500, residual: 500 },
        },
      ],
    }
    const nullTotalText = normalizedRenderText(React.createElement(ContextCompositionChart, { context: nullTotalContext }))
    expect(nullTotalText).toMatch(/Residual: 10\.0\s*%/)
    expect(nullTotalText).not.toContain('not measurable')

    const zeroTotalContext: ContextCompositionData = {
      measurable: true,
      measuredInputTotal: 0,
      residualTotal: 0,
      turns: [
        {
          index: 1,
          buckets: { system_prompt: 1200 },
        },
      ],
    }
    const zeroTotalElement = React.createElement(ContextCompositionChart, { context: zeroTotalContext })
    const zeroTotalText = normalizedRenderText(zeroTotalElement)
    expect(zeroTotalText).toContain('not measurable')
    expect(zeroTotalText).not.toContain('0.0%')
    expect(findAllElements(zeroTotalElement, (el) => el.props['data-testid'] === 'context-caveat-unmeasured-residual')).toHaveLength(1)
  })
})
