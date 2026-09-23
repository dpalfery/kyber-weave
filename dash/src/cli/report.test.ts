// Command tests for `kyberdash report` (R11.1, R11.4, R11.5, R11.10–R11.13).
//
// The process is spawned rather than calling `runContextReport` in-process so
// the exit codes are the ones a person or an agent actually sees. Invalid
// arguments must fail before the store is created: CanonStore's constructor
// mkdir's the parent, and a typo that left a new canon.db behind would be a
// worse failure than the message.

import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { spawnSync } from 'node:child_process'

import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { buildProgram } from './program.js'

const homes: string[] = []

afterEach(async () => {
  while (homes.length > 0) {
    const home = homes.pop()
    if (home) await rm(home, { recursive: true, force: true })
  }
})

async function makeHome(): Promise<string> {
  const home = await mkdtemp(join(tmpdir(), 'kyberdash-report-cli-'))
  homes.push(home)
  return home
}

function emptyStore(home: string): string {
  const db = join(home, 'canon.db')
  const store = new CanonStore(db)
  store.close()
  return db
}

function runCli(args: string[], home: string, extraEnv: Record<string, string> = {}) {
  return spawnSync(process.execPath, ['--import', 'tsx', 'src/launcher.ts', ...args], {
    cwd: new URL('../..', import.meta.url),
    env: { ...process.env, HOME: home, TZ: 'UTC', ...extraEnv },
    encoding: 'utf-8',
    timeout: 30_000,
  })
}

describe('kyberdash report is the default command (R11.1)', () => {
  it('stays registered as the default', () => {
    const program = buildProgram() as unknown as { _defaultCommandName?: string }
    expect(program._defaultCommandName).toBe('report')
  })

  it('runs with no command name', async () => {
    const home = await makeHome()
    const db = emptyStore(home)
    const named = runCli(['report', '--format', 'json', '--db', db], home)
    const implied = runCli(['--format', 'json', '--db', db], home)
    expect(named.status, named.stderr).toBe(0)
    expect(implied.status, implied.stderr).toBe(0)
    expect(JSON.parse(implied.stdout).schemaVersion).toBe(1)
  })
})

describe('kyberdash report flags (R11.11)', () => {
  it('defaults --days to 7 and --limit to 5', async () => {
    const home = await makeHome()
    const db = emptyStore(home)
    const res = runCli(['report', '--format', 'json', '--db', db], home)
    expect(res.status, res.stderr).toBe(0)
    const body = JSON.parse(res.stdout) as { scope: { days: number }; findings: unknown[] }
    expect(body.scope.days).toBe(7)
    expect(body.findings).toEqual([])
  })

  it('scopes --harness, --session, --run and --days into the document', async () => {
    const home = await makeHome()
    const db = emptyStore(home)
    const res = runCli([
      'report',
      '--format', 'json',
      '--db', db,
      '--harness', 'claude-cli',
      '--session', 'sess-1',
      '--run', 'run-1',
      '--days', '3',
      '--limit', '2',
    ], home)
    expect(res.status, res.stderr).toBe(0)
    expect(JSON.parse(res.stdout).scope).toEqual({
      days: 3,
      harness: 'claude-cli',
      sessionId: 'sess-1',
      runId: 'run-1',
    })
  })
})

