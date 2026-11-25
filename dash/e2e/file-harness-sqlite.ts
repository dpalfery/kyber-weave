// Two sessions per SQLite-format file-import harness, each 10 or 12 turns: `kilo-shared-runtime`
// (kilo.db), `devin` (sessions.db), `warp` (warp.sqlite) and `cursor` (state.vscdb). Each database is
// built with `node:sqlite` at the path `HarnessSourceRegistry` checks for the running platform, with
// the tables and row shapes the mapper tests export (KiloMapperTests, DevinWarpMapperTests,
// CursorMapperTests). Timestamps derive from the run clock passed in (D10). A contract states
// each section as measured or as its explicit not-measurable state, from the mapper tests and
// docs/dash/telemetry-inventory.md, never from what the screen shows.
import { mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'

import type { FileHarnessContract, FileHarnessFixture } from './file-harness-contracts.js'
import { BASE_CONTRACT, TURN_SPACING_MS, hasTool, iso, plansFor, toolCallCount, type SessionPlan } from './file-harness-jsonl.js'

/** Opens (creating the directory and file) a SQLite database under `homeDir`, runs `work`, always closes it. */
function withDatabase(homeDir: string, relativePath: string, work: (db: DatabaseSync) => void): void {
  const target = join(homeDir, relativePath)
  mkdirSync(dirname(target), { recursive: true })
  const db = new DatabaseSync(target)
  try {
    work(db)
  } finally {
    db.close()
  }
}

/** The path the registry checks for each platform, relative to the home directory. */
function platformPath(paths: { readonly darwin: string; readonly win32: string; readonly other: string }): string {
  if (process.platform === 'darwin') {
    return paths.darwin
  }
  if (process.platform === 'win32') {
    return paths.win32
  }
  return paths.other
}

function sqliteFixtures(
  planHarness: string,
  contractFor: (plan: SessionPlan) => FileHarnessContract,
  build: (db: DatabaseSync, plan: SessionPlan, baseTimeMs: number) => void,
  relativePath: string,
): FileHarnessFixture[] {
  return plansFor(planHarness).map((plan) => ({
    contract: contractFor(plan),
    build: (homeDir: string, baseTimeMs: number) => {
      withDatabase(homeDir, relativePath, (db) => build(db, plan, baseTimeMs))
    },
  }))
}

// --- kilo-shared-runtime: kilo.db session / message / part ------------------------------------
const KILO_MODEL = 'claude-sonnet-4-5'

const KILO_PATH = platformPath({
  darwin: 'Library/Application Support/kilo/kilo.db',
  win32: 'AppData/Local/kilo/kilo.db',
  other: '.local/share/kilo/kilo.db',
})

function buildKilo(db: DatabaseSync, plan: SessionPlan, baseTimeMs: number): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS session (id TEXT PRIMARY KEY, title TEXT, parent_id TEXT, time_created INTEGER);
    CREATE TABLE IF NOT EXISTS message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, time_created INTEGER, data TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT, time_created INTEGER, data TEXT NOT NULL);
  `)
  db.prepare('INSERT INTO session (id, title, parent_id, time_created) VALUES (?, ?, NULL, ?)').run(
    plan.id,
    `Kilo file import ${plan.id}`,
    baseTimeMs,
  )
  const insertMessage = db.prepare('INSERT INTO message (id, session_id, time_created, data) VALUES (?, ?, ?, ?)')
  const insertPart = db.prepare('INSERT INTO part (id, message_id, session_id, time_created, data) VALUES (?, ?, ?, ?, ?)')
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    const userId = `${plan.id}-u-${turn}`
    const assistantId = `${plan.id}-a-${turn}`
    insertMessage.run(userId, plan.id, at, JSON.stringify({ role: 'user' }))
    insertPart.run(`${userId}-text`, userId, plan.id, at, JSON.stringify({ type: 'text', text: `Step ${turn}: continue the work` }))
    insertMessage.run(
      assistantId,
      plan.id,
      at + 1_000,
      JSON.stringify({
        role: 'assistant',
        modelID: KILO_MODEL,
        tokens: { input: 100 + turn, output: 40 + turn, reasoning: 0, cache: { read: 500, write: 40 } },
      }),
    )
    insertPart.run(`${assistantId}-text`, assistantId, plan.id, at + 1_000, JSON.stringify({ type: 'text', text: `Finished step ${turn}.` }))
    if (hasTool(turn)) {
      insertPart.run(
        `${assistantId}-tool`,
        assistantId,
        plan.id,
        at + 1_000,
        JSON.stringify({
          type: 'tool',
          callID: `${plan.id}-call-${turn}`,
          tool: 'bash',
          state: { status: 'completed', input: { command: 'ls' }, output: `file-${turn}.txt\n` },
        }),
      )
    }
  }
}

// --- devin: ~/.local/share/devin/cli/sessions.db ----------------------------------------------
// DevinMapper documents sessions.db as project/title/model/timestamp enrichment only (no turn
// data), and no retained source states its columns: the `sessions` columns below are the
// documented enrichment fields. The sessions carry their own ids, apart from the transcript ones.
const DEVIN_SESSIONS_PATH = '.local/share/devin/cli/sessions.db'

function buildDevinSessions(db: DatabaseSync, plan: SessionPlan, baseTimeMs: number): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS sessions (
      id TEXT PRIMARY KEY, title TEXT, project TEXT, model TEXT, created_at TEXT, updated_at TEXT, hidden INTEGER NOT NULL DEFAULT 0
    );
  `)
  db.prepare('INSERT INTO sessions (id, title, project, model, created_at, updated_at, hidden) VALUES (?, ?, ?, ?, ?, ?, 0)').run(
    plan.id,
    `Devin sqlite import ${plan.id}`,
    '/workspace/repo',
    'claude-sonnet-4-6',
    iso(baseTimeMs),
    iso(baseTimeMs + plan.turns * TURN_SPACING_MS),
  )
}

