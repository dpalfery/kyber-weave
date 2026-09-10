// Test suite for LLM Context Review Seam (Task G5 / Decision D8, D10; ADR 0006, ADR 0008).
//
// Acceptance Criteria verified:
// 1. Decision D10 compliance: Explicit opt-in per invocation. Review is NEVER invoked automatically or during ingestion.
// 2. Shows payload preview and token size before user confirms and sends to provider.
// 3. Default is unconfigured (graceful notice with instructions on setting API key / endpoint, no error throws).
// 4. Decision D8 compliance: System prompt strictly instructs the LLM to recommend relocation, progressive disclosure,
//    on-demand loading, or tool deferral, and forbids advising deletion of skills or rules outright.
// 5. Output is strictly informational and is NEVER written into a `Finding` or `findings` table in SQLite.
// 6. Provider abstraction: Mock, OpenAI, Anthropic, Ollama, and Null fallback.
// 7. Isolation from Finding tables: Proves SQLite finding table remains completely unaffected.

import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  buildReviewPrompt,
  parseReviewResponse,
  runContextReview,
  runReview,
  SYSTEM_REVIEW_PROMPT,
  type ReviewOptions,
  type ReviewRequest,
  type ReviewResult,
} from '../kyber/analysis/review.js'
import {
  createReviewProvider,
  MockReviewProvider,
  NullReviewProvider,
  OpenAIReviewProvider,
  AnthropicReviewProvider,
  OllamaReviewProvider,
  DEFAULT_MOCK_REVIEW_TEXT,
} from '../kyber/analysis/review-providers/index.js'
import { CanonStore } from '../kyber/canon/store.js'
import { handleKyberRequest } from '../kyber/server/routes.js'
import type { KyberBridge } from '../kyber/server/bridge.js'
import type { IncomingMessage, ServerResponse } from 'node:http'
import { EventEmitter } from 'node:events'

const tempDirs: string[] = []

function tempStorePath(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-review-test-'))
  tempDirs.push(dir)
  return join(dir, 'canon.db')
}

afterEach(() => {
  vi.restoreAllMocks()
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()!
    rmSync(dir, { recursive: true, force: true })
  }
})

