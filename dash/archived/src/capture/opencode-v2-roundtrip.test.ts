// OpenCode 2.2.1 round trip (RED until the plugin, adapter and projection land).
//
// Replays the captured run A through the production modules in the order a live capture travels:
// plugin -> OTLP JSON body -> receiver decode -> canonical ingest -> projection -> session API.
// Every stage has its own tests, and a failure names the first stage that broke. Each test owns
// its temp canon.db and its injected fetch, and nothing touches the network or the user's store.

import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { ingestBatch } from '../canon/ingest.js'
import { projectCanonicalStore } from '../canon/projection.js'
import { CanonStore } from '../canon/store.js'
import type { NotMeasurable } from '../canon/types.js'
import { decodeOtlpJson, type OtlpSpan as DecodedSpan } from '../otel/receiver.js'
import { KyberBridge } from '../server/bridge.js'
import { KYBERDASH_OPENCODE_CONTEXT_ENV } from './harnesses/opencode-plugin/consent.js'
import { createSetup } from './harnesses/opencode-plugin/entry.js'
import {
  FOREIGN_SESSION_ID,
  LOOPBACK_TRACES_URL,
  OWNED_SESSION_ID,
  consentPathIn,
  createHost,
  expectGoldenWire,
  mergeBodies,
  noSleep,
  readGolden,
  receiver,
  replayRunA,
  spansOf,
  start,
  stopPlugin,
  useTempDirectories,
  type Receiver,
} from './harnesses/opencode-plugin/testing.js'

/**
 * runA's user prompt, as the session API carries it, quotes included
 * (fixtures/v2/runA.kyberdash-api-session.v3-expected.json). The projection
 * previews the prompt literally: the quotes are OpenCode's own data, not a
 * wrapper to strip (architect ruling D27).
 */
const RUN_A_PROMPT =
  "\"Load the skill named kw-probe-skill using the skill tool, then read the file notes.txt with the read tool, then follow the skill's instruction. Do not do anything else.\""
const RUN_A_MODEL = 'muse-spark-1.3-contributor'

/** The shape a not-measurable value takes on the session API: an absence with a reason, never a zero. */
const CONTENT_CAPTURE = { availability: 'not_measurable', reason: expect.stringMatching(/content capture/i) }

/** The session API fields this round trip pins (see the v3 expected document for the field paths). */
type SessionApi = {
  id: string
  session_id: string
  harness: string
  label: string
  summary: {
    turn_count: number
    request_count: number
    total_input: number
    total_output: number
    total_cache_read: number
    total_cache_creation: number
    duration_ms: number
    models: string[]
    cost: { basis: string; status: string; value?: number; currency?: string }
    tools_offered: string[] | NotMeasurable
    tool_calls: number
    tools_invoked: string[]
    skills_listed?: string[] | NotMeasurable
    skills_invoked?: string[] | NotMeasurable
  }
  context: {
    first: { buckets: Record<string, number | NotMeasurable>; reported_input: number | NotMeasurable }
  }
  requests: Array<{ request: string; turns: number; model: string; prompt: string | NotMeasurable | null }>
  turns: Array<{
    index: number
    model: string
    input: number
    output: number
    reasoning: number
    cache_read: number
    cache_creation: number
  }>
  skills: { measurable: true; listed_count: number; rows: unknown[] } | NotMeasurable
}

const tempDir = useTempDirectories()
const openStores: CanonStore[] = []

afterEach(() => {
  for (const store of openStores.splice(0)) store.close()
})

/** Runs one pipeline stage. A failure is re-thrown with the stage's name, so the first broken stage is the one reported. */
async function stage<T>(name: string, work: () => T | Promise<T>): Promise<T> {
  try {
    return await work()
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    throw new Error(`OpenCode round trip stopped at the ${name} stage: ${reason}`, { cause: error })
  }
}

/** The consent the plugin reads: the env override alone, over a consent file that does not exist (so off unless overridden). */
function consentEnv(consent: boolean): NodeJS.ProcessEnv {
  return consent ? { [KYBERDASH_OPENCODE_CONTEXT_ENV]: '1' } : {}
}

/** Stage 1, plugin: runA through the fake V2 host; every POST the plugin makes lands on the injected fetch. */
async function postedWire(consent: boolean, dir: string): Promise<Receiver> {
  const wire = receiver()
  const host = createHost()
  await stage('plugin', async () => {
    const cleanup = await start(
      createSetup({
        fetchFn: wire.fetch,
        env: consentEnv(consent),
        consentPath: consentPathIn(dir),
        sleepFn: noSleep,
        randomFn: () => 0,
      }),
      host,
    )
    await replayRunA(host)
    await stopPlugin(cleanup)
  })
  return wire
}

/** Stage 2, receiver decode: each accepted OTLP JSON body goes through decodeOtlpJson, as the receiver does. */
function decode(wire: Receiver): Promise<DecodedSpan[]> {
  return stage('receiver decode', () =>
    wire.accepted.flatMap((body) => decodeOtlpJson(JSON.stringify(body))),
  )
}

