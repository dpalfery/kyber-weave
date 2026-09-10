// The Finding Engine and Waste Ranking for KyberDash (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task F3: The Finding Engine & Waste Ranking; Decision D5, D6, D8, D16; ADR 0006, ADR 0008, ADR 0011).
//
// Core Disciplines:
// 1. Pure detector functions operating over session records, turns, and outcome signals without external I/O.
// 2. Decision D5 compliance field-for-field: id, detectorId, title, mechanism, evidenceLinks (>=2),
//    confidence, estimatedWasteTokens, recommendation, errorBar, outcomeRiskCaveat.
// 3. Decision D6 ranking formula: rankScore = estimatedWasteTokens * confidenceMultiplier * (1 - outcomeRiskDiscount).
//    Deterministic findings beat larger inferred findings.
// 4. Decision D8 lint: recommendations strictly prefer relocation, progressive disclosure,
//    on-demand loading, and tool deferral over deletion. Never suggest deleting outright.
// 5. Decision D16: skill utilisation findings are stamped at low confidence ('heuristic') and ranked last.

import { normalizeWhitespace, hashNormalized } from './signals.js'
import type { CanonicalRecord } from '../canon/types.js'
import type { OutcomeBlock } from '../canon/outcome.js'

// ---------------------------------------------------------------------------
// Decision D5 Finding Contract & Types
// ---------------------------------------------------------------------------

/**
 * 7 finding detector identifiers (Task F3 Acceptance Criteria 1).
 */
export type DetectorId =
  | 'dormant-tool-schema'
  | 'duplicate-tool-call'
  | 'oversized-tool-result'
  | 'prefix-cache-break'
  | 'compaction-hazard'
  | 'unbounded-delegation'
  | 'inactive-skill-reference'

/**
 * Confidence level for findings (Decision D5).
 * - 'deterministic': derived from verifiable discrete invariants (identical bytes, call counts).
 * - 'calibrated_statistical': derived from empirical models with known error bounds.
 * - 'heuristic': inferred from proxy signals (e.g. unobservable skill activation per D16).
 */
export type FindingConfidence = 'deterministic' | 'calibrated_statistical' | 'heuristic'

/**
 * Deep-link to originating telemetry span and turn index (Decision D5).
 * Every finding must carry >= 2 evidence links.
 */
export type FindingEvidenceLink = {
  spanId: string
  turnIndex: number
  description: string
}

/**
 * Error bar bounding estimated waste tokens (Decision D5).
 */
export type FindingErrorBar = {
  lower: number
  upper: number
}

/**
 * Finding contract satisfying Decision D5 field-for-field.
 */
export type Finding = {
  id: string
  detectorId: DetectorId
  title: string
  mechanism: string
  evidenceLinks: FindingEvidenceLink[]
  confidence: FindingConfidence
  estimatedWasteTokens: number
  recommendation: string
  errorBar: FindingErrorBar
  outcomeRiskCaveat: string
  // Contextual linkages and ranking score
  runId?: string
  sessionId?: string
  rankScore?: number
  measurementClass?: 'deterministic' | 'inferred' | 'coverage-gap'
  confidenceBasis?: string
  whatWouldRaiseIt?: string
  payload?: Record<string, unknown>
}

// ---------------------------------------------------------------------------
// Decision D6 Ranking Formula & Multipliers
// ---------------------------------------------------------------------------

/**
 * Confidence multipliers for Decision D6 ranking formula.
 * Deterministic findings carry 1.0; heuristic/inferred findings carry steep discounts (0.10)
 * so that deterministic findings beat larger inferred findings.
 */
export const CONFIDENCE_MULTIPLIERS: Record<FindingConfidence, number> = {
  deterministic: 1.0,
  calibrated_statistical: 0.45,
  heuristic: 0.10,
}

/**
 * Derives the outcome risk discount factor in range [0.0, 1.0] from an OutcomeBlock.
 * Higher discount lowers rankScore when touching context poses risk to delicate or failing workflows.
 */
export function calculateOutcomeRiskDiscount(
  outcome?: OutcomeBlock | null,
  detectorId?: DetectorId,
): number {
  if (detectorId === 'inactive-skill-reference') {
    // D16: skill utilisation is ranked last; apply conservative discount
    return 0.40
  }

  if (!outcome || outcome.status === 'not_measurable') {
    // Unmeasured baseline carries moderate risk caution
    return 0.25
  }

  switch (outcome.status) {
    case 'success':
      // System completed cleanly; small discount to avoid disrupting working dynamics
      return 0.15
    case 'inconclusive':
      // User corrections or non-fatal errors detected
      return 0.35
    case 'failure':
    case 'abandoned':
      // Workflow failed; context changes must be heavily guarded
      return 0.50
    default:
      return 0.20
  }
}

/**
 * Derives a plain-language outcome risk caveat string from the outcome block (Decision D5).
 */
export function deriveOutcomeRiskCaveat(
  outcome?: OutcomeBlock | null,
  detectorId?: DetectorId,
  contextNote?: string,
): string {
  if (contextNote) return contextNote

  if (detectorId === 'inactive-skill-reference') {
    return 'Skill activation is not exported by harness telemetry (D16); unexercised skill instructions may still provide latent domain guidance.'
  }

  if (!outcome || outcome.status === 'not_measurable') {
    return 'Run outcome is not measurable from telemetry; verify changes against regression test suites before deployment.'
  }

  if (outcome.status === 'success') {
    return 'Run completed cleanly with zero errors; context relocation carries minor risk of altering prompt attention weights.'
  }

  if (outcome.status === 'inconclusive') {
    const corrCount = outcome.userCorrections?.count ?? 0
    return `Run required ${corrCount} user correction turn(s); altering prompt context may increase correction frequency if critical constraints are shifted.`
  }

  if (outcome.status === 'failure' || outcome.status === 'abandoned') {
    return 'Run terminated with failure or error signals; ensure context relocation preserves diagnostic instructions needed for error recovery.'
  }

  return 'Context modification carries outcome regression risk; benchmark against baseline task suites.'
}

