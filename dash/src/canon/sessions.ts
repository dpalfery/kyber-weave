// Derived sessions (spec: docs/specs/kyberdash; ADR 0006 D3). One row per
// conversation, built by running the analysis layer over the canonical
// records and caching the result the dashboard reads.
//
// This is the wire that was missing. `analyzeContext`, `rankSchemas` and
// `buildTimeline` were written, tested and then called by nothing: the web
// dashboard read a precomputed payload out of the retired Python pipeline's
// SQLite instead, and `/api/kyber/context` fabricated the same shape from
// per-turn counters with `toolDefinitionsByServer: {}` hard-coded. Everything
// below is assembly of parts that already existed.
//
// The output shape is the payload the dashboard already consumes, so the
// frontend does not move when the source of truth does.

import { analyzeContext, reduceContextParts, type ContextPart, type ContextTurn } from '../analysis/context.js'
import { rankSchemas, type ToolDefinition } from '../analysis/schema.js'
import { auxiliarySpend, buildTimeline, subagentSessions } from '../analysis/timeline.js'
import { costBasisMismatchOf, measuredInput, sumCosts, type CostBasisMismatch } from './cost.js'
import { isCopilotHarness, priceCopilotTurn } from './copilot-rates.js'
import { isPublishedTableHarness, pricePublishedTurn } from './published-pricing.js'
import { contextLimitOf, DEFAULT_CONTEXT_LIMIT } from './context-window.js'
import { catalogWindowForRecords } from './model-window-catalog.js'
import { groupByCanonicalHarness, harnessExportsCacheCounter, normalizeHarnessName, surveyFamily } from './measurability.js'
import { dedupeTwinTurns } from './twin-dedupe.js'
import { formatSessionTitle } from './naming.js'
import { buildFindingsYielding } from './findings.js'
import { buildHarnessRollup } from './harnesses.js'
import { buildRuns } from './runs.js'
import { CanonStore, releaseRecordParts, type ProjectedSessionRecord, type SessionRow } from './store.js'
import { extractInvokedSkills, readListingOf, skillsForSession, type ListingResult, type SkillsView } from './skills.js'
import { activeTokenizer, createCachedCounter, loadO200kCounter } from './tokens.js'
import { notMeasurable, type CanonicalRecord, type ContentPart, type CostBlock, type Measurability, type MetricAvailability, type NotMeasurable } from './types.js'

// The default window and the reported-window rule moved to
// `./context-window.js` so the finding detector can read the same derivation
// without importing this module — `analysis/findings.js` must not import
// `canon/sessions.js` (cycle via `canon/findings.ts`). Re-exported here so
// existing importers keep resolving the default from the session analysis.
export { DEFAULT_CONTEXT_LIMIT } from './context-window.js'

/** Attributes naming the agent, repository and branch, for the session header. */
const AGENT_NAME_KEYS = ['gen_ai.agent.name', 'agent.name'] as const
const REPO_KEYS = ['vcs.repository.name', 'repo'] as const
const BRANCH_KEYS = ['vcs.ref.head.name', 'branch'] as const
const MODEL_KEYS = ['gen_ai.response.model', 'gen_ai.request.model', 'model'] as const

/** `invoke_agent`, optionally followed by a space or colon and a target; never `invoke_agent_foo`. */
const INVOKE_AGENT_NAME_RE = /^invoke_agent(?:[ :]|$)/

// ---------------------------------------------------------------------------
// The projection's payload discipline
//
// WHY: a record's payload is the harness's whole span attribute map, and the
// projection reads a handful of attributes out of it. On the measured corpus
// one session held 10,933 model calls whose payloads inflated and parsed to
// 1.5 GB, and the projection held every one of them at once: `toRecord` makes
// the payload a lazy property (the `defineLazy` pattern, for a 3.5 GB peak)
// and that property CACHES what it computed, so the first reader of a
// record's payload kept it alive for as long as the record was — and the
// record was as long as the session.
//
// The fix follows the two precedents already in this tree. `iterateSessions`
// became a generator so a caller can stream sessions instead of materializing
// them; the store's `recordsForSessionProjected` is the same move for a
// session's payloads: the records are still held (the helpers below need the
// whole session), but each record's payload is reduced to the attributes the
// projection reads and the payload itself is released before the next record
// is touched.
//
// WHAT survives the reduction: every key (the timeline's evidence scan reads
// the key names, and a key that names the evidence IS evidence) and every
// value the readers look up. WHAT does not: the harness's payload — the
// prompt, the conversation, the tool catalogue — under keys no reader asks
// for. The reduction is verified against the timeline's own reading, so a
// value that carried the evidence is kept rather than silently lost.
// ---------------------------------------------------------------------------

/** The timeline node a span occupies, wherever the tree put it. */
function timelineNodeFor(
  node: ReturnType<typeof buildTimeline>,
  spanId: string,
): ReturnType<typeof buildTimeline> | undefined {
  if (node.spanId === spanId) return node
  for (const child of node.children) {
    const found = timelineNodeFor(child, spanId)
    if (found !== undefined) return found
  }
  return undefined
}

/**
 * The timeline's evidence reading of one record: whether the record reads as
 * subagent evidence, and whether it reads as auxiliary evidence.
 *
 * The timeline decides this from the record's payload — every key and every
 * string value — and the answer decides which subtree the record lands in, so
 * the persisted timeline changes when the answer changes.
 */
function evidenceReadingOf(record: CanonicalRecord): { subagent: boolean; auxiliary: boolean } {
  const node = timelineNodeFor(buildTimeline([record]), record.spanId)
  return { subagent: node?.isSubagent === true, auxiliary: node?.isAuxiliary === true }
}

/**
 * Adopt the reduced attributes for one record, unless the reduction moved the
 * timeline's evidence reading.
 *
 * WHY the comparison: the timeline reads the evidence out of the payload, and
 * a record whose evidence the reduction dropped would move between the
 * conversation and the auxiliary group — a different persisted timeline. Such
 * a record keeps its whole payload instead; the reduction is a bound on the
 * working set, never a change to the answer.
 */
function adoptProjectedAttributes(projected: ProjectedSessionRecord): void {
  const { record, attributes } = projected
  const payload = record.raw
  const before = evidenceReadingOf(record)
  record.raw = attributes
  const after = evidenceReadingOf(record)
  if (before.subagent === after.subagent && before.auxiliary === after.auxiliary) return
  record.raw = payload
}


function isInvokeAgentSpan(record: CanonicalRecord): boolean {
  if (record.op === "agent.invoke") return true
  const opName = attributeOf(record, ["gen_ai.operation.name"])
  if (opName === "invoke_agent") return true
  if (INVOKE_AGENT_NAME_RE.test(record.name)) return true
  const toolName = attributeOf(record, ["gen_ai.tool.name", "tool.name", "tool_name"])
  if (toolName === "invoke_agent") return true
  return false
}

function collectDescendantRecords(
  rootSpanId: string,
  records: readonly CanonicalRecord[],
): CanonicalRecord[] {
  const childrenByParent = new Map<string, CanonicalRecord[]>()
  for (const record of records) {
    if (record.parentSpanId) {
      const list = childrenByParent.get(record.parentSpanId) ?? []
      list.push(record)
      childrenByParent.set(record.parentSpanId, list)
    }
  }

  const result: CanonicalRecord[] = []
  const queue = [rootSpanId]
  const seen = new Set<string>([rootSpanId])

  while (queue.length > 0) {
    const currentId = queue.shift()!
    const children = childrenByParent.get(currentId) ?? []
    for (const child of children) {
      if (!seen.has(child.spanId)) {
        seen.add(child.spanId)
        result.push(child)
        if (!isInvokeAgentSpan(child)) {
          queue.push(child.spanId)
        }
      }
    }
  }

  return result
}

function extractAgentName(invokeSpan: CanonicalRecord): string | null {
  const attr = attributeOf(invokeSpan, AGENT_NAME_KEYS)
  if (attr && attr.trim()) return attr.trim()
  if (invokeSpan.name.startsWith("invoke_agent ")) {
    const candidate = invokeSpan.name.slice("invoke_agent ".length).trim()
    if (candidate) return candidate
  }
  return null
}

