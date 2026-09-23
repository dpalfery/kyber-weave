// Context composition and pressure (R7). A turn's input buckets by part type
// across the five canonical buckets — never by message role, which the input
// type cannot even express; instruction fences embedded in history and tool
// results are stripped into the instruction bucket; tool definitions resolve
// to MCP servers only through ground truth, never a split of the prefixed
// name; the gap the buckets leave is an explicit residual attributed to
// tokenizer drift only when a derived count could actually cause it;
// headroom, accumulation and pressure are against the context limit; and a
// sharp fresh-input rise between consecutive turns is flagged. A source with
// no message structure is not measurable and is never handed a residual.

import { describe, expect, it } from 'vitest'

import { CANONICAL_CONTENT_KEYS, type CanonicalContentKey } from '../canon/types.js'
import {
  analyzeContext,
  type ContextAnalysis,
  type ContextPart,
  type ContextTurn,
} from './context.js'

const LIMIT = 100_000

function part(part: CanonicalContentKey, tokens: number, overrides: Partial<ContextPart> = {}): ContextPart {
  // Measured counts everywhere by default: the arithmetic under test is the
  // module's, not the tokenizer's. Derived counts appear only where a test
  // is about derivation.
  return { part, text: 'x'.repeat(tokens * 4), tokens, ...overrides }
}

function turn(overrides: Partial<ContextTurn> = {}): ContextTurn {
  return { parts: [], inputTokens: 0, freshInput: 0, ...overrides }
}

/** Narrow a result to its measurable variant, failing loudly otherwise. */
function expectMeasurable(result: ContextAnalysis) {
  if (!result.measurable) {
    throw new Error(`expected a measurable analysis, got not measurable (${result.reason})`)
  }
  return result
}

/** A deterministic counter for derivation tests: one token per character. */
const charsAsTokens = (text: string): number => text.length

describe('analyzeContext — buckets by part type (R7.1)', () => {
  it('lands each of the five part types in its own bucket', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [
              part('system_prompt', 1_000),
              part('tool_definitions', 2_000),
              part('instruction_context', 500),
              part('conversation_history', 3_000),
              part('tool_result_content', 4_000),
            ],
            inputTokens: 10_500,
            freshInput: 10_500,
          }),
        ],
        { contextLimit: LIMIT }
      )
    )

    expect(result.turns[0].buckets).toEqual({
      system_prompt: 1_000,
      tool_definitions: 2_000,
      instruction_context: 500,
      conversation_history: 3_000,
      tool_result_content: 4_000,
    })
    expect(result.turns[0].bucketedTokens).toBe(10_500)
  })

  it('sums multiple parts of one type into that type alone', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [part('tool_result_content', 120), part('tool_result_content', 80)],
            inputTokens: 200,
            freshInput: 200,
          }),
        ],
        { contextLimit: LIMIT }
      )
    )

    expect(result.turns[0].buckets.tool_result_content).toBe(200)
    expect(result.turns[0].buckets.conversation_history).toBe(0)
  })
})

describe('analyzeContext — never by role (R7.2)', () => {
  it('exposes exactly the five part-type keys and no role anywhere', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [part('system_prompt', 10), part('conversation_history', 20)],
            inputTokens: 30,
            freshInput: 30,
          }),
        ],
        { contextLimit: LIMIT }
      )
    )

    for (const composition of result.turns) {
      expect(Object.keys(composition.buckets).sort()).toEqual([...CANONICAL_CONTENT_KEYS].sort())
    }
  })

  it('buckets by part type even where a harness would have used different roles', () => {
    // The same two texts, delivered in whichever message a harness chose:
    // a system prompt carried by a user-role message and history echoed by
    // an assistant-role one. Part type decides the bucket; role cannot.
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [
              part('system_prompt', 700),
              part('conversation_history', 1_300),
              part('tool_result_content', 900),
            ],
            inputTokens: 2_900,
            freshInput: 2_900,
          }),
        ],
        { contextLimit: LIMIT }
      )
    )

    expect(result.turns[0].buckets.system_prompt).toBe(700)
    expect(result.turns[0].buckets.conversation_history).toBe(1_300)
    expect(result.turns[0].buckets.tool_result_content).toBe(900)
  })
})

