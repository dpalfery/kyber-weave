import { Sidebar } from 'codeburn-desktop'

/**
 * `.sb` is `flex: 0 0 186px`, so the rail only takes its real width inside a
 * flex row — bare, it would stretch to whatever it is dropped into. This frame
 * is the window's canvas ground cropped to the rail, so the white nav column
 * and its right-hand rule read the way they do in the app.
 */
function Rail({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', width: 203, minHeight: 440, background: 'var(--canvas)', color: 'var(--ink)', fontSize: 13 }}>
      {children}
    </div>
  )
}

/**
 * The boot destination. The active item takes `.ni.on` — accent rule on the
 * left edge, tinted ground, and its glyph switches to the accent stroke.
 * Every item carries its own platform-rendered shortcut keycap.
 */
export function OverviewSelected() {
  return (
    <Rail>
      <Sidebar active="overview" onNavigate={() => {}} />
    </Rail>
  )
}

/**
 * A mid-list destination. The `push` spacer keeps the About/social footer
 * pinned to the bottom no matter which of the ten items is active.
 */
export function SpendSelected() {
  return (
    <Rail>
      <Sidebar active="spend" onNavigate={() => {}} />
    </Rail>
  )
}

/**
 * Settings sits below the eight numbered destinations and takes a punctuation
 * shortcut, so its keycap is `⌘,` rather than a digit.
 */
export function SettingsSelected() {
  return (
    <Rail>
      <Sidebar active="settings" onNavigate={() => {}} />
    </Rail>
  )
}

/**
 * The last item. Selecting it shows the highlight against the footer rule —
 * the tightest spacing the rail has to hold.
 */
export function PluginsSelected() {
  return (
    <Rail>
      <Sidebar active="plugins" onNavigate={() => {}} />
    </Rail>
  )
}
