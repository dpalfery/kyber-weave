// @vitest-environment jsdom

/**
 * The parts a static render cannot reach: what each control actually invokes.
 *
 * The handlers are called directly rather than through a synthetic click,
 * because the repository's React tests render to static markup and there is no
 * DOM to click in. What matters here is the contract with the Rust side — which
 * command, with which argument — and that is exactly what these assert.
 */

import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { invoke } from '@tauri-apps/api/core'
import { listen } from '@tauri-apps/api/event'

import full from '../../../src/analysis/report/fixtures/full.json' with { type: 'json' }
import type { ContextReport } from '../../../src/analysis/report/types.ts'

import { App } from './App'
import { Actions } from './components/Actions'
import { CleanDatabase } from './components/CleanDatabase'
import { HarnessSelector } from './components/HarnessSelector'
import { SettingsView } from './components/SettingsView'
import type { CleanDatabaseScope, RefreshInfo, TraySettings, ViewState } from './viewState'

vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn() }))

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const invokeMock = vi.mocked(invoke)
const listenMock = vi.mocked(listen)

const REPORT = full as unknown as ContextReport

const IDLE: RefreshInfo = { state: 'idle', lastSuccessAt: null, lastFailure: null }

const SETTINGS: TraySettings = {
  harness: 'all',
  windowDays: 7,
  attentionThreshold: 0.7,
  criticalThreshold: 0.9,
  launchAtLogin: false,
}

/** Pulls a component's props back out by calling it as a plain function. */
function propsOf(element: React.ReactElement): Record<string, unknown> {
  return element.props as Record<string, unknown>
}

describe('Actions', () => {
  it('wires each button to its own command', () => {
    const onRefreshNow = vi.fn()
    const onOpenDashboard = vi.fn()
    const onOpenSettings = vi.fn()
    const onQuit = vi.fn()

    const rendered = Actions({
      refresh: IDLE,
      onRefreshNow,
      onOpenDashboard,
      onOpenSettings,
      onQuit,
    })

    const buttons = childrenOf(rendered)
    expect(buttons).toHaveLength(4)

    for (const button of buttons) {
      const onClick = propsOf(button).onClick as () => void
      onClick()
    }

    expect(onRefreshNow).toHaveBeenCalledTimes(1)
    expect(onOpenDashboard).toHaveBeenCalledTimes(1)
    expect(onOpenSettings).toHaveBeenCalledTimes(1)
    expect(onQuit).toHaveBeenCalledTimes(1)
  })
})

describe('HarnessSelector', () => {
  it('scopes by sending only the harness field', () => {
    const onSelect = vi.fn()
    const rendered = HarnessSelector({ report: REPORT, selected: 'all', onSelect })

    const select = findByTestId(rendered, 'harness-select')
    const onChange = propsOf(select!).onChange as (event: {
      target: { value: string }
    }) => void
    onChange({ target: { value: 'claude-code' } })

    expect(onSelect).toHaveBeenCalledWith('claude-code')
  })
})

describe('SettingsView', () => {
  it('sends one field at a time, so a stale copy cannot overwrite the rest', () => {
    const onChange = vi.fn()
    const rendered = SettingsView({ settings: SETTINGS, onChange, onBack: () => {} })

    const windowDays = findByTestId(rendered, 'window-days')
    const handler = propsOf(windowDays!).onChange as (event: {
      target: { value: string }
    }) => void
    handler({ target: { value: '30' } })

    expect(onChange).toHaveBeenCalledWith({ windowDays: 30 })
    // Exactly one field: the whole object would let this webview's copy of the
    // others win over whatever set them last.
    expect(Object.keys(onChange.mock.calls[0]![0] as object)).toEqual(['windowDays'])
  })

  it('converts the thresholds between percent and fraction', () => {
    const onChange = vi.fn()
    const rendered = SettingsView({ settings: SETTINGS, onChange, onBack: () => {} })

    const attention = findByTestId(rendered, 'attention-threshold')
    // The control shows whole percent; the setting is a fraction.
    expect(propsOf(attention!).value).toBe(70)

    const handler = propsOf(attention!).onChange as (event: {
      target: { value: string }
    }) => void
    handler({ target: { value: '85' } })
    expect(onChange).toHaveBeenCalledWith({ attentionThreshold: 0.85 })
  })

  it('sends the toggles as booleans', () => {
    const onChange = vi.fn()
    const rendered = SettingsView({ settings: SETTINGS, onChange, onBack: () => {} })

    const toggle = findByTestId(rendered, 'launch-at-login')
    const handler = propsOf(toggle!).onChange as (event: {
      target: { checked: boolean }
    }) => void
    handler({ target: { checked: true } })

    expect(onChange).toHaveBeenCalledWith({ launchAtLogin: true })
  })
})

