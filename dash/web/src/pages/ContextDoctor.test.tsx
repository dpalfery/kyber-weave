// @vitest-environment happy-dom
// The rendered suites below mount the real component with @testing-library/react,
// so this file runs under happy-dom (the static-markup suites above are unaffected:
// renderToStaticMarkup needs no DOM). dash/vitest.config.ts is left untouched.
import { describe, it, expect, vi, afterEach } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
// Aliased, because the module-scope `render` below is the static-markup helper
// and a plain `render` import would be shadowed by it at every call site.
import { render as renderDom, screen, fireEvent, cleanup, within, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { useState } from 'react'

import { ContextDoctor, browserRows, nextAccumulated, retainedEnvelope, servedOffset, FindingsBrowserView, nextBrowserOffset, currentBrowserPage, shouldKeepPreviousData, type AccumulatedPage } from './ContextDoctor.js'
import { CoverageIngestPanel } from '../components/maintenance/CoverageIngestPanel.js'
import { fetchCoverage, type FindingsPage, type KyberCoverageRefresh, type KyberFinding, type KyberHarnessSummary, type KyberCoverage } from '../lib/kyberApi.js'

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
      <ContextDoctor initialFindings={page} />
    </QueryClientProvider>,
  )
}

/**
 * A findings page for the pure paging helpers. The envelope travels with the
 * rows, because the rows are only meaningful under it — the same shape the
 * browser query hands `nextAccumulated`.
 */
function findingsPage(
  rows: readonly KyberFinding[],
  offset: number,
  total: number,
  unknownWindowSessions = 0,
): FindingsPage {
  return {
    findings: [...rows],
    offset,
    total,
    detectorCounts: {},
    unknownWindowSessions,
  }
}

/** A findings page for the rendered suites; the envelope is overridable. */
function browserPage(
  ids: string[],
  detectorCounts: Record<string, number>,
  harness = 'cursor',
  envelope: { total?: number; unknownWindowSessions?: number } = {},
): FindingsPage {
  return {
    findings: ids.map((id, i) => finding(id, `det-${i}`, harness)),
    total: envelope.total ?? ids.length,
    offset: 0,
    detectorCounts,
    unknownWindowSessions: envelope.unknownWindowSessions ?? 0,
  }
}

const renderedHarnesses: KyberHarnessSummary[] = [
  { ...harnessRow, harness: 'cursor', name: 'Cursor' },
  { ...harnessRow, harness: 'claude-code', name: 'Claude Code' },
]

const emptyCoverage: KyberCoverage = {
  refresh: {
    lastSuccessAt: null,
    lastFailure: null,
    inProgress: null,
    historyWeeks: null,
    coveredFrom: null,
    coveredThrough: null,
  },
  ingest: { status: 'unknown', reason: 'test stub', sources: [], lastReceivedAt: null },
  quarantineByReason: [],
  checkpoints: [],
}

function renderDoctor(initial: FindingsPage): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  renderDom(
    <QueryClientProvider client={client}>
      <ContextDoctor initialFindings={initial} />
    </QueryClientProvider>,
  )
}

/** Canonical sessions the session selector offers, one per harness. */
const SCOPE_SESSIONS = [
  { session_id: 'sess-pi-1', harness: 'pi', label: 'pi run' },
  { session_id: 'sess-pi-2', harness: 'pi', label: null },
  { session_id: 'sess-cursor-1', harness: 'cursor', label: 'cursor run' },
]

function browserSection(): HTMLElement {
  return screen.getByTestId('all-workspace-findings')
}

/**
 * Queries scoped to the browser section. The headline card above reads the
 * same initial rows (shared initial page), so global card testids are
 * ambiguous — every row assertion lives inside the browser section.
 */
function browser() {
  return within(browserSection())
}

/**
 * Stubs every endpoint ContextDoctor queries, serving the findings pages the
 * browser's offset walk asks for and recording the offsets requested. Offset 0
 * arrives with no `offset` param (`fetchFindings` drops a zero offset as
 * falsy), so the mount refetch and the headline card both read `first`.
 * Requests carrying an offset wait on `release`, so a mid-flight assertion is
 * deterministic — no real timers, no real network — and `failAt` turns one of
 * them into a bounded fetch failure (F2).
 */
function stubPagedFetch(opts: {
  first: FindingsPage
  byOffset?: Readonly<Record<string, FindingsPage>>
  failAt?: readonly string[]
  release?: () => Promise<void>
}): string[] {
  const requested: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string) => {
      const url = new URL(input, 'http://localhost')
      const json = (payload: unknown) => ({ ok: true, json: async () => payload }) as unknown as Response
      if (url.pathname === '/api/kyber/sessions') return json({ sessions: SCOPE_SESSIONS })
      if (url.pathname === '/api/kyber/harnesses') return json({ harnesses: renderedHarnesses })
      if (url.pathname === '/api/kyber/runs') return json({ runs: [] })
      if (url.pathname === '/api/kyber/coverage') return json(emptyCoverage)
      if (url.pathname === '/api/kyber/findings') {
        const offset = url.searchParams.get('offset')
        if (offset === null) return json(opts.first)
        requested.push(offset)
        if (opts.release) await opts.release()
        if (opts.failAt?.includes(offset)) throw new Error(`offset ${offset} failed`)
        return json(opts.byOffset?.[offset] ?? opts.first)
      }
      throw new Error(`unstubbed fetch: ${input}`)
    }),
  )
  return requested
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
  /** A stored page, envelope included — what the accumulate effect writes. */
  const page = (scope: string, offset: number, prefix: string, n: number): AccumulatedPage => ({
    scope,
    offset,
    total: 115,
    detectorCounts: {},
    unknownWindowSessions: 0,
    rows: rows(prefix, n),
  })

  it('renders page one before the effect has stored it (static render)', () => {
    // Only the first page exists at this point; nothing has been accumulated.
    expect(browserRows([], scope, 0, rows('p0', 25)).map((r) => r.id)).toEqual(
      rows('p0', 25).map((r) => r.id),
    )
  })

  it('extends the list with page two rather than replacing page one', () => {
    const stored = [page(scope, 0, 'p0', 25), page(scope, 25, 'p1', 25)]
    const ids = browserRows(stored, scope, 25, undefined).map((r) => r.id)
    expect(ids).toHaveLength(50)
    expect(ids).toContain('p0-1')
    expect(ids).toContain('p1-25')
  })

  it('orders pages by offset regardless of arrival order', () => {
    const stored = [page(scope, 50, 'p2', 25), page(scope, 0, 'p0', 25), page(scope, 25, 'p1', 25)]
    const ids = browserRows(stored, scope, 50, undefined).map((r) => r.id)
    expect(ids[0]).toBe('p0-1')
    expect(ids[25]).toBe('p1-1')
    expect(ids[50]).toBe('p2-1')
  })

  it('never re-appends a page fetched under a previous filter scope', () => {
    const stored = [page(other, 0, 'narrowed', 25), page(scope, 0, 'p0', 25)]
    const ids = browserRows(stored, scope, 0, undefined).map((r) => r.id)
    expect(ids).toHaveLength(25)
    expect(ids.some((id) => id.startsWith('narrowed'))).toBe(false)
  })
})