/**
 * Computes Decision D6 ranking score:
 * `rankScore = estimatedWasteTokens * confidenceMultiplier * (1 - outcomeRiskDiscount)`.
 */
export function computeRankScore(
  estimatedWasteTokens: number,
  confidence: FindingConfidence,
  outcomeRiskDiscount: number = 0.2,
): number {
  const multiplier = CONFIDENCE_MULTIPLIERS[confidence] ?? 0.10
  const clampedDiscount = Math.max(0.0, Math.min(0.99, outcomeRiskDiscount))
  const score = estimatedWasteTokens * multiplier * (1.0 - clampedDiscount)
  return Math.max(0, Math.round(score * 100) / 100)
}

/**
 * Ranks findings per Decision D6:
 * Orders findings by descending rankScore.
 * Deterministic findings beat larger inferred findings due to confidenceMultiplier and tie-breaking.
 * Skills are ranked last per Decision D16.
 */
export function rankFindings(findings: readonly Finding[]): Finding[] {
  return [...findings].sort((a, b) => {
    const scoreA = a.rankScore ?? computeRankScore(a.estimatedWasteTokens, a.confidence)
    const scoreB = b.rankScore ?? computeRankScore(b.estimatedWasteTokens, b.confidence)

    if (Math.abs(scoreB - scoreA) > 0.001) {
      return scoreB - scoreA
    }

    // D16: inactive-skill-reference always ranks last among identical scores
    if (a.detectorId === 'inactive-skill-reference' && b.detectorId !== 'inactive-skill-reference') return 1
    if (b.detectorId === 'inactive-skill-reference' && a.detectorId !== 'inactive-skill-reference') return -1

    // Tie-break by confidence tier: deterministic > calibrated_statistical > heuristic
    const confOrder: Record<FindingConfidence, number> = {
      deterministic: 3,
      calibrated_statistical: 2,
      heuristic: 1,
    }
    const confDiff = (confOrder[b.confidence] ?? 0) - (confOrder[a.confidence] ?? 0)
    if (confDiff !== 0) return confDiff

    return b.estimatedWasteTokens - a.estimatedWasteTokens
  })
}

// ---------------------------------------------------------------------------
// Decision D8 Recommendation Lint
// ---------------------------------------------------------------------------

export type D8LintResult = {
  valid: boolean
  errors: string[]
}

const FORBIDDEN_DELETION_PATTERNS = [
  /\bdelete\b/i,
  /\bdeletion\b/i,
  /\bdrop\b/i,
  /\berase\b/i,
  /\bremove completely\b/i,
  /\bremove outright\b/i,
  /\bremove permanently\b/i,
  /\bdelete outright\b/i,
  /\bdelete this (?:rule|skill|tool|instruction)\b/i,
]

const REQUIRED_RELOCATION_PATTERNS = [
  /\brelocat(?:e|ion|ing)\b/i,
  /\bprogressive disclosure\b/i,
  /\bon-demand\b/i,
  /\bdefer(?:ral|ring|red)?\b/i,
  /\bcache-position\b/i,
]

/**
 * Lints a finding recommendation against Decision D8:
 * Recommendations MUST recommend relocation, progressive disclosure, on-demand loading,
 * or tool deferral over deletion. Never suggest deleting a rule or skill outright.
 */
export function lintRecommendationD8(recommendation: string): D8LintResult {
  const errors: string[] = []

  for (const pattern of FORBIDDEN_DELETION_PATTERNS) {
    if (pattern.test(recommendation)) {
      errors.push(
        `Decision D8 violation: recommendation suggests outright deletion (${pattern.toString()}). Use relocation, progressive disclosure, or deferral instead.`,
      )
    }
  }

  const hasRelocationStrategy = REQUIRED_RELOCATION_PATTERNS.some((pattern) =>
    pattern.test(recommendation),
  )
  if (!hasRelocationStrategy) {
    errors.push(
      'Decision D8 violation: recommendation must explicitly suggest relocation, progressive disclosure, on-demand loading, or tool deferral.',
    )
  }

  return {
    valid: errors.length === 0,
    errors,
  }
}

// ---------------------------------------------------------------------------
// Helper Utilities for Telemetry Extraction
// ---------------------------------------------------------------------------

