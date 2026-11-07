// Integration and unit tests for the OTLP receiver (R2.1–R2.3). The two
// acceptance criteria that carry weight are 2.2 and 2.3: the same fixture
// posted as `application/json` (what the existing collectors hand-roll) and
// as `application/x-protobuf` (what an OTel SDK exporter emits by default)
// must land as *identical* stored records. The protobuf fixture below is
// genuine wire-format bytes, encoded field-by-field in this file — not the
// JSON body relabelled — so the equality assertion exercises the receiver's
// protobuf decoder rather than trusting it.

import { createRequire } from 'node:module'
import type { IncomingMessage, ServerResponse } from 'node:http'

import { afterAll, describe, expect, it, vi } from 'vitest'

import { decodeOtlpMetricsProtobuf } from './receiver.js'
import {
  DEFAULT_OTLP_PORT,
  OTLP_ADMIN_PAUSE_PATH,
  OTLP_ADMIN_RESUME_PATH,
  OTLP_LOGS_PATH,
  OTLP_TRACES_PATH,
  InMemoryLogStore,
  InMemorySpanStore,
  OtlpDecodeError,
  OtlpReceiver,
  decodeOtlpLogJson,
  decodeOtlpLogProtobuf,
  decodeOtlpJson,
  decodeOtlpMetricsJson,
  decodeOtlpProtobuf,
  type OtlpLog,
  type OtlpMetric,
  type OtlpSpan,
} from './receiver.js'

const PACKAGE_VERSION = String(
  (createRequire(import.meta.url)('../../package.json') as { version?: string }).version,
)

// ---------------------------------------------------------------------------
// One fixture, two encodings
// ---------------------------------------------------------------------------

const TRACE_ID = '0af7651916cd43dd8448eb211c80319c'
const ROOT_SPAN_ID = 'b7ad6b7169203331'
const CHILD_SPAN_ID = '5b8efff798038103'

/** Fixture attribute values, tagged so each encoder can render its wire form. */
type FixtureValue =
  | { t: 'string'; v: string }
  | { t: 'int'; v: number }
  | { t: 'double'; v: number }
  | { t: 'bool'; v: boolean }
  | { t: 'bytes'; v: string } // hex
  | { t: 'array'; v: FixtureValue[] }
  | { t: 'kvlist'; v: { key: string; value: FixtureValue }[] }

type FixtureAttribute = { key: string; value: FixtureValue }

type FixtureSpan = {
  spanId: string
  parentSpanId: string | null
  name: string
  kindNumber: number
  start: bigint
  end: bigint
  attributes: FixtureAttribute[]
  status: { code: number; message?: string }
}

const FIXTURE = {
  traceId: TRACE_ID,
  resourceAttributes: [
    { key: 'service.name', value: { t: 'string', v: 'codeburn' } },
    { key: 'service.version', value: { t: 'string', v: '0.9.23' } },
  ] as FixtureAttribute[],
  scopeName: 'codeburn.collector',
  scopeVersion: '1.0.0',
  spans: [
    {
      spanId: ROOT_SPAN_ID,
      parentSpanId: null,
      name: 'agent.turn',
      kindNumber: 1, // INTERNAL
      start: 1756478400123456789n,
      end: 1756478401373456789n,
      attributes: [
        { key: 'gen_ai.request.model', value: { t: 'string', v: 'claude-sonnet-4-5' } },
        { key: 'gen_ai.usage.input_tokens', value: { t: 'int', v: 965 } },
        { key: 'gen_ai.usage.output_tokens', value: { t: 'int', v: 50 } },
        { key: 'gen_ai.usage.cache_read', value: { t: 'int', v: 800 } },
        { key: 'gen_ai.request.temperature', value: { t: 'double', v: 0.7 } },
        { key: 'gen_ai.request.stream', value: { t: 'bool', v: true } },
        {
          key: 'gen_ai.prompt.fragments',
          value: { t: 'array', v: [{ t: 'string', v: 'system' }, { t: 'int', v: 2 }] },
        },
        {
          key: 'gen_ai.metadata',
          value: { t: 'kvlist', v: [{ key: 'run', value: { t: 'string', v: '7f3' } }] },
        },
      ] as FixtureAttribute[],
      status: { code: 1 }, // OK
    },
    {
      spanId: CHILD_SPAN_ID,
      parentSpanId: ROOT_SPAN_ID,
      name: 'tool.read_file',
      kindNumber: 3, // CLIENT
      start: 1756478400200000000n,
      end: 1756478400600000000n,
      attributes: [
        { key: 'tool.name', value: { t: 'string', v: 'read' } },
        { key: 'tool.payload.digest', value: { t: 'bytes', v: 'deadbeef01' } },
      ] as FixtureAttribute[],
      status: { code: 2, message: 'file not found' }, // ERROR
    },
  ] as FixtureSpan[],
}

// --- JSON wire form ---------------------------------------------------------

function jsonValue(value: FixtureValue): unknown {
  switch (value.t) {
    case 'string':
      return { stringValue: value.v }
    case 'int':
      // proto3 JSON encodes int64 as a decimal string.
      return { intValue: String(value.v) }
    case 'double':
      return { doubleValue: value.v }
    case 'bool':
      return { boolValue: value.v }
    case 'bytes':
      // proto3 JSON encodes bytes as base64.
      return { bytesValue: Buffer.from(value.v, 'hex').toString('base64') }
    case 'array':
      return { arrayValue: { values: value.v.map(jsonValue) } }
    case 'kvlist':
      return {
        kvlistValue: {
          values: value.v.map((entry) => ({ key: entry.key, value: jsonValue(entry.value) })),
        },
      }
  }
}

/**
 * The OTLP JSON request for the fixture. `hand-rolled` is the collector
 * style already posting to 4318 (hex ids, numeric enums); `canonical` is
 * proto3 JSON (base64 ids, symbol-name enums) as an OTel SDK would send.
 */
function jsonFixture(variant: 'hand-rolled' | 'canonical'): string {
  const canonical = variant === 'canonical'
  const id = (hex: string): string =>
    canonical ? Buffer.from(hex, 'hex').toString('base64') : hex
  const kind = (n: number): string | number =>
    canonical ? ['SPAN_KIND_UNSPECIFIED', 'SPAN_KIND_INTERNAL', 'SPAN_KIND_SERVER', 'SPAN_KIND_CLIENT', 'SPAN_KIND_PRODUCER', 'SPAN_KIND_CONSUMER'][n] : n
  const statusCode = (n: number): string | number =>
    canonical ? ['STATUS_CODE_UNSET', 'STATUS_CODE_OK', 'STATUS_CODE_ERROR'][n] : n

  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: FIXTURE.resourceAttributes.map((attribute) => ({
            key: attribute.key,
            value: jsonValue(attribute.value),
          })),
        },
        scopeSpans: [
          {
            scope: { name: FIXTURE.scopeName, version: FIXTURE.scopeVersion },
            spans: FIXTURE.spans.map((span) => ({
              traceId: id(FIXTURE.traceId),
              spanId: id(span.spanId),
              ...(span.parentSpanId === null ? {} : { parentSpanId: id(span.parentSpanId) }),
              name: span.name,
              kind: kind(span.kindNumber),
              startTimeUnixNano: span.start.toString(),
              endTimeUnixNano: span.end.toString(),
              attributes: span.attributes.map((attribute) => ({
                key: attribute.key,
                value: jsonValue(attribute.value),
              })),
              status:
                span.status.message === undefined
                  ? { code: statusCode(span.status.code) }
                  : { code: statusCode(span.status.code), message: span.status.message },
            })),
          },
        ],
      },
    ],
  })
}

// --- protobuf wire form -----------------------------------------------------

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

function pbTag(field: number, wire: number): number[] {
  return pbVarint((BigInt(field) << 3n) | BigInt(wire))
}

function pbLengthDelimited(field: number, data: number[] | Uint8Array): number[] {
  const bytes = data instanceof Uint8Array ? Array.from(data) : data
  return [...pbTag(field, 2), ...pbVarint(bytes.length), ...bytes]
}

function pbStringField(field: number, value: string): number[] {
  return pbLengthDelimited(field, Array.from(Buffer.from(value, 'utf8')))
}

function pbBytesField(field: number, hex: string): number[] {
  return pbLengthDelimited(field, Buffer.from(hex, 'hex'))
}

function pbVarintField(field: number, value: bigint | number): number[] {
  return [...pbTag(field, 0), ...pbVarint(value)]
}

function pbFixed64Field(field: number, value: bigint): number[] {
  const out = pbTag(field, 1)
  for (let byte = 0; byte < 8; byte++) {
    out.push(Number((value >> BigInt(8 * byte)) & 0xffn))
  }
  return out
}

function pbSubmessage(field: number, content: number[]): number[] {
  return pbLengthDelimited(field, content)
}

