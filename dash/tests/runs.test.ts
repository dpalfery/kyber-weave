import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, describe, expect, it } from 'vitest'

import { buildRuns, deriveRunIdentity, linkExecutions } from '../kyber/canon/runs.js'
import { CanonStore, SCHEMA_VERSION } from '../kyber/canon/store.js'
import type { CanonicalRecord, ExecutionCandidate, TokenUsage } from '../kyber/canon/types.js'

const tempDirs: string[] = []

function tempStorePath(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-runs-test-'))
  tempDirs.push(dir)
  return join(dir, 'canon.db')
}

afterEach(() => {
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()!
    rmSync(dir, { recursive: true, force: true })
  }
})

function tokens(over: Partial<TokenUsage> = {}): TokenUsage {
  return {
    freshInput: 500,
    cacheRead: 100,
    cacheCreation: 0,
    output: 50,
    reportedInput: 600,
    reportedOutput: 50,
    ...over,
  }
}

function makeRecord(spanId: string, over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-1',
    parentSpanId: null,
    source: 'antigravity',
    harness: 'gemini',
    sessionId: 'sess-1',
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-09-03T10:00:00.000Z',
    durationMs: 150,
    status: 'ok',
    tokens: tokens(),
    content: { system_prompt: 'test prompt' },
    cost: { basis: 'unknown', status: 'no_rate' },
    ...over,
  }
}

