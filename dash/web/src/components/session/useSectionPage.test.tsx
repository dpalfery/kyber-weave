// @vitest-environment happy-dom
// The shared section-page hook. Every paged section reads through it, so the
// query key, the previous-page hold and the session change are pinned here
// once: the key carries the session, the section and the page; the previous
// page stays defined while the next one is in flight; a refetch re-runs the
// query; and opening another session restarts the section at its first page.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement, ReactNode } from 'react'

import type { KyberSessionSection } from '../../lib/kyberApi.js'
import type { KyberSectionPageLimit, KyberSectionPageOrder } from './types.js'
import { useSectionPage } from './useSectionPage.js'

const SESSION = 'sess-1'

interface Row {
  request: string
}

interface Page {
  entries: Row[]
  data: Row[]
  total: number
  limit: number
  offset: number
  page: number
  order?: KyberSectionPageOrder
}

function pageOf(
  sessionId: string,
  page: number,
  limit: KyberSectionPageLimit = 25,
  order: KyberSectionPageOrder = 'desc',
): Page {
  const rows: Row[] = [
    { request: `${sessionId}-r${(page - 1) * 2 + 1}` },
    { request: `${sessionId}-r${(page - 1) * 2 + 2}` },
  ]
  return { entries: rows, data: rows, total: 128, limit, offset: (page - 1) * limit, page, order }
}

function queryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function makeWrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }): ReactElement {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>
  }
}

type HookProps = { sessionId: string; section?: KyberSessionSection }

function mountHook(
  fetchPage: (
    sessionId: string,
    page: number,
    limit: KyberSectionPageLimit,
    order: KyberSectionPageOrder,
  ) => Promise<Page>,
  initialProps: HookProps,
) {
  const client = queryClient()
  const mounted = renderHook(
    ({ sessionId, section = 'requests' }: HookProps) => useSectionPage(sessionId, section, fetchPage),
    {
      initialProps,
      wrapper: makeWrapper(client),
    },
  )
  return { ...mounted, client }
}

