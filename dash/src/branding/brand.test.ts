import { describe, expect, it } from 'vitest'

import { applyHtmlBrand, BRAND, resolveCliName } from '../brand-overlay.js'

describe('KyberDash brand overlay', () => {
  it('defaults the CLI name to kyberdash for the upstream launcher stems', () => {
    expect(resolveCliName('/tmp/dash/dist/cli.js')).toBe('kyberdash')
    expect(resolveCliName('/usr/local/bin/kyberdash')).toBe('kyberdash')
  })

  it('follows the installed binary basename', () => {
    expect(resolveCliName('/usr/local/bin/kyberdash-nightly')).toBe('kyberdash-nightly')
  })

  it('overlays the title and leaves the authored favicon href alone', () => {
    const html = `<title>Whatever - Local Dashboard</title><link rel="icon" href="${BRAND.faviconPngHref}" />`
    const branded = applyHtmlBrand(html)
    expect(branded).toContain(`<title>${BRAND.htmlTitle}</title>`)
    expect(branded).toContain(`href="${BRAND.faviconPngHref}"`)
    expect(branded).not.toContain('Whatever')
  })
})
