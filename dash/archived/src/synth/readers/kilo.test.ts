// Kilo Code session content reader (decision D7).
//
// Kilo's shared runtime writes ~/.local/share/kilo/kilo.db: one `message` row
// per turn and one `part` row per content fragment. This reader is the content
// half of that store — the SQLite parser already owns its counters — so the
// contract pinned here is which fragments become canonical buckets and which
// do not.
//
// Two mappings are load-bearing and easy to get backwards:
//
//   * One turn per TOKEN-BEARING assistant message, keyed by the Kilo message
//     id. `buildAssistantCall` also emits a call for a zero-token assistant
//     message that still carries text (a router response), so the reader's
//     turn list and the parser's call list are not the same length. Pairing
//     therefore happens by id (`nativeRecordId`/`pairingId`), never by index.
//   * kilo.db stores neither the runtime system prompt nor tool-definition
//     schemas. Emitting those buckets from a part name would present a
//     measurement the store does not carry (R8.5, R10.1).
//
// The fixture is the live schema pinned by kilo-live-schema.test.ts: the
// `session` row carries the columns the current Kilo build writes, and the
// store holds `message`/`part` beside them.
import { createHash } from 'node:crypto'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { contentFromParts } from '../../canon/types.js'
import type { DateRange } from '../../types.js'

import { kiloReader } from './kilo.js'
import type { ReaderTurn } from './types.js'

/// `node:sqlite` is loaded through `createRequire` rather than a static import
/// so the suite still loads on a Node build without it.
const requireForTest = createRequire(import.meta.url)
type SqliteDb = {
  exec(sql: string): void
  prepare(sql: string): { run(...params: unknown[]): void }
  close(): void
}
const openSqlite = (path: string): SqliteDb => {
  const { DatabaseSync } = requireForTest('node:sqlite') as {
    DatabaseSync: new (path: string) => SqliteDb
  }
  return new DatabaseSync(path)
}

const SESSION_ID = 'sess-kilo-reader'
/** One minute after this instant is the first assistant message. */
const T0 = Date.parse('2026-09-10T10:00:00.000Z')
const MINUTE = 60_000

const tempRoots: string[] = []

// Never read the developer's real capture store: every test starts with an empty one.
beforeAll(() => {
  const empty = mkdtempSync(join(tmpdir(), 'kyber-kilo-no-capture-'))
  tempRoots.push(empty)
  process.env.KYBERDASH_KILO_CAPTURE_DIR = empty
})

