// Synthetic fixtures only: tool names, descriptions, schemas, paths and transcript
// content below are invented. They exercise the on-disk shape
// (`<root>/mcp/<server>/<tool>.json` beside `<root>/conversations/<cascade>.db`, and the
// sibling `<root>/brain/<cascade>/.system_generated/logs/transcript.jsonl`), not any real
// user's content. The segment fixtures live in tests/fixtures/antigravity-transcript.
import { createRequire } from 'node:module'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, truncateSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, describe, expect, it } from 'vitest'

import { contentFromParts } from '../../canon/types.js'
import type { CanonicalRecord } from '../../canon/types.js'
import { measurabilityFor } from '../../canon/measurability.js'
import { MAX_SESSION_FILE_BYTES } from '../../ingest/fs-utils.js'
import { createAntigravityProvider } from '../../providers/antigravity.js'
import { PROVIDER_READERS, ingestProviders } from '../provider.js'
import {
  antigravityReader,
  antigravityRootFromConversationPath,
  readAntigravityMcpCatalogue,
} from './antigravity.js'
import type { ParsedProviderCall } from '../../providers/types.js'
import type { ReaderTurn } from './types.js'

const requireForTest = createRequire(import.meta.url)
type SqliteDatabase = {
  exec(sql: string): void
  prepare(sql: string): { run(...params: unknown[]): void }
  close(): void
}
const { DatabaseSync } = requireForTest('node:sqlite') as {
  DatabaseSync: new (path: string) => SqliteDatabase
}

const roots: string[] = []

afterAll(() => {
  for (const root of roots) rmSync(root, { recursive: true, force: true })
})

function makeRoot(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-agy-reader-'))
  roots.push(root)
  mkdirSync(join(root, 'conversations'), { recursive: true })
  writeFileSync(join(root, 'conversations', 'cascade-1.db'), 'not-inspected')
  return root
}

function writeTool(root: string, server: string, file: string, body: unknown): void {
  mkdirSync(join(root, 'mcp', server), { recursive: true })
  writeFileSync(join(root, 'mcp', server, file), typeof body === 'string' ? body : JSON.stringify(body))
}

async function collect(path: string): Promise<ReaderTurn[]> {
  const turns: ReaderTurn[] = []
  for await (const turn of antigravityReader.read(path)) turns.push(turn)
  return turns
}

// ---------------------------------------------------------------------------
// Segment fixtures: gen_metadata rows with last_step_index 2, 5 and 8, beside a
// transcript whose PLANNER_RESPONSE steps sit at 3, 6 and 9.
// ---------------------------------------------------------------------------

const SEGMENTS_CONVERSATION = 'fixture-segments'
const SEGMENTS_FIXTURE = JSON.parse(readFileSync(
  new URL('../../../tests/fixtures/antigravity-transcript/gen-metadata.json', import.meta.url),
  'utf-8',
)) as { conversationId: string; rows: Array<{ idx: number; hex: string }> }
const SEGMENTS_TRANSCRIPT = readFileSync(
  new URL('../../../tests/fixtures/antigravity-transcript/brain/fixture-segments/.system_generated/logs/transcript.jsonl', import.meta.url),
  'utf-8',
)

/** Every args value is JSON-encoded twice; the reader must decode both layers. */
const enc2 = (v: unknown) => JSON.stringify(JSON.stringify(v))

function planner(stepIndex: number, calls: Array<{ name: string; args: Record<string, unknown> }>): string {
  return JSON.stringify({
    step_index: stepIndex,
    source: 'MODEL',
    type: 'PLANNER_RESPONSE',
    status: 'DONE',
    created_at: '2026-06-21T13:32:00Z',
    tool_calls: calls.map((c) => ({
      name: c.name,
      args: Object.fromEntries(Object.entries(c.args).map(([k, v]) => [k, enc2(v)])),
    })),
  })
}

function generic(stepIndex: number, content: string): string {
  return JSON.stringify({ step_index: stepIndex, source: 'SYSTEM', type: 'GENERIC', status: 'DONE', created_at: '2026-06-21T13:32:00Z', content })
}

/** The synthetic fixture transcript, optionally cut to the steps from `fromStep` on. */
function fixtureTranscript(fromStep = 0): string {
  return SEGMENTS_TRANSCRIPT.split(/\r?\n/)
    .filter((line) => line.trim() !== '')
    .filter((line) => (JSON.parse(line) as { step_index: number }).step_index >= fromStep)
    .join('\n') + '\n'
}

