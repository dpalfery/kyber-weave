// Host values are untrusted: a number outside its domain is dropped as unmeasured, not stamped, and
// a host string is cut to a short label before the plugin retains or sends it. The golden replay pins
// ordinary values; these pin the edges.

import { describe, expect, it } from 'vitest'

import { OPENCODE_KEYS } from './contract.js'
import { createSetup } from './entry.js'
import { mapStepSpan } from './mapper.js'
import {
  OWNED_SESSION_ID,
  attributesOf,
  bus,
  busEvent,
  consentPathIn,
  createHost,
  hook,
  mergeBodies,
  noSleep,
  receiver,
  spansOf,
  start,
  stopPlugin,
  useTempDirectories,
  type FakeV2Host,
  type OtlpSpan,
  type Receiver,
} from './testing.js'

const tempDir = useTempDirectories()
const S = OWNED_SESSION_ID
const FIELD_CAP = 1024

function options(rcv: Receiver) {
  return { fetchFn: rcv.fetch, env: {}, consentPath: consentPathIn(tempDir()), sleepFn: noSleep, randomFn: () => 0 }
}

function stringAttr(span: OtlpSpan, key: string): string | undefined {
  return (attributesOf(span).get(key) as { stringValue?: string } | undefined)?.stringValue
}

function spans(rcv: Receiver, kind: string): OtlpSpan[] {
  return spansOf(mergeBodies(rcv.accepted)).filter((span) => stringAttr(span, OPENCODE_KEYS.spanKind) === kind)
}

async function open(host: FakeV2Host): Promise<void> {
  await host.deliverEvent(bus.sessionCreated(S, 1000))
  await host.deliverEvent(bus.executionStarted(S, 1010))
}

describe('event times are epoch milliseconds in a sane range', () => {
  for (const created of [-1000, 1e20, Number.MAX_SAFE_INTEGER]) {
    it(`drops an event created at ${created} instead of stamping it on a span`, async () => {
      const rcv = receiver()
      const host = createHost()
      const cleanup = await start(createSetup(options(rcv)), host)
      await host.deliverEvent(bus.sessionCreated(S, 1000))
      await host.deliverEvent(bus.executionStarted(S, created))
      await host.deliverEvent(bus.executionSucceeded(S, 1100))
      await stopPlugin(cleanup)
      expect(spans(rcv, 'request')).toEqual([])
    })
  }
})

describe('a step cost is a measurement only when it is a finite, non-negative number', () => {
  it('leaves cost absent for a negative cost on the bus', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await open(host)
    await host.deliverEvent(bus.stepStarted(S, 1020, 'msg_neg'))
    await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_neg', { cost: -5 }))
    await host.deliverEvent(bus.executionSucceeded(S, 1060))
    await stopPlugin(cleanup)
    const [step] = spans(rcv, 'step')
    expect(step).toBeDefined()
    expect(attributesOf(step as OtlpSpan).get(OPENCODE_KEYS.cost)).toBeUndefined()
  })

  it('the mapper itself refuses a negative cost record', () => {
    const wire = mapStepSpan({
      sessionId: S,
      messageId: 'msg_neg',
      startMs: 1,
      endMs: 2,
      requestKind: 'primary',
      usage: {},
      skills: [],
      cost: -5,
    })
    expect(wire.attributes.find((attribute) => attribute.key === OPENCODE_KEYS.cost)).toBeUndefined()
  })
})

describe('host strings kept for a span are cut to a label', () => {
  const long = 'x'.repeat(5_000_000)

  it('cuts the request kind, agent, model id, finish reason and tool error type', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await open(host)
    await host.deliverSession('model.request', hook.modelRequest(S, long))
    await host.deliverEvent(
      busEvent('session.step.started', S, 1020, {
        assistantMessageID: 'msg_long',
        agent: long,
        model: { id: long, providerID: long },
      }),
    )
    await host.deliverEvent(bus.toolCalled(S, 1022, 'msg_long', 'call_long', { a: 1 }))
    await host.deliverEvent(bus.toolFailed(S, 1024, 'msg_long', 'call_long', { type: long, message: 'm' }))
    await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_long', { finish: long }))
    await host.deliverEvent(bus.executionSucceeded(S, 1060))
    await stopPlugin(cleanup)
    const [step] = spans(rcv, 'step')
    const [tool] = spans(rcv, 'tool')
    const [request] = spans(rcv, 'request')
    const checks: Array<[OtlpSpan | undefined, string]> = [
      [step, OPENCODE_KEYS.requestKind],
      [step, OPENCODE_KEYS.agent],
      [step, OPENCODE_KEYS.modelId],
      [step, OPENCODE_KEYS.modelProvider],
      [step, OPENCODE_KEYS.stepFinish],
      [tool, OPENCODE_KEYS.toolErrorType],
      [request, OPENCODE_KEYS.agent],
    ]
    for (const [span, key] of checks) {
      const value = span === undefined ? undefined : stringAttr(span, key)
      expect(value, key).toBeDefined()
      expect(Buffer.byteLength(value ?? '', 'utf8'), key).toBeLessThanOrEqual(FIELD_CAP)
    }
  })
})
