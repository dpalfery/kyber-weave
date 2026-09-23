import { describe, it, expect } from 'vitest'
import * as React from 'react'

import { NotMeasurable } from './NotMeasurable.js'
import { DerivedTokens } from './DerivedCaveat.js'
import { TurnAlignedDiff } from './TurnAlignedDiff.js'
import { CompareRuns } from '../../pages/CompareRuns.js'

void CompareRuns

function renderText(element: React.ReactElement): string {
  const walk = (node: unknown): string => {
    if (node == null || typeof node === 'boolean') return ''
    if (typeof node === 'string' || typeof node === 'number') return String(node)
    if (Array.isArray(node)) return node.map(walk).join(' ')
    if (React.isValidElement(node)) {
      const el = node as React.ReactElement<{ children?: unknown }>
      const type = el.type as unknown
      if (typeof type === 'function') {
        const result = (type as (p: unknown) => unknown)(el.props)
        return walk(result)
      }
      return walk((el.props as { children?: unknown }).children)
    }
    return ''
  }
  return walk(element)
}

describe('dash: NotMeasurable rendering', () => {
  it('renders not measurable not zero', () => {
    const text = renderText(React.createElement(NotMeasurable, { reason: 'no tool definitions' }))
    expect(text).toContain('not measurable')
  })
})

describe('dash: Derived-token caveat', () => {
  it('DerivedTokens renders lower bound with model name when derived', () => {
    const text = renderText(React.createElement(DerivedTokens, { count: 9001, model: 'o200k_base', derived: true }))
    expect(text).toContain('lower bound')
    expect(text).toContain('o200k_base')
  })
})

describe('dash: TurnAlignedDiff and CompareRuns UI', () => {
  it('TurnAlignedDiff renders semantic phase titles and turn diff reading', () => {
    const pairs = [
      {
        phase: 'exploration' as const,
        phaseIndex: 0,
        runATurn: {
          turnIndex: 0,
          phase: 'exploration' as const,
          tools: ['grep_search'],
          tokens: { freshInput: 500, output: 100, all: 600 },
        },
        runBTurn: {
          turnIndex: 0,
          phase: 'exploration' as const,
          tools: ['find_by_name'],
          tokens: { freshInput: 800, output: 200, all: 1000 },
        },
        signals: [
          {
            name: 'total_tokens',
            label: 'Tokens',
            delta: 400,
            status: 'compared' as const,
          },
        ],
        reading: 'Phase exploration: Run A used 600 tokens; Run B used 1000 tokens.',
      },
    ]

    const text = renderText(React.createElement(TurnAlignedDiff, { pairs }))
    expect(text).toContain('Exploration')
    expect(text).toMatch(/Turn\s+1/)
    expect(text).toContain('Phase exploration')
    expect(text).toContain('grep_search')
    expect(text).toContain('find_by_name')
  })

  it('TurnAlignedDiff renders not comparable for unmeasured signals', () => {
    const pairs = [
      {
        phase: 'exploration' as const,
        phaseIndex: 0,
        runATurn: {
          turnIndex: 0,
          tokens: { all: 600 },
        },
        runBTurn: {
          turnIndex: 0,
          tokens: { all: 600 },
        },
        signals: [
          {
            name: 'cache_read',
            label: 'Cache-read tokens',
            status: 'not_comparable' as const,
            reason: 'cache_read not measurable in Run A',
          },
        ],
        reading: 'cache read comparison unavailable',
      },
    ]

    const text = renderText(React.createElement(TurnAlignedDiff, { pairs }))
    expect(text).toContain('not comparable')
    expect(text).not.toContain('+0')
  })
})

