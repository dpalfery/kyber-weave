// Tests for the optional Aspire source (R2.6, R2.7). Two criteria carry
// the weight:
//
//  - R2.6's parity: the same OTLP export pulled through `AspireSource` must
//    land as *identical* stored records to pushing it through `OtlpReceiver`
//    — same span ids, trace ids and normalized attributes — because the
//    source deliberately reuses the receiver's decoders and store port, and
//    a second decode path would be a second chance to diverge.
//  - R2.7's grouping: an export read out of the dashboard's ring buffer may
//    already be missing parents (the measured loss: 25 of 1,009 spans), so
//    spans whose `parentSpanId` resolves to nothing must still group — by
//    attribute (`session.id`), with the trace id as fallback — never by
//    ancestry.
//
// The supervision ladder (backoff doubling, cap, reset on success) is
// exercised against a scripted endpoint with millisecond delays, so the
// schedule is asserted as values rather than merely survived. The export
// endpoint itself is a real `node:http` server — the simulation the task
// prescribes — serving genuine OTLP bodies: JSON in the hand-rolled
// collector form, and for the encoding-parity case protobuf bytes encoded
// field-by-field here, not the JSON relabelled.

import { createServer } from 'node:http'
import type { AddressInfo } from 'node:net'
import { afterAll, describe, expect, it } from 'vitest'

import {
  InMemorySpanStore,
  OtlpDecodeError,
  OtlpReceiver,
  OTLP_TRACES_PATH,
  type OtlpSpan,
} from './receiver.js'
import {
  AspireExportError,
  AspireSource,
  DEFAULT_BACKOFF_BASE_MS,
  DEFAULT_GROUP_ATTRIBUTE,
  DEFAULT_POLL_INTERVAL_MS,
  MAX_BACKOFF_MS,
  groupByAttribute,
} from './aspire.js'

// ---------------------------------------------------------------------------
// Fixture: one OTLP export, hand-rolled collector JSON
// ---------------------------------------------------------------------------

const TRACE_ID = '6b0d7a1c94f24e3e9d5c2b8a1f4e6d2c'
const ROOT_SPAN_ID = 'a1b2c3d4e5f60718'
const CHILD_SPAN_ID = '0f1e2d3c4b5a6978'

/** OTLP wire form of a span, hand-rolled style: hex ids, numeric enums. */
type WireSpan = {
  traceId: string
  spanId: string
  parentSpanId?: string
  name: string
  kind: number
  startTimeUnixNano: string
  endTimeUnixNano: string
  attributes?: { key: string; value: unknown }[]
  status?: { code: number; message?: string }
}

const RESOURCE_ATTRIBUTES = [
  { key: 'service.name', value: { stringValue: 'codeburn' } },
  { key: 'service.version', value: { stringValue: '0.9.23' } },
]
const SCOPE = { name: 'kyberdash.aspire-fixture', version: '1.0.0' }

/** Wrap wire spans in an OTLP JSON `ExportTraceServiceRequest`. */
function exportBody(
  spans: WireSpan[],
  opts: { resource?: boolean; scope?: boolean } = {},
): string {
  return JSON.stringify({
    resourceSpans: [
      {
        ...(opts.resource === false ? {} : { resource: { attributes: RESOURCE_ATTRIBUTES } }),
        scopeSpans: [
          {
            ...(opts.scope === false ? {} : { scope: SCOPE }),
            spans,
          },
        ],
      },
    ],
  })
}

const attr = (key: string, value: unknown): { key: string; value: unknown } => ({ key, value })

