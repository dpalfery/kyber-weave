// `buildContextReport` against a seeded bridge (R11.2-R11.7, R11.11, R8.2-R8.5, R14.2).
//
// The bridge is stubbed rather than a real store opened per case: what is under test here
// is the derivation — scoping, ranking, the shape of an unmeasurable figure, the position
// of cost — not SQLite. A seeded store would make each case slower and would not catch a
// single additional derivation bug.
//
// The rule these cases exist to defend is that the report never invents a number. Wherever
// a figure is absent, the assertion checks for `value: null` with a reason AND explicitly
// that it is not `0`, because `0` is the plausible-looking wrong answer a refactor would
// reach for.

import { describe, expect, it } from 'vitest'

import type { KyberBridge, SessionSummary } from '../../server/bridge.js'
import type { Finding } from '../findings.js'
import type { HarnessRollupRow } from '../../canon/types.js'
import { buildContextReport, costByBasis, latestOf, rankFindings, sessionsInScope } from './build.js'
import { isUnmeasurable, type ReportScope } from './types.js'

const NOW = new Date('2026-09-19T12:00:00.000Z')
const hoursAgo = (n: number): string => new Date(NOW.getTime() - n * 3600_000).toISOString()

function session(overrides: Partial<SessionSummary> = {}): SessionSummary {
  return {
    session_id: 'sess-1',
    harness: 'claude-code',
    label: null,
    is_subagent: false,
    parent_session: null,
    agent_name: null,
    repo: 'kyber-weave',
    branch: null,
    started: hoursAgo(3),
    ended: hoursAgo(2),
    turn_count: 12,
    request_count: 30,
    total_input: 1000,
    total_output: 200,
    cost_usd: null,
    models: [],
    problems: 0,
    ...overrides,
  }
}

function finding(overrides: Partial<Finding> = {}): Finding {
  return {
    id: 'find-1',
    detectorId: 'duplicate-tool-call' as Finding['detectorId'],
    title: 'Duplicate tool call',
    mechanism: 'The same grep ran twice with equivalent arguments.',
    evidenceLinks: [
      { spanId: 'span-a', turnIndex: 1, description: 'first call' },
      { spanId: 'span-b', turnIndex: 2, description: 'second call' },
    ],
    confidence: 'high' as Finding['confidence'],
    estimatedWasteTokens: 400,
    recommendation: 'Move the schema into a skill loaded on demand.',
    errorBar: { lower: 300, upper: 500 },
    outcomeRiskCaveat: 'Relocating may change retrieval latency.',
    sessionId: 'sess-1',
    rankScore: 10,
    measurementClass: 'deterministic',
    confidenceBasis: 'two observed spans',
    ...overrides,
  }
}

function rollup(overrides: Partial<HarnessRollupRow> = {}): HarnessRollupRow {
  return { harness: 'claude-code', sampleCount: 5, measurability: {}, ...overrides }
}

type BridgeStub = Partial<Record<keyof KyberBridge, unknown>>

function bridgeOf(parts: {
  sessions?: SessionSummary[]
  findings?: Finding[]
  rollups?: HarnessRollupRow[]
  content?: unknown
}): KyberBridge {
  const stub: BridgeStub = {
    listSessions: () => parts.sessions ?? [],
    listFindings: () => parts.findings ?? [],
    listHarnessRollups: () => parts.rollups ?? [],
    getQuarantine: () => [],
    getProblems: () => [],
    getSessionContent: () => parts.content,
  }
  return stub as unknown as KyberBridge
}

const build = (bridge: KyberBridge, scope: ReportScope = { days: 7 }, options = {}) =>
  buildContextReport(bridge, scope, { now: NOW, ...options })

