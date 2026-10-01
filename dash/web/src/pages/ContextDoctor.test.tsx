import { describe, it, expect } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { ContextDoctor, browserRows, nextAccumulated } from './ContextDoctor.js'
import type { FindingsPage, KyberFinding, KyberHarnessSummary } from '../lib/kyberApi.js'

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