const PARITY_EXPORT = exportBody([
  {
    traceId: TRACE_ID,
    spanId: ROOT_SPAN_ID,
    name: 'agent.turn',
    kind: 1, // INTERNAL
    startTimeUnixNano: '1756478400123456789',
    endTimeUnixNano: '1756478401373456789',
    attributes: [
      attr('session.id', { stringValue: 'sess-alpha' }),
      attr('gen_ai.request.model', { stringValue: 'claude-sonnet-4-5' }),
      attr('gen_ai.usage.input_tokens', { intValue: '965' }),
      attr('gen_ai.usage.output_tokens', { intValue: '50' }),
      attr('gen_ai.request.temperature', { doubleValue: 0.7 }),
      attr('gen_ai.request.stream', { boolValue: true }),
    ],
    status: { code: 1 }, // OK
  },
  {
    traceId: TRACE_ID,
    spanId: CHILD_SPAN_ID,
    parentSpanId: ROOT_SPAN_ID,
    name: 'tool.read_file',
    kind: 3, // CLIENT
    startTimeUnixNano: '1756478400200000000',
    endTimeUnixNano: '1756478400600000000',
    attributes: [
      attr('session.id', { stringValue: 'sess-alpha' }),
      attr('tool.name', { stringValue: 'read' }),
    ],
    status: { code: 2, message: 'file not found' }, // ERROR
  },
])

/** The decoded records both paths must land, written out literally (not
 * recomputed with the implementation's formulas): 1756478400123456789 ns is
 * 2025-08-29T14:40:00.123Z, and the fixture's deltas are exactly 1250 ms
 * and 400 ms. */
const PARITY_EXPECTED: OtlpSpan[] = [
  {
    traceId: TRACE_ID,
    spanId: ROOT_SPAN_ID,
    parentSpanId: null,
    name: 'agent.turn',
    kind: 'internal',
    startTimeUnixNano: '1756478400123456789',
    endTimeUnixNano: '1756478401373456789',
    timestamp: '2025-08-29T14:40:00.123Z',
    durationMs: 1250,
    status: { code: 'ok' },
    attributes: {
      'session.id': 'sess-alpha',
      'gen_ai.request.model': 'claude-sonnet-4-5',
      'gen_ai.usage.input_tokens': 965,
      'gen_ai.usage.output_tokens': 50,
      'gen_ai.request.temperature': 0.7,
      'gen_ai.request.stream': true,
    },
    resource: { 'service.name': 'codeburn', 'service.version': '0.9.23' },
    scope: SCOPE,
  },
  {
    traceId: TRACE_ID,
    spanId: CHILD_SPAN_ID,
    parentSpanId: ROOT_SPAN_ID,
    name: 'tool.read_file',
    kind: 'client',
    startTimeUnixNano: '1756478400200000000',
    endTimeUnixNano: '1756478400600000000',
    timestamp: '2025-08-29T14:40:00.200Z',
    durationMs: 400,
    status: { code: 'error', message: 'file not found' },
    attributes: { 'session.id': 'sess-alpha', 'tool.name': 'read' },
    resource: { 'service.name': 'codeburn', 'service.version': '0.9.23' },
    scope: SCOPE,
  },
]

// ---------------------------------------------------------------------------
// Test servers
// ---------------------------------------------------------------------------

type ExportResponse = { status?: number; contentType?: string; body: string | Uint8Array }

/** A simulated Aspire export endpoint: answers each GET from `handler`. */
type ExportServer = {
  url: string
  /** Number of requests received. */
  requests: () => number
  /** HTTP methods received, in order. */
  methods: () => string[]
  /** Stop serving; also run by the suite's afterAll. */
  stop: () => Promise<void>
}

const exportStoppers: Array<() => Promise<void>> = []
const startedReceivers: OtlpReceiver[] = []
const startedSources: AspireSource[] = []

afterAll(async () => {
  await Promise.all(startedSources.map((source) => source.stop()))
  await Promise.all(startedReceivers.map((receiver) => receiver.stop()))
  await Promise.all(exportStoppers.map((stop) => stop()))
})

