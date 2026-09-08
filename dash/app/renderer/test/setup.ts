import '@testing-library/jest-dom/vitest'
import { afterEach } from 'vitest'

// Only wire DOM cleanup in a browser-like (jsdom) environment; node-env tests
// (cli/main) have no document and must not import RTL's DOM cleanup — nor the
// renderer hook graph (usePolled → ipc touches `window` at load).
if (typeof document !== 'undefined') {
  // Node 26+ ships a native `localStorage` accessor on globalThis that returns
  // `undefined` unless `--localstorage-file` is passed. Vitest's jsdom
  // `populateGlobal` skips keys already present on the Node global unless they
  // are in its known KEYS list — `localStorage` is not — so the jsdom Storage
  // never replaces Node's broken getter. Rebind the jsdom Storage here; setup
  // runs after the environment is installed, so `globalThis.jsdom` exists.
  try {
    const domWindow = (globalThis as unknown as { jsdom?: { window?: { localStorage?: Storage; sessionStorage?: Storage } } }).jsdom?.window
    if (domWindow?.localStorage) {
      Object.defineProperty(globalThis, 'localStorage', {
        value: domWindow.localStorage,
        configurable: true,
        writable: true,
      })
    }
    // sessionStorage currently survives (Node only shadows localStorage), but
    // guard it the same way in case a future Node shadows it too.
    if (domWindow?.sessionStorage && typeof (globalThis as unknown as { sessionStorage?: unknown }).sessionStorage === 'undefined') {
      Object.defineProperty(globalThis, 'sessionStorage', {
        value: domWindow.sessionStorage,
        configurable: true,
        writable: true,
      })
    }
  } catch {
    // If jsdom exposes no Storage, tests fall back to their own stubs.
  }
  const { cleanup } = await import('@testing-library/react')
  // The usePolled memo is module-level and persists across renders; clear it
  // between tests so a cached result from one test never seeds another.
  const { __resetPolledMemo } = await import('../hooks/usePolled')
  afterEach(() => { cleanup(); __resetPolledMemo() })
}
