// T22 — the acceptance oracle for issue #319: "after a wipe only OTel ingestion repopulates the store".
//
// This is the ONE test in the feature that walks the whole loop in one process: fixture folder
// sources -> pre-wipe history -> a real wipe through the shared API -> new OTel telemetry ->
// several scheduled job-host ticks and one surface-initiated refresh -> the opt-in flip -> a
// bare CLI refresh. Every unit test of the feature proves one seam (the settings codec, the gate,
// the route, the host). Only a walk like this one can catch the regression the issue reported:
// folder history quietly reappearing by itself a few minutes after a wipe.
//
// What is real here:
//   * the store (CanonStore over a temp-file canon.db in WAL mode, shared by every participant),
//   * the fixture folder sources and the refresh pipeline that reads them (real
//     `refreshHarnessSources`, real writers, real projection), driven through the real
//     `dash refresh` command action via `registerKyberCommands`,
//   * the HTTP layer: a real loopback `http.Server` and the real `handleKyberRequest` routes,
//     so the clean, the settings PUT and the surface refresh are the production request paths,
//   * the central clean (`cleanDatabase` reached through `KyberBridge.cleanDatabase`),
//   * the OTLP receiver: a real `OtlpReceiver` on port 0, a real `IngestWriter`, and the real
//     `ingestBatch` normalization, fed by a real HTTP POST of a hand-built OTLP/JSON payload,
//   * the `JobHost` scheduling rules (cadence, pause, exit-code contract) on a fake clock,
//   * the coverage read and the store generation.
//
// What is faked, and why each fake is the right one:
//   * the clock. Ticks are advanced explicitly instead of waiting five real minutes, so the
//     test is deterministic and fast. Nothing else depends on wall-clock time.
//   * the job spawner. Production spawns a child process of the same binary; here the "child"
//     runs the same CLI argv in-process against the SAME store. That is the substitution that
//     makes the acceptance run possible at all - a real child would need the built binary - and
//     it does not weaken the claim under test, because the argv, the exit codes and the engine
//     behind them are the real ones.
//   * the refresh lock and the receiver pause. Both are cross-process/sibling-process
//     coordination with production defaults that would write into the real `~/.kyberdash` or
//     dial localhost:4318; neither is what this test is about, and touching the user's home
//     from a test is exactly what the repo's test contract forbids.
//   * the harness folder sources themselves: sanitized fixtures read through the fixture
//     provider/loader harness, the same arrangement as `refresh.integration.test.ts`. Real
//     folders would put the developer's own history in a store this test wipes.
//
// Nothing here reads the real home directory, the real `canon.db`, `server.json` or `jobs.lock`,
// spawns a process, or reaches off the loopback interface.

import { createServer, request, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { Command } from 'commander'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import { ingestBatch } from '../canon/ingest.js'
import { OtlpReceiver, OTLP_TRACES_PATH } from '../otel/receiver.js'
import { IngestWriter } from '../otel/writer.js'
import { handleKyberRequest } from '../server/routes.js'
import { KyberBridge } from '../server/bridge.js'
import { refreshHarnessSources } from '../refresh/orchestrator.js'
import { descriptorFor } from '../refresh/registry.js'
import type { HarnessSourceDescriptor } from '../refresh/types.js'
import { runMaintenancePass } from '../refresh/folder-import.js'
import { SETTING_KEYS, readSetting } from '../settings/shared-settings.js'
import { JobHost, type ChildResult, type JobClock, type JobSpawner } from './host.js'
import type { Provider, SessionSource } from '../providers/types.js'
import { registerKyberCommands } from '../cli/register.js'
import {
  COMMAND_STARTED_AT,
  copyFixture,
  fixtureProvider,
  jsonlLoader,
} from '../refresh/fixtures/integration-harness.js'

// The refresh lock and the receiver pause are cross-process coordination whose production
// defaults would touch the real ~/.kyberdash and dial a sibling receiver on :4318. Neither
// participates in the behaviour under test, so both are replaced by always-succeeding fakes
// (the same approach `coverage-after-clean.test.ts` takes for the lock).
vi.mock('../refresh/lock.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../refresh/lock.js')>()
  return {
    ...actual,
    acquireStoreRefreshLock: vi.fn(async () => ({
      outcome: 'acquired' as const,
      handle: { token: 'wipe-acceptance', release: async () => {}, verifyStillOwner: async () => true },
    })),
  }
})

