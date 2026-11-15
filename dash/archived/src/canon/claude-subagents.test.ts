import { describe, expect, it } from 'vitest'

import { tokens, turn } from './fixtures/records.js'
import { projectCanonicalStore } from './projection.js'
import { buildSessions } from './sessions.js'
import { CanonStore } from './store.js'
import type { CanonicalRecord } from './types.js'

type SessionDbRow = {
  session_id: string
  harness: string
  label: string | null
  is_subagent: number
  parent_session: string | null
  agent_name: string | null
  started: string | null
  ended: string | null
  payload: string
}

function querySessionDbRow(store: CanonStore, sessionId: string): SessionDbRow | undefined {
  return store
    .getDatabase()
    .prepare(
      'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, started, ended, payload FROM session WHERE session_id = ?',
    )
    .get(sessionId) as SessionDbRow | undefined
}

function claudeSpan(
  spanId: string,
  parentSpanId: string | null,
  op: string,
  name: string,
  sessionId: string,
  rawAttributes: Record<string, unknown> = {},
  overrides: Partial<CanonicalRecord> = {},
): CanonicalRecord {
  return turn(spanId, [], {
    harness: 'claude-code',
    source: 'claude-code',
    sessionId,
    parentSpanId,
    op,
    name,
    timestamp: '2026-10-14T10:00:00.000Z',
    durationMs: 100,
    raw: {
      ...rawAttributes,
    },
    tokens: tokens({ freshInput: 1000, reportedInput: 1000, output: 50, reportedOutput: 50 }),
    ...overrides,
  })
}