/** Stage 3, canonical ingest: decoded spans through the adapters into a fresh temp canon.db. */
async function ingest(
  spans: DecodedSpan[],
  dir: string,
): Promise<{ store: CanonStore; outcome: ReturnType<typeof ingestBatch> }> {
  const store = new CanonStore(join(dir, 'canon.db'))
  openStores.push(store)
  const outcome = await stage('canonical ingest', () => ingestBatch(spans, store))
  return { store, outcome }
}

/**
 * Stages 1 to 5 in one call: plugin, receiver decode, canonical ingest, projection and the session
 * API read. Returns the owned session's API payload and the session list the API serves.
 */
async function projectedReplay(
  consent: boolean,
): Promise<{ session: SessionApi; listed: Array<{ session_id: string; harness: string }> }> {
  const dir = tempDir()
  const wire = await postedWire(consent, dir)
  const spans = await decode(wire)
  const { store } = await ingest(spans, dir)
  await stage('projection', () => projectCanonicalStore(store))
  return stage('session API', () => {
    const bridge = new KyberBridge({ canonPath: join(dir, 'canon.db'), store })
    const session = bridge.getSessionPayload<SessionApi>(OWNED_SESSION_ID)
    if (session === null) throw new Error(`the session API has no payload for ${OWNED_SESSION_ID}`)
    const listed = bridge.listSessions().map((row) => ({ session_id: row.session_id, harness: row.harness }))
    return { session, listed }
  })
}

/** The figures that must not move when content consent flips: usage, cost, duration, requests, turns and tool calls. */
function figuresOf(session: SessionApi) {
  return {
    turn_count: session.summary.turn_count,
    request_count: session.summary.request_count,
    total_input: session.summary.total_input,
    total_output: session.summary.total_output,
    total_cache_read: session.summary.total_cache_read,
    total_cache_creation: session.summary.total_cache_creation,
    duration_ms: session.summary.duration_ms,
    cost: session.summary.cost,
    models: session.summary.models,
    tool_calls: session.summary.tool_calls,
    tools_invoked: session.summary.tools_invoked,
    requests: session.requests.map((request) => ({ turns: request.turns, model: request.model })),
    turns: session.turns.map((turn) => ({
      index: turn.index,
      model: turn.model,
      input: turn.input,
      output: turn.output,
      reasoning: turn.reasoning,
      cache_read: turn.cache_read,
      cache_creation: turn.cache_creation,
    })),
  }
}

