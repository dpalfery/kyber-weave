// Static AST cost-isolation enforcement for KyberDash (Decision D9: cost is a secondary
// derived column; docs/rules/secondary-cost-display.md).
//
// Rules enforced:
// - Diagnostic and context contracts (Run, AgentExecution, Finding, Scorecard / HarnessRollup,
//   SignalMeasured, ContextTurn, etc.) must NEVER carry cost-shaped types (CostBlock, CostBasis,
//   CostStatus, RateTable) or pricing/spend properties.
// - Diagnostic modules (findings, signals, runs, outcome, context composition) must NOT import
//   from `canon/cost.ts` or external pricing engines.
//
// This file once also enforced the ADR 0006 merge-zone import boundary. That rule was retired
// with the one-time fork (ADR 0020); cost isolation is independent of it and stays.

import fs from 'node:fs'
import path from 'node:path'
import ts from 'typescript'

// ---------------------------------------------------------------------------
// Types & Contracts
// ---------------------------------------------------------------------------

export type ImportLocation = {
  specifier: string
  line: number
  character: number
  isDynamic: boolean
  isTypeOnly: boolean
}

export type CostPollutionViolation = {
  file: string
  contractName: string
  propertyName: string
  propertyType: string
  line: number
  character: number
  reason: string
}

export type DiagnosticCostImportViolation = {
  file: string
  specifier: string
  line: number
  character: number
  reason: string
}

// ---------------------------------------------------------------------------
// File Enumeration
// ---------------------------------------------------------------------------

const SOURCE_EXTENSIONS = new Set(['.ts', '.tsx', '.js', '.jsx', '.mjs', '.cjs'])
const IGNORED_DIRS = new Set(['node_modules', 'dist', '.git', '.claude', 'build'])

/**
 * Walk a directory recursively and return all TypeScript and JavaScript source files.
 */
export function walkSourceFiles(rootDir: string): string[] {
  const results: string[] = []

  function walk(dir: string) {
    if (!fs.existsSync(dir)) return
    const entries = fs.readdirSync(dir, { withFileTypes: true })
    for (const entry of entries) {
      if (IGNORED_DIRS.has(entry.name)) continue
      const fullPath = path.join(dir, entry.name)
      if (entry.isDirectory()) {
        walk(fullPath)
      } else if (entry.isFile()) {
        const ext = path.extname(entry.name).toLowerCase()
        if (SOURCE_EXTENSIONS.has(ext)) {
          results.push(fullPath)
        }
      }
    }
  }

  walk(rootDir)
  return results.sort()
}

// ---------------------------------------------------------------------------
// AST Import Extraction
// ---------------------------------------------------------------------------

/**
 * Extract all static and dynamic import/export module specifiers from a TypeScript AST.
 */
export function extractImportsFromAst(sourceFile: ts.SourceFile): ImportLocation[] {
  const imports: ImportLocation[] = []

  function visit(node: ts.Node) {
    if (ts.isImportDeclaration(node)) {
      if (node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)) {
        const { line, character } = sourceFile.getLineAndCharacterOfPosition(node.getStart())
        imports.push({
          specifier: node.moduleSpecifier.text,
          line: line + 1,
          character: character + 1,
          isDynamic: false,
          isTypeOnly: node.importClause?.isTypeOnly ?? false,
        })
      }
    } else if (ts.isExportDeclaration(node)) {
      if (node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)) {
        const { line, character } = sourceFile.getLineAndCharacterOfPosition(node.getStart())
        imports.push({
          specifier: node.moduleSpecifier.text,
          line: line + 1,
          character: character + 1,
          isDynamic: false,
          isTypeOnly: node.isTypeOnly,
        })
      }
    } else if (ts.isCallExpression(node)) {
      // Dynamic import: import('...')
      if (
        node.expression.kind === ts.SyntaxKind.ImportKeyword &&
        node.arguments.length > 0 &&
        ts.isStringLiteral(node.arguments[0])
      ) {
        const { line, character } = sourceFile.getLineAndCharacterOfPosition(node.getStart())
        imports.push({
          specifier: node.arguments[0].text,
          line: line + 1,
          character: character + 1,
          isDynamic: true,
          isTypeOnly: false,
        })
      }
      // CommonJS require: require('...')
      if (
        ts.isIdentifier(node.expression) &&
        node.expression.text === 'require' &&
        node.arguments.length > 0 &&
        ts.isStringLiteral(node.arguments[0])
      ) {
        const { line, character } = sourceFile.getLineAndCharacterOfPosition(node.getStart())
        imports.push({
          specifier: node.arguments[0].text,
          line: line + 1,
          character: character + 1,
          isDynamic: true,
          isTypeOnly: false,
        })
      }
    }
    ts.forEachChild(node, visit)
  }

  visit(sourceFile)
  return imports
}

