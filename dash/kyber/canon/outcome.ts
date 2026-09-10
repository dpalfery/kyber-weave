// Outcome modeling as a guard signal for KyberDash recommendations (D7, D8).
//
// Decision D7/D8 rules:
// "Recommend, do not act; outcomes guard findings."
// An unobserved outcome is explicitly not_measurable, NEVER guessed as "completed"
// or "success". Outcome signals are sparsely populated and strictly empirical:
// exit codes, test suite result deltas, explicit user correction turns, and
// process termination reasons.
//
// When outcome signals are absent, the outcome block declares not_measurable
// per field so downstream finding engines cannot claim an unmeasured run was
// successful by absence of evidence.

import { notMeasurable } from './types.js'
import type {
  CanonicalRecord,
  Measurability,
  MetricAvailability,
  RunRow,
} from './types.js'

/**
 * High-level outcome status for a run or agent execution.
 * - 'success': Explicit clean exit (exit code 0, task_complete, all tests passed) with zero errors or corrections.
 * - 'failure': Explicit error exit, unhandled exception, fatal span status, or failing test delta.
 * - 'abandoned': Run was aborted, cancelled by user (SIGINT), or timed out before completion.
 * - 'inconclusive': Conflicting signals (e.g. exit code 0 but user correction turns or non-fatal errors occurred).
 * - 'not_measurable': Telemetry contains no observable termination, error, correction, or test signals (D7/D8).
 */
export type OutcomeStatus =
  | 'success'
  | 'failure'
  | 'abandoned'
  | 'inconclusive'
  | 'not_measurable'

/** An individual error signal captured from spans, tool outputs, or process exits. */
export type ErrorSignal = {
  source: string
  message: string
  code?: string | number
  timestamp?: string
}

/** An explicit user correction turn identified by a deterministic rule. */
export type CorrectionTurn = {
  turnIndex?: number
  timestamp?: string
  rule: string
  excerpt?: string
}

/** Test suite counts and deltas before and after model actions. */
export type TestSuiteCounts = {
  passed: number
  failed: number
  skipped?: number
  total: number
}

/** Test suite deltas observed in tool executions. */
export type TestSuiteDelta = {
  before?: TestSuiteCounts
  after?: TestSuiteCounts
  delta?: {
    passed: number
    failed: number
  }
  availability: MetricAvailability
}

/** Termination details (exit code, termination reason). */
export type TerminationSignal = {
  reason?: string
  exitCode?: number
  availability: MetricAvailability
}

/** Summary of error signals for the run. */
export type ErrorSummary = {
  count: number
  signals: readonly ErrorSignal[]
  availability: MetricAvailability
}

/** Summary of detected user corrections. */
export type CorrectionSummary = {
  count: number
  turns: readonly CorrectionTurn[]
  availability: MetricAvailability
}

/**
 * OutcomeBlock captures observable run outcomes to guard finding recommendations.
 * Follows D7/D8: all fields declare explicit measurability; absence is never 0 or success.
 */
export type OutcomeBlock = {
  status: OutcomeStatus
  statusReason?: string
  termination: TerminationSignal
  errors: ErrorSummary
  userCorrections: CorrectionSummary
  testDeltas: TestSuiteDelta
  availability: MetricAvailability
}

/** Explicit deterministic rules for identifying user correction turns. */
export type UserCorrectionRule = {
  id: string
  name: string
  description: string
  pattern: RegExp
}