describe("nextAccumulated — the first page must be stored (issue #191)", () => {
  const scope = "|"
  const rows = (prefix: string) => [finding(prefix, "duplicate-tool-call", "cursor")]
  const page = (prefix: string, offset: number, total = 2) =>
    findingsPage(rows(prefix), offset, total)

  it("stores page zero instead of skipping it", () => {
    // Skipping offset 0 is what made Load more replace the list: page one was
    // never in the accumulated list, so there was nothing to extend.
    expect(nextAccumulated([], scope, page("p0", 0))).toHaveLength(1)
  })

  it("is a no-op when the same page arrives again", () => {
    const once = nextAccumulated([], scope, page("p0", 0))
    expect(nextAccumulated(once, scope, page("p0", 0))).toHaveLength(1)
  })

  it("stores the same offset under a different scope", () => {
    const once = nextAccumulated([], scope, page("p0", 0))
    expect(nextAccumulated(once, "duplicate-tool-call|", page("p1", 0))).toHaveLength(2)
  })

  it("accumulates page two alongside page one", () => {
    const both = nextAccumulated(nextAccumulated([], scope, page("p0", 0)), scope, page("p1", 25))
    expect(both.map((p) => p.offset)).toEqual([0, 25])
  })

  it("returns the same reference when nothing changed", () => {
    // The accumulate effect depends on the stored list, so a no-op that
    // returns a copy re-renders forever (plan risk: same-reference no-op).
    const once = nextAccumulated([], scope, page("p0", 0))
    expect(nextAccumulated(once, scope, page("p0", 0))).toBe(once)
  })
})

describe("nextAccumulated — a moved total invalidates the scope (F4)", () => {
  const scope = "|"
  const rows = (prefix: string, n: number) =>
    Array.from({ length: n }, (_, i) => finding(`${prefix}-${i + 1}`, "duplicate-tool-call", "cursor"))

  it("stores only the new page when the server's total moves", () => {
    // A rebuild deleted or re-ranked rows inside the window page one covers.
    // Its rows were ranked under a ranking that no longer exists, so they are
    // dropped rather than left on screen for the life of the mount (F4).
    const stored = nextAccumulated([], scope, findingsPage(rows("stale", 25), 0, 115))
    const rebuilt = nextAccumulated(stored, scope, findingsPage(rows("fresh", 25), 25, 90))
    const ids = browserRows(rebuilt, scope, 25, undefined).map((r) => r.id)
    expect(ids).toEqual(rows("fresh", 25).map((r) => r.id))
    expect(ids.some((id) => id.startsWith("stale"))).toBe(false)
  })

  it("keeps the scope when the total is unchanged", () => {
    const stored = nextAccumulated([], scope, findingsPage(rows("a", 25), 0, 115))
    const grown = nextAccumulated(stored, scope, findingsPage(rows("b", 25), 25, 115))
    expect(browserRows(grown, scope, 25, undefined)).toHaveLength(50)
  })
})

describe("retainedEnvelope — the last envelope the rows were served under (F2)", () => {
  const scope = "|"

  it("is undefined when the scope has nothing stored", () => {
    expect(retainedEnvelope([], scope)).toBeUndefined()
  })

  it("carries the total and the unknown-window count, never a zero", () => {
    // A mid-list failure clears `data`, and the heading, chips and suppression
    // banner must read the last envelope the retained rows were served under
    // — a fabricated 0 reads as "all clear" (F2).
    const stored = nextAccumulated([], scope, findingsPage([], 0, 115, 3))
    expect(retainedEnvelope(stored, scope)).toEqual({
      total: 115,
      detectorCounts: {},
      unknownWindowSessions: 3,
    })
  })

  it("returns the newest stored page for the scope only", () => {
    const stored = nextAccumulated([], scope, findingsPage([], 0, 115, 3))
    expect(retainedEnvelope(stored, "duplicate-tool-call|")).toBeUndefined()
  })
})

