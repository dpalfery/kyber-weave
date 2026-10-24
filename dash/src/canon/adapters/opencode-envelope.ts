// The opencode plugin span/turn envelope (T1).
//
// The dash-CLI-managed plugin emits one OTLP span per turn event with its
// identity under the `opencode.*` namespace — the shape surveyed in T0
// (`capture/harnesses/opencode-plugin/fixtures/span-payload-skeleton.json`:
// session id, turn index, agent, model provider/id, hook). Every envelope
// span names the contract it was emitted under in
// `OPENCODE_CONTRACT_VERSION_KEY`. Ingest claims an envelope span only when
// that version is the supported one; an envelope span with an unknown or
// missing version quarantines with `UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON`
// and is never attributed to any harness.
//
// Why the envelope is opt-in by marker rather than by namespace: the
// pre-envelope `Session.load` app span already carries a bare
// `opencode.session.id` with no turn, model, tool, prompt, or version marker
// (see `fixtures/opencode-ai-sdk/non-model-app-spans.json`), and that shape
// keeps the legacy non-model path. Only a span carrying the version key or a
// turn/model/tool/prompt/call/message/hook marker is the envelope.
//
// Attribution never reads `service.name` (R6.2) — that value arrives as
// `RawSpan.source` and is not consulted here either.

import type { RawSpan } from './base.js'

/** The plugin contract this build claims. A bump invalidates stale checkpoints. */
export const OPENCODE_CONTRACT_VERSION = '2'

/** The attribute an envelope span names its emitting contract under. */
export const OPENCODE_CONTRACT_VERSION_KEY = 'opencode.contract_version'

/**
 * The quarantine reason an envelope span with an unknown or missing contract
 * version files under. A fixed string (not a formatted one) so the quarantine
 * view and per-harness wipes can match it exactly.
 */
export const UNKNOWN_OPENCODE_CONTRACT_VERSION_REASON = 'unknown opencode contract version'

/** The envelope session marker. Alone it is not the envelope (see above). */
export const OPENCODE_SESSION_ID_KEY = 'opencode.session.id'

/** Envelope markers that, with or without the version key, make a span the envelope. */
const ENVELOPE_MARKER_PREFIXES = [
  'opencode.turn.',
  'opencode.model.',
  'opencode.tool.',
  'opencode.prompt.',
  'opencode.call.',
  'opencode.message.',
  'opencode.hook',
  'opencode.context.',
] as const

/** Envelope operation markers. A tool marker wins over a model marker. */
const ENVELOPE_MODEL_KEYS = ['opencode.model.id', 'opencode.model.provider'] as const
const ENVELOPE_TOOL_KEYS = ['opencode.tool.name'] as const

/** The envelope prompt text, bucketed as conversation history when present. */
export const OPENCODE_PROMPT_TEXT_KEY = 'opencode.prompt.text'

/**
 * Envelope v2 context keys (plan 2026-10-23 §3.3, D3). The T0-pinned
 * `SessionContext` (fixtures/context-payload.json: `system`, `tools`, plus
 * the skills listing) maps onto these attributes; see mapper (T4) and the
 * adapter bucketing (T5).
 */
export const OPENCODE_CONTEXT_SYSTEM_KEY = 'opencode.context.system'
export const OPENCODE_CONTEXT_TOOLS_KEY = 'opencode.context.tools'
export const OPENCODE_CONTEXT_SKILLS_KEY = 'opencode.context.skills'

const ENVELOPE_CONTEXT_KEYS = [
  OPENCODE_CONTEXT_SYSTEM_KEY,
  OPENCODE_CONTEXT_TOOLS_KEY,
  OPENCODE_CONTEXT_SKILLS_KEY,
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
export function opencodeEnvelopeVersion(span: RawSpan): string | undefined {
  const version = span.attributes[OPENCODE_CONTRACT_VERSION_KEY]
  return typeof version === 'string' && version !== '' ? version : undefined
}

/** True when the span is the envelope and names the supported contract. */
export function isSupportedOpencodeEnvelope(span: RawSpan): boolean {
  return isOpencodeEnvelopeSpan(span) && opencodeEnvelopeVersion(span) === OPENCODE_CONTRACT_VERSION
}

function hasNonEmptyString(attributes: Record<string, unknown>, keys: readonly string[]): boolean {
  return keys.some((key) => {
    const value = attributes[key]
    return typeof value === 'string' && value !== ''
  })
}

/** True when the envelope span carries a tool call. Wins over the model markers. */
export function isOpencodeEnvelopeTool(attributes: Record<string, unknown>): boolean {
  return hasNonEmptyString(attributes, ENVELOPE_TOOL_KEYS)
}

/** True when the envelope span carries context content (D3 marker prefix). */
export function isOpencodeEnvelopeContext(attributes: Record<string, unknown>): boolean {
  return hasNonEmptyString(attributes, ENVELOPE_CONTEXT_KEYS)
}

/**
 * True when the envelope span carries a model call (D4). Model markers are
 * model-call evidence, and so is a context span on its own: a context event
 * is pre-LLM-call request assembly. Counters are never fabricated here —
 * tokens stay unset until a vocabulary with real counters is read.
 */
export function isOpencodeEnvelopeModel(attributes: Record<string, unknown>): boolean {
  return hasNonEmptyString(attributes, ENVELOPE_MODEL_KEYS) || isOpencodeEnvelopeContext(attributes)
}

/** The envelope session id, or undefined. Never synthesized. */
export function opencodeEnvelopeSessionId(attributes: Record<string, unknown>): string | undefined {
  const value = attributes[OPENCODE_SESSION_ID_KEY]
  return typeof value === 'string' && value !== '' ? value : undefined
}

/** The envelope prompt text, or undefined when absent or empty. */
export function opencodeEnvelopePromptText(attributes: Record<string, unknown>): string | undefined {
  const value = attributes[OPENCODE_PROMPT_TEXT_KEY]
  return typeof value === 'string' && value !== '' ? value : undefined
}

function contextText(attributes: Record<string, unknown>, key: string): string | undefined {
  const value = attributes[key]
  // The mapper (T4) stamps strings; a non-string value is never synthesized
  // into text here — T5 bucketing reads only what the span carries.
  return typeof value === 'string' && value !== '' ? value : undefined
}

/** The envelope v2 assembled system prompt, or undefined. Never synthesized. */
export function opencodeEnvelopeContextSystem(attributes: Record<string, unknown>): string | undefined {
  return contextText(attributes, OPENCODE_CONTEXT_SYSTEM_KEY)
}

/** The envelope v2 JSON-serialized tool definitions, or undefined. Never synthesized. */
export function opencodeEnvelopeContextTools(attributes: Record<string, unknown>): string | undefined {
  return contextText(attributes, OPENCODE_CONTEXT_TOOLS_KEY)
}

/** The envelope v2 JSON skills listing, or undefined. Never synthesized. */
export function opencodeEnvelopeContextSkills(attributes: Record<string, unknown>): string | undefined {
  return contextText(attributes, OPENCODE_CONTEXT_SKILLS_KEY)
}
