// @vitest-environment happy-dom
// The bucket drill-down mounts a query-backed panel and drives its toolbar with
// real events, so this suite runs under happy-dom with Testing Library. The
// content is seeded in the query cache with an infinite stale time so mounting
// never refetches.
import { describe, it, expect, vi, afterEach } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'

import { SessionInspectorDrawer } from './SessionInspectorDrawer.js'
import type { KyberSessionContentResult } from '../lib/kyberApi.js'

afterEach(cleanup)

const SESSION_ID = 'sess-bucket-toolbar'
const SPAN_ID = 'span-bucket-toolbar'
const PART = 'system_prompt'
const QUERY_KEY = ['kyber-session-content', SESSION_ID, SPAN_ID, PART, 'full']
const RAW = 'alpha one alpha two alpha three'

function renderBucket(parts: KyberSessionContentResult['parts']) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity, gcTime: Infinity } },
  })
  const body: KyberSessionContentResult = { sessionId: SESSION_ID, spanId: SPAN_ID, parts }
  qc.setQueryData(QUERY_KEY, body)
  return render(
    <QueryClientProvider client={qc}>
      <SessionInspectorDrawer
        open={true}
        onClose={() => {}}
        title="Turn 1 · system_prompt"
        contentRequest={{ sessionId: SESSION_ID, span: SPAN_ID, part: PART }}
      />
    </QueryClientProvider>,
  )
}

function typeQuery(testId: string, value: string) {
  fireEvent.change(screen.getByTestId(testId), { target: { value } })
}

function renderBucketTurn(section: Record<string, unknown>) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity, gcTime: Infinity } },
  })
  qc.setQueryData(['kyber-session-turn', SESSION_ID, 0], section)
  return render(
    <QueryClientProvider client={qc}>
      <SessionInspectorDrawer
        open={true}
        onClose={() => {}}
        title="Turn 1 · tool_definitions"
        contentRequest={{ sessionId: SESSION_ID, turnIndex: 0, bucket: 'tool_definitions' }}
      />
    </QueryClientProvider>,
  )
}

describe('SessionInspectorDrawer: bucket drill-down shared toolbar', () => {
  it('renders the Formatted/Raw toolbar with search and Copy on a bucket part', () => {
    renderBucket([{ spanId: 'span-a', part: PART, text: RAW }])

    expect(screen.getByTestId('drawer-content-0-view-formatted').textContent).toBe('Formatted')
    expect(screen.getByTestId('drawer-content-0-view-raw').textContent).toBe('Raw')
    expect(screen.getByTestId('drawer-content-0-search')).toBeTruthy()
    expect(screen.getByTestId('copy-drawer-content-0')).toBeTruthy()
  })

  it('counts search matches in the formatted view and again in the raw view', async () => {
    renderBucket([{ spanId: 'span-a', part: PART, text: RAW }])

    typeQuery('drawer-content-0-search', 'alpha')
    await waitFor(() => expect(screen.getByTestId('drawer-content-0-search-count').textContent).toBe('1/3'))

    fireEvent.click(screen.getByTestId('drawer-content-0-view-raw'))
    // The exact text is now the rendered body; the search re-scans it.
    expect(screen.getByText(RAW, { selector: 'pre' })).toBeTruthy()
    await waitFor(() => expect(screen.getByTestId('drawer-content-0-search-count').textContent).toBe('1/3'))

    const input = screen.getByTestId('drawer-content-0-search')
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(screen.getByTestId('drawer-content-0-search-count').textContent).toBe('2/3')
  })

  it('copies the exact part text from the raw view', () => {
    const written: string[] = []
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      value: {
        writeText: vi.fn((s: string) => {
          written.push(s)
          return Promise.resolve()
        }),
      },
      configurable: true,
      writable: true,
    })

    renderBucket([{ spanId: 'span-a', part: PART, text: RAW }])
    fireEvent.click(screen.getByTestId('drawer-content-0-view-raw'))
    fireEvent.click(screen.getByTestId('copy-drawer-content-0'))
    expect(written).toEqual([RAW])
  })

  it('shows one toolbar for N identical parts, and one for the distinct part', () => {
    const distinct = 'distinct context body'
    renderBucket([
      { spanId: 'span-a', part: PART, text: RAW },
      { spanId: 'span-b', part: PART, text: distinct },
      { spanId: 'span-c', part: PART, text: RAW },
      { spanId: 'span-d', part: PART, text: RAW },
    ])

    expect(screen.getAllByTestId(/-view-formatted$/)).toHaveLength(2)
    expect(screen.getByTestId('drawer-content-duplicate-note').textContent).toBe('Identical in 3 captured spans')
    expect(screen.getByTestId('drawer-content-0-view-formatted')).toBeTruthy()
    expect(screen.getByTestId('drawer-content-1-view-formatted')).toBeTruthy()
  })
})

