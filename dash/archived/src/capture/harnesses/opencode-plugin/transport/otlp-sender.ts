// The OpenCode capture plugin's OTLP/HTTP sender.
//
// The wire shapes, the endpoint, the delivery error and the body builder are shared with the
// other capture plugins, so the implementation lives in `../../transport/otlp-sender.ts` and this
// module re-exports it. The OpenCode plugin keeps its own module name so its imports, its
// bundle and its tests do not change.

export {
  DEFAULT_OTLP_ENDPOINT,
  DEFAULT_SCOPE_NAME,
  DEFAULT_SCOPE_VERSION,
  DEFAULT_SERVICE_NAME,
  OTLP_TRACES_PATH,
  OtlpDeliveryError,
  buildOtlpTracesBody,
} from '../../transport/otlp-sender.js'
export type {
  OtlpFetch,
  OtlpTracesBody,
  OtlpTracesIdentity,
  OtlpWireAttribute,
  OtlpWireSpan,
  OtlpWireStatus,
  OtlpWireValue,
} from '../../transport/otlp-sender.js'
