import { describe, it, expect } from 'vitest'
import { ingestBatch } from '../canon/ingest.js'
import { CanonStore } from '../canon/store.js'
import type { OtlpSpan } from './receiver.js'

/**
 * T1 diagnostic: Codex OTLP collector ingest path
 *
 * Tests whether live OTLP spans from Codex CLI are accepted by the ingest
 * pipeline or quarantined with a reason.
 *
 * Per D2 decision: verify both file-source and collector paths.
 *
 * T4 (D3 = A) registered `codexAdapter`. A span with the Codex fingerprint
 * (`codex.*` namespace plus GenAI usage) is accepted as harness `codex`; a span
 * carrying only the shared GenAI usage keys still claims no adapter and is
 * quarantined as 'unclaimed' (R6.1), whatever its service.name says (R6.2).
 *
 * Fixture attributes are SOURCE-VERIFIED names from the plan; the span name and
 * the token convention are UNVERIFIED pending an owner capture.
 */

function createCodexOtlpSpan(overrides?: Partial<OtlpSpan>): OtlpSpan {
  const now = Date.now()
  const nanoNow = String(now * 1_000_000)
  return {
    traceId: 'trace-codex-001',
    spanId: 'span-codex-001',
    parentSpanId: null,
    name: 'llm_request',
    kind: 'client',
    startTimeUnixNano: nanoNow,
    endTimeUnixNano: String(BigInt(nanoNow) + 100_000_000n),
    timestamp: new Date(now).toISOString(),
    durationMs: 100,
    status: { code: 'ok' },
    resource: { 'service.name': 'codex_cli_rs' },
    scope: {},
    attributes: {
      'gen_ai.model.name': 'gpt-5.3-codex',
      'gen_ai.usage.input_tokens': 100,
      'gen_ai.usage.output_tokens': 50,
    },
    ...overrides,
  }
}

describe('codex collector — OTLP ingest (T1 diagnostic)', () => {
  it('a span with the Codex fingerprint is accepted as harness codex, keyed by conversation.id', () => {
    const store = new CanonStore(':memory:')
    const span = createCodexOtlpSpan({
      spanId: 'span-codex-full',
      resource: { 'service.name': 'codex-app-server' },
      attributes: {
        model: 'gpt-5.3-codex',
        'conversation.id': 'conv-42',
        'gen_ai.usage.input_tokens': 1000,
        'gen_ai.usage.output_tokens': 200,
        'gen_ai.usage.cache_read.input_tokens': 400,
        'codex.usage.total_tokens': 1200,
      },
    })

    const outcome = ingestBatch([span], store)

    expect(outcome.accepted).toBe(1)
    expect(outcome.quarantined).toBe(0)
    const record = store.get('span-codex-full')
    expect(record?.harness).toBe('codex')
    expect(record?.sessionId).toBe('conv-42')
    store.close()
  })

  it('shared-usage-only span from codex_cli_rs is quarantined as unclaimed (service.name is not evidence)', () => {
    // R6.2: service.name alone is not evidence. Without a `codex.*` attribute the
    // fingerprint does not fire, so the span stays unclaimed.
    const store = new CanonStore(':memory:')
    const span = createCodexOtlpSpan()

    const outcome = ingestBatch([span], store)

    expect(outcome.accepted, 'shared GenAI usage alone does not claim Codex').toBe(0)
    expect(outcome.quarantined, 'the span is quarantined, not dropped').toBe(1)

    const quarantine = store.getQuarantine('span-codex-001')
    expect(quarantine).toBeDefined()
    expect(quarantine?.reason).toBe('unclaimed')

    store.close()
  })

  it('service.name codex_exec with shared-usage-only attributes is also unclaimed', () => {
    // Variant: some configs may emit service.name='codex' instead of 'codex-cli'
    const store = new CanonStore(':memory:')
    const span = createCodexOtlpSpan({
      spanId: 'span-codex-variant',
      resource: { 'service.name': 'codex_exec' },
    })

    const outcome = ingestBatch([span], store)

    expect(outcome.accepted).toBe(0)
    expect(outcome.quarantined).toBe(1)
    expect(store.getQuarantine('span-codex-variant')?.reason).toBe('unclaimed')

    store.close()
  })

  it('health span is quarantined as non-model span', () => {
    // Health/structural spans should be filtered by isStructuralSpan logic
    const store = new CanonStore(':memory:')
    const span = createCodexOtlpSpan({
      spanId: 'span-health',
      name: 'GlobalHttpApi.health',
      kind: 'server',
      attributes: {}, // No gen_ai attributes
    })

    const outcome = ingestBatch([span], store)

    expect(outcome.quarantined).toBe(1)
    expect(store.getQuarantine('span-health')?.reason).toMatch(/non.?model/i)

    store.close()
  })

  it('model and health span batch: model rejected, health quarantined', () => {
    // Both shapes in one batch: model fails due to missing adapter,
    // health is structural and quarantined separately
    const store = new CanonStore(':memory:')
    const modelSpan = createCodexOtlpSpan({ spanId: 'model-1' })
    const healthSpan = createCodexOtlpSpan({
      spanId: 'health-1',
      name: 'GlobalHttpApi.health',
      kind: 'server',
      attributes: {},
    })

    const outcome = ingestBatch([modelSpan, healthSpan], store)

    // Both should be out of the store
    expect(outcome.accepted).toBe(0)
    // Model quarantined as 'unclaimed', health as 'non-model span'
    expect(outcome.quarantined).toBe(2)
    expect(store.getQuarantine('model-1')?.reason).toBe('unclaimed')
    expect(store.getQuarantine('health-1')?.reason).toMatch(/non.?model/i)

    store.close()
  })
})
