// Pure signal detectors for KyberDash (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task F1: The Signal Engine; Decision D3, D5, D6, D16, D17; ADR 0006, ADR 0008, ADR 0009, ADR 0011).
//
// The core discipline proven by the canonical store:
// 1. All detectors are pure functions operating on session, turn, or execution data,
//    with zero external network I/O.
// 2. Absent telemetry is NEVER reported as zero; it emits `{ status: 'not_measurable', reason: string }`
//    in accordance with ADR 0009 and ADR 0011.
// 3. Mathematical boundaries are strictly checked (division by zero, negative inputs, empty sets).
// 4. Prefix stability uses whitespace-normalised SHA-256 hashing and explicitly reports normalisation.
//    Where prefix bytes are absent, it degrades cleanly to the counter-only fallback (`cache_read ÷ input`)
//    labelled `detect-but-cannot-locate`.
// 5. Tool yield credits on weak evidence (1 call) and debits only on strong evidence (resident, 0 calls),
//    with explicit documentation and telemetry acknowledging under-counting of negative-information reads.
// 6. Skill utilisation is stamped at low confidence and ranked last per Decision D16.

import { createHash } from 'node:crypto'
import {
  cacheAvailability,
  harnessDimensionAvailability,
  prefixAvailability,
} from '../canon/measurability.js'
import { isNotMeasurable } from '../canon/types.js'
import type {
  CanonicalRecord,
  Measurability,
  TokenUsage,
} from '../canon/types.js'

// ---------------------------------------------------------------------------
// Common Signal Types & Contracts (Decision D5, Decision D17)
// ---------------------------------------------------------------------------

/** Measurement classification for findings and signals (Decision D5). */
export type MeasurementClass = 'deterministic' | 'inferred' | 'coverage-gap'

/** Confidence level for diagnostic claims. */
export type SignalConfidence = 'high' | 'medium' | 'low'

/** Unmeasurable signal state: absent telemetry is never zero (ADR 0009, ADR 0011). */
export type SignalNotMeasurable = {
  status: 'not_measurable'
  reason: string
}

/** Measurable or derived signal result carrying calibrated metadata and provenance. */
export type SignalMeasured<T = number> = {
  status: 'measured' | 'derived'
  value: T
  measurementClass: MeasurementClass
  confidence?: SignalConfidence
  confidenceBasis?: string
  numerator?: number
  denominator?: number
  metadata?: Record<string, unknown>
  warning?: string
  fallback?: string
}

/** Discriminated union for pure signal results. */
export type SignalResult<T = number> = SignalMeasured<T> | SignalNotMeasurable

/** Type guard for measurable signal results. */
export function isSignalMeasurable<T>(result: SignalResult<T>): result is SignalMeasured<T> {
  return result.status === 'measured' || result.status === 'derived'
}

// ---------------------------------------------------------------------------
// Whitespace Normalization & Hashing (Task F1 Acceptance Criteria)
// ---------------------------------------------------------------------------

/**
 * Normalizes text by trimming outer whitespace and collapsing internal whitespace runs
 * (newlines, tabs, spaces) into a single space.
 *
 * Why: Prompt templates and formatting vary whitespace across sequential turns
 * without altering prompt semantics or invalidating modern token-level prefix caches.
 */
export function normalizeWhitespace(text: string): string {
  return text.trim().replace(/\s+/g, ' ')
}

/**
 * Computes a deterministic SHA-256 hash over whitespace-normalized text.
 */
export function hashNormalized(text: string): string {
  return createHash('sha256').update(normalizeWhitespace(text), 'utf8').digest('hex')
}

// ---------------------------------------------------------------------------
// Signal 1: Context Reuse (contextReuse / contextReuseRatio)
// ---------------------------------------------------------------------------

export type ContextReuseInput = {
  harness?: string
  freshInput?: number
  cacheRead?: number
  cacheCreation?: number
  reportedInput?: number
  turns?: readonly {
    freshInput?: number
    cacheRead?: number
    cacheCreation?: number
    reportedInput?: number
    input?: number
  }[]
  measurability?: Measurability
}

/**
 * Computes the cache reuse ratio: `cacheRead / (freshInput + cacheRead + cacheCreation)`.
 *
 * Edge cases:
 * - Emits `not_measurable` if harness does not export cache counters or if measurability says so.
 * - Emits `not_measurable` if total input is zero (no turns or empty session).
 * - Bounds return value in [0.0, 1.0].
 */