describe("servedOffset — the extent the server served, not the rows painted (F3)", () => {
  const scope = "|"
  const rows = (prefix: string, n: number) =>
    Array.from({ length: n }, (_, i) => finding(`${prefix}-${i + 1}`, "duplicate-tool-call", "cursor"))
  /** A stored page, envelope included — what the accumulate effect writes. */
  const page = (scope: string, offset: number, prefix: string, n: number): AccumulatedPage => ({
    scope,
    offset,
    total: 115,
    detectorCounts: {},
    unknownWindowSessions: 0,
    rows: rows(prefix, n),
  })

  it("is the contiguous run from offset zero, not the highest offset plus its rows", () => {
    // Page two repeats page one's last row (a rebuild shifted the window).
    // `browserRows` dedupes it, so the painted count is 5 while the server
    // extent is 6 — paging from the painted count re-requests a served row.
    const p0 = rows("s", 3)
    const shifted = [p0[2]!, ...rows("n", 2)]
    const stored = nextAccumulated(
      nextAccumulated([], scope, findingsPage(p0, 0, 115)),
      scope,
      findingsPage(shifted, 3, 115),
    )
    expect(browserRows(stored, scope, 3, undefined)).toHaveLength(5)
    expect(servedOffset(stored, scope)).toBe(6)
    expect(nextBrowserOffset(servedOffset(stored, scope), 115)).toBe(6)
  })

  it("counts nothing when the scope's only stored page is at a non-zero offset", () => {
    // The F4 restart drops the scope and stores the page the rebuild was caught
    // at (offset 25), then resets the offset to 0. Reporting 25 as the served
    // extent hands `onLoadMore` the offset it is already sitting on, so every
    // click refetches the page already in the list (F3).
    const stranded = nextAccumulated([], scope, findingsPage(rows("late", 25), 25, 115))
    expect(servedOffset(stranded, scope)).toBe(0)
  })

  it("walks the stored list in offset order, not arrival order", () => {
    // The F4 restart stores the rebuilt page at 25 first; page 0 re-arrives
    // second, leaving the list as [p25, p0]. Trusting arrival order skips p25,
    // never returns for it, and reports 25 — with p25's own rows unfetched and
    // everything past 50 unreachable.
    const stored = nextAccumulated(
      nextAccumulated([], scope, findingsPage(rows("late", 25), 25, 115)),
      scope,
      findingsPage(rows("head", 25), 0, 115),
    )
    expect(stored.map((p) => p.offset)).toEqual([25, 0])
    expect(servedOffset(stored, scope)).toBe(50)
    expect(nextBrowserOffset(servedOffset(stored, scope), 115)).toBe(50)
  })

  it("stops at a hole rather than counting the pages stored beyond it", () => {
    const stored = [page(scope, 0, "p0", 25), page(scope, 50, "p2", 25)]
    expect(servedOffset(stored, scope)).toBe(25)
  })

  it("ends the run at a page that serves zero rows", () => {
    // A page that serves nothing advances no extent, so the run ends there and
    // the page stored beyond it is not counted.
    const stored = [page(scope, 0, "p0", 25), page(scope, 25, "empty", 0), page(scope, 50, "p2", 25)]
    expect(servedOffset(stored, scope)).toBe(25)
  })

  it("is zero when the scope has nothing stored", () => {
    expect(servedOffset([], scope)).toBe(0)
  })

  it("counts only the requested scope", () => {
    const stored = nextAccumulated([], "duplicate-tool-call|", findingsPage(rows("n", 3), 0, 115))
    expect(servedOffset(stored, scope)).toBe(0)
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
      <FindingsBrowserView findings={[]} total={115} detectorCounts={counts} loading={true} />,
    )
    expect(loading).toContain('skeleton-shimmer')
    const loaded = renderToStaticMarkup(
      <FindingsBrowserView findings={fifty} total={115} detectorCounts={counts} loading={true} />,
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
      />,
    )
    expect(html).toContain('findings-clear-filters')
  })

  it('hides the clear control when no filter is active', () => {
    const html = renderToStaticMarkup(
      <FindingsBrowserView findings={[]} total={0} detectorCounts={counts} loading={false} />,
    )
    expect(html).not.toContain('findings-clear-filters')
  })
})
/**
 * Coverage-window wording (issues #189/#199, T10). Pinned on the component
 * that renders the readout now that it lives in the Status panel.
 */
describe('Coverage window wording (issues #189/#199, T10)', () => {
  /** The refresh facts, wrapped in the full payload the readout renders. */
  const coverageFromRefresh = (refresh: KyberCoverageRefresh): KyberCoverage => ({
    refresh,
    ingest: { status: 'unknown', reason: 'not recorded', sources: [], lastReceivedAt: null },
    quarantineByReason: [],
    checkpoints: [],
  })

  it('states a two-week tracked window', () => {
    const html = renderWithQuery(
      <CoverageIngestPanel
        coverage={coverageFromRefresh({
          lastSuccessAt: '2026-09-30T00:00:00.000Z',
          lastFailure: null,
          inProgress: null,
          historyWeeks: 2,
          coveredFrom: '2026-09-16T00:00:00.000Z',
          coveredThrough: '2026-09-30T00:00:00.000Z',
        })}
      />,
    )
    expect(html).toContain('Coverage: last 2 weeks')
  })

  it('states an unknown window honestly instead of defaulting to 2', () => {
    const html = renderWithQuery(
      <CoverageIngestPanel
        coverage={coverageFromRefresh({
          lastSuccessAt: null,
          lastFailure: null,
          inProgress: null,
          historyWeeks: null,
          coveredFrom: null,
          coveredThrough: null,
        })}
      />,
    )
    expect(html).toContain('Coverage window unknown')
  })
})

/**
 * T9 ingest + coverage readout (issues #189/#198/#199). The panel moved out
 * of Context Doctor into the Status panel; its assertions are unchanged.
 */
describe('Ingest coverage panel (issues #189/#198/#199, T9)', () => {
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
    const html = renderWithQuery(<CoverageIngestPanel coverage={KNOWN_COVERAGE} />)
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

  it('renders zero-record sources with their persisted reason, grouped and verbatim (#196)', () => {
    const unit = (sourceKey: string, lastErrorCode: string | null, recordCount = 0) => ({
      ...KNOWN_COVERAGE.checkpoints![0]!,
      harnessId: 'codex-cli',
      sourceKey,
      lastStatus: 'ok',
      lastErrorCode,
      recordCount,
    })
    const coverage: KyberCoverage = {
      ...KNOWN_COVERAGE,
      checkpoints: [
        unit('a', 'window_filtered'),
        unit('b', 'window_filtered'),
        unit('c', 'no_recordable_events'),
        unit('d', null, 4),
      ],
    }
    const html = renderWithQuery(<CoverageIngestPanel coverage={coverage} />)
    expect(html).toContain('data-testid="coverage-zero-record"')
    expect(html).toContain('3 sources discovered but not ingested')
    expect(html).toContain('window_filtered')
    expect(html).toContain('no_recordable_events')
    const rows = html.match(/data-testid="coverage-zero-record-reason"/g) ?? []
    expect(rows).toHaveLength(2)
  })

  it('renders no zero-record section when every ok checkpoint has records', () => {
    const html = renderWithQuery(<CoverageIngestPanel coverage={UNKNOWN_COVERAGE} />)
    expect(html).not.toContain('data-testid="coverage-zero-record"')
  })

  it('renders unknown receiver status without inventing zeros', () => {
    const html = renderWithQuery(<CoverageIngestPanel coverage={UNKNOWN_COVERAGE} />)
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
    const html = renderWithQuery(<CoverageIngestPanel coverage={nullCoverage} />)
    expect(html).toContain('data-testid="coverage-checkpoints-unknown"')
    expect(html).toContain('checkpoint status not observable from this page')
  })

  it('renders no partial section for an empty checkpoint list (genuine zero)', () => {
    const html = renderWithQuery(<CoverageIngestPanel coverage={UNKNOWN_COVERAGE} />)
    expect(html).not.toContain('data-testid="coverage-checkpoints-unknown"')
    expect(html).not.toContain('data-testid="coverage-partial"')
  })

  it('renders an unknown coverage window when the run predates window tracking', () => {
    const html = renderWithQuery(<CoverageIngestPanel coverage={UNKNOWN_COVERAGE} />)
    expect(html).toContain('Coverage window unknown')
    expect(html).not.toContain('last 0 weeks')
  })

  it('states a fresh store distinctly from a legacy tracked run', () => {
    // Review PR #230 (copilot numso): historyWeeks null on a fresh store
    // (lastSuccessAt null) must not read as a run recorded before tracking.
    const fresh = renderWithQuery(<CoverageIngestPanel coverage={UNKNOWN_COVERAGE} />)
    expect(fresh).toContain('no successful refresh recorded')

    const legacyCoverage: KyberCoverage = {
      ...UNKNOWN_COVERAGE,
      refresh: { ...UNKNOWN_COVERAGE.refresh, lastSuccessAt: '2026-09-19T11:00:00.000Z' },
    }
    const legacy = renderWithQuery(<CoverageIngestPanel coverage={legacyCoverage} />)
    expect(legacy).toContain('recorded before window tracking')
    expect(legacy).not.toContain('no successful refresh recorded')
  })

  it('keeps the raw stored source out of user-visible tooltips', () => {
    // Review PR #230 (copilot nums8): the audit-only data attribute stays,
    // but the browser tooltip must not leak the raw codeburn/ namespace.
    const html = renderWithQuery(<CoverageIngestPanel coverage={KNOWN_COVERAGE} />)
    expect(html).toContain('data-raw-source="codeburn/pi"')
    expect(html).not.toContain('title="stored source:')
  })

  it('states the window once, with short dates and correct plurals', () => {
    // Review PR #230 (kilo nux6A): the panel formatted the same three fields
    // a second time with raw ISO stamps and a hardcoded plural — it now
    // reads the shared coverage-window formatter.
    const oneWeek: KyberCoverage = {
      ...KNOWN_COVERAGE,
      refresh: {
        ...KNOWN_COVERAGE.refresh,
        historyWeeks: 1,
        coveredFrom: '2026-09-23T20:52:54.000Z',
        coveredThrough: '2026-09-30T20:52:54.000Z',
      },
    }
    const html = renderWithQuery(<CoverageIngestPanel coverage={oneWeek} />)
    expect(html).toContain('last 1 week (2026-09-23 → 2026-09-30)')
    expect(html).not.toContain('last 1 weeks')
    expect(html).not.toContain('2026-09-23T20:52:54.000Z → 2026-09-30T20:52:54.000Z')
  })
})

