import type { Provider } from './AgentTabStrip'
import { PROVIDER_LABELS } from './AgentTabStrip'
import type { Period } from './PeriodTabs'
import { PERIOD_PHRASES } from './PeriodTabs'
import { TrayIcon } from './Icons'

type Props = {
  provider: Provider
  period: Period
}

export function EmptyProviderState({ provider, period }: Props) {
  return (
    <div className="empty-provider">
      <TrayIcon size={26} className="empty-provider-icon" />
      <div className="empty-provider-text">No {PROVIDER_LABELS[provider]} data for {PERIOD_PHRASES[period]}</div>
    </div>
  )
}