export function contextReuse(input: ContextReuseInput): SignalResult<number> {
  const harness = input.harness

  // Check measurability declaration first
  if (input.measurability) {
    const cacheAvail = input.measurability['cache_read'] ?? input.measurability['cache_hit_rate']
    if (isNotMeasurable(cacheAvail)) {
      return { status: 'not_measurable', reason: cacheAvail.reason }
    }
  }

  // Check harness survey availability if harness is specified
  if (harness) {
    const survey = cacheAvailability(harness)
    if (survey.status === 'unsupported' || survey.status === 'not_measurable') {
      return { status: 'not_measurable', reason: survey.reason }
    }
    const dimAvail = harnessDimensionAvailability(harness, 'cache_hit_rate')
    if (isNotMeasurable(dimAvail)) {
      return { status: 'not_measurable', reason: dimAvail.reason }
    }
  }

  // Accumulate token classes
  let fresh = input.freshInput ?? 0
  let cacheRead = input.cacheRead ?? 0
  let cacheCreation = input.cacheCreation ?? 0

  if (input.turns && input.turns.length > 0) {
    fresh = 0
    cacheRead = 0
    cacheCreation = 0
    for (const turn of input.turns) {
      fresh += turn.freshInput ?? (turn.input !== undefined && turn.cacheRead !== undefined ? Math.max(0, turn.input - turn.cacheRead) : 0)
      cacheRead += turn.cacheRead ?? 0
      cacheCreation += turn.cacheCreation ?? 0
    }
  }

  let totalInput = fresh + cacheRead + cacheCreation
  const fallbackInput = input.reportedInput ?? 0
  if (totalInput <= 0 && fallbackInput > 0) {
    totalInput = fallbackInput
  }

  if (totalInput <= 0) {
    return {
      status: 'not_measurable',
      reason: 'No measurable input tokens in session turns to compute context reuse.',
    }
  }

  const ratio = cacheRead / totalInput
  return {
    status: 'measured',
    value: Math.min(1.0, Math.max(0.0, ratio)),
    measurementClass: 'deterministic',
    confidence: 'high',
    confidenceBasis: 'Computed directly from measured OTel/ASAD disjoint token counters.',
    numerator: cacheRead,
    denominator: totalInput,
    metadata: {
      freshInputTokens: fresh,
      cacheReadTokens: cacheRead,
      cacheCreationTokens: cacheCreation,
      totalInputTokens: totalInput,
    },
  }
}

export const contextReuseRatio = contextReuse

// ---------------------------------------------------------------------------
// Signal 2: Prefix Stability (prefixStability / cachePrefixStability)
// ---------------------------------------------------------------------------

export type TurnPrefixInput = {
  index?: number
  systemPrompt?: string
  instructionContext?: string
  prefixText?: string
  parts?: readonly { part: string; text: string }[]
  tokens?: TokenUsage
}

export type PrefixStabilityInput = {
  harness?: string
  turns?: readonly TurnPrefixInput[]
  measurability?: Measurability
  totalInput?: number
  cacheRead?: number
}

/**
 * Evaluates stability of message prefix across sequential turns using whitespace-normalized hashing.
 *
 * Fallback behavior (Task F1 / ADR 0009 / ADR 0011):
 * - If prefix content is unavailable but cache counters exist, degrades to counter-only fallback
 *   (`cache_read ÷ input`) with label `detect-but-cannot-locate` and measurementClass `inferred`.
 * - If neither prefix bytes nor cache counters exist, emits `not_measurable`.
 * - Requires at least two turns to evaluate sequential stability.
 */
