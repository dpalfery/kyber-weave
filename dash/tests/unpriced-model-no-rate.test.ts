// A model the bundled pricing table does not publish costs `no_rate`, never $0.00 (R2.8).
//
// The bundled table and its cached refresh are the only pricing source now that the
// provider quota and usage APIs are gone, so the table's gaps are the product's gaps.
// The failure mode this pins is the quiet one: `calculateCost` answers 0 for a model it
// cannot price, which is indistinguishable from a genuinely free call until something
// downstream decides which it was. `costBlockFor` is that decision, and it has to keep
// reading the zero as absent — a $0.00 on screen is a claim, and this claim would be false.

import { beforeAll, describe, expect, it } from 'vitest'

import { calculateCost, getModelCosts, loadPricing } from '../src/models.js'
import { costBlockFor } from '../kyber/synth/synth.js'
import type { ParsedProviderCall } from '../kyber/synth/provider.js'

/** A model id no publisher will ever ship, so the table cannot price it. */
const ABSENT_MODEL = 'no-such-vendor/no-such-model-v0'
const PRICED_MODEL = 'claude-sonnet-4-5'

const INPUT_TOKENS = 1000
const OUTPUT_TOKENS = 500

/** `calculateCost(model, input, output, cacheCreation, cacheRead, webSearch)`. */
function priceOf(model: string): number {
  return calculateCost(model, INPUT_TOKENS, OUTPUT_TOKENS, 0, 0, 0)
}

function call(model: string, costUSD: number): ParsedProviderCall {
  return {
    provider: 'claude',
    model,
    costUSD,
    sessionId: 'session-1',
    timestamp: new Date('2026-09-18T10:00:00Z').toISOString(),
    inputTokens: INPUT_TOKENS,
    outputTokens: OUTPUT_TOKENS,
    cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0,
    cachedInputTokens: 0,
    reasoningTokens: 0,
    webSearchRequests: 0,
    tools: [],
    bashCommands: [],
    deduplicationKey: `dedup-${model}`,
  } as unknown as ParsedProviderCall
}

describe('a model absent from the pricing table (R2.8)', () => {
  beforeAll(async () => {
    await loadPricing()
  })

  it('is genuinely absent from the bundled table', () => {
    expect(getModelCosts(ABSENT_MODEL)).toBeNull()
    expect(getModelCosts(PRICED_MODEL)).not.toBeNull()
  })

  it('prices at zero, which is the table saying nothing rather than saying free', () => {
    expect(priceOf(ABSENT_MODEL)).toBe(0)
  })

  it('carries cost status no_rate, not a priced zero', () => {
    const block = costBlockFor(call(ABSENT_MODEL, priceOf(ABSENT_MODEL)))
    expect(block.status).toBe('no_rate')
    expect(block.basis).toBe('unknown')
    expect(block).not.toHaveProperty('value')
  })

  it('still prices a model the table does publish', () => {
    const cost = priceOf(PRICED_MODEL)
    expect(cost).toBeGreaterThan(0)
    const block = costBlockFor(call(PRICED_MODEL, cost))
    expect(block.status).toBe('priced')
    expect(block.value).toBeCloseTo(cost, 12)
  })
})
