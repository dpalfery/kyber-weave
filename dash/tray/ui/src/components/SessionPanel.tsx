/**
 * The latest session: how full its context is, and what is filling it.
 *
 * The composition bar stacks the five canonical buckets plus the residual
 * (R8.2). The residual is drawn rather than folded into the others, because a
 * large one is the signal that the buckets do not add up to the window — and
 * hiding it would make the bar look complete when it is not.
 */

import type { ContextReport } from '../../../../src/analysis/report/types.ts'
import { BUCKET_LABELS, BUCKET_ORDER, percent, tokens } from '../format'

type Props = {
  report: ContextReport
  onOpenView: (view: string) => void
}

function isMeasured(measure: { value: number | null }): measure is { value: number } {
  return measure.value !== null
}

export function SessionPanel({ report, onOpenView }: Props) {
  // Optional as well as nullable: a report may omit the section entirely.
  const session = report.latestSession
  if (session == null) return null

  const turn = session.latestTurn
  const parts = BUCKET_ORDER.map((key) => ({
    key,
    label: BUCKET_LABELS[key],
    measure: turn.buckets[key],
  }))

  // The bar is drawn from measured buckets only. An unmeasurable one has no
  // width to give it, and inventing a zero would be the `0`-for-absent mistake
  // R14.1 exists to stop; it is still listed below with its reason.
  const drawable = parts.filter((part) => isMeasured(part.measure))
  const residual = turn.residual
  const total =
    drawable.reduce((sum, part) => sum + (part.measure.value as number), 0) +
    (isMeasured(residual) ? residual.value : 0)

  return (
    <section className="session-panel" data-testid="session-panel">
      <header className="session-panel__header">
        <button
          type="button"
          className="session-panel__title"
          onClick={() => onOpenView(session.view)}
          data-testid="open-session"
        >
          {session.project ?? 'no project'} · {session.harness}
        </button>
        <span className="session-panel__turns">turn {turn.index}</span>
      </header>

      <p className="session-panel__pressure" data-testid="session-pressure">
        <span className="session-panel__pressure-value">{percent(turn.pressure)}</span>{' '}
        of {tokens(turn.contextWindow)}
      </p>

      {total > 0 && (
        <div className="composition-bar" data-testid="composition-bar" role="img"
             aria-label="Context composition by bucket">
          {drawable.map((part) => (
            <span
              key={part.key}
              className={`composition-bar__part composition-bar__part--${part.key}`}
              style={{ width: `${(((part.measure.value as number) / total) * 100).toFixed(2)}%` }}
              data-bucket={part.key}
            />
          ))}
          {isMeasured(residual) && residual.value > 0 && (
            <span
              className="composition-bar__part composition-bar__part--residual"
              style={{ width: `${((residual.value / total) * 100).toFixed(2)}%` }}
              data-bucket="residual"
            />
          )}
        </div>
      )}

      <dl className="bucket-list" data-testid="bucket-list">
        {parts.map((part) => (
          <div key={part.key} className="bucket-list__row">
            <dt>{part.label}</dt>
            <dd data-bucket={part.key}>{tokens(part.measure)}</dd>
          </div>
        ))}
        <div className="bucket-list__row bucket-list__row--residual">
          <dt>Residual</dt>
          <dd data-bucket="residual">{tokens(residual)}</dd>
        </div>
      </dl>

      {turn.cacheInvalidation && (
        <p className="cache-notice" data-testid="cache-invalidation-notice">
          Cache invalidated this turn
          {session.cacheInvalidationTurns.length > 0 &&
            ` (also turns ${session.cacheInvalidationTurns.join(', ')})`}
          . The next request re-sends the whole prefix.
        </p>
      )}
    </section>
  )
}