vi.mock('../clean/pause.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../clean/pause.js')>()
  return {
    ...actual,
    pauseReceiver: vi.fn(async () => ({ outcome: 'paused' as const })),
    resumeReceiver: vi.fn(async () => {}),
  }
})

/** Session ids the folder fixtures carry; a record with one of these came from a folder. */
const FOLDER_SESSION_IDS = ['copilot-cli-1', 'agy-root'] as const

/** A harness every folder fixture is attributed to, for the folder-source read assertions. */
const FOLDER_HARNESSES = ['antigravity', 'copilot-cli'] as const

const MINUTE_MS = 60_000

// ---------------------------------------------------------------------------
// Harness
// ---------------------------------------------------------------------------

/**
 * A clock the test drives by hand. `setInterval` records nothing and fires nothing: the ticks in
 * this scenario are explicit `host.tick()` calls after an explicit advance, so a real timer
 * would only add a second, unrepeatable path to the same assertions.
 */
class FakeClock implements JobClock {
  private current: number

  constructor(start: number) {
    this.current = start
  }

  now(): number {
    return this.current
  }

  advance(ms: number): void {
    this.current += ms
  }

  setInterval(_fn: () => void, _everyMs: number): unknown {
    return Symbol('fake-interval')
  }

  clearInterval(_id: unknown): void {
    /* nothing was ever armed */
  }
}

/**
 * Runs a job argv in-process against the shared store, exactly as a spawned child of the same
 * binary would, and reports the child's exit code the way `child_process` reports it. The
 * spawner therefore keeps the JobHost's whole contract honest: it sees argv, a deferred
 * `exited` promise, and an exit code - no shortcuts around the host.
 */
class InProcessCliSpawner implements JobSpawner {
  readonly invocations: string[][] = []
  /** Resolves with each finished run's exit code, in invocation order. */
  readonly finished: Array<Promise<number>> = []

  constructor(
    /**
     * A fresh read-write handle per invocation, exactly as a spawned child opens its own
     * connection to the same canon.db. Sharing the harness's handle would be wrong twice over:
     * `dash refresh` closes the store it was given in its `finally`, and a real child is a
     * different process with a different connection. WAL mode is what lets the two see each
     * other's committed writes.
     */
    private readonly openStore: () => CanonStore,
    private readonly dependencies: {
      refreshHarnessSources: typeof refreshHarnessSources
      write: (line: string) => void
      writeError: (line: string) => void
    },
  ) {}

  spawn(_program: string, args: readonly string[]): { exited: Promise<ChildResult>; kill(): void } {
    const argv = [...args]
    this.invocations.push(argv)
    const stderr: string[] = []
    const run = (async (): Promise<ChildResult> => {
      const program = new Command()
      program.exitOverride()
      registerKyberCommands(program, {
        createStore: () => this.openStore(),
        refreshHarnessSources: this.dependencies.refreshHarnessSources,
        write: this.dependencies.write,
        writeError: (line) => {
          stderr.push(line)
          this.dependencies.writeError(line)
        },
        runMaintenancePass,
      })
      // `dash refresh` reports through process.exitCode, exactly as it does in production;
      // the assertion is on that value, so it is read and then cleared rather than faked.
      process.exitCode = undefined
      try {
        await program.parseAsync(['node', 'kyberdash', ...argv])
      } catch (err) {
        // A usage error carries commander's own exit code (2 for bad arguments); anything else
        // is the failure code the host records, exactly as a real child would report.
        const exitCode = (err as { exitCode?: unknown }).exitCode
        return { code: typeof exitCode === 'number' ? exitCode : 1, stderr: stderr.join('\n') }
      }
      return { code: process.exitCode ?? 0, stderr: stderr.join('\n') }
    })()
    const settled = run.then((result) => {
      const code = result.code ?? 1
      this.finished.push(Promise.resolve(code))
      return result
    })
    return { exited: settled, kill: () => {} }
  }
}

