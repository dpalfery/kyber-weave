// @vitest-environment happy-dom
// The reconciliation section: one page of the session's measurable
// per-request reconciliation rows, each compared between its invoke_agent
// root and the sum of its chat turns. Mismatched rows are marked as such —
// a measured match, a measured mismatch and an absent row never read alike.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionReconciliationSection } from './SessionReconciliationSection.js'

const SESSION_ID = 'sess-1'
const ROUTE_PAGE_1 = `/api/kyber/session/${SESSION_ID}/reconciliation?page=1&limit=25&order=desc`
const ROUTE_PAGE_2 = `/api/kyber/session/${SESSION_ID}/reconciliation?page=2&limit=25&order=desc`

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function jsonResponse(body: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => body } as unknown as Response
}

function stubFetch(handler: (url: string) => Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn((url: string) => Promise.resolve(handler(url)))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function mount(): ReturnType<typeof render> {
  const element = (
    <QueryClientProvider client={queryClient()}>
      <SessionReconciliationSection sessionId={SESSION_ID} />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  window.sessionStorage.clear()
  vi.unstubAllGlobals()
})

const MATCHING_ROW = {
  request: 'span-r1',
  root_input: 70000,
  sum_chat_input: 70000,
  input_match: true,
  root_output: 3200,
  sum_chat_output: 3200,
  output_match: true,
}

const MISMATCHED_ROW = {
  request: 'span-r2',
  root_input: 50500,
  sum_chat_input: 42000,
  input_match: false,
  root_output: 3200,
  sum_chat_output: 3200,
  output_match: true,
}

const FIRST_PAGE = {
  reconciliation: [MATCHING_ROW, MISMATCHED_ROW],
  data: [MATCHING_ROW, MISMATCHED_ROW],
  total: 40,
  limit: 25,
  offset: 0,
  page: 1,
}

describe('SessionReconciliationSection states', () => {
  it('shows its loading state while the page is in flight', () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    const loading = screen.getByTestId('session-reconciliation-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount()
    const error = await screen.findByTestId('section-error-reconciliation')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    await waitFor(() => expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2))
    expect(fetchMock.mock.calls[1][0]).toBe(ROUTE_PAGE_1)
  })

  it('lists each row by its position with both input sums and marks the mismatched one', async () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    const section = await screen.findByTestId('reconciliation-section')
    const okRow = within(section).getByTestId('reconciliation-row-1')
    expect(okRow.getAttribute('data-mismatch')).toBe('false')
    expect(okRow.textContent).toContain('Request 1')
    expect(okRow.textContent).toContain('70.0K')
    const badRow = within(section).getByTestId('reconciliation-row-2')
    expect(badRow.getAttribute('data-mismatch')).toBe('true')
    expect(badRow.textContent).toContain('42.0K')
    expect(badRow.textContent).toContain('50.5K')
    expect(badRow.textContent).toContain('mismatch')
  })

  it('never shows a raw span id as a row label', async () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    const section = await screen.findByTestId('reconciliation-section')
    expect(section.textContent).not.toContain('span-r1')
    expect(section.textContent).not.toContain('span-r2')
  })

  it('renders an unmeasured sum as absent, not as zero', async () => {
    const row = { ...MATCHING_ROW, root_input: null, sum_chat_input: null }
    stubFetch(() => jsonResponse({ reconciliation: [row], data: [row], total: 1, limit: 25, offset: 0, page: 1 }))
    mount()
    const firstRow = await screen.findByTestId('reconciliation-row-1')
    expect(firstRow.textContent).toContain('—')
    expect(firstRow.textContent).not.toMatch(/\b0(\.0)?K\b/)
  })

  it('states the empty list honestly and hides the pager when total is 0', async () => {
    stubFetch(() => jsonResponse({ reconciliation: [], data: [], total: 0, limit: 25, offset: 0, page: 1 }))
    mount()
    const empty = await screen.findByTestId('reconciliation-empty')
    expect(empty.textContent).toMatch(/no measurable reconciliation rows/i)
    expect(screen.queryByTestId('list-pager')).toBeNull()
  })

  it('shows limit and order controls on a single non-empty page and hides page buttons', async () => {
    stubFetch(() =>
      jsonResponse({ reconciliation: [MATCHING_ROW], data: [MATCHING_ROW], total: 1, limit: 25, offset: 0, page: 1 }),
    )
    mount()
    await screen.findByTestId('reconciliation-section')
    const pager = screen.getByTestId('list-pager')
    expect(within(pager).getByTestId('pager-limit')).toBeTruthy()
    expect(within(pager).getByTestId('pager-order').textContent).toBe('Newest first')
    expect(within(pager).queryByTestId('pager-prev')).toBeNull()
    expect(within(pager).queryByTestId('pager-next')).toBeNull()
  })
})

