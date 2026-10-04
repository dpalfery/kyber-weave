import { mkdtemp, rm } from 'fs/promises'
import { chmodSync, copyFileSync, existsSync, mkdirSync, rmSync, statSync, writeFileSync } from 'fs'
import { join } from 'path'
import { tmpdir } from 'os'
import { createRequire } from 'node:module'

import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { createWarpProvider, drainWarpDbAccessDenials } from './warp.js'
import { isSqliteAvailable, openDatabase } from '../ingest/sqlite.js'
import type { ParsedProviderCall } from './types.js'

const requireForTest = createRequire(import.meta.url)

type TestDb = {
  exec(sql: string): void
  prepare(sql: string): { run(...params: unknown[]): void }
  close(): void
}

type QueryFixture = {
  exchangeId: string
  conversationId: string
  startTs: string
  input: string
  outputStatus?: string
  modelId?: string
  workingDirectory?: string | null
}

type BlockFixture = {
  blockId: string
  conversationId: string
  startTs: string
  completedTs: string
  exitCode: number
  command: string
}

let tmpDir: string

beforeEach(async () => {
  tmpDir = await mkdtemp(join(tmpdir(), 'warp-provider-test-'))
})

afterEach(async () => {
  await rm(tmpDir, { recursive: true, force: true })
})

function createWarpSchema(db: TestDb): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS agent_conversations (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      conversation_id TEXT NOT NULL,
      conversation_data TEXT NOT NULL,
      last_modified_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
    )
  `)
  db.exec(`
    CREATE TABLE IF NOT EXISTS ai_queries (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      exchange_id TEXT NOT NULL,
      conversation_id TEXT NOT NULL,
      start_ts DATETIME NOT NULL,
      input TEXT NOT NULL,
      working_directory TEXT,
      output_status TEXT NOT NULL,
      model_id TEXT NOT NULL DEFAULT '',
      planning_model_id TEXT NOT NULL DEFAULT '',
      coding_model_id TEXT NOT NULL DEFAULT ''
    )
  `)
  db.exec(`
    CREATE TABLE IF NOT EXISTS blocks (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      pane_leaf_uuid BLOB NOT NULL,
      stylized_command BLOB NOT NULL,
      stylized_output BLOB NOT NULL,
      pwd TEXT,
      git_branch TEXT,
      virtual_env TEXT,
      conda_env TEXT,
      exit_code INTEGER NOT NULL,
      did_execute BOOLEAN NOT NULL,
      completed_ts DATETIME,
      start_ts DATETIME,
      ps1 TEXT,
      honor_ps1 BOOLEAN NOT NULL DEFAULT 0,
      shell TEXT,
      user TEXT,
      host TEXT,
      is_background BOOLEAN NOT NULL DEFAULT 0,
      rprompt TEXT,
      prompt_snapshot TEXT,
      block_id TEXT NOT NULL DEFAULT '',
      ai_metadata TEXT,
      is_local BOOLEAN,
      agent_view_visibility TEXT,
      git_branch_name TEXT
    )
  `)
}

function createWarpDb(dir: string): string {
  mkdirSync(dir, { recursive: true })
  const dbPath = join(dir, 'warp.sqlite')
  const { DatabaseSync: Database } = requireForTest('node:sqlite')
  const db = new Database(dbPath)
  createWarpSchema(db)
  db.close()
  return dbPath
}

/**
 * Readable Warp db with a non-empty -wal and no -shm so openDatabase must take
 * the read-only cache-copy path (same shape as sqlite-readonly-parent fixtures).
 */
function createUncheckpointedWalWarpDb(dir: string): string {
  mkdirSync(dir, { recursive: true })
  const originDir = join(dir, 'origin')
  mkdirSync(originDir)
  const originPath = join(originDir, 'warp.sqlite')
  const dbPath = join(dir, 'warp.sqlite')
  const { DatabaseSync: Database } = requireForTest('node:sqlite')
  const writer = new Database(originPath)
  writer.exec('PRAGMA journal_mode=WAL')
  createWarpSchema(writer)
  writer.exec('PRAGMA wal_checkpoint(TRUNCATE)')
  writer.exec('PRAGMA wal_autocheckpoint=0')
  writer.prepare(
    'INSERT INTO agent_conversations (conversation_id, conversation_data, last_modified_at) VALUES (?, ?, ?)',
  ).run('conv-ro', '{}', '2026-05-18 10:00:00')
  copyFileSync(originPath, dbPath)
  copyFileSync(originPath + '-wal', dbPath + '-wal')
  writer.close()
  expect(statSync(dbPath + '-wal').size).toBeGreaterThan(0)
  expect(existsSync(dbPath + '-shm')).toBe(false)
  return dbPath
}

/** Makes `dir` non-writable, or skips when chmod bits do not bind this process. */
function makeReadOnly(dir: string, skip: (reason?: string) => void): boolean {
  chmodSync(dir, 0o555)
  const probe = join(dir, '.write-probe')
  try {
    writeFileSync(probe, '')
  } catch {
    return true
  }
  rmSync(probe)
  chmodSync(dir, 0o755)
  skip(`SKIP: chmod 0555 did not make ${dir} non-writable for this process`)
  return false
}

function withTestDb(dbPath: string, fn: (db: TestDb) => void): void {
  const { DatabaseSync: Database } = requireForTest('node:sqlite')
  const db = new Database(dbPath)
  try {
    fn(db)
  } finally {
    db.close()
  }
}

function insertConversation(
  db: TestDb,
  conversationId: string,
  conversationData: unknown,
  lastModifiedAt = '2026-05-18 10:10:00',
): void {
  db.prepare(
    'INSERT INTO agent_conversations (conversation_id, conversation_data, last_modified_at) VALUES (?, ?, ?)',
  ).run(conversationId, JSON.stringify(conversationData), lastModifiedAt)
}

function insertQuery(db: TestDb, q: QueryFixture): void {
  db.prepare(
    `INSERT INTO ai_queries (
      exchange_id, conversation_id, start_ts, input, working_directory, output_status, model_id, planning_model_id, coding_model_id
    ) VALUES (?, ?, ?, ?, ?, ?, ?, '', '')`,
  ).run(
    q.exchangeId,
    q.conversationId,
    q.startTs,
    q.input,
    q.workingDirectory ?? null,
    q.outputStatus ?? '"Completed"',
    q.modelId ?? 'auto-efficient',
  )
}

function insertBlock(db: TestDb, b: BlockFixture): void {
  db.prepare(
    `INSERT INTO blocks (
      pane_leaf_uuid, stylized_command, stylized_output, exit_code, did_execute,
      completed_ts, start_ts, block_id, ai_metadata
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).run(
    Buffer.from([0]),
    b.command,
    '',
    b.exitCode,
    1,
    b.completedTs,
    b.startTs,
    b.blockId,
    JSON.stringify({
      requested_command_action_id: `call-${b.blockId}`,
      conversation_id: b.conversationId,
    }),
  )
}

