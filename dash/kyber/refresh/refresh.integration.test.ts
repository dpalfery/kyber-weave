// T7 — end-to-end refresh against sanitized file and virtual-DB fixtures.
// Temp canon.db only (os.tmpdir). Never ~/.kyberdash/canon.db.

import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { join } from 'node:path'
import { Command, CommanderError } from 'commander'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { registerKyberCommands } from '../cli/register.js'
import { refreshHarnessSources } from './orchestrator.js'
import { descriptorFor } from './registry.js'
import type { SourceCheckpoint } from '../canon/source-state.js'
import type { Provider, SessionSource } from '../../src/providers/types.js'

import {
  COMMAND_STARTED_AT,
  copyFixture,
  fixtureProvider,
  jsonlLoader,
  sqliteLoader,
  writeCursorVirtualDb,
} from './fixtures/integration-harness.js'

const USER_CANON = join(homedir(), '.kyberdash', 'canon.db')

const REQUIRED_SPLIT_IDS = [
  'antigravity',
  'antigravity-cli',
  'antigravity-ide',
  'copilot-cli',
  'copilot-vscode',
  'copilot-jetbrains',
  'copilot-agent',
  'codex-cli',
  'codex-desktop',
  'codex-unclassified',
  'claude-cli',
  'claude-desktop',
  'claude-unclassified',
  'cursor',
  'cursor-agent',
  'cline',
  'cline-cli',
  'kiro-cli',
  'kiro-ide',
  'kilo-shared-runtime',
  'kilo-vscode-legacy',
  'pi',
  'opencode',
] as const

const JOB_IDS = [...REQUIRED_SPLIT_IDS, 'droid'] as const

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

function tempRoot(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-refresh-t7-'))
  temporaryRoots.push(root)
  expect(root.startsWith(tmpdir())).toBe(true)
  expect(root).not.toContain('.kyberdash')
  return root
}

function descriptorsFor(ids: readonly string[]) {
  return ids.map((id) => {
    const descriptor = descriptorFor(id)
    if (descriptor === undefined) throw new Error(`missing descriptor ${id}`)
    return descriptor
  })
}

function source(path: string, provider: string, extras: Partial<SessionSource> = {}): SessionSource {
  return { path, project: extras.project ?? provider, provider, ...extras }
}

type World = {
  store: CanonStore
  piPath: string
  droidDiscoverable: boolean
  providers: () => Promise<Provider[]>
}

function arrangeWorld(): World {
  const root = tempRoot()
  const dbPath = join(root, 'canon.db')
  const piPath = copyFixture('pi-cross-cutoff.jsonl', join(root, 'pi', 'pi-cross-cutoff.jsonl'))
  const antigravityPath = copyFixture(
    'antigravity-conversation.jsonl',
    join(root, '.gemini', 'antigravity', 'conversation.jsonl'),
  )
  const antigravityCliPath = copyFixture(
    'antigravity-cli-conversation.jsonl',
    join(root, '.gemini', 'antigravity-cli', 'conversation.jsonl'),
  )
  const copilotPath = copyFixture('copilot-cli-session.jsonl', join(root, '.copilot', 'session.jsonl'))
  const cursorDbPath = writeCursorVirtualDb(join(root, 'cursor', 'state.vscdb'))
  const droidPath = copyFixture('droid-session.jsonl', join(root, '.factory', 'sessions', 'droid.jsonl'))

  const world: World = {
    store: new CanonStore(dbPath),
    piPath,
    droidDiscoverable: true,
    providers: async () => [],
  }

  world.providers = async () => [
    fixtureProvider('pi', [source(piPath, 'pi', { sourceId: 'pi-cross' })], jsonlLoader),
    fixtureProvider('antigravity', [
      source(antigravityPath, 'antigravity', { project: 'antigravity', sourceId: 'agy-root' }),
      source(antigravityCliPath, 'antigravity', { project: 'antigravity-cli', sourceId: 'agy-cli' }),
    ], jsonlLoader),
    fixtureProvider('copilot', [
      source(copilotPath, 'copilot', { sourceType: 'jsonl', sourceId: 'copilot-cli-1' }),
    ], jsonlLoader),
    fixtureProvider('cursor', [source(cursorDbPath, 'cursor', { sourceId: 'cursor-virtual' })], sqliteLoader),
    fixtureProvider('droid', [source(droidPath, 'droid', { sourceId: 'droid-1' })], (session) => {
      if (!world.droidDiscoverable) {
        throw Object.assign(new Error('droid source unreadable'), { path: session.path })
      }
      return jsonlLoader(session)
    }),
    fixtureProvider('gemini', [source(join(root, 'gemini', 'chat.jsonl'), 'gemini', { sourceId: 'gemini-chat' })], () => {
      throw new Error('gemini must not be ingested as a harness')
    }),
  ]

  return world
}

