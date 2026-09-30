// Issue #186 Defect B (plan docs/archive/plans/2026-09-30-issue-186.md, T5 RED). The published-rate
// pricer prices API-billed harnesses (claude-code, codex) from the bundled LiteLLM snapshot
// under R5.3 applicability scoping. Assumed contract for T6:
//   pricePublishedTurn(tokens, model, harness, existing?) -> CostBlock
// where `existing`, when it is a harness-basis block, is returned untouched (R5.2).
import { afterEach, beforeAll, describe, expect, it } from 'vitest'

import {
  calculateCost,
  loadPricing,
  setFlatRateModels,
  setFlatRateRemoved,
  setModelAliases,
  setPriceOverrides,
  GPT_6_LUNA_PROMPT_TOKEN_THRESHOLD,
} from '../pricing/models.js'
import { pricePublishedTurn } from './published-pricing.js'
import type { CostBlock, TokenUsage } from './types.js'

beforeAll(async () => {
  await loadPricing()
})

afterEach(() => {
  setPriceOverrides({})
  setModelAliases({})
  setFlatRateModels([])
  setFlatRateRemoved([])
})

function tokens(p: Partial<TokenUsage> = {}): TokenUsage {
  const t = { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0, ...p }
  return { ...t, reportedInput: t.freshInput + t.cacheRead + t.cacheCreation, reportedOutput: t.output }
}

const MEASURED = tokens({ freshInput: 120_000, cacheRead: 300_000, cacheCreation: 40_000, output: 25_000 })

function expectedFor(model: string, t: TokenUsage): number {
  return calculateCost(model, t.freshInput, t.output, t.cacheCreation, t.cacheRead, 0)
}

describe('pricePublishedTurn: API-billed harnesses', () => {
  it.each(['claude-code', 'claude-cli', 'claude-desktop'])(
    '%s + claude-opus-5 is priced on the published basis, equal to calculateCost',
    (harness) => {
      const block = pricePublishedTurn(MEASURED, 'claude-opus-5', harness)
      expect(block.basis).toBe('published')
      expect(block.status).toBe('priced')
      expect(block.currency).toBe('USD')
      expect(block.value).toBeCloseTo(expectedFor('claude-opus-5', MEASURED), 10)
      expect(block.value).toBeGreaterThan(0)
      expect(block.byModel?.['claude-opus-5']).toBeCloseTo(block.value!, 10)
    },
  )

  it.each(['codex', 'codex-cli'])('%s prices a snapshot model (gpt-5.6-luna) like calculateCost', (harness) => {
    const block = pricePublishedTurn(MEASURED, 'gpt-5.6-luna', harness)
    expect(block).toMatchObject({ basis: 'published', status: 'priced', currency: 'USD' })
    expect(block.value).toBeCloseTo(expectedFor('gpt-5.6-luna', MEASURED), 10)
  })

  it('charges cache classes at their own rates, not the input rate', () => {
    const cached = pricePublishedTurn(tokens({ cacheRead: 1_000_000 }), 'claude-opus-5', 'claude-code')
    expect(cached.value).toBeCloseTo(0.5, 10) // 0.50 / 1M cache read, not 5.00 input
  })
})

describe('pricePublishedTurn: R5.3 applicability scoping', () => {
  it.each(['copilot', 'copilot-cli', 'zcode'])('%s is out_of_scope for the LiteLLM table even for a snapshot model', (harness) => {
    const block = pricePublishedTurn(MEASURED, 'claude-opus-5', harness)
    expect(block).toMatchObject({ basis: 'published', status: 'out_of_scope' })
    expect(block.value).toBeUndefined()
  })

  it('stays out_of_scope for copilot when a price override exists', () => {
    setPriceOverrides({ 'claude-opus-5': { input: 1, output: 1 } })
    expect(pricePublishedTurn(MEASURED, 'claude-opus-5', 'copilot').status).toBe('out_of_scope')
  })
})

