// Task T22: the committed Factory capture plugin bundle is a fresh build of its entry.
//
// The Factory (Droid) capture plugin ships as TEXT. The plugin tree that Factory runs cannot
// resolve `node_modules`, so the plugin is a self-contained bundle whose bytes are committed to
// the repository instead of produced at install time. The rule for a committed generated artifact
// is that it equals a fresh build of its source, so the npm build and the installed bytes are
// the same.
//
// This file is the drift guard for that rule. It pins:
//
//   - the committed `generated/plugin-source.ts` equals a fresh bundle of `entry.ts`, and the
//     bundle script's `--check` mode exits zero against it;
//   - the bundle's first line is the ownership marker. The plugin is run as `node <path>`, so
//     there is no shebang, and the marker is what identifies the file as Kyber-owned;
//   - the bundle is self-contained: it imports `node:` built-ins only and never requires a
//     package, because nothing can resolve `node_modules` beside it;
//   - every project module the bundle inlines is a leaf of the engine;
//   - the artifact is committed text and not a build output: git tracks it and no ignore rule
//     covers it.
//
// The check mode runs in this process rather than as a child process: a child process would add
// a spawn site the storm guard has no bound for. The script decides whether it is the main
// module from `process.argv[1]`, so pointing `process.argv` at the script and importing it runs
// exactly the branch `node scripts/bundle-factory-plugin.mjs --check` runs, with `process.exit`
// stubbed so a stale artifact fails the test instead of killing the test runner.

import {
  copyFileSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve, sep } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import ts from 'typescript'
import { beforeAll, describe, expect, it } from 'vitest'
import { FACTORY_PLUGIN_MARKER, FACTORY_PLUGIN_SOURCE } from './generated/plugin-source.js'

const here = dirname(fileURLToPath(import.meta.url))
/** The dash tree: the bundle script and every module the plugin inlines live under it. */
const dashRoot = resolve(here, '..', '..', '..', '..')
/** The capture family: the only tree a leaf module may reach with a value import. */
const captureRoot = join(dashRoot, 'src', 'capture')
const generatedFile = join(here, 'generated', 'plugin-source.ts')
const bundleScript = join(dashRoot, 'scripts', 'bundle-factory-plugin.mjs')
/** The artifact's path in the repository, as git records it. */
const artifactRepoPath = 'dash/src/capture/harnesses/factory-plugin/generated/plugin-source.ts'

/** The exports of `scripts/bundle-factory-plugin.mjs` this file uses. */
type BundleScript = {
  FACTORY_PLUGIN_MARKER: string
  GENERATED_FILE: string
  bundleFactoryPlugin: () => Promise<string>
  renderPluginSourceModule: (source: string) => string
}

type CheckResult = {
  module?: BundleScript
  exitCode: number
  stderr: string
}

/** The exit a stubbed `process.exit` recorded, so the check branch can be caught. */
class CheckExited extends Error {
  constructor(readonly code: number) {
    super(`the check mode exited ${code}`)
  }
}

/**
 * Run the bundle script's `--check` branch in this process and report what it decided.
 *
 * The script takes its check branch when `process.argv[1]` is the script itself, so the branch
 * runs exactly as `node scripts/bundle-factory-plugin.mjs --check` runs it. The query string on
 * the import keeps this instance apart from the plain import of the same file, which must not
 * take the branch.
 */
async function runCheckMode(scriptPath: string): Promise<CheckResult> {
  const originalArgv = process.argv
  const originalExit = process.exit
  const originalWrite = process.stderr.write
  let exitCode = 0
  let stderr = ''
  process.argv = [originalArgv[0] ?? process.execPath, scriptPath, '--check']
  process.stderr.write = ((chunk: string | Uint8Array): boolean => {
    stderr += typeof chunk === 'string' ? chunk : Buffer.from(chunk).toString('utf8')
    return true
  }) as unknown as typeof process.stderr.write
  process.exit = ((code?: number | string | null): never => {
    exitCode = typeof code === 'number' ? code : 0
    throw new CheckExited(exitCode)
  }) as unknown as typeof process.exit
  try {
    const module = (await import(
      /* @vite-ignore */ `${pathToFileURL(scriptPath).href}?check`
    )) as BundleScript
    return { module, exitCode, stderr }
  } catch (error) {
    if (!(error instanceof CheckExited)) throw error
    return { exitCode, stderr }
  } finally {
    process.argv = originalArgv
    process.exit = originalExit
    process.stderr.write = originalWrite
  }
}

