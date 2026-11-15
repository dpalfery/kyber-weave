// Mock Review Provider for KyberDash LLM Context Review Seam
// (Plan: docs/plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md,
// Task G5: LLM Context Review Seam; Decision D8, D10).

import type { ReviewOptions, ReviewPrompt, ReviewRequest } from '../review.js'
import type { ReviewProvider, ReviewProviderResponse } from './types.js'

export const DEFAULT_MOCK_REVIEW_TEXT = `### Context Review Summary
The inspected turn context carries 4,200 tokens across system instructions, conversation history, and tool definitions.

### Recommendations (Adhering to Decision D8)

1. **Relocate Static Reference Rules before Cache Checkpoint**
- **Strategy**: Relocate
- **Action**: Move unchanging coding standard guidelines to the initial prompt prefix before dynamic session state to improve prefix cache hit rate.
- **Outcome Risk**: May slightly increase first-turn latency if cache is cold.

2. **Progressive Disclosure for Inactive Skill Catalogs**
- **Strategy**: Progressive disclosure
- **Action**: Provide 1-line summary entries in the index and disclose full markdown instructions only upon explicit skill invocation.
- **Outcome Risk**: Agent may require an extra turn to retrieve details for rare skills.

3. **On-Demand Loading for Dormant Tool Schemas**
- **Strategy**: On-demand loading
- **Action**: Defer full JSON schemas for rarely used admin tools until requested via a lightweight discovery tool.
- **Outcome Risk**: Adds 1 tool turn overhead when discovery is triggered.
`

export class MockReviewProvider implements ReviewProvider {
  readonly name = 'mock'
  readonly isConfigured = true

  private cannedResponse: string

  constructor(cannedResponse: string = DEFAULT_MOCK_REVIEW_TEXT) {
    this.cannedResponse = cannedResponse
  }

  setCannedResponse(response: string): void {
    this.cannedResponse = response
  }

  async review(
    _request: ReviewRequest,
    _prompt: ReviewPrompt,
    options?: ReviewOptions,
  ): Promise<ReviewProviderResponse> {
    const text = options?.mockResponse ?? this.cannedResponse
    return {
      rawText: text,
      model: options?.model ?? 'mock-model-v1',
      tokensUsed: {
        promptTokens: 120,
        completionTokens: 250,
        totalTokens: 370,
      },
    }
  }
}
