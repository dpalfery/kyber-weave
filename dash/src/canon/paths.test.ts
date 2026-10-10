import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { Command } from 'commander'

import { registerKyberCommands } from '../cli/register.js'
import { startOtlpCollectorService } from '../otel/service.js'
import { KyberBridge } from '../server/bridge.js'
import { CanonStore } from './store.js'
// RED: this module does not exist yet (T1). Until it does, the whole file fails to load.
import { resolveCanonDbPath } from './paths.js'

/**
 * One resolver owns "which canon.db": explicit > KYBER_CANON_DB > ~/.kyberdash/canon.db.
 * Every test runs under a temp HOME so the real ~/.kyberdash/canon.db is never touched.
 */
let root: string
let home: string
let envDb: string

beforeEach(() => {
  root = mkdtempSync(join(tmpdir(), 'kyber-canon-paths-'))
  home = join(root, 'home')
  envDb = join(root, 'env', 'canon.db')
  // os.homedir() reads HOME (posix) / USERPROFILE (win32) on every call.
  vi.stubEnv('HOME', home)
  vi.stubEnv('USERPROFILE', home)
})

afterEach(() => {
  vi.unstubAllEnvs()
  rmSync(root, { recursive: true, force: true })
})

describe('resolveCanonDbPath precedence', () => {
  it('prefers the explicit path over KYBER_CANON_DB and the default', () => {
    vi.stubEnv('KYBER_CANON_DB', envDb)
    expect(resolveCanonDbPath('/explicit/canon.db')).toBe('/explicit/canon.db')
  })

  it('uses KYBER_CANON_DB when no explicit path is given', () => {
    vi.stubEnv('KYBER_CANON_DB', envDb)
    expect(resolveCanonDbPath()).toBe(envDb)
  })

  it('falls back to <homedir>/.kyberdash/canon.db when neither is set', () => {
    vi.stubEnv('KYBER_CANON_DB', '')
    delete process.env.KYBER_CANON_DB
    expect(resolveCanonDbPath()).toBe(join(home, '.kyberdash', 'canon.db'))
  })
})

describe('every canon.db call site resolves KYBER_CANON_DB (no explicit path)', () => {
  beforeEach(() => {
    vi.stubEnv('KYBER_CANON_DB', envDb)
  })

  /** Fake lock so the CLI never touches the real state dir. */
  const acquiredLock = async () => ({
    outcome: 'acquired' as const,
    handle: { token: 't', release: async () => {}, verifyStillOwner: async () => true },
  })

  it('`dash refresh` opens its store at KYBER_CANON_DB', async () => {
    const opened: string[] = []
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      write: () => {},
      writeError: () => {},
      createStore: (path) => {
        opened.push(path)
        return new CanonStore(':memory:')
      },
      acquireStoreRefreshLock: acquiredLock,
      refreshHarnessSources: async () => ({
        historyWeeks: 2,
        commandStartedAt: new Date().toISOString(),
        rows: [],
        derived: { sessions: 0, runs: 0, executions: 0, rollups: 0 },
        failedJobs: 0,
        derivationFailed: false,
        exitCode: 0,
      }),
    })
    const previous = process.exitCode
    try {
      await program.parseAsync(['node', 'kyberdash', 'dash', 'refresh'])
    } finally {
      process.exitCode = previous
    }
    // Fails today: the CLI's private resolveDbPath ignores the env var.
    expect(opened).toEqual([envDb])
  })

  it('`dash clean` opens its store at KYBER_CANON_DB', async () => {
    const opened: string[] = []
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      write: () => {},
      writeError: () => {},
      createStore: (path) => {
        opened.push(path)
        return new CanonStore(':memory:')
      },
      acquireStoreRefreshLock: acquiredLock,
      cleanDatabase: async () => ({}) as never,
    })
    const previous = process.exitCode
    try {
      await program.parseAsync(['node', 'kyberdash', 'dash', 'clean', '--all', '--yes'])
    } finally {
      process.exitCode = previous
    }
    // Fails today: same private resolveDbPath ignores the env var.
    expect(opened).toEqual([envDb])
  })

  it('`dash settings set` opens its store at KYBER_CANON_DB', async () => {
    const opened: string[] = []
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      write: () => {},
      writeError: () => {},
      createStore: (path) => {
        opened.push(path)
        return new CanonStore(':memory:')
      },
    })
    const previous = process.exitCode
    try {
      await program.parseAsync(['node', 'kyberdash', 'dash', 'settings', 'set', 'jobs.paused', 'on'])
    } finally {
      process.exitCode = previous
    }
    // The same one-resolver rule as refresh and clean: a settings write that lands in a
    // different canon.db than the scheduler reads is a setting nothing acts on.
    expect(opened).toEqual([envDb])
  })

  it('`dash import-history` opens its store at KYBER_CANON_DB', async () => {
    const opened: string[] = []
    const program = new Command()
    program.exitOverride()
    registerKyberCommands(program, {
      write: () => {},
      writeError: () => {},
      createStore: (path) => {
        opened.push(path)
        return new CanonStore(':memory:')
      },
      acquireStoreRefreshLock: acquiredLock,
      importFolderHistory: async () => {},
    })
    const previous = process.exitCode
    try {
      await program.parseAsync(['node', 'kyberdash', 'dash', 'import-history'])
    } finally {
      process.exitCode = previous
    }
    expect(opened).toEqual([envDb])
  })

  it('startOtlpCollectorService defaults dbPath to KYBER_CANON_DB', async () => {
    const service = await startOtlpCollectorService({ port: 0 })
    try {
      // Fails today: the receiver defaults to <home>/.kyberdash/canon.db and ignores the env var.
      // (No public path accessor on the service/CanonStore, so file creation is the observable seam.)
      expect(existsSync(envDb)).toBe(true)
      expect(existsSync(join(home, '.kyberdash', 'canon.db'))).toBe(false)
    } finally {
      await service.close()
    }
  })

  it('new KyberBridge() canonPath equals KYBER_CANON_DB', () => {
    // The bridge already honours the env var inline; this pins that it stays equal to the
    // shared resolver's answer once it delegates (red now only via the missing module).
    expect(new KyberBridge().canonPath).toBe(envDb)
    expect(new KyberBridge().canonPath).toBe(resolveCanonDbPath())
  })
})
