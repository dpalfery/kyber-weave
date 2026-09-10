import { basename } from 'path'

/**
 * KyberDash user-facing brand overlay.
 *
 * Lives under `dash/src/` (not `dash/kyber/`) because tsup and `tsconfig`
 * `rootDir` are `dash/src` — the CLI bundle cannot import outside that tree.
 * This file is ours: upstream codeburn has no counterpart, so a subtree pull
 * keeps it as an extra path rather than a content conflict.
 *
 * Do not rename `dash/package.json` (`name`/`bin.codeburn`), `CODEBURN_*`
 * env vars, cache paths, or `dash/dash/public/codeburn-logo.png`. Those are
 * the upstream identity; renaming them fights every `git subtree pull`.
 */
export const BRAND = {
  productName: 'kyberDash',
  cliName: 'kyberdash',
  htmlTitle: 'kyberDash - Local Dashboard',
  faviconSvgHref: '/kyberdash-logo.svg',
  faviconPngHref: '/kyberdash-logo.png',
} as const

const LAUNCHER_STEMS = new Set(['cli', 'main', 'launch'])

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
