// Wire contract v1 for the Factory (Droid) capture plugin: the `factory.*` attribute namespace, the
// three span names, and the deterministic id derivation the hook mapper uses.
//
// The contract module is written by the next task, so every test here fails until contract.ts
// exists. The module is loaded inside each test rather than imported at the top of the file, so
// the RED proof is per test: each one fails on the missing module and names the guarantee it was
// pinning, instead of the whole file failing to collect as one error.

import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const SESSION = 'ses_factory_01'
const ANCHOR = 'msg_factory_01'

/** The three span kinds of the Factory contract, as the contract names them. */
const KINDS = ['session', 'turn', 'tool'] as const

/**
 * The attribute keys of the Factory contract, in the contract's own spelling. The plugin claims
 * spans under `factory.*` and never under the OpenCode `opencode.*` keys: the OpenCode adapter
 * claims any span carrying those keys, so an OpenCode key here would mis-attribute every Factory
 * row to OpenCode.
 */
const EXPECTED_KEYS = {
  contractVersion: 'factory.contract_version',
  sessionId: 'factory.session.id',
  cwd: 'factory.cwd',
  messageId: 'factory.message.id',
  turnOperation: 'factory.turn.operation',
  promptText: 'factory.turn.prompt.text',
  modelId: 'factory.model.id',
  toolName: 'factory.tool.name',
  toolCallId: 'factory.tool.call_id',
  toolStatus: 'factory.tool.status',
  toolErrorType: 'factory.tool.error_type',
  toolInputBytes: 'factory.tool.input_bytes',
  toolResultBytes: 'factory.tool.result_bytes',
}

/**
 * The three span names of the Factory contract, keyed by the span kind they belong to. The key and
 * the kind are the same word, so `SPAN_NAMES[kind]` is the name of that kind.
 */
const EXPECTED_SPAN_NAMES = {
  session: 'factory.session',
  turn: 'factory.turn',
  tool: 'factory.tool',
}

async function contract() {
  return import('./contract.js')
}

/**
 * The hook-to-span mapper. The contract owns the id derivation, but the mapper is the only place
 * that names the two roles of a turn, so the contract's guarantee about the two spans of one turn
 * is observed through it.
 */
async function mapper() {
  return import('./mapper.js')
}

/** Every module specifier the file imports or re-exports, including type-only and dynamic imports. */
function importSpecifiers(source: string): string[] {
  const file = ts.createSourceFile('contract.ts', source, ts.ScriptTarget.Latest, true)
  const found: string[] = []
  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      found.push(node.moduleSpecifier.text)
    } else if (
      ts.isExportDeclaration(node) &&
      node.moduleSpecifier !== undefined &&
      ts.isStringLiteral(node.moduleSpecifier)
    ) {
      found.push(node.moduleSpecifier.text)
    } else if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments[0] !== undefined &&
      ts.isStringLiteral(node.arguments[0])
    ) {
      found.push(node.arguments[0].text)
    }
    ts.forEachChild(node, visit)
  }
  visit(file)
  return found
}