/** The bundle script's exports, imported without its check branch. */
async function loadScript(): Promise<BundleScript> {
  return (await import(
    /* @vite-ignore */ `${pathToFileURL(bundleScript).href}?exports`
  )) as BundleScript
}

/**
 * The project modules the bundle inlines, as the `// src/...` sections esbuild writes. A module
 * can be listed twice, once for its imports and once for its body, so the list is deduplicated.
 */
function inlinedModules(source: string): string[] {
  const found: string[] = []
  for (const match of source.matchAll(/^\/\/ (src\/[^\n]+)$/gm)) {
    const module = match[1] ?? ''
    if (!found.includes(module)) found.push(module)
  }
  return found
}

/** Specifiers of every static `import … from`, bare `import 'x'`, and dynamic `import('x')`. */
function importSpecifiers(source: string): string[] {
  const found: string[] = []
  const patterns = [/\bfrom\s*["']([^"']+)["']/g, /\bimport\s*["']([^"']+)["']/g, /\bimport\(\s*["']([^"']+)["']\s*\)/g]
  for (const pattern of patterns) {
    for (const match of source.matchAll(pattern)) found.push(match[1] ?? '')
  }
  return found
}

/**
 * A copy of the dash tree the bundle script can build from, with `generated` holding the given
 * artifact text.
 *
 * The script finds its sources relative to its own location, so a copy of the script needs a copy
 * of the tree around it. The tree is created under its real path, because esbuild writes the
 * bundle's section comments relative to the working directory it was given and a symlinked
 * temporary directory would change those comments, and with them the bundle's bytes.
 */
function makeBundleTree(generated: string): string {
  const created = mkdtempSync(join(tmpdir(), 'kyberdash-factory-bundle-'))
  const root = realpathSync(created)
  mkdirSync(join(root, 'scripts'), { recursive: true })
  symlinkSync(join(dashRoot, 'node_modules'), join(root, 'node_modules'), 'dir')
  copyFileSync(bundleScript, join(root, 'scripts', 'bundle-factory-plugin.mjs'))
  for (const rel of inlinedModules(FACTORY_PLUGIN_SOURCE)) {
    const source = join(dashRoot, rel)
    // The bundle entry is virtual: esbuild writes it from stdin, so there is no file to copy.
    if (!existsSync(source)) continue
    const target = join(root, rel)
    mkdirSync(dirname(target), { recursive: true })
    copyFileSync(source, target)
  }
  const target = join(root, 'src', 'capture', 'harnesses', 'factory-plugin', 'generated', 'plugin-source.ts')
  mkdirSync(dirname(target), { recursive: true })
  writeFileSync(target, generated)
  return root
}

// ── The leaf rule ─────────────────────────────────────────────────────────────────────────────

type LeafReason = 'third-party' | 'outside-capture'
type LeafViolation = { specifier: string; reason: LeafReason }

/** Every value import of a module. A type-only import is erased at build time and carries no behaviour. */
function valueImportSpecifiers(source: string): string[] {
  const file = ts.createSourceFile('leaf.ts', source, ts.ScriptTarget.Latest, true)
  const specifiers: string[] = []
  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      const clause = node.importClause
      const typeOnly =
        clause !== undefined &&
        (clause.isTypeOnly ||
          (clause.name === undefined &&
            clause.namedBindings !== undefined &&
            ts.isNamedImports(clause.namedBindings) &&
            clause.namedBindings.elements.length > 0 &&
            clause.namedBindings.elements.every((element) => element.isTypeOnly)))
      if (!typeOnly) specifiers.push(node.moduleSpecifier.text)
    } else if (
      ts.isExportDeclaration(node) &&
      node.moduleSpecifier !== undefined &&
      ts.isStringLiteral(node.moduleSpecifier) &&
      !node.isTypeOnly
    ) {
      specifiers.push(node.moduleSpecifier.text)
    } else if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments[0] !== undefined &&
      ts.isStringLiteral(node.arguments[0])
    ) {
      specifiers.push(node.arguments[0].text)
    }
    ts.forEachChild(node, visit)
  }
  visit(file)
  return specifiers
}

/**
 * The imports a leaf module may not have: a third-party package, or a value import from outside
 * the capture family. A leaf may import `node:` built-ins and the capture family it belongs to.
 */
