// The OpenCode plugin span envelope (wire contract 3, decision D9).
//
// The dash-CLI-managed plugin emits one OTLP trace per OpenCode session, made of
// `request`, `step` and `tool` spans with their identity under the `opencode.*`
// namespace (`capture/harnesses/opencode-plugin/contract.ts` is the single home of
// those keys). Every envelope span names the contract it was emitted under in
// `OPENCODE_CONTRACT_VERSION_KEY`. Ingest claims an envelope span only when that
// version is the supported one; an envelope span with an unknown or missing version
// (contract 2 included) quarantines with `UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON`
// and is never attributed to any harness.
//
// Why the envelope is opt-in by marker rather than by namespace: OpenCode's own
// native `Session.load` app span carries a bare `opencode.session.id` with no span
// kind, turn, model, tool, prompt, or version marker, and that shape is not the
// envelope (it falls to the unclaimed quarantine, decision D6). Only a span carrying
// the version key or a span-kind/turn/model/tool/prompt/call/message/hook/context
// marker is the envelope.
//
// This module stays a LEAF (see `src/architecture/opencode-leaves.test.ts`): it may
// not value-import engine or plugin code, so the strings below repeat the contract
// module's. `opencode-envelope.test.ts` pins `OPENCODE_CONTRACT_VERSION_KEY` and the
// supported version against `contract.ts`, so a drift fails there. The marker
// predicates below are the only surface; the keys themselves live in `contract.ts`.
//
// Attribution never reads `service.name` (R6.2) — that value arrives as
// `RawSpan.source` and is not consulted here either.

import type { RawSpan } from './base.js'

/** The plugin contract this build claims. A bump invalidates stale checkpoints. */
const OPENCODE_CONTRACT_VERSION = '3'

/** The attribute an envelope span names its emitting contract under. */
export const OPENCODE_CONTRACT_VERSION_KEY = 'opencode.contract_version'

/**
 * The quarantine reason an envelope span with an unknown or missing contract
 * version files under. A fixed string (not a formatted one) so the quarantine
 * view and per-harness wipes can match it exactly.
 */
export const UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON = 'unknown opencode contract version'

/**
 * Envelope markers that, with or without the version key, make a span the envelope.
 * `opencode.span.` is the contract-3 marker every plugin span carries; the rest keep
 * recognising a span of an earlier contract whose version key is missing, so it
 * quarantines by version instead of falling to the unclaimed quarantine.
 */
const ENVELOPE_MARKER_PREFIXES = [
  'opencode.span.',
  'opencode.turn.',
  'opencode.model.',
  'opencode.tool.',
  'opencode.prompt.',
  'opencode.call.',
  'opencode.message.',
  'opencode.hook',
  'opencode.context.',
] as const

function hasMarkerPrefix(attributes: Record<string, unknown>): boolean {
  // A marker is the exact key or a dotted child of it: `opencode.hook`
  // matches, `opencode.hook.name` matches, but `opencode.hookyMcHookface`
  // must not. Prefixes that already end in a dot keep their verbatim match.
  return Object.keys(attributes).some((key) =>
    ENVELOPE_MARKER_PREFIXES.some(
      (prefix) => key === prefix || key.startsWith(prefix.endsWith('.') ? prefix : `${prefix}.`),
    ),
  )
}

/** True when the span is the plugin envelope, whatever version it names. */
export function isOpencodeEnvelopeSpan(span: RawSpan): boolean {
  return OPENCODE_CONTRACT_VERSION_KEY in span.attributes || hasMarkerPrefix(span.attributes)
}

/** The contract version the span names, or undefined when missing or not a string. */
function opencodeEnvelopeVersion(span: RawSpan): string | undefined {
  const version = span.attributes[OPENCODE_CONTRACT_VERSION_KEY]
  return typeof version === 'string' && version !== '' ? version : undefined
}

/** True when the span is the envelope and names the supported contract. */
export function isSupportedOpencodeEnvelope(span: RawSpan): boolean {
  return isOpencodeEnvelopeSpan(span) && opencodeEnvelopeVersion(span) === OPENCODE_CONTRACT_VERSION
}
