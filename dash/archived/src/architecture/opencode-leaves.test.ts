// Architecture rule: the OpenCode plugin bundle and the canon adapter both depend on two
// small modules, so each must stay a LEAF. A value import from anywhere but `node:` would
// drag engine code into the single-file plugin bundle (and make canon depend on the plugin).
//
//   - src/canon/adapters/opencode-envelope.ts: the contract constants that mapper.ts (plugin
//     side) and the canon adapter (engine side) both read. `import type` is erased at build
//     time and carries no behaviour, so it is allowed; any value import is a finding.
//   - src/capture/harnesses/opencode-plugin/skills-block.ts: no imports of any kind except `node:`.
//   - src/capture/harnesses/opencode-plugin/contract.ts: the wire contract v3 constants and the id
//     derivation. The plugin, the receiver and the golden-body tests all read it, so it stays import-free.

import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

const SRC_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

const LEAVES = [
  'canon/adapters/opencode-envelope.ts',
  'capture/harnesses/opencode-plugin/skills-block.ts',
  'capture/harnesses/opencode-plugin/contract.ts',
]

/** Module specifiers of every non-`node:` import or re-export that survives compilation. */
export function nonNodeValueImports(source: string): string[] {
  const file = ts.createSourceFile('leaf.ts', source, ts.ScriptTarget.Latest, true)
  const found: string[] = []
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
            clause.namedBindings.elements.every((e) => e.isTypeOnly)))
      if (!typeOnly && !node.moduleSpecifier.text.startsWith('node:')) found.push(node.moduleSpecifier.text)
    } else if (
      ts.isExportDeclaration(node) &&
      node.moduleSpecifier !== undefined &&
      ts.isStringLiteral(node.moduleSpecifier) &&
      !node.isTypeOnly &&
      !node.moduleSpecifier.text.startsWith('node:')
    ) {
      found.push(node.moduleSpecifier.text)
    } else if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments[0] !== undefined &&
      ts.isStringLiteral(node.arguments[0]) &&
      !node.arguments[0].text.startsWith('node:')
    ) {
      found.push(node.arguments[0].text)
    }
    ts.forEachChild(node, visit)
  }
  visit(file)
  return found
}

describe('OpenCode leaf modules scanner', () => {
  it('flags value imports, re-exports and dynamic imports', () => {
    expect(nonNodeValueImports("import { a } from './x.js'\n")).toEqual(['./x.js'])
    expect(nonNodeValueImports("import './side.js'\n")).toEqual(['./side.js'])
    expect(nonNodeValueImports("export * from './y.js'\n")).toEqual(['./y.js'])
    expect(nonNodeValueImports("const m = await import('./z.js')\n")).toEqual(['./z.js'])
    expect(nonNodeValueImports("import { type A, b } from './mixed.js'\n")).toEqual(['./mixed.js'])
  })
  it('allows node: and type-only imports', () => {
    expect(nonNodeValueImports("import { join } from 'node:path'\n")).toEqual([])
    expect(nonNodeValueImports("import type { A } from './t.js'\n")).toEqual([])
    expect(nonNodeValueImports("import { type A } from './t.js'\n")).toEqual([])
  })
})

describe('OpenCode plugin dependencies stay leaves', () => {
  for (const rel of LEAVES) {
    it(`${rel} has no non-node: value imports`, () => {
      const offenders = nonNodeValueImports(readFileSync(path.join(SRC_ROOT, rel), 'utf8'))
      expect(offenders, `${rel} must stay an import-free leaf; found ${offenders.join(', ')}`).toEqual([])
    })
  }
})