describe('kyberdash report exit codes (R11.12)', () => {
  it('exits 2 for an invalid argument before the store opens', async () => {
    const home = await makeHome()
    const db = join(home, 'must-not-be-created', 'canon.db')
    const cases: string[][] = [
      ['report', '--days', '0', '--db', db],
      ['report', '--days', '-1', '--db', db],
      ['report', '--days', '2.5', '--db', db],
      ['report', '--limit', '0', '--db', db],
      ['report', '--format', 'xml', '--db', db],
      ['report', '--harness', 'claud', '--db', db],
    ]
    for (const args of cases) {
      const res = runCli(args, home)
      expect(res.status, `${args.join(' ')}\n${res.stderr}`).toBe(2)
      expect(existsSync(join(home, 'must-not-be-created')), args.join(' ')).toBe(false)
    }
  })

  it('hints the nearest harness id for a typo', async () => {
    const home = await makeHome()
    const res = runCli(['report', '--harness', 'claud', '--db', join(home, 'no.db')], home)
    expect(res.status).toBe(2)
    expect(res.stderr).toContain('unknown harness "claud"')
    expect(res.stderr).toMatch(/Did you mean "claude-/)
  })

  it('exits 1 when the store is missing, naming the path', async () => {
    const home = await makeHome()
    const db = join(home, 'missing', 'canon.db')
    const res = runCli(['report', '--db', db], home)
    expect(res.status, res.stderr).toBe(1)
    expect(res.stderr).toContain(db)
  })

  it('exits 1 when the store cannot be read', async () => {
    const home = await makeHome()
    const db = join(home, 'not-a-store')
    await writeFile(db, 'this is not sqlite')
    const res = runCli(['report', '--db', db], home)
    expect(res.status, res.stderr).toBe(1)
    expect(res.stderr).toContain(db)
  })

  it('exits 0 for an empty store (R11.4)', async () => {
    const home = await makeHome()
    const db = emptyStore(home)
    const res = runCli(['report', '--format', 'text', '--db', db], home)
    expect(res.status, res.stderr).toBe(0)
    expect(res.stdout).toContain('kyberdash dash refresh')
  })
})

describe('kyberdash report is read-only (R11.13)', () => {
  it('does not ingest and does not write a session cache', async () => {
    const home = await makeHome()
    const db = emptyStore(home)
    const cache = join(home, '.cache', 'kyberdash')
    await mkdir(cache, { recursive: true })
    const res = runCli(['report', '--format', 'json', '--db', db], home, {
      KYBERDASH_CACHE_DIR: cache,
    })
    expect(res.status, res.stderr).toBe(0)
    expect(existsSync(join(cache, 'session-cache.v9'))).toBe(false)
  })
})

describe('kyberdash report findings (R11.5)', () => {
  it('ends each finding with its kyberdash web --view command', async () => {
    const home = await makeHome()
    const db = join(home, 'canon.db')
    const store = new CanonStore(db)
    store.upsertFinding({
      id: 'finding-tool-schema-residency',
      detectorId: 'dormant-tool-schema',
      title: 'Tool definitions hold 9,000 tokens across every turn',
      mechanism: 'Schemas are resident in every turn.',
      evidenceLinks: [
        { spanId: 'span-0d41', turnIndex: 3, description: 'resident' },
        { spanId: 'span-0d52', turnIndex: 9, description: 'resident' },
      ],
      confidence: 'deterministic',
      estimatedWasteTokens: 6400,
      recommendation: 'Move the github server schema behind an on-demand skill.',
      errorBar: { lower: 5200, upper: 7600 },
      outcomeRiskCaveat: 'Relocating schemas adds a load step.',
      rankScore: 87.5,
    })
    store.close()
    const res = runCli(['report', '--format', 'text', '--db', db], home)
    expect(res.status, res.stderr).toBe(0)
    expect(res.stdout).toContain('Move the github server schema behind an on-demand skill.')
    expect(res.stdout).toContain('kyberdash web --view finding/finding-tool-schema-residency')
  })
})

describe('kyberdash report formats (R11.8–R11.10)', () => {
  it('emits JSON with schemaVersion and null unmeasurable figures, never 0 (R11.10)', async () => {
    const home = await makeHome()
    const db = emptyStore(home)
    const res = runCli(['report', '--format', 'json', '--db', db], home)
    expect(res.status, res.stderr).toBe(0)
    const body = JSON.parse(res.stdout) as {
      schemaVersion: number
      cost: Array<{ amountUsd: { value: number | null; reason?: string } }>
    }
    expect(body.schemaVersion).toBe(1)
    expect(body.cost[0]?.amountUsd.value).toBeNull()
    expect(body.cost[0]?.amountUsd.reason).toBeTruthy()
    expect(JSON.stringify(body.cost)).not.toContain('"value":0')
  })

  it('emits Markdown without escape sequences (R11.9)', async () => {
    const home = await makeHome()
    const db = emptyStore(home)
    const res = runCli(['report', '--format', 'markdown', '--db', db], home)
    expect(res.status, res.stderr).toBe(0)
    expect(res.stdout).toContain('## Data coverage')
    expect(res.stdout).not.toMatch(/\u001b\[[0-9;]*m/)
  })

  it('does not colour text when stdout is not a TTY (R11.8)', async () => {
    const home = await makeHome()
    const db = emptyStore(home)
    const res = runCli(['report', '--format', 'text', '--db', db], home)
    expect(res.status, res.stderr).toBe(0)
    expect(res.stdout).not.toMatch(/\u001b\[[0-9;]*m/)
  })

  it('does not colour text when NO_COLOR is set (R11.8)', async () => {
    const home = await makeHome()
    const db = emptyStore(home)
    const res = runCli(['report', '--format', 'text', '--db', db], home, { NO_COLOR: '1' })
    expect(res.status, res.stderr).toBe(0)
    expect(res.stdout).not.toMatch(/\u001b\[[0-9;]*m/)
  })
})
