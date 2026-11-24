// @vitest-environment happy-dom
// RunDetail resolves its execution's session through the bounded summary
// route (Task T3) — never by downloading the sessions list — and reads the
// turn count off `summary.turn_count`. Turn numbering stays issue #184's
// contract: transport is 0-based, humans read 1-based. Measured figures,
// scorecard honesty (issue #183) and cost honesty stay pinned.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { RunDetail } from './RunDetail.js'
import type { KyberRunDetail } from '../lib/kyberApi.js'

function createTestQueryClient() {
  // staleTime pinned: data a test seeds must render without the mount refetch
  // firing an unstubbed request.
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: Infinity } } })
}

let queryClient: QueryClient

function jsonResponse(payload: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => payload } as unknown as Response
}

/**
 * Stubs the summary route (and records its requests) and fails every other
 * URL, so a test that sees a request the component never should, fails loudly.
 */
function stubSummary(summary: Record<string, unknown>, opts: { status?: number; hold?: Promise<void> } = {}): string[] {
  const requests: string[] = []
  const status = opts.status ?? 200
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string) => {
      const url = new URL(String(input), 'http://localhost')
      requests.push(`${url.pathname}${url.search}`)
      if (/^\/api\/kyber\/session\/[^/]+\/summary$/.test(url.pathname)) {
        if (opts.hold) await opts.hold
        return jsonResponse(status === 200 ? { session_id: 'sess-1', harness: 'copilot', summary } : {}, status)
      }
      throw new Error(`unstubbed fetch: ${input}`)
    }),
  )
  return requests
}

const baseRun: KyberRunDetail = {
  runId: 'run-184',
  harness: 'copilot',
  groupingBasis: 'derived',
  executionTree: [],
  executions: [
    {
      executionId: 'exec-1',
      runId: 'run-184',
      harness: 'copilot',
      isRoot: true,
    },
  ],
  findings: [],
}

