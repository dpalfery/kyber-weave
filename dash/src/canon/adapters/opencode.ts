// OpenCode harness adapter on plugin wire contract 3 (decision D9).
//
// The adapter claims one thing: spans the KyberDash OpenCode plugin emitted under
// contract "3" (`opencode.contract_version`). An envelope span naming any other
// version, or none, never reaches this adapter: `detect` scores it 0 and the ingest
// seam quarantines it under the unknown-version reason. OpenCode's own native OTel
// (the AI SDK, OpenInference and legacy `ai.*` vocabularies) is no longer mapped
// (D6): those spans score 0 here and fall to the unclaimed quarantine. Attribution
// never reads `service.name` (R6.2) — that value arrives as `RawSpan.source` and is
// not consulted.
//
// One OpenCode session is one trace of three span kinds, named by
// `opencode.span.kind`:
//   - `request` is the parentless `agent.invoke` root, timed by the run;
//   - `step` is one model call, an `llm.invoke` turn, parented to the request. It
//     carries the usage counters and the harness-reported cost;
//   - `tool` is a `tool.invoke` named after `opencode.tool.name`, parented to the
//     step that made it, with its real duration.
//
// Tokens follow the cache-EXCLUSIVE convention (pi's shape, not Copilot's):
// `opencode.usage.input` is fresh input alone and the cache classes are its
// disjoint siblings, so `reportedInput` is their sum (D13). Reasoning is a subset of
// output, and OpenCode reports it beside output rather than inside it, so output is
// output plus reasoning.
//
// Content (system prompt, tool definitions, user prompt) goes to `parts` and never
// to `record.raw` (D11). The skills listing needs no raw key of its own: the system
// prompt part carries the `<available_skills>` block the skills reader derives it
// from, so a consent-off record, which has no content at all, claims no listing.

import type { HarnessAdapter, RawSpan } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import {
  baseRecord,
  exclusiveConvention,
  readOptionalCounter,
  validateRecordTokens,
} from './copilot.js'
import { isSupportedOpencodeEnvelope } from './opencode-envelope.js'
import { OPENCODE_KEYS, type SpanKind } from '../../capture/harnesses/opencode-plugin/contract.js'
import {
  contentFromParts,
  notMeasurable,
  type CanonicalRecord,
  type ContentPart,
  type CostBlock,
  type Measurability,
  type MetricAvailability,
  USER_PROMPT_METRIC,
} from '../types.js'

/** The harness identity stamped on records this adapter wins. */
const OPENCODE_HARNESS = 'opencode'

/** OpenCode prices in US dollars; the plugin reports `opencode.cost` in that currency. */
const REPORTED_COST_CURRENCY = 'USD'

/** The prefix of every context content key (`system`, `tools`, `skills`). */
const CONTEXT_KEY_PREFIX = 'opencode.context.'

/**
 * Keys downstream already reads for the model (`sessions.ts` `MODEL_KEYS`,
 * `context-window.ts` `MODEL_IDENTITY_KEYS`, `store.ts`
 * `PROJECTED_ATTRIBUTE_KEYS`). Bridging writes only here so no downstream
 * reader needs a new key.
 */
const CANONICAL_MODEL_KEYS = ['gen_ai.response.model', 'gen_ai.request.model', 'model'] as const
const BRIDGED_MODEL_KEY = 'gen_ai.request.model'

/** The span kind the plugin stamped, or undefined for a missing or unknown value. */
function spanKindOf(attributes: Record<string, unknown>): SpanKind | undefined {
  const kind = attributes[OPENCODE_KEYS.spanKind]
  return kind === 'request' || kind === 'step' || kind === 'tool' ? kind : undefined
}

/** The canonical operation of each span kind. A new kind fails to compile until it is mapped. */
function opOf(kind: SpanKind): string {
  switch (kind) {
    case 'request':
      return 'agent.invoke'
    case 'step':
      return 'llm.invoke'
    case 'tool':
      return 'tool.invoke'
  }
}

/** A non-empty string attribute, or undefined. Nothing else is text: a number is never coerced. */
function nonEmptyString(attributes: Record<string, unknown>, key: string): string | undefined {
  const value = attributes[key]
  return typeof value === 'string' && value !== '' ? value : undefined
}

/** True for the content keys that go to parts and never stay in `record.raw` (D11). */
function isContentKey(key: string): boolean {
  return key === OPENCODE_KEYS.promptText || key.startsWith(CONTEXT_KEY_PREFIX)
}

/**
 * The span's attributes without its content. Everything else rides through to
 * `record.raw` as it arrived, so a consent-on and a consent-off record differ only
 * by the keys consent withholds.
 */
function metadataOf(attributes: Record<string, unknown>): Record<string, unknown> {
  return Object.fromEntries(Object.entries(attributes).filter(([key]) => !isContentKey(key)))
}

