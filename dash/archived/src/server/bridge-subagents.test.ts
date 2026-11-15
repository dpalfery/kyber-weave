import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { KyberBridge, type SessionSummary } from './bridge.js'

export type SessionRollup = {
  turn_count: number
  request_count: number
  total_input: number
  total_output: number
  cost_usd: number
}

export type NestedSessionSummary = SessionSummary & {
  subagents?: NestedSessionSummary[]
  rollup?: SessionRollup
}

type SeedSessionInput = {
  sessionId: string
  harness?: string
  label?: string | null
  isSubagent?: boolean
  parentSession?: string | null
  agentName?: string | null
  repo?: string | null
  branch?: string | null
  started?: string | null
  ended?: string | null
  turnCount?: number
  requestCount?: number
  totalInput?: number
  totalOutput?: number
  costUsd?: number
}

function insertSession(store: CanonStore, input: SeedSessionInput): void {
  const db = store.getDatabase()
  const summary: Record<string, unknown> = {
    turn_count: input.turnCount ?? 1,
    request_count: input.requestCount ?? 1,
    total_input: input.totalInput ?? 0,
    total_output: input.totalOutput ?? 0,
    models: ['test-model'],
  }
  if (typeof input.costUsd === 'number') {
    summary.cost = {
      basis: 'published',
      status: 'priced',
      value: input.costUsd,
      currency: 'USD',
    }
  } else {
    summary.cost = { basis: 'unknown', status: 'no_rate' }
  }

  const payload = JSON.stringify({
    id: input.sessionId,
    harness: input.harness ?? 'claude-code',
    summary,
    problems: [],
  })

  db.prepare(`
    INSERT INTO session (
      session_id,
      harness,
      label,
      is_subagent,
      parent_session,
      agent_name,
      repo,
      branch,
      started,
      ended,
      payload
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `).run(
    input.sessionId,
    input.harness ?? 'claude-code',
    input.label ?? input.sessionId,
    input.isSubagent ? 1 : 0,
    input.parentSession ?? null,
    input.agentName ?? null,
    input.repo ?? 'test-repo',
    input.branch ?? 'main',
    input.started ?? '2026-10-14T10:00:00.000Z',
    input.ended ?? '2026-10-14T10:30:00.000Z',
    payload,
  )
}

