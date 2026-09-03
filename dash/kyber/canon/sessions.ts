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

import { analyzeContext, type ContextPart, type ContextTurn } from '../analysis/context.js'
import { rankSchemas, type ToolDefinition } from '../analysis/schema.js'
import { auxiliarySpend, buildTimeline, subagentSessions } from '../analysis/timeline.js'
import { measuredInput, sumCosts } from './cost.js'
import { CanonStore, type SessionRow } from './store.js'
import { loadO200kCounter } from './tokens.js'
import type { CanonicalRecord, Measurability } from './types.js'

/**
 * Default context window, used when nothing on the record says otherwise.
 * Named rather than inlined so a wrong headroom figure is traceable to one
 * assumption instead of looking like a measurement.
 */
export const DEFAULT_CONTEXT_LIMIT = 200_000

/** Attributes a harness may report its context window under. */
const CONTEXT_LIMIT_KEYS = [
  'gen_ai.request.max_context_tokens',
  'gen_ai.request.context_window',
  'model_context_window',
] as const

/** Attributes naming the agent, repository and branch, for the session header. */
const AGENT_NAME_KEYS = ['gen_ai.agent.name', 'agent.name'] as const
const REPO_KEYS = ['vcs.repository.name', 'repo'] as const
const BRANCH_KEYS = ['vcs.ref.head.name', 'branch'] as const
const MODEL_KEYS = ['gen_ai.response.model', 'gen_ai.request.model', 'model'] as const

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

/** A turn is a model call. Tool and structural spans are not turns. */
function isTurn(record: CanonicalRecord): boolean {
  return record.op === 'llm.invoke'
}

/**
 * The turn's parts, as the analysis consumes them. Records carrying
 * structured parts pass them through with their server attribution and
 * harness-reported counts intact; records carrying only the flat content map
 * are converted key by key, which loses nothing they had.
 */