/**
 * The model id to bridge onto the record, or undefined when none is needed.
 * An already-canonical model wins by absence (nothing to bridge); otherwise the
 * plugin's `opencode.model.id`. Strings only: coercing a number turned `0` into the
 * model name `"0"`, which is the fabricated identity this bridge never invents.
 * Absent stays absent: no fallback, no empty string, no zero.
 */
function bridgedModelId(attributes: Record<string, unknown>): string | undefined {
  for (const key of CANONICAL_MODEL_KEYS) {
    if (nonEmptyString(attributes, key) !== undefined) return undefined
  }
  return nonEmptyString(attributes, OPENCODE_KEYS.modelId)
}

/**
 * The cost the harness reported. A reported `0` is a priced zero; no figure is
 * `unknown`/`no_rate`, never a published-rate guess (D4) — the cost engine does not
 * second-guess a figure OpenCode computed from its own provider rates.
 */
function reportedCost(attributes: Record<string, unknown>): CostBlock {
  const value = attributes[OPENCODE_KEYS.cost]
  if (typeof value === 'number' && Number.isFinite(value) && value >= 0) {
    return { basis: 'harness', status: 'priced', value, currency: REPORTED_COST_CURRENCY }
  }
  return { basis: 'unknown', status: 'no_rate' }
}

/**
 * A metric's availability. Presence has to come from the same parse the counter
 * itself uses: an unreadable value ('n/a', null, '') is not a measurement, and a
 * reported `0` still parses.
 */
function availability(measured: boolean, absentReason: string): MetricAvailability {
  return measured ? 'measured' : notMeasurable(absentReason)
}

/**
 * Counters of a step, through the cache-EXCLUSIVE convention (D13). Every class that
 * did not parse is declared `not_measurable` rather than left a measured zero, even
 * though the token identity still stores 0 for it.
 */
function applyStepUsage(record: CanonicalRecord, attributes: Record<string, unknown>): void {
  const input = readOptionalCounter(attributes, [OPENCODE_KEYS.usageInput])
  const cacheRead = readOptionalCounter(attributes, [OPENCODE_KEYS.usageCacheRead])
  const cacheCreation = readOptionalCounter(attributes, [OPENCODE_KEYS.usageCacheWrite])
  const output = readOptionalCounter(attributes, [OPENCODE_KEYS.usageOutput])
  const reasoning = readOptionalCounter(attributes, [OPENCODE_KEYS.usageReasoning])

  record.tokens = exclusiveConvention({
    input: input ?? 0,
    cacheRead: cacheRead ?? 0,
    cacheCreation: cacheCreation ?? 0,
    // Reasoning is a subset of output; OpenCode reports it beside output. It is only
    // added to a reported output, so an unreadable output is never topped up with it.
    output: output === undefined ? 0 : output + (reasoning ?? 0),
    ...(output !== undefined && reasoning !== undefined ? { reasoning } : {}),
  })

  // Cache classes are input: a step that reported only a cache counter still
  // measured the input it reads.
  const inputReported = input !== undefined || cacheRead !== undefined || cacheCreation !== undefined
  record.measurability = {
    ...record.measurability,
    token_usage: availability(inputReported, 'OpenCode telemetry did not report input token usage for this span.'),
    output: availability(output !== undefined, 'OpenCode telemetry did not report output token usage for this span.'),
    cache_read: availability(cacheRead !== undefined, 'OpenCode did not export a cache read counter.'),
    cache_creation: availability(
      cacheCreation !== undefined,
      'OpenCode did not export a cache creation counter.',
    ),
  }
}

/**
 * A request or tool span carries no model usage: the counters live on the step. The
 * token identity stores 0 for it, so the absence travels as its own declaration and
 * the session projection never reads that 0 as a measurement.
 */
function declareNoUsage(record: CanonicalRecord): void {
  const reason = 'OpenCode reports token usage on step spans, not on request or tool spans.'
  const declared: Measurability = {
    token_usage: notMeasurable(reason),
    output: notMeasurable(reason),
    cache_read: notMeasurable(reason),
    cache_creation: notMeasurable(reason),
  }
  record.measurability = { ...record.measurability, ...declared }
}

/** Appends a content part after every existing part, recomputing order and content. */
function appendPart(record: CanonicalRecord, part: Omit<ContentPart, 'order'>): void {
  const existing = record.parts ?? []
  const order = existing.reduce((max, entry) => Math.max(max, (entry.order ?? 0) + 1), 0)
  const parts: ContentPart[] = [...existing, { ...part, order }]
  record.parts = parts
  record.content = contentFromParts(parts)
}

/**
 * Why a content metric is not measurable on a span that carries none. The plugin sends content
 * only with the user's consent and puts no consent marker on the wire, so a span without it
 * cannot say whether capture was off or the content simply was not there; both are an absence,
 * stated as one.
 */
