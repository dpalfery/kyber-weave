// Context-item classification by evidence of use for KyberDash (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task F2: Context-Item Classification; Decision D15; ADR 0006, ADR 0008, ADR 0011).
//
// Classification discipline under Decision D15:
// 1. Context items are classified strictly by observable evidence of use:
//    `strong`, `weak`, `none`, or `unobserved`.
// 2. The analysis layer states empirical observability of use and NEVER emits
//    value verdicts (`necessary`, `questionable`, `avoidable`). Value judgments
//    misrepresent telemetry counters as product recommendations (D15, Q3).
// 3. Four evidence states with strictly defined rules:
//    - `strong`: item is directly cited, called, or matched in execution/tool invocation
//      (e.g., tool schema for a tool called in this turn or subsequent turns).
//    - `weak`: item shares vocabulary, file path, or symbol reference with turn queries or responses.
//    - `none`: item is present in context window across turns but never invoked, cited, or matched.
//    - `unobserved`: item could not be analyzed (unmeasured bucket, binary content, or unexported context).
//      `unobserved` is NEVER merged with `none` because absence of telemetry is not evidence of non-use.
// 4. Every classification carries the exact deterministic rule and reasoning that produced it.
// 5. Unattributed residuals (the gap between reported input tokens and reconstructed parts)
//    are isolated as distinct blocks under `unobserved` (`unattributed_residual`), never
//    distributed across the resident items.

import { approximateO200kBase } from '../canon/tokens.js'
import {
  EVIDENCE_OF_USE_LEVELS,
  isNotMeasurable,
  type CanonicalContentKey,
  type EvidenceOfUse,
  type Measurability,
} from '../canon/types.js'
import type { ContextPart, ContextTurn, ResidualAttribution } from './context.js'

// ---------------------------------------------------------------------------
// Types & Contracts (Decision D15)
// ---------------------------------------------------------------------------

export { EVIDENCE_OF_USE_LEVELS, type EvidenceOfUse }

/**
 * Deterministic rules that justify an evidence-of-use classification.
 * Every classification is required to report the specific rule that produced it.
 */
export type ClassificationRule =
  // Strong evidence rules:
  | 'tool_invoked'
  | 'tool_invoked_subsequent'
  | 'tool_invoked_in_session'
  | 'tool_result_invoked'
  | 'direct_citation'
  | 'execution_match'
  // Weak evidence rules:
  | 'path_reference'
  | 'symbol_reference'
  | 'vocabulary_overlap'
  // None evidence rules:
  | 'never_invoked_or_matched'
  // Unobserved evidence rules:
  | 'unmeasured_bucket'
  | 'binary_content'
  | 'unexported_context'
  | 'unattributed_residual'
  | (string & {})

/**
 * A single context block or item under evaluation.
 * Compatible with ContextPart from context.ts with optional item-level metadata.
 */
export type ContextItem = {
  part: CanonicalContentKey | 'residual'
  text?: string
  tokens?: number
  bytes?: number
  name?: string
  server?: string
  filePath?: string
  isBinary?: boolean
  isUnmeasured?: boolean
  metadata?: Record<string, unknown>
}

/**
 * Classified context item carrying its evidence level, rule, reason, and size metrics.
 */
export type ClassifiedContextItem = {
  item: ContextItem
  evidence: EvidenceOfUse
  rule: ClassificationRule
  reason: string
  tokens: number
  bytes: number
  name?: string
  server?: string
  filePath?: string
}

/**
 * Execution and turn telemetry used to evaluate evidence of use.
 */
