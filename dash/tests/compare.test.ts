// Unit tests for Run and Turn Comparison Workflow (Task G4 / Decision D11).
//
// Acceptance Criteria:
// 1. Phase alignment: Aligns runs by task phase (exploration, implementation,
//    verification, resolution) rather than naive turn index.
// 2. Robustness to mismatched lengths: Mismatched turn counts are aligned by
//    semantic phase boundaries rather than index matching.
// 3. Candidate pairing rule: Auto-pairing is proposed only, requiring n >= 5
//    completed pairs before promoting recommendations.
// 4. Comprehensive test suite in dash/tests/compare.test.ts asserting phase alignment
//    over traces with divergent turn lengths and missing phases.

import { describe, expect, it } from 'vitest'

import {
  alignByPhase,
  compareRuns,
  inferTurnPhase,
  type PhaseAlignedTurnPair,
  type RunTurn,
  type TaskPhase,
} from '../kyber/analysis/compare.js'
import {
  evaluatePairSufficiency,
  pairConfidence,
  proposeRunPairs,
  type CandidateRun,
} from '../kyber/analysis/pairing.js'
import type { OutcomeBlock } from '../kyber/canon/outcome.js'
import type { CanonicalRecord, CostBlock, TokenUsage } from '../kyber/canon/types.js'
import { notMeasurable } from '../kyber/canon/types.js'

let spanCounter = 0

function usage(overrides: Partial<TokenUsage> = {}): TokenUsage {
  const freshInput = overrides.freshInput ?? 800
  const cacheRead = overrides.cacheRead ?? 200
  const cacheCreation = overrides.cacheCreation ?? 0
  const output = overrides.output ?? 150
  return {
    freshInput,
    cacheRead,
    cacheCreation,
    output,
    reportedInput: overrides.reportedInput ?? freshInput + cacheRead + cacheCreation,
    reportedOutput: overrides.reportedOutput ?? output,
  }
}

function makeRecord(harness: string, overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  spanCounter += 1
  return {
    spanId: `span-${spanCounter}`,
    traceId: 'trace-compare-1',
    parentSpanId: null,
    source: `${harness}-source`,
    harness,
    name: `${harness}.call`,
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-09-06T12:00:00Z',
    durationMs: 50,
    status: 'ok',
    tokens: usage(),
    content: {},
    cost: { basis: 'published', status: 'priced', value: 0.02, currency: 'USD' },
    ...overrides,
  }
}

function makeTurn(phase: TaskPhase, overrides: Partial<RunTurn> = {}): RunTurn {
  spanCounter += 1
  return {
    turnIndex: overrides.turnIndex ?? spanCounter,
    spanId: `turn-${spanCounter}`,
    phase,
    tokens: {
      freshInput: 800,
      cacheRead: 200,
      cacheCreation: 0,
      output: 150,
      reportedInput: 1000,
      reportedOutput: 150,
      all: 1150,
    },
    cost: { basis: 'published', status: 'priced', value: 0.02, currency: 'USD' },
    tools: [],
    commands: [],
    ...overrides,
  }
}

function successOutcome(): OutcomeBlock {
  return {
    status: 'success',
    statusReason: 'Clean exit with zero errors and passing tests.',
    termination: { exitCode: 0, availability: 'measured' },
    errors: { count: 0, signals: [], availability: 'measured' },
    userCorrections: { count: 0, turns: [], availability: 'measured' },
    testDeltas: {
      after: { passed: 15, failed: 0, total: 15 },
      delta: { passed: 2, failed: 0 },
      availability: 'measured',
    },
    availability: 'measured',
  }
}

