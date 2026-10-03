import { describe, expect, it } from 'vitest'

import { buildAssistantCall } from './session-message.js'

// Regression for PR #264 review: the flat tokens_input/tokens_output/
// tokens_reasoning fallbacks were not validated as finite numbers. A string
// like "100" passed straight through buildAssistantCall, was persisted as a
// string token field, and calculateCost treated it as zero — undercounting
// cost. Invalid values must behave as absent so the `?? 0` default applies.
describe('buildAssistantCall flat token fallbacks', () => {
  const base = {
    providerName: 'opencode',
    dedupKey: 'opencode:sess-1:msg-1',
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
      },
    })
    expect(flat!.inputTokens).toBe(100)
    expect(flat!.outputTokens).toBe(50)
    expect(flat!.reasoningTokens).toBe(5)

    const nested = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        tokens: { input: 7, output: 8, reasoning: 9 },
        usage: { input_tokens: 70, output_tokens: 80 },
        tokens_input: 100,
        tokens_output: 50,
        tokens_reasoning: 5,
      },
    })
    expect(nested!.inputTokens).toBe(7)
    expect(nested!.outputTokens).toBe(8)
    expect(nested!.reasoningTokens).toBe(9)

    const usageOnly = buildAssistantCall({
      ...base,
      data: {
        role: 'assistant',
        modelID: 'claude-opus-4-6',
        usage: { input_tokens: 70, output_tokens: 80 },
        tokens_input: '100' as unknown as number,
      },
    })
    expect(usageOnly!.inputTokens).toBe(70)
    expect(usageOnly!.outputTokens).toBe(80)
  })
})
