import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  CursorHookTurnAssembler,
  DEFAULT_CURSOR_HOOK_OTLP_ENDPOINT,
  postCursorHookOtlpJson,
  runCursorHookStdin,
  toCursorHookOtlpTrace,
  type CursorHookEvent,
} from './cursor-hook.js'

const SESSION_ID = 'cursor-session-9f48'
const TURN_ID = 'turn-0042'

afterEach(() => {
  vi.unstubAllGlobals()
})

function completeTurnEvents(): CursorHookEvent[] {
  return [
    {
      type: 'agent_turn.started',
      sessionId: SESSION_ID,
      turnId: TURN_ID,
      timestamp: '2026-09-04T20:12:00.000Z',
      prompt: 'Add a test for the Cursor hook exporter.',
      promptTokens: 21,
      schemaTokens: 34,
    },
    {
      type: 'tool.started',
      sessionId: SESSION_ID,
      turnId: TURN_ID,
      toolCallId: 'tool-1',
      toolName: 'codegraph_explore',
      timestamp: '2026-09-04T20:12:01.000Z',
    },
    {
      type: 'tool.completed',
      sessionId: SESSION_ID,
      turnId: TURN_ID,
      toolCallId: 'tool-1',
      timestamp: '2026-09-04T20:12:02.000Z',
    },
    {
      type: 'tool.started',
      sessionId: SESSION_ID,
      turnId: TURN_ID,
      toolCallId: 'tool-2',
      toolName: 'apply_patch',
      timestamp: '2026-09-04T20:12:03.000Z',
    },
    {
      type: 'agent_turn.completed',
      sessionId: SESSION_ID,
      turnId: TURN_ID,
      timestamp: '2026-09-04T20:12:05.000Z',
      outputTokens: 55,
      reasoningTokens: 13,
    },
  ]
}

describe('Cursor hook → OTLP trace export', () => {
  it('assembles a single idempotent trace per completed agent turn with stable identity, counts, and tool order', () => {
    const assembler = new CursorHookTurnAssembler()
    const turns = completeTurnEvents().flatMap((event) => assembler.accept(event))

    // Cursor can redeliver a completion hook; it must not manufacture a second
    // analysis turn or a duplicate OTLP trace.
    const duplicate = completeTurnEvents().at(-1)
    if (duplicate === undefined) throw new Error('fixture must end with a completion event')
    expect(assembler.accept(duplicate)).toEqual([])

    expect(turns).toHaveLength(1)
    expect(turns[0]).toMatchObject({
      sessionId: SESSION_ID,
      turnId: TURN_ID,
      promptTokens: 21,
      schemaTokens: 34,
      outputTokens: 55,
      reasoningTokens: 13,
      tools: [
        { toolCallId: 'tool-1', name: 'codegraph_explore', order: 0 },
        { toolCallId: 'tool-2', name: 'apply_patch', order: 1 },
      ],
    })

    const spans = toCursorHookOtlpTrace(turns[0])
    expect(new Set(spans.map((span) => span.traceId))).toEqual(
      new Set([spans[0]?.traceId]),
    )
    expect(spans.filter((span) => span.parentSpanId === null)).toHaveLength(1)
    expect(spans[0]).toMatchObject({
      name: 'cursor.agent_turn',
      attributes: {
        'cursor.session.id': SESSION_ID,
        'cursor.turn.id': TURN_ID,
        'gen_ai.usage.input_tokens': 21,
        'codeburn.schema_tokens': 34,
        'gen_ai.usage.output_tokens': 55,
        'gen_ai.usage.reasoning_tokens': 13,
      },
    })
    expect(spans.filter((span) => span.name === 'cursor.tool')).toMatchObject([
      { attributes: { 'cursor.tool.name': 'codegraph_explore', 'cursor.tool.order': 0 } },
      { attributes: { 'cursor.tool.name': 'apply_patch', 'cursor.tool.order': 1 } },
    ])
  })

  it('declares unexported prompt and schema data not measurable rather than fabricating zero counts', () => {
    const assembler = new CursorHookTurnAssembler()
    const gapEvents = [
      {
        type: 'agent_turn.started',
        sessionId: SESSION_ID,
        turnId: 'turn-with-gaps',
        timestamp: '2026-09-04T20:14:00.000Z',
      },
      {
        type: 'agent_turn.completed',
        sessionId: SESSION_ID,
        turnId: 'turn-with-gaps',
        timestamp: '2026-09-04T20:14:01.000Z',
      },
    ] satisfies CursorHookEvent[]
    const turn = gapEvents.flatMap((event) => assembler.accept(event))[0]

    if (turn === undefined) throw new Error('completion should emit one synthetic turn')
    const root = toCursorHookOtlpTrace(turn).find((span) => span.parentSpanId === null)
    if (root === undefined) throw new Error('a completed turn must export a root OTLP span')

    expect(root.attributes).not.toHaveProperty('gen_ai.usage.input_tokens', 0)
    expect(root.attributes).not.toHaveProperty('codeburn.schema_tokens', 0)
    expect(root.attributes).toMatchObject({
      'codeburn.measurability.prompt_tokens': {
        availability: 'not_measurable',
        reason: expect.stringMatching(/cursor hook|not export|unavailable/i),
      },
      'codeburn.measurability.schema_tokens': {
        availability: 'not_measurable',
        reason: expect.stringMatching(/cursor hook|not export|unavailable/i),
      },
    })
  })

  it('posts each completed turn through the injectable local-delivery seam before writing OTLP JSON', async () => {
    const lines = completeTurnEvents().map((event) => JSON.stringify(event))
    const written: string[] = []
    const posted: Record<string, unknown>[] = []

    await runCursorHookStdin({
      stdin: lines.join('\n'),
      write: (line) => written.push(line),
      post: async (payload) => { posted.push(payload) },
    })

    expect(written).toHaveLength(1)
    expect(posted).toEqual([JSON.parse(written[0])])
    expect(JSON.parse(written[0])).toMatchObject({
      resourceSpans: [
        {
          resource: {
            attributes: expect.arrayContaining([
              expect.objectContaining({ key: 'service.name' }),
            ]),
          },
          scopeSpans: [expect.objectContaining({ spans: expect.any(Array) })],
        },
      ],
    })
  })

  it('POSTs OTLP JSON to the local trace receiver', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response('', { status: 200 }))
    vi.stubGlobal('fetch', fetch)
    const payload = { resourceSpans: [] }

    await postCursorHookOtlpJson(payload)

    expect(fetch).toHaveBeenCalledWith(DEFAULT_CURSOR_HOOK_OTLP_ENDPOINT, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(payload),
    })
  })
})