describe('Claude subagent session projection and task naming (Contract T2.2)', () => {
  it('projects Claude subagent span into a distinct subagent session row with deterministic ID <parent>#agent-<agentId>, parent_session, is_subagent, and agent_name', async () => {
    const parentSessionId = 'claude-parent-sess-001'
    const subagentAgentId = 'researcher-001'
    const expectedSubagentId = `${parentSessionId}#agent-${subagentAgentId}`
    const spawnSpanId = 'subagent-spawn-span-1'

    const parentTurn = claudeSpan(
      'parent-root-span',
      null,
      'llm.invoke',
      'chat claude-3-7-sonnet',
      parentSessionId,
      {},
      {
        timestamp: '2026-10-14T10:00:00.000Z',
        tokens: tokens({ freshInput: 1200, reportedInput: 1200, output: 60, reportedOutput: 60 }),
        cost: { basis: 'harness', status: 'priced', value: 0.005, currency: 'USD' },
      },
    )

    const subagentSpawn = claudeSpan(
      spawnSpanId,
      'parent-root-span',
      'agent.invoke',
      'Task',
      parentSessionId,
      {
        agent_id: subagentAgentId,
        agentId: subagentAgentId,
        agent_type: 'Codebase Researcher',
        agentType: 'Codebase Researcher',
        description: 'Investigate codebase architecture and subagent patterns',
        'gen_ai.agent.name': 'Codebase Researcher',
      },
      {
        timestamp: '2026-10-14T10:00:02.000Z',
        durationMs: 5000,
        tokens: tokens({ freshInput: 0, reportedInput: 0, output: 0, reportedOutput: 0 }),
      },
    )

    const subagentChat = claudeSpan(
      'subagent-chat-span-1',
      spawnSpanId,
      'llm.invoke',
      'chat claude-3-7-sonnet',
      parentSessionId,
      {
        agent_id: subagentAgentId,
        agentId: subagentAgentId,
        agent_type: 'Codebase Researcher',
        agentType: 'Codebase Researcher',
      },
      {
        timestamp: '2026-10-14T10:00:03.000Z',
        durationMs: 2000,
        tokens: tokens({ freshInput: 3000, reportedInput: 3000, output: 150, reportedOutput: 150 }),
        cost: { basis: 'harness', status: 'priced', value: 0.015, currency: 'USD' },
      },
    )

    const subagentTool = claudeSpan(
      'subagent-tool-span-1',
      spawnSpanId,
      'tool.invoke',
      'Read',
      parentSessionId,
      {
        'gen_ai.tool.name': 'Read',
        arguments: JSON.stringify({ path: 'src/canon/sessions.ts' }),
        agent_id: subagentAgentId,
      },
      {
        timestamp: '2026-10-14T10:00:07.000Z',
        durationMs: 500,
        tokens: tokens({ freshInput: 0, reportedInput: 0, output: 0, reportedOutput: 0 }),
      },
    )

    const store = new CanonStore(':memory:')
    store.upsertMany([parentTurn, subagentSpawn, subagentChat, subagentTool])
    await buildSessions(store)

    // 1. Verify parent session row in store and database table
    const parentSession = store.getSession(parentSessionId)
    expect(parentSession, 'Parent session row must exist in store').toBeDefined()
    expect(parentSession?.sessionId).toBe(parentSessionId)
    expect(parentSession?.isSubagent).toBe(false)
    expect(parentSession?.parentSession).toBeNull()

    const parentDbRow = querySessionDbRow(store, parentSessionId)
    expect(parentDbRow, 'Parent session row must exist in session table').toBeDefined()
    expect(parentDbRow?.session_id).toBe(parentSessionId)
    expect(parentDbRow?.is_subagent).toBe(0)
    expect(parentDbRow?.parent_session).toBeNull()

    // 2. Verify subagent session row projected into session table with <parent>#agent-<agentId>
    const subagentSession = store.getSession(expectedSubagentId)
    expect(subagentSession, `Subagent session row must exist for ${expectedSubagentId}`).toBeDefined()
    expect(subagentSession?.sessionId).toBe(expectedSubagentId)
    expect(subagentSession?.isSubagent).toBe(true)
    expect(subagentSession?.parentSession).toBe(parentSessionId)
    expect(subagentSession?.agentName).toBe('Codebase Researcher')
    expect(subagentSession?.label).toBe('Codebase Researcher · Investigate codebase architecture and subagent patterns')

    const subagentDbRow = querySessionDbRow(store, expectedSubagentId)
    expect(subagentDbRow, `Subagent row in session table must exist for ${expectedSubagentId}`).toBeDefined()
    expect(subagentDbRow?.session_id).toBe(expectedSubagentId)
    expect(subagentDbRow?.is_subagent).toBe(1)
    expect(subagentDbRow?.parent_session).toBe(parentSessionId)
    expect(subagentDbRow?.agent_name).toBe('Codebase Researcher')
    expect(subagentDbRow?.label).toBe('Codebase Researcher · Investigate codebase architecture and subagent patterns')

    // 3. Verify timestamps, turn_count, request_count, tokens, cost
    expect(subagentSession?.started).toBe('2026-10-14T10:00:02.000Z')
    expect(subagentSession?.ended).toBe('2026-10-14T10:00:07.000Z')
    expect(subagentDbRow?.started).toBe('2026-10-14T10:00:02.000Z')
    expect(subagentDbRow?.ended).toBe('2026-10-14T10:00:07.000Z')

    expect(subagentSession?.summary?.turn_count).toBe(1)
    expect(subagentSession?.summary?.request_count).toBe(1)
    expect(subagentSession?.summary?.total_input).toBe(3000)
    expect(subagentSession?.summary?.total_output).toBe(150)
    expect(subagentSession?.summary?.cost).toEqual(
      expect.objectContaining({ value: 0.015, currency: 'USD' }),
    )

    store.close()
  })

  it('projects Claude subagent from sidechain records with agent_id and description', async () => {
    const parentSessionId = 'claude-parent-sess-002'
    const sidechainAgentId = 'analyzer-002'
    const expectedSubagentId = `${parentSessionId}#agent-${sidechainAgentId}`

    const parentTurn = claudeSpan(
      'parent-root-span-2',
      null,
      'llm.invoke',
      'chat claude-3-7-sonnet',
      parentSessionId,
      {},
      {
        timestamp: '2026-10-14T10:10:00.000Z',
      },
    )

    const sidechainTurn = claudeSpan(
      'sidechain-turn-1',
      'parent-root-span-2',
      'llm.invoke',
      'chat claude-3-7-sonnet',
      parentSessionId,
      {
        is_sidechain: true,
        agent_id: sidechainAgentId,
        agentId: sidechainAgentId,
        agent_type: 'Performance Analyzer',
        agentType: 'Performance Analyzer',
        description: 'Analyze memory profile during ingest',
        'gen_ai.agent.name': 'Performance Analyzer',
      },
      {
        timestamp: '2026-10-14T10:15:00.000Z',
        tokens: tokens({ freshInput: 2500, reportedInput: 2500, output: 90, reportedOutput: 90 }),
        cost: { basis: 'harness', status: 'priced', value: 0.012, currency: 'USD' },
      },
    )

    const store = new CanonStore(':memory:')
    store.upsertMany([parentTurn, sidechainTurn])
    await buildSessions(store)

    // Subagent row must extract agent_name and description from sidechain attributes
    const subagentSession = store.getSession(expectedSubagentId)
    expect(subagentSession, `Subagent session row must exist for ${expectedSubagentId}`).toBeDefined()
    expect(subagentSession?.sessionId).toBe(expectedSubagentId)
    expect(subagentSession?.isSubagent).toBe(true)
    expect(subagentSession?.parentSession).toBe(parentSessionId)
    expect(subagentSession?.agentName).toBe('Performance Analyzer')
    expect(subagentSession?.label).toBe('Performance Analyzer · Analyze memory profile during ingest')

    const subagentDbRow = querySessionDbRow(store, expectedSubagentId)
    expect(subagentDbRow, `Subagent row in session table must exist for ${expectedSubagentId}`).toBeDefined()
    expect(subagentDbRow?.session_id).toBe(expectedSubagentId)
    expect(subagentDbRow?.is_subagent).toBe(1)
    expect(subagentDbRow?.parent_session).toBe(parentSessionId)
    expect(subagentDbRow?.agent_name).toBe('Performance Analyzer')
    expect(subagentDbRow?.label).toBe('Performance Analyzer · Analyze memory profile during ingest')

    // Parent session row must remain isSubagent: false
    const parentSession = store.getSession(parentSessionId)
    expect(parentSession?.isSubagent).toBe(false)
    expect(parentSession?.parentSession).toBeNull()

    const parentDbRow = querySessionDbRow(store, parentSessionId)
    expect(parentDbRow?.is_subagent).toBe(0)
    expect(parentDbRow?.parent_session).toBeNull()

    store.close()
  })

  it('projects multiple Claude subagents under the same parent session with distinct deterministic IDs', async () => {
    const parentSessionId = 'claude-parent-sess-003'
    const agentId1 = 'planner-003'
    const agentId2 = 'testdev-004'
    const expectedSubagentId1 = `${parentSessionId}#agent-${agentId1}`
    const expectedSubagentId2 = `${parentSessionId}#agent-${agentId2}`
    const spawnSpan1 = 'spawn-span-planner'
    const spawnSpan2 = 'spawn-span-testdev'

    const parentTurn = claudeSpan(
      'parent-root-span-3',
      null,
      'llm.invoke',
      'chat claude-3-7-sonnet',
      parentSessionId,
      {},
      { timestamp: '2026-10-14T10:20:00.000Z' },
    )

    const spawn1 = claudeSpan(
      spawnSpan1,
      'parent-root-span-3',
      'agent.invoke',
      'Task',
      parentSessionId,
      {
        agent_id: agentId1,
        agentId: agentId1,
        agent_type: 'planner',
        agentType: 'planner',
        description: 'Draft implementation plan',
        'gen_ai.agent.name': 'planner',
      },
      { timestamp: '2026-10-14T10:21:00.000Z' },
    )
    const chat1 = claudeSpan(
      'chat-sub-1',
      spawnSpan1,
      'llm.invoke',
      'chat claude-3-7-sonnet',
      parentSessionId,
      { agent_id: agentId1 },
      { timestamp: '2026-10-14T10:22:00.000Z' },
    )

    const spawn2 = claudeSpan(
      spawnSpan2,
      'parent-root-span-3',
      'agent.invoke',
      'Task',
      parentSessionId,
      {
        agent_id: agentId2,
        agentId: agentId2,
        agent_type: 'test-dev',
        agentType: 'test-dev',
        description: 'Author contract tests',
        'gen_ai.agent.name': 'test-dev',
      },
      { timestamp: '2026-10-14T10:23:00.000Z' },
    )
    const chat2 = claudeSpan(
      'chat-sub-2',
      spawnSpan2,
      'llm.invoke',
      'chat claude-3-7-sonnet',
      parentSessionId,
      { agent_id: agentId2 },
      { timestamp: '2026-10-14T10:24:00.000Z' },
    )

    const store = new CanonStore(':memory:')
    store.upsertMany([parentTurn, spawn1, chat1, spawn2, chat2])
    await buildSessions(store)

    expect(store.sessionCount(), 'Must project 1 parent session and 2 subagent sessions').toBe(3)

    const sub1 = querySessionDbRow(store, expectedSubagentId1)
    expect(sub1, `Subagent row in session table must exist for ${expectedSubagentId1}`).toBeDefined()
    expect(sub1?.session_id).toBe(expectedSubagentId1)
    expect(sub1?.is_subagent).toBe(1)
    expect(sub1?.parent_session).toBe(parentSessionId)
    expect(sub1?.agent_name).toBe('planner')
    expect(sub1?.label).toBe('planner · Draft implementation plan')

    const sub2 = querySessionDbRow(store, expectedSubagentId2)
    expect(sub2, `Subagent row in session table must exist for ${expectedSubagentId2}`).toBeDefined()
    expect(sub2?.session_id).toBe(expectedSubagentId2)
    expect(sub2?.is_subagent).toBe(1)
    expect(sub2?.parent_session).toBe(parentSessionId)
    expect(sub2?.agent_name).toBe('test-dev')
    expect(sub2?.label).toBe('test-dev · Author contract tests')

    store.close()
  })

  it('guarantees identical Claude subagent projection via projectCanonicalStore', async () => {
    const parentSessionId = 'claude-parent-sess-004'
    const subagentAgentId = 'dal-dev-005'
    const expectedSubagentId = `${parentSessionId}#agent-${subagentAgentId}`
    const spawnSpanId = 'spawn-span-dal'

    const parentTurn = claudeSpan(
      'parent-root-span-4',
      null,
      'llm.invoke',
      'chat claude-3-7-sonnet',
      parentSessionId,
      {},
      { timestamp: '2026-10-14T10:30:00.000Z' },
    )

    const spawnSpan = claudeSpan(
      spawnSpanId,
      'parent-root-span-4',
      'agent.invoke',
      'Task',
      parentSessionId,
      {
        agent_id: subagentAgentId,
        agentId: subagentAgentId,
        agent_type: 'dal-dev',
        agentType: 'dal-dev',
        description: 'Implement database repository query methods',
        'gen_ai.agent.name': 'dal-dev',
      },
      { timestamp: '2026-10-14T10:31:00.000Z' },
    )

    const subagentChat = claudeSpan(
      'subagent-chat-span-4',
      spawnSpanId,
      'llm.invoke',
      'chat claude-3-7-sonnet',
      parentSessionId,
      { agent_id: subagentAgentId },
      { timestamp: '2026-10-14T10:32:00.000Z' },
    )

    const store = new CanonStore(':memory:')
    store.upsertMany([parentTurn, spawnSpan, subagentChat])
    await projectCanonicalStore(store)

    const subagentDbRow = querySessionDbRow(store, expectedSubagentId)
    expect(subagentDbRow, 'Subagent row must exist through projectCanonicalStore').toBeDefined()
    expect(subagentDbRow?.session_id).toBe(expectedSubagentId)
    expect(subagentDbRow?.is_subagent).toBe(1)
    expect(subagentDbRow?.parent_session).toBe(parentSessionId)
    expect(subagentDbRow?.agent_name).toBe('dal-dev')
    expect(subagentDbRow?.label).toBe('dal-dev · Implement database repository query methods')

    store.close()
  })
})