afterAll(() => {
  delete process.env.KYBERDASH_KILO_CAPTURE_DIR
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

type FixtureMessage = {
  id: string
  role: string
  timeCreated: number | null
  data?: Record<string, unknown>
}

type FixturePart = {
  id: string
  messageId: string
  timeCreated: number | null
  data: Record<string, unknown>
}

/**
 * Build a kilo.db holding exactly the rows a test names. The live `session`
 * column set is written so the fixture is the store Kilo actually produces,
 * not a three-column stand-in the parser happens to tolerate.
 */
function writeKiloDb(
  dbPath: string,
  messages: readonly FixtureMessage[],
  parts: readonly FixturePart[],
): void {
  const db = openSqlite(dbPath)
  try {
    db.exec(`
      CREATE TABLE session (
        id TEXT PRIMARY KEY, project_id TEXT NOT NULL, parent_id TEXT,
        slug TEXT NOT NULL, directory TEXT NOT NULL, title TEXT NOT NULL,
        version TEXT NOT NULL, time_created INTEGER, time_updated INTEGER,
        time_archived INTEGER,
        path TEXT, agent TEXT, model TEXT, cost REAL,
        tokens_input INTEGER, tokens_output INTEGER, tokens_reasoning INTEGER,
        tokens_cache_read INTEGER, tokens_cache_write INTEGER, metadata TEXT
      );
      CREATE TABLE message (
        id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
        time_created INTEGER, time_updated INTEGER, data TEXT NOT NULL
      );
      CREATE TABLE part (
        id TEXT PRIMARY KEY, message_id TEXT NOT NULL,
        session_id TEXT NOT NULL, time_created INTEGER,
        time_updated INTEGER, data TEXT NOT NULL
      );
    `)
    db.prepare(`INSERT INTO session (id, project_id, parent_id, slug, directory, title, version,
                                     time_created, time_updated, time_archived,
                                     path, agent, model, cost, tokens_input, tokens_output,
                                     tokens_reasoning, tokens_cache_read, tokens_cache_write, metadata)
                VALUES (?, 'proj-1', NULL, 'slug-reader', '/Users/hal/myproject', 'Kilo reader contract',
                        '1.4.2', ?, ?, NULL, '/Users/hal/myproject', 'build', ?, 0.42,
                        3000, 800, 0, 0, 0, ?)`)
      .run(
        SESSION_ID,
        T0,
        T0,
        JSON.stringify({ id: 'claude-opus-4-6', providerID: 'anthropic' }),
        JSON.stringify({ source: 'kilo-shared-runtime' }),
      )

    for (const message of messages) {
      db.prepare(`INSERT INTO message (id, session_id, time_created, time_updated, data)
                  VALUES (?, ?, ?, ?, ?)`)
        .run(
          message.id,
          SESSION_ID,
          message.timeCreated,
          message.timeCreated,
          JSON.stringify({ role: message.role, ...message.data }),
        )
    }

    for (const part of parts) {
      db.prepare(`INSERT INTO part (id, message_id, session_id, time_created, time_updated, data)
                  VALUES (?, ?, ?, ?, ?, ?)`)
        .run(part.id, part.messageId, SESSION_ID, part.timeCreated, part.timeCreated, JSON.stringify(part.data))
    }
  } finally {
    db.close()
  }
}

/**
 * The contract fixture. Insertion order is deliberately not chronological, so
 * an implementation that yields in scan order instead of `time_created, id`
 * order fails rather than passing by accident.
 *
 *   msg-u1     user      first question
 *   msg-a1     assistant tokens 100/20 — text, completed tool, reasoning
 *   msg-router assistant NO tokens — text only (the parser yields a call; the
 *                                     reader must not yield a turn)
 *   msg-u2     user      second question
 *   msg-a2     assistant tokens 300/60 — two texts, errored tool, and every
 *                                        part type that is not a bucket
 *   msg-a3     assistant tokens 7/3 — text only
 */
function writeContractDb(dbPath: string): void {
  writeKiloDb(
    dbPath,
    [
      { id: 'msg-a3', role: 'assistant', timeCreated: T0 + 5 * MINUTE, data: { modelID: 'claude-opus-4-6', tokens: { input: 7, output: 3 } } },
      { id: 'msg-u1', role: 'user', timeCreated: T0 },
      { id: 'msg-a2', role: 'assistant', timeCreated: T0 + 4 * MINUTE, data: { modelID: 'claude-opus-4-6', tokens_input: 300, tokens_output: 60 } },
      { id: 'msg-router', role: 'assistant', timeCreated: T0 + 2 * MINUTE, data: { modelID: 'edenai/router-model' } },
      { id: 'msg-a1', role: 'assistant', timeCreated: T0 + MINUTE, data: { modelID: 'claude-opus-4-6', tokens: { input: 100, output: 20, reasoning: 0, cache: { read: 0, write: 0 } } } },
      { id: 'msg-u2', role: 'user', timeCreated: T0 + 3 * MINUTE },
    ],
    [
      { id: 'part-u1-text', messageId: 'msg-u1', timeCreated: T0, data: { type: 'text', text: 'first question' } },
      { id: 'part-a1-text', messageId: 'msg-a1', timeCreated: T0 + MINUTE, data: { type: 'text', text: 'first answer' } },
      {
        id: 'part-a1-tool',
        messageId: 'msg-a1',
        timeCreated: T0 + MINUTE,
        data: {
          type: 'tool',
          callID: 'call-1',
          tool: 'bash',
          state: { status: 'completed', input: { command: 'ls' }, output: 'file.txt\n' },
        },
      },
      { id: 'part-a1-reasoning', messageId: 'msg-a1', timeCreated: T0 + MINUTE, data: { type: 'reasoning', text: 'weighing the options' } },
      { id: 'part-router-text', messageId: 'msg-router', timeCreated: T0 + 2 * MINUTE, data: { type: 'text', text: 'router filler answer' } },
      { id: 'part-u2-text', messageId: 'msg-u2', timeCreated: T0 + 3 * MINUTE, data: { type: 'text', text: 'second question' } },
      { id: 'part-a2-text-1', messageId: 'msg-a2', timeCreated: T0 + 4 * MINUTE, data: { type: 'text', text: 'second answer' } },
      { id: 'part-a2-text-2', messageId: 'msg-a2', timeCreated: T0 + 4 * MINUTE, data: { type: 'text', text: 'second answer, continued' } },
      {
        id: 'part-a2-tool',
        messageId: 'msg-a2',
        timeCreated: T0 + 4 * MINUTE,
        data: {
          type: 'tool',
          callID: 'call-2',
          tool: 'read',
          state: { status: 'error', input: { filePath: '/nope' }, output: 'ENOENT: no such file' },
        },
      },
      { id: 'part-a2-step-start', messageId: 'msg-a2', timeCreated: T0 + 4 * MINUTE, data: { type: 'step-start' } },
      { id: 'part-a2-step-finish', messageId: 'msg-a2', timeCreated: T0 + 4 * MINUTE, data: { type: 'step-finish', reason: 'stop' } },
      { id: 'part-a2-patch', messageId: 'msg-a2', timeCreated: T0 + 4 * MINUTE, data: { type: 'patch', hash: 'abc123' } },
      { id: 'part-a2-file', messageId: 'msg-a2', timeCreated: T0 + 4 * MINUTE, data: { type: 'file', filename: 'src/app.ts' } },
      { id: 'part-a2-compaction', messageId: 'msg-a2', timeCreated: T0 + 4 * MINUTE, data: { type: 'compaction', auto: true } },
      { id: 'part-a3-text', messageId: 'msg-a3', timeCreated: T0 + 5 * MINUTE, data: { type: 'text', text: 'third answer' } },
    ],
  )
}

function newRoot(prefix: string): string {
  const root = mkdtempSync(join(tmpdir(), prefix))
  tempRoots.push(root)
  return root
}

async function readTurns(filePath: string, dateRange?: DateRange): Promise<ReaderTurn[]> {
  const turns: ReaderTurn[] = []
  for await (const turn of kiloReader.read(filePath, dateRange)) turns.push(turn)
  return turns
}

describe('kiloReader — the kilo.db session store', () => {
  let dbPath: string
  let sourcePath: string

  beforeAll(() => {
    const root = newRoot('kyber-kilo-reader-')
    dbPath = join(root, 'kilo.db')
    writeContractDb(dbPath)
    sourcePath = `${dbPath}:${SESSION_ID}`
  })

  it('yields one turn per token-bearing assistant message, ordered by time_created then id', async () => {
    const turns = await readTurns(sourcePath)

    // msg-u1/msg-u2 are user turns and msg-router is an assistant message with
    // no token counters: neither is a turn. The parser still yields a call for
    // msg-router, which is why pairing must key on the message id.
    expect(
      turns.map((turn) => turn.nativeRecordId),
      'turns must be the token-bearing assistant messages in time_created, id order',
    ).toEqual(['msg-a1', 'msg-a2', 'msg-a3'])
  })

  it('orders same-instant messages by id', async () => {
    const root = newRoot('kyber-kilo-reader-tiebreak-')
    const tiedPath = join(root, 'kilo.db')
    writeKiloDb(
      tiedPath,
      [
        { id: 'msg-z9', role: 'assistant', timeCreated: T0 + MINUTE, data: { tokens_input: 10, tokens_output: 5 } },
        { id: 'msg-z1', role: 'assistant', timeCreated: T0 + MINUTE, data: { tokens_input: 20, tokens_output: 6 } },
      ],
      [
        { id: 'part-z9-text', messageId: 'msg-z9', timeCreated: T0 + MINUTE, data: { type: 'text', text: 'inserted first' } },
        { id: 'part-z1-text', messageId: 'msg-z1', timeCreated: T0 + MINUTE, data: { type: 'text', text: 'inserted second' } },
      ],
    )

    const turns = await readTurns(`${tiedPath}:${SESSION_ID}`)

    // Same instant: `id` is the tiebreak, and it is not insertion order.
    expect(turns.map((turn) => turn.nativeRecordId)).toEqual(['msg-z1', 'msg-z9'])
  })

  it('carries the preceding user text and the assistant text parts as conversation_history', async () => {
    const turns = await readTurns(sourcePath)
    expect(turns, 'premise: one turn per token-bearing assistant message').toHaveLength(3)
    const [first, second, third] = turns

    expect(
      contentFromParts(first!.parts).conversation_history,
      'the turn opens with the user text that preceded the assistant message',
    ).toBe('first question\nfirst answer')
    expect(contentFromParts(second!.parts).conversation_history).toBe(
      'second question\nsecond answer\nsecond answer, continued',
    )
    // Each turn carries its own prefix, not the whole session transcript.
    expect(contentFromParts(third!.parts).conversation_history).toBe('third answer')
    expect(contentFromParts(third!.parts).conversation_history).not.toContain('first question')
  })

  it('surfaces tool parts as toolCalls with the callID, tool name, and state input', async () => {
    const turns = await readTurns(sourcePath)
    expect(turns, 'premise: one turn per token-bearing assistant message').toHaveLength(3)
    const [first, second] = turns

    expect(first!.toolCalls).toHaveLength(1)
    expect(first!.toolCalls?.[0]).toMatchObject({
      id: 'call-1',
      name: 'bash',
      arguments: { command: 'ls' },
    })
    expect(second!.toolCalls?.[0]).toMatchObject({
      id: 'call-2',
      name: 'read',
      arguments: { filePath: '/nope' },
    })
  })

  it('records completed tool output as tool_result_content and a tool result', async () => {
    const turns = await readTurns(sourcePath)
    expect(turns, 'premise: one turn per token-bearing assistant message').toHaveLength(3)
    const first = turns[0]!

    expect(
      contentFromParts(first.parts).tool_result_content,
      'a completed tool part contributes its output as tool-result content',
    ).toBe('file.txt\n')
    expect(first.toolResults).toHaveLength(1)
    expect(first.toolResults?.[0]).toMatchObject({
      toolCallId: 'call-1',
      content: 'file.txt\n',
    })
    expect(first.toolResults?.[0]?.isError ?? false, 'a completed tool result is not an error').toBe(false)
  })

  it('marks an errored tool part\'s result as an error', async () => {
    const turns = await readTurns(sourcePath)
    expect(turns, 'premise: one turn per token-bearing assistant message').toHaveLength(3)
    const second = turns[1]!

    expect(second.toolResults?.[0]?.isError, 'state.status === "error" is an errored result').toBe(true)
    expect(second.toolResults?.[0]?.content).toBe('ENOENT: no such file')
  })

  it('emits no content bucket for reasoning, patch, step-start, step-finish, file, or compaction parts', async () => {
    const turns = await readTurns(sourcePath)
    // Without this premise the assertion below is satisfied by an empty reader.
    expect(turns, 'premise: the fixture yields one turn per token-bearing assistant message').toHaveLength(3)

    for (const turn of turns) {
      const kinds = [...new Set(turn.parts.map((part) => part.part))].sort()
      expect(
        kinds,
        `turn ${turn.nativeRecordId} must only bucket content the store carries`,
      ).toEqual(['conversation_history', 'tool_result_content'])
    }
  })

  it('emits no system_prompt or tool_definitions part without a capture store — kilo.db stores neither', async () => {
    const turns = await readTurns(sourcePath)
    // Without this premise the assertion below is satisfied by an empty reader.
    expect(turns, 'premise: the fixture yields one turn per token-bearing assistant message').toHaveLength(3)
    const kinds = new Set(turns.flatMap((turn) => turn.parts.map((part) => part.part)))

    expect(kinds.has('system_prompt')).toBe(false)
    expect(kinds.has('tool_definitions')).toBe(false)
  })

  it('honours dateRange the way the sibling readers do', async () => {
    const root = newRoot('kyber-kilo-reader-window-')
    const windowedPath = join(root, 'kilo.db')
    writeKiloDb(
      windowedPath,
      [
        { id: 'msg-before', role: 'assistant', timeCreated: T0 - 60 * MINUTE, data: { tokens_input: 10, tokens_output: 5 } },
        { id: 'msg-inside', role: 'assistant', timeCreated: T0 + MINUTE, data: { tokens_input: 20, tokens_output: 6 } },
        { id: 'msg-after', role: 'assistant', timeCreated: T0 + 60 * MINUTE, data: { tokens_input: 30, tokens_output: 7 } },
        // A row with no instant cannot be placed in a window; it is not a
        // measured zero, and the sibling readers drop it rather than guess.
        { id: 'msg-untimed', role: 'assistant', timeCreated: null, data: { tokens_input: 40, tokens_output: 8 } },
      ],
      [
        { id: 'part-before-text', messageId: 'msg-before', timeCreated: T0 - 60 * MINUTE, data: { type: 'text', text: 'before window' } },
        { id: 'part-inside-text', messageId: 'msg-inside', timeCreated: T0 + MINUTE, data: { type: 'text', text: 'inside window' } },
        { id: 'part-after-text', messageId: 'msg-after', timeCreated: T0 + 60 * MINUTE, data: { type: 'text', text: 'after window' } },
        { id: 'part-untimed-text', messageId: 'msg-untimed', timeCreated: null, data: { type: 'text', text: 'untimed answer' } },
      ],
    )
    const source = `${windowedPath}:${SESSION_ID}`

    const windowed = await readTurns(source, {
      start: new Date(T0),
      end: new Date(T0 + 30 * MINUTE),
    })
    expect(windowed.map((turn) => turn.nativeRecordId)).toEqual(['msg-inside'])
    expect(contentFromParts(windowed[0]!.parts).conversation_history).toContain('inside window')

    const unfiltered = await readTurns(source)
    expect(unfiltered.map((turn) => turn.nativeRecordId).sort()).toEqual([
      'msg-after',
      'msg-before',
      'msg-inside',
      'msg-untimed',
    ])
  })

  it('yields nothing, without throwing, when the database or the session is missing', async () => {
    const root = newRoot('kyber-kilo-reader-absent-')

    const absentStore = await readTurns(`${join(root, 'absent.db')}:${SESSION_ID}`)
    expect(absentStore, 'a database that is not there is an absence, not an error').toEqual([])

    const absentSession = await readTurns(`${dbPath}:sess-not-here`)
    expect(absentSession, 'a session id the store does not hold is an absence too').toEqual([])
  })
})

describe('kiloReader — with the capture plugin store', () => {
  const blobIn = (store: string, text: string): string => {
    const hash = createHash('sha256').update(text).digest('hex')
    mkdirSync(join(store, 'blobs'), { recursive: true })
    writeFileSync(join(store, 'blobs', hash), text)
    return hash
  }

  function fixture(): { sourcePath: string; store: string } {
    const root = newRoot('kyber-kilo-reader-capture-')
    const dbPath = join(root, 'kilo.db')
    writeKiloDb(
      dbPath,
      [
        { id: 'msg-u1', role: 'user', timeCreated: T0 },
        { id: 'msg-a1', role: 'assistant', timeCreated: T0 + MINUTE, data: { parentID: 'msg-u1', tokens: { input: 10, output: 5 } } },
        { id: 'msg-a2', role: 'assistant', timeCreated: T0 + 2 * MINUTE, data: { parentID: 'msg-unknown', tokens: { input: 10, output: 5 } } },
      ],
      [{ id: 'p1', messageId: 'msg-a1', timeCreated: T0 + MINUTE, data: { type: 'text', text: 'hi' } }],
    )
    const store = join(root, 'capture')
    const systemHash = blobIn(store, 'You are Kilo. <available_skills/>')
    const bashHash = blobIn(store, '{"id":"bash"}')
    mkdirSync(store, { recursive: true })
    writeFileSync(
      join(store, 'index.jsonl'),
      `${JSON.stringify({ v: 1, ts: T0 + MINUTE, sessionID: SESSION_ID, messageID: 'msg-u1', systemHash, toolHashes: { bash: bashHash } })}\n`,
    )
    return { sourcePath: `${dbPath}:${SESSION_ID}`, store }
  }

  it('adds the captured system prompt and tool definitions to the turn that call answered', async () => {
    const { sourcePath, store } = fixture()
    process.env.KYBERDASH_KILO_CAPTURE_DIR = store
    const [first] = await readTurns(sourcePath)
    const content = contentFromParts(first!.parts)
    expect(content.system_prompt).toBe('You are Kilo. <available_skills/>')
    expect(content.tool_definitions).toBe('{"id":"bash"}')
    expect(content.conversation_history).toBe('hi')
  })

  it('leaves the buckets absent for a turn with no captured call', async () => {
    const { sourcePath, store } = fixture()
    process.env.KYBERDASH_KILO_CAPTURE_DIR = store
    const turns = await readTurns(sourcePath)
    const content = contentFromParts(turns[1]!.parts)
    expect(content.system_prompt).toBeUndefined()
    expect(content.tool_definitions).toBeUndefined()
  })
})
