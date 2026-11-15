// The shared OTLP transport: one bounded outbox both capture plugins use.
//
// The transport is extracted from the OpenCode capture plugin's own transport so the Factory
// plugin and the OpenCode plugin share one bounded, security-relevant implementation instead of
// forking it. The shared modules are written by the next task, so every test here fails until
// they exist; the modules are loaded inside each test, so the RED proof is per test.
//
// The contract this file pins:
//
//   - the shared outbox keeps the OpenCode outbox's contract: a refused batch is halved on 429, a
//     failed batch is re-sent, every event arrives once and in order, and the spool is bounded
//     with the oldest event dropped first;
//   - the OpenCode plugin's own transport module becomes a thin re-export shim of the shared one,
//     so the two plugins run the same code and the OpenCode behaviour does not change;
//   - the committed OpenCode plugin bundle still matches a fresh build of the entry. This is the
//     drift guard `node dash/scripts/bundle-opencode-plugin.mjs --check` runs, compared here in
//     process: a child process would add a spawn site the storm guard has no bound for, and the
//     comparison is the same one the script makes.
//
// The API the shared transport must expose:
//
//   transport/outbox.ts:     createOutbox(options) with `enqueue`, `peek`, `status`, `drain` and
//                            `close`, and the options `capacity`, `batchSize`, `endpoint`,
//                            `fetchFn`, `sleepFn`, `randomFn` and `requestTimeoutMs`.
//   transport/otlp-sender.ts: see otlp-sender.test.ts.

import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { describe, expect, it } from 'vitest'

const HERE = dirname(fileURLToPath(import.meta.url))

/** The committed OpenCode plugin bundle, and the script that builds it. */
const GENERATED_FILE = join(HERE, '..', 'opencode-plugin', 'generated', 'plugin-source.ts')
const BUNDLE_SCRIPT = join(HERE, '..', '..', '..', '..', 'scripts', 'bundle-opencode-plugin.mjs')

type BundleScript = {
  OPENCODE_PLUGIN_MARKER: string
  bundleOpencodePlugin: () => Promise<string>
  renderPluginSourceModule: (source: string) => string
}

// ---------------------------------------------------------------------------------------------
// The wire shape the outbox carries

type WireSpan = {
  traceId: string
  spanId: string
  name: string
  kind: 'SPAN_KIND_INTERNAL'
  startTimeUnixNano: string
  endTimeUnixNano: string
  attributes: Array<{ key: string; value: { stringValue: string } }>
}

type OutboxLike = {
  enqueue: (event: WireSpan) => void
  peek: () => readonly unknown[]
  status: () => { queued: number; capacity: number; dropped_spans: number; sent_spans: number }
  drain: () => Promise<void>
  close: () => Promise<void>
}

function span(name: string): WireSpan {
  return {
    traceId: 'a'.repeat(32),
    spanId: 'b'.repeat(16),
    name,
    kind: 'SPAN_KIND_INTERNAL',
    startTimeUnixNano: '1700000000000000000',
    endTimeUnixNano: '1700000000000000001',
    attributes: [{ key: 'test.name', value: { stringValue: name } }],
  }
}

function namesOf(events: readonly unknown[]): string[] {
  return events.map((event) => (event as { name?: unknown }).name as string)
}

// ---------------------------------------------------------------------------------------------
// The receiver: a recording fetch that never touches the network

type Attempt = { url: string; body: unknown }
type Receiver = { fetch: (url: string, init?: RequestInit) => Promise<Response>; attempts: Attempt[]; accepted: unknown[] }

function receiver(policy: (attempt: number) => number | 'throw' = () => 200): Receiver {
  const attempts: Attempt[] = []
  const accepted: unknown[] = []
  const fetch = async (url: string, init?: RequestInit): Promise<Response> => {
    const body: unknown = JSON.parse(String(init?.body ?? '{}'))
    attempts.push({ url, body })
    const answer = policy(attempts.length)
    if (answer === 'throw') throw new Error('connect ECONNREFUSED 127.0.0.1:4318 (synthetic)')
    if (answer >= 200 && answer < 300) accepted.push(body)
    return new Response('{}', { status: answer, headers: { 'content-type': 'application/json' } })
  }
  return { fetch, attempts, accepted }
}

/** Every span of an OTLP trace export body, however the body groups them. */
function spansOf(body: unknown): WireSpan[] {
  const record = body as { resourceSpans?: Array<{ scopeSpans?: Array<{ spans?: WireSpan[] }> }> } | null
  const spans: WireSpan[] = []
  for (const resource of record?.resourceSpans ?? []) {
    for (const scope of resource?.scopeSpans ?? []) spans.push(...(scope?.spans ?? []))
  }
  return spans
}

const noSleep = (): Promise<void> => Promise.resolve()

// ---------------------------------------------------------------------------------------------
// The shared outbox

