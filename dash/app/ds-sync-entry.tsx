// Design-system entry for /design-sync (claude.ai/design import).
// Re-exports the renderer's presentational components as one module so the
// converter can bundle them without a library build. Splash is omitted: it
// imports a .webm the converter's esbuild loader map cannot handle.
// Regenerate by hand if components are added/removed; see .design-sync/NOTES.md.
// The bridge stub MUST stay the first import: renderer/lib/ipc.ts captures
// window.codeburn at module scope, so this has to run before it evaluates.

import './ds-sync-bridge-stub'

export * from './renderer/components/AboutModal'
export * from './renderer/components/ActivityHeatmap'
export * from './renderer/components/CliErrorPanel'
export * from './renderer/components/ConnectAffordance'
export * from './renderer/components/Dropdown'
export * from './renderer/components/EmptyState'
export * from './renderer/components/ErrorBoundary'
export * from './renderer/components/FlameMark'
export * from './renderer/components/Hint'
export * from './renderer/components/ListRow'
export * from './renderer/components/Onboarding'
export * from './renderer/components/Panel'
export * from './renderer/components/ProviderLogo'
export * from './renderer/components/ProviderPop'
export * from './renderer/components/Punchcard'
export * from './renderer/components/RangeCalendar'
export * from './renderer/components/Sankey'
export * from './renderer/components/SegTabs'
export * from './renderer/components/Sidebar'
export * from './renderer/components/Skeleton'
export * from './renderer/components/StackedBars'
export * from './renderer/components/StaleBanner'
export * from './renderer/components/Stat'
export * from './renderer/components/SwitchingBanner'
export * from './renderer/components/TeamRegistry'
export * from './renderer/components/ToastHost'
export * from './renderer/components/TopBar'
export * from './renderer/components/UpdateBanner'
export * from './renderer/components/Window'
export * from './renderer/lib/toast'
