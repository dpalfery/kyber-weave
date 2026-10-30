// Mapping rules of the V2 span model (contract 3), pinned on small synthetic streams. The golden
// replay pins the full bodies; these pin the rules the golden does not exercise: several prompts in
// one execution, failed and interrupted outcomes, request kinds, absent-versus-zero cost, skills
// de-duplicated across their three sources, and tools timed by their hooks alone.

import { describe, expect, it } from 'vitest'

import { OPENCODE_KEYS, SPAN_NAMES } from './contract.js'
import { createSetup } from './entry.js'
import {
  OWNED_SESSION_ID,
  attributesOf,
  bus,
  createHost,
  hook,
  mergeBodies,
  noSleep,
  receiver,
  spansOf,
  start,
  stopPlugin,
  useTempDirectories,
  consentPathIn,
  writeConsent,
  type FakeV2Host,
  type OtlpSpan,
  type Receiver,
} from './testing.js'

const tempDir = useTempDirectories()
const S = OWNED_SESSION_ID

// Consent is on so the content keys (prompt text, session label) are present for the rules that read them.
function options(rcv: Receiver) {
  const consentPath = consentPathIn(tempDir())
  writeConsent(consentPath, true)
  return { fetchFn: rcv.fetch, env: {}, consentPath, sleepFn: noSleep, randomFn: () => 0 }
}

function attr(span: OtlpSpan, key: string): unknown {
  return attributesOf(span).get(key)
}

function ofKind(rcv: Receiver, kind: string): OtlpSpan[] {
  return spansOf(mergeBodies(rcv.accepted)).filter(
    (span) => (attr(span, OPENCODE_KEYS.spanKind) as { stringValue?: string } | undefined)?.stringValue === kind,
  )
}

function requestOf(rcv: Receiver): OtlpSpan {
  const request = spansOf(mergeBodies(rcv.accepted)).find((span) => span.name === SPAN_NAMES.request)
  if (request === undefined) throw new Error('no request span on the wire')
  return request
}

function skillsOf(span: OtlpSpan): string[] | undefined {
  const value = attr(span, OPENCODE_KEYS.skillsInvoked) as { arrayValue?: { values: { stringValue: string }[] } } | undefined
  return value?.arrayValue?.values.map((item) => item.stringValue)
}

async function openExecution(host: FakeV2Host, created = 1000): Promise<void> {
  await host.deliverEvent(bus.sessionCreated(S, created))
  await host.deliverEvent(bus.executionStarted(S, created + 10))
}

describe('the request span', () => {
  it('joins every prompt of one execution with a blank line and counts them', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await openExecution(host)
    await host.deliverSession('prompt', hook.prompt(S, 'msg_p1', 'first'))
    await host.deliverSession('prompt', hook.prompt(S, 'msg_p2', 'second'))
    await host.deliverEvent(bus.executionSucceeded(S, 1100))
    await stopPlugin(cleanup)
    expect(attr(requestOf(rcv), OPENCODE_KEYS.promptText)).toEqual({ stringValue: 'first\n\nsecond' })
    expect(attr(requestOf(rcv), OPENCODE_KEYS.promptCount)).toEqual({ intValue: '2' })
  })

  it('maps a failed execution to error status with outcome failed', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await openExecution(host)
    await host.deliverEvent(bus.executionFailed(S, 1100, { type: 'provider', message: 'upstream 500 (synthetic)' }))
    await stopPlugin(cleanup)
    const request = requestOf(rcv)
    expect(attr(request, OPENCODE_KEYS.executionOutcome)).toEqual({ stringValue: 'failed' })
    expect((request['status'] as { code: string }).code).toBe('STATUS_CODE_ERROR')
  })

  it('maps an interrupted execution to outcome interrupted and carries the reason', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await openExecution(host)
    await host.deliverEvent(bus.executionInterrupted(S, 1100, 'user'))
    await stopPlugin(cleanup)
    const request = requestOf(rcv)
    expect(attr(request, OPENCODE_KEYS.executionOutcome)).toEqual({ stringValue: 'interrupted' })
    expect(attr(request, OPENCODE_KEYS.executionReason)).toEqual({ stringValue: 'user' })
  })
})

