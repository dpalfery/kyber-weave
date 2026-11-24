// Unit contract for the pure turn-content view builder. The C# engine serves the
// byte-budgeted parts alone; blocks, part labels, and assembled text are built in
// the browser from those parts, so this module owns the mapping the retired
// TypeScript server used to own. Every fixture here is a served-parts array — no
// DOM, no fetch, no store.

import { describe, expect, it } from 'vitest'

import {
  assembleTurnContentText,
  buildTurnContentBlocks,
  buildTurnContentView,
  extractMessagesFromHistory,
  labelTurnContentParts,
  type TurnContentSourcePart,
} from './turnContentBlocks.js'

describe('extractMessagesFromHistory', () => {
  it('splits a JSON message array by role, taking string content', () => {
    const history = JSON.stringify([
      { role: 'user', content: 'first question' },
      { role: 'assistant', content: 'first answer' },
      { role: 'model', content: 'second answer' },
      { role: 'tool', content: 'ignored role' },
    ])

    expect(extractMessagesFromHistory(history)).toEqual({
      userMessages: ['first question'],
      assistantMessages: ['first answer', 'second answer'],
    })
  })

  it('reads message parts when a message carries no string content', () => {
    const history = JSON.stringify([
      { role: 'user', parts: [{ text: 'part one' }, 'plain part', { content: 'part three' }, { other: true }] },
    ])

    expect(extractMessagesFromHistory(history).userMessages).toEqual([
      'part one\nplain part\npart three\n{"other":true}',
    ])
  })

  it('falls back to prefixed dialog lines when the text is not JSON', () => {
    const history = 'user: first question\na middle line\nassistant: first answer\nuser: second question'

    expect(extractMessagesFromHistory(history)).toEqual({
      userMessages: ['first question\na middle line', 'second question'],
      assistantMessages: ['first answer'],
    })
  })

  it('yields nothing for text that is neither', () => {
    expect(extractMessagesFromHistory('the recorded factory prompt, plain text')).toEqual({
      userMessages: [],
      assistantMessages: [],
    })
    expect(extractMessagesFromHistory('')).toEqual({ userMessages: [], assistantMessages: [] })
  })
})

