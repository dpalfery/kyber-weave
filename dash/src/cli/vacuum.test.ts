// The contract of `kyberdash dash vacuum`: it reclaims the store's freelist,
// it says what it did, and it refuses a store it cannot read.
//
// WHY a scratch store and never the live one: the live canon.db is the user's
// database and the engine holds it open. A VACUUM rewrites the whole file, so
// the command is exercised here only against a store this test builds and
// throws away.
//
// WHY the freelist is manufactured rather than hoped for: SQLite only returns
// a page to the filesystem when the last row on it is gone, so a table that is
// filled and then dropped is what actually leaves free pages behind. Deleting
// every other row frees nothing — the pages stay half full and stay in use.

import { describe, expect, it } from 'vitest'
import { Command } from 'commander'
import { mkdtempSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'

import { registerKyberCommands } from './register.js'
import { CanonStore, SCHEMA_VERSION } from '../canon/store.js'

type Run = { stdout: string[]; stderr: string[]; exitCode: number | undefined }

/**
 * Run the vacuum verb with the store's writer lock injected, so the test
 * never creates or waits on a lock file in the user's own state directory.
 */
async function runVacuum(...args: string[]): Promise<Run> {
  const stdout: string[] = []
  const stderr: string[] = []
  const program = new Command()
  program.exitOverride()
  registerKyberCommands(program, {
    write: (line: string) => { stdout.push(line) },
    writeError: (line: string) => { stderr.push(line) },
    acquireStoreRefreshLock: async () => ({
      outcome: 'acquired' as const,
      handle: {
        token: 'test',
        release: async () => {},
        verifyStillOwner: async () => true,
      },
    }),
  })

  const previous = process.exitCode
  process.exitCode = undefined
  try {
    await program.parseAsync(['node', 'kyberdash', 'dash', 'vacuum', ...args])
    return { stdout, stderr, exitCode: process.exitCode }
  } finally {
    process.exitCode = previous
  }
}

/** A scratch canon store holding one real row, so "the data is intact" is a real claim. */
function scratchStore(tag: string): string {
  const path = join(mkdtempSync(join(tmpdir(), `kyberdash-vacuum-${tag}-`)), 'canon.db')
  const store = new CanonStore(path)
  store.quarantine('span-alive', ['gen_ai'], 'unclaimed')
  store.close()
  return path
}

/**
 * Fill a table and drop it, which leaves its pages on SQLite's freelist.
 * Returns the freelist size the store now carries.
 */
function manufactureFreelist(path: string): number {
  const db = new DatabaseSync(path)
  db.exec('PRAGMA busy_timeout = 5000')
  db.exec('PRAGMA journal_mode = WAL')
  db.exec('CREATE TABLE vacuum_scratch (id INTEGER PRIMARY KEY, v TEXT)')
  db.exec('BEGIN')
  for (let i = 0; i < 2000; i += 1) {
    db.prepare('INSERT INTO vacuum_scratch (v) VALUES (?)').run('x'.repeat(1000))
  }
  db.exec('COMMIT')
  db.exec('DROP TABLE vacuum_scratch')
  const freelist = Number(
    (db.prepare('PRAGMA freelist_count').get() as { freelist_count: number }).freelist_count,
  )
  db.close()
  return freelist
}

describe('dash vacuum', () => {
  it('is registered beside the other store commands', () => {
    const program = new Command()
    registerKyberCommands(program)
    const dash = program.commands.find(command => command.name() === 'dash')
    expect(dash?.commands.map(command => command.name()).sort()).toContain('vacuum')
  })

  it('reports the before size, the after size, the reclaimed bytes and the freelist it reclaimed', async () => {
    const path = scratchStore('report')
    try {
      const freelistPages = manufactureFreelist(path)
      expect(freelistPages).toBeGreaterThan(0)

      const { stdout, stderr, exitCode } = await runVacuum('--db', path)

      expect(stderr).toEqual([])
      expect(exitCode).toBe(0)
      const output = stdout.join('\n')
      // The four things an operator needs to judge the run.
      expect(output).toContain('Before:')
      expect(output).toContain('After:')
      expect(output).toContain('Reclaimed:')
      // The freelist count the run reclaimed, in pages.
      expect(output).toContain(`${freelistPages} pages`)

      // And the store is smaller afterwards, with the data still in it.
      const store = new CanonStore(path)
      try {
        expect(store.listQuarantine().map(entry => entry.spanId)).toContain('span-alive')
      } finally {
        store.close()
      }
    } finally {
      rmSync(join(path, '..'), { recursive: true, force: true })
    }
  })

  it('shrinks the store file', async () => {
    const path = scratchStore('shrink')
    try {
      manufactureFreelist(path)
      const before = statSync(path).size

      const { exitCode } = await runVacuum('--db', path)

      expect(exitCode).toBe(0)
      expect(statSync(path).size).toBeLessThan(before)
    } finally {
      rmSync(join(path, '..'), { recursive: true, force: true })
    }
  })

  it('reports the reclaimable space on a dry run and writes nothing', async () => {
    const path = scratchStore('dry')
    try {
      const freelistPages = manufactureFreelist(path)
      const before = statSync(path)

      const { stdout, stderr, exitCode } = await runVacuum('--db', path, '--dry-run')

      expect(stderr).toEqual([])
      expect(exitCode).toBe(0)
      const output = stdout.join('\n')
      expect(output).toContain('Reclaimable:')
      expect(output).toContain(`${freelistPages} free pages`)

      // The promise of a dry run: nothing was written. Both the size and the
      // modification time are unchanged.
      const after = statSync(path)
      expect(after.size).toBe(before.size)
      expect(after.mtimeMs).toBe(before.mtimeMs)
    } finally {
      rmSync(join(path, '..'), { recursive: true, force: true })
    }
  })

  it('refuses a store this build cannot read, with the store\'s own refusal', async () => {
    const path = scratchStore('refuse')
    try {
      // Stamp the store with a schema version from the future.
      const db = new DatabaseSync(path)
      db.prepare("UPDATE metadata SET value = ? WHERE key = 'schema_version'").run(
        String(SCHEMA_VERSION + 1),
      )
      db.close()

      // What the store itself says when it meets that store.
      let storeRefusal = ''
      try {
        new CanonStore(path)
      } catch (err) {
        storeRefusal = err instanceof Error ? err.message : String(err)
      }
      expect(storeRefusal).toContain('upgrade KyberDash')

      const { stdout, stderr, exitCode } = await runVacuum('--db', path)

      // Reported, not thrown: an uncaught throw in the packaged CLI prints a
      // stack whose every frame names the whole bundle.
      expect(exitCode).toBe(1)
      expect(stderr.join('\n')).toContain(storeRefusal)
      expect(stderr.join('\n')).toContain('upgrade KyberDash')
      expect(stderr.join('\n')).not.toContain('    at ')
      // And it did not silently do nothing.
      expect(stdout.join('\n')).not.toContain('Vacuumed:')
    } finally {
      rmSync(join(path, '..'), { recursive: true, force: true })
    }
  })
})
