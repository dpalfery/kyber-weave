import { describe, expect, it } from 'vitest'

import {
  GEMINI_SELECTOR_LABEL,
  cacheAvailability,
  harnessSessionId,
  harnessSessionKey,
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
    expect(normalizeHarnessName('cursor-agent')).toBe('cursor-agent')
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
  })
})

describe('T6 derivation does not collapse or seed Gemini', () => {
  it('builds separate sessions and rollups for Cursor and Cursor Agent', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      record('c1', 'cursor', 'native-shared'),
      record('a1', 'cursor-agent', 'native-shared', { timestamp: '2026-09-03T10:00:05.000Z' }),
    ])

    const report = await buildSessions(store)
    expect(report.built).toBe(2)

    expect(store.listSessions('cursor').map((s) => s.sessionId).sort()).not.toEqual(
      store.listSessions('cursor-agent').map((s) => s.sessionId).sort(),
    )
    expect(store.listSessions('cursor')).toHaveLength(1)
    expect(store.listSessions('cursor-agent')).toHaveLength(1)

    const cursorRollup = buildHarnessRollup(store, 'cursor')
    const agentRollup = buildHarnessRollup(store, 'cursor-agent')
    expect((cursorRollup.payload as { sessionCount: number }).sessionCount).toBe(1)
    expect((agentRollup.payload as { sessionCount: number }).sessionCount).toBe(1)
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
      'cursor-agent',
      'kilo-shared-runtime',
      'kilo-vscode-legacy',
      'pi',
    ]) {
      expect(ids).toContain(required)
    }
    expect(ids).toEqual([...new Set(ids)].sort())
    expect(ids.length).toBeGreaterThanOrEqual(HARNESS_DESCRIPTORS.length)
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
    expect(prefixAvailability('cursor-agent').harness).toBe('cursor-agent')
    expect(prefixAvailability('cursor-agent').status).toBe(prefixAvailability('cursor').status)
  })
})

describe('T6 session stamp uses the split canonical id', () => {
  it('stamps cursor-agent on the derived session rather than cursor', () => {
    const row = buildSessionRow('s1', [record('a1', 'cursor-agent', 's1')], approximateO200kBase)
    expect(row.harness).toBe('cursor-agent')
    expect((row.payload as { harness: string }).harness).toBe('cursor-agent')
  })
})

describe('harness-qualified session ids', () => {
  it('qualifies a key only when it spans several harnesses, and inverts only its own prefix', () => {
    expect(harnessSessionId('cursor', 'sess-1', 1)).toBe('sess-1')
    expect(harnessSessionId('cursor', 'sess-1', 2)).toBe('cursor:sess-1')
    expect(harnessSessionKey('cursor:sess-1', 'cursor')).toBe('sess-1')
    // A native key with a colon of its own is not a prefix for another harness.
    expect(harnessSessionKey('workspace:sess-1', 'cursor')).toBeUndefined()
    expect(harnessSessionKey('cursor:', 'cursor')).toBeUndefined()
  })

  it('gathers one canonical harness share of a split key, whatever raw name its records arrived under', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      record('split-vs', 'copilot-chat', 'k-split'),
      record('split-cur', 'cursor', 'k-split', { timestamp: '2026-09-03T10:01:00.000Z' }),
      record('split-gem', 'gemini', 'k-split', { timestamp: '2026-09-03T10:02:00.000Z' }),
    ])

    await buildSessions(store)
    expect(store.builtSessionIds().sort()).toEqual(['copilot-vscode:k-split', 'cursor:k-split'])

    const share = store.recordsForDerivedSession('copilot-vscode:k-split', 'copilot-vscode')
    expect(share.key).toBe('k-split')
    expect(share.records.map((r) => r.spanId)).toEqual(['split-vs'])
    expect(store.recordsForDerivedSession('cursor:k-split', 'cursor').records.map((r) => r.spanId)).toEqual([
      'split-cur',
    ])
    // A raw provider name finds the canonical share it belongs to.
    expect(store.recordsForDerivedSession('copilot-vscode:k-split', 'copilot-chat').records.map((r) => r.spanId)).toEqual([
      'split-vs',
    ])
    store.close()
  })

  it('reads a bare key verbatim even when it contains a colon', () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([record('colon-1', 'cursor', 'cursor:native-id')])

    const share = store.recordsForDerivedSession('cursor:native-id', 'cursor')
    expect(share.key).toBe('cursor:native-id')
    expect(share.records.map((r) => r.spanId)).toEqual(['colon-1'])
    expect(store.recordsForDerivedSession('unknown-key', 'cursor')).toEqual({ key: 'unknown-key', records: [] })
    store.close()
  })
})