function extractTaskSummary(
  subagentRecords: readonly CanonicalRecord[],
  views: ReadonlyMap<string, RecordPartsView>,
): string | undefined {
  for (const record of subagentRecords) {
    const toolName = attributeOf(record, ["gen_ai.tool.name", "tool.name", "tool_name"]) ?? record.name
    const nameLower = toolName.toLowerCase()
    if (nameLower === "task" || nameLower.endsWith(".task")) {
      const raw = record.raw as Record<string, unknown> | null
      const argsRaw =
        raw?.["arguments"] ??
        raw?.["gen_ai.tool.arguments"] ??
        raw?.["tool.arguments"] ??
        raw?.["tool_arguments"] ??
        raw?.["parameters"]
      let argsObj: Record<string, unknown> | null = null
      if (typeof argsRaw === "string") {
        try {
          argsObj = JSON.parse(argsRaw)
        } catch {
          if (argsRaw.trim()) return argsRaw.trim()
        }
      } else if (argsRaw !== null && typeof argsRaw === "object") {
        argsObj = argsRaw as Record<string, unknown>
      }

      if (argsObj !== null && typeof argsObj === "object") {
        const desc =
          argsObj["description"] ??
          argsObj["task"] ??
          argsObj["prompt"] ??
          argsObj["task_description"] ??
          argsObj["summary"]
        if (typeof desc === "string" && desc.trim()) {
          return desc.trim()
        }
      }
    }
  }

  for (const record of subagentRecords) {
    if (isInvokeAgentSpan(record)) {
      const raw = record.raw as Record<string, unknown> | null
      const argsRaw = raw?.["arguments"] ?? raw?.["gen_ai.agent.arguments"] ?? raw?.["task.description"]
      if (typeof argsRaw === "string") {
        try {
          const parsed = JSON.parse(argsRaw) as Record<string, unknown> | null
          const desc = parsed?.description ?? parsed?.task ?? parsed?.prompt
          if (typeof desc === "string" && desc.trim()) return desc.trim()
        } catch {
          if (argsRaw.trim()) return argsRaw.trim()
        }
      } else if (argsRaw !== null && typeof argsRaw === "object") {
        const desc =
          (argsRaw as Record<string, unknown>)["description"] ?? (argsRaw as Record<string, unknown>)["task"]
        if (typeof desc === "string" && desc.trim()) return desc.trim()
      }
    }
  }

  for (const record of subagentRecords) {
    const promptAttr = attributeOf(record, ["gen_ai.prompt", "prompt"])
    if (promptAttr && promptAttr.trim()) {
      return promptAttr.trim()
    }
    // The conversation history's own text, read on demand: the reduction
    // released it, and only a subagent's label needs it.
    const summary = viewOf(views, record).taskSummary()
    if (summary !== undefined) return summary
  }

  return undefined
}

function attributeOf(record: CanonicalRecord, keys: readonly string[]): string | undefined {
  const raw = record.raw
  if (raw === null || typeof raw !== 'object') return undefined
  const attributes = raw as Record<string, unknown>
  for (const key of keys) {
    const value = attributes[key]
    if (typeof value === 'string' && value !== '') return value
    if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  }
  return undefined
}

/** Epoch ms, or NaN when the timestamp is absent or unparseable. Absent is never the epoch. */
function parseTimestamp(ts: string | Date | unknown): number {
  if (ts instanceof Date) return ts.getTime()
  if (typeof ts === 'string') return Date.parse(ts)
  return Number.NaN
}

/** Ascending by time; records with no usable timestamp sort after every timed record. */
function compareTimestamps(a: unknown, b: unknown): number {
  const tA = parseTimestamp(a)
  const tB = parseTimestamp(b)
  const aOk = Number.isFinite(tA)
  const bOk = Number.isFinite(tB)
  if (aOk && bOk) return tA - tB
  if (aOk) return -1
  if (bOk) return 1
  return 0
}

/** First and last timestamps by time, ignoring records without a usable one. */
function timestampBounds(records: readonly CanonicalRecord[]): {
  started: string | Date
  ended: string | Date
} {
  let min: CanonicalRecord['timestamp'] | undefined
  let max: CanonicalRecord['timestamp'] | undefined
  for (const record of records) {
    const t = parseTimestamp(record.timestamp)
    if (!Number.isFinite(t)) continue
    if (min === undefined || t < parseTimestamp(min)) min = record.timestamp
    if (max === undefined || t >= parseTimestamp(max)) max = record.timestamp
  }
  return { started: min ?? records[0]!.timestamp, ended: max ?? records[records.length - 1]!.timestamp }
}

function isClaudeHarness(harness: string): boolean {
  const norm = normalizeHarnessName(harness)
  return norm === 'claude' || norm === 'claude-code' || surveyFamily(harness) === 'claude-code'
}

function extractClaudeAgentId(record: CanonicalRecord): string | undefined {
  const raw = record.raw as Record<string, unknown> | null
  if (raw && typeof raw === 'object') {
    const candidates = [
      raw['agent_id'],
      raw['agentId'],
      raw['subagent_id'],
      raw['subagentId'],
      raw['agent.id'],
      raw['gen_ai.agent.id'],
    ]
    for (const c of candidates) {
      if (typeof c === 'string' && c.trim()) return c.trim()
      if (typeof c === 'number') return String(c)
    }
  }
  if (record.spanId.startsWith('agent-')) {
    return record.spanId
  }
  return undefined
}

/**
 * A Claude subagent's session id: `<parent session>#agent-<agentId>`, scoped by the session it
 * ran under exactly as Copilot's `<parent>#<span>` is. An agent id is only unique within what
 * the harness recorded, so an unscoped `agent-<id>` would let two parents that carry the same
 * id upsert one row, last writer winning. A leading `agent-` on the id is not doubled.
 */
function claudeSubagentSessionId(parentSessionId: string, agentId: string): string {
  const trimmed = agentId.trim()
  const bare = trimmed.startsWith('agent-') ? trimmed.slice('agent-'.length) : trimmed
  return `${parentSessionId}#agent-${bare}`
}

function extractClaudeAgentName(records: readonly CanonicalRecord[]): string | null {
  for (const record of records) {
    const raw = record.raw as Record<string, unknown> | null
    if (!raw || typeof raw !== 'object') continue
    const candidates = [
      raw['agent_type'],
      raw['agentType'],
      raw['gen_ai.agent.name'],
      raw['gen_ai.agent.type'],
      raw['agent_name'],
      raw['agentName'],
      raw['agent.name'],
      raw['agent'],
    ]
    for (const c of candidates) {
      if (typeof c === 'string' && c.trim()) return c.trim()
    }
  }
  for (const record of records) {
    const extracted = extractAgentName(record)
    if (extracted) return extracted
  }
  return null
}

function extractClaudeDescription(
  records: readonly CanonicalRecord[],
  views: ReadonlyMap<string, RecordPartsView>,
): string | undefined {
  for (const record of records) {
    const raw = record.raw as Record<string, unknown> | null
    if (!raw || typeof raw !== 'object') continue
    const candidates = [
      raw['description'],
      raw['task_description'],
      raw['taskDescription'],
      raw['task'],
      raw['gen_ai.agent.description'],
      raw['summary'],
      raw['prompt'],
    ]
    for (const c of candidates) {
      if (typeof c === 'string' && c.trim()) return c.trim()
    }
  }
  return extractTaskSummary(records, views)
}


/**
 * A harness roll-up span: Copilot's `invoke_agent`, which carries the Σ of its
 * child `chat` calls. New ingests label it `agent.invoke`; records stored
 * before that still say `llm.invoke`, so the operation attribute is read too.
 */
function isRollup(record: CanonicalRecord): boolean {
  if (record.op === 'agent.invoke') return true
  return record.op === 'llm.invoke' && attributeOf(record, ['gen_ai.operation.name']) === 'invoke_agent'
}

/**
 * The session's turns: its model calls. A roll-up is not one while the calls it
 * summarises exist -- charting both double-counts every total and puts the
 * whole loop's input ahead of the real, growing calls. A session holding only
 * roll-ups (the child spans never arrived) keeps them as its turns rather than
 * emptying. Tool and structural spans are never turns.
 */
function turnsOf(records: readonly CanonicalRecord[]): CanonicalRecord[] {
  const calls = records.filter((record) => record.op === 'llm.invoke' && !isRollup(record))
  if (calls.length > 0) return calls
  return records.filter(
    (record) => isRollup(record) && (measuredInput(record.tokens) > 0 || record.tokens.output > 0),
  )
}

/**
 * The turn's parts, as the analysis consumes them. Records carrying
 * structured parts pass them through with their server attribution and
 * harness-reported counts intact; records carrying only the flat content map
 * are converted key by key, which loses nothing they had.
 */
function partsOf(
  record: CanonicalRecord,
  parts: readonly ContentPart[] | undefined,
): ContextPart[] {
  if (parts !== undefined && parts.length > 0) {
    return parts.map((part) => ({
      part: part.part,
      text: part.text,
      ...(part.tokens !== undefined ? { tokens: part.tokens } : {}),
      ...(part.server !== undefined ? { server: part.server } : {}),
    }))
  }
  return Object.entries(record.content)
    .filter((entry): entry is [ContextPart['part'], string] => typeof entry[1] === 'string')
    .map(([part, text]) => ({ part, text }))
}

/**
 * What a record's parts said, reduced to the values the derived views read.
 *
 * WHY: the parts carry the turn's text, and the readers need the VALUES — the
 * composition's bucket contributions, the tool catalogue, the skills listing —
 * not the text of every part. The text is the session's working set: on the
 * measured corpus ~10,933 turns x 64 KB of text, about 1.4 GB as UTF-16, and
 * it was held all at once because the record that owned a parts set lived as
 * long as the session.
 *
 * So each record's parts are read once, reduced to these values, and
 * RELEASED: the record keeps the counts, the store keeps the text, and only
 * one record's parts are in memory at a time. The release is a release, not a
 * destruction — a reader that genuinely needs the raw text (the twin
 * collapse's content transplant, a subagent's task summary) asks the record
 * for it again.
 */
export type RecordPartsView = {
  /** The record carries parts. */
  hasParts: boolean
  /** The parts as the composition consumes them: the text counted, then released. */
  contextParts: ContextPart[]
  /** The tool definitions the record offered, with their token cost. */
  toolDefinitions: Array<{ name: string; server?: string; tokens: number }>
  /** The tool names the record offered, in the order they were offered. */
  offeredToolNames: string[]
  /** The record offered tool definitions at all, named or not. */
  hasDefinitionParts: boolean
  /** The skills listing the record states. */
  listing: ListingResult
  /** The buckets the record carries non-empty parts for. */
  carriedBuckets: Set<string>
  /**
   * The task summary the record's conversation history carries.
   *
   * Read on demand: it is the conversation's own text, and only a subagent's
   * label needs it — deriving it for every turn would hold a second copy of
   * the text the reduction just released.
   */
  taskSummary: () => string | undefined
}

