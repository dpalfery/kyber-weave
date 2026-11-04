// The Factory (Droid) harness adapter.
//
// Factory is ONE harness under ONE canonical id: `droid`, the id the Factory
// file source already stores, so the dashboard shows Factory once and the
// plugin-sourced rows sit beside the file-sourced ones.
//
// ONE claim path reaches this adapter: the Kyber-owned Factory plugin
// envelope — spans whose attributes sit in the `factory.*` namespace and
// which name the contract they were emitted under in
// `factory.contract_version`. An envelope naming an unsupported or missing
// version scores zero, and the ingest seam quarantines it under the named
// unknown-version reason (droid-envelope.ts).
//
// Factory's OWN native OpenTelemetry export is NOT claimed (plan D50). Its
// `genai` format is recognised only by `gen_ai.agent.name` = `droid`, and the
// export puts that attribute on the ROOT span alone: a trace of one root and
// `k` children scores 1/(1+k) at the registry's group vote, against a 0.6
// threshold, so no real turn clears it. The `legacy` format carries no
// attribute fingerprint at all — only span names, which no adapter may claim.
// A native span is therefore quarantined as `unclaimed`, and the whole
// decoded packet is kept as the audit evidence.
//
// Tokens: the claimed spans report no counter, so a record declares its token
// usage `not_measurable` with a reason naming the harness and the metric. The
// token identity keeps its zeros, and the declaration is what says they are
// not a measurement — a zero must never read as "nothing was spent".
//
// The reason also names WHERE the counter would come from: the Factory
// session file's companion settings, supplied when this session's id matches a
// file-sourced session. The adapter cannot know at that point whether a file
// row will pair with this session — the pairing happens later, in the
// projection, on exact session-id equality — so the reason names the RULE,
// never the outcome, and never claims the counter was measured.

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
import { appendPart } from './content-parts.js'
import {
  notMeasurable,
  type CanonicalRecord,
  type Measurability,
  type MetricAvailability,
  USER_PROMPT_METRIC,
} from '../types.js'

/** The harness identity stamped on records this adapter wins. */
export const DROID_HARNESS = 'droid'

/**
 * The plugin envelope's span names and the canonical operation each one is.
 * The names are the wire contract's, so a plugin span of an unknown name
 * keeps `unspecified` and is quarantined as non-model rather than guessed at.
 */