describe('the shared outbox keeps the OpenCode delivery contract', () => {
  it('delivers every event once and in order, and halves the batch a 429 refused', async () => {
    const rcv = receiver((attempt) => (attempt === 1 ? 429 : 200))
    const module = await import('./outbox.js')
    const create = (module as { createOutbox?: unknown }).createOutbox
    if (typeof create !== 'function') {
      throw new Error('transport/outbox.ts must export createOutbox(options)')
    }
    const outbox = (create as (options: Record<string, unknown>) => OutboxLike)({
      fetchFn: rcv.fetch,
      sleepFn: noSleep,
      randomFn: () => 0,
      capacity: 10,
      batchSize: 4,
    })
    for (const name of ['one', 'two', 'three', 'four']) outbox.enqueue(span(name))
    await outbox.drain()
    await outbox.close()

    expect(rcv.attempts.length, 'a refused batch is re-sent').toBeGreaterThan(1)
    const refused = spansOf(rcv.attempts[0]?.body).length
    const retried = spansOf(rcv.attempts[1]?.body).length
    expect(retried, 'a 429 halves the batch for the next attempt').toBeLessThanOrEqual(
      Math.max(1, Math.floor(refused / 2)),
    )
    const delivered = rcv.accepted.flatMap(spansOf).map((entry) => entry.name)
    expect(delivered).toEqual(['one', 'two', 'three', 'four'])
  })

  it('re-sends a batch the transport refused', async () => {
    const rcv = receiver((attempt) => (attempt === 1 ? 'throw' : 200))
    const module = await import('./outbox.js')
    const create = (module as { createOutbox?: unknown }).createOutbox
    if (typeof create !== 'function') {
      throw new Error('transport/outbox.ts must export createOutbox(options)')
    }
    const outbox = (create as (options: Record<string, unknown>) => OutboxLike)({
      fetchFn: rcv.fetch,
      sleepFn: noSleep,
      randomFn: () => 0,
      capacity: 10,
    })
    outbox.enqueue(span('one'))
    await outbox.drain()
    await outbox.close()

    expect(rcv.attempts.length, 'a failed batch is re-sent').toBeGreaterThan(1)
    expect(rcv.accepted.flatMap(spansOf).map((entry) => entry.name)).toEqual(['one'])
  })

  it('keeps at most `capacity` events and drops the oldest first', async () => {
    const module = await import('./outbox.js')
    const create = (module as { createOutbox?: unknown }).createOutbox
    if (typeof create !== 'function') {
      throw new Error('transport/outbox.ts must export createOutbox(options)')
    }
    const outbox = (create as (options: Record<string, unknown>) => OutboxLike)({
      fetchFn: () => new Promise<Response>(() => undefined),
      sleepFn: noSleep,
      randomFn: () => 0,
      capacity: 3,
      requestTimeoutMs: 25,
    })
    for (const name of ['one', 'two', 'three', 'four', 'five']) outbox.enqueue(span(name))
    expect(outbox.peek().length, 'the spool is bounded').toBe(3)
    expect(namesOf(outbox.peek())).toEqual(['three', 'four', 'five'])
    expect(outbox.status().dropped_spans, 'the oldest event is dropped first').toBe(2)
    await outbox.close()
  })
})

// ---------------------------------------------------------------------------------------------
// The OpenCode plugin keeps its own module names

describe('the OpenCode plugin transport is a thin re-export of the shared one', () => {
  it('re-exports the shared outbox, so both plugins run the same code', async () => {
    const shared = await import('./outbox.js')
    const shim = await import('../opencode-plugin/transport/outbox.js')
    expect(shim.createOutbox, 'the OpenCode module must re-export the shared createOutbox').toBe(
      shared.createOutbox,
    )
  })

  it('re-exports the shared sender, so both plugins run the same code', async () => {
    const shared = await import('./otlp-sender.js')
    const shim = await import('../opencode-plugin/transport/otlp-sender.js')
    expect(shim.DEFAULT_OTLP_ENDPOINT).toBe(shared.DEFAULT_OTLP_ENDPOINT)
    expect(shim.OtlpDeliveryError).toBe(shared.OtlpDeliveryError)
    expect(shim.buildOtlpTracesBody).toBe(shared.buildOtlpTracesBody)
  })
})

// ---------------------------------------------------------------------------------------------
// The bundle drift guard

describe('the committed OpenCode plugin bundle still matches a fresh build', () => {
  it('equals the module the bundle script renders from a fresh build of the entry', async () => {
    const scriptUrl = pathToFileURL(BUNDLE_SCRIPT).href
    const script = (await import(/* @vite-ignore */ scriptUrl)) as BundleScript
    const expected = script.renderPluginSourceModule(await script.bundleOpencodePlugin())
    expect(readFileSync(GENERATED_FILE, 'utf8')).toBe(expected)
  })
})
