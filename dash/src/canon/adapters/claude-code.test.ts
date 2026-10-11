import { describe, expect, it } from 'vitest'

import {
  claudeApiRequestBodyFailureReason,
  parseClaudeApiRequestBody,
} from './claude-code.js'

/**
 * The Claude Code API request body, as the harness emits it once
 * `OTEL_LOG_RAW_API_BODIES=1` is set (plan
 * plans/2026-10-10-kyberdash-claude-code-turn-content, T1/T2, decision B5).
 *
 * The body is the only live route to Claude Code context. Its spans carry
 * counters and metadata but no content, and its on-disk transcripts never
 * carry a system prompt or tool definitions. Reading only `system` and
 * `tools` therefore left every turn's conversation history empty even when
 * the sender had opted in — the defect this file pins. The mapping it asserts
 * mirrors `copilot.ts` `messageParts`, so one bucketing rule covers both
 * harnesses.
 *
 * Every payload here is synthetic: no captured prompt text, and nothing read
 * from `~/.claude`.
 */

const SYSTEM_TEXT = 'Synthetic system policy for the request body.'

const TOOL_DEFINITION = {
  name: 'search_repository',
  server: 'synthetic-mcp-server',
  description: 'Search a synthetic repository.',
  input_schema: {
    type: 'object',
    properties: { query: { type: 'string' } },
    required: ['query'],
  },
}

const TOOL_USE = {
  type: 'tool_use',
  id: 'toolu_synthetic_1',
  name: 'search_repository',
  input: { query: 'synthetic query' },
}

/** Stands in for base64 payloads; their presence in any part is the failure. */
const IMAGE_DATA = 'c3ludGhldGljLWltYWdlLWJ5dGVz'
const DOCUMENT_DATA = 'c3ludGhldGljLWRvY3VtZW50LWJ5dGVz'
const THINKING_TEXT = 'Synthetic redacted reasoning.'

const REQUEST = {
  system: SYSTEM_TEXT,
  tools: [TOOL_DEFINITION],
  messages: [
    { role: 'user', content: 'Synthetic user prompt.' },
    { role: 'user', content: [{ type: 'text', text: 'Synthetic user text block.' }] },
    {
      role: 'assistant',
      content: [{ type: 'text', text: 'Synthetic assistant reply.' }, TOOL_USE],
    },
    {
      role: 'user',
      content: [
        { type: 'tool_result', tool_use_id: TOOL_USE.id, content: 'Synthetic tool result.' },
      ],
    },
    {
      role: 'user',
      content: [
        {
          type: 'image',
          source: { type: 'base64', media_type: 'image/png', data: IMAGE_DATA },
        },
      ],
    },
    { role: 'assistant', content: [{ type: 'thinking', thinking: THINKING_TEXT }] },
  ],
}

describe('parseClaudeApiRequestBody — messages', () => {
  it('maps request messages onto the B5 buckets in message order', () => {
    const parts = parseClaudeApiRequestBody(JSON.stringify(REQUEST))

    expect(parts.map((part) => part.part)).toEqual([
      'system_prompt',
      'tool_definitions',
      'conversation_history',
      'conversation_history',
      'conversation_history',
      'conversation_history',
      'tool_result_content',
      'conversation_history',
    ])
    expect(parts.map((part) => part.text)).toEqual([
      SYSTEM_TEXT,
      JSON.stringify(TOOL_DEFINITION),
      'Synthetic user prompt.',
      'Synthetic user text block.',
      'Synthetic assistant reply.',
      JSON.stringify(TOOL_USE),
      'Synthetic tool result.',
      '[image block]',
    ])
  })

  it('keeps a tool_use block as JSON rather than inventing text for it', () => {
    const parts = parseClaudeApiRequestBody(JSON.stringify(REQUEST))
    const toolUse = parts.find((part) => part.text.includes(TOOL_USE.id))

    expect(toolUse?.part).toBe('conversation_history')
    expect(JSON.parse(toolUse?.text ?? '')).toEqual(TOOL_USE)
  })

  it('replaces image and document blocks with a placeholder, never their bytes', () => {
    const parts = parseClaudeApiRequestBody(JSON.stringify(REQUEST))
    const withDocument = parseClaudeApiRequestBody(
      JSON.stringify({
        messages: [
          {
            role: 'user',
            content: [
              {
                type: 'document',
                source: { type: 'base64', media_type: 'application/pdf', data: DOCUMENT_DATA },
              },
            ],
          },
        ],
      }),
    )

    expect(parts.some((part) => part.text === '[image block]')).toBe(true)
    expect(withDocument.map((part) => part.text)).toEqual(['[document block]'])
    expect(parts.some((part) => part.text.includes(IMAGE_DATA))).toBe(false)
    expect(withDocument.some((part) => part.text.includes(DOCUMENT_DATA))).toBe(false)
  })

  it('collapses an unknown block shape carrying base64 bytes to a placeholder, never the bytes', () => {
    // An unanticipated block type must not smuggle its bytes through the
    // generic JSON fallback: bytes are payload on any block shape, not only
    // on the shapes the mapping anticipated. Direct `data` and the nested
    // `source.data` shape both collapse.
    const parts = parseClaudeApiRequestBody(
      JSON.stringify({
        messages: [
          {
            role: 'user',
            content: [
              { type: 'gui_screenshot', data: IMAGE_DATA },
              {
                type: 'attachment',
                source: { type: 'base64', media_type: 'application/pdf', data: DOCUMENT_DATA },
              },
            ],
          },
        ],
      }),
    )

    expect(parts.map((part) => part.text)).toEqual(['[gui_screenshot block]', '[attachment block]'])
    expect(parts.some((part) => part.text.includes(IMAGE_DATA))).toBe(false)
    expect(parts.some((part) => part.text.includes(DOCUMENT_DATA))).toBe(false)
  })

  it('skips thinking blocks and never invents an instruction_context split', () => {
    const parts = parseClaudeApiRequestBody(JSON.stringify(REQUEST))
    const texts = parts.map((part) => part.text)

    // The non-thinking blocks in the same message must survive, so the two
    // negative assertions below cannot pass by the payload mapping nothing.
    expect(texts).toContain('Synthetic assistant reply.')
    expect(texts.some((text) => text.includes(THINKING_TEXT))).toBe(false)
    expect(parts.some((part) => part.part === 'instruction_context')).toBe(false)
  })
})

describe('claudeApiRequestBodyFailureReason', () => {
  it('names the content-length setting for a body cut off mid-document — it still begins like JSON', () => {
    const body =
      '{"system":"Synthetic system policy for the request body.","messages":[{"role":"user","content":"Synth'
    expect(parseClaudeApiRequestBody(body)).toEqual([])
    expect(claudeApiRequestBodyFailureReason(body)).toContain(
      'CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH',
    )
  })

  it.each([
    ['a never-JSON string', 'not a request body at all {'],
    ['an empty string', ''],
    ['a whitespace-only string', '   \n  '],
  ])('gives %s a malformed-body reason that does not name the length knob', (_label, body) => {
    // No knob fixes input that was never JSON: the actionable reason is the
    // malformed body itself, so the truncation hint would be noise here.
    expect(parseClaudeApiRequestBody(body)).toEqual([])
    const reason = claudeApiRequestBodyFailureReason(body)
    expect(reason).toBeDefined()
    expect(reason).not.toContain('CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH')
  })

  it('reports no reason for a body it parsed', () => {
    expect(claudeApiRequestBodyFailureReason(JSON.stringify(REQUEST))).toBeUndefined()
  })
})
