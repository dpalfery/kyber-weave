import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, describe, expect, it } from 'vitest'

import {
  buildHarnessRollup,
  computeMedian,
  computeP95,
  coverageFor,
  COVERAGE_DIMENSIONS,
} from '../kyber/canon/harnesses.js'
import {
  harnessDimensionAvailability,
} from '../kyber/canon/measurability.js'
import { CanonStore, SCHEMA_VERSION } from '../kyber/canon/store.js'
import { isNotMeasurable } from '../kyber/canon/types.js'
import type { SessionRow, ExecutionRow, RunRow } from '../kyber/canon/types.js'

const tempDirs: string[] = []

function tempStorePath(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-harnesses-test-'))
  tempDirs.push(dir)
  return join(dir, 'canon.db')
}

afterEach(() => {
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()!
    rmSync(dir, { recursive: true, force: true })
  }
})

describe('harnessDimensionAvailability & coverageFor (ADR 0009, ADR 0011, Task E2/E4)', () => {
  it('declares dimensions for fully-supported harness (copilot)', () => {
    expect(harnessDimensionAvailability('copilot', 'cache_hit_rate')).toBe('measured')
    expect(harnessDimensionAvailability('copilot', 'prefix_stability')).toBe('measured')
    expect(harnessDimensionAvailability('copilot', 'context_pressure')).toBe('measured')
    expect(harnessDimensionAvailability('copilot', 'tool_yield')).toBe('measured')
    expect(harnessDimensionAvailability('copilot', 'delegation_overhead')).toBe('measured')
    expect(harnessDimensionAvailability('copilot', 'field_coverage')).toBe('measured')

    expect(coverageFor('copilot')).toBe(1.0)
  })

  it('declares not_measurable with explicit reason for Cursor absent telemetry (never zero)', () => {
    const cacheAvail = harnessDimensionAvailability('cursor', 'cache_hit_rate')
    expect(isNotMeasurable(cacheAvail)).toBe(true)
    if (isNotMeasurable(cacheAvail)) {
      expect(cacheAvail.reason).toContain('without cache read/write counters')
    }

    const prefixAvail = harnessDimensionAvailability('cursor', 'prefix_stability')
    expect(isNotMeasurable(prefixAvail)).toBe(true)
    if (isNotMeasurable(prefixAvail)) {
      expect(prefixAvail.reason).toContain('without multi-turn prefix reconstruction')
    }

    const toolAvail = harnessDimensionAvailability('cursor', 'tool_yield')
    expect(isNotMeasurable(toolAvail)).toBe(true)
    if (isNotMeasurable(toolAvail)) {
      expect(toolAvail.reason).toContain('does not export tool definition schemas')
    }

    const delAvail = harnessDimensionAvailability('cursor', 'delegation_overhead')
    expect(isNotMeasurable(delAvail)).toBe(true)
    if (isNotMeasurable(delAvail)) {
      expect(delAvail.reason).toContain('does not export execution hierarchy')
    }

    // Context pressure and token usage are measurable for Cursor
    expect(harnessDimensionAvailability('cursor', 'context_pressure')).toBe('measured')
    expect(harnessDimensionAvailability('cursor', 'token_usage')).toBe('measured')

    // Degraded coverage reflects the missing telemetry
    const coverage = coverageFor('cursor')
    expect(coverage).toBeGreaterThan(0)
    expect(coverage).toBeLessThan(1.0)
    expect(coverage).toBe(Number((2 / COVERAGE_DIMENSIONS.length).toFixed(4)))
  })

  it('declares not_measurable for uninstrumented harnesses (Aider, OpenCode)', () => {
    // Aider lacks native OTLP telemetry and structured cache counters
    const aiderCache = harnessDimensionAvailability('aider', 'cache_hit_rate')
    expect(isNotMeasurable(aiderCache)).toBe(true)
    if (isNotMeasurable(aiderCache)) {
      expect(aiderCache.reason).toContain('LiteLLM')
    }
    expect(coverageFor('aider')).toBe(0.0)

    // OpenCode has experimental OpenTelemetry disabled
    const opencodeCache = harnessDimensionAvailability('opencode', 'cache_hit_rate')
    expect(isNotMeasurable(opencodeCache)).toBe(true)
    if (isNotMeasurable(opencodeCache)) {
      expect(opencodeCache.reason).toContain('experimental OpenTelemetry disabled')
    }
    expect(coverageFor('opencode')).toBe(0.0)

    // Context pressure percentiles must also be not_measurable (ADR 0009 / Task E2)
    const aiderPressureMedian = harnessDimensionAvailability('aider', 'context_pressure_median')
    expect(isNotMeasurable(aiderPressureMedian)).toBe(true)
    if (isNotMeasurable(aiderPressureMedian)) {
      expect(aiderPressureMedian.reason).toContain('LiteLLM')
    }

    const aiderPressureP95 = harnessDimensionAvailability('aider', 'context_pressure_p95')
    expect(isNotMeasurable(aiderPressureP95)).toBe(true)
    if (isNotMeasurable(aiderPressureP95)) {
      expect(aiderPressureP95.reason).toContain('LiteLLM')
    }

    const opencodePressureMedian = harnessDimensionAvailability('opencode', 'context_pressure_median')
    expect(isNotMeasurable(opencodePressureMedian)).toBe(true)
    if (isNotMeasurable(opencodePressureMedian)) {
      expect(opencodePressureMedian.reason).toContain('experimental OpenTelemetry disabled')
    }

    const opencodePressureP95 = harnessDimensionAvailability('opencode', 'context_pressure_p95')
    expect(isNotMeasurable(opencodePressureP95)).toBe(true)
    if (isNotMeasurable(opencodePressureP95)) {
      expect(opencodePressureP95.reason).toContain('experimental OpenTelemetry disabled')
    }
  })

  it('declares not_measurable for uncatalogued harness', () => {
    const avail = harnessDimensionAvailability('unknown-agent-xyz', 'cache_hit_rate')
    expect(isNotMeasurable(avail)).toBe(true)
    if (isNotMeasurable(avail)) {
      expect(avail.reason).toContain('not catalogued in the telemetry inventory')
    }
  })
})