export function prefixStability(input: PrefixStabilityInput): SignalResult<number> {
  const harness = input.harness

  // Check explicit measurability declaration
  if (input.measurability) {
    const prefixAvail = input.measurability['prefix_stability'] ?? input.measurability['system_prompt']
    if (isNotMeasurable(prefixAvail)) {
      // Check if counter fallback is viable
      const cacheAvail = input.measurability['cache_hit_rate'] ?? input.measurability['cache_read']
      if (isNotMeasurable(cacheAvail) || input.cacheRead === undefined || input.totalInput === undefined) {
        return { status: 'not_measurable', reason: prefixAvail.reason }
      }
    }
  }

  // Extract prefix text per turn
  const turnPrefixes: string[] = []
  if (input.turns && input.turns.length > 0) {
    for (const turn of input.turns) {
      if (turn.prefixText !== undefined && turn.prefixText !== '') {
        turnPrefixes.push(turn.prefixText)
      } else if (turn.parts && turn.parts.length > 0) {
        const prefixParts = turn.parts
          .filter((p) => p.part === 'system_prompt' || p.part === 'instruction_context')
          .map((p) => p.text)
        if (prefixParts.length > 0) {
          turnPrefixes.push(prefixParts.join('\n'))
        }
      } else if (turn.systemPrompt !== undefined || turn.instructionContext !== undefined) {
        const combined = [turn.systemPrompt, turn.instructionContext].filter(Boolean).join('\n')
        if (combined !== '') {
          turnPrefixes.push(combined)
        }
      }
    }
  }

  // If prefix text is present across turns, compute deterministic hash stability
  if (turnPrefixes.length >= 2) {
    const hashes = turnPrefixes.map(hashNormalized)
    let stableTransitions = 0
    const totalTransitions = hashes.length - 1

    for (let i = 0; i < totalTransitions; i++) {
      if (hashes[i] === hashes[i + 1]) {
        stableTransitions += 1
      }
    }

    const ratio = stableTransitions / totalTransitions
    return {
      status: 'measured',
      value: Math.min(1.0, Math.max(0.0, ratio)),
      measurementClass: 'deterministic',
      confidence: 'high',
      confidenceBasis: 'Computed from whitespace-normalized SHA-256 hashes of sequential turn prefixes.',
      numerator: stableTransitions,
      denominator: totalTransitions,
      metadata: {
        normalisation: 'whitespace-collapsed-sha256',
        totalTurns: turnPrefixes.length,
        totalTransitions,
        stableTransitions,
        hashes,
      },
    }
  }

  // If turn count is 1 with prefix text, single turn has no sequential transitions
  if (turnPrefixes.length === 1) {
    return {
      status: 'not_measurable',
      reason: 'At least two turns are required to evaluate prefix stability across sequential transitions.',
    }
  }

  // Degradation to counter-only fallback (Decision D17 / Task F1 Acceptance Criteria)
  const prefixSurvey = harness ? prefixAvailability(harness) : undefined
  const fallbackPermitted = prefixSurvey?.fallback === 'detect-but-cannot-locate' || !prefixSurvey

  // Calculate counter fallback if totalInput and cacheRead are accessible
  let totalInput = input.totalInput ?? 0
  let cacheRead = input.cacheRead ?? 0

  if (input.turns && input.turns.length > 0 && totalInput === 0) {
    for (const t of input.turns) {
      if (t.tokens) {
        totalInput += t.tokens.reportedInput || (t.tokens.freshInput + t.tokens.cacheRead + t.tokens.cacheCreation)
        cacheRead += t.tokens.cacheRead
      }
    }
  }

  if (fallbackPermitted && totalInput > 0) {
    const fallbackRatio = cacheRead / totalInput
    return {
      status: 'derived',
      value: Math.min(1.0, Math.max(0.0, fallbackRatio)),
      measurementClass: 'inferred',
      confidence: 'medium',
      confidenceBasis: 'Prefix bytes unavailable; degraded to cache counter ratio fallback (cache_read ÷ input).',
      fallback: 'detect-but-cannot-locate',
      numerator: cacheRead,
      denominator: totalInput,
      metadata: {
        normalisation: 'none (counter-fallback)',
        degraded: true,
        reason: 'Prefix text unavailable for this harness; using counter-only fallback',
      },
    }
  }

  // Neither prefix bytes nor cache counter fallback available
  return {
    status: 'not_measurable',
    reason: prefixSurvey?.reason ?? 'Prefix bytes are not captured and harness does not export cache counters for fallback.',
  }
}

export const cachePrefixStability = prefixStability

// ---------------------------------------------------------------------------
// Signal 3: Tool Yield (toolYield)
// ---------------------------------------------------------------------------

export type DefinedToolInput = {
  name: string
  tokens?: number
  server?: string
}

export type ToolYieldInput = {
  harness?: string
  definedTools?: readonly (string | DefinedToolInput)[]
  invokedTools?: readonly (string | { name: string })[]
  measurability?: Measurability
}

/**
 * Computes tool yield: ratio of distinct invoked tools to defined schema tools.
 *
 * Rules (Task F1 Acceptance Criteria):
 * - Credits on weak evidence: a single invocation of a tool credits it as used.
 * - Debits only on strong evidence: a tool is debited only when defined and never called.
 * - Documents that raw invocation ratio under-counts negative-information reads:
 *   an agent may read a tool definition and intentionally decline to call it, deriving
 *   correct value that is invisible to invocation tracking.
 * - Emits `not_measurable` when tool definitions are absent from telemetry.
 */