function partsOf(record: CanonicalRecord): ContextPart[] {
  if (record.parts !== undefined && record.parts.length > 0) {
    return record.parts.map((part) => ({
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
 * Merge the records' own measurability declarations. A metric is only
 * `not_measurable` for the session when every record that spoke about it said
 * so — one span exporting message structure means the session has structure,
 * even if others did not (R10.1).
 */
function mergeMeasurability(records: readonly CanonicalRecord[]): Measurability | undefined {
  const seen = new Map<string, Set<string>>()
  for (const record of records) {
    for (const [metric, availability] of Object.entries(record.measurability ?? {})) {
      const values = seen.get(metric) ?? new Set<string>()
      values.add(availability)
      seen.set(metric, values)
    }
  }
  if (seen.size === 0) return undefined
  const merged: Measurability = {}
  for (const [metric, values] of seen) {
    if (values.has('measured')) merged[metric] = 'measured'
    else if (values.has('derived')) merged[metric] = 'derived'
    else merged[metric] = 'not_measurable'
  }
  return merged
}

/**
 * Tool definitions for the schema ranking. Only parts carrying a ground-truth
 * `server` are attributed to one; the rest are built-in as far as this system
 * is concerned. Splitting a prefixed name to recover a server is the thing
 * R8.3 forbids, because delimiters occur inside real server names.
 */
function toolDefinitionsOf(
  records: readonly CanonicalRecord[],
  countTokens: (text: string) => number,
): ToolDefinition[] {
  const byName = new Map<string, ToolDefinition>()
  for (const record of records) {
    for (const part of record.parts ?? []) {
      if (part.part !== 'tool_definitions') continue
      for (const tool of expandDefinitions(part.text)) {
        const existing = byName.get(tool.name)
        if (existing === undefined) {
          byName.set(tool.name, {
            name: tool.name,
            // A part's server applies to what it carried; an aggregate blob
            // that named no server yields tools with no server, which the
            // ranking reports as built-in rather than grouping by a guess.
            ...(part.server !== undefined ? { server: part.server } : {}),
            // Per-tool cost is always derived. The harness's aggregate
            // `tool_tokens` covers the whole blob and cannot be divided
            // across N tools without inventing the split -- it is used for
            // the context bucket total, not for this ranking.
            tokens: countTokens(tool.text),
            turnsResident: 1,
          })
        } else {
          existing.turnsResident = (existing.turnsResident ?? 0) + 1
        }
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
function expandDefinitions(text: string): { name: string; text: string }[] {
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    return [{ name: text, text }]
  }

  const one = (value: unknown): { name: string; text: string } => {
    const asText = typeof value === 'string' ? value : JSON.stringify(value)
    if (value !== null && typeof value === 'object') {
      const named = (value as { name?: unknown; function?: { name?: unknown } })
      if (typeof named.name === 'string') return { name: named.name, text: asText }
      // OpenAI-shaped definitions nest the name under `function`.
      if (typeof named.function?.name === 'string') return { name: named.function.name, text: asText }
    }
    return { name: asText, text: asText }
  }

  return Array.isArray(parsed) ? parsed.map(one) : [one(parsed)]
}

/** Tool names the session actually invoked, from its tool spans. */
function invocationsOf(records: readonly CanonicalRecord[]): string[] {
  return records
    .filter((record) => record.op === 'tool.invoke')
    .map((record) => attributeOf(record, ['gen_ai.tool.name']) ?? record.name)
}

/**
 * Whether a group of records says anything at all. The live collector stores
 * every span it receives, including ones that arrive with no attributes and
 * no counters -- 15,535 of them in the measured corpus, every one stamped
 * `op: llm.invoke` and `harness: unattributed` by a receiver that hard-codes
 * both. Building sessions out of those manufactures a session list of
 * near-empty rows. They are skipped here; the receiver should quarantine them
 * at ingest instead, which is a separate fix.
 */
function hasEvidence(records: readonly CanonicalRecord[]): boolean {
  return records.some(
    (record) =>
      measuredInput(record.tokens) > 0 ||
      record.tokens.output > 0 ||
      (record.parts?.length ?? 0) > 0 ||
      Object.keys(record.content).length > 0,
  )
}

export type BuildSessionsReport = {
  built: number
  skipped: number
  /** Rows removed because their session no longer builds. */
  pruned: number
}

/**
 * Build (or rebuild) every derived session in the store.
 *
 * Rebuilding is always safe: the `session` table is a cache over `records`,
 * and every row is replaced wholesale.
 */
export async function buildSessions(store: CanonStore): Promise<BuildSessionsReport> {
  const countTokens = await loadO200kCounter()
  const report: BuildSessionsReport = { built: 0, skipped: 0, pruned: 0 }
  const built = new Set<string>()

  for (const key of store.sessionKeys()) {
    const records = store.recordsForSession(key.key)
    if (records.length === 0 || !hasEvidence(records)) {
      report.skipped += 1
      continue
    }
    store.upsertSession(buildSessionRow(key.key, records, countTokens))
    built.add(key.key)
    report.built += 1
  }

  // A rebuild is authoritative. Rows from an earlier build whose session no
  // longer qualifies are removed rather than left to haunt the session list —
  // this is a cache over `records`, so a stale row is simply wrong.
  for (const sessionId of store.builtSessionIds()) {
    if (built.has(sessionId)) continue
    store.deleteSession(sessionId)
    report.pruned += 1
  }

  return report
}

/** Build one session row from its records. Pure — exported for testing. */
export function buildSessionRow(
  sessionId: string,
  records: readonly CanonicalRecord[],
  countTokens: (text: string) => number,
): SessionRow {
  const turnRecords = records.filter(isTurn)
  const first = records[0]!
  const harness = first.harness

  // Only turns with a measured input can be charted against the context
  // window. A span that carried content but no counters still happened -- it
  // stays in `turns` for spend -- but reconciling buckets against an input of
  // zero yields a negative residual, which is not a finding about the model's
  // context, only about the absent counter. The count is reported so the
  // omission is stated rather than hidden.
  const measuredTurns = turnRecords.filter((record) => measuredInput(record.tokens) > 0)
  const unmeasuredTurns = turnRecords.length - measuredTurns.length
  const contextTurns: ContextTurn[] = measuredTurns.map((record) => ({
    parts: partsOf(record),
    inputTokens: measuredInput(record.tokens),
    freshInput: record.tokens.freshInput,
  }))

  const contextLimit = Number(
    turnRecords.map((r) => attributeOf(r, CONTEXT_LIMIT_KEYS)).find((v) => v !== undefined) ??
      DEFAULT_CONTEXT_LIMIT,
  )
  const measurability = mergeMeasurability(records)
  const context = analyzeContext(contextTurns, {
    contextLimit: Number.isFinite(contextLimit) && contextLimit > 0 ? contextLimit : DEFAULT_CONTEXT_LIMIT,
    countTokens,
    ...(measurability !== undefined ? { measurability } : {}),
  })

  const definitions = toolDefinitionsOf(records, countTokens)
  const schema = rankSchemas(definitions, turnRecords.length, invocationsOf(records), undefined, measurability)

  const timeline = buildTimeline([...records])
  const cost = sumCosts(records.map((record) => record.cost))

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
  const started = records[0]!.timestamp
  const ended = records[records.length - 1]!.timestamp

  const payload = {
    id: sessionId,
    session_id: sessionId,
    harness,
    label: first.name,
    agent_name: attributeOf(first, AGENT_NAME_KEYS) ?? null,
    repo: attributeOf(first, REPO_KEYS) ?? null,
    branch: attributeOf(first, BRANCH_KEYS) ?? null,
    span_count: records.length,
    summary: {
      turn_count: turnRecords.length,
      request_count: records.filter((r) => r.parentSpanId === null).length,
      total_input: totals.input,
      total_output: totals.output,
      total_cache_read: totals.cacheRead,
      total_cache_creation: totals.cacheCreation,
      duration_ms: records.reduce((sum, record) => sum + record.durationMs, 0),
      models,
      cost: cost.ok ? cost.total : { basis: 'unknown' as const, status: 'no_rate' as const },
    },
    // The analysis output, verbatim. `toolDefinitionsByServer` is a Map, which
    // JSON.stringify would silently render as {} — convert it explicitly so a
    // per-server band that exists in the data survives to the chart.
    context: { ...serializeContext(context), unmeasuredTurns },
    tools: schema.measurable
      ? schema.ranked.map((tool) => ({
          name: tool.name,
          server: tool.server ?? null,
          total_schema_cost: tool.cost,
          invoked: tool.invoked,
        }))
      : [],
    schema: schema.measurable
      ? {
          measurable: true as const,
          byServer: Object.fromEntries(schema.byServer),
          neverInvoked: schema.neverInvoked,
          unusedRange: schema.unusedRange,
          turns: schema.turns,
        }
      : schema,
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
    })),
    timeline,
    subagents: subagentSessions(timeline),
    auxiliary: auxiliarySpend(timeline),
    measurability: measurability ?? {},
  }

  return {
    sessionId,
    harness,
    label: first.name,
    isSubagent: false,
    parentSession: null,
    agentName: attributeOf(first, AGENT_NAME_KEYS) ?? null,
    repo: attributeOf(first, REPO_KEYS) ?? null,
    branch: attributeOf(first, BRANCH_KEYS) ?? null,
    started: typeof started === 'string' ? started : started.toISOString(),
    ended: typeof ended === 'string' ? ended : ended.toISOString(),
    payload,
  }
}

/** JSON-safe context analysis: the per-server Map becomes an object. */
function serializeContext(context: ReturnType<typeof analyzeContext>) {
  if (!context.measurable) return context
  return {
    ...context,
    turns: context.turns.map((turn) => ({
      ...turn,
      toolDefinitionsByServer: Object.fromEntries(turn.toolDefinitionsByServer),
    })),
  }
}
