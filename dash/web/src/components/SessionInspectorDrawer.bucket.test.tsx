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