export function toolYield(input: ToolYieldInput): SignalResult<number> {
  const harness = input.harness

  // Check measurability declaration
  if (input.measurability) {
    const schemaAvail = input.measurability['tool_definitions'] ?? input.measurability['tool_yield']
    if (isNotMeasurable(schemaAvail)) {
      return { status: 'not_measurable', reason: schemaAvail.reason }
    }
  }

  if (harness) {
    const dimAvail = harnessDimensionAvailability(harness, 'tool_yield')
    if (isNotMeasurable(dimAvail)) {
      return { status: 'not_measurable', reason: dimAvail.reason }
    }
  }

  if (!input.definedTools || input.definedTools.length === 0) {
    return {
      status: 'not_measurable',
      reason: 'No tool definitions provided or exported by harness schema telemetry.',
    }
  }

  // Extract canonical tool names
  const definedNames = new Set<string>()
  for (const t of input.definedTools) {
    const name = typeof t === 'string' ? t.trim() : t.name.trim()
    if (name) definedNames.add(name)
  }

  if (definedNames.size === 0) {
    return {
      status: 'not_measurable',
      reason: 'No valid tool definitions defined in schema.',
    }
  }

  const invokedNames = new Set<string>()
  if (input.invokedTools) {
    for (const inv of input.invokedTools) {
      const name = typeof inv === 'string' ? inv.trim() : inv.name.trim()
      if (name) invokedNames.add(name)
    }
  }

  // Credit on weak evidence: tool was invoked at least once
  const creditedTools: string[] = []
  const unusedTools: string[] = []

  for (const defined of definedNames) {
    if (invokedNames.has(defined)) {
      creditedTools.push(defined)
    } else {
      unusedTools.push(defined)
    }
  }

  const yieldRatio = creditedTools.length / definedNames.size
  return {
    status: 'measured',
    value: Math.min(1.0, Math.max(0.0, yieldRatio)),
    measurementClass: 'deterministic',
    confidence: 'high',
    confidenceBasis: 'Computed by matching schema definitions against logged tool invocation spans.',
    numerator: creditedTools.length,
    denominator: definedNames.size,
    metadata: {
      undercountsNegativeInformation: true,
      undercountingRationale:
        'Raw invocation yield under-counts negative-information reads: an agent inspecting a tool definition to rule out an action derives value without invoking it.',
      totalDefinedTools: definedNames.size,
      distinctInvokedTools: creditedTools.length,
      creditedTools,
      unusedTools,
    },
  }
}

// ---------------------------------------------------------------------------
// Signal 4: Duplicate Call Rate (duplicateCallRate)
// ---------------------------------------------------------------------------

export type ToolCallInput = {
  name: string
  arguments?: unknown
  callId?: string
}

export type DuplicateCallRateInput = {
  harness?: string
  calls?: readonly ToolCallInput[]
  measurability?: Measurability
}

function serializeArgs(args: unknown): string {
  if (args === undefined || args === null) return ''
  let parsed: unknown = args
  if (typeof args === 'string') {
    try {
      parsed = JSON.parse(args)
    } catch {
      return normalizeWhitespace(args)
    }
  }
  try {
    return normalizeWhitespace(JSON.stringify(parsed))
  } catch {
    return String(args)
  }
}

/**
 * Detects identical/redundant tool calls in a session.
 *
 * A duplicate call is defined as a subsequent invocation with the same tool name
 * and whitespace-normalized arguments as an earlier call.
 */
export function duplicateCallRate(input: DuplicateCallRateInput): SignalResult<number> {
  if (input.measurability) {
    const callAvail = input.measurability['tool_invocations'] ?? input.measurability['token_usage']
    if (isNotMeasurable(callAvail)) {
      return { status: 'not_measurable', reason: callAvail.reason }
    }
  }

  if (!input.calls || input.calls.length === 0) {
    return {
      status: 'not_measurable',
      reason: 'No tool invocations observed in session to evaluate duplicate call rate.',
    }
  }

  const seenCalls = new Set<string>()
  let duplicateCount = 0
  const duplicateDetails: { index: number; name: string; key: string }[] = []

  for (let i = 0; i < input.calls.length; i++) {
    const call = input.calls[i]!
    const key = `${call.name.trim()}::${serializeArgs(call.arguments)}`
    if (seenCalls.has(key)) {
      duplicateCount += 1
      duplicateDetails.push({ index: i, name: call.name, key })
    } else {
      seenCalls.add(key)
    }
  }

  const rate = duplicateCount / input.calls.length
  return {
    status: 'measured',
    value: Math.min(1.0, Math.max(0.0, rate)),
    measurementClass: 'deterministic',
    confidence: 'high',
    confidenceBasis: 'Computed by hashing tool names and whitespace-normalized JSON arguments.',
    numerator: duplicateCount,
    denominator: input.calls.length,
    metadata: {
      normalisation: 'whitespace-collapsed-sha256',
      totalCalls: input.calls.length,
      duplicateCalls: duplicateCount,
      uniqueCalls: seenCalls.size,
      duplicateDetails,
    },
  }
}