function leafViolations(source: string, moduleDir: string): LeafViolation[] {
  const violations: LeafViolation[] = []
  for (const specifier of valueImportSpecifiers(source)) {
    if (specifier.startsWith('node:')) continue
    if (specifier.startsWith('.')) {
      const resolved = resolve(moduleDir, specifier)
      if (resolved.startsWith(captureRoot + sep)) continue
      violations.push({ specifier, reason: 'outside-capture' })
      continue
    }
    violations.push({ specifier, reason: 'third-party' })
  }
  return violations
}

// ── The ignore rules ──────────────────────────────────────────────────────────────────────────

/** The repository root: the directory that holds `.git`. */
function repositoryRoot(): string {
  let current = dashRoot
  for (;;) {
    if (existsSync(join(current, '.git'))) return current
    const parent = dirname(current)
    if (parent === current) return dashRoot
    current = parent
  }
}

/**
 * The git index of this checkout: the file that records git's tracked paths.
 *
 * A checkout's `.git` is a DIRECTORY and a worktree's is a FILE that names the real git
 * directory. Reading the path answers which one it is: a directory cannot be read, and the
 * failure says so. Every other failure is a real failure and propagates, as it did when
 * the directory was told apart by a check of its own.
 */
function gitIndexPath(): string | undefined {
  const dotGit = join(repositoryRoot(), '.git')
  if (!existsSync(dotGit)) return undefined
  try {
    const match = /^gitdir: (.+)$/m.exec(readFileSync(dotGit, 'utf8'))
    const gitDir = match?.[1]?.trim()
    return gitDir === undefined ? undefined : join(gitDir, 'index')
  } catch (error) {
    if ((error as NodeJS.ErrnoException | undefined)?.code === 'EISDIR') return join(dotGit, 'index')
    throw error
  }
}

/** The ignore files that can cover a path: the `.gitignore` of the path and of each parent. */
function ignoreFilesFor(repoPath: string): { base: string; patterns: string[] }[] {
  const root = repositoryRoot()
  const segments = repoPath.split('/')
  const files: { base: string; patterns: string[] }[] = []
  for (let end = segments.length; end >= 0; end -= 1) {
    const file = join(root, ...segments.slice(0, end), '.gitignore')
    if (!existsSync(file)) continue
    files.push({
      base: segments.slice(0, end).join('/'),
      patterns: readFileSync(file, 'utf8').split('\n'),
    })
  }
  return files
}

/** A gitignore pattern as a regular expression. Character classes are not supported here. */
function toRegExp(pattern: string): RegExp {
  let source = ''
  for (let index = 0; index < pattern.length; index += 1) {
    const character = pattern[index] ?? ''
    if (character === '*') {
      if (pattern[index + 1] === '*') {
        if (pattern[index + 2] === '/') {
          source += '(?:.*/)?'
          index += 2
        } else {
          source += '.*'
          index += 1
        }
      } else {
        source += '[^/]*'
      }
      continue
    }
    if (character === '?') {
      source += '[^/]'
      continue
    }
    if ('\\^$.|+()[]{}'.includes(character)) {
      source += `\\${character}`
      continue
    }
    source += character
  }
  return new RegExp(`^${source}$`)
}

/** Does one gitignore pattern ignore the path? A negated pattern un-ignores, so it never covers. */
function patternCovers(pattern: string, base: string, repoPath: string): boolean {
  const trimmed = pattern.trim()
  if (trimmed === '' || trimmed.startsWith('#') || trimmed.startsWith('!')) return false
  let body = trimmed
  const directoryOnly = body.endsWith('/')
  if (directoryOnly) body = body.slice(0, -1)
  const anchored = body.includes('/')
  if (body.startsWith('/')) body = body.slice(1)
  const relative = base === '' ? repoPath : repoPath.slice(base.length + 1)
  const segments = relative.split('/')
  const paths: string[] = []
  for (let end = segments.length; end >= 1; end -= 1) paths.push(segments.slice(0, end).join('/'))
  // A directory-only pattern ignores the directory and everything under it, never a file.
  const candidates = (directoryOnly ? paths.slice(1) : paths).flatMap((candidate) => {
    if (anchored) return [candidate]
    const parts = candidate.split('/')
    return parts.map((_, start) => parts.slice(start).join('/'))
  })
  const regexp = toRegExp(body)
  return candidates.some((candidate) => regexp.test(candidate))
}

