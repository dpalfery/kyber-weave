// @vitest-environment happy-dom
// Issue #190 / plan T3: Compare pickers — empty defaults (D3), option labels (D4),
// subagent / zero-turn filters (D2), fetch only when both ids are explicit.
import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { render, screen, cleanup, fireEvent, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type * as React from 'react'

import { CompareRuns, type RunCandidate } from './CompareRuns.js'
import * as kyberApi from '../lib/kyberApi.js'
import type { KyberRunComparison } from '../lib/kyberApi.js'

function stubComparison(runAId: string, runBId: string): KyberRunComparison {
  return {
    runA: { runId: runAId, harness: 'cursor', turnCount: 3, totalTokens: 150 },
    runB: { runId: runBId, harness: 'claude-code', turnCount: 4, totalTokens: 280 },
    pairs: [],
    phaseSummaries: {
      exploration: {},
      implementation: {},
      verification: {},
      resolution: {},
    },
    totals: { tokenDelta: 130 },
    taskFamily: undefined,
    verdict: {
      status: 'insufficient_history',
      pairCount: 0,
      completedPairCount: 0,
      meetsSufficiencyThreshold: false,
      outcomeRegression: false,
      canPromote: false,
      recommendation: '',
      summary: '',
    },
  }
}

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
    },
  })
}

function renderCompare(ui: React.ReactElement) {
  const client = createTestQueryClient()
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>)
}

function run(partial: Partial<RunCandidate> & Pick<RunCandidate, 'runId' | 'harness'>): RunCandidate {
  return {
    turns: [],
    turnCount: 3,
    totalInput: 1000,
    totalOutput: 200,
    isSubagent: false,
    started: '2026-09-15T14:30:00.000Z',
    ...partial,
  }
}

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

describe('CompareRuns picker defaults (D3)', () => {
  it('both pickers start empty with a Select a run option and do not auto-select runs[0]', () => {
    const runs = [
      run({ runId: 'run-aaa', harness: 'cursor', label: 'Alpha' }),
      run({ runId: 'run-bbb', harness: 'claude-code', label: 'Beta' }),
    ]
    renderCompare(<CompareRuns runs={runs} />)

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const selectB = screen.getByTestId('compare-run-b') as HTMLSelectElement

    expect(selectA.value).toBe('')
    expect(selectB.value).toBe('')
    expect(within(selectA).getByRole('option', { name: 'Select a run' })).toBeTruthy()
    expect(within(selectB).getByRole('option', { name: 'Select a run' })).toBeTruthy()
    expect(selectA.value).not.toBe('run-aaa')
    expect(selectB.value).not.toBe('run-bbb')
  })

  it('treats initialRunAId / initialRunBId as explicit selection without filling the other side', () => {
    const runs = [
      run({ runId: 'run-aaa', harness: 'cursor' }),
      run({ runId: 'run-bbb', harness: 'claude-code' }),
      run({ runId: 'run-ccc', harness: 'copilot' }),
    ]
    renderCompare(<CompareRuns runs={runs} initialRunAId="run-bbb" />)

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const selectB = screen.getByTestId('compare-run-b') as HTMLSelectElement
    expect(selectA.value).toBe('run-bbb')
    expect(selectB.value).toBe('')
  })
})