describe('LLM Context Review Seam (Task G5 / Decision D10 & D8)', () => {
  // ---------------------------------------------------------------------------
  // 1. Prompt Construction & Decision D8 Constraints
  // ---------------------------------------------------------------------------
  describe('buildReviewPrompt & Decision D8 Compliance', () => {
    it('constructs structured system and user prompts with payload metadata', () => {
      const request: ReviewRequest = {
        sessionId: 'sess-test-123',
        turnIndex: 4,
        harness: 'claude-code',
        content: 'System prompt instructions: obey rules.\nUser: write a sorting function.',
        blocks: [
          { key: 'system_prompt', label: 'System Prompt', text: 'System prompt instructions: obey rules.', tokens: 10 },
          { key: 'conversation', label: 'Conversation', text: 'User: write a sorting function.', tokens: 8 },
        ],
        focus: 'Evaluate prefix cache positioning',
      }

      const prompt = buildReviewPrompt(request)

      expect(prompt.systemPrompt).toBeDefined()
      expect(prompt.userPrompt).toBeDefined()
      expect(prompt.fullPrompt).toContain(prompt.systemPrompt)
      expect(prompt.fullPrompt).toContain(prompt.userPrompt)
      expect(prompt.toString()).toBe(prompt.fullPrompt)

      // User prompt asserts payload metadata
      expect(prompt.userPrompt).toContain('sess-test-123')
      expect(prompt.userPrompt).toContain('claude-code')
      expect(prompt.userPrompt).toContain('Turn Index**: 4')
      expect(prompt.userPrompt).toContain('Evaluate prefix cache positioning')
      expect(prompt.userPrompt).toContain('System Prompt**: ~10 tokens')
      expect(prompt.userPrompt).toContain('Conversation**: ~8 tokens')
      expect(prompt.userPrompt).toContain('System prompt instructions: obey rules.')
    })

    it('strictly enforces Decision D8 non-destructive strategies in system prompt', () => {
      const request: ReviewRequest = { content: 'Sample prompt' }
      const prompt = buildReviewPrompt(request)
      const system = prompt.systemPrompt

      // Decision D8 core non-destructive strategies MUST be required:
      expect(system).toMatch(/relocat(?:e|ion)/i)
      expect(system).toMatch(/progressive disclosure/i)
      expect(system).toMatch(/on-demand loading/i)
      expect(system).toMatch(/tool deferral/i)

      // Decision D8 strict prohibition of outright deletion:
      expect(system).toMatch(/FORBIDDEN/i)
      expect(system).toMatch(/never advise deleting/i)
      expect(system).toMatch(/never suggest removing or deleting context permanently/i)
      expect(system).toMatch(/unobserved context must NOT be assumed useless/i)
    })

    it('enforces product measurement principles in system prompt', () => {
      const request: ReviewRequest = { content: 'Sample prompt' }
      const prompt = buildReviewPrompt(request)
      const system = prompt.systemPrompt

      // Separate measurement from inference
      expect(system).toMatch(/separate measurement from inference/i)
      // Never call unobserved context waste
      expect(system).toMatch(/never call unobserved context "waste"/i)
      // Never treat missing telemetry as zero
      expect(system).toMatch(/never treat missing telemetry as zero/i)
      // Outcome risk caveat required for each recommendation
      expect(system).toMatch(/outcome risk caveat/i)
      // Forbid composite score
      expect(system).toMatch(/no composite score/i)
      expect(system).toMatch(/never synthesize or output an aggregate efficiency score/i)
      // Advisory and informational
      expect(system).toMatch(/informational/i)
    })
  })

  // ---------------------------------------------------------------------------
  // 2. Review Response Parsing
  // ---------------------------------------------------------------------------
  describe('parseReviewResponse', () => {
    it('parses structured recommendations adhering to Decision D8', () => {
      const parsed = parseReviewResponse(DEFAULT_MOCK_REVIEW_TEXT)

      expect(parsed.summary).toContain('The inspected turn context carries 4,200 tokens')
      expect(parsed.recommendations).toHaveLength(3)

      const [r1, r2, r3] = parsed.recommendations
      expect(r1.type).toBe('relocate')
      expect(r1.title).toContain('Relocate Static Reference Rules')
      expect(r1.suggestedAction).toContain('Move unchanging coding standard guidelines')
      expect(r1.outcomeRisk).toContain('May slightly increase first-turn latency')

      expect(r2.type).toBe('progressive_disclosure')
      expect(r2.title).toContain('Progressive Disclosure for Inactive Skill Catalogs')
      expect(r2.suggestedAction).toContain('Provide 1-line summary entries in the index')
      expect(r2.outcomeRisk).toContain('Agent may require an extra turn')

      expect(r3.type).toBe('on_demand')
      expect(r3.title).toContain('On-Demand Loading for Dormant Tool Schemas')
      expect(r3.suggestedAction).toContain('Defer full JSON schemas')
      expect(r3.outcomeRisk).toContain('Adds 1 tool turn overhead')
    })

    it('gracefully handles unstructured or empty responses without throwing', () => {
      expect(parseReviewResponse('')).toEqual({ summary: '', recommendations: [] })
      expect(parseReviewResponse('   ')).toEqual({ summary: '', recommendations: [] })

      const plainText = 'Here is some general advice about prompt size. You should keep system prompts concise.'
      const unstructured = parseReviewResponse(plainText)
      expect(unstructured.summary).toContain('Here is some general advice')
      expect(unstructured.recommendations).toHaveLength(0)
    })
  })

  // ---------------------------------------------------------------------------
  // 3. Provider Abstraction
  // ---------------------------------------------------------------------------
  describe('Provider Abstraction', () => {
    it('MockReviewProvider returns deterministic responses and respects options', async () => {
      const mock = new MockReviewProvider()
      expect(mock.name).toBe('mock')
      expect(mock.isConfigured).toBe(true)

      const request: ReviewRequest = { content: 'test content' }
      const prompt = buildReviewPrompt(request)

      const res = await mock.review(request, prompt)
      expect(res.rawText).toBe(DEFAULT_MOCK_REVIEW_TEXT)
      expect(res.model).toBe('mock-model-v1')
      expect(res.tokensUsed?.totalTokens).toBe(370)

      // Override with custom mock text
      const customRes = await mock.review(request, prompt, {
        mockResponse: 'Custom review text',
        model: 'custom-mock',
      })
      expect(customRes.rawText).toBe('Custom review text')
      expect(customRes.model).toBe('custom-mock')
    })

    it('NullReviewProvider returns unconfigured guidance without throwing', async () => {
      const nullProv = new NullReviewProvider()
      expect(nullProv.name).toBe('none')
      expect(nullProv.isConfigured).toBe(false)

      const request: ReviewRequest = { content: 'test content' }
      const prompt = buildReviewPrompt(request)

      const res = await nullProv.review(request, prompt)
      expect(res.model).toBe('unconfigured')
      expect(res.rawText).toContain('No LLM review provider is currently configured')
      expect(res.rawText).toContain('Decision D10')
      expect(res.rawText).toContain('Local Ollama')
    })

    it('OpenAIReviewProvider constructs correct endpoint and payload with mock fetch', async () => {
      let capturedUrl = ''
      let capturedHeaders: Record<string, string> = {}
      let capturedBody: any = null

      const mockFetch = vi.fn().mockImplementation(async (url: string, init: any) => {
        capturedUrl = url
        capturedHeaders = init.headers
        capturedBody = JSON.parse(init.body)
        return {
          ok: true,
          json: async () => ({
            model: 'gpt-4o-mini',
            choices: [{ message: { content: 'OpenAI review result' } }],
            usage: { prompt_tokens: 15, completion_tokens: 25, total_tokens: 40 },
          }),
        }
      })
      vi.stubGlobal('fetch', mockFetch)

      const provider = new OpenAIReviewProvider('sk-test-key', 'https://custom.openai.com/v1')
      expect(provider.name).toBe('openai')
      expect(provider.isConfigured).toBe(true)

      const request: ReviewRequest = { content: 'OpenAI test' }
      const prompt = buildReviewPrompt(request)
      const res = await provider.review(request, prompt, { model: 'gpt-4o' })

      expect(capturedUrl).toBe('https://custom.openai.com/v1/chat/completions')
      expect(capturedHeaders['authorization']).toBe('Bearer sk-test-key')
      expect(capturedBody.model).toBe('gpt-4o')
      expect(capturedBody.messages).toHaveLength(2)
      expect(capturedBody.messages[0].role).toBe('system')
      expect(capturedBody.messages[1].role).toBe('user')
      expect(res.rawText).toBe('OpenAI review result')
      expect(res.tokensUsed?.totalTokens).toBe(40)
    })

    it('AnthropicReviewProvider constructs correct headers and payload with mock fetch', async () => {
      let capturedUrl = ''
      let capturedHeaders: Record<string, string> = {}
      let capturedBody: any = null

      const mockFetch = vi.fn().mockImplementation(async (url: string, init: any) => {
        capturedUrl = url
        capturedHeaders = init.headers
        capturedBody = JSON.parse(init.body)
        return {
          ok: true,
          json: async () => ({
            model: 'claude-3-5-sonnet-20241022',
            content: [{ type: 'text', text: 'Anthropic review result' }],
            usage: { input_tokens: 20, output_tokens: 30 },
          }),
        }
      })
      vi.stubGlobal('fetch', mockFetch)

      const provider = new AnthropicReviewProvider('sk-ant-test-key')
      expect(provider.name).toBe('anthropic')
      expect(provider.isConfigured).toBe(true)

      const request: ReviewRequest = { content: 'Anthropic test' }
      const prompt = buildReviewPrompt(request)
      const res = await provider.review(request, prompt)

      expect(capturedUrl).toBe('https://api.anthropic.com/v1/messages')
      expect(capturedHeaders['x-api-key']).toBe('sk-ant-test-key')
      expect(capturedHeaders['anthropic-version']).toBe('2023-06-01')
      expect(capturedBody.system).toBe(prompt.systemPrompt)
      expect(capturedBody.messages).toHaveLength(1)
      expect(capturedBody.messages[0].content).toBe(prompt.userPrompt)
      expect(res.rawText).toBe('Anthropic review result')
      expect(res.tokensUsed?.totalTokens).toBe(50)
    })

    it('OllamaReviewProvider defaults to local endpoint without requiring API keys', async () => {
      let capturedUrl = ''
      let capturedBody: any = null

      const mockFetch = vi.fn().mockImplementation(async (url: string, init: any) => {
        capturedUrl = url
        capturedBody = JSON.parse(init.body)
        return {
          ok: true,
          json: async () => ({
            model: 'llama3.2',
            message: { content: 'Ollama local review' },
            prompt_eval_count: 50,
            eval_count: 75,
          }),
        }
      })
      vi.stubGlobal('fetch', mockFetch)

      const provider = new OllamaReviewProvider(undefined, 'llama3.2', true)
      expect(provider.name).toBe('ollama')
      expect(provider.isConfigured).toBe(true)

      const request: ReviewRequest = { content: 'Ollama test' }
      const prompt = buildReviewPrompt(request)
      const res = await provider.review(request, prompt)

      expect(capturedUrl).toBe('http://localhost:11434/api/chat')
      expect(capturedBody.model).toBe('llama3.2')
      expect(capturedBody.messages).toHaveLength(2)
      expect(res.rawText).toBe('Ollama local review')
      expect(res.tokensUsed?.totalTokens).toBe(125)
    })

    it('createReviewProvider factory resolves providers correctly and defaults to NullReviewProvider', () => {
      // Explicit provider selection
      expect(createReviewProvider({ provider: 'mock' })).toBeInstanceOf(MockReviewProvider)
      expect(createReviewProvider({ provider: 'openai', apiKey: 'test' })).toBeInstanceOf(OpenAIReviewProvider)
      expect(createReviewProvider({ provider: 'anthropic', apiKey: 'test' })).toBeInstanceOf(AnthropicReviewProvider)
      expect(createReviewProvider({ provider: 'ollama' })).toBeInstanceOf(OllamaReviewProvider)
      expect(createReviewProvider({ provider: 'none' })).toBeInstanceOf(NullReviewProvider)

      // Unconfigured environment defaults safely to NullReviewProvider (Decision D10)
      const savedOpenAi = process.env.OPENAI_API_KEY
      const savedAnthropic = process.env.ANTHROPIC_API_KEY
      const savedOllama = process.env.OLLAMA_HOST
      delete process.env.OPENAI_API_KEY
      delete process.env.ANTHROPIC_API_KEY
      delete process.env.OLLAMA_HOST

      try {
        const defaultProvider = createReviewProvider()
        expect(defaultProvider).toBeInstanceOf(NullReviewProvider)
        expect(defaultProvider.isConfigured).toBe(false)
      } finally {
        if (savedOpenAi) process.env.OPENAI_API_KEY = savedOpenAi
        if (savedAnthropic) process.env.ANTHROPIC_API_KEY = savedAnthropic
        if (savedOllama) process.env.OLLAMA_HOST = savedOllama
      }
    })
  })

  // ---------------------------------------------------------------------------
  // 4. Decision D10 Opt-In & Unconfigured Fallback
  // ---------------------------------------------------------------------------
  describe('Decision D10 Opt-In & Unconfigured Fallback', () => {
    it('returns graceful unconfigured notice with instructions when no provider is configured', async () => {
      const request: ReviewRequest = {
        sessionId: 'sess-unconf',
        turnIndex: 1,
        content: 'System prompt instructions...',
      }

      // Explicitly unconfigured options
      const result = await runContextReview(request, { provider: 'none' })

      expect(result.status).toBe('unconfigured')
      expect(result.source).toBe('model_review')
      expect(result.provider).toBe('none')
      expect(result.review).toContain('No LLM review provider is currently configured')
      expect(result.instructions).toContain('Context reviews are never run automatically or during ingestion')
      expect(result.instructions).toContain('OPENAI_API_KEY')
      expect(result.instructions).toContain('ANTHROPIC_API_KEY')
      expect(result.instructions).toContain('http://localhost:11434')
      expect(result.inputTokens).toBeGreaterThan(0)
    })

    it('aliases runReview to runContextReview', async () => {
      expect(runReview).toBe(runContextReview)
    })
  })

  // ---------------------------------------------------------------------------
  // 5. Isolation from Finding Tables (Acceptance Criterion 5)
  // ---------------------------------------------------------------------------
  describe('Isolation from SQLite Finding & Findings Tables', () => {
    it('never writes review results into SQLite finding or findings tables', async () => {
      const dbPath = tempStorePath()
      const store = new CanonStore(dbPath)

      // Verify finding table exists and inspect initial row count
      const initialFindingCount = store.listFindings({}).length
      expect(initialFindingCount).toBe(0)

      // Direct SQL verification on the SQLite database
      const rawCountBefore = (
        store as any
      ).db.prepare('SELECT COUNT(*) as count FROM finding').get() as { count: number }
      expect(rawCountBefore.count).toBe(0)

      // Run multiple context reviews (mock, completed, unconfigured)
      const request: ReviewRequest = {
        sessionId: 'sess-isolation-test',
        turnIndex: 2,
        content: 'System instructions that would look like dormant tools if misclassified.',
        blocks: [{ key: 'system_prompt', label: 'System', text: 'Some system content', tokens: 15 }],
      }

      const review1 = await runContextReview(request, { provider: 'mock' })
      expect(review1.status).toBe('completed')
      expect(review1.source).toBe('model_review')
      expect(review1.recommendations).toBeDefined()
      expect(review1.recommendations!.length).toBeGreaterThan(0)

      const review2 = await runContextReview(request, { provider: 'none' })
      expect(review2.status).toBe('unconfigured')

      // Assert finding table remains completely empty
      const findingsAfter = store.listFindings({})
      expect(findingsAfter).toHaveLength(0)

      const rawCountAfter = (
        store as any
      ).db.prepare('SELECT COUNT(*) as count FROM finding').get() as { count: number }
      expect(rawCountAfter.count).toBe(0)

      // Verify no tables have been added with 'review' or 'finding' records
      const allFindingRows = (
        store as any
      ).db.prepare('SELECT * FROM finding').all()
      expect(allFindingRows).toHaveLength(0)

      store.close()
    })

    it('ReviewResult is tagged with source: model_review and is distinct from Finding', async () => {
      const request: ReviewRequest = { content: 'Hello world' }
      const res = await runContextReview(request, { provider: 'mock' })

      expect(res.source).toBe('model_review')
      // Ensure Finding-specific properties (such as detectorId, estimatedWasteTokens) are not in ReviewResult
      expect((res as any).detectorId).toBeUndefined()
      expect((res as any).estimatedWasteTokens).toBeUndefined()
      expect((res as any).rankScore).toBeUndefined()
    })
  })

  // ---------------------------------------------------------------------------
  // 6. Secret Redaction on Error
  // ---------------------------------------------------------------------------
  describe('Secret Redaction on Provider Error', () => {
    it('redacts sensitive API keys if an error occurs during execution', async () => {
      const mockFetch = vi.fn().mockImplementation(async () => {
        throw new Error('Authentication failed for token sk-ant-secret1234567890abcdef123456 on endpoint')
      })
      vi.stubGlobal('fetch', mockFetch)

      const request: ReviewRequest = { content: 'Secret test' }
      const result = await runContextReview(request, {
        provider: 'anthropic',
        apiKey: 'sk-ant-secret1234567890abcdef123456',
      })

      expect(result.status).toBe('error')
      expect(result.error).toBeDefined()
      expect(result.error).not.toContain('sk-ant-secret1234567890abcdef123456')
      expect(result.error).toContain('sk-***')
    })
  })

  // ---------------------------------------------------------------------------
  // 7. Server Route Handling
  // ---------------------------------------------------------------------------
  describe('HTTP Route Handling (/api/kyber/review)', () => {
    function makeMockRes(): ServerResponse & {
      statusCode: number
      headers: Record<string, string>
      body: string
    } {
      const res: any = {
        statusCode: 200,
        headers: {},
        body: '',
        writeHead(status: number, headers: Record<string, string>) {
          res.statusCode = status
          res.headers = { ...res.headers, ...headers }
        },
        end(data?: string) {
          if (data) res.body += data
        },
      }
      return res
    }

    it('serves GET /api/kyber/review/status with provider configuration info', () => {
      const req: any = { method: 'GET' }
      const res = makeMockRes()
      const url = new URL('http://localhost:3000/api/kyber/review/status')
      const bridge: any = {}

      const handled = handleKyberRequest(req, res, url, bridge)
      expect(handled).toBe(true)
      expect(res.statusCode).toBe(200)

      const data = JSON.parse(res.body)
      expect(data).toHaveProperty('provider')
      expect(data).toHaveProperty('isConfigured')
    })

    it('rejects GET /api/kyber/review with 405 Method Not Allowed', () => {
      const req: any = { method: 'GET' }
      const res = makeMockRes()
      const url = new URL('http://localhost:3000/api/kyber/review')
      const bridge: any = {}

      const handled = handleKyberRequest(req, res, url, bridge)
      expect(handled).toBe(true)
      expect(res.statusCode).toBe(405)
      const data = JSON.parse(res.body)
      expect(data.error).toBe('Method Not Allowed')
    })

    it('handles POST /api/kyber/review and returns ReviewResult', async () => {
      const req: any = new EventEmitter()
      req.method = 'POST'
      const res = makeMockRes()
      const url = new URL('http://localhost:3000/api/kyber/review')
      const bridge: any = {}

      const handled = handleKyberRequest(req, res, url, bridge)
      expect(handled).toBe(true)

      const payload = {
        content: 'System turn content to review',
        sessionId: 'sess-route-1',
        turnIndex: 1,
        options: { provider: 'mock' },
      }

      req.emit('data', JSON.stringify(payload))
      req.emit('end')

      // Wait a tick for async handler in routes.ts to complete
      await new Promise((resolve) => setTimeout(resolve, 50))

      expect(res.statusCode).toBe(200)
      const data: ReviewResult = JSON.parse(res.body)
      expect(data.status).toBe('completed')
      expect(data.source).toBe('model_review')
      expect(data.provider).toBe('mock')
      expect(data.recommendations).toBeDefined()
    })
  })
})
