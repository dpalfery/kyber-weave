import { describe, it, expect, vi, afterEach } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type * as React from 'react'

import { ContextDoctor, browserRows, nextAccumulated, FindingsBrowserView, nextBrowserOffset, currentBrowserPage } from './ContextDoctor.js'
import { fetchCoverage, type FindingsPage, type KyberFinding, type KyberHarnessSummary, type KyberCoverage } from '../lib/kyberApi.js'

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

function renderWithQuery(ui: React.ReactElement) {
  const queryClient = createTestQueryClient()
  return renderToStaticMarkup(
    <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>,
  )
}

function finding(id: string, detectorId: string, harness: string): KyberFinding {
  return {
    id,
    detectorId,
    title: `${detectorId} ${id}`,
    mechanism: 'mechanism',
    evidenceLinks: [],
    confidence: 'deterministic',
    estimatedWasteTokens: 100,
    recommendation: 'recommendation',
    errorBar: { lower: 80, upper: 120 },
    outcomeRiskCaveat: 'caveat',
    harness,
    rankScore: 100,
  }
}

const harnessRow: KyberHarnessSummary = {
  harness: 'cursor',
  name: 'Cursor',
  sampleCount: 10,
  measurability: {},
}

// Issue #191: the workspace view shows 5 of 115 findings with no way to see
// the rest. The browser section makes the full set — counts, filters, pages —
// visible from Context Doctor. The first page holds FINDINGS_PAGE_SIZE rows;
// Load more advances the offset (it must not refetch a growing prefix).
const detectors = ['duplicate-tool-call', 'compaction-hazard', 'dormant-tool-schema']
const page: FindingsPage = {
  findings: Array.from({ length: 25 }, (_, i) =>
    finding(`f-${i + 1}`, detectors[i % detectors.length]!, i % 2 === 0 ? 'cursor' : 'claude-code'),
  ),
  total: 115,
  offset: 0,
  detectorCounts: {
    'duplicate-tool-call': 49,
    'compaction-hazard': 40,
    'dormant-tool-schema': 26,
  },
  unknownWindowSessions: 3,
}

function render(): string {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return renderToStaticMarkup(
    <QueryClientProvider client={client}>
      <ContextDoctor initialHarnesses={[harnessRow]} initialFindings={page} />
    </QueryClientProvider>,
  )
}

describe('ContextDoctor findings browser (issue #191)', () => {
  it('shows the workspace total with per-detector counts', () => {
    const html = render()
    expect(html).toContain('All Workspace Findings (115)')
    expect(html).toContain('duplicate-tool-call')
    expect(html).toContain('×49')
  })

  it('shows how many sessions have an unknown context window', () => {
    // Condition 3: suppressing default-window findings must not read as
    // \"all clear\" — the unmeasurable sessions stay visible.
    const html = render()
    expect(html).toContain('unknown-window-banner')
    expect(html).toContain('3 sessions with unknown context window')
  })

  it('pages the browser a full page at a time', () => {
    const html = render()
    expect(html).toContain('findings-load-more')
    expect(html).toContain('25 of 115')
  })

  it('keeps the ranked top-5 headline card', () => {
    const html = render()
    expect(html).toContain('Highest-Leverage Workspace Findings')
  })
})

