// Copilot CLI's data.db records ASAD's context taxonomy directly. These
// synthetic rows contain only neutral labels and counts, never CLI history.
import { afterEach, beforeEach, describe, expect, it, expectTypeOf, vi } from 'vitest'
import { DatabaseSync } from 'node:sqlite'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import {
  copilotCliReader,
  loadCopilotCliCalls,
  parseCopilotCliContextRow,
} from './copilot.js'
import type { ContentReader, ReaderTurn } from './types.js'
import { openDatabase } from '../../ingest/sqlite.js'

// Wraps the real opener so a test can count how often the session store is opened.
// The wrapped behaviour is unchanged: every other test still reads a real SQLite file.
vi.mock('../../ingest/sqlite.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../ingest/sqlite.js')>()
  return { ...actual, openDatabase: vi.fn(actual.openDatabase) }
})

const CLI_CONTEXT_ROW = {
  id: 'synthetic-copilot-cli-session',
  context_system_tokens: 120,
  context_conversation_tokens: 340,
  context_tool_definitions_tokens: 560,
  context_mcp_tools_tokens: 780,
  context_buffer_tokens: 90,
  context_tier: 'standard',
}

describe('copilotCliReader', () => {
  it('is exposed through the public ContentReader contract', () => {
    expectTypeOf(copilotCliReader).toMatchTypeOf<ContentReader>()
  })

  it('preserves Copilot CLI’s reported ASAD taxonomy without collapsing buckets', () => {
    expect(parseCopilotCliContextRow(CLI_CONTEXT_ROW)).toEqual({
      context_system_tokens: 120,
      context_conversation_tokens: 340,
      context_tool_definitions_tokens: 560,
      context_mcp_tools_tokens: 780,
      context_buffer_tokens: 90,
      context_tier: 'standard',
    })
  })

  it('does not turn an omitted reported bucket into zero', () => {
    const { context_buffer_tokens: _omitted, ...withoutBuffer } = CLI_CONTEXT_ROW

    expect(parseCopilotCliContextRow(withoutBuffer)).not.toHaveProperty('context_buffer_tokens')
  })
})

describe('loadCopilotCliCalls', () => {
  it('loads a synthetic SQLite session row without adding omitted taxonomy buckets', () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-copilot-cli-'))
    const filePath = join(root, 'data.db')
    const db = new DatabaseSync(filePath)
    try {
      db.exec(`
        CREATE TABLE sessions (
          id TEXT,
          session_id TEXT,
          model TEXT,
          created_at TEXT,
          context_system_tokens INTEGER,
          context_conversation_tokens INTEGER,
          context_tier TEXT
        );
        INSERT INTO sessions VALUES (
          'synthetic-row', 'synthetic-session', 'gpt-5',
          '2026-09-04T12:00:00.000Z', 120, 340, 'standard'
        );
      `)

      expect(loadCopilotCliCalls(filePath)).toMatchObject([{
        provider: 'copilot',
        sessionId: 'synthetic-session',
        context_system_tokens: 120,
        context_conversation_tokens: 340,
        context_tier: 'standard',
      }])
      expect(loadCopilotCliCalls(filePath)[0]).not.toHaveProperty('context_buffer_tokens')
    } finally {
      db.close()
      rmSync(root, { recursive: true, force: true })
    }
  })
})

describe('loadCopilotCliCalls: reader-supplied cost_usd stays harness-reported (issue #186, R5.2)', () => {
  it('a genuine reader figure is carried verbatim on the harness basis', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-copilot-cli-cost-'))
    const filePath = join(root, 'data.db')
    const db = new DatabaseSync(filePath)
    try {
      db.exec(`
        CREATE TABLE sessions (id TEXT, session_id TEXT, model TEXT, created_at TEXT, cost_usd REAL,
          input_tokens INTEGER, output_tokens INTEGER);
        INSERT INTO sessions VALUES ('cost-row', 'cost-session', 'claude-sonnet-5-5',
          '2026-09-04T12:00:00.000Z', 0.42, 1000, 200);
      `)
      const [call] = loadCopilotCliCalls(filePath)
      expect(call!.costUSD).toBe(0.42)
      const { synthesizeCall } = await import('../synth.js')
      const cost = synthesizeCall(call!).cost
      expect(cost.basis).toBe('harness')
      expect(cost.value).toBe(0.42)
    } finally {
      db.close()
      rmSync(root, { recursive: true, force: true })
    }
  })
})