describe('scope (R11.11)', () => {
  it('keeps only sessions inside the day window', () => {
    const sessions = [
      session({ session_id: 'inside', ended: hoursAgo(24) }),
      session({ session_id: 'outside', ended: hoursAgo(24 * 9) }),
    ]
    expect(sessionsInScope(sessions, { days: 7 }, NOW).map((s) => s.session_id)).toEqual(['inside'])
  })

  it('treats a session that never ended as active at its start time', () => {
    const live = session({ session_id: 'live', started: hoursAgo(1), ended: null })
    expect(sessionsInScope([live], { days: 7 }, NOW)).toHaveLength(1)
  })

  it('excludes a session with no usable timestamp rather than dating it now', () => {
    const undated = session({ session_id: 'undated', started: null, ended: null })
    expect(sessionsInScope([undated], { days: 7 }, NOW)).toEqual([])
  })

  it('narrows by harness and by session id', () => {
    const sessions = [session({ session_id: 'a' }), session({ session_id: 'b', harness: 'codex' })]
    expect(sessionsInScope(sessions, { days: 7, harness: 'codex' }, NOW).map((s) => s.session_id))
      .toEqual(['b'])
    expect(sessionsInScope(sessions, { days: 7, sessionId: 'a' }, NOW).map((s) => s.session_id))
      .toEqual(['a'])
  })

  it('narrows to a run through the findings that name it', () => {
    const report = build(
      bridgeOf({
        sessions: [session({ session_id: 'in-run' }), session({ session_id: 'not-in-run' })],
        findings: [finding({ sessionId: 'in-run', runId: 'run-7' })],
      }),
      { days: 7, runId: 'run-7' },
    )
    expect(report.latestSession?.sessionId).toBe('in-run')
  })

  it('carries the scope back on the document, so a reader knows what it covers', () => {
    const scope = { days: 3, harness: 'codex' }
    expect(build(bridgeOf({}), scope).scope).toEqual(scope)
  })
})

describe('latest session (R8.2-R8.4, R11.7)', () => {
  const measurableContent = {
    context: {
      measurable: true,
      contextLimit: 200_000,
      flaggedTurns: [2],
      turns: [
        { index: 1, pressure: 0.2, buckets: {}, residual: { tokens: 0 }, toolDefinitionsByServer: {} },
        {
          index: 2,
          pressure: 0.63,
          buckets: {
            system_prompt: 1200,
            tool_definitions: 9000,
            instruction_context: 400,
            conversation_history: 80_000,
            tool_result_content: 35_000,
          },
          residual: { tokens: 512 },
          toolDefinitionsByServer: { 'mcp-github': 6000, 'mcp-fs': 3000 },
        },
      ],
    },
  }

  it('chooses the session with the greatest end time', () => {
    const older = session({ session_id: 'older', ended: hoursAgo(10) })
    const newer = session({ session_id: 'newer', ended: hoursAgo(1) })
    expect(latestOf([older, newer])?.session_id).toBe('newer')
  })

  it('reports the latest turn pressure, all five buckets and the residual', () => {
    const report = build(bridgeOf({ sessions: [session()], content: measurableContent }))
    const turn = report.latestSession!.latestTurn
    expect(turn.index).toBe(2)
    expect(turn.pressure).toEqual({ value: 0.63 })
    expect(turn.contextWindow).toEqual({ value: 200_000, unit: 'tokens' })
    expect(turn.buckets.tool_definitions).toEqual({ value: 9000, unit: 'tokens' })
    expect(turn.residual).toEqual({ value: 512, unit: 'tokens' })
    expect(Object.keys(turn.buckets)).toHaveLength(5)
  })

  it('flags the turn as a cache invalidation when the analysis flagged it (R8.3)', () => {
    const report = build(bridgeOf({ sessions: [session()], content: measurableContent }))
    expect(report.latestSession!.latestTurn.cacheInvalidation).toBe(true)
    expect(report.latestSession!.cacheInvalidationTurns).toEqual([2])
  })

  it('ranks tool-definition sources by resident tokens (R11.7)', () => {
    const report = build(bridgeOf({ sessions: [session()], content: measurableContent }))
    expect(report.latestSession!.toolDefinitionSources.map((s) => s.source))
      .toEqual(['mcp-github', 'mcp-fs'])
  })

  it('renders an unmeasurable session as reasons, never as zeros (R8.4, R14.1)', () => {
    const report = build(
      bridgeOf({ sessions: [session()], content: { context: { measurable: false } } }),
    )
    const turn = report.latestSession!.latestTurn
    for (const figure of [turn.pressure, turn.contextWindow, turn.residual, ...Object.values(turn.buckets)]) {
      expect(isUnmeasurable(figure)).toBe(true)
      expect(figure.value).not.toBe(0)
      expect(isUnmeasurable(figure) && figure.reason.length).toBeGreaterThan(0)
    }
  })

  it('is null when no session is in scope, rather than an empty shell', () => {
    expect(build(bridgeOf({ sessions: [] })).latestSession).toBeNull()
  })
})

