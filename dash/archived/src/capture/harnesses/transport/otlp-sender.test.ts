// The shared OTLP sender: the endpoint, the delivery error and the export body builder both
// capture plugins use.
//
// The sender is extracted from the OpenCode capture plugin's own sender so the two plugins share
// one implementation of the wire format instead of forking it. The shared module is written by the
// next task, so every test here fails until it exists; the module is loaded inside each test, so
// the RED proof is per test.
//
// The contract this file pins:
//
//   - the export goes to the loopback OTLP/HTTP traces endpoint and nowhere else:
//     http://127.0.0.1:4318/v1/traces;
//   - the delivery error carries the status code, so the caller can act on a 429;
//   - the body builder produces an OTLP `ExportTraceServiceRequest`: the spans ride under
//     `resourceSpans[].scopeSpans[].spans`, and the resource names the service;
//   - the OpenCode plugin's own sender module becomes a thin re-export shim of the shared one.

import { describe, expect, it } from 'vitest'

/** The loopback OTLP/HTTP traces endpoint the receiver listens on. */
const LOOPBACK_TRACES_URL = 'http://127.0.0.1:4318/v1/traces'

type WireSpan = {
  traceId: string
  spanId: string
  name: string
  kind: 'SPAN_KIND_INTERNAL'
  startTimeUnixNano: string
  endTimeUnixNano: string
  attributes: Array<{ key: string; value: { stringValue: string } }>
}

/** The body as this file reads it: the shape an OTLP trace export request must have. */
type OtlpBody = {
  resourceSpans: Array<{
    resource?: { attributes?: Array<{ key: string; value?: unknown }> }
    scopeSpans?: Array<{ spans?: Array<{ name?: string }> }>
  }>
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

describe('the shared OTLP sender', () => {
  it('exports to the loopback traces path and nowhere else', async () => {
    const module = await import('./otlp-sender.js')
    expect(module.OTLP_TRACES_PATH).toBe('/v1/traces')
    expect(module.DEFAULT_OTLP_ENDPOINT).toBe(LOOPBACK_TRACES_URL)
  })

  it('carries the status code on a delivery error, so a 429 can be acted on', async () => {
    const module = await import('./otlp-sender.js')
    const error = new module.OtlpDeliveryError(429, 'Too Many Requests')
    expect(error).toBeInstanceOf(Error)
    expect(error.statusCode).toBe(429)
    expect(error.message).toContain('429')
  })

  it('builds an OTLP trace export body that carries the spans', async () => {
    const module = await import('./otlp-sender.js')
    const body = module.buildOtlpTracesBody([span('one'), span('two')]) as OtlpBody
    expect(Array.isArray(body.resourceSpans)).toBe(true)
    expect(body.resourceSpans).toHaveLength(1)
    const resource = body.resourceSpans[0]!
    const scopeSpans = resource.scopeSpans ?? []
    expect(scopeSpans).toHaveLength(1)
    expect(scopeSpans[0]?.spans?.map((entry) => entry.name)).toEqual(['one', 'two'])
    const service = (resource.resource?.attributes ?? []).find(
      (attribute) => attribute.key === 'service.name',
    )
    expect(service, 'the resource names the service').toBeDefined()
    const value = service?.value as { stringValue?: unknown } | undefined
    expect(value?.stringValue).toBeTruthy()
  })

  it('builds an empty body for no spans, rather than a malformed one', async () => {
    const module = await import('./otlp-sender.js')
    const body = module.buildOtlpTracesBody([]) as OtlpBody
    expect(body.resourceSpans).toHaveLength(1)
    expect(body.resourceSpans[0]?.scopeSpans?.[0]?.spans).toEqual([])
  })
})

describe('the OpenCode plugin sender is a thin re-export of the shared one', () => {
  it('re-exports the same endpoint, error and body builder', async () => {
    const shared = await import('./otlp-sender.js')
    const shim = await import('../opencode-plugin/transport/otlp-sender.js')
    expect(shim.DEFAULT_OTLP_ENDPOINT).toBe(shared.DEFAULT_OTLP_ENDPOINT)
    expect(shim.OTLP_TRACES_PATH).toBe(shared.OTLP_TRACES_PATH)
    expect(shim.OtlpDeliveryError).toBe(shared.OtlpDeliveryError)
    expect(shim.buildOtlpTracesBody).toBe(shared.buildOtlpTracesBody)
  })
})
