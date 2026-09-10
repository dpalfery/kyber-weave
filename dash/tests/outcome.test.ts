import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import {
  calculateOutcomePopulationRate,
  deriveOutcome,
  detectUserCorrection,
  outcomeAvailability,
  USER_CORRECTION_RULES,
} from '../kyber/canon/outcome.js'
import { buildRuns, deriveRunIdentity } from '../kyber/canon/runs.js'
import { CanonStore } from '../kyber/canon/store.js'
import type { CanonicalRecord, TokenUsage } from '../kyber/canon/types.js'
import { ClaudeContentReader } from '../kyber/synth/readers/claude.js'
import { codexReader } from '../kyber/synth/readers/codex.js'
import { piReader } from '../kyber/synth/readers/pi.js'

const tempDirs: string[] = []

function tempStorePath(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-outcome-test-'))
  tempDirs.push(dir)
  return join(dir, 'canon.db')
}

afterEach(() => {
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()!
    rmSync(dir, { recursive: true, force: true })
  }
})

function defaultTokens(): TokenUsage {
  return {
    freshInput: 400,
    cacheRead: 100,
    cacheCreation: 0,
    output: 50,
    reportedInput: 500,
    reportedOutput: 50,
  }
}

function makeRecord(spanId: string, over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-1',
    parentSpanId: null,
    source: 'test-source',
    harness: 'claude-code',
    sessionId: 'sess-1',
    name: 'llm_invoke',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-09-05T10:00:00.000Z',
    durationMs: 200,
    status: 'ok',
    tokens: defaultTokens(),
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    ...over,
  }
}

describe('OutcomeBlock & Decision D7/D8 — Guard signal and measurability', () => {
  it('returns not_measurable status when no outcome signals exist', () => {
    // Normal model invocation spans with no exit code, no test output, and no completion marker
    const records = [
      makeRecord('span-1', { timestamp: '2026-09-05T10:00:00.000Z' }),
      makeRecord('span-2', { timestamp: '2026-09-05T10:00:05.000Z' }),
    ]

    const outcome = deriveOutcome(records)

    // D7/D8 rule: an unobserved outcome must NEVER be guessed as 'success' or 'completed'
    expect(outcome.status).toBe('not_measurable')
    expect(outcome.availability).toEqual({
      availability: 'not_measurable',
      reason: 'Outcome was not observable for this run.',
    })
    expect(outcome.termination.availability).toEqual({
      availability: 'not_measurable',
      reason: 'No termination or exit indicator was emitted by the harness.',
    })
    expect(outcome.testDeltas.availability).toEqual({
      availability: 'not_measurable',
      reason: 'No test execution results observed in records or tool results.',
    })
  })

  it('handles empty record arrays as not_measurable', () => {
    const outcome = deriveOutcome([])

    expect(outcome.status).toBe('not_measurable')
    expect(outcome.availability).toEqual({
      availability: 'not_measurable',
      reason: 'Outcome was not observable for empty record set.',
    })
  })

  it('outcomeAvailability helper inspects target availability accurately', () => {
    expect(outcomeAvailability(null)).toEqual({
      availability: 'not_measurable',
      reason: 'Outcome availability unknown: target was not provided.',
    })

    const unmeasuredRecords = [makeRecord('span-1')]
    expect(outcomeAvailability(unmeasuredRecords)).toEqual({
      availability: 'not_measurable',
      reason: 'Outcome was not observable for this run.',
    })

    const measuredRecords = [
      makeRecord('span-1', {
        raw: { exitCode: 0 },
      }),
    ]
    expect(outcomeAvailability(measuredRecords)).toBe('measured')
  })
})

describe('Successful Exits', () => {
  it('identifies clean process exit code 0 as success', () => {
    const records = [
      makeRecord('span-1', {
        raw: { exitCode: 0 },
      }),
    ]

    const outcome = deriveOutcome(records)

    expect(outcome.status).toBe('success')
    expect(outcome.availability).toBe('measured')
    expect(outcome.termination.exitCode).toBe(0)
    expect(outcome.termination.availability).toBe('measured')
    expect(outcome.errors.count).toBe(0)
    expect(outcome.userCorrections.count).toBe(0)
  })

  it('identifies Codex task_complete event as success', () => {
    const records = [
      makeRecord('span-1', {
        raw: { type: 'task_complete' },
      }),
    ]

    const outcome = deriveOutcome(records)

    expect(outcome.status).toBe('success')
    expect(outcome.termination.reason).toBe('task_complete')
    expect(outcome.termination.exitCode).toBe(0)
    expect(outcome.termination.availability).toBe('measured')
  })

  it('identifies passing test suite results as success', () => {
    const records = [
      makeRecord('span-1', {
        content: {
          tool_result_content: 'Tests: 12 passed, 12 total\nTime: 1.23s',
        },
      }),
    ]

    const outcome = deriveOutcome(records)

    expect(outcome.status).toBe('success')
    expect(outcome.testDeltas.availability).toBe('measured')
    expect(outcome.testDeltas.after?.passed).toBe(12)
    expect(outcome.testDeltas.after?.failed).toBe(0)
  })

  it('computes test deltas between before and after test runs', () => {
    const records = [
      makeRecord('span-1', {
        content: {
          tool_result_content: 'Tests: 2 failed, 10 passed, 12 total',
        },
      }),
      makeRecord('span-2', {
        content: {
          tool_result_content: 'Tests: 0 failed, 12 passed, 12 total',
        },
      }),
    ]

    const outcome = deriveOutcome(records)

    expect(outcome.status).toBe('success')
    expect(outcome.testDeltas.before).toEqual({ passed: 10, failed: 2, total: 12 })
    expect(outcome.testDeltas.after).toEqual({ passed: 12, failed: 0, total: 12 })
    expect(outcome.testDeltas.delta).toEqual({ passed: 2, failed: -2 })
  })
})

