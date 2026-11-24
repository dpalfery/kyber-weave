// @vitest-environment happy-dom
// The drawer fetches what it opens (decision D9): an opener hands it a key —
// a turn index, or a turn index plus a bucket name — never a row looked up in
// a session-wide payload. Every request here is held until the test answers
// it, so the ordering "row first, then its content" is observed, not assumed.
import { describe, it, expect, vi, afterEach } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, cleanup, waitFor } from '@testing-library/react'

import { SessionInspectorDrawer, isTurnBucketContentRequest } from './SessionInspectorDrawer.js'

afterEach(cleanup)

const SESSION_ID = 'sess-d9-fetch'

const turnRoute = (turnIndex: number) => `/api/kyber/session/${SESSION_ID}/turn/${turnIndex}`
const turnContentRoute = (turnIndex: number) => `/api/kyber/session/${SESSION_ID}/turn/${turnIndex}/content?full=true`
const contentRoute = (query: string) => `/api/kyber/session/${SESSION_ID}/content?${query}`

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

interface Held {
  resolve: (response: Response) => void
}

/** Every fetch is held back until the test answers it by URL, in issue order. */
function stubHeldFetch() {
  const calls: string[] = []
  const held = new Map<string, Held[]>()
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      calls.push(url)
      return new Promise<Response>((resolve) => {
        const queue = held.get(url) ?? []
        queue.push({ resolve })
        held.set(url, queue)
      })
    }),
  )
  const last = (route: string) => {
    const queue = held.get(route) ?? []
    return queue[queue.length - 1]
  }
  return {
    calls,
    respond(route: string, body: unknown) {
      last(route)?.resolve({ ok: true, status: 200, json: async () => body } as unknown as Response)
    },
    fail(route: string) {
      last(route)?.resolve({ ok: false, status: 500, json: async () => ({}) } as unknown as Response)
    },
  }
}

function renderDrawer(props: {
  contentRequest: Record<string, unknown>
  rawContent?: Record<string, unknown>
}) {
  return render(
    <QueryClientProvider client={queryClient()}>
      <SessionInspectorDrawer
        open={true}
        onClose={() => {}}
        title="Turn 2 · Full context"
        rawContent={props.rawContent}
        contentRequest={props.contentRequest as never}
      />
    </QueryClientProvider>,
  )
}

/** A served per-turn section: the turn row plus that turn's own context (or null). */
function turnSection(turnIndex: number, turn: Record<string, unknown>, context: unknown = null) {
  return {
    sessionId: SESSION_ID,
    turnIndex,
    turn: { index: turnIndex, spanId: `span-t${turnIndex}`, ...turn },
    context,
  }
}

