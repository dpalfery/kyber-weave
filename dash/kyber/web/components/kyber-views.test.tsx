import { describe, it, expect } from 'vitest'
import * as React from 'react'

import { ContextView } from './ContextView.js'
import { SchemaView } from './SchemaView.js'
import { CompareView } from './CompareView.js'
import { NotMeasurable } from './NotMeasurable.js'
import { DerivedTokens } from './DerivedCaveat.js'

function renderText(element: React.ReactElement): string {
  const walk = (node: unknown): string => {
    if (node == null || typeof node === 'boolean') return ''
    if (typeof node === 'string' || typeof node === 'number') return String(node)
    if (Array.isArray(node)) return node.map(walk).join(' ')
    if (React.isValidElement(node)) {
      const el = node as React.ReactElement<{ children?: unknown }>
      const type = el.type
      if (typeof type === 'function') {
        const result = (type as (p: unknown) => unknown)(el.props)
        return walk(result)
      }
      return walk(el.props.children)
    }
    return ''
  }
  return walk(element)
}

describe('NotMeasurable rendering', () => {
  it('renders not measurable not zero', () => {
    const text = renderText(React.createElement(NotMeasurable, { reason: 'no tool definitions' }))
    expect(text).toContain('not measurable')
    expect(text).not.toContain('>0<')
  })

  it('ContextView not measurable path renders not measurable', () => {
    const text = renderText(React.createElement(ContextView, { analysis: { measurable: false, reason: 'no_message_structure', turns: 2, contextLimit: 100000 } as never }))
    expect(text).toContain('not measurable')
    expect(text).not.toMatch(/\b0 tokens\b/)
  })

  it('SchemaView not measurable renders not measurable with invocation count', () => {
    const text = renderText(React.createElement(SchemaView, { analysis: { measurable: false, invocationCount: 368 } as never }))
    expect(text).toContain('not measurable')
    expect(text).not.toContain('>0<')
  })

  it('CompareView unavailable metric renders not measurable not zero', () => {
    const table = {
      harnesses: ['pi', 'copilot'],
      rows: [
        {
          metric: 'schema_cost_per_turn',
          kind: 'per_turn' as const,
          label: 'Tool-schema tokens per turn',
          unit: 'tokens' as const,
          cells: {
            pi: { measurable: false, availability: 'not_measurable', render: 'not measurable' },
            copilot: { measurable: true, availability: 'derived', value: 120, render: '~120 (derived, lower bound)' },
          },
        },
      ],
      problems: [],
    }
    const text = renderText(React.createElement(CompareView, { table: table as never }))
    expect(text).toContain('not measurable')
    const matches = (text.match(/not measurable/g) ?? []).length
    expect(matches).toBe(1)
    expect(text).not.toMatch(/pi.*>0</)
  })
})

describe('Derived-token caveat', () => {
  it('DerivedTokens renders lower bound with model name when derived', () => {
    const text = renderText(React.createElement(DerivedTokens, { count: 9001, model: 'o200k_base', derived: true }))
    expect(text).toContain('lower bound')
    expect(text).toContain('o200k_base')
  })

  it('DerivedTokens does not show caveat when not derived', () => {
    const text = renderText(React.createElement(DerivedTokens, { count: 9001, model: 'o200k_base', derived: false }))
    expect(text).not.toContain('lower bound')
  })

  it('ContextView with derivedCounts shows lower bound caveat with model', () => {
    const analysis = {
      measurable: true as const,
      contextLimit: 100000,
      turns: [
        {
          index: 1,
          buckets: {
            system_prompt: 1000,
            tool_definitions: 2000,
            instruction_context: 0,
            conversation_history: 3000,
            tool_result_content: 1000,
          },
          toolDefinitionsByServer: new Map(),
          builtinToolDefinitionTokens: 2000,
          strippedInstructionBlocks: { count: 0, tokens: 0 },
          bucketedTokens: 7000,
          residual: { tokens: 500, attribution: 'tokenizer_drift' as const },
          headroom: 93000,
          pressure: 0.07,
          accumulationRate: 7000,
          freshInput: 7000,
        },
      ],
      residualTotal: 500,
      derivedCounts: true,
      derivedModel: 'o200k_base',
      freshJumpFactor: 2,
      flaggedTurns: [],
      sessionAccumulationRate: 7000,
    }
    const text = renderText(React.createElement(ContextView, { analysis: analysis as never }))
    expect(text).toContain('lower bound')
    expect(text).toContain('o200k_base')
  })

  it('SchemaView shows derived caveat for token residencies', () => {
    const analysis = {
      measurable: true as const,
      ranked: [{ name: 'read', server: 'fs', cost: 1000, invoked: true }],
      neverInvoked: [],
      byServer: new Map([['fs', 1000]]),
      unusedRange: { tokenResidencies: 7000, floor: 0, ceiling: 7000 },
      turns: 10,
      derived: true,
      derivedModel: 'o200k_base',
    }
    const text = renderText(React.createElement(SchemaView, { analysis: analysis as never }))
    expect(text).toContain('lower bound')
    expect(text).toContain('o200k_base')
  })

  it('CompareView derived cell shows lower bound', () => {
    const table = {
      harnesses: ['copilot'],
      rows: [
        {
          metric: 'schema_cost_per_turn',
          kind: 'per_turn' as const,
          label: 'Tool-schema tokens per turn',
          unit: 'tokens' as const,
          cells: {
            copilot: { measurable: true, availability: 'derived', value: 55, render: '~55 (derived, lower bound)' },
          },
        },
      ],
      problems: [],
    }
    const text = renderText(React.createElement(CompareView, { table: table as never }))
    expect(text).toContain('lower bound')
  })
})