// --- warp: warp.sqlite agent_conversations / ai_queries / blocks -------------------------------
const WARP_MODEL = 'Claude Sonnet 4.5'

const WARP_PATH = platformPath({
  darwin:
    'Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support/dev.warp.Warp-Stable/warp.sqlite',
  win32: 'AppData/Local/warp/Warp/data/warp.sqlite',
  other: '.local/state/warp-terminal/warp.sqlite',
})

/** Warp stores `YYYY-MM-DD HH:MM:SS.ffffff` UTC timestamps. */
function warpTimestamp(ms: number): string {
  return `${new Date(ms).toISOString().replace('T', ' ').replace('Z', '')}000`
}

function buildWarp(db: DatabaseSync, plan: SessionPlan, baseTimeMs: number): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS agent_conversations (conversation_id TEXT PRIMARY KEY, conversation_data TEXT, last_modified_at TEXT);
    CREATE TABLE IF NOT EXISTS ai_queries (
      exchange_id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, start_ts TEXT, input TEXT, working_directory TEXT,
      output_status TEXT, model_id TEXT, planning_model_id TEXT, coding_model_id TEXT
    );
    CREATE TABLE IF NOT EXISTS blocks (block_id TEXT PRIMARY KEY, start_ts TEXT, stylized_command TEXT);
  `)
  // Warp reports usage per conversation and model, never per exchange.
  const usage = {
    conversation_usage_metadata: {
      token_usage: [
        {
          model_id: WARP_MODEL,
          warp_tokens: plan.turns * 150,
          byok_tokens: 0,
          warp_token_usage_by_category: { primary_agent: plan.turns * 150 },
          byok_token_usage_by_category: {},
        },
      ],
    },
  }
  db.prepare('INSERT INTO agent_conversations (conversation_id, conversation_data, last_modified_at) VALUES (?, ?, ?)').run(
    plan.id,
    JSON.stringify(usage),
    warpTimestamp(baseTimeMs + plan.turns * TURN_SPACING_MS),
  )
  const insertQuery = db.prepare(
    'INSERT INTO ai_queries (exchange_id, conversation_id, start_ts, input, working_directory, output_status, model_id, planning_model_id, coding_model_id) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)',
  )
  const insertBlock = db.prepare('INSERT INTO blocks (block_id, start_ts, stylized_command) VALUES (?, ?, ?)')
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    insertQuery.run(
      `${plan.id}-ex-${turn}`,
      plan.id,
      warpTimestamp(at),
      JSON.stringify([{ Query: { text: `Step ${turn}: continue the work` } }]),
      '/workspace/repo',
      '"Completed"',
      WARP_MODEL,
      '',
      '',
    )
    if (hasTool(turn)) {
      insertBlock.run(`${plan.id}-block-${turn}`, warpTimestamp(at + 2_000), `npm test -- step-${turn}`)
    }
  }
}

// --- cursor: state.vscdb cursorDiskKV bubble rows ----------------------------------------------
const CURSOR_MODEL = 'claude-sonnet-4-5'
const CURSOR_CONTEXT_WINDOW = 200_000

const CURSOR_PATH = platformPath({
  darwin: 'Library/Application Support/Cursor/User/globalStorage/state.vscdb',
  win32: 'AppData/Roaming/Cursor/User/globalStorage/state.vscdb',
  other: '.config/Cursor/User/globalStorage/state.vscdb',
})

/** A deterministic UUID-shaped bubble id for one session, turn and role. */
function bubbleUuid(sessionOrdinal: number, turn: number, role: 1 | 2): string {
  const hex = (value: number, width: number) => value.toString(16).padStart(width, '0')
  return `${hex(sessionOrdinal, 8)}-${hex(turn, 4)}-4000-8000-${hex(role, 12)}`
}

function buildCursor(db: DatabaseSync, plan: SessionPlan, baseTimeMs: number): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
    CREATE TABLE IF NOT EXISTS cursorDiskKV (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
  `)
  const insert = db.prepare('INSERT INTO cursorDiskKV (key, value) VALUES (?, ?)')
  const ordinal = plan.turns
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    const requestId = `${plan.id}-req-${turn}`
    insert.run(
      `bubbleId:${plan.id}:${bubbleUuid(ordinal, turn, 1)}`,
      JSON.stringify({
        type: 1,
        text: `Step ${turn}: continue the work`,
        createdAt: iso(at),
        requestId,
        tokenCount: { inputTokens: 100 + turn, outputTokens: 0 },
        contextWindow: CURSOR_CONTEXT_WINDOW,
      }),
    )
    insert.run(
      `bubbleId:${plan.id}:${bubbleUuid(ordinal, turn, 2)}`,
      JSON.stringify({
        type: 2,
        text: `Finished step ${turn}.`,
        createdAt: iso(at + 1_000),
        requestId,
        modelInfo: { modelName: CURSOR_MODEL },
        tokenCount: { inputTokens: 120 + turn, outputTokens: 40 + turn },
        contextWindow: CURSOR_CONTEXT_WINDOW,
      }),
    )
  }
}