describe('KyberBridge.listSessions hierarchical subagent nesting and rollup (Task T0.3)', () => {
  let tempDir: string
  let dbPath: string
  let store: CanonStore
  let bridge: KyberBridge

  beforeEach(() => {
    tempDir = mkdtempSync(join(tmpdir(), 'kyber-bridge-subagents-'))
    dbPath = join(tempDir, 'canon.db')
    store = new CanonStore(dbPath)
    bridge = new KyberBridge({ store, canonPath: dbPath })

    // 1. Root orchestrator session (parent)
    insertSession(store, {
      sessionId: 'parent-1',
      harness: 'claude-code',
      label: 'Root Orchestrator',
      isSubagent: false,
      parentSession: null,
      agentName: 'orchestrator',
      started: '2026-10-14T10:00:00.000Z',
      ended: '2026-10-14T10:30:00.000Z',
      turnCount: 5,
      requestCount: 6,
      totalInput: 1000,
      totalOutput: 200,
      costUsd: 0.15,
    })

    // 2. Child subagent session 1
    insertSession(store, {
      sessionId: 'child-1',
      harness: 'claude-code',
      label: 'Worker 1',
      isSubagent: true,
      parentSession: 'parent-1',
      agentName: 'coder',
      started: '2026-10-14T10:05:00.000Z',
      ended: '2026-10-14T10:15:00.000Z',
      turnCount: 3,
      requestCount: 4,
      totalInput: 2000,
      totalOutput: 500,
      costUsd: 0.35,
    })

    // 3. Child subagent session 2
    insertSession(store, {
      sessionId: 'child-2',
      harness: 'claude-code',
      label: 'Worker 2',
      isSubagent: true,
      parentSession: 'parent-1',
      agentName: 'reviewer',
      started: '2026-10-14T10:16:00.000Z',
      ended: '2026-10-14T10:25:00.000Z',
      turnCount: 2,
      requestCount: 2,
      totalInput: 1500,
      totalOutput: 300,
      costUsd: 0.10,
    })

    // 4. Standalone root session without subagents
    insertSession(store, {
      sessionId: 'standalone-1',
      harness: 'claude-code',
      label: 'Standalone Session',
      isSubagent: false,
      parentSession: null,
      agentName: 'solo',
      started: '2026-10-14T11:00:00.000Z',
      ended: '2026-10-14T11:10:00.000Z',
      turnCount: 4,
      requestCount: 4,
      totalInput: 800,
      totalOutput: 150,
      costUsd: 0.08,
    })

    // 5. Orphaned subagent whose parent is absent from the store
    insertSession(store, {
      sessionId: 'orphan-subagent',
      harness: 'claude-code',
      label: 'Orphan Subagent',
      isSubagent: true,
      parentSession: 'absent-parent-id',
      agentName: 'detached-worker',
      started: '2026-10-14T09:00:00.000Z',
      ended: '2026-10-14T09:10:00.000Z',
      turnCount: 1,
      requestCount: 1,
      totalInput: 500,
      totalOutput: 100,
      costUsd: 0.05,
    })
  })

  afterEach(() => {
    bridge.close()
    store.close()
    rmSync(tempDir, { recursive: true, force: true })
  })

  it('returns only top-level root sessions omitting child subagent sessions whose parent exists in the store', () => {
    const sessions = bridge.listSessions() as unknown as NestedSessionSummary[]
    const rootIds = sessions.map((s) => s.session_id)

    // Only top-level parents and parentless/orphan sessions appear at root
    expect(rootIds).toContain('parent-1')
    expect(rootIds).toContain('standalone-1')
    expect(rootIds).toContain('orphan-subagent')

    // Child subagents whose parent exists in the store must NOT appear in the top-level list
    expect(rootIds).not.toContain('child-1')
    expect(rootIds).not.toContain('child-2')
    expect(sessions).toHaveLength(3)
  })

  it('nests child subagent sessions inside the parent session subagents array', () => {
    const sessions = bridge.listSessions() as unknown as NestedSessionSummary[]
    const parent = sessions.find((s) => s.session_id === 'parent-1')

    expect(parent).toBeDefined()
    expect(parent?.subagents).toBeDefined()
    expect(Array.isArray(parent?.subagents)).toBe(true)
    expect(parent?.subagents).toHaveLength(2)

    const childIds = parent?.subagents?.map((c) => c.session_id).sort()
    expect(childIds).toEqual(['child-1', 'child-2'])
  })

  it('aggregates parent and child metrics into the read-time rollup property on root sessions', () => {
    const sessions = bridge.listSessions() as unknown as NestedSessionSummary[]
    const parent = sessions.find((s) => s.session_id === 'parent-1')

    expect(parent).toBeDefined()
    expect(parent?.rollup).toBeDefined()

    // Sum of parent (5) + child-1 (3) + child-2 (2) = 10
    expect(parent?.rollup?.turn_count).toBe(10)
    // Sum of parent (6) + child-1 (4) + child-2 (2) = 12
    expect(parent?.rollup?.request_count).toBe(12)
    // Sum of parent (1000) + child-1 (2000) + child-2 (1500) = 4500
    expect(parent?.rollup?.total_input).toBe(4500)
    // Sum of parent (200) + child-1 (500) + child-2 (300) = 1000
    expect(parent?.rollup?.total_output).toBe(1000)
    // Sum of parent ($0.15) + child-1 ($0.35) + child-2 ($0.10) = $0.60
    expect(parent?.rollup?.cost_usd).toBeCloseTo(0.60, 5)
  })

  it('preserves granular unaggregated counters on both parent and nested child sessions', () => {
    const sessions = bridge.listSessions() as unknown as NestedSessionSummary[]
    const parent = sessions.find((s) => s.session_id === 'parent-1')

    expect(parent).toBeDefined()
    // Parent keeps its own granular counters at top level
    expect(parent?.turn_count).toBe(5)
    expect(parent?.request_count).toBe(6)
    expect(parent?.total_input).toBe(1000)
    expect(parent?.total_output).toBe(200)
    expect(parent?.cost_usd).toBeCloseTo(0.15, 5)

    // Children in subagents retain their own granular counters
    const child1 = parent?.subagents?.find((c) => c.session_id === 'child-1')
    expect(child1).toBeDefined()
    expect(child1?.turn_count).toBe(3)
    expect(child1?.request_count).toBe(4)
    expect(child1?.total_input).toBe(2000)
    expect(child1?.total_output).toBe(500)
    expect(child1?.cost_usd).toBeCloseTo(0.35, 5)
    expect(child1?.is_subagent).toBe(true)
    expect(child1?.parent_session).toBe('parent-1')

    const child2 = parent?.subagents?.find((c) => c.session_id === 'child-2')
    expect(child2).toBeDefined()
    expect(child2?.turn_count).toBe(2)
    expect(child2?.request_count).toBe(2)
    expect(child2?.total_input).toBe(1500)
    expect(child2?.total_output).toBe(300)
    expect(child2?.cost_usd).toBeCloseTo(0.10, 5)
    expect(child2?.is_subagent).toBe(true)
    expect(child2?.parent_session).toBe('parent-1')
  })

  it('keeps subagents visible at root when their parent session is absent from the store', () => {
    const sessions = bridge.listSessions() as unknown as NestedSessionSummary[]
    const orphan = sessions.find((s) => s.session_id === 'orphan-subagent')

    expect(orphan).toBeDefined()
    expect(orphan?.is_subagent).toBe(true)
    expect(orphan?.parent_session).toBe('absent-parent-id')
    expect(orphan?.subagents).toEqual([])

    // Rollup reflects the orphan's own metrics
    expect(orphan?.rollup?.turn_count).toBe(1)
    expect(orphan?.rollup?.request_count).toBe(1)
    expect(orphan?.rollup?.total_input).toBe(500)
    expect(orphan?.rollup?.total_output).toBe(100)
    expect(orphan?.rollup?.cost_usd).toBeCloseTo(0.05, 5)
  })

  it('handles standalone root sessions without subagents gracefully', () => {
    const sessions = bridge.listSessions() as unknown as NestedSessionSummary[]
    const standalone = sessions.find((s) => s.session_id === 'standalone-1')

    expect(standalone).toBeDefined()
    expect(standalone?.is_subagent).toBe(false)
    expect(standalone?.parent_session).toBeNull()
    expect(standalone?.subagents).toEqual([])

    // Rollup matches the standalone session's own metrics
    expect(standalone?.rollup?.turn_count).toBe(4)
    expect(standalone?.rollup?.request_count).toBe(4)
    expect(standalone?.rollup?.total_input).toBe(800)
    expect(standalone?.rollup?.total_output).toBe(150)
    expect(standalone?.rollup?.cost_usd).toBeCloseTo(0.08, 5)
  })
})