describe('analyzeContext — instruction-block stripping (R7.1)', () => {
  it('moves fenced instruction blocks out of history into the instruction bucket', () => {
    // Measured carrier tokens are irrelevant once the carrier splits: the
    // pieces are derived, which is the honest accounting and is what the
    // residual-attribution test below leans on.
    const history =
      'before<system-reminder>do this</system-reminder>middle<system-reminder>and this</system-reminder>after'

    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [part('conversation_history', 999, { text: history })],
            inputTokens: 100,
            freshInput: 100,
          }),
        ],
        { contextLimit: LIMIT, countTokens: charsAsTokens }
      )
    )

    const composition = result.turns[0]
    // 'before' + 'middle' + 'after' = 16 chars stay in history.
    expect(composition.buckets.conversation_history).toBe('beforemiddleafter'.length)
    // 'do this' + 'and this' = 15 chars move to instruction context.
    expect(composition.buckets.instruction_context).toBe('do thisand this'.length)
    expect(composition.strippedInstructionBlocks).toEqual({
      count: 2,
      tokens: 'do thisand this'.length,
    })
  })

  it('strips fences out of tool results too, and reports the derived split', () => {
    const file = 'line1\nline2<system-reminder>tool ran</system-reminder>line3'

    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [part('tool_result_content', 999, { text: file })],
            inputTokens: 100,
            freshInput: 100,
          }),
        ],
        { contextLimit: LIMIT, countTokens: charsAsTokens }
      )
    )

    expect(result.turns[0].buckets.tool_result_content).toBe('line1\nline2line3'.length)
    expect(result.turns[0].buckets.instruction_context).toBe('tool ran'.length)
    expect(result.turns[0].strippedInstructionBlocks.count).toBe(1)
    expect(result.derivedCounts).toBe(true)
  })

  it('keeps an unterminated fence as carrier content rather than dropping it', () => {
    const text = 'visible<system-reminder>never closed'

    const result = expectMeasurable(
      analyzeContext(
        [turn({ parts: [part('conversation_history', 42, { text })], inputTokens: 42, freshInput: 42 })],
        { contextLimit: LIMIT }
      )
    )

    // The measured whole-part count is used: with no close marker there is
    // no split to derive, so nothing was stripped and nothing was lost.
    expect(result.turns[0].buckets.conversation_history).toBe(42)
    expect(result.turns[0].strippedInstructionBlocks).toEqual({ count: 0, tokens: 0 })
    expect(result.turns[0].residual.attribution).toBe('unattributed')
  })

  it('honours harness-specific markers when they are supplied', () => {
    const text = 'a[INST]beware[/INST]c'

    const result = expectMeasurable(
      analyzeContext(
        [turn({ parts: [part('conversation_history', 999, { text })], inputTokens: 10, freshInput: 10 })],
        {
          contextLimit: LIMIT,
          countTokens: charsAsTokens,
          instructionMarkers: { open: '[INST]', close: '[/INST]' },
        }
      )
    )

    expect(result.turns[0].buckets.conversation_history).toBe('ac'.length)
    expect(result.turns[0].buckets.instruction_context).toBe('beware'.length)
  })
})

describe('analyzeContext — MCP server resolution', () => {
  it('groups tool definitions by ground-truth server and sums same-server parts', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [
              part('tool_definitions', 300, { server: 'github' }),
              part('tool_definitions', 200, { server: 'github' }),
              part('tool_definitions', 150, { server: 'postgres' }),
            ],
            inputTokens: 650,
            freshInput: 650,
          }),
        ],
        { contextLimit: LIMIT }
      )
    )

    expect(result.turns[0].toolDefinitionsByServer.get('github')).toBe(500)
    expect(result.turns[0].toolDefinitionsByServer.get('postgres')).toBe(150)
    expect(result.turns[0].buckets.tool_definitions).toBe(650)
  })

  it('reports built-in definitions separately instead of guessing a server for them', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [part('tool_definitions', 400, { server: undefined }), part('tool_definitions', 100, { server: 'fs' })],
            inputTokens: 500,
            freshInput: 500,
          }),
        ],
        { contextLimit: LIMIT }
      )
    )

    expect(result.turns[0].builtinToolDefinitionTokens).toBe(400)
    expect([...result.turns[0].toolDefinitionsByServer.keys()]).toEqual(['fs'])
  })

  it('never splits a tool name to guess its server', () => {
    // A server whose own name contains the harness delimiter: ground truth
    // keeps it whole. The part carries no `server` field, so a split-on-
    // delimiter implementation would have manufactured a group named
    // `kyber`; this module counts it as built-in and invents nothing.
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [part('tool_definitions', 250, { server: 'kyber__weave' })],
            inputTokens: 250,
            freshInput: 250,
          }),
        ],
        { contextLimit: LIMIT }
      )
    )

    expect(result.turns[0].toolDefinitionsByServer.get('kyber__weave')).toBe(250)
  })
})

