// @vitest-environment happy-dom
// Server-sent warning and status-line strings may repeat; React keys must not collide.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render, cleanup } from '@testing-library/react'

import statusMixed from '../../../../src/server/fixtures/capture-api/status-mixed.json'
import type { KyberCaptureHarness } from '../../lib/kyberApi.js'
import { HarnessCollectionList, decideRow } from './HarnessCollectionList.js'

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
