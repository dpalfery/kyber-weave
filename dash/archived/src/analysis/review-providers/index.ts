// Provider factory and registry for KyberDash LLM Context Review Seam
// (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task G5: LLM Context Review Seam; Decision D10, Decision D8; ADR 0006).

import type { ReviewOptions } from '../review.js'
import { AnthropicReviewProvider } from './anthropic.js'
import { MockReviewProvider } from './mock.js'
import { NullReviewProvider } from './null-provider.js'
import { OllamaReviewProvider } from './ollama.js'
import { OpenAIReviewProvider } from './openai.js'
import type { ReviewProvider } from './types.js'

export * from './types.js'
export * from './null-provider.js'
export * from './mock.js'
export * from './openai.js'
export * from './anthropic.js'
export * from './ollama.js'

/**
 * Creates or resolves an appropriate ReviewProvider based on explicit options or environment.
 * Default is NullReviewProvider (unconfigured), fulfilling Decision D10:
 * No context leaves the machine without explicit configuration and per-invocation user action.
 */
export function createReviewProvider(options?: ReviewOptions): ReviewProvider {
  const providerKey = options?.provider?.toLowerCase()?.trim()

  if (providerKey === 'mock') {
    return new MockReviewProvider(options?.mockResponse)
  }

  if (providerKey === 'openai') {
    return new OpenAIReviewProvider(options?.apiKey, options?.endpoint)
  }

  if (providerKey === 'anthropic') {
    return new AnthropicReviewProvider(options?.apiKey, options?.endpoint)
  }

  if (providerKey === 'ollama') {
    return new OllamaReviewProvider(
      options?.endpoint,
      options?.model || 'llama3.2',
      true, // explicitly requested
    )
  }

  if (providerKey === 'none') {
    return new NullReviewProvider()
  }

  // Auto-detection based on options or environment
  if (options?.apiKey) {
    // If an apiKey is provided without a provider, guess Anthropic (sk-ant-) or OpenAI
    if (options.apiKey.startsWith('sk-ant-')) {
      return new AnthropicReviewProvider(options.apiKey, options.endpoint)
    }
    return new OpenAIReviewProvider(options.apiKey, options.endpoint)
  }

  // If local Ollama endpoint is specified
  if (options?.endpoint?.includes('11434')) {
    return new OllamaReviewProvider(options.endpoint, options?.model, true)
  }

  // Detect from environment in preference order: local Ollama -> Anthropic -> OpenAI -> Null
  if (process.env.ANTHROPIC_API_KEY) {
    return new AnthropicReviewProvider(undefined, options?.endpoint)
  }

  if (process.env.OPENAI_API_KEY) {
    return new OpenAIReviewProvider(undefined, options?.endpoint)
  }

  if (process.env.OLLAMA_HOST || process.env.OLLAMA_BASE_URL) {
    return new OllamaReviewProvider(options?.endpoint, options?.model, true)
  }

  // Default is unconfigured per Decision D10
  return new NullReviewProvider()
}
