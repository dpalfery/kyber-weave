// OTLP/HTTP wire shapes for the OpenCode capture plugin (wire contract 3).
//
// The plugin builds finished request, step and tool spans (`../mapper.ts`) and the bounded outbox
// (`./outbox.ts`) posts them to the loopback receiver (live OTLP/HTTP on 127.0.0.1:4318, Q2). This
// module only names the wire types, the endpoint, the delivery error, and builds a valid
// `ExportTraceServiceRequest` body. Boundedness, retry and drop accounting live in `outbox.ts`.

/** OTLP/HTTP trace export path on the loopback receiver. */
export const OTLP_TRACES_PATH = '/v1/traces'

/** Live loopback OTLP/HTTP endpoint (Q2): never leaves this machine. */
export const DEFAULT_OTLP_ENDPOINT = `http://127.0.0.1:4318${OTLP_TRACES_PATH}`

export type OtlpFetch = (url: string, init?: RequestInit) => Promise<Response>

/** A non-2xx answer from the receiver; `statusCode` drives 429 handling. */
export class OtlpDeliveryError extends Error {
  readonly statusCode: number

  constructor(statusCode: number, statusText: string) {
    super(`OTLP delivery failed: ${statusCode} ${statusText}`.trim())
    this.name = 'OtlpDeliveryError'
    this.statusCode = statusCode
  }
}

/** An OTLP/JSON attribute value. Integers ride as decimal strings, as the OTLP JSON mapping requires. */
export type OtlpWireValue =
  | { stringValue: string }
  | { intValue: string }
  | { doubleValue: number }
  | { arrayValue: { values: Array<{ stringValue: string }> } }

export type OtlpWireAttribute = { key: string; value: OtlpWireValue }

export type OtlpWireStatus = { code: 'STATUS_CODE_OK' | 'STATUS_CODE_ERROR' }

export type OtlpWireSpan = {
  traceId: string
  spanId: string
  /** Absent for a root span; never an empty string. */
  parentSpanId?: string
  name: string
  kind: 'SPAN_KIND_INTERNAL'
  startTimeUnixNano: string
  endTimeUnixNano: string
  attributes: OtlpWireAttribute[]
  status?: OtlpWireStatus
}

export type OtlpTracesBody = {
  resourceSpans: Array<{
    resource: { attributes: OtlpWireAttribute[] }
    scopeSpans: Array<{
      scope: { name: string; version: string }
      spans: OtlpWireSpan[]
    }>
  }>
}

const SCOPE_NAME = 'kyberdash.opencode-plugin'
const SCOPE_VERSION = '1.0.0'
const SERVICE_NAME = 'kyberdash-opencode-plugin'

/** Build the OTLP JSON `ExportTraceServiceRequest` for finished spans. */
export function buildOtlpTracesBody(spans: readonly OtlpWireSpan[]): OtlpTracesBody {
  return {
    resourceSpans: [
      {
        resource: {
          attributes: [{ key: 'service.name', value: { stringValue: SERVICE_NAME } }],
        },
        scopeSpans: [
          {
            scope: { name: SCOPE_NAME, version: SCOPE_VERSION },
            spans: [...spans],
          },
        ],
      },
    ],
  }
}