describe('the step span', () => {
  it('reports the primary request kind by default and the kind of a model.request hook before it', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await openExecution(host)
    await host.deliverEvent(bus.stepStarted(S, 1020, 'msg_default'))
    await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_default'))
    await host.deliverSession('model.request', hook.modelRequest(S, 'compaction'))
    await host.deliverEvent(bus.stepStarted(S, 1040, 'msg_compact'))
    await host.deliverEvent(bus.stepEnded(S, 1050, 'msg_compact'))
    await host.deliverEvent(bus.executionSucceeded(S, 1060))
    await stopPlugin(cleanup)
    const steps = ofKind(rcv, 'step')
    const kindOf = (messageID: string) =>
      steps.find((span) => (attr(span, OPENCODE_KEYS.messageId) as { stringValue: string }).stringValue === messageID)
    expect(attr(kindOf('msg_default') as OtlpSpan, OPENCODE_KEYS.requestKind)).toEqual({ stringValue: 'primary' })
    expect(attr(kindOf('msg_compact') as OtlpSpan, OPENCODE_KEYS.requestKind)).toEqual({ stringValue: 'compaction' })
  })

  it('leaves cost absent when the step reports none, and includes a reported zero', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await openExecution(host)
    await host.deliverEvent(bus.stepStarted(S, 1020, 'msg_zero'))
    await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_zero', { cost: 0 }))
    await host.deliverEvent(bus.stepStarted(S, 1040, 'msg_silent'))
    await host.deliverEvent(bus.stepEnded(S, 1050, 'msg_silent'))
    await host.deliverEvent(bus.executionSucceeded(S, 1060))
    await stopPlugin(cleanup)
    const byMessage = (messageID: string) =>
      ofKind(rcv, 'step').find((span) => (attr(span, OPENCODE_KEYS.messageId) as { stringValue: string }).stringValue === messageID)
    expect(attr(byMessage('msg_zero') as OtlpSpan, OPENCODE_KEYS.cost)).toEqual({ doubleValue: 0 })
    expect(attr(byMessage('msg_silent') as OtlpSpan, OPENCODE_KEYS.cost)).toBeUndefined()
  })

  it('counts a skill once across the skill tool, the prompt skills and the activation event', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await openExecution(host)
    await host.deliverSession('prompt', hook.prompt(S, 'msg_p1', 'use skills', [{ id: 'alpha' }]))
    await host.deliverSession('context', hook.context(S, ['sys']))
    await host.deliverSession('model.request', hook.modelRequest(S))
    await host.deliverEvent(bus.stepStarted(S, 1020, 'msg_skills'))
    await host.deliverTool('execute.before', hook.toolBefore(S, 'msg_skills', 'call_skill', 'skill', { id: 'alpha' }))
    await host.deliverTool('execute.after', hook.toolAfterCompleted(S, 'msg_skills', 'call_skill', 'skill', { id: 'alpha' }))
    await host.deliverEvent(bus.skillActivated(S, 1025, 'alpha'))
    await host.deliverEvent(bus.skillActivated(S, 1027, 'beta'))
    await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_skills'))
    await host.deliverEvent(bus.executionSucceeded(S, 1060))
    await stopPlugin(cleanup)
    const step = ofKind(rcv, 'step')[0]
    expect(step).toBeDefined()
    expect(skillsOf(step as OtlpSpan)?.slice().sort()).toEqual(['alpha', 'beta'])
  })

  it('times a tool from its hooks alone when no bus event arrives, and parents it to its step', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await openExecution(host)
    await host.deliverSession('context', hook.context(S, ['sys']))
    await host.deliverSession('model.request', hook.modelRequest(S))
    await host.deliverEvent(bus.stepStarted(S, 1020, 'msg_hook'))
    await host.deliverTool('execute.before', hook.toolBefore(S, 'msg_hook', 'call_hook', 'read', { path: 'a.txt' }))
    await host.deliverTool('execute.after', hook.toolAfterCompleted(S, 'msg_hook', 'call_hook', 'read', { path: 'a.txt' }))
    await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_hook'))
    await host.deliverEvent(bus.executionSucceeded(S, 1060))
    await stopPlugin(cleanup)
    const tool = ofKind(rcv, 'tool').find((span) => (attr(span, OPENCODE_KEYS.toolCallId) as { stringValue: string }).stringValue === 'call_hook')
    const step = ofKind(rcv, 'step').find((span) => (attr(span, OPENCODE_KEYS.messageId) as { stringValue: string }).stringValue === 'msg_hook')
    expect(tool, 'the hook-timed tool span').toBeDefined()
    expect(attr(tool as OtlpSpan, OPENCODE_KEYS.toolTiming)).toEqual({ stringValue: 'hook' })
    expect((tool as OtlpSpan)['parentSpanId']).toBe((step as OtlpSpan)['spanId'])
  })
})