/** Builds <root>/conversations/<cascade>.db from the fixture rows, plus optional transcript and MCP tool. */
function makeSegmentsRoot(options: { transcript?: string; mcpTool?: boolean }): { root: string; dbPath: string } {
  const root = mkdtempSync(join(tmpdir(), 'kyber-agy-segments-'))
  roots.push(root)
  mkdirSync(join(root, 'conversations'), { recursive: true })
  const dbPath = join(root, 'conversations', `${SEGMENTS_CONVERSATION}.db`)
  const db = new DatabaseSync(dbPath)
  try {
    db.exec('CREATE TABLE gen_metadata (idx integer, data blob, size integer NOT NULL DEFAULT 0, PRIMARY KEY (idx))')
    for (const row of SEGMENTS_FIXTURE.rows) {
      const data = Buffer.from(row.hex, 'hex')
      db.prepare('INSERT INTO gen_metadata (idx, data, size) VALUES (?, ?, ?)').run(row.idx, data, data.length)
    }
  } finally {
    db.close()
  }
  if (options.transcript !== undefined) {
    const logs = join(root, 'brain', SEGMENTS_CONVERSATION, '.system_generated', 'logs')
    mkdirSync(logs, { recursive: true })
    writeFileSync(join(logs, 'transcript.jsonl'), options.transcript)
  }
  if (options.mcpTool === true) {
    writeTool(root, 'srv-a', 'alpha_lookup.json', { name: 'alpha_lookup', description: 'Looks up alpha.' })
  }
  return { root, dbPath }
}

function turnFor(turns: readonly ReaderTurn[], nativeRecordId: string): ReaderTurn | undefined {
  return turns.find((turn) => turn.nativeRecordId === nativeRecordId)
}

/** The turn for one agy step; a missing turn fails with the step named, not a TypeError. */
function requireTurn(turns: readonly ReaderTurn[], nativeRecordId: string): ReaderTurn {
  const turn = turnFor(turns, nativeRecordId)
  expect(turn, `no reader turn with nativeRecordId ${nativeRecordId}`).toBeDefined()
  return turn!
}

/** Parser calls for a conversation database, through the real provider (and its pairing). */
async function providerCalls(dbPath: string): Promise<ParsedProviderCall[]> {
  const cacheDir = mkdtempSync(join(tmpdir(), 'kyber-agy-cache-'))
  roots.push(cacheDir)
  const previous = process.env['KYBERDASH_CACHE_DIR']
  process.env['KYBERDASH_CACHE_DIR'] = cacheDir
  try {
    const parser = createAntigravityProvider().createSessionParser(
      { path: dbPath, project: 'antigravity-cli', provider: 'antigravity' },
      new Set(),
    )
    const calls: ParsedProviderCall[] = []
    for await (const call of parser.parse()) calls.push(call)
    return calls
  } finally {
    if (previous === undefined) delete process.env['KYBERDASH_CACHE_DIR']
    else process.env['KYBERDASH_CACHE_DIR'] = previous
  }
}

async function ingestCalls(calls: ParsedProviderCall[], dbPath: string) {
  return ingestProviders(['antigravity-cli'], () => ({
    calls,
    filePath: dbPath,
    harnessId: 'antigravity-cli',
    sourceKey: `antigravity-cli:${SEGMENTS_CONVERSATION}`,
  }))
}

/** The parent call record, identified by its created_at (each fixture call has its own). */
function parentAt(records: readonly CanonicalRecord[], iso: string): CanonicalRecord | undefined {
  return records.find((r) => r.op !== 'tool.invoke' && new Date(r.timestamp).toISOString() === iso)
}

function toolRecordsOf(records: readonly CanonicalRecord[], parent: CanonicalRecord): CanonicalRecord[] {
  return records.filter((r) => r.op === 'tool.invoke' && r.parentSpanId === parent.spanId)
}

const CALL_2_AT = '2026-06-21T13:32:20.000Z'
const CALL_5_AT = '2026-06-21T13:33:00.000Z'
const CALL_8_AT = '2026-06-21T13:34:00.000Z'