function doubleBits(value: number): bigint {
  const view = new DataView(new ArrayBuffer(8))
  view.setFloat64(0, value, false)
  return view.getBigUint64(0, false)
}

function pbValue(value: FixtureValue): number[] {
  switch (value.t) {
    case 'string':
      return pbStringField(1, value.v)
    case 'int':
      return pbVarintField(3, value.v)
    case 'double':
      return pbFixed64Field(4, doubleBits(value.v))
    case 'bool':
      return pbVarintField(2, value.v ? 1 : 0)
    case 'bytes':
      return pbBytesField(7, value.v)
    case 'array':
      return pbSubmessage(
        5,
        value.v.flatMap((item) => pbSubmessage(1, pbValue(item))),
      )
    case 'kvlist':
      return pbSubmessage(
        6,
        value.v.flatMap((entry) => pbSubmessage(1, [...pbStringField(1, entry.key), ...pbSubmessage(2, pbValue(entry.value))])),
      )
  }
}

function pbKeyValue(attribute: FixtureAttribute): number[] {
  return [...pbStringField(1, attribute.key), ...pbSubmessage(2, pbValue(attribute.value))]
}

/** Genuine OTLP protobuf bytes for the same fixture the JSON body carries. */
function protobufFixture(): Uint8Array {
  // Resource { repeated KeyValue attributes = 1 }, which is ResourceSpans
  // field 1 — the attributes nest inside a Resource message, not directly.
  const resource = FIXTURE.resourceAttributes.flatMap((attribute) =>
    pbLengthDelimited(1, pbKeyValue(attribute)),
  )
  const scopeSpans = [
    ...pbSubmessage(1, [
      ...pbStringField(1, FIXTURE.scopeName),
      ...pbStringField(2, FIXTURE.scopeVersion),
    ]),
    ...FIXTURE.spans.flatMap((span) =>
      pbSubmessage(2, [
        ...pbBytesField(1, FIXTURE.traceId),
        ...pbBytesField(2, span.spanId),
        ...(span.parentSpanId === null ? [] : pbBytesField(4, span.parentSpanId)),
        ...pbStringField(5, span.name),
        ...pbVarintField(6, span.kindNumber),
        ...pbFixed64Field(7, span.start),
        ...pbFixed64Field(8, span.end),
        ...span.attributes.flatMap((attribute) => pbLengthDelimited(9, pbKeyValue(attribute))),
        ...pbSubmessage(
          15,
          span.status.message === undefined
            ? pbVarintField(3, span.status.code)
            : [...pbStringField(2, span.status.message), ...pbVarintField(3, span.status.code)],
        ),
      ]),
    ),
  ]
  return Uint8Array.from(
    pbSubmessage(1, [...pbSubmessage(1, resource), ...pbSubmessage(2, scopeSpans)]),
  )
}

// --- expected decoded records ----------------------------------------------

/** The documented mapping from fixture values to plain JS values. */
function plainValue(value: FixtureValue): unknown {
  switch (value.t) {
    case 'string':
      return value.v
    case 'int':
      return value.v
    case 'double':
      return value.v
    case 'bool':
      return value.v
    case 'bytes':
      return value.v // hex, lower-case
    case 'array':
      return value.v.map(plainValue)
    case 'kvlist':
      return Object.fromEntries(value.v.map((entry) => [entry.key, plainValue(entry.value)]))
  }
}

function plainAttributes(attributes: readonly FixtureAttribute[]): Record<string, unknown> {
  return Object.fromEntries(attributes.map((a) => [a.key, plainValue(a.value)]))
}

// Timestamps and durations are written out literally, not recomputed with
// the implementation's formulas: 1756478400123456789 ns is
// 2025-08-29T14:40:00.123Z, and the fixture's deltas are exactly 1250 ms
// and 400 ms.
const EXPECTED_SPANS: OtlpSpan[] = [
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
    attributes: plainAttributes(FIXTURE.spans[0].attributes),
    resource: { 'service.name': 'codeburn', 'service.version': '0.9.23' },
    scope: { name: 'codeburn.collector', version: '1.0.0' },
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
    attributes: plainAttributes(FIXTURE.spans[1].attributes),
    resource: { 'service.name': 'codeburn', 'service.version': '0.9.23' },
    scope: { name: 'codeburn.collector', version: '1.0.0' },
  },
]

// ---------------------------------------------------------------------------
// Decoder unit tests
// ---------------------------------------------------------------------------

describe('decodeOtlpJson (R2.2)', () => {
  it('decodes the hand-rolled collector form: hex ids, numeric enums, string int64s', () => {
    expect(decodeOtlpJson(jsonFixture('hand-rolled'))).toEqual(EXPECTED_SPANS)
  })

  it('decodes the canonical proto3 JSON form to the same records: base64 ids, symbol enums', () => {
    expect(decodeOtlpJson(jsonFixture('canonical'))).toEqual(
      decodeOtlpJson(jsonFixture('hand-rolled')),
    )
  })

  it('accepts an empty ExportTraceServiceRequest', () => {
    expect(decodeOtlpJson('{}')).toEqual([])
    expect(decodeOtlpJson('{"resourceSpans": null}')).toEqual([])
  })

  it('rejects a body that is not JSON, with a diagnostic', () => {
    expect(() => decodeOtlpJson('not json at all')).toThrow(OtlpDecodeError)
    expect(() => decodeOtlpJson('[]')).toThrow(/must be a JSON object/)
  })

  it('rejects a span whose ids are missing or the wrong width', () => {
    const spanBody = (spanId: string): string =>
      JSON.stringify({
        resourceSpans: [
          { scopeSpans: [{ spans: [{ traceId: TRACE_ID, spanId, name: 'x', startTimeUnixNano: '1', endTimeUnixNano: '2' }] }] },
        ],
      })
    expect(() => decodeOtlpJson(spanBody(''))).toThrow(/traceId|spanId/)
    // 8 hex chars is half a span id: the wrong width must be rejected, not
    // silently padded or truncated.
    expect(() => decodeOtlpJson(spanBody('b7ad6b71'))).toThrow(/spanId/)
    expect(() => decodeOtlpJson(spanBody('not!base64##'))).toThrow(/spanId/)
  })

  it('rejects attributes that are not an array of key/value pairs', () => {
    const body = JSON.stringify({
      resourceSpans: [{ scopeSpans: [{ spans: [{ traceId: TRACE_ID, spanId: ROOT_SPAN_ID, name: 'x', startTimeUnixNano: '1', endTimeUnixNano: '2', attributes: { model: 'x' } }] }] }],
    })
    expect(() => decodeOtlpJson(body)).toThrow(/attributes must be an array/)
  })
})

describe('decodeOtlpProtobuf (R2.3)', () => {
  it('decodes genuine wire-format bytes to the same records as the JSON path', () => {
    expect(decodeOtlpProtobuf(protobufFixture())).toEqual(EXPECTED_SPANS)
  })

  it('rejects a truncated message', () => {
    const bytes = protobufFixture()
    expect(() => decodeOtlpProtobuf(bytes.subarray(0, bytes.length - 4))).toThrow(OtlpDecodeError)
  })

  it('skips unknown fields instead of rejecting the payload', () => {
    // A span carrying two fields this build does not know (a varint and a
    // length-delimited blob) must still decode: forward compatibility with
    // a newer OTLP revision.
    const span = [
      ...pbBytesField(1, FIXTURE.traceId),
      ...pbBytesField(2, ROOT_SPAN_ID),
      ...pbStringField(5, 'future.proof'),
      ...pbVarintField(6, 1),
      ...pbFixed64Field(7, 1756478400123456789n),
      ...pbFixed64Field(8, 1756478401373456789n),
      ...pbVarintField(99, 12345), // unknown varint
      ...pbLengthDelimited(100, [0xff, 0x00, 0xee]), // unknown blob
    ]
    const request = pbSubmessage(
      1,
      pbSubmessage(2, pbSubmessage(2, span)),
    )
    const spans = decodeOtlpProtobuf(Uint8Array.from(request))
    expect(spans).toHaveLength(1)
    expect(spans[0].name).toBe('future.proof')
    expect(spans[0].timestamp).toBe('2025-08-29T14:40:00.123Z')
  })
})

// ---------------------------------------------------------------------------
// Receiver integration tests
// ---------------------------------------------------------------------------

const started: OtlpReceiver[] = []

afterAll(async () => {
  await Promise.all(started.map((receiver) => receiver.stop()))
})

async function startReceiver(maxBodyBytes?: number): Promise<{
  receiver: OtlpReceiver
  store: InMemorySpanStore
  url: string
}> {
  const store = new InMemorySpanStore()
  const receiver = new OtlpReceiver({
    port: 0, // ephemeral: never fight a real collector or another test run
    store,
    ...(maxBodyBytes === undefined ? {} : { maxBodyBytes }),
  })
  await receiver.start()
  started.push(receiver)
  return { receiver, store, url: `http://127.0.0.1:${receiver.port}` }
}