describe('findings (R11.5, R14.4)', () => {
  it('orders by rank score descending, breaking ties on id for a stable diff', () => {
    const ranked = rankFindings(
      [
        finding({ id: 'b', rankScore: 5 }),
        finding({ id: 'a', rankScore: 5 }),
        finding({ id: 'c', rankScore: 9 }),
      ],
      5,
    )
    expect(ranked.map((f) => f.id)).toEqual(['c', 'a', 'b'])
  })

  it('honours the limit', () => {
    const many = [1, 2, 3, 4, 5, 6].map((n) => finding({ id: `f-${n}`, rankScore: n }))
    expect(rankFindings(many, 2).map((f) => f.id)).toEqual(['f-6', 'f-5'])
  })

  it('carries every contract field, with the recommendation verbatim (R14.4)', () => {
    const source = finding()
    const [carried] = rankFindings([source], 5)
    expect(carried).toMatchObject({
      id: source.id,
      title: source.title,
      mechanism: source.mechanism,
      measurementClass: 'deterministic',
      confidenceBasis: 'two observed spans',
      recommendation: source.recommendation,
      outcomeRiskCaveat: source.outcomeRiskCaveat,
      errorBar: { low: 300, high: 500 },
      view: 'finding/find-1',
    })
    expect(carried!.evidenceIds).toEqual(['span-a', 'span-b'])
    expect(carried!.evidenceIds.length).toBeGreaterThanOrEqual(2)
  })

  it('reports a missing waste estimate as absent, not as zero recoverable tokens', () => {
    const [carried] = rankFindings(
      [finding({ estimatedWasteTokens: Number.NaN })],
      5,
    )
    expect(isUnmeasurable(carried!.recoverableTokens)).toBe(true)
    expect(carried!.recoverableTokens.value).not.toBe(0)
  })

  it('drops findings whose session is outside the window', () => {
    const report = build(
      bridgeOf({
        sessions: [session({ session_id: 'in' })],
        findings: [finding({ id: 'keep', sessionId: 'in' }), finding({ id: 'drop', sessionId: 'gone' })],
      }),
    )
    expect(report.findings!.map((f) => f.id)).toEqual(['keep'])
  })
})

describe('harness dimensions (R11.6, R14.3)', () => {
  it('reports six separate dimensions and no composite', () => {
    const report = build(bridgeOf({ rollups: [rollup({ contextPressureMedian: 0.4 })] }))
    const dims = report.harnesses![0]!.dimensions
    expect(Object.keys(dims)).toHaveLength(6)
    expect(JSON.stringify(dims).toLowerCase()).not.toContain('"score"')
  })

  it('narrows to the scoped harness', () => {
    const report = build(
      bridgeOf({ rollups: [rollup(), rollup({ harness: 'codex' })] }),
      { days: 7, harness: 'codex' },
    )
    expect(report.harnesses!.map((h) => h.harness)).toEqual(['codex'])
  })
})

