// @vitest-environment happy-dom
// The summary section: the session view's header strip, banners and overview
// tiles, read from the bounded summary route through the client. Every figure
// is the server's own — an unmeasured value renders as an honest absence,
// never as 0 or a default that looks like data. This suite pins that honesty
// and the tile logic ported from the payload era.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionSummarySection, formatCredits } from './SessionSummarySection.js'
import type { SessionSummaryPayload } from './types.js'

const SESSION_ID = 'sess-abc-123'
const ROUTE = `/api/kyber/session/${SESSION_ID}/summary`

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

function mount(onSelectSession?: (sessionId: string) => void): ReturnType<typeof render> {
  const element = (
    <QueryClientProvider client={queryClient()}>
      <SessionSummarySection sessionId={SESSION_ID} onSelectSession={onSelectSession} />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const BASE_RECORD: SessionSummaryPayload = {
  turn_count: 5,
  reported_turn_count: 5,
  request_count: 2,
  total_input: 120500,
  total_output: 6400,
  total_cache_read: 75000,
  total_cache_creation: 18000,
  cache_creation_coverage: 2,
  cache_hit_ratio: 0.622,
  total_reasoning: 1500,
  duration_ms: 38400,
  models: ['claude-3-5-sonnet'],
  tool_calls: 8,
  tools_invoked: 2,
  tools_offered: 3,
  median_ttft_ms: 540,
  aux_chat_calls: 2,
  aux_models: ['gpt-4o-mini'],
  aux_input: 1200,
  aux_output: 80,
  unused_schema_per_turn: 900,
  schema_tokens_per_turn: 2800,
  defs_turns: 5,
  schema_waste_cost: { usd_low: 0.03, usd_high: 0.12, credits_low: 0.15, credits_high: 0.6 },
  cost: { usd: 1.25, credits: 6.25, basis: 'published_rates', status: 'ok' },
}

function summaryBody(record: Partial<SessionSummaryPayload> = {}): Record<string, unknown> {
  return {
    session_id: SESSION_ID,
    harness: 'copilot',
    label: 'Fix parser AST recursion',
    is_subagent: false,
    parent_session: null,
    agent_name: 'coder-bot',
    repo: 'github.com/my-org/kyber-repo',
    branch: 'feature/ast-fix',
    started: '2026-11-19T10:00:00.000Z',
    ended: '2026-11-19T11:00:00.000Z',
    span_count: 48,
    problems: 0,
    problems_count: 0,
    request_count: 2,
    costBasisMismatch: null,
    summary: { ...BASE_RECORD, ...record },
    notes: ['Harness does not export raw workspace info files.', 'Tokens computed with o200k_base tokenizer.'],
    reconciliation: { total: 2, mismatched: 0 },
    tools_offered_count: 3,
    tools_invoked_count: 2,
    unused_tools_count: 1,
    skills_count: 2,
  }
}

describe('SessionSummarySection header and banners', () => {
  it('renders the harness badge, label, agent, repo and the span count', async () => {
    stubFetch(() => jsonResponse(summaryBody()))
    mount()
    expect((await screen.findByTestId('session-harness-badge')).textContent).toBe('copilot')
    expect(screen.getByTestId('session-label').textContent).toBe('Fix parser AST recursion')
    expect(screen.getByTestId('session-summary-header').textContent).toContain('coder-bot')
    expect(screen.getByTestId('session-summary-header').textContent).toContain('feature/ast-fix')
    expect(screen.getByTestId('session-summary-header').textContent).toContain('48 spans')
  })

  it('falls back to the session id when no label was stored', async () => {
    stubFetch(() => jsonResponse({ ...summaryBody(), label: null }))
    mount()
    expect((await screen.findByTestId('session-label')).textContent).toBe(SESSION_ID)
  })

  it('renders the reconciliation OK badge from the summary counts', async () => {
    stubFetch(() => jsonResponse(summaryBody()))
    mount()
    const badge = await screen.findByTestId('reconciliation-ok')
    expect(badge.textContent).toContain('Reconciliation OK')
    expect(badge.textContent).toContain('across 2 requests')
  })

  it('renders the MISMATCH badge with the served counts when any request mismatched', async () => {
    stubFetch(() => jsonResponse({ ...summaryBody(), reconciliation: { total: 2, mismatched: 1 } }))
    mount()
    const badge = await screen.findByTestId('reconciliation-mismatch')
    expect(badge.textContent).toContain('Reconciliation MISMATCH')
    expect(badge.textContent).toContain('on 1 of 2 request(s)')
  })

  it('renders no reconciliation badge while no request was measurable', async () => {
    stubFetch(() => jsonResponse({ ...summaryBody(), reconciliation: { total: 0, mismatched: 0 } }))
    mount()
    await screen.findByTestId('session-harness-badge')
    expect(screen.queryByTestId('reconciliation-ok')).toBeNull()
    expect(screen.queryByTestId('reconciliation-mismatch')).toBeNull()
  })

  it('renders the harness notes banner verbatim', async () => {
    stubFetch(() => jsonResponse(summaryBody()))
    mount()
    const notes = await screen.findByTestId('harness-notes')
    expect(notes.textContent).toContain('What copilot does not export:')
    expect(notes.textContent).toContain('Harness does not export raw workspace info files.')
  })

  it('renders the auxiliary calls banner from the served figures', async () => {
    stubFetch(() => jsonResponse(summaryBody()))
    mount()
    const banner = await screen.findByTestId('aux-calls-banner')
    expect(banner.textContent).toContain('2 auxiliary gpt-4o-mini call(s)')
    expect(banner.textContent).toContain('1.2K in / 80 out')
  })

  it('renders the subagent notice with the parent session link and reports the selection', async () => {
    const onSelectSession = vi.fn()
    stubFetch(() =>
      jsonResponse({
        ...summaryBody(),
        is_subagent: true,
        parent_session: 'parent-sess-888888',
        agent_name: 'subagent-worker',
      }),
    )
    mount(onSelectSession)
    const notice = await screen.findByTestId('subagent-notice')
    expect(notice.textContent).toContain('Subagent session')
    expect(notice.textContent).toContain('subagent-worker')
    fireEvent.click(screen.getByTestId('parent-session-link'))
    expect(onSelectSession).toHaveBeenCalledWith('parent-sess-888888')
  })
})

describe('SessionSummarySection overview tiles', () => {
  it('renders every measured figure from the summary record and the section counts', async () => {
    stubFetch(() => jsonResponse(summaryBody()))
    mount()
    const strip = await screen.findByTestId('overview-strip-section')
    const text = strip.textContent ?? ''
    expect(text).toContain('120.5K')
    expect(text).toContain('75.0K from cache')
    expect(text).toContain('62.2% hit ratio')
    expect(text).toContain('on 2 turns')
    expect(text).toContain('$1.25')
    expect(text).toContain('38.4s')
    expect(text).toContain('48')
    expect(text).toContain('5')
    expect(text).toContain('2')
    expect(text).toContain('8')
    expect(text).toContain('3')
    expect(text).toContain('1 never called')
  })

  it('marks an unreported tool-call counter unavailable instead of rendering zero', async () => {
    stubFetch(() => jsonResponse(summaryBody({ tool_calls: undefined, tools_invoked: undefined })))
    mount()
    const tile = await screen.findByTestId('metric-tool-calls')
    expect(tile.getAttribute('data-measured')).toBe('false')
    expect(tile.textContent).toContain('—')
    expect(tile.textContent).not.toMatch(/>0/)
    expect(tile.textContent).toContain('Tool invocation count was not reported by copilot.')
  })

  it('preserves a recorded zero tool-call counter as measured', async () => {
    stubFetch(() => jsonResponse(summaryBody({ tool_calls: 0 })))
    mount()
    const tile = await screen.findByTestId('metric-tool-calls')
    expect(tile.getAttribute('data-measured')).toBe('true')
    expect(tile.textContent).toContain('Calls0')
  })

  it('counts distinct invoked tools from the served invocation list', async () => {
    stubFetch(() => jsonResponse(summaryBody({ tool_calls: 5, tools_invoked: ['a', 'b', 'c'] })))
    mount()
    const tile = await screen.findByTestId('metric-tool-calls')
    expect(tile.textContent).toContain('3 distinct invoked')
  })

  it('renders an unreported turn count as absent, not as zero', async () => {
    stubFetch(() => jsonResponse(summaryBody({ turn_count: null })))
    mount()
    const strip = await screen.findByTestId('overview-strip-section')
    expect(strip.textContent).toContain('Turns')
    const turnsTile = within(strip).getAllByText('Turns', { exact: true })[0]!
    const value = turnsTile.nextElementSibling ?? turnsTile.parentElement
    expect((value?.textContent ?? '')).not.toMatch(/^\s*0\s*$/)
  })

  it('never prints "on 0 turns" when cache creation coverage was not reported', async () => {
    stubFetch(() => jsonResponse(summaryBody({ total_cache_creation: 10800000, cache_creation_coverage: undefined })))
    mount()
    const strip = await screen.findByTestId('overview-strip-section')
    expect(strip.textContent).toContain('Cache Creation')
    expect(strip.textContent).not.toContain('on 0 turns')
  })

  it.each(['harness', 'harness_reported'])('labels cost basis %s as reported by the harness', async (basis) => {
    stubFetch(() => jsonResponse(summaryBody({ cost: { usd: 0.85, basis, status: 'ok' }, cost_reported: undefined })))
    mount()
    const tile = await screen.findByTestId('metric-cost')
    expect(tile.textContent).toContain('$0.85')
    expect(tile.textContent).toContain('reported')
    expect(tile.textContent).not.toContain('calculated from published rates')
  })

  it('shows a harness-reported zero instead of an unpriced published block', async () => {
    stubFetch(() =>
      jsonResponse(
        summaryBody({
          cost: { basis: 'published', status: 'out_of_scope' },
          cost_reported: { basis: 'harness', status: 'priced', value: 0, currency: 'USD' },
        }),
      ),
    )
    mount()
    const tile = await screen.findByTestId('metric-cost')
    expect(tile.textContent).toContain('$0.00')
    expect(tile.textContent).toContain('reported')
    expect(tile.textContent).not.toContain('calculated from published rates')
  })

  it('labels a published cost basis as calculated from published rates', async () => {
    stubFetch(() => jsonResponse(summaryBody({ cost: { usd: 0.85, basis: 'published', status: 'ok' }, cost_reported: undefined })))
    mount()
    const tile = await screen.findByTestId('metric-cost')
    expect(tile.textContent).toContain('$0.85')
    expect(tile.textContent).toContain('calculated from published rates')
    expect(tile.textContent).not.toContain('reported by harness')
    expect(tile.textContent).not.toMatch(/estimated/i)
  })
})

describe('SessionSummarySection states', () => {
  it('shows its loading skeleton while the summary is in flight', () => {
    stubFetch(() => jsonResponse(summaryBody()))
    mount()
    const loading = screen.getByTestId('session-summary-loading')
    expect(loading.getAttribute('role')).toBe('status')
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount()
    const error = await screen.findByTestId('session-summary-error')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2)
    expect(fetchMock.mock.calls[0][0]).toBe(ROUTE)
  })

  it('states a faulted projection as the section error, never as zero tiles', async () => {
    stubFetch(() => jsonResponse({ ...summaryBody(), projection_status: 'PROJECTION_FAILED' }))
    mount()
    const error = await screen.findByTestId('session-summary-error')
    expect(error.textContent).toContain('PROJECTION_FAILED')
    expect(screen.queryByTestId('overview-strip-section')).toBeNull()
    expect(screen.queryByTestId('session-summary-header')).toBeNull()
  })
})

describe('formatCredits', () => {
  it('formats a credit figure and refuses an absent one', () => {
    expect(formatCredits(null)).toBe('—')
    expect(formatCredits(undefined)).toBe('—')
    expect(formatCredits(12.345)).toBe('12.35')
    expect(formatCredits(150)).toBe('150')
  })
})