// ---------------------------------------------------------------------------
// Signal 5: Oversized Results (oversizedResults / oversizedResultShare)
// ---------------------------------------------------------------------------

export type ToolResultInput = {
  toolName?: string
  content: string | unknown
  tokens?: number
  characters?: number
  thresholdTokens?: number
  thresholdCharacters?: number
}

export type OversizedResultsInput = {
  harness?: string
  results?: readonly ToolResultInput[]
  measurability?: Measurability
  defaultTokenThreshold?: number
  defaultCharThreshold?: number
}

export const DEFAULT_OVERSIZED_TOKEN_THRESHOLD = 2000
export const DEFAULT_OVERSIZED_CHAR_THRESHOLD = 8000

/**
 * Flags tool result blocks that exceed expected length thresholds.
 *
 * Ratio: `oversizedResultsCount / totalResultsCount`.
 */
export function oversizedResults(input: OversizedResultsInput): SignalResult<number> {
  if (input.measurability) {
    const resultAvail = input.measurability['tool_result_content']
    if (isNotMeasurable(resultAvail)) {
      return { status: 'not_measurable', reason: resultAvail.reason }
    }
  }

  if (!input.results || input.results.length === 0) {
    return {
      status: 'not_measurable',
      reason: 'No tool results captured in session to evaluate size.',
    }
  }

  const tokenThreshold = input.defaultTokenThreshold ?? DEFAULT_OVERSIZED_TOKEN_THRESHOLD
  const charThreshold = input.defaultCharThreshold ?? DEFAULT_OVERSIZED_CHAR_THRESHOLD

  let oversizedCount = 0
  let totalBytes = 0
  let oversizedBytes = 0
  const flagged: { index: number; toolName?: string; tokens?: number; characters: number }[] = []

  for (let i = 0; i < input.results.length; i++) {
    const res = input.results[i]!
    const textContent = typeof res.content === 'string' ? res.content : JSON.stringify(res.content ?? '')
    const charCount = res.characters ?? textContent.length
    totalBytes += charCount

    const resTokenThreshold = res.thresholdTokens ?? tokenThreshold
    const resCharThreshold = res.thresholdCharacters ?? charThreshold

    const isOversizedByTokens = res.tokens !== undefined && res.tokens > resTokenThreshold
    const isOversizedByChars = charCount > resCharThreshold

    if (isOversizedByTokens || isOversizedByChars) {
      oversizedCount += 1
      oversizedBytes += charCount
      flagged.push({
        index: i,
        toolName: res.toolName,
        tokens: res.tokens,
        characters: charCount,
      })
    }
  }

  const share = oversizedCount / input.results.length
  return {
    status: 'measured',
    value: Math.min(1.0, Math.max(0.0, share)),
    measurementClass: 'deterministic',
    confidence: 'high',
    confidenceBasis: 'Computed by comparing captured result lengths against configured thresholds.',
    numerator: oversizedCount,
    denominator: input.results.length,
    metadata: {
      totalResults: input.results.length,
      oversizedCount,
      totalBytes,
      oversizedBytes,
      byteShare: totalBytes > 0 ? oversizedBytes / totalBytes : 0,
      tokenThreshold,
      charThreshold,
      flagged,
    },
  }
}

export const oversizedResultShare = oversizedResults

// ---------------------------------------------------------------------------
// Signal 6: Compaction Pressure (compactionPressure)
// ---------------------------------------------------------------------------

export type CompactionPressureInput = {
  harness?: string
  peakInputTokens?: number
  contextLimit?: number
  turns?: readonly { inputTokens?: number; reportedInput?: number }[]
  measurability?: Measurability
}

export const DEFAULT_CONTEXT_WINDOW_LIMIT = 200_000

/**
 * Measures context window consumption and compaction risk: `peakInputTokens / contextLimit`.
 */