function failureOutcome(): OutcomeBlock {
  return {
    status: 'failure',
    statusReason: 'Test suite failed with 3 errors.',
    termination: { exitCode: 1, availability: 'measured' },
    errors: {
      count: 1,
      signals: [{ source: 'vitest', message: 'Assertion failed in test' }],
      availability: 'measured',
    },
    userCorrections: { count: 1, turns: [], availability: 'measured' },
    testDeltas: {
      after: { passed: 12, failed: 3, total: 15 },
      delta: { passed: 0, failed: 3 },
      availability: 'measured',
    },
    availability: 'measured',
  }
}

// ---------------------------------------------------------------------------
// Phase Alignment Tests (Acceptance Criteria 1 & 2)
// ---------------------------------------------------------------------------

describe('Phase Alignment vs Naive Index Matching (Criterion 1)', () => {
  it('aligns runs by semantic phase rather than turn index', () => {
    // Run A: 3 exploration turns, then 1 implementation turn
    const turnsA: RunTurn[] = [
      makeTurn('exploration', { turnIndex: 0, tools: ['find_by_name'] }),
      makeTurn('exploration', { turnIndex: 1, tools: ['read_file'] }),
      makeTurn('exploration', { turnIndex: 2, tools: ['grep_search'] }),
      makeTurn('implementation', { turnIndex: 3, tools: ['write_to_file'] }),
    ]

    // Run B: 1 exploration turn, then 2 implementation turns
    const turnsB: RunTurn[] = [
      makeTurn('exploration', { turnIndex: 0, tools: ['grep_search'] }),
      makeTurn('implementation', { turnIndex: 1, tools: ['replace_file_content'] }),
      makeTurn('implementation', { turnIndex: 2, tools: ['edit_file'] }),
    ]

    const pairs = alignByPhase(turnsA, turnsB)

    // In naive indexing:
    // Index 1 would compare A[1] (exploration) with B[1] (implementation) — a semantic mismatch!
    // In phase alignment:
    // Exploration phase: 3 pairs (max of 3 and 1)
    // Implementation phase: 2 pairs (max of 1 and 2)
    const explorationPairs = pairs.filter((p) => p.phase === 'exploration')
    const implementationPairs = pairs.filter((p) => p.phase === 'implementation')

    expect(explorationPairs).toHaveLength(3)
    expect(implementationPairs).toHaveLength(2)

    // Pair 0 of exploration: both present
    expect(explorationPairs[0]!.runATurn?.phase).toBe('exploration')
    expect(explorationPairs[0]!.runBTurn?.phase).toBe('exploration')

    // Pair 1 & 2 of exploration: Run B is null because Run B finished exploration in 1 turn
    expect(explorationPairs[1]!.runATurn?.turnIndex).toBe(1)
    expect(explorationPairs[1]!.runBTurn).toBeNull()
    expect(explorationPairs[2]!.runATurn?.turnIndex).toBe(2)
    expect(explorationPairs[2]!.runBTurn).toBeNull()

    // Implementation phase:
    // Pair 0: Run A implementation (turnIndex 3) aligns with Run B implementation (turnIndex 1)
    expect(implementationPairs[0]!.runATurn?.phase).toBe('implementation')
    expect(implementationPairs[0]!.runBTurn?.phase).toBe('implementation')
    expect(implementationPairs[0]!.runATurn?.turnIndex).toBe(3)
    expect(implementationPairs[0]!.runBTurn?.turnIndex).toBe(1)

    // Pair 1: Run A has no 2nd implementation turn, so runATurn is null
    expect(implementationPairs[1]!.runATurn).toBeNull()
    expect(implementationPairs[1]!.runBTurn?.turnIndex).toBe(2)
  })

  it('preserves phase sequence: exploration -> implementation -> verification -> resolution', () => {
    const turnsA: RunTurn[] = [
      makeTurn('exploration', { turnIndex: 0 }),
      makeTurn('implementation', { turnIndex: 1 }),
      makeTurn('verification', { turnIndex: 2 }),
      makeTurn('resolution', { turnIndex: 3 }),
    ]
    const turnsB: RunTurn[] = [
      makeTurn('exploration', { turnIndex: 0 }),
      makeTurn('implementation', { turnIndex: 1 }),
      makeTurn('resolution', { turnIndex: 2 }),
    ]

    const pairs = alignByPhase(turnsA, turnsB)
    const phases = pairs.map((p) => p.phase)

    expect(phases).toEqual(['exploration', 'implementation', 'verification', 'resolution'])
  })
})

