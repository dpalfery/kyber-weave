// @vitest-environment happy-dom
// The problems section: one page of the session's stored ingest problems,
// each row carrying its severity, code and message. An empty page states that
// no problem was stored — never that the session was flawless beyond the store.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionProblemsSection } from './SessionProblemsSection.js'

const SESSION_ID = 'sess-1'
const ROUTE_PAGE_1 = `/api/kyber/session/${SESSION_ID}/problems?page=1&limit=25&order=desc`
const ROUTE_PAGE_2 = `/api/kyber/session/${SESSION_ID}/problems?page=2&limit=25&order=desc`

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
      <SessionProblemsSection sessionId={SESSION_ID} />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  window.sessionStorage.clear()
  vi.unstubAllGlobals()
})

const PROBLEM_ROW = {
  id: 7,
  spanId: 'span-1',
  severity: 'warning',
  code: 'PARSE_SKIPPED',
  message: 'A record was skipped',
  location: 'turn 3',
  sessionId: SESSION_ID,
  harness: 'claude-code',
  timestamp: '2026-11-19T10:00:00.000Z',
}

const FIRST_PAGE = {
  problems: [PROBLEM_ROW],
  data: [PROBLEM_ROW],
  total: 30,
  limit: 25,
  offset: 0,
  page: 1,
}

describe('SessionProblemsSection states', () => {
  it('shows its loading state while the page is in flight', () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    const loading = screen.getByTestId('session-problems-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount()
    const error = await screen.findByTestId('section-error-problems')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    await waitFor(() => expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2))
    expect(fetchMock.mock.calls[1][0]).toBe(ROUTE_PAGE_1)
  })

  it('lists each stored problem with its severity, code and message', async () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    const row = await screen.findByTestId('problem-row-7')
    expect(row.textContent).toContain('warning')
    expect(row.textContent).toContain('PARSE_SKIPPED')
    expect(row.textContent).toContain('A record was skipped')
    expect(row.textContent).toContain('turn 3')
  })

  it('states the empty page honestly and hides the pager when total is 0', async () => {
    stubFetch(() => jsonResponse({ problems: [], data: [], total: 0, limit: 25, offset: 0, page: 1 }))
    mount()
    const empty = await screen.findByTestId('problems-empty')
    expect(empty.textContent).toMatch(/no stored ingest problems/i)
    expect(screen.queryByTestId('list-pager')).toBeNull()
  })

  it('shows limit and order controls on a single non-empty page and hides page buttons', async () => {
    stubFetch(() => jsonResponse({ problems: [PROBLEM_ROW], data: [PROBLEM_ROW], total: 1, limit: 25, offset: 0, page: 1 }))
    mount()
    await screen.findByTestId('problem-row-7')
    const pager = screen.getByTestId('list-pager')
    expect(within(pager).getByTestId('pager-limit')).toBeTruthy()
    expect(within(pager).getByTestId('pager-order').textContent).toBe('Newest first')
    expect(within(pager).queryByTestId('pager-prev')).toBeNull()
    expect(within(pager).queryByTestId('pager-next')).toBeNull()
  })
})

describe('SessionProblemsSection paging', () => {
  it('shows the shared pager past 25 rows and requests page two only on press', async () => {
    const rowTwo = { ...PROBLEM_ROW, id: 26, message: 'A later record was skipped' }
    const fetchMock = stubFetch((url: string) =>
      jsonResponse(
        url.includes('page=2')
          ? { problems: [rowTwo], data: [rowTwo], total: 30, limit: 25, offset: 25, page: 2 }
          : FIRST_PAGE,
      ),
    )
    mount()
    await screen.findByTestId('problem-row-7')
    const pager = screen.getByTestId('list-pager')
    expect(pager.getAttribute('aria-label')).toBe('Stored problem pages')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(false)

    fireEvent.click(within(pager).getByTestId('pager-next'))
    await screen.findByTestId('problem-row-26')
    expect(screen.getByTestId('problem-row-26').textContent).toContain('A later record was skipped')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(true)
  })

  it("holds the previous page's rows on screen while the next page is in flight, then replaces them", async () => {
    let resolveSecond: (value: Response) => void = () => {}
    const fetchMock = stubFetch((url: string) => {
      if (url.includes('page=2')) {
        return new Promise<Response>((resolve) => {
          resolveSecond = resolve
        }) as unknown as Response
      }
      return jsonResponse(FIRST_PAGE)
    })
    mount()
    await screen.findByTestId('problem-row-7')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(false)

    fireEvent.click(screen.getByTestId('pager-next'))
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(true))
    // The next page is held back: the rows the viewer was reading stay on
    // screen, and the new page's rows are absent until it lands.
    expect(screen.getByTestId('problem-row-7')).toBeTruthy()
    expect(screen.queryByTestId('problem-row-26')).toBeNull()

    resolveSecond(
      jsonResponse({
        problems: [{ ...PROBLEM_ROW, id: 26, message: 'A later record was skipped' }],
        data: [{ ...PROBLEM_ROW, id: 26, message: 'A later record was skipped' }],
        total: 30,
        limit: 25,
        offset: 25,
        page: 2,
      }),
    )
    await screen.findByTestId('problem-row-26')
    expect(screen.queryByTestId('problem-row-7')).toBeNull()
  })

  it('changes page size (25 -> 50 -> 100), persists per section, toggles sort order, and resets to page 1', async () => {
    const fetchMock = stubFetch((url: string) => {
      const parsed = new URL(url, 'http://localhost')
      const page = Number(parsed.searchParams.get('page') ?? '1')
      const limit = Number(parsed.searchParams.get('limit') ?? '25')
      const order = parsed.searchParams.get('order') ?? 'desc'
      return jsonResponse({
        problems: FIRST_PAGE.problems,
        data: FIRST_PAGE.data,
        total: 120,
        limit,
        offset: (page - 1) * limit,
        page,
        order,
      })
    })
    mount()
    await screen.findByTestId('problems-section')

    fireEvent.click(screen.getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('problems?page=2&limit=25&order=desc'))).toBe(true),
    )

    fireEvent.change(screen.getByTestId('pager-limit'), { target: { value: '50' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('problems?page=1&limit=50&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:problems')).toBe('50')

    fireEvent.change(screen.getByTestId('pager-limit'), { target: { value: '100' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('problems?page=1&limit=100&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:problems')).toBe('100')

    fireEvent.click(screen.getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('problems?page=2&limit=100&order=desc'))).toBe(true),
    )

    fireEvent.click(screen.getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('problems?page=1&limit=100&order=asc'))).toBe(true),
    )
    expect(screen.getByTestId('pager-order').textContent).toBe('Oldest first')

    fireEvent.click(screen.getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('problems?page=1&limit=100&order=desc')).length).toBeGreaterThanOrEqual(2),
    )
  })
})