describe('Decision D13 — Run boundary derivation', () => {
  it('requires explicit run identity where emitted by the harness', () => {
    const record = makeRecord('span-explicit', {
      raw: {
        'gen_ai.run.id': 'run-exp-001',
        cwd: '/Users/test/projects/kyber',
      },
    })

    const identity = deriveRunIdentity(record)

    expect(identity.runId).toBe('run-exp-001')
    expect(identity.groupingBasis).toBe('explicit')
    expect(identity.groupingRule).toBe('explicit_run_id')
    expect(identity.workingDirectory).toBe('/Users/test/projects/kyber')
  })

  it('detects alternative explicit run attributes (run.id, task.id, workflow.id)', () => {
    const r1 = makeRecord('span-1', { raw: { 'run.id': 'run-alpha' } })
    const r2 = makeRecord('span-2', { raw: { 'task.id': 'task-beta' } })
    const r3 = makeRecord('span-3', { raw: { 'workflow.id': 'flow-gamma' } })

    expect(deriveRunIdentity(r1).runId).toBe('run-alpha')
    expect(deriveRunIdentity(r1).groupingBasis).toBe('explicit')

    expect(deriveRunIdentity(r2).runId).toBe('task-beta')
    expect(deriveRunIdentity(r2).groupingBasis).toBe('explicit')

    expect(deriveRunIdentity(r3).runId).toBe('flow-gamma')
    expect(deriveRunIdentity(r3).groupingBasis).toBe('explicit')
  })

  it('provides a labelled derived grouping based on working-directory and time gaps', () => {
    const r1 = makeRecord('span-d1', {
      timestamp: '2026-09-03T10:00:00.000Z',
      raw: { 'process.working_directory': '/repo/app' },
    })

    const identity = deriveRunIdentity([r1])

    expect(identity.groupingBasis).toBe('derived')
    expect(identity.groupingRule).toBe('working_directory_and_inactivity_window')
    expect(identity.workingDirectory).toBe('/repo/app')
    expect(identity.runId).toMatch(/^derived:gemini:_repo_app:/)
  })

  it('never silently presents a heuristic grouping as raw fact', () => {
    const rNoRunId = makeRecord('span-heuristic', {
      raw: { cwd: '/workspace' },
    })

    const identity = deriveRunIdentity(rNoRunId)

    // D13 strictly mandates that derived groupings are explicitly labelled 'derived'
    expect(identity.groupingBasis).toBe('derived')
    expect(identity.groupingBasis).not.toBe('explicit')
  })

  it('clusters sessions within the inactivity window into one derived run and separates past window', async () => {
    const store = new CanonStore(':memory:')

    // Session 1: 10:00 to 10:05
    store.upsert(
      makeRecord('s1-1', {
        sessionId: 'sess-1',
        timestamp: '2026-09-03T10:00:00.000Z',
        raw: { cwd: '/Users/dave/repo' },
      }),
    )
    store.upsert(
      makeRecord('s1-2', {
        sessionId: 'sess-1',
        timestamp: '2026-09-03T10:05:00.000Z',
        raw: { cwd: '/Users/dave/repo' },
      }),
    )

    // Session 2: 10:20 (15 minutes after sess-1 ended, within 30 min window) -> same derived run
    store.upsert(
      makeRecord('s2-1', {
        sessionId: 'sess-2',
        timestamp: '2026-09-03T10:20:00.000Z',
        raw: { cwd: '/Users/dave/repo' },
      }),
    )

    // Session 3: 11:30 (70 minutes after sess-2 ended, past 30 min window) -> new derived run
    store.upsert(
      makeRecord('s3-1', {
        sessionId: 'sess-3',
        timestamp: '2026-09-03T11:30:00.000Z',
        raw: { cwd: '/Users/dave/repo' },
      }),
    )

    const report = await buildRuns(store, { inactivityWindowMs: 30 * 60 * 1000 })

    expect(report.runsBuilt).toBe(2)
    expect(report.executionsBuilt).toBe(3)

    const runs = store.listRuns()
    expect(runs).toHaveLength(2)

    // Newest run (Session 3)
    const runNew = runs[0]!
    expect(runNew.executionCount).toBe(1)
    expect(runNew.groupingBasis).toBe('derived')
    expect(runNew.groupingRule).toBe('working_directory_and_inactivity_window')

    // Earlier run (Sessions 1 and 2 clustered together)
    const runClustered = runs[1]!
    expect(runClustered.executionCount).toBe(2)
    expect(runClustered.groupingBasis).toBe('derived')
    expect(runClustered.groupingRule).toBe('working_directory_and_inactivity_window')

    const clusteredExecs = store.listExecutions(runClustered.runId)
    expect(clusteredExecs.map((e) => e.executionId).sort()).toEqual(['sess-1', 'sess-2'])

    store.close()
  })

  it('groups sessions with explicit run id regardless of time gap', async () => {
    const store = new CanonStore(':memory:')

    store.upsert(
      makeRecord('exp-1', {
        sessionId: 'sess-exp-1',
        timestamp: '2026-09-03T10:00:00.000Z',
        raw: { 'gen_ai.run.id': 'benchmark-run-42', cwd: '/benchmarks' },
      }),
    )
    store.upsert(
      makeRecord('exp-2', {
        sessionId: 'sess-exp-2',
        timestamp: '2026-09-03T15:00:00.000Z', // 5 hours later
        raw: { 'gen_ai.run.id': 'benchmark-run-42', cwd: '/benchmarks' },
      }),
    )

    await buildRuns(store)

    const run = store.getRun('benchmark-run-42')
    expect(run).toBeDefined()
    expect(run?.groupingBasis).toBe('explicit')
    expect(run?.groupingRule).toBe('explicit_run_id')
    expect(run?.executionCount).toBe(2)

    const execs = store.listExecutions('benchmark-run-42')
    expect(execs).toHaveLength(2)
    store.close()
  })

  it('falls back to single-execution derived run when working directory is absent', async () => {
    const store = new CanonStore(':memory:')

    store.upsert(
      makeRecord('nodir-1', {
        sessionId: 'sess-nodir',
        timestamp: '2026-09-03T10:00:00.000Z',
        raw: {}, // no cwd, no run id
      }),
    )

    await buildRuns(store)

    const runs = store.listRuns()
    expect(runs).toHaveLength(1)
    expect(runs[0]!.groupingBasis).toBe('derived')
    expect(runs[0]!.groupingRule).toBe('session_fallback')
    expect(runs[0]!.workingDirectory).toBeNull()
    store.close()
  })
})

