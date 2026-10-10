import { describe, expect, it } from 'vitest'

import {
  GEMINI_SELECTOR_LABEL,
  cacheAvailability,
  SessionIdentities,
  isExcludedHarnessIdentity,
  normalizeHarnessName,
  prefixAvailability,
} from './measurability.js'
import { buildHarnessRollup } from './harnesses.js'
import { buildRuns } from './runs.js'
import { buildSessionRow, buildSessions } from './sessions.js'
import { CanonStore } from './store.js'
import { isNotMeasurable } from './types.js'
import type { CanonicalRecord } from './types.js'
import { approximateO200kBase } from './tokens.js'
import { HARNESS_DESCRIPTORS } from '../refresh/registry.js'

function usage(over: Partial<CanonicalRecord['tokens']> = {}): CanonicalRecord['tokens'] {
  return {
    freshInput: 100,
    cacheRead: 0,
    cacheCreation: 0,
    output: 10,
    reportedInput: 100,
    reportedOutput: 10,
    ...over,
  }
}

function record(spanId: string, harness: string, sessionId: string, over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId,
    traceId: `trace-${sessionId}`,
    parentSpanId: null,
    source: harness,
    harness,
    sessionId,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-09-03T10:00:00.000Z',
    durationMs: 10,
    status: 'ok',
    tokens: usage({ reportedInput: 500, freshInput: 400, cacheRead: 100, output: 20 }),
    content: { system_prompt: 'hello' },
    cost: { basis: 'unknown', status: 'no_rate' },
    ...over,
  }
}

describe('T6 split harness identity', () => {
  it('keeps required surfaces as distinct canonical ids', () => {
    expect(normalizeHarnessName('antigravity')).toBe('antigravity')
    expect(normalizeHarnessName('antigravity-cli')).toBe('antigravity-cli')
    expect(normalizeHarnessName('antigravity-ide')).toBe('antigravity-ide')
    expect(normalizeHarnessName('copilot-cli')).toBe('copilot-cli')
    expect(normalizeHarnessName('copilot-vscode')).toBe('copilot-vscode')
    expect(normalizeHarnessName('copilot-jetbrains')).toBe('copilot-jetbrains')
    expect(normalizeHarnessName('copilot-agent')).toBe('copilot-agent')
    expect(normalizeHarnessName('cursor')).toBe('cursor')
    // Issue #182: Cursor Agent is Cursor's agent front-end, not a second
    // harness — one session key under both names is one `cursor` session.
    expect(normalizeHarnessName('cursor-agent')).toBe('cursor')
    expect(normalizeHarnessName('cline')).toBe('cline')
    expect(normalizeHarnessName('cline-cli')).toBe('cline-cli')
  })

  it('does not treat Gemini as a persisted harness id while keeping the selector label', () => {
    expect(isExcludedHarnessIdentity('gemini')).toBe(true)
    expect(isExcludedHarnessIdentity('Gemini')).toBe(true)
    expect(isExcludedHarnessIdentity('antigravity')).toBe(false)
    expect(GEMINI_SELECTOR_LABEL).toBe('Gemini')
  })

  it('maps Kilo only as far as native evidence proves', () => {
    expect(normalizeHarnessName('kilo')).toBe('kilo-shared-runtime')
    expect(normalizeHarnessName('kilo-code')).toBe('kilo-shared-runtime')
    expect(normalizeHarnessName('kilo-shared-runtime')).toBe('kilo-shared-runtime')
    expect(normalizeHarnessName('kilo-vscode-legacy')).toBe('kilo-vscode-legacy')
    expect(normalizeHarnessName('kilo-cli')).toBe('kilo-shared-runtime')
  })

  it('does not silently pick Claude CLI or Codex CLI for an unclassified client', () => {
    expect(normalizeHarnessName('claude')).toBe('claude-unclassified')
    expect(normalizeHarnessName('codex-unclassified')).toBe('codex-unclassified')
    expect(normalizeHarnessName('codex-cli')).toBe('codex-cli')
    expect(normalizeHarnessName('claude-cli')).toBe('claude-cli')
    // Issue #182: the desktop OTLP export is Claude Code under a second name.
    expect(normalizeHarnessName('claude-desktop')).toBe('claude-code')
  })
})