beforeEach(() => {
  queryClient = createTestQueryClient()
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

// Issue #184: transport is 0-based `turnIndex` (URL, router, content route) while
// every human-facing turn label is 1-based (`Turn #N`).
describe('RunDetail turns table numbering', () => {
  it('renders a 1-based label for a measured turn row', () => {
    stubSummary({})
    render(
      <QueryClientProvider client={queryClient}>
        <RunDetail
          runId="run-184"
          initialRun={{ ...baseRun, turns: [{ turnIndex: 5, model: 'm', tokens: 1200 }] }}
        />
      </QueryClientProvider>,
    )

    expect(screen.getAllByText('Turn #6').length).toBeGreaterThan(0)
    expect(screen.queryByText('Turn #5')).toBeNull()
  })

  it('keeps the 0-based transport identity on the row drill control', () => {
    stubSummary({})
    render(
      <QueryClientProvider client={queryClient}>
        <RunDetail
          runId="run-184"
          initialRun={{ ...baseRun, turns: [{ turnIndex: 5, model: 'm', tokens: 1200 }] }}
        />
      </QueryClientProvider>,
    )

    // Navigation still carries the raw 0-based index to `/turn/5`.
    expect(screen.getByTestId('turn-row-5')).not.toBeNull()
    expect(screen.getByTestId('drill-turn-5')).not.toBeNull()
  })
})

// Task T3: the execution's session resolves through the bounded summary route.
describe('RunDetail resolves the execution session (Task T3)', () => {
  const runWithSession: KyberRunDetail = {
    ...baseRun,
    executions: [
      {
        executionId: 'exec-1',
        runId: 'run-184',
        sessionId: 'sess-1',
        harness: 'copilot',
        isRoot: true,
      },
    ],
  }

  function renderRun(props: { run?: KyberRunDetail } = {}) {
    return render(
      <QueryClientProvider client={queryClient}>
        <RunDetail runId="run-184" initialRun={props.run ?? runWithSession} />
      </QueryClientProvider>,
    )
  }

  it('resolves through /session/:id/summary and never downloads the sessions list', async () => {
    const requests = stubSummary({ turn_count: 3 })
    renderRun()

    await waitFor(() => expect(requests).toContain('/api/kyber/session/sess-1/summary'))
    expect(requests.some((url) => url.includes('/api/kyber/sessions'))).toBe(false)
  })

  it('shows summary.turn_count as the synthesized turn rows', async () => {
    stubSummary({ turn_count: 3 })
    renderRun()

    await screen.findAllByText('Turn #3')
    expect(screen.getByTestId('turn-row-0')).not.toBeNull()
    expect(screen.getByTestId('drill-turn-2')).not.toBeNull()
    expect(screen.queryByText('Turn #4')).toBeNull()
  })

  it('holds a reading state while the summary is in flight, never a claimed zero', async () => {
    let release!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    stubSummary({ turn_count: 3 }, { hold: gate })
    renderRun()

    // The run is here (initialRun), but the turn count is not yet: the panel
    // says it is reading, it does not say "no turns recorded".
    await waitFor(() => expect(screen.getByTestId('turns-session-loading')).not.toBeNull())
    expect(screen.queryByTestId('turn-row-0')).toBeNull()

    release()
    await screen.findAllByText('Turn #3')
  })

  it('renders the summary read failure as an error state, not an empty table', async () => {
    stubSummary({}, { status: 500 })
    renderRun()

    const failure = await screen.findByTestId('turns-session-error')
    expect(failure.textContent).toContain('Request failed (500)')
    expect(screen.queryByTestId('turn-row-0')).toBeNull()
  })

  it('says nothing about turns when the served summary carries no turn_count', async () => {
    stubSummary({})
    renderRun()

    // turn_count absent is unmeasured, not zero: the empty state stays.
    await waitFor(() => expect(screen.getByTestId('turns-empty').textContent).toMatch(/No turns recorded/))
  })
})

// Issue #183: the turn table must render served measured figures (not dashes
// beside measured data), and the scorecard must render the served run-scoped
// dimensions — with honest absence when the server sent none.
describe('RunDetail measured figures (issue #183)', () => {
  const measuredRun: KyberRunDetail = {
    runId: 'run-183',
    harness: 'copilot',
    groupingBasis: 'explicit',
    turnCount: 2,
    totalInput: 3000,
    costUsd: 0.015,
    executionTree: [],
    executions: [
      {
        executionId: 'exec-1',
        runId: 'run-183',
        harness: 'copilot',
        isRoot: true,
      },
    ],
    findings: [],
    turns: [
      {
        turnIndex: 0,
        executionId: 'exec-1',
        model: 'gpt-4o',
        tokens: 1100,
        inputTokens: 1000,
        outputTokens: 100,
        contextPressure: 0.05,
        cacheHitRatio: 0.2,
        costUsd: 0.01,
      },
      { turnIndex: 1, executionId: 'exec-1' },
    ],
    scorecard: {
      cacheEfficiency: { value: 0.3333, display: '33%' },
      contextHygiene: { value: 0.05, display: '5%' },
    },
  }

  it('renders measured turn figures where the server sent them', () => {
    stubSummary({})
    render(
      <QueryClientProvider client={queryClient}>
        <RunDetail runId="run-183" initialRun={measuredRun} />
      </QueryClientProvider>,
    )

    expect(screen.getAllByText('gpt-4o').length).toBeGreaterThan(0)
    expect(screen.getAllByText('5%').length).toBeGreaterThan(0)
    expect(screen.getAllByText('20%').length).toBeGreaterThan(0)
    expect(screen.getAllByText('$0.010').length).toBeGreaterThan(0)
    // The unmeasured second row keeps its dashes — honesty both ways.
    expect(screen.getAllByText('Turn #2').length).toBeGreaterThan(0)
  })

  it('renders the served run-scoped scorecard as measured', () => {
    stubSummary({})
    render(
      <QueryClientProvider client={queryClient}>
        <RunDetail runId="run-183" initialRun={measuredRun} />
      </QueryClientProvider>,
    )

    expect(screen.getAllByTestId('dimension-value').length).toBeGreaterThan(0)
    expect(screen.getByText('33%')).not.toBeNull()
  })

  it('states honest absence without the harness-telemetry claim when unscored', () => {
    stubSummary({})
    const { scorecard: _omitted, ...unscored } = measuredRun
    render(
      <QueryClientProvider client={queryClient}>
        <RunDetail runId="run-183" initialRun={{ ...unscored, turns: undefined }} />
      </QueryClientProvider>,
    )

    expect(screen.getAllByText(/projection has not scored it yet/).length).toBeGreaterThan(0)
    expect(document.body.textContent).not.toContain('does not export cache')
  })
})

// Review follow-up (Copilot C7, Kilo K3): a priced zero cost is a
// measurement, not a missing figure — and a partial run total says so.
describe('RunDetail cost honesty', () => {
  const pricedZeroRun: KyberRunDetail = {
    runId: 'run-zero',
    harness: 'copilot',
    groupingBasis: 'explicit',
    costUsd: 0,
    executionTree: [],
    executions: [
      {
        executionId: 'exec-1',
        runId: 'run-zero',
        harness: 'copilot',
        isRoot: true,
      },
    ],
    findings: [],
    turns: [{ turnIndex: 0, model: 'gpt-4o', tokens: 100, costUsd: 0 }],
  }

  function renderRun(run: KyberRunDetail) {
    stubSummary({})
    render(
      <QueryClientProvider client={queryClient}>
        <RunDetail runId={run.runId} initialRun={run} />
      </QueryClientProvider>,
    )
  }

  it('renders a priced zero turn cost as $0.00, not a dash', () => {
    renderRun(pricedZeroRun)
    expect(screen.getAllByText('$0.00').length).toBeGreaterThan(0)
  })

  it('marks a partial run total instead of presenting it as complete', () => {
    renderRun({ ...pricedZeroRun, costUsd: 0.015, costStatus: 'partial' })
    expect(screen.getAllByText(/partial/i).length).toBeGreaterThan(0)
  })

  // Final polish (Kilo A): the cost marker is cost-precise — a turns-only
  // gap marks no cost cell.
  it('shows no cost marker when only turns are partial', () => {
    renderRun({
      ...pricedZeroRun,
      costUsd: 0.015,
      partial: true,
      partialFields: ['turnCount'],
    })
    expect(screen.queryByTestId('secondary-cost-partial')).toBeNull()
  })

  it('shows the cost marker when the cost itself is partial', () => {
    renderRun({
      ...pricedZeroRun,
      costUsd: 0.015,
      partial: true,
      partialFields: ['costUsd'],
    })
    expect(screen.getByTestId('secondary-cost-partial')).not.toBeNull()
  })
})

// Review re-review (Kilo 5): the partial marker must be visible text, not a
// hover-only title.
describe('RunDetail partial visibility', () => {
  it('shows a visible partial marker for a partial run total', () => {
    stubSummary({})
    render(
      <QueryClientProvider client={queryClient}>
        <RunDetail
          runId="run-zero"
          initialRun={{
            runId: 'run-zero',
            harness: 'copilot',
            groupingBasis: 'explicit',
            costUsd: 0.015,
            costStatus: 'partial',
            executionTree: [],
            executions: [],
            findings: [],
          }}
        />
      </QueryClientProvider>,
    )

    expect(screen.getByTestId('secondary-cost-partial')).not.toBeNull()
  })
})

// Final polish (Kilo B): a partial turn cost carries its own marker.
describe('RunDetail turn cost partial', () => {
  it('marks a partial turn cost beside the figure', () => {
    stubSummary({})
    render(
      <QueryClientProvider client={queryClient}>
        <RunDetail
          runId="run-zero"
          initialRun={{
            runId: 'run-zero',
            harness: 'copilot',
            groupingBasis: 'explicit',
            executionTree: [],
            executions: [],
            findings: [],
            turns: [{ turnIndex: 0, model: 'gpt-4o', tokens: 100, costUsd: 0.01, costStatus: 'partial' }],
          }}
        />
      </QueryClientProvider>,
    )

    expect(screen.getByTestId('turn-cost-partial-0')).not.toBeNull()
    // The explanation must be reachable without a hovering mouse.
    expect(screen.getByTestId('turn-cost-partial-0').getAttribute('tabindex')).toBe('0')
  })
})
