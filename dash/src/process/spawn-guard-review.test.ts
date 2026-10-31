// PR review rework for the spawn guard (cluster review/spawn-guard). Fakes only: every
// spawnFn here records its call and starts no process. The guard is the one chokepoint that
// stops a spawn storm, so each hole a reviewer found is pinned by a test that fails without it.
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  allowRealSpawns,
  assertMayHost,
  clearSpawnGuardHits,
  guardedSpawn,
  isSpawnedChildRole,
  spawnLedgerEntries,
  SpawnRefusedError,
  type SpawnKind,
} from './spawn-guard.js'

const ENV_KEYS = [
  'KYBERDASH_SPAWN_GUARD',
  'KYBERDASH_SPAWN_ROLE',
  'KYBERDASH_SPAWN_DEPTH',
  'KYBERDASH_SPAWN_LEDGER_FILE',
  'KYBERDASH_LEDGER_RUN_STARTED_AT',
] as const
const ALL_KINDS: readonly SpawnKind[] = ['job', 'receiver', 'engine', 'tool']

let savedEnv: Record<string, string | undefined> = {}
const tempDirs: string[] = []

beforeEach(() => {
  savedEnv = {}
  for (const key of ENV_KEYS) savedEnv[key] = process.env[key]
  process.env.KYBERDASH_SPAWN_GUARD = 'deny'
  delete process.env.KYBERDASH_SPAWN_ROLE
  delete process.env.KYBERDASH_SPAWN_DEPTH
  // Never write a fake pid into the run's real cross-process ledger.
  delete process.env.KYBERDASH_SPAWN_LEDGER_FILE
})

afterEach(() => {
  for (const key of ENV_KEYS) {
    const value = savedEnv[key]
    if (value === undefined) delete process.env[key]
    else process.env[key] = value
  }
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true })
  vi.restoreAllMocks()
})

type Call = { readonly env: NodeJS.ProcessEnv | undefined }

function recorder(child: unknown = undefined) {
  const calls: Call[] = []
  const spawnFn = vi.fn((_program: string, _args: readonly string[], options: { env?: NodeJS.ProcessEnv }) => {
    calls.push({ env: options.env })
    return child as undefined
  })
  return { spawnFn, calls }
}

function spawnAs(kind: SpawnKind, spawnFn: ReturnType<typeof recorder>['spawnFn'], env?: NodeJS.ProcessEnv): void {
  guardedSpawn(spawnFn, { kind, program: '/bin/echo', args: ['x'], options: env === undefined ? undefined : { env } })
}

describe('C2: the child keeps the guard when the caller supplies its own env', () => {
  it('copies KYBERDASH_SPAWN_GUARD from the parent when options.env omits it', () => {
    const { spawnFn, calls } = recorder()
    allowRealSpawns('review test: env inheritance')

    spawnAs('job', spawnFn, { PATH: '/usr/bin' })

    expect(calls[0]?.env?.KYBERDASH_SPAWN_GUARD).toBe('deny')
    expect(calls[0]?.env?.PATH).toBe('/usr/bin')
  })

  it('copies the ledger variables from the parent when options.env omits them', () => {
    const { spawnFn, calls } = recorder()
    allowRealSpawns('review test: ledger env inheritance')
    process.env.KYBERDASH_SPAWN_LEDGER_FILE = '/some/ledger'
    process.env.KYBERDASH_LEDGER_RUN_STARTED_AT = '123'

    spawnAs('job', spawnFn, { PATH: '/usr/bin' })

    expect(calls[0]?.env?.KYBERDASH_SPAWN_LEDGER_FILE).toBe('/some/ledger')
    expect(calls[0]?.env?.KYBERDASH_LEDGER_RUN_STARTED_AT).toBe('123')
  })

  it('keeps a value the caller set explicitly', () => {
    const { spawnFn, calls } = recorder()
    allowRealSpawns('review test: explicit value wins')

    spawnAs('job', spawnFn, { KYBERDASH_SPAWN_GUARD: 'custom' })

    expect(calls[0]?.env?.KYBERDASH_SPAWN_GUARD).toBe('custom')
  })
})

describe('C3: role handling is an allowlist', () => {
  it('refuses every kind when the calling role is tool', () => {
    process.env.KYBERDASH_SPAWN_ROLE = 'tool'
    allowRealSpawns('review test: tool role must not spawn')
    for (const kind of ALL_KINDS) {
      const { spawnFn } = recorder()
      expect(() => spawnAs(kind, spawnFn), kind).toThrow(SpawnRefusedError)
      expect(spawnFn, kind).not.toHaveBeenCalled()
    }
    clearSpawnGuardHits()
  })

  it('lets an engine spawn a job and a receiver, never an engine or a tool', () => {
    process.env.KYBERDASH_SPAWN_ROLE = 'engine'
    allowRealSpawns('review test: engine allowlist')
    for (const kind of ['job', 'receiver'] as const) {
      const { spawnFn } = recorder()
      spawnAs(kind, spawnFn)
      expect(spawnFn, kind).toHaveBeenCalledTimes(1)
    }
    for (const kind of ['engine', 'tool'] as const) {
      const { spawnFn } = recorder()
      expect(() => spawnAs(kind, spawnFn), kind).toThrow(SpawnRefusedError)
      expect(spawnFn, kind).not.toHaveBeenCalled()
    }
    clearSpawnGuardHits()
  })

  it('refuses an unknown role for every kind', () => {
    process.env.KYBERDASH_SPAWN_ROLE = 'mystery'
    allowRealSpawns('review test: unknown role')
    const { spawnFn } = recorder()
    expect(() => spawnAs('job', spawnFn)).toThrow(SpawnRefusedError)
    expect(spawnFn).not.toHaveBeenCalled()
    clearSpawnGuardHits()
  })
})

