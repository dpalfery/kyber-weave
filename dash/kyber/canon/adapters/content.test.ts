import { describe, expect, it } from 'vitest'

import antigravitySpan from './__fixtures__/antigravity-span.json' with { type: 'json' }
import { canonicalContent, canonicalParts } from './copilot.js'
import { contentFromParts, type ContentPart } from '../types.js'

// The fixture is a real span's attribute map taken off the wire from
// ~/.kyberdash/canon.db: real key names, real value shapes, real counters.
// Every free-text body and identifier is replaced with synthetic filler,
// because R12.3 forbids committing captured content and git history is
// permanent — regenerate it with `node kyber/tools/capture-content-fixture.mjs`.
//
// The key names are what matters here. The mapping this replaces passed its
// own hand-written tests while reading `gen_ai.prompt`, an attribute none of
// the harnesses in the corpus emit, and so wrote `{}` for all 20,445 stored
// records. A fixture invented alongside the mapping cannot catch that class
// of error; one whose keys came off the wire can.

const attributes = antigravitySpan as Record<string, unknown>
const bucketsOf = (parts: readonly ContentPart[]) =>
  parts.reduce<Record<string, number>>((acc, part) => {
    acc[part.part] = (acc[part.part] ?? 0) + 1
    return acc
  }, {})

describe('canonicalParts — real Antigravity span', () => {
  it('fills the buckets the harness actually sends', () => {
    const buckets = bucketsOf(canonicalParts(attributes))

    expect(buckets.system_prompt).toBe(1)
    expect(buckets.tool_definitions).toBe(1)
    expect(buckets.instruction_context).toBeGreaterThanOrEqual(1)
    expect(buckets.conversation_history).toBeGreaterThanOrEqual(1)
  })

  it('carries the harness-reported count so the bucket is measured, not derived', () => {
    const system = canonicalParts(attributes).find((part) => part.part === 'system_prompt')

    expect(system?.tokens).toBe(attributes['gen_ai.usage.sys_tokens'])
    expect(system?.tokens).toBeGreaterThan(0)
  })

  it('does not count the system prompt twice', () => {
    // The harness sends the same text as `gen_ai.system_instructions` and as
    // the leading system-role message. Counting both inflates the bar past
    // the model's own reported input; the Python pipeline does exactly that.
    const history = canonicalParts(attributes)
      .filter((part) => part.part === 'conversation_history')
      .map((part) => part.text)
      .join('\n')
    const system = attributes['gen_ai.system_instructions'] as string

    expect(history).not.toContain(system.slice(0, 200))
  })

  it('attributes no MCP server when the payload names none', () => {
    // Antigravity sends bare `[{"name": "..."}]` — no schemas, no servers.
    // Those tokens belong in builtinToolDefinitionTokens, never in a guessed
    // per-server band (R8.3).
    const tools = canonicalParts(attributes).filter((part) => part.part === 'tool_definitions')

    expect(tools).toHaveLength(1)
    expect(tools[0]?.server).toBeUndefined()
    expect(tools[0]?.tokens).toBe(attributes['gen_ai.usage.tool_tokens'])
  })
})

describe('canonicalParts — attribution and absence', () => {
  it('emits one part per tool when definitions name their server', () => {
    const parts = canonicalParts({
      'gen_ai.tool.definitions': JSON.stringify([
        { name: 'search', server: 'context7' },
        { name: 'resolve', server: 'context7' },
        { name: 'explore', server: 'codegraph' },
      ]),
      'gen_ai.usage.tool_tokens': 4200,
    })

    expect(parts.map((part) => part.server)).toEqual(['context7', 'context7', 'codegraph'])
    // The aggregate is dropped rather than spread across three tools: one
    // total divided by N would fabricate the per-server figures the chart
    // then presents as measured.
    expect(parts.every((part) => part.tokens === undefined)).toBe(true)
  })

  it('never recovers a server by splitting a prefixed tool name', () => {
    const parts = canonicalParts({
      'gen_ai.tool.definitions': JSON.stringify([{ name: 'mcp__kyber-weave-m__docs_explore' }]),
    })

    expect(parts[0]?.server).toBeUndefined()
  })

  it('leaves a bucket absent rather than zero when the attribute is missing', () => {
    const parts = canonicalParts({ 'gen_ai.usage.input_tokens': 100 })

    expect(parts).toEqual([])
    expect(canonicalContent({ 'gen_ai.usage.input_tokens': 100 })).toEqual({})
  })

  it('buckets on part type, never on role', () => {
    const parts = canonicalParts({
      'gen_ai.input.messages': JSON.stringify([
        { role: 'user', parts: [{ type: 'tool_result', text: 'file contents' }] },
        { role: 'assistant', parts: [{ type: 'text', text: 'reply' }] },
      ]),
    })

    expect(parts.find((part) => part.text === 'file contents')?.part).toBe('tool_result_content')
    expect(parts.find((part) => part.text === 'reply')?.part).toBe('conversation_history')
  })

  it('prefers structured messages over a flattened prompt', () => {
    const parts = canonicalParts({
      'gen_ai.input.messages': JSON.stringify([
        { role: 'user', parts: [{ type: 'text', text: 'structured' }] },
      ]),
      'gen_ai.prompt': 'flattened',
    })

    expect(parts.map((part) => part.text)).toEqual(['structured'])
  })

  it('still reads a flattened prompt when it is all the harness sends', () => {
    expect(canonicalContent({ 'gen_ai.prompt': 'flattened' })).toEqual({
      conversation_history: 'flattened',
    })
  })
})

describe('contentFromParts', () => {
  it('collapses parts back into the flat map older consumers read', () => {
    const flat = contentFromParts(canonicalParts(attributes))

    expect(flat.system_prompt).toBe(attributes['gen_ai.system_instructions'])
    expect(Object.keys(flat).length).toBeGreaterThan(1)
  })

  it('joins parts of one bucket in order', () => {
    expect(
      contentFromParts([
        { part: 'conversation_history', text: 'second', order: 2 },
        { part: 'conversation_history', text: 'first', order: 1 },
      ]),
    ).toEqual({ conversation_history: 'first\nsecond' })
  })
})
