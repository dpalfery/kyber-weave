import { describe, expect, it } from 'vitest'

import { buildAssistantCall } from './session-message.js'

// Regression for PR #264 review: the flat tokens_input/tokens_output/
// tokens_reasoning fallbacks were not validated as finite numbers. A string
// like "100" passed straight through buildAssistantCall, was persisted as a
// string token field, and calculateCost treated it as zero — undercounting
// cost. Invalid values must behave as absent so the `?? 0` default applies.
describe('buildAssistantCall flat token fallbacks', () => {
  const base = {
    providerName: 'kilo-code',
    dedupKey: 'kilo-code:sess-1:msg-1',
    sessionId: 'sess-1',
    parts: [{ type: 'text', text: 'answer' }],
    timeCreatedMs: 1700000001000,
    userMessage: 'question',
  }

  it('treats a string tokens_input as absent', () => {
    const call = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        tokens_input: '100' as unknown as number,
        tokens_output: 50,
      },
    })
    expect(call).not.toBeNull()
    expect(call!.inputTokens).toBe(0)
    expect(call!.outputTokens).toBe(50)
  })

  it('treats NaN, Infinity, and string flat fallbacks as absent', () => {
    const call = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        tokens_input: Number.NaN,
        tokens_output: Number.POSITIVE_INFINITY,
        tokens_reasoning: '5' as unknown as number,
      },
    })
    expect(call).not.toBeNull()
    expect(call!.inputTokens).toBe(0)
    expect(call!.outputTokens).toBe(0)
    expect(call!.reasoningTokens).toBe(0)
  })

  it('keeps numeric flat fallbacks and nested tokens/usage precedence unchanged', () => {
    const flat = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        tokens_input: 100,
        tokens_output: 50,
        tokens_reasoning: 5,
        tokens_cache_read: 10,
        tokens_cache_write: 20,
      },
    })
    expect(flat!.inputTokens).toBe(100)
    expect(flat!.outputTokens).toBe(50)
    expect(flat!.reasoningTokens).toBe(5)
    expect(flat!.cacheReadInputTokens).toBe(10)
    expect(flat!.cacheCreationInputTokens).toBe(20)

    const nested = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        tokens: { input: 7, output: 8, reasoning: 9, cache: { read: 3, write: 4 } },
        usage: { input_tokens: 70, output_tokens: 80 },
        tokens_input: 100,
        tokens_output: 50,
        tokens_reasoning: 5,
        tokens_cache_read: 10,
        tokens_cache_write: 20,
      },
    })
    expect(nested!.inputTokens).toBe(7)
    expect(nested!.outputTokens).toBe(8)
    expect(nested!.reasoningTokens).toBe(9)
    expect(nested!.cacheReadInputTokens).toBe(3)
    expect(nested!.cacheCreationInputTokens).toBe(4)

    const usageOnly = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        usage: { input_tokens: 70, output_tokens: 80, cache_read_input_tokens: 30, cache_creation_input_tokens: 40 },
        tokens_input: '100' as unknown as number,
        tokens_cache_read: 10,
      },
    })
    expect(usageOnly!.inputTokens).toBe(70)
    expect(usageOnly!.outputTokens).toBe(80)
    expect(usageOnly!.cacheReadInputTokens).toBe(30)
    expect(usageOnly!.cacheCreationInputTokens).toBe(40)
  })

  it('treats nested string token fields as absent', () => {
    const nested = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        tokens: { input: '100' as unknown as number, output: 50 },
      },
    })
    expect(nested).not.toBeNull()
    expect(nested!.inputTokens).toBe(0)
    expect(nested!.outputTokens).toBe(50)

    const usage = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        usage: { input_tokens: '100' as unknown as number, output_tokens: 50 },
      },
    })
    expect(usage).not.toBeNull()
    expect(usage!.inputTokens).toBe(0)
    expect(usage!.outputTokens).toBe(50)
  })

  it('normalizes a model object to providerID/id', () => {
    const call = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        model: { id: 'claude-sonnet-4', providerID: 'anthropic' },
      },
    })
    expect(call).not.toBeNull()
    expect(call!.model).toBe('anthropic/claude-sonnet-4')
  })

  it('recognizes markdown parts as substantive text activity', () => {
    const call = buildAssistantCall({
      providerName: 'kilo-code',
      dedupKey: 'kilo-code:sess-1:msg-2',
      sessionId: 'sess-1',
      parts: [{ type: 'markdown', text: 'assistant markdown response' }],
      timeCreatedMs: 1700000002000,
      userMessage: 'question',
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
      },
    })
    expect(call).not.toBeNull()
    expect(call!.sessionId).toBe('sess-1')
  })
})
