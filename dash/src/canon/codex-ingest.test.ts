import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { createCodexProvider } from '../providers/codex.js'
import { clearCodexMemCaches } from '../ingest/codex-cache.js'
import { descriptorFor, sourceKeyFor } from '../refresh/registry.js'
import { refreshHarnessSources } from '../refresh/orchestrator.js'
import { iterateNativeUnits, sliceCallsToWindow } from '../refresh/source-reader.js'
import type { ParsedProviderCall } from '../providers/types.js'
import { CanonStore } from './store.js'

/**
 * T1 diagnostic for issue #196: what the REAL Codex ingest path persists on
 * source_checkpoint rows for three rollout shapes.
 *
 * Path exercised, unmocked except for the provider's home directory:
 *   createCodexProvider(<tmp home>)           providers/codex.ts
 *     -> refreshHarnessSources                refresh/orchestrator.ts
 *     -> runHarnessJob                        refresh/orchestrator.ts (private, reached via the above)
 *     -> iterateNativeUnits / readNativeUnit  refresh/source-reader.ts
 *     -> sliceCallsToWindow                   refresh/source-reader.ts
 *     -> ingestProviders + codexReader        synth/provider.ts, synth/readers/codex.ts
 *     -> CanonStore.commitSourceUnit          canon/store.ts (real sqlite file in tmp)
 *
 * Fixture rollouts (all originator `codex-cli`):
 *   in-window   session_meta + turn_context(model) + token_count, 2026-09-30
 *   pre-window  same shape, entirely 2026-08-01 (before the 2-week window)
 *   no-usage    in-window session_meta only; no turn_context, no token_count
 *
 * Window: commandStartedAt 2026-10-01T12:00:00Z, 2 weeks => start 2026-09-17.
 *
 * Passing tests characterise the T3 contract: a zero-record unit persists why
 * (`window_filtered` or `no_recordable_events`) on the checkpoint.
 */

const COMMAND_STARTED_AT = new Date('2026-10-01T12:00:00.000Z')
const HARNESS = 'codex-cli'

const roots: string[] = []
let codexHome: string
let cacheDir: string
let store: CanonStore
let previousCacheDir: string | undefined
let fixtures: Fixtures

function meta(sessionId: string, timestamp: string): string {
  return JSON.stringify({
    type: 'session_meta',
    timestamp,
    payload: { session_id: sessionId, cwd: '/Users/test/proj', originator: 'codex-cli' },
  })
}

function turnContext(timestamp: string): string {
  return JSON.stringify({ type: 'turn_context', timestamp, payload: { model: 'gpt-5.3-codex' } })
}

function tokenCount(timestamp: string): string {
  const usage = {
    input_tokens: 1000,
    cached_input_tokens: 0,
    output_tokens: 200,
    reasoning_output_tokens: 0,
    total_tokens: 1200,
  }
  return JSON.stringify({
    type: 'event_msg',
    timestamp,
    payload: { type: 'token_count', info: { last_token_usage: usage, total_token_usage: usage } },
  })
}

function writeRollout(day: string, name: string, lines: string[]): string {
  const [year, month, dd] = day.split('-')
  const dir = join(codexHome, 'sessions', year!, month!, dd!)
  mkdirSync(dir, { recursive: true })
  const path = join(dir, name)
  writeFileSync(path, lines.join('\n') + '\n')
  return path
}

type Fixtures = { inWindow: string; preWindow: string; noUsage: string }

function writeFixtures(): Fixtures {
  const inWindow = writeRollout('2026-09-30', 'rollout-in-window.jsonl', [
    meta('sess-in', '2026-09-30T10:00:00Z'),
    turnContext('2026-09-30T10:00:05Z'),
    tokenCount('2026-09-30T10:01:00Z'),
  ])
  const preWindow = writeRollout('2026-08-01', 'rollout-pre-window.jsonl', [
    meta('sess-pre', '2026-08-01T10:00:00Z'),
    turnContext('2026-08-01T10:00:05Z'),
    tokenCount('2026-08-01T10:01:00Z'),
  ])
  const noUsage = writeRollout('2026-09-30', 'rollout-no-usage.jsonl', [meta('sess-none', '2026-09-30T11:00:00Z')])
  return { inWindow, preWindow, noUsage }
}

function provider() {
  // Explicit primaryDir + empty launcherRoots: no launcher-home detection touches the real ~/.codex.
  return createCodexProvider(codexHome, { primaryDir: codexHome, launcherRoots: [] })
}

async function refresh() {
  return refreshHarnessSources(
    store,
    {
      getAllProviders: async () => [provider()],
      descriptors: ['codex-cli', 'codex-desktop', 'codex-unclassified'].map(id => descriptorFor(id)!),
      commandStartedAt: COMMAND_STARTED_AT,
      parseAllSessions: async () => undefined,
      jobConcurrency: 1,
    },
    { historyWeeks: 2 },
  )
}

beforeEach(() => {
  const root = mkdtempSync(join(tmpdir(), 'codex-ingest-t1-'))
  roots.push(root)
  codexHome = join(root, 'codex-home')
  cacheDir = join(root, 'cache')
  mkdirSync(codexHome, { recursive: true })
  mkdirSync(cacheDir, { recursive: true })
  previousCacheDir = process.env['KYBERDASH_CACHE_DIR']
  process.env['KYBERDASH_CACHE_DIR'] = cacheDir
  clearCodexMemCaches()
  store = new CanonStore(join(root, 'canon.db'))
  fixtures = writeFixtures()
})