describe('analyzeContext — the residual is explicit (R7.3)', () => {
  it('exposes the gap between measured input and bucketed tokens, unattributed when all counts are measured', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [part('system_prompt', 800), part('conversation_history', 1_000)],
            inputTokens: 2_500, // 700 tokens the buckets do not account for
            freshInput: 2_500,
          }),
        ],
        { contextLimit: LIMIT }
      )
    )

    expect(result.turns[0].residual).toEqual({ tokens: 700, attribution: 'unattributed' })
    expect(result.residualTotal).toBe(700)
  })

  it('attributes the residual to tokenizer drift only where a derived count caused it', () => {
    // Same 700-token gap, but this time one part carried no measured count
    // and was tokenized: the gap is now genuinely the tokenizer's.
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({
            parts: [
              part('system_prompt', 800),
              part('conversation_history', 0, { tokens: undefined, text: 'four thousand unmeasured characters' }),
            ],
            inputTokens: 2_500,
            freshInput: 2_500,
          }),
        ],
        { contextLimit: LIMIT, countTokens: charsAsTokens }
      )
    )

    expect(result.derivedCounts).toBe(true)
    expect(result.turns[0].residual.attribution).toBe('tokenizer_drift')
    expect(result.turns[0].buckets.conversation_history).toBe('four thousand unmeasured characters'.length)
  })

  it('exposes a negative residual raw rather than clamping it away', () => {
    const result = expectMeasurable(
      analyzeContext(
        [turn({ parts: [part('system_prompt', 1_200)], inputTokens: 1_000, freshInput: 1_000 })],
        { contextLimit: LIMIT }
      )
    )

    expect(result.turns[0].residual.tokens).toBe(-200)
    expect(result.residualTotal).toBe(-200)
  })

  it('sums per-turn residuals into the session total', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({ parts: [part('system_prompt', 100)], inputTokens: 150, freshInput: 150 }),
          turn({ parts: [part('system_prompt', 100)], inputTokens: 180, freshInput: 30 }),
        ],
        { contextLimit: LIMIT }
      )
    )

    expect(result.turns.map((composition) => composition.residual.tokens)).toEqual([50, 80])
    expect(result.residualTotal).toBe(130)
  })
})

describe('analyzeContext — headroom, accumulation, pressure (R7.4)', () => {
  const SESSION = [
    turn({ parts: [part('system_prompt', 10_000)], inputTokens: 10_000, freshInput: 10_000 }),
    turn({
      parts: [part('system_prompt', 10_000), part('conversation_history', 5_000)],
      inputTokens: 15_000,
      freshInput: 5_000,
    }),
    turn({
      parts: [part('system_prompt', 10_000), part('conversation_history', 15_000)],
      inputTokens: 25_000,
      freshInput: 10_000,
    }),
  ]

  it('computes headroom and pressure against the context limit per turn', () => {
    const result = expectMeasurable(analyzeContext(SESSION, { contextLimit: 100_000 }))

    expect(result.turns.map((composition) => composition.headroom)).toEqual([90_000, 85_000, 75_000])
    expect(result.turns.map((composition) => composition.pressure)).toEqual([0.1, 0.15, 0.25])
  })

  it('reports per-turn accumulation and the session rate', () => {
    const result = expectMeasurable(analyzeContext(SESSION, { contextLimit: 100_000 }))

    // The first turn grew from nothing, so its rate is its whole input.
    expect(result.turns.map((composition) => composition.accumulationRate)).toEqual([10_000, 5_000, 10_000])
    expect(result.sessionAccumulationRate).toBe((25_000 - 10_000) / 2)
  })

  it('a single-turn session reports its input as both its rate and the session rate', () => {
    const result = expectMeasurable(
      analyzeContext([SESSION[0]], { contextLimit: 100_000 })
    )

    expect(result.turns[0].accumulationRate).toBe(10_000)
    expect(result.sessionAccumulationRate).toBe(10_000)
  })
})

