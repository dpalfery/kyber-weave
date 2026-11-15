// The Factory (Droid) plugin span envelope.
//
// The Kyber-owned Factory plugin emits one span per session, turn and tool
// call, with their identity under the `factory.*` namespace
// (`factory.session.id`, `factory.model.id`, `factory.tool.name`, and the
// contract it was emitted under in `factory.contract_version`). Every envelope
// span names that contract version. Ingest claims an envelope span only when
// the version is the supported one; an envelope span naming any other version,
// or none, scores zero and quarantines under
// `UNKNOWN_DROID_CONTRACT_VERSION_REASON` — it is never claimed and never
// misattributed to any harness. The same guard the OpenCode envelope carries.
//
// The envelope is recognised by the `factory.` namespace and by nothing else:
// a lookalike key (`factoryx.session.id`) is not the envelope, and the native
// export's `gen_ai.*` vocabulary is not the envelope either. The span's source
// name is never consulted (R6.2) — that value arrives as `RawSpan.source`.
//
// This module stays a LEAF (mirroring `canon/adapters/opencode-envelope.ts`):
// it may not value-import engine or plugin code, so the contract strings below
// repeat the wire contract's. An `import type` is erased at build time and
// carries no behaviour, so it is allowed; a runtime import of any project
// module or package is a finding.

import type { RawSpan } from './base.js'

/** The plugin contract this build claims. A bump invalidates stale checkpoints. */
const DROID_CONTRACT_VERSION = '1'

/** The attribute an envelope span names its emitting contract under. */
export const DROID_CONTRACT_VERSION_KEY = 'factory.contract_version'

/**
 * The namespace every plugin envelope key sits under. A key belongs to the
 * envelope when it starts with it, so `factoryx.session.id` — a lookalike — is
 * not the envelope.
 */
const ENVELOPE_NAMESPACE = 'factory.'

/**
 * The quarantine reason an envelope span with an unknown or missing contract
 * version files under. A fixed string (not a formatted one) so the quarantine
 * view and per-harness wipes can match it exactly.
 */
export const UNKNOWN_DROID_CONTRACT_VERSION_REASON = 'unknown droid contract version'

/** True when the span carries the plugin envelope's namespace. */
function hasEnvelopeMarker(attributes: Record<string, unknown>): boolean {
  return Object.keys(attributes).some((key) => key.startsWith(ENVELOPE_NAMESPACE))
}

/** True when the span is the plugin envelope, whatever version it names. */
export function isDroidEnvelopeSpan(span: RawSpan): boolean {
  return DROID_CONTRACT_VERSION_KEY in span.attributes || hasEnvelopeMarker(span.attributes)
}

/** The contract version the span names, or undefined when missing or not a string. */
function droidEnvelopeVersion(span: RawSpan): string | undefined {
  const version = span.attributes[DROID_CONTRACT_VERSION_KEY]
  return typeof version === 'string' && version !== '' ? version : undefined
}

/** True when the span is the envelope and names the supported contract. */
export function isSupportedDroidEnvelope(span: RawSpan): boolean {
  return isDroidEnvelopeSpan(span) && droidEnvelopeVersion(span) === DROID_CONTRACT_VERSION
}
