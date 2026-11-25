import { describe, it, expect } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { RecommendationPanel } from './RecommendationPanel.js'

const render = (waste: number | null | undefined): string =>
  renderToStaticMarkup(
    <RecommendationPanel
      recommendation="Apply progressive disclosure: load tool definitions on demand."
      estimatedWasteTokens={waste}
    />,
  )

describe('RecommendationPanel: measured waste, no invented band', () => {
  it('reads not measured when the server sent no waste figure', () => {
    const html = render(null)

    expect(html).toContain('data-testid="recoverable-waste-tokens"')
    expect(html).toContain('not measured')
    expect(html).not.toMatch(/estimated/i)
    expect(html).not.toContain('≈')
    expect(html).not.toMatch(/\[\s*\d[\d.,KkMm]*\s*[–-]/)
  })

  it('shows a measured figure in tokens and draws no error bar or gauge', () => {
    const html = render(15_000)

    expect(html).toContain('15.0K tokens')
    expect(html).not.toContain('data-testid="recommendation-error-bar"')
    expect(html).not.toContain('data-testid="error-bar-visual"')
    expect(html).not.toMatch(/Estimate:/)
    expect(html).not.toMatch(/\[12\.0K/)
  })
})
