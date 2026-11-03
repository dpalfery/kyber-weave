// Golden replay of the OpenCode 2.2.1 capture (fixtures/v2/runA) through the V2 plugin's setup.
//
// The replay is the contract: a faithful stream of the real host's events and hook inputs must
// produce exactly the golden OTLP bodies, byte for byte after canonical key ordering. The rules the
// golden cannot express (the 30-minute fallback and cleanup) are pinned with small synthetic streams.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { OPENCODE_KEYS, SPAN_NAMES, OPENCODE_CONTRACT_VERSION } from './contract.js'
import { createSetup } from './entry.js'
import {
  FOREIGN_SESSION_ID,
  LOOPBACK_TRACES_URL,
  OWNED_SESSION_ID,
  attributesOf,
  bus,
  canonicalText,
  consentPathIn,
  createHost,
  expectGoldenWire,
  isCleanup,
  mergeBodies,
  noSleep,
  readGolden,
  receiver,
  replayRunA,
  spansOf,
  start,
  stopPlugin,
  useTempDirectories,
  writeConsent,
  type FakeV2Host,
  type OtlpBody,
  type OtlpSpan,
  type ProbeLine,
  type Receiver,
  type ReplayHooks,
} from './testing.js'

const STEP_1 = 'msg_18b3c6754001zOaRnFAQ8wClXP'
const STEP_2 = 'msg_18b3c8a44001ZD4defsrkMbE3G'
const READ_CALL = 'call_01a18b3c92917069bb0005c1c5cfa496'
const V1_LIFECYCLE_KINDS = ['session.start', 'turn.start', 'span.start', 'span.end', 'turn.end', 'session.end', 'tool_result']
const THIRTY_MINUTES_MS = 30 * 60 * 1000
/** runA's execution.succeeded `created` (1793337367427 ms) in OTLP nanoseconds. */
const EXECUTION_SUCCEEDED_NANOS = '1793337367427000000'

const tempDir = useTempDirectories()
afterEach(() => {
  vi.useRealTimers()
})

type SetupOptions = Parameters<typeof createSetup>[0]

function setupOptions(rcv: Receiver, consentPath: string, env: NodeJS.ProcessEnv = {}): SetupOptions {
  return { fetchFn: rcv.fetch, env, consentPath, sleepFn: noSleep, randomFn: () => 0 }
}

function absentConsent(): string {
  return consentPathIn(tempDir())
}

function attr(span: OtlpSpan, key: string): unknown {
  return attributesOf(span).get(key)
}

function stepSpan(spans: readonly OtlpSpan[], messageID: string): OtlpSpan | undefined {
  return spans.find(
    (span) =>
      canonicalText(attr(span, OPENCODE_KEYS.spanKind)) === canonicalText({ stringValue: 'step' }) &&
      canonicalText(attr(span, OPENCODE_KEYS.messageId)) === canonicalText({ stringValue: messageID }),
  )
}

function requestSpans(body: OtlpBody): OtlpSpan[] {
  return spansOf(body).filter((span) => span.name === SPAN_NAMES.request)
}

/**
 * runA with the experimental.idle line (line 47, inside step three) moved to just after step.ended
 * (line 49). The index guards fail loudly if the fixture ever changes shape.
 */
function moveIdleAfterStepEnded(lines: ProbeLine[]): ProbeLine[] {
  const idle = lines[46]
  if (idle?.kind !== 'session.hook:experimental.idle') throw new Error('runA line 47 is not experimental.idle')
  const rest = lines.filter((_, index) => index !== 46)
  if (rest[47]?.kind !== 'event:session.step.ended') throw new Error('runA line 49 is not step.ended')
  return [...rest.slice(0, 48), idle, ...rest.slice(48)]
}

function asRecord(value: unknown): Record<string, unknown> {
  return value as Record<string, unknown>
}

async function runReplay(
  setup: SetupOptions,
  host: FakeV2Host = createHost(),
  lines?: ReplayHooks,
): Promise<unknown> {
  const cleanup = await start(createSetup(setup), host)
  await replayRunA(host, lines)
  await stopPlugin(cleanup)
  return cleanup
}

