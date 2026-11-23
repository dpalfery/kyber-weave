// @vitest-environment happy-dom
// The call-tree and duration views. The timeline section owns the level
// queries; these tests pin what a viewer sees and what a press asks the
// section to load. Synthetic nodes only — no fixture carries a real payload.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'

import { DurationView, TimelineView, type TimelineLevelState, type TimelineNode } from './TimelineView.js'

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

function node(overrides: Partial<TimelineNode> = {}): TimelineNode {
  return {
    spanId: 'span-1',
    parentId: null,
    children: [],
    childCount: 0,
    startMs: 0,
    durationMs: 1000,
    kind: 'agent',
    name: 'invoke_agent',
    attributes: {},
    isSubagent: false,
    isAuxiliary: false,
    cost: { basis: 'none', status: '—' },
    ...overrides,
  }
}

function treeRoot(children: TimelineNode[]): TimelineNode {
  return node({ spanId: 'session-root', name: 'Session', children })
}

const LOADED: TimelineLevelState = { loading: false, error: null, canLoadMore: false, loadedCount: 1, total: 1 }

describe('TimelineView: call tree', () => {
  it('offers expansion only where children exist, and asks the section to load them', () => {
    const onToggleExpand = vi.fn()
    render(
      <TimelineView
        root={treeRoot([
          node({ spanId: 'parent-1', name: 'invoke_agent parent', childCount: 3 }),
          node({ spanId: 'leaf-1', name: 'Read file' }),
        ])}
        onToggleExpand={onToggleExpand}
      />,
    )
    const expand = screen.getByTestId('timeline-node-expand')
    expect(expand.getAttribute('aria-expanded')).toBe('false')
    fireEvent.click(expand)
    expect(onToggleExpand).toHaveBeenCalledTimes(1)
    expect(onToggleExpand.mock.calls[0]![0].spanId).toBe('parent-1')
  })

  it('renders the loaded children of an expanded node and hides the collapsed count', () => {
    const parent = node({
      spanId: 'parent-1',
      name: 'invoke_agent parent',
      childCount: 1,
      children: [node({ spanId: 'child-1', name: 'Read file', parentId: 'parent-1' })],
    })
    render(
      <TimelineView
        root={treeRoot([parent, node({ spanId: 'leaf-1', childCount: 2 })])}
        expandedIds={['parent-1']}
        levelState={(parentId) => (parentId === 'parent-1' ? LOADED : undefined)}
      />,
    )
    expect(screen.getByText('Read file')).toBeTruthy()
    expect(screen.getAllByTestId('timeline-node').length).toBe(3)
    expect(screen.getByTestId('timeline-node-hidden').textContent).toBe('2 hidden')
  })

  it('states a level still loading', () => {
    render(
      <TimelineView
        root={treeRoot([node({ spanId: 'parent-1', childCount: 5 })])}
        expandedIds={['parent-1']}
        levelState={(parentId) =>
          parentId === 'parent-1' ? { loading: true, error: null, canLoadMore: false, loadedCount: 0, total: 5 } : undefined}
      />,
    )
    const loading = screen.getByTestId('timeline-level-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states a level failure and retries it on press', () => {
    const onRetryLevel = vi.fn()
    render(
      <TimelineView
        root={treeRoot([node({ spanId: 'parent-1', childCount: 5 })])}
        expandedIds={['parent-1']}
        levelState={(parentId) =>
          parentId === 'parent-1'
            ? { loading: false, error: 'Request failed (500)', canLoadMore: false, loadedCount: 0, total: 5 }
            : undefined}
        onRetryLevel={onRetryLevel}
      />,
    )
    const failure = screen.getByTestId('timeline-level-error')
    expect(failure.textContent).toContain('Request failed (500)')
    fireEvent.click(screen.getByTestId('timeline-level-retry'))
    expect(onRetryLevel).toHaveBeenCalledWith('parent-1')
  })

  it('offers the next page of a level that is not fully loaded', () => {
    const onLoadMore = vi.fn()
    render(
      <TimelineView
        root={treeRoot([node({ spanId: 'parent-1', childCount: 30 })])}
        expandedIds={['parent-1']}
        levelState={(parentId) =>
          parentId === 'parent-1'
            ? { loading: false, error: null, canLoadMore: true, loadedCount: 25, total: 30 }
            : undefined}
        onLoadMore={onLoadMore}
      />,
    )
    const more = screen.getByTestId('timeline-level-more')
    expect(more.textContent).toContain('5')
    fireEvent.click(more)
    expect(onLoadMore).toHaveBeenCalledWith('parent-1')
  })

  it('offers the next page of the top level under its rows', () => {
    const onLoadMore = vi.fn()
    render(
      <TimelineView
        root={treeRoot([node({ spanId: 'top-1' })])}
        levelState={(parentId) =>
          parentId === null ? { loading: false, error: null, canLoadMore: true, loadedCount: 25, total: 30 } : undefined}
        onLoadMore={onLoadMore}
      />,
    )
    fireEvent.click(screen.getByTestId('timeline-level-more'))
    expect(onLoadMore).toHaveBeenCalledWith(null)
  })
})

describe('TimelineView: selected node attributes', () => {
  function jsonResponse(body: unknown, status = 200): Response {
    return { ok: status < 400, status, json: () => Promise.resolve(body) } as unknown as Response
  }

  it('fetches the attributes of a selected node that also carries some inline, and renders the fetched keys', async () => {
    // A content-free timeline node still names a few typed attributes inline,
    // but the consent-allowed prompt text the engine attaches only on the
    // span-scoped read lives behind the attributes route. The attributes panel
    // fetches the node it shows even when the node carries something inline;
    // the inline map stays on screen until the fetch answers.
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/kyber/span/span-1/attributes')) {
        return Promise.resolve(
          jsonResponse({
            spanId: 'span-1',
            attributes: { 'agent.name': 'coder-bot', 'factory.turn.prompt.text': 'the consent-allowed prompt' },
          }),
        )
      }
      return Promise.resolve(jsonResponse({}, 404))
    })
    vi.stubGlobal('fetch', fetchMock)
    render(<TimelineView root={treeRoot([node({ attributes: { 'agent.name': 'coder-bot' } })])} />)

    fireEvent.click(screen.getByTestId('timeline-node'))
    const panel = await screen.findByTestId('timeline-attributes')
    await waitFor(() => expect(panel.textContent).toContain('the consent-allowed prompt'))
    expect(panel.textContent).toContain('coder-bot')
    expect(
      fetchMock.mock.calls.some(([input]) => String(input).includes('/api/kyber/span/span-1/attributes')),
    ).toBe(true)
  })
})

describe('DurationView: duration bars', () => {
  it('scales every loaded node to the bounds the section answered', () => {
    render(
      <DurationView
        nodes={[
          node({ spanId: 'a', startMs: 0, durationMs: 400 }),
          node({ spanId: 'b', startMs: 400, durationMs: 100 }),
        ]}
        bounds={{ startMs: 0, endMs: 500 }}
      />,
    )
    const barA = screen.getByTestId('timeline-bar-a')
    const barB = screen.getByTestId('timeline-bar-b')
    expect(barA.style.left).toBe('0%')
    expect(barA.style.width).toBe('80%')
    expect(barB.style.left).toBe('80%')
    expect(barB.style.width).toBe('20%')
  })

  it('lists the nodes loaded so far, one row each', () => {
    render(
      <DurationView
        nodes={[
          node({ spanId: 'a', name: 'invoke_agent parent' }),
          node({ spanId: 'b', name: 'Read file', kind: 'tool' }),
        ]}
        bounds={{ startMs: 0, endMs: 1000 }}
      />,
    )
    expect(screen.getAllByTestId('timeline-bar-row').length).toBe(2)
    expect(screen.getByTestId('timeline-bar-b')).toBeTruthy()
  })
})
