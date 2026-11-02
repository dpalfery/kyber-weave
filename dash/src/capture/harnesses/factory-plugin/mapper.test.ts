// Hook-to-span mapping for the Factory (Droid) capture plugin (contract 1): the eight documented
// hook events onto the four `factory.*` span names, the attributes each event's row of the
// contract carries, and every attribute the payload does not carry left off the wire.
//
// The mapper module is written by the next task, so every test here fails until mapper.ts exists.
// The module is loaded inside each test rather than imported at the top of the file, so the RED
// proof is per test: each one fails on the missing module and names the guarantee it was pinning,
// instead of the whole file failing to collect as one error.
//
// The contract this file pins:
//   - the mapper takes the raw hook payload as a record of unknown values, plus an options object
//     whose `contentAllowed` flag gates the prompt text;
//   - a byte count is the size of the payload value as it travels on the wire, and it is present
//     exactly when the payload carries the value it counts;
//   - a tool outcome travels in the payload (`tool_status`, `tool_error_type`); the mapper never
//     invents an outcome the payload did not state.

import { describe, expect, it } from 'vitest'

const SESSION = 'ses_factory_01'
const PARENT_SESSION = 'ses_factory_parent'
const MESSAGE = 'msg_factory_01'
const CALL = 'call_factory_01'

/** The eight hook events Factory documents, in the order the contract lists them. */
const HOOK_EVENTS = [
  'SessionStart',
  'UserPromptSubmit',
  'PreToolUse',
  'PostToolUse',
  'Stop',
  'SubagentStop',
  'SessionEnd',
  'PreCompact',
] as const

type RawAttribute = { key: string; value?: unknown }

type RawSpan = {
  traceId?: string
  spanId?: string
  name?: string
  attributes?: RawAttribute[]
}

async function mapper() {
  return import('./mapper.js')
}

/** Map one hook payload and return the spans the mapper produced. */
async function mapSpans(
  payload: Record<string, unknown>,
  contentAllowed = true,
): Promise<RawSpan[]> {
  const { mapHookEvent } = await mapper()
  const result = mapHookEvent(payload, { contentAllowed })
  if (!Array.isArray(result)) {
    throw new Error('mapHookEvent must return an array of spans')
  }
  return result.map((entry) => {
    if (typeof entry !== 'object' || entry === null) {
      throw new Error('a span must be an object')
    }
    return entry as RawSpan
  })
}

/** Map one hook payload that must produce exactly one span, and return it. */
async function mapOne(payload: Record<string, unknown>, contentAllowed = true): Promise<RawSpan> {
  const spans = await mapSpans(payload, contentAllowed)
  expect(spans, `the span of ${String(payload.hook_event_name)}`).toHaveLength(1)
  return spans[0]
}

/** The OTLP value object of one attribute, or undefined when the span does not carry it. */
function attributeOf(span: RawSpan, key: string): Record<string, unknown> | undefined {
  const attribute = (span.attributes ?? []).find((candidate) => candidate.key === key)
  if (attribute === undefined) return undefined
  const value = attribute.value
  if (typeof value !== 'object' || value === null) {
    throw new Error(
      `the ${key} attribute must carry an OTLP value object, got ${JSON.stringify(value)}`,
    )
  }
  return value as Record<string, unknown>
}

/** The string an attribute carries, or undefined when the span does not carry it. */
function stringAttr(span: RawSpan, key: string): string | undefined {
  const value = attributeOf(span, key)
  if (value === undefined) return undefined
  const stringValue = value.stringValue
  if (typeof stringValue !== 'string') {
    throw new Error(`the ${key} attribute must carry a stringValue`)
  }
  return stringValue
}

/** The number an attribute carries, or undefined when the span does not carry it. */
function numberAttr(span: RawSpan, key: string): number | undefined {
  const value = attributeOf(span, key)
  if (value === undefined) return undefined
  if (typeof value.intValue === 'string') return Number(value.intValue)
  if (typeof value.doubleValue === 'number') return value.doubleValue
  throw new Error(`the ${key} attribute must carry a numeric value`)
}

/** The span id of a span, or a failure when the span carries none. */
function spanIdOf(span: RawSpan): string {
  if (typeof span.spanId !== 'string') {
    throw new Error('a span must carry a span id')
  }
  return span.spanId
}