describe('browserRows — paging accumulates instead of replacing (issue #191)', () => {
  const scope = '|'
  const other = 'duplicate-tool-call|'
  const rows = (prefix: string, n: number) =>
    Array.from({ length: n }, (_, i) => finding(`${prefix}-${i + 1}`, 'duplicate-tool-call', 'cursor'))

  it('renders page one before the effect has stored it (static render)', () => {
    // Only the first page exists at this point; nothing has been accumulated.
    expect(browserRows([], scope, 0, rows('p0', 25)).map((r) => r.id)).toEqual(
      rows('p0', 25).map((r) => r.id),
    )
  })

  it('extends the list with page two rather than replacing page one', () => {
    const stored = [
      { scope, offset: 0, rows: rows('p0', 25) },
      { scope, offset: 25, rows: rows('p1', 25) },
    ]
    const ids = browserRows(stored, scope, 25, undefined).map((r) => r.id)
    expect(ids).toHaveLength(50)
    expect(ids).toContain('p0-1')
    expect(ids).toContain('p1-25')
  })

  it('orders pages by offset regardless of arrival order', () => {
    const stored = [
      { scope, offset: 50, rows: rows('p2', 25) },
      { scope, offset: 0, rows: rows('p0', 25) },
      { scope, offset: 25, rows: rows('p1', 25) },
    ]
    const ids = browserRows(stored, scope, 50, undefined).map((r) => r.id)
    expect(ids[0]).toBe('p0-1')
    expect(ids[25]).toBe('p1-1')
    expect(ids[50]).toBe('p2-1')
  })

  it('never re-appends a page fetched under a previous filter scope', () => {
    const stored = [
      { scope: other, offset: 0, rows: rows('narrowed', 25) },
      { scope, offset: 0, rows: rows('p0', 25) },
    ]
    const ids = browserRows(stored, scope, 0, undefined).map((r) => r.id)
    expect(ids).toHaveLength(25)
    expect(ids.some((id) => id.startsWith('narrowed'))).toBe(false)
  })
})

describe("nextAccumulated — the first page must be stored (issue #191)", () => {
  const scope = "|"
  const rows = (prefix: string) => [finding(prefix, "duplicate-tool-call", "cursor")]

  it("stores page zero instead of skipping it", () => {
    // Skipping offset 0 is what made Load more replace the list: page one was
    // never in the accumulated list, so there was nothing to extend.
    expect(nextAccumulated([], scope, 0, rows("p0"))).toHaveLength(1)
  })

  it("is a no-op when the same page arrives again", () => {
    const once = nextAccumulated([], scope, 0, rows("p0"))
    expect(nextAccumulated(once, scope, 0, rows("p0"))).toHaveLength(1)
  })

  it("stores the same offset under a different scope", () => {
    const once = nextAccumulated([], scope, 0, rows("p0"))
    expect(nextAccumulated(once, "duplicate-tool-call|", 0, rows("p1"))).toHaveLength(2)
  })

  it("accumulates page two alongside page one", () => {
    const both = nextAccumulated(nextAccumulated([], scope, 0, rows("p0")), scope, 25, rows("p1"))
    expect(both.map((p) => p.offset)).toEqual([0, 25])
  })
})

describe('FindingsBrowserView second page (review M1)', () => {
  const detectors = ['duplicate-tool-call', 'compaction-hazard', 'dormant-tool-schema']
  const fifty = Array.from({ length: 50 }, (_, i) =>
    finding(`g-${i + 1}`, detectors[i % detectors.length]!, i % 2 === 0 ? 'cursor' : 'claude-code'),
  )
  const counts = { 'duplicate-tool-call': 49, 'compaction-hazard': 40, 'dormant-tool-schema': 26 }

  function renderBrowser(): string {
    return renderToStaticMarkup(
      <FindingsBrowserView
        findings={fifty}
        total={115}
        detectorCounts={counts}
        unknownWindowSessions={3}
        loading={false}
        harnesses={[{ harness: 'cursor', name: 'Cursor' }]}
        onSelectFinding={() => {}}
      />,
    )
  }

  it('renders two accumulated pages with no skeleton and no duplicate rows', () => {
    const html = renderBrowser()
    expect(html).not.toContain('skeleton-shimmer')
    for (let i = 1; i <= 50; i++) {
      const occurrences = html.split(`data-testid="finding-card-g-${i}"`).length - 1
      expect(occurrences).toBe(1)
    }
  })

  it('keeps heading count, chips and banner across pages', () => {
    const html = renderBrowser()
    expect(html).toContain('All Workspace Findings (115)')
    expect(html).toContain('duplicate-tool-call')
    expect(html).toContain('×49')
    expect(html).toContain('3 sessions with unknown context window')
    expect(html).toContain('findings-load-more')
    expect(html).toContain('50 of 115')
  })

  it('shows the skeleton only when nothing is stored yet', () => {
    const loading = renderToStaticMarkup(
      <FindingsBrowserView findings={[]} total={115} detectorCounts={counts} loading={true} harnesses={[]} />,
    )
    expect(loading).toContain('skeleton-shimmer')
    const loaded = renderToStaticMarkup(
      <FindingsBrowserView findings={fifty} total={115} detectorCounts={counts} loading={true} harnesses={[]} />,
    )
    expect(loaded).not.toContain('skeleton-shimmer')
  })
})