async function refresh(world: World, historyWeeks?: number) {
  return refreshHarnessSources(
    world.store,
    {
      getAllProviders: world.providers,
      descriptors: descriptorsFor(JOB_IDS),
      jobConcurrency: 2,
      writerCapacity: 2,
      commandStartedAt: COMMAND_STARTED_AT,
      parseAllSessions: async () => undefined,
    },
    historyWeeks === undefined ? {} : { historyWeeks },
  )
}

function row(report: Awaited<ReturnType<typeof refresh>>, harnessId: string) {
  const found = report.rows.find((entry) => entry.harnessId === harnessId)
  if (found === undefined) throw new Error(`missing harness row ${harnessId}`)
  return found
}

function harnessesOf(store: CanonStore): string[] {
  return [...new Set(store.listAll().map((record) => record.harness))].sort()
}

function isoTimestamp(value: Date | string): string {
  return value instanceof Date ? value.toISOString() : value
}

function checkpointKey(harnessId: string, checkpoints: Map<string, SourceCheckpoint>): string {
  const found = [...checkpoints.keys()].find((key) => key.startsWith(`${harnessId}:`) && !key.startsWith(`${harnessId}-`))
  if (found === undefined) throw new Error(`missing checkpoint for ${harnessId}`)
  return found
}

function checkpointMap(store: CanonStore): Map<string, SourceCheckpoint> {
  return new Map(store.listSourceCheckpoints().map((checkpoint) => [
    `${checkpoint.harnessId}:${checkpoint.sourceKey}`,
    checkpoint,
  ]))
}

function seedRawHistory(store: CanonStore): string {
  const spanId = 'deadbeefcafebabe'
  store.upsert({
    spanId,
    traceId: '0123456789abcdef0123456789abcdef',
    parentSpanId: null,
    source: 'otlp',
    harness: 'cursor-agent',
    sessionId: 'raw-history-seed',
    name: 'cursor-agent:seed',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-07-01T00:00:00.000Z',
    durationMs: 5,
    status: 'ok',
    tokens: {
      freshInput: 3,
      cacheRead: 0,
      cacheCreation: 0,
      output: 1,
      reportedInput: 3,
      reportedOutput: 1,
    },
    content: { conversation_history: 'pre-refresh raw history' },
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: { 'session.id': 'raw-history-seed', preserved: true },
  })
  return spanId
}

