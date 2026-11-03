// OTLP/HTTP wire shapes shared by the capture plugins (wire contract 3).
//
// Extracted from the OpenCode capture plugin's own sender so the Factory plugin posts the same
// wire format instead of forking it. The OpenCode module is now a re-export of this one.
//
// A plugin builds finished spans (`../mapper.ts` for OpenCode, `../factory-plugin/mapper.ts` for
// Factory) and a transport posts them to the loopback receiver (live OTLP/HTTP on 127.0.0.1:4318,
// Q2). This module only names the wire types, the endpoint, the delivery error, and builds a
// valid `ExportTraceServiceRequest` body. Boundedness, retry and drop accounting live in
// `outbox.ts`.
//
// This module is a LEAF: it imports nothing, so neither plugin drags engine code into the small
// bounded process it runs in.

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

/**
 * The service and scope a body names. The defaults are the OpenCode plugin's wire identity: it
 * is the plugin that used this sender first, so the rows it already produced and the bundle it
 * ships stay what they were. A plugin that reports under its own identity passes its own names.
 */
export type OtlpTracesIdentity = {
  /** The `service.name` the resource carries. */
  serviceName?: string
  /** The scope name the spans ride under. */
  scopeName?: string
  /** The scope version the spans ride under. */
  scopeVersion?: string
}

/** The OpenCode plugin's `service.name`, and the default for every body. */
export const DEFAULT_SERVICE_NAME = 'kyberdash-opencode-plugin'
/** The OpenCode plugin's scope name, and the default for every body. */
export const DEFAULT_SCOPE_NAME = 'kyberdash.opencode-plugin'
/** The scope version every body carries. */
export const DEFAULT_SCOPE_VERSION = '1.0.0'

/** Build the OTLP JSON `ExportTraceServiceRequest` for finished spans. */
export function buildOtlpTracesBody(
  spans: readonly OtlpWireSpan[],
  identity: OtlpTracesIdentity = {},
): OtlpTracesBody {
  const serviceName = identity.serviceName ?? DEFAULT_SERVICE_NAME
  const scopeName = identity.scopeName ?? DEFAULT_SCOPE_NAME
  const scopeVersion = identity.scopeVersion ?? DEFAULT_SCOPE_VERSION
  return {
    resourceSpans: [
      {
        resource: {
          attributes: [{ key: 'service.name', value: { stringValue: serviceName } }],
        },
        scopeSpans: [
          {
            scope: { name: scopeName, version: scopeVersion },
            spans: [...spans],
          },
        ],
      },
    ],
  }
}
