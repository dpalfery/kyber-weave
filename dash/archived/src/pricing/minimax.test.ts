import { describe, it, expect } from 'vitest'

import { getModelCosts, getShortModelName } from './models.js'

// Verifies MiniMax pricing loaded from FALLBACK_PRICING (no network call).
// pricingCache stays null until loadPricing() runs, so getModelCosts falls
// through to FALLBACK_PRICING which is what we want to validate here.

describe('MiniMax model pricing', () => {
  it('returns pricing for MiniMax-M2.7', () => {
    const costs = getModelCosts('MiniMax-M2.7')
    expect(costs).not.toBeNull()
    expect(costs!.inputCostPerToken).toBe(0.3e-6)
    expect(costs!.outputCostPerToken).toBe(1.2e-6)
    expect(costs!.cacheReadCostPerToken).toBe(0.06e-6)
    expect(costs!.cacheWriteCostPerToken).toBe(0.375e-6)
    expect(costs!.fastMultiplier).toBe(1)
  })

  it('returns pricing for MiniMax-M2.7-highspeed', () => {
    const costs = getModelCosts('MiniMax-M2.7-highspeed')
    expect(costs).not.toBeNull()
    expect(costs!.inputCostPerToken).toBe(0.6e-6)
    expect(costs!.outputCostPerToken).toBe(2.4e-6)
    expect(costs!.cacheReadCostPerToken).toBe(0.06e-6)
    expect(costs!.cacheWriteCostPerToken).toBe(0.375e-6)
    expect(costs!.fastMultiplier).toBe(1)
  })

  it('returns official pricing for MiniMax-M3', () => {
    // MiniMax moved M3 to tiered pricing (platform.minimax.io pay-as-you-go):
    // $0.3/$1.2 per M is the official standard tier (inputs up to 512K), with
    // $0.6/$2.4 above 512K. The snapshot carries the standard tier, matching
    // how other tiered models are priced here.
    const costs = getModelCosts('MiniMax-M3')
    expect(costs).not.toBeNull()
    expect(costs!.inputCostPerToken).toBe(0.3e-6)
    expect(costs!.outputCostPerToken).toBe(1.2e-6)
  })

  it('returns official pricing for MiniMax-M3.1-Flash', () => {
    // platform.minimax.io pricing page, retrieved 2026-11-09: $0.15 input, $0.60 output,
    // $0.015 cache read per million tokens; no cache-write price is published.
    for (const id of ['MiniMax-M3.1-Flash', 'minimax-m3.1-flash']) {
      const costs = getModelCosts(id)
      expect(costs, id).not.toBeNull()
      expect(costs!.inputCostPerToken).toBeCloseTo(0.15e-6, 15)
      expect(costs!.outputCostPerToken).toBeCloseTo(0.6e-6, 15)
      expect(costs!.cacheReadCostPerToken).toBeCloseTo(0.015e-6, 15)
      expect(costs!.cacheWriteCostIsExplicit).toBe(false)
    }
  })

  it('highspeed pricing is distinct from base model pricing', () => {
    const base = getModelCosts('MiniMax-M2.7')
    const fast = getModelCosts('MiniMax-M2.7-highspeed')
    expect(fast!.inputCostPerToken).toBeGreaterThan(base!.inputCostPerToken)
    expect(fast!.outputCostPerToken).toBeGreaterThan(base!.outputCostPerToken)
  })

  it('returns short name for MiniMax-M2.7', () => {
    expect(getShortModelName('MiniMax-M2.7')).toBe('MiniMax M2.7')
  })

  it('returns short name for MiniMax-M2.7-highspeed', () => {
    expect(getShortModelName('MiniMax-M2.7-highspeed')).toBe('MiniMax M2.7 Highspeed')
  })

  it('handles MiniMax model ID with date suffix', () => {
    expect(getShortModelName('MiniMax-M2.7-20260101')).toBe('MiniMax M2.7')
  })
})