// ---------------------------------------------------------------------------
// Robustness to Mismatched Lengths and Missing Phases (Criterion 2 & 4)
// ---------------------------------------------------------------------------

describe('Robustness to Mismatched Lengths and Missing Phases (Criterion 2 & 4)', () => {
  it('correctly handles divergent turn counts in a single phase', () => {
    // Run A spent 5 turns exploring; Run B spent 1 turn
    const turnsA: RunTurn[] = Array.from({ length: 5 }, (_, i) =>
      makeTurn('exploration', { turnIndex: i, tools: ['find_by_name'] })
    )
    const turnsB: RunTurn[] = [makeTurn('exploration', { turnIndex: 0, tools: ['glob'] })]

    const pairs = alignByPhase(turnsA, turnsB)
    expect(pairs).toHaveLength(5)

    expect(pairs[0]!.runATurn).not.toBeNull()
    expect(pairs[0]!.runBTurn).not.toBeNull()

    for (let i = 1; i < 5; i++) {
      expect(pairs[i]!.runATurn).not.toBeNull()
      expect(pairs[i]!.runBTurn).toBeNull()
      expect(pairs[i]!.reading).toContain('completed phase faster')
    }
  })

  it('correctly handles an entirely missing phase in one run', () => {
    // Run A: Exploration and Implementation only (skipped verification)
    const turnsA: RunTurn[] = [
      makeTurn('exploration', { turnIndex: 0 }),
      makeTurn('implementation', { turnIndex: 1 }),
    ]

    // Run B: Exploration, Implementation, and Verification
    const turnsB: RunTurn[] = [
      makeTurn('exploration', { turnIndex: 0 }),
      makeTurn('implementation', { turnIndex: 1 }),
      makeTurn('verification', { turnIndex: 2, tools: ['vitest'] }),
      makeTurn('verification', { turnIndex: 3, tools: ['tsc'] }),
    ]

    const pairs = alignByPhase(turnsA, turnsB)
    const verificationPairs = pairs.filter((p) => p.phase === 'verification')

    expect(verificationPairs).toHaveLength(2)
    for (const vp of verificationPairs) {
      expect(vp.runATurn).toBeNull()
      expect(vp.runBTurn).not.toBeNull()
      expect(vp.reading).toContain('Run A had no corresponding turn')
    }
  })

  it('correctly handles runs where neither run executed a specific phase', () => {
    // Both runs executed exploration and implementation, neither did verification
    const turnsA: RunTurn[] = [makeTurn('exploration', { turnIndex: 0 })]
    const turnsB: RunTurn[] = [makeTurn('exploration', { turnIndex: 0 })]

    const pairs = alignByPhase(turnsA, turnsB)
    const verificationPairs = pairs.filter((p) => p.phase === 'verification')
    const resolutionPairs = pairs.filter((p) => p.phase === 'resolution')

    expect(verificationPairs).toHaveLength(0)
    expect(resolutionPairs).toHaveLength(0)
    expect(pairs).toHaveLength(1)
  })

  it('handles completely disjoint phases across runs', () => {
    // Run A executed only exploration (read-only run)
    const turnsA: RunTurn[] = [makeTurn('exploration', { turnIndex: 0 })]
    // Run B executed only resolution (aborted after commit)
    const turnsB: RunTurn[] = [makeTurn('resolution', { turnIndex: 0 })]

    const pairs = alignByPhase(turnsA, turnsB)
    expect(pairs).toHaveLength(2)

    const expPair = pairs.find((p) => p.phase === 'exploration')!
    expect(expPair.runATurn).not.toBeNull()
    expect(expPair.runBTurn).toBeNull()

    const resPair = pairs.find((p) => p.phase === 'resolution')!
    expect(resPair.runATurn).toBeNull()
    expect(resPair.runBTurn).not.toBeNull()
  })
})