export type ClassificationTelemetry = {
  /** Tools called in the current turn. */
  invokedTools?: readonly string[] | ReadonlySet<string>
  /** Tools called in subsequent turns in the same session. */
  subsequentInvokedTools?: readonly string[] | ReadonlySet<string>
  /** Tools called anywhere in the session. */
  sessionInvokedTools?: readonly string[] | ReadonlySet<string>
  /** File paths explicitly accessed or referenced in execution. */
  referencedPaths?: readonly string[] | ReadonlySet<string>
  /** Code symbols (functions, classes, interfaces) referenced in execution. */
  referencedSymbols?: readonly string[] | ReadonlySet<string>
  /** Shell or bash commands executed during the turn. */
  executedCommands?: readonly string[] | ReadonlySet<string>
  /** Explicit citations or reference keys cited in user or assistant text. */
  citations?: readonly string[] | ReadonlySet<string>
  /** User queries or messages in this turn. */
  queries?: readonly string[]
  /** Assistant responses or thoughts in this turn. */
  responses?: readonly string[]
  /** Source measurability declarations (R10.1). */
  measurability?: Measurability
}

/**
 * Input shape for classifyTurnContext, accommodating ContextTurn and explicit telemetry.
 */
export type TurnContextInput = {
  parts: readonly (ContextPart | ContextItem)[]
  inputTokens: number
  freshInput?: number
  telemetry?: ClassificationTelemetry
  invokedTools?: readonly string[] | ReadonlySet<string>
  subsequentInvokedTools?: readonly string[] | ReadonlySet<string>
  sessionInvokedTools?: readonly string[] | ReadonlySet<string>
  referencedPaths?: readonly string[] | ReadonlySet<string>
  referencedSymbols?: readonly string[] | ReadonlySet<string>
  executedCommands?: readonly string[] | ReadonlySet<string>
  citations?: readonly string[] | ReadonlySet<string>
  queries?: readonly string[]
  responses?: readonly string[]
  measurability?: Measurability
  countTokens?: (text: string) => number
}

/**
 * Aggregated metrics for one evidence-of-use bucket.
 */
export type EvidenceBucketSummary = {
  evidence: EvidenceOfUse
  tokens: number
  tokenFraction: number
  bytes: number
  byteFraction: number
  itemCount: number
  items: readonly ClassifiedContextItem[]
}

/**
 * Isolated unattributed residual block (the gap between reported input and reconstructed total).
 */
export type IsolatedResidual = {
  tokens: number
  tokenFraction: number
  bytes: number
  byteFraction: number
  attribution: ResidualAttribution
  rule: 'unattributed_residual'
  evidence: 'unobserved'
  isolated: true
}

/**
 * Turn context classification result isolating residuals and providing per-bucket accounting.
 */
export type ClassifiedTurnContext = {
  /** All classified items resident in the turn, including the isolated residual item if one exists. */
  items: readonly ClassifiedContextItem[]
  /** Per-evidence-of-use bucket accounting (strong, weak, none, unobserved). */
  byEvidence: Record<EvidenceOfUse, EvidenceBucketSummary>
  /** The isolated unattributed residual. */
  residual: IsolatedResidual
  /** Total input tokens reported for the turn. */
  totalInputTokens: number
  /** Sum of tokens across reconstructed parts before residual isolation. */
  reconstructedTokens: number
  /** Total bytes across reconstructed items. */
  totalBytes: number
}

/** Options for context classification. */
export type ClassifyOptions = {
  /** Custom token counter for parts lacking measured counts (defaults to o200k_base). */
  countTokens?: (text: string) => number
  /** Minimum shared vocabulary words to trigger weak vocabulary overlap (defaults to 2). */
  vocabularyOverlapThreshold?: number
}

// ---------------------------------------------------------------------------
// Text & Symbol Extraction Utilities
// ---------------------------------------------------------------------------

