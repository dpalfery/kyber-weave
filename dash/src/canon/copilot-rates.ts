// GitHub Copilot credits table (issue #186, Defect B). Copilot bills per 1M tokens in AI credits
// (1 credit = $0.01) with separate input / cached input / cache write / output classes and an
// input-size tier. A class GitHub lists as "Not applicable" (GPT-6 Luna cache write) carries no rate here and falls back to the input rate. Rates below are the page's USD figures (credits / 100), retrieved 2026-09-30.
// Kept apart from the LiteLLM pricer so a Copilot turn is never priced at API list rates (R5.3).
import { normalizeHarnessName } from './measurability.js'
import { priceWithTable, type Rate, type RateTable } from './cost.js'
import type { CostBlock, TokenUsage } from './types.js'

export const COPILOT_CREDITS_SOURCE = {
  url: 'https://docs.github.com/copilot/reference/copilot-billing/models-and-pricing',
  retrieved: '2026-09-30',
  credit_usd: 0.01,
} as const

const COPILOT_RATES: ReadonlyArray<readonly [string, Rate]> = [
  ['claude-sonnet-5-5', { inputRate: 2, outputRate: 10, cacheReadRate: 0.2, cacheWriteRate: 2.5 }],
  ['claude-opus-5', { inputRate: 5, outputRate: 25, cacheReadRate: 0.5, cacheWriteRate: 6.25 }],
  [
    'gpt-6-luna',
    {
      inputRate: 0.1,
      outputRate: 0.5,
      cacheReadRate: 0.01,
      tiers: [
        { upTo: 272_000, inputRate: 0.1, outputRate: 0.5, cacheReadRate: 0.01 },
        { upTo: Infinity, inputRate: 0.2, outputRate: 0.75, cacheReadRate: 0.02 },
      ],
    },
  ],
]

export const COPILOT_CREDITS_TABLE: RateTable = {
  name: 'github-copilot-credits',
  currency: 'USD',
  applicability: ['copilot'],
  tiers: [],
  publishedRates: new Map(COPILOT_RATES),
}

/** Collapse every Copilot surface (`copilot-cli`, `copilot-vscode`, `github-copilot`, ...) to `copilot`. */
function copilotFamily(harness: string | undefined): string | undefined {
  if (harness === undefined) return undefined
  const id = normalizeHarnessName(harness)
  return id === 'copilot' || id.startsWith('copilot-') ? 'copilot' : id
}

/**
 * Price a Copilot turn from the credits table. A harness-reported figure (R5.2) is returned
 * untouched; any other existing block (for example a LiteLLM-derived `published` figure) is
 * replaced, never kept. Non-Copilot harnesses are `out_of_scope`; an unlisted model is `no_rate`.
 */
export function priceCopilotTurn(
  tokens: TokenUsage,
  model: string | undefined,
  harness: string | undefined,
  existing?: CostBlock,
): CostBlock {
  if (existing?.basis === 'harness') return existing
  return priceWithTable(tokens, model, copilotFamily(harness), COPILOT_CREDITS_TABLE)
}