// ---------------------------------------------------------------------------
// Signal Comparison & Unmeasured Telemetry (Criterion 1 & 4)
// ---------------------------------------------------------------------------

describe('Signal Comparison and Telemetry Honesty (Criterion 1 & 4)', () => {
  it('renders unmeasurable signals as not_comparable rather than delta 0', () => {
    // Run A declared cache_read not measurable
    const turnA = makeTurn('exploration', {
      measurability: { cache_read: notMeasurable('pi fixture does not report cache_read.') },
      tokens: { freshInput: 1000, cacheRead: 0, output: 100, all: 1100 },
    })

    // Run B reported 500 cache-read tokens
    const turnB = makeTurn('exploration', {
      tokens: { freshInput: 500, cacheRead: 500, output: 100, all: 1100 },
    })

    const pairs = alignByPhase([turnA], [turnB])
    expect(pairs).toHaveLength(1)

    const cacheSignal = pairs[0]!.signals.find((s) => s.name === 'cache_read')
    expect(cacheSignal).toBeDefined()
    expect(cacheSignal!.status).toBe('not_comparable')
    expect(cacheSignal!.delta).toBeUndefined()
    expect(cacheSignal!.delta).not.toBe(0)
    expect(cacheSignal!.reason).toContain('not measurable in Run A')
  })

  it('computes exact deltas when signals are measurable in both runs', () => {
    const turnA = makeTurn('implementation', {
      tokens: { freshInput: 800, cacheRead: 200, output: 200, all: 1200 },
    })
    const turnB = makeTurn('implementation', {
      tokens: { freshInput: 400, cacheRead: 600, output: 100, all: 1100 },
    })

    const pairs = alignByPhase([turnA], [turnB])
    const totalSignal = pairs[0]!.signals.find((s) => s.name === 'total_tokens')!

    expect(totalSignal.status).toBe('compared')
    expect(totalSignal.runAValue).toBe(1200)
    expect(totalSignal.runBValue).toBe(1100)
    expect(totalSignal.delta).toBe(-100)
  })

  it('refuses cost comparison across conflicting cost bases', () => {
    const turnA = makeTurn('implementation', {
      cost: { basis: 'published', status: 'priced', value: 0.05, currency: 'USD' },
    })
    const turnB = makeTurn('implementation', {
      cost: { basis: 'harness', status: 'priced', value: 0.02, currency: 'USD' },
    })

    const pairs = alignByPhase([turnA], [turnB])
    const costSignal = pairs[0]!.signals.find((s) => s.name === 'cost')!

    expect(costSignal.status).toBe('not_comparable')
    expect(costSignal.delta).toBeUndefined()
    expect(costSignal.reason).toContain('bases or currencies differ')
  })
})

// ---------------------------------------------------------------------------
// Candidate Pairing Heuristics & Sufficiency Threshold (Criterion 3)
// ---------------------------------------------------------------------------

