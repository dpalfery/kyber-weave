// Static import-graph AST boundary test enforcing ADR 0006 merge-zone boundaries
// and Decision D9 cost isolation / secondary rule (Plan Task H1).
//
// Acceptance Criteria:
// 1. Parse imports across `dash/kyber/**` and `dash/dash/src/**`.
// 2. Mechanically fail if non-adapter modules (outside `dash/kyber/adapter/**` or `dash/kyber/synth/**`)
//    import vendored CodeBurn internals (`dash/src/**` or root `src/**`).
// 3. Mechanically fail if cost types or pricing calculations pollute diagnostic / context contracts
//    in `dash/kyber/analysis/**` or `dash/kyber/canon/**` (per D9 cost secondary rule).
// 4. Ensure `npx --prefix dash vitest run kyber/tools/boundary.test.ts` passes with 100%.
// 5. Run `npm --prefix dash test` to ensure full suite passes.

import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

import {
  checkDiagnosticCostImports,
  checkImportBoundary,
  checkSourceAstForCostPollution,
  extractImportsFromAst,
  isAllowedUpstreamImporter,
  isVendoredCodeBurnInternal,
  scanCostIsolation,
  scanMergeZoneBoundaries,
  walkSourceFiles,
} from './boundary.js'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

// Resolve dash root (dash/kyber/tools -> dash/)
const DASH_ROOT = path.resolve(__dirname, '../..')

