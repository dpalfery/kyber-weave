/**
 * The parts a static render cannot reach: what each control actually invokes.
 *
 * The handlers are called directly rather than through a synthetic click,
 * because the repository's React tests render to static markup and there is no
 * DOM to click in. What matters here is the contract with the Rust side — which
 * command, with which argument — and that is exactly what these assert.
 */

import { describe, expect, it, vi } from 'vitest'

import full from '../../../src/analysis/report/fixtures/full.json' with { type: 'json' }
import type { ContextReport } from '../../../src/analysis/report/types.ts'

import { Actions } from './components/Actions'
import { HarnessSelector } from './components/HarnessSelector'
import { SettingsView } from './components/SettingsView'
import type { RefreshInfo, TraySettings } from './viewState'

const REPORT = full as unknown as ContextReport

const IDLE: RefreshInfo = { state: 'idle', lastSuccessAt: null, lastFailure: null }

const SETTINGS: TraySettings = {
  harness: 'all',
  windowDays: 7,
  refreshMinutes: 5,
  attentionThreshold: 0.7,
  criticalThreshold: 0.9,
  launchAtLogin: false,
  hostReceiver: false,
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