describe('FindingsBrowserView clear filters (review S1)', () => {
  const counts = { 'duplicate-tool-call': 49, 'compaction-hazard': 0, 'dormant-tool-schema': 0 }

  it('offers clearing while a detector filter is active', () => {
    // A zero-count chip row hides every chip including the active one; the
    // explicit clear control is the way back.
    const html = renderToStaticMarkup(
      <FindingsBrowserView
        findings={[]}
        total={0}
        detectorCounts={counts}
        detectorFilter="duplicate-tool-call"
        loading={false}
        harnesses={[]}
      />,
    )
    expect(html).toContain('findings-clear-filters')
  })

  it('hides the clear control when no filter is active', () => {
    const html = renderToStaticMarkup(
      <FindingsBrowserView findings={[]} total={0} detectorCounts={counts} loading={false} harnesses={[]} />,
    )
    expect(html).not.toContain('findings-clear-filters')
  })
})
/**
 * T10 web matrix honesty (issues #189/#199): the coverage banner wiring.
 * The ingest activity panel is T9's hunk in this same file — merge-keep-both.
 */
describe('ContextDoctor coverage banner (issues #189/#199, T10)', () => {
  it('renders the coverage window banner above the matrix', () => {
    const html = renderWithQuery(
      <ContextDoctor
        initialHarnesses={[
          {
            harness: 'pi',
            name: 'Pi',
            sampleCount: 2,
            measurability: { token_usage: 'measured' },
          },
        ]}
        initialFindings={[]}
        initialCoverage={{
          lastSuccessAt: '2026-09-30T00:00:00.000Z',
          lastFailure: null,
          inProgress: null,
          historyWeeks: 2,
          coveredFrom: '2026-09-16T00:00:00.000Z',
          coveredThrough: '2026-09-30T00:00:00.000Z',
        }}
      />,
    )
    expect(html).toContain('Coverage: last 2 weeks')
    expect(html).toContain('scorecard-matrix')
  })

  it('states an unknown window honestly instead of defaulting to 2', () => {
    const html = renderWithQuery(
      <ContextDoctor
        initialHarnesses={[]}
        initialFindings={[]}
        initialCoverage={{
          lastSuccessAt: null,
          lastFailure: null,
          inProgress: null,
          historyWeeks: null,
          coveredFrom: null,
          coveredThrough: null,
        }}
      />,
    )
    expect(html).toContain('Coverage window unknown')
  })
})

/**
 * T9 web ingest + coverage panel (issues #189/#198/#199): the ingest
 * activity panel. The banner suite above is T10's hunk — merge-keep-both.
 */