/**
 * The record's parts, reduced to the values the derived views read, and
 * released.
 *
 * The reduction happens where the text is in hand: the composition's counts
 * are made here, the tool catalogue is parsed here, and the skills listing is
 * read here. What leaves is a handful of numbers and names per record.
 */
function reduceRecordParts(
  record: CanonicalRecord,
  countTokens: (text: string) => number,
): RecordPartsView {
  const parts = record.parts
  const effective = partsOf(record, parts)
  const contextParts = reduceContextParts(effective, { countTokens })

  const carriedBuckets = new Set<string>()
  for (const part of effective) {
    if (part.text !== '') carriedBuckets.add(part.part)
  }

  // The tool definitions, from the parts that carry them. The catalogue ranks
  // the named ones; the offered names are what the session's summary lists.
  const definitions: Array<{ text: string; server?: string; catalogue: boolean }> = []
  if (parts !== undefined && parts.length > 0) {
    for (const part of parts) {
      if (part.part !== 'tool_definitions' && (part.part as string) !== 'tool_definition') continue
      definitions.push({
        text: part.text,
        catalogue: part.part === 'tool_definitions',
        ...(part.server !== undefined ? { server: part.server } : {}),
      })
    }
  } else {
    // A record without parts carries the definitions in its flat content.
    const content = record.content as Record<string, unknown>
    if (typeof content['tool_definitions'] === 'string') {
      definitions.push({ text: content['tool_definitions'], catalogue: false })
    }
    if (typeof content['tool_definition'] === 'string') {
      definitions.push({ text: content['tool_definition'], catalogue: false })
    }
  }
  const toolDefinitions: Array<{ name: string; server?: string; tokens: number }> = []
  const offeredToolNames: string[] = []
  const seenOffered = new Set<string>()
  for (const definition of definitions) {
    for (const tool of extractToolDefinitionsFromText(definition.text)) {
      const name = tool.name.trim()
      if (name === '') continue
      if (!seenOffered.has(name)) {
        seenOffered.add(name)
        offeredToolNames.push(name)
      }
      if (!definition.catalogue) continue
      toolDefinitions.push({
        name,
        ...(definition.server !== undefined ? { server: definition.server } : {}),
        tokens: countTokens(tool.text),
      })
    }
  }

  const listing = readListingOf(record, parts)

  const view: RecordPartsView = {
    hasParts: parts !== undefined && parts.length > 0,
    contextParts,
    toolDefinitions,
    offeredToolNames,
    hasDefinitionParts: definitions.length > 0,
    listing,
    carriedBuckets,
    taskSummary: () => taskSummaryOf(record),
  }

  // The text has been counted, parsed and read. The record keeps the values;
  // the store keeps the text, for a reader that asks for it again.
  releaseRecordParts(record)
  return view
}

/**
 * The task summary a record's conversation history carries.
 *
 * The conversation's own text, read on demand: only a subagent's label needs
 * it, and a session's turns would otherwise hold a second copy of the text the
 * reduction released. The parts are released again on the way out.
 */
function taskSummaryOf(record: CanonicalRecord): string | undefined {
  try {
    const parts = record.parts
    if (parts !== undefined && parts.length > 0) {
      for (const part of parts) {
        if (part.part === 'conversation_history' && part.text && part.text.trim()) {
          return part.text.trim()
        }
      }
    }
    const content = record.content
    if (
      content &&
      typeof content['conversation_history'] === 'string' &&
      content['conversation_history'].trim()
    ) {
      return content['conversation_history'].trim()
    }
    return undefined
  } finally {
    releaseRecordParts(record)
  }
}

/**
 * The twin collapse's own reading of a record, where it merged one.
 *
 * The collapse can hand a keeper the parts of its twin, so the keeper's
 * reduced values are stale afterwards: they are derived again from the merged
 * parts. A record the collapse left alone keeps its values, and the parts it
 * re-read to judge whether it had any are released again.
 */
function refreshMergedViews(
  merged: readonly CanonicalRecord[],
  original: readonly CanonicalRecord[],
  views: Map<string, RecordPartsView>,
  countTokens: (text: string) => number,
): void {
  const before = new Set(original)
  for (const record of merged) {
    if (before.has(record)) {
      releaseRecordParts(record)
      continue
    }
    views.set(record.spanId, reduceRecordParts(record, countTokens))
  }
}

/**
 * The records' reduced parts, one view per record.
 *
 * The projection reduces every record of the session before the readers run,
 * so the whole session's text is never in memory at once.
 */
function deriveRecordViews(
  records: readonly CanonicalRecord[],
  countTokens: (text: string) => number,
): Map<string, RecordPartsView> {
  const views = new Map<string, RecordPartsView>()
  for (const record of records) {
    views.set(record.spanId, reduceRecordParts(record, countTokens))
  }
  return views
}

/**
 * The record's reduced parts.
 *
 * Every record the readers touch carries a view: the projection reduces them
 * all before the readers run, and a caller that passes records it did not
 * reduce gets them reduced here.
 */
function viewOf(
  views: ReadonlyMap<string, RecordPartsView>,
  record: CanonicalRecord,
): RecordPartsView {
  const view = views.get(record.spanId)
  if (view === undefined) {
    throw new Error(`no reduced parts for record ${record.spanId}`)
  }
  return view
}

/**
 * Merge the records' own measurability declarations. A metric is only
 * `not_measurable` for the session when every record that spoke about it said
 * so — one span exporting message structure means the session has structure,
 * even if others did not (R10.1).
 */
export function mergeMeasurability(records: readonly CanonicalRecord[]): Measurability | undefined {
  const seen = new Map<string, MetricAvailability[]>()
  for (const record of records) {
    for (const [metric, availability] of Object.entries(record.measurability ?? {})) {
      const values = seen.get(metric) ?? []
      values.push(availability)
      seen.set(metric, values)
    }
  }
  if (seen.size === 0) return undefined
  const merged: Measurability = {}
  for (const [metric, values] of seen) {
    if (values.some((value) => value === 'measured')) merged[metric] = 'measured'
    else if (values.some((value) => value === 'derived')) merged[metric] = 'derived'
    else {
      merged[metric] = values.find(
        (value): value is NotMeasurable =>
          typeof value === 'object' && value.availability === 'not_measurable',
      ) ?? {
        availability: 'not_measurable',
        reason: 'The source did not provide this metric.',
      }
    }
  }
  return merged
}

function unavailableFor(measurability: Measurability | undefined, metric: string): NotMeasurable | undefined {
  const value = measurability?.[metric]
  return typeof value === 'object' && value.availability === 'not_measurable' ? value : undefined
}

/**
 * The canonical buckets the session's records actually carry non-empty
 * parts for (B6). Session-level availability is derived from this
 * evidence, not only from the merged declarations: a bucket any record
 * carries parts for was measurable, whatever a declaration stamped at
 * normalize time — before parts reached the record by another route, such
 * as a request-body log or the interaction prompt — still says.
 */
function carriedBucketsOf(
  records: readonly CanonicalRecord[],
  views: ReadonlyMap<string, RecordPartsView>,
): Set<string> {
  const carried = new Set<string>()
  for (const record of records) {
    for (const bucket of viewOf(views, record).carriedBuckets) {
      carried.add(bucket)
    }
  }
  return carried
}

/**
 * The declaration the composition gate answers from (B6): the merged map
 * minus every content-key `not_measurable` a record's own parts
 * contradict. Without this, one sibling's stale stamp keeps the whole
 * session's composition refused — `declared_not_measurable` — over text
 * the corpus demonstrably holds. Only the gate sees the adjusted map:
 * per-bucket overlays keep the declarations, so a turn that observed a
 * bucket at zero still gets the reason its records gave.
 */
function compositionDeclaration(
  measurability: Measurability | undefined,
  carriedBuckets: ReadonlySet<string>,
): Measurability | undefined {
  if (measurability === undefined) return undefined
  const adjusted = { ...measurability }
  for (const key of carriedBuckets) {
    const declared = adjusted[key]
    if (typeof declared === 'object' && declared.availability === 'not_measurable') {
      delete adjusted[key]
    }
  }
  return adjusted
}

/**
 * Tool definitions for the schema ranking. Only parts carrying a ground-truth
 * `server` are attributed to one; the rest are built-in as far as this system
 * is concerned. Splitting a prefixed name to recover a server is the thing
 * R8.3 forbids, because delimiters occur inside real server names.
 */
function toolDefinitionsOf(
  records: readonly CanonicalRecord[],
  views: ReadonlyMap<string, RecordPartsView>,
): ToolDefinition[] {
  const byName = new Map<string, ToolDefinition>()
  for (const record of records) {
    for (const definition of viewOf(views, record).toolDefinitions) {
      // Unnamed definitions (unparseable blobs, schemaless entries) stay
      // out of the name-keyed ranking: every one would collapse into a
      // single blank-named row, silently dropping all but the first
      // blob's cost. Their residence is already counted in the
      // `tool_definitions` context bucket, so nothing is lost by skipping.
      const existing = byName.get(definition.name)
      if (existing === undefined) {
        byName.set(definition.name, {
          name: definition.name,
          // A part's server applies to what it carried; an aggregate blob
          // that named no server yields tools with no server, which the
          // ranking reports as built-in rather than grouping by a guess.
          ...(definition.server !== undefined ? { server: definition.server } : {}),
          // Per-tool cost is always derived. The harness's aggregate
          // `tool_tokens` covers the whole blob and cannot be divided
          // across N tools without inventing the split -- it is used for
          // the context bucket total, not for this ranking.
          tokens: definition.tokens,
          turnsResident: 1,
        })
      } else {
        existing.turnsResident = (existing.turnsResident ?? 0) + 1
      }
    }
  }
  return [...byName.values()]
}

