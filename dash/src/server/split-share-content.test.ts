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

describe('turn content for a session split across harnesses', () => {
  it('serves the captured system prompt for the harness-qualified session id', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      turn('span-copilot', 'copilot', '2026-11-05T10:00:00.000Z'),
      turn('span-cli', 'copilot-cli', '2026-11-05T10:01:00.000Z'),
    ])
    await buildSessions(store)
    const split = store.sessionIdentities().shareOf('copilot:shared-key')
    expect(split).toEqual({ key: 'shared-key', harness: 'copilot' })

    const bridge = new KyberBridge({ canonPath: ':memory:', store })
    try {
      const content = bridge.assembleTurnContent('copilot:shared-key', 0)
      expect(content).not.toBeNull()
      const system = content!.blocks.find((b) => b.key === 'system_prompt')
      expect(system?.text).toContain('system prompt of span-copilot')
    } finally {
      bridge.close()
    }
  })
})