describe('pricePublishedTurn: U11 additions', () => {
  it('prices claude-sonnet-5-5 at 2.00 input / 10.00 output per 1M', () => {
    const block = pricePublishedTurn(tokens({ freshInput: 1_000_000, output: 1_000_000 }), 'claude-sonnet-5-5', 'claude-code')
    expect(block).toMatchObject({ basis: 'published', status: 'priced', currency: 'USD' })
    expect(block.value).toBeCloseTo(12.0, 10)
  })

  it('prices claude-sonnet-5-5 cache read at 0.20 and cache write at 2.50 per 1M', () => {
    expect(pricePublishedTurn(tokens({ cacheRead: 1_000_000 }), 'claude-sonnet-5-5', 'claude-code').value).toBeCloseTo(0.2, 10)
    expect(pricePublishedTurn(tokens({ cacheCreation: 1_000_000 }), 'claude-sonnet-5-5', 'claude-code').value).toBeCloseTo(2.5, 10)
  })

  it('prices gpt-6-luna at 0.10 / 0.50 per 1M with cache read 0.01 at or below 272K measured input', () => {
    const t = tokens({ freshInput: 100_000, cacheRead: 100_000, output: 100_000 })
    const block = pricePublishedTurn(t, 'gpt-6-luna', 'codex')
    expect(block).toMatchObject({ basis: 'published', status: 'priced' })
    expect(block.value).toBeCloseTo(0.01 + 0.001 + 0.05, 10)
  })

  it('selects the >272K tier (0.20 / 0.75, cache read 0.02) by measured input', () => {
    const t = tokens({ freshInput: 200_000, cacheRead: 100_000, output: 100_000 }) // 300K measured
    const block = pricePublishedTurn(t, 'gpt-6-luna', 'codex')
    expect(block.status).toBe('priced')
    expect(block.value).toBeCloseTo(0.04 + 0.002 + 0.075, 10)
  })

  it('treats exactly 272K measured input as the base tier', () => {
    const t = tokens({ freshInput: 272_000 })
    expect(pricePublishedTurn(t, 'gpt-6-luna', 'codex').value).toBeCloseTo(0.0272, 10)
  })
})

describe('pricePublishedTurn: no_rate / not_billed / overrides / aliases', () => {
  const UNLISTED = 'zz-unpublished-model'

  it('an unlisted model is published/no_rate with no figure', () => {
    const block = pricePublishedTurn(MEASURED, UNLISTED, 'claude-code')
    expect(block).toMatchObject({ basis: 'published', status: 'no_rate' })
    expect(block.value).toBeUndefined()
  })

  it('a flatRateModels entry is not_billed, not $0.00', () => {
    setFlatRateModels(['claude-opus-5'])
    const block = pricePublishedTurn(MEASURED, 'claude-opus-5', 'claude-code')
    expect(block).toMatchObject({ basis: 'published', status: 'not_billed' })
    expect(block.value).toBeUndefined()
  })

  it('priceOverrides price an unlisted model, and clearing returns it to no_rate', () => {
    setPriceOverrides({ [UNLISTED]: { input: 3, output: 15 } })
    const t = tokens({ freshInput: 1_000_000, output: 1_000_000 })
    const priced = pricePublishedTurn(t, UNLISTED, 'claude-code')
    expect(priced).toMatchObject({ basis: 'published', status: 'priced' })
    expect(priced.value).toBeCloseTo(18, 10)

    setPriceOverrides({})
    expect(pricePublishedTurn(t, UNLISTED, 'claude-code').status).toBe('no_rate')
  })

  it('modelAliases price an unlisted model as its target', () => {
    setModelAliases({ [UNLISTED]: 'claude-opus-5' })
    const block = pricePublishedTurn(MEASURED, UNLISTED, 'claude-code')
    expect(block.status).toBe('priced')
    expect(block.value).toBeCloseTo(expectedFor('claude-opus-5', MEASURED), 10)
  })
})