/**
 * Split a tool-definition part into individual tools. Harnesses send either
 * one definition per part or the whole catalogue as a single JSON array; an
 * array left unsplit ranks as one tool whose name is the entire blob, which
 * is how "[{\"name\": \"define_subagent\"}, ...]" ends up in a cost table.
 */
function extractToolDefinitionsFromText(text: string): { name: string; text: string }[] {
  if (!text || !text.trim()) return []
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    // Non-JSON content (plain text, YAML, truncated write):
    // Preserved for token residence as a counted-but-unnamed entry without
    // promoting the raw text to a tool name.
    return [{ name: "", text }]
  }

  const items: unknown[] = Array.isArray(parsed)
    ? parsed
    : (parsed !== null && typeof parsed === "object" && Array.isArray((parsed as Record<string, unknown>).tools))
      ? ((parsed as Record<string, unknown>).tools as unknown[])
      : [parsed]

  const results: { name: string; text: string }[] = []
  for (const item of items) {
    if (typeof item === "string" && item.trim()) {
      results.push({ name: item.trim(), text: item })
    } else if (item !== null && typeof item === "object") {
      const obj = item as { name?: unknown; function?: { name?: unknown } }
      const itemText = JSON.stringify(item)
      if (typeof obj.name === "string" && obj.name.trim()) {
        results.push({ name: obj.name.trim(), text: itemText })
      } else if (typeof obj.function?.name === "string" && obj.function.name.trim()) {
        results.push({ name: obj.function.name.trim(), text: itemText })
      } else {
        results.push({ name: "", text: itemText })
      }
    }
  }
  return results
}

/** Tool names the session actually called, from its tool spans. */
function invocationsOf(records: readonly CanonicalRecord[]): string[] {
  return records
    .filter((record) => record.op === "tool.invoke")
    .map((record) => (attributeOf(record, ["gen_ai.tool.name", "tool.name", "tool_name"]) ?? record.name).trim())
    // A nameless span falls back to the generic op name; neither is a tool identity.
    .filter((name) => name !== "" && name !== "tool.invoke")
}

function hasEvidence(
  records: readonly CanonicalRecord[],
  views: ReadonlyMap<string, RecordPartsView>,
): boolean {
  // A session is a conversation with a model. A trace carrying no model call
  // is not one, however many spans it holds: the receiver ingests everything
  // sent to it, including the HTTP client and server spans emitted by
  // instrumented libraries — 29,771 of them in the measured corpus, with no
  // tokens and no content, which grouped by trace into 44 rows that looked
  // like sessions and were not. Requiring a turn is what distinguishes an
  // agent session from ambient telemetry that happened to share a trace.
  const turns = turnsOf(records)
  if (turns.length === 0) return false

  return turns.some(
    (record) =>
      measuredInput(record.tokens) > 0 ||
      record.tokens.output > 0 ||
      viewOf(views, record).hasParts ||
      // A record WITHOUT parts: its flat content is the only evidence left,
      // and the store read it eagerly for such a row, so asking for it here
      // costs nothing. A record WITH parts is already counted above.
      (!viewOf(views, record).hasParts && Object.keys(record.content).length > 0),
  )
}

export type BuildSessionsReport = {
  built: number
  skipped: number
  /** Rows removed because their session no longer builds. */
  pruned: number
  /** Harness rollup rows rebuilt over the sessions, runs and executions. */
  rollups: number
  /** Findings the detector suite emitted over the rebuilt runs. */
  findings: number
}

export type AsadContextBucket = {
  buckets: Record<string, number | NotMeasurable>
  reported_input: number | NotMeasurable
}

export type AsadTool = {
  name?: string
  server?: string
  schema_tokens: number
  invocations: number
  turns_resident: number
  errors?: number
  total_schema_cost?: number
}

export type AsadServer = {
  server: string
  is_mcp: true
  tools: number
  schema_tokens: number
  invocations: number
  unused_tools: number
  unused_cost: number
}

/**
 * One skill on the session's skills view. `turns_listed` holds the payload
 * turn indices (0-based) the skill was listed on. `invocations` is declared
 * not measurable, never zero, when the harness has no invocation source.
 */
export type AsadSkillRow = {
  name: string
  listed: boolean
  invocations: number | NotMeasurable
  turns_listed: number[]
  /** The first listing text observed for the skill in the session; absent when none carried one. */
  description?: string
  /** Tokens of `description` through the injected counter; absent with no description. */
  derived_tokens?: number
}

/**
 * The session's skills view. `reported_skill_tokens` is the harness's per-turn
 * resident figure, never summed. The listed and uninvoked counts are each
 * absent when the side they depend on is not measured.
 */
export type AsadSkillsPayload =
  | {
      measurable: true
      reported_skill_tokens?: number
      listed_count?: number
      listed_uninvoked_count?: number
      rows: AsadSkillRow[]
    }
  | NotMeasurable

export type AsadSessionPayload = {
  id: string
  session_id: string
  harness: string
  label: string
  agent_name: string | null
  repo: string | null
  branch: string | null
  span_count: number
  context: {
    first: AsadContextBucket
    last: AsadContextBucket
    [key: string]: unknown
  }
  tools: AsadTool[]
  timeline: ReturnType<typeof buildTimeline>['children'] | NotMeasurable
  turns: Array<Record<string, unknown>>
  /**
   * The server's basis verdict: `null` when the session's cost blocks agree, or
   * when nothing was priced. Decided here, where `sumCosts` already refuses to
   * blend, so a display layer renders the answer instead of re-deriving it
   * (rule R1). Always present once a store has been re-projected by a build
   * that writes it — `undefined` only on a row stored before this field existed,
   * which a display layer must read as "unknown", never as "no mismatch".
   */
  costBasisMismatch: CostBasisMismatch | null
  /**
   * The skills view. Absent only on a row stored before this field existed,
   * which a display layer must read as "unknown", never as "no skills".
   */
  skills?: AsadSkillsPayload
  requests: Array<Record<string, unknown>>
  servers: AsadServer[]
  coverage: Record<string, number>
  problems: Array<Record<string, unknown>>
  reconciliation: Array<Record<string, unknown>>
  summary: Record<string, unknown>
  [key: string]: unknown
}

/**
 * Projection-time repricing (issue #186, U9). Turn records of API-billed harnesses are priced from
 * the published table and Copilot-family turns from the credits table; a changed block is written
 * back so the session summary, payload and cost contributions agree. Harness-reported costs (R5.2)
 * and other harnesses are left alone, and an unchanged block causes no write.
 *
 * Design note: `records.cost_json` is the derived, re-derivable cost cache. The report path reads
 * it, the next projection reprices it, and a re-ingest overwrite self-heals it. The changed blocks
 * of one session are written back in a single transaction (`setCosts`), once per session.
 */
function repriceTurns(store: CanonStore, records: CanonicalRecord[]): CanonicalRecord[] {
  const changes: Array<{ spanId: string; cost: CostBlock }> = []
  const repriced = records.map((record) => {
    if (record.op !== 'llm.invoke' && !isRollup(record)) return record
    const model = attributeOf(record, MODEL_KEYS)
    let next: CostBlock
    if (isPublishedTableHarness(record.harness)) {
      next = pricePublishedTurn(record.tokens, model, record.harness, record.cost)
    } else if (isCopilotHarness(record.harness)) {
      next = priceCopilotTurn(record.tokens, model, record.harness, record.cost)
    } else {
      return record
    }
    if (JSON.stringify(next) === JSON.stringify(record.cost)) return record
    changes.push({ spanId: record.spanId, cost: next })
    // The record is repriced in place rather than copied. A copy would spread
    // the record, and spreading a record READS its lazy fields — the payload
    // and the parts — so the copy would hold a whole decompressed parts set
    // for as long as the session, which is the working set this projection
    // just released.
    record.cost = next
    return record
  })
  if (changes.length > 0) store.setCosts(changes)
  return repriced
}

/**
 * Build (or rebuild) every derived session in the store.
 *
 * Rebuilding is always safe: the `session` table is a cache over `records`,
 * and every row is replaced wholesale.
 *
 * `options.yieldToLoop` is called between sessions (and between the runs and
 * findings phases), so a rebuild over a real corpus hands the event loop back
 * between units of work instead of holding it for the whole pass. The
 * projection injects it; a direct build (tooling, tests) does not interleave.
 */
