// Identity and bounds of the per-session state the engine keeps: what makes two requests distinct,
// which directory spellings name the same instance, how a skill id is spelled on every path, and how
// many skills one step keeps.

import { describe, expect, it } from 'vitest'

import { OPENCODE_KEYS } from './contract.js'
import { createSetup } from './entry.js'
import {
  OWNED_DIRECTORY,
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
  type OtlpSpan,
  type Receiver,
} from './testing.js'

const tempDir = useTempDirectories()
const S = OWNED_SESSION_ID

function options(rcv: Receiver) {
  return { fetchFn: rcv.fetch, env: {}, consentPath: consentPathIn(tempDir()), sleepFn: noSleep, randomFn: () => 0 }
}

function stringAttr(span: OtlpSpan, key: string): string | undefined {
  return (attributesOf(span).get(key) as { stringValue?: string } | undefined)?.stringValue
}

function spans(rcv: Receiver, kind: string): OtlpSpan[] {
  return spansOf(mergeBodies(rcv.accepted)).filter((span) => stringAttr(span, OPENCODE_KEYS.spanKind) === kind)
}

/** A bus event as the host would deliver it had it stamped no event id. */
function withoutId(event: unknown): unknown {
  const { id: _omitted, ...rest } = event as Record<string, unknown>
  void _omitted
  return rest
}

describe('a request has its own identity even when the host sent no event id', () => {
  it('two executions started at the same time with no event id are two requests, not one', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await host.deliverEvent(bus.sessionCreated(S, 900))
    await host.deliverEvent(withoutId(bus.executionStarted(S, 1000)))
    await host.deliverEvent(withoutId(bus.executionStarted(S, 1000)))
    await host.deliverEvent(bus.executionSucceeded(S, 1100))
    await stopPlugin(cleanup)
    const requests = spans(rcv, 'request')
    expect(requests).toHaveLength(2)
    expect(new Set(requests.map((span) => span['spanId'])).size).toBe(2)
  })
})

function skillsOf(span: OtlpSpan | undefined): string[] | undefined {
  const value = span === undefined ? undefined : (attributesOf(span).get(OPENCODE_KEYS.skillsInvoked) as
    | { arrayValue?: { values: { stringValue: string }[] } }
    | undefined)
  return value?.arrayValue?.values.map((item) => item.stringValue)
}

describe('a skill id is spelled one way on every path', () => {
  it('counts a prompt skill, a skill tool call and an activation event with padded ids as one skill', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await host.deliverEvent(bus.sessionCreated(S, 900))
    await host.deliverEvent(bus.executionStarted(S, 1000))
    await host.deliverSession('prompt', hook.prompt(S, 'msg_p', 'go', [{ id: ' alpha ' }]))
    await host.deliverEvent(bus.stepStarted(S, 1020, 'msg_a'))
    await host.deliverTool('execute.before', hook.toolBefore(S, 'msg_a', 'call_s', 'skill', { id: ' alpha\n' }))
    await host.deliverTool('execute.after', hook.toolAfterCompleted(S, 'msg_a', 'call_s', 'skill', { id: ' alpha\n' }))
    await host.deliverEvent(bus.skillActivated(S, 1025, 'alpha '))
    await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_a'))
    await host.deliverEvent(bus.executionSucceeded(S, 1060))
    await stopPlugin(cleanup)
    expect(skillsOf(spans(rcv, 'step')[0])).toEqual(['alpha'])
    expect(stringAttr(spans(rcv, 'tool')[0] as OtlpSpan, OPENCODE_KEYS.skillId)).toBe('alpha')
  })
})

describe('the skills one step keeps are bounded', () => {
  it('keeps at most 64 of the skills activated during one open step, newest last', async () => {
    const rcv = receiver()
    const host = createHost()
    const cleanup = await start(createSetup(options(rcv)), host)
    await host.deliverEvent(bus.sessionCreated(S, 900))
    await host.deliverEvent(bus.executionStarted(S, 1000))
    await host.deliverEvent(bus.stepStarted(S, 1020, 'msg_many'))
    for (let index = 0; index < 200; index += 1) {
      await host.deliverEvent(bus.skillActivated(S, 1021, `skill-${index}`))
    }
    await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_many'))
    await host.deliverEvent(bus.executionSucceeded(S, 1060))
    await stopPlugin(cleanup)
    const skills = skillsOf(spans(rcv, 'step')[0])
    expect(skills).toHaveLength(64)
    expect(skills?.at(-1)).toBe('skill-199')
  })
})

describe('a directory is the same instance however it is spelled', () => {
  for (const spelling of [`${OWNED_DIRECTORY}/`, `${OWNED_DIRECTORY}//`, '/HOME/scratch/./oc-probe', '/HOME/scratch/other/../oc-probe']) {
    it(`owns a session whose events name the directory as ${spelling}`, async () => {
      const rcv = receiver()
      const host = createHost({ directory: OWNED_DIRECTORY })
      const cleanup = await start(createSetup(options(rcv)), host)
      await host.deliverEvent(bus.sessionCreated('ses_spelled', 1000, spelling))
      await host.deliverEvent(bus.executionStarted('ses_spelled', 1010))
      await host.deliverEvent(bus.executionSucceeded('ses_spelled', 1020))
      await stopPlugin(cleanup)
      expect(spans(rcv, 'request')).toHaveLength(1)
    })
  }

  it('still drops a session of a different directory', async () => {
    const rcv = receiver()
    const host = createHost({ directory: `${OWNED_DIRECTORY}/` })
    const cleanup = await start(createSetup(options(rcv)), host)
    await host.deliverEvent(bus.sessionCreated('ses_other', 1000, `${OWNED_DIRECTORY}-two`))
    await host.deliverEvent(bus.executionStarted('ses_other', 1010))
    await host.deliverEvent(bus.executionSucceeded('ses_other', 1020))
    await stopPlugin(cleanup)
    expect(rcv.attempts).toEqual([])
  })

  it('a setup for the same directory spelled with a trailing slash supersedes the earlier one', async () => {
    const first = createHost({ directory: OWNED_DIRECTORY })
    const second = createHost({ directory: `${OWNED_DIRECTORY}/` })
    await start(createSetup(options(receiver())), first)
    const cleanup = await start(createSetup(options(receiver())), second)
    expect(first.closedSubscriptions).toBe(1)
    await stopPlugin(cleanup)
  })
})