/** Common English stop words filtered out of vocabulary overlap analysis. */
const STOP_WORDS = new Set([
  'about', 'above', 'after', 'again', 'against', 'all', 'also', 'and', 'any',
  'are', 'because', 'been', 'before', 'being', 'below', 'between', 'both',
  'but', 'can', 'cannot', 'could', 'did', 'does', 'doing', 'down', 'during',
  'each', 'few', 'for', 'from', 'further', 'had', 'has', 'have', 'having',
  'here', 'how', 'into', 'its', 'itself', 'just', 'more', 'most', 'not',
  'now', 'only', 'other', 'our', 'ours', 'out', 'over', 'own', 'same',
  'should', 'some', 'such', 'than', 'that', 'the', 'their', 'theirs', 'them',
  'themselves', 'then', 'there', 'these', 'they', 'this', 'those', 'through',
  'too', 'under', 'until', 'very', 'was', 'were', 'what', 'when', 'where',
  'which', 'while', 'who', 'whom', 'why', 'will', 'with', 'would', 'you',
  'your', 'yours', 'yourself',
])

/**
 * Normalize an incoming ContextPart or ContextItem into a unified ContextItem.
 */
function toContextItem(raw: ContextItem | ContextPart): ContextItem {
  return {
    part: raw.part,
    text: raw.text,
    tokens: raw.tokens,
    server: raw.server,
    name: 'name' in raw ? raw.name : undefined,
    filePath: 'filePath' in raw ? raw.filePath : undefined,
    isBinary: 'isBinary' in raw ? raw.isBinary : undefined,
    isUnmeasured: 'isUnmeasured' in raw ? raw.isUnmeasured : undefined,
    bytes: 'bytes' in raw ? raw.bytes : undefined,
    metadata: 'metadata' in raw ? raw.metadata : undefined,
  }
}

/**
 * Extract potential tool names from tool definition text or schema JSON.
 */
export function extractToolNames(text: string, explicitName?: string): string[] {
  const names = new Set<string>()
  if (explicitName !== undefined && explicitName.trim() !== '') {
    names.add(explicitName.trim())
  }

  // Matches JSON schema `"name": "tool_name"`
  const jsonMatches = text.matchAll(/"name"\s*:\s*"([a-zA-Z0-9_.:-]+)"/g)
  for (const match of jsonMatches) {
    if (match[1]) names.add(match[1])
  }

  // Matches function or tool declarations: `tool tool_name` or `function tool_name`
  const declMatches = text.matchAll(/\b(?:tool|function)\s+([a-zA-Z0-9_-]+)/g)
  for (const match of declMatches) {
    if (match[1]) names.add(match[1])
  }

  return [...names]
}

/**
 * Extract file paths from text (e.g. `src/utils.ts`, `canon/store.ts`).
 */
export function extractFilePaths(text: string): string[] {
  const paths = new Set<string>()
  const matches = text.matchAll(/(?:[\w.-]+[/\\])+[\w.-]+\.[a-zA-Z0-9]+/g)
  for (const match of matches) {
    paths.add(match[0].replace(/\\/g, '/'))
  }
  return [...paths]
}

/**
 * Extract code symbols (identifiers, types, classes, functions) from text.
 */
export function extractSymbols(text: string): string[] {
  const symbols = new Set<string>()

  // Declarations: function foo, class Bar, interface Baz, type Qux, const/let quux
  const declMatches = text.matchAll(
    /\b(?:export\s+)?(?:function|class|interface|type|enum|const|let|var)\s+([a-zA-Z_$][a-zA-Z0-9_$]*)/g
  )
  for (const match of declMatches) {
    if (match[1] && match[1].length >= 3) symbols.add(match[1])
  }

  // PascalCase and camelCase identifiers with at least 4 characters
  const idMatches = text.matchAll(/\b(?:[A-Z][a-zA-Z0-9_$]{3,}|[a-z]+[A-Z][a-zA-Z0-9_$]*)\b/g)
  for (const match of idMatches) {
    symbols.add(match[0])
  }

  return [...symbols]
}

/**
 * Extract significant vocabulary words from text, lowercased and stripped of stop words.
 */