const PLUGIN_SPAN_OPS: Readonly<Record<string, string>> = {
  'factory.session': 'agent.invoke',
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

/**
 * The plugin envelope's prompt text key.
 *
 * The prompt is content: the plugin sends it only under the owner's consent,
 * and the store's gate strips it from a claimed envelope while the consent is
 * off. So a span that carries the key is a span whose prompt was captured.
 */
const PLUGIN_PROMPT_TEXT_KEYS = ['factory.turn.prompt.text'] as const

/**
 * The plugin envelope's turn operation key.
 *
 * The plugin emits TWO turn spans for one turn: the prompt turn, which
 * carries the operation and the prompt text, and the completion turn, which
 * carries neither. The operation is what tells them apart.
 */
const PLUGIN_TURN_OPERATION_KEYS = ['factory.turn.operation'] as const

/**
 * The prompt text the wire carries at most, in bytes.
 *
 * The plugin cuts the prompt to this bound before it sends it. The value is
 * the wire's, repeated here for the same reason the envelope's strings are:
 * the canon adapter never depends on the plugin.
 */
const MAX_PROMPT_BYTES = 64 * 1024

/** The marker the wire appends to a prompt it cut. */
const PROMPT_TRUNCATION_MARKER = '\n[kyberdash: truncated]'

/**
 * The prompt text, cut to the wire's own bound.
 *
 * The plugin caps the prompt before it sends it, so a span that carries more
 * is not the plugin's. The prompt is the one piece of content the corpus
 * never purges, so it must not land unbounded: the text is cut at a
 * character boundary and marked, exactly as the wire marks it.
 */
function cappedPrompt(text: string): string {
  if (Buffer.byteLength(text, 'utf8') <= MAX_PROMPT_BYTES) return text
  const budget = MAX_PROMPT_BYTES - Buffer.byteLength(PROMPT_TRUNCATION_MARKER, 'utf8')
  const head = text.slice(0, budget)
  const bytes = Buffer.from(head, 'utf8')
  let end = Math.min(budget, bytes.length)
  // A cut that lands inside a character would leave a broken one behind.
  while (end > 0 && end < bytes.length && ((bytes[end] ?? 0) & 0xc0) === 0x80) end -= 1
  return `${bytes.toString('utf8', 0, end)}${PROMPT_TRUNCATION_MARKER}`
}

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
 * The reasons a record declares its counter classes under.
 *
 * The plugin's spans carry no token counters, so every class is absent — but
 * the numbers exist elsewhere: the Factory session file's companion settings
 * hold the session's token totals, and the projection pairs them with this
 * session on exact session-id equality. The reason therefore states where the
 * counter WOULD come from, never that it is there, so a reader can tell
 * "never measured" from "not joined yet".
 */
const COUNTER_REASONS: CounterReasons = {
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
 * The token counters the span reported, and the declaration of each class.
 *
 * The claimed spans report no counter today, so the common case is a record
 * whose token identity keeps its zeros and whose every class is declared
 * `not_measurable` with the reason above. A counter that IS reported is
 * measured, and only that class is.
 */
function applyUsage(record: CanonicalRecord, attributes: Record<string, unknown>): void {
  const input = readOptionalCounter(attributes, INPUT_TOKEN_KEYS)
  const output = readOptionalCounter(attributes, OUTPUT_TOKEN_KEYS)
  const cacheRead = readOptionalCounter(attributes, CACHE_READ_KEYS)
  const cacheCreation = readOptionalCounter(attributes, CACHE_CREATION_KEYS)

  if (input === undefined && output === undefined && cacheRead === undefined && cacheCreation === undefined) {
    const declared: Measurability = {
      token_usage: notMeasurable(COUNTER_REASONS.token_usage),
      output: notMeasurable(COUNTER_REASONS.output),
      cache_read: notMeasurable(COUNTER_REASONS.cache_read),
      cache_creation: notMeasurable(COUNTER_REASONS.cache_creation),
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
    token_usage: availability(input !== undefined, COUNTER_REASONS.token_usage),
    output: availability(output !== undefined, COUNTER_REASONS.output),
    cache_read: availability(cacheRead !== undefined, COUNTER_REASONS.cache_read),
    cache_creation: availability(cacheCreation !== undefined, COUNTER_REASONS.cache_creation),
  }
}

/**
 * File the prompt the span carries, and declare it when it is absent.
 *
 * The prompt is the owner's own text, and it reaches the record only when the
 * owner's consent let it travel. A turn that carries it files it as
 * conversation history, so the session view shows what was asked; a turn
 * without one declares the prompt `not_measurable` with a reason that names
 * the consent, so the absence is stated rather than shown as a blank.
 *
 * Only the PROMPT turn says anything about the prompt. The plugin's second
 * turn span for the same turn — the completion — carries no prompt by
 * construction, so a consent-off reason on it would name a consent that was
 * never asked for.
 */
function applyPrompt(record: CanonicalRecord, raw: RawSpan): void {
  if (raw.name !== 'factory.turn') return
  const prompt = firstNonEmptyString(raw.attributes, PLUGIN_PROMPT_TEXT_KEYS)
  const operation = firstNonEmptyString(raw.attributes, PLUGIN_TURN_OPERATION_KEYS)
  // A span that carries neither the operation nor the prompt is the
  // completion turn: it says nothing about a prompt.
  if (prompt === undefined && operation === undefined) return
  if (prompt !== undefined) {
    appendPart(record, {
      part: 'conversation_history',
      text: cappedPrompt(prompt),
      role: 'user',
    })
  }
  record.measurability = {
    ...record.measurability,
    [USER_PROMPT_METRIC]:
      prompt !== undefined
        ? 'measured'
        : notMeasurable(
            "Factory's content consent is off, so this turn did not capture the prompt text; turn the Factory capture on in KyberDash Settings to record it.",
          ),
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

export const droidAdapter: HarnessAdapter = {
  name: DROID_HARNESS,
  namespaces: ['factory'],

  /**
   * The plugin envelope is the only thing this adapter claims, and only under
   * the supported contract: an envelope naming any other version, or none,
   * scores 0 so no vote can claim it, and the ingest seam quarantines it
   * under the named unknown-version reason.
   *
   * Factory's own native export scores 0 whatever it carries. It was
   * recognised by `gen_ai.agent.name` = `droid` alone, which the export puts
   * on the root span of a trace and nowhere else — so the group's score was
   * the winner's evidence over the group's span count, `1/(1+k)` for a real
   * trace, and never cleared the registry's threshold. The span's source name
   * is never read (R6.2), and neither is its name.
   */
  detect(span) {
    if (isDroidEnvelopeSpan(span)) return isSupportedDroidEnvelope(span) ? 1 : 0
    return 0
  },

  /**
   * The model's own calls are the turns the session's accounting reconciles
   * over; a tool call ranks below them, and a session root above neither.
   */
  relevance(span) {
    if (!isDroidPluginSpan(span)) return 0
    const op = PLUGIN_SPAN_OPS[span.name]
    if (op === 'llm.invoke') return 1
    if (op === 'tool.invoke') return 0.5
    return 0
  },

  normalize(raw) {
    const record = baseRecord(this, raw)
    if (isDroidPluginSpan(raw)) {
      applyPluginEnvelope(record, raw)
      applyPrompt(record, raw)
    }
    applyUsage(record, raw.attributes)
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