describe('Percentile calculation helpers', () => {
  it('computes median for odd and even length sorted arrays', () => {
    expect(computeMedian([])).toBe(0)
    expect(computeMedian([0.42])).toBe(0.42)
    expect(computeMedian([0.1, 0.2, 0.5])).toBe(0.2)
    expect(computeMedian([0.1, 0.2, 0.3, 0.4])).toBe(0.25)
  })

  it('computes nearest-rank p95', () => {
    expect(computeP95([])).toBe(0)
    expect(computeP95([0.5])).toBe(0.5)

    const samples = Array.from({ length: 100 }, (_, i) => (i + 1) / 100)
    // 95th element in 1..100 is 0.95
    expect(computeP95(samples)).toBe(0.95)
  })
})

describe('buildHarnessRollup — Empty / uncollected harnesses', () => {
  it('emits explicit not_measurable with reason and no computed dimensions for zero-run harness', () => {
    const store = new CanonStore(':memory:')
    const rollup = buildHarnessRollup(store, 'opencode')

    expect(rollup.harness).toBe('opencode')
    expect(rollup.sampleCount).toBe(0)
    // Computed dimensions MUST BE null, never fabricated or zero!
    expect(rollup.contextPressureMedian).toBeNull()
    expect(rollup.contextPressureP95).toBeNull()
    expect(rollup.cacheHitRate).toBeNull()
    expect(rollup.toolYield).toBeNull()
    expect(rollup.delegationOverhead).toBeNull()
    expect(rollup.fieldCoverage).toBe(0.0)

    // Measurability flags must explain the reason
    expect(isNotMeasurable(rollup.measurability['context_pressure'])).toBe(true)
    expect(isNotMeasurable(rollup.measurability['cache_hit_rate'])).toBe(true)
    expect(isNotMeasurable(rollup.measurability['tool_yield'])).toBe(true)
    expect(isNotMeasurable(rollup.measurability['delegation_overhead'])).toBe(true)

    store.close()
  })

  it('includes all catalogued harnesses when building rollups over empty store', () => {
    const store = new CanonStore(':memory:')
    const rollups = buildHarnessRollup(store)

    expect(rollups.length).toBeGreaterThanOrEqual(10)
    const harnesses = rollups.map((r) => r.harness)
    expect(harnesses).toContain('copilot')
    expect(harnesses).toContain('claude-code')
    expect(harnesses).toContain('cursor')
    expect(harnesses).toContain('aider')
    expect(harnesses).toContain('codex')
    expect(harnesses).toContain('gemini')

    for (const r of rollups) {
      expect(r.sampleCount).toBe(0)
      expect(r.cacheHitRate).toBeNull()
    }

    store.close()
  })
})