describe('Error Exits & Failures', () => {
  it('identifies non-zero exit code as failure', () => {
    const records = [
      makeRecord('span-1', {
        raw: { exitCode: 1 },
      }),
    ]

    const outcome = deriveOutcome(records)

    expect(outcome.status).toBe('failure')
    expect(outcome.availability).toBe('measured')
    expect(outcome.termination.exitCode).toBe(1)
    expect(outcome.statusReason).toContain('non-zero exit code 1')
  })

  it('identifies terminal span error status as failure', () => {
    const records = [
      makeRecord('span-1', { status: 'ok' }),
      makeRecord('span-2', { status: 'error', name: 'RateLimitError' }),
    ]

    const outcome = deriveOutcome(records)

    expect(outcome.status).toBe('failure')
    expect(outcome.errors.count).toBe(1)
    expect(outcome.errors.signals[0]?.code).toBe('error')
    expect(outcome.errors.signals[0]?.message).toBe('RateLimitError')
  })

  it('identifies failing test suite delta as failure', () => {
    const records = [
      makeRecord('span-1', {
        content: {
          tool_result_content: 'Tests: 3 failed, 9 passed, 12 total',
        },
      }),
    ]

    const outcome = deriveOutcome(records)

    expect(outcome.status).toBe('failure')
    expect(outcome.testDeltas.after?.failed).toBe(3)
    expect(outcome.statusReason).toContain('3 failing test(s)')
  })

  it('identifies abandoned or user-cancelled runs', () => {
    const records = [
      makeRecord('span-1', {
        raw: { terminationReason: 'user_cancelled' },
      }),
    ]

    const outcome = deriveOutcome(records)

    expect(outcome.status).toBe('abandoned')
    expect(outcome.availability).toBe('measured')
    expect(outcome.statusReason).toContain('cancelled or aborted')
  })
})

describe('User Correction Turns — Deterministic Rules', () => {
  it('evaluates USER_CORRECTION_RULES against explicit test vectors', () => {
    expect(USER_CORRECTION_RULES.length).toBeGreaterThanOrEqual(2)

    // Prefix negation rule
    expect(detectUserCorrection('No, that is not right.').matched).toBe(true)
    expect(detectUserCorrection('No, that is not right.').rule).toBe('prefix_negation')
    expect(detectUserCorrection('Wait, stop here.').matched).toBe(true)
    expect(detectUserCorrection('Wait, stop here.').rule).toBe('prefix_negation')
    expect(detectUserCorrection("Don't do that, revert it.").matched).toBe(true)
    expect(detectUserCorrection('Actually, change the color to blue.').matched).toBe(true)

    // Mistake assertion rule
    expect(detectUserCorrection("That's wrong, you missed the type definition.").matched).toBe(true)
    expect(detectUserCorrection("That's wrong, you missed the type definition.").rule).toBe('mistake_assertion')
    expect(detectUserCorrection("That didn't work, Vitest exited with code 1.").matched).toBe(true)
    expect(detectUserCorrection('You made a mistake in the SQL join.').matched).toBe(true)
    expect(detectUserCorrection('Fix the error in the parser.').matched).toBe(true)
    expect(detectUserCorrection('Undo that change.').matched).toBe(true)

    // Non-corrections
    expect(detectUserCorrection('Please implement the auth flow.').matched).toBe(false)
    expect(detectUserCorrection('Looks good! Now please write documentation.').matched).toBe(false)
    expect(detectUserCorrection('').matched).toBe(false)
    expect(detectUserCorrection(undefined).matched).toBe(false)
  })

  it('detects user corrections in conversation history and classifies exit as inconclusive if exit code was 0', () => {
    const records = [
      makeRecord('span-1', {
        content: {
          conversation_history: "No, that's wrong. You broke the parser, fix the error.",
        },
      }),
      makeRecord('span-2', {
        raw: { exitCode: 0 },
      }),
    ]

    const outcome = deriveOutcome(records)

    expect(outcome.userCorrections.count).toBe(1)
    expect(outcome.userCorrections.turns[0]?.rule).toBe('prefix_negation')
    expect(outcome.userCorrections.turns[0]?.excerpt).toContain("No, that's wrong")
    // Clean exit code but required user corrections -> inconclusive
    expect(outcome.status).toBe('inconclusive')
    expect(outcome.statusReason).toContain('required 1 user correction turn(s)')
  })
})