describe('the Factory wire contract v1', () => {
  it('the wire contract version is the string "1"', async () => {
    const { FACTORY_CONTRACT_VERSION } = await contract()
    expect(FACTORY_CONTRACT_VERSION).toBe('1')
  })

  it('the attribute keys are the contract strings', async () => {
    const { FACTORY_KEYS } = await contract()
    const keys = FACTORY_KEYS as unknown as Record<string, unknown>
    for (const [name, value] of Object.entries(EXPECTED_KEYS)) {
      expect(keys[name], `the ${name} attribute key`).toBe(value)
    }
    // The contract declares exactly these keys: a key nothing writes is a claim the wire does
    // not keep, and a key the contract dropped but the map still carries is a claim the plugin
    // still makes.
    expect(Object.keys(keys).sort(), 'the attribute keys the contract declares').toEqual(
      Object.keys(EXPECTED_KEYS).sort(),
    )
  })

  it('the span names are factory.session, factory.turn and factory.tool', async () => {
    const { SPAN_NAMES } = await contract()
    const names = SPAN_NAMES as unknown as Record<string, unknown>
    for (const [kind, value] of Object.entries(EXPECTED_SPAN_NAMES)) {
      expect(names[kind], `the ${kind} span name`).toBe(value)
    }
    // The contract declares exactly these names: a name the plugin never emits is a kind that
    // does not exist, and a name the plugin emits but the contract no longer declares is a span
    // the wire has no name for.
    expect(Object.keys(names).sort(), 'the span kinds the contract declares').toEqual(
      Object.keys(EXPECTED_SPAN_NAMES).sort(),
    )
    expect(Object.values(names).sort(), 'the span names the contract declares').toEqual(
      Object.values(EXPECTED_SPAN_NAMES).sort(),
    )
  })

  it('the contract declares exactly the span names the plugin emits', async () => {
    // The contract is the plugin's wire format, so the names it declares must be the names the
    // plugin writes and no others. Every documented hook event is mapped here, so a span the
    // plugin writes under a name the contract does not declare fails this case, and so does a
    // declared name the plugin never writes.
    const { SPAN_NAMES } = await contract()
    const { FACTORY_HOOK_EVENTS, mapHookEvent } = await mapper()
    const emitted = new Set<string>()
    for (const event of FACTORY_HOOK_EVENTS) {
      for (const span of mapHookEvent({
        hook_event_name: event,
        session_id: SESSION,
        message_id: ANCHOR,
        tool_call_id: ANCHOR,
      })) {
        emitted.add(span.name)
      }
    }
    const declared = Object.values(SPAN_NAMES as unknown as Record<string, unknown>).map(String)
    expect([...emitted].sort(), 'the span names the plugin emits').toEqual([...declared].sort())
  })

  it('no attribute key and no span name claims the OpenCode namespace', async () => {
    const { FACTORY_KEYS, SPAN_NAMES } = await contract()
    const keys = Object.values(FACTORY_KEYS as unknown as Record<string, unknown>)
    const names = Object.values(SPAN_NAMES as unknown as Record<string, unknown>)
    for (const key of keys) {
      expect(typeof key, 'an attribute key').toBe('string')
      expect(String(key).startsWith('opencode.'), `the ${String(key)} key`).toBe(false)
      expect(String(key).startsWith('factory.'), `the ${String(key)} key`).toBe(true)
    }
    for (const name of names) {
      expect(String(name).startsWith('opencode.'), `the ${String(name)} span name`).toBe(false)
      expect(String(name).startsWith('factory.'), `the ${String(name)} span name`).toBe(true)
    }
  })

  it('a trace id is 32 lowercase hex characters', async () => {
    const { traceIdFor } = await contract()
    expect(traceIdFor(SESSION, ANCHOR)).toMatch(/^[0-9a-f]{32}$/)
  })

  it('a span id is 16 lowercase hex characters for every kind', async () => {
    const { spanIdFor } = await contract()
    for (const kind of KINDS) {
      expect(spanIdFor(kind, SESSION, ANCHOR), `kind ${kind}`).toMatch(/^[0-9a-f]{16}$/)
    }
  })

  it('equal input derives equal ids', async () => {
    const { spanIdFor, traceIdFor } = await contract()
    expect(traceIdFor(SESSION, ANCHOR)).toBe(traceIdFor(SESSION, ANCHOR))
    expect(spanIdFor('tool', SESSION, ANCHOR)).toBe(spanIdFor('tool', SESSION, ANCHOR))
  })

  it('a span id differs by kind for the same anchor', async () => {
    const { spanIdFor } = await contract()
    const ids = KINDS.map((kind) => spanIdFor(kind, SESSION, ANCHOR))
    expect(new Set(ids).size).toBe(KINDS.length)
  })

  it('a span id differs by anchor for the same kind', async () => {
    const { spanIdFor } = await contract()
    const ids = ['anchor-a', 'anchor-b', 'anchor-c'].map((anchor) => spanIdFor('tool', SESSION, anchor))
    expect(new Set(ids).size).toBe(3)
  })

  it('a trace id differs by anchor for the same session', async () => {
    // The trace id is derived from the session id AND the anchor id, so one unit of work (a turn,
    // a tool call) carries its own trace and the units of one session do not share one.
    const { traceIdFor } = await contract()
    const ids = ['anchor-a', 'anchor-b', 'anchor-c'].map((anchor) => traceIdFor(SESSION, anchor))
    expect(new Set(ids).size).toBe(3)
  })

  it('a trace id differs by session for the same anchor', async () => {
    const { traceIdFor } = await contract()
    expect(traceIdFor('ses_factory_one', ANCHOR)).not.toBe(traceIdFor('ses_factory_two', ANCHOR))
  })

  it('ids stay distinct when a session id and an anchor contain the field separator', async () => {
    // Joined with a bare "|", both of these were the string "factory|tool|s|tool|s2|a".
    const { spanIdFor } = await contract()
    expect(spanIdFor('tool', 's|tool|s2', 'a')).not.toBe(spanIdFor('tool', 's', 'tool|s2|a'))
    // An escape character in a part must not recreate the collision.
    expect(spanIdFor('tool', 's\\|', 'a')).not.toBe(spanIdFor('tool', 's\\', '|a'))
  })

  it('contract.ts imports no package: it stays a leaf of the plugin', async () => {
    await contract()
    const source = readFileSync(path.join(HERE, 'contract.ts'), 'utf8')
    const offenders = importSpecifiers(source).filter(
      (specifier) => !specifier.startsWith('node:') && !specifier.startsWith('.'),
    )
    expect(offenders, `contract.ts must not import a package; found ${offenders.join(', ')}`).toEqual([])
  })

  it('the two spans of one turn carry different span ids and one trace id', async () => {
    // The contract's id derivation gives the two spans of one turn two span ids and one trace id.
    // What the two roles must never share is the span id: a shared one lets the completion
    // replace the prompt. What they must always share is the trace id: a turn's prompt and its
    // completion are one unit of work.
    const { mapHookEvent } = await mapper()
    const prompt = mapHookEvent({
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: ANCHOR,
    })
    const completion = mapHookEvent({
      hook_event_name: 'Stop',
      session_id: SESSION,
      message_id: ANCHOR,
    })
    expect(prompt, 'the spans of the prompt').toHaveLength(1)
    expect(completion, 'the spans of the completion').toHaveLength(1)
    const promptSpan = prompt[0]
    const completionSpan = completion[0]
    expect(promptSpan.spanId).toMatch(/^[0-9a-f]{16}$/)
    expect(completionSpan.spanId).toMatch(/^[0-9a-f]{16}$/)
    expect(completionSpan.spanId, 'the completion span id').not.toBe(promptSpan.spanId)
    expect(promptSpan.traceId).toMatch(/^[0-9a-f]{32}$/)
    expect(completionSpan.traceId, 'the trace id of the turn').toBe(promptSpan.traceId)
  })
})
