import { chmodSync, copyFileSync, existsSync, mkdirSync, readdirSync, statSync, utimesSync, writeFileSync } from 'node:fs'
import { mkdtemp, rm } from 'node:fs/promises'
import { createRequire } from 'node:module'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  isSqliteReadonlyError,
  openDatabase,
  sqliteSupportsUriFilenames,
} from '../src/sqlite.js'
import {
  discoverSqliteSessions,
  type SqliteProviderConfig,
} from '../src/providers/sqlite-session-parser.js'

const requireForTest = createRequire(import.meta.url)

type NativeDatabase = {
  exec(sql: string): void
  prepare(sql: string): { run(...params: unknown[]): void; all(...params: unknown[]): unknown[] }
  close(): void
}

type NativeDatabaseCtor = new (path: string) => NativeDatabase

const { DatabaseSync: NativeDatabase } = requireForTest('node:sqlite') as {
  DatabaseSync: NativeDatabaseCtor
}

let sourceRoot: string
let cacheRoot: string
let previousCacheDir: string | undefined
const openWriters: NativeDatabase[] = []

beforeEach(async () => {
  sourceRoot = await mkdtemp(join(tmpdir(), 'codeburn-sqlite-source-'))
  cacheRoot = await mkdtemp(join(tmpdir(), 'codeburn-sqlite-cache-'))
  previousCacheDir = process.env['CODEBURN_CACHE_DIR']
  process.env['CODEBURN_CACHE_DIR'] = cacheRoot
})

afterEach(async () => {
  chmodSync(sourceRoot, 0o755)
  chmodSync(cacheRoot, 0o755)
  for (const writer of openWriters.splice(0)) writer.close()
  await rm(sourceRoot, { recursive: true, force: true })
  await rm(cacheRoot, { recursive: true, force: true })
  if (previousCacheDir === undefined) delete process.env['CODEBURN_CACHE_DIR']
  else process.env['CODEBURN_CACHE_DIR'] = previousCacheDir
})

function createClosedWalDatabase(dbPath: string): void {
  const db = new NativeDatabase(dbPath)
  db.exec('PRAGMA journal_mode=WAL')
  db.exec('CREATE TABLE values_table (c INTEGER)')
  db.prepare('INSERT INTO values_table (c) VALUES (?)').run(1)
  db.close()
}

function createOpenWalDatabase(dbPath: string): NativeDatabase {
  const db = new NativeDatabase(dbPath)
  db.exec('PRAGMA journal_mode=WAL')
  db.exec('CREATE TABLE values_table (c INTEGER)')
  db.prepare('INSERT INTO values_table (c) VALUES (?)').run(1)
  expect(existsSync(dbPath + '-wal')).toBe(true)
  expect(existsSync(dbPath + '-shm')).toBe(true)
  openWriters.push(db)
  return db
}

/// A database plus a non-empty -wal and no -shm: what a snapshot, an rsync or an
/// unclean unmount of a live source leaves behind. The second row exists only in
/// the -wal, so dropping it would be silent data loss rather than an error.
function writeUncheckpointedWalDatabase(dbPath: string): void {
  const originDir = join(sourceRoot, `origin-${readdirSync(sourceRoot).length}`)
  mkdirSync(originDir)
  const originPath = join(originDir, 'state.vscdb')
  const writer = new NativeDatabase(originPath)
  writer.exec('PRAGMA journal_mode=WAL')
  writer.exec('CREATE TABLE values_table (c INTEGER)')
  writer.prepare('INSERT INTO values_table (c) VALUES (?)').run(1)
  writer.exec('PRAGMA wal_checkpoint(TRUNCATE)')
  writer.exec('PRAGMA wal_autocheckpoint=0')
  writer.prepare('INSERT INTO values_table (c) VALUES (?)').run(2)
  copyFileSync(originPath, dbPath)
  copyFileSync(originPath + '-wal', dbPath + '-wal')
  writer.close()
  expect(statSync(dbPath + '-wal').size).toBeGreaterThan(0)
  expect(existsSync(dbPath + '-shm')).toBe(false)
}

function createDiscoveryDatabase(dbPath: string): void {
  const db = new NativeDatabase(dbPath)
  db.exec('PRAGMA journal_mode=WAL')
  db.exec(`
    CREATE TABLE session (
      id TEXT PRIMARY KEY,
      directory TEXT,
      title TEXT,
      time_created INTEGER,
      parent_id TEXT,
      time_archived INTEGER
    )
  `)
  db.exec('CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data BLOB)')
  db.exec('CREATE TABLE part (id INTEGER PRIMARY KEY, message_id TEXT, session_id TEXT, data BLOB)')
  db.prepare(
    'INSERT INTO session (id, directory, title, time_created, parent_id, time_archived) VALUES (?, ?, ?, ?, ?, ?)',
  ).run('session-1', '/tmp/project', 'Read-only fixture', Date.now(), null, null)
  db.close()
}