// ── The tests ────────────────────────────────────────────────────────────────────────────────

describe('the committed Factory capture plugin bundle (drift guard)', () => {
  let check: CheckResult
  let script: BundleScript

  beforeAll(async () => {
    check = await runCheckMode(bundleScript)
    script = await loadScript()
  })

  it('the bundle script check mode exits zero against the committed artifact', () => {
    expect(
      check.exitCode,
      `the check mode must accept the committed artifact; it said: ${check.stderr}`,
    ).toBe(0)
    expect(check.stderr).toBe('')
  })

  it('equals a fresh bundle of the entry', async () => {
    expect(FACTORY_PLUGIN_SOURCE).toBe(await script.bundleFactoryPlugin())
  })

  it('is the exact module text the bundle script renders', () => {
    expect(readFileSync(generatedFile, 'utf8')).toBe(script.renderPluginSourceModule(FACTORY_PLUGIN_SOURCE))
  })

  it('is the file the bundle script reads when it checks the artifact', () => {
    expect(script.GENERATED_FILE).toBe(generatedFile)
  })
})

describe('the check mode tells a fresh artifact from a stale one', () => {
  it('exits zero for the committed artifact and one for a drifted one', async () => {
    const committed = readFileSync(generatedFile, 'utf8')
    const freshRoot = makeBundleTree(committed)
    const staleRoot = makeBundleTree(`${committed}\n// drifted\n`)
    try {
      const fresh = await runCheckMode(join(freshRoot, 'scripts', 'bundle-factory-plugin.mjs'))
      expect(fresh.exitCode, `the check mode rejected the committed artifact: ${fresh.stderr}`).toBe(0)
      expect(fresh.stderr).toBe('')
      // The copied tree builds the committed bundle byte for byte, so the drift below is the
      // only difference the stale check can have found.
      expect(await fresh.module?.bundleFactoryPlugin()).toBe(FACTORY_PLUGIN_SOURCE)

      const stale = await runCheckMode(join(staleRoot, 'scripts', 'bundle-factory-plugin.mjs'))
      expect(stale.exitCode).toBe(1)
      expect(stale.stderr).toContain('stale')
    } finally {
      rmSync(freshRoot, { recursive: true, force: true })
      rmSync(staleRoot, { recursive: true, force: true })
    }
  })
})

describe('the ownership marker of the Factory capture plugin', () => {
  it('is the first line of the bundle and of the committed file', async () => {
    const script = await loadScript()
    expect(script.FACTORY_PLUGIN_MARKER).toBe('// kyberdash-factory-plugin v1')
    expect(FACTORY_PLUGIN_MARKER).toBe(script.FACTORY_PLUGIN_MARKER)
    expect(FACTORY_PLUGIN_SOURCE.split('\n')[0]).toBe(FACTORY_PLUGIN_MARKER)
    expect(readFileSync(generatedFile, 'utf8').split('\n')[0]).toBe(FACTORY_PLUGIN_MARKER)
  })

  it('is not a shebang, because the plugin is run as `node <path>`', () => {
    expect(FACTORY_PLUGIN_SOURCE.startsWith('#!')).toBe(false)
    expect(readFileSync(generatedFile, 'utf8').startsWith('#!')).toBe(false)
  })
})

