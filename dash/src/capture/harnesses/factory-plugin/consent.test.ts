// T5 (Factory / Droid capture plugin): the consent reader that decides whether prompt and
// tool content is ever recorded.
//
// The consent file lives in KyberDash's own state directory — `~/.kyberdash/factory-plugin.json`
// — never in the owner's Factory tree — and holds `{ "version": 1, "content": <boolean> }`.
// Content is OFF unless that exact file says otherwise. Missing, unreadable, malformed,
// wrong shape, unknown version and oversized files all read OFF, and the reader never
// throws: it always answers a decision.
//
// This mirrors the OpenCode consent reader, the established precedent in this repository
// (dash/src/capture/harnesses/opencode-plugin/consent.ts): the same exported names with
// FACTORY substituted for OPENCODE, the same `<home>/.kyberdash/<name>.json` path shape, the
// same `KYBERDASH_*_CONTEXT` override where `1` forces content on and `0` forces it off, and
// the same 4096-byte size bound.
//
// The module under test must export:
//   FACTORY_CONSENT_FILE_NAME        -> 'factory-plugin.json'
//   KYBERDASH_FACTORY_CONTEXT_ENV    -> 'KYBERDASH_FACTORY_CONTEXT'
//   resolveConsentPath(home?)        -> '<home>/.kyberdash/factory-plugin.json'
//   readContentConsent(path)         -> boolean
//   resolveContentConsent(env, path) -> boolean
//
// It must stay a LEAF — node: built-ins only, no project module and no third-party package —
// because it is bundled into the plugin file. That rule is asserted at the bottom of this
// file, in the shape dash/src/architecture/opencode-leaves.test.ts established.
//
// The module does not exist yet: this file is the RED proof. It is loaded through a dynamic
// import with a computed specifier so Vitest still COLLECTS the file and every test fails
// with the one reason that matters — the module under test is missing — instead of the whole
// file failing to collect.
//
// Every fixture lives in a temporary directory that the suite removes afterwards, so no test
// ever reads or writes the real `~/.kyberdash`.

import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

import ts from 'typescript'
import { afterEach, describe, expect, it } from 'vitest'

/** The module under test. It does not exist yet; every test fails on this import. */
type ConsentModule = typeof import('./consent.js')

const CONSENT_MODULE_SPECIFIER = './consent.js'

async function loadConsent(): Promise<ConsentModule> {
  return (await import(/* @vite-ignore */ CONSENT_MODULE_SPECIFIER)) as ConsentModule
}

/** The documented size bound: the same bound the OpenCode consent reader uses. */
const MAX_CONSENT_BYTES = 4096

/** The one file body that turns content on. */
const ON_BODY = '{"version":1,"content":true}'

const dirs: string[] = []
const temp = (): string => {
  const dir = mkdtempSync(join(tmpdir(), 'kd-factory-consent-'))
  dirs.push(dir)
  return dir
}
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

/** A consent file under a throwaway home. */
async function consentFixture(body: string): Promise<string> {
  const consent = await loadConsent()
  const home = temp()
  mkdirSync(join(home, '.kyberdash'), { recursive: true })
  const path = consent.resolveConsentPath(home)
  writeFileSync(path, body)
  return path
}

/** A consent path under a throwaway home where no file was ever written. */
async function missingConsentPath(): Promise<string> {
  const consent = await loadConsent()
  return consent.resolveConsentPath(temp())
}

describe('the Factory consent file', () => {
  it('is named factory-plugin.json inside KyberDash\'s own state directory', async () => {
    const consent = await loadConsent()
    expect(consent.FACTORY_CONSENT_FILE_NAME).toBe('factory-plugin.json')
    expect(consent.resolveConsentPath('/h/u')).toBe(join('/h/u', '.kyberdash', 'factory-plugin.json'))
  })

  it('never resolves inside the owner\'s Factory tree', async () => {
    const consent = await loadConsent()
    const path = consent.resolveConsentPath('/h/u')
    expect(path).toContain(join('.kyberdash', 'factory-plugin.json'))
    expect(path).not.toContain('.factory')
  })

  it('defaults to the current home directory', async () => {
    const consent = await loadConsent()
    expect(consent.resolveConsentPath()).toBe(join(homedir(), '.kyberdash', 'factory-plugin.json'))
  })
})

