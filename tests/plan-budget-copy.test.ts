import { describe, expect, it } from 'vitest'

import { planBudgetHeadline, planStatusText } from '../src/dashboard.js'
import type { PlanUsage } from '../src/plan-usage.js'

function usage(overrides: Partial<PlanUsage> = {}): PlanUsage {
  return {
    plan: {
      id: 'supergrok-heavy',
      monthlyUsd: 300,
      provider: 'grok',
      resetDay: 1,
      setAt: '2026-08-01T00:00:00.000Z',
    },
    periodStart: new Date('2026-08-01T00:00:00'),
    periodEnd: new Date('2026-09-01T00:00:00'),
    spentApiEquivalentUsd: 33.82,
    budgetUsd: 300,
    percentUsed: 11.273,
    status: 'under',
    projectedMonthUsd: 40,
    daysUntilReset: 13,
    ...overrides,
  }
}

// The TUI truncates both lines end-first at the terminal width, so the row is
// headline + two spaces + the 10-cell bar + a space + the percentage.
const PLAN_ROW_TAIL = '  ' + '#'.repeat(10) + ' '
function planRow(planUsage: PlanUsage): string {
  return planBudgetHeadline(planUsage) + PLAN_ROW_TAIL + planUsage.percentUsed.toFixed(1) + '%'
}

describe('plan budget copy', () => {
  it('labels SuperGrok as a budget, not a live window', () => {
    expect(planBudgetHeadline(usage())).toBe('SuperGrok Heavy: $33.82 API-equivalent / $300.00 budget')
    const status = planStatusText(usage())
    expect(status).toBe('Well within budget. Not a live provider window. Projected: $40.00. Next budget reset in 13 days.')
  })

  it('uses the same budget language for every preset, not only Grok', () => {
    const cursor = usage({
      plan: {
        id: 'cursor-pro',
        monthlyUsd: 20,
        provider: 'cursor',
        resetDay: 1,
        setAt: '2026-08-01T00:00:00.000Z',
      },
      budgetUsd: 20,
      spentApiEquivalentUsd: 8.2,
      status: 'near',
    })
    expect(planBudgetHeadline(cursor)).toBe('Cursor Pro: $8.20 API-equivalent / $20.00 budget')
    expect(planStatusText(cursor)).toContain('Approaching budget')
  })

  it('labels Copilot as AI Credits, not API-equivalent USD', () => {
    const copilot = usage({
      plan: {
        id: 'copilot-pro',
        monthlyCredits: 1500,
        monthlyUsd: 15,
        provider: 'copilot',
        resetDay: 1,
        setAt: '2026-08-01T00:00:00.000Z',
      },
      budgetUsd: 15,
      spentApiEquivalentUsd: 0.015,
      spentCredits: 1.5,
      budgetCredits: 1500,
      percentUsed: 0.1,
      creditsIncomplete: false,
    })
    expect(planBudgetHeadline(copilot)).toBe('Copilot Pro: 1.5 / 1500 AI Credits')
    const status = planStatusText(copilot)
    expect(status).toContain('Not a live provider window.')
    expect(status).not.toMatch(/GitHub|UTC|copilot_internal/i)
    expect(status).not.toMatch(/calendar/i)
  })

  // computePeriodFromResetDay builds an anniversary window from plan.resetDay
  // (1-28, settable with `codeburn plan set --reset-day`), so a plan that resets
  // on the 15th is NOT on a calendar month and must never be called one.
  it('never calls an anniversary reset window a calendar month', () => {
    for (const resetDay of [1, 15, 28]) {
      const status = planStatusText(usage({
        plan: { id: 'supergrok-heavy', monthlyUsd: 300, provider: 'grok', resetDay, setAt: '2026-08-01T00:00:00.000Z' },
        daysUntilReset: 4,
      }))
      expect(status).not.toMatch(/calendar/i)
      expect(status).toContain('Next budget reset in 4 days.')
    }
  })

  // The row and the status each get one truncate-end line. At 80 columns the
  // percentage must survive on the row and the projection on the status line.
  it('keeps the percentage and the projection readable at 80 columns', () => {
    for (const planUsage of [
      usage(),
      usage({ status: 'over', spentApiEquivalentUsd: 640, percentUsed: 213.3 }),
      usage({ plan: { id: 'custom', monthlyUsd: 300, provider: 'openrouter', resetDay: 1, setAt: '2026-08-01T00:00:00.000Z' } }),
    ]) {
      const row = planRow(planUsage)
      expect(row.length).toBeLessThanOrEqual(80)
      expect(row.slice(0, 80)).toContain(`${planUsage.percentUsed.toFixed(1)}%`)
      expect(planStatusText(planUsage).slice(0, 80)).toContain('Projected: $40.00.')
    }
  })
})
