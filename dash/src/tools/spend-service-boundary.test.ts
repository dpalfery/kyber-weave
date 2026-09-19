// The quota, subscription and currency services are gone, and must stay gone (R2.7).
//
// Deleting the modules is not the guarantee — a later change can add them back one
// import at a time, and each step looks reasonable on its own. This walks every
// retained source file and fails on an import that resolves to one of them, so the
// first step is the one that fails rather than the tenth.
//
// The currency service is the sharpest case: it fetched live FX rates from
// api.frankfurter.app and multiplied them into displayed costs. Costs are USD now,
// from the bundled pricing table and nothing else (R2.8), so the host check below
// guards the behaviour as well as the module.

import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

import { walkSourceFiles } from './cost-isolation.js'

const DASH_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..')

/**
 * Modules whose return would reintroduce a banned dependency, as paths from the
 * dash root without an extension.
 *
 * Paths, not basenames: `src/act/plans.ts` is the action-plan model and has nothing
 * to do with subscription plans, so a name match would ban the wrong file.
 */
const BANNED_MODULES = new Set([
  'src/quota', //        provider quota windows
  'src/plans', //        subscription plan catalogue
  'src/plan-usage', //   subscription budget tracking
  'src/copilot-aiu', //  Copilot AI-credit conversion, read only by the plan code
  'src/currency', //     Frankfurter FX conversion
])

/** Hosts that would mean a third-party rate is being fetched again. */
const BANNED_HOSTS = [/\bapi\.frankfurter\.app\b/, /\bopenexchangerates\.org\b/, /\bexchangerate\b/i]

export type BannedImport = { file: string; specifier: string; resolved: string }

/**
 * Report every relative import in `source` that resolves to a banned module.
 *
 * Resolution is textual rather than filesystem-based on purpose: the banned modules
 * do not exist, so a resolver that requires the file to be present would report
 * nothing and the check would pass vacuously.
 */
export function findBannedImports(source: string, filePath: string, root = DASH_ROOT): BannedImport[] {
  const found: BannedImport[] = []
  for (const imported of ts.preProcessFile(source, true, true).importedFiles) {
    const specifier = imported.fileName
    if (!specifier.startsWith('.')) continue
    const absolute = path.resolve(path.dirname(filePath), specifier)
    const resolved = path.relative(root, absolute).split(path.sep).join('/').replace(/\.[cm]?[jt]sx?$/, '')
    if (BANNED_MODULES.has(resolved)) found.push({ file: filePath, specifier, resolved })
  }
  return found
}

describe('spend-service boundary (R2.7)', () => {
  // Runtime boundary: the walk covers modules that ship, not the colocated
  // tests about them — a test's fixture strings quote the banned imports
  // verbatim. (The old `kyber` walk is gone with the merged tree.)
  const sourceFiles = walkSourceFiles(path.join(DASH_ROOT, 'src'))
    .filter(file => !/\.(test|spec)\.[cm]?[jt]sx?$/.test(file))

  it('scans a non-empty source tree', () => {
    expect(sourceFiles.length).toBeGreaterThan(100)
  })

  it('imports no quota, subscription or currency service', () => {
    const violations = sourceFiles.flatMap(file => findBannedImports(readFileSync(file, 'utf8'), file))
    if (violations.length > 0) {
      const report = violations
        .map(v => `  - ${path.relative(DASH_ROOT, v.file)} imports '${v.specifier}' (${v.resolved})`)
        .join('\n')
      expect.fail(
        `Requirement 2.7 forbids the quota, subscription and currency services.\n` +
          `${violations.length} import(s) bring one back:\n${report}`
      )
    }
    expect(violations).toHaveLength(0)
  })

  it('calls no currency-exchange service', () => {
    const offenders = sourceFiles.filter(file => {
      const source = readFileSync(file, 'utf8')
      return BANNED_HOSTS.some(host => host.test(source))
    })
    expect(offenders.map(f => path.relative(DASH_ROOT, f))).toEqual([])
  })

  it('fails when a module imports the currency service again', () => {
    const file = path.join(DASH_ROOT, 'src', 'format.ts')
    const violations = findBannedImports("import { formatCost } from './currency.js'", file)
    expect(violations).toHaveLength(1)
    expect(violations[0]!.resolved).toBe('src/currency')
  })

  it('fails on a type-only import of the plan service, and on a nested one', () => {
    expect(
      findBannedImports("import type { PlanUsage } from '../plan-usage.js'", path.join(DASH_ROOT, 'src/act/report.ts'))
    ).toHaveLength(1)
    expect(
      findBannedImports("const q = await import('./quota.js')", path.join(DASH_ROOT, 'src/dashboard.tsx'))
    ).toHaveLength(1)
  })

  it('leaves the action-plan model alone', () => {
    const violations = findBannedImports(
      "import type { FindingPlan } from './plans.js'",
      path.join(DASH_ROOT, 'src/act/report.ts')
    )
    expect(violations).toEqual([])
  })
})
