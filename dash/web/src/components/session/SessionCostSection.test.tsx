// @vitest-environment happy-dom
// The cost section: the session cost and token accounting panel, fed by the
// summary the loader read. The panel renders the cost blocks the server sent
// and the basis-mismatch verdict the engine already decided; this view never
// computes either (display layer, ADR 0033).
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import type { ReactElement } from 'react'

import { SessionCostSection } from './SessionCostSection.js'
import type { KyberSessionSummarySection } from '../../lib/kyberApi.js'
import type { SessionSummaryPayload } from './types.js'

afterEach(() => {
  cleanup()
})

const RECORD: SessionSummaryPayload = {
  total_input: 120500,
  total_output: 6400,
  total_cache_read: 75000,
  total_cache_creation: 18000,
  cache_hit_ratio: 0.622,
  cost: { usd: 1.25, credits: 6.25, basis: 'published_rates', status: 'ok' },
}

const SUMMARY: KyberSessionSummarySection = {
  session_id: 'sess-abc-123',
  harness: 'copilot',
  is_subagent: false,
  span_count: 48,
  problems: 0,
  problems_count: 0,
  request_count: 2,
  costBasisMismatch: null,
  summary: RECORD as unknown as KyberSessionSummarySection['summary'],
  notes: [],
  reconciliation: { total: 2, mismatched: 0 },
  tools_offered_count: 3,
  tools_invoked_count: 2,
  unused_tools_count: 1,
  skills_count: 2,
}

function mountSection(
  overrides: Partial<Parameters<typeof SessionCostSection>[0]> = {},
): ReturnType<typeof render> {
  const props = { summary: SUMMARY, summaryRecord: RECORD, loading: false, ...overrides }
  const element = (<SessionCostSection {...props} />) as ReactElement
  return render(element)
}

describe('SessionCostSection states', () => {
  it('shows its loading state while the summary is in flight', () => {
    mountSection({ summary: null, summaryRecord: null, loading: true })
    const loading = screen.getByTestId('session-cost-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', () => {
    const onRetry = vi.fn()
    mountSection({ summary: null, summaryRecord: null, loading: false, error: 'Request failed (500)', onRetry })
    const error = screen.getByTestId('section-error-cost')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    expect(onRetry).toHaveBeenCalled()
  })

  it('renders the cost panel with the published block from the summary record', () => {
    mountSection()
    const panel = screen.getByTestId('session-cost-panel')
    expect(panel.textContent).toContain('Session Cost & Token Accounting')
    expect(within(panel).getByTestId('cost-basis-card-published').textContent).toContain('$1.25')
  })

  it('renders the engine-decided basis mismatch verdict verbatim', () => {
    const mismatch = {
      code: 'COST_BASIS_MISMATCH',
      message: 'The harness-reported cost and the published-rate cost disagree.',
      bases: ['harness', 'published'],
      totalsByBasis: { harness: 0.85, published: 1.1 },
    }
    mountSection({ summary: { ...SUMMARY, costBasisMismatch: mismatch } })
    const warning = screen.getByTestId('cost-basis-mismatch-warning')
    expect(warning.textContent).toContain('The harness-reported cost and the published-rate cost disagree.')
  })

  it('renders no mismatch warning when the engine said the bases agree', () => {
    mountSection({ summary: { ...SUMMARY, costBasisMismatch: null } })
    expect(screen.queryByTestId('cost-basis-mismatch-warning')).toBeNull()
  })
})