describe('Candidate Pairing Rule & Sufficiency Threshold (Criterion 3)', () => {
  const baseCandidate = (id: string, overrides: Partial<CandidateRun> = {}): CandidateRun => ({
    runId: id,
    harness: 'claude',
    taskFamily: 'auth-migration',
    workingDirectory: '/home/repo/project',
    repo: 'project',
    started: '2026-09-06T10:00:00Z',
    ended: '2026-09-06T10:15:00Z',
    outcome: successOutcome(),
    ...overrides,
  })

  it('proposes run pairs with status "proposed" only', () => {
    const runA = baseCandidate('run-1', { harness: 'claude' })
    const runB = baseCandidate('run-2', { harness: 'copilot' })

    const proposed = proposeRunPairs([runA, runB])
    expect(proposed).toHaveLength(1)

    // Acceptance criterion 3: Auto-pairing is proposed only
    expect(proposed[0]!.status).toBe('proposed')
    expect(proposed[0]!.userConfirmed).toBeUndefined()
  })

  it('refuses to promote recommendations when completed pairs n < 5', () => {
    const runA = baseCandidate('run-1')
    const runB = baseCandidate('run-2', { harness: 'copilot' })

    // Provide n = 3 (< 5) completed historical pairs
    const proposed = proposeRunPairs([runA, runB], { completedPairsCount: 3 })
    expect(proposed[0]!.completedPairCount).toBe(3)
    expect(proposed[0]!.meetsSufficiencyThreshold).toBe(false)
    expect(proposed[0]!.canPromote).toBe(false)
    expect(proposed[0]!.recommendationStatus).toBe('insufficient_history')
    expect(proposed[0]!.verdictMessage).toContain('Minimum threshold is n >= 5 completed pairs')
  })

  it('promotes recommendations when completed pairs n >= 5', () => {
    const runA = baseCandidate('run-1')
    const runB = baseCandidate('run-2', { harness: 'copilot' })

    // Provide n = 5 (threshold met)
    const proposed = proposeRunPairs([runA, runB], { completedPairsCount: 5 })
    expect(proposed[0]!.completedPairCount).toBe(5)
    expect(proposed[0]!.meetsSufficiencyThreshold).toBe(true)
    expect(proposed[0]!.canPromote).toBe(true)
    expect(proposed[0]!.recommendationStatus).toBe('promoted')
    expect(proposed[0]!.verdictMessage).toContain('Sufficiency threshold satisfied')
  })

  it('evaluates pair confidence based on heuristics', () => {
    const runA = baseCandidate('run-1', { harness: 'claude', taskFamily: 'auth-fix' })
    const runB = baseCandidate('run-2', { harness: 'copilot', taskFamily: 'auth-fix' })

    const result = pairConfidence(runA, runB)
    expect(result.confidence).toBeGreaterThan(0.7)
    expect(result.heuristics).toContain('same_task_family')
    expect(result.heuristics).toContain('same_working_directory')
    expect(result.heuristics).toContain('different_harness')
    expect(result.heuristics).toContain('outcome_comparable')
  })

  it('never pairs a run with itself', () => {
    const runA = baseCandidate('run-1')
    const result = pairConfidence(runA, runA)
    expect(result.confidence).toBe(0)
    expect(result.reasons[0]).toContain('Cannot pair run with itself')
  })
})

// ---------------------------------------------------------------------------
// compareRuns Workflow & Outcome Guards (Criterion 3)
// ---------------------------------------------------------------------------