describe('antigravityReader', () => {
  it('emits one tool_definitions part per MCP server with the schema files verbatim', async () => {
    const root = makeRoot()
    const alpha = { name: 'alpha_lookup', description: 'Looks up alpha.', parameters: { properties: { q: { type: 'string' } } } }
    writeTool(root, 'srv-b', 'beta_run.json', { name: 'beta_run', description: 'Runs beta.' })
    writeTool(root, 'srv-a', 'alpha_lookup.json', alpha)
    writeTool(root, 'srv-a', 'instructions.md', '# not json')

    const turns = await collect(join(root, 'conversations', 'cascade-1.db'))

    expect(turns).toHaveLength(1)
    const parts = turns[0]!.parts
    expect(parts.map((p) => [p.part, p.server])).toEqual([
      ['tool_definitions', 'srv-a'],
      ['tool_definitions', 'srv-b'],
    ])
    expect(JSON.parse(parts[0]!.text)).toEqual([alpha])
    expect(parts[0]!.tokens).toBeUndefined()
    expect(Object.keys(contentFromParts(parts))).toEqual(['tool_definitions'])
  })

  it('skips malformed, nameless and oversized schema files without inventing tools', async () => {
    const root = makeRoot()
    writeTool(root, 'srv', 'broken.json', '{ not json')
    writeTool(root, 'srv', 'nameless.json', { description: 'no name' })
    writeTool(root, 'srv', 'blank.json', { name: '  ' })
    writeTool(root, 'srv', 'big.json', { name: 'big', description: 'x'.repeat(1_100_000) })
    writeTool(root, 'srv', 'ok.json', { name: 'ok_tool' })

    const parts = await readAntigravityMcpCatalogue(root)

    expect(parts).toHaveLength(1)
    expect((JSON.parse(parts[0]!.text) as Array<{ name: string }>).map((t) => t.name)).toEqual(['ok_tool'])
  })

  it('yields nothing when there is no MCP directory, no tools, or the path is not a conversation store', async () => {
    const empty = makeRoot()
    expect(await collect(join(empty, 'conversations', 'cascade-1.db'))).toEqual([])

    mkdirSync(join(empty, 'mcp', 'srv-empty'), { recursive: true })
    expect(await collect(join(empty, 'conversations', 'cascade-1.db'))).toEqual([])

    const root = makeRoot()
    writeTool(root, 'srv', 'a.json', { name: 'a' })
    expect(await collect(join(root, 'conversations', 'missing.db'))).toEqual([])
    expect(await collect(join(root, 'antigravity-statusline.jsonl'))).toEqual([])
  })

  it('resolves the app-data root only for conversation databases', () => {
    expect(antigravityRootFromConversationPath('/h/.gemini/antigravity-cli/conversations/x.db')).toBe(
      '/h/.gemini/antigravity-cli',
    )
    expect(antigravityRootFromConversationPath('/h/.gemini/antigravity/conversations/x.pb')).toBeUndefined()
    expect(antigravityRootFromConversationPath('/h/.gemini/antigravity/other/x.db')).toBeUndefined()
  })

  it('is registered for every Antigravity harness id and keeps built-in content unmeasurable', () => {
    for (const id of ['antigravity', 'antigravity-cli', 'antigravity-ide']) {
      expect(PROVIDER_READERS.get(id)).toBe(antigravityReader)
      const m = measurabilityFor(id)
      expect(m['tool_definitions']).toMatchObject({
        availability: 'not_measurable',
        reason: expect.stringContaining('Antigravity does not export tool definitions'),
      })
      expect(m['system_prompt']).toMatchObject({ availability: 'not_measurable' })
    }
  })

  it('lands the catalogue on the first synthesized turn, naming each tool and its server', async () => {
    // The calls carry agy-step pairing ids from a real conversation database, so the
    // catalogue turn must reach the first call without positional pairing.
    const { dbPath } = makeSegmentsRoot({ mcpTool: true })
    const mk = (lsi: number, n: number): ParsedProviderCall => ({
      provider: 'antigravity',
      model: 'synthetic-model',
      inputTokens: 100,
      outputTokens: 10,
      cacheCreationInputTokens: 0,
      cacheReadInputTokens: 0,
      cachedInputTokens: 0,
      reasoningTokens: 0,
      webSearchRequests: 0,
      costUSD: 0,
      tools: [],
      bashCommands: [],
      timestamp: `2026-08-29T12:00:0${n}.000Z`,
      speed: 'standard',
      deduplicationKey: `antigravity:${SEGMENTS_CONVERSATION}:${n}`,
      pairingId: `agy-step:${lsi}`,
      userMessage: '',
      sessionId: SEGMENTS_CONVERSATION,
    })

    const { records, problems } = await ingestProviders(['antigravity-cli'], () => ({
      calls: [mk(2, 0), mk(5, 1)],
      filePath: dbPath,
      harnessId: 'antigravity-cli',
      sourceKey: `antigravity-cli:${SEGMENTS_CONVERSATION}`,
    }))

    expect(problems).toEqual([])
    const withDefs = records.filter((r) => r.parts?.some((p) => p.part === 'tool_definitions'))
    expect(withDefs).toHaveLength(1)
    expect(withDefs[0]!.parts![0]).toMatchObject({ part: 'tool_definitions', server: 'srv-a' })
    expect(records.filter((r) => r.parts === undefined || r.parts.length === 0).length).toBe(records.length - 1)
  })
})

