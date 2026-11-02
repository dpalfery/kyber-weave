// Architecture rule: the Factory capture plugin's own modules are LEAVES of the engine.
//
// The Factory plugin is a one-shot hook process that runs inside somebody else's tool, and its
// transport and consent writer are shared with the OpenCode plugin. Neither may reach into the
// engine: a leaf that imports engine code drags the whole engine into a plugin that has to run
// as a small, bounded, one-shot process, and a shared module that depends on the plugin that
// depends on it is a cycle waiting to happen.
//
// The rule, at source level:
//
//   - a leaf may import `node:` built-ins, and the capture family it belongs to (a relative
//     import that stays under `src/capture/`);
//   - a leaf may import TYPE ONLY from anywhere: `import type` is erased at build time and
//     carries no behaviour;
//   - a leaf may import NO third-party package: a bare specifier that is not a `node:` built-in
//     drags a dependency into the plugin;
//   - a leaf may import NOTHING from the OpenCode capture plugin: the OpenCode plugin's transport
//     modules are re-export shims of the shared ones, so the dependency runs the other way.
//
// The scanner is pinned by its own cases below, so it cannot pass by finding nothing.

import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

const SRC_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
/** The capture family: the only tree a leaf may reach with a value import. */
const CAPTURE_ROOT = path.join(SRC_ROOT, 'capture')

/** The modules the rule covers. */
const LEAVES = [
  'capture/harnesses/transport/otlp-sender.ts',
  'capture/harnesses/transport/outbox.ts',
  'capture/consent-file.ts',
  'capture/harnesses/factory-plugin/entry.ts',
  'capture/harnesses/factory-plugin/spool.ts',
  'capture/harnesses/factory-plugin/contract.ts',
  'capture/harnesses/factory-plugin/mapper.ts',
  'capture/harnesses/factory-plugin/consent.ts',
]

export type LeafReason = 'third-party' | 'outside-capture' | 'opencode-plugin'

export type LeafFinding = { specifier: string; reason: LeafReason }

function isInside(root: string, candidate: string): boolean {
  return candidate === root || candidate.startsWith(root + path.sep)
}

/**
 * Every import a leaf module may not have: a third-party package, a value import from outside
 * the capture family, or any import of the OpenCode capture plugin.
 */
export function leafImportFindings(
  source: string,
  moduleDir: string,
  captureRoot: string = CAPTURE_ROOT,
): LeafFinding[] {
  const file = ts.createSourceFile('leaf.ts', source, ts.ScriptTarget.Latest, true)
  const found: LeafFinding[] = []
  const classify = (specifier: string): void => {
    if (specifier.startsWith('node:')) return
    if (specifier.startsWith('.')) {
      const resolved = path.resolve(moduleDir, specifier)
      if (isInside(captureRoot, resolved)) {
        if (isInside(path.join(captureRoot, 'harnesses', 'opencode-plugin'), resolved)) {
          found.push({ specifier, reason: 'opencode-plugin' })
        }
        return
      }
      found.push({ specifier, reason: 'outside-capture' })
      return
    }
    found.push({ specifier, reason: 'third-party' })
  }
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
      if (!typeOnly) classify(node.moduleSpecifier.text)
    } else if (
      ts.isExportDeclaration(node) &&
      node.moduleSpecifier !== undefined &&
      ts.isStringLiteral(node.moduleSpecifier) &&
      !node.isTypeOnly
    ) {
      classify(node.moduleSpecifier.text)
    } else if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments[0] !== undefined &&
      ts.isStringLiteral(node.arguments[0])
    ) {
      classify(node.arguments[0].text)
    }
    ts.forEachChild(node, visit)
  }
  visit(file)
  return found
}

describe('the Factory leaf scanner', () => {
  const MODULE_DIR = path.join(CAPTURE_ROOT, 'harnesses', 'factory-plugin')

  it('flags a third-party package', () => {
    expect(leafImportFindings("import { z } from 'zod'\n", MODULE_DIR)).toEqual([
      { specifier: 'zod', reason: 'third-party' },
    ])
    expect(leafImportFindings("import { join } from 'path'\n", MODULE_DIR)).toEqual([
      { specifier: 'path', reason: 'third-party' },
    ])
    expect(leafImportFindings("export * from 'zod'\n", MODULE_DIR)).toEqual([
      { specifier: 'zod', reason: 'third-party' },
    ])
    expect(leafImportFindings("const m = await import('zod')\n", MODULE_DIR)).toEqual([
      { specifier: 'zod', reason: 'third-party' },
    ])
  })

  it('flags a value import from outside the capture family', () => {
    expect(
      leafImportFindings("import { x } from '../../../canon/adapters/opencode-envelope.js'\n", MODULE_DIR),
    ).toEqual([{ specifier: '../../../canon/adapters/opencode-envelope.js', reason: 'outside-capture' }])
    expect(leafImportFindings("import { r } from '../../../otel/receiver.js'\n", MODULE_DIR)).toEqual([
      { specifier: '../../../otel/receiver.js', reason: 'outside-capture' },
    ])
  })

  it('flags an import of the OpenCode capture plugin', () => {
    expect(
      leafImportFindings("import { createOutbox } from '../opencode-plugin/transport/outbox.js'\n", MODULE_DIR),
    ).toEqual([{ specifier: '../opencode-plugin/transport/outbox.js', reason: 'opencode-plugin' }])
  })

  it('allows node: built-ins, the capture family, and type-only imports', () => {
    expect(leafImportFindings("import { readFileSync } from 'node:fs'\n", MODULE_DIR)).toEqual([])
    expect(leafImportFindings("import { mapHookEvent } from './mapper.js'\n", MODULE_DIR)).toEqual([])
    expect(leafImportFindings("import { join } from 'node:path'\n", MODULE_DIR)).toEqual([])
    expect(leafImportFindings("const m = await import('./spool.js')\n", MODULE_DIR)).toEqual([])
    expect(leafImportFindings("import type { A } from '../../../canon/x.js'\n", MODULE_DIR)).toEqual([])
    expect(leafImportFindings("import { type A } from '../../../canon/x.js'\n", MODULE_DIR)).toEqual([])
    expect(leafImportFindings("export type { A } from '../../../canon/x.js'\n", MODULE_DIR)).toEqual([])
  })

  it('does not mistake a value import for a type-only one', () => {
    expect(leafImportFindings("import { type A, b } from '../../../canon/x.js'\n", MODULE_DIR)).toEqual([
      { specifier: '../../../canon/x.js', reason: 'outside-capture' },
    ])
  })
})

describe('the Factory capture modules stay leaves', () => {
  for (const rel of LEAVES) {
    it(`${rel} imports no third-party package and nothing outside the capture family`, () => {
      const file = path.join(SRC_ROOT, rel)
      expect(existsSync(file), `${rel} must exist`).toBe(true)
      const findings = leafImportFindings(
        readFileSync(file, 'utf8'),
        path.dirname(file),
        CAPTURE_ROOT,
      )
      expect(
        findings.map((finding) => `${finding.specifier} (${finding.reason})`),
        `${rel} must stay a leaf of the engine`,
      ).toEqual([])
    })
  }
})