describe('C4: a tool child counts as a spawned child', () => {
  it('isSpawnedChildRole is true for tool', () => {
    expect(isSpawnedChildRole({ KYBERDASH_SPAWN_ROLE: 'tool' })).toBe(true)
  })

  it('assertMayHost refuses a tool role', () => {
    expect(() => assertMayHost({ KYBERDASH_SPAWN_ROLE: 'tool' })).toThrow(/may not host/)
  })
})

describe('C5: a depth that is not a plain non-negative integer is refused', () => {
  it.each(['   ', '1.5', '-1', '0x1', 'abc', '9007199254740993'])('refuses depth %j', (depth) => {
    process.env.KYBERDASH_SPAWN_DEPTH = depth
    allowRealSpawns('review test: bad depth')
    const { spawnFn } = recorder()
    expect(() => spawnAs('job', spawnFn)).toThrow(SpawnRefusedError)
    expect(spawnFn).not.toHaveBeenCalled()
    clearSpawnGuardHits()
  })

  it('assertMayHost refuses a whitespace depth', () => {
    expect(() => assertMayHost({ KYBERDASH_SPAWN_DEPTH: '   ' })).toThrow(/may not host/)
  })

  it('still accepts depth 1 with surrounding whitespace trimmed', () => {
    process.env.KYBERDASH_SPAWN_DEPTH = ' 1 '
    allowRealSpawns('review test: trimmed depth')
    const { spawnFn, calls } = recorder()
    spawnAs('job', spawnFn)
    expect(calls[0]?.env?.KYBERDASH_SPAWN_DEPTH).toBe('2')
  })
})

describe('C6: only a real pid is recorded in the ledger', () => {
  it.each([Number.NaN, 0, -5, 1.5, Number.POSITIVE_INFINITY])('does not record pid %s', (pid) => {
    allowRealSpawns('review test: bad pid')
    const before = spawnLedgerEntries().length
    spawnAs('job', recorder({ pid }).spawnFn)
    expect(spawnLedgerEntries()).toHaveLength(before)
  })

  it('records a positive integer pid', () => {
    allowRealSpawns('review test: good pid')
    const before = spawnLedgerEntries().length
    spawnAs('job', recorder({ pid: 4242 }).spawnFn)
    expect(spawnLedgerEntries()).toHaveLength(before + 1)
  })
})

describe('C7: the ledger file is constrained and failures are not hidden', () => {
  function ledgerDir(prefix = 'kyberdash-ledger-'): string {
    const dir = mkdtempSync(join(tmpdir(), prefix))
    tempDirs.push(dir)
    return dir
  }
  const spawnWithLedger = (file: string): void => {
    process.env.KYBERDASH_SPAWN_LEDGER_FILE = file
    allowRealSpawns('review test: ledger path')
    spawnAs('job', recorder({ pid: 4242 }).spawnFn)
  }

  it('appends to a file inside a kyberdash-ledger temp directory', () => {
    const file = join(ledgerDir(), 'spawns.jsonl')
    writeFileSync(file, '')
    spawnWithLedger(file)
    expect(readFileSync(file, 'utf8')).toContain('"pid":4242')
  })

  it('does not append outside a kyberdash-ledger temp directory', () => {
    const file = join(ledgerDir('other-'), 'spawns.jsonl')
    writeFileSync(file, '')
    spawnWithLedger(file)
    expect(readFileSync(file, 'utf8')).toBe('')
  })

  it('does not append through a .. segment', () => {
    const dir = ledgerDir()
    const outside = ledgerDir('other-')
    writeFileSync(join(outside, 'spawns.jsonl'), '')
    spawnWithLedger(join(dir, '..', outside.split('/').pop() ?? '', 'spawns.jsonl'))
    expect(readFileSync(join(outside, 'spawns.jsonl'), 'utf8')).toBe('')
  })

  it('does not append to a relative path', () => {
    const name = `relative-ledger-${process.pid}.jsonl`
    tempDirs.push(join(process.cwd(), name))
    spawnWithLedger(name)
    expect(existsSync(join(process.cwd(), name))).toBe(false)
  })

  it('swallows ENOENT silently', () => {
    const stderr = vi.spyOn(process.stderr, 'write').mockImplementation(() => true)
    spawnWithLedger(join(ledgerDir(), 'missing-dir', 'spawns.jsonl'))
    expect(stderr).not.toHaveBeenCalled()
  })

  it('reports any other failure to stderr once', () => {
    const stderr = vi.spyOn(process.stderr, 'write').mockImplementation(() => true)
    const dir = ledgerDir()
    // The "file" is a directory: appending fails with EISDIR, not ENOENT.
    const file = join(dir, 'spawns.jsonl')
    mkdirSync(file)
    spawnWithLedger(file)
    spawnWithLedger(file)
    expect(stderr).toHaveBeenCalledTimes(1)
    expect(String(stderr.mock.calls[0]?.[0])).toContain('EISDIR')
  })
})