describe('buildHarnessRollup — Context pressure aggregation', () => {
  it('aggregates median and p95 peak context pressure across sessions', () => {
    const store = new CanonStore(':memory:')

    // Create 5 sessions with distinct peak context pressures: 0.10, 0.20, 0.30, 0.40, 0.50
    const pressures = [0.10, 0.20, 0.30, 0.40, 0.50]
    for (let i = 0; i < pressures.length; i++) {
      const p = pressures[i]!
      const sessionRow: SessionRow = {
        sessionId: `sess-copilot-${i}`,
        harness: 'copilot',
        payload: {
          id: `sess-copilot-${i}`,
          session_id: `sess-copilot-${i}`,
          harness: 'copilot',
          context: {
            measurable: true,
            turns: [
              { pressure: p * 0.5, headroom: 1000 },
              { pressure: p, headroom: 500 }, // Peak turn
            ],
          },
          summary: {
            turn_count: 2,
            total_input: 1000,
            total_output: 100,
            total_cache_read: 200,
          },
        },
      }
      store.upsertSession(sessionRow)
    }

    const rollup = buildHarnessRollup(store, 'copilot')

    expect(rollup.sampleCount).toBe(5)
    expect(rollup.contextPressureMedian).toBe(0.3)
    expect(rollup.contextPressureP95).toBe(0.5)
    expect(rollup.measurability['context_pressure']).toBe('measured')
    expect(rollup.measurability['context_pressure_median']).toBe('measured')
    expect(rollup.measurability['context_pressure_p95']).toBe('measured')

    store.close()
  })

  it('marks context pressure availability as derived if any session used derived counts', () => {
    const store = new CanonStore(':memory:')

    store.upsertSession({
      sessionId: 'sess-derived-1',
      harness: 'copilot',
      payload: {
        id: 'sess-derived-1',
        session_id: 'sess-derived-1',
        harness: 'copilot',
        context: {
          measurable: true,
          derivedCounts: true,
          turns: [{ pressure: 0.25 }],
        },
        summary: { total_input: 500 },
      },
    })

    const rollup = buildHarnessRollup(store, 'copilot')
    expect(rollup.contextPressureMedian).toBe(0.25)
    expect(rollup.measurability['context_pressure']).toBe('derived')

    store.close()
  })
})

describe('buildHarnessRollup — Cache hit rate & non-zero fallback', () => {
  it('computes totalCacheRead / totalInput correctly for cache-supporting harness', () => {
    const store = new CanonStore(':memory:')

    // Session 1: 300 cache read / 1000 input
    store.upsertSession({
      sessionId: 'sess-c1',
      harness: 'copilot',
      payload: {
        id: 'sess-c1',
        session_id: 'sess-c1',
        harness: 'copilot',
        summary: {
          total_cache_read: 300,
          total_input: 1000,
        },
      },
    })

    // Session 2: 600 cache read / 1000 input
    store.upsertSession({
      sessionId: 'sess-c2',
      harness: 'copilot',
      payload: {
        id: 'sess-c2',
        session_id: 'sess-c2',
        harness: 'copilot',
        summary: {
          total_cache_read: 600,
          total_input: 1000,
        },
      },
    })

    const rollup = buildHarnessRollup(store, 'copilot')
    // Total cache read: 900, Total input: 2000 -> 900 / 2000 = 0.45
    expect(rollup.cacheHitRate).toBe(0.45)
    expect(rollup.measurability['cache_hit_rate']).toBe('measured')

    store.close()
  })

  it('emits null (never 0) for Cursor cache hit rate even when sessions have tokens', () => {
    const store = new CanonStore(':memory:')

    store.upsertSession({
      sessionId: 'sess-cursor-1',
      harness: 'cursor',
      payload: {
        id: 'sess-cursor-1',
        session_id: 'sess-cursor-1',
        harness: 'cursor',
        context: {
          measurable: true,
          turns: [{ pressure: 0.15 }],
        },
        summary: {
          total_cache_read: 0,
          total_input: 5000,
        },
      },
    })

    const rollup = buildHarnessRollup(store, 'cursor')

    // Cursor cannot measure cache hit rate; must be null, never 0!
    expect(rollup.cacheHitRate).toBeNull()
    const avail = rollup.measurability['cache_hit_rate']
    expect(isNotMeasurable(avail)).toBe(true)
    if (isNotMeasurable(avail)) {
      expect(avail.reason).toContain('without cache read/write counters')
    }

    // Context pressure is still measurable
    expect(rollup.contextPressureMedian).toBe(0.15)

    store.close()
  })
})

