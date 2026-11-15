// LLM Context Review Seam for KyberDash
// (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task G5: LLM Context Review Seam; Decision D8, Decision D10; ADR 0006, ADR 0008, ADR 0011).
//
// Core Disciplines:
// 1. Decision D10 compliance: Context review is strictly opt-in per invocation and local by default.
//    No telemetry or context text leaves the machine without a discrete, intentional user action.
//    Review is NEVER invoked automatically or during ingestion.
// 2. Decision D8 compliance: The system prompt strictly directs the LLM to recommend non-destructive
//    strategies (relocation, progressive disclosure, on-demand loading, tool deferral) and strictly
//    forbids advising deletion of skills, rules, tools, or instructions outright.
// 3. Telemetry honesty: Empirical measurements must be separated from inference. Unobserved context
//    is never called "waste" (absence of telemetry is not evidence of non-use). Missing telemetry is
//    never treated as zero.
// 4. Outcome-risk caveat: Every recommendation must articulate how the proposed change could hurt
//    the run or cause task regressions.
// 5. No composite score: The review must never compute or output an aggregate efficiency score or grade.
// 6. Isolation from Finding tables: Output is strictly informational ('model_review') and is NEVER
//    written into the canonical `finding` table or any SQLite store.

import { approximateO200kBase } from '../canon/tokens.js'
import {
  createReviewProvider,
  type ReviewProvider,
  type ReviewProviderResponse,
  type ReviewTokenUsage,
} from './review-providers/index.js'

// ---------------------------------------------------------------------------
// Types & Contracts
// ---------------------------------------------------------------------------

/**
 * Context block component passed into a review request (e.g. system prompt, conversation, tool defs).
 */
export interface ReviewContextBlock {
  key?: string
  label?: string
  tokens?: number
  text: string
}

/**
 * Request payload for reviewing turn or block context.
 */
export interface ReviewRequest {
  /** Session ID of the run/turn being reviewed */
  sessionId?: string
  /** Turn index if reviewing a specific turn */
  turnIndex?: number
  /** Assembled context text to review */
  content: string
  /** Optional structured blocks composing the turn */
  blocks?: ReviewContextBlock[]
  /** Harness name (e.g. 'claude-code', 'cursor', 'copilot') */
  harness?: string
  /** Model name */
  model?: string
  /** Optional developer focus area or prompt question */
  focus?: string
}

/**
 * Options controlling provider selection and review execution.
 */
export interface ReviewOptions {
  /** Target provider: 'openai' | 'anthropic' | 'ollama' | 'mock' | 'none' */
  provider?: 'openai' | 'anthropic' | 'ollama' | 'mock' | 'none' | string
  /** Model override */
  model?: string
  /** API key override (credentials are never logged or stored) */
  apiKey?: string
  /** Base URL or endpoint override (e.g. for local Ollama http://localhost:11434) */
  endpoint?: string
  /** Maximum generation tokens */
  maxTokens?: number
  /** Sampling temperature */
  temperature?: number
  /** Mock response string for tests or offline operation */
  mockResponse?: string
}

/**
 * Prompt structure returned by buildReviewPrompt.
 */
export interface ReviewPrompt {
  systemPrompt: string
  userPrompt: string
  /** Full text convenience representation */
  fullPrompt: string
  toString(): string
}

/**
 * Category of recommendation, strictly adhering to Decision D8.
 */
export type ReviewRecommendationType =
  | 'relocate'
  | 'progressive_disclosure'
  | 'on_demand'
  | 'defer'
  | 'general'

/**
 * Structured recommendation extracted from the review response.
 */
export interface ReviewRecommendation {
  type: ReviewRecommendationType
  title: string
  strategy: string
  suggestedAction: string
  outcomeRisk: string
  rawSnippet?: string
}

/**
 * Result returned by runContextReview.
 * Labeled explicitly as 'model_review' — never a canonical Finding.
 */