describe('formatCoverageAgo (ingest panel)', () => {
  it('reads a future timestamp as clock skew, never as just now', async () => {
    // Review PR #230 (kilo nux6E): Math.max(0, ...) folded future stamps
    // into `just now` — a claim the data does not support.
    const { formatCoverageAgo } = await import('../components/maintenance/CoverageIngestPanel.js')
    expect(formatCoverageAgo('2026-09-30T20:00:00.000Z', Date.parse('2026-09-30T19:00:00.000Z'))).toMatch(
      /future|clock skew/i,
    )
    expect(formatCoverageAgo('2026-09-30T20:00:00.000Z', Date.parse('2026-09-30T20:00:30.000Z'))).toBe('just now')
    expect(formatCoverageAgo('not-a-timestamp')).toBe('unknown age')
  })
})

describe('browser paging races (review M3)', () => {
  it('derives the next offset from the extent the server served', () => {
    // `served` is `servedOffset` over the stored pages: the extent of the
    // contiguous run from offset 0 the server has answered for. It cannot move
    // while a fetch is in flight — two quick clicks on Load more resolve to
    // the same offset instead of skipping a page — and unlike the painted row
    // count it survives a deduped overlap (F3). These are
    // `nextBrowserOffset`'s own contract; only the argument's provenance
    // changed.
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
      />,
    )
    expect(html).toContain('findings-load-more')
    expect(html).toContain('50 of 115')
    expect(html).toMatch(/<button[^>]*disabled[^>]*data-testid="findings-load-more"|<button[^>]*data-testid="findings-load-more"[^>]*disabled/)
  })
})

describe('shouldKeepPreviousData — placeholder scope for the findings browser', () => {
  const key = (detector: string, harness: string, session: string, offset: number): readonly unknown[] => [
    'kyber-findings-browser',
    detector,
    harness,
    session,
    offset,
  ]

  it('keeps previous rows when only the offset changed', () => {
    expect(shouldKeepPreviousData(key('', '', '', 0), key('', '', '', 25))).toBe(true)
  })

  it('drops previous rows when the detector filter changed', () => {
    // Harness slot identical — the detector slot alone must drop the data.
    expect(
      shouldKeepPreviousData(key('', 'cursor', '', 0), key('duplicate-tool-call', 'cursor', '', 0)),
    ).toBe(false)
  })

  it('drops previous rows when the harness filter changed', () => {
    // Detector slot identical — the harness slot alone must drop the data.
    expect(shouldKeepPreviousData(key('', '', '', 0), key('', 'cursor', '', 0))).toBe(false)
  })

  it('drops previous rows when the session scope changed', () => {
    // A session is a narrower scope than a harness: page one of the previous
    // session's list must not pose as page one of this one's.
    expect(shouldKeepPreviousData(key('', '', '', 0), key('', '', 'sess-1', 0))).toBe(false)
  })

  it('drops previous rows when both filters changed', () => {
    expect(
      shouldKeepPreviousData(key('', '', '', 0), key('duplicate-tool-call', 'cursor', '', 0)),
    ).toBe(false)
  })

  it('drops previous rows when there is no previous query key', () => {
    expect(shouldKeepPreviousData(undefined, key('', '', '', 0))).toBe(false)
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
        error="boom: ECONNREFUSED 127.0.0.1:4747 :: connection string secret=abc"
        onRetry={onRetry}
      />,
    )
    expect(html).toContain('findings-error')
    expect(html).toContain('Findings failed to load')
    expect(html).not.toContain('ECONNREFUSED')
    expect(html).not.toContain('secret=abc')
  })

  it('keeps the rows and offers retry inline when a page fails mid-list', () => {
    // F2, contract change: rows on screen plus a failure is a retryable wedge,
    // not a reason to hide what the user already read. The full-panel
    // `findings-error` is the no-rows state only — it is what made the
    // mid-list failure indistinguishable from "you have seen everything".
    const rows = [finding('e-1', 'duplicate-tool-call', 'cursor')]
    const html = renderToStaticMarkup(
      <FindingsBrowserView findings={rows} total={1} detectorCounts={{}} loading={false} error="x" />,
    )
    expect(html).toContain('finding-card-e-1')
    expect(html).toContain('findings-inline-error')
    expect(html).toContain('findings-retry')
  })
})

