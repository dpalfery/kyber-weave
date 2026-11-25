import { describe, it, expect } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { FindingList } from './FindingList.js'
import type { KyberFinding } from '../../lib/kyberApi.js'

type FindingOverrides = Partial<KyberFinding>

function finding(overrides: FindingOverrides): KyberFinding {
  return {
    id: 'finding-1',
    detectorId: 'resident-schema',
    title: 'Resident schema',
    mechanism: 'A tool schema stayed resident without being invoked.',
    evidenceLinks: [],
    confidence: 'deterministic',
    recommendation: 'Load the tool definition on demand.',
    outcomeRiskCaveat: 'Deferring may delay the first call to this tool.',
    ...overrides,
  }
}

const render = (findings: KyberFinding[]): string =>
  renderToStaticMarkup(<FindingList findings={findings} />)

describe('FindingList: recoverable waste is shown as measured, never estimated', () => {
  it('reads not measured for a finding with no waste figure, never zero or a range', () => {
    const html = render([finding({ estimatedWasteTokens: null, errorBar: null })])

    expect(html).toContain('data-testid="finding-waste-tokens"')
    expect(html).toContain('not measured')
    expect(html).not.toMatch(/estimated/i)
    expect(html).not.toContain('≈')
    // The card carries no error-bar range even when the finding does not.
    expect(html).not.toMatch(/\[\s*\d[\d.,KkMm]*\s*[–-]/)
  })

  it('shows a measured waste figure without drawing its error bar', () => {
    const html = render([
      finding({ estimatedWasteTokens: 5_000, errorBar: { lower: 12_345, upper: 67_890 } }),
    ])

    expect(html).toContain('5.0K')
    // The bounds are not rendered at all: an invented band is an estimate (D15).
    expect(html).not.toContain('12.3K')
    expect(html).not.toContain('67.9K')
    expect(html).not.toContain('Calibrated confidence error bar')
  })

  it('heads the figure as recoverable waste, not as an estimate', () => {
    const html = render([finding({ estimatedWasteTokens: 5_000 })])

    expect(html).toContain('Recoverable Waste')
    expect(html).not.toMatch(/estimated/i)
    expect(html).not.toMatch(/waste estimate/i)
  })
})
