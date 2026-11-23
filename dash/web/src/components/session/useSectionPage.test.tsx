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
}

function pageOf(sessionId: string, page: number): Page {
  const rows: Row[] = [
    { request: `${sessionId}-r${(page - 1) * 2 + 1}` },
    { request: `${sessionId}-r${(page - 1) * 2 + 2}` },
  ]
  return { entries: rows, data: rows, total: 128, limit: 25, offset: (page - 1) * 25, page }
}

function queryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function makeWrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }): ReactElement {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>
  }
}

type HookProps = { sessionId: string }

function mountHook(fetchPage: (sessionId: string, page: number) => Promise<Page>, initialProps: HookProps) {
  const client = queryClient()
  const mounted = renderHook(({ sessionId }: HookProps) => useSectionPage(sessionId, 'requests', fetchPage), {
    initialProps,
    wrapper: makeWrapper(client),
  })
  return { ...mounted, client }
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('useSectionPage', () => {
  it('caches the section under a key that carries the session, the section and the page', async () => {
    const fetchPage = vi.fn(async (sessionId: string, page: number) => pageOf(sessionId, page))
    const { result, client } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    expect(fetchPage).toHaveBeenCalledWith(SESSION, 1)
    expect(result.current.page).toBe(1)
    expect(client.getQueryData(['kyber-session', SESSION, 'requests', 1])).toEqual(pageOf(SESSION, 1))
  })

  it('requests each page under its own key', async () => {
    const fetchPage = vi.fn(async (sessionId: string, page: number) => pageOf(sessionId, page))
    const { result, client } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    act(() => result.current.setPage(3))
    await waitFor(() => expect(result.current.page).toBe(3))
    await waitFor(() => expect(result.current.data?.offset).toBe(50))
    expect(fetchPage).toHaveBeenLastCalledWith(SESSION, 3)
    expect(client.getQueryData(['kyber-session', SESSION, 'requests', 3])).toEqual(pageOf(SESSION, 3))
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

  it('refetch re-runs the query for the page the viewer is on', async () => {
    const fetchPage = vi.fn(async (sessionId: string, page: number) => pageOf(sessionId, page))
    const { result } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    const calls = fetchPage.mock.calls.length
    await act(async () => {
      result.current.refetch()
    })
    await waitFor(() => expect(fetchPage.mock.calls.length).toBeGreaterThan(calls))
    expect(fetchPage).toHaveBeenLastCalledWith(SESSION, 1)
  })

  it('restarts at page 1 when the viewer opens another session', async () => {
    const fetchPage = vi.fn(async (sessionId: string, page: number) => pageOf(sessionId, page))
    const { result, rerender } = mountHook(fetchPage, { sessionId: SESSION })

    await waitFor(() => expect(result.current.data).toBeDefined())
    act(() => result.current.setPage(2))
    await waitFor(() => expect(fetchPage).toHaveBeenCalledWith(SESSION, 2))

    rerender({ sessionId: 'sess-2' })
    // The new session is read from its first page — page 2 of the previous
    // session is not where a viewer opening it is.
    expect(result.current.page).toBe(1)
    await waitFor(() => expect(fetchPage).toHaveBeenCalledWith('sess-2', 1))
    expect(fetchPage.mock.calls.some(([sessionId, page]) => sessionId === 'sess-2' && page === 2)).toBe(false)
  })
})
