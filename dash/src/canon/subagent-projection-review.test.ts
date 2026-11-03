// Review follow-ups for the subagent projection (PR 367): delimiter-exact invoke_agent
// detection, and absent/corrupt timestamps never read as the epoch.

import { describe, expect, it } from 'vitest'

import { tokens, turn } from './fixtures/records.js'
import { join } from 'node:path'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'

import { KyberBridge } from '../server/bridge.js'
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

    const sub = store.getSession('claude-sess-ts#agent-a1b2c3d4e5f6a7b8c')
    expect(sub?.started).toBe('2026-10-14T10:00:05.000Z')
    expect(sub?.ended).toBe('2026-10-14T10:00:09.000Z')
    store.close()
  })
})

describe('projected subagents are marked as included in their parent', () => {
  it.each([
    ['copilot', 'copilot-sess-inc', 'copilot-sess-inc#inv', (id: string) => [
      span('copilot', 'root', null, 'llm.invoke', 'chat gpt-4o', id),
      span('copilot', 'inv', 'root', 'agent.invoke', 'invoke_agent architect', id, { 'gen_ai.agent.name': 'architect' }),
      span('copilot', 'child', 'inv', 'llm.invoke', 'chat gpt-4o', id),
    ]],
    ['claude-code', 'claude-sess-inc', 'claude-sess-inc#agent-a1b2c3d4e5f6a7b8c', (id: string) => {
      const agent = { agent_id: 'a1b2c3d4e5f6a7b8c' }
      return [
        span('claude-code', 'root', null, 'llm.invoke', 'chat claude', id),
        span('claude-code', 'spawn', 'root', 'agent.invoke', 'Task', id, agent),
        span('claude-code', 'child', 'spawn', 'llm.invoke', 'chat claude', id, agent),
      ]
    }],
  ] as const)('%s: the child is flagged, and the parent rollup is not inflated', async (_h, sessionId, childId, records) => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-included-'))
    const store = new CanonStore(join(dir, 'canon.db'))
    store.upsertMany(records(sessionId))
    await buildSessions(store)

    const flag = (id: string) =>
      (store.getSession(id)?.summary as { included_in_parent?: boolean } | undefined)?.included_in_parent

    expect(flag(childId)).toBe(true)
    expect(flag(sessionId)).toBeUndefined()

    const bridge = new KyberBridge({ store, canonPath: join(dir, 'canon.db') })
    const [root] = bridge.listSessions()
    expect(root?.session_id).toBe(sessionId)
    expect(root?.subagents?.map((s) => s.session_id)).toEqual([childId])
    expect(root?.rollup?.turn_count).toBe(root?.turn_count)
    bridge.close?.()
    store.close()
    rmSync(dir, { recursive: true, force: true })
  })
})

describe('Claude subagent ids are scoped by parent', () => {
  const sharedAgent = { agent_id: 'researcher-001' }
  const family = (sessionId: string, suffix: string): CanonicalRecord[] => [
    span('claude-code', `root-${suffix}`, null, 'llm.invoke', 'chat claude', sessionId),
    span('claude-code', `spawn-${suffix}`, `root-${suffix}`, 'agent.invoke', 'Task', sessionId, {
      ...sharedAgent,
      description: `work for ${suffix}`,
    }),
    span('claude-code', `chat-${suffix}`, `spawn-${suffix}`, 'llm.invoke', 'chat claude', sessionId, sharedAgent),
  ]

  it('keeps one row per parent when two parents reuse the same agent id', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([...family('parent-one', 'one'), ...family('parent-two', 'two')])
    await buildSessions(store)

    const one = store.getSession('parent-one#agent-researcher-001')
    const two = store.getSession('parent-two#agent-researcher-001')
    expect(one?.parentSession).toBe('parent-one')
    expect(two?.parentSession).toBe('parent-two')
    expect(one?.label).toContain('work for one')
    expect(two?.label).toContain('work for two')
    expect(store.getSession('agent-researcher-001')).toBeUndefined()
    store.close()
  })

  it('does not double the agent- prefix when the id already carries it', async () => {
    const sessionId = 'parent-prefixed'
    const store = new CanonStore(':memory:')
    store.upsertMany([
      span('claude-code', 'root', null, 'llm.invoke', 'chat claude', sessionId),
      span('claude-code', 'spawn', 'root', 'agent.invoke', 'Task', sessionId, { agent_id: 'agent-a1b2c3d4' }),
      span('claude-code', 'chat', 'spawn', 'llm.invoke', 'chat claude', sessionId, { agent_id: 'agent-a1b2c3d4' }),
    ])
    await buildSessions(store)

    expect(subagentIds(store)).toEqual([`${sessionId}#agent-a1b2c3d4`])
    store.close()
  })

  it('does not project a subagent row as a parent of its own subagents', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany(family('parent-once', 'once'))
    await buildSessions(store)
    await buildSessions(store)

    expect(store.builtSessionIds().sort()).toEqual(['parent-once', 'parent-once#agent-researcher-001'])
    store.close()
  })
})
