import { describe, it, expect, vi } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'

import {
  SchemaCostRanking,
  type SchemaCostAnalysis,
  type RankedTool,
} from './SchemaCostRanking'

const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: {
      H?: unknown
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

function findElementByTestId(node: unknown, testId: string): React.ReactElement<Record<string, unknown>> | null {
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
      return node as React.ReactElement<Record<string, unknown>>
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

const ranked: RankedTool[] = [
  { name: 'write_file', server: 'fs', cost: 6000, invoked: true },
  { name: 'mcp_git_status', server: 'git', cost: 4500, invoked: false },
  { name: 'read_file', cost: 3500, invoked: true },
]

const measurable: SchemaCostAnalysis = {
  measurable: true,
  ranked,
  neverInvoked: [ranked[1]!],
  byServer: { git: 4500, fs: 6000 },
  unusedRange: { tokenResidencies: 4500, floor: 0, ceiling: 4500 },
  turns: 5,
}

describe('SchemaCostRanking', () => {
  it('renders tools in descending resident-cost order', () => {
    const html = renderHtml(<SchemaCostRanking schema={measurable} />)
    const writeIdx = html.indexOf('write_file')
    const gitIdx = html.indexOf('mcp_git_status')
    const readIdx = html.indexOf('read_file')
    expect(writeIdx).toBeGreaterThan(-1)
    expect(gitIdx).toBeGreaterThan(-1)
    expect(readIdx).toBeGreaterThan(-1)
    expect(writeIdx).toBeLessThan(gitIdx)
    expect(gitIdx).toBeLessThan(readIdx)
  })

  it('marks never-invoked tools as distinct from invoked ones', () => {
    const html = renderHtml(<SchemaCostRanking schema={measurable} />)
    expect(html).toContain('data-testid="tool-row-mcp_git_status"')
    expect(html).toContain('data-never-invoked="true"')
    expect(html).toContain('0 calls')
    expect(html).toContain('never called')
    expect(html).toContain('bg-amber-500/10')

    const invokedRow = html.slice(html.indexOf('data-testid="tool-row-write_file"'), html.indexOf('data-testid="tool-row-mcp_git_status"'))
    expect(invokedRow).toContain('data-never-invoked="false"')
    expect(invokedRow).not.toContain('never called')
  })

  it('rolls up resident cost per MCP server from byServer', () => {
    const html = renderHtml(<SchemaCostRanking schema={measurable} />)
    expect(html).toContain('data-testid="schema-server-rollup"')
    expect(html).toContain('data-testid="schema-server-fs"')
    expect(html).toContain('data-testid="schema-server-git"')
    expect(html).toContain('fs')
    expect(html).toContain('git')
    expect(html).toContain('6.0K')
    expect(html).toContain('4.5K')
  })

  it('groups tools with no server as Built-in and does not guess a server from the name', () => {
    const schema: SchemaCostAnalysis = {
      measurable: true,
      ranked: [
        { name: 'context7__query', cost: 8000, invoked: false },
        { name: 'search', server: 'codegraph', cost: 3000, invoked: true },
        { name: 'Read', cost: 1000, invoked: true },
      ],
      neverInvoked: [{ name: 'context7__query', cost: 8000, invoked: false }],
      byServer: { codegraph: 3000 },
      unusedRange: { tokenResidencies: 8000, floor: 0, ceiling: 8000 },
      turns: 4,
    }
    const html = renderHtml(<SchemaCostRanking schema={schema} />)

    expect(html).toContain('data-testid="schema-server-builtin"')
    expect(html).toContain('Built-in')
    expect(html).toContain('data-testid="schema-server-codegraph"')
    expect(html).not.toContain('data-testid="schema-server-context7"')
    expect(html).not.toContain('data-testid="schema-server-context7__query"')

    const builtinBlock = html.slice(
      html.indexOf('data-testid="schema-server-builtin"'),
      html.indexOf('data-testid="schema-unused-range"'),
    )
    expect(builtinBlock).toContain('9.0K')
  })

  it('renders unused cost as a floor-to-ceiling range of token residencies when there is no currency', () => {
    const html = renderHtml(<SchemaCostRanking schema={measurable} />)
    expect(html).toContain('data-testid="schema-unused-range"')
    expect(html).toContain('token residencies')
    expect(html).toMatch(/0\s+–\s+4\.5K/)
    expect(html).not.toContain('$')
  })

  it('renders unused cost as a priced range when currency is present, still never as a single figure', () => {
    const priced: SchemaCostAnalysis = {
      ...measurable,
      unusedRange: { tokenResidencies: 4500, floor: 0.03, ceiling: 0.12, currency: 'USD' },
    }
    const html = renderHtml(<SchemaCostRanking schema={priced} />)
    expect(html).toContain('data-testid="schema-unused-range"')
    expect(html).toMatch(/\$0\.03\s+–\s+\$0\.12/)
    expect(html).not.toContain('token residencies')
  })

  it('renders a not-measurable card instead of an empty ranking when definitions were not exported', () => {
    const html = renderHtml(
      <SchemaCostRanking schema={{ measurable: false, invocationCount: 368 }} />,
    )
    expect(html).toContain('data-testid="schema-cost-not-measurable"')
    expect(html).toContain('Not Measurable')
    expect(html).toContain('368')
    expect(html).toContain('exported no tool definitions')
    expect(html).not.toContain('data-testid="tools-ranking-table"')
    expect(html).not.toContain('data-testid="schema-server-rollup"')
    expect(html).not.toContain('No tools recorded for this session.')
  })

  it('uses the payload tools array when schema.ranked was not serialised', () => {
    const schema: SchemaCostAnalysis = {
      measurable: true,
      neverInvoked: [{ name: 'dead', server: 'git', cost: 2000, invoked: false }],
      byServer: { git: 2000, fs: 5000 },
      unusedRange: { tokenResidencies: 2000, floor: 0, ceiling: 2000 },
      turns: 3,
    }
    const html = renderHtml(
      <SchemaCostRanking
        schema={schema}
        tools={[
          { name: 'write', server: 'fs', total_schema_cost: 5000, invoked: true },
          { name: 'dead', server: 'git', total_schema_cost: 2000, invoked: false },
        ]}
      />,
    )
    expect(html.indexOf('write')).toBeLessThan(html.indexOf('dead'))
    expect(html).toContain('1 of 2 offered tools were never invoked')
    expect(html).toContain('2.0K')
  })

  it('forwards the original tool row on click so the session drawer still opens', () => {
    const onSelect = vi.fn()
    const tools = [
      { name: 'read_file', server: 'builtin', invocations: 5, total_schema_cost: 3500, invoked: true },
    ]
    const tree = SchemaCostRanking({
      schema: {
        measurable: true,
        ranked: [{ name: 'read_file', server: 'builtin', cost: 3500, invoked: true }],
        neverInvoked: [],
        byServer: { builtin: 3500 },
        unusedRange: { tokenResidencies: 0, floor: 0, ceiling: 0 },
        turns: 5,
      },
      tools,
      onSelectTool: onSelect,
    })
    const row = findElementByTestId(tree, 'tool-row-read_file')
    expect(row).not.toBeNull()
    expect(row?.props.onClick).toBeTypeOf('function')
    ;(row?.props.onClick as () => void)()
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ name: 'read_file', invocations: 5 }))
  })
})
