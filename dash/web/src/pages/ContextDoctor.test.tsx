import { describe, it, expect } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { ContextDoctor } from './ContextDoctor.js'
import type { FindingsPage, KyberFinding, KyberHarnessSummary } from '../lib/kyberApi.js'
import {
  accumulateFindingsPage,
  flattenFindingsPages,
  type FindingsPageAcc,
} from '../lib/kyberApi.js'

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

describe('findings page accumulation (review M1)', () => {
  const rows = (prefix: string, n: number): KyberFinding[] =>
    Array.from({ length: n }, (_, i) => finding(`${prefix}-${i}`, 'duplicate-tool-call', 'cursor'))

  it('keeps page 1 when page 2 arrives', () => {
    // The Load-more bug: accumulating pages 2..N while the first page lived
    // only in the query made the visible set shrink to a different 25.
    let pages: FindingsPageAcc[] = []
    pages = accumulateFindingsPage(pages, 0, rows('p1', 25))
    pages = accumulateFindingsPage(pages, 25, rows('p2', 25))
    const flat = flattenFindingsPages(pages)
    expect(flat).toHaveLength(50)
    expect(flat[0]?.id).toBe('p1-0')
    expect(flat[25]?.id).toBe('p2-0')
  })

  it('never appends the same offset twice', () => {
    let pages: FindingsPageAcc[] = []
    pages = accumulateFindingsPage(pages, 0, rows('p1', 25))
    pages = accumulateFindingsPage(pages, 0, rows('p1', 25))
    expect(flattenFindingsPages(pages)).toHaveLength(25)
  })

  it('orders out-of-order arrivals by offset', () => {
    let pages: FindingsPageAcc[] = []
    pages = accumulateFindingsPage(pages, 25, rows('p2', 10))
    pages = accumulateFindingsPage(pages, 0, rows('p1', 25))
    const flat = flattenFindingsPages(pages)
    expect(flat[0]?.id).toBe('p1-0')
    expect(flat).toHaveLength(35)
  })
})