function bucketFigures(container: HTMLElement): string {
  const inspector = container.querySelector('[data-testid="context-bucket-inspector"]')
  if (!inspector) throw new Error('bucket inspector did not render')
  return inspector.textContent ?? ''
}

describe('SessionInspectorDrawer: bucket drill-down figures are never NaN', () => {
  it('shows not measured for a not-measurable bucket, never NaN% or 0.0%', () => {
    const { container } = renderBucketTurn({
      sessionId: SESSION_ID,
      turnIndex: 0,
      turn: { index: 0, spanId: 'span-t0' },
      context: {
        index: 1,
        buckets: { tool_definitions: { availability: 'not_measurable', reason: 'not exported' }, residual: 500 },
        reported_input: 1000,
      },
    })

    const text = bucketFigures(container)
    expect(text.match(/not measured/g)).toHaveLength(2)
    expect(text).not.toContain('NaN%')
    expect(text).not.toContain('0.0%')
  })

  it('shows not measured when the bucket key is absent', () => {
    const { container } = renderBucketTurn({
      sessionId: SESSION_ID,
      turnIndex: 0,
      turn: { index: 0, spanId: 'span-t0' },
      context: { index: 1, buckets: { residual: 500 }, reported_input: 1000 },
    })

    const text = bucketFigures(container)
    expect(text.match(/not measured/g)).toHaveLength(2)
    expect(text).not.toContain('NaN%')
  })

  it.each([
    ['NaN', Number.NaN],
    ['Infinity', Number.POSITIVE_INFINITY],
  ])('shows not measured for a %s bucket figure, never NaN%% or Infinity%%', (_name, value) => {
    const { container } = renderBucketTurn({
      sessionId: SESSION_ID,
      turnIndex: 0,
      turn: { index: 0, spanId: 'span-t0' },
      context: { index: 1, buckets: { tool_definitions: value, residual: 500 }, reported_input: 1000 },
    })

    const text = bucketFigures(container)
    expect(text.match(/not measured/g)).toHaveLength(2)
    expect(text).not.toContain('NaN')
    expect(text).not.toContain('Infinity')
  })

  it('ignores a non-finite reported total and divides by the finite bucket sum', () => {
    const { container } = renderBucketTurn({
      sessionId: SESSION_ID,
      turnIndex: 0,
      turn: { index: 0, spanId: 'span-t0' },
      context: { index: 1, buckets: { tool_definitions: 250, residual: 250 }, reported_input: Number.NaN },
    })

    const text = bucketFigures(container)
    expect(text).toContain('50.0%')
    expect(text).not.toContain('NaN')
  })

  it('shows a measured zero as 0 and 0.0%, staying measured', () => {
    const { container } = renderBucketTurn({
      sessionId: SESSION_ID,
      turnIndex: 0,
      turn: { index: 0, spanId: 'span-t0' },
      context: { index: 1, buckets: { tool_definitions: 0, residual: 1000 }, reported_input: 1000 },
    })

    const text = bucketFigures(container)
    expect(text).toContain('0.0%')
    expect(text).not.toContain('not measured')
  })

  it('shows a measured 250 of 1000 as 25.0%', () => {
    const { container } = renderBucketTurn({
      sessionId: SESSION_ID,
      turnIndex: 0,
      turn: { index: 0, spanId: 'span-t0' },
      context: { index: 1, buckets: { tool_definitions: 250, residual: 750 }, reported_input: 1000 },
    })

    expect(bucketFigures(container)).toContain('25.0%')
  })

  it('takes the bucket sum as the total when every reported figure is non-numeric', () => {
    const { container } = renderBucketTurn({
      sessionId: SESSION_ID,
      turnIndex: 0,
      turn: { index: 0, spanId: 'span-t0', cumulative_input: { x: 1 }, input: 'n/a', fresh: null },
      context: { index: 1, buckets: { tool_definitions: 250, residual: 750 }, reported_input: 'n/a' },
    })

    const text = bucketFigures(container)
    expect(text).toContain('25.0%')
    expect(text).not.toContain('NaN')
  })
})

describe('SessionInspectorDrawer: bucket drill-down on a faulted projection', () => {
  it('shows the error state, never the bucket figures or a zero', () => {
    renderBucketTurn({
      sessionId: SESSION_ID,
      turnIndex: 0,
      turn: { index: 0, spanId: 'span-t0' },
      context: { index: 1, buckets: { tool_definitions: 0, residual: 0 }, reported_input: 0 },
      projection_status: 'PROJECTION_FAILED',
    })

    const error = screen.getByTestId('turn-section-error')
    expect(error.getAttribute('role')).toBe('alert')
    expect(error.textContent).toContain('PROJECTION_FAILED')
    // No bucket inspector, no total, no zero: the route answered "not data".
    expect(screen.queryByTestId('context-bucket-inspector')).toBeNull()
    expect(screen.queryByTestId('turn-token-totals')).toBeNull()
    expect(screen.queryByTestId('turn-token-totals-absent')).toBeNull()
  })
})