describe('SessionInspectorDrawer: the drawer fetches what it opens (D9)', () => {
  it('recognizes the keyed bucket request and keeps it out of the whole-turn request', () => {
    expect(isTurnBucketContentRequest({ sessionId: SESSION_ID, turnIndex: 0, bucket: 'tool_definitions' })).toBe(true)
    expect(isTurnBucketContentRequest({ sessionId: SESSION_ID, turnIndex: 0 })).toBe(false)
    expect(isTurnBucketContentRequest({ sessionId: SESSION_ID, bucket: 'tool_definitions' })).toBe(false)
    expect(isTurnBucketContentRequest(null)).toBe(false)
    // A bucket request is never also a whole-turn request.
    expect(isTurnBucketContentRequest({ sessionId: SESSION_ID, turnIndex: 0, full: true })).toBe(false)
  })

  it('requests /turn/:index when a whole turn opens, and its content only once the row answers', async () => {
    const router = stubHeldFetch()
    renderDrawer({ contentRequest: { sessionId: SESSION_ID, turnIndex: 1, full: true } })

    await waitFor(() => expect(router.calls).toContain(turnRoute(1)))
    // Content waits for the row: the drawer opened on a key, not on a payload row.
    expect(screen.getByTestId('turn-section-loading')).toBeTruthy()
    expect(router.calls.filter((url) => url === turnContentRoute(1))).toEqual([])

    router.respond(turnRoute(1), turnSection(1, { fresh: 5000, cache_read: 42000, cache_creation: 0 }))
    await waitFor(() => expect(router.calls).toContain(turnContentRoute(1)))
    router.respond(turnContentRoute(1), {
      sessionId: SESSION_ID,
      turnIndex: 1,
      blocks: [],
      parts: [],
      assembledText: '',
    })
    expect(await screen.findByTestId('turn-content-full')).toBeTruthy()
    // The measured totals come from the fetched row, not from an opener payload.
    expect(screen.getByTestId('turn-total-cache-read').textContent).toContain('42.0K')
  })

  it('states the per-turn route failure for a whole turn and requests no content', async () => {
    const router = stubHeldFetch()
    renderDrawer({ contentRequest: { sessionId: SESSION_ID, turnIndex: 2, full: true } })

    await waitFor(() => expect(router.calls).toContain(turnRoute(2)))
    router.fail(turnRoute(2))

    const error = await screen.findByTestId('turn-section-error')
    expect(error.textContent).toContain('Request failed (500)')
    expect(router.calls.filter((url) => url.includes('/turn/2/content'))).toEqual([])
  })

  it('computes a bucket drill-down from the fetched turn context, then requests its content on the turn span', async () => {
    const router = stubHeldFetch()
    renderDrawer({ contentRequest: { sessionId: SESSION_ID, turnIndex: 0, bucket: 'tool_definitions' } })

    await waitFor(() => expect(router.calls).toContain(turnRoute(0)))
    // No content request before the row answers — and no figures yet either.
    expect(screen.getByTestId('turn-section-loading')).toBeTruthy()
    expect(router.calls.filter((url) => url.includes('/content'))).toEqual([])

    // The per-turn route answers with this turn's own context figures.
    router.respond(turnRoute(0), turnSection(0, {}, { index: 1, buckets: { tool_definitions: 321, residual: 44 }, reported_input: 1234 }))

    const inspector = await screen.findByTestId('context-bucket-inspector')
    expect(inspector.textContent).toContain('321')
    expect(inspector.textContent).toContain('1.2K')
    // The content request derives from the fetched row's span.
    await waitFor(() =>
      expect(router.calls).toContain(contentRoute('span=span-t0&part=tool_definitions&full=true')),
    )
  })

  it('keeps the session-level system prompt off any turn span', async () => {
    const router = stubHeldFetch()
    renderDrawer({ contentRequest: { sessionId: SESSION_ID, turnIndex: 0, bucket: 'system_prompt' } })

    await waitFor(() => expect(router.calls).toContain(turnRoute(0)))
    router.respond(turnRoute(0), turnSection(0, {}, { index: 1, buckets: { system_prompt: 4000 }, reported_input: 91000 }))

    expect(await screen.findByTestId('context-bucket-inspector')).toBeTruthy()
    await waitFor(() => expect(router.calls).toContain(contentRoute('part=system_prompt&full=true')))
    expect(router.calls.filter((url) => url.includes('content?span='))).toEqual([])
  })

  it('states the per-turn route failure for a bucket drill-down and requests no content', async () => {
    const router = stubHeldFetch()
    renderDrawer({ contentRequest: { sessionId: SESSION_ID, turnIndex: 3, bucket: 'residual' } })

    await waitFor(() => expect(router.calls).toContain(turnRoute(3)))
    router.fail(turnRoute(3))

    const error = await screen.findByTestId('turn-section-error')
    expect(error.textContent).toContain('Request failed (500)')
    expect(router.calls.filter((url) => url.includes('/content'))).toEqual([])
  })

  it('requests no content when a non-prompt bucket’s turn row carries no span', async () => {
    const router = stubHeldFetch()
    renderDrawer({ contentRequest: { sessionId: SESSION_ID, turnIndex: 4, bucket: 'tool_definitions' } })

    await waitFor(() => expect(router.calls).toContain(turnRoute(4)))
    // A turn row may predate span ids. Without one there is no turn-scoped
    // content route to open, and dropping the empty span would fetch SESSION
    // content under a turn-labelled panel, so the drawer asks for nothing and
    // shows the bucket's own captured content instead.
    router.respond(
      turnRoute(4),
      turnSection(
        4,
        { spanId: undefined, content: { tool_definitions: 'captured tool defs' } },
        { index: 1, buckets: { tool_definitions: 42 }, reported_input: 900 },
      ),
    )

    const inspector = await screen.findByTestId('context-bucket-inspector')
    expect(inspector.textContent).toContain('42')
    expect(inspector.textContent).toContain('captured tool defs')
    // Give a wrong implementation a turn to issue its request, then prove none.
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(router.calls.filter((url) => url.includes('/content'))).toEqual([])
    expect(screen.queryByTestId('drawer-content-loading')).toBeNull()
    expect(screen.queryByTestId('drawer-content-fallback-note')).toBeNull()
  })
})