/** SQLite-format harnesses (T7). */
export const FILE_HARNESS_SQLITE_FIXTURES: readonly FileHarnessFixture[] = [
  ...sqliteFixtures(
    'kilo-shared-runtime',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'kilo-shared-runtime',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      // kilo.db stores neither the runtime system prompt nor tool-definition schemas.
      contextMeasurable: false,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'published_priced',
      expectedModels: [KILO_MODEL],
    }),
    buildKilo,
    KILO_PATH,
  ),
  ...sqliteFixtures(
    'devin-sqlite',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'devin',
      sessionId: plan.id,
      turns: plan.turns,
      // sessions.db carries session metadata only: no steps, tokens or tool calls.
      expectedToolCalls: null,
      tokensMeasurable: false,
      perTurnTokensMeasurable: false,
      expectedCostStatus: 'out_of_scope',
      expectedModels: ['Sonnet 4.6'],
    }),
    buildDevinSessions,
    DEVIN_SESSIONS_PATH,
  ),
  ...sqliteFixtures(
    'warp',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'warp',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      // Warp reports tokens per conversation and model, never per exchange (decision D15).
      tokensMeasurable: true,
      perTurnTokensMeasurable: false,
      expectedCostStatus: 'published_priced',
      expectedModels: [WARP_MODEL],
    }),
    buildWarp,
    WARP_PATH,
  ),
  ...sqliteFixtures(
    'cursor',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'cursor',
      sessionId: plan.id,
      turns: plan.turns,
      // Agent-stream tool attachment is a live-only surface the bubble rows do not carry.
      expectedToolCalls: null,
      tokensMeasurable: true,
      // The bubbles store contextWindow, so context is measured.
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'published_priced',
      expectedModels: [CURSOR_MODEL],
    }),
    buildCursor,
    CURSOR_PATH,
  ),
]