describe('buildHarnessRollup — Tool yield aggregation', () => {
  it('computes invoked tools over defined tools ratio', () => {
    const store = new CanonStore(':memory:')

    store.upsertSession({
      sessionId: 'sess-tool-1',
      harness: 'copilot',
      payload: {
        id: 'sess-tool-1',
        session_id: 'sess-tool-1',
        harness: 'copilot',
        tools: [
          { schema_tokens: 100, invocations: 3, turns_resident: 2 },
          { schema_tokens: 100, invocations: 0, turns_resident: 2 },
          { schema_tokens: 100, invocations: 1, turns_resident: 2 },
          { schema_tokens: 100, invocations: 0, turns_resident: 2 },
        ],
        summary: { total_input: 500 },
      },
    })

    const rollup = buildHarnessRollup(store, 'copilot')
    // 2 invoked out of 4 defined = 0.50
    expect(rollup.toolYield).toBe(0.5)
    expect(rollup.measurability['tool_yield']).toBe('measured')

    store.close()
  })

  it('aggregates tool yield from ASAD servers if tools array is not present', () => {
    const store = new CanonStore(':memory:')

    store.upsertSession({
      sessionId: 'sess-srv-1',
      harness: 'copilot',
      payload: {
        id: 'sess-srv-1',
        session_id: 'sess-srv-1',
        harness: 'copilot',
        servers: [
          { server: 'fs', is_mcp: true, tools: 6, unused_tools: 2, invocations: 4, schema_tokens: 200, unused_cost: 0 },
        ],
        summary: { total_input: 500 },
      },
    })

    const rollup = buildHarnessRollup(store, 'copilot')
    // 4 used out of 6 defined = 0.6667
    expect(rollup.toolYield).toBe(0.6667)
    expect(rollup.measurability['tool_yield']).toBe('measured')

    store.close()
  })

  it('emits null (never 0) for tool yield on harnesses lacking tool definitions (Cursor)', () => {
    const store = new CanonStore(':memory:')

    store.upsertSession({
      sessionId: 'sess-cursor-tool',
      harness: 'cursor',
      payload: {
        id: 'sess-cursor-tool',
        session_id: 'sess-cursor-tool',
        harness: 'cursor',
        summary: { total_input: 500 },
      },
    })

    const rollup = buildHarnessRollup(store, 'cursor')
    expect(rollup.toolYield).toBeNull()
    expect(isNotMeasurable(rollup.measurability['tool_yield'])).toBe(true)

    store.close()
  })
})

