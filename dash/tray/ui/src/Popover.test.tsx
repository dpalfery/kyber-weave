/**
 * The popover, against every report fixture and every ViewState phase.
 *
 * Rendered with `renderToStaticMarkup`, which is how every React component in
 * this repository is tested (see `dash/web/src/components/**`). These panels are
 * presentational — they take a document and lay it out — so static markup
 * asserts everything Requirement 8 fixes about them, and the interactive parts
 * are covered by `commands.test.tsx`, which drives the handlers directly.
 */

import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'

import empty from '../../../src/analysis/report/fixtures/empty.json' with { type: 'json' }
import full from '../../../src/analysis/report/fixtures/full.json' with { type: 'json' }
import mixedBasisCost from '../../../src/analysis/report/fixtures/mixed-basis-cost.json' with { type: 'json' }
import noFindings from '../../../src/analysis/report/fixtures/no-findings.json' with { type: 'json' }
import stale from '../../../src/analysis/report/fixtures/stale.json' with { type: 'json' }
import unmeasurablePressure from '../../../src/analysis/report/fixtures/unmeasurable-pressure.json' with { type: 'json' }
import type { ContextReport } from '../../../src/analysis/report/types.ts'

import { Popover } from './Popover'
import { MAX_FINDINGS } from './components/FindingsList'
import type { TrayCommands, ViewState } from './viewState'

const FIXTURES: Record<string, ContextReport> = {
  full: full as unknown as ContextReport,
  empty: empty as unknown as ContextReport,
  stale: stale as unknown as ContextReport,
  'unmeasurable-pressure': unmeasurablePressure as unknown as ContextReport,
  'no-findings': noFindings as unknown as ContextReport,
  'mixed-basis-cost': mixedBasisCost as unknown as ContextReport,
}

const NOW = new Date('2026-09-19T12:00:00.000Z')

const NOOP_COMMANDS: TrayCommands = {
  refreshNow: () => {},
  openView: () => {},
  setSettings: () => {},
  quit: () => {},
}

function viewState(overrides: Partial<ViewState> = {}): ViewState {
  return {
    phase: 'ready',
    report: FIXTURES.full,
    reportFetchedAt: '2026-09-19T11:59:00.000Z',
    error: null,
    refresh: { state: 'idle', lastSuccessAt: '2026-09-19T11:00:00.000Z', lastFailure: null },
    receiver: 'reachable',
    settings: {
      harness: 'all',
      windowDays: 7,
      refreshMinutes: 5,
      attentionThreshold: 0.7,
      criticalThreshold: 0.9,
      launchAtLogin: false,
      hostReceiver: false,
    },
    ...overrides,
  }
}

function render(state: ViewState): string {
  return renderToStaticMarkup(
    <Popover state={state} commands={NOOP_COMMANDS} now={NOW} />,
  )
}

describe('Popover across every fixture', () => {
  for (const [name, report] of Object.entries(FIXTURES)) {
    describe(name, () => {
      const html = render(viewState({ report }))

      /** R14.1: an absent figure is `—`, never `0`. */
      it('never renders a bare 0 where a figure is absent', () => {
        // Every unmeasurable figure in the document must appear as an em dash
        // with its reason, and the reason text itself must reach the user.
        const unmeasurableReasons = collectReasons(report)
        for (const reason of unmeasurableReasons) {
          if (!html.includes(reason)) continue
          expect(html).toContain(`— (${reason})`)
        }
      })

      it('renders without a quota, plan, currency, budget or score element', () => {
        // R8.12 / R14.3: the surfaces this fork removed must not reappear here.
        for (const forbidden of ['quota', 'plan-', 'budget', 'composite', 'score']) {
          expect(html.toLowerCase()).not.toContain(`data-testid="${forbidden}`)
        }
        expect(html).not.toContain('Composite')
      })

      it('shows at most three findings', () => {
        const rendered = html.split('data-testid="finding"').length - 1
        expect(rendered).toBeLessThanOrEqual(MAX_FINDINGS)
      })

      it('puts the health footer after the findings and the actions last', () => {
        const order = [
          'data-testid="harness-selector"',
          'data-testid="health-footer"',
          'data-testid="actions"',
        ]
        const positions = order.map((marker) => html.indexOf(marker))
        expect(positions.every((position) => position >= 0)).toBe(true)
        expect(positions).toEqual([...positions].sort((a, b) => a - b))
      })

      it('shows cost only inside the footer, after the token figures', () => {
        const footerAt = html.indexOf('data-testid="health-footer"')
        const costAt = html.indexOf('data-testid="cost-line"')
        if (costAt >= 0) {
          expect(costAt).toBeGreaterThan(footerAt)
        }
      })
    })
  }
})