export function compactionPressure(input: CompactionPressureInput): SignalResult<number> {
  const harness = input.harness

  if (input.measurability) {
    const tokenAvail = input.measurability['token_usage']
    if (isNotMeasurable(tokenAvail)) {
      return { status: 'not_measurable', reason: tokenAvail.reason }
    }
  }

  if (harness) {
    const dimAvail = harnessDimensionAvailability(harness, 'context_pressure')
    if (isNotMeasurable(dimAvail)) {
      return { status: 'not_measurable', reason: dimAvail.reason }
    }
  }

  let peak = input.peakInputTokens ?? 0
  if (input.turns && input.turns.length > 0) {
    for (const turn of input.turns) {
      const turnTokens = turn.inputTokens ?? turn.reportedInput ?? 0
      if (turnTokens > peak) peak = turnTokens
    }
  }

  if (peak <= 0) {
    return {
      status: 'not_measurable',
      reason: 'No positive token counts available to compute compaction pressure.',
    }
  }

  const limit = input.contextLimit ?? DEFAULT_CONTEXT_WINDOW_LIMIT
  if (limit <= 0) {
    return {
      status: 'not_measurable',
      reason: `Context limit must be a positive integer, received: ${limit}.`,
    }
  }

  const ratio = peak / limit
  const risk: 'low' | 'moderate' | 'high' | 'critical' =
    ratio >= 0.9 ? 'critical' : ratio >= 0.75 ? 'high' : ratio >= 0.5 ? 'moderate' : 'low'

  return {
    status: 'measured',
    value: ratio,
    measurementClass: 'deterministic',
    confidence: 'high',
    confidenceBasis: 'Computed from peak turn input tokens relative to the declared context window limit.',
    numerator: peak,
    denominator: limit,
    metadata: {
      peakInputTokens: peak,
      contextLimit: limit,
      remainingTokens: Math.max(0, limit - peak),
      compactionRisk: risk,
    },
  }
}

// ---------------------------------------------------------------------------
// Signal 7: Delegation Overhead (delegationOverhead)
// ---------------------------------------------------------------------------

export type ExecutionInput = {
  isRoot: boolean
  tokens?: number | TokenUsage
  parentExecutionId?: string | null
}

export type DelegationOverheadInput = {
  harness?: string
  rootTokens?: number
  subagentTokens?: number
  executions?: readonly ExecutionInput[]
  measurability?: Measurability
}

/**
 * Computes delegation overhead: ratio of subagent execution tokens to total workflow tokens.
 *
 * Rules:
 * - Emits `not_measurable` if harness does not export execution hierarchy or subagent parentage.
 * - Single-agent workflows with verified structure report `0.0` overhead.
 */
export function delegationOverhead(input: DelegationOverheadInput): SignalResult<number> {
  const harness = input.harness

  if (input.measurability) {
    const structAvail = input.measurability['execution_structure'] ?? input.measurability['delegation_overhead']
    if (isNotMeasurable(structAvail)) {
      return { status: 'not_measurable', reason: structAvail.reason }
    }
  }

  if (harness) {
    const dimAvail = harnessDimensionAvailability(harness, 'delegation_overhead')
    if (isNotMeasurable(dimAvail)) {
      return { status: 'not_measurable', reason: dimAvail.reason }
    }
  }

  let rootTokens = input.rootTokens ?? 0
  let subagentTokens = input.subagentTokens ?? 0
  let subagentCount = 0

  if (input.executions && input.executions.length > 0) {
    rootTokens = 0
    subagentTokens = 0
    for (const exec of input.executions) {
      const tokens =
        typeof exec.tokens === 'number'
          ? exec.tokens
          : exec.tokens
            ? exec.tokens.reportedInput + exec.tokens.output
            : 0

      if (exec.isRoot) {
        rootTokens += tokens
      } else {
        subagentTokens += tokens
        subagentCount += 1
      }
    }
  }

  const totalTokens = rootTokens + subagentTokens
  if (totalTokens <= 0) {
    return {
      status: 'not_measurable',
      reason: 'No token usage recorded across workflow executions to compute delegation overhead.',
    }
  }

  const overhead = subagentTokens / totalTokens
  return {
    status: 'measured',
    value: Math.min(1.0, Math.max(0.0, overhead)),
    measurementClass: 'deterministic',
    confidence: 'high',
    confidenceBasis: 'Computed from subagent execution tokens divided by total workflow tokens.',
    numerator: subagentTokens,
    denominator: totalTokens,
    metadata: {
      rootTokens,
      subagentTokens,
      totalTokens,
      subagentCount,
    },
  }
}

// ---------------------------------------------------------------------------
// Signal 8: Skill Utilisation (skillUtilisation)
// ---------------------------------------------------------------------------

export type SkillUtilisationInput = {
  harness?: string
  referencedSkills?: readonly string[]
  executedSkills?: readonly string[]
  contextContent?: string
  measurability?: Measurability
}

const SKILL_TAG_REGEX = /<skill\s+name=["']([^"']+)["']/gi
const SKILL_COLON_REGEX = /(?:skill|tool_skill):\s*([a-zA-Z0-9_-]+)/gi