describe('ContextDoctor ingest + coverage panel (issues #189/#198/#199, T9)', () => {
  const KNOWN_COVERAGE: KyberCoverage = {
    refresh: {
      lastSuccessAt: '2026-09-30T20:52:54.000Z',
      lastFailure: null,
      inProgress: null,
      historyWeeks: 2,
      coveredFrom: '2026-09-16T20:52:54.000Z',
      coveredThrough: '2026-09-30T20:52:54.000Z',
    },
    ingest: {
      status: 'known',
      lastReceivedAt: '2026-09-30T20:00:00.000Z',
      sources: [
        {
          source: 'codeburn/pi',
          display: 'pi',
          kind: 'local-file',
          recordCount: 863,
          ingestedCount: 0,
          lastReceivedAt: null,
        },
        {
          source: 'agy',
          display: 'agy',
          kind: 'otlp',
          recordCount: 20794,
          ingestedCount: 1200,
          lastReceivedAt: '2026-09-30T20:00:00.000Z',
        },
      ],
    },
    quarantineByReason: [{ reason: 'unclaimed', count: 430536 }],
    checkpoints: [
      {
        harnessId: 'copilot-cli',
        sourceKey: 'session:agent-7f3',
        providerId: 'copilot',
        parserId: 'copilot-cli',
        parserContractVersion: '1',
        format: 'jsonl',
        sourceRootLabel: '~/.copilot/session-state',
        revisionToken: 'rev-1',
        coveredFromUtc: '2026-09-16T00:00:00.000Z',
        coveredThroughUtc: '2026-09-30T00:00:00.000Z',
        lastAttemptUtc: '2026-09-30T20:52:54.000Z',
        lastSuccessUtc: null,
        lastStatus: 'partial',
        lastErrorCode: null,
        unitCount: 1,
        recordCount: 0,
      },
    ],
  }

  const UNKNOWN_COVERAGE: KyberCoverage = {
    refresh: {
      lastSuccessAt: null,
      lastFailure: null,
      inProgress: null,
      historyWeeks: null,
      coveredFrom: null,
      coveredThrough: null,
    },
    ingest: {
      status: 'unknown',
      reason: 'no receiver activity recorded',
      sources: [],
      lastReceivedAt: null,
    },
    quarantineByReason: [],
    checkpoints: [],
  }

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('fetchCoverage reads GET /api/kyber/coverage', async () => {
    const payload = KNOWN_COVERAGE
    const json = vi.fn(async () => payload)
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({ ok: true, json }) as unknown as Response),
    )
    const result = await fetchCoverage()
    expect(fetch).toHaveBeenCalledWith('/api/kyber/coverage')
    expect(result.ingest.status).toBe('known')
    expect(result.quarantineByReason).toEqual([{ reason: 'unclaimed', count: 430536 }])
  })

  it('renders last-received, per-source display names, and the partial-checkpoint line', () => {
    const html = renderWithQuery(<ContextDoctor initialCoverage={KNOWN_COVERAGE} />)
    // Last-received line carries the timestamp and a relative age, never a liveness claim.
    expect(html).toContain('last received 2026-09-30T20:00:00.000Z')
    expect(html).toContain('ago')
    expect(html).not.toContain('running')
    // Per-source counts use T7 display names; the stored codeburn/ id rides
    // along only as a raw-source audit attribute, never as the shown harness.
    expect(html).toContain('>pi<')
    expect(html).toContain('863')
    expect(html).toContain('data-raw-source="codeburn/pi"')
    expect(html).not.toContain('>codeburn/pi<')
    // Partial checkpoint state is explicit, with its zero-record count stated.
    expect(html).toContain('1 source partial (problems recorded, coverage incomplete)')
    expect(html).toContain('0 records')
    // Quarantine reasons stay verbatim with their counts.
    expect(html).toContain('unclaimed')
    expect(html).toContain('430536')
  })

  it('renders unknown receiver status without inventing zeros', () => {
    const html = renderWithQuery(<ContextDoctor initialCoverage={UNKNOWN_COVERAGE} />)
    expect(html).toContain(
      'no receiver activity recorded — receiver status is not observable from this page',
    )
    expect(html).not.toContain('running')
    // Unknown is never a zero: no zero counts anywhere in the ingest panel.
    const panel = html.slice(html.indexOf('data-testid="coverage-ingest-panel"'))
    expect(panel).not.toContain('>0<')
  })

  it('renders unknown checkpoint status when checkpoints is null, never a zero', () => {
    // MUST FIX thread 4150060217 RED: null checkpoints (unreadable read)
    // must render an explicit unknown line; [] (genuine zero) keeps no
    // partial section.
    const nullCoverage = {
      ...UNKNOWN_COVERAGE,
      checkpoints: null,
    } as unknown as KyberCoverage
    const html = renderWithQuery(<ContextDoctor initialCoverage={nullCoverage} />)
    expect(html).toContain('data-testid="coverage-checkpoints-unknown"')
    expect(html).toContain('checkpoint status not observable from this page')
  })

  it('renders no partial section for an empty checkpoint list (genuine zero)', () => {
    const html = renderWithQuery(<ContextDoctor initialCoverage={UNKNOWN_COVERAGE} />)
    expect(html).not.toContain('data-testid="coverage-checkpoints-unknown"')
    expect(html).not.toContain('data-testid="coverage-partial"')
  })

  it('renders an unknown coverage window when the run predates window tracking', () => {
    const html = renderWithQuery(<ContextDoctor initialCoverage={UNKNOWN_COVERAGE} />)
    expect(html).toContain('Coverage window unknown')
    expect(html).not.toContain('last 0 weeks')
  })

  it('states a fresh store distinctly from a legacy tracked run', () => {
    // Review PR #230 (copilot numso): historyWeeks null on a fresh store
    // (lastSuccessAt null) must not read as a run recorded before tracking.
    const fresh = renderWithQuery(<ContextDoctor initialCoverage={UNKNOWN_COVERAGE} />)
    expect(fresh).toContain('no successful refresh recorded')

    const legacyCoverage: KyberCoverage = {
      ...UNKNOWN_COVERAGE,
      refresh: { ...UNKNOWN_COVERAGE.refresh, lastSuccessAt: '2026-09-19T11:00:00.000Z' },
    }
    const legacy = renderWithQuery(<ContextDoctor initialCoverage={legacyCoverage} />)
    expect(legacy).toContain('recorded before window tracking')
    expect(legacy).not.toContain('no successful refresh recorded')
  })

  it('keeps the raw stored source out of user-visible tooltips', () => {
    // Review PR #230 (copilot nums8): the audit-only data attribute stays,
    // but the browser tooltip must not leak the raw codeburn/ namespace.
    const html = renderWithQuery(<ContextDoctor initialCoverage={KNOWN_COVERAGE} />)
    expect(html).toContain('data-raw-source="codeburn/pi"')
    expect(html).not.toContain('title="stored source:')
  })

  it('states the window once, with short dates and correct plurals', () => {
    // Review PR #230 (kilo nux6A): the panel formatted the same three fields
    // a second time with raw ISO stamps and a hardcoded plural — it now
    // reads the matrix banner's shared formatter.
    const oneWeek: KyberCoverage = {
      ...KNOWN_COVERAGE,
      refresh: {
        ...KNOWN_COVERAGE.refresh,
        historyWeeks: 1,
        coveredFrom: '2026-09-23T20:52:54.000Z',
        coveredThrough: '2026-09-30T20:52:54.000Z',
      },
    }
    const html = renderWithQuery(<ContextDoctor initialCoverage={oneWeek} />)
    expect(html).toContain('last 1 week (2026-09-23 → 2026-09-30)')
    expect(html).not.toContain('last 1 weeks')
    expect(html).not.toContain('2026-09-23T20:52:54.000Z → 2026-09-30T20:52:54.000Z')
  })
})

