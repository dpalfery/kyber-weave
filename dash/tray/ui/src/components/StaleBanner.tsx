/**
 * Says the figures below are not current (R7.4, R8.11).
 *
 * A banner rather than a replacement: the last document is still the best
 * information available, and hiding it would leave the user with nothing. What
 * it must not do is let the figures read as current, so the banner carries the
 * age and the reason.
 */

type Props = {
  fetchedAt: string | null
  error: string | null
  age: string
}

export function StaleBanner({ fetchedAt, error, age }: Props) {
  return (
    <div className="stale-banner" role="status" data-testid="stale-banner">
      <p className="stale-banner__line">
        Showing data from {age}
        {fetchedAt === null && ' — nothing has been fetched yet'}.
      </p>
      {error !== null && (
        <p className="stale-banner__reason" data-testid="stale-reason">
          {error}
        </p>
      )}
    </div>
  )
}