export const USER_CORRECTION_RULES: readonly UserCorrectionRule[] = [
  {
    id: 'prefix_negation',
    name: 'Prefix Negation or Halt',
    description: 'User turn begins with an explicit negation or stop directive (e.g., "no", "wait", "wrong", "stop", "actually", "dont").',
    pattern: /^(?:no[,.\s]|wait[,.\s]|wrong[,.\s]|stop[,.\s]|hold on[,.\s]|actually[,.\s]|don't\b|dont\b|never mind\b|nevermind\b)/i,
  },
  {
    id: 'mistake_assertion',
    name: 'Mistake or Error Assertion',
    description: 'User turn asserts an error, mistake, failure, or requests a revert/retry (e.g., "that is wrong", "you made a mistake", "that failed", "fix the error").',
    pattern: /\b(?:that(?:'s| is)? (?:wrong|incorrect|broken|not what i (?:asked|meant|wanted))|you (?:made a mistake|forgot|broke|misunderstood)|that didn't work|that did not work|that failed|fix (?:the|this) (?:error|bug|issue|failure)|try again|revert (?:that|this)|undo (?:that|this))\b/i,
  },
] as const

export type CorrectionDetectionResult = {
  matched: boolean
  rule?: string
  excerpt?: string
}

/**
 * Detects whether a text snippet represents an explicit user correction turn.
 * Evaluates against deterministic rules, never heuristic guesses.
 */
export function detectUserCorrection(text: unknown): CorrectionDetectionResult {
  if (typeof text !== 'string') {
    return { matched: false }
  }
  const trimmed = text.trim()
  if (trimmed === '') {
    return { matched: false }
  }

  for (const rule of USER_CORRECTION_RULES) {
    if (rule.pattern.test(trimmed)) {
      const excerpt = trimmed.length > 120 ? `${trimmed.slice(0, 117)}...` : trimmed
      return {
        matched: true,
        rule: rule.id,
        excerpt,
      }
    }
  }

  return { matched: false }
}

/** Helper extracting string/number raw attributes safely. */
function rawField(record: CanonicalRecord, keys: readonly string[]): unknown {
  if (record.raw === null || typeof record.raw !== 'object' || Array.isArray(record.raw)) {
    return undefined
  }
  const raw = record.raw as Record<string, unknown>
  for (const key of keys) {
    if (key in raw && raw[key] !== undefined && raw[key] !== null) {
      return raw[key]
    }
  }
  return undefined
}

/** Regex patterns for extracting test suite run results from tool outputs. */
const TEST_OUTPUT_PATTERNS = [
  // Vitest / Jest: Tests: 2 failed, 10 passed, 12 total
  /(?:Tests|Test Files)\s*:\s*(?:(\d+)\s*failed,\s*)?(\d+)\s*passed(?:,\s*(\d+)\s*total)?/i,
  // Vitest icon style: ✓ 10 passed | ✗ 2 failed
  /(?:✓|passed:?)\s*(\d+)\s*passed(?:\s*(?:\||,)\s*(?:✗|failed:?)\s*(\d+)\s*failed)?/i,
  // Pytest: 2 failed, 10 passed in 1.23s
  /(?:(\d+)\s*failed,?\s*)?(\d+)\s*passed(?:\s*in\s*[\d.]+s)?/i,
  // Generic: 10 passed, 2 failed
  /(\d+)\s*passed\s*,\s*(\d+)\s*failed/i,
  // Go / Cargo: test result: ok. 10 passed; 0 failed
  /test result:\s*(?:ok|FAILED)\.\s*(\d+)\s*passed;\s*(\d+)\s*failed/i,
]

function parseTestCounts(text: string): TestSuiteCounts | undefined {
  for (const pattern of TEST_OUTPUT_PATTERNS) {
    const match = pattern.exec(text)
    if (!match) continue

    // Pattern 1: (failed, passed, total)
    if (pattern === TEST_OUTPUT_PATTERNS[0]) {
      const failed = match[1] ? Number.parseInt(match[1], 10) : 0
      const passed = match[2] ? Number.parseInt(match[2], 10) : 0
      const total = match[3] ? Number.parseInt(match[3], 10) : passed + failed
      return { passed, failed, total }
    }
    // Pattern 2: (passed, failed)
    if (pattern === TEST_OUTPUT_PATTERNS[1]) {
      const passed = Number.parseInt(match[1] ?? '0', 10)
      const failed = match[2] ? Number.parseInt(match[2], 10) : 0
      return { passed, failed, total: passed + failed }
    }
    // Pattern 3: (failed, passed)
    if (pattern === TEST_OUTPUT_PATTERNS[2]) {
      const failed = match[1] ? Number.parseInt(match[1], 10) : 0
      const passed = match[2] ? Number.parseInt(match[2], 10) : 0
      return { passed, failed, total: passed + failed }
    }
    // Pattern 4: (passed, failed)
    if (pattern === TEST_OUTPUT_PATTERNS[3]) {
      const passed = Number.parseInt(match[1] ?? '0', 10)
      const failed = Number.parseInt(match[2] ?? '0', 10)
      return { passed, failed, total: passed + failed }
    }
    // Pattern 5: (passed, failed)
    if (pattern === TEST_OUTPUT_PATTERNS[4]) {
      const passed = Number.parseInt(match[1] ?? '0', 10)
      const failed = Number.parseInt(match[2] ?? '0', 10)
      return { passed, failed, total: passed + failed }
    }
  }
  return undefined
}

/**
 * Derives an OutcomeBlock for a set of canonical records and optional session.
 *
 * Adheres strictly to D7/D8:
 * - If no outcome signals exist, status is 'not_measurable' with unavailable declarations.
 * - Exit codes, user corrections, test suite deltas, and termination reasons are extracted
 *   from observable telemetry only.
 */
export function deriveOutcome(
  records: readonly CanonicalRecord[],
  _session?: unknown,
): OutcomeBlock {
  if (records.length === 0) {
    return {
      status: 'not_measurable',
      statusReason: 'No records provided to observe run outcome.',
      termination: {
        availability: notMeasurable('No records available to measure termination.'),
      },
      errors: {
        count: 0,
        signals: [],
        availability: notMeasurable('No records available to observe error channels.'),
      },
      userCorrections: {
        count: 0,
        turns: [],
        availability: notMeasurable('No conversation history available to detect user corrections.'),
      },
      testDeltas: {
        availability: notMeasurable('No test execution results observed.'),
      },
      availability: notMeasurable('Outcome was not observable for empty record set.'),
    }
  }

  // 1. Extract termination reason and exit code
  let observedExitCode: number | undefined
  let observedTerminationReason: string | undefined

  for (const record of records) {
    const rawExit = rawField(record, ['exitCode', 'exit_code', 'status_code'])
    if (typeof rawExit === 'number' && Number.isFinite(rawExit)) {
      observedExitCode = rawExit
    }

    const rawTerm = rawField(record, ['terminationReason', 'termination_reason', 'stop_reason', 'finish_reason'])
    if (typeof rawTerm === 'string' && rawTerm.trim() !== '') {
      observedTerminationReason = rawTerm.trim()
    }

    const rawType = rawField(record, ['type', 'event'])
    if (typeof rawType === 'string' && (rawType === 'task_complete' || rawType === 'task_failed' || rawType === 'turn_aborted')) {
      observedTerminationReason = rawType
      if (rawType === 'task_complete' && observedExitCode === undefined) {
        observedExitCode = 0
      }
    }
  }

  // 2. Extract error signals
  const errorSignals: ErrorSignal[] = []
  for (const record of records) {
    const ts = typeof record.timestamp === 'string' ? record.timestamp : record.timestamp.toISOString()

    if (record.status === 'error' || record.status === 'fatal' || record.status === 'failure') {
      errorSignals.push({
        source: record.source,
        message: record.name || 'Span error status',
        code: record.status,
        timestamp: ts,
      })
    }

    const rawErr = rawField(record, ['error', 'exception'])
    if (rawErr !== undefined) {
      errorSignals.push({
        source: record.harness,
        message: typeof rawErr === 'string' ? rawErr : JSON.stringify(rawErr),
        timestamp: ts,
      })
    }

    const editFailed = rawField(record, ['editFailed'])
    if (typeof editFailed === 'number' && editFailed > 0) {
      errorSignals.push({
        source: record.harness,
        message: `${editFailed} patch edit(s) failed`,
        timestamp: ts,
      })
    }

    // Inspect tool results for fatal unhandled exceptions
    const toolOutput = record.content.tool_result_content
    if (typeof toolOutput === 'string' && toolOutput !== '') {
      if (/Traceback \(most recent call last\):|FATAL ERROR:|UnhandledPromiseRejection|panic: /i.test(toolOutput)) {
        errorSignals.push({
          source: record.harness,
          message: 'Unhandled exception or panic observed in tool result',
          timestamp: ts,
        })
      }
    }
  }

  // 3. Extract user correction turns
  const correctionTurns: CorrectionTurn[] = []
  let hasConversationHistory = false

  for (let idx = 0; idx < records.length; idx++) {
    const record = records[idx]!
    const ts = typeof record.timestamp === 'string' ? record.timestamp : record.timestamp.toISOString()

    // Explicit telemetry flag from reader
    const isCorr = rawField(record, ['isCorrection'])
    const corrRule = rawField(record, ['correctionRule'])
    if (isCorr === true) {
      hasConversationHistory = true
      correctionTurns.push({
        turnIndex: idx,
        timestamp: ts,
        rule: typeof corrRule === 'string' ? corrRule : 'explicit_telemetry_indicator',
      })
      continue
    }

    // User message in raw call
    const userMessage = rawField(record, ['userMessage'])
    if (typeof userMessage === 'string' && userMessage.trim() !== '') {
      hasConversationHistory = true
      const check = detectUserCorrection(userMessage)
      if (check.matched) {
        correctionTurns.push({
          turnIndex: idx,
          timestamp: ts,
          rule: check.rule ?? 'prefix_negation',
          excerpt: check.excerpt,
        })
      }
    }

    // Conversation history parts
    if (record.parts && record.parts.length > 0) {
      for (const part of record.parts) {
        if (part.part === 'conversation_history') {
          hasConversationHistory = true
          const check = detectUserCorrection(part.text)
          if (check.matched) {
            correctionTurns.push({
              turnIndex: idx,
              timestamp: ts,
              rule: check.rule ?? 'prefix_negation',
              excerpt: check.excerpt,
            })
          }
        }
      }
    } else if (typeof record.content.conversation_history === 'string' && record.content.conversation_history !== '') {
      hasConversationHistory = true
      const check = detectUserCorrection(record.content.conversation_history)
      if (check.matched) {
        correctionTurns.push({
          turnIndex: idx,
          timestamp: ts,
          rule: check.rule ?? 'prefix_negation',
          excerpt: check.excerpt,
        })
      }
    }
  }

  // 4. Extract test suite results and deltas
  const testOutputs: TestSuiteCounts[] = []
  for (const record of records) {
    if (record.parts) {
      for (const part of record.parts) {
        if (part.part === 'tool_result_content') {
          const parsed = parseTestCounts(part.text)
          if (parsed) testOutputs.push(parsed)
        }
      }
    } else if (typeof record.content.tool_result_content === 'string') {
      const parsed = parseTestCounts(record.content.tool_result_content)
      if (parsed) testOutputs.push(parsed)
    }
  }

  let testDeltas: TestSuiteDelta
  if (testOutputs.length === 0) {
    testDeltas = {
      availability: notMeasurable('No test execution results observed in records or tool results.'),
    }
  } else if (testOutputs.length === 1) {
    const counts = testOutputs[0]!
    testDeltas = {
      after: counts,
      delta: {
        passed: counts.passed,
        failed: counts.failed,
      },
      availability: 'measured',
    }
  } else {
    const before = testOutputs[0]!
    const after = testOutputs[testOutputs.length - 1]!
    testDeltas = {
      before,
      after,
      delta: {
        passed: after.passed - before.passed,
        failed: after.failed - before.failed,
      },
      availability: 'measured',
    }
  }

  // 5. Build Sub-Blocks
  const termination: TerminationSignal =
    observedExitCode !== undefined || observedTerminationReason !== undefined
      ? {
          exitCode: observedExitCode,
          reason: observedTerminationReason,
          availability: 'measured',
        }
      : {
          availability: notMeasurable('No termination or exit indicator was emitted by the harness.'),
        }

  const errors: ErrorSummary = {
    count: errorSignals.length,
    signals: errorSignals,
    availability: 'measured',
  }

  const userCorrections: CorrectionSummary = hasConversationHistory
    ? {
        count: correctionTurns.length,
        turns: correctionTurns,
        availability: 'measured',
      }
    : {
        count: 0,
        turns: [],
        availability: notMeasurable('No conversation history available to detect user corrections.'),
      }

  // 6. Synthesize Overall Outcome Status (D7/D8)
  let status: OutcomeStatus = 'not_measurable'
  let statusReason: string = 'No observable outcome signals (exit code, termination event, test deltas, or errors) were emitted.'
  let outcomeAvail: MetricAvailability = notMeasurable('Outcome was not observable for this run.')

  const isAbandoned =
    observedTerminationReason !== undefined &&
    /^(?:cancelled|aborted|sigint|user_cancelled|interrupted)$/i.test(observedTerminationReason)

  if (isAbandoned) {
    status = 'abandoned'
    statusReason = 'Run was cancelled or aborted before completion.'
    outcomeAvail = 'measured'
  } else if (observedExitCode !== undefined && observedExitCode !== 0) {
    status = 'failure'
    statusReason = `Process or command terminated with non-zero exit code ${observedExitCode}.`
    outcomeAvail = 'measured'
  } else if (testDeltas.after && testDeltas.after.failed > 0) {
    status = 'failure'
    statusReason = `Test suite failed with ${testDeltas.after.failed} failing test(s).`
    outcomeAvail = 'measured'
  } else if (testDeltas.delta && testDeltas.delta.failed > 0) {
    status = 'failure'
    statusReason = `Test suite delta introduced ${testDeltas.delta.failed} new test failure(s).`
    outcomeAvail = 'measured'
  } else if (
    errorSignals.length > 0 &&
    (records[records.length - 1]?.status === 'error' ||
      observedTerminationReason === 'task_failed' ||
      observedTerminationReason === 'error' ||
      observedTerminationReason === 'fatal')
  ) {
    status = 'failure'
    statusReason = `Run terminated with fatal error: ${errorSignals[0]?.message ?? 'unhandled error'}.`
    outcomeAvail = 'measured'
  } else if (
    userCorrections.count > 0 &&
    (observedExitCode === 0 || observedTerminationReason === 'task_complete')
  ) {
    // Clean exit but required human intervention/correction
    status = 'inconclusive'
    statusReason = `Run exited with code 0 but required ${userCorrections.count} user correction turn(s).`
    outcomeAvail = 'measured'
  } else if (
    errorSignals.length > 0 &&
    (observedExitCode === 0 || observedTerminationReason === 'task_complete')
  ) {
    status = 'inconclusive'
    statusReason = `Run exited cleanly but recorded ${errorSignals.length} non-fatal error signal(s).`
    outcomeAvail = 'measured'
  } else if (
    (observedExitCode === 0 || observedTerminationReason === 'task_complete') &&
    errorSignals.length === 0 &&
    userCorrections.count === 0
  ) {
    status = 'success'
    statusReason = 'Run completed cleanly with zero exit code and zero errors or user corrections.'
    outcomeAvail = 'measured'
  } else if (
    testDeltas.after &&
    testDeltas.after.passed > 0 &&
    testDeltas.after.failed === 0 &&
    errorSignals.length === 0 &&
    userCorrections.count === 0
  ) {
    status = 'success'
    statusReason = `Test suite passed with ${testDeltas.after.passed} test(s) and zero errors.`
    outcomeAvail = 'measured'
  } else {
    // Unmeasured fallback: D7/D8 strictly forbids guessing "success" or "completed"
    status = 'not_measurable'
    statusReason = 'No observable outcome signals (exit code, termination event, test deltas, or fatal errors) were emitted.'
    outcomeAvail = notMeasurable('Outcome was not observable for this run.')
  }

  return {
    status,
    statusReason,
    termination,
    errors,
    userCorrections,
    testDeltas,
    availability: outcomeAvail,
  }
}

/**
 * Returns the MetricAvailability of the outcome for a target.
 * Accepts an OutcomeBlock, a record set, or a Measurability map.
 */
export function outcomeAvailability(
  target?: OutcomeBlock | readonly CanonicalRecord[] | Measurability | null,
): MetricAvailability {
  if (target === null || target === undefined) {
    return notMeasurable('Outcome availability unknown: target was not provided.')
  }
  if ('status' in target && 'availability' in target) {
    return target.availability
  }
  if (Array.isArray(target)) {
    return deriveOutcome(target).availability
  }
  const measurability = target as Measurability
  if (measurability['outcome'] !== undefined) {
    return measurability['outcome']
  }
  return notMeasurable('This source does not export outcome telemetry.')
}

/** Summary of live corpus outcome measurability (Plan §6). */
export type OutcomePopulationReport = {
  totalRuns: number
  measurableCount: number
  unmeasurableCount: number
  populationRate: number
}

/**
 * Calculates outcome population rate across a set of runs or outcome blocks.
 * Used to monitor the §6 risk: below 60% outcome population, finding recommendations
 * must display an unverified state.
 */
export function calculateOutcomePopulationRate(
  runs: readonly (RunRow | OutcomeBlock)[],
): OutcomePopulationReport {
  if (runs.length === 0) {
    return {
      totalRuns: 0,
      measurableCount: 0,
      unmeasurableCount: 0,
      populationRate: 0,
    }
  }

  let measurable = 0
  for (const item of runs) {
    const outcome = 'outcome' in item ? (item as RunRow).outcome : (item as OutcomeBlock)
    if (outcome && outcome.status !== 'not_measurable' && outcome.availability === 'measured') {
      measurable++
    }
  }

  return {
    totalRuns: runs.length,
    measurableCount: measurable,
    unmeasurableCount: runs.length - measurable,
    populationRate: measurable / runs.length,
  }
}
