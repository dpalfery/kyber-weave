// The split-read contract is declared once.
//
// WHY: the store's reader and the bridge's reader answer the same question —
// what does this session's payload say — and they must answer alike (the
// both-copies rule). What they read *with* was not shared: the SQL, the JSON
// parsers and the rule that decides where a session's turns come from were
// copied into both files. Two copies meant a fix to the null or empty handling
// had to be made twice, and a test that covered one copy said nothing about
// the other. The readers themselves stay two — they read through different
// handles, and only the contract they read with is shared.
//
// This scan is the pin. It names the declarations of the contract and asserts
// that the two reader files hold none of them: the contract lives in one
// module, and a reader that grew its own copy of a statement or a parser
// fails here rather than in a corpus.
//
// The scan is checked against the readers themselves first, so a green scan
// means "no duplicate declarations", never "the scanner found nothing".

import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

const TEST_DIR = path.dirname(fileURLToPath(import.meta.url))
const CANON_DIR = TEST_DIR
const SERVER_DIR = path.resolve(TEST_DIR, '..', 'server')

/** The module that owns the contract: the leaf both readers import. */
const SHARED_MODULE = path.join(CANON_DIR, 'session-read.ts')

/**
 * The names the contract owns.
 *
 * The three statements are the split read's own; the parsers are the text
 * SQLite's JSON functions hand back; `turnObjectOf` is the turn row's decode;
 * `sessionTurnsOf` is the shape decision — the payload's own turns, else the
 * split rows, else an empty list.
 */
const CONTRACT_NAMES = [
  'SESSION_PAYLOAD_FRAGMENTS_SELECT',
  'SESSION_TURNS_SELECT',
  'SESSION_PAYLOAD_SELECT',
  'parseJsonObject',
  'parseJsonArray',
  'jsonObjectOf',
  'turnObjectOf',
  'sessionTurnsOf',
] as const

/**
 * The store's own copy of the object parser.
 *
 * It was the same rule as the contract's `parseJsonObject` under another name,
 * so it goes with the rest of the copies.
 */
const READER_PARSER_COPY = 'parseSessionPayload'

/** The two files that read a session's payload, and must not declare the contract. */
const READERS: ReadonlyArray<{
  label: string
  file: string
  /** Copies of the contract's rules this reader held under another name. */
  extraCopies: readonly string[]
}> = [
  {
    label: 'dash/src/canon/store.ts',
    file: path.join(CANON_DIR, 'store.ts'),
    extraCopies: [READER_PARSER_COPY],
  },
  {
    label: 'dash/src/server/bridge.ts',
    file: path.join(SERVER_DIR, 'bridge.ts'),
    extraCopies: [],
  },
]

/**
 * The names a module declares at its top level.
 *
 * Read through the TypeScript parser rather than by matching text: a name in
 * a comment or in a doc block is not a declaration, and a scanner that read
 * them as one would report a duplicate where there is none.
 */
function declaredNames(file: string): Set<string> {
  const source = ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.ESNext, true)
  const names = new Set<string>()
  for (const statement of source.statements) {
    if (ts.isVariableStatement(statement)) {
      for (const declaration of statement.declarationList.declarations) {
        if (ts.isIdentifier(declaration.name)) names.add(declaration.name.text)
      }
      continue
    }
    if (ts.isFunctionDeclaration(statement) && statement.name !== undefined) {
      names.add(statement.name.text)
    }
  }
  return names
}

describe('the split-read contract is declared once', () => {
  it('holds none of the contract declarations in the readers', () => {
    // The reader half. The store's reader is the one the dashboard reads
    // through the store handle; the bridge's reader reads through its own.
    // Neither holds a copy of the contract: the contract is one definition
    // they both import.
    const offenders: string[] = []
    for (const reader of READERS) {
      const declared = declaredNames(reader.file)
      for (const name of [...CONTRACT_NAMES, ...reader.extraCopies]) {
        if (declared.has(name)) offenders.push(`${reader.label}: ${name}`)
      }
    }
    expect(offenders, 'the readers must not declare the split-read contract').toEqual([])
  })

  it('declares the whole contract in the shared module', () => {
    // The positive half: the module the readers import holds every name, so
    // the scan above is not satisfied by deleting the copies and nothing else.
    expect(existsSync(SHARED_MODULE), 'the shared split-read module must exist').toBe(true)
    const declared = declaredNames(SHARED_MODULE)
    const missing = CONTRACT_NAMES.filter((name) => !declared.has(name))
    expect(missing, 'the shared module must declare the whole contract').toEqual([])
  })
})