export function extractVocabulary(text: string): Set<string> {
  const words = new Set<string>()
  const matches = text.toLowerCase().matchAll(/\b[a-z]{3,}\b/g)
  for (const match of matches) {
    const word = match[0]
    if (!STOP_WORDS.has(word)) {
      words.add(word)
    }
  }
  return words
}

function toSet(input?: readonly string[] | ReadonlySet<string>): Set<string> {
  if (!input) return new Set<string>()
  if (input instanceof Set) return new Set<string>(input)
  return new Set<string>(input)
}

// ---------------------------------------------------------------------------
// Context-Item Classification (classifyContextItem)
// ---------------------------------------------------------------------------

/**
 * Classify a single context item based on observable execution telemetry.
 *
 * Evaluation order:
 * 1. `unobserved`: Unmeasured buckets, binary content, or unexported context.
 * 2. `strong`: Tool schema called in current, subsequent, or session turns,
 *    or item directly cited/executed.
 * 3. `weak`: Shared vocabulary, file paths, or symbol references with turn queries/responses.
 * 4. `none`: Resident in context window across turns but uninvoked, uncited, and unmatched.
 *
 * In strict compliance with Decision D15, results never produce value verdicts.
 */
export function classifyContextItem(
  rawItem: ContextItem | ContextPart,
  telemetry: ClassificationTelemetry = {},
  options: ClassifyOptions = {}
): ClassifiedContextItem {
  const item = toContextItem(rawItem)
  const text = item.text ?? ''
  const bytes = item.bytes ?? Buffer.byteLength(text, 'utf8')
  const countTokens = options.countTokens ?? approximateO200kBase
  const tokens = item.tokens ?? countTokens(text)
  const threshold = options.vocabularyOverlapThreshold ?? 2

  // 1. Check for 'unobserved' state.
  // Unobserved means the harness cannot or did not report observability of use.
  // It is never merged with 'none'.
  if (item.part === 'residual' || item.name === 'unattributed_residual') {
    return {
      item,
      evidence: 'unobserved',
      rule: 'unattributed_residual',
      reason: 'Unattributed residual tokens not exported in reconstructed context parts.',
      tokens,
      bytes,
      name: item.name,
      server: item.server,
      filePath: item.filePath,
    }
  }

  if (item.isBinary) {
    return {
      item,
      evidence: 'unobserved',
      rule: 'binary_content',
      reason: 'Binary content cannot be analyzed for text, symbol, or invocation references.',
      tokens,
      bytes,
      name: item.name,
      server: item.server,
      filePath: item.filePath,
    }
  }

  if (item.isUnmeasured) {
    return {
      item,
      evidence: 'unobserved',
      rule: 'unmeasured_bucket',
      reason: 'Item was explicitly declared unmeasured by telemetry harness.',
      tokens,
      bytes,
      name: item.name,
      server: item.server,
      filePath: item.filePath,
    }
  }

  if (telemetry.measurability && item.part in telemetry.measurability) {
    const availability = telemetry.measurability[item.part]
    if (isNotMeasurable(availability)) {
      return {
        item,
        evidence: 'unobserved',
        rule: 'unmeasured_bucket',
        reason: `Bucket '${item.part}' declared not measurable by harness: ${availability.reason}`,
        tokens,
        bytes,
        name: item.name,
        server: item.server,
        filePath: item.filePath,
      }
    }
  }

  if (text === '' && tokens > 0) {
    return {
      item,
      evidence: 'unobserved',
      rule: 'unexported_context',
      reason: 'Context tokens billed but block content was omitted or unexported by harness.',
      tokens,
      bytes,
      name: item.name,
      server: item.server,
      filePath: item.filePath,
    }
  }

  // Normalization of telemetry sets
  const invokedTools = toSet(telemetry.invokedTools)
  const subsequentInvokedTools = toSet(telemetry.subsequentInvokedTools)
  const sessionInvokedTools = toSet(telemetry.sessionInvokedTools)
  const referencedPaths = toSet(telemetry.referencedPaths)
  const referencedSymbols = toSet(telemetry.referencedSymbols)
  const executedCommands = toSet(telemetry.executedCommands)
  const citations = toSet(telemetry.citations)

  // 2. Check for 'strong' evidence.
  // Item is directly cited, called, or matched in execution/tool invocation.

  // 2a. Tool definition match
  if (item.part === 'tool_definitions' || (item.name !== undefined && item.part !== 'tool_result_content')) {
    const toolNames = extractToolNames(text, item.name)
    for (const name of toolNames) {
      if (invokedTools.has(name)) {
        return {
          item,
          evidence: 'strong',
          rule: 'tool_invoked',
          reason: `Tool '${name}' directly invoked in current turn execution.`,
          tokens,
          bytes,
          name: item.name ?? name,
          server: item.server,
          filePath: item.filePath,
        }
      }
      if (subsequentInvokedTools.has(name)) {
        return {
          item,
          evidence: 'strong',
          rule: 'tool_invoked_subsequent',
          reason: `Tool '${name}' invoked in subsequent turn execution.`,
          tokens,
          bytes,
          name: item.name ?? name,
          server: item.server,
          filePath: item.filePath,
        }
      }
      if (sessionInvokedTools.has(name)) {
        return {
          item,
          evidence: 'strong',
          rule: 'tool_invoked_in_session',
          reason: `Tool '${name}' invoked during session execution.`,
          tokens,
          bytes,
          name: item.name ?? name,
          server: item.server,
          filePath: item.filePath,
        }
      }
    }
  }

  // 2b. Direct citation in execution citations
  if (item.name && citations.has(item.name)) {
    return {
      item,
      evidence: 'strong',
      rule: 'direct_citation',
      reason: `Item '${item.name}' directly cited in turn telemetry.`,
      tokens,
      bytes,
      name: item.name,
      server: item.server,
      filePath: item.filePath,
    }
  }

  if (item.filePath && (citations.has(item.filePath) || referencedPaths.has(item.filePath))) {
    return {
      item,
      evidence: 'strong',
      rule: 'direct_citation',
      reason: `File '${item.filePath}' directly cited or executed in turn telemetry.`,
      tokens,
      bytes,
      name: item.name,
      server: item.server,
      filePath: item.filePath,
    }
  }

  // 2c. Tool result matching active tool invocation
  if (item.part === 'tool_result_content' && invokedTools.size > 0) {
    // If the item carries a specific tool name matching an invocation
    if (item.name && invokedTools.has(item.name)) {
      return {
        item,
        evidence: 'strong',
        rule: 'tool_result_invoked',
        reason: `Tool result generated from active invocation of '${item.name}'.`,
        tokens,
        bytes,
        name: item.name,
        server: item.server,
        filePath: item.filePath,
      }
    }
  }

  // 2d. Command execution match
  if (executedCommands.size > 0 && text !== '') {
    for (const cmd of executedCommands) {
      if (text.includes(cmd)) {
        return {
          item,
          evidence: 'strong',
          rule: 'execution_match',
          reason: `Execution command '${cmd}' directly matched in context block.`,
          tokens,
          bytes,
          name: item.name,
          server: item.server,
          filePath: item.filePath,
        }
      }
    }
  }

  // 3. Check for 'weak' evidence.
  // Item shares vocabulary, file path, or symbol reference with turn queries or responses.
  const queryText = (telemetry.queries ?? []).join(' ')
  const responseText = (telemetry.responses ?? []).join(' ')
  const turnInteractionText = `${queryText} ${responseText}`.trim()

  // 3a. File path match
  const itemPaths = new Set<string>()
  if (item.filePath) itemPaths.add(item.filePath)
  for (const p of extractFilePaths(text)) itemPaths.add(p)

  if (itemPaths.size > 0) {
    // Check against telemetry paths
    for (const p of itemPaths) {
      if (referencedPaths.has(p)) {
        return {
          item,
          evidence: 'weak',
          rule: 'path_reference',
          reason: `File path '${p}' referenced in execution telemetry.`,
          tokens,
          bytes,
          name: item.name,
          server: item.server,
          filePath: item.filePath ?? p,
        }
      }
      // Check if turn queries or responses mention the path or filename
      const baseName = p.split('/').pop()
      if (
        turnInteractionText.includes(p) ||
        (baseName && baseName.length >= 4 && turnInteractionText.includes(baseName))
      ) {
        return {
          item,
          evidence: 'weak',
          rule: 'path_reference',
          reason: `File path '${p}' referenced in turn query or response.`,
          tokens,
          bytes,
          name: item.name,
          server: item.server,
          filePath: item.filePath ?? p,
        }
      }
    }
  }

  // 3b. Code symbol reference match
  const itemSymbols = extractSymbols(text)
  if (itemSymbols.length > 0) {
    for (const sym of itemSymbols) {
      if (referencedSymbols.has(sym)) {
        return {
          item,
          evidence: 'weak',
          rule: 'symbol_reference',
          reason: `Code symbol '${sym}' referenced in execution telemetry.`,
          tokens,
          bytes,
          name: item.name,
          server: item.server,
          filePath: item.filePath,
        }
      }
      if (turnInteractionText.includes(sym)) {
        return {
          item,
          evidence: 'weak',
          rule: 'symbol_reference',
          reason: `Code symbol '${sym}' referenced in turn query or response.`,
          tokens,
          bytes,
          name: item.name,
          server: item.server,
          filePath: item.filePath,
        }
      }
    }
  }

  // 3c. Vocabulary overlap
  if (turnInteractionText !== '') {
    const itemVocab = extractVocabulary(text)
    const turnVocab = extractVocabulary(turnInteractionText)
    const shared: string[] = []

    for (const word of itemVocab) {
      if (turnVocab.has(word)) {
        shared.push(word)
      }
    }

    // Weak evidence: shared domain-specific vocabulary
    const hasSubstantialTerm = shared.some((w) => w.length >= 6)
    if (shared.length >= threshold || (shared.length >= 1 && hasSubstantialTerm)) {
      return {
        item,
        evidence: 'weak',
        rule: 'vocabulary_overlap',
        reason: `Shared vocabulary (${shared.slice(0, 3).join(', ')}) with turn query or response.`,
        tokens,
        bytes,
        name: item.name,
        server: item.server,
        filePath: item.filePath,
      }
    }
  }

  // 4. Default to 'none'.
  // Item is resident in context window across turns but was never invoked, cited, or matched.
  return {
    item,
    evidence: 'none',
    rule: 'never_invoked_or_matched',
    reason: 'Context item resident in window but uninvoked, uncited, and unmatched in session.',
    tokens,
    bytes,
    name: item.name,
    server: item.server,
    filePath: item.filePath,
  }
}