function post(url: string, contentType: string, body: string | Uint8Array): Promise<Response> {
  return fetch(`${url}${OTLP_TRACES_PATH}`, {
    method: 'POST',
    headers: { 'content-type': contentType },
    // fetch takes a typed-array body at runtime; the pinned BodyInit type
    // predates TypeScript 5.8's generic typed arrays, so widen once here.
    body: body as BodyInit,
  })
}

/** A synthetic OTLP log record that correlates to the fixture's root span. */
function jsonLogFixture(): string {
  return JSON.stringify({
    resourceLogs: [
      {
        resource: { attributes: [{ key: 'service.name', value: { stringValue: 'synthetic-test' } }] },
        scopeLogs: [
          {
            scope: { name: 'synthetic.logger', version: '1.0.0' },
            logRecords: [
              {
                timeUnixNano: '1756478400123456789',
                traceId: TRACE_ID,
                spanId: ROOT_SPAN_ID,
                body: { stringValue: 'synthetic log enrichment' },
                attributes: [{ key: 'session_id', value: { stringValue: 'session-synthetic-1' } }],
              },
            ],
          },
        ],
      },
    ],
  })
}

/** Genuine OTLP/protobuf bytes for the same synthetic log fixture. */
function protobufLogFixture(): Uint8Array {
  const resource = pbLengthDelimited(
    1,
    pbKeyValue({ key: 'service.name', value: { t: 'string', v: 'synthetic-test' } }),
  )
  const logRecord = [
    ...pbFixed64Field(1, 1756478400123456789n),
    ...pbSubmessage(5, pbValue({ t: 'string', v: 'synthetic log enrichment' })),
    ...pbLengthDelimited(
      6,
      pbKeyValue({ key: 'session_id', value: { t: 'string', v: 'session-synthetic-1' } }),
    ),
    ...pbBytesField(9, TRACE_ID),
    ...pbBytesField(10, ROOT_SPAN_ID),
  ]
  const scopeLogs = [
    ...pbSubmessage(1, [...pbStringField(1, 'synthetic.logger'), ...pbStringField(2, '1.0.0')]),
    ...pbSubmessage(2, logRecord),
  ]
  return Uint8Array.from(pbSubmessage(1, [...pbSubmessage(1, resource), ...pbSubmessage(2, scopeLogs)]))
}

function postLogs(url: string, contentType: string, body: string | Uint8Array): Promise<Response> {
  return fetch(`${url}${OTLP_LOGS_PATH}`, {
    method: 'POST',
    headers: { 'content-type': contentType },
    body: body as BodyInit,
  })
}

describe('OtlpReceiver (R2.1)', () => {
  it('listens on the OTLP standard port 4318 by default', () => {
    expect(DEFAULT_OTLP_PORT).toBe(4318)
    expect(new OtlpReceiver().port).toBe(4318)
  })

  it('binds the port it was given and reports the assigned port', async () => {
    const { receiver } = await startReceiver()
    expect(receiver.port).toBeGreaterThan(0)
    const probe = await fetch(`http://127.0.0.1:${receiver.port}/`)
    expect(probe.status).toBe(404)
  })

  it('serves POST /v1/traces and nothing else at that path', async () => {
    const { url } = await startReceiver()
    // A path the receiver does not serve at all. `/v1/metrics` is served, so
    // it cannot stand in for "an unknown path" any more.
    const wrongPath = await fetch(`${url}/v1/not-a-signal`, { method: 'POST' })
    expect(wrongPath.status).toBe(404)

    const wrongMethod = await fetch(`${url}${OTLP_TRACES_PATH}`, { method: 'GET' })
    expect(wrongMethod.status).toBe(405)
    expect(wrongMethod.headers.get('allow')).toBe('POST')
  })

  it('stops listening on stop()', async () => {
    const store = new InMemorySpanStore()
    const receiver = new OtlpReceiver({ port: 0, store })
    await receiver.start()
    const url = `http://127.0.0.1:${receiver.port}`
    await post(url, 'application/json', jsonFixture('hand-rolled'))
    expect(store.spans).toHaveLength(2)

    await receiver.stop()
    await expect(post(url, 'application/json', jsonFixture('hand-rolled'))).rejects.toThrow()
    expect(store.spans).toHaveLength(2)
  })
})

describe('OtlpReceiver JSON encoding (R2.2)', () => {
  it('accepts application/json and stores the decoded spans', async () => {
    const { store, url } = await startReceiver()
    const response = await post(url, 'application/json', jsonFixture('hand-rolled'))

    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({ acceptedSpans: 2 })
    expect(store.spans).toEqual(EXPECTED_SPANS)
  })

  it('accepts the media type with parameters (charset)', async () => {
    const { store, url } = await startReceiver()
    const response = await post(url, 'application/json; charset=utf-8', jsonFixture('canonical'))
    expect(response.status).toBe(200)
    expect(store.spans).toEqual(EXPECTED_SPANS)
  })

  it('accepts the canonical proto3 JSON an OTel SDK would send', async () => {
    const { store, url } = await startReceiver()
    const response = await post(url, 'application/json', jsonFixture('canonical'))
    expect(response.status).toBe(200)
    expect(store.spans).toEqual(EXPECTED_SPANS)
  })
})

describe('OtlpReceiver protobuf encoding (R2.3)', () => {
  it('accepts application/x-protobuf and stores the decoded spans', async () => {
    const { store, url } = await startReceiver()
    const response = await post(url, 'application/x-protobuf', protobufFixture())

    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({ acceptedSpans: 2 })
    expect(store.spans).toEqual(EXPECTED_SPANS)
  })

  it('accepts application/protobuf as well', async () => {
    const { store, url } = await startReceiver()
    const response = await post(url, 'application/protobuf', protobufFixture())
    expect(response.status).toBe(200)
    expect(store.spans).toEqual(EXPECTED_SPANS)
  })
})

describe('OtlpReceiver log signal (ADR 0009)', () => {
  it('decodes equivalent JSON and protobuf log payloads into the same correlation-ready record', () => {
    expect(decodeOtlpLogProtobuf(protobufLogFixture())).toEqual(decodeOtlpLogJson(jsonLogFixture()))
  })

  it.each([
    ['application/json', jsonLogFixture()],
    ['application/x-protobuf', protobufLogFixture()],
  ] as const)('accepts %s at POST /v1/logs', async (contentType, body) => {
    const spans = new InMemorySpanStore()
    const logs = new InMemoryLogStore()
    const receiver = new OtlpReceiver({ port: 0, store: { spans, logs } })
    await receiver.start()
    started.push(receiver)

    const response = await postLogs(
      `http://127.0.0.1:${receiver.port}`,
      contentType,
      body,
    )

    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({ acceptedLogs: 1 })
    expect(logs.logs).toEqual([
      expect.objectContaining({
        traceId: TRACE_ID,
        spanId: ROOT_SPAN_ID,
        sessionId: 'session-synthetic-1',
        body: 'synthetic log enrichment',
      } satisfies Partial<OtlpLog>),
    ])
    expect(spans.spans).toEqual([])
  })
})

/**
 * Claude Code sends the conversation under `session.id`. The receiver read only
 * `session_id`, so every log's sessionId was null and the session-time fallback
 * was dead (D4). Both encodings read the same key, so both are pinned.
 */
