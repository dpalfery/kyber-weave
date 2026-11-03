// @vitest-environment happy-dom
// Server-sent warning and status-line strings may repeat; React keys must not collide.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render, cleanup } from '@testing-library/react'

import statusMixed from '../../../../src/server/fixtures/capture-api/status-mixed.json'
import type { KyberCaptureHarness } from '../../lib/kyberApi.js'
import { HarnessCollectionList, decideRow } from './HarnessCollectionList.js'
import { screen } from '@testing-library/react'

afterEach(cleanup)

describe('HarnessCollectionList keys', () => {
  it('renders repeated warnings and status lines without duplicate-key warnings', () => {
    const harness = JSON.parse(JSON.stringify(statusMixed.harnesses[0])) as KyberCaptureHarness
    // An older server sends no verdict, and the details then list the raw warnings.
    harness.health = null
    harness.issues = []
    harness.warnings = [
      { code: 'w', text: 'same' },
      { code: 'w', text: 'same' },
    ]
    harness.statusLines = ['line', 'line']
    const error = vi.spyOn(console, 'error').mockImplementation(() => {})
    const { container } = render(
      <HarnessCollectionList harnesses={[harness]} busy={false} alerts={{}} onToggle={() => {}} onReset={() => {}} />,
    )
    const keyWarnings = error.mock.calls.filter((call) => String(call[0]).includes('same key'))
    error.mockRestore()
    expect(keyWarnings).toEqual([])
    expect(container.textContent?.match(/Warning \(w\)/g)).toHaveLength(2)
  })
})

describe('decideRow: the receipt rule applies in every state', () => {
  const row = (overrides: Partial<KyberCaptureHarness>): KyberCaptureHarness =>
    ({
      id: 'synthetic',
      displayName: 'Synthetic',
      mode: 'managed',
      state: 'off',
      installed: 'detected',
      receipt: 'absent',
      ...overrides,
    }) as KyberCaptureHarness

  it('offers no action to a partial ghost the server did not detect', () => {
    // The guard must sit above the `partial` branch: below it, a half-configured ghost was
    // offered Enable and Disable while an `off` harness was not.
    expect(decideRow(row({ state: 'partial', installed: 'not-detected' })).actions).toEqual([])
  })

  it('offers no action to an on harness the server could not inspect', () => {
    expect(decideRow(row({ state: 'on', installed: 'unknown' })).actions).toEqual([])
  })

  it('keeps enable and disable for a partial harness the server did record', () => {
    expect(
      decideRow(row({ state: 'partial', installed: 'not-detected', receipt: 'recorded' })).actions,
    ).toEqual(['enable', 'disable'])
  })

  it('still offers enable to a detected harness that is off', () => {
    expect(decideRow(row({ state: 'off', installed: 'detected' })).actions).toEqual(['enable'])
  })
})

describe('HarnessCollectionList: the issue list', () => {
  const fixtureRow = (id: string): KyberCaptureHarness =>
    JSON.parse(JSON.stringify(statusMixed.harnesses.find((h) => h.id === id))) as KyberCaptureHarness
  const renderRow = (harness: KyberCaptureHarness) =>
    render(<HarnessCollectionList harnesses={[harness]} busy={false} alerts={{}} onToggle={() => {}} onReset={() => {}} />)

  it('lists every issue with severity, text, file path and remedy, and counts them', () => {
    const harness = fixtureRow('copilot')
    renderRow(harness)

    const list = screen.getByTestId('settings-harness-issues-copilot')
    expect(list.textContent).toContain(`${harness.issues.length} issues found`)
    harness.issues.forEach((issue, index) => {
      const item = screen.getByTestId(`settings-harness-issue-copilot-${index}`)
      expect(item.textContent).toContain(issue.severity === 'blocking' ? 'Blocking:' : 'Warning:')
      expect(item.textContent).toContain(issue.text)
      if (issue.path) expect(item.textContent).toContain(issue.path)
      if (issue.remedy) expect(item.textContent).toContain(issue.remedy)
    })
  })

  it('says "1 issue found" for a single issue and shows the row as enabled but needing attention', () => {
    renderRow(fixtureRow('claude-code'))

    expect(screen.getByTestId('settings-harness-issues-claude-code').textContent).toContain('1 issue found')
    const state = screen.getByTestId('settings-harness-state-claude-code')
    expect(state.textContent).toBe('Enabled — needs attention')
    expect(state.className).toContain('text-amber')
  })

  it('shows no issue list for a healthy row', () => {
    const harness = fixtureRow('claude-code')
    harness.health = 'ok'
    harness.issues = []
    renderRow(harness)

    expect(screen.queryByTestId('settings-harness-issues-claude-code')).toBeNull()
    expect(screen.getByTestId('settings-harness-state-claude-code').textContent).toBe('Enabled')
  })

  it('does not repeat the warnings in the details once the verdict lists them as issues', () => {
    const harness = fixtureRow('claude-code')
    expect(harness.warnings.length).toBeGreaterThan(0)
    const { container } = renderRow(harness)

    expect(container.textContent).not.toContain('Warning (endpoint-differs)')
  })
})

describe('HarnessCollectionList: OpenCode plugin and consent lines (T4, D15)', () => {
  it('renders the engine-decided plugin and consent status lines as text, deriving nothing', () => {
    const harness = JSON.parse(JSON.stringify(statusMixed.harnesses[0])) as KyberCaptureHarness
    harness.id = 'opencode'
    harness.statusLines = ['plugin: installed', 'content consent: on (system prompt, tool definitions and skills are captured)']
    render(
      <HarnessCollectionList harnesses={[harness]} busy={false} alerts={{}} onToggle={() => {}} onReset={() => {}} />,
    )
    const lines = screen.getByTestId('settings-harness-status-lines-opencode')
    expect(lines.textContent).toContain('plugin: installed')
    expect(lines.textContent).toContain('content consent: on')
  })
})