describe('pricePublishedTurn: R5.2 harness-reported figures', () => {
  it('passes a harness-basis block through untouched', () => {
    const harness: CostBlock = { basis: 'harness', status: 'priced', value: 1.23, currency: 'USD' }
    const out = pricePublishedTurn(MEASURED, 'claude-opus-5', 'claude-code', harness)
    expect(out).toEqual(harness)
  })

  it('does not reprice a harness block for an out-of-scope harness either', () => {
    const harness: CostBlock = { basis: 'harness', status: 'priced', value: 0.4, currency: 'USD' }
    expect(pricePublishedTurn(MEASURED, 'claude-opus-5', 'copilot', harness)).toEqual(harness)
  })
})

// PR #225 review follow-up (plan 2026-09-30-issue-186-review-fixes, T3 RED).
describe('pricePublishedTurn: absent model (comment 591)', () => {
  it('returns published/no_rate explicitly for an undefined model on an in-scope harness', () => {
    const block = pricePublishedTurn(MEASURED, undefined, 'claude-code')
    expect(block).toEqual({ basis: 'published', status: 'no_rate' })
  })

  it('is out_of_scope for an undefined model on copilot (scope is checked before the model)', () => {
    expect(pricePublishedTurn(MEASURED, undefined, 'copilot')).toEqual({ basis: 'published', status: 'out_of_scope' })
  })

  it('passes a harness block through untouched even with no model', () => {
    const harness: CostBlock = { basis: 'harness', status: 'priced', value: 0.9, currency: 'USD' }
    expect(pricePublishedTurn(MEASURED, undefined, 'claude-code', harness)).toEqual(harness)
  })
})

describe('pricePublishedTurn: cache-write policy (comment 621)', () => {
  it('bills gpt-6-luna cache creation as input ($0.10 per 1M) on the base tier, not a fabricated 1.25x', () => {
    const block = pricePublishedTurn(tokens({ cacheCreation: 100_000 }), 'gpt-6-luna', 'codex')
    expect(block.status).toBe('priced')
    expect(block.value).toBeCloseTo((100_000 * 0.1) / 1e6, 10)
  })

  it('bills gpt-6-luna cache creation as input on the >272K tier ($0.20 per 1M)', () => {
    const block = pricePublishedTurn(tokens({ cacheCreation: 1_000_000 }), 'gpt-6-luna', 'codex')
    expect(block.value).toBeCloseTo(0.2, 10) // 1M measured input is above 272K
  })

  it('leaves an explicit cache-write rate unchanged (claude-sonnet-5-5 still $2.50 per 1M)', () => {
    const block = pricePublishedTurn(tokens({ cacheCreation: 1_000_000 }), 'claude-sonnet-5-5', 'claude-code')
    expect(block.value).toBeCloseTo(2.5, 10)
  })
})

describe('pricePublishedTurn: gpt-6-luna tier measure (comment 625)', () => {
  it('exports the 272K threshold', () => {
    expect(GPT_6_LUNA_PROMPT_TOKEN_THRESHOLD).toBe(272_000)
  })

  it('prices 265,000 fresh + 10,000 cache creation (275,000 measured) on the >272K tier', () => {
    const block = pricePublishedTurn(tokens({ freshInput: 265_000, cacheCreation: 10_000 }), 'gpt-6-luna', 'codex')
    expect(block.value).toBeCloseTo(275_000 * 0.2e-6, 10)
  })

  it('keeps 262,000 fresh + 10,000 cache creation (exactly 272,000 measured) on the base tier', () => {
    const block = pricePublishedTurn(tokens({ freshInput: 262_000, cacheCreation: 10_000 }), 'gpt-6-luna', 'codex')
    expect(block.value).toBeCloseTo(272_000 * 0.1e-6, 10)
  })
})