describe('OtlpReceiver session identity key (D4)', () => {
  function jsonLogWithAttributes(attributes: readonly (readonly [string, string])[]): string {
    return JSON.stringify({
      resourceLogs: [
        {
          resource: { attributes: [{ key: 'service.name', value: { stringValue: 'claude-code-desktop' } }] },
          scopeLogs: [
            {
              scope: { name: 'com.anthropic.claude_code.events' },
              logRecords: [
                {
                  timeUnixNano: '1756478400123456789',
                  traceId: TRACE_ID,
                  spanId: ROOT_SPAN_ID,
                  body: { stringValue: 'claude_code.api_request_body' },
                  attributes: attributes.map(([key, value]) => ({ key, value: { stringValue: value } })),
                },
              ],
            },
          ],
        },
      ],
    })
  }

  function protobufLogWithAttributes(attributes: readonly (readonly [string, string])[]): Uint8Array {
    const resource = pbLengthDelimited(
      1,
      pbKeyValue({ key: 'service.name', value: { t: 'string', v: 'claude-code-desktop' } }),
    )
    const logRecord = [
      ...pbFixed64Field(1, 1756478400123456789n),
      ...pbSubmessage(5, pbValue({ t: 'string', v: 'claude_code.api_request_body' })),
      ...attributes.flatMap(([key, v]) => pbLengthDelimited(6, pbKeyValue({ key, value: { t: 'string', v } }))),
      ...pbBytesField(9, TRACE_ID),
      ...pbBytesField(10, ROOT_SPAN_ID),
    ]
    const scopeLogs = [
      ...pbSubmessage(1, [...pbStringField(1, 'com.anthropic.claude_code.events')]),
      ...pbSubmessage(2, logRecord),
    ]
    return Uint8Array.from(pbSubmessage(1, [...pbSubmessage(1, resource), ...pbSubmessage(2, scopeLogs)]))
  }

  it('reads the session from session.id in the JSON encoding when no session_id is sent', () => {
    const [log] = decodeOtlpLogJson(jsonLogWithAttributes([['session.id', 'session-desktop-1']]))
    expect(log?.sessionId).toBe('session-desktop-1')
  })

  it('reads the session from session.id in the protobuf encoding when no session_id is sent', () => {
    const [log] = decodeOtlpLogProtobuf(protobufLogWithAttributes([['session.id', 'session-desktop-1']]))
    expect(log?.sessionId).toBe('session-desktop-1')
  })

  it('prefers session_id over session.id when a sender sends both, in either encoding', () => {
    const attributes = [
      ['session.id', 'from-session-dot'],
      ['session_id', 'from-session-underscore'],
    ] as const
    expect(decodeOtlpLogJson(jsonLogWithAttributes(attributes))[0]?.sessionId).toBe('from-session-underscore')
    expect(decodeOtlpLogProtobuf(protobufLogWithAttributes(attributes))[0]?.sessionId).toBe(
      'from-session-underscore',
    )
  })

  /**
   * The JSON decoder rejects an empty `stringValue` object outright, so an empty
   * string reaches the session helper only as a bare JSON primitive (which
   * hand-rolled senders do post) or over protobuf.
   */
  function jsonLogWithBareAttributes(attributes: Record<string, string>): string {
    const parsed = JSON.parse(jsonLogWithAttributes([]))
    parsed.resourceLogs[0].scopeLogs[0].logRecords[0].attributes = Object.entries(attributes).map(
      ([key, value]) => ({ key, value }),
    )
    return JSON.stringify(parsed)
  }

  it('falls through an empty session_id to session.id, in either encoding', () => {
    expect(
      decodeOtlpLogJson(jsonLogWithBareAttributes({ session_id: '', 'session.id': 'from-session-dot' }))[0]?.sessionId,
    ).toBe('from-session-dot')
    expect(
      decodeOtlpLogProtobuf(
        protobufLogWithAttributes([
          ['session_id', ''],
          ['session.id', 'from-session-dot'],
        ]),
      )[0]?.sessionId,
    ).toBe('from-session-dot')
  })

  it('reads a null session when both session keys are empty or absent, in either encoding', () => {
    expect(decodeOtlpLogJson(jsonLogWithBareAttributes({ session_id: '', 'session.id': '' }))[0]?.sessionId).toBeNull()
    expect(
      decodeOtlpLogProtobuf(
        protobufLogWithAttributes([
          ['session_id', ''],
          ['session.id', ''],
        ]),
      )[0]?.sessionId,
    ).toBeNull()
    expect(decodeOtlpLogJson(jsonLogWithAttributes([]))[0]?.sessionId).toBeNull()
    expect(decodeOtlpLogProtobuf(protobufLogWithAttributes([]))[0]?.sessionId).toBeNull()
  })
})

describe('OtlpReceiver parity between encodings', () => {
  it('stores identical records for the same fixture posted in each encoding', async () => {
    const json = await startReceiver()
    const protobuf = await startReceiver()

    const jsonResponse = await post(json.url, 'application/json', jsonFixture('hand-rolled'))
    const protobufResponse = await post(protobuf.url, 'application/x-protobuf', protobufFixture())
    expect(jsonResponse.status).toBe(200)
    expect(protobufResponse.status).toBe(200)

    // Identical record-for-record, not merely similar.
    expect(protobuf.store.spans).toEqual(json.store.spans)
    expect(json.store.spans).toEqual(EXPECTED_SPANS)
    expect(protobuf.store.spans).toEqual(EXPECTED_SPANS)

    // And on the fields the store is keyed and grouped by.
    for (const spans of [json.store.spans, protobuf.store.spans]) {
      expect(spans.map((span) => span.spanId)).toEqual([ROOT_SPAN_ID, CHILD_SPAN_ID])
      expect(spans.map((span) => span.traceId)).toEqual([TRACE_ID, TRACE_ID])
      expect(spans.map((span) => span.name)).toEqual(['agent.turn', 'tool.read_file'])
    }
  })

  it('re-ingesting the same payload in the other encoding changes nothing stored', async () => {
    const { store, url } = await startReceiver()
    await post(url, 'application/json', jsonFixture('hand-rolled'))
    await post(url, 'application/x-protobuf', protobufFixture())

    // The in-memory store appends, but every record is the same shape, so
    // the protobuf pass is a re-ingest of the same corpus (R2.5's shape).
    expect(store.spans).toHaveLength(4)
    expect(store.spans[2]).toEqual(store.spans[0])
    expect(store.spans[3]).toEqual(store.spans[1])
  })
})

describe('OtlpReceiver malformed payloads', () => {
  it('rejects unparseable JSON with a diagnostic and ingests nothing', async () => {
    const { store, url } = await startReceiver()
    const response = await post(url, 'application/json', '{"resourceSpans": [') // truncated

    expect(response.status).toBe(400)
    const body = (await response.json()) as { error: { code: string; message: string } }
    expect(body.error.code).toBe('OTLP_MALFORMED_PAYLOAD')
    expect(body.error.message).toMatch(/does not parse/)
    expect(store.spans).toEqual([])
  })

  it('rejects truncated protobuf with a diagnostic and ingests nothing', async () => {
    const { store, url } = await startReceiver()
    const bytes = protobufFixture()
    const response = await post(url, 'application/x-protobuf', bytes.subarray(0, bytes.length - 6))

    expect(response.status).toBe(400)
    const body = (await response.json()) as { error: { code: string } }
    expect(body.error.code).toBe('OTLP_MALFORMED_PAYLOAD')
    expect(store.spans).toEqual([])
  })

  it('rejects an unsupported media type with 415', async () => {
    const { store, url } = await startReceiver()
    const response = await post(url, 'text/plain', jsonFixture('hand-rolled'))

    expect(response.status).toBe(415)
    expect(store.spans).toEqual([])
  })

  it('rejects an oversized body with 413', async () => {
    const { store, url } = await startReceiver(32)
    const response = await post(url, 'application/json', jsonFixture('hand-rolled'))

    expect(response.status).toBe(413)
    expect(store.spans).toEqual([])
  })

  it('treats an empty protobuf body as an empty, valid request', async () => {
    const { store, url } = await startReceiver()
    const response = await post(url, 'application/x-protobuf', new Uint8Array(0))

    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({ acceptedSpans: 0 })
    expect(store.spans).toEqual([])
  })
})

describe('OtlpReceiver GET /healthz (R10.6, R10.8)', () => {
  it('identifies this process as the KyberDash receiver, with the build version', async () => {
    const { url } = await startReceiver()
    const response = await fetch(`${url}/healthz`)

    expect(response.status).toBe(200)
    expect(response.headers.get('content-type')).toContain('application/json')
    // Identity keys keep their names; the health contract is additive (endpoint,
    // listening, lastAcceptedAt, window facts) so the tray probe keeps reading it.
    expect(await response.json()).toMatchObject({
      service: 'kyberdash-otlp',
      version: PACKAGE_VERSION,
      paused: false,
    })
  })

  it('still returns 404 or 405 for every other GET path', async () => {
    const { url } = await startReceiver()

    expect((await fetch(`${url}/`)).status).toBe(404)
    // `/v1/metrics` is served now, so it is not an "every other GET path"
    // any more; the metrics path's own method rule is pinned with it.
    expect((await fetch(`${url}/v1/not-a-signal`)).status).toBe(404)

    const traces = await fetch(`${url}${OTLP_TRACES_PATH}`)
    expect(traces.status).toBe(405)
    const logs = await fetch(`${url}${OTLP_LOGS_PATH}`)
    expect(logs.status).toBe(405)
  })
})

