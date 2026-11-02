// The Factory (Droid) harness adapter.
//
// Factory is ONE harness under ONE canonical id: `droid`, the id the Factory
// file source already stores, so the dashboard shows Factory once and the
// plugin-sourced rows sit beside the file-sourced ones.
//
// Two claim paths reach this one adapter, and they are told apart by attribute
// fingerprint ALONE — never by the span's source name (R6.2):
//
//   1. The Kyber-owned Factory plugin envelope: spans whose attributes sit in
//      the `factory.*` namespace and which name the contract they were emitted
//      under in `factory.contract_version`. An envelope naming an unsupported
//      or missing version scores zero, and the ingest seam quarantines it
//      under the named unknown-version reason (droid-envelope.ts).
//   2. Factory's own native OpenTelemetry export, in its `genai` format: spans
//      carrying `gen_ai.agent.name` = `droid`. Any other agent name scores
//      zero, so another harness's native export is never claimed.
//
// The plugin envelope is checked FIRST. One session can be observed by both
// paths — the native export carries the Droid session id in
// `gen_ai.conversation.id`, the same id the plugin's session spans carry — so
// the two are joinable to one session and one turn, and counting both would
// count that turn twice. The precedence rule therefore lives at the ingest
// seam: a native span for a session the plugin already covers is kept as
// evidence without adding to that session's turn, tool and duration totals.
//
// Tokens: neither path reports a counter, so a record declares its token usage
// `not_measurable` with a reason naming the harness and the metric. The token
// identity keeps its zeros, and the declaration is what says they are not a
// measurement — a zero must never read as "nothing was spent".
//
// The plugin path's reason also names WHERE the counter would come from: the
// Factory session file's companion settings, supplied when this session's id
// matches a file-sourced session. The adapter cannot know at that point
// whether a file row will pair with this session — the pairing happens later,
// in the projection, on exact session-id equality — so the reason names the
// RULE, never the outcome, and never claims the counter was measured. The
// native export has no companion settings of its own to pair with, so its
// reasons stay clear of the file source.

import type { HarnessAdapter, RawSpan } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import {
  baseRecord,
  convertInclusiveCounts,
  readOptionalCounter,
  validateRecordTokens,
  CACHE_CREATION_KEYS,
  CACHE_READ_KEYS,
  INPUT_TOKEN_KEYS,
  OUTPUT_TOKEN_KEYS,
} from './copilot.js'
import { isDroidEnvelopeSpan, isSupportedDroidEnvelope } from './droid-envelope.js'
import {
  notMeasurable,
  type CanonicalRecord,
  type Measurability,
  type MetricAvailability,
} from '../types.js'

/** The harness identity stamped on records this adapter wins. */
export const DROID_HARNESS = 'droid'

/**
 * The quarantine reason a native Factory span files under when the plugin
 * envelope already covers its session. The span is not dropped: the quarantine
 * row keeps the decoded packet as the evidence that the session was seen
 * twice. A fixed string (not a formatted one) so the quarantine view and
 * per-harness wipes can match it exactly.
 */
export const DROID_NATIVE_SUPERSEDED_REASON =
  'droid native span superseded by the Factory plugin envelope'

/** The attribute Factory's native export names its agent identity under. */
const NATIVE_AGENT_KEY = 'gen_ai.agent.name'

/** The agent identity Factory's native export reports about itself. */
const NATIVE_AGENT_VALUE = 'droid'

/** The attribute the native export names its operation under. */
const NATIVE_OPERATION_KEY = 'gen_ai.operation.name'

/**
 * The attribute the native export names a failure under. `error.type` is the
 * OpenTelemetry convention for the type of the error a span records; Factory's
 * native export sets it to `rejected` on a policy-denied tool call.
 */
const NATIVE_ERROR_TYPE_KEY = 'error.type'

/** The native export's operations and the canonical operation each one is. */
const NATIVE_SPAN_OPS: Readonly<Record<string, string>> = {
  chat: 'llm.invoke',
  execute_tool: 'tool.invoke',
  invoke_agent: 'agent.invoke',
}

/**
 * The plugin envelope's span names and the canonical operation each one is.
 * The names are the wire contract's, so a plugin span of an unknown name
 * keeps `unspecified` and is quarantined as non-model rather than guessed at.
 */
const PLUGIN_SPAN_OPS: Readonly<Record<string, string>> = {
  'factory.session': 'agent.invoke',
  'factory.child_session': 'agent.invoke',
  'factory.turn': 'llm.invoke',
  'factory.tool': 'tool.invoke',
}