/** Real macrotask yields, so a timer-driven flush that fake timers released can reach the receiver. */
async function yieldToPendingWork(): Promise<void> {
  for (let turn = 0; turn < 20; turn += 1) {
    await new Promise<void>((resolve) => setImmediate(resolve))
  }
}

describe('golden replay of runA through the V2 setup', () => {
  it('emits exactly the consent-on golden body when content consent is on', async () => {
    const consentPath = absentConsent()
    writeConsent(consentPath, true)
    const rcv = receiver()
    await runReplay(setupOptions(rcv, consentPath))
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-on')
  })

  it('emits exactly the consent-off golden body when content consent is off', async () => {
    const rcv = receiver()
    await runReplay(setupOptions(rcv, absentConsent()))
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })

  it('the KYBERDASH_OPENCODE_CONTEXT=1 override turns content on over a missing consent file', async () => {
    const rcv = receiver()
    await runReplay(setupOptions(rcv, absentConsent(), { KYBERDASH_OPENCODE_CONTEXT: '1' }))
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-on')
  })

  it('the KYBERDASH_OPENCODE_CONTEXT=0 override forces content off over a consenting file', async () => {
    const consentPath = absentConsent()
    writeConsent(consentPath, true)
    const rcv = receiver()
    await runReplay(setupOptions(rcv, consentPath, { KYBERDASH_OPENCODE_CONTEXT: '0' }))
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })

  it('never emits a span for the foreign session that shares the service', async () => {
    const rcv = receiver()
    await runReplay(setupOptions(rcv, absentConsent()))
    expect(JSON.stringify(rcv.attempts.map((attempt) => attempt.body))).not.toContain(FOREIGN_SESSION_ID)
    for (const span of spansOf(mergeBodies(rcv.accepted))) {
      expect(attr(span, OPENCODE_KEYS.sessionId)).toEqual({ stringValue: OWNED_SESSION_ID })
    }
    // The owned session alone must reproduce the golden: the 22 foreign events add nothing.
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })

  it('emits only request, step and tool spans, all on contract 3, and no V1 lifecycle kind', async () => {
    const rcv = receiver()
    await runReplay(setupOptions(rcv, absentConsent()))
    expect(rcv.attempts.length).toBeGreaterThan(0)
    for (const attempt of rcv.attempts) {
      expect(Object.keys(attempt.body)).toEqual(['resourceSpans'])
      expect(attempt.url).toBe(LOOPBACK_TRACES_URL)
    }
    const names = new Set<string>(Object.values(SPAN_NAMES))
    for (const span of spansOf(mergeBodies(rcv.accepted))) {
      expect(names.has(span.name)).toBe(true)
      expect(attr(span, OPENCODE_KEYS.contractVersion)).toEqual({ stringValue: OPENCODE_CONTRACT_VERSION })
      expect(['request', 'step', 'tool']).toContain((attr(span, OPENCODE_KEYS.spanKind) as { stringValue: string }).stringValue)
    }
    const wire = JSON.stringify(rcv.attempts.map((attempt) => attempt.body))
    for (const kind of V1_LIFECYCLE_KINDS) expect(wire, `V1 kind ${kind} on the wire`).not.toContain(kind)
  })

  it('a second setup in the same instance stops the first generation', async () => {
    const host = createHost()
    const consentPath = absentConsent()
    writeConsent(consentPath, true)
    const first = receiver()
    const second = receiver()
    await start(createSetup(setupOptions(first, absentConsent())), host)
    const secondCleanup = await start(createSetup(setupOptions(second, consentPath)), host)
    expect(host.closedSubscriptions).toBe(1)
    await replayRunA(host)
    await stopPlugin(secondCleanup)
    expect(first.attempts).toEqual([])
    expectGoldenWire(mergeBodies(second.accepted), 'consent-on')
  })

  it('setup returns a cleanup that stops the subscription and flushes the outbox', async () => {
    const host = createHost()
    const rcv = receiver()
    const cleanup = await start(createSetup(setupOptions(rcv, absentConsent())), host)
    if (!isCleanup(cleanup)) throw new Error('setup returned no cleanup')
    await replayRunA(host)
    await cleanup()
    expect(host.closedSubscriptions).toBe(1)
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })

  it('a throwing fetch never rejects a hook or event handler, and the session emits once the receiver recovers', async () => {
    const host = createHost()
    const rcv = receiver((attempt) => (attempt <= 3 ? 'throw' : 200))
    const cleanup = await start(createSetup(setupOptions(rcv, absentConsent())), host)
    await expect(replayRunA(host)).resolves.toBeUndefined()
    await expect(stopPlugin(cleanup)).resolves.toBeUndefined()
    expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
  })

  it('a tool that fails maps to opencode.tool.status=error and its error type', async () => {
    const rcv = receiver()
    const error = { type: 'permission', message: 'denied (synthetic)' }
    await runReplay(setupOptions(rcv, absentConsent()), createHost(), {
      transform: (line) => {
        const payload = asRecord(line.payload)
        if (line.kind === 'tool.hook:execute.after' && payload['id'] === READ_CALL) {
          const kept = Object.fromEntries(Object.entries(payload).filter(([key]) => key !== 'result'))
          return { kind: line.kind, payload: { ...kept, status: 'error', error } }
        }
        if (line.kind === 'event:session.tool.success' && asRecord(payload['data'])['id'] === READ_CALL) {
          const data = asRecord(payload['data'])
          return {
            kind: 'event:session.tool.failed',
            payload: {
              ...payload,
              type: 'session.tool.failed',
              data: { sessionID: data['sessionID'], assistantMessageID: data['assistantMessageID'], id: READ_CALL, error },
            },
          }
        }
        return line
      },
    })
    const tool = spansOf(mergeBodies(rcv.accepted)).find(
      (span) => canonicalText(attr(span, OPENCODE_KEYS.toolCallId)) === canonicalText({ stringValue: READ_CALL }),
    )
    expect(tool, 'the failed read tool span').toBeDefined()
    if (tool === undefined) return
    expect(attr(tool, OPENCODE_KEYS.toolStatus)).toEqual({ stringValue: 'error' })
    expect(attr(tool, OPENCODE_KEYS.toolErrorType)).toEqual({ stringValue: 'permission' })
  })

  it('consent is re-read per event: content turns on for a step whose context arrives after the change', async () => {
    const consentPath = absentConsent()
    const rcv = receiver()
    // Consent flips after line 22 (step one ends). Step one's context (line 8) was read with consent off;
    // step two's context (line 23) is read after the flip.
    await runReplay(setupOptions(rcv, consentPath), createHost(), {
      afterLine: (lineNumber) => {
        if (lineNumber === 22) writeConsent(consentPath, true)
      },
    })
    const actual = spansOf(mergeBodies(rcv.accepted))
    const golden = spansOf(readGolden('consent-on'))
    const actualStepOne = stepSpan(actual, STEP_1)
    const actualStepTwo = stepSpan(actual, STEP_2)
    const goldenStepTwo = stepSpan(golden, STEP_2)
    expect(actualStepOne).toBeDefined()
    expect(actualStepTwo).toBeDefined()
    expect(goldenStepTwo).toBeDefined()
    if (actualStepOne === undefined || actualStepTwo === undefined || goldenStepTwo === undefined) return
    expect(attr(actualStepOne, OPENCODE_KEYS.contextSystem)).toBeUndefined()
    for (const key of [OPENCODE_KEYS.contextSystem, OPENCODE_KEYS.contextTools, OPENCODE_KEYS.contextSkills]) {
      expect(attr(actualStepTwo, key), key).toEqual(attr(goldenStepTwo, key))
    }
  })

  it('experimental.idle never closes a request: as recorded, or moved after step.ended, the request is one ok span', async () => {
    const asRecorded = receiver()
    await runReplay(setupOptions(asRecorded, absentConsent()))
    const idleAfterStepEnded = receiver()
    await runReplay(setupOptions(idleAfterStepEnded, absentConsent()), createHost(), {
      order: moveIdleAfterStepEnded,
    })
    for (const rcv of [asRecorded, idleAfterStepEnded]) {
      const requests = requestSpans(mergeBodies(rcv.accepted))
      expect(requests, 'exactly one request span').toHaveLength(1)
      expect(requests[0]?.['endTimeUnixNano']).toBe(EXECUTION_SUCCEEDED_NANOS)
      expect(asRecord(requests[0]?.['status'])['code']).toBe('STATUS_CODE_OK')
      expectGoldenWire(mergeBodies(rcv.accepted), 'consent-off')
    }
  })

  it('an execution without a terminal event is emitted once as unknown, ending at its last created, after 30 minutes without events', async () => {
    const S = OWNED_SESSION_ID
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] })
    const host = createHost()
    const rcv = receiver()
    let cleanup: unknown
    try {
      cleanup = await start(createSetup(setupOptions(rcv, absentConsent())), host)
      await host.deliverEvent(bus.sessionCreated(S, 1000))
      await host.deliverEvent(bus.executionStarted(S, 1010))
      await host.deliverEvent(bus.stepStarted(S, 1020, 'msg_quiet'))
      await host.deliverEvent(bus.stepEnded(S, 1030, 'msg_quiet'))
      await vi.advanceTimersByTimeAsync(THIRTY_MINUTES_MS - 60_000)
      await yieldToPendingWork()
      expect(requestSpans(mergeBodies(rcv.accepted)), 'nothing emitted before 30 minutes').toHaveLength(0)
      await vi.advanceTimersByTimeAsync(120_000)
      await yieldToPendingWork()
      await vi.advanceTimersByTimeAsync(THIRTY_MINUTES_MS)
      await yieldToPendingWork()
      const requests = requestSpans(mergeBodies(rcv.accepted))
      expect(requests, 'emitted exactly once').toHaveLength(1)
      const [request] = requests
      if (request === undefined) return
      expect(attr(request, OPENCODE_KEYS.executionOutcome)).toEqual({ stringValue: 'unknown' })
      expect(request['endTimeUnixNano']).toBe(String(1030 * 1_000_000))
    } finally {
      vi.useRealTimers()
      await stopPlugin(cleanup)
    }
  })

  it('cleanup emits each open execution once as unknown', async () => {
    const first = OWNED_SESSION_ID
    const second = 'ses_open_second'
    const host = createHost()
    const rcv = receiver()
    const cleanup = await start(createSetup(setupOptions(rcv, absentConsent())), host)
    await host.deliverEvent(bus.sessionCreated(first, 1000))
    await host.deliverEvent(bus.executionStarted(first, 1010))
    await host.deliverEvent(bus.sessionCreated(second, 2000))
    await host.deliverEvent(bus.executionStarted(second, 2010))
    await host.deliverEvent(bus.stepStarted(second, 2020, 'msg_second'))
    await stopPlugin(cleanup)
    const requests = requestSpans(mergeBodies(rcv.accepted))
    expect(requests, 'one request per open execution').toHaveLength(2)
    const expectedEnd: Record<string, string> = {
      [first]: String(1010 * 1_000_000),
      [second]: String(2020 * 1_000_000),
    }
    for (const sessionID of [first, second]) {
      const mine = requests.filter(
        (span) => canonicalText(attr(span, OPENCODE_KEYS.sessionId)) === canonicalText({ stringValue: sessionID }),
      )
      expect(mine, `one request for ${sessionID}`).toHaveLength(1)
      const [only] = mine
      if (only === undefined) continue
      expect(attr(only, OPENCODE_KEYS.executionOutcome)).toEqual({ stringValue: 'unknown' })
      expect(only['endTimeUnixNano']).toBe(expectedEnd[sessionID])
    }
  })
})