async function startExportServer(
  handler: () => ExportResponse | Promise<ExportResponse>,
): Promise<ExportServer> {
  const methods: string[] = []
  const server = createServer((req, res) => {
    methods.push(req.method ?? '(none)')
    void Promise.resolve(handler()).then((answer) => {
      const headers: Record<string, string> = {}
      if (answer.contentType !== undefined) headers['content-type'] = answer.contentType
      res.writeHead(answer.status ?? 200, headers)
      res.end(typeof answer.body === 'string' ? answer.body : Buffer.from(answer.body))
    })
  })
  await new Promise<void>((resolve, reject) => {
    const onListening = (): void => {
      server.off('error', onError)
      resolve()
    }
    const onError = (err: Error): void => {
      server.off('listening', onListening)
      reject(err)
    }
    server.once('listening', onListening)
    server.once('error', onError)
    server.listen(0, '127.0.0.1')
  })
  const stop = async (): Promise<void> => {
    server.closeAllConnections()
    await new Promise<void>((resolve) => server.close(() => resolve()))
  }
  exportStoppers.push(stop)
  return {
    url: `http://127.0.0.1:${(server.address() as AddressInfo).port}`,
    requests: () => methods.length,
    methods: () => [...methods],
    stop,
  }
}

async function startReceiver(): Promise<{ store: InMemorySpanStore; url: string }> {
  const store = new InMemorySpanStore()
  const receiver = new OtlpReceiver({ port: 0, store })
  await receiver.start()
  startedReceivers.push(receiver)
  return { store, url: `http://127.0.0.1:${receiver.port}` }
}

const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms))