type Fixtures = {
  providers: () => Promise<Provider[]>
  // `descriptorFor` is a lookup over the registry, so its own return type carries `undefined`
  // for a harness that is not registered; `descriptors` below narrows that away by throwing,
  // which is why this is `HarnessSourceDescriptor[]` and not the lookup's return type.
  descriptors: () => HarnessSourceDescriptor[]
}

/** The whole arranged world, including the real OTLP receiver sitting on an ephemeral port. */
type World = {
  base: string
  store: CanonStore
  bridge: KyberBridge
  host: JobHost
  clock: FakeClock
  spawner: InProcessCliSpawner
  fixtures: Fixtures
  stdout: string[]
  root: string
  writer: IngestWriter
  otlpUrl: string
}

const temporaryRoots: string[] = []
const openSockets: Array<{ close: () => Promise<void> }> = []

beforeEach(() => {
  // `dash refresh` resolves its store through the one resolver (A13), so pointing the env var
  // at the temp canon.db is what makes the in-process child write the store under test. HOME is
  // deliberately NOT touched: nothing here may read or write the developer's ~/.kyberdash.
  temporaryRoots.push(mkdtempSync(join(tmpdir(), 'kyber-wipe-acceptance-')))
  process.env['KYBER_CANON_DB'] = join(temporaryRoots[temporaryRoots.length - 1]!, 'canon.db')
})

