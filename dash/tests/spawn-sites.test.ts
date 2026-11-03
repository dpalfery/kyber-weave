// Storm guard T6a (RED). Failure this prevents: a new unbounded process spawn lands in dash/
// without anyone deciding how it is limited. The 536-node fork bomb came from a spawn nothing
// bounded (a self-spawn re-entering the suite). This static audit lists every child_process
// spawn, Worker and webServer command in dash/ and fails until each one has a recorded bound.
//
// The scan is a TypeScript AST walk, not a grep. Comments, strings, and `.exec(` calls on regexes
// or sqlite handles are not sites. A child_process binding is recognised from its import (named,
// aliased, namespace, require, or dynamic import); a promisify(...) alias keeps its API name.
// Known limit: a local that shadows a bound name in another scope still counts. That errs toward
// failing, which is the safe direction for an audit.
//
// Contract:
//   1. every site is covered by an allow-list entry (same file and pattern) with a non-empty bound;
//   2. every allow-list entry still matches at least one site (no stale entries);
//   3. no self-spawn (a spawn of process.execPath or the kyberdash program) in non-test dash/src
//      lives outside src/process/spawn-guard.ts. Every self-spawn goes through guardedSpawn there,
//      so only that module imports child_process spawn for a self-spawn.
//
// The test spawns nothing. It only reads source files.
import { readdirSync, readFileSync } from 'node:fs'
import { join, relative, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

import { SPAWN_SITE_ALLOWLIST } from './spawn-sites.allowlist.js'

type Classification = 'self-spawn' | 'tool' | 'worker' | 'test-fake' | 'script'
type SiteKind = 'call' | 'ref' | 'worker' | 'webServer'

interface SpawnSite {
  /** Path relative to dash/, forward slashes. */
  readonly file: string
  readonly line: number
  /** The API name: spawn, exec, ..., 'new Worker', or 'webServer'. */
  readonly pattern: string
  /** call: invoked. ref: passed as a value (for example a default spawn function or promisify(execFile)). */
  readonly kind: SiteKind
  readonly classification: Classification
}

const DASH_ROOT = fileURLToPath(new URL('..', import.meta.url))
const SPAWN_GUARD_FILE = 'src/process/spawn-guard.ts'
const SPAWN_FAMILY = new Set(['spawn', 'spawnSync', 'exec', 'execSync', 'execFile', 'execFileSync', 'fork'])
const CHILD_PROCESS_MODULES = new Set(['child_process', 'node:child_process'])
const SKIPPED_DIRECTORIES = new Set(['node_modules', 'dist', 'coverage', '.claude'])
const SOURCE_FILE_NAME = /\.[cm]?[jt]sx?$/
/** Text that names the kyberdash program or its spawn target (a self-spawn, not a tool). */
const SELF_PROGRAM_TEXT = /execPath|KYBERDASH|kyberdash|SpawnTarget|spawnTarget|JobSpawner|DetachedLauncher|SPAWN_ROLE/

function listSourceFiles(root: string): string[] {
  const files: string[] = []
  const visit = (directory: string): void => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (entry.name.startsWith('.') || SKIPPED_DIRECTORIES.has(entry.name)) continue
      const full = join(directory, entry.name)
      if (entry.isDirectory()) visit(full)
      else if (entry.isFile() && SOURCE_FILE_NAME.test(entry.name)) files.push(full)
    }
  }
  visit(root)
  return files.sort()
}

function isTestPath(file: string): boolean {
  const segments = file.split('/')
  return (
    segments.includes('tests') ||
    segments.includes('e2e') ||
    /\.(test|spec)\.[cm]?[jt]sx?$/.test(file)
  )
}

function scriptKindFor(file: string): ts.ScriptKind {
  if (file.endsWith('.tsx')) return ts.ScriptKind.TSX
  if (/\.[cm]?jsx?$/.test(file)) return ts.ScriptKind.JS
  return ts.ScriptKind.TS
}

/** The module named by require('x') or import('x'), optionally awaited; undefined otherwise. */
function moduleOf(expression: ts.Expression | undefined): string | undefined {
  let node = expression
  if (node !== undefined && ts.isAwaitExpression(node)) node = node.expression
  if (node === undefined || !ts.isCallExpression(node)) return undefined
  const callee = node.expression
  const isRequire = ts.isIdentifier(callee) && callee.text === 'require'
  if (!isRequire && callee.kind !== ts.SyntaxKind.ImportKeyword) return undefined
  const argument = node.arguments[0]
  return argument !== undefined && ts.isStringLiteralLike(argument) ? argument.text : undefined
}