export interface ReviewResult {
  /** Execution status */
  status: 'completed' | 'unconfigured' | 'error'
  /** Explicit source label indicating this is second-opinion model output */
  source: 'model_review'
  /** Provider identifier */
  provider: string
  /** Model identifier */
  model?: string
  /** Full markdown text of the review */
  review: string
  /** Extracted structured recommendations satisfying Decision D8 */
  recommendations?: ReviewRecommendation[]
  /** Informational setup guidance when unconfigured */
  instructions?: string
  /** Estimated input tokens for the analyzed payload */
  inputTokens?: number
  /** Usage reported by the LLM provider */
  tokensUsed?: ReviewTokenUsage
  /** Error message if execution encountered an issue */
  error?: string
  /** ISO timestamp of review generation */
  timestamp: string
}

// ---------------------------------------------------------------------------
// System Prompt Builder (Decision D8 & D10 Compliance)
// ---------------------------------------------------------------------------

/**
 * System prompt template enforcing Decision D8, D10, and Kyber telemetry principles.
 */
export const SYSTEM_REVIEW_PROMPT = `You are the KyberDash Context Architecture Reviewer, providing a second-opinion diagnostic review of agent turn context.

YOUR CORE MANDATE (Decision D8 Compliance):
You MUST recommend non-destructive context optimization strategies:
1. Relocation: Moving invariant or static context (system rules, style guides) before prefix cache breakpoints, or into searchable reference files.
2. Progressive disclosure: Providing compact summaries or index entries initially, and disclosing full rules or schemas only when triggered.
3. On-demand loading: Retaining pointers and dynamically fetching detailed reference or file contents upon request.
4. Tool deferral: Deferring tool schemas or MCP tool definitions until an agent explicitly activates or requests discovery.

STRICT PROHIBITION (Decision D8 Compliance):
- FORBIDDEN: You must NEVER advise deleting skills, rules, tools, or instructions outright.
- Never suggest removing or deleting context permanently (e.g. "delete this rule", "remove this skill outright").
- Unobserved context must NOT be assumed useless. It may be crucial safety boundaries, negative guidance, or domain invariants.

PRODUCT MEASUREMENT PRINCIPLES:
1. Separate measurement from inference: Clearly distinguish what was directly observed in the context payload from what you infer or hypothesize.
2. Never call unobserved context "waste": Absence of telemetry is not evidence of non-use.
3. Never treat missing telemetry as zero: If a metric or signal is absent, label it as an unobserved coverage gap.
4. Outcome risk caveat: For EVERY recommendation, you MUST explicitly state how the proposed change could hurt the run or cause regressions (e.g. extra turn latency, loss of guidance, retrieval failure).
5. No composite score: Never synthesize or output an aggregate efficiency score, percentage grade, or composite rating.
6. Purely informational: Your analysis is advisory and informational.

OUTPUT FORMAT:
Provide your review in clean Markdown with the following sections:

### Context Composition Summary
(A concise breakdown of the turn context and observations regarding cache positioning, token density, and schema footprint.)

### Recommendations
(List 2 to 4 actionable recommendations adhering to Decision D8. For each recommendation, use the format below:)

#### [Recommendation Title]
- **Strategy**: [Relocate | Progressive disclosure | On-demand loading | Tool deferral]
- **Action**: [Specific concrete action to relocate, defer, or progressively disclose]
- **Outcome Risk**: [Specific risk of how this change could make the run worse or fail task execution]
`

/**
 * Builds the structured review prompt for an LLM review request.
 * Asserts Decision D8 constraints and formats the turn payload cleanly.
 */
