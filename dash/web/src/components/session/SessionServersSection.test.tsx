// @vitest-environment happy-dom
// The servers section: one page of the per-server schema and invocation
// rollup the engine computed. The view lays the rows out; it never computes a
// rollup itself (display layer, ADR 0033). A server that was never invoked is
// a measured zero and renders as one.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionServersSection } from './SessionServersSection.js'

const SESSION_ID = 'sess-1'
const ROUTE_PAGE_2 = `/api/kyber/session/${SESSION_ID}/servers?page=2&limit=25&order=desc`

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
      <SessionServersSection sessionId={SESSION_ID} />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  window.sessionStorage.clear()
  vi.unstubAllGlobals()
})

const BUILTIN_ROW = {
  server: 'builtin',
  is_mcp: false,
  tools: 2,
  schema_tokens: 1900,
  total_schema_cost: 9500,
  invocations: 8,
  unused_tools: 0,
  unused_cost: 0,
}

const GIT_ROW = {
  server: 'git',
  is_mcp: true,
  tools: 1,
  schema_tokens: 900,
  total_schema_cost: 4500,
  invocations: 0,
  unused_tools: 1,
  unused_cost: 4500,
}

const FIRST_PAGE = {
  servers: [BUILTIN_ROW, GIT_ROW],
  data: [BUILTIN_ROW, GIT_ROW],
  total: 2,
  limit: 25,
  offset: 0,
  page: 1,
}

describe('SessionServersSection states', () => {
  it('shows its loading state while the page is in flight', () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    const loading = screen.getByTestId('session-servers-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount()
    const error = await screen.findByTestId('section-error-servers')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2)
  })

  it('lists each server with its tool, invocation and unused figures', async () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    const section = await screen.findByTestId('servers-section')
    const builtin = within(section).getByTestId('server-row-builtin')
    expect(builtin.textContent).toContain('builtin')
    expect(builtin.textContent).not.toContain('MCP')
    expect(builtin.textContent).toContain('8')
    const git = within(section).getByTestId('server-row-git')
    expect(git.textContent).toContain('git')
    expect(git.textContent).toContain('MCP')
    expect(git.textContent).toContain('4.5K')
    expect(git.textContent).toContain('1 unused')
  })

  it('states the empty page honestly and hides the pager when total is 0', async () => {
    stubFetch(() => jsonResponse({ servers: [], data: [], total: 0, limit: 25, offset: 0, page: 1 }))
    mount()
    const empty = await screen.findByTestId('servers-empty')
    expect(empty.textContent).toMatch(/no servers recorded/i)
    expect(screen.queryByTestId('list-pager')).toBeNull()
  })

  it('shows limit and order controls on a single non-empty page and hides page buttons', async () => {
    stubFetch(() => jsonResponse(FIRST_PAGE))
    mount()
    await screen.findByTestId('servers-section')
    const pager = screen.getByTestId('list-pager')
    expect(within(pager).getByTestId('pager-limit')).toBeTruthy()
    expect(within(pager).getByTestId('pager-order').textContent).toBe('Newest first')
    expect(within(pager).queryByTestId('pager-prev')).toBeNull()
    expect(within(pager).queryByTestId('pager-next')).toBeNull()
  })
})

describe('SessionServersSection paging', () => {
  it('shows the shared pager past 25 rows and requests page two only on press', async () => {
    const rowTwo = { ...GIT_ROW, server: 'search' }
    const fetchMock = stubFetch((url: string) =>
      jsonResponse(
        url.includes('page=2')
          ? { servers: [rowTwo], data: [rowTwo], total: 30, limit: 25, offset: 25, page: 2 }
          : { servers: FIRST_PAGE.servers, data: FIRST_PAGE.data, total: 30, limit: 25, offset: 0, page: 1 },
      ),
    )
    mount()
    await screen.findByTestId('servers-section')
    const pager = screen.getByTestId('list-pager')
    expect(pager.getAttribute('aria-label')).toBe('Server pages')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(false)

    fireEvent.click(within(pager).getByTestId('pager-next'))
    await screen.findByTestId('server-row-search')
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
      return jsonResponse({ servers: FIRST_PAGE.servers, data: FIRST_PAGE.data, total: 30, limit: 25, offset: 0, page: 1 })
    })
    mount()
    await screen.findByTestId('server-row-builtin')
    expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(false)

    fireEvent.click(screen.getByTestId('pager-next'))
    await waitFor(() => expect(fetchMock.mock.calls.some(([url]) => url === ROUTE_PAGE_2)).toBe(true))
    // The next page is held back: the rows the viewer was reading stay on
    // screen, and the new page's rows are absent until it lands.
    expect(screen.getByTestId('server-row-builtin')).toBeTruthy()
    expect(screen.getByTestId('server-row-git')).toBeTruthy()
    expect(screen.queryByTestId('server-row-search')).toBeNull()

    resolveSecond(
      jsonResponse({
        servers: [{ ...GIT_ROW, server: 'search' }],
        data: [{ ...GIT_ROW, server: 'search' }],
        total: 30,
        limit: 25,
        offset: 25,
        page: 2,
      }),
    )
    await screen.findByTestId('server-row-search')
  })

  it('changes page size (25 -> 50 -> 100), persists per section, toggles sort order, and resets to page 1', async () => {
    const fetchMock = stubFetch((url: string) => {
      const parsed = new URL(url, 'http://localhost')
      const page = Number(parsed.searchParams.get('page') ?? '1')
      const limit = Number(parsed.searchParams.get('limit') ?? '25')
      const order = parsed.searchParams.get('order') ?? 'desc'
      return jsonResponse({
        servers: FIRST_PAGE.servers,
        data: FIRST_PAGE.data,
        total: 120,
        limit,
        offset: (page - 1) * limit,
        page,
        order,
      })
    })
    mount()
    await screen.findByTestId('servers-section')

    fireEvent.click(screen.getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('servers?page=2&limit=25&order=desc'))).toBe(true),
    )

    fireEvent.change(screen.getByTestId('pager-limit'), { target: { value: '50' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('servers?page=1&limit=50&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:servers')).toBe('50')

    fireEvent.change(screen.getByTestId('pager-limit'), { target: { value: '100' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('servers?page=1&limit=100&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:servers')).toBe('100')

    fireEvent.click(screen.getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('servers?page=2&limit=100&order=desc'))).toBe(true),
    )

    fireEvent.click(screen.getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('servers?page=1&limit=100&order=asc'))).toBe(true),
    )
    expect(screen.getByTestId('pager-order').textContent).toBe('Oldest first')

    fireEvent.click(screen.getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('servers?page=1&limit=100&order=desc')).length).toBeGreaterThanOrEqual(2),
    )
  })
})