describe('loadCopilotCliCalls: absent cost_usd is not a harness figure (issue #186, review 4149313627)', () => {
  const withRow = (values: string, run: (call: ReturnType<typeof loadCopilotCliCalls>[number]) => void) => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-copilot-cli-nocost-'))
    const filePath = join(root, 'data.db')
    const db = new DatabaseSync(filePath)
    try {
      db.exec(`
        CREATE TABLE sessions (id TEXT, session_id TEXT, model TEXT, created_at TEXT, cost_usd REAL,
          input_tokens INTEGER, output_tokens INTEGER);
        INSERT INTO sessions VALUES ${values};
      `)
      const [call] = loadCopilotCliCalls(filePath)
      run(call!)
    } finally {
      db.close()
      rmSync(root, { recursive: true, force: true })
    }
  }

  it('a finite cost_usd still sets costHarnessReported true', () => {
    withRow(
      `('c1', 'cs1', 'claude-sonnet-5-5', '2026-09-04T12:00:00.000Z', 0.42, 1000, 200)`,
      (call) => {
        expect(call.costHarnessReported).toBe(true)
        expect(call.costUSD).toBe(0.42)
      },
    )
  })

  it('a NULL cost_usd does not set costHarnessReported', () => {
    withRow(
      `('c2', 'cs2', 'claude-sonnet-5-5', '2026-09-04T12:00:00.000Z', NULL, 1000, 200)`,
      (call) => {
        expect(call.costHarnessReported).not.toBe(true)
      },
    )
  })

  it('a NULL cost_usd does not synthesize a harness-basis cost', async () => {
    const { synthesizeCall } = await import('../synth.js')
    let basis: string | undefined
    withRow(
      `('c3', 'cs3', 'claude-sonnet-5-5', '2026-09-04T12:00:00.000Z', NULL, 1000, 200)`,
      (call) => {
        basis = synthesizeCall(call).cost.basis
      },
    )
    expect(basis).not.toBe('harness')
  })
})