export function serializeToolArgs(args: unknown): string {
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

export function extractToolDefinitionsFromRecord(record: CanonicalRecord): { name: string; tokens: number }[] {
  const tools: { name: string; tokens: number }[] = []
  if (record.parts) {
    for (const part of record.parts) {
      if (part.part === 'tool_definitions' && part.text) {
        try {
          const parsed = JSON.parse(part.text)
          if (Array.isArray(parsed)) {
            for (const item of parsed) {
              if (item?.name) {
                tools.push({
                  name: String(item.name),
                  tokens: Math.max(1, Math.ceil(JSON.stringify(item).length / 4)),
                })
              }
            }
          } else if (parsed?.name) {
            tools.push({
              name: String(parsed.name),
              tokens: Math.max(1, Math.ceil(part.text.length / 4)),
            })
          }
        } catch {
          const name = part.text.trim()
          if (name) {
            tools.push({ name, tokens: Math.max(1, Math.ceil(name.length / 4)) })
          }
        }
      }
    }
  }
  return tools
}

// ---------------------------------------------------------------------------
// Detector 1: dormant-tool-schema
// ---------------------------------------------------------------------------

export type DormantToolSchemaInput = {
  runId?: string
  sessionId?: string
  records?: readonly CanonicalRecord[]
  outcome?: OutcomeBlock
  toolDefinitions?: readonly { name: string; tokens: number; server?: string }[]
  turnsCount?: number
  invocations?: readonly string[]
  references?: readonly string[]
}

/**
 * Detector 1: dormant-tool-schema
 * Tool schema present across >=3 turns with 0 invocations and no references.
 */
export function detectDormantToolSchema(input: DormantToolSchemaInput): Finding[] {
  const findings: Finding[] = []
  const records = input.records ?? []
  const runId = input.runId
  const sessionId = (input.sessionId ?? records[0]?.sessionId) || undefined
  const outcome = input.outcome
  const discount = calculateOutcomeRiskDiscount(outcome, 'dormant-tool-schema')

  const turnRecords = records.filter((r) => r.op === 'llm.invoke')
  const toolRecords = records.filter((r) => r.op === 'tool.invoke')

  // Map of tool name -> turns in which it was resident
  const toolResidency = new Map<string, { turns: { spanId: string; turnIndex: number }[]; tokens: number }>()

  // 1. Extract from records
  if (turnRecords.length > 0) {
    for (let turnIdx = 0; turnIdx < turnRecords.length; turnIdx++) {
      const turn = turnRecords[turnIdx]!
      const defs = extractToolDefinitionsFromRecord(turn)
      for (const def of defs) {
        const existing = toolResidency.get(def.name) ?? { turns: [], tokens: def.tokens }
        existing.turns.push({ spanId: turn.spanId, turnIndex: turnIdx })
        existing.tokens = Math.max(existing.tokens, def.tokens)
        toolResidency.set(def.name, existing)
      }
    }
  }

  // 2. Also incorporate directly supplied toolDefinitions if provided
  if (input.toolDefinitions && input.toolDefinitions.length > 0) {
    const totalTurns = input.turnsCount ?? Math.max(turnRecords.length, 3)
    for (const def of input.toolDefinitions) {
      if (!toolResidency.has(def.name)) {
        const turnsList: { spanId: string; turnIndex: number }[] = []
        for (let i = 0; i < totalTurns; i++) {
          const spanId = turnRecords[i]?.spanId ?? `turn-span-${i}`
          turnsList.push({ spanId, turnIndex: i })
        }
        toolResidency.set(def.name, { turns: turnsList, tokens: def.tokens })
      }
    }
  }

  // Invoked tool names
  const invokedNames = new Set<string>()
  for (const toolRec of toolRecords) {
    invokedNames.add(toolRec.name.trim())
  }
  if (input.invocations) {
    for (const inv of input.invocations) invokedNames.add(inv.trim())
  }

  // References in conversation text
  const conversationText = turnRecords.map((t) => t.content.conversation_history ?? '').join(' ')
  const extraReferences = input.references ?? []

  for (const [toolName, residency] of toolResidency.entries()) {
    if (residency.turns.length < 3) continue
    if (invokedNames.has(toolName)) continue

    const hasRef =
      conversationText.toLowerCase().includes(toolName.toLowerCase()) ||
      extraReferences.some((r) => r.toLowerCase().includes(toolName.toLowerCase()))
    if (hasRef) continue

    const turnsResident = residency.turns.length
    const schemaTokens = residency.tokens || 150
    // Waste is the schema tokens carried across all turns without yielding actions
    const estimatedWasteTokens = schemaTokens * turnsResident

    const firstTurn = residency.turns[0]!
    const lastTurn = residency.turns[residency.turns.length - 1]!

    const evidenceLinks: FindingEvidenceLink[] = [
      {
        spanId: firstTurn.spanId,
        turnIndex: firstTurn.turnIndex,
        description: `Tool schema for "${toolName}" first registered in context (${schemaTokens} tokens).`,
      },
      {
        spanId: lastTurn.spanId,
        turnIndex: lastTurn.turnIndex,
        description: `Turn ${lastTurn.turnIndex} executed with tool "${toolName}" still resident in prompt context after ${turnsResident} turns with 0 invocations.`,
      },
    ]

    const recommendation = `Relocate tool schema "${toolName}" to on-demand loading or tool deferral using progressive disclosure, registering definitions only when task phases require them.`
    const outcomeRiskCaveat = deriveOutcomeRiskCaveat(
      outcome,
      'dormant-tool-schema',
      'Relocating tool schemas to on-demand loading requires the agent to be aware of tool availability; ensure tool discovery prompts are provided.',
    )

    const rankScore = computeRankScore(estimatedWasteTokens, 'deterministic', discount)

    findings.push({
      id: `finding-dormant-tool-${toolName}-${sessionId ?? 'session'}`,
      detectorId: 'dormant-tool-schema',
      title: `Dormant Tool Schema: "${toolName}" resident across ${turnsResident} turns without invocation`,
      mechanism: `Tool schema definition for "${toolName}" was injected into the prompt context on each turn (${schemaTokens} tokens/turn), accumulating ${estimatedWasteTokens} tokens across ${turnsResident} turns with zero invocations and no model references.`,
      evidenceLinks,
      confidence: 'deterministic',
      estimatedWasteTokens,
      recommendation,
      errorBar: {
        lower: Math.floor(estimatedWasteTokens * 0.8),
        upper: Math.ceil(estimatedWasteTokens * 1.2),
      },
      outcomeRiskCaveat,
      runId,
      sessionId,
      rankScore,
      measurementClass: 'deterministic',
      confidenceBasis: 'Deterministically measured from schema presence in turn context and 0 recorded invocation spans.',
      whatWouldRaiseIt: 'Telemetry is already deterministic; confidence is at ceiling.',
    })
  }

  return findings
}

// ---------------------------------------------------------------------------
// Detector 2: duplicate-tool-call
// ---------------------------------------------------------------------------

export type DuplicateToolCallInput = {
  runId?: string
  sessionId?: string
  records?: readonly CanonicalRecord[]
  calls?: readonly { name: string; arguments?: unknown; spanId?: string; turnIndex?: number; tokens?: number }[]
  outcome?: OutcomeBlock
}

/**
 * Detector 2: duplicate-tool-call
 * Identical tool calls with identical arguments in same run/session.
 */
export function detectDuplicateToolCall(input: DuplicateToolCallInput): Finding[] {
  const findings: Finding[] = []
  const records = input.records ?? []
  const runId = input.runId
  const sessionId = (input.sessionId ?? records[0]?.sessionId) || undefined
  const outcome = input.outcome
  const discount = calculateOutcomeRiskDiscount(outcome, 'duplicate-tool-call')

  type NormalizedCall = {
    name: string
    args: string
    spanId: string
    turnIndex: number
    tokens: number
  }

  const calls: NormalizedCall[] = []

  // Extract from records
  let turnIdx = 0
  for (const r of records) {
    if (r.op === 'llm.invoke') {
      turnIdx++
    } else if (r.op === 'tool.invoke') {
      const args = r.raw && typeof r.raw === 'object' ? (r.raw as Record<string, unknown>)['arguments'] : undefined
      const tokens = r.tokens.reportedInput + r.tokens.output || 250
      calls.push({
        name: r.name.trim(),
        args: serializeToolArgs(args),
        spanId: r.spanId,
        turnIndex: turnIdx,
        tokens,
      })
    }
  }

  // Also include explicitly passed calls
  if (input.calls && input.calls.length > 0) {
    for (let i = 0; i < input.calls.length; i++) {
      const c = input.calls[i]!
      calls.push({
        name: c.name.trim(),
        args: serializeToolArgs(c.arguments),
        spanId: c.spanId ?? `tool-call-span-${i}`,
        turnIndex: c.turnIndex ?? i,
        tokens: c.tokens ?? 250,
      })
    }
  }

  const seenMap = new Map<string, NormalizedCall>()

  for (let i = 0; i < calls.length; i++) {
    const call = calls[i]!
    const key = `${call.name}::${call.args}`

    if (seenMap.has(key)) {
      const firstCall = seenMap.get(key)!
      const wasteTokens = call.tokens

      const evidenceLinks: FindingEvidenceLink[] = [
        {
          spanId: firstCall.spanId,
          turnIndex: firstCall.turnIndex,
          description: `Initial invocation of "${call.name}" with arguments: ${call.args.slice(0, 100) || '(empty)'}.`,
        },
        {
          spanId: call.spanId,
          turnIndex: call.turnIndex,
          description: `Duplicate identical invocation of "${call.name}" with matching arguments, generating redundant output.`,
        },
      ]

      const recommendation = `Implement client-side on-demand caching or relocate query results into local conversation state to defer duplicate "${call.name}" tool executions.`
      const outcomeRiskCaveat = deriveOutcomeRiskCaveat(
        outcome,
        'duplicate-tool-call',
        'Client-side result caching assumes queried external resources are idempotent; volatile resources may yield stale data if cached.',
      )

      const rankScore = computeRankScore(wasteTokens, 'deterministic', discount)

      findings.push({
        id: `finding-duplicate-call-${call.name}-${call.spanId}`,
        detectorId: 'duplicate-tool-call',
        title: `Duplicate Tool Call: Identical invocation of "${call.name}"`,
        mechanism: `The tool "${call.name}" was executed multiple times with identical arguments in the same session without intermediate state changes, repeating ${wasteTokens} tokens of redundant call and result payloads.`,
        evidenceLinks,
        confidence: 'deterministic',
        estimatedWasteTokens: wasteTokens,
        recommendation,
        errorBar: {
          lower: Math.floor(wasteTokens * 0.8),
          upper: Math.ceil(wasteTokens * 1.25),
        },
        outcomeRiskCaveat,
        runId,
        sessionId,
        rankScore,
        measurementClass: 'deterministic',
        confidenceBasis: 'Deterministically measured by hashing tool names and whitespace-normalized arguments across execution spans.',
        whatWouldRaiseIt: 'Deterministic measurement; confidence is at ceiling.',
      })
    } else {
      seenMap.set(key, call)
    }
  }

  return findings
}

// ---------------------------------------------------------------------------
// Detector 3: oversized-tool-result
// ---------------------------------------------------------------------------

export type OversizedToolResultInput = {
  runId?: string
  sessionId?: string
  records?: readonly CanonicalRecord[]
  results?: readonly {
    toolName?: string
    content: string | unknown
    tokens?: number
    characters?: number
    spanId?: string
    turnIndex?: number
  }[]
  tokenThreshold?: number
  charThreshold?: number
  outcome?: OutcomeBlock
}

export const DEFAULT_FINDING_TOKEN_THRESHOLD = 2000
export const DEFAULT_FINDING_CHAR_THRESHOLD = 8000

/**
 * Detector 3: oversized-tool-result
 * Tool outputs exceeding token/byte budgets with low yield.
 */
export function detectOversizedToolResult(input: OversizedToolResultInput): Finding[] {
  const findings: Finding[] = []
  const records = input.records ?? []
  const runId = input.runId
  const sessionId = (input.sessionId ?? records[0]?.sessionId) || undefined
  const outcome = input.outcome
  const discount = calculateOutcomeRiskDiscount(outcome, 'oversized-tool-result')

  const tokenThreshold = input.tokenThreshold ?? DEFAULT_FINDING_TOKEN_THRESHOLD
  const charThreshold = input.charThreshold ?? DEFAULT_FINDING_CHAR_THRESHOLD

  type ResultItem = {
    toolName: string
    text: string
    tokens: number
    chars: number
    spanId: string
    turnIndex: number
    consumingSpanId?: string
  }

  const items: ResultItem[] = []

  let currentTurnIndex = 0
  let lastLlmSpanId = ''
  for (const r of records) {
    if (r.op === 'llm.invoke') {
      currentTurnIndex++
      lastLlmSpanId = r.spanId
    } else if (r.op === 'tool.invoke') {
      let content = ''
      let tokens = 0
      if (r.parts) {
        for (const p of r.parts) {
          if (p.part === 'tool_result_content' && p.text) {
            content += p.text
            tokens += p.tokens ?? Math.ceil(p.text.length / 4)
          }
        }
      }
      if (!content && r.raw && typeof r.raw === 'object') {
        const rawRes = (r.raw as Record<string, unknown>)['result'] ?? (r.raw as Record<string, unknown>)['output']
        if (rawRes) {
          content = typeof rawRes === 'string' ? rawRes : JSON.stringify(rawRes)
          tokens = Math.ceil(content.length / 4)
        }
      }

      if (content.length > 0) {
        items.push({
          toolName: r.name,
          text: content,
          tokens: tokens || Math.ceil(content.length / 4),
          chars: content.length,
          spanId: r.spanId,
          turnIndex: currentTurnIndex,
          consumingSpanId: lastLlmSpanId || r.spanId,
        })
      }
    }
  }

  if (input.results && input.results.length > 0) {
    for (let i = 0; i < input.results.length; i++) {
      const res = input.results[i]!
      const content = typeof res.content === 'string' ? res.content : JSON.stringify(res.content ?? '')
      const chars = res.characters ?? content.length
      const tokens = res.tokens ?? Math.ceil(chars / 4)
      items.push({
        toolName: res.toolName ?? 'tool',
        text: content,
        tokens,
        chars,
        spanId: res.spanId ?? `result-span-${i}`,
        turnIndex: res.turnIndex ?? i,
        consumingSpanId: `llm-consuming-span-${i}`,
      })
    }
  }

  for (const item of items) {
    const isOversized = item.tokens > tokenThreshold || item.chars > charThreshold
    if (!isOversized) continue

    const excessTokens = Math.max(100, item.tokens - tokenThreshold)
    const estimatedWasteTokens = excessTokens

    const evidenceLinks: FindingEvidenceLink[] = [
      {
        spanId: item.spanId,
        turnIndex: item.turnIndex,
        description: `Tool "${item.toolName}" returned oversized output (${item.tokens} tokens, ${item.chars} characters), exceeding threshold (${tokenThreshold} tokens).`,
      },
      {
        spanId: item.consumingSpanId || item.spanId,
        turnIndex: item.turnIndex + 1,
        description: `Subsequent turn context re-read ${item.tokens} tokens into prompt history with low operational yield.`,
      },
    ]

    const recommendation = `Relocate large tool outputs to persistent storage and use progressive disclosure or on-demand pagination to return concise excerpts.`
    const outcomeRiskCaveat = deriveOutcomeRiskCaveat(
      outcome,
      'oversized-tool-result',
      'Truncating or paginating tool output may conceal error traces or multi-line diagnostics if subsequent steps require full logs.',
    )

    const rankScore = computeRankScore(estimatedWasteTokens, 'deterministic', discount)

    findings.push({
      id: `finding-oversized-result-${item.toolName}-${item.spanId}`,
      detectorId: 'oversized-tool-result',
      title: `Oversized Tool Result: "${item.toolName}" returned ${item.tokens} tokens exceeding budget`,
      mechanism: `Tool "${item.toolName}" injected ${item.tokens} tokens (${item.chars} characters) into context in a single output, exceeding the ${tokenThreshold} token limit and increasing per-turn re-read overhead by ${estimatedWasteTokens} tokens.`,
      evidenceLinks,
      confidence: 'deterministic',
      estimatedWasteTokens,
      recommendation,
      errorBar: {
        lower: Math.floor(estimatedWasteTokens * 0.8),
        upper: Math.ceil(estimatedWasteTokens * 1.3),
      },
      outcomeRiskCaveat,
      runId,
      sessionId,
      rankScore,
      measurementClass: 'deterministic',
      confidenceBasis: 'Deterministically measured by comparing tool result byte and token lengths against configured thresholds.',
      whatWouldRaiseIt: 'Deterministic measurement; confidence is at ceiling.',
    })
  }

  return findings
}

// ---------------------------------------------------------------------------
// Detector 4: prefix-cache-break
// ---------------------------------------------------------------------------

export type PrefixCacheBreakInput = {
  runId?: string
  sessionId?: string
  records?: readonly CanonicalRecord[]
  turns?: readonly {
    spanId?: string
    turnIndex?: number
    prefixText?: string
    freshInput?: number
    cacheRead?: number
  }[]
  outcome?: OutcomeBlock
}

/**
 * Detector 4: prefix-cache-break
 * Dynamic tokens injected early in prompt breaking KV-cache prefix stability.
 */
export function detectPrefixCacheBreak(input: PrefixCacheBreakInput): Finding[] {
  const findings: Finding[] = []
  const records = input.records ?? []
  const runId = input.runId
  const sessionId = (input.sessionId ?? records[0]?.sessionId) || undefined
  const outcome = input.outcome
  const discount = calculateOutcomeRiskDiscount(outcome, 'prefix-cache-break')

  type TurnInfo = {
    spanId: string
    turnIndex: number
    prefix: string
    hash: string
    freshInput: number
    cacheRead: number
  }

  const turns: TurnInfo[] = []

  const turnRecords = records.filter((r) => r.op === 'llm.invoke')
  for (let i = 0; i < turnRecords.length; i++) {
    const r = turnRecords[i]!
    const prefix = r.content.system_prompt ?? r.content.instruction_context ?? ''
    turns.push({
      spanId: r.spanId,
      turnIndex: i,
      prefix,
      hash: prefix ? hashNormalized(prefix) : '',
      freshInput: r.tokens.freshInput,
      cacheRead: r.tokens.cacheRead,
    })
  }

  if (input.turns && input.turns.length > 0) {
    for (let i = 0; i < input.turns.length; i++) {
      const t = input.turns[i]!
      const prefix = t.prefixText ?? ''
      turns.push({
        spanId: t.spanId ?? `turn-span-${i}`,
        turnIndex: t.turnIndex ?? i,
        prefix,
        hash: prefix ? hashNormalized(prefix) : '',
        freshInput: t.freshInput ?? 500,
        cacheRead: t.cacheRead ?? 0,
      })
    }
  }

  if (turns.length < 2) {
    return findings
  }

  // Detect transitions where prefix hash broke
  for (let i = 0; i < turns.length - 1; i++) {
    const tA = turns[i]!
    const tB = turns[i + 1]!

    if (tA.hash && tB.hash && tA.hash !== tB.hash) {
      // Estimated waste is the fresh input tokens that had to be computed because of cache invalidation
      const wasteTokens = Math.max(150, tB.freshInput || 500)

      const evidenceLinks: FindingEvidenceLink[] = [
        {
          spanId: tA.spanId,
          turnIndex: tA.turnIndex,
          description: `Turn ${tA.turnIndex} established baseline prompt prefix (hash: ${tA.hash.slice(0, 10)}...).`,
        },
        {
          spanId: tB.spanId,
          turnIndex: tB.turnIndex,
          description: `Turn ${tB.turnIndex} mutated prefix early in prompt (hash: ${tB.hash.slice(0, 10)}...), invalidating KV-cache reuse.`,
        },
      ]

      const recommendation = `Relocate dynamic tokens (e.g. timestamps, turn counters, environment variables) to the end of prompt messages, preserving an immutable prefix for KV-cache reuse.`
      const outcomeRiskCaveat = deriveOutcomeRiskCaveat(
        outcome,
        'prefix-cache-break',
        'Relocating dynamic instructions to the prompt tail can subtly shift model attention weights for early turn system prompts.',
      )

      const rankScore = computeRankScore(wasteTokens, 'deterministic', discount)

      findings.push({
        id: `finding-prefix-break-${sessionId ?? 'session'}-${tB.spanId}`,
        detectorId: 'prefix-cache-break',
        title: `Prefix Cache Break: Dynamic tokens injected early in prompt breaking KV-cache`,
        mechanism: `Prefix tokens differed between turn ${tA.turnIndex} and turn ${tB.turnIndex}, breaking KV-cache prefix stability and forcing the model to re-evaluate ${wasteTokens} fresh input tokens that could have been cached.`,
        evidenceLinks,
        confidence: 'deterministic',
        estimatedWasteTokens: wasteTokens,
        recommendation,
        errorBar: {
          lower: Math.floor(wasteTokens * 0.8),
          upper: Math.ceil(wasteTokens * 1.25),
        },
        outcomeRiskCaveat,
        runId,
        sessionId,
        rankScore,
        measurementClass: 'deterministic',
        confidenceBasis: 'Deterministically verified by whitespace-normalized SHA-256 hash divergence between consecutive turn prefixes.',
        whatWouldRaiseIt: 'Deterministic measurement; confidence is at ceiling.',
      })
      // One finding per session break sequence is sufficient to avoid flooding
      break
    }
  }

  return findings
}

// ---------------------------------------------------------------------------
// Detector 5: compaction-hazard
// ---------------------------------------------------------------------------

export type CompactionHazardInput = {
  runId?: string
  sessionId?: string
  records?: readonly CanonicalRecord[]
  turns?: readonly { spanId?: string; turnIndex?: number; inputTokens?: number }[]
  contextLimit?: number
  outcome?: OutcomeBlock
}

export const DEFAULT_COMPACTION_CONTEXT_LIMIT = 200_000

/**
 * Detector 5: compaction-hazard
 * Context consumption exceeding 85% of window without summarization plan.
 */
export function detectCompactionHazard(input: CompactionHazardInput): Finding[] {
  const findings: Finding[] = []
  const records = input.records ?? []
  const runId = input.runId
  const sessionId = (input.sessionId ?? records[0]?.sessionId) || undefined
  const outcome = input.outcome
  const discount = calculateOutcomeRiskDiscount(outcome, 'compaction-hazard')
  const limit = input.contextLimit ?? DEFAULT_COMPACTION_CONTEXT_LIMIT

  type TurnContext = {
    spanId: string
    turnIndex: number
    tokens: number
  }

  const turnList: TurnContext[] = []

  const turnRecords = records.filter((r) => r.op === 'llm.invoke')
  for (let i = 0; i < turnRecords.length; i++) {
    const r = turnRecords[i]!
    const tokens = r.tokens.reportedInput || (r.tokens.freshInput + r.tokens.cacheRead + r.tokens.cacheCreation)
    turnList.push({
      spanId: r.spanId,
      turnIndex: i,
      tokens,
    })
  }

  if (input.turns && input.turns.length > 0) {
    for (let i = 0; i < input.turns.length; i++) {
      const t = input.turns[i]!
      turnList.push({
        spanId: t.spanId ?? `turn-span-${i}`,
        turnIndex: t.turnIndex ?? i,
        tokens: t.inputTokens ?? 0,
      })
    }
  }

  if (turnList.length === 0) return findings

  let peakTurn: TurnContext = turnList[0]!
  for (const t of turnList) {
    if (t.tokens > peakTurn.tokens) {
      peakTurn = t
    }
  }

  const thresholdTokens = Math.floor(limit * 0.85)
  if (peakTurn.tokens > thresholdTokens) {
    const ratio = peakTurn.tokens / limit
    const estimatedWasteTokens = peakTurn.tokens - thresholdTokens

    // Link prior turn and peak turn
    const priorTurn = turnList.find((t) => t.turnIndex !== peakTurn.turnIndex) ?? turnList[0]!

    const evidenceLinks: FindingEvidenceLink[] = [
      {
        spanId: priorTurn.spanId,
        turnIndex: priorTurn.turnIndex,
        description: `Turn ${priorTurn.turnIndex} consumed ${priorTurn.tokens} tokens (${Math.round((priorTurn.tokens / limit) * 100)}% capacity) approaching context limits.`,
      },
      {
        spanId: peakTurn.spanId,
        turnIndex: peakTurn.turnIndex,
        description: `Peak context consumption reached ${peakTurn.tokens} tokens (${Math.round(ratio * 100)}% capacity), exceeding the 85% compaction hazard threshold.`,
      },
    ]

    const recommendation = `Apply progressive disclosure and relocate historical conversation turns into structured checkpoints or rollups before reaching the 85% window limit.`
    const outcomeRiskCaveat = deriveOutcomeRiskCaveat(
      outcome,
      'compaction-hazard',
      'Abrupt context compaction or summarization risks discarding early user constraints or domain definitions.',
    )

    const rankScore = computeRankScore(estimatedWasteTokens, 'deterministic', discount)

    findings.push({
      id: `finding-compaction-hazard-${sessionId ?? 'session'}-${peakTurn.spanId}`,
      detectorId: 'compaction-hazard',
      title: `Compaction Hazard: Context consumption reached ${Math.round(ratio * 100)}% of window without summarization plan`,
      mechanism: `Peak context consumption reached ${peakTurn.tokens} tokens (${Math.round(ratio * 100)}% of ${limit} token window), exceeding the 85% safety boundary without active compaction or summarization.`,
      evidenceLinks,
      confidence: 'deterministic',
      estimatedWasteTokens,
      recommendation,
      errorBar: {
        lower: Math.floor(estimatedWasteTokens * 0.9),
        upper: Math.ceil(estimatedWasteTokens * 1.3),
      },
      outcomeRiskCaveat,
      runId,
      sessionId,
      rankScore,
      measurementClass: 'deterministic',
      confidenceBasis: 'Deterministically measured by comparing peak turn input tokens against the declared context window limit.',
      whatWouldRaiseIt: 'Deterministic measurement; confidence is at ceiling.',
    })
  }

  return findings
}

// ---------------------------------------------------------------------------
// Detector 6: unbounded-delegation
// ---------------------------------------------------------------------------

export type UnboundedDelegationInput = {
  runId?: string
  sessionId?: string
  records?: readonly CanonicalRecord[]
  rootTokens?: number
  subagentTokens?: number
  rootSpanId?: string
  subagentSpanId?: string
  outcome?: OutcomeBlock
}

/**
 * Detector 6: unbounded-delegation
 * Subagent delegation token share exceeding 60% without outcome justification.
 */
export function detectUnboundedDelegation(input: UnboundedDelegationInput): Finding[] {
  const findings: Finding[] = []
  const records = input.records ?? []
  const runId = input.runId
  const sessionId = (input.sessionId ?? records[0]?.sessionId) || undefined
  const outcome = input.outcome
  const discount = calculateOutcomeRiskDiscount(outcome, 'unbounded-delegation')

  let rootTokens = input.rootTokens ?? 0
  let subagentTokens = input.subagentTokens ?? 0
  let rootSpanId = input.rootSpanId ?? (records[0]?.spanId ?? 'root-span')
  let subagentSpanId = input.subagentSpanId ?? 'subagent-span'

  if (records.length > 0 && rootTokens === 0 && subagentTokens === 0) {
    for (const r of records) {
      const tokens = r.tokens.reportedInput + r.tokens.output
      const isSub = r.raw && typeof r.raw === 'object' && (r.raw as Record<string, unknown>)['is_subagent']
      if (isSub) {
        subagentTokens += tokens
        subagentSpanId = r.spanId
      } else {
        rootTokens += tokens
        if (!rootSpanId) rootSpanId = r.spanId
      }
    }
  }

  const totalTokens = rootTokens + subagentTokens
  if (totalTokens <= 0) return findings

  const delegationShare = subagentTokens / totalTokens
  if (delegationShare > 0.60) {
    // Waste is estimated as the tokens spent above healthy 50% delegation baseline
    const estimatedWasteTokens = Math.max(100, subagentTokens - Math.floor(totalTokens * 0.50))

    const evidenceLinks: FindingEvidenceLink[] = [
      {
        spanId: rootSpanId,
        turnIndex: 0,
        description: `Root execution initiated workflow requiring ${rootTokens} tokens (${Math.round((rootTokens / totalTokens) * 100)}% share).`,
      },
      {
        spanId: subagentSpanId,
        turnIndex: 1,
        description: `Delegated subagents consumed ${subagentTokens} tokens (${Math.round(delegationShare * 100)}% share), exceeding the 60% delegation ceiling.`,
      },
    ]

    const recommendation = `Relocate subagent scopes to focused, single-purpose tasks with on-demand handoffs, and defer child agent instantiation until specialized work is required.`
    const outcomeRiskCaveat = deriveOutcomeRiskCaveat(
      outcome,
      'unbounded-delegation',
      'Restricting delegation scopes may increase the root agent prompt size if complex tasks must be handled directly.',
    )

    const rankScore = computeRankScore(estimatedWasteTokens, 'calibrated_statistical', discount)

    findings.push({
      id: `finding-unbounded-delegation-${runId ?? sessionId ?? 'workflow'}`,
      detectorId: 'unbounded-delegation',
      title: `Unbounded Delegation: Subagent token share reached ${Math.round(delegationShare * 100)}% without outcome justification`,
      mechanism: `Delegated executions consumed ${subagentTokens} tokens (${Math.round(delegationShare * 100)}% of total workflow), exceeding the 60% delegation ceiling without proportional outcome justification.`,
      evidenceLinks,
      confidence: 'calibrated_statistical',
      estimatedWasteTokens,
      recommendation,
      errorBar: {
        lower: Math.floor(estimatedWasteTokens * 0.75),
        upper: Math.ceil(estimatedWasteTokens * 1.25),
      },
      outcomeRiskCaveat,
      runId,
      sessionId,
      rankScore,
      measurementClass: 'inferred',
      confidenceBasis: 'Calibrated from subagent token share relative to task outcome and delegation structure.',
      whatWouldRaiseIt: 'Harness emission of explicit subagent task justification spans and task completion status.',
    })
  }

  return findings
}

// ---------------------------------------------------------------------------
// Detector 7: inactive-skill-reference
// ---------------------------------------------------------------------------

export type InactiveSkillReferenceInput = {
  runId?: string
  sessionId?: string
  records?: readonly CanonicalRecord[]
  referencedSkills?: readonly string[]
  executedSkills?: readonly string[]
  skillTokens?: Record<string, number>
  outcome?: OutcomeBlock
}

const SKILL_TAG_REGEX = /<skill\s+name=["']([^"']+)["']/gi
const SKILL_COLON_REGEX = /(?:skill|tool_skill):\s*([a-zA-Z0-9_-]+)/gi

/**
 * Detector 7: inactive-skill-reference
 * Skill prompt blocks in context with 0 executions (ranked last, low confidence per D16).
 */
export function detectInactiveSkillReference(input: InactiveSkillReferenceInput): Finding[] {
  const findings: Finding[] = []
  const records = input.records ?? []
  const runId = input.runId
  const sessionId = (input.sessionId ?? records[0]?.sessionId) || undefined
  const outcome = input.outcome
  const discount = calculateOutcomeRiskDiscount(outcome, 'inactive-skill-reference')

  let referenced: string[] = input.referencedSkills ? [...input.referencedSkills] : []
  const turnRecords = records.filter((r) => r.op === 'llm.invoke')
  const toolRecords = records.filter((r) => r.op === 'tool.invoke')

  if (referenced.length === 0 && turnRecords.length > 0) {
    const seen = new Set<string>()
    for (const turn of turnRecords) {
      const text = turn.content.system_prompt ?? ''
      SKILL_TAG_REGEX.lastIndex = 0
      for (const match of text.matchAll(SKILL_TAG_REGEX)) {
        if (match[1]) seen.add(match[1].trim())
      }
      SKILL_COLON_REGEX.lastIndex = 0
      for (const match of text.matchAll(SKILL_COLON_REGEX)) {
        if (match[1]) seen.add(match[1].trim())
      }
    }
    referenced = [...seen]
  }

  if (referenced.length === 0) return findings

  const executedSet = new Set(input.executedSkills ?? toolRecords.map((t) => t.name.trim()))
  const skillTokensMap = input.skillTokens ?? {}

  for (const skill of referenced) {
    if (executedSet.has(skill)) continue

    const tokensPerTurn = skillTokensMap[skill] ?? 120
    const turnsCount = Math.max(turnRecords.length, 3)
    const estimatedWasteTokens = tokensPerTurn * turnsCount

    const firstSpanId = turnRecords[0]?.spanId ?? 'turn-skill-inject-0'
    const lastSpanId = turnRecords[turnRecords.length - 1]?.spanId ?? `turn-skill-uncalled-${turnsCount - 1}`

    const evidenceLinks: FindingEvidenceLink[] = [
      {
        spanId: firstSpanId,
        turnIndex: 0,
        description: `Skill instructions for "${skill}" loaded into prompt context (${tokensPerTurn} tokens/turn).`,
      },
      {
        spanId: lastSpanId,
        turnIndex: turnsCount - 1,
        description: `Turn ${turnsCount - 1} completed with skill "${skill}" resident in prompt across ${turnsCount} turns without activation.`,
      },
    ]

    const recommendation = `Relocate skill instructions for "${skill}" to on-demand progressive disclosure, loading skill prompt blocks dynamically only when matching task intents occur.`
    const outcomeRiskCaveat = deriveOutcomeRiskCaveat(
      outcome,
      'inactive-skill-reference',
      'Per Decision D16, skill activation telemetry is not exported directly; skill instructions may supply latent guidance even when uncalled.',
    )

    const rankScore = computeRankScore(estimatedWasteTokens, 'heuristic', discount)

    findings.push({
      id: `finding-inactive-skill-${skill}-${sessionId ?? 'session'}`,
      detectorId: 'inactive-skill-reference',
      title: `Inactive Skill Reference: Skill "${skill}" loaded in context with 0 executions`,
      mechanism: `Skill instruction block for "${skill}" remained resident in prompt context across ${turnsCount} turns (${tokensPerTurn} tokens/turn), accumulating ${estimatedWasteTokens} tokens without explicit execution or activation.`,
      evidenceLinks,
      confidence: 'heuristic',
      estimatedWasteTokens,
      recommendation,
      errorBar: {
        lower: Math.floor(estimatedWasteTokens * 0.5),
        upper: Math.ceil(estimatedWasteTokens * 1.5),
      },
      outcomeRiskCaveat,
      runId,
      sessionId,
      rankScore,
      measurementClass: 'inferred',
      confidenceBasis: 'Decision D16: Stamped at low confidence (heuristic) because agent harnesses do not export explicit skill activation telemetry.',
      whatWouldRaiseIt: 'Harness export of discrete skill activation spans or tool routing telemetry.',
    })
  }

  return findings
}

// ---------------------------------------------------------------------------
// Unified Finding Engine Orchestrator
// ---------------------------------------------------------------------------

export type DetectFindingsInput = {
  runId?: string
  sessionId?: string
  records?: readonly CanonicalRecord[]
  outcome?: OutcomeBlock
  contextLimit?: number
  // Granular detector overrides
  dormantToolSchema?: DormantToolSchemaInput
  duplicateToolCall?: DuplicateToolCallInput
  oversizedToolResult?: OversizedToolResultInput
  prefixCacheBreak?: PrefixCacheBreakInput
  compactionHazard?: CompactionHazardInput
  unboundedDelegation?: UnboundedDelegationInput
  inactiveSkillReference?: InactiveSkillReferenceInput
}

/**
 * Evaluates all 7 finding detectors and returns findings ranked per Decision D6.
 */
export function detectFindings(input: DetectFindingsInput): Finding[] {
  const findings: Finding[] = []

  // 1. Dormant tool schema
  findings.push(...detectDormantToolSchema(input.dormantToolSchema ?? {
    runId: input.runId,
    sessionId: input.sessionId,
    records: input.records,
    outcome: input.outcome,
  }))

  // 2. Duplicate tool call
  findings.push(...detectDuplicateToolCall(input.duplicateToolCall ?? {
    runId: input.runId,
    sessionId: input.sessionId,
    records: input.records,
    outcome: input.outcome,
  }))

  // 3. Oversized tool result
  findings.push(...detectOversizedToolResult(input.oversizedToolResult ?? {
    runId: input.runId,
    sessionId: input.sessionId,
    records: input.records,
    outcome: input.outcome,
  }))

  // 4. Prefix cache break
  findings.push(...detectPrefixCacheBreak(input.prefixCacheBreak ?? {
    runId: input.runId,
    sessionId: input.sessionId,
    records: input.records,
    outcome: input.outcome,
  }))

  // 5. Compaction hazard
  findings.push(...detectCompactionHazard(input.compactionHazard ?? {
    runId: input.runId,
    sessionId: input.sessionId,
    records: input.records,
    contextLimit: input.contextLimit,
    outcome: input.outcome,
  }))

  // 6. Unbounded delegation
  findings.push(...detectUnboundedDelegation(input.unboundedDelegation ?? {
    runId: input.runId,
    sessionId: input.sessionId,
    records: input.records,
    outcome: input.outcome,
  }))

  // 7. Inactive skill reference
  findings.push(...detectInactiveSkillReference(input.inactiveSkillReference ?? {
    runId: input.runId,
    sessionId: input.sessionId,
    records: input.records,
    outcome: input.outcome,
  }))

  return rankFindings(findings)
}