describe('ADR 0006 Merge-Zone Boundary & D9 Cost Isolation (Task H1)', () => {
  // -------------------------------------------------------------------------
  // Live Repository Tests
  // -------------------------------------------------------------------------

  describe('Live Repository Scans', () => {
    it('enforces merge-zone boundaries across dash/kyber/** and dash/dash/src/**', () => {
      // Acceptance Criterion 1 & 2:
      // Parse imports across dash/kyber/** and dash/dash/src/**.
      // Mechanically fail if non-adapter modules (outside canon/adapters/** or synth/**)
      // import vendored CodeBurn internals (dash/src/** or root src/**).
      const violations = scanMergeZoneBoundaries(DASH_ROOT)

      if (violations.length > 0) {
        const report = violations
          .map(
            v =>
              `  - ${path.relative(DASH_ROOT, v.file)}:${v.line}:${v.character}\n` +
              `    Import: '${v.specifier}' -> '${path.relative(DASH_ROOT, v.resolvedTarget)}'\n` +
              `    Reason: ${v.reason}`
          )
          .join('\n')
        expect.fail(`Found ${violations.length} merge-zone boundary violation(s):\n${report}`)
      }

      expect(violations).toHaveLength(0)
    })

    it('enforces cost isolation on diagnostic and context contracts (per Decision D9)', () => {
      // Acceptance Criterion 3:
      // Mechanically fail if cost types or pricing calculations pollute diagnostic / context
      // contracts in dash/kyber/analysis/** or dash/kyber/canon/**.
      const { contractViolations, importViolations } = scanCostIsolation(DASH_ROOT)

      if (contractViolations.length > 0) {
        const report = contractViolations
          .map(
            v =>
              `  - ${path.relative(DASH_ROOT, v.file)}:${v.line}:${v.character}\n` +
              `    Contract: '${v.contractName}'\n` +
              `    Polluting Property: '${v.propertyName}' (${v.propertyType})\n` +
              `    Reason: ${v.reason}`
          )
          .join('\n')
        expect.fail(`Found ${contractViolations.length} contract cost pollution violation(s):\n${report}`)
      }

      if (importViolations.length > 0) {
        const report = importViolations
          .map(
            v =>
              `  - ${path.relative(DASH_ROOT, v.file)}:${v.line}:${v.character}\n` +
              `    Import: '${v.specifier}'\n` +
              `    Reason: ${v.reason}`
          )
          .join('\n')
        expect.fail(`Found ${importViolations.length} diagnostic cost import violation(s):\n${report}`)
      }

      expect(contractViolations).toHaveLength(0)
      expect(importViolations).toHaveLength(0)
    })

    it('verifies non-empty file coverage across merge zone and dashboard source', () => {
      const kyberFiles = walkSourceFiles(path.resolve(DASH_ROOT, 'kyber'))
      const dashSrcFiles = walkSourceFiles(path.resolve(DASH_ROOT, 'dash/src'))

      expect(kyberFiles.length).toBeGreaterThan(40)
      expect(dashSrcFiles.length).toBeGreaterThan(20)
    })
  })

  // -------------------------------------------------------------------------
  // Mechanical Enforcement & Negative Test Cases
  // -------------------------------------------------------------------------

  describe('Mechanical Failure Verification (Negative Tests)', () => {
    it('mechanically fails when a non-adapter module imports vendored CodeBurn internals', () => {
      const fakeFilePath = path.resolve(DASH_ROOT, 'kyber/analysis/bad_import.ts')
      const fakeSource = `
        import { parseApiCall } from '../../src/parser.js'
        export function compute() { return parseApiCall() }
      `
      const sf = ts.createSourceFile(fakeFilePath, fakeSource, ts.ScriptTarget.Latest, true)
      const imports = extractImportsFromAst(sf)
      expect(imports).toHaveLength(1)

      const violation = checkImportBoundary(fakeFilePath, imports[0]!, DASH_ROOT)
      expect(violation).not.toBeNull()
      expect(violation?.reason).toContain('boundary violation')
      expect(violation?.reason).toContain('bad_import.ts')
      expect(violation?.reason).toContain('src/parser.js')
    })

    it('mechanically fails when a UI module in dash/dash/src imports vendored CodeBurn internals', () => {
      const fakeFilePath = path.resolve(DASH_ROOT, 'dash/src/components/BadUI.tsx')
      const fakeSource = `
        import { loadPricing } from '../../../src/models.js'
        export function BadUI() { return <div>{loadPricing()}</div> }
      `
      const sf = ts.createSourceFile(fakeFilePath, fakeSource, ts.ScriptTarget.Latest, true)
      const imports = extractImportsFromAst(sf)

      const violation = checkImportBoundary(fakeFilePath, imports[0]!, DASH_ROOT)
      expect(violation).not.toBeNull()
      expect(violation?.reason).toContain('boundary violation')
      expect(violation?.reason).toContain('src/models.js')
    })

    it('permits adapter and synthesizer modules to import vendored CodeBurn interfaces', () => {
      const synthFilePath = path.resolve(DASH_ROOT, 'kyber/synth/custom_reader.ts')
      const adapterFilePath = path.resolve(DASH_ROOT, 'kyber/canon/adapters/custom_adapter.ts')

      expect(isAllowedUpstreamImporter(synthFilePath)).toBe(true)
      expect(isAllowedUpstreamImporter(adapterFilePath)).toBe(true)

      const fakeSource = `import type { ParsedProviderCall } from '../../src/providers/types.js'`
      const sf = ts.createSourceFile('test.ts', fakeSource, ts.ScriptTarget.Latest, true)
      const imports = extractImportsFromAst(sf)

      expect(checkImportBoundary(synthFilePath, imports[0]!, DASH_ROOT)).toBeNull()
      expect(checkImportBoundary(adapterFilePath, imports[0]!, DASH_ROOT)).toBeNull()
    })

    it('permits imports of KyberDash own brand overlay from src/brand-overlay', () => {
      const brandingTestPath = path.resolve(DASH_ROOT, 'kyber/branding/brand.test.ts')
      const overlayPath = path.resolve(DASH_ROOT, 'src/brand-overlay.js')

      expect(isVendoredCodeBurnInternal(overlayPath)).toBe(false)

      const fakeSource = `import { BRAND } from '../../src/brand-overlay.js'`
      const sf = ts.createSourceFile('test.ts', fakeSource, ts.ScriptTarget.Latest, true)
      const imports = extractImportsFromAst(sf)

      expect(checkImportBoundary(brandingTestPath, imports[0]!, DASH_ROOT)).toBeNull()
    })

    it('mechanically fails when a cost property is added to RunRow', () => {
      const fakeCode = `
        import type { CostBlock } from './cost.js'
        export type RunRow = {
          runId: string
          harness: string
          cost?: CostBlock
        }
      `
      const sf = ts.createSourceFile('runs.ts', fakeCode, ts.ScriptTarget.Latest, true)
      const violations = checkSourceAstForCostPollution(sf, 'runs.ts')

      expect(violations.length).toBeGreaterThan(0)
      expect(violations.some(v => v.contractName === 'RunRow' && v.propertyName === 'cost')).toBe(true)
    })

    it('mechanically fails when a spend property is added to Finding', () => {
      const fakeCode = `
        export type Finding = {
          id: string
          title: string
          spend: number
        }
      `
      const sf = ts.createSourceFile('findings.ts', fakeCode, ts.ScriptTarget.Latest, true)
      const violations = checkSourceAstForCostPollution(sf, 'findings.ts')

      expect(violations.length).toBeGreaterThan(0)
      expect(violations.some(v => v.contractName === 'Finding' && v.propertyName === 'spend')).toBe(true)
    })

    it('mechanically fails when a cost-shaped type is referenced on ExecutionRow', () => {
      const fakeCode = `
        import type { CostStatus } from './types.js'
        export type ExecutionRow = {
          executionId: string
          status: CostStatus
        }
      `
      const sf = ts.createSourceFile('types.ts', fakeCode, ts.ScriptTarget.Latest, true)
      const violations = checkSourceAstForCostPollution(sf, 'types.ts')

      expect(violations.length).toBeGreaterThan(0)
      expect(violations.some(v => v.contractName === 'ExecutionRow' && v.propertyType === 'CostStatus')).toBe(true)
    })

    it('permits token-based waste and residency figures on diagnostic contracts', () => {
      const validCode = `
        export type Finding = {
          id: string
          title: string
          estimatedWasteTokens: number
        }
        export type ContextPart = {
          part: string
          text: string
          tokens?: number
        }
      `
      const sf = ts.createSourceFile('valid.ts', validCode, ts.ScriptTarget.Latest, true)
      const violations = checkSourceAstForCostPollution(sf, 'valid.ts')

      expect(violations).toHaveLength(0)
    })

    it('mechanically fails when a pure diagnostic module imports canon/cost.ts', () => {
      const fakeFilePath = path.resolve(DASH_ROOT, 'kyber/analysis/findings.ts')
      const fakeCode = `
        import { sumCosts } from '../canon/cost.js'
        export const x = 1
      `
      const sf = ts.createSourceFile(fakeFilePath, fakeCode, ts.ScriptTarget.Latest, true)
      const violations = checkDiagnosticCostImports(sf, fakeFilePath, DASH_ROOT)

      expect(violations.length).toBeGreaterThan(0)
      expect(violations[0]!.reason).toContain('Decision D9 cost-secondary violation')
      expect(violations[0]!.specifier).toBe('../canon/cost.js')
    })
  })
})