function makeSourceParentReadOnly(skip: (reason?: string) => void): boolean {
  chmodSync(sourceRoot, 0o555)
  const mode = statSync(sourceRoot).mode & 0o777
  if ((mode & 0o222) !== 0) {
    skip(`SKIP: chmod 0555 did not make the fixture parent non-writable (mode ${mode.toString(8)})`)
    return false
  }
  return true
}

function makeSourceParentWritable(): void {
  chmodSync(sourceRoot, 0o755)
}

function cachedDatabaseFiles(): string[] {
  try {
    return readdirSync(join(cacheRoot, 'sqlite-ro')).filter(name => name.endsWith('.db'))
  } catch {
    return []
  }
}

function readValue(dbPath: string): number {
  const db = openDatabase(dbPath)
  try {
    const rows = db.query<{ c: number }>('SELECT c FROM values_table')
    return rows[0]?.c ?? -1
  } finally {
    db.close()
  }
}

describe('SQLite read-only parent fallback', () => {
  it('keeps the existing writable-parent open behaviour', () => {
    const dbPath = join(sourceRoot, 'state.vscdb')
    createClosedWalDatabase(dbPath)
    expect(existsSync(dbPath + '-wal')).toBe(false)
    expect(existsSync(dbPath + '-shm')).toBe(false)

    expect(readValue(dbPath)).toBe(1)

    expect(existsSync(dbPath + '-wal')).toBe(true)
    expect(existsSync(dbPath + '-shm')).toBe(true)
    expect(cachedDatabaseFiles()).toEqual([])
  })

  it('reads a read-only parent with no -wal, in place where it can and by copy where it cannot', ({ skip }) => {
    const dbPath = join(sourceRoot, 'state.vscdb')
    createClosedWalDatabase(dbPath)
    expect(existsSync(dbPath + '-wal')).toBe(false)
    expect(existsSync(dbPath + '-shm')).toBe(false)
    if (!makeSourceParentReadOnly(skip)) return

    expect(readValue(dbPath)).toBe(1)

    expect(existsSync(dbPath + '-wal')).toBe(false)
    expect(existsSync(dbPath + '-shm')).toBe(false)
    // No WAL frames exist, so there is nothing to go stale and nothing worth
    // copying: immutable reads the source in place. node:sqlite only honours
    // URI filenames on newer builds, and on the 22.13 floor the copy stands in.
    expect(cachedDatabaseFiles()).toHaveLength(sqliteSupportsUriFilenames() ? 0 : 1)
  })

  it('opens directly when a read-only parent already has WAL sidecars', ({ skip }) => {
    const dbPath = join(sourceRoot, 'state.vscdb')
    createOpenWalDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return

    expect(readValue(dbPath)).toBe(1)
    expect(cachedDatabaseFiles()).toEqual([])
  })

  it('reads un-checkpointed WAL rows when the parent is read-only and the -shm is absent', ({ skip }) => {
    // SQLite reports a -wal without its -shm as SQLITE_CANTOPEN, not
    // SQLITE_READONLY, and the un-checkpointed row lives only in the -wal.
    const dbPath = join(sourceRoot, 'state.vscdb')
    writeUncheckpointedWalDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return

    const db = openDatabase(dbPath)
    try {
      expect(db.query<{ c: number }>('SELECT c FROM values_table ORDER BY c')).toEqual([{ c: 1 }, { c: 2 }])
    } finally {
      db.close()
    }
    expect(existsSync(dbPath + '-shm')).toBe(false)
    expect(cachedDatabaseFiles()).toHaveLength(1)
  })

  it('reuses an unchanged fallback copy instead of copying the database again', ({ skip }) => {
    const dbPath = join(sourceRoot, 'state.vscdb')
    writeUncheckpointedWalDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return

    expect(readValue(dbPath)).toBe(1)
    const first = cachedDatabaseFiles()
    expect(first).toHaveLength(1)
    const cachedPath = join(cacheRoot, 'sqlite-ro', first[0]!)
    const firstIno = statSync(cachedPath).ino
    expect(readValue(dbPath)).toBe(1)
    expect(cachedDatabaseFiles()).toEqual(first)
    expect(statSync(cachedPath).ino).toBe(firstIno)
  })

  it('publishes a refreshed copy beside the old one and keeps at most one predecessor', ({ skip }) => {
    const dbPath = join(sourceRoot, 'state.vscdb')
    writeUncheckpointedWalDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return
    expect(readValue(dbPath)).toBe(1)
    const [first] = cachedDatabaseFiles()
    const firstIno = statSync(join(cacheRoot, 'sqlite-ro', first!)).ino

    // A changed source must not overwrite the copy a concurrent reader may still
    // have open: Windows cannot unlink it, and the name is the fingerprint.
    makeSourceParentWritable()
    writeUncheckpointedWalDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return
    expect(readValue(dbPath)).toBe(1)
    const second = cachedDatabaseFiles()
    expect(second).toHaveLength(2)
    expect(second).toContain(first)
    expect(statSync(join(cacheRoot, 'sqlite-ro', first!)).ino).toBe(firstIno)

    makeSourceParentWritable()
    writeUncheckpointedWalDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return
    expect(readValue(dbPath)).toBe(1)
    const third = cachedDatabaseFiles()
    expect(third).toHaveLength(2)
    expect(third).not.toContain(first)
  })

  it('evicts a copy left untouched for a day, including one whose source is gone', ({ skip }) => {
    const dbPath = join(sourceRoot, 'state.vscdb')
    writeUncheckpointedWalDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return
    expect(readValue(dbPath)).toBe(1)
    const cacheDir = join(cacheRoot, 'sqlite-ro')
    const predecessor = join(cacheDir, cachedDatabaseFiles()[0]!)

    // A copy of a database that no longer exists is simply one nothing touches.
    const orphan = join(cacheDir, `${'0'.repeat(32)}.deadbeefdeadbeef.db`)
    writeFileSync(orphan, 'orphan')
    const aDayAndAnHourAgo = new Date(Date.now() - 25 * 60 * 60 * 1000)
    utimesSync(orphan, aDayAndAnHourAgo, aDayAndAnHourAgo)

    makeSourceParentWritable()
    writeUncheckpointedWalDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return
    expect(readValue(dbPath)).toBe(1)
    expect(existsSync(orphan)).toBe(false)
    expect(existsSync(predecessor)).toBe(true)

    // A day without a read and the superseded copy goes too.
    utimesSync(predecessor, aDayAndAnHourAgo, aDayAndAnHourAgo)
    expect(readValue(dbPath)).toBe(1)

    expect(existsSync(predecessor)).toBe(false)
    expect(cachedDatabaseFiles()).toHaveLength(1)
  })

  it('says so instead of going quiet when the cache copy cannot be written', ({ skip }) => {
    const dbPath = join(sourceRoot, 'state.vscdb')
    writeUncheckpointedWalDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return
    chmodSync(cacheRoot, 0o555)

    const stderr = vi.spyOn(process.stderr, 'write').mockReturnValue(true)
    try {
      expect(() => readValue(dbPath)).toThrow()
      const notices = stderr.mock.calls.filter(([chunk]) => String(chunk).includes('cache copy could not be written'))
      expect(notices).toHaveLength(1)
      expect(String(notices[0]?.[0])).toContain(dbPath)
    } finally {
      stderr.mockRestore()
      chmodSync(cacheRoot, 0o755)
    }
  })

  it('keeps a genuinely missing database distinguishable from SQLITE_READONLY', () => {
    let thrown: unknown
    try {
      openDatabase(join(sourceRoot, 'missing.vscdb'))
    } catch (err) {
      thrown = err
    }

    expect(thrown).toBeDefined()
    expect(isSqliteReadonlyError(thrown)).toBe(false)
    expect(thrown).toMatchObject({ errcode: 14, message: 'unable to open database file' })
  })

  it('surfaces one read-only notice in SQLite discovery and still finds the session', async ({ skip }) => {
    const dbPath = join(sourceRoot, 'state.db')
    createDiscoveryDatabase(dbPath)
    if (!makeSourceParentReadOnly(skip)) return

    const config: SqliteProviderConfig = {
      providerName: 'opencode',
      displayName: 'OpenCode',
      dbDir: sourceRoot,
      dbFilePrefix: 'state',
    }
    const stderr = vi.spyOn(process.stderr, 'write').mockReturnValue(true)
    try {
      const sessions = await discoverSqliteSessions(config)
      expect(sessions).toHaveLength(1)
      expect(sessions[0]?.path).toBe(`${dbPath}:session-1`)
      expect(stderr.mock.calls.filter(([chunk]) => String(chunk).includes('read-only directory'))).toHaveLength(1)

      await discoverSqliteSessions(config)
      expect(stderr.mock.calls.filter(([chunk]) => String(chunk).includes('read-only directory'))).toHaveLength(1)
    } finally {
      stderr.mockRestore()
    }
  })
})
