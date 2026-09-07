// Review provider contract for KyberDash LLM Context Review Seam
// (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task G5: LLM Context Review Seam; Decision D10, Decision D8; ADR 0006).

import type { ReviewOptions, ReviewPrompt, ReviewRequest } from '../review.js'

/**
 * Normalized token usage reported by provider.
 */
export interface ReviewTokenUsage {
  promptTokens?: number
  completionTokens?: number
  totalTokens?: number
}

/**
 * Raw response returned by an LLM provider adapter before parsing into ReviewResult.
 */
export interface ReviewProviderResponse {
  rawText: string
  model: string
  tokensUsed?: ReviewTokenUsage
}

/**
 * Common interface for all LLM context review providers.
 * All providers must handle missing configuration gracefully without throwing.
 */
export interface ReviewProvider {
  /** Unique provider identifier (e.g., 'openai', 'anthropic', 'ollama', 'mock', 'none') */
  readonly name: string
  /** Whether the provider has required credentials/endpoints configured */
  readonly isConfigured: boolean
  /** Perform the review over the constructed prompt */
  review(
    request: ReviewRequest,
    prompt: ReviewPrompt,
    options?: ReviewOptions,
  ): Promise<ReviewProviderResponse>
}