describe('readContentConsent', () => {
  it('is on for exactly { "version": 1, "content": true }', async () => {
    const consent = await loadConsent()
    expect(consent.readContentConsent(await consentFixture(ON_BODY))).toBe(true)
  })

  it('is on for the writer\'s pretty-printed body too', async () => {
    const consent = await loadConsent()
    const pretty = JSON.stringify({ version: 1, content: true }, null, 2)
    expect(consent.readContentConsent(await consentFixture(pretty))).toBe(true)
  })

  it('is off for version 1 with content false', async () => {
    const consent = await loadConsent()
    expect(consent.readContentConsent(await consentFixture('{"version":1,"content":false}'))).toBe(false)
  })

  it.each([
    ['content is missing', '{"version":1}'],
    ['content is a string', '{"version":1,"content":"true"}'],
    ['content is a number', '{"version":1,"content":1}'],
    ['content is null', '{"version":1,"content":null}'],
    ['version is missing', '{"content":true}'],
    ['version is a string', '{"version":"1","content":true}'],
    ['version is 0', '{"version":0,"content":true}'],
    ['version is 2', '{"version":2,"content":true}'],
    ['version is 99', '{"version":99,"content":true}'],
    ['the body is not JSON', 'nope'],
    ['the body is truncated JSON', '{"version":1,'],
    ['the body is empty', ''],
    ['the body is a JSON null', 'null'],
    ['the body is a JSON array', '[true]'],
    ['the body is a JSON string', '"true"'],
    ['the body is a JSON number', '1'],
  ])('is off when %s', async (_label, body) => {
    const consent = await loadConsent()
    expect(consent.readContentConsent(await consentFixture(body))).toBe(false)
  })

  it('is off when the file does not exist', async () => {
    const consent = await loadConsent()
    expect(consent.readContentConsent(await missingConsentPath())).toBe(false)
  })

  it('is off when a parent path segment is a file, so the consent file cannot be read', async () => {
    const consent = await loadConsent()
    const home = temp()
    const blocker = join(home, 'blocker')
    writeFileSync(blocker, 'not a directory')
    expect(consent.readContentConsent(join(blocker, '.kyberdash', 'factory-plugin.json'))).toBe(false)
  })

  it.skipIf(process.platform === 'win32' || process.getuid?.() === 0)(
    'is off when the file exists but cannot be read',
    async () => {
      const consent = await loadConsent()
      const path = await consentFixture(ON_BODY)
      chmodSync(path, 0o000)
      expect(consent.readContentConsent(path)).toBe(false)
    },
  )

  it('is on for a file exactly at the 4096-byte bound', async () => {
    const consent = await loadConsent()
    const body = ON_BODY + ' '.repeat(MAX_CONSENT_BYTES - ON_BODY.length)
    expect(body.length).toBe(MAX_CONSENT_BYTES)
    expect(consent.readContentConsent(await consentFixture(body))).toBe(true)
  })

  it('is off for a file past the 4096-byte bound, so an oversized file is never consent', async () => {
    const consent = await loadConsent()
    const body = ON_BODY + ' '.repeat(MAX_CONSENT_BYTES - ON_BODY.length + 1)
    expect(body.length).toBe(MAX_CONSENT_BYTES + 1)
    expect(consent.readContentConsent(await consentFixture(body))).toBe(false)
  })

  it('answers a decision instead of throwing for every hostile input', async () => {
    const consent = await loadConsent()
    const home = temp()
    const blocker = join(home, 'blocker')
    writeFileSync(blocker, 'not a directory')
    const hostile = [
      await missingConsentPath(),
      join(blocker, '.kyberdash', 'factory-plugin.json'),
      await consentFixture('{'),
      await consentFixture('null'),
      await consentFixture(''),
    ]
    for (const path of hostile) {
      expect(consent.readContentConsent(path)).toBe(false)
    }
  })
})

describe('resolveContentConsent', () => {
  it('names the environment override KYBERDASH_FACTORY_CONTEXT', async () => {
    const consent = await loadConsent()
    expect(consent.KYBERDASH_FACTORY_CONTEXT_ENV).toBe('KYBERDASH_FACTORY_CONTEXT')
  })

  it('turns content on regardless of what the file says', async () => {
    const consent = await loadConsent()
    const off = await consentFixture('{"version":1,"content":false}')
    const missing = await missingConsentPath()
    expect(consent.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: '1' }, off)).toBe(true)
    expect(consent.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: '1' }, missing)).toBe(true)
  })

  it('turns content off regardless of what the file says', async () => {
    const consent = await loadConsent()
    const on = await consentFixture(ON_BODY)
    const missing = await missingConsentPath()
    expect(consent.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: '0' }, on)).toBe(false)
    expect(consent.resolveContentConsent({ KYBERDASH_FACTORY_CONTEXT: '0' }, missing)).toBe(false)
  })

  it('defers to the file when the override is absent', async () => {
    const consent = await loadConsent()
    const on = await consentFixture(ON_BODY)
    const off = await consentFixture('{"version":1,"content":false}')
    expect(consent.resolveContentConsent({}, on)).toBe(true)
    expect(consent.resolveContentConsent({}, off)).toBe(false)
  })
})

const CONSENT_SOURCE_PATH = fileURLToPath(new URL('./consent.ts', import.meta.url))

/**
 * Module specifiers of every non-`node:` import or re-export that survives compilation.
 * The shape dash/src/architecture/opencode-leaves.test.ts established.
 */
function nonNodeValueImports(source: string): string[] {
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

describe('the Factory consent reader is a leaf', () => {
  it('flags a value import from another module', async () => {
    // The scanner only earns its keep on the module under test, so it waits for that module.
    await loadConsent()
    expect(nonNodeValueImports("import { a } from './x.js'\n")).toEqual(['./x.js'])
    expect(nonNodeValueImports("import './side.js'\n")).toEqual(['./side.js'])
    expect(nonNodeValueImports("export * from './y.js'\n")).toEqual(['./y.js'])
    expect(nonNodeValueImports("const m = await import('./z.js')\n")).toEqual(['./z.js'])
  })

  it('allows node: and type-only imports', async () => {
    await loadConsent()
    expect(nonNodeValueImports("import { join } from 'node:path'\n")).toEqual([])
    expect(nonNodeValueImports("import type { A } from './t.js'\n")).toEqual([])
    expect(nonNodeValueImports("import { type A } from './t.js'\n")).toEqual([])
  })

  it('imports node: built-ins only, so it can be bundled into the plugin file', () => {
    const source = readFileSync(CONSENT_SOURCE_PATH, 'utf8')
    const offenders = nonNodeValueImports(source)
    expect(offenders, `consent.ts must stay a leaf; found ${offenders.join(', ')}`).toEqual([])
  })
})