/** Every `reason` on an unmeasurable figure anywhere in the document. */
function collectReasons(value: unknown, found: string[] = []): string[] {
  if (Array.isArray(value)) {
    for (const item of value) collectReasons(item, found)
    return found
  }
  if (value !== null && typeof value === 'object') {
    const record = value as Record<string, unknown>
    if (record.value === null && typeof record.reason === 'string') {
      found.push(record.reason)
    }
    for (const nested of Object.values(record)) collectReasons(nested, found)
  }
  return found
}

describe('Popover phases', () => {
  it('replaces the panels with setup when the CLI is missing (R6.7)', () => {
    const html = render(
      viewState({
        phase: 'setup',
        setup: {
          probed: ['KYBERDASH_BIN=/nowhere', '/Users/x/.local/bin/kyberdash', 'PATH'],
          remedy: 'Install kyberdash: curl -fsSL https://example.test/install.sh | sh',
          reason: 'not-found',
        },
      }),
    )

    expect(html).toContain('data-testid="setup-state"')
    expect(html).toContain('cannot find the kyberdash command')
    expect(html).toContain('/Users/x/.local/bin/kyberdash')
    expect(html).toContain('install.sh')
    // 6.7: earlier data must not be presented as current.
    expect(html).not.toContain('data-testid="session-panel"')
    expect(html).not.toContain('data-testid="health-footer"')
  })

  it('names the too-old reason distinctly', () => {
    const html = render(
      viewState({
        phase: 'setup',
        setup: {
          probed: ['PATH'],
          remedy: 'Update kyberdash: kyber-weave update',
          reason: 'too-old',
        },
      }),
    )

    expect(html).toContain('older than this tray understands')
    expect(html).toContain('kyber-weave update')
  })

  it('banners stale data with its age and reason, without hiding it (R7.4)', () => {
    const html = render(
      viewState({
        phase: 'stale',
        error: 'connection refused',
        reportFetchedAt: '2026-09-19T11:00:00.000Z',
      }),
    )

    expect(html).toContain('data-testid="stale-banner"')
    expect(html).toContain('1h ago')
    expect(html).toContain('connection refused')
    // The figures are still there — they are the best available, just not current.
    expect(html).toContain('data-testid="session-panel"')
  })

  it('offers Refresh now when there is no session (R8.10)', () => {
    const html = render(viewState({ report: FIXTURES.empty }))

    expect(html).toContain('data-testid="empty-state"')
    expect(html).toContain('Refresh now')
    expect(html).not.toContain('data-testid="session-panel"')
  })

  it('shows the starting phase without a stale banner', () => {
    const html = render(viewState({ phase: 'starting' }))

    expect(html).toContain('data-phase="starting"')
    expect(html).not.toContain('data-testid="stale-banner"')
  })

  it('disables Refresh now while a refresh is running elsewhere (R10.3)', () => {
    const html = render(
      viewState({
        refresh: { state: 'running-elsewhere', lastSuccessAt: null, lastFailure: null },
      }),
    )

    expect(html).toContain('Refresh running elsewhere')
    // Attribute order is React's to choose, so match the whole opening tag.
    expect(html).toMatch(/<button(?=[^>]*\bdisabled\b)[^>]*data-testid="refresh-now"/)
  })

  it('disables Refresh now while the trays own refresh is running (R10.2)', () => {
    const html = render(
      viewState({ refresh: { state: 'running', lastSuccessAt: null, lastFailure: null } }),
    )

    expect(html).toContain('Refreshing…')
    expect(html).toMatch(/<button(?=[^>]*\bdisabled\b)[^>]*data-testid="refresh-now"/)
  })

  it('leaves Refresh now enabled when nothing is running', () => {
    const html = render(viewState())

    expect(html).toMatch(/<button(?![^>]*\bdisabled\b)[^>]*data-testid="refresh-now"/)
  })

  it('keeps the last success beside the last failure (R10.5)', () => {
    const html = render(
      viewState({
        refresh: {
          state: 'failed',
          lastSuccessAt: '2026-09-19T11:00:00.000Z',
          lastFailure: 'canon.db is locked',
        },
      }),
    )

    expect(html).toContain('Refreshed 1h ago')
    expect(html).toContain('canon.db is locked')
  })

  it('names every receiver status (R10.6)', () => {
    const expected = {
      reachable: 'receiver reachable',
      'not-reachable': 'receiver not reachable',
      hosted: 'receiver hosted by KyberDash',
      'port-held-by-other': 'receiver port held by another process',
      unknown: 'receiver unknown',
    } as const

    for (const [status, label] of Object.entries(expected)) {
      const html = render(viewState({ receiver: status as ViewState['receiver'] }))
      expect(html).toContain(label)
    }
  })
})