describe('runA through the production pipeline, content consent on', () => {
  it('plugin: posts OTLP JSON to the loopback collector and never sends a foreign session', async () => {
    const wire = await postedWire(true, tempDir())
    expect(wire.attempts.length, 'the plugin posted nothing').toBeGreaterThan(0)
    for (const attempt of wire.attempts) {
      expect(attempt.url).toBe(LOOPBACK_TRACES_URL)
      expect(attempt.contentType).toBe('application/json')
    }
    expect(wire.accepted.length, 'a posted body was refused').toBe(wire.attempts.length)
    expect(JSON.stringify(wire.attempts.map((attempt) => attempt.body))).not.toContain(FOREIGN_SESSION_ID)
  })

  it('plugin: the posted wire is the consent-on golden body', async () => {
    const wire = await postedWire(true, tempDir())
    expectGoldenWire(mergeBodies(wire.accepted), 'consent-on')
  })

  it('receiver decode: decodeOtlpJson reads every posted span, all on the golden trace of the owned session', async () => {
    const wire = await postedWire(true, tempDir())
    const spans = await decode(wire)
    expect(spans.length, 'decode dropped or invented spans').toBe(spansOf(mergeBodies(wire.accepted)).length)
    // The session rides on the trace id (one trace per session); the span carries no session.id attribute.
    const goldenTraces = new Set(spansOf(readGolden('consent-on')).map((span) => String(span['traceId'])))
    expect(new Set(spans.map((span) => span.traceId))).toEqual(goldenTraces)
  })

  it('canonical ingest: nothing decoded is quarantined or rejected', async () => {
    const dir = tempDir()
    const spans = await decode(await postedWire(true, dir))
    const { outcome } = await ingest(spans, dir)
    expect(outcome.accepted, 'ingest accepted no span').toBeGreaterThan(0)
    expect(outcome.quarantined, 'ingest quarantined a decoded span').toBe(0)
    expect(outcome.rejected, 'ingest rejected a decoded span').toBe(0)
  })

  it('projection: lists exactly one opencode session, the owned runA session', async () => {
    const { listed } = await projectedReplay(true)
    expect(listed).toEqual([{ session_id: OWNED_SESSION_ID, harness: 'opencode' }])
  })

  it('usage: input, cache read, cache creation and output totals are the runA figures', async () => {
    const { session } = await projectedReplay(true)
    expect(session.summary).toMatchObject({
      total_input: 40108,
      total_output: 1529,
      total_cache_read: 27475,
      total_cache_creation: 0,
    })
  })

  it('turns: three turns whose input, cache-read and reasoning figures sum to the runA totals', async () => {
    const { session } = await projectedReplay(true)
    expect(session.turns).toHaveLength(3)
    const sum = (pick: (turn: SessionApi['turns'][number]) => number): number =>
      session.turns.reduce((total, turn) => total + pick(turn), 0)
    expect(sum((turn) => turn.input)).toBe(40108)
    expect(sum((turn) => turn.cache_read)).toBe(27475)
    expect(sum((turn) => turn.reasoning)).toBe(1376)
  })

  it('label: the session carries the runA title', async () => {
    const { session } = await projectedReplay(true)
    expect(session.label).toBe('kw-probe runA shared-service')
  })

  it('requests: one request, three turns in the session', async () => {
    const { session } = await projectedReplay(true)
    expect(session.summary).toMatchObject({ request_count: 1, turn_count: 3 })
    expect(session.requests).toHaveLength(1)
    expect(session.requests[0]).toMatchObject({ turns: 3 })
  })

  it('requests: the request names the runA model', async () => {
    const { session } = await projectedReplay(true)
    expect(session.summary.models).toEqual([RUN_A_MODEL])
    expect(session.requests[0]).toMatchObject({ model: RUN_A_MODEL })
  })

  it('requests: the request carries the runA prompt', async () => {
    const { session } = await projectedReplay(true)
    expect(session.requests[0]?.prompt).toSatisfy(
      (prompt: unknown) => typeof prompt === 'string' && prompt.startsWith(RUN_A_PROMPT),
    )
  })

  it('cost: priced zero on the harness basis', async () => {
    const { session } = await projectedReplay(true)
    expect(session.summary.cost).toMatchObject({ basis: 'harness', status: 'priced', value: 0, currency: 'USD' })
  })

  it('duration: 13367 ms, the runA packet figure', async () => {
    const { session } = await projectedReplay(true)
    expect(session.summary.duration_ms).toBe(13367)
  })

  it('tools: two calls, skill and read invoked, twelve offered', async () => {
    const { session } = await projectedReplay(true)
    expect(session.summary.tool_calls).toBe(2)
    expect(session.summary.tools_invoked).toEqual(expect.arrayContaining(['skill', 'read']))
    expect(session.summary.tools_offered).toHaveLength(12)
  })

  it('system prompt: the context carries a measured system-prompt bucket above zero', async () => {
    const { session } = await projectedReplay(true)
    expect(session.context.first.buckets.system_prompt).toBeGreaterThan(0)
  })

  it('skills: 37 listed, kw-probe-skill among them and invoked', async () => {
    const { session } = await projectedReplay(true)
    expect(session.summary.skills_listed).toHaveLength(37)
    expect(session.summary.skills_listed).toEqual(expect.arrayContaining(['kw-probe-skill']))
    expect(session.summary.skills_invoked).toEqual(expect.arrayContaining(['kw-probe-skill']))
    expect(session.skills).toMatchObject({ measurable: true, listed_count: 37 })
  })
})

describe('runA through the production pipeline, content consent off', () => {
  it('plugin: the posted wire is the consent-off golden body, and the user prompt never leaves the host', async () => {
    const wire = await postedWire(false, tempDir())
    expectGoldenWire(mergeBodies(wire.accepted), 'consent-off')
    expect(JSON.stringify(wire.attempts.map((attempt) => attempt.body))).not.toContain(
      'Load the skill named kw-probe-skill',
    )
  })

  it('usage, cost, duration, request, turn and tool figures are identical to consent on', async () => {
    const on = await projectedReplay(true)
    const off = await projectedReplay(false)
    // A replay that measured nothing would also compare equal, so the consent-on side must carry figures.
    expect(on.session.summary.total_input).toBeGreaterThan(0)
    expect(figuresOf(off.session)).toEqual(figuresOf(on.session))
  })

  it('system prompt bucket is not_measurable with a content-capture reason, never zero', async () => {
    const { session } = await projectedReplay(false)
    expect(session.context.first.buckets.system_prompt).toEqual(expect.objectContaining(CONTENT_CAPTURE))
  })

  it('user prompt is not_measurable with a content-capture reason, never zero', async () => {
    const { session } = await projectedReplay(false)
    expect(session.requests[0]?.prompt).toEqual(expect.objectContaining(CONTENT_CAPTURE))
  })

  it('tools offered are not_measurable with a content-capture reason, never zero', async () => {
    const { session } = await projectedReplay(false)
    expect(session.summary.tools_offered).toEqual(expect.objectContaining(CONTENT_CAPTURE))
  })

  it('skills listed are not_measurable with a content-capture reason, never a list or zero', async () => {
    const { session } = await projectedReplay(false)
    expect(session.skills).toEqual(expect.objectContaining(CONTENT_CAPTURE))
    expect(Array.isArray(session.summary.skills_listed)).toBe(false)
  })
})
