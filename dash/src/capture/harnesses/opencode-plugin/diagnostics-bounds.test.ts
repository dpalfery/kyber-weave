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
    (sum, event) =>
      sum +
      (event.systemPrompt?.length ?? 0) * 2 +
      (event.toolsText?.length ?? 0) * 2 +
      (event.skills?.reduce((inner, skill) => inner + skill.length * 2, 0) ?? 0),
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

  it('countsMultibyteContentAndLargeIdentitiesAsBytes', async () => {
    const plugin = createOpencodePlugin({ now: () => NOW })
    const unicode = 'é🎉'.repeat(2_000)
    await plugin.context({ sessionId: `session-f2-unicode-${'s'.repeat(10_000)}`, systemPrompt: unicode })
    const event = plugin.events.at(-1)
    expect(event).toBeDefined()
    // UTF-16 storage is two bytes per unit: the estimate must cover it.
    expect(unicode.length * 2).toBeGreaterThan(unicode.length)
    expect(retainedContentBytes(plugin)).toBeGreaterThanOrEqual(unicode.length * 2)
    expect(retainedContentBytes(plugin)).toBeLessThanOrEqual(1_048_576)
  })

  it('omitsASingleOversizeEventButStillDeliversItWhole', async () => {
    const delivered: Array<{ systemPrompt?: string }> = []
    const plugin = createOpencodePlugin({
      now: () => NOW,
      emit: (event) => {
        delivered.push({ systemPrompt: event.systemPrompt })
      },
    })
    const oversize = 'x'.repeat(700_000)
    await plugin.context({ sessionId: 'session-f2-huge', systemPrompt: oversize })

    // The tail omits the record that alone exceeds the budget ...
    expect(plugin.events).toHaveLength(0)
    expect(plugin.droppedEvents).toBe(1)
    // ... while delivery still received the full original content.
    expect(delivered).toHaveLength(1)
    expect(delivered[0]?.systemPrompt?.length).toBe(700_000)
  })

  it('normalizesFailuresInsteadOfRetainingArbitraryErrorObjects', async () => {
    const plugin = createOpencodePlugin({
      now: () => NOW,
      emit: () => {
        const error = new Error('receiver down') as Error & { detail?: unknown }
        error.detail = { buffer: Buffer.alloc(1_000_000), nested: { deep: [1, 2, 3] } }
        throw error
      },
    })
    await plugin.hooks['turn.start']({ sessionId: 'session-f2-errshape', turnIndex: 1 })

    expect(plugin.failures).toHaveLength(1)
    const failure = plugin.failures[0]
    expect(failure).toMatchObject({ kind: 'turn.start', sessionId: 'session-f2-errshape' })
    expect(failure?.error).toEqual({ name: 'Error', message: 'receiver down' })
    // No buffer, cause, or custom field survives on the retained record.
    expect(JSON.stringify(plugin.failures).length).toBeLessThan(1_000)
  })

  it('keepsOfflinePendingDeliveryWholeAcrossSessionEndCleanup', async () => {
    let receiverUp = false
    const bodies: string[] = []
    const installed = createInstalledOpencodePlugin({
      fetchFn: async (_url: string, init?: RequestInit) => {
        if (!receiverUp) throw new Error('receiver down')
        bodies.push(String(init?.body ?? ''))
        return new Response('{}', { status: 200 })
      },
      sleepFn: async () => {},
      now: () => NOW,
    })
    const sessionId = 'session-f2-offline-cleanup'
    await installed.plugin.hooks['session.start']({ sessionId })
    await installed.plugin.context({ sessionId, systemPrompt: PROMPT })
    await installed.outbox.flushSettled()
    expect(installed.outbox.status().queued).toBe(2)

    // Session-end cleanup strips the diagnostic copies only.
    await installed.plugin.hooks['session.end']({ sessionId })
    expect(
      installed.plugin.events
        .filter((event) => event.sessionId === sessionId)
        .every((event) => event.systemPrompt === undefined),
    ).toBe(true)

    // The pending offline delivery still carries the full original content.
    receiverUp = true
    await installed.outbox.drain()
    await installed.outbox.close()
    expect(installed.outbox.status().queued).toBe(0)
    expect(bodies.length).toBeGreaterThan(0)
    expect(bodies.join('')).toContain(PROMPT)
  })
})
