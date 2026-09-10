/**
 * Keeps a memo-served payload honest while a newly selected period/provider
 * settles. The values remain useful, but they are not presented as the fresh
 * answer for the selection until the request completes.
 */
export function SwitchingBanner() {
  return (
    <div role="status" aria-live="polite" className="stale-banner switching-banner">
      Refreshing selected view…
    </div>
  )
}