async function collectCalls(
  dbPath: string,
  conversationId: string,
  seenKeys = new Set<string>(),
): Promise<ParsedProviderCall[]> {
  const provider = createWarpProvider(dbPath)
  const source = {
    path: `${dbPath}:${conversationId}`,
    project: 'warp',
    provider: 'warp',
  }
  const calls: ParsedProviderCall[] = []
  for await (const call of provider.createSessionParser(source, seenKeys).parse()) {
    calls.push(call)
  }
  return calls
}

const skipUnlessSqlite = isSqliteAvailable() ? describe : describe.skip

skipUnlessSqlite('warp provider', () => {
  it('discovers sessions and sanitizes project names from working_directory', async () => {
    const dbPath = createWarpDb(tmpDir)
    withTestDb(dbPath, (db) => {
      insertConversation(db, 'conv-1', { conversation_usage_metadata: { token_usage: [] } })
      insertQuery(db, {
        exchangeId: 'ex-1',
        conversationId: 'conv-1',
        startTs: '2026-05-18 10:00:00.000000',
        input: '[]',
        workingDirectory: '/Users/test/project-a',
      })
    })

    const provider = createWarpProvider(dbPath)
    const sessions = await provider.discoverSessions()
    expect(sessions).toHaveLength(1)
    expect(sessions[0]!.provider).toBe('warp')
    expect(sessions[0]!.path).toBe(`${dbPath}:conv-1`)
    expect(sessions[0]!.project).toBe('Users-test-project-a')
  })

  it('parses one call per completed exchange and estimates tokens from primary-agent totals', async () => {
    const dbPath = createWarpDb(tmpDir)
    withTestDb(dbPath, (db) => {
      insertConversation(db, 'conv-1', {
        conversation_usage_metadata: {
          token_usage: [
            {
              model_id: 'GPT-5.3 Codex (medium reasoning)',
              warp_tokens: 300,
              byok_tokens: 0,
              warp_token_usage_by_category: { primary_agent: 300 },
              byok_token_usage_by_category: {},
            },
            {
              model_id: 'Claude Haiku 4.5',
              warp_tokens: 90,
              byok_tokens: 0,
              warp_token_usage_by_category: { full_terminal_use: 90 },
              byok_token_usage_by_category: {},
            },
          ],
        },
      })
      insertQuery(db, {
        exchangeId: 'ex-1',
        conversationId: 'conv-1',
        startTs: '2026-05-18 10:00:00.000000',
        input: JSON.stringify([{ Query: { text: 'short prompt' } }]),
        modelId: 'auto-efficient',
        workingDirectory: '/Users/test/project-a',
      })
      insertQuery(db, {
        exchangeId: 'ex-2',
        conversationId: 'conv-1',
        startTs: '2026-05-18 10:03:00.000000',
        input: JSON.stringify([{ Query: { text: 'longer prompt with substantially more detail for weighting' } }]),
        modelId: 'auto-efficient',
        workingDirectory: '/Users/test/project-a',
      })
    })

    const calls = await collectCalls(dbPath, 'conv-1')
    expect(calls).toHaveLength(2)
    expect(calls.map(call => call.deduplicationKey)).toEqual([
      'warp:conv-1:ex-1',
      'warp:conv-1:ex-2',
    ])
    expect(calls.map(call => call.userMessage)).toEqual([
      'short prompt',
      'longer prompt with substantially more detail for weighting',
    ])
    expect(calls.every(call => call.model === 'gpt-5.3-codex')).toBe(true)
    expect(calls.every(call => call.costIsEstimated === true)).toBe(true)
    expect(calls.reduce((sum, call) => sum + call.inputTokens, 0)).toBe(300)
    expect(calls[1]!.inputTokens).toBeGreaterThan(calls[0]!.inputTokens)
    expect(calls[0]!.projectPath).toBe('/Users/test/project-a')
    expect(calls[0]!.project).toBe('Users-test-project-a')
  })

  it('attributes command blocks to the nearest preceding exchange and extracts Bash commands', async () => {
    const dbPath = createWarpDb(tmpDir)
    withTestDb(dbPath, (db) => {
      insertConversation(db, 'conv-2', {
        conversation_usage_metadata: {
          token_usage: [
            {
              model_id: 'GPT-5.3 Codex (medium reasoning)',
              warp_tokens: 120,
              byok_tokens: 0,
              warp_token_usage_by_category: { primary_agent: 120 },
              byok_token_usage_by_category: {},
            },
          ],
        },
      })
      insertQuery(db, {
        exchangeId: 'ex-a',
        conversationId: 'conv-2',
        startTs: '2026-05-18 11:00:00.000000',
        input: JSON.stringify([{ Query: { text: 'run tests' } }]),
      })
      insertQuery(db, {
        exchangeId: 'ex-b',
        conversationId: 'conv-2',
        startTs: '2026-05-18 11:05:00.000000',
        input: JSON.stringify([{ Query: { text: 'summarize results' } }]),
      })
      insertBlock(db, {
        blockId: 'block-1',
        conversationId: 'conv-2',
        startTs: '2026-05-18 11:01:00.000000',
        completedTs: '2026-05-18 11:01:04.000000',
        exitCode: 0,
        command: 'npm test && git status',
      })
    })

    const calls = await collectCalls(dbPath, 'conv-2')
    expect(calls).toHaveLength(2)
    expect(calls[0]!.tools).toEqual(['Bash'])
    expect(calls[0]!.bashCommands).toEqual(['npm', 'git'])
    expect(calls[1]!.tools).toEqual([])
    expect(calls[1]!.bashCommands).toEqual([])
  })

  it('exposes open EACCES as a drainable access denial for doctor (#197)', async ({ skip }) => {
    // chmod bit clearing is a no-op on Windows; the doctor suite covers the
    // injected-error path. Here we pin the real discoverFromDb → drain wire.
    if (process.platform === 'win32') skip()
    if (!isSqliteAvailable()) skip()

    drainWarpDbAccessDenials()
    const dbPath = createWarpDb(tmpDir)
    chmodSync(dbPath, 0o000)
    try {
      const provider = createWarpProvider(dbPath)
      expect(await provider.discoverSessions()).toEqual([])
      expect(drainWarpDbAccessDenials()).toEqual([dbPath])
    } finally {
      chmodSync(dbPath, 0o644)
    }
  })

  it('does not record an unwritable cache as a Warp access denial (#197)', async ({ skip }) => {
    // Readable source + cache-dir EACCES must not noteWarpDbAccessDenial — that
    // path is a kyberdash cache failure, not a TCC denial of the Warp db.
    // On Node builds where the sidecar failure surfaces at query time,
    // discoverFromDb's validateSchema catch swallows the throw before
    // noteWarpDbAccessDenial; drain must still stay empty (no FDA false positive).
    if (process.platform === 'win32') skip()
    if (!isSqliteAvailable()) skip()

    const sourceDir = join(tmpDir, 'source')
    const cacheDir = join(tmpDir, 'cache')
    mkdirSync(cacheDir, { recursive: true })
    const dbPath = createUncheckpointedWalWarpDb(sourceDir)
    if (!makeReadOnly(sourceDir, skip)) return

    const previousCacheDir = process.env['KYBERDASH_CACHE_DIR']
    process.env['KYBERDASH_CACHE_DIR'] = cacheDir
    chmodSync(cacheDir, 0o000)
    drainWarpDbAccessDenials()
    try {
      // Pin the misattribution at the sqlite boundary: cache-dir EACCES must not
      // surface as a permission errno (the signal noteWarpDbAccessDenial keys on).
      let thrown: unknown
      try {
        const db = openDatabase(dbPath)
        try {
          db.query('SELECT 1 AS ok FROM agent_conversations LIMIT 1')
        } catch (err) {
          thrown = err
        }
        try { db.close() } catch { /* query-time fallback may have closed it */ }
      } catch (err) {
        thrown = err
      }
      expect(thrown).toBeDefined()
      const code = (thrown as NodeJS.ErrnoException | undefined)?.code
      expect(code === 'EACCES' || code === 'EPERM').toBe(false)

      const provider = createWarpProvider(dbPath)
      // Query-time cache failure can leave the wrapper's db already closed; the
      // discover finally may throw "database is not open". Sessions are still
      // empty and — critically — must not be filed as a Warp access denial.
      const sessions = await provider.discoverSessions().catch(() => [])
      expect(sessions).toEqual([])
      expect(drainWarpDbAccessDenials()).toEqual([])
    } finally {
      chmodSync(cacheDir, 0o755)
      chmodSync(sourceDir, 0o755)
      if (previousCacheDir === undefined) delete process.env['KYBERDASH_CACHE_DIR']
      else process.env['KYBERDASH_CACHE_DIR'] = previousCacheDir
      drainWarpDbAccessDenials()
    }
  })

  it('skips pending or invalid exchanges and does not poison seenKeys for skipped rows', async () => {
    const dbPath = createWarpDb(tmpDir)
    withTestDb(dbPath, (db) => {
      insertConversation(db, 'conv-3', {
        conversation_usage_metadata: {
          token_usage: [
            {
              model_id: 'GPT-5.3 Codex (medium reasoning)',
              warp_tokens: 42,
              byok_tokens: 0,
              warp_token_usage_by_category: { primary_agent: 42 },
              byok_token_usage_by_category: {},
            },
          ],
        },
      })
      insertQuery(db, {
        exchangeId: 'ex-skip-seen',
        conversationId: 'conv-3',
        startTs: '2026-05-18 12:00:00.000000',
        input: JSON.stringify([{ Query: { text: 'already seen' } }]),
      })
      insertQuery(db, {
        exchangeId: 'ex-pending',
        conversationId: 'conv-3',
        startTs: '2026-05-18 12:01:00.000000',
        input: JSON.stringify([{ Query: { text: 'still running' } }]),
        outputStatus: '"Pending"',
      })
      insertQuery(db, {
        exchangeId: 'ex-invalid-ts',
        conversationId: 'conv-3',
        startTs: 'not-a-timestamp',
        input: JSON.stringify([{ Query: { text: 'bad timestamp' } }]),
      })
    })

    const seen = new Set<string>(['warp:conv-3:ex-skip-seen'])
    const calls = await collectCalls(dbPath, 'conv-3', seen)
    expect(calls).toEqual([])
    expect(seen.has('warp:conv-3:ex-invalid-ts')).toBe(false)
    expect(seen.has('warp:conv-3:ex-pending')).toBe(false)
  })
})
