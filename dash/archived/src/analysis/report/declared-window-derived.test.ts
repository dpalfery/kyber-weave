// P2.R — D5 at the report layer: declared and catalog windows are derived figures,
// never plain measured pressure/window rows that look like telemetry.

import { describe, expect, it } from 'vitest'

import type { KyberBridge, SessionSummary } from '../../server/bridge.js'
import { buildContextReport } from './build.js'
import { isUnmeasurable, type ReportScope } from './types.js'

const NOW = new Date('2026-09-19T12:00:00.000Z')
const hoursAgo = (n: number): string => new Date(NOW.getTime() - n * 3600_000).toISOString()

function session(overrides: Partial<SessionSummary> = {}): SessionSummary {
  return {
    session_id: 'sess-declared',
    harness: 'pi',
    label: null,
    is_subagent: false,
    parent_session: null,
    agent_name: null,
    repo: 'kyber-weave',
    branch: null,
    started: hoursAgo(3),
    ended: hoursAgo(1),
    turn_count: 1,
    request_count: 1,
    total_input: 600_000,
    total_output: 100,
    cost_usd: null,
    cost: { basis: 'unknown', status: 'no_rate' },
    models: [],
    problems: 0,
    ...overrides,
  }
}

function bridgeWithPayload(payload: unknown): KyberBridge {
  return {
    listSessions: () => [session()],
    listFindings: () => [],
    listHarnessRollups: () => [],
    getQuarantineCount: () => 0,
    getProblemCount: () => 0,
    getRefreshState: () => ({ lastSuccessAt: null, lastFailure: null, inProgress: null }),
    getSessionCostContributions: () => [],
    getQuarantine: () => [],
    getProblems: () => [],
    getSessionContent: (sessionId: string) => ({ sessionId, parts: [] }),
    getSessionPayload: () => payload,
  } as unknown as KyberBridge
}

type ReportDerivedFigure = {
  class: 'derived'
  label: string
  value: number
  unit?: string
}

function expectReportDerived(
  figure: unknown,
  label: string,
  value: number,
  unit?: string,
): asserts figure is ReportDerivedFigure {
  expect(figure).toMatchObject({
    class: 'derived',
    label,
    value,
    ...(unit === undefined ? {} : { unit }),
  })
  expect(isUnmeasurable(figure as { value: null; reason: string })).toBe(false)
  expect(figure).not.toEqual({ value })
  expect(figure).not.toEqual(unit === undefined ? { value } : { value, unit })
}

describe('P2.R: declared and catalog windows in latestSession (D5, D15)', () => {
  const scope: ReportScope = { days: 7 }

  it('surfaces declared-window pressure and context window as derived, labelled declared window', () => {
    const payload = {
      summary: { turn_count: 1, total_input: 600_000 },
      context: {
        measurable: true,
        contextLimit: 1_000_000,
        contextLimitSource: 'declared',
        turns: [
          {
            index: 1,
            pressure: 0.6,
            buckets: {},
            residual: { tokens: 0 },
            toolDefinitionsByServer: {},
          },
        ],
      },
    }

    const report = buildContextReport(bridgeWithPayload(payload), scope, { now: NOW })
    const turn = report.latestSession!.latestTurn

    expectReportDerived(turn.pressure, 'declared window', 0.6)
    expectReportDerived(turn.contextWindow, 'declared window', 1_000_000, 'tokens')
  })

  it('accepts catalog provenance as derived vendor catalog, never measured or reported', () => {
    const payload = {
      summary: { turn_count: 1, total_input: 64_000 },
      context: {
        measurable: true,
        contextLimit: 128_000,
        contextLimitSource: 'catalog',
        turns: [
          {
            index: 1,
            pressure: 0.5,
            buckets: {},
            residual: { tokens: 0 },
            toolDefinitionsByServer: {},
          },
        ],
      },
    }

    const report = buildContextReport(bridgeWithPayload(payload), scope, { now: NOW })
    const turn = report.latestSession!.latestTurn

    expectReportDerived(turn.pressure, 'vendor catalog', 0.5)
    expectReportDerived(turn.contextWindow, 'vendor catalog', 128_000, 'tokens')

    const serialized = JSON.stringify(turn)
    expect(serialized).not.toContain('"contextLimitSource":"reported"')
    expect(serialized).not.toContain('"contextLimitSource":"declared"')
    expect(serialized).not.toMatch(/"pressure":\{"value":0\.5\}/)
  })
})
