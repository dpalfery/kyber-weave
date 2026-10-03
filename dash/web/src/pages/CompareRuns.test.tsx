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

/** Future-facing unavailable comparison the T2/T4 pair must render honestly. */
function unavailableComparison(): KyberRunComparison & {
  runA: KyberRunComparison['runA'] & { metricsReason?: string }
  runB: KyberRunComparison['runB'] & { metricsReason?: string }
  totals: Record<string, unknown>
  verdict: KyberRunComparison['verdict'] & { historyReason?: string }
} {
  return {
    runA: {
      runId: 'cursor-run-latest',
      harness: 'cursor',
      label: 'Issue 190 baseline',
      totalTokens: 0,
      turnCount: 0,
      metricsReason: 'no comparable canonical turns for Run A',
    },
    runB: {
      runId: 'claude-run-older',
      harness: 'claude-code',
      label: 'Claude candidate',
      totalTokens: 0,
      turnCount: 0,
      metricsReason: 'no comparable canonical turns for Run B',
    },
    pairs: [],
    phaseSummaries: emptyPhaseSummaries,
    totals: {
      tokenDelta: 0,
      tokenDeltaReason: 'token totals are not comparable for this pair',
    },
    verdict: {
      status: 'insufficient_history',
      pairCount: 0,
      completedPairCount: 0,
      meetsSufficiencyThreshold: false,
      outcomeRegression: false,
      canPromote: false,
      recommendation: '',
      summary: '',
      historyReason: 'recommendation history is not measured',
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

    expect(text).toContain('recommendation history is not measured')
    expect(text).not.toMatch(/0\s*\/\s*5/)
    expect(text).not.toMatch(/Completed Pairs:\s*0/)
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