async function until(what: string, predicate: () => boolean, timeoutMs = 2_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${what}`)
    await sleep(5)
  }
}

// ---------------------------------------------------------------------------
// R2.6 — parity with the receiver path
// ---------------------------------------------------------------------------

describe('AspireSource parity with the receiver path (R2.6)', () => {
  it('lands identical stored records for the export it pulls and the POST the receiver takes', async () => {
    const server = await startExportServer(() => ({
      contentType: 'application/json',
      body: PARITY_EXPORT,
    }))
    const { store: receiverStore, url } = await startReceiver()

    const posted = await fetch(`${url}${OTLP_TRACES_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: PARITY_EXPORT,
    })
    expect(posted.status).toBe(200)
    expect(await posted.json()).toEqual({ acceptedSpans: 2 })

    const aspireStore = new InMemorySpanStore()
    const source = new AspireSource({ dashboardUrl: server.url, store: aspireStore })
    const spans = await source.pollOnce()

    expect(spans).toHaveLength(2)
    // Identical record-for-record, not merely similar — the load-bearing
    // assertion: pulling the export stores exactly what pushing it stores.
    expect(aspireStore.spans).toEqual(receiverStore.spans)
    expect(aspireStore.spans).toEqual(PARITY_EXPECTED)
    expect(receiverStore.spans).toEqual(PARITY_EXPECTED)

    // And on the fields the task names, after the receiver's normalization.
    expect(aspireStore.spans.map((span) => span.spanId)).toEqual(
      receiverStore.spans.map((span) => span.spanId),
    )
    expect(aspireStore.spans.map((span) => span.traceId)).toEqual(
      receiverStore.spans.map((span) => span.traceId),
    )
    expect(aspireStore.spans.map((span) => span.attributes)).toEqual(
      receiverStore.spans.map((span) => span.attributes),
    )
  })

  it('decodes a protobuf export to the same records as the JSON export', async () => {
    // A minimal span — no resource, no scope, no attributes, no status —
    // encoded as genuine OTLP wire bytes below. Its JSON counterpart omits
    // the same fields, so the two exports carry identical information.
    const protobufSpan = [
      ...pbBytesField(1, Buffer.from(TRACE_ID, 'hex')),
      ...pbBytesField(2, Buffer.from(ROOT_SPAN_ID, 'hex')),
      ...pbStringField(5, 'agent.turn'),
      ...pbVarintField(6, 1), // INTERNAL
      ...pbFixed64Field(7, 1756478400123456789n),
      ...pbFixed64Field(8, 1756478401373456789n),
    ]
    // ExportTraceServiceRequest { ResourceSpans { ScopeSpans { Span } } },
    // with the resource and scope submessages absent, matching the JSON.
    const protobufExport = Uint8Array.from(
      pbSubmessage(1, pbSubmessage(2, pbSubmessage(2, protobufSpan))),
    )
    const jsonExport = exportBody(
      [
        {
          traceId: TRACE_ID,
          spanId: ROOT_SPAN_ID,
          name: 'agent.turn',
          kind: 1,
          startTimeUnixNano: '1756478400123456789',
          endTimeUnixNano: '1756478401373456789',
        },
      ],
      { resource: false, scope: false },
    )

    const protobufServer = await startExportServer(() => ({
      contentType: 'application/x-protobuf',
      body: protobufExport,
    }))
    const jsonServer = await startExportServer(() => ({
      contentType: 'application/json',
      body: jsonExport,
    }))

    const protobufSpans = await new AspireSource({ dashboardUrl: protobufServer.url }).pollOnce()
    const jsonSpans = await new AspireSource({ dashboardUrl: jsonServer.url }).pollOnce()

    expect(protobufSpans).toEqual(jsonSpans)
    expect(jsonSpans).toEqual([
      {
        traceId: TRACE_ID,
        spanId: ROOT_SPAN_ID,
        parentSpanId: null,
        name: 'agent.turn',
        kind: 'internal',
        startTimeUnixNano: '1756478400123456789',
        endTimeUnixNano: '1756478401373456789',
        timestamp: '2025-08-29T14:40:00.123Z',
        durationMs: 1250,
        status: { code: 'unset' },
        attributes: {},
        resource: {},
        scope: {},
      },
    ])
  })

  it('polls on start(), lands the same records through the supervised loop, and stops on stop()', async () => {
    // First poll serves the export; later polls serve an empty request —
    // a drained ring buffer — so repeated polls write nothing further and
    // the store is comparable record-for-record.
    let call = 0
    const server = await startExportServer(() => {
      call += 1
      return call === 1
        ? { contentType: 'application/json', body: PARITY_EXPORT }
        : { contentType: 'application/json', body: '{}' }
    })
    const store = new InMemorySpanStore()
    const source = new AspireSource({ dashboardUrl: server.url, store, pollIntervalMs: 20 })

    expect(source.running).toBe(false)
    source.start()
    startedSources.push(source)
    expect(source.running).toBe(true)

    await until('two spans and at least three polls', () => store.spans.length === 2 && server.requests() >= 3)
    expect(store.spans).toEqual(PARITY_EXPECTED)
    expect(server.methods().every((method) => method === 'GET')).toBe(true)

    await source.stop()
    expect(source.running).toBe(false)
    const stoppedAt = server.requests()
    await sleep(80) // several poll intervals
    expect(server.requests()).toBe(stoppedAt) // no poll after stop
    expect(store.spans).toEqual(PARITY_EXPECTED) // and nothing new landed
  })

  it('reads a response with no content type as JSON', async () => {
    const server = await startExportServer(() => ({ body: PARITY_EXPORT })) // no content-type header
    const spans = await new AspireSource({ dashboardUrl: server.url }).pollOnce()
    expect(spans).toEqual(PARITY_EXPECTED)
  })
})

// ---------------------------------------------------------------------------
// fetchAspireExport failure surface
// ---------------------------------------------------------------------------