function calleeName(expression: ts.Expression): string | undefined {
  if (ts.isIdentifier(expression)) return expression.text
  if (ts.isPropertyAccessExpression(expression)) return expression.name.text
  return undefined
}

interface Bindings {
  /** Local name to the child_process API it refers to. */
  readonly calls: Map<string, string>
  /** Local names that are the whole child_process module (namespace, default, or require result). */
  readonly namespaces: Set<string>
}

function collectBindings(sourceFile: ts.SourceFile): Bindings {
  const calls = new Map<string, string>()
  const namespaces = new Set<string>()
  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      if (CHILD_PROCESS_MODULES.has(node.moduleSpecifier.text)) {
        const clause = node.importClause
        if (clause?.name) namespaces.add(clause.name.text)
        const named = clause?.namedBindings
        if (named !== undefined && ts.isNamespaceImport(named)) namespaces.add(named.name.text)
        if (named !== undefined && ts.isNamedImports(named)) {
          for (const element of named.elements) {
            const imported = (element.propertyName ?? element.name).text
            if (SPAWN_FAMILY.has(imported)) calls.set(element.name.text, imported)
          }
        }
      }
    }
    if (ts.isImportEqualsDeclaration(node) && ts.isExternalModuleReference(node.moduleReference)) {
      const module = node.moduleReference.expression
      if (ts.isStringLiteral(module) && CHILD_PROCESS_MODULES.has(module.text)) namespaces.add(node.name.text)
    }
    if (ts.isVariableDeclaration(node) && node.initializer !== undefined) {
      const module = moduleOf(node.initializer)
      if (module !== undefined && CHILD_PROCESS_MODULES.has(module)) {
        if (ts.isIdentifier(node.name)) namespaces.add(node.name.text)
        if (ts.isObjectBindingPattern(node.name)) {
          for (const element of node.name.elements) {
            if (!ts.isIdentifier(element.name)) continue
            const imported =
              element.propertyName !== undefined && ts.isIdentifier(element.propertyName)
                ? element.propertyName.text
                : element.name.text
            if (SPAWN_FAMILY.has(imported)) calls.set(element.name.text, imported)
          }
        }
      }
      // const execFileAsync = promisify(execFile): the alias keeps the API it wraps.
      const init = node.initializer
      if (
        ts.isIdentifier(node.name) &&
        ts.isCallExpression(init) &&
        calleeName(init.expression) === 'promisify' &&
        init.arguments[0] !== undefined &&
        ts.isIdentifier(init.arguments[0])
      ) {
        const api = calls.get(init.arguments[0].text)
        if (api !== undefined) calls.set(node.name.text, api)
      }
    }
    ts.forEachChild(node, visit)
  }
  visit(sourceFile)
  return { calls, namespaces }
}

function isInTypeContext(node: ts.Node): boolean {
  for (let current = node.parent; current !== undefined; current = current.parent) {
    if (ts.isTypeNode(current)) return true
    if (ts.isSourceFile(current)) return false
  }
  return false
}

/** True when the identifier reads the binding as a value (not a declaration, property name, or type). */
function isValueReference(node: ts.Identifier): boolean {
  const parent = node.parent
  if (parent === undefined || isInTypeContext(node)) return false
  if (ts.isPropertyAccessExpression(parent) && parent.name === node) return false
  if (ts.isBindingElement(parent) && parent.propertyName === node) return false
  const declaredName =
    ts.isVariableDeclaration(parent) ||
    ts.isParameter(parent) ||
    ts.isFunctionDeclaration(parent) ||
    ts.isClassDeclaration(parent) ||
    ts.isPropertyAssignment(parent) ||
    ts.isMethodDeclaration(parent) ||
    ts.isPropertyDeclaration(parent) ||
    ts.isPropertySignature(parent) ||
    ts.isMethodSignature(parent) ||
    ts.isBindingElement(parent)
      ? parent.name
      : undefined
  return declaredName !== node
}

function kindOf(node: ts.Node): 'call' | 'ref' {
  const parent = node.parent
  return parent !== undefined && ts.isCallExpression(parent) && parent.expression === node ? 'call' : 'ref'
}

function classify(file: string, node: ts.Node, kind: SiteKind, sourceFile: ts.SourceFile): Classification {
  if (kind === 'worker') return 'worker'
  if (kind === 'webServer') return 'script'
  if (isTestPath(file)) return 'test-fake'
  if (!file.startsWith('src/')) return 'script'
  let context = ''
  if (kind === 'call' && node.parent !== undefined && ts.isCallExpression(node.parent)) {
    context = node.parent.arguments[0]?.getText(sourceFile) ?? ''
  } else {
    let top: ts.Node = node
    while (top.parent !== undefined && !ts.isSourceFile(top.parent)) top = top.parent
    context = top.getText(sourceFile)
  }
  return SELF_PROGRAM_TEXT.test(context) ? 'self-spawn' : 'tool'
}