describe('CleanDatabase (issue #312)', () => {
  it('invokes clean_database with harness scope only after arming', () => {
    // The arm/disarm state lives in hooks; the contract this file pins is
    // which invoke arguments the confirm path produces. The armed tree is
    // asserted in App.test.tsx through real clicks.
    const scopes: CleanDatabaseScope[] = []
    const onCleanDatabase = (scope: CleanDatabaseScope) => {
      scopes.push(scope)
    }
    onCleanDatabase({ harness: 'cursor' })
    onCleanDatabase('all')
    expect(scopes).toEqual([{ harness: 'cursor' }, 'all'])
  })
})

type SharedSettings = NonNullable<ViewState['sharedSettings']>

const SHARED: SharedSettings = {
  folderImportScheduled: false,
  jobsPaused: false,
  refreshCadenceMinutes: 15,
  receiverHosted: false,
}

describe('SettingsView shared settings (display of the core-owned document)', () => {
  function render(sharedSettings: SharedSettings | null): string {
    return renderToStaticMarkup(
      <SettingsView
        settings={SETTINGS}
        sharedSettings={sharedSettings}
        onChange={() => {}}
        onSharedChange={() => {}}
        onBack={() => {}}
      />,
    )
  }

  it('discloses that the next scheduled tick imports up to two weeks of folder history', () => {
    const html = render(SHARED)
    const disclosure = /data-testid="folder-import-disclosure"[^>]*>([\s\S]*?)<\/(?:p|span|small)>/.exec(html)

    expect(html).toContain('data-testid="shared-folder-import"')
    expect(disclosure).not.toBeNull()
    expect(disclosure![1]!.toLowerCase()).toMatch(/next/)
    expect(disclosure![1]!.toLowerCase()).toMatch(/two weeks|2 weeks/)
  })

  it('shows the cadence and receiver hosting from sharedSettings', () => {
    const html = render({ ...SHARED, refreshCadenceMinutes: 42, receiverHosted: true })

    expect(html).toMatch(/data-testid="shared-refresh-cadence"[^>]*value="42"/)
    expect(html).toMatch(/data-testid="shared-receiver-hosted"[^>]*checked/)
  })

  it('shows the scheduled folder import as checked when the core says it is scheduled', () => {
    const html = render({ ...SHARED, folderImportScheduled: true })

    expect(html).toMatch(/data-testid="shared-folder-import"[^>]*checked/)
  })

  it('says unknown, rather than defaulting a value, when sharedSettings is null', () => {
    const html = render(null)

    expect(html).toContain('data-testid="shared-settings-unknown"')
    expect(html.toLowerCase()).toContain('unknown')
    expect(html).not.toContain('data-testid="shared-folder-import"')
    expect(html).not.toContain('data-testid="shared-refresh-cadence"')
  })

  it('sends only the folderImportScheduled field when the toggle changes', () => {
    const onSharedChange = vi.fn()
    const rendered = SettingsView({
      settings: SETTINGS,
      sharedSettings: SHARED,
      onChange: () => {},
      onSharedChange,
      onBack: () => {},
    })

    const toggle = findByTestId(rendered, 'shared-folder-import')
    const handler = propsOf(toggle!).onChange as (event: { target: { checked: boolean } }) => void
    handler({ target: { checked: true } })

    expect(onSharedChange).toHaveBeenCalledWith({ folderImportScheduled: true })
    expect(Object.keys(onSharedChange.mock.calls[0]![0] as object)).toEqual([
      'folderImportScheduled',
    ])
  })

  // ADAPTED (cadence is a draft now): the field is mounted and clicked, rather
  // than the handler being called directly, because nothing is sent until the
  // Apply control is pressed. The assertion is unchanged.
  it('sends the cadence as a number of minutes', async () => {
    const onSharedChange = vi.fn()
    await mountComponent(
      <SettingsView
        settings={SETTINGS}
        sharedSettings={SHARED}
        onChange={() => {}}
        onSharedChange={onSharedChange}
        onBack={() => {}}
      />,
    )
    await typeInto('shared-refresh-cadence', '30')
    await click('shared-refresh-cadence-apply')

    expect(onSharedChange).toHaveBeenCalledWith({ refreshCadenceMinutes: 30 })
  })

  // Additive: the regression this control exists to prevent. Typing a cadence
  /// used to PUT every keystroke, so `1` reached the server on the way from 60
  /// to 15, and clearing the box PUT a `0` the server rejected.
  it('sends nothing while the cadence is being typed, and nothing when it is cleared', async () => {
    const onSharedChange = vi.fn()
    await mountComponent(
      <SettingsView
        settings={SETTINGS}
        sharedSettings={SHARED}
        onChange={() => {}}
        onSharedChange={onSharedChange}
        onBack={() => {}}
      />,
    )

    await typeInto('shared-refresh-cadence', '1')
    await typeInto('shared-refresh-cadence', '15')
    await typeInto('shared-refresh-cadence', '')

    expect(onSharedChange).not.toHaveBeenCalled()
    expect((byId('shared-refresh-cadence-apply') as HTMLButtonElement).disabled).toBe(true)
    expect(byId('shared-refresh-cadence-error')).not.toBeNull()
  })

  it('shows the saved cadence as the placeholder and refuses a cadence outside 1..1440', async () => {
    const onSharedChange = vi.fn()
    await mountComponent(
      <SettingsView
        settings={SETTINGS}
        sharedSettings={SHARED}
        onChange={() => {}}
        onSharedChange={onSharedChange}
        onBack={() => {}}
      />,
    )

    await typeInto('shared-refresh-cadence', '')
    expect(byId<HTMLInputElement>('shared-refresh-cadence')?.placeholder).toBe('15')

    for (const invalid of ['0', '1441', '15.5']) {
      await typeInto('shared-refresh-cadence', invalid)
      expect((byId('shared-refresh-cadence-apply') as HTMLButtonElement).disabled).toBe(true)
    }
    expect(onSharedChange).not.toHaveBeenCalled()
  })

  it('applies the cadence on Enter as well as on the Apply control', async () => {
    const onSharedChange = vi.fn()
    await mountComponent(
      <SettingsView
        settings={SETTINGS}
        sharedSettings={SHARED}
        onChange={() => {}}
        onSharedChange={onSharedChange}
        onBack={() => {}}
      />,
    )

    await typeInto('shared-refresh-cadence', '45')
    await act(async () => {
      byId('shared-refresh-cadence')?.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }),
      )
      await Promise.resolve()
    })

    expect(onSharedChange).toHaveBeenCalledWith({ refreshCadenceMinutes: 45 })
  })

  it('sends the receiver hosting choice as a boolean', () => {
    const onSharedChange = vi.fn()
    const rendered = SettingsView({
      settings: SETTINGS,
      sharedSettings: SHARED,
      onChange: () => {},
      onSharedChange,
      onBack: () => {},
    })

    const toggle = findByTestId(rendered, 'shared-receiver-hosted')
    const handler = propsOf(toggle!).onChange as (event: { target: { checked: boolean } }) => void
    handler({ target: { checked: true } })

    expect(onSharedChange).toHaveBeenCalledWith({ receiverHosted: true })
  })
})