export function buildReviewPrompt(request: ReviewRequest): ReviewPrompt {
  const content = (request.content || '').trim()
  const blocks = request.blocks || []
  const tokenEstimate = approximateO200kBase(content)

  const blockSummaryLines: string[] = []
  if (blocks.length > 0) {
    blockSummaryLines.push('Turn Composition Blocks:')
    for (const b of blocks) {
      const bTokens = b.tokens ?? approximateO200kBase(b.text || '')
      blockSummaryLines.push(`- **${b.label || b.key || 'Block'}**: ~${bTokens.toLocaleString()} tokens`)
    }
  }

  const userPromptParts: string[] = [
    '## Context Payload for Review',
    request.harness ? `- **Harness**: ${request.harness}` : '',
    request.sessionId ? `- **Session ID**: ${request.sessionId}` : '',
    request.turnIndex !== undefined ? `- **Turn Index**: ${request.turnIndex}` : '',
    `- **Estimated Payload Size**: ~${tokenEstimate.toLocaleString()} tokens (${content.length.toLocaleString()} characters)`,
    blockSummaryLines.length > 0 ? '\n' + blockSummaryLines.join('\n') : '',
    request.focus ? `\n### Developer Focus\n${request.focus}\n` : '',
    '\n### Assembled Turn Content\n```text\n' + content + '\n```\n',
    'Please analyze this context payload according to your system guidelines. Provide specific recommendations for relocation, progressive disclosure, on-demand loading, or tool deferral, and include an outcome-risk caveat for each.',
  ].filter(Boolean)

  const userPrompt = userPromptParts.join('\n')
  const fullPrompt = `${SYSTEM_REVIEW_PROMPT}\n\n---\n\n${userPrompt}`

  return {
    systemPrompt: SYSTEM_REVIEW_PROMPT,
    userPrompt,
    fullPrompt,
    toString: () => fullPrompt,
  }
}

// ---------------------------------------------------------------------------
// Review Response Parser
// ---------------------------------------------------------------------------

/**
 * Classifies a recommendation strategy string into a ReviewRecommendationType.
 */
function classifyStrategyType(raw: string): ReviewRecommendationType {
  const s = raw.toLowerCase()
  if (s.includes('relocat')) return 'relocate'
  if (s.includes('progressive') || s.includes('disclosure')) return 'progressive_disclosure'
  if (s.includes('on-demand') || s.includes('on demand')) return 'on_demand'
  if (s.includes('defer')) return 'defer'
  return 'general'
}

/**
 * Parses raw LLM markdown text into structured recommendations.
 * Gracefully handles unstructured outputs without throwing errors.
 */