function withheldContent(metric: string): MetricAvailability {
  return notMeasurable(
    `OpenCode content capture is off, so this span did not capture ${metric}; turn OpenCode capture on in KyberDash Settings to record it.`,
  )
}

/**
 * File the content the span carries as parts (D11): the user prompt as conversation
 * history, the assembled system prompt, and the serialized tool definitions. Content
 * the plugin withheld (no consent) leaves the record without parts, never with
 * empty ones, and declares what it withheld `not_measurable` with a reason naming
 * content capture, so the session states the absence instead of printing a zero. The
 * declarations are per span kind: a request owns the prompt, a step owns the system
 * prompt, the tool catalogue and the skills listing.
 */
function applyContent(record: CanonicalRecord, attributes: Record<string, unknown>, kind: SpanKind | undefined): void {
  const prompt = nonEmptyString(attributes, OPENCODE_KEYS.promptText)
  if (prompt !== undefined) appendPart(record, { part: 'conversation_history', text: prompt })
  const system = nonEmptyString(attributes, OPENCODE_KEYS.contextSystem)
  if (system !== undefined) appendPart(record, { part: 'system_prompt', text: system })
  const tools = nonEmptyString(attributes, OPENCODE_KEYS.contextTools)
  if (tools !== undefined) appendPart(record, { part: 'tool_definitions', text: tools })

  if (kind === 'request') {
    record.measurability = {
      ...record.measurability,
      [USER_PROMPT_METRIC]: prompt !== undefined ? 'measured' : withheldContent('user_prompt'),
    }
  } else if (kind === 'step') {
    const skills = nonEmptyString(attributes, OPENCODE_KEYS.contextSkills)
    record.measurability = {
      ...record.measurability,
      system_prompt: system !== undefined ? 'measured' : withheldContent('system_prompt'),
      tool_definitions: tools !== undefined ? 'measured' : withheldContent('tool_definitions'),
      skills_listed: skills !== undefined || system !== undefined ? 'measured' : withheldContent('skills_listed'),
    }
  }
}

/** Map one supported contract-3 span onto the record `baseRecord` started. */
function applyEnvelope(record: CanonicalRecord, raw: RawSpan): void {
  const attributes = raw.attributes
  const kind = spanKindOf(attributes)

  const sessionId = nonEmptyString(attributes, OPENCODE_KEYS.sessionId)
  if (sessionId !== undefined) record.sessionId = sessionId

  // A span of unknown kind keeps `unspecified`, which the ingest seam quarantines
  // as non-model rather than guessing which of the three it is.
  if (kind !== undefined) record.op = opOf(kind)
  if (kind === 'tool') {
    // A tool call is named after its tool. The wire span name is the same for every
    // tool, so it tells the tools apart no better than the kind does.
    record.name = nonEmptyString(attributes, OPENCODE_KEYS.toolName) ?? record.name
  }

  if (kind === 'step') applyStepUsage(record, attributes)
  else declareNoUsage(record)
  record.cost = reportedCost(attributes)

  applyContent(record, attributes, kind)

  const bridged = bridgedModelId(attributes)
  record.raw = {
    ...metadataOf(attributes),
    ...(bridged !== undefined ? { [BRIDGED_MODEL_KEY]: bridged } : {}),
  }
}

export const opencodeAdapter: HarnessAdapter = {
  name: OPENCODE_HARNESS,
  namespaces: ['opencode'],

  /**
   * The plugin envelope is claimed only under the supported contract: an unknown or
   * missing version scores 0 so no vote can claim it. A supported envelope scores 1
   * whatever its kind, so a span of unknown kind keeps its group's confidence and
   * `normalize` leaves its operation unspecified for the non-model quarantine.
   * Nothing else scores: not a bare `opencode.session.id`, not shared `gen_ai.*`
   * keys, not any native OpenCode OTel vocabulary (D6).
   */
  detect(span) {
    return isSupportedOpencodeEnvelope(span) ? 1 : 0
  },

  /** Steps are the model calls per-request accounting reconciles over. */
  relevance(span) {
    return isSupportedOpencodeEnvelope(span) && spanKindOf(span.attributes) === 'step' ? 1 : 0
  },

  normalize(raw) {
    const record = baseRecord(this, raw)
    // `detect` scores any other version 0, so this runs for the supported contract;
    // a span handed in directly under another keeps `unspecified` and its payload.
    if (isSupportedOpencodeEnvelope(raw)) applyEnvelope(record, raw)
    return record
  },

  group: traceGroup,
  resolveRoot: resolveRootByParentage,
  validate: validateRecordTokens,

  /**
   * Every counter class is per-span: a reported counter is measured, an absent one is
   * `not_measurable` on the record. They are not a standing export gap, so none is
   * listed here.
   */
  unexportedMetrics() {
    return []
  },
}