export async function buildSessions(
  store: CanonStore,
  options?: BuildSessionsOptions,
): Promise<BuildSessionsReport> {
  const yieldToLoop = options?.yieldToLoop ?? (async () => {})
  // Counted through the store's `token_cache` memo, not the bare encoder. The
  // memo is what R4.6 provisioned the table for, and passing the raw counter
  // here is why it had never held a row: a rebuild re-tokenized every part of
  // every session, and a rebuild is the common case.
  const counter = createCachedCounter(store.tokenCache(), await activeTokenizer(), await loadO200kCounter())
  const countTokens = counter.count
  const report: BuildSessionsReport = { built: 0, skipped: 0, pruned: 0, rollups: 0, findings: 0 }
  const built = new Set<string>()
  const identities = store.sessionIdentities()

  for (const key of store.sessionKeys()) {
    // Before the unit, not after: a session that is skipped still consumed a
    // turn, and the waiter's wait is bounded by one session either way.
    await yieldToLoop()
    // The session's payloads are reduced as they are read: the records stay
    // (the twin collapse, the descendant collection and the subagent
    // projection need the whole session), their decompressed telemetry does
    // not.
    const records: CanonicalRecord[] = []
    for (const projected of store.recordsForSessionProjected(key.key)) {
      adoptProjectedAttributes(projected)
      records.push(projected.record)
    }
    const grouped = groupByCanonicalHarness(repriceTurns(store, records))
    if (grouped.size === 0) {
      report.skipped += 1
      continue
    }
    // The parts are reduced and released the same way the payloads are: a
    // record's parts are read once, reduced to the values the readers need,
    // and dropped, so the session's decompressed text is never in memory all
    // at once. The views are what the readers read instead.
    const views = deriveRecordViews(records, countTokens)
    for (const [harness, records] of grouped) {
      if (records.length === 0 || !hasEvidence(records, views)) {
        report.skipped += 1
        continue
      }
      // Twin front-end collectors describe the same turns twice (issue #182,
      // ADR 0009 D4): collapse same-turn observations before the row sums
      // them, or the merged session double-counts one conversation.
      const merged = dedupeTwinTurns(records, key.key)
      // The collapse may hand a keeper its twin's parts, so the keeper's
      // reduced values are stale afterwards: they are derived again from the
      // merged parts. A record the collapse left alone keeps its values, and
      // the parts it re-read to judge whether it had any are released again.
      refreshMergedViews(merged, records, views, countTokens)
      if (merged.length === 0 || !hasEvidence(merged, views)) {
        report.skipped += 1
        continue
      }
      const sessionId = identities.claim(key.key, harness)
      const parentRow = buildSessionRow(sessionId, merged, countTokens, store, {
        isSubagent: false,
        parentSession: null,
        parts: views,
      })
      store.upsertSession(parentRow)
      built.add(sessionId)
      report.built += 1

      // Subagent session projection (Contract T1.1)
      const isCopilot = isCopilotHarness(harness) || harness === "copilot"
      if (isCopilot && !sessionId.includes("#")) {
        const invokeSpans = merged.filter((r) => isInvokeAgentSpan(r) && r.spanId !== sessionId)
        for (const invokeSpan of invokeSpans) {
          const subagentId = `${sessionId}#${invokeSpan.spanId}`
          const descendants = collectDescendantRecords(invokeSpan.spanId, merged)
          const rootInvokeSpan: CanonicalRecord = {
            ...invokeSpan,
            parentSpanId: null,
          }
          const sortedDescendants = [...descendants].sort((a, b) => compareTimestamps(a.timestamp, b.timestamp))
          if (sortedDescendants.length > 0) {
            let earliest = rootInvokeSpan.timestamp
            for (const d of sortedDescendants) {
              const tD = d.timestamp instanceof Date ? d.timestamp.getTime() : Date.parse(String(d.timestamp))
              const tE = earliest instanceof Date ? earliest.getTime() : Date.parse(String(earliest))
              if (Number.isFinite(tD) && Number.isFinite(tE) && tD < tE) {
                earliest = d.timestamp
              }
            }
            rootInvokeSpan.timestamp = earliest
          }
          const subagentRecords = [rootInvokeSpan, ...sortedDescendants]

          const agentName = extractAgentName(invokeSpan)
          const taskSummary = extractTaskSummary(subagentRecords, views)
          const label = formatSessionTitle({
            agentName: agentName ?? undefined,
            taskSummary,
            sessionId: subagentId,
            harness,
          })

          const subagentRow = buildSessionRow(
            subagentId,
            subagentRecords,
            countTokens,
            store,
            {
              includedInParent: true,
              isSubagent: true,
              parentSession: sessionId,
              agentName,
              label,
              parts: views,
            },
          )

          store.upsertSession(subagentRow)
          built.add(subagentId)
          report.built += 1
        }
      }

      // Subagent session projection (Claude - Contract T2.2)
      const isClaude = isClaudeHarness(harness) || merged.some((r) => isClaudeHarness(r.harness))
      // Not for a native sidechain session (`agent-…`) or a row this projection already scoped (`…#…`).
      if (isClaude && !sessionId.startsWith("agent-") && !sessionId.includes("#")) {
        const recordAgentMap = new Map<string, string>()

        for (const record of merged) {
          const agentId = extractClaudeAgentId(record)
          if (agentId) {
            recordAgentMap.set(record.spanId, agentId)
          }
        }

        for (const record of merged) {
          const agentId = recordAgentMap.get(record.spanId)
          if (agentId) {
            const descendants = collectDescendantRecords(record.spanId, merged)
            for (const desc of descendants) {
              if (!recordAgentMap.has(desc.spanId)) {
                recordAgentMap.set(desc.spanId, agentId)
              }
            }
          }
        }

        const subagentGroups = new Map<string, CanonicalRecord[]>()
        for (const record of merged) {
          const agentId = recordAgentMap.get(record.spanId)
          if (agentId) {
            const subagentId = claudeSubagentSessionId(sessionId, agentId)
            const list = subagentGroups.get(subagentId) ?? []
            list.push(record)
            subagentGroups.set(subagentId, list)
          }
        }

        for (const [subagentId, groupRecords] of subagentGroups) {
          if (groupRecords.length === 0) continue

          const subagentSpanIds = new Set(groupRecords.map((r) => r.spanId))
          const preparedRecords = groupRecords.map((r) => {
            if (r.parentSpanId === null || !subagentSpanIds.has(r.parentSpanId)) {
              return { ...r, parentSpanId: null }
            }
            return { ...r }
          })

          let rootIndex = preparedRecords.findIndex((r) => isInvokeAgentSpan(r) || r.op === 'agent.invoke')
          if (rootIndex === -1 && preparedRecords.length > 0) {
            rootIndex = 0
          }
          if (rootIndex >= 0) {
            const rootSpanId = preparedRecords[rootIndex]!.spanId
            for (let i = 0; i < preparedRecords.length; i++) {
              const rec = preparedRecords[i]!
              if (rec.spanId === rootSpanId) {
                preparedRecords[i] = { ...rec, parentSpanId: null }
              } else if (rec.parentSpanId === null || !subagentSpanIds.has(rec.parentSpanId)) {
                preparedRecords[i] = { ...rec, parentSpanId: rootSpanId }
              }
            }
            const currentRoot = preparedRecords.find((r) => r.spanId === rootSpanId)!
            // The true earliest of the records that carry a usable timestamp, not whichever
            // record happens to be first; a root with no usable timestamp takes it as well.
            const earliest = preparedRecords
              .filter((r) => Number.isFinite(parseTimestamp(r.timestamp)))
              .sort((a, b) => compareTimestamps(a.timestamp, b.timestamp))[0]
            if (
              earliest !== undefined &&
              !(parseTimestamp(currentRoot.timestamp) <= parseTimestamp(earliest.timestamp))
            ) {
              currentRoot.timestamp =
                earliest.timestamp instanceof Date ? new Date(earliest.timestamp) : earliest.timestamp
            }
          }

          preparedRecords.sort((a, b) => compareTimestamps(a.timestamp, b.timestamp))

          const agentName = extractClaudeAgentName(preparedRecords)
          const taskSummary = extractClaudeDescription(preparedRecords, views)
          const label = formatSessionTitle({
            agentName: agentName ?? undefined,
            taskSummary,
            sessionId: subagentId,
            harness,
          })

          let parentSession = sessionId
          for (const r of preparedRecords) {
            const p = attributeOf(r, [
              'parent_session_id',
              'parent_session',
              'parentSessionId',
              'parentSession',
              'gen_ai.parent_session_id',
            ])
            if (p && p.trim()) {
              parentSession = p.trim()
              break
            }
          }

          const subagentRow = buildSessionRow(
            subagentId,
            preparedRecords,
            countTokens,
            store,
            {
              // Only when the parent is the session these records were merged under; a parent
              // named by an attribute may be a different session whose counters do not hold them.
              includedInParent: parentSession === sessionId,
              isSubagent: true,
              parentSession,
              agentName,
              label,
              parts: views,
            },
          )

          store.upsertSession(subagentRow)
          built.add(subagentId)
          report.built += 1
        }
      }
    }
  }

  // Counting is done; the buffered misses are written once rather than one
  // fsync at a time.
  counter.flush()

  // A rebuild is authoritative. Rows from an earlier build whose session no
  // longer qualifies are removed rather than left to haunt the session list —
  // this is a cache over `records`, so a stale row is simply wrong.
  for (const sessionId of store.builtSessionIds()) {
    if (built.has(sessionId)) continue
    store.deleteSession(sessionId)
    report.pruned += 1
  }

  // Rebuild run and execution tables over canonical records (D13, ADR 0008)
  await buildRuns(store, { yieldToLoop })

  // Everything downstream of `run` is a cache over the same records, so it is
  // rebuilt here too. Leaving rollups and findings to the refresh path alone
  // let `kyber build` produce runs the dashboard could list but not score: the
  // harness endpoint 404s on a missing rollup and the scorecard then reads as
  // "telemetry missing" for telemetry that was collected and never aggregated.
  report.rollups = buildHarnessRollup(store).length
  report.findings = (await buildFindingsYielding(store, yieldToLoop)).findingsBuilt

  return report
}