// ---------------------------------------------------------------------------
// Turn Context Classification (classifyTurnContext)
// ---------------------------------------------------------------------------

function emptyEvidenceBucket(evidence: EvidenceOfUse): EvidenceBucketSummary {
  return {
    evidence,
    tokens: 0,
    tokenFraction: 0,
    bytes: 0,
    byteFraction: 0,
    itemCount: 0,
    items: [],
  }
}

/**
 * Classify every context block resident in a turn, compute bucket totals and fractions,
 * and isolate unattributed residuals into a dedicated unobserved block.
 *
 * In accordance with Acceptance Criterion 3:
 * - Computes token counts and byte fractions per evidence-of-use bucket.
 * - Isolates unattributed residuals (usage-record input minus reconstructed total) as their
 *   own block under `unobserved` with rule `unattributed_residual`, never distributed across items.
 */
export function classifyTurnContext(
  input: TurnContextInput | ContextTurn,
  telemetryOverride?: ClassificationTelemetry,
  options: ClassifyOptions = {}
): ClassifiedTurnContext {
  const parts = input.parts
  const inputTokens = input.inputTokens
  const countTokens = options.countTokens ?? ('countTokens' in input ? input.countTokens : undefined) ?? approximateO200kBase

  // Merge telemetry from input object and optional telemetryOverride argument
  const telemetry: ClassificationTelemetry = {
    ...('telemetry' in input && input.telemetry ? input.telemetry : {}),
    ...('invokedTools' in input && input.invokedTools ? { invokedTools: input.invokedTools } : {}),
    ...('subsequentInvokedTools' in input && input.subsequentInvokedTools ? { subsequentInvokedTools: input.subsequentInvokedTools } : {}),
    ...('sessionInvokedTools' in input && input.sessionInvokedTools ? { sessionInvokedTools: input.sessionInvokedTools } : {}),
    ...('referencedPaths' in input && input.referencedPaths ? { referencedPaths: input.referencedPaths } : {}),
    ...('referencedSymbols' in input && input.referencedSymbols ? { referencedSymbols: input.referencedSymbols } : {}),
    ...('executedCommands' in input && input.executedCommands ? { executedCommands: input.executedCommands } : {}),
    ...('citations' in input && input.citations ? { citations: input.citations } : {}),
    ...('queries' in input && input.queries ? { queries: input.queries } : {}),
    ...('responses' in input && input.responses ? { responses: input.responses } : {}),
    ...('measurability' in input && input.measurability ? { measurability: input.measurability } : {}),
    ...(telemetryOverride ?? {}),
  }

  // 1. Classify each resident part
  const classifiedParts: ClassifiedContextItem[] = []
  let reconstructedTokens = 0
  let totalBytes = 0
  let derivedAnyToken = false

  for (const part of parts) {
    if (part.tokens === undefined) derivedAnyToken = true
    const classified = classifyContextItem(part, telemetry, { ...options, countTokens })
    classifiedParts.push(classified)
    reconstructedTokens += classified.tokens
    totalBytes += classified.bytes
  }

  // 2. Isolate unattributed residuals (R7.3 / Acceptance Criterion 3)
  const residualTokens = inputTokens - reconstructedTokens
  const attribution: ResidualAttribution = derivedAnyToken ? 'tokenizer_drift' : 'unattributed'

  let residualBlock: ClassifiedContextItem | undefined
  if (residualTokens > 0) {
    residualBlock = {
      item: {
        part: 'residual',
        name: 'unattributed_residual',
        text: '',
        tokens: residualTokens,
        bytes: 0,
        isUnmeasured: true,
      },
      evidence: 'unobserved',
      rule: 'unattributed_residual',
      reason: 'Unattributed residual tokens not accounted for in reconstructed context parts.',
      tokens: residualTokens,
      bytes: 0,
    }
  }

  // All items for the turn including the isolated residual if present
  const allItems: ClassifiedContextItem[] = residualBlock
    ? [...classifiedParts, residualBlock]
    : classifiedParts

  // 3. Aggregate totals and compute exact fractions per evidence-of-use bucket
  const buckets: Record<EvidenceOfUse, EvidenceBucketSummary> = {
    strong: emptyEvidenceBucket('strong'),
    weak: emptyEvidenceBucket('weak'),
    none: emptyEvidenceBucket('none'),
    unobserved: emptyEvidenceBucket('unobserved'),
  }

  for (const item of allItems) {
    const b = buckets[item.evidence]
    b.tokens += item.tokens
    b.bytes += item.bytes
    b.itemCount += 1
    ;(b.items as ClassifiedContextItem[]).push(item)
  }

  const denominatorTokens = inputTokens > 0 ? inputTokens : reconstructedTokens
  for (const key of EVIDENCE_OF_USE_LEVELS) {
    const b = buckets[key]
    b.tokenFraction = denominatorTokens > 0 ? b.tokens / denominatorTokens : 0
    b.byteFraction = totalBytes > 0 ? b.bytes / totalBytes : 0
  }

  const residualTokenFraction = denominatorTokens > 0 ? residualTokens / denominatorTokens : 0
  const residual: IsolatedResidual = {
    tokens: residualTokens,
    tokenFraction: residualTokenFraction,
    bytes: 0,
    byteFraction: 0,
    attribution,
    rule: 'unattributed_residual',
    evidence: 'unobserved',
    isolated: true,
  }

  return {
    items: allItems,
    byEvidence: buckets,
    residual,
    totalInputTokens: inputTokens,
    reconstructedTokens,
    totalBytes,
  }
}