afterEach(() => {
  cleanup()
  window.sessionStorage.clear()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('useSectionPage', () => {
  it('caches the section under a key that carries the session, section, page, limit and order', async () => {
    const fetchPage = vi.fn(
      async (sessionId: string, page: number, limit: KyberSectionPageLimit, order: KyberSectionPageOrder) =>
        pageOf(sessionId, page, limit, order),
    )
    const { result, client } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    expect(fetchPage).toHaveBeenCalledWith(SESSION, 1, 25, 'desc')
    expect(result.current.page).toBe(1)
    expect(result.current.limit).toBe(25)
    expect(result.current.order).toBe('desc')
    expect(result.current.pagerProps).toMatchObject({
      page: 1,
      limit: 25,
      total: 128,
      limitOptions: [25, 50, 100],
      order: 'desc',
    })
    expect(typeof result.current.pagerProps.onLimitChange).toBe('function')
    expect(typeof result.current.pagerProps.onOrderChange).toBe('function')
    expect(client.getQueryData(['kyber-session', SESSION, 'requests', 1, 25, 'desc'])).toEqual(
      pageOf(SESSION, 1, 25, 'desc'),
    )
  })

  it('requests each page under its own key', async () => {
    const fetchPage = vi.fn(
      async (sessionId: string, page: number, limit: KyberSectionPageLimit, order: KyberSectionPageOrder) =>
        pageOf(sessionId, page, limit, order),
    )
    const { result, client } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    act(() => result.current.setPage(3))
    await waitFor(() => expect(result.current.page).toBe(3))
    await waitFor(() => expect(result.current.data?.offset).toBe(50))
    expect(fetchPage).toHaveBeenLastCalledWith(SESSION, 3, 25, 'desc')
    expect(client.getQueryData(['kyber-session', SESSION, 'requests', 3, 25, 'desc'])).toEqual(
      pageOf(SESSION, 3, 25, 'desc'),
    )
  })

  it('persists page size per section in sessionStorage and isolates sections', async () => {
    window.sessionStorage.setItem('kyberdash:session-section:limit:tools', '100')
    const fetchRequests = vi.fn(
      async (sessionId: string, page: number, limit: KyberSectionPageLimit, order: KyberSectionPageOrder) =>
        pageOf(sessionId, page, limit, order),
    )
    const fetchTools = vi.fn(
      async (sessionId: string, page: number, limit: KyberSectionPageLimit, order: KyberSectionPageOrder) =>
        pageOf(sessionId, page, limit, order),
    )

    const requestsHook = mountHook(fetchRequests, { sessionId: SESSION, section: 'requests' })
    const toolsHook = mountHook(fetchTools, { sessionId: SESSION, section: 'tools' })

    await waitFor(() => expect(requestsHook.result.current.data).toBeDefined())
    await waitFor(() => expect(toolsHook.result.current.data).toBeDefined())

    expect(requestsHook.result.current.limit).toBe(25)
    expect(toolsHook.result.current.limit).toBe(100)
    expect(fetchTools).toHaveBeenCalledWith(SESSION, 1, 100, 'desc')

    act(() => requestsHook.result.current.setLimit(50))
    await waitFor(() => expect(requestsHook.result.current.limit).toBe(50))
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:requests')).toBe('50')
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:tools')).toBe('100')
  })

  it('falls back to 25 when sessionStorage has an invalid value or throws', async () => {
    window.sessionStorage.setItem('kyberdash:session-section:limit:requests', '30')
    const fetchPage = vi.fn(
      async (sessionId: string, page: number, limit: KyberSectionPageLimit, order: KyberSectionPageOrder) =>
        pageOf(sessionId, page, limit, order),
    )
    const badSizeHook = mountHook(fetchPage, { sessionId: SESSION, section: 'requests' })
    await waitFor(() => expect(badSizeHook.result.current.data).toBeDefined())
    expect(badSizeHook.result.current.limit).toBe(25)

    cleanup()
    window.sessionStorage.setItem('kyberdash:session-section:limit:requests', 'not-a-number')
    const nonNumericHook = mountHook(fetchPage, { sessionId: SESSION, section: 'requests' })
    await waitFor(() => expect(nonNumericHook.result.current.data).toBeDefined())
    expect(nonNumericHook.result.current.limit).toBe(25)

    cleanup()
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('storage blocked')
    })
    const throwingHook = mountHook(fetchPage, { sessionId: SESSION, section: 'requests' })
    await waitFor(() => expect(throwingHook.result.current.data).toBeDefined())
    expect(throwingHook.result.current.limit).toBe(25)
  })

  it('resets page to 1 when limit or order changes and updates query key', async () => {
    const fetchPage = vi.fn(
      async (sessionId: string, page: number, limit: KyberSectionPageLimit, order: KyberSectionPageOrder) =>
        pageOf(sessionId, page, limit, order),
    )
    const { result, client } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    act(() => result.current.setPage(3))
    await waitFor(() => expect(result.current.page).toBe(3))

    act(() => result.current.setLimit(50))
    expect(result.current.page).toBe(1)
    expect(result.current.limit).toBe(50)
    await waitFor(() => expect(fetchPage).toHaveBeenLastCalledWith(SESSION, 1, 50, 'desc'))
    expect(client.getQueryData(['kyber-session', SESSION, 'requests', 1, 50, 'desc'])).toEqual(
      pageOf(SESSION, 1, 50, 'desc'),
    )

    act(() => result.current.setPage(2))
    await waitFor(() => expect(result.current.page).toBe(2))

    act(() => result.current.setOrder('asc'))
    expect(result.current.page).toBe(1)
    expect(result.current.order).toBe('asc')
    await waitFor(() => expect(fetchPage).toHaveBeenLastCalledWith(SESSION, 1, 50, 'asc'))
    expect(client.getQueryData(['kyber-session', SESSION, 'requests', 1, 50, 'asc'])).toEqual(
      pageOf(SESSION, 1, 50, 'asc'),
    )
  })

  it("keeps the previous page's data defined while the next page is in flight", async () => {
    let resolveSecond: (value: Page) => void = () => {}
    const fetchPage = vi.fn((sessionId: string, page: number) => {
      if (page === 2) {
        return new Promise<Page>((resolve) => {
          resolveSecond = resolve
        })
      }
      return Promise.resolve(pageOf(sessionId, page))
    })
    const { result } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    const firstPage = result.current.data

    act(() => result.current.setPage(2))
    // The next page is held back in flight: the previous page stays defined,
    // and the section reports that it is fetching.
    expect(result.current.isFetching).toBe(true)
    expect(result.current.data).toEqual(firstPage)

    act(() => resolveSecond(pageOf(SESSION, 2)))
    await waitFor(() => expect(result.current.data?.offset).toBe(25))
    expect(result.current.isFetching).toBe(false)
    expect(result.current.page).toBe(2)
  })

  it('reports a failed page turn instead of holding the previous page silently', async () => {
    const fetchPage = vi.fn(async (sessionId: string, page: number) => {
      if (page === 2) throw new Error('page 2 failed')
      return pageOf(sessionId, page)
    })
    const { result } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    act(() => result.current.setPage(2))
    // The failed page is an error the section shows, never page 1's rows
    // standing in for page 2 with no word of the failure.
    await waitFor(() => expect(result.current.isError).toBe(true))
    expect(result.current.failure).toBe('page 2 failed')
    expect(result.current.data).toBeUndefined()
  })

  it('refetch re-runs the query for the page the viewer is on', async () => {
    const fetchPage = vi.fn(async (sessionId: string, page: number) => pageOf(sessionId, page))
    const { result } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    const calls = fetchPage.mock.calls.length
    await act(async () => {
      result.current.refetch()
    })
    await waitFor(() => expect(fetchPage.mock.calls.length).toBeGreaterThan(calls))
    expect(fetchPage).toHaveBeenLastCalledWith(SESSION, 1, 25, 'desc')
  })

  it('restarts at page 1 when the viewer opens another session', async () => {
    const fetchPage = vi.fn(async (sessionId: string, page: number) => pageOf(sessionId, page))
    const { result, rerender } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    act(() => result.current.setPage(2))
    await waitFor(() => expect(fetchPage).toHaveBeenCalledWith(SESSION, 2, 25, 'desc'))

    rerender({ sessionId: 'sess-2' })
    // The new session is read from its first page — page 2 of the previous
    // session is not where a viewer opening it is.
    expect(result.current.page).toBe(1)
    await waitFor(() => expect(fetchPage).toHaveBeenCalledWith('sess-2', 1, 25, 'desc'))
    expect(fetchPage.mock.calls.some(([sessionId, page]) => sessionId === 'sess-2' && page === 2)).toBe(false)
  })
})
