import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mkdir, rm, writeFile } from 'fs/promises'
import { join } from 'path'
import { createRequire } from 'node:module'

import { clearSessionCache, parseAllSessions } from '../src/parser.js'
import { isSqliteAvailable } from '../src/sqlite.js'

// The exported Hermes provider resolves HERMES_HOME when its singleton is
// created, at import time. Point it at the fixture during module hoisting so the
// pipeline can never read the developer's real ~/.hermes.
const testRoot = vi.hoisted(() => {
  const root = `${process.env['TMPDIR'] || '/tmp'}/hermes-pr-pipeline-${process.pid}-${Date.now()}`
  process.env['HERMES_HOME'] = `${root}/hermes`
  return root
})

const HERMES_HOME = join(testRoot, 'hermes')
const CACHE_DIR = join(testRoot, 'cache')
const PR_URL = 'https://github.com/getagentseal/codeburn/pull/1039'
const requireForTest = createRequire(import.meta.url)

function seedDb(repoRoot: string): void {
  const { DatabaseSync: Database } = requireForTest('node:sqlite')
  const db = new Database(join(HERMES_HOME, 'state.db'))
  db.exec(`
    CREATE TABLE sessions (
      id TEXT PRIMARY KEY, source TEXT, model TEXT, cwd TEXT, git_repo_root TEXT,
      input_tokens INTEGER DEFAULT 0, output_tokens INTEGER DEFAULT 0,
      cache_read_tokens INTEGER DEFAULT 0, cache_write_tokens INTEGER DEFAULT 0,
      reasoning_tokens INTEGER DEFAULT 0, estimated_cost_usd REAL, actual_cost_usd REAL,
      api_call_count INTEGER DEFAULT 0, tool_call_count INTEGER DEFAULT 0,
      started_at REAL, title TEXT
    )
  `)
  db.exec(`
    CREATE TABLE messages (
      id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, role TEXT NOT NULL,
      content TEXT, tool_calls TEXT, timestamp REAL NOT NULL
    )
  `)
  const startedAt = Date.now() / 1000 - 3600
  db.prepare(
    `INSERT INTO sessions (id, source, model, git_repo_root, input_tokens, output_tokens,
      estimated_cost_usd, api_call_count, started_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).run('pr-pipeline', 'cli', 'claude-opus-4-6', repoRoot, 1000, 200, 0.5, 1, startedAt)
  db.prepare('INSERT INTO messages (session_id, role, content, timestamp) VALUES (?, ?, ?, ?)')
    .run('pr-pipeline', 'user', 'ship the router fix', startedAt)
  db.prepare('INSERT INTO messages (session_id, role, content, timestamp) VALUES (?, ?, ?, ?)')
    .run('pr-pipeline', 'assistant', `Opened ${PR_URL} for review`, startedAt + 1)
  db.close()
}

beforeEach(async () => {
  clearSessionCache()
  await rm(testRoot, { recursive: true, force: true })
  await mkdir(HERMES_HOME, { recursive: true })
  process.env['HERMES_HOME'] = HERMES_HOME
  process.env['CODEBURN_CACHE_DIR'] = CACHE_DIR
})

afterEach(async () => {
  clearSessionCache()
  await rm(testRoot, { recursive: true, force: true })
})

const skipUnlessSqlite = isSqliteAvailable() ? describe : describe.skip

skipUnlessSqlite('hermes PR links through the parse pipeline', () => {
  it('carries a transcript PR URL into the session summary', async () => {
    const repoRoot = join(testRoot, 'codeburn')
    await mkdir(join(repoRoot, '.git'), { recursive: true })
    await writeFile(
      join(repoRoot, '.git', 'config'),
      '[remote "origin"]\n\turl = https://github.com/getagentseal/codeburn.git\n',
    )
    seedDb(repoRoot)

    const projects = await parseAllSessions(undefined, 'hermes')
    const sessions = projects.flatMap(project => project.sessions)
    expect(sessions).toHaveLength(1)
    expect(sessions[0]!.prLinks).toEqual([PR_URL])
    expect(sessions[0]!.prAttributionSource).toBe('transcript')
  })
})