describe('fetchAspireExport failures', () => {
  it('rejects a non-2xx export with the status in the diagnostic', async () => {
    const server = await startExportServer(() => ({
      status: 503,
      contentType: 'application/json',
      body: 'unavailable',
    }))
    const source = new AspireSource({ dashboardUrl: server.url })
    await expect(source.fetchAspireExport()).rejects.toThrow(AspireExportError)
    await expect(source.fetchAspireExport()).rejects.toThrow(/503/)
  })

  it('rejects an unsupported content type', async () => {
    const server = await startExportServer(() => ({
      contentType: 'text/html',
      body: '<html>dashboard ui</html>',
    }))
    await expect(new AspireSource({ dashboardUrl: server.url }).fetchAspireExport()).rejects.toThrow(
      /content type text\/html is not supported/,
    )
  })

  it('rejects a 200 body that is not OTLP with the receiver diagnostic', async () => {
    const server = await startExportServer(() => ({
      contentType: 'application/json',
      body: 'not json at all',
    }))
    // The receiver's own error class: the decode contract is identical, so
    // the diagnostics are too.
    await expect(new AspireSource({ dashboardUrl: server.url }).fetchAspireExport()).rejects.toThrow(
      OtlpDecodeError,
    )
  })

  it('wraps an unreachable endpoint', async () => {
    const server = await startExportServer(() => ({ body: '{}' }))
    await server.stop()
    await expect(new AspireSource({ dashboardUrl: server.url }).fetchAspireExport()).rejects.toThrow(
      AspireExportError,
    )
  })
})

// ---------------------------------------------------------------------------
// Supervision with backoff
// ---------------------------------------------------------------------------

describe('AspireSource supervision with backoff', () => {
  it('backs off exponentially, caps, recovers, and resets the ladder on success', async () => {
    // Three failures, one success (the poll that lands the corpus), then
    // two more failures — the ladder must restart from the base after the
    // success rather than continuing to climb.
    const script = [503, 503, 503, 200, 503, 503]
    let call = 0
    const server = await startExportServer(() => {
      const step = script[Math.min(call, script.length - 1)]
      call += 1
      return step === 200
        ? { contentType: 'application/json', body: PARITY_EXPORT }
        : { status: step, contentType: 'application/json', body: 'unavailable' }
    })

    const failures: Array<{ err: unknown; backoffMs: number }> = []
    const store = new InMemorySpanStore()
    const source = new AspireSource({
      dashboardUrl: server.url,
      store,
      pollIntervalMs: 15,
      backoffBaseMs: 5,
      maxBackoffMs: 40,
      onPollError: (err, backoffMs) => failures.push({ err, backoffMs }),
    })
    source.start()
    startedSources.push(source)

    await until('five failed polls', () => failures.length >= 5)
    await source.stop()

    expect(failures.slice(0, 5).map((failure) => failure.backoffMs)).toEqual([5, 10, 20, 5, 10])
    expect(failures[0].err).toBeInstanceOf(AspireExportError)
    expect((failures[0].err as Error).message).toMatch(/503/)
    // The one successful poll landed the whole corpus.
    expect(store.spans).toEqual(PARITY_EXPECTED)
  })

  it('caps the backoff at maxBackoffMs', async () => {
    const server = await startExportServer(() => ({ status: 500, body: 'down' }))
    const delays: number[] = []
    const source = new AspireSource({
      dashboardUrl: server.url,
      backoffBaseMs: 1,
      maxBackoffMs: 8,
      onPollError: (_err, backoffMs) => delays.push(backoffMs),
    })
    source.start()
    startedSources.push(source)

    await until('six failed polls', () => delays.length >= 6)
    await source.stop()

    expect(delays.slice(0, 6)).toEqual([1, 2, 4, 8, 8, 8])
  })

  it('pins the documented cadence: 5 s poll, 1 s base backoff, 30 s cap', () => {
    expect(DEFAULT_POLL_INTERVAL_MS).toBe(5_000)
    expect(DEFAULT_BACKOFF_BASE_MS).toBe(1_000)
    expect(MAX_BACKOFF_MS).toBe(30_000)
    expect(DEFAULT_GROUP_ATTRIBUTE).toBe('session.id')
  })
})

// ---------------------------------------------------------------------------
// R2.7 — grouping when parents are missing
// ---------------------------------------------------------------------------

