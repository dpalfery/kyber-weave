// Integration and unit tests for the OTLP receiver (R2.1–R2.3). The two
// acceptance criteria that carry weight are 2.2 and 2.3: the same fixture
// posted as `application/json` (what the existing collectors hand-roll) and
// as `application/x-protobuf` (what an OTel SDK exporter emits by default)
// must land as *identical* stored records. The protobuf fixture below is
// genuine wire-format bytes, encoded field-by-field in this file — not the
// JSON body relabelled — so the equality assertion exercises the receiver's
// protobuf decoder rather than trusting it.

import { afterAll, describe, expect, it } from 'vitest'

import {
  DEFAULT_OTLP_PORT,
  OTLP_LOGS_PATH,
  OTLP_TRACES_PATH,
  InMemoryLogStore,
  InMemorySpanStore,
  OtlpDecodeError,
  OtlpReceiver,
  decodeOtlpLogJson,
  decodeOtlpLogProtobuf,
  decodeOtlpJson,
  decodeOtlpProtobuf,
  type OtlpLog,
  type OtlpSpan,
} from './receiver.js'

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
    const wrongPath = await fetch(`${url}/v1/metrics`, { method: 'POST' })
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
