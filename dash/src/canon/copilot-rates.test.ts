// Issue #186 Defect B, Copilot (plan docs/archive/plans/2026-09-30-issue-186.md, T7 RED). GitHub Copilot
// bills per 1M tokens in AI credits (1 credit = $0.01), with separate input / cached input /
// cache write / output classes and an input-size tier. Assumed contract for T8, in
// `./copilot-rates.ts`:
//   COPILOT_CREDITS_TABLE: RateTable        applicability ['copilot'], currency 'USD', publishedRates
//                                           in USD per 1M (credits / 100); per-model Rate may carry
//                                           cacheReadRate, cacheWriteRate, tiers[{upTo, ...classes}]
//   COPILOT_CREDITS_SOURCE                  { url, retrieved, credit_usd } provenance
//   priceCopilotTurn(tokens, model, harness, existing?) -> CostBlock
//                                           normalizes copilot-* / github-copilot to 'copilot'
// A Rate tier is chosen by measured input (fresh + cacheRead + cacheCreation): the first tier whose
// inclusive upTo covers it; a tier with upTo Infinity is the open-ended top tier.
import { beforeAll, describe, expect, it } from 'vitest'

import {
  COPILOT_CREDITS_SOURCE,
  COPILOT_CREDITS_TABLE,
  isCopilotHarness,
  priceCopilotTurn,
} from './copilot-rates.js'
import { GPT_6_LUNA_PROMPT_TOKEN_THRESHOLD, loadPricing } from '../pricing/models.js'
import { priceWithTable, type Rate } from './cost.js'
import { pricePublishedTurn } from './published-pricing.js'
import type { CostBlock, TokenUsage } from './types.js'

function tokens(p: Partial<TokenUsage> = {}): TokenUsage {
  const t = { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0, ...p }
  return { ...t, reportedInput: t.freshInput + t.cacheRead + t.cacheCreation, reportedOutput: t.output }
}

const M = 1_000_000
const FOUR_CLASSES = tokens({ freshInput: M, cacheRead: M, cacheCreation: M, output: M })

describe('Copilot credits table: provenance', () => {
  it('is scoped to Copilot and quotes USD', () => {
    expect(COPILOT_CREDITS_TABLE.applicability).toEqual(['copilot'])
    expect(COPILOT_CREDITS_TABLE.currency).toBe('USD')
    expect(COPILOT_CREDITS_TABLE.name).toMatch(/copilot/i)
  })

  it('cites GitHub models-and-pricing, the retrieval date and the credit value', () => {
    expect(COPILOT_CREDITS_SOURCE).toEqual({
      url: 'https://docs.github.com/copilot/reference/copilot-billing/models-and-pricing',
      retrieved: '2026-09-30',
      credit_usd: 0.01,
    })
  })

  it.each(['claude-sonnet-5-5', 'claude-opus-5', 'gpt-6-luna'])('itemizes %s', (model) => {
    expect(COPILOT_CREDITS_TABLE.publishedRates?.has(model)).toBe(true)
  })
})

describe('priceCopilotTurn: per-class arithmetic at the cited GitHub rates', () => {
  it('prices claude-opus-5 input + cache read + cache write + output: 5.00 + 0.50 + 6.25 + 25.00 = $36.75', () => {
    const block = priceCopilotTurn(FOUR_CLASSES, 'claude-opus-5', 'copilot')
    expect(block).toMatchObject({ basis: 'published', status: 'priced', currency: 'USD' })
    expect(block.value).toBeCloseTo(36.75, 10)
    expect(block.byModel?.['claude-opus-5']).toBeCloseTo(36.75, 10)
  })

  it('prices claude-sonnet-5-5: 2.00 + 0.20 + 2.50 + 10.00 = $14.70', () => {
    const block = priceCopilotTurn(FOUR_CLASSES, 'claude-sonnet-5-5', 'copilot')
    expect(block.status).toBe('priced')
    expect(block.value).toBeCloseTo(14.7, 10)
  })

  it('charges cache reads at the cache rate, not the input rate', () => {
    const block = priceCopilotTurn(tokens({ cacheRead: M }), 'claude-opus-5', 'copilot')
    expect(block.value).toBeCloseTo(0.5, 10)
  })

  it('charges cache writes at the cache-write rate, not the input rate', () => {
    const block = priceCopilotTurn(tokens({ cacheCreation: M }), 'claude-opus-5', 'copilot')
    expect(block.value).toBeCloseTo(6.25, 10)
  })

  it('priceWithTable over the table agrees for the normalized harness id', () => {
    const block = priceWithTable(FOUR_CLASSES, 'claude-opus-5', 'copilot', COPILOT_CREDITS_TABLE)
    expect(block.value).toBeCloseTo(36.75, 10)
  })
})

