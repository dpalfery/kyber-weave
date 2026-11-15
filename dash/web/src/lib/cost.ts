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
  if (typeof block.value === 'number' && Number.isFinite(block.value) && typeof block.currency === 'string') {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency: block.currency }).format(block.value)
  }
  if (block.status === 'not_billed') return 'not billed'
  if (block.status === 'out_of_scope') return 'out of scope'
  return 'no published rate'
}