// ---------------------------------------------------------------------------
// Cost Isolation Rule Checking (Decision D9 & Task H1)
// ---------------------------------------------------------------------------

/**
 * Diagnostic contracts that must never carry cost types or pricing calculations per Decision D9.
 */
export const DIAGNOSTIC_CONTRACT_NAMES = new Set([
  'RunRow',
  'Run',
  'ExecutionRow',
  'AgentExecution',
  'ExecutionTreeNode',
  'ExecutionTree',
  'HarnessRollupRow',
  'Finding',
  'FindingEvidenceLink',
  'FindingErrorBar',
  'SignalMeasured',
  'SignalNotMeasurable',
  'OutcomeBlock',
  'ContextPart',
  'ContextTurn',
  'ContextAnalysis',
  'CompositionBuckets',
  'ClassifiedTurnContext',
  'ContextItem',
  'ClassifiedContextItem',
])

/** Disallowed property names representing monetary cost, spend, pricing, or currency rates. */
const DISALLOWED_COST_PROPERTY_REGEX =
  /^(cost|spend|spending|price|pricing|dollars?|cents?|billed|rates?|costBasis|costBlock|costStatus)$/i

/** Disallowed cost types on diagnostic contracts. */
const DISALLOWED_COST_TYPE_REGEX =
  /\b(CostBlock|CostBasis|CostStatus|RateTable|SchemaCostRates)\b/

/** Allowed exceptions (e.g. estimatedWasteTokens is token count, not cost). */
function isAllowedTokenOrDiagnosticProperty(propName: string): boolean {
  // estimatedWasteTokens, tokenResidencies, etc. are token counts
  return /token/i.test(propName)
}

/**
 * Inspect a TypeLiteralNode or InterfaceDeclaration members for cost pollution.
 */
function inspectMembersForCostPollution(
  members: ts.NodeArray<ts.TypeElement>,
  contractName: string,
  sourceFile: ts.SourceFile,
  filePath: string
): CostPollutionViolation[] {
  const violations: CostPollutionViolation[] = []

  for (const member of members) {
    if (ts.isPropertySignature(member)) {
      const propName = member.name.getText(sourceFile)
      const propType = member.type ? member.type.getText(sourceFile) : ''
      const { line, character } = sourceFile.getLineAndCharacterOfPosition(member.getStart())

      // 1. Property Name check
      if (DISALLOWED_COST_PROPERTY_REGEX.test(propName) && !isAllowedTokenOrDiagnosticProperty(propName)) {
        violations.push({
          file: filePath,
          contractName,
          propertyName: propName,
          propertyType: propType,
          line: line + 1,
          character: character + 1,
          reason:
            `Decision D9 cost-secondary violation: diagnostic contract '${contractName}' carries cost property '${propName}'. ` +
            `Spend and cost figures must remain derived and isolated from diagnostic contracts.`,
        })
      }

      // 2. Property Type check
      if (DISALLOWED_COST_TYPE_REGEX.test(propType)) {
        violations.push({
          file: filePath,
          contractName,
          propertyName: propName,
          propertyType: propType,
          line: line + 1,
          character: character + 1,
          reason:
            `Decision D9 cost-secondary violation: diagnostic contract '${contractName}' property '${propName}' references cost type '${propType}'. ` +
            `Cost types (CostBlock, CostBasis, CostStatus, RateTable) must not pollute diagnostic contracts.`,
        })
      }
    }
  }

  return violations
}