const TRACE_ALPHA = 'aaaabbbbccccdddd0000111122223333'
const TRACE_BETA_1 = 'bbbbaaaaccccdddd0000111122223333'
const TRACE_BETA_2 = 'ccccbbbbddddaaaa1111222233334444'
const TRACE_UNGROUPED = 'ddddccccbbbbaaaa1111222233334444'
const SPAN_A = 'aaaaaaaaaaaaaaaa'
const SPAN_B = 'bbbbbbbbbbbbbbbb'
const SPAN_C = 'cccccccccccccccc'
const SPAN_D = 'dddddddddddddddd'
const SPAN_E = 'eeeeeeeeeeeeeeee'
const SPAN_F = 'ffffffffffffffff'
/** Parents the dashboard's ring buffer evicted before the export ran. */
const EVICTED_ROOT = '9090909090909090'
const EVICTED_TURN = '8080808080808080'
const EVICTED_TOOL = '7070707070707070'

const session = (id: string): { key: string; value: unknown } => attr('session.id', { stringValue: id })

/**
 * An export with holes in it, shaped like the measured loss (R2.7):
 *  - sess-alpha: a root, its child, and a span whose parent (a run root)
 *    was evicted — same trace;
 *  - sess-beta: two *different* trace ids in one session, one span's parent
 *    evicted — the "17 sessions held 27 run identifiers" shape;
 *  - a span with no session attribute at all, parent evicted.
 */
const EVICTED_PARENT_EXPORT = exportBody([
  {
    traceId: TRACE_ALPHA, spanId: SPAN_A, name: 'run.start', kind: 1,
    startTimeUnixNano: '1756478600000000000', endTimeUnixNano: '1756478600001000000',
    attributes: [session('sess-alpha')],
  },
  {
    traceId: TRACE_ALPHA, spanId: SPAN_B, parentSpanId: SPAN_A, name: 'turn.chat', kind: 1,
    startTimeUnixNano: '1756478600002000000', endTimeUnixNano: '1756478600015000000',
    attributes: [session('sess-alpha')],
  },
  {
    traceId: TRACE_ALPHA, spanId: SPAN_C, parentSpanId: EVICTED_ROOT, name: 'tool.call', kind: 3,
    startTimeUnixNano: '1756478600003000000', endTimeUnixNano: '1756478600009000000',
    attributes: [session('sess-alpha')],
  },
  {
    traceId: TRACE_BETA_1, spanId: SPAN_D, name: 'run.start', kind: 1,
    startTimeUnixNano: '1756478700000000000', endTimeUnixNano: '1756478700001000000',
    attributes: [session('sess-beta')],
  },
  {
    traceId: TRACE_BETA_2, spanId: SPAN_E, parentSpanId: EVICTED_TURN, name: 'turn.chat', kind: 1,
    startTimeUnixNano: '1756478700002000000', endTimeUnixNano: '1756478700015000000',
    attributes: [session('sess-beta')],
  },
  {
    traceId: TRACE_UNGROUPED, spanId: SPAN_F, parentSpanId: EVICTED_TOOL, name: 'tool.call', kind: 3,
    startTimeUnixNano: '1756478700003000000', endTimeUnixNano: '1756478700009000000',
  },
])