describe('antigravityReader transcript size cap (F2)', () => {
  const transcriptPathOf = (root: string): string =>
    join(root, 'brain', SEGMENTS_CONVERSATION, '.system_generated', 'logs', 'transcript.jsonl')

  it('yields no invocation turns from a transcript over the session-file cap, though its leading lines are valid', async () => {
    const { root, dbPath } = makeSegmentsRoot({ transcript: fixtureTranscript() })
    // Zero bytes extend the file past the cap without writing them. The valid lines still lead.
    truncateSync(transcriptPathOf(root), MAX_SESSION_FILE_BYTES + 1)

    const turns = await collect(dbPath)

    expect(turns.filter((t) => (t.toolCalls ?? []).length > 0 || (t.toolResults ?? []).length > 0)).toEqual([])
  })

  it('still yields the invocation turns of a transcript just under the session-file cap', async () => {
    const { root, dbPath } = makeSegmentsRoot({ transcript: fixtureTranscript() })
    truncateSync(transcriptPathOf(root), MAX_SESSION_FILE_BYTES - 1)

    const turns = await collect(dbPath)

    expect(turns.map((t) => t.nativeRecordId)).toEqual(['agy-step:2', 'agy-step:5', 'agy-step:8'])
  })
})

describe('antigravityReader agy-step turns from the transcript', () => {
  it('yields one turn per agy step, each holding the tool calls of its own segment with decoded arguments', async () => {
    const { dbPath } = makeSegmentsRoot({ transcript: fixtureTranscript() })

    const turns = await collect(dbPath)

    expect(turns.map((t) => t.nativeRecordId)).toEqual(['agy-step:2', 'agy-step:5', 'agy-step:8'])
    expect(turns.map((t) => (t.toolCalls ?? []).map((c) => [c.name, c.arguments]))).toEqual([
      [['run_command', { CommandLine: 'synthetic-command --dry-run', Cwd: '/synthetic/workspace' }]],
      [['view_file', { AbsolutePath: '/synthetic/workspace/src/sample.ts', StartLine: 12 }]],
      [['grep_search', { SearchPath: '/synthetic/workspace', Query: 'TODO', MaxResults: 5 }]],
    ])
  })

  it('pairs a single-call segment with its GENERIC step as that call\'s result', async () => {
    const { dbPath } = makeSegmentsRoot({ transcript: fixtureTranscript() })

    const turn = requireTurn(await collect(dbPath), 'agy-step:2')
    const [call] = turn.toolCalls!

    expect(call).toBeDefined()
    expect(call!.id).not.toBe('')
    expect(turn.toolResults).toHaveLength(1)
    expect(turn.toolResults![0]).toMatchObject({ toolCallId: call!.id, content: 'synthetic command output' })
  })

  it('leaves results absent for a segment holding two tool calls', async () => {
    const text = [
      planner(3, [
        { name: 'alpha_tool', args: { Path: '/synthetic/a' } },
        { name: 'beta_tool', args: { Path: '/synthetic/b' } },
      ]),
      generic(4, 'synthetic shared output'),
      planner(6, [{ name: 'gamma_tool', args: { Path: '/synthetic/c' } }]),
    ].join('\n') + '\n'
    const { dbPath } = makeSegmentsRoot({ transcript: text })

    const turns = await collect(dbPath)
    const two = requireTurn(turns, 'agy-step:2')

    expect((two.toolCalls ?? []).map((c) => c.name)).toEqual(['alpha_tool', 'beta_tool'])
    expect(two.toolResults ?? []).toEqual([])
    expect((requireTurn(turns, 'agy-step:5').toolCalls ?? []).map((c) => c.name)).toEqual(['gamma_tool'])
  })

  it('gives no parts to a segment that lies before the transcript\'s first step, and pairs the rest normally', async () => {
    const { dbPath } = makeSegmentsRoot({ transcript: fixtureTranscript(6) })

    const turns = await collect(dbPath)
    const first = turnFor(turns, 'agy-step:2')

    expect(first?.parts ?? []).toEqual([])
    expect(first?.toolCalls ?? []).toEqual([])
    expect(first?.toolResults ?? []).toEqual([])
    expect((requireTurn(turns, 'agy-step:5').toolCalls ?? []).map((c) => c.name)).toEqual(['view_file'])
    expect((requireTurn(turns, 'agy-step:8').toolCalls ?? []).map((c) => c.name)).toEqual(['grep_search'])
  })

  it('skips an unparseable transcript line without throwing and keeps the valid steps', async () => {
    const text = [
      planner(3, [{ name: 'alpha_tool', args: { Path: '/synthetic/a' } }]),
      '{"step_index": 4, "type": "GENERIC", "content": ',
      planner(6, [{ name: 'beta_tool', args: { Path: '/synthetic/b' } }]),
    ].join('\n') + '\n'
    const { dbPath } = makeSegmentsRoot({ transcript: text })

    const turns = await collect(dbPath)

    expect((requireTurn(turns, 'agy-step:2').toolCalls ?? []).map((c) => c.name)).toEqual(['alpha_tool'])
    expect((requireTurn(turns, 'agy-step:5').toolCalls ?? []).map((c) => c.name)).toEqual(['beta_tool'])
  })

  it('yields no invocation turns without a transcript, and the MCP catalogue turn is unchanged', async () => {
    const { dbPath } = makeSegmentsRoot({ mcpTool: true })

    const turns = await collect(dbPath)

    expect(turns.filter((t) => (t.toolCalls ?? []).length > 0 || (t.toolResults ?? []).length > 0)).toEqual([])
    const catalogue = turns.filter((t) => t.parts.some((p) => p.part === 'tool_definitions'))
    expect(catalogue).toHaveLength(1)
    expect(catalogue[0]!.parts.map((p) => [p.part, p.server])).toEqual([['tool_definitions', 'srv-a']])
  })

  it('keeps the MCP catalogue on the first agy-step turn when a transcript is present', async () => {
    const { dbPath } = makeSegmentsRoot({ mcpTool: true, transcript: fixtureTranscript() })

    const turns = await collect(dbPath)

    expect(turns[0]!.nativeRecordId).toBe('agy-step:2')
    expect(turns[0]!.parts.map((p) => [p.part, p.server])).toEqual([['tool_definitions', 'srv-a']])
    expect((turns[0]!.toolCalls ?? []).map((c) => c.name)).toEqual(['run_command'])
    expect(turns.slice(1).some((t) => t.parts.some((p) => p.part === 'tool_definitions'))).toBe(false)
  })
})

