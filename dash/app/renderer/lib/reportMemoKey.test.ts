import { describe, expect, it } from 'vitest'

import { reportMemoKey } from './reportMemoKey'

describe('reportMemoKey', () => {
  it('rolls Today at the local calendar boundary', () => {
    const before = new Date(2026, 7, 28, 23, 59, 59)
    const after = new Date(2026, 7, 29, 0, 0, 1)
    expect(reportMemoKey('sessions', 'today', 'all', null, '', before))
      .not.toBe(reportMemoKey('sessions', 'today', 'all', null, '', after))
  })

  it('keeps historical horizons stable across a new day', () => {
    const before = new Date(2026, 7, 28, 23, 59, 59)
    const after = new Date(2026, 7, 29, 0, 0, 1)
    expect(reportMemoKey('sessions', 'week', 'all', null, '', before))
      .toBe(reportMemoKey('sessions', 'week', 'all', null, '', after))
  })

  it('rolls Month at the local month boundary', () => {
    const august = new Date(2026, 7, 31, 23, 59, 59)
    const september = new Date(2026, 8, 1, 0, 0, 1)
    expect(reportMemoKey('plans', 'month', 'all', null, '', august))
      .not.toBe(reportMemoKey('plans', 'month', 'all', null, '', september))
  })
})