// ---------------------------------------------------------------------------
// Multi-Turn Session Classification (classifySessionContext)
// ---------------------------------------------------------------------------

export type SessionContextTurn = {
  parts: readonly (ContextPart | ContextItem)[]
  inputTokens: number
  freshInput?: number
  invokedTools?: readonly string[] | ReadonlySet<string>
  queries?: readonly string[]
  responses?: readonly string[]
  referencedPaths?: readonly string[] | ReadonlySet<string>
  referencedSymbols?: readonly string[] | ReadonlySet<string>
  executedCommands?: readonly string[] | ReadonlySet<string>
  citations?: readonly string[] | ReadonlySet<string>
  measurability?: Measurability
}

export type SessionContextInput = {
  turns: readonly SessionContextTurn[]
  measurability?: Measurability
  countTokens?: (text: string) => number
}

/**
 * Classifies an entire multi-turn session context window across time.
 *
 * Why: Context resident in turn `i` may contain a tool schema called in turn `i + k`.
 * Evaluating the session holistically allows distinguishing tools called in subsequent turns
 * (evidence: `strong`, rule: `tool_invoked_subsequent`) from tools never called throughout
 * the session (evidence: `none`, rule: `never_invoked_or_matched`).
 */
export function classifySessionContext(
  input: SessionContextInput,
  options: ClassifyOptions = {}
): ClassifiedTurnContext[] {
  const turns = input.turns
  const sessionInvokedTools = new Set<string>()
  const turnInvokedToolsList: Set<string>[] = []

  // Pre-pass: collect invocations across the session
  for (const turn of turns) {
    const turnTools = toSet(turn.invokedTools)
    turnInvokedToolsList.push(turnTools)
    for (const tool of turnTools) {
      sessionInvokedTools.add(tool)
    }
  }

  const results: ClassifiedTurnContext[] = []

  // Classify each turn with awareness of past, current, and future invocations
  for (let i = 0; i < turns.length; i++) {
    const currentTurn = turns[i]
    const currentInvoked = turnInvokedToolsList[i]

    // Subsequent tool invocations: all tools called in turns > i
    const subsequentTools = new Set<string>()
    for (let j = i + 1; j < turns.length; j++) {
      for (const tool of turnInvokedToolsList[j]) {
        subsequentTools.add(tool)
      }
    }

    const telemetry: ClassificationTelemetry = {
      invokedTools: currentInvoked,
      subsequentInvokedTools: subsequentTools,
      sessionInvokedTools,
      referencedPaths: currentTurn.referencedPaths,
      referencedSymbols: currentTurn.referencedSymbols,
      executedCommands: currentTurn.executedCommands,
      citations: currentTurn.citations,
      queries: currentTurn.queries,
      responses: currentTurn.responses,
      measurability: currentTurn.measurability ?? input.measurability,
    }

    results.push(
      classifyTurnContext(
        {
          parts: currentTurn.parts,
          inputTokens: currentTurn.inputTokens,
          freshInput: currentTurn.freshInput,
          telemetry,
        },
        undefined,
        options
      )
    )
  }

  return results
}
