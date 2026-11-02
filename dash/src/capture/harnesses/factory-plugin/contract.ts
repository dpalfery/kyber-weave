// Wire contract v1 for the Factory (Droid) capture plugin: the `factory.*` attribute namespace, the
// four span names, and the deterministic id derivation the hook mapper uses.
//
// This module is a LEAF: it imports only `node:` built-ins, so the plugin bundle never drags
// engine code in and the canon adapter never depends on the plugin.
//
// The namespace is `factory.*` and deliberately NOT the OpenCode `opencode.*` keys: the OpenCode
// adapter claims every span that carries those keys, so an OpenCode key here would mis-attribute
// every Factory row to OpenCode.

import { createHash } from 'node:crypto'

/** The wire contract version the plugin stamps on every span and the adapter checks. */
export const FACTORY_CONTRACT_VERSION = '1'

/**
 * Span attribute keys. Every key is in the `factory.*` namespace, and every one of them names a
 * value the hook payload can carry or a value the contract itself defines.
 */
export const FACTORY_KEYS = {
  contractVersion: 'factory.contract_version',
  sessionId: 'factory.session.id',
  parentSessionId: 'factory.parent.session.id',
  cwd: 'factory.cwd',
  messageId: 'factory.message.id',
  turnOperation: 'factory.turn.operation',
  promptText: 'factory.turn.prompt.text',
  modelId: 'factory.model.id',
  toolName: 'factory.tool.name',
  toolCallId: 'factory.tool.call_id',
  toolStatus: 'factory.tool.status',
  toolErrorType: 'factory.tool.error_type',
  toolInputBytes: 'factory.tool.input_bytes',
  toolResultBytes: 'factory.tool.result_bytes',
} as const

/** The four span kinds of the Factory contract. */
export type FactorySpanKind = 'session' | 'turn' | 'tool' | 'child_session'

/**
 * The roles a turn's spans play: the prompt the user submitted and the completion the turn
 * produced. Both are `turn` spans of one turn, so they share that turn's trace id; they are two
 * spans of one turn, so the role is what tells their span ids apart.
 */
export type FactoryTurnRole = 'prompt' | 'completion'

/**
 * Span names, one per kind. The key and the kind are the same word, so `SPAN_NAMES[kind]` is the
 * name of that kind: `child_session`, not `childSession`.
 */
export const SPAN_NAMES = {
  session: 'factory.session',
  turn: 'factory.turn',
  tool: 'factory.tool',
  child_session: 'factory.child_session',
} as const satisfies Record<FactorySpanKind, string>

function sha256Hex(text: string): string {
  return createHash('sha256').update(text).digest('hex')
}

/**
 * One field of a derivation string. `|` separates fields and `\` escapes it, so no two distinct
 * field lists join to the same string: a session id and an anchor id that both contain the
 * separator cannot collide with a session id and an anchor id that merely split at it.
 */
function field(value: string): string {
  return value.replace(/[\\|]/g, '\\$&')
}

/**
 * The trace id of one unit of work: the first 32 hex of SHA-256 over
 * `factory|trace|<session>|<anchor>`, each field escaped (see `field`).
 *
 * The anchor is part of the derivation, so two anchors in one session are two traces: a turn and a
 * tool call do not share one trace, and the units of one session are not forced into one.
 */
export function traceIdFor(sessionId: string, anchorId: string): string {
  return sha256Hex(`factory|trace|${field(sessionId)}|${field(anchorId)}`).slice(0, 32)
}

/**
 * The span id of a kind and anchor within a session: the first 16 hex of SHA-256 over
 * `factory|<kind>|<session>|<anchor>`, each field escaped (see `field`).
 *
 * The anchor is the id of the unit of work the span covers: a tool call's call id for a tool span,
 * a message id for a turn span, the session id for a session span. The two hooks of one unit (the
 * `PreToolUse` and the `PostToolUse` of one call) therefore share a span id, and two units never
 * do.
 *
 * A role qualifies a span inside its kind. The prompt span and the completion span of one turn
 * are both turn spans of one turn, so they share the anchor and the trace id, and the role is
 * what tells their span ids apart: one shared span id would let the completion land on the
 * prompt's row and take the prompt text and the model with it. A span with no role derives over
 * the fields above, so the kinds that have no roles keep the ids they had.
 */
export function spanIdFor(
  kind: FactorySpanKind,
  sessionId: string,
  anchorId: string,
  role: FactoryTurnRole | '' = '',
): string {
  const parts = role === '' ? [kind, sessionId, anchorId] : [kind, role, sessionId, anchorId]
  return sha256Hex(`factory|${parts.map((part) => field(part)).join('|')}`).slice(0, 16)
}