/**
 * Evaluates referenced vs executed skills (Decision D16).
 *
 * Rules:
 * - Stamped at low confidence per Decision D16 because no current agent harness
 *   exports explicit skill activation telemetry.
 * - Measurement class is `inferred`.
 * - Ranked last per Decision D6 and D16.
 */
export function skillUtilisation(input: SkillUtilisationInput): SignalResult<number> {
  if (input.measurability) {
    const skillAvail = input.measurability['skills'] ?? input.measurability['skill_utilisation']
    if (isNotMeasurable(skillAvail)) {
      return { status: 'not_measurable', reason: skillAvail.reason }
    }
  }

  let referenced = input.referencedSkills ? [...input.referencedSkills] : []

  // Extract from contextContent if referencedSkills not provided directly
  if (referenced.length === 0 && input.contextContent) {
    const seen = new Set<string>()

    SKILL_TAG_REGEX.lastIndex = 0
    for (const match of input.contextContent.matchAll(SKILL_TAG_REGEX)) {
      if (match[1]) seen.add(match[1].trim())
    }
    SKILL_COLON_REGEX.lastIndex = 0
    for (const match of input.contextContent.matchAll(SKILL_COLON_REGEX)) {
      if (match[1]) seen.add(match[1].trim())
    }
    referenced = [...seen]
  }

  if (referenced.length === 0) {
    return {
      status: 'not_measurable',
      reason: 'No skill references or definitions identified in prompt context.',
    }
  }

  const executedSet = new Set(input.executedSkills?.map((s) => s.trim()) ?? [])
  const credited: string[] = []
  const unexecuted: string[] = []

  for (const ref of referenced) {
    if (executedSet.has(ref)) {
      credited.push(ref)
    } else {
      unexecuted.push(ref)
    }
  }

  const ratio = credited.length / referenced.length
  return {
    status: 'derived',
    value: Math.min(1.0, Math.max(0.0, ratio)),
    measurementClass: 'inferred',
    confidence: 'low',
    confidenceBasis:
      'Stamped at low confidence per Decision D16 because agent harnesses do not export explicit skill activation telemetry.',
    numerator: credited.length,
    denominator: referenced.length,
    metadata: {
      d16RankedLast: true,
      referencedSkills: referenced,
      executedSkills: credited,
      unexecutedSkills: unexecuted,
    },
  }
}

// ---------------------------------------------------------------------------
// Unified Signal Engine Orchestrator
// ---------------------------------------------------------------------------

export type SignalName =
  | 'contextReuse'
  | 'prefixStability'
  | 'toolYield'
  | 'duplicateCallRate'
  | 'oversizedResults'
  | 'compactionPressure'
  | 'delegationOverhead'
  | 'skillUtilisation'

export type SignalEngineResults = {
  contextReuse: SignalResult<number>
  prefixStability: SignalResult<number>
  toolYield: SignalResult<number>
  duplicateCallRate: SignalResult<number>
  oversizedResults: SignalResult<number>
  compactionPressure: SignalResult<number>
  delegationOverhead: SignalResult<number>
  skillUtilisation: SignalResult<number>
}

export type SignalEngineInput = {
  harness?: string
  records?: readonly CanonicalRecord[]
  measurability?: Measurability
  contextLimit?: number
  // Optional granular inputs overriding record derivation
  contextReuse?: ContextReuseInput
  prefixStability?: PrefixStabilityInput
  toolYield?: ToolYieldInput
  duplicateCallRate?: DuplicateCallRateInput
  oversizedResults?: OversizedResultsInput
  compactionPressure?: CompactionPressureInput
  delegationOverhead?: DelegationOverheadInput
  skillUtilisation?: SkillUtilisationInput
}

/**
 * Computes the 8 pure signals for a session or run payload.
 */