describe('buildHarnessRollup — Delegation overhead aggregation', () => {
  it('computes delegation overhead as child tokens / total tokens', () => {
    const store = new CanonStore(':memory:')

    // Create run and executions
    const run: RunRow = {
      runId: 'run-del-1',
      harness: 'copilot',
      groupingBasis: 'explicit',
      executionCount: 2,
    }
    store.upsertRun(run)

    // Root execution session: 800 input, 200 output = 1000 tokens
    store.upsertSession({
      sessionId: 'sess-root',
      harness: 'copilot',
      payload: {
        id: 'sess-root',
        session_id: 'sess-root',
        harness: 'copilot',
        summary: { total_input: 800, total_output: 200 },
      },
    })

    // Child execution session: 400 input, 100 output = 500 tokens
    store.upsertSession({
      sessionId: 'sess-child',
      harness: 'copilot',
      payload: {
        id: 'sess-child',
        session_id: 'sess-child',
        harness: 'copilot',
        isSubagent: true,
        summary: { total_input: 400, total_output: 100 },
      },
    })

    const rootExec: ExecutionRow = {
      executionId: 'exec-root',
      runId: 'run-del-1',
      sessionId: 'sess-root',
      harness: 'copilot',
      isRoot: true,
      parentExecutionId: null,
      parentLinkage: 'measured',
    }

    const childExec: ExecutionRow = {
      executionId: 'exec-child',
      runId: 'run-del-1',
      sessionId: 'sess-child',
      harness: 'copilot',
      isRoot: false,
      parentExecutionId: 'exec-root',
      parentLinkage: 'measured',
    }

    store.upsertExecutions([rootExec, childExec])

    const rollup = buildHarnessRollup(store, 'copilot')
    // Child tokens: 500, Total tokens: 1500 -> 500 / 1500 = 0.3333
    expect(rollup.delegationOverhead).toBe(0.3333)
    expect(rollup.measurability['delegation_overhead']).toBe('measured')

    store.close()
  })

  it('measures delegation overhead as 0.0 when all executions are root (no delegations)', () => {
    const store = new CanonStore(':memory:')

    store.upsertSession({
      sessionId: 'sess-solo',
      harness: 'copilot',
      payload: {
        id: 'sess-solo',
        session_id: 'sess-solo',
        harness: 'copilot',
        summary: { total_input: 500, total_output: 100 },
      },
    })

    store.upsertExecution({
      executionId: 'exec-solo',
      runId: 'run-solo',
      sessionId: 'sess-solo',
      harness: 'copilot',
      isRoot: true,
      parentExecutionId: null,
      parentLinkage: 'measured',
    })

    const rollup = buildHarnessRollup(store, 'copilot')
    // Zero child executions -> 0.0 delegation overhead, perfectly measurable
    expect(rollup.delegationOverhead).toBe(0.0)
    expect(rollup.measurability['delegation_overhead']).toBe('measured')

    store.close()
  })

  it('emits null (never 0) for delegation overhead on unlinked harnesses (Cursor)', () => {
    const store = new CanonStore(':memory:')

    store.upsertSession({
      sessionId: 'sess-cursor-del',
      harness: 'cursor',
      payload: {
        id: 'sess-cursor-del',
        session_id: 'sess-cursor-del',
        harness: 'cursor',
        summary: { total_input: 1000 },
      },
    })

    const rollup = buildHarnessRollup(store, 'cursor')
    expect(rollup.delegationOverhead).toBeNull()
    expect(isNotMeasurable(rollup.measurability['delegation_overhead'])).toBe(true)

    store.close()
  })
})