/** The plugin envelope's session id keys, in the spellings the wire uses. */
const PLUGIN_SESSION_ID_KEYS = [
  'factory.session.id',
  'factory.session_id',
  'factory.sessionId',
] as const

/** The plugin envelope's model keys, in the spellings the wire uses. */
const PLUGIN_MODEL_KEYS = ['factory.model.id', 'factory.model'] as const

/** The plugin envelope's tool name keys, in the spellings the wire uses. */
const PLUGIN_TOOL_NAME_KEYS = ['factory.tool.name', 'factory.tool_name'] as const

/** The native export's session id keys: its conversation id and its session id. */
const NATIVE_SESSION_ID_KEYS = ['gen_ai.conversation.id', 'session.id', 'gen_ai.session.id'] as const

/**
 * Keys downstream already reads for the model (`sessions.ts` `MODEL_KEYS`,
 * `store.ts` `PROJECTED_ATTRIBUTE_KEYS`). The plugin's model is bridged onto
 * one of them so no downstream reader needs a new key.
 */
const CANONICAL_MODEL_KEYS = ['gen_ai.response.model', 'gen_ai.request.model', 'model'] as const

/** The key the plugin envelope's model is bridged onto. */
const BRIDGED_MODEL_KEY = 'gen_ai.request.model'

/** The key the plugin envelope's tool name is bridged onto. */
const BRIDGED_TOOL_NAME_KEY = 'gen_ai.tool.name'

/** A non-empty string attribute, or undefined. Nothing else is text: a number is never coerced. */
function nonEmptyString(attributes: Record<string, unknown>, key: string): string | undefined {
  const value = attributes[key]
  return typeof value === 'string' && value !== '' ? value : undefined
}

/** The first non-empty string across a key family, or undefined when none is. */
function firstNonEmptyString(
  attributes: Record<string, unknown>,
  keys: readonly string[],
): string | undefined {
  for (const key of keys) {
    const value = nonEmptyString(attributes, key)
    if (value !== undefined) return value
  }
  return undefined
}

/**
 * The model id to bridge onto the record, or undefined when none is needed.
 * An already-canonical model wins by absence (nothing to bridge); otherwise
 * the plugin envelope's own model key. Strings only: coercing a number turned
 * `0` into the model name `"0"`, which is the fabricated identity this bridge
 * never invents. Absent stays absent: no fallback, no empty string, no zero.
 */
function bridgedModelId(attributes: Record<string, unknown>): string | undefined {
  for (const key of CANONICAL_MODEL_KEYS) {
    if (nonEmptyString(attributes, key) !== undefined) return undefined
  }
  return firstNonEmptyString(attributes, PLUGIN_MODEL_KEYS)
}

/** True when the span is the Kyber-owned plugin envelope. */
export function isDroidPluginSpan(span: RawSpan): boolean {
  return isDroidEnvelopeSpan(span)
}

/** True when the span is Factory's own native export, by its agent identity. */
export function isDroidNativeSpan(span: RawSpan): boolean {
  return span.attributes[NATIVE_AGENT_KEY] === NATIVE_AGENT_VALUE
}

/**
 * The session id a droid span belongs to, or undefined when it names none.
 * The plugin envelope's own keys come first; the native export's conversation
 * id and session id follow. Never synthesized: a span that names no session
 * names none.
 */
export function droidSessionIdOf(span: RawSpan): string | undefined {
  return (
    firstNonEmptyString(span.attributes, PLUGIN_SESSION_ID_KEYS) ??
    firstNonEmptyString(span.attributes, NATIVE_SESSION_ID_KEYS)
  )
}

/**
 * True when a stored record came from the plugin envelope rather than from the
 * native export. The plugin's span names are the wire contract's, so the name
 * answers it — and answers it without decompressing the record's raw payload,
 * which is what makes the check affordable across a session's whole record
 * set.
 */
export function isDroidPluginRecord(record: CanonicalRecord): boolean {
  return record.name.startsWith('factory.')
}

/**
 * True when a stored record came from Factory's native export rather than from
 * the plugin envelope. The native export names its agent identity, and only its
 * own spans carry it — the file source does not stamp it.
 */
export function isDroidNativeRecord(record: CanonicalRecord): boolean {
  return droidRawAttributesOf(record)[NATIVE_AGENT_KEY] === NATIVE_AGENT_VALUE
}

