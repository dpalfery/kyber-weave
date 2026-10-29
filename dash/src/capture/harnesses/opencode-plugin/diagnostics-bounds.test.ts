// F2 regression: production diagnostic retention must be bounded.
//
// The skeleton recorded every delivery attempt and every failure in
// process-lifetime arrays, so a long session retained every payload in the
// OpenCode host process. These tests pin the fix: count- and byte-bounded
// diagnostic rings (drop-oldest with counters) plus session-end release of
// the ended session's payload bodies, on both the skeleton and the real
// installed pipeline (successful and failed delivery).

import { describe, expect, it } from 'vitest'

import { createInstalledOpencodePlugin, createOpencodePlugin } from './plugin.js'

const NOW = '2026-10-29T00:00:00.000Z'
const PROMPT = 'p'.repeat(8_192)

async function fireContext(
  plugin: ReturnType<typeof createOpencodePlugin>,
  sessionId: string,
  count: number,
): Promise<void> {
  for (let i = 0; i < count; i += 1) {
    await plugin.context({ sessionId, turnIndex: i, systemPrompt: PROMPT, toolsText: PROMPT })
  }
}

function retainedContentBytes(plugin: ReturnType<typeof createOpencodePlugin>): number {
  return plugin.events.reduce(
    (sum, event) => sum + (event.systemPrompt?.length ?? 0) + (event.toolsText?.length ?? 0),
    0,
  )
}

describe('Opencode plugin diagnostics are bounded (F2)', () => {
  it('boundsSuccessfulDeliveryHistoryByCountAndBytes', async () => {
    const plugin = createOpencodePlugin({ now: () => NOW })
    await fireContext(plugin, 'session-f2-count', 2_000)

    expect(plugin.events.length).toBeLessThanOrEqual(200)
    expect(retainedContentBytes(plugin)).toBeLessThanOrEqual(1_048_576)
    expect(plugin.droppedEvents).toBe(2_000 - plugin.events.length)
    // The ring keeps the freshest boundary, not the oldest.
    expect(plugin.events.at(-1)).toMatchObject({ kind: 'session.context', turnIndex: 1_999 })
  })

  it('releasesAnEndedSessionsPayloadBodies', async () => {
    const plugin = createOpencodePlugin({ now: () => NOW })
    await plugin.hooks['session.start']({ sessionId: 'session-f2-end' })
    await fireContext(plugin, 'session-f2-end', 10)
    await plugin.hooks['session.end']({ sessionId: 'session-f2-end' })

    const ended = plugin.events.filter((event) => event.sessionId === 'session-f2-end')
    expect(ended.length).toBeGreaterThan(0)
    expect(
      ended.reduce(
        (sum, event) => sum + (event.systemPrompt?.length ?? 0) + (event.toolsText?.length ?? 0),
        0,
      ),
    ).toBe(0)
    // Identity survives the release: the boundary record stays queryable.
    expect(ended.map((event) => event.kind)).toContain('session.end')
  })

  it('boundsFailedDeliveryHistoryToo', async () => {
    const plugin = createOpencodePlugin({
      now: () => NOW,
      emit: () => {
        throw new Error('receiver down')
      },
    })
    for (let i = 0; i < 500; i += 1) {
      await plugin.hooks['turn.start']({ sessionId: 'session-f2-fail', turnIndex: i })
    }

    expect(plugin.failures.length).toBeLessThanOrEqual(100)
    expect(plugin.droppedFailures).toBe(500 - plugin.failures.length)
    expect(plugin.failures.at(-1)).toMatchObject({ kind: 'turn.start' })
  })

  it('keepsTheInstalledPipelineBoundedOnSuccessfulDelivery', async () => {
    const installed = createInstalledOpencodePlugin({
      fetchFn: async () => new Response('{}', { status: 200 }),
      sleepFn: async () => {},
      now: () => NOW,
    })
    for (let i = 0; i < 500; i += 1) {
      await installed.plugin.context({ sessionId: 'session-f2-installed', turnIndex: i, systemPrompt: PROMPT })
    }
    await installed.outbox.drain()
    await installed.outbox.close()

    expect(installed.plugin.events.length).toBeLessThanOrEqual(200)
    expect(retainedContentBytes(installed.plugin)).toBeLessThanOrEqual(1_048_576)
  })

  it('keepsTheInstalledPipelineBoundedOnFailedDelivery', async () => {
    const installed = createInstalledOpencodePlugin({
      fetchFn: async () => {
        throw new Error('receiver down')
      },
      sleepFn: async () => {},
      now: () => NOW,
    })
    for (let i = 0; i < 300; i += 1) {
      await installed.plugin.context({ sessionId: 'session-f2-installed-fail', turnIndex: i, systemPrompt: PROMPT })
    }
    await installed.outbox.flushSettled()
    await installed.outbox.close()

    expect(installed.plugin.events.length).toBeLessThanOrEqual(200)
    expect(installed.plugin.failures.length).toBeLessThanOrEqual(100)
    expect(retainedContentBytes(installed.plugin)).toBeLessThanOrEqual(1_048_576)
  })
})
