// Ollama Local Review Provider for KyberDash LLM Context Review Seam
// (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task G5: LLM Context Review Seam; Decision D10: Local by default).

import type { ReviewOptions, ReviewPrompt, ReviewRequest } from '../review.js'
import type { ReviewProvider, ReviewProviderResponse } from './types.js'

export class OllamaReviewProvider implements ReviewProvider {
  readonly name = 'ollama'

  /**
   * For local Ollama, isConfigured is true when explicitly requested or when
   * an Ollama endpoint is designated, because Ollama runs locally without API keys.
   */
  get isConfigured(): boolean {
    return Boolean(
      process.env.OLLAMA_HOST ||
      process.env.OLLAMA_BASE_URL ||
      this.explicitEndpoint ||
      this.forceConfigured,
    )
  }

  constructor(
    private readonly explicitEndpoint?: string,
    private readonly defaultModel: string = 'llama3.2',
    private readonly forceConfigured: boolean = false,
  ) {}

  async review(
    _request: ReviewRequest,
    prompt: ReviewPrompt,
    options?: ReviewOptions,
  ): Promise<ReviewProviderResponse> {
    const rawEndpoint =
      options?.endpoint ||
      this.explicitEndpoint ||
      process.env.OLLAMA_HOST ||
      process.env.OLLAMA_BASE_URL ||
      'http://localhost:11434'

    const baseUrl = rawEndpoint.replace(/\/+$/, '')
    const url = baseUrl.endsWith('/api/chat') ? baseUrl : `${baseUrl}/api/chat`
    const model = options?.model || this.defaultModel

    const payload = {
      model,
      messages: [
        { role: 'system', content: prompt.systemPrompt },
        { role: 'user', content: prompt.userPrompt },
      ],
      stream: false,
      options: {
        temperature: options?.temperature ?? 0.2,
        num_predict: options?.maxTokens ?? 2048,
      },
    }

    let response: Response
    try {
      response = await fetch(url, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
        },
        body: JSON.stringify(payload),
      })
    } catch (netErr) {
      throw new Error(
        `Failed to reach local Ollama endpoint at ${baseUrl}. Ensure Ollama is running ('ollama run ${model}'). ` +
        `Error: ${netErr instanceof Error ? netErr.message : String(netErr)}`,
      )
    }

    if (!response.ok) {
      const errorText = await response.text().catch(() => '')
      throw new Error(
        `Ollama request failed (${response.status}): ${errorText.slice(0, 300)}`,
      )
    }

    const data = (await response.json()) as {
      model?: string
      message?: { content?: string }
      prompt_eval_count?: number
      eval_count?: number
    }

    const text = data.message?.content ?? ''
    return {
      rawText: text,
      model: data.model ?? model,
      tokensUsed: {
        promptTokens: data.prompt_eval_count,
        completionTokens: data.eval_count,
        totalTokens:
          (data.prompt_eval_count ?? 0) + (data.eval_count ?? 0),
      },
    }
  }
}