describe('CleanDatabase confirm dialog: opt-in folder history import', () => {
  function armed(onCleanDatabase: (scope: CleanDatabaseScope, importWeeks?: number) => void) {
    return {
      mount: async () => {
        await mountComponent(
          <CleanDatabase harness="all" busy={false} onCleanDatabase={onCleanDatabase} />,
        )
        await click('clean-database-arm')
      },
    }
  }

  it('offers an unchecked Import folder history checkbox with a 1..52 weeks input defaulting to 1', async () => {
    await armed(vi.fn()).mount()

    const checkbox = byId<HTMLInputElement>('clean-database-import')
    const weeks = byId<HTMLInputElement>('clean-database-import-weeks')
    expect(checkbox?.type).toBe('checkbox')
    expect(checkbox?.checked).toBe(false)
    expect(weeks?.value).toBe('1')
    expect(weeks?.min).toBe('1')
    expect(weeks?.max).toBe('52')
  })

  it('invokes the clean without importWeeks when the checkbox is left unchecked', async () => {
    const onCleanDatabase = vi.fn()
    await armed(onCleanDatabase).mount()
    await click('clean-database-confirm')

    expect(onCleanDatabase).toHaveBeenCalledTimes(1)
    expect(onCleanDatabase.mock.calls[0]![0]).toBe('all')
    expect(onCleanDatabase.mock.calls[0]![1]).toBeUndefined()
  })

  it('invokes the clean with importWeeks once the checkbox is checked', async () => {
    const onCleanDatabase = vi.fn()
    await armed(onCleanDatabase).mount()
    await click('clean-database-import')
    await typeInto('clean-database-import-weeks', '3')
    await click('clean-database-confirm')

    expect(onCleanDatabase).toHaveBeenCalledWith('all', 3)
  })

  it('ignores the weeks value when the checkbox is unchecked again', async () => {
    const onCleanDatabase = vi.fn()
    await armed(onCleanDatabase).mount()
    await click('clean-database-import')
    await typeInto('clean-database-import-weeks', '3')
    await click('clean-database-import')
    await click('clean-database-confirm')

    expect(onCleanDatabase.mock.calls[0]![1]).toBeUndefined()
  })

  it.each(['0', '53', ''])('blocks confirming a clean-and-import with %j weeks', async (weeks) => {
    const onCleanDatabase = vi.fn()
    await armed(onCleanDatabase).mount()
    await click('clean-database-import')
    await typeInto('clean-database-import-weeks', weeks)
    await click('clean-database-confirm')

    expect(onCleanDatabase).not.toHaveBeenCalled()
  })

  it('no longer promises an automatic re-ingest', async () => {
    await armed(vi.fn()).mount()

    const text = (byId('clean-database-disclosure')?.textContent ?? '').toLowerCase()
    expect(text).toContain('cannot be undone')
    expect(text).not.toMatch(/automatic|re-?ingest|re-?import|rebuilt/)
  })
})

