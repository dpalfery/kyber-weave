import { ListRow, Panel, UpdateBanner } from 'codeburn-desktop'

/**
 * UpdateBanner takes no props: it reads `useUpdateStatus()`, which calls
 * `getUpdateStatus()` once and then stays live on `onUpdateStatus`. Both are
 * bridge members, so each card feeds them through `window.codeburn.__fixtures`.
 */
const AVAILABLE = { currentVersion: '0.9.23', latestVersion: '0.9.24', updateAvailable: true, tag: 'desktop-v0.9.24' }
const NEXT_PATCH = { currentVersion: '0.9.23', latestVersion: '0.9.25', updateAvailable: true, tag: 'desktop-v0.9.25' }
const MAJOR = { currentVersion: '0.9.23', latestVersion: '1.0.0', updateAvailable: true, tag: 'desktop-v1.0.0' }
const UP_TO_DATE = { currentVersion: '0.9.23', latestVersion: '0.9.23', updateAvailable: false, tag: null }

/** Dismiss is remembered per release tag, so the same version never nags twice. */
const DISMISS_KEY = 'codeburn.updateDismissed'

/** App's content column: the banner sits between the window chrome and the section body. */
function SectionBody() {
  return (
    <Panel title="Spend by project" right="Last 7 days">
      <ListRow no="01" title="kyber-weave" sub="412 sessions" value="$184.20" />
      <ListRow no="02" title="codeburn" sub="288 sessions" value="$121.75" />
      <ListRow no="03" title="agentseal-site" sub="97 sessions" value="$38.40" />
    </Panel>
  )
}

/** The launch check found a newer desktop tag: the nudge sits above the section. */
export function ReleaseAvailable() {
  window.codeburn.__fixtures.getUpdateStatus = AVAILABLE
  window.localStorage.removeItem(DISMISS_KEY)
  return (
    <div className="ct">
      <UpdateBanner />
      <SectionBody />
    </div>
  )
}

/**
 * The same release, already dismissed: the tag in `codeburn.updateDismissed`
 * matches, so the strip is gone and the section body sits flush at the top.
 */
export function DismissedForThisRelease() {
  window.codeburn.__fixtures.getUpdateStatus = AVAILABLE
  window.localStorage.setItem(DISMISS_KEY, AVAILABLE.tag)
  return (
    <div className="ct">
      <UpdateBanner />
      <SectionBody />
    </div>
  )
}

/**
 * A newer release than the dismissed one shows again — dismissal is scoped to
 * the tag, not to the banner.
 */
export function NewerReleaseAfterDismiss() {
  window.codeburn.__fixtures.getUpdateStatus = NEXT_PATCH
  window.localStorage.setItem(DISMISS_KEY, AVAILABLE.tag)
  return (
    <div className="ct">
      <UpdateBanner />
      <SectionBody />
    </div>
  )
}

/**
 * Launch found nothing; the 24h background check pushes a release later in the
 * session. The push arrives on `onUpdateStatus` after the initial read resolves,
 * which is the order the main process actually produces.
 */
export function PushedByBackgroundCheck() {
  window.codeburn.__fixtures.getUpdateStatus = UP_TO_DATE
  window.codeburn.__fixtures.onUpdateStatus = (cb: (s: typeof MAJOR) => void) => { setTimeout(() => cb(MAJOR), 0) }
  window.localStorage.removeItem(DISMISS_KEY)
  return (
    <div className="ct">
      <UpdateBanner />
      <SectionBody />
    </div>
  )
}