describe('formatCoverageAgo (ingest panel)', () => {
  it('reads a future timestamp as clock skew, never as just now', async () => {
    // Review PR #230 (kilo nux6E): Math.max(0, ...) folded future stamps
    // into `just now` — a claim the data does not support.
    const { formatCoverageAgo } = await import('./ContextDoctor.js')
    expect(formatCoverageAgo('2026-09-30T20:00:00.000Z', Date.parse('2026-09-30T19:00:00.000Z'))).toMatch(
      /future|clock skew/i,
    )
    expect(formatCoverageAgo('2026-09-30T20:00:00.000Z', Date.parse('2026-09-30T20:00:30.000Z'))).toBe('just now')
    expect(formatCoverageAgo('not-a-timestamp')).toBe('unknown age')
  })
})

describe('browser paging races (review M3)', () => {
  it('derives the next offset from loaded rows, not from repeated clicks', () => {
    // Two quick clicks while a fetch is in flight must not advance past the
    // rows on screen: the offset is the loaded count, which cannot move
    // until new rows arrive.
    expect(nextBrowserOffset(25, 115)).toBe(25)
    expect(nextBrowserOffset(25, 115)).toBe(25)
    expect(nextBrowserOffset(115, 115)).toBeUndefined()
    expect(nextBrowserOffset(0, 0)).toBeUndefined()
  })

  it('never treats placeholder rows as the current page', () => {
    const rows = [{ id: 'x' } as unknown as import('../lib/kyberApi.js').KyberFinding]
    expect(currentBrowserPage(rows, false)).toBe(rows)
    expect(currentBrowserPage(rows, true)).toBeUndefined()
    expect(currentBrowserPage(undefined, false)).toBeUndefined()
  })

  it('disables Load more while a fetch is in flight', () => {
    const fifty = Array.from({ length: 50 }, (_, i) =>
      finding(`h-${i + 1}`, 'duplicate-tool-call', 'cursor'),
    )
    const html = renderToStaticMarkup(
      <FindingsBrowserView
        findings={fifty}
        total={115}
        detectorCounts={{ 'duplicate-tool-call': 49 }}
        loading={true}
        harnesses={[]}
      />,
    )
    expect(html).toContain('findings-load-more')
    expect(html).toContain('50 of 115')
    expect(html).toMatch(/<button[^>]*disabled[^>]*data-testid="findings-load-more"|<button[^>]*data-testid="findings-load-more"[^>]*disabled/)
  })
})