describe('compareRuns Workflow & Outcome Guards (Criterion 3)', () => {
  it('refuses recommendation promotion when outcome regresses even with n >= 5', () => {
    const turnsA = [
      makeTurn('exploration', { turnIndex: 0 }),
      makeTurn('implementation', { turnIndex: 1 }),
    ]
    const turnsB = [
      makeTurn('exploration', { turnIndex: 0 }),
      makeTurn('implementation', { turnIndex: 1 }),
    ]

    const runA = {
      runId: 'run-success',
      harness: 'claude',
      outcome: successOutcome(),
      turns: turnsA,
    }

    const runB = {
      runId: 'run-failure',
      harness: 'copilot',
      outcome: failureOutcome(), // Regressed outcome!
      turns: turnsB,
    }

    // Historical completed pairs n = 10, but outcome regressed in this comparison
    const summary = compareRuns(runA, runB, { completedPairCount: 10 })

    expect(summary.verdict.outcomeRegression).toBe(true)
    expect(summary.verdict.canPromote).toBe(false)
    expect(summary.verdict.status).toBe('outcome_regression')
    expect(summary.verdict.refusalReason).toContain('outcome regression detected')
  })

  it('promotes recommendation when n >= 5 and no outcome regression exists', () => {
    const turnsA = [makeTurn('exploration', { turnIndex: 0 })]
    const turnsB = [makeTurn('exploration', { turnIndex: 0 })]

    const runA = {
      runId: 'run-a',
      harness: 'claude',
      outcome: successOutcome(),
      turns: turnsA,
    }
    const runB = {
      runId: 'run-b',
      harness: 'copilot',
      outcome: successOutcome(),
      turns: turnsB,
    }

    const summary = compareRuns(runA, runB, { completedPairCount: 6 })

    expect(summary.verdict.outcomeRegression).toBe(false)
    expect(summary.verdict.meetsSufficiencyThreshold).toBe(true)
    expect(summary.verdict.canPromote).toBe(true)
    expect(summary.verdict.status).toBe('promoted')
  })

  it('operates transparently over raw CanonicalRecord arrays', () => {
    const recordA = makeRecord('pi', {
      name: 'read_file',
      op: 'tool.invoke',
    })
    const recordB = makeRecord('gemini', {
      name: 'write_to_file',
      op: 'tool.invoke',
    })

    const summary = compareRuns(
      { runId: 'rec-run-a', harness: 'pi', turns: [recordA] },
      { runId: 'rec-run-b', harness: 'gemini', turns: [recordB] }
    )

    expect(summary.pairs).toHaveLength(2)
    // recordA inferred as exploration, recordB inferred as implementation
    const expPair = summary.pairs.find((p) => p.phase === 'exploration')!
    expect(expPair.runATurn).not.toBeNull()
    expect(expPair.runBTurn).toBeNull()

    const impPair = summary.pairs.find((p) => p.phase === 'implementation')!
    expect(impPair.runATurn).toBeNull()
    expect(impPair.runBTurn).not.toBeNull()
  })
})

// ---------------------------------------------------------------------------
// Phase Inference Heuristics
// ---------------------------------------------------------------------------

describe('inferTurnPhase Heuristics', () => {
  it('identifies exploration from read, search, and list tools', () => {
    expect(inferTurnPhase({ tools: ['grep_search'] })).toBe('exploration')
    expect(inferTurnPhase({ tools: ['find_by_name'] })).toBe('exploration')
    expect(inferTurnPhase({ tools: ['list_dir'] })).toBe('exploration')
    expect(inferTurnPhase({ tools: ['read_file'] })).toBe('exploration')
  })

  it('identifies implementation from write, replace, and edit tools', () => {
    expect(inferTurnPhase({ tools: ['write_to_file'] })).toBe('implementation')
    expect(inferTurnPhase({ tools: ['replace_file_content'] })).toBe('implementation')
    expect(inferTurnPhase({ tools: ['edit_file'] })).toBe('implementation')
  })

  it('identifies verification from test and check commands or tools', () => {
    expect(inferTurnPhase({ tools: ['vitest'] })).toBe('verification')
    expect(inferTurnPhase({ tools: ['run_tests'] })).toBe('verification')
    expect(inferTurnPhase({ commands: ['npm test'] })).toBe('verification')
    expect(inferTurnPhase({ commands: ['npx tsc --noEmit'] })).toBe('verification')
  })

  it('identifies resolution from git commit and task completion', () => {
    expect(inferTurnPhase({ tools: ['task_complete'] })).toBe('resolution')
    expect(inferTurnPhase({ tools: ['git_commit'] })).toBe('resolution')
    expect(inferTurnPhase({ commands: ['git commit -m "fix"'] })).toBe('resolution')
  })

  it('respects an explicit phase override', () => {
    expect(inferTurnPhase({ phase: 'resolution', tools: ['grep_search'] })).toBe('resolution')
  })
})