describe('T6 derivation does not collapse or seed Gemini', () => {
  it('merges twin front-ends into one session instead of splitting', async () => {
    // Issue #182: `cursor` and `cursor-agent` rows under one key are one
    // harness reached through two front ends, so the bare key stays one row.
    const store = new CanonStore(':memory:')
    store.upsert(record('c1', 'cursor', 'native-shared'))
    await buildSessions(store)
    expect(store.getSessionPayload('native-shared')).toBeDefined()

    store.upsert(record('a1', 'cursor-agent', 'native-shared', {
      timestamp: '2026-09-03T10:00:05.000Z',
    }))
    const report = await buildSessions(store)

    expect(report.pruned).toBe(0)
    expect(store.getSessionPayload('native-shared')).toBeDefined()
    expect(store.getSessionPayload('cursor:native-shared')).toBeUndefined()
    expect(store.getSessionPayload('cursor-agent:native-shared')).toBeUndefined()
    store.close()
  })

  it('merges Cursor and Cursor Agent shares into one session and rollup', async () => {
    // Issue #182: twin front-end shares of one key build one session, one
    // run, and one rollup sample — never a twin row per surface.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      record('c1', 'cursor', 'native-shared'),
      record('a1', 'cursor-agent', 'native-shared', { timestamp: '2026-09-03T10:00:05.000Z' }),
    ])

    const report = await buildSessions(store)
    expect(report.built).toBe(1)

    expect(store.listSessions('cursor')).toHaveLength(1)
    expect(store.listSessions('cursor-agent')).toHaveLength(0)

    const cursorRollup = buildHarnessRollup(store, 'cursor')
    expect((cursorRollup.payload as { sessionCount: number }).sessionCount).toBe(1)
    store.close()
  })

  it('filters Antigravity variants to their own rows', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      record('g1', 'antigravity', 'agy-1'),
      record('c1', 'antigravity-cli', 'agy-cli-1'),
      record('i1', 'antigravity-ide', 'agy-ide-1'),
    ])
    await buildSessions(store)

    expect(store.listSessions('antigravity')).toHaveLength(1)
    expect(store.listSessions('antigravity-cli')).toHaveLength(1)
    expect(store.listSessions('antigravity-ide')).toHaveLength(1)
    expect(store.listRuns('antigravity').every((r) => r.harness === 'antigravity')).toBe(true)
    expect(store.listRuns('antigravity-cli').every((r) => r.harness === 'antigravity-cli')).toBe(true)
    expect(store.listRuns('antigravity-ide')).toHaveLength(1)
    store.close()
  })

  it('does not emit a Gemini session, run, or rollup from gemini-harness records', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([record('g1', 'gemini', 'gem-1')])
    const report = await buildSessions(store)

    expect(report.built).toBe(0)
    expect(store.listSessions('gemini')).toHaveLength(0)
    expect(store.listRuns('gemini')).toHaveLength(0)

    const rollups = buildHarnessRollup(store)
    expect(rollups.map((r) => r.harness)).not.toContain('gemini')
    expect(store.getHarnessRollup('gemini')).toBeUndefined()
    store.close()
  })

  it('builds an antigravity session and rollup from live agy records while Gemini stays excluded', async () => {
    // Issue #195: rows the fix re-attributes to `antigravity` must project
    // into sessions and rollups; the `gemini` identity itself stays dropped.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      record('a1', 'antigravity', 'agy-1', { source: 'agy' }),
      record('g1', 'gemini', 'gem-1', { source: 'agy' }),
    ])
    const report = await buildSessions(store)

    expect(report.built).toBe(1)
    expect(store.listSessions('antigravity')).toHaveLength(1)
    expect(store.listSessions('gemini')).toHaveLength(0)

    const rollups = buildHarnessRollup(store)
    expect(rollups.map((r) => r.harness)).toContain('antigravity')
    expect(rollups.map((r) => r.harness)).not.toContain('gemini')
    expect(store.getHarnessRollup('antigravity')?.sampleCount).toBeGreaterThan(0)
    store.close()
  })

  it('seeds registered split harnesses, not Gemini, on an empty store', () => {
    const store = new CanonStore(':memory:')
    const rollups = buildHarnessRollup(store)
    const ids = rollups.map((r) => r.harness)

    expect(ids).not.toContain('gemini')
    for (const required of [
      'antigravity',
      'antigravity-cli',
      'antigravity-ide',
      'copilot-cli',
      'copilot-vscode',
      'cursor',
      'kilo-shared-runtime',
      'kilo-vscode-legacy',
      'pi',
    ]) {
      expect(ids).toContain(required)
    }
    // Issue #182: folded front-ends seed no rollup of their own — a row that
    // can never gain a session is dead UI beside the harness it merged into.
    expect(ids).not.toContain('cursor-agent')
    expect(ids).not.toContain('claude-desktop')
    expect(ids).toEqual([...new Set(ids)].sort())
    // Seeds are canonical harness ids: folded front-ends share their owner's
    // seed, so the bound counts canonical ids, not raw descriptors.
    const canonicalSeeds = new Set(HARNESS_DESCRIPTORS.map((d) => normalizeHarnessName(d.harnessId)))
    expect(ids.length).toBeGreaterThanOrEqual(canonicalSeeds.size)
    store.close()
  })

  it('does not join explicit run ids across harnesses', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      record('c1', 'copilot-cli', 'cli-sess', { raw: { 'gen_ai.run.id': 'shared-run' } }),
      record('v1', 'copilot-vscode', 'vscode-sess', { raw: { 'gen_ai.run.id': 'shared-run' } }),
    ])
    await buildRuns(store)

    const cli = store.listRuns('copilot-cli')
    const vscode = store.listRuns('copilot-vscode')
    expect(cli).toHaveLength(1)
    expect(vscode).toHaveLength(1)
    expect(cli[0]!.runId).not.toBe(vscode[0]!.runId)
    store.close()
  })
})