describe('SessionReconciliationSection paging', () => {
  it('shows the shared pager past 25 rows and requests page two only on press', async () => {
    const pageTwoRow = { ...MATCHING_ROW, request: 'span-r26' }
    const fetchMock = stubFetch((url: string) =>
      jsonResponse(
        url.includes('page=2')
          ? { reconciliation: [pageTwoRow], data: [pageTwoRow], total: 40, limit: 25, offset: 25, page: 2 }
          : FIRST_PAGE,
      ),
    )
    mount()
    await screen.findByTestId('reconciliation-section')
    const pager = screen.getByTestId('list-pager')
    expect(pager.getAttribute('aria-label')).toBe('Reconciliation pages')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(false)

    fireEvent.click(within(pager).getByTestId('pager-next'))
    await screen.findByTestId('reconciliation-row-26')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(true)
  })

  it("holds the previous page's rows on screen while the next page is in flight, then replaces them", async () => {
    let resolveSecond: (value: Response) => void = () => {}
    const pageTwoRow = { ...MATCHING_ROW, request: 'span-r26' }
    const fetchMock = stubFetch((url: string) => {
      if (url.includes('page=2')) {
        return new Promise<Response>((resolve) => {
          resolveSecond = resolve
        }) as unknown as Response
      }
      return jsonResponse(FIRST_PAGE)
    })
    mount()
    await screen.findByTestId('reconciliation-row-1')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(false)

    fireEvent.click(screen.getByTestId('pager-next'))
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(true))
    // The next page is held back: the rows the viewer was reading stay on
    // screen, and the new page's rows are absent until it lands.
    expect(screen.getByTestId('reconciliation-row-1').textContent).toContain('Request 1')
    expect(screen.getByTestId('reconciliation-row-2').textContent).toContain('mismatch')
    expect(screen.queryByTestId('reconciliation-row-26')).toBeNull()

    resolveSecond(
      jsonResponse({ reconciliation: [pageTwoRow], data: [pageTwoRow], total: 40, limit: 25, offset: 25, page: 2 }),
    )
    await screen.findByTestId('reconciliation-row-26')
  })

  it('changes page size (25 -> 50 -> 100), persists per section, toggles sort order, and resets to page 1', async () => {
    const fetchMock = stubFetch((url: string) => {
      const parsed = new URL(url, 'http://localhost')
      const page = Number(parsed.searchParams.get('page') ?? '1')
      const limit = Number(parsed.searchParams.get('limit') ?? '25')
      const order = parsed.searchParams.get('order') ?? 'desc'
      return jsonResponse({
        reconciliation: FIRST_PAGE.reconciliation,
        data: FIRST_PAGE.data,
        total: 120,
        limit,
        offset: (page - 1) * limit,
        page,
        order,
      })
    })
    mount()
    await screen.findByTestId('reconciliation-section')

    fireEvent.click(screen.getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('reconciliation?page=2&limit=25&order=desc'))).toBe(true),
    )

    fireEvent.change(screen.getByTestId('pager-limit'), { target: { value: '50' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('reconciliation?page=1&limit=50&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:reconciliation')).toBe('50')

    fireEvent.change(screen.getByTestId('pager-limit'), { target: { value: '100' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('reconciliation?page=1&limit=100&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:reconciliation')).toBe('100')

    fireEvent.click(screen.getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('reconciliation?page=2&limit=100&order=desc'))).toBe(true),
    )

    fireEvent.click(screen.getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('reconciliation?page=1&limit=100&order=asc'))).toBe(true),
    )
    expect(screen.getByTestId('pager-order').textContent).toBe('Oldest first')

    fireEvent.click(screen.getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('reconciliation?page=1&limit=100&order=desc')).length).toBeGreaterThanOrEqual(2),
    )
  })
})