describe('priceCopilotTurn: gpt-6-luna input-size tier (R5.6)', () => {
  it('at or below 272K measured input uses 0.10 / 0.01 / 0.50', () => {
    // 272,000 measured (inclusive bound): 200K fresh + 72K cache read, 1M output.
    const block = priceCopilotTurn(tokens({ freshInput: 200_000, cacheRead: 72_000, output: M }), 'gpt-6-luna', 'copilot')
    expect(block.value).toBeCloseTo((200_000 * 0.1 + 72_000 * 0.01 + M * 0.5) / M, 10)
  })

  it('above 272K measured input uses the long-context tier 0.20 / 0.02 / 0.75', () => {
    // 300K measured: 200K fresh + 100K cache read, 1M output. Cache counts toward tier selection.
    const block = priceCopilotTurn(tokens({ freshInput: 200_000, cacheRead: 100_000, output: M }), 'gpt-6-luna', 'copilot')
    expect(block.value).toBeCloseTo((200_000 * 0.2 + 100_000 * 0.02 + M * 0.75) / M, 10)
  })
})

describe('priceCopilotTurn: gpt-6-luna cache write is "Not applicable" on the cited page', () => {
  // GitHub lists no cache-write rate for GPT-6 Luna; the documented fallback bills cache-creation
  // tokens at the input rate in each tier (corrected from an uncited 0.125 / 0.25).
  it('bills 200K cache-creation tokens (base tier, at or below 272K) at the input rate: 200K * 0.10 / 1M = $0.02', () => {
    const block = priceCopilotTurn(tokens({ cacheCreation: 200_000 }), 'gpt-6-luna', 'copilot')
    expect(block.value).toBeCloseTo((200_000 * 0.1) / M, 10)
  })

  it('bills cache-creation tokens at the long-context input rate above 272K ($0.20)', () => {
    const block = priceCopilotTurn(tokens({ freshInput: 0, cacheCreation: 300_000 }), 'gpt-6-luna', 'copilot')
    expect(block.value).toBeCloseTo((300_000 * 0.2) / M, 10)
  })
})

describe('priceCopilotTurn: R5.3 scoping and the unpriced answers', () => {
  it.each(['copilot', 'copilot-cli', 'copilot-vscode', 'github-copilot'])('prices harness %s', (harness) => {
    expect(priceCopilotTurn(FOUR_CLASSES, 'claude-opus-5', harness).status).toBe('priced')
  })

  it.each(['claude-code', 'claude-cli', 'codex', 'pi', undefined])(
    'the credits table prices no non-Copilot harness (%s): out_of_scope, no figure',
    (harness) => {
      const block = priceCopilotTurn(FOUR_CLASSES, 'claude-opus-5', harness)
      expect(block).toEqual({ basis: 'published', status: 'out_of_scope' })
      expect(block.value).toBeUndefined()
    },
  )

  it('R5.3 regression: claude-code + the same model is priced only by the LiteLLM path, never the credits table', () => {
    expect(priceWithTable(FOUR_CLASSES, 'claude-opus-5', 'claude-code', COPILOT_CREDITS_TABLE)).toEqual({
      basis: 'published',
      status: 'out_of_scope',
    })
  })

  it('Copilot is out_of_scope for the LiteLLM published-table pricer', () => {
    expect(pricePublishedTurn(FOUR_CLASSES, 'claude-opus-5', 'copilot-cli')).toEqual({
      basis: 'published',
      status: 'out_of_scope',
    })
  })

  it('an unlisted model is no_rate, with no figure', () => {
    const block = priceCopilotTurn(FOUR_CLASSES, 'zz-unpublished-model', 'copilot')
    expect(block).toEqual({ basis: 'published', status: 'no_rate' })
  })

  it('a model marked billed:false is not_billed', () => {
    const table = {
      ...COPILOT_CREDITS_TABLE,
      publishedRates: new Map([['gpt-free', { billed: false as const }]]),
    }
    expect(priceWithTable(FOUR_CLASSES, 'gpt-free', 'copilot', table)).toEqual({
      basis: 'published',
      status: 'not_billed',
    })
  })
})

describe('priceCopilotTurn: provenance of an existing block', () => {
  it('a harness-reported Copilot figure wins untouched (R5.2)', () => {
    const existing: CostBlock = { basis: 'harness', status: 'priced', value: 0.42, currency: 'USD' }
    expect(priceCopilotTurn(FOUR_CLASSES, 'claude-opus-5', 'copilot', existing)).toBe(existing)
  })

  it('a LiteLLM-derived figure (published basis) is replaced by the credits price, never kept', () => {
    const litellmDerived: CostBlock = {
      basis: 'published',
      status: 'priced',
      value: 99,
      currency: 'USD',
      byModel: { 'claude-opus-5': 99 },
    }
    const block = priceCopilotTurn(FOUR_CLASSES, 'claude-opus-5', 'copilot', litellmDerived)
    expect(block.basis).toBe('published')
    expect(block.value).toBeCloseTo(36.75, 10)
  })

  it('an unpriced Copilot model with a stale LiteLLM figure becomes no_rate, not the stale figure', () => {
    const stale: CostBlock = { basis: 'published', status: 'priced', value: 1, currency: 'USD' }
    expect(priceCopilotTurn(FOUR_CLASSES, 'zz-unpublished-model', 'copilot', stale)).toEqual({
      basis: 'published',
      status: 'no_rate',
    })
  })
})

