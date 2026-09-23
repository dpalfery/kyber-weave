// OpenAI / OpenAI-Compatible Review Provider for KyberDash LLM Context Review Seam
// (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task G5: LLM Context Review Seam; Decision D10).

import type { ReviewOptions, ReviewPrompt, ReviewRequest } from '../review.js'
import type { ReviewProvider, ReviewProviderResponse } from './types.js'

export class OpenAIReviewProvider implements ReviewProvider {
  readonly name = 'openai'

  get isConfigured(): boolean {
    return Boolean(process.env.OPENAI_API_KEY || this.explicitApiKey)
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
    const apiKey = options?.apiKey || this.explicitApiKey || process.env.OPENAI_API_KEY
    if (!apiKey) {
      throw new Error(
        'OpenAI API key not configured. Set the OPENAI_API_KEY environment variable or supply an apiKey in options.',
      )
    }

    const rawEndpoint =
      options?.endpoint ||
      this.explicitEndpoint ||
      process.env.OPENAI_BASE_URL ||
      'https://api.openai.com/v1'
    const baseUrl = rawEndpoint.replace(/\/+$/, '')
    const url = baseUrl.endsWith('/chat/completions')
      ? baseUrl
      : `${baseUrl}/chat/completions`

    const model = options?.model || 'gpt-4o-mini'
    const maxTokens = options?.maxTokens ?? 2048
    const temperature = options?.temperature ?? 0.2

    const payload = {
      model,
      messages: [
        { role: 'system', content: prompt.systemPrompt },
        { role: 'user', content: prompt.userPrompt },
      ],
      max_tokens: maxTokens,
      temperature,
    }

    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        authorization: `Bearer ${apiKey}`,
      },
      body: JSON.stringify(payload),
    })

    if (!response.ok) {
      const errorText = await response.text().catch(() => '')
      throw new Error(
        `OpenAI request failed (${response.status}): ${errorText.slice(0, 300)}`,
      )
    }

    const data = (await response.json()) as {
      model?: string
      choices?: Array<{ message?: { content?: string } }>
      usage?: {
        prompt_tokens?: number
        completion_tokens?: number
        total_tokens?: number
      }
    }

    const text = data.choices?.[0]?.message?.content ?? ''
    return {
      rawText: text,
      model: data.model ?? model,
      tokensUsed: {
        promptTokens: data.usage?.prompt_tokens,
        completionTokens: data.usage?.completion_tokens,
        totalTokens: data.usage?.total_tokens,
      },
    }
  }
}