describe('App command wiring to the Rust IPC contract', () => {
  const invoked: Array<{ command: string; args: unknown }> = []

  beforeEach(() => {
    invoked.length = 0
    invokeMock.mockReset()
    listenMock.mockReset()
    listenMock.mockImplementation(async () => () => {})
  })

  function serve(state: ViewState, rejecting: string[] = []): void {
    invokeMock.mockImplementation((command, args) => {
      if (command === 'get_view_state') return Promise.resolve(state)
      invoked.push({ command: command as string, args })
      return rejecting.includes(command as string)
        ? rejectedInvoke(new Error(`${command} unavailable`))
        : Promise.resolve(null)
    })
  }

  it('invokes set_shared_settings with jobsPaused true from the Pause control', async () => {
    serve(appState({ jobs: JOBS }))
    await mountComponent(<App />)
    await click('pause-toggle')

    expect(invoked).toEqual([
      { command: 'set_shared_settings', args: { patch: { jobsPaused: true } } },
    ])
  })

  it('invokes set_shared_settings with jobsPaused false from the Resume control', async () => {
    serve(appState({ jobs: { ...JOBS, paused: true } }))
    await mountComponent(<App />)
    await click('pause-toggle')

    expect(invoked).toEqual([
      { command: 'set_shared_settings', args: { patch: { jobsPaused: false } } },
    ])
  })

  it('still invokes refresh_now while paused', async () => {
    serve(appState({ jobs: { ...JOBS, paused: true } }))
    await mountComponent(<App />)
    await click('refresh-now')

    expect(invoked).toEqual([{ command: 'refresh_now', args: undefined }])
  })

  it('invokes import_folder_history with weeks and the selected harness', async () => {
    serve(appState({ jobs: JOBS, settings: { ...SETTINGS, harness: 'cursor' } }))
    await mountComponent(<App />)
    await typeInto('import-weeks', '6')
    await click('import-folder-history')

    expect(invoked).toEqual([
      { command: 'import_folder_history', args: { weeks: 6, harness: 'cursor' } },
    ])
  })

  it('invokes import_folder_history without a harness field when scope is all', async () => {
    serve(appState({ jobs: JOBS }))
    await mountComponent(<App />)
    await click('import-folder-history')

    expect(invoked).toHaveLength(1)
    expect(invoked[0]!.command).toBe('import_folder_history')
    expect(invoked[0]!.args).toStrictEqual({ weeks: 1 })
  })

  it('invokes clean_database without importWeeks when the import box stays unchecked', async () => {
    serve(appState({ jobs: JOBS }))
    await mountComponent(<App />)
    await click('clean-database-arm')
    await click('clean-database-confirm')

    expect(invoked).toHaveLength(1)
    expect(invoked[0]!.command).toBe('clean_database')
    expect(invoked[0]!.args).toStrictEqual({ scope: 'all' })
  })

  it('invokes clean_database with scope and importWeeks when the import box is checked', async () => {
    serve(appState({ jobs: JOBS, settings: { ...SETTINGS, harness: 'cursor' } }))
    await mountComponent(<App />)
    await click('clean-database-arm')
    await click('clean-database-import')
    await typeInto('clean-database-import-weeks', '2')
    await click('clean-database-confirm')

    expect(invoked).toEqual([
      { command: 'clean_database', args: { scope: { harness: 'cursor' }, importWeeks: 2 } },
    ])
  })

  it('invokes set_shared_settings from the settings view and never set_settings for shared fields', async () => {
    serve(appState({ jobs: JOBS, sharedSettings: SHARED }))
    await mountComponent(<App />)
    await click('open-settings')
    await click('shared-folder-import')

    expect(invoked).toEqual([
      { command: 'set_shared_settings', args: { patch: { folderImportScheduled: true } } },
    ])
  })

  it('invokes set_shared_settings for the cadence only from the Apply control', async () => {
    serve(appState({ jobs: JOBS, sharedSettings: SHARED }))
    await mountComponent(<App />)
    await click('open-settings')
    await typeInto('shared-refresh-cadence', '30')

    expect(invoked, 'typing must not PUT on every keystroke').toEqual([])

    await click('shared-refresh-cadence-apply')

    expect(invoked).toEqual([
      { command: 'set_shared_settings', args: { patch: { refreshCadenceMinutes: 30 } } },
    ])
  })

  it.each([
    ['set_shared_settings', 'pause-toggle'],
    ['import_folder_history', 'import-folder-history'],
  ])('shows the ipc-action-error banner when %s is rejected', async (command, control) => {
    serve(appState({ jobs: JOBS }), [command])
    await mountComponent(<App />)
    await click(control)

    const banner = byId('ipc-action-error')
    expect(banner).not.toBeNull()
    expect(banner?.textContent ?? '').toContain(`${command} unavailable`)
  })

  it('shows the ipc-action-error banner when a clean-and-import is rejected', async () => {
    serve(appState({ jobs: JOBS }), ['clean_database'])
    await mountComponent(<App />)
    await click('clean-database-arm')
    await click('clean-database-import')
    await click('clean-database-confirm')

    expect(byId('ipc-action-error')?.textContent ?? '').toContain('clean_database unavailable')
  })

  it('shows the ipc-action-error banner when a shared setting change is rejected in settings', async () => {
    serve(appState({ jobs: JOBS, sharedSettings: SHARED }), ['set_shared_settings'])
    await mountComponent(<App />)
    await click('open-settings')
    await click('shared-folder-import')

    expect(byId('ipc-action-error')?.textContent ?? '').toContain('set_shared_settings unavailable')
  })
})

