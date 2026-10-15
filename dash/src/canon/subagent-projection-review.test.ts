// Review follow-ups for the subagent projection (PR 367): delimiter-exact invoke_agent
// detection, and absent/corrupt timestamps never read as the epoch.

import { describe, expect, it } from 'vitest'

import { tokens, turn } from './fixtures/records.js'
import { buildSessions } from './sessions.js'
import { CanonStore } from './store.js'
import type { CanonicalRecord } from './types.js'

function span(
  harness: 'copilot' | 'claude-code',
  spanId: string,
  parentSpanId: string | null,
  op: string,
  name: string,
  sessionId: string,
  raw: Record<string, unknown> = {},
  overrides: Partial<CanonicalRecord> = {},
): CanonicalRecord {
  return turn(spanId, [], {
    harness,
    source: harness === 'copilot' ? 'github-copilot' : 'claude-code',
    sessionId,
    parentSpanId,
    op,
    name,
    timestamp: '2026-10-14T10:00:00.000Z',
    raw: harness === 'copilot' ? { 'copilot_chat.chat_session_id': sessionId, ...raw } : raw,
    tokens: tokens({ freshInput: 100, reportedInput: 100, output: 10, reportedOutput: 10 }),
    ...overrides,
  })
}

const subagentIds = (store: CanonStore): string[] =>
  store.builtSessionIds().filter((id) => id.includes('#') || id.startsWith('agent-'))

describe('invoke_agent detection is delimiter-exact', () => {
  it.each(['invoke_agent_foo', 'invoke_agents', 'invoke_agentic loop'])(
    'a span named %s is not a subagent invocation',
    async (name) => {
      const sessionId = 'copilot-sess-name'
      const store = new CanonStore(':memory:')
      store.upsertMany([
        span('copilot', 'root', null, 'llm.invoke', 'chat gpt-4o', sessionId),
        span('copilot', 'phantom', 'root', 'llm.invoke', name, sessionId),
      ])
      await buildSessions(store)
      expect(subagentIds(store)).toEqual([])
      store.close()
    },
  )

  it.each(['invoke_agent', 'invoke_agent architect', 'invoke_agent:architect'])(
    'a span named %s still is',
    async (name) => {
      const sessionId = 'copilot-sess-name-ok'
      const store = new CanonStore(':memory:')
      store.upsertMany([
        span('copilot', 'root', null, 'llm.invoke', 'chat gpt-4o', sessionId),
        span('copilot', 'inv', 'root', 'llm.invoke', name, sessionId),
        span('copilot', 'child', 'inv', 'llm.invoke', 'chat gpt-4o', sessionId),
      ])
      await buildSessions(store)
      expect(subagentIds(store)).toEqual([`${sessionId}#inv`])
      store.close()
    },
  )
})

describe('a corrupt or missing timestamp is not the epoch', () => {
  it('Claude subagent start and end come from the valid records only', async () => {
    const sessionId = 'claude-sess-ts'
    const agent = { agent_id: 'a1b2c3d4e5f6a7b8c' }
    const store = new CanonStore(':memory:')
    store.upsertMany([
      span('claude-code', 'root', null, 'llm.invoke', 'chat claude', sessionId, {}, { timestamp: '2026-10-14T09:59:00.000Z' }),
      span('claude-code', 'sub-bad', 'root', 'llm.invoke', 'chat claude', sessionId, agent, { timestamp: 'not-a-date' }),
      span('claude-code', 'sub-spawn', 'root', 'agent.invoke', 'Task', sessionId, agent, { timestamp: '2026-10-14T10:00:05.000Z' }),
      span('claude-code', 'sub-late', 'sub-spawn', 'llm.invoke', 'chat claude', sessionId, agent, { timestamp: '2026-10-14T10:00:09.000Z' }),
    ])
    await buildSessions(store)

    const sub = store.getSession('agent-a1b2c3d4e5f6a7b8c')
    expect(sub?.started).toBe('2026-10-14T10:00:05.000Z')
    expect(sub?.ended).toBe('2026-10-14T10:00:09.000Z')
    store.close()
  })
})