describe('antigravity agy-step records through ingestProviders', () => {
  it('attaches each tool.invoke record under the call record of its own segment', async () => {
    const { dbPath } = makeSegmentsRoot({ transcript: fixtureTranscript() })

    const { records, problems } = await ingestCalls(await providerCalls(dbPath), dbPath)

    expect(problems).toEqual([])
    const parentOf = (iso: string) => parentAt(records, iso)
    expect(toolRecordsOf(records, parentOf(CALL_2_AT)!).map((r) => [r.name, r.status])).toEqual([['run_command', 'ok']])
    expect(toolRecordsOf(records, parentOf(CALL_5_AT)!).map((r) => [r.name, r.status])).toEqual([['view_file', 'ok']])
    expect(toolRecordsOf(records, parentOf(CALL_8_AT)!).map((r) => [r.name, r.status])).toEqual([['grep_search', 'ok']])
  })

  it('gives a call with no matching segment no parts and no tool records', async () => {
    const { dbPath } = makeSegmentsRoot({ transcript: fixtureTranscript(6) })

    const { records } = await ingestCalls(await providerCalls(dbPath), dbPath)

    const unmatched = parentAt(records, CALL_2_AT)!
    expect(unmatched).toBeDefined()
    expect(unmatched.parts ?? []).toEqual([])
    expect(toolRecordsOf(records, unmatched)).toEqual([])
    expect(toolRecordsOf(records, parentAt(records, CALL_5_AT)!).map((r) => r.name)).toEqual(['view_file'])
  })

  it('lands the MCP catalogue on the first call record even when no transcript is present', async () => {
    const { dbPath } = makeSegmentsRoot({ mcpTool: true })

    const { records, problems } = await ingestCalls(await providerCalls(dbPath), dbPath)

    expect(problems).toEqual([])
    const withDefs = records.filter((r) => r.parts?.some((p) => p.part === 'tool_definitions'))
    expect(withDefs).toHaveLength(1)
    expect(new Date(withDefs[0]!.timestamp).toISOString()).toBe(CALL_2_AT)
  })
})
