// @vitest-environment happy-dom
// Issue #190: Compare must not silently pick runs, must label inventory rows, and must
// not fabricate zero turns/tokens/history when measurements are absent.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import {
  render as renderDom,
  screen,
  fireEvent,
  cleanup,
  waitFor,
} from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type * as React from 'react'

import { CompareRuns, type RunCandidate } from './CompareRuns.js'
import type { KyberRunComparison, KyberRunSummary } from '../lib/kyberApi.js'

vi.mock('../lib/kyberApi.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../lib/kyberApi.js')>()
  return {
    ...actual,
    fetchRuns: vi.fn(),
    fetchRunComparison: vi.fn(),
  }
})

import { fetchRuns, fetchRunComparison } from '../lib/kyberApi.js'

const mockedFetchRuns = vi.mocked(fetchRuns)
const mockedFetchComparison = vi.mocked(fetchRunComparison)

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
    },
  })
}

function renderCompare(ui: React.ReactElement, client = createTestQueryClient()) {
  return renderDom(<QueryClientProvider client={client}>{ui}</QueryClientProvider>)
}

function runSummary(overrides: Partial<KyberRunSummary> & Pick<KyberRunSummary, 'runId' | 'harness'>): KyberRunSummary {
  return {
    groupingBasis: 'derived',
    ...overrides,
  }
}

function runCandidate(overrides: Partial<RunCandidate> & Pick<RunCandidate, 'runId' | 'harness'>): RunCandidate {
  return {
    turns: [],
    ...overrides,
  }
}

/** Inventory is newest-first, as served by `GET /api/kyber/runs`. */
const inventory: KyberRunSummary[] = [
  runSummary({
    runId: 'cursor-run-latest',
    harness: 'cursor',
    label: 'Issue 190 baseline',
    started: '2026-10-01T14:30:00.000Z',
    turnCount: 12,
  }),
  runSummary({
    runId: 'claude-run-older',
    harness: 'claude-code',
    label: 'Claude candidate',
    started: '2026-09-28T09:15:00.000Z',
    turnCount: 8,
  }),
  runSummary({
    runId: 'zcode-subagent-looking',
    harness: 'zcode',
    label: 'subagent micro task',
    started: '2026-10-02T01:00:00.000Z',
    turnCount: 2,
  }),
  runSummary({
    runId: 'run-no-turn-count',
    harness: 'cursor',
    label: 'Unknown size run',
    started: '2026-09-15T12:00:00.000Z',
  }),
]

function optionTexts(select: HTMLSelectElement): string[] {
  return Array.from(select.options).map((option) => option.text)
}

function optionValues(select: HTMLSelectElement): string[] {
  return Array.from(select.options).map((option) => option.value)
}

function isoDatePrefix(iso: string): string {
  return iso.slice(0, 10)
}

/** T4 contract: each inventory row is identifiable without opening raw ids only. */
function expectDescriptiveOption(text: string, run: KyberRunSummary) {
  if (run.started) {
    expect(text).toContain(isoDatePrefix(run.started))
  } else {
    expect(text).toMatch(/date unknown/i)
  }
  expect(text).toContain(run.harness)
  if (run.turnCount != null) {
    expect(text).toMatch(new RegExp(String(run.turnCount)))
  } else {
    expect(text).toMatch(/turns unknown/i)
  }
  expect(text).toContain(run.label ?? run.runId)
}

const emptyPhaseSummaries = {
  exploration: {},
  implementation: {},
  verification: {},
  resolution: {},
} as KyberRunComparison['phaseSummaries']

