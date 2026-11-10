import { describe, expect, it } from 'vitest'

import { buildSessions } from '../canon/sessions.js'
import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import { KyberBridge } from './bridge.js'

// A session key that spans two harnesses is split into one session per
// harness named `${harness}:${key}`. Records stay under the raw key, so the
// content readers must resolve the split id back to it. Before the fix every
// read for such a session returned nothing: the system prompt, tools and the
// per-turn bands all vanished in the dashboard.

function turn(spanId: string, harness: string, timestamp: string): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-shared-key',
    parentSpanId: null,
    sessionId: 'shared-key',
    source: 'synthetic',
    harness,
    name: 'chat gpt-5.6-luna',
    op: 'llm.invoke',
    kind: 'client',
    timestamp,
    durationMs: 100,
    status: 'ok',
    tokens: { freshInput: 100, cacheRead: 200, cacheCreation: 0, output: 10, reportedInput: 300, reportedOutput: 10 },
    content: {},
    parts: [{ part: 'system_prompt', text: `system prompt of ${spanId}`, tokens: 50 }],
    cost: { basis: 'published', status: 'no_rate' },
    raw: { model: 'gpt-5.6-luna' },
  }
}

async function fixture(): Promise<{ store: CanonStore; bridge: KyberBridge }> {
  const store = new CanonStore(':memory:')
  store.upsertMany([
    turn('span-copilot', 'copilot', '2026-11-05T10:00:00.000Z'),
    turn('span-cli', 'copilot-cli', '2026-11-05T10:01:00.000Z'),
  ])
  await buildSessions(store)
  return { store, bridge: new KyberBridge({ canonPath: ':memory:', store }) }
}

describe('turn content for a session split across harnesses', () => {
  it.each([
    ['copilot:shared-key', 'span-copilot', 'span-cli'],
    ['copilot-cli:shared-key', 'span-cli', 'span-copilot'],
  ])('serves only its own share for %s', async (sessionId, own, sibling) => {
    const { store, bridge } = await fixture()
    try {
      expect(store.sessionIdentities().shareOf(sessionId)).toBeDefined()
      const system = bridge.assembleTurnContent(sessionId, 0)?.blocks.find((b) => b.key === 'system_prompt')
      expect(system?.text).toContain(`system prompt of ${own}`)
      expect(system?.text).not.toContain(sibling)
      // The sibling harness's turn is not this share's second turn.
      expect(bridge.assembleTurnContent(sessionId, 1)).toBeNull()
    } finally {
      bridge.close()
    }
  })

  it('refuses a sibling harness span and keeps the share known only by its own records', async () => {
    const { bridge } = await fixture()
    try {
      const priv = bridge as unknown as {
        loadContentRecords(id: string, span?: string): unknown[]
        loadSessionSystemPrompts(id: string): unknown[]
        sessionKnown(id: string): boolean
      }
      expect(priv.loadContentRecords('copilot:shared-key', 'span-cli')).toEqual([])
      expect(priv.loadContentRecords('copilot:shared-key', 'span-copilot')).toHaveLength(1)
      expect(priv.loadContentRecords('copilot:shared-key')).toHaveLength(1)
      expect(priv.loadSessionSystemPrompts('copilot:shared-key')).toEqual([])
      expect(priv.sessionKnown('copilot:shared-key')).toBe(true)
    } finally {
      bridge.close()
    }
  })
})