/** How a build hands the event loop back between units of work. */
export type BuildSessionsOptions = {
  /**
   * Called between units of work (a session, a run, a run's findings).
   *
   * The projection injects a real yield so a pending HTTP handler is not
   * starved by a rebuild; a direct build has nothing to interleave with.
   */
  readonly yieldToLoop?: () => Promise<void>
}

export type BuildSessionRowOptions = {
  /** The row's records are a subset of its parent's, so the parent's counters already include it. */
  includedInParent?: boolean
  isSubagent?: boolean
  parentSession?: string | null
  agentName?: string | null
  label?: string | null
  /**
   * The records' reduced parts, one view per record.
   *
   * The projection reduces every record's parts before the readers run — the
   * text is the session's working set, and a record that held it lived as
   * long as the session. A caller with no views gets them reduced here.
   */
  parts?: ReadonlyMap<string, RecordPartsView>
}

/** Build one session row from its records. Pure — exported for testing. */
export function buildSessionRow(
  sessionId: string,
  records: readonly CanonicalRecord[],
  countTokens: (text: string) => number,
  store?: CanonStore,
  options?: BuildSessionRowOptions,
): SessionRow {
  // The reduced parts, one view per record. The projection hands them over
  // (it reduced them before the twin collapse ran); a direct build reduces
  // them here, and releases each record's text as it goes.
  const views = new Map<string, RecordPartsView>(options?.parts ?? [])
  for (const record of records) {
    if (!views.has(record.spanId)) views.set(record.spanId, reduceRecordParts(record, countTokens))
  }
  const turnRecords = turnsOf(records)
  const first = records[0]!
  // The canonical harness, not the provider entry the record arrived under.
  // `run`, `execution` and `listHarnesses` are all keyed canonically, and
  // storing the raw name here meant the rollup's `listSessions(harness)` never
  // matched them: the claude-code scorecard computed its context pressure,
  // cache hit rate and tool yield from the 1 session stored as `claude-code`
  // while 58 more sat under `claude`, and still reported a sample count of 59
  // because that count comes from runs. Cursor lost 10 sessions the same way.
  const harness = normalizeHarnessName(first.harness)

  // Only turns with a measured input can be charted against the context
  // window. A span that carried content but no counters still happened -- it
  // stays in `turns` for spend -- but reconciling buckets against an input of
  // zero yields a negative residual, which is not a finding about the model's
  // context, only about the absent counter. The count is reported so the
  // omission is stated rather than hidden.
  const measuredTurns = turnRecords.filter((record) => measuredInput(record.tokens) > 0)
  const unmeasuredTurns = turnRecords.length - measuredTurns.length
  // Some harnesses (Copilot) send the system prompt on the first request only,
  // yet every later request still carries it in its input. The last one seen
  // stands in for turns that report none; a turn's own always wins. Done here,
  // not in `analyzeContext`, which stays a pure per-turn bucketing.
  let lastSystem: ContextPart[] = []
  const contextTurns: ContextTurn[] = measuredTurns.map((record) => {
    const parts = viewOf(views, record).contextParts
    const own = parts.filter((part) => part.part === 'system_prompt')
    if (own.length > 0) lastSystem = own
    return {
      parts: own.length > 0 ? parts : [...lastSystem, ...parts],
      inputTokens: measuredInput(record.tokens),
      freshInput: record.tokens.freshInput,
    }
  })

  // Same rule the finding detector uses (`contextLimitOf`): first turn record
  // to name a window wins, default otherwise, so a session row and a
  // compaction finding built from the same records can never disagree.
  // Catalog replaces only that default. A reported or declared window stays
  // what the records said; the 200K constant is not relabelled as catalog.
  const telemetryWindow = contextLimitOf(records)
  const window =
    telemetryWindow.contextLimitSource === 'default' && store !== undefined
      ? (catalogWindowForRecords(records, store) ?? telemetryWindow)
      : telemetryWindow
  // `analyzeContext` rejects a non-positive limit. An absent bridge window
  // has none: composition still buckets against the placeholder, and the
  // published payload drops that placeholder so 200K is not stored as the
  // window (G1-Q2 = (b)).
  const analysisLimit = window.contextLimit > 0 ? window.contextLimit : DEFAULT_CONTEXT_LIMIT
  const measurability = mergeMeasurability(records)
  const carriedBuckets = carriedBucketsOf(records, views)
  const context = analyzeContext(contextTurns, {
    contextLimit: analysisLimit,
    contextLimitSource: window.contextLimitSource,
    countTokens,
    ...(measurability !== undefined
      ? { measurability: compositionDeclaration(measurability, carriedBuckets) }
      : {}),
  })

  const definitions = toolDefinitionsOf(records, views)
  const schema = rankSchemas(definitions, turnRecords.length, invocationsOf(records), undefined, measurability)
  const toolRecords = records.filter((r) => r.op === 'tool.invoke')

  const invocationCounts = new Map<string, number>()
  const errorCounts = new Map<string, number>()
  for (const record of toolRecords) {
    const name = (attributeOf(record, ['gen_ai.tool.name', 'tool.name', 'tool_name']) ?? record.name).trim()
    if (!name) continue
    invocationCounts.set(name, (invocationCounts.get(name) ?? 0) + 1)
    const isError =
      record.status === 'error' ||
      attributeOf(record, ['gen_ai.tool.status', 'status']) === 'error' ||
      (record.raw !== null &&
        typeof record.raw === 'object' &&
        ((record.raw as Record<string, unknown>)['gen_ai.tool.status'] === 'error' ||
          (record.raw as Record<string, unknown>).is_error === true ||
          (record.raw as Record<string, unknown>).isError === true))
    if (isError) {
      errorCounts.set(name, (errorCounts.get(name) ?? 0) + 1)
    }
  }

  const rankedTools = schema.measurable
    ? schema.ranked
    : definitions.map((definition) => ({
        name: definition.name,
        ...(definition.server !== undefined ? { server: definition.server } : {}),
        cost: definition.tokens * (definition.turnsResident ?? turnRecords.length),
      }))
  const definitionsByName = new Map(definitions.map((definition) => [definition.name, definition]))
  const seenToolNames = new Set<string>()
  const tools: AsadTool[] = rankedTools.map((tool) => {
    seenToolNames.add(tool.name)
    const definition = definitionsByName.get(tool.name)
    const schemaTokens = definition?.tokens ?? 0
    const turnsResident = definition?.turnsResident ?? turnRecords.length
    const invocationsCount = invocationCounts.get(tool.name) ?? 0
    const errorCount = errorCounts.get(tool.name) ?? 0
    // A row always carries its name, server and resident cost: a session with
    // no tool calls still offered definitions, and the ranking is about those.
    return {
      name: tool.name,
      schema_tokens: schemaTokens,
      invocations: invocationsCount,
      turns_resident: turnsResident,
      errors: errorCount,
      ...(tool.cost !== undefined ? { total_schema_cost: tool.cost } : {}),
      ...(definition?.server !== undefined ? { server: definition.server } : {}),
    }
  })

  for (const [name, invocationsCount] of invocationCounts) {
    if (!seenToolNames.has(name)) {
      seenToolNames.add(name)
      tools.push({
        name,
        schema_tokens: 0,
        invocations: invocationsCount,
        turns_resident: 0,
        errors: errorCounts.get(name) ?? 0,
      })
    }
  }
  const servers: AsadServer[] = schema.measurable
    ? [...schema.byServer.entries()].map(([server]) => {
        const serverTools = rankedTools.filter((tool) => tool.server === server)
        return {
          server,
          is_mcp: true,
          tools: serverTools.length,
          schema_tokens: serverTools.reduce(
            (sum, tool) => sum + (definitionsByName.get(tool.name)?.tokens ?? 0),
            0,
          ),
          invocations: serverTools.reduce(
            (sum, tool) => sum + (invocationCounts.get(tool.name) ?? 0),
            0,
          ),
          unused_tools: serverTools.filter((tool) => (invocationCounts.get(tool.name) ?? 0) === 0).length,
          unused_cost: serverTools
            .filter((tool) => (invocationCounts.get(tool.name) ?? 0) === 0)
            .reduce((sum, tool) => sum + tool.cost, 0),
        }
      })
    : []

  const analyzedTurns = context.measurable ? context.turns : []
  const contextBucketDeclarations = {
    system_prompt: 'system_prompt',
    conversation_history: 'conversation_history',
    tool_definitions: 'tool_definitions',
    tool_results: 'tool_result_content',
    response: 'response',
  } as const
  const unavailableBuckets = Object.fromEntries(
    Object.entries(contextBucketDeclarations).flatMap(([bucket, declaration]) => {
      const unavailable =
        unavailableFor(measurability, declaration) ??
        // The "did not capture" fallback must not fire over parts a record
        // demonstrably captured (B6) — only reachable when composition
        // stayed unmeasurable for a reason other than this bucket.
        (!context.measurable && !carriedBuckets.has(declaration)
          ? notMeasurable(`Session records for ${harness} did not capture ${declaration}.`)
          : undefined)
      return unavailable === undefined ? [] : [[bucket, unavailable]]
    }),
  )
  const unavailableCanonicalBuckets = Object.fromEntries(
    Object.entries(contextBucketDeclarations).flatMap(([bucket, canonical]) =>
      unavailableBuckets[bucket] === undefined ? [] : [[canonical, unavailableBuckets[bucket]]],
    ),
  )
  const unavailableContextBuckets = { ...unavailableCanonicalBuckets, ...unavailableBuckets }
  // A session-wide declaration merges every record's, so it must not erase a count
  // this turn observed; it only stands in for a bucket the turn has at zero.
  const unavailableOverlay = (buckets: Record<string, number>) =>
    Object.fromEntries(
      Object.entries(contextBucketDeclarations).flatMap(([bucket, canonical]) => {
        const unavailable = unavailableBuckets[bucket]
        return unavailable !== undefined && !buckets[canonical]
          ? [[canonical, unavailable], [bucket, unavailable]]
          : []
      }),
    )
  const contextBucket = (turn: (typeof analyzedTurns)[number] | undefined, reportedInput: number): AsadContextBucket => ({
    buckets: turn?.buckets
      ? { ...turn.buckets, ...unavailableOverlay(turn.buckets) }
      : Object.keys(unavailableContextBuckets).length > 0
        ? unavailableContextBuckets
        : {},
    reported_input: unavailableFor(measurability, 'token_usage') ?? reportedInput,
  })
  const serialized = serializeContext(context)
  // Drop the analysis placeholder. A stored 200K limit, or a pressure figure
  // computed against it, would be the default window standing in for a bridge
  // span that declared none.
  const publishedContext =
    window.contextLimitSource === 'absent'
      ? {
          ...serialized,
          contextLimit: 0,
          ...(serialized.measurable
            ? {
                turns: serialized.turns.map((turn) => {
                  const { pressure: _pressure, headroom: _headroom, ...rest } = turn
                  return rest
                }),
              }
            : {}),
        }
      : serialized
  const contextShape = {
    ...publishedContext,
    contextLimitSource: window.contextLimitSource,
    first: contextBucket(analyzedTurns[0], measuredTurns[0]?.tokens.reportedInput ?? 0),
    last: contextBucket(
      analyzedTurns[analyzedTurns.length - 1],
      measuredTurns[measuredTurns.length - 1]?.tokens.reportedInput ?? 0,
    ),
    unmeasuredTurns,
  }

  const timeline = buildTimeline([...records])
  const cost = sumCosts(turnRecords.map((record) => record.cost))
  // The same refusal, as a verdict the payload carries: the panel renders this
  // text, so the browser never re-runs the sum itself (rule R1).
  const costBasisMismatch = costBasisMismatchOf(turnRecords.map((record) => record.cost), cost)

  const totals = turnRecords.reduce(
    (acc, record) => ({
      input: acc.input + measuredInput(record.tokens),
      output: acc.output + record.tokens.output,
      cacheRead: acc.cacheRead + record.tokens.cacheRead,
      cacheCreation: acc.cacheCreation + record.tokens.cacheCreation,
    }),
    { input: 0, output: 0, cacheRead: 0, cacheCreation: 0 },
  )

  const models = [...new Set(turnRecords.map((r) => attributeOf(r, MODEL_KEYS)).filter(Boolean))]
  const { started, ended } = timestampBounds(records)

  let toolCalls: number | undefined = undefined
  let toolsInvoked: string[] | undefined = undefined

  if (toolRecords.length > 0) {
    // A derived count can be a lower bound. When the collector truncated the
    // conversation before its end, the tool records say so, and the summary
    // must not turn that into a bare finite number: the dashboard reads a
    // finite `tool_calls` as a measured one, and a figure that is not measured
    // is omitted, never printed as exact — the rule `cache_hit_ratio` and
    // `cache_creation_coverage` already follow here. The per-record caveat is
    // where a reader learns why the count is absent.
    const toolCallsLowerBound = toolRecords.some((record) => {
      const availability = record.measurability?.tool_calls
      return typeof availability === 'object' && availability.availability === 'not_measurable'
    })
    if (!toolCallsLowerBound) {
      toolCalls = toolRecords.length
    }
    toolsInvoked = Array.from(new Set(invocationsOf(records)))
  } else {
    const isClaude = harness === 'claude' || harness === 'claude-code' || surveyFamily(harness) === 'claude-code'
    const toolCallsAvailability = measurability?.tool_calls ?? (isClaude ? 'measured' : undefined)
    if (toolCallsAvailability === 'measured' || toolCallsAvailability === 'derived') {
      toolCalls = 0
      toolsInvoked = []
    }
  }

  // The tool names the session's records offered, in the order they were
  // offered. The names come from the reduced parts: the catalogue's text was
  // parsed where it was in hand, and the text itself was released.
  const offeredToolNames: string[] = []
  const seenOfferedToolNames = new Set<string>()
  let hasDefinitionParts = false
  for (const record of records) {
    const view = viewOf(views, record)
    if (view.hasDefinitionParts) hasDefinitionParts = true
    for (const name of view.offeredToolNames) {
      if (seenOfferedToolNames.has(name)) continue
      seenOfferedToolNames.add(name)
      offeredToolNames.push(name)
    }
  }
  const toolsOffered: string[] | undefined = hasDefinitionParts ? offeredToolNames : undefined

  // Issue #185: the session tiles read `cache_hit_ratio` and
  // `cache_creation_coverage`, which no writer ever emitted — one derivation
  // here, the same `cache_read ÷ input` formula the tile footnotes and the
  // harness rollup uses. A figure that is not measured is omitted, never zero:
  // without a measured input the ratio has no denominator, and without turn
  // rows the coverage has nothing to count. Review follow-up (Kilo K1,
  // Copilot C2/C3): turns existing is not a measured counter — when the input
  // is undeclared, or the harness's vocabulary holds no such counter (Cursor
  // exports totals but no cache counters), both keys stay absent so the tiles
  // cannot print a 0% ratio or "on 0 turns" beside stored zeros.
  const summaryTotalInput = unavailableFor(measurability, 'token_usage') ?? totals.input
  const cacheHitRatio =
    typeof summaryTotalInput === 'number' &&
    summaryTotalInput > 0 &&
    harnessExportsCacheCounter(harness, 'read')
      ? totals.cacheRead / summaryTotalInput
      : undefined
  // Review re-review (Kilo 2): the ratio beside this already requires input
  // > 0 — a measured zero input emits neither key.
  const cacheCreationCoverage =
    typeof summaryTotalInput === 'number' &&
    summaryTotalInput > 0 &&
    turnRecords.length > 0 &&
    harnessExportsCacheCounter(harness, 'creation')
      ? turnRecords.filter((record) => record.tokens.cacheCreation > 0).length
      : undefined

  // The skills view (T2). The engine decides what is listed and invoked; this
  // only maps it onto the payload, numbering turns by their payload index.
  // One fold yields both the view and the per-turn listing (T7 blocker 2).
  const { view: skillsView, listed: skillsListed } = skillsForSession(
    records,
    turnRecords,
    measurability ?? {},
    countTokens,
    listingsOf(records, views),
  )
  const skillsInvoked =
    'availability' in skillsView || skillsView.invocationsAvailability !== undefined
      ? undefined
      : invokedByTurnOf(records)
  const skills = skillsPayloadOf(skillsView, firstDescriptionsOf(skillsListed, turnRecords), countTokens, turnRecords)

  const rawAgentName = attributeOf(first, AGENT_NAME_KEYS) ?? null
  const agentName = options?.agentName !== undefined ? options.agentName : rawAgentName
  const label = options?.label !== undefined && options.label !== null ? options.label : first.name

  const payload: AsadSessionPayload = {
    id: sessionId,
    session_id: sessionId,
    harness,
    label,
    agent_name: agentName,
    repo: attributeOf(first, REPO_KEYS) ?? null,
    branch: attributeOf(first, BRANCH_KEYS) ?? null,
    span_count: records.length,
    summary: {
      turn_count: turnRecords.length,
      request_count: records.filter((r) => r.parentSpanId === null).length,
      total_input: summaryTotalInput,
      total_output: totals.output,
      total_cache_read: totals.cacheRead,
      total_cache_creation: totals.cacheCreation,
      ...(cacheHitRatio !== undefined ? { cache_hit_ratio: cacheHitRatio } : {}),
      ...(cacheCreationCoverage !== undefined
        ? { cache_creation_coverage: cacheCreationCoverage }
        : {}),
      duration_ms: records.reduce((sum, record) => sum + record.durationMs, 0),
      models,
      cost: cost.ok ? cost.total : { basis: 'unknown' as const, status: 'no_rate' as const },
      ...(toolCalls !== undefined ? { tool_calls: toolCalls } : {}),
      ...(toolsInvoked !== undefined ? { tools_invoked: toolsInvoked } : {}),
      ...(toolsOffered !== undefined ? { tools_offered: toolsOffered } : {}),
      ...skillsSummaryOf(skillsView),
      ...(options?.includedInParent ? { included_in_parent: true } : {}),
    },
    // The analysis output, verbatim. `toolDefinitionsByServer` is a Map, which
    // JSON.stringify would silently render as {} — convert it explicitly so a
    // per-server band that exists in the data survives to the chart.
    context: contextShape,
    tools,
    schema: schema.measurable
      ? {
          measurable: true as const,
          byServer: Object.fromEntries(schema.byServer),
          neverInvoked: schema.neverInvoked,
          unusedRange: schema.unusedRange,
          turns: schema.turns,
        }
      : unavailableFor(measurability, 'schema_cost') ?? {
          availability: 'not_measurable',
          reason: 'The source did not provide tool-definition schema data.',
        },
    turns: turnRecords.map((record, index) => ({
      index,
      spanId: record.spanId,
      timestamp: record.timestamp,
      model: attributeOf(record, MODEL_KEYS) ?? null,
      input: measuredInput(record.tokens),
      output: record.tokens.output,
      fresh: record.tokens.freshInput,
      cache_read: record.tokens.cacheRead,
      cache_creation: record.tokens.cacheCreation,
      reasoning: record.tokens.reasoning ?? null,
      ...skillsListedOnTurn(skillsListed?.get(record.spanId)),
      ...skillsInvokedOnTurn(skillsInvoked, record.spanId),
    })),
    // ASAD renders the session root's children; the synthetic root is an
    // analysis detail and is not part of the wire contract. The attribute maps
    // are dropped on the way out — see `withoutAttributes`.
    timeline: unavailableFor(measurability, 'execution_structure') ?? withoutAttributes(timeline.children),
    requests: records
      .filter((record) => record.parentSpanId === null)
      .map((record) => ({
        request: record.spanId,
        timestamp: typeof record.timestamp === 'string' ? record.timestamp : record.timestamp.toISOString(),
        turns: turnRecords.includes(record) ? 1 : 0,
        model: attributeOf(record, MODEL_KEYS) ?? null,
      })),
    servers,
    coverage: {
      schema: schema.measurable ? 1 : 0,
      context: context.measurable ? 1 : 0,
    },
    problems: cost.ok ? [] : [cost.problem],
    costBasisMismatch,
    reconciliation: turnRecords.map((record) => ({
      request: record.spanId,
      root_input: record.tokens.reportedInput,
      sum_chat_input: measuredInput(record.tokens),
      input_match: record.tokens.reportedInput === measuredInput(record.tokens),
      root_output: record.tokens.reportedOutput,
      sum_chat_output: record.tokens.output,
      output_match: record.tokens.reportedOutput === record.tokens.output,
    })),
    subagents: subagentSessions(timeline),
    auxiliary: auxiliarySpend(timeline),
    skills,
    // A declared skills verdict is merged here with its reason, so the
    // measurability map states why the skills view is absent.
    measurability: { ...(measurability ?? {}), ...('availability' in skills ? { skills } : {}) },
  }

  const parentSessionAttr =
    attributeOf(first, ['parent_session', 'parent_session_id', 'gen_ai.parent_session_id', 'parentSession']) ?? null
  const isSubagent =
    options?.isSubagent !== undefined
      ? options.isSubagent
      : (parentSessionAttr !== null || Boolean(attributeOf(first, ['gen_ai.is_subagent', 'is_subagent'])))
  const parentSession =
    options?.parentSession !== undefined ? options.parentSession : parentSessionAttr

  return {
    sessionId,
    harness,
    label,
    isSubagent,
    parentSession,
    agentName,
    repo: attributeOf(first, REPO_KEYS) ?? null,
    branch: attributeOf(first, BRANCH_KEYS) ?? null,
    started: typeof started === 'string' ? started : started.toISOString(),
    ended: typeof ended === 'string' ? ended : ended.toISOString(),
    summary: payload.summary,
    payload,
  }
}