describe('CanonStore — harness_rollup CRUD & migration v5 -> v6', () => {
  it('persists and retrieves harness rollups with getHarnessRollup and listHarnessRollups', () => {
    const store = new CanonStore(':memory:')

    const row = {
      harness: 'test-harness',
      sampleCount: 12,
      contextPressureMedian: 0.28,
      contextPressureP95: 0.64,
      cacheHitRate: 0.72,
      toolYield: 0.45,
      delegationOverhead: 0.15,
      fieldCoverage: 0.8333,
      measurability: {
        context_pressure: 'measured' as const,
        cache_hit_rate: 'measured' as const,
        tool_yield: 'measured' as const,
        delegation_overhead: 'measured' as const,
        field_coverage: 'measured' as const,
      },
      payload: { note: 'test payload' },
    }

    store.upsertHarnessRollup(row)
    expect(store.harnessRollupCount()).toBe(1)

    const fetched = store.getHarnessRollup('test-harness')
    expect(fetched).toBeDefined()
    expect(fetched?.harness).toBe('test-harness')
    expect(fetched?.sampleCount).toBe(12)
    expect(fetched?.contextPressureMedian).toBe(0.28)
    expect(fetched?.contextPressureP95).toBe(0.64)
    expect(fetched?.cacheHitRate).toBe(0.72)
    expect(fetched?.toolYield).toBe(0.45)
    expect(fetched?.delegationOverhead).toBe(0.15)
    expect(fetched?.fieldCoverage).toBe(0.8333)
    expect(fetched?.measurability).toEqual(row.measurability)
    expect(fetched?.payload).toEqual({ note: 'test payload' })

    const list = store.listHarnessRollups()
    expect(list.length).toBe(1)
    expect(list[0]?.harness).toBe('test-harness')

    store.deleteHarnessRollup('test-harness')
    expect(store.harnessRollupCount()).toBe(0)
    expect(store.getHarnessRollup('test-harness')).toBeUndefined()

    store.close()
  })

  it('migrates a v5 store to v6 and creates harness_rollup table without data loss', () => {
    const path = tempStorePath()
    const db = new DatabaseSync(path)

    // Construct a v5 schema database manually
    db.exec(`
      CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
      CREATE TABLE records (
        span_id TEXT PRIMARY KEY,
        trace_id TEXT,
        parent_span_id TEXT,
        source TEXT NOT NULL,
        harness TEXT NOT NULL,
        session_id TEXT,
        name TEXT NOT NULL,
        op TEXT NOT NULL,
        kind TEXT NOT NULL,
        timestamp TEXT NOT NULL,
        duration_ms INTEGER NOT NULL,
        status TEXT NOT NULL,
        tokens_json TEXT NOT NULL,
        content_json TEXT NOT NULL,
        cost_json TEXT NOT NULL,
        measurability_json TEXT,
        parts_json BLOB,
        raw BLOB
      );
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY,
        harness TEXT NOT NULL,
        started TEXT,
        payload TEXT NOT NULL
      );
      CREATE TABLE run (
        run_id TEXT PRIMARY KEY,
        harness TEXT NOT NULL,
        started TEXT,
        grouping_basis TEXT NOT NULL
      );
      CREATE TABLE execution (
        execution_id TEXT PRIMARY KEY,
        run_id TEXT NOT NULL,
        session_id TEXT,
        parent_execution_id TEXT,
        harness TEXT NOT NULL,
        started TEXT,
        parent_linkage_json TEXT NOT NULL
      );
      INSERT INTO metadata (key, value) VALUES ('schema_version', '5');
      INSERT INTO session (session_id, harness, payload) VALUES ('s1', 'copilot', '{"id":"s1"}');
    `)
    db.close()

    // Opening with CanonStore should execute migrations forward
    const store = new CanonStore(path)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(SCHEMA_VERSION).toBeGreaterThanOrEqual(6)

    // Assert existing v5 data is preserved
    expect(store.sessionCount()).toBe(1)

    // Assert harness_rollup table exists and operates cleanly
    expect(store.harnessRollupCount()).toBe(0)
    store.upsertHarnessRollup({
      harness: 'copilot',
      sampleCount: 1,
      contextPressureMedian: 0.1,
      contextPressureP95: 0.1,
      cacheHitRate: 0.5,
      toolYield: 1.0,
      delegationOverhead: 0.0,
      fieldCoverage: 1.0,
      measurability: { test: 'measured' },
    })
    expect(store.harnessRollupCount()).toBe(1)
    expect(store.getHarnessRollup('copilot')?.harness).toBe('copilot')

    store.close()
  })

  it('rebuilds harness rollups idempotently on subsequent runs', () => {
    const store = new CanonStore(':memory:')

    store.upsertSession({
      sessionId: 'sess-idem',
      harness: 'copilot',
      payload: {
        id: 'sess-idem',
        session_id: 'sess-idem',
        harness: 'copilot',
        summary: { total_input: 1000, total_cache_read: 200 },
      },
    })

    const run1 = buildHarnessRollup(store, 'copilot')
    expect(run1.cacheHitRate).toBe(0.2)
    const countAfterFirst = store.harnessRollupCount()

    // Second run should cleanly overwrite without duplicating rows or erroring
    const run2 = buildHarnessRollup(store, 'copilot')
    expect(run2.cacheHitRate).toBe(0.2)
    expect(store.harnessRollupCount()).toBe(countAfterFirst)

    store.close()
  })
})

