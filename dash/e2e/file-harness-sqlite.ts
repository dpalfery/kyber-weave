// Two sessions per SQLite-format file-import harness, each 10 or 12 turns: `kilo-shared-runtime`
// (kilo.db), `devin` (sessions.db), `warp` (warp.sqlite) and `cursor` (state.vscdb). Each database is
// built with `node:sqlite` at the path `HarnessSourceRegistry` checks for the running platform, with
// the tables and row shapes the mapper tests export (KiloMapperTests, DevinWarpMapperTests,
// CursorMapperTests). Timestamps derive from the run clock passed in (D10). A contract states
// each section as measured or as its explicit not-measurable state, from the mapper tests and
// docs/dash/telemetry-inventory.md, never from what the screen shows.
import { mkdirSync, rmSync, writeFileSync } from 'node:fs'
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
  build: (db: DatabaseSync, plan: SessionPlan, baseTimeMs: number, homeDir: string) => void,
  relativePath: string,
): FileHarnessFixture[] {
  return plansFor(planHarness).map((plan) => ({
    contract: contractFor(plan),
    build: (homeDir: string, baseTimeMs: number) => {
      withDatabase(homeDir, relativePath, (db) => build(db, plan, baseTimeMs, homeDir))
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

/** Literal per-turn values the Kilo fixture reports; the JSONL fixtures report the same numbers per turn. */
const KILO_TURN_TOKENS = { input: 101, output: 41, reasoning: 7, cacheRead: 203, cacheWrite: 10 } as const
const KILO_TOOL_COMMAND = 'ls'
const KILO_TOOL_OUTPUT = 'file.txt\n'
const KILO_DIRECTORY = '/workspace/repo'
/** The child session hangs under the first root (the 12-turn plan). */
const KILO_FIRST_ROOT_ID = plansFor('kilo-shared-runtime')[0].id
const KILO_CHILD_SUFFIX = 'child'

/** Writes one user prompt and one assistant reply, with the tool call when `withTool` is set. */
function insertKiloExchange(
  db: DatabaseSync,
  sessionId: string,
  turn: number,
  at: number,
  withTool: boolean,
  tokens: { readonly input: number; readonly output: number; readonly reasoning: number; readonly cacheRead: number; readonly cacheWrite: number } | null,
): void {
  const insertMessage = db.prepare('INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES (?, ?, ?, ?, ?)')
  const insertPart = db.prepare(
    'INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES (?, ?, ?, ?, ?, ?)',
  )
  const userId = `${sessionId}-u-${turn}`
  const assistantId = `${sessionId}-a-${turn}`
  insertMessage.run(userId, sessionId, at, at, JSON.stringify({ role: 'user' }))
  insertPart.run(
    `${userId}-text`,
    userId,
    sessionId,
    at,
    at,
    JSON.stringify({ type: 'text', text: `Step ${turn}: continue the work` }),
  )
  const assistantData = tokens === null
    ? { role: 'assistant', modelID: KILO_MODEL }
    : {
        role: 'assistant',
        modelID: KILO_MODEL,
        tokens: {
          input: tokens.input,
          output: tokens.output,
          reasoning: tokens.reasoning,
          cache: { read: tokens.cacheRead, write: tokens.cacheWrite },
        },
      }
  insertMessage.run(assistantId, sessionId, at + 1_000, at + 1_000, JSON.stringify(assistantData))
  insertPart.run(
    `${assistantId}-text`,
    assistantId,
    sessionId,
    at + 1_000,
    at + 1_000,
    JSON.stringify({ type: 'text', text: `Finished step ${turn}.` }),
  )
  if (withTool) {
    insertPart.run(
      `${assistantId}-tool`,
      assistantId,
      sessionId,
      at + 1_000,
      at + 1_000,
      JSON.stringify({
        type: 'tool',
        callID: `${sessionId}-call-${turn}`,
        tool: 'bash',
        state: { status: 'completed', input: { command: KILO_TOOL_COMMAND }, output: KILO_TOOL_OUTPUT },
      }),
    )
  }
}

function buildKilo(db: DatabaseSync, plan: SessionPlan, baseTimeMs: number): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS session (
      id TEXT PRIMARY KEY, parent_id TEXT, directory TEXT, title TEXT,
      time_created INTEGER, time_updated INTEGER, time_archived INTEGER
    );
    CREATE TABLE IF NOT EXISTS message (
      id TEXT PRIMARY KEY, session_id TEXT NOT NULL, time_created INTEGER, time_updated INTEGER, data TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS part (
      id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT, time_created INTEGER, time_updated INTEGER,
      data TEXT NOT NULL
    );
  `)
  const endMs = baseTimeMs + plan.turns * TURN_SPACING_MS
  db.prepare(
    'INSERT INTO session (id, parent_id, directory, title, time_created, time_updated, time_archived) VALUES (?, NULL, ?, ?, ?, ?, NULL)',
  ).run(plan.id, KILO_DIRECTORY, `Kilo file import ${plan.id}`, baseTimeMs, endMs)
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    insertKiloExchange(db, plan.id, turn, baseTimeMs + turn * TURN_SPACING_MS, hasTool(turn), KILO_TURN_TOKENS)
  }
  if (plan.id === KILO_FIRST_ROOT_ID) {
    const childId = `${plan.id}-${KILO_CHILD_SUFFIX}`
    const childAt = baseTimeMs + TURN_SPACING_MS
    db.prepare(
      'INSERT INTO session (id, parent_id, directory, title, time_created, time_updated, time_archived) VALUES (?, ?, ?, ?, ?, ?, NULL)',
    ).run(childId, plan.id, KILO_DIRECTORY, `Kilo child session ${plan.id}`, childAt, childAt + 1_000)
    insertKiloExchange(db, childId, 1, childAt, false, null)
  }
}

// --- devin: ~/.local/share/devin/cli/sessions.db ----------------------------------------------
// The real sessions.db schema: `sessions` (one row per session) and `message_nodes` (one row per chat
// message, its `chat_message` column a JSON object with `role`, `content`, `metadata` and, on an
// assistant node, `tool_calls`). DevinSqliteConversationSource exports both as the document DevinMapper
// maps: user nodes are the turns, assistant nodes carry `metadata.metrics`, tool and system nodes emit
// no step. The sessions carry their own ids, apart from the transcript ones.
const DEVIN_SESSIONS_PATH = '.local/share/devin/cli/sessions.db'
const DEVIN_MODEL = 'claude-sonnet-4-6'

function buildDevinSessions(db: DatabaseSync, plan: SessionPlan, baseTimeMs: number): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS sessions (
      id TEXT PRIMARY KEY, working_directory TEXT, model TEXT, title TEXT, created_at TEXT,
      last_activity_at TEXT, hidden INTEGER, metadata TEXT
    );
    CREATE TABLE IF NOT EXISTS message_nodes (
      row_id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, node_id TEXT NOT NULL, parent_node_id TEXT,
      chat_message TEXT NOT NULL, created_at TEXT, metadata TEXT, UNIQUE(session_id, node_id)
    );
  `)
  db.prepare(
    'INSERT INTO sessions (id, working_directory, model, title, created_at, last_activity_at, hidden, metadata) VALUES (?, ?, ?, ?, ?, ?, 0, NULL)',
  ).run(
    plan.id,
    '/workspace/repo',
    DEVIN_MODEL,
    `Devin sqlite import ${plan.id}`,
    iso(baseTimeMs),
    iso(baseTimeMs + plan.turns * TURN_SPACING_MS),
  )
  const insertNode = db.prepare(
    'INSERT INTO message_nodes (session_id, node_id, parent_node_id, chat_message, created_at, metadata) VALUES (?, ?, ?, ?, ?, NULL)',
  )
  let nodeNumber = 0
  let parentId: string | null = null
  const addNode = (chatMessage: Record<string, unknown>, at: number): void => {
    nodeNumber += 1
    const nodeId = `${plan.id}-node-${nodeNumber}`
    insertNode.run(plan.id, nodeId, parentId, JSON.stringify(chatMessage), iso(at))
    parentId = nodeId
  }
  addNode({ role: 'system', content: 'You are Devin, a software engineering agent.' }, baseTimeMs)
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    addNode(
      { role: 'user', content: `Step ${turn}: continue the work`, metadata: { is_user_input: true, created_at: iso(at) } },
      at,
    )
    const toolCalls = hasTool(turn)
      ? [{ id: `${plan.id}-tc-${turn}`, name: 'read_file', arguments: { path: `src/file-${turn}.ts` } }]
      : undefined
    addNode(
      {
        role: 'assistant',
        content: `Step ${turn} done`,
        metadata: {
          generation_model: DEVIN_MODEL,
          created_at: iso(at + 1_000),
          metrics: {
            input_tokens: 100 + turn,
            output_tokens: 20 + turn,
            cache_read_tokens: 5,
            cache_creation_tokens: 10,
          },
        },
        ...(toolCalls ? { tool_calls: toolCalls } : {}),
      },
      at + 1_000,
    )
    if (toolCalls) {
      addNode({ role: 'tool', content: `contents of src/file-${turn}.ts`, tool_call_id: toolCalls[0].id }, at + 2_000)
    }
  }
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
    CREATE TABLE IF NOT EXISTS blocks (block_id TEXT PRIMARY KEY, start_ts TEXT, stylized_command TEXT, ai_metadata TEXT);
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
  const insertBlock = db.prepare('INSERT INTO blocks (block_id, start_ts, stylized_command, ai_metadata) VALUES (?, ?, ?, ?)')
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
      insertBlock.run(
        `${plan.id}-block-${turn}`,
        warpTimestamp(at + 2_000),
        `npm test -- step-${turn}`,
        JSON.stringify({ conversation_id: plan.id }),
      )
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

/** The one fixed hash-named workspace folder; its state.vscdb names the first composer and its workspace.json the project. */
const CURSOR_WORKSPACE_HASH = '5b7c1f0e2a9d4c3b8e6f1a0d9c2b7e4f'
const CURSOR_WORKSPACE_PATH = platformPath({
  darwin: `Library/Application Support/Cursor/User/workspaceStorage/${CURSOR_WORKSPACE_HASH}/state.vscdb`,
  win32: `AppData/Roaming/Cursor/User/workspaceStorage/${CURSOR_WORKSPACE_HASH}/state.vscdb`,
  other: `.config/Cursor/User/workspaceStorage/${CURSOR_WORKSPACE_HASH}/state.vscdb`,
})
const CURSOR_PROJECT_FOLDER = '/workspace/repo'

/** A deterministic UUID-shaped bubble id for one session, turn and role. */
function bubbleUuid(sessionOrdinal: number, turn: number, role: 1 | 2): string {
  const hex = (value: number, width: number) => value.toString(16).padStart(width, '0')
  return `${hex(sessionOrdinal, 8)}-${hex(turn, 4)}-4000-8000-${hex(role, 12)}`
}

/** Writes the workspace folder that names the first composer and the project folder (once, for the first session). */
function buildCursorWorkspace(homeDir: string, firstComposerId: string): void {
  withDatabase(homeDir, CURSOR_WORKSPACE_PATH, (db) => {
    db.exec('CREATE TABLE IF NOT EXISTS ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)')
    db.prepare('INSERT INTO ItemTable (key, value) VALUES (?, ?)').run(
      'composer.composerData',
      JSON.stringify({ allComposers: [{ composerId: firstComposerId }] }),
    )
  })
  writeFileSync(
    join(dirname(join(homeDir, CURSOR_WORKSPACE_PATH)), 'workspace.json'),
    JSON.stringify({ folder: `file://${CURSOR_PROJECT_FOLDER}` }),
  )
}

function buildCursor(db: DatabaseSync, plan: SessionPlan, baseTimeMs: number, homeDir: string): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
    CREATE TABLE IF NOT EXISTS cursorDiskKV (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
  `)
  const insert = db.prepare('INSERT INTO cursorDiskKV (key, value) VALUES (?, ?)')
  const firstComposerId = plansFor('cursor')[0].id
  insert.run(`composerData:${plan.id}`, JSON.stringify({ composerId: plan.id, createdAt: iso(baseTimeMs) }))
  if (plan.id === firstComposerId) {
    buildCursorWorkspace(homeDir, firstComposerId)
  }
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
      // kilo.db stores the message and tool text parts, which the context chart measures; it stores neither
      // the runtime system prompt nor tool-definition schemas, so schemaMeasurable stays off.
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      // The published rate table prices only the claude and codex families (CanonPricing.IsPublishedTableHarness),
      // so a Kilo session that reports tokens is out of scope for published pricing, as kimi-code is.
      expectedCostStatus: 'out_of_scope',
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
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
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
      // Warp reports one total per conversation and model (TokenClass.Total), never per exchange (decision D15)
      // and never split into input and output, so the Total Input and Total Output cards stay unmeasured.
      tokensMeasurable: false,
      perTurnTokensMeasurable: false,
      // Warp reports no cost of its own and only a per-model total, which ReportedSessionUsage.Price cannot
      // price from the published table, so the panel states no published rate.
      expectedCostStatus: 'no_rate',
      // WarpMapper.NormalizeModel maps the display name Warp stores to the canonical model id.
      expectedModels: ['claude-sonnet-4-5'],
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
      // The published rate table prices only the claude and codex families (CanonPricing.IsPublishedTableHarness),
      // and the dashboard row shows this session as out of scope for published pricing, as kilo does.
      expectedCostStatus: 'out_of_scope',
      expectedModels: [CURSOR_MODEL],
    }),
    buildCursor,
    CURSOR_PATH,
  ),
]

// ── Unreadable SQLite fixtures ──────────────────────────────────────────────
//
// The corrupt/unreadable sources `~/.kyberdash/canon.db` recorded ingest problems for, written into
// a caller-supplied isolated home so the parity suite can assert the honest diagnostics without
// overwriting a healthy database. Each path reuses the same platform helper the healthy fixtures do.

/** One unreadable source: the problem code it must raise and the harness id it is recorded under. */
export type UnreadableSqliteSource = {
  readonly harness: 'cursor' | 'devin' | 'warp' | 'zcode'
  readonly code: 'CURSOR_WORKSPACE_UNREADABLE' | 'DEVIN_SESSION_UNREADABLE' | 'SQLITE_SOURCE_UNREADABLE' | 'SOURCE_FORMAT_UNEVIDENCED'
  /** Relative path under the isolated home the corrupt source is written at. */
  readonly relativePath: string
}

/** A non-SQLite file body: SQLite refuses it with "file is not a database". */
const CORRUPT_DATABASE_BYTES = 'kyber-parity-corrupt-database-body'

/** The fixed workspace hash the corrupt Cursor workspace folder sits under. */
const CURSOR_UNREADABLE_WORKSPACE_HASH = 'deadbeefdeadbeefdeadbeefdeadbeef'

/**
 * Writes the corrupt/unreadable SQLite sources the parity suite's second isolated server asserts:
 * a readable Cursor global `state.vscdb` beside a corrupt workspace `state.vscdb`
 * (`CURSOR_WORKSPACE_UNREADABLE`), a Devin `sessions.db` whose `last_activity_at` does not parse
 * (`DEVIN_SESSION_UNREADABLE`, with no transcript JSON beside it), a corrupt `warp.sqlite`
 * (`SQLITE_SOURCE_UNREADABLE`), and a `zcode` `db.sqlite` the registry cannot evidence
 * (`SOURCE_FORMAT_UNEVIDENCED`).
 *
 * @param homeDir The isolated home directory of the calling server.
 * @returns The written sources, each naming the problem code it must raise.
 */
export function buildUnreadableSqliteFixtures(homeDir: string): readonly UnreadableSqliteSource[] {
  const cursorWorkspacePath = platformPath({
    darwin: `Library/Application Support/Cursor/User/workspaceStorage/${CURSOR_UNREADABLE_WORKSPACE_HASH}/state.vscdb`,
    win32: `AppData/Roaming/Cursor/User/workspaceStorage/${CURSOR_UNREADABLE_WORKSPACE_HASH}/state.vscdb`,
    other: `.config/Cursor/User/workspaceStorage/${CURSOR_UNREADABLE_WORKSPACE_HASH}/state.vscdb`,
  })

  // The global database opens cleanly but holds no bubble rows, so it lists no composer and
  // fabricates no session; the corrupt workspace database is what raises CURSOR_WORKSPACE_UNREADABLE.
  withDatabase(homeDir, CURSOR_PATH, (db) => {
    db.exec('CREATE TABLE IF NOT EXISTS cursorDiskKV (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)')
  })

  const corruptTargets: readonly string[] = [
    cursorWorkspacePath,
    DEVIN_SESSIONS_PATH,
    WARP_PATH,
    '.zcode/cli/db/db.sqlite',
  ]
  for (const relativePath of corruptTargets) {
    const target = join(homeDir, relativePath)
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(target, CORRUPT_DATABASE_BYTES)
  }

  // The Devin gap is a row whose last_activity_at does not parse, not a corrupt file: the source
  // opens cleanly and refuses the session, recording DEVIN_SESSION_UNREADABLE.
  const devinTarget = join(homeDir, DEVIN_SESSIONS_PATH)
  rmSync(devinTarget, { force: true })
  withDatabase(homeDir, DEVIN_SESSIONS_PATH, (db) => {
    db.exec(`
      CREATE TABLE IF NOT EXISTS sessions (
        id TEXT PRIMARY KEY, working_directory TEXT, model TEXT, title TEXT, created_at TEXT,
        last_activity_at TEXT, hidden INTEGER, metadata TEXT
      );
      CREATE TABLE IF NOT EXISTS message_nodes (
        row_id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, node_id TEXT NOT NULL, parent_node_id TEXT,
        chat_message TEXT NOT NULL, created_at TEXT, metadata TEXT, UNIQUE(session_id, node_id)
      );
    `)
    db.prepare(
      'INSERT INTO sessions (id, working_directory, model, title, created_at, last_activity_at, hidden, metadata) VALUES (?, ?, ?, ?, ?, ?, 0, NULL)',
    ).run('parity-devin-unreadable', '/synthetic/workspace', 'claude-sonnet-4-6', 'Synthetic unreadable Devin session', 'not-a-date', 'not-a-date')
    db.prepare(
      'INSERT INTO message_nodes (session_id, node_id, parent_node_id, chat_message, created_at, metadata) VALUES (?, ?, NULL, ?, ?, NULL)',
    ).run(
      'parity-devin-unreadable',
      'parity-devin-unreadable-node-1',
      JSON.stringify({ role: 'user', content: 'Synthetic unreadable Devin session.' }),
      new Date(0).toISOString(),
    )
  })

  return [
    { harness: 'cursor', code: 'CURSOR_WORKSPACE_UNREADABLE', relativePath: cursorWorkspacePath },
    { harness: 'devin', code: 'DEVIN_SESSION_UNREADABLE', relativePath: DEVIN_SESSIONS_PATH },
    { harness: 'warp', code: 'SQLITE_SOURCE_UNREADABLE', relativePath: WARP_PATH },
    { harness: 'zcode', code: 'SOURCE_FORMAT_UNEVIDENCED', relativePath: '.zcode/cli/db/db.sqlite' },
  ]
}
