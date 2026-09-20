import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

import ts from 'typescript'
import { describe, expect, it } from 'vitest'

/**
 * Requirement 3.7: user-visible text must not name CodeBurn outside the
 * attribution notices of Requirement 1.5.
 *
 * Task 4.1 renamed the package, variables and directories, but 39 `codeburn:`
 * prefixes survived in stderr copy and shipped, because nothing asserted 3.7 —
 * `UpstreamSeveranceTests` guards the *domain* ties (`getagentseal`,
 * `agentseal.org`, `codeburn.app`) that Requirement 1.6 names, and the bare
 * product name is not one of them.
 *
 * The scan reads literals off the TypeScript AST rather than matching text.
 * Comments are the reason: a doc comment may quote `` `kyberdash otel` `` to
 * name a command, and that is not rendered copy. Hand-lexing that also has to
 * tell a regex literal from a division, which is what the parser is for.
 */

const SOURCE_ROOTS = ['src', 'web/src']
const DASH_ROOT = fileURLToPath(new URL('../..', import.meta.url))

/**
 * Wire formats and stored keys, which are data rather than copy. Renaming one
 * is a migration — emitters already send `codeburn.*` attributes, canon rows
 * already carry the `codeburn/` prefix, and browsers already hold the theme
 * key — so each stays until its own change carries the migration with it.
 */
const ALLOWED = [
  // The `${}` alternative covers the per-metric keys built as template literals.
  {
    pattern: /^codeburn\.(?:[a-z0-9_.-]|\$\{[a-z0-9_.]+\})+$/i,
    why: 'OTLP attribute key or instrumentation scope name',
  },
  { pattern: /^codeburn\/$/, why: 'canon FILE_SOURCE_PREFIX, written into stored rows' },
  { pattern: /^codeburn$/, why: 'vendor namespace matched against what emitters send' },
  { pattern: /^codeburn-theme$/, why: 'browser storage key already set in live browsers' },
  { pattern: /^file:codeburn-uri-probe\?mode=memory$/, why: 'in-memory probe URI, never rendered' },
]

/** Upstream markers the brand overlay rewrites; the pattern has to name what it replaces. */
const REWRITE_SITES = new Set(['src/brand-overlay.ts'])

function sourceFiles(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name === 'dist' || name.startsWith('.')) continue
    const path = join(dir, name)
    if (statSync(path).isDirectory()) sourceFiles(path, out)
    else if (/\.tsx?$/.test(name) && !/\.test\.tsx?$/.test(name)) out.push(path)
  }
  return out
}

/**
 * Every string, template and JSX text node, with its 1-based line. Templates
 * are taken as written, holes included, so `` `codeburn: ${detail}` `` is read
 * as the copy it renders however the hole resolves.
 */
export function renderedLiterals(source: string, fileName = 'probe.tsx'): { line: number; text: string }[] {
  const tree = ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
  const found: { line: number; text: string }[] = []

  const visit = (node: ts.Node): void => {
    const isLiteral =
      ts.isStringLiteral(node) ||
      ts.isNoSubstitutionTemplateLiteral(node) ||
      ts.isTemplateExpression(node)

    if (isLiteral || ts.isJsxText(node)) {
      const raw = node.getText(tree)
      // Drop the delimiters so a pattern can anchor on the value itself. JSX
      // text has none, and carries its surrounding indentation, so it is trimmed.
      const text = isLiteral ? raw.slice(1, -1) : raw.trim()
      if (text.length > 0) {
        found.push({ line: tree.getLineAndCharacterOfPosition(node.getStart(tree)).line + 1, text })
      }
    }

    ts.forEachChild(node, visit)
  }

  visit(tree)
  return found
}

describe('Requirement 3.7 — user-visible text does not name CodeBurn', () => {
  it('finds no unexplained CodeBurn string in shipped source', () => {
    const offenders: string[] = []

    for (const root of SOURCE_ROOTS) {
      for (const file of sourceFiles(join(DASH_ROOT, root))) {
        const relativePath = relative(DASH_ROOT, file).split(sep).join('/')
        if (REWRITE_SITES.has(relativePath)) continue

        for (const { line, text } of renderedLiterals(readFileSync(file, 'utf-8'), file)) {
          if (!/codeburn/i.test(text)) continue
          if (ALLOWED.some(({ pattern }) => pattern.test(text))) continue
          offenders.push(`${relativePath}:${line}  ${text.slice(0, 120)}`)
        }
      }
    }

    expect(
      offenders,
      'Requirement 3.7: these literals name CodeBurn where a user can see it. ' +
        'Rename them to kyberdash, or — if the value is a wire format or a stored key — ' +
        `add it to ALLOWED with the reason it cannot change:\n  ${offenders.join('\n  ')}`,
    ).toEqual([])
  })

  it('still flags a regression of the prefix that shipped', () => {
    // The exact shape that survived task 4.1, pinned so the scan above cannot
    // be loosened into passing by accident.
    const [literal] = renderedLiterals('process.stderr.write(`codeburn: cannot open ${name}\\n`)')
    expect(literal?.text).toBe('codeburn: cannot open ${name}\\n')
    expect(ALLOWED.some(({ pattern }) => pattern.test(literal?.text ?? ''))).toBe(false)
  })

  it('reads copy and JSX text but not the comments that explain them', () => {
    const source = [
      '/** Started by `codeburn kyber otel`. */',
      '// historical: codeburn wrote ~/.config/codeburn/config.json',
      "const msg = 'kyberdash: ready' // codeburn used to say this",
      'const re = /[\'"`]/g',
      'const el = <p>kyberdash is ready</p>',
    ].join('\n')

    expect(renderedLiterals(source).map(({ text }) => text)).toEqual([
      'kyberdash: ready',
      'kyberdash is ready',
    ])
  })
})