describe('CompareRuns option labels (D4)', () => {
  it('renders date · harness · Nt · tokens · shortId (optional label prefix)', () => {
    const runs = [
      run({
        runId: 'derived:cursor:0123456789abcdefghijklmnop',
        harness: 'cursor',
        label: 'hotfix',
        turnCount: 7,
        totalInput: 1200,
        totalOutput: 300,
        started: '2026-09-15T14:30:00.000Z',
      }),
    ]
    renderCompare(<CompareRuns runs={runs} />)

    const selectA = screen.getByTestId('compare-run-a')
    const option = within(selectA).getByRole('option', { name: /hotfix/ })
    const text = option.textContent ?? ''
    const localDate = new Date('2026-09-15T14:30:00.000Z').toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: '2-digit',
    })
    expect(text).toContain(localDate)
    expect(text).toContain('cursor')
    expect(text).toContain('7t')
    expect(text).toContain('1,500')
    expect(text).toMatch(/hotfix/)
    expect(text).toContain('·')
    expect(text).not.toMatch(/derived:cursor:0123456789abcdefghijklmnop/)
  })

  it('shows — for tokens when input and output are absent', () => {
    const runs = [
      run({
        runId: 'run-no-tokens',
        harness: 'cursor',
        turnCount: 2,
        totalInput: undefined,
        totalOutput: undefined,
      }),
    ]
    renderCompare(<CompareRuns runs={runs} />)
    const selectA = screen.getByTestId('compare-run-a')
    const option = within(selectA).getByRole('option', { name: /run-no-tokens|2t/ })
    expect(option.textContent).toContain('—')
  })

  it('sets title with full runId and cwd only when truncation collides', () => {
    // Two long ids that share the same shortRunId elision ends after stripping
    // the derived prefix — force collision by using identical display short forms
    // via the same truncated middle-elision pattern with different full ids that
    // still collapse to one label body except shortId. Prefer asserting: when two
    // options share the same visible label text, both carry a title with runId + cwd.
    const sharedHarness = 'cursor'
    const runs = [
      run({
        runId: 'derived:cursor:aaaaaaaaaXXXXXXXXXbbbbbbbb',
        harness: sharedHarness,
        workingDirectory: '/Users/hal/proj-a',
        label: undefined,
        turnCount: 1,
        totalInput: 10,
        totalOutput: 10,
        started: '2026-09-15T14:30:00.000Z',
      }),
      run({
        runId: 'derived:cursor:aaaaaaaaaYYYYYYYYYbbbbbbbb',
        harness: sharedHarness,
        workingDirectory: '/Users/hal/proj-b',
        label: undefined,
        turnCount: 1,
        totalInput: 10,
        totalOutput: 10,
        started: '2026-09-15T14:30:00.000Z',
      }),
    ]
    renderCompare(<CompareRuns runs={runs} />)

    const selectA = screen.getByTestId('compare-run-a')
    const options = within(selectA)
      .getAllByRole('option')
      .filter((o) => (o as HTMLOptionElement).value !== '') as HTMLOptionElement[]
    expect(options).toHaveLength(2)
    const labels = options.map((o) => o.textContent ?? '')
    // shortRunId keeps first 9 + … + last 8 → both become aaaaaaaaa…bbbbbbbb
    expect(labels[0]).toBe(labels[1])
    for (const opt of options) {
      expect(opt.title).toContain(opt.value)
      expect(opt.title).toMatch(/\/Users\/hal\/proj-/)
    }
  })
})

describe('CompareRuns picker filters (D2)', () => {
  it('default list omits is_subagent runs and zero turnCount runs', () => {
    const runs = [
      run({ runId: 'parent-ok', harness: 'cursor', turnCount: 4 }),
      run({ runId: 'sub-agent', harness: 'zcode', turnCount: 5, isSubagent: true }),
      run({ runId: 'empty-turns', harness: 'cursor', turnCount: 0 }),
    ]
    renderCompare(<CompareRuns runs={runs} />)

    const selectA = screen.getByTestId('compare-run-a')
    const values = within(selectA)
      .getAllByRole('option')
      .map((o) => (o as HTMLOptionElement).value)
      .filter(Boolean)
    expect(values).toEqual(['parent-ok'])
  })

  it('Show subagents toggle reveals subagent runs but still hides zero-turn runs', () => {
    const runs = [
      run({ runId: 'parent-ok', harness: 'cursor', turnCount: 4 }),
      run({ runId: 'sub-agent', harness: 'zcode', turnCount: 5, isSubagent: true }),
      run({ runId: 'empty-turns', harness: 'cursor', turnCount: 0 }),
    ]
    renderCompare(<CompareRuns runs={runs} />)

    const toggle = screen.getByRole('checkbox', { name: /show subagents/i })
    fireEvent.click(toggle)

    const selectA = screen.getByTestId('compare-run-a')
    const values = within(selectA)
      .getAllByRole('option')
      .map((o) => (o as HTMLOptionElement).value)
      .filter(Boolean)
    expect(values).toContain('parent-ok')
    expect(values).toContain('sub-agent')
    expect(values).not.toContain('empty-turns')
  })

  it('keeps an initially selected subagent in the option list without enabling Show subagents', () => {
    const runs = [
      run({ runId: 'parent-ok', harness: 'cursor', turnCount: 4 }),
      run({ runId: 'sub-agent', harness: 'zcode', turnCount: 5, isSubagent: true }),
      run({ runId: 'empty-turns', harness: 'cursor', turnCount: 0 }),
    ]
    renderCompare(
      <CompareRuns runs={runs} initialRunAId="sub-agent" initialRunBId="parent-ok" />,
    )

    const selectA = screen.getByTestId('compare-run-a') as HTMLSelectElement
    const selectB = screen.getByTestId('compare-run-b') as HTMLSelectElement
    expect(selectA.value).toBe('sub-agent')
    expect(selectB.value).toBe('parent-ok')
    expect(within(selectA).getByRole('option', { name: /sub-agent|5t/ })).toBeTruthy()
    expect((screen.getByTestId('compare-show-subagents') as HTMLInputElement).checked).toBe(false)

    const values = within(selectA)
      .getAllByRole('option')
      .map((o) => (o as HTMLOptionElement).value)
      .filter(Boolean)
    expect(values).toContain('sub-agent')
    expect(values).toContain('parent-ok')
    expect(values).not.toContain('empty-turns')
  })
})