describe('labelTurnContentParts', () => {
  it('labels the canonical kinds and carries span, tokens, server, and truncation', () => {
    const parts = labelTurnContentParts([
      { spanId: 'span-1', part: 'system_prompt', text: 'Be helpful.', tokens: 12 },
      { spanId: 'span-1', part: 'instruction_context', text: 'Project context', tokens: 5 },
      { spanId: 'span-1', part: 'tool_result_content', text: 'read produced 42 lines', tokens: 7 },
      { spanId: 'span-1', part: 'system_prompt', text: 'cut short', truncated: true },
    ])

    expect(parts.map((p) => [p.id, p.part, p.label, p.order])).toEqual([
      ['part-sys-0', 'system_prompt', 'System Prompt', 1],
      ['part-inst-1', 'instruction_context', 'Instruction Context', 2],
      ['part-toolres-2', 'tool_result_content', 'Tool Results', 3],
      ['part-sys-3', 'system_prompt', 'System Prompt', 4],
    ])
    expect(parts[0]).toMatchObject({ spanId: 'span-1', text: 'Be helpful.', tokens: 12 })
    expect(parts[3]).toMatchObject({ truncated: true })
  })

  it('labels tool definitions by server and keeps non-residency', () => {
    const parts = labelTurnContentParts([
      { spanId: 'span-1', part: 'tool_definitions', text: '{"name":"a"}', server: 'mcp-alpha' },
      { spanId: 'span-1', part: 'tool_definitions', text: '{"name":"b"}' },
      { spanId: 'span-1', part: 'tool_definitions', text: '{"name":"c"}', server: 'mcp-beta', resident: false },
    ])

    expect(parts.map((p) => [p.part, p.label, p.resident])).toEqual([
      ['tool_definitions', 'Tools (mcp-alpha)', undefined],
      ['tool_definitions', 'Tools', undefined],
      ['tool_definitions', 'Tools (mcp-beta)', false],
    ])
  })

  it('splits conversation history into user and assistant parts with proportional tokens', () => {
    const history = JSON.stringify([
      { role: 'user', content: 'question one' },
      { role: 'assistant', content: 'answer one' },
      { role: 'assistant', content: 'answer two' },
    ])
    const parts = labelTurnContentParts([
      { spanId: 'span-1', part: 'conversation_history', text: history, tokens: 300 },
    ])

    expect(parts.map((p) => [p.id, p.part, p.label, p.text, p.tokens])).toEqual([
      ['part-user-0', 'user_messages', 'User Messages', 'question one', 100],
      ['part-asst-1', 'assistant_turns', 'Assistant Turns', 'answer one\n\nanswer two', 200],
    ])
    expect(parts.every((p) => p.spanId === 'span-1')).toBe(true)
  })

  it('emits user messages and the raw conversation history when no split is extractable', () => {
    const parts = labelTurnContentParts([
      { spanId: 'span-1', part: 'conversation_history', text: 'plain dialog', tokens: 9 },
    ])

    expect(parts.map((p) => [p.id, p.part, p.label, p.text, p.tokens])).toEqual([
      ['part-user-0', 'user_messages', 'User Messages', 'plain dialog', 9],
      ['part-conv-1', 'conversation_history', 'Conversation History', 'plain dialog', undefined],
    ])
    // The served tokens are counted once across the parts, not once per copy.
    expect(parts.reduce((sum, p) => sum + (p.tokens ?? 0), 0)).toBe(9)
  })

  it('splits served tokens so the parts sum to exactly the served count', () => {
    const history = JSON.stringify([
      { role: 'user', content: 'q' },
      { role: 'assistant', content: 'a' },
    ])
    for (const tokens of [1, 3, 7]) {
      const parts = labelTurnContentParts([{ part: 'conversation_history', text: history, tokens }])
      expect(parts.reduce((sum, p) => sum + (p.tokens ?? 0), 0)).toBe(tokens)
    }
  })

  it('reads a null message content as no message, never the text "null"', () => {
    const history = JSON.stringify([
      { role: 'user', content: null },
      { role: 'assistant', content: 'answer' },
    ])
    expect(extractMessagesFromHistory(history)).toEqual({ userMessages: [], assistantMessages: ['answer'] })
  })

  it('labels an unknown part kind by its key with underscores as spaces', () => {
    const parts = labelTurnContentParts([{ spanId: 'span-1', part: 'mystery_blob', text: 'blob bytes' }])

    expect(parts[0]).toMatchObject({ part: 'mystery_blob', label: 'mystery blob', text: 'blob bytes' })
  })
})

