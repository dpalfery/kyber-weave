import { execSync } from 'node:child_process'
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const srcRoot = dirname(fileURLToPath(import.meta.url))
const webRoot = join(srcRoot, '..')
const dashRoot = join(webRoot, '..')
const tokensPath = join(srcRoot, 'kyber.css')
const indexPath = join(srcRoot, 'index.css')
const distCssDir = join(dashRoot, 'dist', 'dash', 'assets')

function walk(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir, { withFileTypes: true })) {
    if (name.name === 'node_modules' || name.name === 'dist') continue
    const path = join(dir, name.name)
    if (name.isDirectory()) {
      walk(path, out)
      continue
    }
    if (
      /\.(tsx|ts|css)$/.test(name.name) &&
      name.name !== 'kyber.css' &&
      name.name !== 'kyber.test.ts'
    ) {
      out.push(path)
    }
  }
  return out
}

function definedThemeTokens(css: string): string[] {
  const block = css.match(/@theme[\s\S]*?\{([\s\S]*?)\n\}/)
  if (!block) return []
  return [...block[1]!.matchAll(/^\s*(--[A-Za-z0-9-]+):/gm)].map((match) => match[1]!)
}

function referencedNames(source: string): Set<string> {
  const names = new Set<string>()
  for (const match of source.matchAll(/var\(\s*(--[A-Za-z0-9-]+)/g)) {
    names.add(match[1]!)
  }
  return names
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function hasTokenUse(source: string, needle: string): boolean {
  return new RegExp(`(^|[^A-Za-z0-9-])${escapeRegex(needle)}([^A-Za-z0-9-]|$)`).test(source)
}

/**
 * Map an @theme token to the strings a component might use to consume it.
 * `--color-primary` is `bg-primary` / `text-primary` / `var(--primary)`;
 * `--text-density-sm` is the `text-density-sm` utility, and so on.
 */
function consumptionNeedles(themeToken: string): string[] {
  if (themeToken.endsWith('--line-height')) return []
  if (themeToken.startsWith('--color-')) {
    const stem = themeToken.slice('--color-'.length)
    return [
      themeToken,
      `--${stem}`,
      `bg-${stem}`,
      `text-${stem}`,
      `border-${stem}`,
      `from-${stem}`,
      `to-${stem}`,
      `ring-${stem}`,
      `fill-${stem}`,
      `stroke-${stem}`,
    ]
  }
  if (themeToken.startsWith('--font-')) {
    return [themeToken, `font-${themeToken.slice('--font-'.length)}`]
  }
  if (themeToken.startsWith('--text-')) {
    return [themeToken, `text-${themeToken.slice('--text-'.length)}`]
  }
  if (themeToken.startsWith('--spacing-')) {
    const stem = themeToken.slice('--spacing-'.length)
    return [themeToken, `gap-${stem}`, `p-${stem}`, `m-${stem}`, `px-${stem}`]
  }
  if (themeToken.startsWith('--radius-')) {
    return [themeToken, `rounded-${themeToken.slice('--radius-'.length)}`]
  }
  if (themeToken.startsWith('--tracking-')) {
    return [themeToken, `tracking-${themeToken.slice('--tracking-'.length)}`]
  }
  if (themeToken.startsWith('--leading-')) {
    return [themeToken, `leading-${themeToken.slice('--leading-'.length)}`]
  }
  return [themeToken]
}

function isReferenced(themeToken: string, source: string, vars: Set<string>): boolean {
  const needles = consumptionNeedles(themeToken)
  for (const needle of needles) {
    if (needle.startsWith('--')) {
      if (vars.has(needle) || vars.has(themeToken)) return true
      continue
    }
    if (hasTokenUse(source, needle)) return true
  }
  return false
}

function presentInBuilt(themeToken: string, built: string): boolean {
  if (built.includes(themeToken)) return true
  return consumptionNeedles(themeToken).some((needle) => needle !== '' && built.includes(needle))
}

describe('shared design tokens (R8.12)', () => {
  it('lives in kyber.css and is imported by the dashboard stylesheet', () => {
    expect(existsSync(tokensPath), 'dash/web/src/kyber.css must exist so the tray can import the same file').toBe(
      true,
    )
    const index = readFileSync(indexPath, 'utf8')
    expect(index).toMatch(/@import\s+["'].\/kyber\.css["']/)
    expect(index).not.toMatch(/@theme\s+inline/)
  })

  it('the built CSS still defines every token the components reference', () => {
    const tokensCss = readFileSync(tokensPath, 'utf8')
    const themeTokens = definedThemeTokens(tokensCss)
    expect(themeTokens.length).toBeGreaterThan(0)

    const sources = walk(srcRoot).map((path) => readFileSync(path, 'utf8')).join('\n')
    const vars = referencedNames(sources)
    const referencedTheme = themeTokens.filter((token) => isReferenced(token, sources, vars))
    expect(referencedTheme.length, 'components should consume at least some @theme tokens').toBeGreaterThan(0)

    execSync('npm run build', { cwd: webRoot, stdio: 'pipe' })
    expect(existsSync(distCssDir), 'vite should emit CSS under dist/dash/assets').toBe(true)
    const built = readdirSync(distCssDir)
      .filter((name) => name.endsWith('.css'))
      .map((name) => readFileSync(join(distCssDir, name), 'utf8'))
      .join('\n')

    const missing = referencedTheme.filter((token) => !presentInBuilt(token, built))
    expect(missing, `built CSS dropped theme tokens: ${missing.join(', ')}`).toEqual([])
  })
})