describe('CompareRuns comparison fetch gating (D3)', () => {
  beforeEach(() => {
    vi.spyOn(kyberApi, 'fetchRuns').mockResolvedValue([
      {
        runId: 'live-a',
        harness: 'cursor',
        groupingBasis: 'derived',
        turnCount: 3,
        totalInput: 100,
        totalOutput: 50,
        isSubagent: false,
        started: '2026-09-15T14:30:00.000Z',
      },
      {
        runId: 'live-b',
        harness: 'claude-code',
        groupingBasis: 'derived',
        turnCount: 4,
        totalInput: 200,
        totalOutput: 80,
        isSubagent: false,
        started: '2026-09-16T10:00:00.000Z',
      },
    ])
  })

  it('does not fetch comparison until both pickers have distinct explicit selections', async () => {
    const fetchCompare = vi
      .spyOn(kyberApi, 'fetchRunComparison')
      .mockResolvedValue(stubComparison('live-a', 'live-b'))

    renderCompare(<CompareRuns />)

    await screen.findByTestId('compare-run-a')
    expect(fetchCompare).not.toHaveBeenCalled()

    fireEvent.change(screen.getByTestId('compare-run-a'), { target: { value: 'live-a' } })
    expect(fetchCompare).not.toHaveBeenCalled()

    fireEvent.change(screen.getByTestId('compare-run-b'), { target: { value: 'live-b' } })
    await vi.waitFor(() => {
      expect(fetchCompare).toHaveBeenCalledWith('live-a', 'live-b')
    })
  })

  it('fetches when both initialRun ids are provided (deep-link = explicit)', async () => {
    const fetchCompare = vi
      .spyOn(kyberApi, 'fetchRunComparison')
      .mockResolvedValue(stubComparison('live-a', 'live-b'))

    renderCompare(<CompareRuns initialRunAId="live-a" initialRunBId="live-b" />)

    await vi.waitFor(() => {
      expect(fetchCompare).toHaveBeenCalledWith('live-a', 'live-b')
    })
  })

  it('fetches when initialRunAId is a filtered-out subagent and B is set', async () => {
    vi.spyOn(kyberApi, 'fetchRuns').mockResolvedValue([
      {
        runId: 'live-parent',
        harness: 'cursor',
        groupingBasis: 'derived',
        turnCount: 3,
        totalInput: 100,
        totalOutput: 50,
        isSubagent: false,
        started: '2026-09-15T14:30:00.000Z',
      },
      {
        runId: 'live-sub',
        harness: 'zcode',
        groupingBasis: 'derived',
        turnCount: 5,
        totalInput: 200,
        totalOutput: 80,
        isSubagent: true,
        started: '2026-09-16T10:00:00.000Z',
      },
    ])
    const fetchCompare = vi
      .spyOn(kyberApi, 'fetchRunComparison')
      .mockResolvedValue(stubComparison('live-sub', 'live-parent'))

    renderCompare(<CompareRuns initialRunAId="live-sub" initialRunBId="live-parent" />)

    const selectA = await screen.findByTestId('compare-run-a')
    expect((selectA as HTMLSelectElement).value).toBe('live-sub')
    expect(within(selectA).getByRole('option', { name: /live-sub|5t/ })).toBeTruthy()
    expect((screen.getByTestId('compare-show-subagents') as HTMLInputElement).checked).toBe(false)

    await vi.waitFor(() => {
      expect(fetchCompare).toHaveBeenCalledWith('live-sub', 'live-parent')
    })
  })
})