describe('cost (R8.9, R14.2, R5.4, R5.5)', () => {
  const priced = (basis: string, value: number) =>
    ({ sessionId: 'sess-1', basis, status: 'priced', value })

  it('sums within a basis', () => {
    expect(costByBasis([priced('harness', 1.5), priced('harness', 2.25)])).toEqual([
      { basis: 'harness', amountUsd: { value: 3.75, unit: 'USD' } },
    ])
  })

  it('keeps bases apart rather than blending them into one total', () => {
    const rows = costByBasis([priced('harness', 2), priced('published', 3)])
    expect(rows).toEqual([
      { basis: 'harness', amountUsd: { value: 2, unit: 'USD' } },
      { basis: 'published', amountUsd: { value: 3, unit: 'USD' } },
    ])
    // The blended figure would have been 5. It must appear nowhere.
    expect(rows.some((r) => r.amountUsd.value === 5)).toBe(false)
  })

  it('reports an unpriced basis with its reason, not a zero total (R5.4, R5.5)', () => {
    const [row] = costByBasis([
      { sessionId: 'sess-1', basis: 'published', status: 'no_rate' },
      { sessionId: 'sess-1', basis: 'published', status: 'not_billed' },
    ])
    expect(isUnmeasurable(row!.amountUsd)).toBe(true)
    expect(row!.amountUsd.value).not.toBe(0)
    expect(isUnmeasurable(row!.amountUsd) && row!.amountUsd.reason).toContain('no_rate')
  })

  it('does not add an unpriced row for a basis that also has priced records', () => {
    const rows = costByBasis([
      priced('harness', 1),
      { sessionId: 'sess-1', basis: 'harness', status: 'no_rate' },
    ])
    expect(rows).toHaveLength(1)
    expect(rows[0]!.amountUsd).toEqual({ value: 1, unit: 'USD' })
  })

  it('reports absence rather than a zero when nothing in scope carried a cost', () => {
    const [row] = costByBasis([])
    expect(isUnmeasurable(row!.amountUsd)).toBe(true)
    expect(row!.amountUsd.value).not.toBe(0)
  })

  it('is the last key in the document, so no surface can lead with it', () => {
    const report = build(bridgeOf({ sessions: [session()] }))
    expect(Object.keys(report).at(-1)).toBe('cost')
  })
})

describe('sections and document shape', () => {
  it('omits detection by default, because it walks the filesystem', () => {
    expect(build(bridgeOf({})).detection).toBeUndefined()
  })

  it('includes detection only when asked, from the injected source', () => {
    const report = build(bridgeOf({}), { days: 7 }, {
      sections: ['detection'],
      detection: () => [{ harness: 'codex', detected: true, probedPaths: ['~/.codex'] }],
    })
    expect(report.detection).toEqual([{ harness: 'codex', detected: true, probedPaths: ['~/.codex'] }])
  })

  it('builds only the sections asked for', () => {
    const report = build(bridgeOf({ sessions: [session()] }), { days: 7 }, { sections: ['findings'] })
    expect(report.findings).toBeDefined()
    expect(report.coverage).toBeUndefined()
    expect(report.cost).toBeUndefined()
  })

  it('carries the schema version and a generation timestamp', () => {
    const report = build(bridgeOf({}))
    expect(report.schemaVersion).toBe(1)
    expect(report.generatedAt).toBe(NOW.toISOString())
  })

  it('hints at the remedy when the window holds no session (R11.4)', () => {
    const report = build(bridgeOf({ sessions: [] }))
    expect(report.coverage!.hints.join(' ')).toContain('kyberdash dash refresh')
  })

  it('survives a bridge whose reads throw, rather than losing the whole report', () => {
    const broken = {
      listSessions: () => { throw new Error('db gone') },
      listFindings: () => { throw new Error('db gone') },
      listHarnessRollups: () => { throw new Error('db gone') },
      getQuarantine: () => { throw new Error('db gone') },
      getProblems: () => { throw new Error('db gone') },
    } as unknown as KyberBridge
    const report = build(broken)
    expect(report.schemaVersion).toBe(1)
    expect(report.findings).toEqual([])
    expect(report.latestSession).toBeNull()
  })
})
