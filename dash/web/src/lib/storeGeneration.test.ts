// @vitest-environment happy-dom
// Issue #319 T13 (RED, architecture rule R1): the web dashboard never schedules
// anything — the JobHost does. But a clean, a manual import, or a `kyberdash
// refresh` from another surface all change the store underneath this tab, and a
// tab that never re-reads shows stale counts with no indication why.
//
// The contract this file pins is the display layer's only concession to time:
// a hook that polls `GET /api/kyber/jobs` roughly every 10 seconds and, when
// the reported `storeGeneration` differs from the previous poll, invalidates the
// react-query cache so the visible numbers are re-fetched. A poll that returns
// the same generation must NOT invalidate — otherwise every dashboard would
// refetch the world ten times a minute for nothing.
//
// API shape chosen here (the module and export do not exist yet; the name is
// recorded so the implementation lands on one contract, not several):
//   dash/web/src/lib/storeGeneration.ts → export function useStoreGeneration(): void
// It reads the generation through the same kyberApi client as every other read
// (never raw `fetch` from a component) and takes no arguments — callers just
// mount it. Timers are faked; no test here may reach a real server.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, cleanup, waitFor } from '@testing-library/react'
import { act, createElement } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { useStoreGeneration } from './storeGeneration.js'

const POLL_MS = 10_000

/**
 * Renders nothing but the hook; the cache spy is the observable. Built with
 * `createElement` rather than JSX because the module under test is `.ts` — a
 * hook and its probe need no template syntax to be exercised.
 */
function StoreGenerationProbe() {
  useStoreGeneration()
  return createElement('span', { 'data-testid': 'probe' })
}

function mountProbe(queryClient: QueryClient) {
  return renderDom(
    createElement(
      QueryClientProvider,
      { client: queryClient },
      createElement(StoreGenerationProbe),
    ),
  )
}

function jobsBody(generation: number, state = 'idle'): unknown {
  return {
    refresh: { state, lastSuccessAt: '2026-10-09T10:00:00.000Z', lastFailure: null, nextDueAt: null },
    paused: false,
    storeGeneration: generation,
    hostedElsewhere: false,
  }
}

/**
 * Answers GET /jobs from the live `generation` binding so a poll can change it.
 * The body is snapshotted at call time, not inside `json()`: a lazy read would
 * let the baseline poll observe the generation a later poll had already
 * changed, which would make a correct implementation look broken.
 */
function stubJobsFetch(state: { generation: number; status?: number }) {
  const calls: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      const status = state.status ?? 200
      const body = jobsBody(state.generation)
      calls.push(String(url))
      return {
        ok: status >= 200 && status < 300,
        status,
        json: async () => body,
      } as Response
    }),
  )
  return calls
}

function mount(): { queryClient: QueryClient; invalidate: ReturnType<typeof vi.spyOn> } {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
  mountProbe(queryClient)
  return { queryClient, invalidate }
}

async function tick(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms)
  })
}

beforeEach(() => {
  // shouldAdvanceTime keeps microtasks (the fetch promises) flowing while the
  // clock is under test control, so the poll itself is what moves time.
  vi.useFakeTimers({ shouldAdvanceTime: true })
})

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('useStoreGeneration', () => {
  it('reads the job status once on mount and then about every ten seconds', async () => {
    const calls = stubJobsFetch({ generation: 7 })
    mount()
    await waitFor(() => expect(calls.length).toBe(1))
    expect(calls[0]).toContain('/api/kyber/jobs')

    await tick(POLL_MS)
    await waitFor(() => expect(calls.length).toBe(2))
    await tick(POLL_MS)
    await waitFor(() => expect(calls.length).toBe(3))

    // Only the job status is polled — a poll must not fan out into the data
    // endpoints, or the cache churn it is meant to avoid would come back.
    expect(calls.every((url) => url.includes('/api/kyber/jobs'))).toBe(true)
  })

  it('invalidates the cache when the generation changes', async () => {
    const state: { generation: number; status?: number } = { generation: 7 }
    const calls = stubJobsFetch(state)
    const { invalidate } = mount()
    // WHY wait for the call, not for the absence of invalidation: the baseline
    // poll must be observed before it can be the baseline. `not.toHaveBeenCalled()`
    // is true on the very first check, so waiting on it proves nothing.
    await waitFor(() => expect(calls.length).toBe(1))
    // Flush the resolved baseline so the hook has recorded generation 7 before
    // the state moves on.
    await tick(0)
    expect(invalidate).not.toHaveBeenCalled()

    state.generation = 8
    await tick(POLL_MS)
    await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(1))

    // A stable generation then stays quiet: no repeat invalidation per poll.
    await tick(POLL_MS * 2)
    expect(invalidate).toHaveBeenCalledTimes(1)
  })

  it('does not invalidate when the generation is unchanged', async () => {
    const calls = stubJobsFetch({ generation: 7 })
    const { invalidate } = mount()
    await waitFor(() => expect(calls.length).toBe(1))
    await tick(0)

    await tick(POLL_MS * 3)
    expect(invalidate).not.toHaveBeenCalled()
  })

  it('keeps the last observed generation across a failed poll', async () => {
    const state: { generation: number; status?: number } = { generation: 7 }
    const calls = stubJobsFetch(state)
    const { invalidate } = mount()
    // The first poll has to succeed: "keeps the last observed generation" is
    // only meaningful when something was observed before the failure.
    await waitFor(() => expect(calls.length).toBe(1))
    await tick(0)
    expect(invalidate).not.toHaveBeenCalled()

    // A read that failed reports nothing; it must not read as "generation 0",
    // which would look like a wipe and invalidate every query on the page.
    state.status = 500
    await tick(POLL_MS)
    await waitFor(() => expect(calls.length).toBe(2))
    expect(invalidate).not.toHaveBeenCalled()

    // And the next successful poll that reports the same 7 is still quiet.
    state.status = 200
    await tick(POLL_MS)
    await waitFor(() => expect(calls.length).toBe(3))
    expect(invalidate).not.toHaveBeenCalled()

    // A real change after the gap still invalidates, exactly once.
    state.generation = 8
    await tick(POLL_MS)
    await waitFor(() => expect(invalidate).toHaveBeenCalledTimes(1))
  })

  it('stops polling once unmounted', async () => {
    const calls = stubJobsFetch({ generation: 7 })
    const { unmount } = mountProbe(
      new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } }),
    )
    await waitFor(() => expect(calls.length).toBe(1))
    await tick(0)

    unmount()
    await tick(POLL_MS * 4)
    expect(calls).toHaveLength(1)
  })
})