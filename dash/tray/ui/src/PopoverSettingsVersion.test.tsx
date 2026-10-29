// @vitest-environment jsdom

/**
 * The KyberDash version line in Settings, reached the way a user reaches it:
 * the popover mounts, the Settings button opens the view, and the line is read
 * from the DOM.
 *
 * This is the only part of the popover that needs a real DOM, so the jsdom
 * pragma lives here rather than on `Popover.test.tsx`, which renders to static
 * markup under the default node environment.
 */

import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import full from '../../../src/analysis/report/fixtures/full.json' with { type: 'json' }
import type { ContextReport } from '../../../src/analysis/report/types.ts'

import { Popover } from './Popover'
import type { KyberdashVersion, TrayCommands, ViewState } from './viewState'

const NOW = new Date('2026-09-19T12:00:00.000Z')

const NOOP_COMMANDS: TrayCommands = {
  refreshNow: () => {},
  openView: () => {},
  setSettings: () => {},
  quit: () => {},
  cleanDatabase: () => {},
  importFolderHistory: () => {},
  setSharedSettings: () => {},
}

function viewState(overrides: Partial<ViewState> = {}): ViewState {
  return {
    phase: 'ready',
    report: full as unknown as ContextReport,
    reportFetchedAt: '2026-09-19T11:59:00.000Z',
    error: null,
    refresh: { state: 'idle', lastSuccessAt: '2026-09-19T11:00:00.000Z', lastFailure: null },
    receiver: 'reachable',
    jobs: null,
    sharedSettings: null,
    settings: {
      harness: 'all',
      windowDays: 7,
      attentionThreshold: 0.7,
      criticalThreshold: 0.9,
      launchAtLogin: false,
    },
    ...overrides,
  }
}

describe('Popover settings view shows the KyberDash version', () => {
  let container: HTMLDivElement
  let root: Root | undefined

  beforeEach(() => {
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    container = document.createElement('div')
    document.body.appendChild(container)
  })

  afterEach(async () => {
    if (root !== undefined) {
      const mounted = root
      await act(async () => {
        mounted.unmount()
      })
      root = undefined
    }
    container.remove()
  })

  /** Mounts the popover, then opens Settings the way a user does: the Settings button. */
  async function openSettings(state: ViewState): Promise<void> {
    await act(async () => {
      root = createRoot(container)
      root.render(<Popover state={state} commands={NOOP_COMMANDS} now={NOW} />)
    })
    const openButton = container.querySelector<HTMLButtonElement>('[data-testid="open-settings"]')
    expect(openButton, 'the popover has no open-settings button').not.toBeNull()
    await act(async () => {
      openButton!.click()
    })
    expect(container.querySelector('[data-testid="settings-view"]'), 'Settings did not open').not.toBeNull()
  }

  function versionLine(): HTMLElement | null {
    return container.querySelector<HTMLElement>('[data-testid="settings-kyberdash-version"]')
  }

  it('shows the KyberDash version the core reports', async () => {
    await openSettings(
      viewState({ kyberdashVersion: { version: '9.8.7', apiVersion: 1 } }),
    )

    expect(versionLine(), 'settings-kyberdash-version is missing from Settings').not.toBeNull()
    expect(versionLine()!.textContent).toBe('KyberDash version 9.8.7')
  })

  it('says unknown when the core could not read the version (null)', async () => {
    await openSettings(viewState({ kyberdashVersion: null }))

    expect(versionLine(), 'settings-kyberdash-version is missing from Settings').not.toBeNull()
    expect(versionLine()!.textContent).toBe('KyberDash version unknown')
  })

  it('says unknown when the core sends no version key at all', async () => {
    await openSettings(viewState())

    expect(versionLine(), 'settings-kyberdash-version is missing from Settings').not.toBeNull()
    expect(versionLine()!.textContent).toBe('KyberDash version unknown')
  })

  // Malformed shapes: the type forbids these, but the bridge carries the core's
  // JSON verbatim, so the line must not print "undefined" or "null" from them.
  it('says unknown when the version object has no version field', async () => {
    await openSettings(
      viewState({ kyberdashVersion: {} as unknown as KyberdashVersion }),
    )

    expect(versionLine(), 'settings-kyberdash-version is missing from Settings').not.toBeNull()
    expect(versionLine()!.textContent).toBe('KyberDash version unknown')
  })

  it('says unknown when the version object carries a null version', async () => {
    await openSettings(
      viewState({ kyberdashVersion: { version: null, apiVersion: 1 } as unknown as KyberdashVersion }),
    )

    expect(versionLine(), 'settings-kyberdash-version is missing from Settings').not.toBeNull()
    expect(versionLine()!.textContent).toBe('KyberDash version unknown')
  })

  it('says unknown when the version object carries an empty version string', async () => {
    await openSettings(
      viewState({ kyberdashVersion: { version: '', apiVersion: 1 } }),
    )

    expect(versionLine(), 'settings-kyberdash-version is missing from Settings').not.toBeNull()
    expect(versionLine()!.textContent).toBe('KyberDash version unknown')
  })

  it('says unknown when the version object carries a whitespace-only version string', async () => {
    await openSettings(
      viewState({ kyberdashVersion: { version: '   ', apiVersion: 1 } }),
    )

    expect(versionLine(), 'settings-kyberdash-version is missing from Settings').not.toBeNull()
    expect(versionLine()!.textContent).toBe('KyberDash version unknown')
  })

  // The engine's placeholder for "no release version". Shown as a real version it
  // would look like a working install that is in fact unversioned.
  it('says unknown when the version object carries the 0.0.0 placeholder', async () => {
    await openSettings(
      viewState({ kyberdashVersion: { version: '0.0.0', apiVersion: 1 } }),
    )

    expect(versionLine(), 'settings-kyberdash-version is missing from Settings').not.toBeNull()
    expect(versionLine()!.textContent).toBe('KyberDash version unknown')
  })

  it('places the version line just above the Back navigation', async () => {
    await openSettings(
      viewState({ kyberdashVersion: { version: '9.8.7', apiVersion: 1 } }),
    )

    const line = versionLine()
    const back = container.querySelector<HTMLElement>('[data-testid="settings-back"]')
    expect(line, 'settings-kyberdash-version is missing from Settings').not.toBeNull()
    expect(back).not.toBeNull()
    // The line must come before Back in document order.
    expect(line!.compareDocumentPosition(back!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })
})