describe('FindingsBrowserView complete set (review optional)', () => {
  it('has no load-more button when every finding is shown', () => {
    const rows = Array.from({ length: 115 }, (_, i) =>
      finding(`w-${i + 1}`, 'duplicate-tool-call', 'cursor'),
    )
    const html = renderToStaticMarkup(
      <FindingsBrowserView
        findings={rows}
        total={115}
        detectorCounts={{ 'duplicate-tool-call': 115 }}
        loading={false}
        harnesses={[]}
      />,
    )
    expect(html).not.toContain('findings-load-more')
    expect(html).toContain('All Workspace Findings (115)')
  })
})

describe('FindingsBrowserView query errors (council review)', () => {
  it('surfaces a bounded error with retry instead of stale rows', () => {
    const onRetry = () => {}
    const html = renderToStaticMarkup(
      <FindingsBrowserView
        findings={[]}
        total={0}
        detectorCounts={{}}
        loading={false}
        harnesses={[]}
        error="boom: ECONNREFUSED 127.0.0.1:4747 :: connection string secret=abc"
        onRetry={onRetry}
      />,
    )
    expect(html).toContain('findings-error')
    expect(html).toContain('Findings failed to load')
    expect(html).not.toContain('ECONNREFUSED')
    expect(html).not.toContain('secret=abc')
  })

  it('shows rows, not the error panel, when both are present', () => {
    const rows = [finding('e-1', 'duplicate-tool-call', 'cursor')]
    const html = renderToStaticMarkup(
      <FindingsBrowserView findings={rows} total={1} detectorCounts={{}} loading={false} harnesses={[]} error="x" />,
    )
    expect(html).not.toContain('findings-error')
    expect(html).toContain('finding-card-e-1')
  })
})