// D23: the content reader resolves each session's journal at
// `<home>/.copilot/session-state/<sessionId>/events.jsonl` by exact directory
// name. HOME is redirected into a scratch tree so no real CLI history is read.
describe('copilotCliReader session journal (D23)', () => {
  const ROOT_STALE = 'Stale root prompt, superseded by the latest root system.message.'
  const ROOT_S1 = 'Root prompt.\n<available_skills>\n<skill><name>alpha</name><description>Does alpha.</description><location>/x/alpha</location></skill>\n</available_skills>'
  const SUB_S1 = 'Subagent prompt that no canon record may carry.'

  let home = ''

  beforeEach(() => {
    home = mkdtempSync(join(tmpdir(), 'copilot-journal-home-'))
    vi.stubEnv('HOME', home)
  })

  afterEach(() => {
    vi.unstubAllEnvs()
    rmSync(home, { recursive: true, force: true })
  })

  function writeJournal(sessionDirName: string, lines: readonly object[]): string {
    const dir = join(home, '.copilot', 'session-state', sessionDirName)
    mkdirSync(dir, { recursive: true })
    const file = join(dir, 'events.jsonl')
    writeFileSync(file, lines.map((line) => JSON.stringify(line)).join('\n') + '\n')
    return file
  }

  function writeStore(sessionIds: readonly string[]): string {
    const dir = join(home, '.copilot')
    mkdirSync(dir, { recursive: true })
    const file = join(dir, 'session-store.db')
    const db = new DatabaseSync(file)
    try {
      db.exec('CREATE TABLE sessions (id TEXT, cwd TEXT, repository TEXT);')
      const insert = db.prepare('INSERT INTO sessions (id, cwd, repository) VALUES (?, ?, ?)')
      for (const id of sessionIds) insert.run(id, `/synthetic/${id}`, `synthetic/${id}`)
    } finally {
      db.close()
    }
    return file
  }

  async function collect(file: string): Promise<ReaderTurn[]> {
    const turns: ReaderTurn[] = []
    for await (const turn of copilotCliReader.read(file)) turns.push(turn)
    return turns
  }

  function systemPromptsOf(turn: ReaderTurn): string[] {
    return turn.parts.filter((part) => part.part === 'system_prompt').map((part) => part.text)
  }

  function s1Journal(): string {
    return writeJournal('S1', [
      { type: 'system.message', data: { role: 'system', content: ROOT_STALE } },
      { type: 'system.message', data: { role: 'system', content: ROOT_S1 } },
      { type: 'system.message', agentId: 'agent-sub-1', data: { role: 'system', content: SUB_S1 } },
      { type: 'assistant.message', data: { messageId: 'msg-s1-root' } },
      { type: 'assistant.message', agentId: 'agent-sub-1', data: { messageId: 'msg-s1-sub' } },
      { type: 'assistant.message', data: { messageId: 'msg-s1-root-2' } },
    ])
  }

  it('attaches the latest root prompt to every turn of the session and never the subagent prompt', async () => {
    const turns = await collect(s1Journal())

    expect(turns.length).toBeGreaterThan(0)
    for (const turn of turns) {
      expect(turn.sessionId).toBe('S1')
      expect(systemPromptsOf(turn)).toEqual([ROOT_S1])
    }
    expect(JSON.stringify(turns)).not.toContain(SUB_S1)
  })

  it('reads the session journals a store path names and attaches each root prompt', async () => {
    const store = writeStore(['S1', 'S2'])
    s1Journal()

    const turns = await collect(store)
    const s1Turns = turns.filter((turn) => turn.sessionId === 'S1')

    expect(s1Turns.length).toBeGreaterThan(0)
    for (const turn of s1Turns) expect(systemPromptsOf(turn)).toEqual([ROOT_S1])
  })

  it('resolves a session journal by exact directory name, never by prefix', async () => {
    const store = writeStore(['S1', 'S3'])
    s1Journal()
    writeJournal('pending-session:S3', [
      { type: 'system.message', data: { role: 'system', content: 'PENDING prompt for a different directory.' } },
      { type: 'assistant.message', data: { messageId: 'msg-pending' } },
    ])

    const turns = await collect(store)

    // Positive control: S1 really resolves, so the S3 assertions are not vacuous.
    expect(turns.filter((turn) => turn.sessionId === 'S1').some((turn) => systemPromptsOf(turn).length > 0)).toBe(true)
    expect(turns.filter((turn) => turn.sessionId === 'S3').flatMap(systemPromptsOf)).toEqual([])
    expect(JSON.stringify(turns)).not.toContain('PENDING prompt')
  })

  it('attaches no prompt part when a session journal holds only a subagent prompt', async () => {
    const file = writeJournal('S7', [
      { type: 'system.message', agentId: 'agent-sub-7', data: { role: 'system', content: SUB_S1 } },
      { type: 'assistant.message', agentId: 'agent-sub-7', data: { messageId: 'msg-s7-sub' } },
    ])

    const turns = await collect(file)

    expect(turns.flatMap(systemPromptsOf)).toEqual([])
  })

  it('opens the session store once for several journals read one after another, not once per journal', async () => {
    // Three sessions in the store, each with its own journal: the store names all three.
    writeStore(['s1', 's2', 's3'])
    const journals = ['s1', 's2', 's3'].map((id) => writeJournal(id, [
      { type: 'system.message', data: { role: 'system', content: `Root prompt for ${id}.` } },
      { type: 'assistant.message', data: { messageId: `msg-${id}` } },
    ]))
    const storePath = join(home, '.copilot', 'session-store.db')

    const open = vi.mocked(openDatabase)
    open.mockClear()
    for (const journal of journals) await collect(journal)

    const storeOpens = open.mock.calls.filter(([path]) => path === storePath)
    expect(storeOpens, 'opens of the session store across three journal reads').toHaveLength(1)
  })

  it('yields nothing for an absent session store', async () => {
    expect(await collect(join(home, '.copilot', 'missing-session-store.db'))).toEqual([])
  })

  it('does not report tools offered: the journal holds no tool schemas', async () => {
    const turns = await collect(s1Journal())

    expect(turns.some((turn) => turn.toolsOffered !== undefined)).toBe(false)
  })
})