/** Server-shaped unavailable comparison — mirrors `ComparisonSummary` from compare.ts. */
function unavailableComparison(): KyberRunComparison {
  return {
    runA: {
      runId: 'cursor-run-latest',
      harness: 'cursor',
      label: 'Issue 190 baseline',
      availability: 'unavailable',
      reason: 'no comparable canonical turns for Run A',
      metricsReason: 'no comparable canonical turns for Run A',
    },
    runB: {
      runId: 'claude-run-older',
      harness: 'claude-code',
      label: 'Claude candidate',
      availability: 'unavailable',
      reason: 'no comparable canonical turns for Run B',
      metricsReason: 'no comparable canonical turns for Run B',
    },
    pairs: [],
    phaseSummaries: emptyPhaseSummaries,
    totals: {
      availability: 'unavailable',
      reason: 'token totals are not comparable for this pair',
      turnCountA: 0,
      turnCountB: 0,
      turnDelta: 0,
      costComparable: true,
    },
    verdict: {
      status: 'insufficient_history',
      pairCount: 0,
      historyAvailability: 'unavailable',
      meetsSufficiencyThreshold: false,
      outcomeRegression: false,
      canPromote: false,
      recommendation: '',
      summary: '',
      refusalReason:
        'Recommendation promotion refused: completed pair history is unavailable. Auto-pairing is proposed only.',
    },
  }
}