const byCodeUnit = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0)

/**
 * The reduced listings, one per record.
 *
 * The listing is read out of the system prompt's text, so it is derived
 * where the text is in hand and handed to the skills fold as a value.
 */
function listingsOf(
  records: readonly CanonicalRecord[],
  views: ReadonlyMap<string, RecordPartsView>,
): Map<string, ListingResult> {
  const listings = new Map<string, ListingResult>()
  for (const record of records) {
    listings.set(record.spanId, viewOf(views, record).listing)
  }
  return listings
}

/**
 * The first listing text observed for each skill, walking the turns in order.
 * A later, different description never replaces it; a name no listing described
 * is absent. Empty when the listing side is not measured.
 */
function firstDescriptionsOf(
  listed: Map<string, Map<string, string | undefined>> | undefined,
  turnRecords: readonly CanonicalRecord[],
): Map<string, string> {
  const first = new Map<string, string>()
  if (listed === undefined) return first
  for (const turn of turnRecords) {
    for (const [name, description] of listed.get(turn.spanId) ?? []) {
      if (description !== undefined && !first.has(name)) first.set(name, description)
    }
  }
  return first
}

/** The distinct names each turn invoked, keyed by turn spanId. */
function invokedByTurnOf(records: readonly CanonicalRecord[]): Map<string, string[]> | undefined {
  const result = extractInvokedSkills(records)
  if ('availability' in result) return undefined
  const byTurn = new Map<string, Set<string>>()
  for (const invocation of result.invocations) {
    const names = byTurn.get(invocation.turnSpanId) ?? new Set<string>()
    names.add(invocation.name)
    byTurn.set(invocation.turnSpanId, names)
  }
  return new Map([...byTurn].map(([spanId, names]) => [spanId, [...names].sort(byCodeUnit)]))
}

