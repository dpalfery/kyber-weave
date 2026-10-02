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
 * Every test here CHARACTERISES current behaviour and passes today: no Codex
 * adapter is registered in ADAPTERS (dash/src/canon/ingest.ts, the `ADAPTERS`
 * array), so a Codex model span claims no adapter and is quarantined with reason
 * 'unclaimed'. These are not RED tests.
 *
 * No intentional-RED contract test is added: nothing in docs/dash or the
 * adapter specs states that Codex CLI emits OTLP or that a Codex OTLP model span
 * must be accepted (the telemetry inventory documents Codex via rollout files
 * only). Whether Codex spans SHOULD be accepted is therefore an owner decision
 * (plan D2 / T4), not a contract this file can assert.
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
    resource: { 'service.name': 'codex-cli' },
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
  it('characterisation: codex-cli model span is quarantined as unclaimed (no Codex adapter registered)', () => {
    // Current behaviour: Codex has no adapter in the ingest.ts ADAPTERS registry,
    // so the span is quarantined with reason 'unclaimed'.
    const store = new CanonStore(':memory:')
    const span = createCodexOtlpSpan()

    const outcome = ingestBatch([span], store)

    expect(outcome.accepted, 'no adapter claims a codex-cli span').toBe(0)
    expect(outcome.quarantined, 'the span is quarantined, not dropped').toBe(1)

    const quarantine = store.getQuarantine('span-codex-001')
    expect(quarantine).toBeDefined()
    expect(quarantine?.reason).toBe('unclaimed')

    store.close()
  })

  it('service.name codex also produces unclaimed quarantine', () => {
    // Variant: some configs may emit service.name='codex' instead of 'codex-cli'
    const store = new CanonStore(':memory:')
    const span = createCodexOtlpSpan({
      spanId: 'span-codex-variant',
      resource: { 'service.name': 'codex' },
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
