export type CostBasis = 'published' | 'harness' | 'unknown'

export type CostStatus = 'priced' | 'partial' | 'no_rate' | 'out_of_scope' | 'not_billed'

export type CostBlock = {
  basis: CostBasis
  status: CostStatus
  value?: number
  currency?: string
  byModel?: Record<string, number>
}

export function renderCost(block: CostBlock): string {
  if (block.status === 'not_billed') return 'not billed'
  if (block.status === 'out_of_scope') return 'out of scope'
  // Only a fully priced block is money. A partial or unrated block can still carry a value, and
  // showing that value with a currency symbol would claim a price nobody published.
  if (block.status !== 'priced') return 'no published rate'
  if (typeof block.value !== 'number' || !Number.isFinite(block.value)) return 'no published rate'

  const currency = block.currency
  if (typeof currency !== 'string' || currency.length === 0) return 'no published rate'

  try {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(block.value)
  } catch {
    // An unknown ISO code makes Intl throw while rendering; show the amount with its code instead.
    return `${new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 }).format(block.value)} ${currency}`
  }
}