// Issue #312, plan T5 RED: the receiver pauses ingestion on an explicit admin
// call so a database clean can hold the store without racing the writer.
// Traces and logs shed load with 503 + Retry-After (OTLP exporters retry, so
// nothing is dropped); the liveness probe stays 200 so the tray still knows
// the port is ours. A lease TTL auto-resumes a cleaner that crashed mid-wipe.
// The admin routes do not exist yet — every test below fails until T5 lands.
describe('OtlpReceiver pause for database clean (issue #312)', () => {
  async function pause(url: string, body?: unknown): Promise<Response> {
    return fetch(`${url}${OTLP_ADMIN_PAUSE_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    })
  }

  it('pauses and resumes: traces shed load with 503 while paused, then ingest again', async () => {
    const { url } = await startReceiver()

    const paused = await pause(url)
    expect(paused.status).toBe(200)
    expect((await paused.json()) as { paused: boolean }).toMatchObject({ paused: true })

    const shed = await post(url, 'application/json', '{}')
    expect(shed.status).toBe(503)
    expect(shed.headers.get('retry-after')).not.toBeNull()

    const resumed = await fetch(`${url}${OTLP_ADMIN_RESUME_PATH}`, { method: 'POST' })
    expect(resumed.status).toBe(200)
    expect((await resumed.json()) as { paused: boolean }).toMatchObject({ paused: false })

    const accepted = await post(url, 'application/json', minimalTraceBody())
    expect(accepted.status).toBe(200)
  })

  it('rejects non-POST admin calls with 405', async () => {
    const { url } = await startReceiver()

    expect((await fetch(`${url}${OTLP_ADMIN_PAUSE_PATH}`)).status).toBe(405)
    expect((await fetch(`${url}${OTLP_ADMIN_RESUME_PATH}`)).status).toBe(405)
  })

  it('rejects a cross-origin admin POST with 403 and stays unpaused (F3)', async () => {
    const { receiver, url } = await startReceiver()

    for (const path of [OTLP_ADMIN_PAUSE_PATH, OTLP_ADMIN_RESUME_PATH]) {
      const response = await fetch(`${url}${path}`, {
        method: 'POST',
        headers: { Origin: 'https://evil.example' },
        body: '{}',
      })
      expect(response.status).toBe(403)
      expect(await response.json()).toMatchObject({ error: { code: 'OTLP_FORBIDDEN' } })
    }
    expect(receiver.isPaused()).toBe(false)

    const accepted = await post(url, 'application/json', '{}')
    expect(accepted.status).toBe(200)
  })

  it('rejects an admin pause from a non-loopback peer with 403 (F3)', async () => {
    const receiver = new OtlpReceiver({ port: 0 })
    const req = {
      url: OTLP_ADMIN_PAUSE_PATH,
      method: 'POST',
      headers: {},
      socket: { remoteAddress: '203.0.113.7' },
    } as unknown as IncomingMessage
    let status = 0
    const res = {
      setHeader: () => {},
      writeHead: (s: number) => {
        status = s
      },
      end: () => {},
    } as unknown as ServerResponse
    await receiver.handleRequest(req, res)
    expect(status).toBe(403)
    expect(receiver.isPaused()).toBe(false)
  })

  it('reports paused:true on healthz while paused, without breaking the tray probe', async () => {
    const { url } = await startReceiver()
    await pause(url)

    const health = await fetch(`${url}/healthz`)
    expect(health.status).toBe(200)
    expect(await health.json()).toMatchObject({ service: 'kyberdash-otlp', paused: true })

    await fetch(`${url}${OTLP_ADMIN_RESUME_PATH}`, { method: 'POST' })
    expect(await (await fetch(`${url}/healthz`)).json()).toMatchObject({
      service: 'kyberdash-otlp',
      paused: false,
    })
  })

  it('auto-resumes when the pause lease expires', async () => {
    vi.useFakeTimers()
    try {
      const { url } = await startReceiver()
      await pause(url, { leaseMs: 60_000 })

      expect((await post(url, 'application/json', '{}')).status).toBe(503)
      await vi.advanceTimersByTimeAsync(60_001)

      const accepted = await post(url, 'application/json', minimalTraceBody())
      expect(accepted.status).toBe(200)
    } finally {
      vi.useRealTimers()
    }
  })

  it('sheds logs with 503 while paused too', async () => {
    const { url } = await startReceiver()
    await pause(url)

    const shed = await fetch(`${url}${OTLP_LOGS_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{}',
    })
    expect(shed.status).toBe(503)
    expect(shed.headers.get('retry-after')).not.toBeNull()
  })
})

/** Smallest body the JSON trace decoder accepts: one fixture span. */
function minimalTraceBody(): string {
  return jsonFixture('hand-rolled')
}

// ---------------------------------------------------------------------------
// The native OTLP metrics path
// ---------------------------------------------------------------------------

/**
 * The OTLP metrics export path. Spelled out rather than imported because the
 * receiver does not serve it yet, and importing a name that does not exist
 * fails the whole file before a single case runs — which would say nothing
 * about the behaviour each case below requires.
 */
const OTLP_METRICS_PATH = '/v1/metrics'

/** A metric name from Factory's native `legacy` namespace. */
const LEGACY_METRIC_NAME = 'droid.code.files_modified'
/** A metric name from Factory's native `genai` namespace. */
const GENAI_METRIC_NAME = 'gen_ai.client.operation.duration'
/** The value the legacy metric's single data point carries. */
const LEGACY_METRIC_VALUE = 4200
/** The value the genai metric's single data point carries. */
const GENAI_METRIC_VALUE = 7300

/**
 * An OTLP/JSON metrics request carrying the names Factory's native export
 * emits — one from its `legacy` namespace and one from its `genai` namespace.
 * The receiver must accept the vocabulary without requiring any particular
 * name, so the fixture names real ones and the assertions watch them survive.
 */
function jsonMetricsFixture(): string {
  const metric = (name: string, value: number): unknown => ({
    name,
    description: `Factory native metric ${name}`,
    unit: '1',
    sum: {
      aggregationTemporality: 2,
      isMonotonic: true,
      dataPoints: [
        {
          asInt: String(value),
          timeUnixNano: '1756478400123456789',
          attributes: [
            { key: 'session.id', value: { stringValue: 'droid-session-native-only' } },
          ],
        },
      ],
    },
  })
  return JSON.stringify({
    resourceMetrics: [
      {
        resource: {
          attributes: [{ key: 'service.name', value: { stringValue: 'droid' } }],
        },
        scopeMetrics: [
          {
            scope: { name: 'droid.telemetry', version: '1.0.0' },
            metrics: [
              metric(LEGACY_METRIC_NAME, LEGACY_METRIC_VALUE),
              metric(GENAI_METRIC_NAME, GENAI_METRIC_VALUE),
            ],
          },
        ],
      },
    ],
  })
}

type ProtobufMetricPointSpec = {
  asInt?: number | bigint
  asDouble?: number
  count?: number | bigint
  sum?: number
  timeUnixNano?: bigint | string
  startTimeUnixNano?: bigint | string
  attributes?: FixtureAttribute[]
}

type ProtobufMetricSpec = {
  name: string
  description?: string
  unit?: string
  kind?: 'gauge' | 'sum' | 'histogram' | 'exponentialHistogram' | 'summary' | 'unknown'
  aggregationTemporality?: number
  isMonotonic?: boolean
  dataPoints?: ProtobufMetricPointSpec[]
}

function encodeMetricDataPoint(
  point: ProtobufMetricPointSpec,
  kind: ProtobufMetricSpec['kind'],
): number[] {
  const fields: number[] = []
  if (point.startTimeUnixNano !== undefined) {
    fields.push(...pbFixed64Field(2, BigInt(point.startTimeUnixNano)))
  }
  if (point.timeUnixNano !== undefined) {
    fields.push(...pbFixed64Field(3, BigInt(point.timeUnixNano)))
  }
  if (kind === 'histogram' || kind === 'summary' || kind === 'exponentialHistogram') {
    if (point.count !== undefined) {
      fields.push(...pbFixed64Field(4, BigInt(point.count)))
    }
    if (point.sum !== undefined) {
      fields.push(...pbFixed64Field(5, doubleBits(point.sum)))
    }
    const attrField = kind === 'histogram' ? 9 : kind === 'exponentialHistogram' ? 1 : 7
    if (point.attributes && point.attributes.length > 0) {
      for (const attr of point.attributes) {
        fields.push(...pbLengthDelimited(attrField, pbKeyValue(attr)))
      }
    }
  } else {
    if (point.asDouble !== undefined) {
      fields.push(...pbFixed64Field(4, doubleBits(point.asDouble)))
    }
    if (point.asInt !== undefined) {
      fields.push(...pbFixed64Field(6, BigInt(point.asInt)))
    }
    if (point.attributes && point.attributes.length > 0) {
      for (const attr of point.attributes) {
        fields.push(...pbLengthDelimited(7, pbKeyValue(attr)))
      }
    }
  }
  return fields
}