describe('Readers Integration — Transcript indicators', () => {
  it('extracts task_complete and user corrections from Codex transcript', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-codex-test-'))
    tempDirs.push(dir)
    const filePath = join(dir, 'session.jsonl')

    const lines = [
      JSON.stringify({ type: 'session_meta', payload: { session_id: 'codex-sess-1' } }),
      JSON.stringify({
        type: 'response_item',
        payload: { type: 'message', role: 'user', content: [{ type: 'text', text: 'No, that is incorrect.' }] },
      }),
      JSON.stringify({ type: 'event_msg', payload: { type: 'task_complete', duration_ms: 1200 } }),
    ]
    writeFileSync(filePath, lines.join('\n'), 'utf-8')

    const turns = []
    for await (const turn of codexReader.read(filePath)) {
      turns.push(turn)
    }

    expect(turns.length).toBeGreaterThan(0)
    const lastTurn = turns[turns.length - 1]!
    expect(lastTurn.terminationReason).toBe('task_complete')
    expect(lastTurn.exitCode).toBe(0)
    expect(lastTurn.isCorrection).toBe(true)
    expect(lastTurn.correctionRule).toBe('prefix_negation')
  })

  it('extracts exit code and user corrections from Claude Code transcript', () => {
    const lines = [
      JSON.stringify({ sessionId: 'claude-sess-1' }),
      JSON.stringify({
        message: {
          role: 'user',
          content: "That didn't work, please fix the error.",
        },
      }),
      JSON.stringify({ type: 'exit', exitCode: 0 }),
    ]

    const reader = new ClaudeContentReader()
    const result = reader.readSession(lines)

    expect(result.exitCode).toBe(0)
    expect(result.terminationReason).toBe('exit')
    expect(result.isCorrection).toBe(true)
    expect(result.correctionRule).toBe('mistake_assertion')
  })

  it('extracts termination and user correction from pi transcript', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-pi-test-'))
    tempDirs.push(dir)
    const filePath = join(dir, 'session.jsonl')

    const lines = [
      JSON.stringify({ type: 'session', id: 'pi-sess-1' }),
      JSON.stringify({
        type: 'message',
        message: {
          role: 'user',
          content: 'You made a mistake in the query.',
        },
      }),
      JSON.stringify({ type: 'exit', reason: 'completed', code: 0 }),
    ]
    writeFileSync(filePath, lines.join('\n'), 'utf-8')

    const turns = []
    for await (const turn of piReader.read(filePath)) {
      turns.push(turn)
    }

    expect(turns.length).toBe(1)
    const turn = turns[0]!
    expect(turn.terminationReason).toBe('completed')
    expect(turn.exitCode).toBe(0)
    expect(turn.isCorrection).toBe(true)
    expect(turn.correctionRule).toBe('mistake_assertion')
  })
})

describe('Run linkage and SQLite round-trip', () => {
  it('attaches outcome to DerivedRunIdentity in deriveRunIdentity', () => {
    const record = makeRecord('span-exp-1', {
      raw: {
        'gen_ai.run.id': 'run-test-01',
        exitCode: 0,
      },
    })

    const identity = deriveRunIdentity(record)

    expect(identity.runId).toBe('run-test-01')
    expect(identity.outcome).toBeDefined()
    expect(identity.outcome?.status).toBe('success')
    expect(identity.outcome?.termination.exitCode).toBe(0)
  })

  it('buildRuns attaches outcome to RunRow and persists it in SQLite', async () => {
    const store = new CanonStore(tempStorePath())

    store.upsertMany([
      makeRecord('span-run-1', {
        sessionId: 'sess-run-1',
        raw: {
          'gen_ai.run.id': 'run-measured-01',
          exitCode: 0,
        },
        tokens: defaultTokens(),
      }),
    ])

    const report = await buildRuns(store)
    expect(report.runsBuilt).toBe(1)

    const run = store.getRun('run-measured-01')
    expect(run).toBeDefined()
    expect(run?.outcome).toBeDefined()
    expect(run?.outcome?.status).toBe('success')
    expect(run?.outcome?.termination.exitCode).toBe(0)

    store.close()
  })
})

describe('Population Rate Monitoring (Plan §6)', () => {
  it('calculates outcome population rate accurately', () => {
    const measuredRecord = makeRecord('span-1', { raw: { exitCode: 0 } })
    const unmeasuredRecord = makeRecord('span-2')

    const measuredOutcome = deriveOutcome([measuredRecord])
    const unmeasuredOutcome = deriveOutcome([unmeasuredRecord])

    expect(measuredOutcome.status).toBe('success')
    expect(unmeasuredOutcome.status).toBe('not_measurable')

    const report = calculateOutcomePopulationRate([measuredOutcome, unmeasuredOutcome])

    expect(report.totalRuns).toBe(2)
    expect(report.measurableCount).toBe(1)
    expect(report.unmeasurableCount).toBe(1)
    expect(report.populationRate).toBe(0.5)
  })

  it('handles empty run arrays for population rate calculation', () => {
    const report = calculateOutcomePopulationRate([])
    expect(report.totalRuns).toBe(0)
    expect(report.populationRate).toBe(0)
  })
})