describe('CompareRuns: intentional selection (#190)', () => {
  beforeEach(() => {
    mockedFetchRuns.mockReset()
    mockedFetchComparison.mockReset()
    mockedFetchRuns.mockResolvedValue(inventory)
    mockedFetchComparison.mockResolvedValue(unavailableComparison())
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('both run selectors begin at Select a run with no implicit inventory selection', async () => {
    renderCompare(<CompareRuns />)

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a')).toBeTruthy()
      expect(screen.getByTestId('compare-run-b')).toBeTruthy()
    })

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const selectB = screen.getByTestId('compare-run-b') as HTMLSelectElement

    expect(selectA.value).toBe('')
    expect(selectB.value).toBe('')
    expect(optionTexts(selectA)).toContain('Select a run')
    expect(optionTexts(selectB)).toContain('Select a run')
    expect(screen.queryByTestId('compare-n-guard')).toBeNull()
  })

  it('does not request comparison until two distinct runs are explicitly selected', async () => {
    renderCompare(<CompareRuns />)

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a')).toBeTruthy()
    })
    expect(mockedFetchRuns).toHaveBeenCalledTimes(1)
    expect(mockedFetchComparison).not.toHaveBeenCalled()

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    fireEvent.change(selectA, { target: { value: 'cursor-run-latest' } })

    await waitFor(() => {
      expect(selectA.value).toBe('cursor-run-latest')
    })
    expect(mockedFetchComparison).not.toHaveBeenCalled()

    const selectB = screen.getByTestId('compare-run-b') as HTMLSelectElement
    fireEvent.change(selectB, { target: { value: 'claude-run-older' } })

    await waitFor(() => {
      expect(mockedFetchComparison).toHaveBeenCalledTimes(1)
    })
    expect(mockedFetchComparison).toHaveBeenCalledWith('cursor-run-latest', 'claude-run-older')
  })

  it('treats deep-link run ids as explicit selections and loads comparison', async () => {
    renderCompare(
      <CompareRuns initialRunAId="cursor-run-latest" initialRunBId="claude-run-older" />,
    )

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a')).toBeTruthy()
      expect(mockedFetchComparison).toHaveBeenCalledWith('cursor-run-latest', 'claude-run-older')
    })

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const selectB = screen.getByTestId('compare-run-b') as HTMLSelectElement
    expect(selectA.value).toBe('cursor-run-latest')
    expect(selectB.value).toBe('claude-run-older')
  })

  it('clears deep-linked Run A and stops comparing when harness filter excludes it', async () => {
    renderCompare(
      <CompareRuns initialRunAId="cursor-run-latest" initialRunBId="claude-run-older" />,
    )

    await waitFor(() => {
      expect(mockedFetchComparison).toHaveBeenCalledWith('cursor-run-latest', 'claude-run-older')
    })

    const callsBeforeFilter = mockedFetchComparison.mock.calls.length

    fireEvent.change(screen.getByTestId('compare-run-a-harness-filter'), {
      target: { value: 'claude-code' },
    })

    await waitFor(() => {
      const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
      expect(selectA.value).toBe('')
    })

    expect(mockedFetchComparison.mock.calls.length).toBe(callsBeforeFilter)
    expect(screen.queryByTestId('compare-n-guard')).toBeNull()
  })

  it('clears Run A selection and stops comparing when harness filter excludes the selected run', async () => {
    renderCompare(<CompareRuns />)

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a')).toBeTruthy()
    })

    fireEvent.change(screen.getByTestId('compare-run-a'), {
      target: { value: 'cursor-run-latest' },
    })
    fireEvent.change(screen.getByTestId('compare-run-b'), {
      target: { value: 'claude-run-older' },
    })

    await waitFor(() => {
      expect(mockedFetchComparison).toHaveBeenCalledWith('cursor-run-latest', 'claude-run-older')
    })

    const callsBeforeFilter = mockedFetchComparison.mock.calls.length

    fireEvent.change(screen.getByTestId('compare-run-a-harness-filter'), {
      target: { value: 'claude-code' },
    })

    await waitFor(() => {
      const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
      expect(selectA.value).toBe('')
    })

    expect(mockedFetchComparison.mock.calls.length).toBe(callsBeforeFilter)
    expect(screen.queryByTestId('compare-n-guard')).toBeNull()
  })

  it('narrows Run A and Run B inventories independently by harness filter', async () => {
    renderCompare(<CompareRuns />)

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a-harness-filter')).toBeTruthy()
      expect(screen.getByTestId('compare-run-b-harness-filter')).toBeTruthy()
    })

    const filterA = screen.getByTestId('compare-run-a-harness-filter') as HTMLSelectElement
    const filterB = screen.getByTestId('compare-run-b-harness-filter') as HTMLSelectElement

    fireEvent.change(filterA, { target: { value: 'cursor' } })
    fireEvent.change(filterB, { target: { value: 'claude-code' } })

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const selectB = screen.getByTestId('compare-run-b') as HTMLSelectElement

    const aValues = optionValues(selectA).filter(Boolean)
    const bValues = optionValues(selectB).filter(Boolean)

    expect(aValues).toEqual(['cursor-run-latest', 'run-no-turn-count'])
    expect(bValues).toEqual(['claude-run-older'])

    fireEvent.change(selectA, { target: { value: 'cursor-run-latest' } })
    fireEvent.change(selectB, { target: { value: 'claude-run-older' } })

    await waitFor(() => {
      expect(mockedFetchComparison).toHaveBeenCalledWith('cursor-run-latest', 'claude-run-older')
    })
  })

  it('labels each run option with ISO date harness turn count and distinguishing label', async () => {
    renderCompare(<CompareRuns />)

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a')).toBeTruthy()
    })

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const byValue = new Map(Array.from(selectA.options).map((option) => [option.value, option.text]))

    for (const run of inventory) {
      expectDescriptiveOption(byValue.get(run.runId) ?? '', run)
    }
  })

  it('preserves newest-first server order within a filtered inventory', async () => {
    renderCompare(<CompareRuns />)

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a-harness-filter')).toBeTruthy()
    })

    fireEvent.change(screen.getByTestId('compare-run-a-harness-filter'), {
      target: { value: 'cursor' },
    })

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const cursorIds = optionValues(selectA).filter(Boolean)
    expect(cursorIds).toEqual(['cursor-run-latest', 'run-no-turn-count'])
  })

  it('does not fetch comparison when both selectors choose the same run', async () => {
    renderCompare(<CompareRuns />)

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a')).toBeTruthy()
    })

    const callsAfterAutoSelect = mockedFetchComparison.mock.calls.length

    fireEvent.change(screen.getByTestId('compare-run-a'), {
      target: { value: 'cursor-run-latest' },
    })
    fireEvent.change(screen.getByTestId('compare-run-b'), {
      target: { value: 'cursor-run-latest' },
    })

    await waitFor(() => {
      const selectB = screen.getByTestId('compare-run-b') as HTMLSelectElement
      expect(selectB.value).toBe('cursor-run-latest')
    })

    expect(mockedFetchComparison.mock.calls.length).toBe(callsAfterAutoSelect)
    expect(screen.queryByText(/Phase-Aligned Turn Diff/i)).toBeNull()
  })

  it('permits cross-harness comparison when each side selects a different harness', async () => {
    renderCompare(<CompareRuns />)

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a-harness-filter')).toBeTruthy()
    })

    fireEvent.change(screen.getByTestId('compare-run-a-harness-filter'), {
      target: { value: 'cursor' },
    })
    fireEvent.change(screen.getByTestId('compare-run-b-harness-filter'), {
      target: { value: 'zcode' },
    })

    fireEvent.change(screen.getByTestId('compare-run-a'), {
      target: { value: 'cursor-run-latest' },
    })
    fireEvent.change(screen.getByTestId('compare-run-b'), {
      target: { value: 'zcode-subagent-looking' },
    })

    await waitFor(() => {
      expect(mockedFetchComparison).toHaveBeenCalledWith('cursor-run-latest', 'zcode-subagent-looking')
    })
  })

  it('keeps ZCode inventory rows visible and never labels them as subagents', async () => {
    renderCompare(<CompareRuns />)

    await waitFor(() => {
      expect(screen.getByTestId('compare-run-a')).toBeTruthy()
    })

    expect(screen.queryByTestId('compare-subagent-filter')).toBeNull()
    expect(screen.queryByLabelText(/subagent/i)).toBeNull()

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const zcodeOption = Array.from(selectA.options).find(
      (option) => option.value === 'zcode-subagent-looking',
    )
    expect(zcodeOption).toBeTruthy()
    expect(zcodeOption?.text).toContain('zcode')
    // Harness-only filtering must not append a inferred "(subagent)" role marker.
    expect(zcodeOption?.text).not.toMatch(/\(subagent\)/i)
  })
})

