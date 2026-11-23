// @vitest-environment happy-dom
// The requests section: one page of the session's user requests, labelled by
// each request's own prompt preview (or its position when it carried none),
// paged through the shared pager. A request is named by its span id; that id is
// never its label. Every state is asserted with the other routes irrelevant —
// the section owns exactly this one query.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionRequestsSection } from './SessionRequestsSection.js'

const SESSION_ID = 'sess-1'
const ROUTE_PAGE_1 = `/api/kyber/session/${SESSION_ID}/requests?page=1&limit=25`
const ROUTE_PAGE_2 = `/api/kyber/session/${SESSION_ID}/requests?page=2&limit=25`

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
      <SessionRequestsSection sessionId={SESSION_ID} />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const FIRST_PAGE = {
  requests: [
    { request: 'span-r1', timestamp: '2026-11-19T10:00:00.000Z', turns: 3, model: 'claude-3-5-sonnet', prompt: 'Analyze the recursion bug in ast.ts' },
    { request: 'span-r2', timestamp: '2026-11-19T10:05:00.000Z', turns: 2, model: 'claude-3-5-sonnet', prompt: 'Apply the patch and verify tests pass' },
  ],
  data: [
    { request: 'span-r1', timestamp: '2026-11-19T10:00:00.000Z', turns: 3, model: 'claude-3-5-sonnet', prompt: 'Analyze the recursion bug in ast.ts' },
    { request: 'span-r2', timestamp: '2026-11-19T10:05:00.000Z', turns: 2, model: 'claude-3-5-sonnet', prompt: 'Apply the patch and verify tests pass' },
  ],
  total: 128,
  limit: 25,
  offset: 0,
  page: 1,
}

const SECOND_PAGE = {
  requests: [{ request: 'span-r26', timestamp: '2026-11-19T11:00:00.000Z', turns: 1, model: 'claude-3-5-sonnet', prompt: 'Rewrite the parser entry point' }],
  data: [{ request: 'span-r26', timestamp: '2026-11-19T11:00:00.000Z', turns: 1, model: 'claude-3-5-sonnet', prompt: 'Rewrite the parser entry point' }],
  total: 128,
  limit: 25,
  offset: 25,
  page: 2,
}

describe('SessionRequestsSection states', () => {
  it('shows its loading state while the page is in flight', () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    const loading = screen.getByTestId('session-requests-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount()
    const error = await screen.findByTestId('section-error-requests')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    await waitFor(() => expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2))
    expect(fetchMock.mock.calls[1][0]).toBe(ROUTE_PAGE_1)
  })

  it('lists the first page with the headline from the window total, each row labelled by its prompt and its own turn count', async () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    const banner = await screen.findByTestId('user-requests')
    expect(banner.textContent).toContain('128 user requests in this session:')
    expect(screen.getByTestId('request-row-1').textContent).toContain('Analyze the recursion bug in ast.ts')
    expect(screen.getByTestId('request-row-1').textContent).toContain('3 turns')
    expect(screen.getByTestId('request-row-2').textContent).toContain('Apply the patch and verify tests pass')
    expect(screen.getByTestId('request-row-2').textContent).toContain('2 turns')
  })

  it('labels a request whose prompt was not captured by its position, never by its span id', async () => {
    const page = {
      requests: [
        { request: 'span-7f3a9c21b0d4e5f6', timestamp: null, turns: 3, model: null, prompt: 'Trace the flaky ingest retry' },
        { request: 'span-18c2d9e0a4b7f355', timestamp: null, turns: 2, model: null, prompt: null },
      ],
      data: [
        { request: 'span-7f3a9c21b0d4e5f6', timestamp: null, turns: 3, model: null, prompt: 'Trace the flaky ingest retry' },
        { request: 'span-18c2d9e0a4b7f355', timestamp: null, turns: 2, model: null, prompt: null },
      ],
      total: 2,
      limit: 25,
      offset: 0,
      page: 1,
    }
    stubFetch(() => jsonResponse(page))
    mount()
    await screen.findByTestId('user-requests')
    expect(screen.getByTestId('request-row-1').textContent).toContain('Trace the flaky ingest retry')
    const secondRow = screen.getByTestId('request-row-2')
    expect(secondRow.textContent).toContain('Request 2')
    expect(secondRow.textContent).toMatch(/\(2 turns\)/)
    const banner = screen.getByTestId('user-requests')
    expect(banner.textContent).not.toContain('span-18c2d9e0a4b7f355')
  })

  it('states the empty list honestly', async () => {
    stubFetch(() => jsonResponse({ requests: [], data: [], total: 0, limit: 25, offset: 0, page: 1 }))
    mount()
    const empty = await screen.findByTestId('requests-empty')
    expect(empty.textContent).toMatch(/no user requests/i)
  })

  it('shows no pager while one page holds every request', async () => {
    stubFetch(() =>
      jsonResponse({
        requests: FIRST_PAGE.requests,
        data: FIRST_PAGE.data,
        total: 2,
        limit: 25,
        offset: 0,
        page: 1,
      }),
    )
    mount()
    await screen.findByTestId('user-requests')
    expect(screen.queryByTestId('list-pager')).toBeNull()
  })
})

describe('SessionRequestsSection paging', () => {
  it('shows the shared pager past 25 rows and requests page two only on press', async () => {
    const fetchMock = stubFetch((url: string) => jsonResponse(url.includes('page=2') ? SECOND_PAGE : FIRST_PAGE))
    mount()
    await screen.findByTestId('user-requests')
    const pager = screen.getByTestId('list-pager')
    expect(pager.getAttribute('aria-label')).toBe('User request pages')
    expect(within(pager).getByTestId('pager-page-1').getAttribute('aria-current')).toBe('page')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(false)

    fireEvent.click(within(pager).getByTestId('pager-next'))
    await screen.findByTestId('request-row-26')
    expect(screen.getByTestId('request-row-26').textContent).toContain('Rewrite the parser entry point')
    expect(within(pager).getByTestId('pager-page-2').getAttribute('aria-current')).toBe('page')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(true)
  })

  it('numbers rows by their position in the whole list, not within the page', async () => {
    stubFetch(() => jsonResponse(SECOND_PAGE))
    mount()
    await screen.findByTestId('user-requests')
    expect(screen.getByTestId('request-row-26').textContent).toContain('Rewrite the parser entry point')
  })
})
