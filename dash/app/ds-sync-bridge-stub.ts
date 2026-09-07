// Neutral host-bridge stub for the /design-sync bundle (claude.ai/design only).
//
// `renderer/lib/ipc.ts` captures `window.codeburn` at MODULE SCOPE:
//     export const codeburn: CodeburnBridge = window.codeburn
// In the real app the Electron preload defines that global before the renderer
// bundle evaluates. In Claude Design nothing does, so `codeburn` is permanently
// undefined and every component that touches the bridge (Sidebar, AboutModal,
// Onboarding, TeamTabContent, UpdateBanner) throws on mount — not just in the
// preview cards, but in any design built with them.
//
// This module is imported FIRST by `ds-sync-entry.tsx`, so it runs before
// `lib/ipc.ts` reads the global. It installs a bridge only when one is absent,
// so it is inert anywhere a real preload exists. Every method resolves to
// `null`: components render their genuine empty/default state rather than
// fabricated data. A preview that needs realistic input opts in per card via
// `window.codeburn.__fixtures` (see .design-sync/NOTES.md).
//
// Not part of the app build — nothing under `renderer/` imports this file.

type AnyFn = (...args: unknown[]) => unknown

/** The bridge's only non-method members (CodeburnBridge in renderer/lib/types.ts). */
const SCALARS: Record<string, unknown> = { platform: 'darwin', arch: 'arm64' }

/** Members that take a callback and return an unsubscribe function. */
const SUBSCRIPTIONS = new Set(['onProgress', 'onUpdateStatus'])

function makeBridgeStub(): unknown {
  const fixtures: Record<string, unknown> = {}
  return new Proxy(
    { __fixtures: fixtures },
    {
      get(_target, prop) {
        if (typeof prop !== 'string') return undefined
        if (prop === '__fixtures') return fixtures
        if (prop in SCALARS) return SCALARS[prop]

        if (SUBSCRIPTIONS.has(prop)) {
          return (callback: AnyFn) => {
            const fixture = fixtures[prop]
            // A fixture may push one synchronous event so a subscriber-driven
            // card can show a real state; otherwise the stream stays silent.
            if (typeof fixture === 'function') (fixture as AnyFn)(callback)
            return () => {}
          }
        }

        return (...args: unknown[]) => {
          const fixture = fixtures[prop]
          const value = typeof fixture === 'function' ? (fixture as AnyFn)(...args) : fixture
          return Promise.resolve(value ?? null)
        }
      },
      has: () => true,
    },
  )
}

const host = globalThis as { codeburn?: unknown }
if (!host.codeburn) host.codeburn = makeBridgeStub()