describe('browserRows staleness and overlap (review)', () => {
  const rows = (prefix: string, n: number, from = 1): import('../lib/kyberApi.js').KyberFinding[] =>
    Array.from({ length: n }, (_, i) => finding(`${prefix}-${from + i}`, 'duplicate-tool-call', 'cursor'))

  it('dedupes a shifted page by id instead of double-rendering', () => {
    // A rebuild inserts one higher-ranked finding: the whole window shifts
    // by one and page 2's first row repeats page 1's last row.
    const p0 = rows('s', 25)
    const shifted = [p0[24]!, ...rows('n', 24)]
    const stored = nextAccumulated(
      nextAccumulated([], '|', findingsPage(p0, 0, 115)),
      '|',
      findingsPage(shifted, 25, 115),
    )
    const flat = browserRows(stored, '|', 25, undefined)
    const ids = flat.map((r) => r.id)
    expect(flat).toHaveLength(49)
    expect(new Set(ids).size).toBe(49)
  })

  it('replaces a stored page whose rows changed instead of keeping stale rows', () => {
    const first = nextAccumulated([], '|', findingsPage(rows('a', 25), 0, 115))
    const second = nextAccumulated(first, '|', findingsPage(rows('b', 25), 0, 115))
    const flat = browserRows(second, '|', 0, undefined)
    expect(flat.map((r) => r.id)).toEqual(rows('b', 25).map((r) => r.id))
  })
})

/**
 * Rendered filter-change regression (must-fix-1 review): mounting the real
 * `ContextDoctor` with a real QueryClient and stubbed fetch, then changing a
 * filter must drop the old filter's rows at once — no placeholder pose, no
 * stale re-append, no skeleton over stored rows. Unit suites above pin the
 * helpers; these pin the wired path (query key + placeholderData +
 * currentBrowserPage + the resetBrowse effect).
 */
describe('ContextDoctor filter change — rendered (must-fix-1)', () => {
  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  /**
   * Stubs every endpoint ContextDoctor queries. Requests carrying the *new*
   * filter wait on a gate the test releases, so the mid-flight assertions
   * observe the loading state deterministically — no real timers, no real
   * network. Unfiltered findings requests always serve `initial`: the offset-0
   * browser page and the headline card share one URL (`fetchFindings` drops
   * a zero offset as falsy), so both read the same rows — the browser-scoped
   * assertions below are unaffected, and the mount refetch is a same-ids
   * no-op in `nextAccumulated`.
   */
  function stubFetch(opts: {
    initial: FindingsPage
    filtered: FindingsPage
    isFilteredRequest: (url: URL) => boolean
    release: () => Promise<void>
  }): void {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string) => {
        const url = new URL(input, 'http://localhost')
        const json = (payload: unknown) =>
          ({ ok: true, json: async () => payload }) as unknown as Response
        if (url.pathname === '/api/kyber/sessions') return json({ sessions: SCOPE_SESSIONS })
        if (url.pathname === '/api/kyber/harnesses') return json({ harnesses: renderedHarnesses })
        if (url.pathname === '/api/kyber/runs') return json({ runs: [] })
        if (url.pathname === '/api/kyber/coverage') return json(emptyCoverage)
        if (url.pathname === '/api/kyber/findings') {
          if (opts.isFilteredRequest(url)) {
            await opts.release()
            return json(opts.filtered)
          }
          return json(opts.initial)
        }
        throw new Error(`unstubbed fetch: ${input}`)
      }),
    )
  }

  it('changing the selected harness drops the old rows at once and never re-appends them', async () => {
    const initial = browserPage(['a-1', 'a-2', 'a-3'], {})
    const filtered = browserPage(['b-1', 'b-2'], {})
    let release!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    stubFetch({
      initial,
      filtered,
      isFilteredRequest: (url) => url.searchParams.get('harness') === 'claude-code',
      release: () => gate,
    })
    // The harness scope is owned above the page: Context Doctor receives it
    // as `activeHarness` rather than selecting a harness of its own.
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    const utils = renderDom(
      <QueryClientProvider client={client}>
        <ContextDoctor initialFindings={initial} />
      </QueryClientProvider>,
    )

    // (a) Page 1 for filter A renders (synchronously, from initialFindings).
    expect(browser().getByTestId('finding-card-a-1')).not.toBeNull()
    expect(browser().getByTestId('finding-card-a-3')).not.toBeNull()
    expect(browser().getByText('All Workspace Findings (3)')).not.toBeNull()

    // (b) The owner selects a harness; the new page stays gated.
    utils.rerender(
      <QueryClientProvider client={client}>
        <ContextDoctor initialFindings={initial} activeHarness="claude-code" />
      </QueryClientProvider>,
    )
    expect(browser().getByTestId('findings-scope-note').textContent).toContain('Claude Code')

    // (c) Mid-flight: old ids are ABSENT (not hidden), and the section shows
    // a skeleton over zero rows — no placeholder leak, no stale rows.
    expect(browser().queryByTestId('finding-card-a-1')).toBeNull()
    expect(browser().queryByTestId('finding-card-a-2')).toBeNull()
    expect(browser().queryByTestId('finding-card-a-3')).toBeNull()
    expect(browser().queryByTestId('finding-list-empty')).toBeNull()
    expect(browserSection().querySelector('.skeleton-shimmer')).not.toBeNull()

    // New page resolves: exactly its rows render, once each.
    release()
    await browser().findByTestId('finding-card-b-1')
    expect(browser().getByText('All Workspace Findings (2)')).not.toBeNull()
    expect(browser().getAllByTestId('finding-card-b-1')).toHaveLength(1)
    expect(browser().getAllByTestId('finding-card-b-2')).toHaveLength(1)
    // `appended` was reset: no stale page-A row re-appears and the list is
    // complete, so Load more stays hidden.
    expect(browser().queryByTestId('finding-card-a-1')).toBeNull()
    expect(browser().queryByTestId('finding-card-a-2')).toBeNull()
    expect(browser().queryByTestId('finding-card-a-3')).toBeNull()
    expect(browser().queryAllByTestId(/finding-card-/)).toHaveLength(2)
    expect(browser().queryByTestId('findings-load-more')).toBeNull()
    expect(browserSection().querySelector('.skeleton-shimmer')).toBeNull()
  })

  it('changing the detector filter drops the old rows at once and never re-appends them', async () => {
    const initial = browserPage(['a-1', 'a-2', 'a-3'], { 'dup-det': 7 })
    const filtered = browserPage(['c-1', 'c-2'], { 'dup-det': 2 })
    let release!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    stubFetch({
      initial,
      filtered,
      isFilteredRequest: (url) => url.searchParams.get('detector') === 'dup-det',
      release: () => gate,
    })
    renderDoctor(initial)

    // (a) Page 1 for filter A renders, with the detector chip.
    expect(browser().getByTestId('finding-card-a-1')).not.toBeNull()
    expect(browser().getByText('All Workspace Findings (3)')).not.toBeNull()
    fireEvent.click(screen.getByTestId('findings-detector-chip-dup-det'))

    // (c) Mid-flight: old ids are ABSENT (not hidden); skeleton, not rows.
    expect(browser().queryByTestId('finding-card-a-1')).toBeNull()
    expect(browser().queryByTestId('finding-card-a-2')).toBeNull()
    expect(browser().queryByTestId('finding-card-a-3')).toBeNull()
    expect(browserSection().querySelector('.skeleton-shimmer')).not.toBeNull()

    // New page resolves: exactly its rows render, once each.
    release()
    await browser().findByTestId('finding-card-c-1')
    expect(browser().getByText('All Workspace Findings (2)')).not.toBeNull()
    expect(browser().getAllByTestId('finding-card-c-1')).toHaveLength(1)
    expect(browser().getAllByTestId('finding-card-c-2')).toHaveLength(1)
    // `appended` was reset: no stale page-A row re-appears and the list is
    // complete, so Load more stays hidden.
    expect(browser().queryByTestId('finding-card-a-1')).toBeNull()
    expect(browser().queryByTestId('finding-card-a-2')).toBeNull()
    expect(browser().queryByTestId('finding-card-a-3')).toBeNull()
    expect(browser().queryAllByTestId(/finding-card-/)).toHaveLength(2)
    expect(browser().queryByTestId('findings-load-more')).toBeNull()
    expect(browserSection().querySelector('.skeleton-shimmer')).toBeNull()
  })
})

