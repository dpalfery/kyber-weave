// @vitest-environment happy-dom
// The charts section: the session's turns and context composition, read from
// the bounded charts route and rendered through the existing spend charts. A
// turn band opens the whole-turn inspector; a context bucket opens its bucket
// drill-down. Both content requests are the section's own to compute.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionChartsSection } from './SessionChartsSection.js'
import type { OpenInspectorRequest } from './types.js'

const SESSION_ID = 'sess-abc-123'

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function jsonResponse(body: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => body } as unknown as Response
}

function stubFetch(handler: () => Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(() => Promise.resolve(handler()))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function mount(openInspector: (request: OpenInspectorRequest) => void): ReturnType<typeof render> {
  const element = (
    <QueryClientProvider client={queryClient()}>
      <SessionChartsSection sessionId={SESSION_ID} openInspector={openInspector} />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const CHARTS_BODY = {
  sessionId: SESSION_ID,
  turns: [
    {
      index: 0,
      spanId: 'turn-span-1',
      model: 'claude-3-5-sonnet',
      durationMs: 4200,
      fresh: 27500,
      cache_read: 0,
      cache_creation: 18000,
      output: 1200,
      has_tool_defs: true,
      content: {
        tool_definitions: [{ name: 'read_file', description: 'Read file contents', parameters: { path: { type: 'string' } } }],
      },
    },
    {
      index: 1,
      spanId: 'turn-span-2',
      model: 'claude-3-5-sonnet',
      durationMs: 5100,
      fresh: 5000,
      cache_read: 42000,
      cache_creation: 0,
      output: 1800,
    },
  ],
  context: {
    measurable: true,
    contextLimit: 200000,
    turns: [
      {
        turn: 1,
        reported_input: 45500,
        buckets: {
          system_prompt: 4000,
          tool_definitions: 2800,
          instruction_context: 7200,
          conversation_history: 12000,
          tool_result_content: 18000,
          residual: 1500,
        },
      },
    ],
    first: { turn: 1, buckets: { system_prompt: 4000 }, reported_input: 45500 },
    last: { turn: 2, buckets: { system_prompt: 4000 }, reported_input: 91000 },
    unmeasuredTurns: 0,
  },
}

describe('SessionChartsSection states', () => {
  it('shows its loading state while the section is in flight', () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    mount(() => {})
    const loading = screen.getByTestId('session-charts-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount(() => {})
    const error = await screen.findByTestId('section-error-charts')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2)
  })

  it('renders the spend charts from the section turns and context', async () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    mount(() => {})
    const section = await screen.findByTestId('spend-composition-section')
    expect(within(section).getByTestId('session-spend-charts')).toBeTruthy()
    expect(within(section).getByTestId('turn-spend-chart')).toBeTruthy()
    expect(within(section).getByTestId('turn-bar-0')).toBeTruthy()
    expect(within(section).getByTestId('turn-bar-1')).toBeTruthy()
    expect(within(section).getByTestId('context-composition-chart')).toBeTruthy()
  })

  it('renders the honest not-measurable context banner when the engine sent one', async () => {
    const reason = 'The harness records tool invocations, not a measurable context composition.'
    stubFetch(() =>
      jsonResponse({ ...CHARTS_BODY, context: { measurable: false, reason } }),
    )
    mount(() => {})
    const section = await screen.findByTestId('spend-composition-section')
    const banner = within(section).getByTestId('context-composition-not-measurable')
    expect(banner.textContent).toContain(reason)
  })
})

describe('SessionChartsSection inspector wiring', () => {
  it('opens the whole-turn lossless request when a turn band is clicked', async () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('spend-composition-section')
    fireEvent.click(within(section).getByTestId('turn-bar-0'))
    expect(openInspector).toHaveBeenCalledTimes(1)
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Turn 1 · Full context')
    expect(request.contentRequest).toEqual({ sessionId: SESSION_ID, turnIndex: 0, full: true })
    // D9: the opener passes the turn's key only — no row rides the payload.
    expect(request.content).toEqual({ turnIndex: 0 })
  })

  it('opens the keyed bucket request when a context segment is clicked', async () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('spend-composition-section')
    fireEvent.click(within(section).getByTestId('context-segment-tool_definitions'))
    expect(openInspector).toHaveBeenCalledTimes(1)
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Turn 1 · tool_definitions')
    // D9: the drill-down carries the key pair; the drawer fetches the turn
    // (widened with its context) and derives the span-scoped content request.
    expect(request.contentRequest).toEqual({
      sessionId: SESSION_ID,
      turnIndex: 0,
      bucket: 'tool_definitions',
    })
    expect(request.content).toEqual({ turnIndex: 0, bucket: 'tool_definitions' })
  })

  it('keys the system-prompt bucket to its turn, for the drawer to keep off any span', async () => {
    stubFetch(() => jsonResponse(CHARTS_BODY))
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('spend-composition-section')
    fireEvent.click(within(section).getByTestId('context-segment-system_prompt'))
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Turn 1 · system_prompt')
    expect(request.contentRequest).toEqual({ sessionId: SESSION_ID, turnIndex: 0, bucket: 'system_prompt' })
  })
})