describe('CanonStore — session retrieval encapsulation (ADR 0008, Task E2)', () => {
  it('lists sessions optionally filtered by harness without breaching store encapsulation', () => {
    const store = new CanonStore(':memory:')
    store.upsertSession({
      sessionId: 'sess-copilot-1',
      harness: 'copilot',
      label: 'test copilot session',
      started: '2026-09-01T10:00:00.000Z',
      payload: { context: { measurable: true } },
    })
    store.upsertSession({
      sessionId: 'sess-copilot-2',
      harness: 'copilot',
      label: 'newer copilot session',
      started: '2026-09-02T10:00:00.000Z',
      payload: { context: { measurable: true } },
    })
    store.upsertSession({
      sessionId: 'sess-cursor-1',
      harness: 'cursor',
      label: 'cursor session',
      started: '2026-09-01T12:00:00.000Z',
      payload: { context: { measurable: true } },
    })

    const allSessions = store.listSessions()
    expect(allSessions).toHaveLength(3)
    // Newest first
    expect(allSessions[0]?.sessionId).toBe('sess-copilot-2')

    const copilotSessions = store.listSessions('copilot')
    expect(copilotSessions).toHaveLength(2)
    expect(copilotSessions.map((s) => s.sessionId)).toEqual(['sess-copilot-2', 'sess-copilot-1'])

    const cursorSessions = store.listSessions('cursor')
    expect(cursorSessions).toHaveLength(1)
    expect(cursorSessions[0]?.sessionId).toBe('sess-cursor-1')

    const absentSessions = store.listSessions('aider')
    expect(absentSessions).toHaveLength(0)

    const session = store.getSession('sess-copilot-1')
    expect(session).toBeDefined()
    expect(session?.sessionId).toBe('sess-copilot-1')
    expect(session?.harness).toBe('copilot')
    expect(session?.payload).toEqual({ context: { measurable: true } })

    expect(store.getSession('nonexistent')).toBeUndefined()

    store.close()
  })
})

describe('buildHarnessRollup — sessions are streamed, not materialized', () => {
  // The dimensions each used to walk an array holding every session payload
  // for the harness. Payloads are the largest objects in the store — 995 MB on
  // the measured corpus, one session of it 264 MB, nearly all of it the
  // timeline's preserved span attributes — and building that array took this
  // phase's peak to 4.4 GB. The reductions below must be unchanged.

  function pressureSession(id: string, harness: string, peak: number): SessionRow {
    return {
      sessionId: id,
      harness,
      payload: {
        id,
        session_id: id,
        harness,
        context: { measurable: true, turns: [{ pressure: peak * 0.5 }, { pressure: peak }] },
        summary: { total_input: 1000, total_output: 100, total_cache_read: 250 },
        tools: [{ invocations: 1 }, { invocations: 0 }],
      },
    }
  }

  it('reduces every session the harness owns, not just the first page of them', () => {
    const store = new CanonStore(':memory:')
    const peaks = [0.1, 0.2, 0.3, 0.4, 0.5]
    peaks.forEach((peak, i) => store.upsertSession(pressureSession(`s-${i}`, 'copilot', peak)))

    const rollup = buildHarnessRollup(store, 'copilot')

    expect(rollup.sampleCount).toBe(5)
    expect(rollup.contextPressureMedian).toBe(0.3)
    expect(rollup.contextPressureP95).toBe(0.5)
    // 250 cache read against 1000 input, five times over.
    expect(rollup.cacheHitRate).toBe(0.25)
    // One of two tools invoked per session.
    expect(rollup.toolYield).toBe(0.5)
    expect((rollup.payload as { sessionCount: number }).sessionCount).toBe(5)

    store.close()
  })

  it('counts only the harness it was asked for', () => {
    const store = new CanonStore(':memory:')
    store.upsertSession(pressureSession('a', 'copilot', 0.4))
    store.upsertSession(pressureSession('b', 'cursor', 0.9))

    expect((buildHarnessRollup(store, 'copilot').payload as { sessionCount: number }).sessionCount).toBe(1)
    expect(buildHarnessRollup(store, 'copilot').contextPressureP95).toBe(0.4)

    store.close()
  })

  it('does not fold a not_measurable total into the cache denominator', () => {
    // total_input is an object when the harness reported no counter; summing
    // it as a number would silently produce a rate against garbage.
    const store = new CanonStore(':memory:')
    store.upsertSession({
      sessionId: 'unmeasured',
      harness: 'copilot',
      payload: {
        summary: {
          total_input: { availability: 'not_measurable', reason: 'no counter' },
          total_cache_read: 400,
        },
      },
    })

    const rollup = buildHarnessRollup(store, 'copilot')

    expect(rollup.cacheHitRate).toBeNull()
    expect(isNotMeasurable(rollup.measurability['cache_hit_rate'])).toBe(true)
    store.close()
  })
})
