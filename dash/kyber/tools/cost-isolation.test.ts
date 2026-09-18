// Static AST test enforcing Decision D9 cost isolation (docs/rules/secondary-cost-display.md):
// diagnostic and context contracts carry no cost-shaped types, and pure diagnostic modules
// import no cost or pricing engine. The ADR 0006 merge-zone import boundary this suite once
// also enforced was retired with the one-time fork (ADR 0020).

import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

import {
  checkDiagnosticCostImports,
  checkSourceAstForCostPollution,
  scanCostIsolation,
  walkSourceFiles,
} from './cost-isolation.js'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

// Resolve dash root (dash/kyber/tools -> dash/)
const DASH_ROOT = path.resolve(__dirname, '../..')

describe('D9 cost isolation', () => {
  // -------------------------------------------------------------------------
  // Live Repository Tests
  // -------------------------------------------------------------------------

  describe('Live Repository Scans', () => {
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

    it('scans a non-empty source tree', () => {
      expect(walkSourceFiles(path.resolve(DASH_ROOT, 'kyber')).length).toBeGreaterThan(40)
    })
  })

  describe('Mechanical Failure Verification (Negative Tests)', () => {
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