/** The record's raw attributes, or an empty map when it carries none. */
export function droidRawAttributesOf(record: CanonicalRecord): Record<string, unknown> {
  const raw = record.raw
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return {}
  return raw as Record<string, unknown>
}

/**
 * A metric's availability. Presence has to come from the same parse the
 * counter itself uses: an unreadable value ('n/a', null, '') is not a
 * measurement, and a reported `0` still parses.
 */
function availability(measured: boolean, absentReason: string): MetricAvailability {
  return measured ? 'measured' : notMeasurable(absentReason)
}

/** The counter classes a record declares its token usage under. */
type CounterClass = 'token_usage' | 'output' | 'cache_read' | 'cache_creation'

/** The reason each counter class declares when it cannot be measured. */
type CounterReasons = Readonly<Record<CounterClass, string>>

/**
 * The plugin path's reasons.
 *
 * The plugin's spans carry no token counters, so every class is absent — but
 * the numbers exist elsewhere: the Factory session file's companion settings
 * hold the session's token totals, and the projection pairs them with this
 * session on exact session-id equality. The reason therefore states where the
 * counter WOULD come from, never that it is there, so a reader can tell
 * "never measured" from "not joined yet".
 */
const PLUGIN_COUNTER_REASONS: CounterReasons = {
  token_usage:
    "Droid telemetry did not report token usage for this span; the Factory session file's companion settings supply it when this session's id matches a file-sourced session.",
  output:
    "Droid telemetry did not report output token usage for this span; the Factory session file's companion settings supply it when this session's id matches a file-sourced session.",
  cache_read:
    "Droid did not export a cache read counter; the Factory session file's companion settings supply it when this session's id matches a file-sourced session.",
  cache_creation:
    "Droid did not export a cache creation counter; the Factory session file's companion settings supply it when this session's id matches a file-sourced session.",
}

/**
 * The native export's reasons.
 *
 * A native Factory span has no companion settings file of its own to pair
 * with, so its reasons name the harness and the metric alone. Naming the file
 * source here would point at a pairing that can never happen.
 */
const NATIVE_COUNTER_REASONS: CounterReasons = {
  token_usage: 'Droid telemetry did not report token usage for this span.',
  output: 'Droid telemetry did not report output token usage for this span.',
  cache_read: 'Droid did not export a cache read counter.',
  cache_creation: 'Droid did not export a cache creation counter.',
}

/**
 * The token counters the span reported, and the declaration of each class.
 *
 * Neither claim path reports a counter today, so the common case is a record
 * whose token identity keeps its zeros and whose every class is declared
 * `not_measurable` with the reason the claim path gives. A counter that IS
 * reported is measured, and only that class is.
 *
 * The reasons are the caller's, because the two claim paths say different
 * things: only the plugin path's counters are supplied by the file source's
 * companion settings, so only its reasons name that pairing.
 */
function applyUsage(
  record: CanonicalRecord,
  attributes: Record<string, unknown>,
  reasons: CounterReasons,
): void {
  const input = readOptionalCounter(attributes, INPUT_TOKEN_KEYS)
  const output = readOptionalCounter(attributes, OUTPUT_TOKEN_KEYS)
  const cacheRead = readOptionalCounter(attributes, CACHE_READ_KEYS)
  const cacheCreation = readOptionalCounter(attributes, CACHE_CREATION_KEYS)

  if (input === undefined && output === undefined && cacheRead === undefined && cacheCreation === undefined) {
    const declared: Measurability = {
      token_usage: notMeasurable(reasons.token_usage),
      output: notMeasurable(reasons.output),
      cache_read: notMeasurable(reasons.cache_read),
      cache_creation: notMeasurable(reasons.cache_creation),
    }
    record.measurability = { ...record.measurability, ...declared }
    return
  }

  record.tokens = convertInclusiveCounts({
    input: input ?? 0,
    cacheRead: cacheRead ?? 0,
    cacheCreation: cacheCreation ?? 0,
    output: output ?? 0,
    inputPresent: input !== undefined,
  })
  record.measurability = {
    ...record.measurability,
    token_usage: availability(input !== undefined, reasons.token_usage),
    output: availability(output !== undefined, reasons.output),
    cache_read: availability(cacheRead !== undefined, reasons.cache_read),
    cache_creation: availability(cacheCreation !== undefined, reasons.cache_creation),
  }
}

/**
 * Map the plugin envelope onto the record `baseRecord` started.
 *
 * The envelope names its identity under the `factory.*` namespace, so the
 * session id, the model and the tool name are read from there and bridged
 * onto the keys the projection already reads. The model is never defaulted: a
 * span that names none carries none.
 */