describe('T6 unavailable is not a measured zero', () => {
  it('keeps empty registered harness dimensions null with a reason', () => {
    const store = new CanonStore(':memory:')
    const rollup = buildHarnessRollup(store, 'pi')
    expect(rollup.sampleCount).toBe(0)
    expect(rollup.cacheHitRate).toBeNull()
    expect(rollup.contextPressureMedian).toBeNull()
    expect(isNotMeasurable(rollup.measurability['cache_hit_rate'])).toBe(true)
    store.close()
  })

  it('records a measured cache-hit zero when the harness exported counters', () => {
    const store = new CanonStore(':memory:')
    store.upsertSession({
      sessionId: 'copilot-cli:zero-cache',
      harness: 'copilot-cli',
      payload: {
        summary: { total_input: 1000, total_cache_read: 0 },
      },
    })
    const rollup = buildHarnessRollup(store, 'copilot-cli')
    expect(rollup.cacheHitRate).toBe(0)
    expect(rollup.measurability['cache_hit_rate']).toBe('measured')
    store.close()
  })

  it('inherits Copilot CLI cache survey from Copilot without collapsing the id', () => {
    const cache = cacheAvailability('copilot-cli')
    expect(cache.harness).toBe('copilot-cli')
    expect(cache.status).toBe('supported')
    expect(prefixAvailability('cursor-agent').harness).toBe('cursor')
    expect(prefixAvailability('cursor-agent').status).toBe(prefixAvailability('cursor').status)
  })
})

describe('T6 session stamp uses the folded canonical id', () => {
  it('stamps cursor on the derived session rather than cursor-agent', () => {
    const row = buildSessionRow('s1', [record('a1', 'cursor-agent', 's1')], approximateO200kBase)
    expect(row.harness).toBe('cursor')
    expect((row.payload as { harness: string }).harness).toBe('cursor')
  })
})