describe('buildTurnContentBlocks', () => {
  it('answers the five canonical blocks in order, empty ones included', () => {
    const blocks = buildTurnContentBlocks(labelTurnContentParts([]))

    expect(blocks.map((b) => [b.key, b.label, b.text, b.parts.length, b.tokens])).toEqual([
      ['system_prompt', 'System prompt', '', 0, undefined],
      ['tool_definitions', 'Tools', '', 0, undefined],
      ['instruction_context', 'Instruction context', '', 0, undefined],
      ['conversation_history', 'Conversation history', '', 0, undefined],
      ['tool_result_content', 'Tool results', '', 0, undefined],
    ])
  })

  it('joins member texts, sums resident tokens, and drops zero token sums', () => {
    const blocks = buildTurnContentBlocks(
      labelTurnContentParts([
        { spanId: 'span-1', part: 'system_prompt', text: 'first half', tokens: 10 },
        { spanId: 'span-1', part: 'system_prompt', text: 'second half', tokens: 15 },
        { spanId: 'span-1', part: 'tool_definitions', text: '{"name":"zeta"}', tokens: 0 },
        { spanId: 'span-1', part: 'tool_definitions', text: '{"name":"offered"}', resident: false, tokens: 400 },
      ]),
    )

    const system = blocks.find((b) => b.key === 'system_prompt')!
    expect(system.text).toBe('first half\n\nsecond half')
    expect(system.tokens).toBe(25)

    // A non-resident definition stays in the block's parts but adds no tokens, and a
    // block whose resident members measure nothing carries no token figure at all.
    const tools = blocks.find((b) => b.key === 'tool_definitions')!
    expect(tools.text).toBe('{"name":"zeta"}\n\n{"name":"offered"}')
    expect(tools.parts.map((p) => p.resident)).toEqual([undefined, false])
    expect(tools.tokens).toBeUndefined()
  })

  it('drops a conversation history part a user part already carries', () => {
    // The unextractable-history fallback emits the raw text twice: as user
    // messages and as conversation history. The block keeps it once.
    const blocks = buildTurnContentBlocks(
      labelTurnContentParts([{ spanId: 'span-1', part: 'conversation_history', text: 'plain dialog', tokens: 9 }]),
    )

    const history = blocks.find((b) => b.key === 'conversation_history')!
    expect(history.parts.map((p) => p.part)).toEqual(['user_messages'])
    expect(history.text).toBe('plain dialog')
    expect(history.tokens).toBe(9)
  })

  it('keeps a conversation history part no user part matches by text', () => {
    // The dedup pairs by exact text, never by kind alone: a history part whose
    // text no user part carries stays in the block with its own bytes.
    const history = buildTurnContentBlocks([
      { id: 'part-user-0', part: 'user_messages', label: 'User Messages', text: 'question' },
      { id: 'part-conv-1', part: 'conversation_history', label: 'Conversation History', text: 'raw transcript' },
    ]).find((b) => b.key === 'conversation_history')!

    expect(history.parts.map((p) => p.part)).toEqual(['user_messages', 'conversation_history'])
    expect(history.text).toBe('question\n\nraw transcript')
  })

  it('aggregates a split history without a raw conversation history part', () => {
    const history = buildTurnContentBlocks(
      labelTurnContentParts([
        {
          spanId: 'span-1',
          part: 'conversation_history',
          text: JSON.stringify([{ role: 'user', content: 'question' }]),
          tokens: 30,
        },
      ]),
    ).find((b) => b.key === 'conversation_history')!

    // The split emitted a user part alone — no raw history part exists to drop.
    expect(history.parts.map((p) => p.part)).toEqual(['user_messages'])
    expect(history.text).toBe('question')
    expect(history.tokens).toBe(30)
  })
})

describe('assembleTurnContentText', () => {
  it('heads each non-blank block section and joins them with a blank line', () => {
    const blocks = buildTurnContentBlocks(
      labelTurnContentParts([
        { spanId: 'span-1', part: 'system_prompt', text: '  Be helpful.  ', tokens: 12 },
        { spanId: 'span-1', part: 'conversation_history', text: 'plain dialog', tokens: 9 },
        { spanId: 'span-1', part: 'mystery_blob', text: 'not in any block' },
      ]),
    )

    expect(assembleTurnContentText(blocks)).toBe(
      '=== SYSTEM PROMPT ===\nBe helpful.\n\n=== CONVERSATION HISTORY ===\nplain dialog',
    )
  })

  it('answers empty text when no block carries content', () => {
    expect(assembleTurnContentText(buildTurnContentBlocks([]))).toBe('')
  })
})

describe('buildTurnContentView', () => {
  it('composes parts, blocks, and assembled text over a served-parts array', () => {
    const source: TurnContentSourcePart[] = [
      { spanId: 'span-1', part: 'system_prompt', text: 'Be helpful.', tokens: 12 },
      { spanId: 'span-1', part: 'conversation_history', text: 'user: hello\nassistant: hi', tokens: 40 },
      { spanId: 'span-1', part: 'mystery_blob', text: 'blob bytes', server: 'x', resident: false, truncated: true },
    ]

    const view = buildTurnContentView(source)

    // The unknown kind stays a labelled part (the drawer renders it under its own
    // header) while the canonical blocks carry only their own members.
    expect(view.parts.map((p) => p.part)).toEqual([
      'system_prompt',
      'user_messages',
      'assistant_turns',
      'mystery_blob',
    ])
    expect(view.blocks.find((b) => b.key === 'conversation_history')!.text).toBe('hello\n\nhi')
    expect(view.assembledText).toBe(
      '=== SYSTEM PROMPT ===\nBe helpful.\n\n=== CONVERSATION HISTORY ===\nhello\n\nhi',
    )
  })
})