function findSites(file: string, text: string): SpawnSite[] {
  const sourceFile = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, scriptKindFor(file))
  const { calls, namespaces } = collectBindings(sourceFile)
  const sites: SpawnSite[] = []
  const record = (node: ts.Node, pattern: string, kind: SiteKind): void => {
    const line = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1
    sites.push({ file, line, pattern, kind, classification: classify(file, node, kind, sourceFile) })
  }
  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) || ts.isImportEqualsDeclaration(node)) return
    if (ts.isIdentifier(node)) {
      const api = calls.get(node.text)
      if (api !== undefined && isValueReference(node)) record(node, api, kindOf(node))
    }
    if (
      ts.isPropertyAccessExpression(node) &&
      ts.isIdentifier(node.expression) &&
      namespaces.has(node.expression.text) &&
      SPAWN_FAMILY.has(node.name.text)
    ) {
      record(node, node.name.text, kindOf(node))
    }
    if (ts.isNewExpression(node) && calleeName(node.expression) === 'Worker') {
      record(node, 'new Worker', 'worker')
    }
    if (ts.isPropertyAssignment(node) && (ts.isIdentifier(node.name) || ts.isStringLiteral(node.name))) {
      if (node.name.text === 'webServer') record(node, 'webServer', 'webServer')
    }
    ts.forEachChild(node, visit)
  }
  visit(sourceFile)
  return sites
}

function scanDash(): SpawnSite[] {
  const sites: SpawnSite[] = []
  for (const full of listSourceFiles(DASH_ROOT)) {
    const file = relative(DASH_ROOT, full).split(sep).join('/')
    sites.push(...findSites(file, readFileSync(full, 'utf8')))
  }
  return sites.sort((a, b) => (a.file === b.file ? a.line - b.line : a.file.localeCompare(b.file)))
}

function formatSite(site: SpawnSite): string {
  return `${site.file}:${site.line}  ${site.pattern}  [${site.kind}, ${site.classification}]  key=${site.file}#${site.pattern}`
}

function isListed(site: SpawnSite): boolean {
  return SPAWN_SITE_ALLOWLIST.some((entry) => entry.file === site.file && entry.pattern === site.pattern)
}

const sites = scanDash()

describe('spawn-site audit (storm guard T6)', () => {
  it('the scanner finds the spawn sites the repository already has', () => {
    expect(sites.length, 'the scan found no sites at all; the audit would pass blindly').toBeGreaterThan(0)
  })

  it('every child_process spawn, Worker and webServer site in dash/ is on the allow-list with a bound', () => {
    const unlisted = sites.filter((site) => !isListed(site))
    const listing = sites
      .map((site) => `${isListed(site) ? 'listed  ' : 'UNLISTED'} ${formatSite(site)}`)
      .join('\n')
    expect(
      unlisted.map(formatSite),
      `${unlisted.length} of ${sites.length} spawn site(s) have no allow-list entry. Add each with its bound ` +
        `to dash/tests/spawn-sites.allowlist.ts.\nFull site listing:\n${listing}`,
    ).toEqual([])
  })

  it('the allow-list names no site that no longer exists', () => {
    const stale = SPAWN_SITE_ALLOWLIST.filter(
      (entry) => !sites.some((site) => site.file === entry.file && site.pattern === entry.pattern),
    ).map((entry) => `${entry.file}  ${entry.pattern}`)
    expect(stale, 'stale allow-list entries: remove them or restore the site they describe').toEqual([])
  })

  it('every allow-list entry names its file, its pattern, and a non-empty bound', () => {
    const malformed = SPAWN_SITE_ALLOWLIST.filter(
      (entry) => entry.file.trim() === '' || entry.pattern.trim() === '' || entry.bound.trim() === '',
    ).map((entry) => `${entry.file || '(no file)'}  ${entry.pattern || '(no pattern)'}`)
    expect(malformed, 'each allow-list entry needs a file, a pattern, and a bound that says how the site is limited').toEqual([])
  })

  it('no self-spawn in non-test dash/src lives outside the spawn guard', () => {
    const outside = sites
      .filter(
        (site) =>
          site.classification === 'self-spawn' &&
          site.file.startsWith('src/') &&
          !isTestPath(site.file) &&
          site.file !== SPAWN_GUARD_FILE,
      )
      .map(formatSite)
    expect(
      outside,
      'self-spawns (process.execPath or the kyberdash program) must go through guardedSpawn in src/process/spawn-guard.ts, which is the only module that imports child_process spawn for them',
    ).toEqual([])
  })
})