/** The size of a payload value as it travels on the wire, in bytes. */
function jsonBytes(value: unknown): number {
  return new TextEncoder().encode(JSON.stringify(value)).length
}

/** A payload that carries every field the contract names, for the event under test. */
function payloadFor(event: string): Record<string, unknown> {
  return {
    hook_event_name: event,
    session_id: SESSION,
    parent_session_id: PARENT_SESSION,
    message_id: MESSAGE,
    tool_call_id: CALL,
    tool_name: 'Read',
    tool_input: { path: 'a.txt' },
    tool_response: { ok: true },
    tool_status: 'completed',
    tool_error_type: 'ValidationError',
    cwd: '/work/repo',
    permission_mode: 'default',
    model: 'factory-model-01',
    prompt: 'first prompt',
    session_duration_ms: 1200,
    message_count: 3,
  }
}

describe('the Factory hook-to-span mapping', () => {
  it('a SessionStart hook yields one factory.session span with the session id and the cwd', async () => {
    const span = await mapOne({
      hook_event_name: 'SessionStart',
      session_id: SESSION,
      cwd: '/work/repo',
    })
    expect(span.name).toBe('factory.session')
    expect(stringAttr(span, 'factory.session.id')).toBe(SESSION)
    expect(stringAttr(span, 'factory.cwd')).toBe('/work/repo')
    expect(stringAttr(span, 'factory.contract_version')).toBe('1')
  })

  it('a SessionStart hook without a cwd carries no cwd attribute', async () => {
    const span = await mapOne({ hook_event_name: 'SessionStart', session_id: SESSION })
    expect(attributeOf(span, 'factory.cwd')).toBeUndefined()
  })

  it('a SessionStart hook carries no message, tool, prompt or model attribute', async () => {
    const span = await mapOne({
      hook_event_name: 'SessionStart',
      session_id: SESSION,
      cwd: '/work/repo',
    })
    expect(attributeOf(span, 'factory.message.id')).toBeUndefined()
    expect(attributeOf(span, 'factory.tool.name')).toBeUndefined()
    expect(attributeOf(span, 'factory.tool.call_id')).toBeUndefined()
    expect(attributeOf(span, 'factory.tool.input_bytes')).toBeUndefined()
    expect(attributeOf(span, 'factory.turn.prompt.text')).toBeUndefined()
    expect(attributeOf(span, 'factory.model.id')).toBeUndefined()
  })

  it('a UserPromptSubmit hook yields one factory.turn span with the operation llm.invoke', async () => {
    const span = await mapOne({
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: MESSAGE,
      prompt: 'first prompt',
    })
    expect(span.name).toBe('factory.turn')
    expect(stringAttr(span, 'factory.session.id')).toBe(SESSION)
    expect(stringAttr(span, 'factory.message.id')).toBe(MESSAGE)
    expect(stringAttr(span, 'factory.turn.operation')).toBe('llm.invoke')
  })

  it('a UserPromptSubmit hook carries the prompt text only when content is allowed', async () => {
    const payload = {
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: MESSAGE,
      prompt: 'first prompt',
    }
    const allowed = await mapOne(payload, true)
    expect(stringAttr(allowed, 'factory.turn.prompt.text')).toBe('first prompt')
    const denied = await mapOne(payload, false)
    expect(attributeOf(denied, 'factory.turn.prompt.text')).toBeUndefined()
  })

  it('a UserPromptSubmit hook without a prompt carries no prompt text', async () => {
    const span = await mapOne({
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: MESSAGE,
    })
    expect(attributeOf(span, 'factory.turn.prompt.text')).toBeUndefined()
  })

  it('a prompt longer than the cap is cut on the wire', async () => {
    const prompt = 'x'.repeat(100_000)
    const span = await mapOne({
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: MESSAGE,
      prompt,
    })
    const text = stringAttr(span, 'factory.turn.prompt.text') ?? ''
    expect(new TextEncoder().encode(text).length).toBeLessThanOrEqual(64 * 1024)
    expect(text.length).toBeLessThan(prompt.length)
  })

  it('a UserPromptSubmit hook carries the model only when the payload states one', async () => {
    const stated = await mapOne({
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: MESSAGE,
      model: 'factory-model-01',
    })
    expect(stringAttr(stated, 'factory.model.id')).toBe('factory-model-01')
    const unstated = await mapOne({
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: MESSAGE,
    })
    expect(attributeOf(unstated, 'factory.model.id')).toBeUndefined()
  })

  it('a PreToolUse hook yields one factory.tool span with the tool name, the call id and the input size', async () => {
    const span = await mapOne({
      hook_event_name: 'PreToolUse',
      session_id: SESSION,
      tool_call_id: CALL,
      tool_name: 'Read',
      tool_input: { path: 'a.txt' },
    })
    expect(span.name).toBe('factory.tool')
    expect(stringAttr(span, 'factory.session.id')).toBe(SESSION)
    expect(stringAttr(span, 'factory.tool.name')).toBe('Read')
    expect(stringAttr(span, 'factory.tool.call_id')).toBe(CALL)
    expect(numberAttr(span, 'factory.tool.input_bytes')).toBe(jsonBytes({ path: 'a.txt' }))
  })

  it('a PreToolUse hook carries no tool status, error type or result size', async () => {
    const span = await mapOne({
      hook_event_name: 'PreToolUse',
      session_id: SESSION,
      tool_call_id: CALL,
      tool_name: 'Read',
      tool_input: { path: 'a.txt' },
    })
    expect(attributeOf(span, 'factory.tool.status')).toBeUndefined()
    expect(attributeOf(span, 'factory.tool.error_type')).toBeUndefined()
    expect(attributeOf(span, 'factory.tool.result_bytes')).toBeUndefined()
  })

  it('a PreToolUse hook without a tool input carries no input size', async () => {
    const span = await mapOne({
      hook_event_name: 'PreToolUse',
      session_id: SESSION,
      tool_call_id: CALL,
      tool_name: 'Read',
    })
    expect(attributeOf(span, 'factory.tool.input_bytes')).toBeUndefined()
  })

  it('a PostToolUse hook yields one factory.tool span with the status and the result size', async () => {
    const span = await mapOne({
      hook_event_name: 'PostToolUse',
      session_id: SESSION,
      tool_call_id: CALL,
      tool_name: 'Read',
      tool_input: { path: 'a.txt' },
      tool_response: { ok: true },
      tool_status: 'completed',
    })
    expect(span.name).toBe('factory.tool')
    expect(stringAttr(span, 'factory.tool.name')).toBe('Read')
    expect(stringAttr(span, 'factory.tool.call_id')).toBe(CALL)
    expect(stringAttr(span, 'factory.tool.status')).toBe('completed')
    expect(numberAttr(span, 'factory.tool.input_bytes')).toBe(jsonBytes({ path: 'a.txt' }))
    expect(numberAttr(span, 'factory.tool.result_bytes')).toBe(jsonBytes({ ok: true }))
  })

  it('a PostToolUse hook states the error type the payload carries', async () => {
    const span = await mapOne({
      hook_event_name: 'PostToolUse',
      session_id: SESSION,
      tool_call_id: CALL,
      tool_name: 'Read',
      tool_response: { error: 'bad input' },
      tool_status: 'error',
      tool_error_type: 'ValidationError',
    })
    expect(stringAttr(span, 'factory.tool.status')).toBe('error')
    expect(stringAttr(span, 'factory.tool.error_type')).toBe('ValidationError')
  })

  it('a PostToolUse hook states no outcome the payload did not give', async () => {
    const span = await mapOne({
      hook_event_name: 'PostToolUse',
      session_id: SESSION,
      tool_call_id: CALL,
      tool_name: 'Read',
      tool_response: { ok: true },
    })
    expect(attributeOf(span, 'factory.tool.status')).toBeUndefined()
    expect(attributeOf(span, 'factory.tool.error_type')).toBeUndefined()
  })

  it('a PostToolUse hook without a tool response carries no result size', async () => {
    const span = await mapOne({
      hook_event_name: 'PostToolUse',
      session_id: SESSION,
      tool_call_id: CALL,
      tool_name: 'Read',
      tool_status: 'completed',
    })
    expect(attributeOf(span, 'factory.tool.result_bytes')).toBeUndefined()
  })

  it('the PreToolUse and the PostToolUse of one tool call share a span id and a trace id', async () => {
    const pre = await mapOne({
      hook_event_name: 'PreToolUse',
      session_id: SESSION,
      tool_call_id: CALL,
      tool_name: 'Read',
      tool_input: { path: 'a.txt' },
    })
    const post = await mapOne({
      hook_event_name: 'PostToolUse',
      session_id: SESSION,
      tool_call_id: CALL,
      tool_name: 'Read',
      tool_response: { ok: true },
      tool_status: 'completed',
    })
    expect(pre.spanId).toMatch(/^[0-9a-f]{16}$/)
    expect(post.spanId).toBe(pre.spanId)
    expect(pre.traceId).toMatch(/^[0-9a-f]{32}$/)
    expect(post.traceId).toBe(pre.traceId)
  })

  it('a Stop hook yields one factory.turn span with the session id and the message id', async () => {
    const span = await mapOne({
      hook_event_name: 'Stop',
      session_id: SESSION,
      message_id: MESSAGE,
    })
    expect(span.name).toBe('factory.turn')
    expect(stringAttr(span, 'factory.session.id')).toBe(SESSION)
    expect(stringAttr(span, 'factory.message.id')).toBe(MESSAGE)
  })

  it('a SubagentStop hook yields one factory.child_session span carrying the child and the parent session id separately', async () => {
    const span = await mapOne({
      hook_event_name: 'SubagentStop',
      session_id: SESSION,
      parent_session_id: PARENT_SESSION,
    })
    expect(span.name).toBe('factory.child_session')
    expect(stringAttr(span, 'factory.session.id')).toBe(SESSION)
    expect(stringAttr(span, 'factory.parent.session.id')).toBe(PARENT_SESSION)
    expect(stringAttr(span, 'factory.parent.session.id')).not.toBe(SESSION)
  })

  it('a SubagentStop hook without a parent session id carries no parent session id', async () => {
    const span = await mapOne({
      hook_event_name: 'SubagentStop',
      session_id: SESSION,
    })
    expect(stringAttr(span, 'factory.session.id')).toBe(SESSION)
    expect(attributeOf(span, 'factory.parent.session.id')).toBeUndefined()
  })

  it('a SessionEnd hook yields one factory.session span with the session id', async () => {
    const span = await mapOne({ hook_event_name: 'SessionEnd', session_id: SESSION })
    expect(span.name).toBe('factory.session')
    expect(stringAttr(span, 'factory.session.id')).toBe(SESSION)
  })

  it('a PreCompact hook yields no span', async () => {
    const spans = await mapSpans({
      hook_event_name: 'PreCompact',
      session_id: SESSION,
      message_id: MESSAGE,
    })
    expect(spans).toHaveLength(0)
  })

  it('an unknown hook event yields no span', async () => {
    const spans = await mapSpans({
      hook_event_name: 'SomethingElse',
      session_id: SESSION,
    })
    expect(spans).toHaveLength(0)
  })

  it('every hook event yields at most one span, and every span names the contract version', async () => {
    for (const event of HOOK_EVENTS) {
      const spans = await mapSpans(payloadFor(event))
      expect(spans.length, `the ${event} event`).toBeLessThanOrEqual(1)
      for (const span of spans) {
        expect(stringAttr(span, 'factory.contract_version'), `the ${event} event`).toBe('1')
      }
    }
  })

  it('no span carries an OpenCode attribute or span name', async () => {
    for (const event of HOOK_EVENTS) {
      for (const span of await mapSpans(payloadFor(event))) {
        expect(String(span.name).startsWith('opencode.'), `the ${event} span name`).toBe(false)
        for (const attribute of span.attributes ?? []) {
          expect(attribute.key.startsWith('opencode.'), `the ${event} ${attribute.key} key`).toBe(false)
        }
      }
    }
  })

  it('no span carries an empty or null attribute value', async () => {
    for (const event of HOOK_EVENTS) {
      for (const span of await mapSpans(payloadFor(event))) {
        for (const attribute of span.attributes ?? []) {
          const value = attribute.value
          expect(value, `the ${event} ${attribute.key} value`).not.toBeNull()
          if (typeof value === 'object' && value !== null) {
            const stringValue = (value as Record<string, unknown>).stringValue
            if (typeof stringValue === 'string') {
              expect(stringValue, `the ${event} ${attribute.key} value`).not.toBe('')
            }
          }
        }
      }
    }
  })

  it('the same payload maps to the same span id and the same trace id', async () => {
    const payload = payloadFor('PreToolUse')
    const first = await mapOne(payload)
    const second = await mapOne(payload)
    expect(first.spanId).toMatch(/^[0-9a-f]{16}$/)
    expect(second.spanId).toBe(first.spanId)
    expect(second.traceId).toBe(first.traceId)
  })

  it('two tool calls of one session carry different span ids', async () => {
    const first = await mapOne({
      hook_event_name: 'PreToolUse',
      session_id: SESSION,
      tool_call_id: 'call_factory_one',
      tool_name: 'Read',
      tool_input: { path: 'a.txt' },
    })
    const second = await mapOne({
      hook_event_name: 'PreToolUse',
      session_id: SESSION,
      tool_call_id: 'call_factory_two',
      tool_name: 'Read',
      tool_input: { path: 'b.txt' },
    })
    expect(second.spanId).not.toBe(first.spanId)
  })

  it('the session span and the turn span of one session carry different span ids', async () => {
    const session = await mapOne({
      hook_event_name: 'SessionStart',
      session_id: SESSION,
      cwd: '/work/repo',
    })
    const turn = await mapOne({
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: MESSAGE,
      prompt: 'first prompt',
    })
    expect(turn.spanId).not.toBe(session.spanId)
  })

  it('the prompt span and the completion span of one turn are distinct spans under one trace', async () => {
    // The prompt and the completion of one turn are two spans of one turn: two span ids, so the
    // completion cannot land on the prompt's row, and one trace id, so the turn stays one unit of
    // work. The two payloads carry the same message id on purpose: nothing in the contract
    // promises that Factory's reply id differs from the user's message id, so the two spans must
    // be distinct by construction.
    const prompt = await mapOne({
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: MESSAGE,
      prompt: 'first prompt',
      model: 'factory-model-01',
    })
    const completion = await mapOne({
      hook_event_name: 'Stop',
      session_id: SESSION,
      message_id: MESSAGE,
    })
    expect(prompt.name).toBe('factory.turn')
    expect(completion.name).toBe('factory.turn')
    expect(prompt.spanId).toMatch(/^[0-9a-f]{16}$/)
    expect(completion.spanId).toMatch(/^[0-9a-f]{16}$/)
    expect(spanIdOf(completion), 'the completion span id').not.toBe(spanIdOf(prompt))
    expect(prompt.traceId).toMatch(/^[0-9a-f]{32}$/)
    expect(completion.traceId, 'the trace id of the turn').toBe(prompt.traceId)
  })

  it('the completion span of a turn does not replace the prompt span: the prompt text and the model survive', async () => {
    // The store keys a span row by its span id, so two spans of one turn that share an id are one
    // row and the second overwrites the first. The prompt text and the model live only on the
    // prompt's span, so a shared id loses them.
    const prompt = await mapOne({
      hook_event_name: 'UserPromptSubmit',
      session_id: SESSION,
      message_id: MESSAGE,
      prompt: 'first prompt',
      model: 'factory-model-01',
    })
    const completion = await mapOne({
      hook_event_name: 'Stop',
      session_id: SESSION,
      message_id: MESSAGE,
    })
    const rows = new Map<string, RawSpan>()
    rows.set(spanIdOf(prompt), prompt)
    rows.set(spanIdOf(completion), completion)
    expect(rows.size, 'the rows one turn writes').toBe(2)
    const prompts = [...rows.values()].filter(
      (row) => stringAttr(row, 'factory.turn.prompt.text') !== undefined,
    )
    expect(prompts, 'the row carrying the prompt text').toHaveLength(1)
    expect(stringAttr(prompts[0], 'factory.turn.prompt.text')).toBe('first prompt')
    expect(stringAttr(prompts[0], 'factory.model.id')).toBe('factory-model-01')
  })
})