describe('Parent/child execution linkage & execution trees', () => {
  it('links executions with trace parentage as measured parent/child', () => {
    // Session A (root) contains span 'span-root'
    // Session B (subagent) contains span 'span-child', whose parentSpanId is 'span-root'
    const candA: ExecutionCandidate = {
      executionId: 'exec-root',
      runId: 'run-trace-tree',
      sessionId: 'sess-root',
      harness: 'gemini',
      agentName: 'orchestrator',
      started: '2026-09-03T10:00:00.000Z',
      ended: '2026-09-03T10:02:00.000Z',
      records: [
        makeRecord('span-root', { sessionId: 'sess-root', parentSpanId: null }),
      ],
    }

    const candB: ExecutionCandidate = {
      executionId: 'exec-child',
      runId: 'run-trace-tree',
      sessionId: 'sess-child',
      harness: 'gemini',
      agentName: 'code-analyzer',
      started: '2026-09-03T10:00:30.000Z',
      ended: '2026-09-03T10:01:30.000Z',
      records: [
        makeRecord('span-child', { sessionId: 'sess-child', parentSpanId: 'span-root' }),
      ],
    }

    const linked = linkExecutions([candA, candB])

    const rootExec = linked.find((e) => e.executionId === 'exec-root')!
    const childExec = linked.find((e) => e.executionId === 'exec-child')!

    expect(rootExec.isRoot).toBe(true)
    expect(rootExec.parentExecutionId).toBeNull()
    expect(rootExec.parentLinkage).toBe('measured')

    expect(childExec.isRoot).toBe(false)
    expect(childExec.parentExecutionId).toBe('exec-root')
    expect(childExec.parentLinkage).toBe('measured')
  })

  it('links executions with session parentage (parentSessionId)', () => {
    const candRoot: ExecutionCandidate = {
      executionId: 'sess-copilot-root',
      runId: 'run-session-linkage',
      sessionId: 'sess-copilot-root',
      harness: 'copilot',
      agentName: 'lead-agent',
      records: [makeRecord('span-c1')],
    }

    const candChild: ExecutionCandidate = {
      executionId: 'sess-copilot-sub',
      runId: 'run-session-linkage',
      sessionId: 'sess-copilot-sub',
      parentSessionId: 'sess-copilot-root',
      harness: 'copilot',
      agentName: 'worker-agent',
      records: [makeRecord('span-c2')],
    }

    const linked = linkExecutions([candRoot, candChild])

    const child = linked.find((e) => e.executionId === 'sess-copilot-sub')!
    expect(child.isRoot).toBe(false)
    expect(child.parentExecutionId).toBe('sess-copilot-root')
    expect(child.parentLinkage).toBe('measured')
  })

  it('reports not_measurable when multi-session trace has no parentage', () => {
    const cand1: ExecutionCandidate = {
      executionId: 'exec-1',
      runId: 'run-unlinked',
      sessionId: 'sess-1',
      harness: 'gemini',
      records: [makeRecord('span-1', { parentSpanId: null })],
    }

    const cand2: ExecutionCandidate = {
      executionId: 'exec-2',
      runId: 'run-unlinked',
      sessionId: 'sess-2',
      harness: 'gemini',
      records: [makeRecord('span-2', { parentSpanId: null })],
    }

    const linked = linkExecutions([cand1, cand2])

    expect(linked[0]!.isRoot).toBe(true)
    expect(linked[1]!.isRoot).toBe(true)
    expect(linked[0]!.parentExecutionId).toBeNull()
    expect(linked[1]!.parentExecutionId).toBeNull()

    expect(linked[0]!.parentLinkage).toEqual({
      availability: 'not_measurable',
      reason: expect.stringContaining('parent-child trace or session parentage'),
    })
    expect(linked[1]!.parentLinkage).toEqual({
      availability: 'not_measurable',
      reason: expect.stringContaining('parent-child trace or session parentage'),
    })
  })

  it('yields one execution per run and reports not_measurable for flattened-subagent harness', () => {
    // File-sourced session where harness cannot export execution structure
    const candFlattened: ExecutionCandidate = {
      executionId: 'sess-claude-flattened',
      runId: 'run-flat',
      sessionId: 'sess-claude-flattened',
      harness: 'claude-code',
      records: [
        makeRecord('span-f1', {
          source: 'codeburn/claude',
          harness: 'claude-code',
          measurability: {
            execution_structure: {
              availability: 'not_measurable',
              reason: 'Claude Code session files record flattened subagent activity without hierarchy.',
            },
          },
        }),
      ],
    }

    const linked = linkExecutions([candFlattened])

    expect(linked).toHaveLength(1)
    expect(linked[0]!.isRoot).toBe(true)
    expect(linked[0]!.parentExecutionId).toBeNull()
    expect(linked[0]!.parentLinkage).toEqual({
      availability: 'not_measurable',
      reason: expect.stringContaining('Claude Code session files'),
    })
  })

  it('builds full hierarchical execution tree via store.getExecutionTree(runId)', async () => {
    const store = new CanonStore(':memory:')

    // 1 Root and 2 Delegated Children in one explicit run
    // Root: span-1
    // Child 1: span-2 (parent: span-1)
    // Child 2: span-3 (parent: span-1)
    // Grandchild: span-4 (parent: span-2)
    const runId = 'multi-level-tree-run'

    store.upsertMany([
      makeRecord('span-1', {
        sessionId: 'exec-root',
        parentSpanId: null,
        raw: { 'gen_ai.run.id': runId, 'gen_ai.agent.name': 'Director' },
      }),
      makeRecord('span-2', {
        sessionId: 'exec-child-1',
        parentSpanId: 'span-1',
        raw: { 'gen_ai.run.id': runId, 'gen_ai.agent.name': 'Researcher' },
      }),
      makeRecord('span-3', {
        sessionId: 'exec-child-2',
        parentSpanId: 'span-1',
        raw: { 'gen_ai.run.id': runId, 'gen_ai.agent.name': 'Writer' },
      }),
      makeRecord('span-4', {
        sessionId: 'exec-grandchild-1',
        parentSpanId: 'span-2',
        raw: { 'gen_ai.run.id': runId, 'gen_ai.agent.name': 'FactChecker' },
      }),
    ])

    await buildRuns(store)

    const tree = store.getExecutionTree(runId)

    // Top-level should have exactly 1 root
    expect(tree).toHaveLength(1)
    const rootNode = tree[0]!
    expect(rootNode.executionId).toBe('exec-root')
    expect(rootNode.agentName).toBe('Director')
    expect(rootNode.isRoot).toBe(true)
    expect(rootNode.parentLinkage).toBe('measured')

    // Root should have 2 children: Researcher and Writer
    expect(rootNode.children).toHaveLength(2)
    const researcher = rootNode.children.find((c) => c.executionId === 'exec-child-1')!
    const writer = rootNode.children.find((c) => c.executionId === 'exec-child-2')!
    expect(researcher).toBeDefined()
    expect(writer).toBeDefined()
    expect(researcher.parentExecutionId).toBe('exec-root')
    expect(researcher.parentLinkage).toBe('measured')
    expect(writer.parentExecutionId).toBe('exec-root')
    expect(writer.parentLinkage).toBe('measured')

    // Researcher should have 1 child: FactChecker
    expect(researcher.children).toHaveLength(1)
    const factChecker = researcher.children[0]!
    expect(factChecker.executionId).toBe('exec-grandchild-1')
    expect(factChecker.agentName).toBe('FactChecker')
    expect(factChecker.parentExecutionId).toBe('exec-child-1')
    expect(factChecker.parentLinkage).toBe('measured')
    expect(factChecker.children).toHaveLength(0)

    store.close()
  })
})

