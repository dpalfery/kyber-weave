// @vitest-environment happy-dom
// Server-sent warning and status-line strings may repeat; React keys must not collide.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render, cleanup } from '@testing-library/react'

import statusMixed from '../../../../src/server/fixtures/capture-api/status-mixed.json'
import type { KyberCaptureHarness } from '../../lib/kyberApi.js'
import { HarnessCollectionList } from './HarnessCollectionList.js'

afterEach(cleanup)

describe('HarnessCollectionList keys', () => {
  it('renders repeated warnings and status lines without duplicate-key warnings', () => {
    const harness = JSON.parse(JSON.stringify(statusMixed.harnesses[0])) as KyberCaptureHarness
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
