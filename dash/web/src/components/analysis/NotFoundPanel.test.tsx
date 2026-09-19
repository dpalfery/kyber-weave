import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'

import { NotFoundPanel } from './NotFoundPanel.js'

describe('NotFoundPanel (R5.4)', () => {
  it('names the missing id and links to Context Doctor at /', () => {
    const html = renderToStaticMarkup(<NotFoundPanel id="fid-missing" />)
    expect(html).toContain('data-testid="not-found-panel"')
    expect(html).toContain('fid-missing')
    expect(html).toMatch(/href="\/"/)
    expect(html).toContain('Context Doctor')
  })
})
