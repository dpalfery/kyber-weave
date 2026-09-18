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
 * @param {{ root: string, entries: string[], scanRoots: string[], aliases?: Record<string, string> }} options
 *   `root` is the directory paths are reported relative to; `entries`, `scanRoots` and alias
 *   targets are relative to it. `aliases` maps a specifier prefix such as `@` to a directory.
 * @returns {Promise<string[]>} unreachable non-test files, relative to `root`, sorted.
 */
export async function findUnreachable({ root, entries, scanRoots, aliases = {} }) {
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
      if (isTestOnly(rel) || reached.has(file)) continue
      unreachable.push(rel.split(sep).join('/'))
    }
  }
  return unreachable.sort()
}

/**
 * Entry points, with the reason each one is loaded other than by import.
 */
export const ENTRIES = [
  'src/cli.ts', //              the installed launcher
  'src/main.ts', //             tsup and SEA entry
  'src/parse-worker.ts', //     loaded by URL from parse-workers.ts as a worker thread
  'src/sea-shim.cjs', //        SEA entry that evaluates the bundled app
  'src/sea-devtools-stub.js', // aliased in by tsup.sea.config.ts
  'kyber/tools/parity.ts', //   operator tools, run with tsx
  'kyber/tools/reingest.ts',
  'kyber/tools/capture-content-fixture.mjs',
  'kyber/tools/cost-isolation.ts', // run by its test as a repository-wide check
  'dash/src/main.tsx', //       the web dashboard's Vite entry
]
export const SCAN_ROOTS = ['src', 'kyber', 'dash/src']
export const ALIASES = { '@': 'dash/src' }

const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)
if (isMain) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
  const unreachable = await findUnreachable({ root, entries: ENTRIES, scanRoots: SCAN_ROOTS, aliases: ALIASES })
  if (process.argv.includes('--json')) {
    process.stdout.write(JSON.stringify(unreachable, null, 2) + '\n')
  } else if (unreachable.length === 0) {
    process.stdout.write('check:reachable: every source file is reached by an entry point\n')
  } else {
    process.stdout.write(`check:reachable: ${unreachable.length} source file(s) no entry point reaches:\n`)
    for (const file of unreachable) process.stdout.write(`  ${file}\n`)
    process.stdout.write('Delete them with their tests, or add the entry that legitimately loads them.\n')
  }
  process.exit(unreachable.length === 0 ? 0 : 1)
}