/**
 * Rendered paging regressions (F2/F3): mounting the real `ContextDoctor` with
 * a real QueryClient and a stubbed findings endpoint, then walking Load more.
 * These pin the wired path — the query's error state, `placeholderData`, the
 * retained envelope and the served-extent offset — where the pure suites above
 * pin the helpers alone.
 */
describe('ContextDoctor paging — rendered (F2/F3)', () => {
  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('keeps the retained total, rows, banner and retry when a later page fails', async () => {
    // F2: on a page-2 failure the query clears `data`, and reading the
    // heading, the suppression banner and the Load-more gate off that failed
    // envelope rewrote 115 to 0 and 3 to 0 — "all clear", no retry, and no
    // way to tell from "you have seen everything".
    const first = browserPage(['a-1', 'a-2', 'a-3'], {}, 'cursor', {
      total: 115,
      unknownWindowSessions: 3,
    })
    stubPagedFetch({ first, failAt: ['3'] })
    renderDoctor(first)

    // Page one is on screen with the real total, the suppression banner and
    // Load more.
    expect(browser().getByText('All Workspace Findings (115)')).not.toBeNull()
    expect(browser().getByTestId('finding-card-a-3')).not.toBeNull()
    expect(browser().getByTestId('unknown-window-banner').textContent).toContain(
      '3 sessions with unknown context window',
    )
    expect(browser().queryByTestId('findings-load-more')).not.toBeNull()

    // The offset request fails.
    fireEvent.click(browser().getByTestId('findings-load-more'))
    await browser().findByTestId('findings-retry')

    // The rows the user already read stay, under the last successful
    // envelope: the count is not rewritten to 0, and the suppression banner
    // keeps its measured 3 rather than reading as "unmeasurable: none".
    expect(browser().getAllByTestId(/finding-card-/)).toHaveLength(3)
    expect(browser().getByText('All Workspace Findings (115)')).not.toBeNull()
    expect(browser().getByTestId('unknown-window-banner').textContent).toContain(
      '3 sessions with unknown context window',
    )
    // The failure is escapable and the tail is still reachable.
    expect(browser().getByTestId('findings-inline-error')).not.toBeNull()
    expect(browser().queryByTestId('findings-load-more')).not.toBeNull()
  })

  it('requests the offset the server served, not the rows painted', async () => {
    // F3: a rebuild shifted the window, so page 2's first row repeats page
    // 1's last. The id dedupe paints 5 rows over a served extent of 6, and an
    // offset derived from the painted count re-requests offset 5.
    const first = findingsPage(
      ['a-1', 'a-2', 'a-3'].map((id, i) => finding(id, `det-${i}`, 'cursor')),
      0,
      115,
    )
    const requested = stubPagedFetch({
      first,
      byOffset: {
        // offset 3: the overlap is a-3, already on screen from page one.
        '3': findingsPage(
          ['a-3', 'a-4', 'a-5'].map((id, i) => finding(id, `det-${i}`, 'cursor')),
          3,
          115,
        ),
        '6': findingsPage(
          ['a-6', 'a-7'].map((id, i) => finding(id, `det-${i}`, 'cursor')),
          6,
          115,
        ),
      },
    })
    renderDoctor(first)

    fireEvent.click(browser().getByTestId('findings-load-more'))
    // Five distinct rows render: a-3 is not painted twice.
    await browser().findByTestId('finding-card-a-5')
    expect(browser().queryAllByTestId(/finding-card-/)).toHaveLength(5)

    fireEvent.click(browser().getByTestId('findings-load-more'))
    await browser().findByTestId('finding-card-a-7')
    // Offsets 3 then 6 — never 5, an offset the server already served.
    expect(requested).toEqual(['3', '6'])
  })
})

/**
 * Scope owned above the page (harness + session). The owner of the dashboard
 * header — `App` — holds the selected harness and the session opened in the
 * explorer, and passes them down. Context Doctor never selects a harness of
 * its own: a second harness selector here would be a second, disagreeing
 * answer to "which harness am I looking at".
 *
 * What these pin:
 *   - both findings queries (headline card and paged browser) carry the
 *     harness AND session parameters, so the scope is the server's;
 *   - the session selector defaults to All sessions and lists only the
 *     selected harness's canonical sessions;
 *   - a scope change refetches from page one and never paints the previous
 *     scope's rows under the new heading;
 *   - a session that leaves the selected harness is cleared rather than left
 *     filtering findings by a session outside the scope;
 *   - the standalone page (no `onSessionChange`) still works off internal
 *     state.
 */