describe('dash refresh deterministic CLI acceptance', () => {
  it('refreshes the default two-week fixture window with per-harness rows and canonical counts', async () => {
    const world = arrangeWorld()
    try {
      const report = await refresh(world)

      expect(report.historyWeeks).toBe(2)
      expect(report.exitCode).toBe(0)
      expect(report.rows.map((entry) => entry.harnessId)).toEqual([...JOB_IDS])

      expect(row(report, 'pi')).toMatchObject({ status: 'partial', created: 1, updated: 0 })
      expect(row(report, 'pi').problems).toBeGreaterThan(0)
      expect(row(report, 'antigravity')).toMatchObject({ status: 'ok', created: 1, updated: 0 })
      expect(row(report, 'antigravity-cli')).toMatchObject({ status: 'ok', created: 1, updated: 0 })
      expect(row(report, 'copilot-cli')).toMatchObject({ status: 'ok', created: 1, updated: 0 })
      expect(row(report, 'cursor')).toMatchObject({ status: 'ok', created: 1, updated: 0 })
      expect(row(report, 'droid')).toMatchObject({ status: 'ok', created: 1, updated: 0 })
      expect(row(report, 'antigravity-ide').status).toBe('unavailable')

      const records = world.store.listAll()
      expect(records).toHaveLength(6)
      expect(harnessesOf(world.store)).toEqual([
        'antigravity',
        'antigravity-cli',
        'copilot-cli',
        'cursor',
        'droid',
        'pi',
      ])
      expect(records.some((record) => record.sessionId === 'pi-cross' && isoTimestamp(record.timestamp).startsWith('2026-08-20'))).toBe(false)
      expect(records.some((record) => record.sessionId === 'pi-cross' && isoTimestamp(record.timestamp).startsWith('2026-08-10'))).toBe(false)
      expect(world.store.listSessions('pi')).toHaveLength(1)
      expect(world.store.listRuns('pi')).toHaveLength(1)
      expect(world.store.getHarnessRollup('pi')?.sampleCount).toBe(1)
      expect(world.store.getHarnessRollup('antigravity')?.harness).toBe('antigravity')
      expect(world.store.getHarnessRollup('antigravity-cli')?.harness).toBe('antigravity-cli')
      expect(report.derived.sessions).toBeGreaterThan(0)
      expect(report.derived.runs).toBeGreaterThan(0)
      expect(report.derived.rollups).toBeGreaterThan(0)
    } finally {
      world.store.close()
    }
  })

  it('refreshes the same immutable inputs again with New=0, Updated=0, stable checkpoints, span ids, and derived counts', async () => {
    const world = arrangeWorld()
    try {
      const first = await refresh(world)
      const spanIds = world.store.spanIds()
      const checkpoints = checkpointMap(world.store)
      const derived = { ...first.derived }

      const second = await refresh(world)

      expect(second.exitCode).toBe(0)
      for (const harnessId of ['pi', 'antigravity', 'antigravity-cli', 'copilot-cli', 'cursor', 'droid']) {
        expect(row(second, harnessId)).toMatchObject({ created: 0, updated: 0 })
        expect(['unchanged', 'ok']).toContain(row(second, harnessId).status)
      }
      expect(world.store.spanIds()).toEqual(spanIds)
      expect(checkpointMap(world.store)).toEqual(checkpoints)
      expect(second.derived).toEqual(derived)
    } finally {
      world.store.close()
    }
  })

  it('changes only the mutated native unit checkpoint, provenance, and canonical rows', async () => {
    const world = arrangeWorld()
    try {
      await refresh(world)
      const beforeCheckpoints = checkpointMap(world.store)
      const beforeSpanIds = new Set(world.store.spanIds())
      const piKey = checkpointKey('pi', beforeCheckpoints)

      writeFileSync(
        world.piPath,
        `${readFileSync(world.piPath, 'utf8').trimEnd()}\n${JSON.stringify({
          type: 'message',
          timestamp: '2026-09-07T10:00:00.000Z',
          sessionId: 'pi-cross',
          turnId: 'mutated',
          provider: 'pi',
          model: 'test-model',
          message: { role: 'user', content: 'changed native unit' },
        })}\n`,
      )

      const report = await refresh(world)
      expect(row(report, 'pi').created).toBe(1)
      expect(row(report, 'antigravity')).toMatchObject({ created: 0, updated: 0 })
      expect(row(report, 'cursor')).toMatchObject({ created: 0, updated: 0 })

      const afterCheckpoints = checkpointMap(world.store)
      expect(afterCheckpoints.get(piKey)?.revisionToken).not.toBe(beforeCheckpoints.get(piKey)?.revisionToken)
      expect(afterCheckpoints.get(piKey)?.recordCount).toBeGreaterThan(beforeCheckpoints.get(piKey)?.recordCount ?? 0)
      for (const [key, checkpoint] of beforeCheckpoints) {
        if (key === piKey) continue
        expect(afterCheckpoints.get(key)).toEqual(checkpoint)
      }

      const added = world.store.spanIds().filter((spanId) => !beforeSpanIds.has(spanId))
      expect(added).toHaveLength(1)
      expect(world.store.getRecordProvenance(added[0]!)?.harnessId).toBe('pi')
    } finally {
      world.store.close()
    }
  })

  it('processes only the newly uncovered interval plus genuinely changed records at --history-weeks 6', async () => {
    const world = arrangeWorld()
    try {
      const first = await refresh(world)
      expect(row(first, 'pi').created).toBe(1)
      expect(world.store.listAll().filter((record) => record.harness === 'pi')).toHaveLength(1)
      const beforeCheckpoints = checkpointMap(world.store)
      const beforeSpanIds = world.store.spanIds()

      const expanded = await refresh(world, 6)
      expect(expanded.historyWeeks).toBe(6)
      expect(row(expanded, 'pi').created).toBeGreaterThan(0)
      // Contract: a widened window must not re-upsert already-covered records.
      // Today T5 re-slices the full DateRange (orchestrator.ts:65 and :173–186)
      // and counts existing span ids as Updated (orchestrator.ts:241–244).
      expect(row(expanded, 'pi').updated).toBe(0)
      expect(row(expanded, 'antigravity').created).toBe(0)
      expect(row(expanded, 'antigravity').updated).toBe(0)
      expect(row(expanded, 'cursor').created).toBe(1)
      expect(row(expanded, 'cursor').updated).toBe(0)

      const piRecords = world.store.listAll().filter((record) => record.harness === 'pi')
      expect(piRecords).toHaveLength(3)
      expect(piRecords.map((record) => isoTimestamp(record.timestamp)).sort()).toEqual([
        '2026-08-10T12:00:00.000Z',
        '2026-08-20T12:00:00.000Z',
        '2026-09-06T10:00:00.000Z',
      ])
      expect(world.store.listAll().filter((record) => record.harness === 'cursor')).toHaveLength(2)

      const afterIds = new Set(world.store.spanIds())
      for (const spanId of beforeSpanIds) expect(afterIds.has(spanId)).toBe(true)

      const piCheckpoint = checkpointMap(world.store).get(checkpointKey('pi', checkpointMap(world.store)))
      expect(piCheckpoint?.coveredFromUtc).toBe('2026-08-01T18:00:00.000Z')
      const antigravityKey = checkpointKey('antigravity', beforeCheckpoints)
      expect(checkpointMap(world.store).get(antigravityKey)?.revisionToken)
        .toBe(beforeCheckpoints.get(antigravityKey)?.revisionToken)
    } finally {
      world.store.close()
    }
  })

  it('persists other harnesses when one source is unreadable, does not advance that checkpoint, and exits 1', async () => {
    const world = arrangeWorld()
    world.droidDiscoverable = false
    try {
      const failed = await refresh(world)
      expect(failed.exitCode).toBe(1)
      expect(row(failed, 'droid').status).toBe('failed')
      expect(row(failed, 'pi').status).toBe('partial')
      expect(harnessesOf(world.store)).toEqual([
        'antigravity',
        'antigravity-cli',
        'copilot-cli',
        'cursor',
        'pi',
      ])
      expect(world.store.listSourceCheckpoints('droid')).toEqual([])
      expect(world.store.getHarnessRollup('pi')).toBeDefined()
      const healthy = checkpointMap(world.store)

      world.droidDiscoverable = true
      const recovered = await refresh(world)
      expect(recovered.exitCode).toBe(0)
      expect(row(recovered, 'droid').status).toBe('ok')
      expect(row(recovered, 'droid').created).toBe(1)
      expect(row(recovered, 'pi')).toMatchObject({ created: 0, updated: 0 })
      expect(world.store.listSourceCheckpoints('droid')).toHaveLength(1)
      expect(world.store.listAll().some((record) => record.harness === 'droid')).toBe(true)
      const piKey = checkpointKey('pi', healthy)
      expect(checkpointMap(world.store).get(piKey)).toEqual(healthy.get(piKey))
    } finally {
      world.store.close()
    }
  })

  it('exits 2 for --history-weeks 0, -1, 1.5, and non-numeric values before the DB file exists', async () => {
    async function parseRefresh(args: string[]): Promise<CommanderError> {
      const program = new Command()
      program.exitOverride()
      registerKyberCommands(program)
      try {
        await program.parseAsync(['node', 'codeburn', 'dash', 'refresh', ...args])
        throw new Error('expected usage error')
      } catch (error) {
        if (error instanceof CommanderError) return error
        throw error
      }
    }

    for (const value of ['0', '-1', '1.5', 'abc']) {
      const root = tempRoot()
      const db = join(root, 'must-not-exist', 'canon.db')
      const error = await parseRefresh(['--history-weeks', value, '--db', db])
      expect(error.exitCode).toBe(2)
      expect(existsSync(db)).toBe(false)
      expect(db).not.toBe(USER_CANON)
    }
  })

  it('never persists harness gemini and keeps every required split identity on its own row', async () => {
    const world = arrangeWorld()
    try {
      const report = await refresh(world)
      expect(report.rows.map((entry) => entry.harnessId)).toEqual([...JOB_IDS])
      expect(new Set(report.rows.map((entry) => entry.harnessId)).size).toBe(JOB_IDS.length)

      for (const id of REQUIRED_SPLIT_IDS) {
        expect(report.rows.filter((entry) => entry.harnessId === id)).toHaveLength(1)
      }

      expect(harnessesOf(world.store).includes('gemini')).toBe(false)
      expect(world.store.listAll().some((record) => record.harness === 'gemini')).toBe(false)
      expect(world.store.getHarnessRollup('gemini')).toBeUndefined()
      expect(world.store.getHarnessRollup('antigravity')?.harness).toBe('antigravity')
      expect(world.store.getHarnessRollup('antigravity-cli')?.harness).toBe('antigravity-cli')
      expect(world.store.listSessions('antigravity').every((session) => session.harness === 'antigravity')).toBe(true)
      expect(world.store.listSessions('antigravity-cli').every((session) => session.harness === 'antigravity-cli')).toBe(true)
      expect(world.store.listRuns('antigravity').every((run) => run.harness === 'antigravity')).toBe(true)
      expect(world.store.listRuns('antigravity-cli').every((run) => run.harness === 'antigravity-cli')).toBe(true)
    } finally {
      world.store.close()
    }
  })

  it('slices cross-cutoff chats, restarts after partial failure, and preserves unmatched raw history', async () => {
    const world = arrangeWorld()
    world.droidDiscoverable = false
    try {
      const seed = seedRawHistory(world.store)
      const rawBefore = world.store.get(seed)
      expect(rawBefore?.raw).toMatchObject({ preserved: true })

      const partial = await refresh(world)
      expect(partial.exitCode).toBe(1)
      const inWindowPi = world.store.listAll().filter((record) => record.harness === 'pi')
      expect(inWindowPi).toHaveLength(1)
      expect(isoTimestamp(inWindowPi[0]!.timestamp)).toBe('2026-09-06T10:00:00.000Z')
      // Content older than 14 days is emptied in place; the row and raw payload stay.
      expect(world.store.get(seed)?.harness).toBe('cursor-agent')
      expect(world.store.get(seed)?.raw).toMatchObject({ preserved: true })
      expect(world.store.spanIds()).toContain(seed)
      expect(world.store.listAll().some((record) => record.harness === 'droid')).toBe(false)

      world.droidDiscoverable = true
      const restarted = await refresh(world)
      expect(restarted.exitCode).toBe(0)
      expect(row(restarted, 'droid').created).toBe(1)
      expect(row(restarted, 'pi')).toMatchObject({ created: 0, updated: 0 })
      expect(world.store.get(seed)?.raw).toMatchObject({ preserved: true })
      expect(world.store.spanIds()).toContain(seed)
    } finally {
      world.store.close()
    }
  })
})