afterEach(() => {
  store.close()
  if (previousCacheDir === undefined) delete process.env['KYBERDASH_CACHE_DIR']
  else process.env['KYBERDASH_CACHE_DIR'] = previousCacheDir
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true })
})

const WINDOW = { start: new Date('2026-09-17T12:00:00.000Z'), end: COMMAND_STARTED_AT }

function checkpointOf(path: string) {
  return store.getSourceCheckpoint(HARNESS, sourceKeyFor(HARNESS, { path, project: 'x', provider: 'codex' }))
}

async function parseAll(path: string): Promise<ParsedProviderCall[]> {
  const calls: ParsedProviderCall[] = []
  const parser = provider().createSessionParser({ path, project: 'x', provider: 'codex' }, new Set())
  for await (const call of parser.parse()) calls.push(call)
  return calls
}

describe('codex ingest: checkpoint rows for three rollout shapes (T1 diagnostic)', () => {
  it('discovers all three rollouts as codex-cli native units and attributes the in-window one to envelopes', async () => {
    const units = await iterateNativeUnits(HARNESS, {
      providers: [provider()],
      dateRange: WINDOW,
      parseAllSessions: async () => undefined,
    })

    const byPath = new Map(units.map(unit => [unit.source.path, unit]))
    expect([...byPath.keys()].sort()).toEqual(
      [fixtures.inWindow, fixtures.noUsage, fixtures.preWindow].sort(),
    )
    expect(byPath.get(fixtures.inWindow)?.envelopes).toHaveLength(1)
    expect(byPath.get(fixtures.preWindow)?.envelopes).toHaveLength(0)
    expect(byPath.get(fixtures.noUsage)?.envelopes).toHaveLength(0)
    // No unit carries a SourceProblem: nothing in the reader marks the empty ones.
    for (const unit of units) expect(unit.problems, unit.source.path).toEqual([])
  })

  it('pre-window drop happens in sliceCallsToWindow, silently: parser yields the call, slice removes it with no problem', async () => {
    const parsed = await parseAll(fixtures.preWindow)
    expect(parsed).toHaveLength(1)

    const sliced = sliceCallsToWindow(parsed, WINDOW)

    expect(sliced.inWindow).toEqual([])
    expect(sliced.problems).toEqual([])
  })

  it('no-usage session: the Codex parser itself yields zero calls (no window involved)', async () => {
    expect(await parseAll(fixtures.noUsage)).toEqual([])
  })

  it('(a) in-window rollout: checkpoint is ok with record_count > 0 and one canonical record exists', async () => {
    const report = await refresh()

    const checkpoint = checkpointOf(fixtures.inWindow)
    expect(checkpoint?.lastStatus).toBe('ok')
    expect(checkpoint?.recordCount).toBeGreaterThan(0)
    expect(checkpoint?.lastErrorCode).toBeNull()
    expect(store.count()).toBe(1)
    const row = report.rows.find(entry => entry.harnessId === HARNESS)
    expect(row).toMatchObject({ units: 3, created: 1, problems: 0, status: 'ok' })
  })

  it('(b) pre-window rollout: checkpoint is status ok, record_count 0, reason window_filtered, no problem, no quarantine', async () => {
    await refresh()

    const checkpoint = checkpointOf(fixtures.preWindow)
    expect(checkpoint).toMatchObject({ lastStatus: 'ok', recordCount: 0, lastErrorCode: 'window_filtered', unitCount: 1 })
    expect(checkpoint?.coveredFromUtc).toBe(WINDOW.start.toISOString())
    expect(store.getProblems().filter(problem => problem.spanId.includes(checkpoint!.sourceKey))).toEqual([])
    expect(store.listQuarantine()).toEqual([])
  })

  it('(c) in-window session without token_count/model: checkpoint is status ok, record_count 0, reason no_recordable_events, no problem', async () => {
    await refresh()

    const checkpoint = checkpointOf(fixtures.noUsage)
    expect(checkpoint).toMatchObject({ lastStatus: 'ok', recordCount: 0, lastErrorCode: 'no_recordable_events', unitCount: 1 })
    expect(store.getProblems().filter(problem => problem.spanId.includes(checkpoint!.sourceKey))).toEqual([])
    expect(store.listQuarantine()).toEqual([])
  })

  // Honest-unobservability contract (issue #196): a zero-record unit says why.
  it('contract: zero-record checkpoint carries a reason (honest-unobservability)', async () => {
    await refresh()

    for (const path of [fixtures.preWindow, fixtures.noUsage]) {
      const checkpoint = checkpointOf(path)
      expect(checkpoint?.recordCount, path).toBe(0)
      const problems = store.getProblems().filter(problem => problem.spanId.includes(checkpoint!.sourceKey))
      const hasReason = checkpoint?.lastErrorCode != null || problems.length > 0
      expect(hasReason, `zero-record checkpoint for ${path} has neither lastErrorCode nor a problem row`).toBe(true)
    }
  })
})
