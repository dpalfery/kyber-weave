import { describe, expect, it } from 'vitest'

import { applyHtmlBrand, BRAND, resolveCliName } from '../../src/brand-overlay.js'

describe('KyberDash brand overlay', () => {
  it('defaults the CLI name to kyberdash for the upstream launcher stems', () => {
    expect(resolveCliName('/tmp/dash/dist/cli.js')).toBe('kyberdash')
    expect(resolveCliName('/usr/local/bin/kyberdash')).toBe('kyberdash')
  })

  it('follows the installed binary basename', () => {
    expect(resolveCliName('/usr/local/bin/codeburn')).toBe('codeburn')
  })

  it('rewrites upstream HTML chrome without touching the bootstrap marker', () => {
    const html =
      '<title>CodeBurn - Local Dashboard</title><link rel="icon" href="/codeburn-logo.png" />'
    const branded = applyHtmlBrand(html)
    expect(branded).toContain(`<title>${BRAND.htmlTitle}</title>`)
    expect(branded).toContain(`href="${BRAND.faviconPngHref}"`)
    expect(branded).not.toContain('CodeBurn')
    expect(branded).not.toContain('/codeburn-logo.png')
  })
})