afterEach(async () => {
  delete process.env['KYBER_CANON_DB']
  for (const socket of openSockets.splice(0)) await socket.close()
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

/** The fixture folder sources: sanitized session files under a temp "home". */
function folderFixtures(root: string): Fixtures {
  const antigravityPath = copyFixture(
    'antigravity-conversation.jsonl',
    join(root, 'folders', '.gemini', 'antigravity', 'conversation.jsonl'),
  )
  const copilotPath = copyFixture(
    'copilot-cli-session.jsonl',
    join(root, 'folders', '.copilot', 'session.jsonl'),
  )
  const providers = async (): Promise<Provider[]> => [
    fixtureProvider(
      'antigravity',
      [{ path: antigravityPath, project: 'antigravity', provider: 'antigravity', sourceId: 'agy-t22' } as SessionSource],
      jsonlLoader,
    ),
    fixtureProvider(
      'copilot',
      [{ path: copilotPath, project: 'copilot', provider: 'copilot', sourceType: 'jsonl', sourceId: 'copilot-t22' } as SessionSource],
      jsonlLoader,
    ),
  ]
  const descriptors = () =>
    FOLDER_HARNESSES.map((id) => {
      const descriptor = descriptorFor(id)
      if (descriptor === undefined) throw new Error(`missing descriptor ${id}`)
      return descriptor
    })
  return { providers, descriptors }
}

/**
 * The refresh every folder read goes through, in the test and in the in-process child alike.
 * The fixed `commandStartedAt` is what makes the two-week window (and therefore the folder
 * records' timestamps) identical no matter when the test runs.
 */
function fixtureRefresh(fixtures: Fixtures) {
  return (
    store: CanonStore,
    _deps?: unknown,
    options?: { historyWeeks?: number; trigger?: 'cli' | 'tray' | 'scheduled' | 'web' },
  ): Promise<Awaited<ReturnType<typeof refreshHarnessSources>>> =>
    refreshHarnessSources(
      store,
      {
        getAllProviders: fixtures.providers,
        descriptors: fixtures.descriptors(),
        jobConcurrency: 2,
        writerCapacity: 2,
        commandStartedAt: COMMAND_STARTED_AT,
        parseAllSessions: async () => undefined,
      },
      { historyWeeks: options?.historyWeeks ?? 2, trigger: options?.trigger ?? 'cli' },
    )
}

// --- OTLP wire form --------------------------------------------------------

type OtlpAttribute = { key: string; value: { stringValue: string } | { intValue: string } }

type OtlpSpanFixture = {
  spanId: string
  name: string
  startNanos: bigint
  attributes: OtlpAttribute[]
}

/** One OTLP/JSON export request, in the hand-rolled collector encoding the receiver accepts. */
function otlpJsonRequest(spans: readonly OtlpSpanFixture[]): string {
  return JSON.stringify({
    resourceSpans: [
      {
        resource: { attributes: [{ key: 'service.name', value: { stringValue: 'kyberdash-t22' } }] },
        scopeSpans: [
          {
            scope: { name: 'kyberdash.t22', version: '1.0.0' },
            spans: spans.map((span) => ({
              traceId: '0123456789abcdef0123456789abcdef',
              spanId: span.spanId,
              name: span.name,
              kind: 3,
              startTimeUnixNano: span.startNanos.toString(),
              endTimeUnixNano: (span.startNanos + 1_000_000n).toString(),
              attributes: span.attributes,
              status: { code: 1 },
            })),
          },
        ],
      },
    ],
  })
}

/**
 * Claude Code's own counter shape. Chosen because the adapter vote claims it as a real harness
 * (`claude-code`), so the record is stored rather than quarantined - and because that harness is
 * NOT one of the folder fixtures, which keeps "telemetry arrived" and "a folder was read" two
 * distinguishable facts in the assertions below.
 */
const CLAUDE_ATTRIBUTES: OtlpAttribute[] = [
  { key: 'claude.deployment_mode', value: { stringValue: '1p' } },
  { key: 'input_tokens', value: { intValue: '120' } },
  { key: 'cache_read_tokens', value: { intValue: '400' } },
  { key: 'cache_creation_tokens', value: { intValue: '80' } },
  { key: 'output_tokens', value: { intValue: '30' } },
]

async function startReceiver(store: CanonStore): Promise<{
  receiver: OtlpReceiver
  writer: IngestWriter
  url: string
}> {
  // The real ingest mapping the collector service installs (otel/service.ts `toCanon`), minus
  // the audit row: `ingestBatch` is the normalization both the live receiver and a rebuild from
  // an export travel, so this is the production ingest path with the telemetry-panel counter
  // left out - the panel is not what this scenario is about.
  const writer = new IngestWriter(
    { upsertMany: (spans) => { ingestBatch(spans, store) } },
    { batchSize: 1, flushIntervalMs: 1 },
  )
  const receiver = new OtlpReceiver({ port: 0, store: writer })
  await receiver.start()
  return { receiver, writer, url: `http://127.0.0.1:${receiver.port}` }
}

/** Post spans as one OTLP request and wait for the writer to drain: ingestion is complete or failed. */
async function postOtlp(
  url: string,
  writer: IngestWriter,
  spans: readonly OtlpSpanFixture[],
): Promise<void> {
  const response = await fetch(`${url}${OTLP_TRACES_PATH}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: otlpJsonRequest(spans),
  })
  expect(response.status).toBe(200)
  await writer.flush()
}

// --- HTTP ------------------------------------------------------------------

type HttpResult = { status: number; json: unknown }

function send(
  base: string,
  method: string,
  path: string,
  body?: unknown,
): Promise<HttpResult> {
  return new Promise((resolve, reject) => {
    const payload = body === undefined ? undefined : JSON.stringify(body)
    const headers: Record<string, string> = {}
    if (payload !== undefined) {
      headers['content-type'] = 'application/json'
      headers['content-length'] = String(Buffer.byteLength(payload))
    }
    // No Origin header, exactly as the tray's native client sends one: the server's guard
    // admits loopback requests without one, so this is the production path for both surfaces.
    const req = request(`${base}${path}`, { method, headers }, (res) => {
      let text = ''
      res.setEncoding('utf8')
      res.on('data', (chunk: string) => {
        text += chunk
      })
      res.on('end', () => {
        let json: unknown
        try {
          json = JSON.parse(text)
        } catch {
          json = undefined
        }
        resolve({ status: res.statusCode ?? 0, json })
      })
    })
    req.on('error', reject)
    if (payload !== undefined) req.write(payload)
    req.end()
  })
}

async function startServer(
  bridge: KyberBridge,
  store: CanonStore,
  host: JobHost,
): Promise<{ server: Server; base: string }> {
  const server = createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://127.0.0.1')
    const handled = handleKyberRequest(req, res, url, bridge, { store, jobHost: host })
    if (!handled) {
      res.statusCode = 404
      res.end('{}')
    }
  })
  await new Promise<void>((resolve) => { server.listen(0, '127.0.0.1', () => resolve()) })
  const base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  openSockets.push({
    close: () => new Promise<void>((resolve) => { server.close(() => resolve()) }),
  })
  return { server, base }
}

// The receiver itself is not part of the world a test reasons about - it is started here only
// so the OTLP port is real, and stopped through the `openSockets` entry below - so the arranged
// world is exactly `World`, which already carries `writer` and `otlpUrl`.
async function arrange(): Promise<World> {
  const root = temporaryRoots[temporaryRoots.length - 1]!
  const dbPath = join(root, 'canon.db')
  const store = new CanonStore(dbPath)
  const fixtures = folderFixtures(root)
  const stdout: string[] = []
  const spawner = new InProcessCliSpawner(() => new CanonStore(dbPath), {
    refreshHarnessSources: fixtureRefresh(fixtures) as typeof refreshHarnessSources,
    write: (line) => { stdout.push(line) },
    writeError: (line) => { stdout.push(line) },
  })
  // A fixed epoch, unrelated to wall-clock time: the JobHost's cadence arithmetic is the only
  // thing that reads it, and this makes "three ticks later" an exact statement.
  const clock = new FakeClock(Date.parse('2026-10-10T09:00:00.000Z'))
  const host = new JobHost({
    store,
    stateDir: join(root, 'state'),
    program: 'kyberdash',
    clock,
    spawner,
    // The central maintenance pass, not a copy: purge + projection is engine work.
    // Its timestamp is the fake clock's, not the wall clock's: the arrangement claims
    // nothing in this world reads wall-clock time, and a `new Date()` here would break
    // that claim silently.
    maintenance: () => runMaintenancePass(store, new Date(clock.now())),
  })
  const bridge = new KyberBridge({ store, reopenCheckIntervalMs: 0 })
  const { base } = await startServer(bridge, store, host)
  const { receiver, writer, url } = await startReceiver(store)
  openSockets.push({ close: async () => { await receiver.stop() } })
  return { base, store, bridge, host, clock, spawner, fixtures, stdout, root, writer, otlpUrl: url }
}

function folderSourcedSessions(store: CanonStore): string[] {
  return store
    .listAll()
    // A record may carry no session id at all (`string | null | undefined`); only a real id
    // can be a folder session id, so the narrowing is part of the filter's meaning.
    .map((record) => record.sessionId)
    .filter(
      (sessionId): sessionId is string =>
        typeof sessionId === 'string' && (FOLDER_SESSION_IDS as readonly string[]).includes(sessionId),
    )
}

function folderImportSetting(store: CanonStore): string | undefined {
  return store.getMetadata(SETTING_KEYS.folderImportScheduled)
}

/**
 * Wait until the spawner has no child in flight AND the JobHost has recorded its outcome. The
 * host clears `busy` in a continuation of the promise the spawner resolves, so awaiting the
 * spawner alone can return a tick early and make the next tick look "still busy" - a test-only
 * ordering artefact, not the production cadence.
 */
async function settleJobs(world: Pick<World, 'spawner' | 'host'>): Promise<void> {
  await Promise.all(world.spawner.finished.splice(0))
  while (world.host.getStatus().state === 'running') {
    await new Promise((resolve) => setImmediate(resolve))
  }
}

/** A record that predates the content-retention floor, seeded straight into the store. */
function seedExpiredContentRow(store: CanonStore, spanId: string): CanonicalRecord {
  const record: CanonicalRecord = {
    spanId,
    traceId: 'fedcba9876543210fedcba9876543210',
    parentSpanId: null,
    source: 't22-seed',
    harness: 'claude-code',
    sessionId: 't22-expired-seed',
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    // Older than CONTENT_RETENTION_DAYS by years, so the purge is not sensitive to when the
    // test runs: any plausible "now" is past the floor.
    timestamp: '2024-01-01T00:00:00.000Z',
    durationMs: 10,
    status: 'ok',
    tokens: { freshInput: 1, cacheRead: 0, cacheCreation: 0, output: 1, reportedInput: 1, reportedOutput: 1 },
    content: { conversation_history: 'expired content that must be purged' },
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: { seeded: true },
  }
  store.upsert(record)
  return record
}

describe('issue #319 acceptance: after a wipe, only OTel ingestion repopulates the store', () => {
  it('holds across scheduled ticks and a surface refresh until folder import is turned on', async () => {
    const world = await arrange()
    const { store, bridge, host, clock, spawner, base } = world

    // --- Shared settings are absent, so folder import is off (D1) ------------------
    expect(folderImportSetting(store)).toBeUndefined()
    expect(readSetting(store, SETTING_KEYS.folderImportScheduled)).toBe('off')

    // --- (1) Pre-wipe state: folder history, one OTel record, one refresh_run row ---
    const seeded = await fixtureRefresh(world.fixtures)(store, undefined, {})
    expect(seeded.exitCode).toBe(0)
    expect(folderSourcedSessions(store).sort()).toEqual([...FOLDER_SESSION_IDS].sort())

    await postOtlp(world.otlpUrl, world.writer, [
      {
        spanId: 'aaaaaaaaaaaaaaa1',
        name: 'llm_request',
        startNanos: 1_757_000_000_000_000_000n,
        attributes: CLAUDE_ATTRIBUTES,
      },
    ])
    expect(store.get('aaaaaaaaaaaaaaa1')).toBeDefined()

    const refreshRunsBeforeWipe = store.listRefreshRuns()
    expect(refreshRunsBeforeWipe.length).toBeGreaterThan(0)
    const preWipeWindow = bridge.getRefreshState()
    expect(preWipeWindow.coveredFrom).not.toBeNull()

    const generationBeforeWipe = (await send(base, 'GET', '/api/kyber/jobs')).json as { storeGeneration: number }

    // --- (2) Wipe all through the shared API: the central clean, no re-import -------
    const clean = await send(base, 'POST', '/api/kyber/clean', { all: true, confirm: true })
    expect(clean.status).toBe(200)
    expect(clean.json).toMatchObject({ reingested: false, historyWeeks: null })
    expect(store.listAll()).toHaveLength(0)
    // The pre-wipe refresh_run rows survive as audit history (ADR 0032 D8) ...
    expect(store.listRefreshRuns().map((run) => run.id)).toContain(refreshRunsBeforeWipe[0]!.id)
    // ... but they no longer describe the store: the coverage read ignores rows stamped before
    // the wipe, so the footer must not advertise a window over data that no longer exists.
    const postWipeCoverage = bridge.getRefreshState()
    expect(postWipeCoverage.coveredFrom).toBeNull()
    expect(postWipeCoverage.historyWeeks).toBeNull()

    const generationAfterWipe = (await send(base, 'GET', '/api/kyber/jobs')).json as { storeGeneration: number }
    expect(generationAfterWipe.storeGeneration).not.toBe(generationBeforeWipe.storeGeneration)

    // --- (3) New OTel telemetry, through the real receiver and ingest path ---------
    await postOtlp(world.otlpUrl, world.writer, [
      {
        spanId: 'aaaaaaaaaaaaaaa2',
        name: 'llm_request',
        startNanos: 1_757_000_000_000_000_000n,
        attributes: CLAUDE_ATTRIBUTES,
      },
      {
        spanId: 'aaaaaaaaaaaaaaa3',
        name: 'llm_request',
        startNanos: 1_757_000_000_000_000_000n,
        attributes: CLAUDE_ATTRIBUTES,
      },
    ])
    expect(store.get('aaaaaaaaaaaaaaa2')).toBeDefined()
    expect(store.get('aaaaaaaaaaaaaaa3')).toBeDefined()

    // --- (4a) Three scheduled ticks on the job host, with the folder gate off -------
    // The expired row goes in now, before any tick: the maintenance pass is what has to remove
    // its content, and a folder-skipped tick must still run that pass (ADR 0018's purge has no
    // other production caller).
    seedExpiredContentRow(store, 'expired00000001')
    expect(Object.keys(store.get('expired00000001')!.content).length).toBeGreaterThan(0)

    for (let tick = 0; tick < 3; tick += 1) {
      // Past the cadence, so the host considers the tick due - the clock is the only input.
      clock.advance(10 * MINUTE_MS)
      await host.tick()
      // One job at a time is the host's rule, and a tick whose predecessor is still running
      // is deliberately a no-op. In production the next tick is minutes later and the child has
      // long exited; here the same ordering is stated explicitly by awaiting each child.
      await settleJobs(world)
    }

    // --- (4b) One surface-initiated refresh, the same way a browser button does -------
    const refresh = await send(base, 'POST', '/api/kyber/refresh', { surface: 'web' })
    expect(refresh.status).toBe(202)
    await settleJobs(world)

    // --- (5) The assertions the issue was about ------------------------------------
    // Every job the host started was a scheduled or web run, i.e. a gated one.
    expect(spawner.invocations).toEqual([
      ['dash', 'refresh', '--trigger', 'scheduled'],
      ['dash', 'refresh', '--trigger', 'scheduled'],
      ['dash', 'refresh', '--trigger', 'scheduled'],
      ['dash', 'refresh', '--trigger', 'web'],
    ])
    expect(store.listAll().map((record) => record.spanId).sort()).toEqual(
      ['aaaaaaaaaaaaaaa2', 'aaaaaaaaaaaaaaa3', 'expired00000001'],
    )
    expect(folderSourcedSessions(store)).toEqual([])
    // Retention kept running on the gated ticks: the row survives, its content does not.
    expect(store.get('expired00000001')).toBeDefined()
    expect(Object.keys(store.get('expired00000001')!.content)).toEqual([])
    // A skipped refresh read no sources, so it must not claim to have: no new refresh_run row.
    expect(store.listRefreshRuns().map((run) => run.id)).toEqual(
      refreshRunsBeforeWipe.map((run) => run.id),
    )
    // And the coverage read still reports no folder window.
    expect(bridge.getRefreshState().coveredFrom).toBeNull()

    // --- (6) Opt in, and the very next tick brings the folders back -----------------
    const put = await send(base, 'PUT', '/api/kyber/settings', { folderImportScheduled: true })
    expect(put.status).toBe(200)
    expect(put.json).toMatchObject({ folderImportScheduled: true })

    clock.advance(10 * MINUTE_MS)
    await host.tick()
    await settleJobs(world)

    expect(folderSourcedSessions(store).sort()).toEqual([...FOLDER_SESSION_IDS].sort())
    expect(store.listRefreshRuns().length).toBe(refreshRunsBeforeWipe.length + 1)

    // --- (7) A bare CLI refresh imports folders even with the setting off (D4) -------
    // Turn it off again so the CLI claim is tested against the gate's closed state, which is
    // the state the operator is actually in by default.
    const off = await send(base, 'PUT', '/api/kyber/settings', { folderImportScheduled: false })
    expect(off.status).toBe(200)
    expect(readSetting(store, SETTING_KEYS.folderImportScheduled)).toBe('off')

    store.wipeHarnesses(['antigravity', 'copilot-cli'])
    expect(folderSourcedSessions(store)).toEqual([])

    const exitCodes: number[] = []
    const cliRun = spawner
      .spawn('kyberdash', ['dash', 'refresh'])
      .exited.then((result: ChildResult) => {
        exitCodes.push(result.code ?? -1)
      })
    await cliRun

    expect(exitCodes).toEqual([0])
    expect(folderSourcedSessions(store).sort()).toEqual([...FOLDER_SESSION_IDS].sort())

    await host.close()
  }, 60_000)
})