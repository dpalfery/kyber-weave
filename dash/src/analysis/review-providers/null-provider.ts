// Null / Unconfigured Review Provider for KyberDash LLM Context Review Seam
// (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task G5: LLM Context Review Seam; Decision D10: Default is unconfigured; graceful notice without throwing).

import type { ReviewOptions, ReviewPrompt, ReviewRequest } from '../review.js'
import type { ReviewProvider, ReviewProviderResponse } from './types.js'

/**
 * Default fallback provider when no API keys or endpoints are configured.
 * Decision D10 compliance: KyberDash never defaults to sending context to an external
 * service without explicit configuration and per-invocation opt-in. When unconfigured,
 * it returns a structured informational notice with clear setup guidance rather than throwing.
 */
export class NullReviewProvider implements ReviewProvider {
  readonly name = 'none'
  readonly isConfigured = false

  async review(
    _request: ReviewRequest,
    _prompt: ReviewPrompt,
    _options?: ReviewOptions,
  ): Promise<ReviewProviderResponse> {
    return {
      rawText:
        'No LLM review provider is currently configured. KyberDash context review is opt-in and local by default per Decision D10.\n\n' +
        'To configure a provider, choose one of the following:\n' +
        '1. Local Ollama (private & local): Ensure Ollama is running (`ollama run llama3.2`) at http://localhost:11434 (or set OLLAMA_HOST).\n' +
        '2. Anthropic: Export ANTHROPIC_API_KEY=sk-ant-...\n' +
        '3. OpenAI: Export OPENAI_API_KEY=sk-...',
      model: 'unconfigured',
    }
  }
}