/** A turn's `skills_invoked`: names it invoked, `[]` when measured and none. Absent when not measured. */
function skillsInvokedOnTurn(invoked: Map<string, string[]> | undefined, spanId: string): { skills_invoked?: string[] } {
  if (invoked === undefined) return {}
  return { skills_invoked: invoked.get(spanId) ?? [] }
}

/** The engine's skills view on the payload: rows by name, turns as payload indices. */
function skillsPayloadOf(
  view: SkillsView,
  firstDescriptions: ReadonlyMap<string, string>,
  countTokens: (text: string) => number,
  turnRecords: readonly CanonicalRecord[],
): AsadSkillsPayload {
  if ('availability' in view) return view
  const indexBySpan = new Map<string, number>()
  turnRecords.forEach((record, index) => {
    if (!indexBySpan.has(record.spanId)) indexBySpan.set(record.spanId, index)
  })
  const rows: AsadSkillRow[] = [...view.rows]
    .sort((a, b) => byCodeUnit(a.name, b.name))
    .map((row) => {
      const description = firstDescriptions.get(row.name)
      return {
        name: row.name,
        listed: row.listed,
        invocations: row.invocations ?? view.invocationsAvailability ?? notMeasurable('Skill invocations are not measurable.'),
        turns_listed: row.turnSpanIds.flatMap((spanId) => indexBySpan.get(spanId) ?? []),
        ...(description !== undefined ? { description, derived_tokens: countTokens(description) } : {}),
      }
    })
  return {
    measurable: true,
    ...(view.reportedSkillTokens !== undefined ? { reported_skill_tokens: view.reportedSkillTokens } : {}),
    listed_count: rows.filter((row) => row.listed).length,
    // Counted only when both sides are measured: an unmeasured invocation side
    // makes "listed and never invoked" unknowable, so it is not reported as zero.
    ...(view.invocationsAvailability === undefined
      ? { listed_uninvoked_count: rows.filter((row) => row.listed && row.invocations === 0).length }
      : {}),
    rows,
  }
}

/** The summary's skill names: listed names always, invoked names only when measured. */
function skillsSummaryOf(view: SkillsView): { skills_listed?: string[]; skills_invoked?: string[] } {
  if ('availability' in view) return {}
  const skillsListed = view.rows.filter((row) => row.listed).map((row) => row.name).sort(byCodeUnit)
  if (view.invocationsAvailability !== undefined) return { skills_listed: skillsListed }
  const skillsInvoked = view.rows
    .filter((row) => row.invocations !== undefined && row.invocations > 0)
    .map((row) => row.name)
    .sort(byCodeUnit)
  return { skills_listed: skillsListed, skills_invoked: skillsInvoked }
}

/** A turn's `skills_listed`, present only when that turn's listing is measured. */
function skillsListedOnTurn(listed: Map<string, string | undefined> | undefined): { skills_listed?: string[] } {
  if (listed === undefined) return {}
  return { skills_listed: [...listed.keys()].sort(byCodeUnit) }
}

/**
 * The timeline as it is persisted: structure and metadata, without the
 * harness-emitted attribute map each node carried.
 *
 * Those maps are the record's raw span payload, verbatim. Keeping them here
 * meant the derived cache re-stored the entire raw corpus uncompressed — 266 MB
 * of a single 264 MB session payload, and nearly all of the 995 MB the session
 * table held, which is what made rebuilding one harness rollup cost 4.4 GB.
 * They are still available per span through `CanonStore.spanAttributes`, which
 * the inspector calls for the one node it is showing (R9.2 is preserved: the
 * attributes are inspectable, they are simply no longer copied into every
 * session row).
 */
function withoutAttributes(
  nodes: ReturnType<typeof buildTimeline>['children'],
): ReturnType<typeof buildTimeline>['children'] {
  return nodes.map((node) => ({
    ...node,
    attributes: {},
    children: withoutAttributes(node.children),
  }))
}

/** JSON-safe context analysis: the per-server Map becomes an object. */
function serializeContext(context: ReturnType<typeof analyzeContext>) {
  if (!context.measurable) return context
  return {
    ...context,
    turns: context.turns.map((turn) => ({
      ...turn,
      toolDefinitionsByServer: Object.fromEntries(turn.toolDefinitionsByServer),
      // Issue #187: the measured per-turn input, under the key the web
      // composition view already reads (`reported_input`). Without it the view
      // reconciles buckets against `bucketedTokens` and reports residual 0.0%.
      reported_input: turn.inputTokens,
    })),
  }
}
