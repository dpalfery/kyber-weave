// Anthropic Review Provider for KyberDash LLM Context Review Seam
// (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task G5: LLM Context Review Seam; Decision D10).

import type { ReviewOptions, ReviewPrompt, ReviewRequest } from '../review.js'
import type { ReviewProvider, ReviewProviderResponse } from './types.js'

export class AnthropicReviewProvider implements ReviewProvider {
  readonly name = 'anthropic'

  get isConfigured(): boolean {
    return Boolean(process.env.ANTHROPIC_API_KEY || this.explicitApiKey)
  }

  constructor(
    private readonly explicitApiKey?: string,
    private readonly explicitEndpoint?: string,
  ) {}

  async review(
    _request: ReviewRequest,
    prompt: ReviewPrompt,
    options?: ReviewOptions,
  ): Promise<ReviewProviderResponse> {
    const apiKey = options?.apiKey || this.explicitApiKey || process.env.ANTHROPIC_API_KEY
    if (!apiKey) {
      throw new Error(
        'Anthropic API key not configured. Set the ANTHROPIC_API_KEY environment variable or supply an apiKey in options.',
      )
    }

    const rawEndpoint =
      options?.endpoint ||
      this.explicitEndpoint ||
      process.env.ANTHROPIC_BASE_URL ||
      'https://api.anthropic.com/v1'
    const baseUrl = rawEndpoint.replace(/\/+$/, '')
    const url = baseUrl.endsWith('/messages') ? baseUrl : `${baseUrl}/messages`

    const model = options?.model || 'claude-3-5-haiku-20241022'
    const maxTokens = options?.maxTokens ?? 2048
    const temperature = options?.temperature ?? 0.2

    const payload = {
      model,
      system: prompt.systemPrompt,
      messages: [{ role: 'user', content: prompt.userPrompt }],
      max_tokens: maxTokens,
      temperature,
    }

    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-api-key': apiKey,
        'anthropic-version': '2023-06-01',
      },
      body: JSON.stringify(payload),
    })

    if (!response.ok) {
      const errorText = await response.text().catch(() => '')
      throw new Error(
        `Anthropic request failed (${response.status}): ${errorText.slice(0, 300)}`,
      )
    }

    const data = (await response.json()) as {
      model?: string
      content?: Array<{ type?: string; text?: string }>
      usage?: {
        input_tokens?: number
        output_tokens?: number
      }
    }

    const text =
      data.content
        ?.filter((c) => c.type === 'text' || !c.type)
        .map((c) => c.text ?? '')
        .join('\n') ?? ''

    return {
      rawText: text,
      model: data.model ?? model,
      tokensUsed: {
        promptTokens: data.usage?.input_tokens,
        completionTokens: data.usage?.output_tokens,
        totalTokens:
          (data.usage?.input_tokens ?? 0) + (data.usage?.output_tokens ?? 0),
      },
    }
  }
}