// PR #225 review follow-up (plan 2026-09-30-issue-186-review-fixes, T3 RED).
describe('isCopilotHarness (comment 583)', () => {
  it.each(['copilot', 'copilot-cli', 'copilot-vscode'])('is true for %s', (h) => {
    expect(isCopilotHarness(h)).toBe(true)
  })

  it.each(['claude-code', 'codex'])('is false for %s', (h) => {
    expect(isCopilotHarness(h)).toBe(false)
  })

  it('is false for undefined', () => {
    expect(isCopilotHarness(undefined)).toBe(false)
  })
})

describe('Copilot credits table: shape guards (comments 643, 604)', () => {
  const entries = [...(COPILOT_CREDITS_TABLE.publishedRates ?? new Map()).entries()]

  // `Rate` is a union with the unbilled arm `{ billed: false }`; assert the entry is billed so the
  // class and tier fields type-check without weakening any assertion.
  function billedRate(model: string): Extract<Rate, { inputRate: number }> {
    const rate = COPILOT_CREDITS_TABLE.publishedRates!.get(model)!
    expect(rate.billed).not.toBe(false)
    return rate as Extract<Rate, { inputRate: number }>
  }

  it('has entries to check', () => {
    expect(entries.length).toBeGreaterThan(0)
  })

  it.each(entries.map(([model]) => model))('%s sets cacheReadRate and cacheWriteRate on the entry and every tier', (model) => {
    const rate = billedRate(model)
    expect(rate.cacheReadRate).toBeDefined()
    expect(rate.cacheWriteRate).toBeDefined()
    for (const tier of rate.tiers ?? []) {
      expect(tier.cacheReadRate).toBeDefined()
      expect(tier.cacheWriteRate).toBeDefined()
    }
  })

  it.each(entries.map(([model]) => model))('%s ends any tier list in an upTo: Infinity tier', (model) => {
    const tiers = billedRate(model).tiers
    if (!tiers || tiers.length === 0) return
    expect(tiers[tiers.length - 1]!.upTo).toBe(Infinity)
  })

  it('gives gpt-6-luna explicit cache-write rates equal to its input rate (0.10 base, 0.20 long)', () => {
    const tiers = billedRate('gpt-6-luna').tiers!
    expect(tiers[0]!.cacheWriteRate).toBe(0.1)
    expect(tiers[1]!.cacheWriteRate).toBe(0.2)
  })

  it('uses the shared GPT_6_LUNA_PROMPT_TOKEN_THRESHOLD as the first gpt-6-luna tier bound', () => {
    const tiers = billedRate('gpt-6-luna').tiers!
    expect(tiers[0]!.upTo).toBe(GPT_6_LUNA_PROMPT_TOKEN_THRESHOLD)
  })
})

describe('gpt-6-luna: published (LiteLLM) and Copilot credits paths agree (comment 625)', () => {
  beforeAll(async () => {
    await loadPricing()
  })

  const cases: Array<[string, Partial<TokenUsage>]> = [
    ['272,000 fresh', { freshInput: 272_000, output: 10_000 }],
    ['272,001 fresh', { freshInput: 272_001, output: 10_000 }],
    ['262,000 fresh + 10,000 cache creation = 272,000', { freshInput: 262_000, cacheCreation: 10_000, output: 10_000 }],
    ['262,001 fresh + 10,000 cache creation = 272,001', { freshInput: 262_001, cacheCreation: 10_000, output: 10_000 }],
    ['100,000 fresh + 172,001 cache creation = 272,001', { freshInput: 100_000, cacheCreation: 172_001, output: 10_000 }],
    ['cache read + cache creation mix at 272,001', { freshInput: 50_000, cacheRead: 100_000, cacheCreation: 122_001, output: 10_000 }],
  ]

  it.each(cases)('gives the same figure on both paths: %s', (_label, p) => {
    const t = tokens(p)
    const published = pricePublishedTurn(t, 'gpt-6-luna', 'codex')
    const copilot = priceCopilotTurn(t, 'gpt-6-luna', 'copilot')
    expect(published.status).toBe('priced')
    expect(copilot.status).toBe('priced')
    expect(published.value).toBeCloseTo(copilot.value!, 10)
  })

  it('selects the long tier on both paths at 272,001 cache-creation-heavy measured input', () => {
    const t = tokens({ freshInput: 100_000, cacheCreation: 172_001 })
    const expected = 272_001 * 0.2e-6
    expect(pricePublishedTurn(t, 'gpt-6-luna', 'codex').value).toBeCloseTo(expected, 10)
    expect(priceCopilotTurn(t, 'gpt-6-luna', 'copilot').value).toBeCloseTo(expected, 10)
  })
})