describe('CompareRuns: honest unavailable metrics (#190)', () => {
  afterEach(() => {
    cleanup()
  })

  it('renders unavailable turn totals and token delta as em dash with reason not numeric zero', () => {
    const comparison = unavailableComparison()
    renderCompare(
      <CompareRuns
        runs={[
          runCandidate({ runId: 'cursor-run-latest', harness: 'cursor', label: 'Issue 190 baseline' }),
          runCandidate({ runId: 'claude-run-older', harness: 'claude-code', label: 'Claude candidate' }),
        ]}
        selectedAId="cursor-run-latest"
        selectedBId="claude-run-older"
        comparison={comparison}
      />,
    )

    const guard = screen.getByTestId('compare-n-guard')
    const text = guard.textContent ?? ''

    expect(text).toContain('—')
    expect(text).toContain('token totals are not comparable for this pair')
    expect(text).not.toMatch(/Token Delta:\s*0\b/)
    expect(text).not.toMatch(/Token Delta:\s*\+0\b/)

    const page = screen.getByTestId('page-compare').textContent ?? ''
    expect(page).toContain('no comparable canonical turns for Run A')
    expect(page).toContain('no comparable canonical turns for Run B')
    expect(page).not.toMatch(/Turns:\s*0\b/)
  })

  it('shows measured turnCount when tokens are withheld but turns resolved', () => {
    // availability:unavailable means tokens withheld — turnCount may still be known
    // (buildComparisonRunSide ships it on the token-gap branch).
    const comparison: KyberRunComparison = {
      ...unavailableComparison(),
      runA: {
        runId: 'cursor-run-latest',
        harness: 'gemini',
        label: 'Gemini baseline',
        availability: 'unavailable',
        reason: 'Gemini explicit caching has no write counter.',
        metricsReason: 'Gemini explicit caching has no write counter.',
        turnCount: 12,
      },
      runB: {
        runId: 'claude-run-older',
        harness: 'claude-code',
        label: 'Claude candidate',
        availability: 'unavailable',
        reason: 'Gemini explicit caching has no write counter.',
        metricsReason: 'Gemini explicit caching has no write counter.',
        turnCount: 8,
      },
      totals: {
        availability: 'unavailable',
        reason: 'Gemini explicit caching has no write counter.',
        turnCountA: 12,
        turnCountB: 8,
        turnDelta: -4,
        costComparable: true,
      },
    }
    renderCompare(
      <CompareRuns
        runs={[
          runCandidate({
            runId: 'cursor-run-latest',
            harness: 'gemini',
            label: 'Gemini baseline',
            turnCount: 12,
          }),
          runCandidate({
            runId: 'claude-run-older',
            harness: 'claude-code',
            label: 'Claude candidate',
            turnCount: 8,
          }),
        ]}
        selectedAId="cursor-run-latest"
        selectedBId="claude-run-older"
        comparison={comparison}
      />,
    )

    const page = screen.getByTestId('page-compare').textContent ?? ''
    expect(page).toMatch(/Turns:\s*12/)
    expect(page).toMatch(/Turns:\s*8/)
    expect(page).not.toMatch(/Turns:\s*—/)
  })

  it('states recommendation history is unavailable instead of showing zero of five', () => {
    const comparison = unavailableComparison()
    renderCompare(
      <CompareRuns
        runs={[
          runCandidate({ runId: 'cursor-run-latest', harness: 'cursor' }),
          runCandidate({ runId: 'claude-run-older', harness: 'claude-code' }),
        ]}
        selectedAId="cursor-run-latest"
        selectedBId="claude-run-older"
        comparison={comparison}
      />,
    )

    const guard = screen.getByTestId('compare-n-guard')
    const text = guard.textContent ?? ''

    expect(text).toContain('Recommendation history: —')
    expect(text).toContain('recommendation history is not measured')
    expect(text).toContain('Token Delta: —')
    expect(text).toContain('token totals are not comparable for this pair')
    expect(text).not.toMatch(/0\s*\/\s*5/)
    expect(text).not.toMatch(/Completed Pairs:\s*0/)
  })

  it('does not mislabel an outcome-regression refusal as a history gap', () => {
    const comparison: KyberRunComparison = {
      ...unavailableComparison(),
      totals: {
        availability: 'measured',
        tokensA: 100,
        tokensB: 120,
        tokenDelta: 20,
        turnCountA: 1,
        turnCountB: 1,
        turnDelta: 0,
        costComparable: true,
      },
      runA: {
        runId: 'cursor-run-latest',
        harness: 'cursor',
        availability: 'measured',
        totalTokens: 100,
        turnCount: 1,
      },
      runB: {
        runId: 'claude-run-older',
        harness: 'claude-code',
        availability: 'measured',
        totalTokens: 120,
        turnCount: 1,
      },
      verdict: {
        status: 'outcome_regression',
        pairCount: 1,
        historyAvailability: 'unavailable',
        meetsSufficiencyThreshold: false,
        outcomeRegression: true,
        canPromote: false,
        recommendation: '',
        summary: '',
        refusalReason:
          'Promotion refused: outcome regression detected between paired runs. Modifications cannot be recommended when correctness or test outcomes regress.',
      },
    }
    renderCompare(
      <CompareRuns
        runs={[
          runCandidate({
            runId: 'cursor-run-latest',
            harness: 'cursor',
            outcome: { status: 'success' },
          }),
          runCandidate({
            runId: 'claude-run-older',
            harness: 'claude-code',
            outcome: { status: 'failure' },
          }),
        ]}
        selectedAId="cursor-run-latest"
        selectedBId="claude-run-older"
        comparison={comparison}
      />,
    )

    const guard = screen.getByTestId('compare-n-guard')
    const text = guard.textContent ?? ''

    expect(text).toContain('Recommendation history: — (recommendation history is not measured)')
    expect(text).toContain('Outcome Regression Guard Refusal')
    expect(text).toContain('outcome regression detected between paired runs')
    // History row must not wear the outcome-regression refusal text.
    expect(text).not.toMatch(
      /Recommendation history: — \(Promotion refused: outcome regression/,
    )
  })
})

describe('CompareRuns: confirm pair clears harness filters (#190)', () => {
  afterEach(() => {
    cleanup()
  })

  it('loads a proposed pair that the active harness filter would have excluded', () => {
    const onConfirmPair = vi.fn()
    renderCompare(
      <CompareRuns
        runs={[
          runCandidate({ runId: 'cursor-run-latest', harness: 'cursor' }),
          runCandidate({ runId: 'claude-run-older', harness: 'claude-code' }),
        ]}
        proposedPairs={[
          {
            pairId: 'pair-1',
            runAId: 'cursor-run-latest',
            runBId: 'claude-run-older',
            taskFamily: 'issue-190',
            confidence: 0.9,
            heuristics: ['same-task-family'],
            reasons: ['same task family'],
            completedPairCount: 2,
            meetsSufficiencyThreshold: false,
            canPromote: false,
            recommendationStatus: 'proposed_only',
            verdictMessage: 'proposed only',
          },
        ]}
        onConfirmPair={onConfirmPair}
        comparison={unavailableComparison()}
      />,
    )

    // Restrict Run A to claude-code so cursor-run-latest is filtered out.
    fireEvent.change(screen.getByTestId('compare-run-a-harness-filter'), {
      target: { value: 'claude-code' },
    })
    // Drawer starts open when proposed pairs are supplied.
    fireEvent.click(screen.getByRole('button', { name: /Confirm & Load Pair/i }))

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const selectB = screen.getByTestId('compare-run-b') as HTMLSelectElement
    expect(selectA.value).toBe('cursor-run-latest')
    expect(selectB.value).toBe('claude-run-older')
    expect(onConfirmPair).toHaveBeenCalledWith('pair-1')
  })
})

describe('CompareRuns: measured history below threshold preserves the server refusal reason (#190)', () => {
  afterEach(() => {
    cleanup()
  })

  it('shows the server refusal reason instead of the client count message', () => {
    const verdict: KyberRunComparison['verdict'] = {
      status: 'insufficient_history',
      pairCount: 0,
      completedPairCount: 3,
      historyAvailability: 'measured',
      meetsSufficiencyThreshold: false,
      outcomeRegression: false,
      canPromote: false,
      recommendation:
        'Pairing proposed for manual review. At least 5 completed pairs without outcome regression are required before promoting automated recommendations.',
      refusalReason:
        'Auto-promotion refused: observed 3 completed pair(s), but minimum threshold is n >= 5 completed pairs. Automatic pairing is proposed only and requires manual confirmation.',
      summary: '',
    }
    const comparison = { ...unavailableComparison(), verdict }
    renderCompare(
      <CompareRuns
        runs={[
          runCandidate({ runId: 'cursor-run-latest', harness: 'cursor' }),
          runCandidate({ runId: 'claude-run-older', harness: 'claude-code' }),
        ]}
        selectedAId="cursor-run-latest"
        selectedBId="claude-run-older"
        comparison={comparison}
      />,
    )

    const guard = screen.getByTestId('compare-n-guard')
    const text = guard.textContent ?? ''

    expect(text).toContain('Auto-promotion refused: observed 3 completed pair(s)')
    expect(text).toContain('requires manual confirmation')
    expect(text).not.toContain('Recommendation promotion refused')
  })
})