describe('grouping when parents are missing (R2.7)', () => {
  it('groups spans whose parent was evicted by attribute, not ancestry', async () => {
    const server = await startExportServer(() => ({
      contentType: 'application/json',
      body: EVICTED_PARENT_EXPORT,
    }))
    const spans = await new AspireSource({ dashboardUrl: server.url }).pollOnce()

    expect(spans).toHaveLength(6)
    // Precondition: of the declared parents, only SPAN_A survived the ring
    // buffer — the three evicted ids resolve to nothing.
    const ids = new Set(spans.map((span) => span.spanId))
    for (const span of spans) {
      if (span.parentSpanId === null) continue
      expect(ids.has(span.parentSpanId)).toBe(span.parentSpanId === SPAN_A)
    }

    const groups = groupByAttribute(spans)
    expect(groups.size).toBe(3)
    expect([...groups.keys()]).toEqual(['sess-alpha', 'sess-beta', TRACE_UNGROUPED])
    // Every span is grouped — including the three whose parents resolve to
    // nothing — and no span is dropped.
    expect([...groups.get('sess-alpha')!.map((span) => span.spanId)]).toEqual([SPAN_A, SPAN_B, SPAN_C])
    expect([...groups.get('sess-beta')!.map((span) => span.spanId)]).toEqual([SPAN_D, SPAN_E])
    expect([...groups.get(TRACE_UNGROUPED)!.map((span) => span.spanId)]).toEqual([SPAN_F])

    // sess-beta spans carry two different trace ids: trace- or
    // ancestry-based grouping would split the session in two (the measured
    // fragmentation); the attribute keeps it whole.
    expect(new Set(groups.get('sess-beta')!.map((span) => span.traceId)).size).toBe(2)
  })

  it('falls back to the trace id when the grouping attribute is absent or unusable', () => {
    const missing = bareSpan('t1', 's1')
    const nullValue = bareSpan('t1', 's2', { 'session.id': null })
    const empty = bareSpan('t2', 's3', { 'session.id': '' })
    const structured = bareSpan('t2', 's4', { 'session.id': { run: 1 } })

    const groups = groupByAttribute([missing, nullValue, empty, structured])
    expect([...groups.keys()]).toEqual(['t1', 't2'])
    expect(groups.get('t1')).toEqual([missing, nullValue])
    expect(groups.get('t2')).toEqual([empty, structured])
  })

  it('groups by any attribute key it is given, uniting trace ids', () => {
    const first = bareSpan('t1', 's1', { 'run.id': 'run-9' })
    const second = bareSpan('t2', 's2', { 'run.id': 'run-9' })
    const groups = groupByAttribute([first, second], 'run.id')
    expect([...groups.keys()]).toEqual(['run-9'])
    expect(groups.get('run-9')).toHaveLength(2)
  })
})

/** Minimal decoded span for the grouping unit tests. */
function bareSpan(
  traceId: string,
  spanId: string,
  attributes: Record<string, unknown> = {},
): OtlpSpan {
  return {
    traceId,
    spanId,
    parentSpanId: null,
    name: 'x',
    kind: 'internal',
    startTimeUnixNano: '1',
    endTimeUnixNano: '2',
    timestamp: new Date(0).toISOString(),
    durationMs: 0,
    status: { code: 'unset' },
    attributes,
    resource: {},
    scope: {},
  }
}

// ---------------------------------------------------------------------------
// Protobuf wire helpers — the encoding-parity fixture
// ---------------------------------------------------------------------------

function pbVarint(value: bigint | number): number[] {
  let rest = BigInt(value)
  const out: number[] = []
  for (;;) {
    const byte = Number(rest & 0x7fn)
    rest >>= 7n
    if (rest === 0n) {
      out.push(byte)
      return out
    }
    out.push(byte | 0x80)
  }
}

const pbTag = (field: number, wire: number): number[] =>
  pbVarint((BigInt(field) << 3n) | BigInt(wire))

function pbBytesField(field: number, data: number[] | Uint8Array): number[] {
  const bytes = data instanceof Uint8Array ? Array.from(data) : data
  return [...pbTag(field, 2), ...pbVarint(bytes.length), ...bytes]
}

const pbStringField = (field: number, value: string): number[] =>
  pbBytesField(field, Array.from(Buffer.from(value, 'utf8')))

const pbVarintField = (field: number, value: bigint | number): number[] => [
  ...pbTag(field, 0),
  ...pbVarint(value),
]

function pbFixed64Field(field: number, value: bigint): number[] {
  const out = pbTag(field, 1)
  for (let byte = 0; byte < 8; byte++) {
    out.push(Number((value >> BigInt(8 * byte)) & 0xffn))
  }
  return out
}

const pbSubmessage = (field: number, content: number[]): number[] => pbBytesField(field, content)