function encodeMetric(spec: ProtobufMetricSpec): number[] {
  const fields: number[] = [
    ...pbStringField(1, spec.name),
    ...(spec.description ? pbStringField(2, spec.description) : []),
    ...(spec.unit ? pbStringField(3, spec.unit) : []),
  ]

  const kind = spec.kind ?? 'sum'
  const points = (spec.dataPoints ?? []).flatMap((p) =>
    pbSubmessage(1, encodeMetricDataPoint(p, kind)),
  )

  switch (kind) {
    case 'gauge':
      fields.push(...pbSubmessage(5, points))
      break
    case 'sum':
      fields.push(
        ...pbSubmessage(7, [
          ...points,
          ...(spec.aggregationTemporality !== undefined
            ? pbVarintField(2, spec.aggregationTemporality)
            : []),
          ...(spec.isMonotonic !== undefined ? pbVarintField(3, spec.isMonotonic ? 1 : 0) : []),
        ]),
      )
      break
    case 'histogram':
      fields.push(
        ...pbSubmessage(9, [
          ...points,
          ...(spec.aggregationTemporality !== undefined
            ? pbVarintField(2, spec.aggregationTemporality)
            : []),
        ]),
      )
      break
    case 'exponentialHistogram':
      fields.push(
        ...pbSubmessage(10, [
          ...points,
          ...(spec.aggregationTemporality !== undefined
            ? pbVarintField(2, spec.aggregationTemporality)
            : []),
        ]),
      )
      break
    case 'summary':
      fields.push(...pbSubmessage(11, points))
      break
    case 'unknown':
      break
  }

  return fields
}

/**
 * Genuine OTLP protobuf bytes for metrics payloads. By default mirrors the
 * two-metric fixture jsonMetricsFixture() emits.
 */
function protobufMetricsFixture(
  customMetrics?: ProtobufMetricSpec[],
  options?: {
    resourceAttributes?: FixtureAttribute[]
    scopeName?: string
    scopeVersion?: string
  },
): Uint8Array {
  const resourceAttrs = options?.resourceAttributes ?? [
    { key: 'service.name', value: { t: 'string', v: 'droid' } },
  ]
  const scopeName = options?.scopeName ?? 'droid.telemetry'
  const scopeVersion = options?.scopeVersion ?? '1.0.0'

  const metrics: ProtobufMetricSpec[] =
    customMetrics ?? [
      {
        name: LEGACY_METRIC_NAME,
        description: `Factory native metric ${LEGACY_METRIC_NAME}`,
        unit: '1',
        kind: 'sum',
        aggregationTemporality: 2,
        isMonotonic: true,
        dataPoints: [
          {
            asInt: LEGACY_METRIC_VALUE,
            timeUnixNano: 1756478400123456789n,
            attributes: [
              { key: 'session.id', value: { t: 'string', v: 'droid-session-native-only' } },
            ],
          },
        ],
      },
      {
        name: GENAI_METRIC_NAME,
        description: `Factory native metric ${GENAI_METRIC_NAME}`,
        unit: '1',
        kind: 'sum',
        aggregationTemporality: 2,
        isMonotonic: true,
        dataPoints: [
          {
            asInt: GENAI_METRIC_VALUE,
            timeUnixNano: 1756478400123456789n,
            attributes: [
              { key: 'session.id', value: { t: 'string', v: 'droid-session-native-only' } },
            ],
          },
        ],
      },
    ]

  const resource = resourceAttrs.flatMap((attribute) =>
    pbLengthDelimited(1, pbKeyValue(attribute)),
  )

  const scopeMetrics = [
    ...pbSubmessage(1, [
      ...pbStringField(1, scopeName),
      ...pbStringField(2, scopeVersion),
    ]),
    ...metrics.flatMap((m) => pbSubmessage(2, encodeMetric(m))),
  ]

  const resourceMetrics = [
    ...pbSubmessage(1, resource),
    ...pbSubmessage(2, scopeMetrics),
  ]

  return Uint8Array.from(pbSubmessage(1, resourceMetrics))
}

/**
 * A receiver store that records what it is handed.
 *
 * The metrics port is offered on both wirings the receiver may use — flat on
 * the store, and nested beside `spans` and `logs` — so the assertions are
 * about what the receiver hands over, not about which wiring it looked for
 * the port by.
 */
class RecordingStore {
  readonly recordedSpans: OtlpSpan[] = []
  readonly recordedLogs: OtlpLog[] = []
  readonly recordedMetrics: unknown[] = []

  write(spans: readonly OtlpSpan[]): void {
    this.recordedSpans.push(...spans)
  }

  writeLogs(logs: readonly OtlpLog[]): void {
    this.recordedLogs.push(...logs)
  }

  writeMetrics(metrics: readonly unknown[]): void {
    this.recordedMetrics.push(...metrics)
  }

  readonly spans = {
    write: (spans: readonly OtlpSpan[]): void => {
      this.recordedSpans.push(...spans)
    },
  }

  readonly logs = {
    writeLogs: (logs: readonly OtlpLog[]): void => {
      this.recordedLogs.push(...logs)
    },
  }

  readonly metrics = {
    writeMetrics: (metrics: readonly unknown[]): void => {
      this.recordedMetrics.push(...metrics)
    },
  }
}

/** Every string that appears anywhere in a handed payload, keys included. */
function stringsIn(value: unknown): string[] {
  if (typeof value === 'string') return [value]
  if (Array.isArray(value)) return value.flatMap((entry) => stringsIn(entry))
  if (typeof value === 'object' && value !== null) {
    return Object.entries(value as Record<string, unknown>).flatMap(([key, entry]) => [
      key,
      ...stringsIn(entry),
    ])
  }
  return []
}

async function startReceiverWithStore(store: RecordingStore): Promise<{
  receiver: OtlpReceiver
  url: string
}> {
  const receiver = new OtlpReceiver({ port: 0, store })
  await receiver.start()
  started.push(receiver)
  return { receiver, url: `http://127.0.0.1:${receiver.port}` }
}

function postMetrics(url: string, contentType: string, body: string | Uint8Array): Promise<Response> {
  return fetch(`${url}${OTLP_METRICS_PATH}`, {
    method: 'POST',
    headers: { 'content-type': contentType },
    body: body as BodyInit,
  })
}