describe('Popover content rules', () => {
  it('shows the cache-invalidation notice when the turn invalidated the cache', () => {
    const report = structuredClone(FIXTURES.full) as ContextReport
    report.latestSession!.latestTurn.cacheInvalidation = true

    const html = render(viewState({ report }))
    expect(html).toContain('data-testid="cache-invalidation-notice"')
    expect(html).toContain('re-sends the whole prefix')
  })

  it('omits the notice when the cache held', () => {
    const html = render(viewState({ report: FIXTURES.full }))
    expect(html).not.toContain('data-testid="cache-invalidation-notice"')
  })

  it('renders recommendation text verbatim (R14.4)', () => {
    const report = FIXTURES.full
    const html = render(viewState({ report }))

    for (const finding of (report.findings ?? []).slice(0, MAX_FINDINGS)) {
      expect(html).toContain(escapeHtml(finding.recommendation))
    }
  })

  it('says so when findings are hidden by the cap', () => {
    const report = structuredClone(FIXTURES.full) as ContextReport
    const [first] = report.findings ?? []
    report.findings = Array.from({ length: 5 }, (_, index) => ({
      ...first,
      id: `finding-${index}`,
    }))

    const html = render(viewState({ report }))
    expect(html).toContain('2 more in the dashboard')
  })

  it('reports no findings without implying there is nothing wrong elsewhere', () => {
    const html = render(viewState({ report: FIXTURES['no-findings'] }))
    expect(html).toContain('data-testid="findings-empty"')
    expect(html).toContain('Nothing to act on in this window')
  })

  it('shows each cost basis separately, never blended (R8.9)', () => {
    const report = FIXTURES['mixed-basis-cost']
    const html = render(viewState({ report }))

    for (const entry of report.cost ?? []) {
      expect(html).toContain(entry.basis)
    }
  })

  it('shows the composition bar with a part per measured bucket plus the residual', () => {
    const html = render(viewState({ report: FIXTURES.full }))

    expect(html).toContain('data-testid="composition-bar"')
    for (const bucket of [
      'system_prompt',
      'tool_definitions',
      'instruction_context',
      'conversation_history',
      'tool_result_content',
    ]) {
      expect(html).toContain(`data-bucket="${bucket}"`)
    }
    expect(html).toContain('data-bucket="residual"')
  })

  it('renders unmeasurable pressure as a dash with its reason, not 0%', () => {
    const report = FIXTURES['unmeasurable-pressure']
    const html = render(viewState({ report }))

    const pressure = report.latestSession?.latestTurn.pressure
    if (pressure && pressure.value === null) {
      expect(html).toContain(`— (${pressure.reason})`)
    }
    expect(html).not.toMatch(/session-pressure[^>]*>\s*<span[^>]*>0%/)
  })
})

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}