/**
 * Scan a TypeScript source file AST for cost pollution within declared diagnostic contracts.
 */
export function checkSourceAstForCostPollution(
  sourceFile: ts.SourceFile,
  filePath: string,
  contractNames: Set<string> = DIAGNOSTIC_CONTRACT_NAMES
): CostPollutionViolation[] {
  const violations: CostPollutionViolation[] = []

  function visit(node: ts.Node) {
    if (ts.isTypeAliasDeclaration(node) && contractNames.has(node.name.text)) {
      const contractName = node.name.text
      if (ts.isTypeLiteralNode(node.type)) {
        violations.push(...inspectMembersForCostPollution(node.type.members, contractName, sourceFile, filePath))
      } else if (ts.isIntersectionTypeNode(node.type)) {
        for (const typeElem of node.type.types) {
          if (ts.isTypeLiteralNode(typeElem)) {
            violations.push(...inspectMembersForCostPollution(typeElem.members, contractName, sourceFile, filePath))
          }
        }
      }
    } else if (ts.isInterfaceDeclaration(node) && contractNames.has(node.name.text)) {
      const contractName = node.name.text
      violations.push(...inspectMembersForCostPollution(node.members, contractName, sourceFile, filePath))
    }

    ts.forEachChild(node, visit)
  }

  visit(sourceFile)
  return violations
}

/**
 * Diagnostic modules that must never import from canon/cost.ts or external pricing calculations.
 */
export const PURE_DIAGNOSTIC_MODULES = [
  'src/analysis/findings.ts',
  'src/analysis/signals.ts',
  'src/analysis/context.ts',
  'src/analysis/classify.ts',
  'src/canon/runs.ts',
  'src/canon/outcome.ts',
  'src/canon/harnesses.ts',
  'src/canon/findings.ts',
]

/**
 * Check if a diagnostic module imports cost or pricing engines.
 */
export function checkDiagnosticCostImports(
  sourceFile: ts.SourceFile,
  filePath: string,
  dashRoot: string
): DiagnosticCostImportViolation[] {
  const violations: DiagnosticCostImportViolation[] = []
  const relPath = path.relative(dashRoot, filePath).replace(/\\/g, '/')

  // Only check diagnostic modules
  const isDiagnosticModule = PURE_DIAGNOSTIC_MODULES.some(m => relPath.endsWith(m))
  if (!isDiagnosticModule) return violations

  const imports = extractImportsFromAst(sourceFile)
  for (const imp of imports) {
    const spec = imp.specifier
    if (
      spec.includes('canon/cost') ||
      spec.includes('src/models') ||
      spec.includes('src/pricing')
    ) {
      violations.push({
        file: filePath,
        specifier: spec,
        line: imp.line,
        character: imp.character,
        reason:
          `Decision D9 cost-secondary violation: pure diagnostic module '${relPath}' imports cost/pricing engine '${spec}'. ` +
          `Diagnostic analysis must operate strictly over telemetry, token usage, and outcome signals without depending on cost tables.`,
      })
    }
  }

  return violations
}

/**
 * Scan every diagnostic contract and module under dash/src/ for cost pollution.
 */
export function scanCostIsolation(dashRoot: string): {
  contractViolations: CostPollutionViolation[]
  importViolations: DiagnosticCostImportViolation[]
} {
  const contractViolations: CostPollutionViolation[] = []
  const importViolations: DiagnosticCostImportViolation[] = []

  const files = walkSourceFiles(path.resolve(dashRoot, 'src'))

  for (const file of files) {
    const content = fs.readFileSync(file, 'utf8')
    const sf = ts.createSourceFile(file, content, ts.ScriptTarget.Latest, true)

    const cViolations = checkSourceAstForCostPollution(sf, file)
    if (cViolations.length > 0) {
      contractViolations.push(...cViolations)
    }

    const iViolations = checkDiagnosticCostImports(sf, file, dashRoot)
    if (iViolations.length > 0) {
      importViolations.push(...iViolations)
    }
  }

  return { contractViolations, importViolations }
}