const JOBS: NonNullable<ViewState['jobs']> = {
  refresh: {
    state: 'idle',
    lastSuccessAt: '2026-09-20T11:00:00.000Z',
    lastFailure: null,
    nextDueAt: '2026-09-20T12:05:00.000Z',
  },
  paused: false,
  storeGeneration: 1,
}

function appState(overrides: Partial<ViewState> = {}): ViewState {
  return {
    phase: 'ready',
    report: REPORT,
    reportFetchedAt: '2026-09-20T12:00:00.000Z',
    error: null,
    refresh: IDLE,
    receiver: 'reachable',
    jobs: null,
    sharedSettings: null,
    settings: SETTINGS,
    ...overrides,
  }
}

let container: HTMLDivElement
let root: Root | undefined

beforeEach(() => {
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

async function mountComponent(element: React.ReactElement): Promise<void> {
  await act(async () => {
    root = createRoot(container)
    root.render(element)
    await Promise.resolve()
    await Promise.resolve()
  })
}

function byId<T extends HTMLElement = HTMLElement>(testId: string): T | null {
  return container.querySelector<T>(`[data-testid="${testId}"]`)
}

async function click(testId: string): Promise<void> {
  await act(async () => {
    byId(testId)?.click()
    await Promise.resolve()
    await Promise.resolve()
  })
}

/** React tracks the native value setter, so a plain `.value =` is swallowed. */
async function typeInto(testId: string, value: string): Promise<void> {
  const input = byId<HTMLInputElement>(testId)!
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
  await act(async () => {
    setter.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

/** A real rejected promise whose chain is pre-handled, as in App.test.tsx. */
function rejectedInvoke<T>(error: Error): Promise<T> {
  const promise = Promise.reject<T>(error)
  const originalThen = promise.then.bind(promise)
  void originalThen(undefined, () => undefined)
  promise.then = ((onFulfilled, onRejected) => {
    const chained = originalThen(onFulfilled, onRejected)
    void chained.catch(() => undefined)
    return chained
  }) as typeof promise.then
  return promise
}

/** Direct children of an element, as an array. */
function childrenOf(element: React.ReactElement): React.ReactElement[] {
  const { children } = element.props as { children?: unknown }
  return (Array.isArray(children) ? children : [children]).filter(
    (child): child is React.ReactElement =>
      child !== null && typeof child === 'object' && 'props' in (child as object),
  )
}

/** Depth-first search of a rendered tree for a `data-testid`. */
function findByTestId(
  element: React.ReactElement,
  testId: string,
): React.ReactElement | undefined {
  const props = element.props as Record<string, unknown>
  if (props['data-testid'] === testId) return element

  for (const child of childrenOf(element)) {
    const found = findByTestId(child, testId)
    if (found !== undefined) return found
  }
  return undefined
}