function applyPluginEnvelope(record: CanonicalRecord, raw: RawSpan): void {
  const attributes = raw.attributes

  const sessionId = firstNonEmptyString(attributes, PLUGIN_SESSION_ID_KEYS)
  if (sessionId !== undefined) record.sessionId = sessionId

  // A span of an unknown kind keeps `unspecified`, which the ingest seam
  // quarantines as non-model rather than guessing which kind it is.
  const op = PLUGIN_SPAN_OPS[raw.name]
  if (op !== undefined) record.op = op

  const toolName = firstNonEmptyString(attributes, PLUGIN_TOOL_NAME_KEYS)
  const bridged = bridgedModelId(attributes)
  record.raw = {
    ...attributes,
    ...(bridged !== undefined ? { [BRIDGED_MODEL_KEY]: bridged } : {}),
    ...(toolName !== undefined ? { [BRIDGED_TOOL_NAME_KEY]: toolName } : {}),
  }
}

/**
 * Map Factory's native export onto the record `baseRecord` started.
 *
 * The operation attribute names the canonical operation; the content
 * attributes (`gen_ai.input.messages` and the rest) are gated by a
 * content-consent flag elsewhere, so the adapter never requires them.
 */
function applyNativeExport(record: CanonicalRecord, raw: RawSpan): void {
  const attributes = raw.attributes

  const sessionId = firstNonEmptyString(attributes, NATIVE_SESSION_ID_KEYS)
  if (sessionId !== undefined) record.sessionId = sessionId

  const operation = nonEmptyString(attributes, NATIVE_OPERATION_KEY)
  const op = operation !== undefined ? NATIVE_SPAN_OPS[operation] : undefined
  if (op !== undefined) record.op = op

  // A span that names an error type failed: the canonical status has to say
  // so, or a policy-denied tool call reads as an ordinary one. The type
  // itself stays in the record's raw attributes — it is the evidence of why
  // the call was denied, and the status only says that it was.
  if (nonEmptyString(attributes, NATIVE_ERROR_TYPE_KEY) !== undefined) {
    record.status = 'error'
  }
}

export const droidAdapter: HarnessAdapter = {
  name: DROID_HARNESS,
  namespaces: ['factory', 'gen_ai'],

  /**
   * The plugin envelope is checked FIRST, and only under the supported
   * contract: an envelope naming any other version, or none, scores 0 so no
   * vote can claim it, and the ingest seam quarantines it under the named
   * unknown-version reason.
   *
   * The native export is claimed by its agent identity alone: `droid` is
   * Factory's own name for itself, and any other agent name scores 0 — the
   * shared `gen_ai.*` keys every exporter emits are not an identity. The
   * span's source name is never read (R6.2).
   */
  detect(span) {
    if (isDroidEnvelopeSpan(span)) return isSupportedDroidEnvelope(span) ? 1 : 0
    if (isDroidNativeSpan(span)) return 1
    return 0
  },

  /**
   * The model's own calls are the turns the session's accounting reconciles
   * over; a tool call ranks below them, and a session root above neither.
   */
  relevance(span) {
    if (isDroidPluginSpan(span)) {
      const op = PLUGIN_SPAN_OPS[span.name]
      if (op === 'llm.invoke') return 1
      if (op === 'tool.invoke') return 0.5
      return 0
    }
    if (isDroidNativeSpan(span)) {
      const operation = nonEmptyString(span.attributes, NATIVE_OPERATION_KEY)
      const op = operation !== undefined ? NATIVE_SPAN_OPS[operation] : undefined
      if (op === 'llm.invoke') return 1
      if (op === 'tool.invoke') return 0.5
      return 0
    }
    return 0
  },

  normalize(raw) {
    const record = baseRecord(this, raw)
    const plugin = isDroidPluginSpan(raw)
    if (plugin) applyPluginEnvelope(record, raw)
    else if (isDroidNativeSpan(raw)) applyNativeExport(record, raw)
    applyUsage(record, raw.attributes, plugin ? PLUGIN_COUNTER_REASONS : NATIVE_COUNTER_REASONS)
    return record
  },

  group: traceGroup,
  resolveRoot: resolveRootByParentage,
  validate: validateRecordTokens,

  /**
   * Every counter class is per-span: a reported counter is measured, an absent
   * one is `not_measurable` on the record. They are not a standing export
   * gap, so none is listed here.
   */
  unexportedMetrics() {
    return []
  },
}
