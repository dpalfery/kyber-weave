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
}

function querySessionDbRow(store: CanonStore, sessionId: string): SessionDbRow | undefined {
  return store
    .getDatabase()
    .prepare(
      'SELECT session_id, harness, label, is_subagent, parent_session, agent_name FROM session WHERE session_id = ?',
    )
    .get(sessionId) as SessionDbRow | undefined
}

function copilotSpan(
  spanId: string,
  parentSpanId: string | null,
  op: string,
  name: string,
  sessionId: string,
  rawAttributes: Record<string, unknown> = {},
  overrides: Partial<CanonicalRecord> = {},
): CanonicalRecord {
  return turn(spanId, [], {
    harness: 'copilot',
    source: 'github-copilot',
    sessionId,
    parentSpanId,
    op,
    name,
    raw: {
      'copilot_chat.chat_session_id': sessionId,
      ...rawAttributes,
    },
    tokens: tokens({ freshInput: 1000, reportedInput: 1000, output: 50, reportedOutput: 50 }),
    ...overrides,
  })
}

describe('Copilot subagent span projection and task naming (Contract T1.1)', () => {
  it('projects Copilot invoke_agent span into a distinct subagent session row with deterministic ID, parent_session, is_subagent, and agent_name', async () => {
    const parentSessionId = 'copilot-parent-sess-001'
    const invokeSpanId = 'invoke-agent-span-1'
    const expectedSubagentId = `${parentSessionId}#${invokeSpanId}`

    const parentTurn = copilotSpan(
      'parent-root-span',
      null,
      'llm.invoke',
      'chat gpt-4o',
      parentSessionId,
      { 'gen_ai.operation.name': 'chat', 'github.copilot.chat.turn.id': 'turn-0' },
      {
        tokens: tokens({ freshInput: 1200, reportedInput: 1200, output: 60, reportedOutput: 60 }),
      },
    )

    const invokeSpan = copilotSpan(
      invokeSpanId,
      'parent-root-span',
      'agent.invoke',
      'invoke_agent architect',
      parentSessionId,
      {
        'gen_ai.operation.name': 'invoke_agent',
        'gen_ai.agent.name': 'architect',
        'github.copilot.chat.turn.id': 'turn-1',
      },
      {
        tokens: tokens({ freshInput: 4000, reportedInput: 4000, output: 150, reportedOutput: 150 }),
      },
    )

    const subagentChat = copilotSpan(
      'subagent-chat-span-1',
      invokeSpanId,
      'llm.invoke',
      'chat gpt-4o',
      parentSessionId,
      { 'gen_ai.operation.name': 'chat', 'github.copilot.chat.turn.id': 'turn-1-sub-0' },
      {
        tokens: tokens({ freshInput: 2000, reportedInput: 2000, output: 80, reportedOutput: 80 }),
      },
    )

    const taskTool = copilotSpan(
      'subagent-task-tool-1',
      invokeSpanId,
      'tool.invoke',
      'task',
      parentSessionId,
      {
        'gen_ai.tool.name': 'task',
        arguments: JSON.stringify({ description: 'Design database schema for subagents' }),
      },
      {
        tokens: tokens({ freshInput: 0, reportedInput: 0, output: 0, reportedOutput: 0 }),
      },
    )

    const store = new CanonStore(':memory:')
    store.upsertMany([parentTurn, invokeSpan, subagentChat, taskTool])
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

    // 2. Verify subagent session row projected into session table
    const subagentSession = store.getSession(expectedSubagentId)
    expect(subagentSession, `Subagent session row must exist for ${expectedSubagentId}`).toBeDefined()
    expect(subagentSession?.sessionId).toBe(expectedSubagentId)
    expect(subagentSession?.isSubagent).toBe(true)
    expect(subagentSession?.parentSession).toBe(parentSessionId)
    expect(subagentSession?.agentName).toBe('architect')
    expect(subagentSession?.label).toBe('architect · Design database schema for subagents')

    const subagentDbRow = querySessionDbRow(store, expectedSubagentId)
    expect(subagentDbRow, `Subagent row in session table must exist for ${expectedSubagentId}`).toBeDefined()
    expect(subagentDbRow?.session_id).toBe(expectedSubagentId)
    expect(subagentDbRow?.is_subagent).toBe(1)
    expect(subagentDbRow?.parent_session).toBe(parentSessionId)
    expect(subagentDbRow?.agent_name).toBe('architect')
    expect(subagentDbRow?.label).toBe('architect · Design database schema for subagents')

    store.close()
  })

  it('extracts agent_name from agent.name and falls back to prompt for label when task tool call is absent', async () => {
    const parentSessionId = 'copilot-parent-sess-002'
    const invokeSpanId = 'invoke-agent-span-2'
    const expectedSubagentId = `${parentSessionId}#${invokeSpanId}`

    const parentTurn = copilotSpan(
      'parent-root-span-2',
      null,
      'llm.invoke',
      'chat gpt-4o',
      parentSessionId,
      { 'gen_ai.operation.name': 'chat' },
    )

    const invokeSpan = copilotSpan(
      invokeSpanId,
      'parent-root-span-2',
      'agent.invoke',
      'invoke_agent code-review',
      parentSessionId,
      {
        'gen_ai.operation.name': 'invoke_agent',
        'agent.name': 'code-review',
      },
    )

    const subagentChat = copilotSpan(
      'subagent-chat-span-2',
      invokeSpanId,
      'llm.invoke',
      'chat gpt-4o',
      parentSessionId,
      {
        'gen_ai.operation.name': 'chat',
        'gen_ai.prompt': 'Review security posture of authentication module',
      },
      {
        parts: [{ part: 'conversation_history', text: 'Review security posture of authentication module' }],
      },
    )

    const store = new CanonStore(':memory:')
    store.upsertMany([parentTurn, invokeSpan, subagentChat])
    await buildSessions(store)

    // Subagent row must extract agent_name from agent.name and format label with prompt fallback
    const subagentSession = store.getSession(expectedSubagentId)
    expect(subagentSession, `Subagent session row must exist for ${expectedSubagentId}`).toBeDefined()
    expect(subagentSession?.sessionId).toBe(expectedSubagentId)
    expect(subagentSession?.isSubagent).toBe(true)
    expect(subagentSession?.parentSession).toBe(parentSessionId)
    expect(subagentSession?.agentName).toBe('code-review')
    expect(subagentSession?.label).toBe('code-review · Review security posture of authentication module')

    const subagentDbRow = querySessionDbRow(store, expectedSubagentId)
    expect(subagentDbRow, `Subagent row in session table must exist for ${expectedSubagentId}`).toBeDefined()
    expect(subagentDbRow?.session_id).toBe(expectedSubagentId)
    expect(subagentDbRow?.is_subagent).toBe(1)
    expect(subagentDbRow?.parent_session).toBe(parentSessionId)
    expect(subagentDbRow?.agent_name).toBe('code-review')
    expect(subagentDbRow?.label).toBe('code-review · Review security posture of authentication module')

    store.close()
  })

  it('projects multiple subagents under the same parent session with distinct deterministic IDs', async () => {
    const parentSessionId = 'copilot-parent-sess-003'
    const invokeSpan1 = 'invoke-span-planner'
    const invokeSpan2 = 'invoke-span-testdev'
    const expectedSubagentId1 = `${parentSessionId}#${invokeSpan1}`
    const expectedSubagentId2 = `${parentSessionId}#${invokeSpan2}`

    const parentTurn = copilotSpan(
      'parent-root-span-3',
      null,
      'llm.invoke',
      'chat gpt-4o',
      parentSessionId,
      { 'gen_ai.operation.name': 'chat' },
    )

    const invoke1 = copilotSpan(
      invokeSpan1,
      'parent-root-span-3',
      'agent.invoke',
      'invoke_agent planner',
      parentSessionId,
      { 'gen_ai.operation.name': 'invoke_agent', 'gen_ai.agent.name': 'planner' },
    )
    const chat1 = copilotSpan(
      'chat-sub-1',
      invokeSpan1,
      'llm.invoke',
      'chat gpt-4o',
      parentSessionId,
      { 'gen_ai.operation.name': 'chat' },
    )
    const taskTool1 = copilotSpan(
      'task-tool-1',
      invokeSpan1,
      'tool.invoke',
      'task',
      parentSessionId,
      { 'gen_ai.tool.name': 'task', arguments: JSON.stringify({ description: 'Draft implementation plan' }) },
      { tokens: tokens({ freshInput: 0, reportedInput: 0, output: 0, reportedOutput: 0 }) },
    )

    const invoke2 = copilotSpan(
      invokeSpan2,
      'parent-root-span-3',
      'agent.invoke',
      'invoke_agent test-dev',
      parentSessionId,
      { 'gen_ai.operation.name': 'invoke_agent', 'gen_ai.agent.name': 'test-dev' },
    )
    const chat2 = copilotSpan(
      'chat-sub-2',
      invokeSpan2,
      'llm.invoke',
      'chat gpt-4o',
      parentSessionId,
      { 'gen_ai.operation.name': 'chat' },
    )
    const taskTool2 = copilotSpan(
      'task-tool-2',
      invokeSpan2,
      'tool.invoke',
      'task',
      parentSessionId,
      { 'gen_ai.tool.name': 'task', arguments: JSON.stringify({ description: 'Author contract tests' }) },
      { tokens: tokens({ freshInput: 0, reportedInput: 0, output: 0, reportedOutput: 0 }) },
    )

    const store = new CanonStore(':memory:')
    store.upsertMany([parentTurn, invoke1, chat1, taskTool1, invoke2, chat2, taskTool2])
    await buildSessions(store)

    const sessions = store.listSessions()
    expect(sessions.length, 'Must project 1 parent session and 2 subagent sessions').toBe(3)

    const sub1 = querySessionDbRow(store, expectedSubagentId1)
    expect(sub1).toBeDefined()
    expect(sub1?.is_subagent).toBe(1)
    expect(sub1?.parent_session).toBe(parentSessionId)
    expect(sub1?.agent_name).toBe('planner')
    expect(sub1?.label).toBe('planner · Draft implementation plan')

    const sub2 = querySessionDbRow(store, expectedSubagentId2)
    expect(sub2).toBeDefined()
    expect(sub2?.is_subagent).toBe(1)
    expect(sub2?.parent_session).toBe(parentSessionId)
    expect(sub2?.agent_name).toBe('test-dev')
    expect(sub2?.label).toBe('test-dev · Author contract tests')

    store.close()
  })

  it('guarantees identical subagent projection via projectCanonicalStore', async () => {
    const parentSessionId = 'copilot-parent-sess-004'
    const invokeSpanId = 'invoke-agent-span-4'
    const expectedSubagentId = `${parentSessionId}#${invokeSpanId}`

    const parentTurn = copilotSpan(
      'parent-root-span-4',
      null,
      'llm.invoke',
      'chat gpt-4o',
      parentSessionId,
      { 'gen_ai.operation.name': 'chat' },
    )

    const invokeSpan = copilotSpan(
      invokeSpanId,
      'parent-root-span-4',
      'agent.invoke',
      'invoke_agent dal-dev',
      parentSessionId,
      {
        'gen_ai.operation.name': 'invoke_agent',
        'gen_ai.agent.name': 'dal-dev',
      },
    )

    const subagentChat = copilotSpan(
      'subagent-chat-span-4',
      invokeSpanId,
      'llm.invoke',
      'chat gpt-4o',
      parentSessionId,
      { 'gen_ai.operation.name': 'chat' },
    )

    const taskTool = copilotSpan(
      'subagent-task-tool-4',
      invokeSpanId,
      'tool.invoke',
      'task',
      parentSessionId,
      {
        'gen_ai.tool.name': 'task',
        arguments: JSON.stringify({ description: 'Implement database repository query methods' }),
      },
      { tokens: tokens({ freshInput: 0, reportedInput: 0, output: 0, reportedOutput: 0 }) },
    )

    const store = new CanonStore(':memory:')
    store.upsertMany([parentTurn, invokeSpan, subagentChat, taskTool])
    await projectCanonicalStore(store)

    const subagentDbRow = querySessionDbRow(store, expectedSubagentId)
    expect(subagentDbRow, `Subagent row must exist through projectCanonicalStore`).toBeDefined()
    expect(subagentDbRow?.session_id).toBe(expectedSubagentId)
    expect(subagentDbRow?.is_subagent).toBe(1)
    expect(subagentDbRow?.parent_session).toBe(parentSessionId)
    expect(subagentDbRow?.agent_name).toBe('dal-dev')
    expect(subagentDbRow?.label).toBe('dal-dev · Implement database repository query methods')

    store.close()
  })
})
