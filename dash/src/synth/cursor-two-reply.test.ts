// P2.T5 — Cursor claimed-turn guard: one request with two token-bearing
// reply bubbles must not fan the same reader turn out to both usage spans.
// Exactly one synthesized `llm.invoke` carries the reader parts and window.

import { describe, expect, it } from 'vitest'
import { fileURLToPath } from 'node:url'

import type { ParsedProviderCall } from '../providers/types.js'
import { ingestProviders } from './provider.js'

const SESSION = 'cursor-pairing-session'
const REQUEST = 'cursor-pairing-req-1'
const WINDOW = 128000

function fixturePath(): string {
  return fileURLToPath(new URL('./fixtures/cursor-pairing.json', import.meta.url))
}

function cursorCall(spec: Partial<ParsedProviderCall> = {}): ParsedProviderCall {
  return {
    provider: 'cursor',
    model: 'gpt-5',
    inputTokens: 0,
    outputTokens: 0,
    cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0,
    cachedInputTokens: 0,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0.001,
    costIsEstimated: true,
    tools: [],
    bashCommands: [],
    timestamp: '2026-09-22T10:03:01.000Z',
    speed: 'standard',
    deduplicationKey: `cursor:${SESSION}:reply`,
    userMessage: 'synthetic pairing reply carrying token usage',
    sessionId: SESSION,
    ...spec,
  }
}

describe('P2.T5 Cursor two-reply claimed-turn guard', () => {
  it('attaches reader parts to exactly one call when two token-bearing replies share a request', async () => {
    const promptCall = cursorCall({
      inputTokens: 120,
      outputTokens: 0,
      timestamp: '2026-09-22T10:03:00.000Z',
      deduplicationKey: `cursor:${SESSION}:prompt`,
      userMessage: 'synthetic pairing prompt about workspace wiring',
      turnId: REQUEST,
    })
    const firstReply = cursorCall({
      inputTokens: 240,
      outputTokens: 60,
      timestamp: '2026-09-22T10:03:01.000Z',
      deduplicationKey: `cursor:${SESSION}:reply-a`,
      userMessage: 'first token-bearing reply for the request',
      pairingId: REQUEST,
    })
    const secondReply = cursorCall({
      inputTokens: 180,
      outputTokens: 40,
      timestamp: '2026-09-22T10:03:02.000Z',
      deduplicationKey: `cursor:${SESSION}:reply-b`,
      userMessage: 'second token-bearing reply for the same request',
      pairingId: REQUEST,
    })

    const result = await ingestProviders(['cursor'], () => ({
      calls: [promptCall, firstReply, secondReply],
      filePath: fixturePath(),
      harnessId: 'cursor',
      sourceKey: `cursor:${SESSION}`,
    }))

    expect(result.problems).toEqual([])
    const invokes = result.records.filter((record) => record.op === 'llm.invoke')
    expect(invokes).toHaveLength(3)
    expect(new Set(invokes.map((record) => record.spanId))).toHaveLength(3)

    const carriers = invokes.filter((record) => (record.parts ?? []).length > 0)
    expect(carriers).toHaveLength(1)

    const carrier = carriers[0]!
    expect(carrier.content.instruction_context).toContain('synthetic pairing instruction alpha')
    expect(carrier.content.conversation_history).toContain('synthetic pairing prompt about workspace wiring')
    expect(carrier.content.tool_result_content).toContain('synthetic pairing tool context beta')
    expect((carrier.raw as Record<string, unknown>)['contextWindow']).toBe(WINDOW)

    for (const nonCarrier of invokes.filter((record) => record !== carrier)) {
      expect(nonCarrier.parts ?? []).toEqual([])
      expect(nonCarrier.content).toEqual({})
      expect(nonCarrier.raw).not.toHaveProperty('contextWindow')
    }
  })
})