describe('session identities', () => {
  it('keeps a single-harness key as its id and qualifies each share of a split key', () => {
    const identities = new SessionIdentities([
      { key: 'solo', harness: 'cursor' },
      { key: 'split', harness: 'cursor' },
      { key: 'split', harness: 'copilot-chat' },
    ])

    expect(identities.idFor('solo', 'cursor')).toBe('solo')
    expect(identities.idFor('split', 'cursor')).toBe('cursor:split')
    // Shares are keyed by canonical harness, whatever raw name the record arrived under.
    expect(identities.idFor('split', 'copilot-chat')).toBe('copilot-vscode:split')
    expect(identities.shareOf('copilot-vscode:split')).toEqual({ key: 'split', harness: 'copilot-vscode' })
    expect(identities.shareOf('split')).toBeUndefined()
  })

  it('re-prefixes a split share whose id a native key already holds, and maps each id back', () => {
    const identities = new SessionIdentities([
      { key: 'cursor:k', harness: 'cursor' },
      { key: 'k', harness: 'cursor' },
      { key: 'k', harness: 'copilot-cli' },
    ])

    expect(identities.idFor('cursor:k', 'cursor')).toBe('cursor:k')
    expect(identities.idFor('k', 'cursor')).toBe('cursor:cursor:k')
    expect(identities.idFor('k', 'copilot-cli')).toBe('copilot-cli:k')
    expect(identities.shareOf('cursor:k')).toEqual({ key: 'cursor:k', harness: 'cursor' })
    expect(identities.shareOf('cursor:cursor:k')).toEqual({ key: 'k', harness: 'cursor' })
  })

  it('mints the same ids whatever order the pairs arrive in', () => {
    const pairs = [
      { key: 'cursor:k', harness: 'cursor' },
      { key: 'k', harness: 'cursor' },
      { key: 'k', harness: 'copilot-cli' },
      { key: 'j', harness: 'cursor' },
      { key: 'j', harness: 'aider' },
    ]
    const forward = new SessionIdentities(pairs)
    const reverse = new SessionIdentities([...pairs].reverse())
    for (const { key, harness } of pairs) {
      expect(reverse.idFor(key, harness)).toBe(forward.idFor(key, harness))
    }
  })

  it('gives an excluded identity no share, and mints a late share clear of the key it joins', () => {
    const identities = new SessionIdentities([
      { key: 'k', harness: 'cursor' },
      { key: 'k', harness: 'gemini' },
    ])

    expect(identities.idFor('k', 'cursor')).toBe('k')
    expect(identities.idFor('k', 'gemini')).toBeUndefined()
    // A share the table never saw (a record ingested after it was built) must
    // not take the bare key its sibling already owns.
    expect(identities.claim('k', 'aider')).toBe('aider:k')
    expect(identities.shareOf('aider:k')).toEqual({ key: 'k', harness: 'aider' })
  })

  it('builds each split share from its own records under the id the table assigns', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      record('split-vs', 'copilot-chat', 'k-split'),
      record('split-cur', 'cursor', 'k-split', { timestamp: '2026-09-03T10:01:00.000Z' }),
      record('split-gem', 'gemini', 'k-split', { timestamp: '2026-09-03T10:02:00.000Z' }),
      record('native-cur', 'cursor', 'cursor:k-split', { timestamp: '2026-09-03T10:03:00.000Z' }),
    ])

    await buildSessions(store)

    expect(store.builtSessionIds().sort()).toEqual(['copilot-vscode:k-split', 'cursor:cursor:k-split', 'cursor:k-split'])
    expect(store.recordsForShare('k-split', 'copilot-chat').map((r) => r.spanId)).toEqual(['split-vs'])
    expect(store.recordsForShare('k-split', 'cursor').map((r) => r.spanId)).toEqual(['split-cur'])
    expect(store.recordsForShare('cursor:k-split', 'cursor').map((r) => r.spanId)).toEqual(['native-cur'])
    store.close()
  })
})