describe('Schema migration & store accessors', () => {
  it('stamps new stores with current SCHEMA_VERSION', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)

    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(SCHEMA_VERSION).toBeGreaterThanOrEqual(5)
    store.close()
  })

  it('migrates a v4 database to v5 and creates run and execution tables cleanly', () => {
    const path = tempStorePath()
    const db = new DatabaseSync(path)

    // Set up a v4 schema database manually
    db.exec(`
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
      CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
      CREATE TABLE session (
        session_id TEXT PRIMARY KEY,
        harness TEXT NOT NULL,
        label TEXT,
        is_subagent INTEGER NOT NULL DEFAULT 0,
        parent_session TEXT,
        agent_name TEXT,
        repo TEXT,
        branch TEXT,
        started TEXT,
        ended TEXT,
        payload TEXT NOT NULL
      );
      CREATE TABLE pending_logs (log_id TEXT PRIMARY KEY, payload TEXT NOT NULL);
      CREATE TABLE quarantined_logs (log_id TEXT PRIMARY KEY, payload TEXT NOT NULL, reason TEXT NOT NULL);
      CREATE TABLE enriched_logs (log_id TEXT PRIMARY KEY);
      INSERT INTO metadata (key, value) VALUES ('schema_version', '4');
    `)
    db.close()

    // Opening with CanonStore should trigger migration 4 -> 5 -> current
    const store = new CanonStore(path)
    expect(Number(store.getMetadata('schema_version'))).toBeGreaterThanOrEqual(5)

    // Verify run and execution tables are functional
    store.upsertRun({
      runId: 'run-v5-test',
      harness: 'gemini',
      groupingBasis: 'explicit',
      groupingRule: 'explicit_run_id',
    })

    store.upsertExecution({
      executionId: 'exec-v5-test',
      runId: 'run-v5-test',
      harness: 'gemini',
      isRoot: true,
      parentLinkage: 'measured',
    })

    expect(store.getRun('run-v5-test')?.runId).toBe('run-v5-test')
    expect(store.getExecution('exec-v5-test')?.executionId).toBe('exec-v5-test')

    store.close()
  })

  it('filters runs by harness with listRuns(harnessId)', async () => {
    const store = new CanonStore(':memory:')

    store.upsertRun({
      runId: 'run-gemini-1',
      harness: 'gemini',
      groupingBasis: 'derived',
      started: '2026-09-03T10:00:00.000Z',
    })
    store.upsertRun({
      runId: 'run-copilot-1',
      harness: 'copilot',
      groupingBasis: 'derived',
      started: '2026-09-03T11:00:00.000Z',
    })
    store.upsertRun({
      runId: 'run-gemini-2',
      harness: 'gemini',
      groupingBasis: 'explicit',
      started: '2026-09-03T12:00:00.000Z',
    })

    const allRuns = store.listRuns()
    expect(allRuns).toHaveLength(3)

    const geminiRuns = store.listRuns('gemini')
    expect(geminiRuns).toHaveLength(2)
    expect(geminiRuns.map((r) => r.runId)).toEqual(['run-gemini-2', 'run-gemini-1'])

    const copilotRuns = store.listRuns('copilot')
    expect(copilotRuns).toHaveLength(1)
    expect(copilotRuns[0]!.runId).toBe('run-copilot-1')

    store.close()
  })

  it('rebuilds run and execution tables with zero data loss on subsequent passes', async () => {
    const store = new CanonStore(':memory:')

    store.upsertMany([
      makeRecord('span-reb-1', {
        sessionId: 'sess-reb-1',
        raw: { 'gen_ai.run.id': 'run-rebuild-1' },
      }),
      makeRecord('span-reb-2', {
        sessionId: 'sess-reb-2',
        raw: { 'gen_ai.run.id': 'run-rebuild-2' },
      }),
    ])

    const rep1 = await buildRuns(store)
    expect(rep1.runsBuilt).toBe(2)
    expect(rep1.executionsBuilt).toBe(2)
    expect(rep1.prunedRuns).toBe(0)

    // Second build without data changes leaves exactly same counts
    const rep2 = await buildRuns(store)
    expect(rep2.runsBuilt).toBe(2)
    expect(rep2.executionsBuilt).toBe(2)
    expect(rep2.prunedRuns).toBe(0)

    // Deleting a record removes its corresponding run on next build
    store.quarantineAndDelete('span-reb-2', ['gen_ai'], 'test quarantine')
    const rep3 = await buildRuns(store)
    expect(rep3.runsBuilt).toBe(1)
    expect(rep3.executionsBuilt).toBe(1)
    expect(rep3.prunedRuns).toBe(1)
    expect(rep3.prunedExecutions).toBe(1)
    expect(store.getRun('run-rebuild-2')).toBeUndefined()

    store.close()
  })
})
