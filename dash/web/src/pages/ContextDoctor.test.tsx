import { describe, it, expect, vi, afterEach } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { ContextDoctor } from './ContextDoctor.js'
import { fetchCoverage, type KyberCoverage } from '../lib/kyberApi.js'

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