export function parseReviewResponse(rawText: string): {
  summary: string
  recommendations: ReviewRecommendation[]
} {
  if (!rawText || !rawText.trim()) {
    return { summary: '', recommendations: [] }
  }

  const recommendations: ReviewRecommendation[] = []
  const text = rawText.trim()

  // Match recommendation blocks: e.g. "#### [Title]" or "1. **[Title]**"
  const itemPattern = /(?:####|\d+\.)\s+(?:\*\*)?([^\n*]+)(?:\*\*)?\n([\s\S]*?)(?=(?:####|\d+\.|\n###|$))/g

  let match: RegExpExecArray | null
  while ((match = itemPattern.exec(text)) !== null) {
    const rawTitle = match[1].trim()
    const body = match[2].trim()

    // Skip section headings like "Context Composition Summary" if matched accidentally
    if (rawTitle.toLowerCase().includes('summary') || rawTitle.toLowerCase().includes('recommendation')) {
      continue
    }

    const strategyMatch = body.match(/-\s*\*\*Strategy\*\*:\s*([^\n]+)/i)
    const actionMatch = body.match(/-\s*\*\*Action\*\*:\s*([^\n]+)/i)
    const riskMatch = body.match(/-\s*\*\*Outcome Risk\*\*:\s*([^\n]+)/i)

    const rawStrategy = strategyMatch?.[1]?.trim() || ''
    const suggestedAction = actionMatch?.[1]?.trim() || body.slice(0, 200)
    const outcomeRisk = riskMatch?.[1]?.trim() || 'No explicit outcome risk stated.'

    recommendations.push({
      type: classifyStrategyType(rawStrategy || rawTitle),
      title: rawTitle,
      strategy: rawStrategy || 'Non-destructive optimization',
      suggestedAction,
      outcomeRisk,
      rawSnippet: body,
    })
  }

  // Extract summary if available
  let summary = ''
  const summaryMatch = text.match(/###\s*Context Composition Summary\s*\n([\s\S]*?)(?=\n###|$)/i)
  if (summaryMatch) {
    summary = summaryMatch[1].trim()
  } else {
    // Fallback summary is the first paragraph
    const firstPara = text.split(/\n\n+/)[0]
    summary = firstPara.slice(0, 300)
  }

  return { summary, recommendations }
}

// ---------------------------------------------------------------------------
// Context Review Execution Seam
// ---------------------------------------------------------------------------

/**
 * Strips potential API keys or Authorization headers from error messages.
 */
function sanitizeErrorMessage(err: unknown): string {
  const msg = err instanceof Error ? err.message : String(err)
  return msg
    .replace(/sk-[a-zA-Z0-9_-]{20,}/g, 'sk-***')
    .replace(/Bearer\s+[a-zA-Z0-9._-]+/gi, 'Bearer ***')
}

/**
 * Runs an on-demand LLM review over turn context.
 *
 * Disciplines enforced:
 * - Decision D10: Opt-in only. Must be explicitly invoked by user action.
 * - Default is unconfigured: Returns a structured notice with setup instructions without throwing.
 * - Isolation: Model output is strictly informational ('model_review') and NEVER written to SQLite.
 */
export async function runContextReview(
  request: ReviewRequest,
  options: ReviewOptions = {},
): Promise<ReviewResult> {
  const content = request.content ?? ''
  const inputTokens = approximateO200kBase(content)
  const timestamp = new Date().toISOString()

  // 1. Resolve review provider
  const provider: ReviewProvider = createReviewProvider(options)

  // 2. Default unconfigured check (Decision D10 compliance)
  if (!provider.isConfigured) {
    return {
      status: 'unconfigured',
      source: 'model_review',
      provider: provider.name,
      review:
        'No LLM review provider is currently configured. KyberDash context review is opt-in and local by default per Decision D10.\n\n' +
        'To configure a provider, choose one of the following:\n' +
        '1. Local Ollama (private & local): Ensure Ollama is running (`ollama run llama3.2`) at http://localhost:11434 (or set OLLAMA_HOST).\n' +
        '2. Anthropic: Export ANTHROPIC_API_KEY=sk-ant-...\n' +
        '3. OpenAI: Export OPENAI_API_KEY=sk-...',
      instructions:
        'Context reviews are never run automatically or during ingestion. ' +
        'To enable LLM context reviews, set OPENAI_API_KEY, ANTHROPIC_API_KEY, or run local Ollama at http://localhost:11434.',
      inputTokens,
      timestamp,
    }
  }

  // 3. Build prompt enforcing Decision D8 non-destructive strategies
  const prompt = buildReviewPrompt(request)

  // 4. Execute provider review with secret-safe error boundary
  try {
    const response: ReviewProviderResponse = await provider.review(request, prompt, options)
    const { recommendations } = parseReviewResponse(response.rawText)

    return {
      status: 'completed',
      source: 'model_review',
      provider: provider.name,
      model: response.model,
      review: response.rawText,
      recommendations,
      inputTokens,
      tokensUsed: response.tokensUsed,
      timestamp,
    }
  } catch (err) {
    const safeError = sanitizeErrorMessage(err)
    return {
      status: 'error',
      source: 'model_review',
      provider: provider.name,
      review: `LLM context review encountered an error: ${safeError}`,
      error: safeError,
      inputTokens,
      timestamp,
    }
  }
}

/** Alias for plan G5 runReview naming */
export const runReview = runContextReview