describe('the Factory capture plugin bundle is self-contained', () => {
  it('imports only node: built-ins and never requires', () => {
    const specifiers = importSpecifiers(FACTORY_PLUGIN_SOURCE)
    expect(specifiers.length).toBeGreaterThan(0)
    expect(specifiers.filter((specifier) => !specifier.startsWith('node:'))).toEqual([])
    expect(FACTORY_PLUGIN_SOURCE).not.toMatch(/\brequire\(/)
  })
})

describe('the Factory capture plugin bundle inlines only leaves', () => {
  it('inlines the plugin modules and nothing else', () => {
    expect(inlinedModules(FACTORY_PLUGIN_SOURCE).sort()).toEqual(
      [
        'src/capture/consent-file.ts',
        'src/capture/harnesses/factory-plugin/contract.ts',
        'src/capture/harnesses/factory-plugin/consent.ts',
        'src/capture/harnesses/factory-plugin/entry.ts',
        'src/capture/harnesses/factory-plugin/factory-plugin-bundle-entry.ts',
        'src/capture/harnesses/factory-plugin/mapper.ts',
        'src/capture/harnesses/factory-plugin/spool.ts',
        'src/capture/harnesses/transport/otlp-sender.ts',
      ].sort(),
    )
  })

  it('every inlined module is a leaf of the engine', () => {
    const modules = inlinedModules(FACTORY_PLUGIN_SOURCE)
    expect(modules.length).toBeGreaterThan(0)
    const offenders: string[] = []
    for (const rel of modules) {
      const file = join(dashRoot, rel)
      // The bundle entry is virtual: esbuild writes it from stdin, so there is no module to read.
      if (!existsSync(file)) continue
      const violations = leafViolations(readFileSync(file, 'utf8'), dirname(file))
      for (const violation of violations) {
        offenders.push(`${rel} imports ${violation.specifier} (${violation.reason})`)
      }
    }
    expect(offenders, `a module the bundle inlines must stay a leaf:\n${offenders.join('\n')}`).toEqual([])
  })
})

describe('the Factory capture plugin bundle is committed text, not a build output', () => {
  const index = gitIndexPath()

  it.skipIf(index === undefined)('is tracked by git', () => {
    const tracked = readFileSync(index as string)
    expect(
      tracked.includes(Buffer.from(`${artifactRepoPath}\0`, 'utf8')),
      `${artifactRepoPath} must be tracked by git: a committed generated artifact is what ships`,
    ).toBe(true)
  })

  it('is covered by no ignore rule', () => {
    const offenders: string[] = []
    for (const file of ignoreFilesFor(artifactRepoPath)) {
      for (const pattern of file.patterns) {
        if (patternCovers(pattern, file.base, artifactRepoPath)) {
          offenders.push(`${file.base || '.'}/.gitignore: ${pattern.trim()}`)
        }
      }
    }
    expect(offenders, `the committed artifact must not be ignored:\n${offenders.join('\n')}`).toEqual([])
  })
})

describe('the leaf rule this file applies', () => {
  it('allows node: built-ins and the capture family', () => {
    const moduleDir = join(captureRoot, 'harnesses', 'factory-plugin')
    expect(leafViolations("import { readFileSync } from 'node:fs'\n", moduleDir)).toEqual([])
    expect(leafViolations("import { mapHookEvent } from './mapper.js'\n", moduleDir)).toEqual([])
    expect(leafViolations("import { writeFileAtomic } from '../../consent-file.js'\n", moduleDir)).toEqual([])
  })

  it('allows a type-only import from anywhere, because it is erased at build time', () => {
    const moduleDir = join(captureRoot, 'harnesses', 'factory-plugin')
    expect(leafViolations("import type { A } from '../../../canon/x.js'\n", moduleDir)).toEqual([])
    expect(leafViolations("import { type A, b } from '../../../canon/x.js'\n", moduleDir)).toEqual([
      { specifier: '../../../canon/x.js', reason: 'outside-capture' },
    ])
  })

  it('flags a third-party package and a value import from outside the capture family', () => {
    const moduleDir = join(captureRoot, 'harnesses', 'factory-plugin')
    expect(leafViolations("import { z } from 'zod'\n", moduleDir)).toEqual([
      { specifier: 'zod', reason: 'third-party' },
    ])
    expect(leafViolations("import { r } from '../../../otel/receiver.js'\n", moduleDir)).toEqual([
      { specifier: '../../../otel/receiver.js', reason: 'outside-capture' },
    ])
  })
})

describe('the ignore matcher this file applies', () => {
  it('covers a path by name, by directory and by wildcard', () => {
    expect(patternCovers('node_modules', '', 'dash/node_modules/esbuild/lib/main.js')).toBe(true)
    expect(patternCovers('*.ts', '', 'dash/src/capture/entry.ts')).toBe(true)
    expect(patternCovers('*.ts', '', 'dash/src/capture/entry.js')).toBe(false)
    expect(patternCovers('/dash/dist/', '', 'dash/dist/app.js')).toBe(true)
    expect(patternCovers('dist/', 'dash', 'dash/dist/app.js')).toBe(true)
    expect(patternCovers('dist/', 'dash', 'dash/src/dist/app.js')).toBe(true)
    expect(patternCovers('dash/**/dist/', '', 'dash/src/dist/app.js')).toBe(true)
    expect(patternCovers('# a comment', '', 'dash/src/capture/entry.ts')).toBe(false)
    expect(patternCovers('!keep-me.ts', '', 'dash/src/capture/keep-me.ts')).toBe(false)
  })
})