export function computeSignals(input: SignalEngineInput): SignalEngineResults {
  const harness = input.harness
  const measurability = input.measurability
  const records = input.records ?? []

  // Derive turn inputs from records if records are provided
  const turnRecords = records.filter((r) => r.op === 'llm.invoke')
  const toolRecords = records.filter((r) => r.op === 'tool.invoke')

  // 1. Context Reuse
  const contextReuseInput: ContextReuseInput = input.contextReuse ?? {
    harness,
    measurability,
    turns: turnRecords.map((r) => ({
      freshInput: r.tokens.freshInput,
      cacheRead: r.tokens.cacheRead,
      cacheCreation: r.tokens.cacheCreation,
      reportedInput: r.tokens.reportedInput,
    })),
  }
  const resContextReuse = contextReuse(contextReuseInput)

  // 2. Prefix Stability
  const prefixStabilityInput: PrefixStabilityInput = input.prefixStability ?? {
    harness,
    measurability,
    turns: turnRecords.map((r, i) => ({
      index: i,
      prefixText: r.content.system_prompt ?? r.content.instruction_context,
      parts: r.parts ? [...r.parts] : undefined,
      tokens: r.tokens,
    })),
    totalInput: turnRecords.reduce((sum, r) => sum + (r.tokens.reportedInput || (r.tokens.freshInput + r.tokens.cacheRead + r.tokens.cacheCreation)), 0),
    cacheRead: turnRecords.reduce((sum, r) => sum + r.tokens.cacheRead, 0),
  }
  const resPrefixStability = prefixStability(prefixStabilityInput)

  // 3. Tool Yield
  const definedTools: string[] = []
  for (const r of records) {
    if (r.parts) {
      for (const p of r.parts) {
        if (p.part === 'tool_definitions' && p.text) {
          try {
            const parsed = JSON.parse(p.text)
            if (Array.isArray(parsed)) {
              for (const item of parsed) {
                if (item?.name) definedTools.push(String(item.name))
              }
            } else if (parsed?.name) {
              definedTools.push(String(parsed.name))
            }
          } catch {
            definedTools.push(p.text.trim())
          }
        }
      }
    }
  }

  const toolYieldInput: ToolYieldInput = input.toolYield ?? {
    harness,
    measurability,
    definedTools: definedTools.length > 0 ? definedTools : undefined,
    invokedTools: toolRecords.map((r) => r.name),
  }
  const resToolYield = toolYield(toolYieldInput)

  // 4. Duplicate Call Rate
  const duplicateCallRateInput: DuplicateCallRateInput = input.duplicateCallRate ?? {
    harness,
    measurability,
    calls: toolRecords.map((r) => ({
      name: r.name,
      arguments: r.raw && typeof r.raw === 'object' ? (r.raw as Record<string, unknown>)['arguments'] : undefined,
      callId: r.spanId,
    })),
  }
  const resDuplicateCallRate = duplicateCallRate(duplicateCallRateInput)

  // 5. Oversized Results
  const resultParts: ToolResultInput[] = []
  for (const r of records) {
    if (r.parts) {
      for (const p of r.parts) {
        if (p.part === 'tool_result_content') {
          resultParts.push({
            toolName: r.name,
            content: p.text,
            tokens: p.tokens,
            characters: p.text.length,
          })
        }
      }
    }
  }

  const oversizedResultsInput: OversizedResultsInput = input.oversizedResults ?? {
    harness,
    measurability,
    results: resultParts.length > 0 ? resultParts : undefined,
  }
  const resOversizedResults = oversizedResults(oversizedResultsInput)

  // 6. Compaction Pressure
  const compactionPressureInput: CompactionPressureInput = input.compactionPressure ?? {
    harness,
    measurability,
    contextLimit: input.contextLimit,
    turns: turnRecords.map((r) => ({
      inputTokens: r.tokens.reportedInput || (r.tokens.freshInput + r.tokens.cacheRead + r.tokens.cacheCreation),
      reportedInput: r.tokens.reportedInput,
    })),
  }
  const resCompactionPressure = compactionPressure(compactionPressureInput)

  // 7. Delegation Overhead
  const delegationOverheadInput: DelegationOverheadInput = input.delegationOverhead ?? {
    harness,
    measurability,
    executions: records.length > 0
      ? [
          {
            isRoot: true,
            tokens: records.reduce((sum, r) => sum + r.tokens.reportedInput + r.tokens.output, 0),
          },
        ]
      : undefined,
  }
  const resDelegationOverhead = delegationOverhead(delegationOverheadInput)

  // 8. Skill Utilisation
  const skillUtilisationInput: SkillUtilisationInput = input.skillUtilisation ?? {
    harness,
    measurability,
    contextContent: turnRecords.map((r) => r.content.system_prompt ?? '').join('\n'),
    executedSkills: toolRecords.map((r) => r.name),
  }
  const resSkillUtilisation = skillUtilisation(skillUtilisationInput)

  return {
    contextReuse: resContextReuse,
    prefixStability: resPrefixStability,
    toolYield: resToolYield,
    duplicateCallRate: resDuplicateCallRate,
    oversizedResults: resOversizedResults,
    compactionPressure: resCompactionPressure,
    delegationOverhead: resDelegationOverhead,
    skillUtilisation: resSkillUtilisation,
  }
}
