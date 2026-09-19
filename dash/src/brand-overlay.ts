import { basename } from 'path'

/**
 * KyberDash user-facing brand overlay.
 *
 * One place for the product's name, CLI name and marks, so a surface renders
 * them without hard-coding a string it might later disagree with.
 */
export const BRAND = {
  productName: 'kyberDash',
  cliName: 'kyberdash',
  htmlTitle: 'kyberDash - Local Dashboard',
  faviconSvgHref: '/kyberdash-logo.svg',
  faviconPngHref: '/kyberdash-logo.png',
} as const

const LAUNCHER_STEMS = new Set(['cli', 'main', 'launch', 'launcher'])

export function resolveCliName(argv1 = process.argv[1]): string {
  const fromEnv = process.env['KYBERDASH_CLI_NAME']?.trim()
  if (fromEnv) return fromEnv
  const stem = basename(argv1 ?? '').replace(/\.(js|mjs|cjs|ts|exe)$/i, '')
  if (stem && !LAUNCHER_STEMS.has(stem.toLowerCase())) return stem
  return BRAND.cliName
}

export function applyHtmlBrand(html: string): string {
  return html
    .replace(/<title>[^<]*<\/title>/i, `<title>${BRAND.htmlTitle}</title>`)
    .replace(/href="\/codeburn-logo\.png"/g, `href="${BRAND.faviconPngHref}"`)
}
