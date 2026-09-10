import type { Model } from '../lib/payload'
import type { CurrencyState } from '../lib/currency'
import { formatCompactCurrency, formatTokens } from '../lib/currency'
import { CollapsibleSection } from './CollapsibleSection'
import { FixedBar, COL_COST, COL_COUNT } from './ActivitySection'

type Props = {
  models: Model[]
  inputTokens: number
  outputTokens: number
  cacheHitPercent: number
  currency: CurrencyState
}

export function ModelsSection({ models, inputTokens, outputTokens, cacheHitPercent, currency }: Props) {
  if (models.length === 0) return null
  const maxCost = Math.max(...models.map(m => m.cost), 0.01)

  return (
    <CollapsibleSection
      caption="Models"
      columns={[
        { label: 'Cost', width: COL_COST },
        { label: 'Calls', width: COL_COUNT },
      ]}
    >
      {models.map(m => (
        <div key={m.name} className="data-row">
          <FixedBar fraction={m.cost / maxCost} />
          <span className="row-name">{m.name}</span>
          <span className="row-cost" style={{ minWidth: COL_COST }}>{formatCompactCurrency(m.cost, currency)}</span>
          <span className="row-count" style={{ minWidth: COL_COUNT }}>{m.calls}</span>
        </div>
      ))}
      {(inputTokens > 0 || outputTokens > 0) && (
        <div className="tokens-line">
          <span className="tokens-label">Tokens</span>
          <span className="tokens-value">{formatTokens(inputTokens)} in</span>
          <span className="tokens-sep">·</span>
          <span className="tokens-value">{formatTokens(outputTokens)} out</span>
          <span className="tokens-sep">·</span>
          <span className="tokens-value">{Math.round(cacheHitPercent)}% cache hit</span>
        </div>
      )}
    </CollapsibleSection>
  )
}
