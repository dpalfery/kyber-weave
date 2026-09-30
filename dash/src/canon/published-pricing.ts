// Published-rate pricer for API-billed harnesses (issue #186, Defect B). Prices a turn from the
// bundled LiteLLM snapshot under R5.3 applicability scoping. Kept out of canon/cost.ts so the pure
// R5.x engine stays independent of pricing/.
import { calculateCost, getModelCosts, isFlatRateModel } from '../pricing/models.js'
import type { CostBlock, TokenUsage } from './types.js'

/** Harnesses billed per token at the vendor list price, compared lower-cased and trimmed. */
const LITELLM_APPLICABILITY: ReadonlySet<string> = new Set([
  'claude-code',
  'claude-cli',
  'claude-desktop',
  'claude-unclassified',
  'claude',
  'codex',
  'codex-cli',
  'codex-desktop',
  'codex-unclassified',
])

export function isPublishedTableHarness(harness: string | undefined): boolean {
  return harness !== undefined && LITELLM_APPLICABILITY.has(harness.trim().toLowerCase())
}

export function pricePublishedTurn(
  tokens: TokenUsage,
  model: string,
  harness: string | undefined,
  existing?: CostBlock,
): CostBlock {
  // R5.2: a harness-reported figure is never touched.
  if (existing?.basis === 'harness') return existing
  // R5.3: the table prices only the harnesses it names.
  if (!isPublishedTableHarness(harness)) return { basis: 'published', status: 'out_of_scope' }
  // R5.5: an explicitly unbilled model is not $0.00.
  if (isFlatRateModel(model)) return { basis: 'published', status: 'not_billed' }
  // R5.4: no rate, or a stub with nothing billable, is a missing rate.
  const costs = getModelCosts(model)
  if (
    !costs ||
    !(costs.inputCostPerToken > 0 || costs.outputCostPerToken > 0 || costs.cacheReadCostPerToken > 0 || costs.cacheWriteCostPerToken > 0)
  ) {
    return { basis: 'published', status: 'no_rate' }
  }
  const value = calculateCost(model, tokens.freshInput, tokens.output, tokens.cacheCreation, tokens.cacheRead, 0)
  return { basis: 'published', status: 'priced', value, currency: 'USD', byModel: { [model]: value } }
}
