#!/usr/bin/env node
// Lists source files that no entry point reaches.
//
// This walks the import graph from the entries and reports each non-test source file under
// the scanned roots that the walk never visits. Deleting a command and then running this is
// how the modules that only that command used are found; running it in CI is how they stay
// found.
//
// The graph comes from TypeScript's `preProcessFile`, not from a bundler's metafile. A bundler
// erases type-only imports, so a module that exports nothing but types would always look
// dead to it, although deleting it breaks the build. `preProcessFile` reports static, type-only
// and dynamic `import()` specifiers alike, and `typescript` is already a dev dependency, so no
// new package (such as knip) is needed to answer the question.
//
// Two passes run, because a file can be dead in two different ways:
//
//   production  entries are ENTRIES; reports src/ and web/src/ modules no entry reaches.
//   test        entries are every *.test.ts/*.spec.ts plus TEST_ENTRIES; reports test-support
//               files - fixtures, helpers, setup - that no test reaches.
//
// The test pass exists because the production pass cannot see these files at all: its scan
// roots exclude tests/, and isTestOnly() filters out anything under a fixtures/ directory so
// that test files do not all read as dead from a production entry point. That left
// test-support code with no gate on it, and a mock identity provider outlived by two years
// the sign-in feature it existed for.
//
// Usage: node scripts/unreachable.mjs        prints unreachable files, exits 1 if any
//        node scripts/unreachable.mjs --json prints them as a JSON array

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs'
import { dirname, join, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

const SOURCE_EXTENSIONS = ['.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.mjs', '.cjs']

/**
 * A file is test-only, and therefore never expected on a production path, when its name marks
 * it as a test or a test kit, or it lives under a directory that only tests read.
 */
export function isTestOnly(relPath) {
  const parts = relPath.split(/[\\/]/)
  const name = parts[parts.length - 1]
  if (/\.(test|spec)\.[cm]?[jt]sx?$/.test(name)) return true
  if (name.endsWith('.d.ts') || /^testing\.[cm]?[jt]sx?$/.test(name)) return true
  return parts.some(part => part === '__snapshots__' || part === 'fixtures' || part === '__fixtures__' || part === 'test-utils')
}

const TEST_FILE_RE = /\.(test|spec)\.[cm]?[jt]sx?$/

/**
 * A file is test-support when tests are the only thing that should reach it, but it is not
 * itself a test: fixtures, helpers, worker bodies, setup. These are what the test pass scans.
 */
export function isTestSupport(relPath) {
  const parts = relPath.split(/[\\/]/)
  const name = parts[parts.length - 1]
  if (TEST_FILE_RE.test(name)) return false // a test is an entry, never a subject
  if (name.endsWith('.d.ts')) return false
  if (/^testing\.[cm]?[jt]sx?$/.test(name)) return true
  return parts.some(part => part === 'fixtures' || part === '__fixtures__' || part === 'test-utils') ||
    relPath.split(/[\\/]/).slice(0, 2).join('/') === 'tests/setup'
}

function walk(dir, out) {
  let entries
  try {
    entries = readdirSync(dir)
  } catch {
    return out
  }
  for (const name of entries) {
    if (name === 'node_modules' || name === 'dist' || name.startsWith('.')) continue
    const path = join(dir, name)
    if (statSync(path).isDirectory()) walk(path, out)
    else if (SOURCE_EXTENSIONS.some(ext => name.endsWith(ext))) out.push(path)
  }
  return out
}

/** Resolve a relative or aliased specifier to a source file, the way TS's bundler resolution would. */
function resolveSpecifier(importer, specifier, aliases) {
  let base
  if (specifier.startsWith('.')) {
    base = resolve(dirname(importer), specifier)
  } else {
    const alias = Object.keys(aliases).find(prefix => specifier === prefix || specifier.startsWith(prefix + '/'))
    if (!alias) return null // a package: never this project's dead code
    base = resolve(aliases[alias], specifier.slice(alias.length + 1))
  }
  const stem = base.replace(/\.([cm]?js|jsx)$/, '')
  const candidates = [
    base,
    ...SOURCE_EXTENSIONS.map(ext => stem + ext),
    ...SOURCE_EXTENSIONS.map(ext => join(base, 'index' + ext)),
  ]
  return candidates.find(candidate => existsSync(candidate) && statSync(candidate).isFile()) ?? null
}

/**
 * @param {{ root: string, entries: string[], scanRoots: string[], aliases?: Record<string, string>,
 *   subject?: (relPath: string) => boolean }} options
 *   `root` is the directory paths are reported relative to; `entries`, `scanRoots` and alias
 *   targets are relative to it. `aliases` maps a specifier prefix such as `@` to a directory.
 *   `subject` decides which walked files this pass reports on; the default reports every file
 *   that is not test-only, which is the production pass.
 * @returns {Promise<string[]>} unreachable subject files, relative to `root`, sorted.
 */
export async function findUnreachable({ root, entries, scanRoots, aliases = {}, subject = rel => !isTestOnly(rel) }) {
  const absoluteAliases = Object.fromEntries(Object.entries(aliases).map(([prefix, dir]) => [prefix, resolve(root, dir)]))
  const reached = new Set()
  const queue = entries.map(entry => resolve(root, entry))
  while (queue.length > 0) {
    const file = queue.pop()
    if (reached.has(file) || !existsSync(file)) continue
    reached.add(file)
    const info = ts.preProcessFile(readFileSync(file, 'utf8'), true, true)
    for (const imported of info.importedFiles) {
      const target = resolveSpecifier(file, imported.fileName, absoluteAliases)
      if (target && !reached.has(target)) queue.push(target)
    }
  }
  const unreachable = []
  for (const scanRoot of scanRoots) {
    for (const file of walk(resolve(root, scanRoot), [])) {
      const rel = relative(root, file)
      if (!subject(rel) || reached.has(file)) continue
      unreachable.push(rel.split(sep).join('/'))
    }
  }
  return unreachable.sort()
}

/**
 * Entry points, with the reason each one is loaded other than by import.
 */
export const ENTRIES = [
  'src/launcher.ts', //         the installed launcher
  'src/cli/main.ts', //         tsup and SEA entry
  'src/ingest/parse-worker.ts', // loaded by URL from parse-workers.ts as a worker thread
  'src/sea-shim.cjs', //        SEA entry that evaluates the bundled app
  'src/sea-devtools-stub.js', // aliased in by tsup.sea.config.ts
  'src/tools/parity.ts', //     operator tools, run with tsx
  'src/tools/reingest.ts',
  'src/tools/capture-content-fixture.mjs',
  'src/tools/cost-isolation.ts', // run by its test as a repository-wide check
  'src/tools/report-fixtures.ts', // `npm run report:fixtures`, regenerates the renderer fixtures
  'web/src/main.tsx', //        the web dashboard's Vite entry
]
export const SCAN_ROOTS = ['src', 'web/src']
export const ALIASES = { '@': 'web/src' }

/**
 * Test-support files loaded other than by an import, so the walk cannot find them itself.
 * Each is spawned or injected by path, which `preProcessFile` cannot see - the path is a
 * string literal, not a specifier. Anything not listed here has to be imported by a test.
 */
export const TEST_ENTRIES = [
  'tests/setup/env-isolation.ts', //                  vitest.config.ts setupFiles
  'tests/fixtures/cache-refresh-worker.ts', //        spawned by path from lock-process.test.ts
  'tests/fixtures/cache-refresh-corrupt-owner.ts', // spawned via spawnFixture() in lock-corrupt-body.test.ts
  'tests/fixtures/cache-refresh-slow-owner.ts', //    spawned via spawnFixture() in lock-corrupt-body.test.ts
]

/** Where tests and their support live; mirrors vitest.config.ts `include`. */
export const TEST_SCAN_ROOTS = ['src', 'web/src', 'tests', 'scripts']

/** Every test file under the test scan roots, which are the test pass's entry points. */
export function findTestFiles(root) {
  const found = []
  for (const scanRoot of TEST_SCAN_ROOTS) {
    for (const file of walk(resolve(root, scanRoot), [])) {
      const rel = relative(root, file).split(sep).join('/')
      if (TEST_FILE_RE.test(rel)) found.push(rel)
    }
  }
  return found.sort()
}

const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)
if (isMain) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
  const unreachable = await findUnreachable({ root, entries: ENTRIES, scanRoots: SCAN_ROOTS, aliases: ALIASES })
  const unreachableTestSupport = await findUnreachable({
    root,
    entries: [...TEST_ENTRIES, ...findTestFiles(root)],
    scanRoots: TEST_SCAN_ROOTS,
    aliases: ALIASES,
    subject: isTestSupport,
  })
  if (process.argv.includes('--json')) {
    process.stdout.write(JSON.stringify({ source: unreachable, testSupport: unreachableTestSupport }, null, 2) + '\n')
    process.exit(unreachable.length + unreachableTestSupport.length === 0 ? 0 : 1)
  }
  if (unreachable.length === 0) {
    process.stdout.write('check:reachable: every source file is reached by an entry point\n')
  } else {
    process.stdout.write(`check:reachable: ${unreachable.length} source file(s) no entry point reaches:\n`)
    for (const file of unreachable) process.stdout.write(`  ${file}\n`)
    process.stdout.write('Delete them with their tests, or add the entry that legitimately loads them.\n')
  }
  if (unreachableTestSupport.length === 0) {
    process.stdout.write('check:reachable: every test-support file is reached by a test\n')
  } else {
    process.stdout.write(`check:reachable: ${unreachableTestSupport.length} test-support file(s) no test reaches:\n`)
    for (const file of unreachableTestSupport) process.stdout.write(`  ${file}\n`)
    process.stdout.write('Delete them, or add the TEST_ENTRIES line naming what loads them by path.\n')
  }
  process.exit(unreachable.length + unreachableTestSupport.length === 0 ? 0 : 1)
}
