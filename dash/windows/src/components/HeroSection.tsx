import type { MenubarPayload } from '../lib/payload'
import type { CurrencyState } from '../lib/currency'
import { formatCurrency, plural } from '../lib/currency'
import { prettyDate, todayKey } from '../lib/dates'
import { SectionCaption } from './CollapsibleSection'

type Props = {
  payload: MenubarPayload | null
  currency: CurrencyState
  periodLabel: string
  isToday: boolean
}

export function HeroSection({ payload, currency, periodLabel, isToday }: Props) {
  const todayLabel = prettyDate(todayKey())
  const caption = isToday ? `Today · ${todayLabel}` : (payload?.current.label || periodLabel)

  return (
    <section className="hero">
      <SectionCaption text={caption} />
      <div className="hero-row">
        {payload ? (
          <div className="hero-amount">{formatCurrency(payload.current.cost, currency)}</div>
        ) : (
          <div className="hero-amount hero-skeleton" aria-label="Loading" />
        )}
        <div className="hero-meta">
          {payload ? (
            <>
              <span className="hero-calls">{payload.current.calls.toLocaleString()} {payload.current.calls === 1 ? 'call' : 'calls'}</span>
              <span className="hero-sessions">{plural(payload.current.sessions, 'session')}</span>
            </>
          ) : (
            <>
              <span className="hero-skeleton-line" />
              <span className="hero-skeleton-line short" />
            </>
          )}
        </div>
      </div>
    </section>
  )
}