describe('analyzeContext — flagging sharp fresh-input rises (R7.5)', () => {
  const LIMIT_OPTS = { contextLimit: LIMIT }

  it('flags a turn whose fresh input reaches the factor, and reports the factor', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({ parts: [part('system_prompt', 10)], inputTokens: 10, freshInput: 1_000 }),
          turn({ parts: [part('system_prompt', 10)], inputTokens: 20, freshInput: 2_000 }),
        ],
        LIMIT_OPTS
      )
    )

    expect(result.flaggedTurns).toEqual([2])
    expect(result.turns[1].freshInputJump).toEqual({ previous: 1_000, factor: 2 })
    expect(result.turns[0].freshInputJump).toBeUndefined()
  })

  it('does not flag a rise just under the factor', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({ parts: [part('system_prompt', 10)], inputTokens: 10, freshInput: 1_001 }),
          turn({ parts: [part('system_prompt', 10)], inputTokens: 20, freshInput: 2_001 }),
        ],
        LIMIT_OPTS
      )
    )

    expect(result.flaggedTurns).toEqual([])
  })

  it('a zero-to-small first burst is not flagged; a zero-to-large burst is', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({ parts: [part('system_prompt', 10)], inputTokens: 10, freshInput: 0 }),
          // 12 tokens of greeting: below the 1_000 floor.
          turn({ parts: [part('system_prompt', 10)], inputTokens: 12, freshInput: 12 }),
          // A cold-cache burst: at the floor from zero, flagged.
          turn({ parts: [part('system_prompt', 10)], inputTokens: 10_012, freshInput: 10_000 }),
        ],
        LIMIT_OPTS
      )
    )

    expect(result.flaggedTurns).toEqual([3])
    expect(result.turns[2].freshInputJump).toEqual({ previous: 12, factor: 10_000 / 12 })
  })

  it('honours a custom factor and floor', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({ parts: [part('system_prompt', 10)], inputTokens: 10, freshInput: 4_000 }),
          turn({ parts: [part('system_prompt', 10)], inputTokens: 20, freshInput: 8_000 }),
          turn({ parts: [part('system_prompt', 10)], inputTokens: 30, freshInput: 0 }),
          turn({ parts: [part('system_prompt', 10)], inputTokens: 40, freshInput: 500 }),
        ],
        { contextLimit: LIMIT, freshJumpFactor: 3, zeroPreviousFloor: 100 }
      )
    )

    // 8_000 is only 2x of 4_000: not sharp at factor 3. 500 from zero is
    // above the custom 100-token floor: sharp.
    expect(result.flaggedTurns).toEqual([4])
    expect(result.turns[1].freshInputJump).toBeUndefined()
    expect(result.turns[3].freshInputJump).toEqual({ previous: 0, factor: Infinity })
  })
})

describe('analyzeContext — no message structure (R7.6)', () => {
  it('reports not measurable for a session whose turns supply no parts', () => {
    const result = analyzeContext(
      [
        turn({ inputTokens: 5_000, freshInput: 5_000 }),
        turn({ inputTokens: 6_000, freshInput: 1_000 }),
      ],
      { contextLimit: LIMIT }
    )

    expect(result).toEqual({
      measurable: false,
      reason: 'no_message_structure',
      turns: 2,
      contextLimit: LIMIT,
    })
  })

  it('answers an empty session the same way, not with an all-zero chart', () => {
    const result = analyzeContext([], { contextLimit: LIMIT })

    expect(result.measurable).toBe(false)
    if (!result.measurable) {
      // Narrowed: the not-measurable variant carries no residual a surface
      // could chart and read as tokenizer drift — by type, not convention.
      expect(result).not.toHaveProperty('residualTotal')
      expect(result).not.toHaveProperty('turns.0.buckets')
    }
  })

  it('is measurable when only one turn of the session carries parts', () => {
    const result = expectMeasurable(
      analyzeContext(
        [
          turn({ inputTokens: 0, freshInput: 0 }),
          turn({ parts: [part('system_prompt', 50)], inputTokens: 50, freshInput: 50 }),
        ],
        { contextLimit: LIMIT }
      )
    )

    // The part-less turn stays honest: nothing bucketed, everything residual,
    // and unattributed — its counts were measured, so drift is not the cause.
    expect(result.turns[0].bucketedTokens).toBe(0)
    expect(result.turns[0].residual).toEqual({ tokens: 0, attribution: 'unattributed' })
    expect(result.turns[1].buckets.system_prompt).toBe(50)
  })
})
