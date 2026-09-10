import { describe, it, expect } from 'vitest'

import { AI_CREDIT_USD, creditsToUsd, isFiniteNanoAiu, nanoAiuToCredits } from '../src/copilot-aiu.js'

describe('copilot AIU helpers', () => {
  it('converts 1.5e9 nano-AIU to 1.5 credits and $0.015', () => {
    expect(nanoAiuToCredits(1_500_000_000)).toBe(1.5)
    expect(creditsToUsd(1.5)).toBeCloseTo(0.015, 12)
    expect(1.5 / 1500 * 100).toBeCloseTo(0.1, 12)
    expect(1.5 * AI_CREDIT_USD).toBeCloseTo(0.015, 12)
  })

  it('accepts only finite nanoAiu', () => {
    expect(isFiniteNanoAiu(1_500_000_000)).toBe(true)
    expect(isFiniteNanoAiu(0)).toBe(true)
    expect(isFiniteNanoAiu(undefined)).toBe(false)
    expect(isFiniteNanoAiu(Number.NaN)).toBe(false)
    expect(isFiniteNanoAiu(Number.POSITIVE_INFINITY)).toBe(false)
  })
})