describe('OtlpReceiver metrics signal (the native OTLP path)', () => {
  it('serves POST /v1/metrics and hands the decoded metrics to the metrics sink', async () => {
    const store = new RecordingStore()
    const { receiver, url } = await startReceiverWithStore(store)

    const response = await postMetrics(url, 'application/json', jsonMetricsFixture())

    expect(response.status).toBe(200)
    expect(response.headers.get('content-type')).toContain('application/json')
    // The metric names the exporter sent reach the sink: the receiver decodes
    // the payload, it does not filter it by name.
    const handed = stringsIn(store.recordedMetrics)
    expect(handed, 'the decoded metrics must reach the metrics sink').toContain(LEGACY_METRIC_NAME)
    expect(handed).toContain(GENAI_METRIC_NAME)
    // The values are decoded too, not dropped on the way through.
    const recorded = JSON.stringify(store.recordedMetrics)
    expect(recorded).toContain(String(LEGACY_METRIC_VALUE))
    expect(recorded).toContain(String(GENAI_METRIC_VALUE))
    // A request that was accepted is not receiver-health evidence of failure.
    expect(receiver.getHealth().failures5m).toBe(0)
  })

  it('rejects a malformed metrics body with a diagnostic and hands nothing to the sink', async () => {
    const store = new RecordingStore()
    const { receiver, url } = await startReceiverWithStore(store)

    const response = await postMetrics(url, 'application/json', '{"resourceMetrics": [')

    expect(response.status).toBe(400)
    const body = (await response.json()) as { error: { code: string } }
    expect(body.error.code).toBe('OTLP_MALFORMED_PAYLOAD')
    expect(store.recordedMetrics).toEqual([])
    // A refused metrics payload is receiver-health evidence, exactly as a
    // refused trace or log payload is.
    expect(receiver.getHealth().failures5m).toBe(1)
  })

  it('sheds a metrics request with 503 while the receiver is paused', async () => {
    const store = new RecordingStore()
    const { receiver, url } = await startReceiverWithStore(store)
    const paused = await fetch(`${url}${OTLP_ADMIN_PAUSE_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: '{}',
    })
    expect(paused.status).toBe(200)

    const shed = await postMetrics(url, 'application/json', jsonMetricsFixture())
    expect(shed.status).toBe(503)
    expect(shed.headers.get('retry-after')).not.toBeNull()
    expect(store.recordedMetrics).toEqual([])
    // A shed while paused is an operator-chosen state, not a failure.
    expect(receiver.getHealth().failures5m).toBe(0)

    const resumed = await fetch(`${url}${OTLP_ADMIN_RESUME_PATH}`, { method: 'POST' })
    expect(resumed.status).toBe(200)
    const accepted = await postMetrics(url, 'application/json', jsonMetricsFixture())
    expect(accepted.status).toBe(200)
    expect(store.recordedMetrics.length).toBeGreaterThan(0)
  })

  it('leaves an unknown path a 404 and serves no OTLP/gRPC endpoint', async () => {
    const { url } = await startReceiverWithStore(new RecordingStore())

    const unknown = await fetch(`${url}/v1/not-a-signal`, { method: 'POST' })
    expect(unknown.status).toBe(404)
    expect(((await unknown.json()) as { error: { code: string } }).error.code).toBe('OTLP_NOT_FOUND')

    // OTLP over gRPC is not served: the receiver speaks HTTP only, so the
    // gRPC service path is an unknown path like any other.
    const grpc = await fetch(
      `${url}/opentelemetry.proto.collector.metrics.v1.MetricsService/Export`,
      { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' },
    )
    expect(grpc.status).toBe(404)
  })

  it('serves the export paths over POST only', async () => {
    const { url } = await startReceiverWithStore(new RecordingStore())

    for (const path of [OTLP_TRACES_PATH, OTLP_LOGS_PATH, OTLP_METRICS_PATH]) {
      const response = await fetch(`${url}${path}`)
      expect(response.status, `${path} must answer 405 for a GET`).toBe(405)
      expect(response.headers.get('allow')).toBe('POST')
    }
  })

  it('accepts application/x-protobuf at POST /v1/metrics and hands decoded metrics to the sink', async () => {
    const store = new RecordingStore()
    const { receiver, url } = await startReceiverWithStore(store)

    const response = await postMetrics(url, 'application/x-protobuf', protobufMetricsFixture())

    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({ acceptedMetrics: 2 })
    const handed = stringsIn(store.recordedMetrics)
    expect(handed, 'the decoded metrics must reach the metrics sink').toContain(LEGACY_METRIC_NAME)
    expect(handed).toContain(GENAI_METRIC_NAME)
    const recorded = JSON.stringify(store.recordedMetrics)
    expect(recorded).toContain(String(LEGACY_METRIC_VALUE))
    expect(recorded).toContain(String(GENAI_METRIC_VALUE))
    expect(receiver.getHealth().failures5m).toBe(0)
  })

  it('accepts application/protobuf with parameters (charset) at POST /v1/metrics', async () => {
    const store = new RecordingStore()
    const { receiver, url } = await startReceiverWithStore(store)

    const response = await postMetrics(
      url,
      'application/protobuf; charset=utf-8',
      protobufMetricsFixture(),
    )

    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({ acceptedMetrics: 2 })
    expect(receiver.getHealth().failures5m).toBe(0)
  })

    it('leaves the log path untouched by the metrics path', async () => {
    const store = new RecordingStore()
    const { url } = await startReceiverWithStore(store)

    const response = await postLogs(url, 'application/json', jsonLogFixture())
    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({ acceptedLogs: 1 })
    expect(store.recordedLogs).toHaveLength(1)
    // The metrics sink saw nothing: a log request is not a metrics request.
    expect(store.recordedMetrics).toEqual([])
  })
})

// ---------------------------------------------------------------------------
// The metric decode seam — identity stripping
// ---------------------------------------------------------------------------

/**
 * The identity keys a metric point may carry. A metric point is not a span,
 * but its attributes come from the same exporter resource, so the account
 * identity rides along in them.
 */
const METRIC_IDENTITY_KEYS = [
  'user.email',
  'user.id',
  'organization.id',
  'enduser.id',
  'account.id',
]

/** The correlation and usage keys a metric point must keep. */
const METRIC_RETAINED_ATTRIBUTES: Record<string, string> = {
  'session.id': 'synth-session-1',
  'conversation.id': 'synth-conversation-1',
  'gen_ai.request.model': 'synth-model',
}

/** The metric shapes whose points carry their own attributes. */
type MetricPointShape = 'sum' | 'histogram' | 'summary'

/**
 * An OTLP/JSON metrics body whose single data point carries identity
 * attributes beside the correlation and usage attributes. The resource
 * carries identity too, so the point's own attributes are the only place
 * the leak can hide.
 */
function metricIdentityBody(shape: MetricPointShape): string {
  const attributes = Object.entries({
    ...Object.fromEntries(
      METRIC_IDENTITY_KEYS.map((key) => [key, `synth-${key}`] as const),
    ),
    ...METRIC_RETAINED_ATTRIBUTES,
  }).map(([key, value]) => ({ key, value: { stringValue: value } }))
  const point = {
    timeUnixNano: '1756478400123456789',
    attributes,
    // A sum point carries a value; a histogram or summary point carries a
    // count and a sum instead.
    ...(shape === 'sum' ? { asDouble: 4200 } : { count: '3', sum: 12.5 }),
  }
  return JSON.stringify({
    resourceMetrics: [
      {
        resource: {
          attributes: [
            { key: 'service.name', value: { stringValue: 'droid' } },
            { key: 'user.email', value: { stringValue: 'synth-resource-email' } },
          ],
        },
        scopeMetrics: [
          {
            scope: { name: 'droid.telemetry', version: '1.0.0' },
            metrics: [
              {
                name: 'droid.code.files_modified',
                [shape]: { aggregationTemporality: 2, dataPoints: [point] },
              },
            ],
          },
        ],
      },
    ],
  })
}

/** The attribute keys a decoded metric point carries. */
function metricPointAttributeKeys(attributes: Record<string, unknown> | undefined): string[] {
  return Object.keys(attributes ?? {})
}

describe('the metric decode seam (identity stripping)', () => {
  it('strips identity keys from a decoded metric point and retains session/conversation/gen_ai', () => {
    const metrics = decodeOtlpMetricsJson(metricIdentityBody('sum'))
    expect(metrics).toHaveLength(1)
    const point = metrics[0]?.dataPoints[0]
    expect(point, 'the metric must carry its data point').toBeDefined()

    const keys = metricPointAttributeKeys(point?.attributes)
    for (const key of METRIC_IDENTITY_KEYS) {
      expect(keys, `metric point attribute ${key} must not survive the decode`).not.toContain(key)
    }
    for (const [key, value] of Object.entries(METRIC_RETAINED_ATTRIBUTES)) {
      expect(point?.attributes?.[key], `metric point attribute ${key} must survive the decode`).toBe(
        value,
      )
    }
    // The metric's own resource attributes are stripped on the same seam.
    expect(metricPointAttributeKeys(metrics[0]?.resource)).not.toContain('user.email')
  })

  it('strips identity keys from a histogram point that carries a count and a sum', () => {
    const metrics = decodeOtlpMetricsJson(metricIdentityBody('histogram'))
    expect(metrics).toHaveLength(1)
    const point = metrics[0]?.dataPoints[0]
    expect(point, 'the histogram must carry its data point').toBeDefined()
    // The count and the sum still travel; only the identity goes.
    expect(point?.count).toBe(3)
    expect(point?.sum).toBe(12.5)

    const keys = metricPointAttributeKeys(point?.attributes)
    for (const key of METRIC_IDENTITY_KEYS) {
      expect(keys, `histogram point attribute ${key} must not survive the decode`).not.toContain(key)
    }
    expect(point?.attributes?.['session.id']).toBe(METRIC_RETAINED_ATTRIBUTES['session.id'])
  })

  it('strips identity keys from a summary point', () => {
    const metrics = decodeOtlpMetricsJson(metricIdentityBody('summary'))
    expect(metrics).toHaveLength(1)
    const point = metrics[0]?.dataPoints[0]
    expect(point, 'the summary must carry its data point').toBeDefined()

    const keys = metricPointAttributeKeys(point?.attributes)
    for (const key of METRIC_IDENTITY_KEYS) {
      expect(keys, `summary point attribute ${key} must not survive the decode`).not.toContain(key)
    }
    expect(point?.attributes?.['gen_ai.request.model']).toBe(
      METRIC_RETAINED_ATTRIBUTES['gen_ai.request.model'],
    )
  })
})


describe('decodeOtlpMetricsProtobuf', () => {
  it('decodes gauge, sum, histogram, exponentialHistogram, and summary metrics', () => {
    const bytes = protobufMetricsFixture([
      {
        name: 'test.gauge',
        kind: 'gauge',
        dataPoints: [{ asDouble: 1.23, timeUnixNano: 1756478400123456789n }],
      },
      {
        name: 'test.sum',
        kind: 'sum',
        aggregationTemporality: 2,
        isMonotonic: true,
        dataPoints: [{ asInt: 42, timeUnixNano: 1756478400123456789n }],
      },
      {
        name: 'test.histogram',
        kind: 'histogram',
        aggregationTemporality: 1,
        dataPoints: [{ count: 10, sum: 55.5, timeUnixNano: 1756478400123456789n }],
      },
      {
        name: 'test.exponentialHistogram',
        kind: 'exponentialHistogram',
        aggregationTemporality: 2,
        dataPoints: [{ count: 5, sum: 20.0, timeUnixNano: 1756478400123456789n }],
      },
      {
        name: 'test.summary',
        kind: 'summary',
        dataPoints: [{ count: 8, sum: 100.0, timeUnixNano: 1756478400123456789n }],
      },
    ])

    const decoded = decodeOtlpMetricsProtobuf(bytes)
    expect(decoded).toHaveLength(5)
    expect(decoded.map((m: OtlpMetric) => m.kind)).toEqual([
      'gauge',
      'sum',
      'histogram',
      'exponentialHistogram',
      'summary',
    ])
    expect(decoded.map((m: OtlpMetric) => m.name)).toEqual([
      'test.gauge',
      'test.sum',
      'test.histogram',
      'test.exponentialHistogram',
      'test.summary',
    ])
    const expHist = decoded.find((m: OtlpMetric) => m.kind === 'exponentialHistogram')
    expect(expHist?.dataPoints).toHaveLength(1)
    expect(expHist?.dataPoints[0]?.count).toBe(5)
    expect(expHist?.dataPoints[0]?.sum).toBeCloseTo(20.0, 1)
  })

  it('decodes as_int and as_double NumberDataPoints with stripped attributes and string timestamps', () => {
    const bytes = protobufMetricsFixture(
      [
        {
          name: 'metric.points',
          kind: 'gauge',
          dataPoints: [
            {
              asInt: 100,
              startTimeUnixNano: 1756478400000000000n,
              timeUnixNano: 1756478400123456789n,
              attributes: [
                { key: 'session.id', value: { t: 'string', v: 'session-123' } },
                { key: 'user.email', value: { t: 'string', v: 'leak@example.com' } },
                { key: 'user.id', value: { t: 'string', v: 'usr-999' } },
              ],
            },
            {
              asDouble: 3.14159,
              startTimeUnixNano: 1756478400000000000n,
              timeUnixNano: 1756478400123456789n,
              attributes: [
                { key: 'conversation.id', value: { t: 'string', v: 'conv-456' } },
                { key: 'account.id', value: { t: 'string', v: 'acc-888' } },
              ],
            },
          ],
        },
      ],
      {
        resourceAttributes: [
          { key: 'service.name', value: { t: 'string', v: 'test-svc' } },
          { key: 'user.email', value: { t: 'string', v: 'leak-resource@example.com' } },
        ],
      },
    )

    const [metric] = decodeOtlpMetricsProtobuf(bytes)
    expect(metric?.dataPoints).toHaveLength(2)

    const intPoint = metric?.dataPoints[0]
    expect(intPoint?.value).toBe(100)
    expect(intPoint?.startTimeUnixNano).toBe('1756478400000000000')
    expect(intPoint?.timeUnixNano).toBe('1756478400123456789')
    expect(intPoint?.attributes['session.id']).toBe('session-123')
    expect(intPoint?.attributes['user.email']).toBeUndefined()
    expect(intPoint?.attributes['user.id']).toBeUndefined()

    const doublePoint = metric?.dataPoints[1]
    expect(doublePoint?.value).toBeCloseTo(3.14159, 4)
    expect(doublePoint?.startTimeUnixNano).toBe('1756478400000000000')
    expect(doublePoint?.timeUnixNano).toBe('1756478400123456789')
    expect(doublePoint?.attributes['conversation.id']).toBe('conv-456')
    expect(doublePoint?.attributes['account.id']).toBeUndefined()

    expect(metric?.resource['service.name']).toBe('test-svc')
    expect(metric?.resource['user.email']).toBeUndefined()
  })

  it('decodes sum with aggregation_temporality and is_monotonic', () => {
    const bytes = protobufMetricsFixture([
      {
        name: 'cumulative.monotonic.sum',
        kind: 'sum',
        aggregationTemporality: 2,
        isMonotonic: true,
        dataPoints: [{ asInt: 50, timeUnixNano: 1756478400123456789n }],
      },
      {
        name: 'delta.nonmonotonic.sum',
        kind: 'sum',
        aggregationTemporality: 1,
        isMonotonic: false,
        dataPoints: [{ asInt: -10, timeUnixNano: 1756478400123456789n }],
      },
    ])

    const [m1, m2] = decodeOtlpMetricsProtobuf(bytes)
    expect(m1?.aggregationTemporality).toBe(2)
    expect(m1?.isMonotonic).toBe(true)
    expect(m1?.dataPoints[0]?.value).toBe(50)

    expect(m2?.aggregationTemporality).toBe(1)
    expect(m2?.isMonotonic).toBe(false)
    expect(m2?.dataPoints[0]?.value).toBe(-10)
  })

  it('decodes histogram with count and sum', () => {
    const bytes = protobufMetricsFixture([
      {
        name: 'request.duration',
        kind: 'histogram',
        aggregationTemporality: 2,
        dataPoints: [
          {
            count: 42,
            sum: 1234.5,
            timeUnixNano: 1756478400123456789n,
          },
        ],
      },
    ])

    const [metric] = decodeOtlpMetricsProtobuf(bytes)
    expect(metric?.kind).toBe('histogram')
    expect(metric?.aggregationTemporality).toBe(2)
    expect(metric?.dataPoints).toHaveLength(1)
    const point = metric?.dataPoints[0]
    expect(point?.count).toBe(42)
    expect(point?.sum).toBeCloseTo(1234.5, 1)
    expect(point?.value).toBeNull()
  })

  it('decodes a metric with no data points as kind unknown and empty dataPoints array', () => {
    const bytes = protobufMetricsFixture([
      {
        name: 'unmeasured.metric',
        description: 'a metric with no data shape',
        unit: 'ms',
        kind: 'unknown',
      },
    ])

    const [metric] = decodeOtlpMetricsProtobuf(bytes)
    expect(metric).toMatchObject({
      name: 'unmeasured.metric',
      description: 'a metric with no data shape',
      unit: 'ms',
      kind: 'unknown',
      dataPoints: [],
    })
  })

  it('rejects a metric with multiple oneof data shapes set, throwing OtlpDecodeError', () => {
    // Malformed metric carrying both gauge (field 5) and sum (field 7)
    const malformedMetric = [
      ...pbStringField(1, 'malformed.metric'),
      ...pbSubmessage(5, pbSubmessage(1, pbFixed64Field(4, doubleBits(10.0)))),
      ...pbSubmessage(7, pbSubmessage(1, pbFixed64Field(6, 20n))),
    ]
    const scopeMetrics = [
      ...pbSubmessage(1, [...pbStringField(1, 'test.scope')]),
      ...pbSubmessage(2, malformedMetric),
    ]
    const resourceMetrics = [
      ...pbSubmessage(1, []),
      ...pbSubmessage(2, scopeMetrics),
    ]
    const bytes = Uint8Array.from(pbSubmessage(1, resourceMetrics))

    expect(() => decodeOtlpMetricsProtobuf(bytes)).toThrow(OtlpDecodeError)
  })

  it('returns independent resource and scope objects for sibling metrics in the same batch', () => {
    const bytes = protobufMetricsFixture([
      { name: 'm1', kind: 'gauge', dataPoints: [{ asInt: 1 }] },
      { name: 'm2', kind: 'gauge', dataPoints: [{ asInt: 2 }] },
    ])

    const [m1, m2] = decodeOtlpMetricsProtobuf(bytes)
    expect(m1?.resource).not.toBe(m2?.resource)
    expect(m1?.scope).not.toBe(m2?.scope)
    expect(m1?.resource).toEqual(m2?.resource)
    expect(m1?.scope).toEqual(m2?.scope)
  })

  it('normalizes unknown or invalid aggregationTemporality to undefined', () => {
    const sumWithUnknownTemporality = [
      ...pbStringField(1, 'sum.with.invalid.temporality'),
      ...pbSubmessage(7, [
        ...pbVarintField(2, 99), // 99 is invalid
        ...pbSubmessage(1, pbFixed64Field(6, 42n)),
      ]),
    ]
    const scopeMetrics = [
      ...pbSubmessage(1, [...pbStringField(1, 'test.scope')]),
      ...pbSubmessage(2, sumWithUnknownTemporality),
    ]
    const resourceMetrics = [
      ...pbSubmessage(1, []),
      ...pbSubmessage(2, scopeMetrics),
    ]
    const bytes = Uint8Array.from(pbSubmessage(1, resourceMetrics))

    const [metric] = decodeOtlpMetricsProtobuf(bytes)
    expect(metric?.aggregationTemporality).toBeUndefined()
  })

  it('throws OtlpDecodeError on truncated protobuf payload', () => {
    const bytes = protobufMetricsFixture()
    expect(() => decodeOtlpMetricsProtobuf(bytes.subarray(0, bytes.length - 4))).toThrow(
      OtlpDecodeError,
    )
  })
})
