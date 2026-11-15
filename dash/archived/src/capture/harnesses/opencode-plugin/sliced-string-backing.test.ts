// Content text reaches the wire exactly, or capped: prompt text is bounded at 64 KiB of UTF-8 without
// splitting a character, and short text keeps its code units (surrogate pairs and CRLF) untouched.

import { describe, expect, it } from 'vitest'

import { OPENCODE_KEYS, SPAN_NAMES } from './contract.js'
import { createSetup } from './entry.js'
import {
  OWNED_SESSION_ID,
  attributesOf,
  bus,
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
  writeConsent,
  type OtlpSpan,
  type Receiver,
} from './testing.js'

const tempDir = useTempDirectories()
const S = OWNED_SESSION_ID
const PROMPT_CAP_BYTES = 64 * 1024

function options(rcv: Receiver, consentPath: string) {
  return { fetchFn: rcv.fetch, env: {}, consentPath, sleepFn: noSleep, randomFn: () => 0 }
}

function stringAttr(span: OtlpSpan, key: string): string | undefined {
  return (attributesOf(span).get(key) as { stringValue?: string } | undefined)?.stringValue
}

describe('content text is bounded or exact (F2 sliced-string backing)', () => {
  it('a prompt over 64 KiB is capped at 64 KiB of UTF-8 and never splits a character', async () => {
    const rcv = receiver()
    const consentPath = consentPathIn(tempDir())
    writeConsent(consentPath, true)
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv, consentPath)), host)
    await host.deliverEvent(bus.sessionCreated(S, 1000))
    await host.deliverEvent(bus.executionStarted(S, 1010))
    await host.deliverSession('prompt', hook.prompt(S, 'msg_big', '\u{1F600}'.repeat(40_000)))
    await host.deliverEvent(bus.executionSucceeded(S, 1100))
    await stopPlugin(cleanup)
    const request = spansOf(mergeBodies(rcv.accepted)).find((span) => span.name === SPAN_NAMES.request)
    const text = request === undefined ? undefined : stringAttr(request, OPENCODE_KEYS.promptText)
    expect(text, 'the capped prompt text').toBeDefined()
    if (text === undefined) return
    expect(Buffer.byteLength(text, 'utf8')).toBeLessThanOrEqual(PROMPT_CAP_BYTES)
    expect(text.startsWith('\u{1F600}')).toBe(true)
    expect(Buffer.from(text, 'utf8').toString('utf8')).toBe(text)
    expect(text).not.toContain('�')
  })

  it('short prompt and system text keep their code units exactly, including surrogate pairs and CRLF', async () => {
    const rcv = receiver()
    const consentPath = consentPathIn(tempDir())
    writeConsent(consentPath, true)
    const prompt = 'héllo \u{1F600}\r\nend'
    const system = 'sys \u{1F600}\r\nend'
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv, consentPath)), host)
    await host.deliverEvent(bus.sessionCreated(S, 1000))
    await host.deliverEvent(bus.executionStarted(S, 1010))
    await host.deliverSession('prompt', hook.prompt(S, 'msg_short', prompt))
    await host.deliverSession('context', hook.context(S, [system]))
    await host.deliverSession('model.request', hook.modelRequest(S))
    await host.deliverEvent(bus.stepStarted(S, 1020, 'msg_short'))
    await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_short'))
    await host.deliverEvent(bus.executionSucceeded(S, 1100))
    await stopPlugin(cleanup)
    const spans = spansOf(mergeBodies(rcv.accepted))
    const request = spans.find((span) => span.name === SPAN_NAMES.request)
    const step = spans.find((span) => span.name === SPAN_NAMES.step)
    expect(request === undefined ? undefined : stringAttr(request, OPENCODE_KEYS.promptText)).toBe(prompt)
    expect(step === undefined ? undefined : stringAttr(step, OPENCODE_KEYS.contextSystem)).toBe(system)
  })
})