describe('ContextDoctor scope: harness and session', () => {
  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  /**
   * Stubs the endpoints the scoped page reads and records every findings URL
   * it asked for, so the scope parameters can be asserted per request rather
   * than per component. Session-scoped requests are gated so a mid-flight
   * assertion is deterministic.
   */
  function stubScoped(opts: {
    page?: FindingsPage
    scopedPage?: FindingsPage
    isScoped?: (url: URL) => boolean
    release?: () => Promise<void>
    sessionsStatus?: number
  } = {}): string[] {
    const requested: string[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: string) => {
        const url = new URL(input, 'http://localhost')
        const json = (payload: unknown) => ({ ok: true, json: async () => payload }) as unknown as Response
        if (url.pathname === '/api/kyber/sessions') {
          const status = opts.sessionsStatus ?? 200
          return status === 200
            ? json({ sessions: SCOPE_SESSIONS })
            : ({ ok: false, status, json: async () => ({ error: 'sessions failed' }) } as unknown as Response)
        }
        if (url.pathname === '/api/kyber/findings') {
          requested.push(url.search)
          if (opts.isScoped?.(url)) {
            await opts.release?.()
            return json(opts.scopedPage ?? opts.page ?? browserPage([], {}))
          }
          return json(opts.page ?? browserPage([], {}))
        }
        throw new Error(`unstubbed fetch: ${input}`)
      }),
    )
    return requested
  }

  /** The select only once the canonical sessions for the scope are listed. */
  async function sessionSelect(): Promise<HTMLSelectElement> {
    const select = (await screen.findByTestId('context-doctor-session-select')) as HTMLSelectElement
    await waitFor(() => expect(select.options.length).toBeGreaterThan(1))
    return select
  }

  /**
   * A controlled owner, as `App` will be: it holds the session and feeds it
   * back down. The page reports a choice; the scope only moves when this
   * component moves it.
   */
  function renderOwnerControlled(props: {
    activeHarness?: string
    onSelect?: (sessionId: string | undefined) => void
  }) {
    function Owner() {
      const [session, setSession] = useState<string | undefined>(undefined)
      return (
        <ContextDoctor
          activeHarness={props.activeHarness}
          sessionId={session}
          onSessionChange={(id) => {
            props.onSelect?.(id)
            setSession(id)
          }}
        />
      )
    }
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    return renderDom(
      <QueryClientProvider client={client}>
        <Owner />
      </QueryClientProvider>,
    )
  }

  function renderScoped(props: {
    initial?: FindingsPage
    activeHarness?: string
    sessionId?: string
    onSessionChange?: (sessionId: string | undefined) => void
  }) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    // `in` rather than `??`: an override may CLEAR the session by passing an
    // explicit undefined, which a nullish fallback would swallow — the very
    // case the controlled-selection tests are about.
    const tree = (extra?: { activeHarness?: string; sessionId?: string }) => (
      <QueryClientProvider client={client}>
        <ContextDoctor
          initialFindings={props.initial}
          activeHarness={extra !== undefined && 'activeHarness' in extra ? extra.activeHarness : props.activeHarness}
          sessionId={extra !== undefined && 'sessionId' in extra ? extra.sessionId : props.sessionId}
          onSessionChange={props.onSessionChange}
        />
      </QueryClientProvider>
    )
    const utils = renderDom(tree())
    return { ...utils, rerender: (extra: { activeHarness?: string; sessionId?: string }) => utils.rerender(tree(extra)) }
  }

  it('carries the harness and session scope on both findings queries', async () => {
    const requested = stubScoped()
    // An owner is present, so the props are the scope: without one the page is
    // standalone and ignores `sessionId` entirely.
    renderScoped({ activeHarness: 'pi', sessionId: 'sess-pi-2', onSessionChange: () => {} })

    await screen.findByTestId('context-doctor-session-select')
    await waitFor(() => expect(requested.length).toBeGreaterThanOrEqual(2))
    // The headline card and the paged browser are separate queries; each must
    // carry the scope, or the page mixes scopes inside one screen.
    expect(requested.some((qs) => qs.includes('harness=pi'))).toBe(true)
    expect(requested.some((qs) => qs.includes('sessionId=sess-pi-2'))).toBe(true)
    expect(requested.every((qs) => qs.includes('harness=pi'))).toBe(true)
    expect(requested.every((qs) => qs.includes('sessionId=sess-pi-2'))).toBe(true)
  })

  it('defaults the session selector to All sessions and lists only the selected harness', async () => {
    stubScoped()
    renderScoped({ activeHarness: 'pi' })

    const select = (await screen.findByTestId('context-doctor-session-select')) as HTMLSelectElement
    // Default: the whole harness, not an arbitrary first session.
    expect(select.value).toBe('')
    await waitFor(() =>
      expect(Array.from((select as HTMLSelectElement).options).map((option) => option.value)).toEqual([
        '',
        'sess-pi-1',
        'sess-pi-2',
      ]),
    )
  })

  it('lists every harness session when no harness is selected', async () => {
    stubScoped()
    renderScoped({})

    const select = (await screen.findByTestId('context-doctor-session-select')) as HTMLSelectElement
    await waitFor(() =>
      expect(Array.from(select.options).map((option) => option.value)).toEqual([
        '',
        'sess-pi-1',
        'sess-pi-2',
        'sess-cursor-1',
      ]),
    )
    expect(screen.getByTestId('context-doctor-session-scope-note').textContent).toMatch(/all sessions/i)
  })

  it('says the session list could not be read, and keeps All sessions', async () => {
    stubScoped({ sessionsStatus: 500 })
    renderScoped({ activeHarness: 'pi' })

    const note = await screen.findByTestId('context-doctor-session-scope-note')
    expect(note.textContent).not.toContain('sessions failed')
    const select = screen.getByTestId('context-doctor-session-select') as HTMLSelectElement
    // An unreadable session list is not a session choice: the scope stays
    // where it was rather than silently narrowing to nothing.
    expect(Array.from(select.options).map((option) => option.value)).toEqual([''])
  })

  it('refetches under the chosen session and tells the owner', async () => {
    const requested = stubScoped()
    const changes: (string | undefined)[] = []
    renderOwnerControlled({ activeHarness: 'pi', onSelect: (id) => changes.push(id) })

    fireEvent.change(await sessionSelect(), { target: { value: 'sess-pi-1' } })
    expect(changes).toEqual(['sess-pi-1'])
    await waitFor(() => expect(requested.some((qs) => qs.includes('sessionId=sess-pi-1'))).toBe(true))
  })

  it('works off internal state when no owner callback is supplied', async () => {
    const requested = stubScoped()
    renderScoped({})

    fireEvent.change(await sessionSelect(), { target: { value: 'sess-cursor-1' } })
    await waitFor(() => expect(requested.some((qs) => qs.includes('sessionId=sess-cursor-1'))).toBe(true))
  })

  it('restarts at page one and never paints the previous scope rows', async () => {
    const first = browserPage(['a-1', 'a-2', 'a-3'], {}, 'pi', { total: 115 })
    const scoped = browserPage(['b-1', 'b-2'], {}, 'pi')
    let release!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    const requested = stubScoped({
      page: first,
      scopedPage: scoped,
      isScoped: (url) => url.searchParams.get('sessionId') === 'sess-pi-1',
      release: () => gate,
    })
    const utils = renderScoped({ initial: first, activeHarness: 'pi', onSessionChange: () => {} })

    // Page one of the harness scope is served like any other page: the seeded
    // workspace rows are only ever the page one of an UNSCOPED list.
    await browser().findByTestId('finding-card-a-1')
    expect(browser().getByText('All Workspace Findings (115)')).not.toBeNull()

    utils.rerender({ activeHarness: 'pi', sessionId: 'sess-pi-1' })

    // Mid-flight the previous scope's rows are gone, not greyed out.
    expect(browser().queryByTestId('finding-card-a-1')).toBeNull()
    expect(browser().queryByTestId('finding-card-a-3')).toBeNull()

    release()
    await browser().findByTestId('finding-card-b-1')
    expect(browser().getAllByTestId(/finding-card-/)).toHaveLength(2)
    expect(browser().queryByTestId('finding-card-a-1')).toBeNull()
    // Paging restarted: the new scope's page one was requested with no offset,
    // even though the previous scope had walked to page two.
    expect(
      requested.some((qs) => qs.includes('sessionId=sess-pi-1') && !qs.includes('offset=')),
    ).toBe(true)
    expect(requested.some((qs) => qs.includes('offset='))).toBe(false)
  })

  it('asks the owner to clear a session the selected harness does not hold', async () => {
    stubScoped()
    const changes: (string | undefined)[] = []
    renderScoped({
      activeHarness: 'pi',
      sessionId: 'sess-cursor-1',
      onSessionChange: (id) => changes.push(id),
    })

    // A session from another harness would scope the findings to something the
    // selector cannot even show. The scope belongs to the owner, so the page
    // reports it rather than filtering by a session outside the scope.
    await waitFor(() => expect(changes).toEqual([undefined]))
  })

  it('ends the session scope when the controlled owner clears it', async () => {
    // A controlled page reads the owner's `sessionId` and nothing else. If it
    // fell back to the session it last chose itself, "All sessions" and a
    // harness change would be undone by a selection the owner has already
    // discarded — the page would keep querying a session nobody selected.
    const requested = stubScoped()
    const changes: (string | undefined)[] = []
    renderOwnerControlled({ activeHarness: 'pi', onSelect: (id) => changes.push(id) })

    fireEvent.change(await sessionSelect(), { target: { value: 'sess-pi-1' } })
    await waitFor(() => expect(requested.some((qs) => qs.includes('sessionId=sess-pi-1'))).toBe(true))

    // The owner drops the selection: All sessions at the page, an absent
    // `sessionId` above it. The page must not fall back to what it chose.
    fireEvent.change(screen.getByTestId('context-doctor-session-select'), { target: { value: '' } })
    expect(changes).toEqual(['sess-pi-1', undefined])

    expect((screen.getByTestId('context-doctor-session-select') as HTMLSelectElement).value).toBe('')
    expect(screen.getByTestId('findings-scope-note').textContent).toMatch(/all sessions/i)
    await waitFor(() => expect(requested[requested.length - 1]).not.toContain('sessionId='))
    // And it stays ended: nothing goes on querying the discarded session.
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(requested[requested.length - 1]).not.toContain('sessionId=')
  })

  it('never queries a controlled session under a harness that changed', async () => {
    const requested = stubScoped()
    const changes: (string | undefined)[] = []
    const utils = renderScoped({
      activeHarness: 'pi',
      sessionId: 'sess-pi-1',
      onSessionChange: (id) => changes.push(id),
    })
    await waitFor(() => expect(requested.some((qs) => qs.includes('sessionId=sess-pi-1'))).toBe(true))

    utils.rerender({ activeHarness: 'cursor' })

    // The stale session belongs to the harness just left. The page reports the
    // mismatch to the owner, and no request pairs it with the new harness.
    await waitFor(() => expect(changes).toContain(undefined))
    expect(
      requested.some((qs) => qs.includes('harness=cursor') && qs.includes('sessionId=sess-pi-1')),
    ).toBe(false)
  })

  it('drops a session that leaves the selected harness when it owns the selection', async () => {
    const requested = stubScoped()
    const utils = renderScoped({})

    fireEvent.change(await sessionSelect(), { target: { value: 'sess-cursor-1' } })
    await waitFor(() => expect(requested.some((qs) => qs.includes('sessionId=sess-cursor-1'))).toBe(true))

    // The owner narrows to a harness that does not hold that session.
    utils.rerender({ activeHarness: 'pi' })

    expect((screen.getByTestId('context-doctor-session-select') as HTMLSelectElement).value).toBe('')
    await waitFor(() => expect(requested[requested.length - 1]).not.toContain('sessionId='))
  })

  it('shows the scope the browser is showing, and no duplicate harness selector', () => {
    const html = renderWithQuery(<ContextDoctor initialFindings={[]} activeHarness="claude-code" />)
    expect(html).toContain('findings-scope-note')
    expect(html).toContain('Claude Code')
    // The owner above the page picks the harness; a second selector here would
    // be a second answer to the same question.
    expect(html).not.toContain('findings-harness-filter')
  })

  it('renders findings only: no maintenance, ingest coverage or scorecard matrix', () => {
    const html = renderWithQuery(<ContextDoctor initialFindings={[]} />)
    expect(html).not.toContain('maintenance-panel')
    expect(html).not.toContain('coverage-ingest-panel')
    expect(html).not.toContain('scorecard-matrix')
    expect(html).not.toContain('Wipe database')
  })
})
