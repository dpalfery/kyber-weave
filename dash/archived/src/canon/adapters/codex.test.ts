import { describe, expect, it } from 'vitest'

import { codexAdapter } from './codex.js'
import { rawSpan } from './testing.js'
import { ADAPTERS } from '../ingest.js'

/**
 * Codex OTLP adapter (issue #196, T4b). Attribute names come from the plan's
 * source research of the OpenAI Codex exporters:
 *   - SOURCE-VERIFIED: conversation.id, model, slug, gen_ai.usage.*, codex.usage.*,
 *     service.name codex_cli_rs / codex_exec / codex-app-server.
 *   - UNVERIFIED (owner capture before production use): the exported span name of
 *     handle_responses, trace_id/span_id on log records, and the token convention.
 *     The convention is ASSUMED cache-inclusive (OpenAI style); if real payloads are
 *     exclusive, validation fails loudly (negative fresh input) rather than miscounting.
 */
const usage = (overrides: Record<string, unknown> = {}) => ({
  'gen_ai.usage.input_tokens': 1000,
  'gen_ai.usage.output_tokens': 200,
  'gen_ai.usage.cache_read.input_tokens': 400,
  'gen_ai.usage.cache_write.input_tokens': 100,
  'codex.usage.reasoning_output_tokens': 50,
  'codex.usage.total_tokens': 1200,
  'conversation.id': 'conv-1',
  model: 'gpt-5.3-codex',
  ...overrides,
})

describe('codexAdapter.detect', () => {
  it.each([
    ['full fingerprint', usage(), 1],
    ['codex namespace alone', { 'codex.usage.total_tokens': 5 }, 0.6],
    ['shared GenAI usage alone', { 'gen_ai.usage.input_tokens': 5 }, 0.4],
  ])('scores %s as %s', (_label, attributes, score) => {
    expect(codexAdapter.detect(rawSpan({ spanId: 'a', attributes }))).toBe(score)
  })

  it('never reads the source name (R6.2)', () => {
    const span = rawSpan({ spanId: 'a', source: 'codex_cli_rs', attributes: { 'gen_ai.usage.input_tokens': 5 } })
    expect(codexAdapter.detect(span)).toBe(0.4)
  })

  it('is registered with the production adapters', () => {
    expect(ADAPTERS).toContain(codexAdapter)
  })
})

describe('codexAdapter.normalize', () => {
  it('converts cache-inclusive counters into disjoint classes, with session and reasoning', () => {
    const record = codexAdapter.normalize(rawSpan({ spanId: 's1', attributes: usage() }))
    expect(record.harness).toBe('codex')
    expect(record.sessionId).toBe('conv-1')
    expect(record.tokens).toMatchObject({
      freshInput: 500,
      cacheRead: 400,
      cacheCreation: 100,
      output: 200,
      reasoning: 50,
      reportedInput: 1000,
    })
    expect(codexAdapter.validate(record)).toBeUndefined()
  })

  it('surfaces an exclusive-convention payload as a validation problem, not a silent miscount', () => {
    const record = codexAdapter.normalize(
      rawSpan({
        spanId: 's2',
        attributes: usage({ 'gen_ai.usage.input_tokens': 100, 'gen_ai.usage.cache_read.input_tokens': 400 }),
      }),
    )
    expect(codexAdapter.validate(record)).toBeDefined()
  })

  it('does not persist prompts, tool arguments/output, or account identity', () => {
    const record = codexAdapter.normalize(
      rawSpan({
        spanId: 's3',
        attributes: usage({
          'user.email': 'a@example.com',
          'user.account_id': 'acct-1',
          prompt: 'secret prompt',
          'tool.arguments': '{"cmd":"rm"}',
          'tool.output': 'out',
          'gen_ai.input.messages': 'secret',
        }),
      }),
    )
    const serialized = JSON.stringify(record)
    for (const leaked of ['a@example.com', 'acct-1', 'secret', 'rm', '"out"']) {
      expect(serialized, leaked).not.toContain(leaked)
    }
    expect(record.raw).toMatchObject({ model: 'gpt-5.3-codex', 'conversation.id': 'conv-1' })
  })

  it('declares tool_definitions unexported', () => {
    expect(codexAdapter.unexportedMetrics()).toContain('tool_definitions')
  })
})
