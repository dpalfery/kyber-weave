// SQLite query bridge for the canonical KyberDash store.

import { existsSync, readFileSync } from 'node:fs'
import { inflateSync } from 'node:zlib'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { createRequire } from 'node:module'
import { APPROXIMATE_TOKENIZER, tokenizerName } from '../canon/tokens.js'
import { sumCosts } from '../canon/cost.js'
import {
  CanonStore,
  decompressRaw,
  toFinding,
  toPrediction,
  toRecord,
  toRunRow,
  toHarnessRollupRow,
  toExecutionRow,
  type FindingDbRow,
  type PredictionDbRow,
  type RunDbRow,
  type HarnessRollupDbRow,
  type ExecutionDbRow,
} from '../canon/store.js'
import {
  calculateCalibrationCurve,
  type CalibrationCurveResult,
  type PredictionRecord,
} from '../analysis/calibration.js'
import { compareHarnesses } from '../analysis/compare.js'
import type { Finding } from '../analysis/findings.js'
import {
  CANONICAL_CONTENT_KEYS,
  type CanonicalContent,
  type CanonicalContentKey,
  type CanonicalRecord,
  type ContentPart,
  type CostBlock,
  type RunRow,
  type ExecutionRow,
  type ExecutionTreeNode,
  type HarnessRollupRow,
} from '../canon/types.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}
type DatabaseSync = import('node:sqlite').DatabaseSync

export type MetricAvailability = 'measured' | 'derived' | 'not_measurable'
export type MetricKind = 'per_turn' | 'total'
export type MetricUnit = 'tokens' | 'share' | 'currency' | 'count'

export type MetricCell = {
  measurable: boolean
  availability: MetricAvailability
  value?: number
  basis?: string
  currency?: string
  render: string
}

export type MetricRow = {
  metric: string
  kind: MetricKind
  label: string
  unit: MetricUnit
  cells: Record<string, MetricCell>
}

export type ComparisonTableResult = {
  harnesses: string[]
  rows: MetricRow[]
  problems: Array<{ severity: string; code: string; message: string }>
}

export type SessionSummary = {
  session_id: string
  harness: string
  label: string | null
  is_subagent: boolean
  parent_session: string | null
  agent_name: string | null
  repo: string | null
  branch: string | null
  started: string | null
  ended: string | null
  turn_count: number | null
  request_count: number | null
  total_input: number | null
  total_output: number | null
  cost_usd: number | null
  models: string[]
  problems: number
}

export type QuarantineRow = {
  span_id: string
  source: string | null
  name: string | null
  namespaces: string | null
  reason: string | null
  seen_at: number | string | null
}

export type ProblemRow = {
  id: number | string
  session_id: string | null
  span_id: string | null
  severity: string
  code: string
  message: string
  at: number | string | null
  harness: string | null
}

export type ParsedSummary = {
  turn_count?: number | null
  request_count?: number | null
  total_input?: number | null
  total_output?: number | null
  total_cache_read?: number | null
  total_cache_creation?: number | null
  schema_tokens_per_turn?: number | null
  cost?: {
    usd?: number | null
    basis?: string | null
    status?: string | null
  } | null
  models?: string[] | null
}

export type SessionPayload = Record<string, unknown> & {
  id?: string
  harness?: string
  summary?: ParsedSummary
  turns?: unknown[]
  problems?: unknown[]
}

export type KyberMetaResult = {
  span_count: number
  quarantined: number
  tokenizer: {
    kind: string
    note: string
  }
  rates: {
    credit_usd: number | null
    source: string | null
    retrieved: string | null
    note: string | null
  }
  harnesses: Record<string, unknown>
  sources: Array<{ origin: string; seen: number; new: number }>
}

export type KyberBridgeOptions = {
  canonPath?: string
  ratesPath?: string
  canonDb?: DatabaseSync
  /**
   * When present, content drill-down reads through `recordsForSession` / `get`
   * instead of issuing its own SQL. Tests inject an in-memory store this way;
   * production falls back to the already-open `canonDb` handle.
   */
  store?: CanonStore
}

/**
 * Unclipped inspector payload. `_clip` stays on the session list and the
 * session payload — those are summaries. This shape is what a band click
 * reads so a 11,000-character system prompt is the real text, not a 2,000
 * character stub.
 *
 * `{ sessionId, spanId?, parts: [{ spanId, part, text, tokens?, server?, truncated?, totalLength? }] }`
 *
 * `spanId` is present only when the caller asked for one span. `tokens` and
 * `server` are omitted when the store did not have them — absent is not zero.
 * `truncated` / `totalLength` appear only when this response hit the
 * per-response budget; silent truncation is the defect this route exists to
 * fix.
 */
export type SessionContentPart = {
  spanId: string
  part: CanonicalContentKey
  text: string
  tokens?: number
  server?: string
  truncated?: boolean
  totalLength?: number
}

export type SessionContentResult = {
  sessionId: string
  spanId?: string
  parts: SessionContentPart[]
}

export type SessionContentOptions = {
  spanId?: string
  part?: string
}

export type TurnContentPart = {
  id?: string
  spanId?: string
  part: string
  label: string
  text: string
  tokens?: number
  server?: string
  truncated?: boolean
  totalLength?: number
  order?: number
}

export type TurnContentBlock = {
  key: string
  label: string
  tokens?: number
  parts: TurnContentPart[]
  text: string
  truncated?: boolean
  totalLength?: number
  notMeasurable?: { reason: string }
}

export type TurnContentResult = {
  sessionId: string
  turnIndex: number
  spanId?: string
  model?: string
  blocks: TurnContentBlock[]
  parts: TurnContentPart[]
  assembledText: string
  truncated?: boolean
  totalLength?: number
}

/**
 * Generous ceiling for one unclipped content response (a few megabytes of
 * characters). A single system prompt fits; a 7,000-turn dump does not get
 * serialized whole. When a part does not fit, it is cut and flagged — never
 * silently shortened.
 */
export const CONTENT_RESPONSE_BUDGET = 2_000_000

interface SessionDbRow {
  session_id: string
  harness: string
  label?: string | null
  is_subagent?: number | boolean | null
  parent_session?: string | null
  agent_name?: string | null
  repo?: string | null
  branch?: string | null
  started?: string | null
  ended?: string | null
  summary_json?: string | object | null
  problems_count?: number | null
  payload?: string | null
}

interface TraceRecordRow {
  session_key: string
  harness: string
  source?: string | null
  name?: string | null
  started?: string | null
  ended?: string | null
  span_count?: number | null
  turn_count?: number | null
  request_count?: number | null
  tokens_json?: string | null
  cost_json?: string | null
}

interface QuarantineDbRow {
  span_id: string
  source?: string | null
  name?: string | null
  namespaces?: string | null
  reason?: string | null
  seen_at?: number | string | null
}

interface ProblemDbRow {
  id: number | string
  session_id?: string | null
  span_id?: string | null
  severity: string
  code: string
  message: string
  at?: number | string | null
  harness?: string | null
  location?: string | null
}

interface IngestLogRow {
  origin: string
  seen?: number | null
  new?: number | null
}

/** Maximum length for string values before leaf truncation in payloads. */
export const MAX_STRING_LENGTH = 2000

/**
 * Bounds payload size by clipping long leaf strings rather than whole structures.
 * Recursing keeps JSON shape intact so truncated tools and parts still render cleanly.
 */
export function _clip<T = unknown>(value: T, maxLen = MAX_STRING_LENGTH, depth = 0): T {
  if (typeof value === 'string') {
    return (
      value.length > maxLen
        ? value.slice(0, maxLen) + `... [truncated, ${value.length} chars]`
        : value
    ) as unknown as T
  }
  if (depth >= 8) {
    return value
  }
  if (Array.isArray(value)) {
    return value.map((item) => _clip(item, maxLen, depth + 1)) as unknown as T
  }
  if (value !== null && typeof value === 'object') {
    const res: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      res[k] = _clip(v, maxLen, depth + 1)
    }
    return res as unknown as T
  }
  return value
}

type ContentSourceRecord = {
  spanId: string
  sessionKey: string | null
  parts: ContentPart[]
}

function isCanonicalPart(value: string): value is CanonicalContentKey {
  return (CANONICAL_CONTENT_KEYS as readonly string[]).includes(value)
}

function sessionKeyOf(record: Pick<CanonicalRecord, 'sessionId' | 'traceId'>): string | null {
  return record.sessionId ?? record.traceId ?? null
}

function partsFromRecord(record: CanonicalRecord): ContentPart[] {
  if (record.parts !== undefined && record.parts.length > 0) {
    return [...record.parts].sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
  }
  const synthesized: ContentPart[] = []
  for (const key of CANONICAL_CONTENT_KEYS) {
    const text = record.content[key]
    if (typeof text === 'string' && text !== '') {
      synthesized.push({ part: key, text })
    }
  }
  return synthesized
}

function partsFromRow(row: Record<string, unknown>): ContentPart[] {
  if (row.parts_json !== null && row.parts_json !== undefined) {
    try {
      const parts = decompressRaw(row.parts_json as Uint8Array) as ContentPart[]
      if (Array.isArray(parts) && parts.length > 0) {
        return [...parts].sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
      }
    } catch {
      // Fall through to content_json — a corrupt blob is not an empty session.
    }
  }
  if (typeof row.content_json === 'string' && row.content_json !== '' && row.content_json !== '{}') {
    try {
      const content = JSON.parse(row.content_json) as CanonicalContent
      const synthesized: ContentPart[] = []
      for (const key of CANONICAL_CONTENT_KEYS) {
        const text = content[key]
        if (typeof text === 'string' && text !== '') {
          synthesized.push({ part: key, text })
        }
      }
      return synthesized
    } catch {
      return []
    }
  }
  return []
}

function toContentSource(record: CanonicalRecord): ContentSourceRecord {
  return {
    spanId: record.spanId,
    sessionKey: sessionKeyOf(record),
    parts: partsFromRecord(record),
  }
}

function applyContentBudget(
  parts: SessionContentPart[],
  budget: number,
): SessionContentPart[] {
  let remaining = budget
  return parts.map((part) => {
    const totalLength = part.text.length
    if (remaining <= 0) {
      return { ...part, text: '', truncated: true, totalLength }
    }
    if (totalLength > remaining) {
      const text = part.text.slice(0, remaining)
      remaining = 0
      return { ...part, text, truncated: true, totalLength }
    }
    remaining -= totalLength
    return part
  })
}

function extractMessagesFromHistory(text: string): { userMessages: string[]; assistantMessages: string[] } {
  const userMessages: string[] = []
  const assistantMessages: string[] = []
  if (!text) return { userMessages, assistantMessages }

  let parsed: unknown = null
  try {
    parsed = JSON.parse(text)
  } catch {
    // text is not JSON
  }

  if (Array.isArray(parsed)) {
    for (const msg of parsed) {
      if (!msg || typeof msg !== 'object') continue
      const role = String((msg as Record<string, unknown>).role || '').toLowerCase()
      let msgText = ''
      const content = (msg as Record<string, unknown>).content
      const textVal = (msg as Record<string, unknown>).text
      const partsVal = (msg as Record<string, unknown>).parts

      if (typeof content === 'string') {
        msgText = content
      } else if (typeof textVal === 'string') {
        msgText = textVal
      } else if (Array.isArray(partsVal)) {
        msgText = partsVal
          .map((pt: unknown) =>
            typeof pt === 'string'
              ? pt
              : typeof (pt as Record<string, unknown>)?.text === 'string'
                ? String((pt as Record<string, unknown>).text)
                : typeof (pt as Record<string, unknown>)?.content === 'string'
                  ? String((pt as Record<string, unknown>).content)
                  : JSON.stringify(pt),
          )
          .join('\n')
      } else if (content !== undefined) {
        msgText = JSON.stringify(content, null, 2)
      }

      if (role === 'user') {
        if (msgText.trim()) userMessages.push(msgText.trim())
      } else if (role === 'assistant' || role === 'model') {
        if (msgText.trim()) assistantMessages.push(msgText.trim())
      }
    }
  }

  if (userMessages.length === 0 && assistantMessages.length === 0) {
    // Regex matching user / assistant turns in raw dialog text
    const lines = text.split('\n')
    let currentRole: 'user' | 'assistant' | null = null
    let currentChunk: string[] = []

    for (const line of lines) {
      const userMatch = line.match(/^(?:user|human):\s*(.*)$/i)
      const asstMatch = line.match(/^(?:assistant|model|bot):\s*(.*)$/i)

      if (userMatch) {
        if (currentRole === 'user' && currentChunk.length > 0) userMessages.push(currentChunk.join('\n').trim())
        if (currentRole === 'assistant' && currentChunk.length > 0) assistantMessages.push(currentChunk.join('\n').trim())
        currentRole = 'user'
        currentChunk = userMatch[1] ? [userMatch[1]] : []
      } else if (asstMatch) {
        if (currentRole === 'user' && currentChunk.length > 0) userMessages.push(currentChunk.join('\n').trim())
        if (currentRole === 'assistant' && currentChunk.length > 0) assistantMessages.push(currentChunk.join('\n').trim())
        currentRole = 'assistant'
        currentChunk = asstMatch[1] ? [asstMatch[1]] : []
      } else if (currentRole) {
        currentChunk.push(line)
      }
    }
    if (currentRole === 'user' && currentChunk.length > 0) userMessages.push(currentChunk.join('\n').trim())
    if (currentRole === 'assistant' && currentChunk.length > 0) assistantMessages.push(currentChunk.join('\n').trim())
  }

  return { userMessages, assistantMessages }
}

export class KyberBridge {
  private canonDb?: DatabaseSync
  private readonly store?: CanonStore
  readonly canonPath: string
  readonly ratesPath: string | undefined

  constructor(options?: KyberBridgeOptions) {
    this.canonPath =
      options?.canonPath ??
      process.env.KYBER_CANON_DB ??
      join(homedir(), '.kyberdash', 'canon.db')

    this.ratesPath = options?.ratesPath

    this.store = options?.store

    if (options?.canonDb) {
      this.canonDb = options.canonDb
      try {
        this.canonDb.exec('PRAGMA busy_timeout = 5000')
      } catch {}
    } else {
      this.canonDb = this.openDb(this.canonPath)
    }

  }

  private openDb(filePath: string): DatabaseSync | undefined {
    if (filePath !== ':memory:' && !existsSync(filePath)) {
      return undefined
    }
    try {
      const isMemory = filePath === ':memory:'
      const db = new DatabaseSync(
        filePath,
        isMemory ? { open: true } : { open: true, readOnly: true }
      )
      db.exec('PRAGMA busy_timeout = 5000')
      return db
    } catch (err) {
      console.warn(`[KyberBridge] Failed to open SQLite database at ${filePath}:`, err)
      return undefined
    }
  }

  private hasTable(db: DatabaseSync | undefined, tableName: string): boolean {
    if (!db) return false
    try {
      const row = db
        .prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name = ?")
        .get(tableName)
      return Boolean(row)
    } catch {
      return false
    }
  }

  /**
   * Close open SQLite database handles.
   */
  close(): void {
    try {
      this.canonDb?.close()
    } catch {}
    this.canonDb = undefined
  }

  /**
   * List all available sessions from the canonical store, sorted by started DESC.
   */
  listSessions(limit?: number): SessionSummary[] {
    const list: SessionSummary[] = []
    const seenIds = new Set<string>()

    // 1. Query primary canon.db `session` table if available
    if (this.hasTable(this.canonDb, 'session')) {
      try {
        let rows: SessionDbRow[] = []
        try {
          // Fast path: use json_extract so we don't pull large payload blobs across the bridge
          rows = this.canonDb!
            .prepare(
              'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, ' +
                "json_extract(payload, '$.summary') as summary_json, " +
                "json_array_length(json_extract(payload, '$.problems')) as problems_count " +
                'FROM session ORDER BY started DESC'
            )
            .all() as unknown as SessionDbRow[]
        } catch {
          // Fallback if SQLite json functions are unavailable
          rows = this.canonDb!
            .prepare(
              'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload ' +
                'FROM session ORDER BY started DESC'
            )
            .all() as unknown as SessionDbRow[]
        }

        for (const row of rows) {
          seenIds.add(row.session_id)
          let summ: ParsedSummary | null = null
          let problemsCount = 0

          if (row.summary_json !== undefined && row.summary_json !== null) {
            if (typeof row.summary_json === 'string') {
              try {
                summ = JSON.parse(row.summary_json) as ParsedSummary
              } catch {}
            } else if (typeof row.summary_json === 'object') {
              summ = row.summary_json as ParsedSummary
            }
            problemsCount = Number(row.problems_count) || 0
          } else if (typeof row.payload === 'string') {
            try {
              const p = JSON.parse(row.payload) as { summary?: ParsedSummary; problems?: unknown[] }
              summ = p?.summary ?? null
              problemsCount = Array.isArray(p?.problems) ? p.problems.length : 0
            } catch {}
          }

          summ = summ ?? {}
          list.push({
            session_id: row.session_id,
            harness: row.harness,
            label: row.label ?? null,
            is_subagent: Boolean(row.is_subagent),
            parent_session: row.parent_session ?? null,
            agent_name: row.agent_name ?? null,
            repo: row.repo ?? null,
            branch: row.branch ?? null,
            started: row.started ?? null,
            ended: row.ended ?? null,
            turn_count: summ.turn_count ?? null,
            request_count: summ.request_count ?? null,
            total_input: summ.total_input ?? null,
            total_output: summ.total_output ?? null,
            cost_usd: summ.cost?.usd ?? null,
            models: Array.isArray(summ.models) ? summ.models : [],
            problems: problemsCount,
          })
        }
      } catch (err) {
        console.warn('[KyberBridge] Failed querying session table in canon.db:', err)
      }
    }

    // 2. Query canon.db `records` table for sessions not yet in list.
    // Groups by COALESCE(session_id, trace_id) + harness (the session identity).
    // Tokens and cost are aggregated in TypeScript over parsed JSON columns so
    // that the cost-basis blending rule (sumCosts) is enforced without SQL.
    if (this.hasTable(this.canonDb, 'records')) {
      try {
        // Try the full query with session_id, tokens_json, cost_json columns
        // (schema v3+). Fall back to a minimal query for older schemas.
        let spanRows: TraceRecordRow[] = []
        try {
          spanRows = this.canonDb!
            .prepare(
              'SELECT COALESCE(session_id, trace_id) as session_key, harness, source, name, ' +
                'timestamp as started, timestamp as ended, ' +
                'op, parent_span_id, tokens_json, cost_json ' +
                'FROM records WHERE COALESCE(session_id, trace_id) IS NOT NULL ' +
                'ORDER BY timestamp ASC'
            )
            .all() as unknown as TraceRecordRow[]
        } catch {
          // Older schema without session_id or tokens_json/cost_json
          spanRows = this.canonDb!
            .prepare(
              'SELECT trace_id as session_key, harness, source, name, ' +
                'timestamp as started, timestamp as ended, ' +
                'op, parent_span_id ' +
                'FROM records WHERE trace_id IS NOT NULL ' +
                'ORDER BY timestamp ASC'
            )
            .all() as unknown as TraceRecordRow[]
        }

        // Group rows by (session_key, harness) in TypeScript
        type SpanRow = TraceRecordRow & {
          op?: string | null
          parent_span_id?: string | null
        }
        const groups = new Map<
          string,
          {
            harness: string
            source: string | null
            name: string | null
            started: string | null
            ended: string | null
            spanCount: number
            turnCount: number
            requestCount: number
            costBlocks: CostBlock[]
            totalInput: number
            totalOutput: number
            totalCacheRead: number
            totalCacheCreation: number
            models: Set<string>
          }
        >()

        for (const row of spanRows as SpanRow[]) {
          const key = `${row.session_key}\0${row.harness}`
          const existing = groups.get(key)
          const isLlmInvoke = row.op === 'llm.invoke'
          const isRoot = row.parent_span_id == null

          // Parse tokens
          let freshInput = 0
          let cacheRead = 0
          let cacheCreation = 0
          let output = 0
          let modelStr: string | null = null
          if (row.tokens_json) {
            try {
              const tok = JSON.parse(row.tokens_json) as {
                freshInput?: number
                cacheRead?: number
                cacheCreation?: number
                output?: number
                reportedModel?: string
              }
              freshInput = Number(tok.freshInput) || 0
              cacheRead = Number(tok.cacheRead) || 0
              cacheCreation = Number(tok.cacheCreation) || 0
              output = Number(tok.output) || 0
              modelStr = tok.reportedModel ?? null
            } catch {}
          }

          // Parse cost block
          let costBlock: CostBlock | null = null
          if (row.cost_json) {
            try {
              costBlock = JSON.parse(row.cost_json) as CostBlock
            } catch {}
          }

          if (!existing) {
            groups.set(key, {
              harness: row.harness,
              source: row.source ?? null,
              name: row.name ?? null,
              started: row.started ?? null,
              ended: row.ended ?? null,
              spanCount: 1,
              turnCount: isLlmInvoke ? 1 : 0,
              requestCount: isRoot ? 1 : 0,
              costBlocks: costBlock ? [costBlock] : [],
              totalInput: freshInput + cacheRead + cacheCreation,
              totalOutput: output,
              totalCacheRead: cacheRead,
              totalCacheCreation: cacheCreation,
              models: modelStr ? new Set([modelStr]) : new Set(),
            })
          } else {
            existing.spanCount += 1
            if (isLlmInvoke) existing.turnCount += 1
            if (isRoot) existing.requestCount += 1
            if (costBlock) existing.costBlocks.push(costBlock)
            existing.totalInput += freshInput + cacheRead + cacheCreation
            existing.totalOutput += output
            existing.totalCacheRead += cacheRead
            existing.totalCacheCreation += cacheCreation
            if (row.ended && (existing.ended == null || row.ended > existing.ended)) {
              existing.ended = row.ended
            }
            if (modelStr) existing.models.add(modelStr)
          }
        }

        for (const [key, group] of groups) {
          const sessionKey = key.split('\0')[0]!
          if (seenIds.has(sessionKey)) continue
          seenIds.add(sessionKey)

          // Use sumCosts to respect cost-basis blending rules
          const costResult = sumCosts(group.costBlocks)
          const costUsd =
            costResult.ok && costResult.total.status !== 'no_rate' &&
            typeof costResult.total.value === 'number'
              ? costResult.total.value
              : null

          list.push({
            session_id: sessionKey,
            harness: group.harness,
            label: group.name || `${group.harness} session`,
            is_subagent: false,
            parent_session: null,
            agent_name: group.source || group.harness,
            repo: null,
            branch: null,
            started: group.started ?? null,
            ended: group.ended ?? null,
            turn_count: group.turnCount || group.spanCount || null,
            request_count: group.requestCount || 1,
            total_input: group.totalInput || null,
            total_output: group.totalOutput || null,
            cost_usd: costUsd,
            models: [...group.models],
            problems: 0,
          })
        }
      } catch (err) {
        console.warn('[KyberBridge] Failed querying records table in canon.db:', err)
      }
    }

    // Sort globally by started DESC
    list.sort((a, b) => (b.started ?? '').localeCompare(a.started ?? ''))

    if (typeof limit === 'number' && limit > 0) {
      return list.slice(0, Math.floor(limit))
    }
    return list
  }

  /**
   * Get the full precomputed view payload for a given session ID.
   * The canonical session payload is returned without route-level adaptation.
   * Strings longer than maxLen are safely truncated to prevent oversized JSON responses.
   */
  getSessionPayload<T = SessionPayload>(sessionId: string): T | null {
    if (!sessionId) return null

    if (this.store) {
      const payload = this.store.getSessionPayload(sessionId)
      return payload === undefined ? null : (_clip(payload) as T)
    }

    if (this.hasTable(this.canonDb, 'session')) {
      try {
        const row = this.canonDb!
          .prepare('SELECT payload FROM session WHERE session_id = ?')
          .get(sessionId) as { payload: string } | undefined
        if (row && typeof row.payload === 'string') {
          const parsed = JSON.parse(row.payload) as unknown
          return _clip(parsed) as T
        }
      } catch (err) {
        console.warn(
          `[KyberBridge] Error reading session payload from canon.db for ${sessionId}:`,
          err
        )
      }
    }

    return null
  }

  /**
   * One span's harness-emitted attributes, for the timeline inspector (R9.2).
   *
   * The timeline nodes in a session payload no longer carry their attribute
   * map: it is the record's raw span payload, and copying it into every
   * session made the derived cache a second, uncompressed copy of the whole
   * corpus. The inspector asks for the node it is showing instead, and pays one
   * primary-key seek and one inflate for it.
   *
   * `_clip` is deliberately not applied, for the same reason it is not applied
   * to unclipped content: this route exists so a span click can show what the
   * harness actually emitted.
   */
  getSpanAttributes(spanId: string): { spanId: string; attributes: Record<string, unknown> } | null {
    if (!spanId) return null

    if (this.store) {
      const attributes = this.store.spanAttributes(spanId)
      return attributes === undefined ? null : { spanId, attributes }
    }

    if (this.hasTable(this.canonDb, 'records')) {
      try {
        const row = this.canonDb!
          .prepare('SELECT raw FROM records WHERE span_id = ?')
          .get(spanId) as { raw: unknown } | undefined
        if (row && row.raw !== null && row.raw !== undefined) {
          const parsed = JSON.parse(
            inflateSync(Buffer.from(row.raw as Uint8Array)).toString('utf8'),
          ) as unknown
          if (parsed !== null && typeof parsed === 'object') {
            return { spanId, attributes: parsed as Record<string, unknown> }
          }
        }
      } catch (err) {
        console.warn(`[KyberBridge] Error reading span attributes for ${spanId}:`, err)
      }
    }

    return null
  }

  /**
   * Unclipped content for the inspector. `_clip` is deliberately not applied:
   * this is the route that exists so a band click can show the real prompt.
   * Prefer `CanonStore.recordsForSession` / `get` when a store is injected;
   * otherwise the same lookup runs over the already-open `canonDb` handle
   * (opening a second CanonStore on the live file would migrate it).
   */
  getSessionContent(
    sessionId: string,
    options: SessionContentOptions = {},
  ): SessionContentResult | null {
    if (!sessionId) return null

    // An unknown part is a bad filter, not a successful empty response.
    if (options.part !== undefined && options.part !== '' && !isCanonicalPart(options.part)) {
      return null
    }

    const records = this.loadContentRecords(sessionId, options.spanId)
    if (records.length === 0 && !this.sessionKnown(sessionId)) {
      return null
    }
    // A known session does not make an arbitrary span valid.
    if (options.spanId !== undefined && options.spanId !== '' && records.length === 0) {
      return null
    }

    const partFilter =
      options.part !== undefined && options.part !== '' && isCanonicalPart(options.part)
        ? options.part
        : undefined

    const assembled: SessionContentPart[] = []
    for (const record of records) {
      for (const piece of record.parts) {
        if (partFilter !== undefined && piece.part !== partFilter) continue
        const entry: SessionContentPart = {
          spanId: record.spanId,
          part: piece.part,
          text: piece.text,
        }
        if (piece.tokens !== undefined) entry.tokens = piece.tokens
        if (piece.server !== undefined) entry.server = piece.server
        assembled.push(entry)
      }
    }
    // Canonical-but-absent is also an invalid part filter for this session/span.
    if (partFilter !== undefined && assembled.length === 0) return null

    const result: SessionContentResult = {
      sessionId,
      parts: applyContentBudget(assembled, CONTENT_RESPONSE_BUDGET),
    }
    if (options.spanId) result.spanId = options.spanId
    return result
  }

  /**
   * Unclipped assembled turn content for the Context Inspector (Task G1 / Decision D14).
   * Retrieves all blocks and parts for the given turn index (0-indexed or 1-indexed fallback),
   * sub-divided into canonical context blocks (system_prompt, tool_definitions, instruction_context,
   * conversation_history, tool_result_content) and parts (including user_messages, assistant_turns, etc.).
   */
  assembleTurnContent(
    sessionId: string,
    turnIndex: number,
    budget = CONTENT_RESPONSE_BUDGET,
  ): TurnContentResult | null {
    if (!sessionId || turnIndex < 0 || isNaN(turnIndex)) return null
    if (!this.sessionKnown(sessionId)) return null

    let targetSpanId: string | undefined
    let model: string | undefined

    // 1. Try resolving via session payload if available
    const payload = this.getSessionPayload<SessionPayload>(sessionId) as any
    if (payload && Array.isArray(payload.turns) && payload.turns.length > 0) {
      const turnItem =
        payload.turns.find((t: any, i: number) => t.index === turnIndex || i === turnIndex) ??
        (turnIndex >= 1 && turnIndex <= payload.turns.length ? payload.turns[turnIndex - 1] : undefined)
      if (turnItem) {
        if (typeof turnItem.spanId === 'string') targetSpanId = turnItem.spanId
        if (typeof turnItem.model === 'string') model = turnItem.model
      }
    }

    // 2. If targetSpanId not resolved yet, search canonical records
    if (!targetSpanId) {
      if (this.store) {
        const records = this.store.recordsForSession(sessionId)
        const turnRecords = records.filter((r) => r.op === 'llm.invoke')
        const pool = turnRecords.length > 0 ? turnRecords : records
        const target =
          pool.find((r, i) => (r as any).index === turnIndex || i === turnIndex) ??
          (turnIndex >= 1 && turnIndex <= pool.length ? pool[turnIndex - 1] : undefined)
        if (target) {
          targetSpanId = target.spanId
          model = (target as any).model ?? target.name
        }
      } else if (this.hasTable(this.canonDb, 'records')) {
        try {
          const rows = this.canonDb!
            .prepare('SELECT * FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp')
            .all(sessionId) as Record<string, unknown>[]
          const turnRows = rows.filter((r) => r.op === 'llm.invoke')
          const pool = turnRows.length > 0 ? turnRows : rows
          const target =
            pool.find((r, i) => Number(r.index) === turnIndex || i === turnIndex) ??
            (turnIndex >= 1 && turnIndex <= pool.length ? pool[turnIndex - 1] : undefined)
          if (target) {
            targetSpanId = String(target.span_id)
            model = String(target.name || '')
          }
        } catch {
          // ignore
        }
      }
    }

    if (!targetSpanId) return null

    // Load unclipped content records for this span
    const contentRecords = this.loadContentRecords(sessionId, targetSpanId)
    const rawParts: ContentPart[] = contentRecords[0]?.parts ?? []

    // Map and extract structured parts (system prompt, tools, user messages, assistant turns, etc.)
    const parts: TurnContentPart[] = []
    let orderCounter = 0

    for (const p of rawParts) {
      if (p.part === 'system_prompt') {
        parts.push({
          id: `part-sys-${orderCounter++}`,
          spanId: targetSpanId,
          part: 'system_prompt',
          label: 'System Prompt',
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      } else if (p.part === 'tool_definitions') {
        const label = p.server ? `Tools (${p.server})` : 'Tools'
        parts.push({
          id: `part-tool-${orderCounter++}`,
          spanId: targetSpanId,
          part: 'tool_definitions',
          label,
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      } else if (p.part === 'instruction_context') {
        parts.push({
          id: `part-inst-${orderCounter++}`,
          spanId: targetSpanId,
          part: 'instruction_context',
          label: 'Instruction Context',
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      } else if (p.part === 'tool_result_content') {
        parts.push({
          id: `part-toolres-${orderCounter++}`,
          spanId: targetSpanId,
          part: 'tool_result_content',
          label: 'Tool Results',
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      } else if (p.part === 'conversation_history') {
        const { userMessages, assistantMessages } = extractMessagesFromHistory(p.text)
        if (userMessages.length > 0 || assistantMessages.length > 0) {
          if (userMessages.length > 0) {
            parts.push({
              id: `part-user-${orderCounter++}`,
              spanId: targetSpanId,
              part: 'user_messages',
              label: 'User Messages',
              text: userMessages.join('\n\n'),
              tokens: p.tokens ? Math.round(p.tokens * (userMessages.length / (userMessages.length + assistantMessages.length))) : undefined,
              order: p.order ?? orderCounter,
            })
          }
          if (assistantMessages.length > 0) {
            parts.push({
              id: `part-asst-${orderCounter++}`,
              spanId: targetSpanId,
              part: 'assistant_turns',
              label: 'Assistant Turns',
              text: assistantMessages.join('\n\n'),
              tokens: p.tokens ? Math.round(p.tokens * (assistantMessages.length / (userMessages.length + assistantMessages.length))) : undefined,
              order: p.order ?? orderCounter,
            })
          }
        } else {
          // Generic conversation history
          parts.push({
            id: `part-user-${orderCounter++}`,
            spanId: targetSpanId,
            part: 'user_messages',
            label: 'User Messages',
            text: p.text,
            tokens: p.tokens,
            order: p.order ?? orderCounter,
          })
          parts.push({
            id: `part-conv-${orderCounter++}`,
            spanId: targetSpanId,
            part: 'conversation_history',
            label: 'Conversation History',
            text: p.text,
            tokens: p.tokens,
            order: p.order ?? orderCounter,
          })
        }
      } else {
        parts.push({
          id: `part-other-${orderCounter++}`,
          spanId: targetSpanId,
          part: p.part,
          label: String(p.part).replace(/_/g, ' '),
          text: p.text,
          tokens: p.tokens,
          server: p.server,
          order: p.order ?? orderCounter,
        })
      }
    }

    // Canonical context blocks
    const blockDefs: Array<{ key: string; label: string; partKeys: string[] }> = [
      { key: 'system_prompt', label: 'System prompt', partKeys: ['system_prompt'] },
      { key: 'tool_definitions', label: 'Tools', partKeys: ['tool_definitions'] },
      { key: 'instruction_context', label: 'Instruction context', partKeys: ['instruction_context'] },
      { key: 'conversation_history', label: 'Conversation history', partKeys: ['conversation_history', 'user_messages', 'assistant_turns'] },
      { key: 'tool_result_content', label: 'Tool results', partKeys: ['tool_result_content'] },
    ]

    const blocks: TurnContentBlock[] = []
    for (const def of blockDefs) {
      const matchingParts = parts.filter((p) => def.partKeys.includes(p.part))
      // Filter out redundant conversation_history if user_messages / assistant_turns is already present
      const uniqueParts = matchingParts.filter((p, idx, arr) => {
        if (p.part === 'conversation_history' && arr.some((other) => other.part === 'user_messages' && other.text === p.text)) {
          return false
        }
        return true
      })
      const text = uniqueParts.map((p) => p.text).join('\n\n')
      const tokens = uniqueParts.reduce((sum, p) => sum + (p.tokens ?? 0), 0) || undefined

      const block: TurnContentBlock = {
        key: def.key,
        label: def.label,
        parts: uniqueParts,
        text,
        tokens,
      }

      // Check measurability from payload
      const measurability = payload?.measurability?.[def.key] ?? payload?.context?.first?.buckets?.[def.key]
      if (measurability && typeof measurability === 'object' && measurability.availability === 'not_measurable') {
        block.notMeasurable = { reason: measurability.reason || 'Not measurable for this harness.' }
      }

      blocks.push(block)
    }

    // Assemble whole-turn plain text with clear headings per block
    const sectionTexts: string[] = []
    for (const b of blocks) {
      if (b.text.trim()) {
        sectionTexts.push(`=== ${b.label.toUpperCase()} ===\n${b.text.trim()}`)
      }
    }
    let assembledText = sectionTexts.join('\n\n')
    let truncated = false
    let totalLength: number | undefined

    if (assembledText.length > budget) {
      totalLength = assembledText.length
      assembledText = assembledText.slice(0, budget)
      truncated = true
    }

    // Apply budget to individual blocks and parts
    for (const block of blocks) {
      if (block.text.length > budget) {
        block.totalLength = block.text.length
        block.text = block.text.slice(0, budget)
        block.truncated = true
      }
      for (const part of block.parts) {
        if (part.text.length > budget) {
          part.totalLength = part.text.length
          part.text = part.text.slice(0, budget)
          part.truncated = true
        }
      }
    }
    for (const part of parts) {
      if (part.text.length > budget) {
        part.totalLength = part.text.length
        part.text = part.text.slice(0, budget)
        part.truncated = true
      }
    }

    const result: TurnContentResult = {
      sessionId,
      turnIndex,
      spanId: targetSpanId,
      model,
      blocks,
      parts,
      assembledText,
      ...(truncated ? { truncated, totalLength } : {}),
    }

    return result
  }


  /**
   * A session is known if a derived row exists or any record keys to it.
   * The session table is cheap; records are the fallback for a corpus that
   * has not been built into `session` yet. Checking the table first avoids
   * clipping a large payload just to decide whether to 404.
   */
  private sessionKnown(sessionId: string): boolean {
    if (this.store?.getSessionPayload(sessionId) !== undefined) return true
    if (this.hasTable(this.canonDb, 'session')) {
      try {
        const row = this.canonDb!.prepare('SELECT 1 FROM session WHERE session_id = ?').get(sessionId)
        if (row) return true
      } catch {
        // Older or partial schemas still fall through to the records check.
      }
    }
    // Records without a derived session row still make the session real —
    // otherwise a span miss on an unbuilt session would 404 as "unknown".
    if (this.store) return this.store.recordsForSession(sessionId).length > 0
    return this.loadContentRecordsFromDb(sessionId).length > 0
  }

  /**
   * `get` when a span is named so a 7,000-turn session is not decompressed
   * just to return one band; `recordsForSession` otherwise.
   */
  private loadContentRecords(sessionId: string, spanId?: string): ContentSourceRecord[] {
    if (this.store) {
      if (spanId) {
        const record = this.store.get(spanId)
        if (record === undefined) return []
        if (sessionKeyOf(record) !== sessionId) return []
        return [toContentSource(record)]
      }
      return this.store.recordsForSession(sessionId).map(toContentSource)
    }
    return this.loadContentRecordsFromDb(sessionId, spanId)
  }

  private loadContentRecordsFromDb(sessionId: string, spanId?: string): ContentSourceRecord[] {
    if (!this.hasTable(this.canonDb, 'records')) return []

    if (spanId) {
      try {
        const row = this.canonDb!
          .prepare('SELECT * FROM records WHERE span_id = ?')
          .get(spanId) as Record<string, unknown> | undefined
        if (row === undefined) return []
        const key =
          row.session_id !== null && row.session_id !== undefined
            ? String(row.session_id)
            : row.trace_id !== null && row.trace_id !== undefined
              ? String(row.trace_id)
              : null
        if (key !== sessionId) return []
        return [
          {
            spanId: String(row.span_id),
            sessionKey: key,
            parts: partsFromRow(row),
          },
        ]
      } catch {
        return []
      }
    }

    try {
      const rows = this.canonDb!
        .prepare(
          'SELECT * FROM records WHERE COALESCE(session_id, trace_id) = ? ORDER BY timestamp',
        )
        .all(sessionId) as Record<string, unknown>[]
      return rows.map((row) => ({
        spanId: String(row.span_id),
        sessionKey: sessionId,
        parts: partsFromRow(row),
      }))
    } catch {
      try {
        const rows = this.canonDb!
          .prepare('SELECT * FROM records WHERE trace_id = ? ORDER BY timestamp')
          .all(sessionId) as Record<string, unknown>[]
        return rows.map((row) => ({
          spanId: String(row.span_id),
          sessionKey: sessionId,
          parts: partsFromRow(row),
        }))
      } catch {
        return []
      }
    }
  }

  /**
   * Return cross-harness comparison matrix across all active harnesses.
   */
  private canonicalRecords(): CanonicalRecord[] {
    if (this.store) return this.store.listAll()
    if (!this.hasTable(this.canonDb, 'records')) return []
    try {
      const rows = this.canonDb!
        .prepare('SELECT * FROM records ORDER BY timestamp')
        .all() as unknown as import('../canon/store.js').RecordRow[]
      return rows.map(toRecord)
    } catch {
      return []
    }
  }

  /**
   * Return cross-harness comparison matrix derived only from canonical records.
   */
  getComparisonTable(): ComparisonTableResult {
    const canonicalRecords = this.canonicalRecords()
    if (canonicalRecords.length === 0) {
      return { harnesses: [], rows: [], problems: [] }
    }

    const byHarness = new Map<string, CanonicalRecord[]>()
    for (const record of canonicalRecords) {
      const records = byHarness.get(record.harness) ?? []
      records.push(record)
      byHarness.set(record.harness, records)
    }

    const preferredOrder = ['copilot', 'gemini', 'pi']
    const availableHarnesses = [...byHarness.keys()]
    const harnesses = [
      ...preferredOrder.filter((harness) => byHarness.has(harness)),
      ...availableHarnesses
        .filter((harness) => !preferredOrder.includes(harness))
        .sort(),
    ]
    const table = compareHarnesses(
      harnesses.map((harness) => byHarness.get(harness) ?? []),
      harnesses,
    )
    return {
      harnesses: table.harnesses,
      problems: table.problems,
      rows: table.rows.map((row) => ({
        ...row,
        cells: Object.fromEntries(
          Object.entries(row.cells).map(([harness, cell]) => [
            harness,
            {
              ...cell,
              availability: typeof cell.availability === 'object' ? cell.availability.availability : cell.availability,
            },
          ]),
        ),
      })),
    }
  }

  getQuarantine(limit = 200): QuarantineRow[] {
    const results: QuarantineRow[] = []
    const seenSpanIds = new Set<string>()

    // 1. Primary: canon.db
    if (this.hasTable(this.canonDb, 'quarantine')) {
      try {
        let rows: QuarantineDbRow[] = []
        try {
          rows = this.canonDb!
            .prepare(
              'SELECT span_id, source, name, namespaces, reason, seen_at ' +
                'FROM quarantine ORDER BY seen_at DESC'
            )
            .all() as unknown as QuarantineDbRow[]
        } catch {
          rows = this.canonDb!
            .prepare(
              'SELECT span_id, namespaces, reason FROM quarantine ORDER BY span_id'
            )
            .all() as unknown as QuarantineDbRow[]
        }
        for (const r of rows) {
          if (!r.span_id || seenSpanIds.has(r.span_id)) continue
          seenSpanIds.add(r.span_id)
          results.push({
            span_id: r.span_id,
            source: r.source ?? null,
            name: r.name ?? null,
            namespaces: r.namespaces ?? null,
            reason: r.reason ?? null,
            seen_at: r.seen_at ?? null,
          })
        }
      } catch (err) {
        console.warn('[KyberBridge] Failed querying quarantine from canon.db:', err)
      }
    }

    if (typeof limit === 'number' && limit > 0) {
      return results.slice(0, Math.floor(limit))
    }
    return results
  }

  /**
   * Return recorded validation errors, token reconciliation mismatches, and anomalies.
   * Reads canonical diagnostics, deduplicated by span_id/id.
   */
  getProblems(limit = 200): ProblemRow[] {
    const results: ProblemRow[] = []
    const seenKeys = new Set<string>()

    // 1. Primary: canon.db ('problem' or 'problems' table)
    const canonTable = this.hasTable(this.canonDb, 'problem')
      ? 'problem'
      : this.hasTable(this.canonDb, 'problems')
        ? 'problems'
        : null

    if (canonTable) {
      try {
        let rows: ProblemDbRow[] = []
        try {
          rows = this.canonDb!
            .prepare(
              `SELECT id, session_id, span_id, severity, code, message, at, harness FROM ${canonTable} ORDER BY id DESC`
            )
            .all() as unknown as ProblemDbRow[]
        } catch {
          rows = this.canonDb!
            .prepare(
              `SELECT id, span_id, severity, code, message, location FROM ${canonTable} ORDER BY id DESC`
            )
            .all() as unknown as ProblemDbRow[]
        }

        for (const r of rows) {
          const key = r.span_id ? `span:${r.span_id}:${r.code}` : `id:${r.id}`
          if (seenKeys.has(key)) continue
          seenKeys.add(key)
          results.push({
            id: r.id,
            session_id: r.session_id ?? null,
            span_id: r.span_id ?? null,
            severity: r.severity,
            code: r.code,
            message: r.message,
            at: r.at ?? null,
            harness: r.harness ?? r.location ?? null,
          })
        }
      } catch (err) {
        console.warn('[KyberBridge] Failed querying problems from canon.db:', err)
      }
    }

    if (typeof limit === 'number' && limit > 0) {
      return results.slice(0, Math.floor(limit))
    }
    return results
  }

  /**
   * List ranked findings optionally filtered by runId or sessionId.
   */
  listFindings(options?: { runId?: string; sessionId?: string; limit?: number }): Finding[] {
    if (this.store) {
      const findings = this.store.listFindings(options?.runId, options?.sessionId)
      if (typeof options?.limit === 'number' && options.limit > 0) {
        return findings.slice(0, Math.floor(options.limit))
      }
      return findings
    }

    if (this.hasTable(this.canonDb, 'finding')) {
      try {
        let sql = 'SELECT * FROM finding'
        const params: unknown[] = []
        const conds: string[] = []
        if (options?.runId) {
          conds.push('run_id = ?')
          params.push(options.runId)
        }
        if (options?.sessionId) {
          conds.push('session_id = ?')
          params.push(options.sessionId)
        }
        if (conds.length > 0) {
          sql += ' WHERE ' + conds.join(' AND ')
        }
        sql += ' ORDER BY rank_score DESC, id ASC'
        if (typeof options?.limit === 'number' && options.limit > 0) {
          sql += ' LIMIT ?'
          params.push(Math.floor(options.limit))
        }
        const rows = this.canonDb!.prepare(sql).all(...(params as (string | number)[])) as unknown as FindingDbRow[]
        return rows.map(toFinding)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying findings from canon.db:', err)
        return []
      }
    }

    return []
  }

  /**
   * Fetch one finding by id.
   */
  getFinding(id: string): Finding | undefined {
    if (this.store) {
      return this.store.getFinding(id)
    }

    if (this.hasTable(this.canonDb, 'finding')) {
      try {
        const row = this.canonDb!
          .prepare('SELECT * FROM finding WHERE id = ?')
          .get(id) as unknown as FindingDbRow | undefined
        return row === undefined ? undefined : toFinding(row)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying finding from canon.db:', err)
        return undefined
      }
    }

    return undefined
  }

  /**
   * List predictions, optionally filtered by runId, findingId, or scoredOnly.
   */
  listPredictions(options?: {
    runId?: string
    findingId?: string
    scoredOnly?: boolean
    limit?: number
  }): PredictionRecord[] {
    if (this.store) {
      return this.store.listPredictions(options)
    }

    if (this.hasTable(this.canonDb, 'prediction')) {
      try {
        let sql = 'SELECT * FROM prediction'
        const conds: string[] = []
        const params: (string | number)[] = []

        if (options?.runId) {
          conds.push('run_id = ?')
          params.push(options.runId)
        }
        if (options?.findingId) {
          conds.push('finding_id = ?')
          params.push(options.findingId)
        }
        if (options?.scoredOnly) {
          conds.push('comparison_run_id IS NOT NULL')
        }
        if (conds.length > 0) {
          sql += ' WHERE ' + conds.join(' AND ')
        }
        sql += ' ORDER BY created_at DESC, id ASC'
        if (typeof options?.limit === 'number' && options.limit > 0) {
          sql += ' LIMIT ?'
          params.push(Math.floor(options.limit))
        }

        const rows = this.canonDb!.prepare(sql).all(...params) as unknown as PredictionDbRow[]
        return rows.map(toPrediction)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying predictions from canon.db:', err)
        return []
      }
    }

    return []
  }

  /**
   * Fetch one prediction by id.
   */
  getPrediction(id: string): PredictionRecord | undefined {
    if (this.store) {
      return this.store.getPrediction(id)
    }

    if (this.hasTable(this.canonDb, 'prediction')) {
      try {
        const row = this.canonDb!
          .prepare('SELECT * FROM prediction WHERE id = ?')
          .get(id) as unknown as PredictionDbRow | undefined
        return row === undefined ? undefined : toPrediction(row)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying prediction from canon.db:', err)
        return undefined
      }
    }

    return undefined
  }

  /**
   * Record or update a prediction.
   */
  recordPrediction(prediction: PredictionRecord): PredictionRecord {
    if (this.store) {
      this.store.upsertPrediction(prediction)
      return prediction
    }

    if (this.canonDb) {
      const errorBar = prediction.errorBar ? JSON.stringify(prediction.errorBar) : null
      const payload = prediction.payload ? JSON.stringify(prediction.payload) : null
      const createdAt = prediction.createdAt || prediction.timestamp || new Date().toISOString()
      const id = prediction.id || `pred-${prediction.findingId}-${prediction.runId}`
      try {
        this.canonDb
          .prepare(
            `INSERT OR REPLACE INTO prediction (
              id, finding_id, run_id, predicted_waste_tokens, confidence,
              confidence_tier, error_bar_json, created_at, comparison_run_id,
              observed_delta_tokens, calibration_score, payload
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
          )
          .run(
            id,
            prediction.findingId,
            prediction.runId,
            prediction.predictedWasteTokens,
            prediction.confidence,
            prediction.confidenceTier ?? null,
            errorBar,
            createdAt,
            prediction.comparisonRunId ?? null,
            prediction.observedDeltaTokens ?? null,
            prediction.calibrationScore ?? null,
            payload
          )
      } catch (err) {
        console.warn('[KyberBridge] Failed upserting prediction into canon.db:', err)
      }
    }

    return prediction
  }

  /**
   * Calculates calibration curve and summary metrics across predictions.
   */
  getCalibrationSummary(options?: { runId?: string }): CalibrationCurveResult {
    if (this.store) {
      return this.store.getCalibrationSummary(options)
    }

    const predictions = this.listPredictions(options)
    return calculateCalibrationCurve(predictions)
  }

  /** Alias for getCalibrationSummary to match route semantics. */
  getCalibrationCurve(options?: { runId?: string }): CalibrationCurveResult {
    return this.getCalibrationSummary(options)
  }

  /**
   * List all harness rollups in ascending harness name order.
   */
  listHarnessRollups(): HarnessRollupRow[] {
    if (this.store) {
      return this.store.listHarnessRollups()
    }
    if (this.hasTable(this.canonDb, 'harness_rollup')) {
      try {
        const rows = this.canonDb!
          .prepare('SELECT * FROM harness_rollup ORDER BY harness ASC')
          .all() as unknown as HarnessRollupDbRow[]
        return rows.map(toHarnessRollupRow)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying harness_rollup from canon.db:', err)
        return []
      }
    }
    return []
  }

  /**
   * Fetch one harness rollup by harness name.
   */
  getHarnessRollup(harness: string): HarnessRollupRow | undefined {
    if (this.store) {
      return this.store.getHarnessRollup(harness)
    }
    if (this.hasTable(this.canonDb, 'harness_rollup')) {
      try {
        const row = this.canonDb!
          .prepare('SELECT * FROM harness_rollup WHERE harness = ?')
          .get(harness) as unknown as HarnessRollupDbRow | undefined
        return row === undefined ? undefined : toHarnessRollupRow(row)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying harness_rollup from canon.db:', err)
        return undefined
      }
    }
    return undefined
  }

  /**
   * List runs, optionally narrowed to one harness; newest first.
   */
  listRuns(harnessId?: string): RunRow[] {
    if (this.store) {
      return this.store.listRuns(harnessId)
    }
    if (this.hasTable(this.canonDb, 'run')) {
      try {
        const rows = (
          harnessId === undefined
            ? this.canonDb!.prepare('SELECT * FROM run ORDER BY started DESC').all()
            : this.canonDb!.prepare('SELECT * FROM run WHERE harness = ? ORDER BY started DESC').all(harnessId)
        ) as unknown as RunDbRow[]
        return rows.map(toRunRow)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying runs from canon.db:', err)
        return []
      }
    }
    return []
  }

  /**
   * Fetch one run by id; absent id gives undefined.
   */
  getRun(id: string): RunRow | undefined {
    if (this.store) {
      return this.store.getRun(id)
    }
    if (this.hasTable(this.canonDb, 'run')) {
      try {
        const row = this.canonDb!
          .prepare('SELECT * FROM run WHERE run_id = ?')
          .get(id) as unknown as RunDbRow | undefined
        return row === undefined ? undefined : toRunRow(row)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying run from canon.db:', err)
        return undefined
      }
    }
    return undefined
  }

  /**
   * List executions, optionally filtered by run id; ordered by started.
   */
  listExecutions(runId?: string): ExecutionRow[] {
    if (this.store) {
      return this.store.listExecutions(runId)
    }
    if (this.hasTable(this.canonDb, 'execution')) {
      try {
        const rows = (
          runId === undefined
            ? this.canonDb!.prepare('SELECT * FROM execution ORDER BY started, execution_id').all()
            : this.canonDb!.prepare('SELECT * FROM execution WHERE run_id = ? ORDER BY started, execution_id').all(runId)
        ) as unknown as ExecutionDbRow[]
        return rows.map(toExecutionRow)
      } catch (err) {
        console.warn('[KyberBridge] Failed querying executions from canon.db:', err)
        return []
      }
    }
    return []
  }

  /**
   * Return hierarchical execution tree for a given run id.
   */
  getExecutionTree(runId: string): ExecutionTreeNode[] {
    if (this.store) {
      return this.store.getExecutionTree(runId)
    }
    const executions = this.listExecutions(runId)
    if (executions.length === 0) return []
    const nodesById = new Map<string, ExecutionTreeNode>()
    for (const exec of executions) {
      nodesById.set(exec.executionId, { ...exec, children: [] })
    }
    const roots: ExecutionTreeNode[] = []
    for (const node of nodesById.values()) {
      if (node.parentExecutionId !== null && node.parentExecutionId !== undefined) {
        const parent = nodesById.get(node.parentExecutionId)
        if (parent !== undefined && parent !== node) {
          parent.children.push(node)
          continue
        }
      }
      roots.push(node)
    }
    return roots
  }

  /**
   * Return metadata: rate definitions, tokenizer info, span/quarantine counts, and harness presence.
   */
  getMeta(): KyberMetaResult {
    let spanCount = 0
    let quarantinedCount = 0

    // Count canonical spans.
    if (this.hasTable(this.canonDb, 'records')) {
      try {
        const r = this.canonDb!.prepare('SELECT COUNT(*) as c FROM records').get() as { c: number }
        spanCount += Number(r.c) || 0
      } catch {}
    }

    // Count canonical quarantine entries.
    if (this.hasTable(this.canonDb, 'quarantine')) {
      try {
        const r = this.canonDb!.prepare('SELECT COUNT(*) as c FROM quarantine').get() as {
          c: number
        }
        quarantinedCount += Number(r.c) || 0
      } catch {}
    }

    // Rates info
    let ratesInfo: KyberMetaResult['rates'] = {
      credit_usd: 0.01,
      source: 'https://docs.github.com/copilot/reference/copilot-billing/models-and-pricing',
      retrieved: '2026-08-06',
      note: "Rates transcribed from GitHub's published models-and-pricing table (USD per 1M tokens x100 = credits per 1M).",
    }
    if (this.ratesPath !== undefined && existsSync(this.ratesPath)) {
      try {
        const raw = readFileSync(this.ratesPath, 'utf8')
        const parsed = JSON.parse(raw) as Partial<KyberMetaResult['rates']>
        ratesInfo = {
          credit_usd: parsed.credit_usd ?? ratesInfo.credit_usd,
          source: parsed.source ?? ratesInfo.source,
          retrieved: parsed.retrieved ?? ratesInfo.retrieved,
          note: parsed.note ?? ratesInfo.note,
        }
      } catch {}
    }

    // Harnesses presence from the canonical meta table.
    const perHarness: Record<string, unknown> = {}
    if (this.hasTable(this.canonDb, 'meta')) {
      try {
        const rows = this.canonDb!
          .prepare("SELECT key, value FROM meta WHERE key LIKE 'meta:%'")
          .all() as Array<{ key: string; value: string }>
        for (const row of rows) {
          const h = row.key.slice('meta:'.length)
          try {
            perHarness[h] = JSON.parse(row.value) as unknown
          } catch {
            perHarness[h] = row.value
          }
        }
      } catch {}
    }

    // Ingest sources from the canonical ingest log.
    const sources: Array<{ origin: string; seen: number; new: number }> = []
    if (this.hasTable(this.canonDb, 'ingest_log')) {
      try {
        const rows = this.canonDb!
          .prepare(
            'SELECT source as origin, SUM(count) as seen, SUM(count) as new ' +
              'FROM ingest_log GROUP BY source ORDER BY MIN(id)'
          )
          .all() as unknown as IngestLogRow[]
        for (const r of rows) {
          sources.push({
            origin: r.origin,
            seen: Number(r.seen) || 0,
            new: Number(r.new) || 0,
          })
        }
      } catch {}
    }

    return {
      span_count: spanCount,
      quarantined: quarantinedCount,
      tokenizer: {
        // Report what actually runs. This said `tiktoken/o200k_base` while
        // the code counted `Math.ceil(length / 4)` -- naming a tokenizer that
        // never ran is worse than naming none, because a caveat sized for one
        // tokenizer's drift is read as covering the other's.
        kind: tokenizerName(),
        note:
          tokenizerName() === APPROXIMATE_TOKENIZER
            ? 'The o200k_base encoder could not be loaded; counts fall back to a character approximation and are a rough lower bound.'
            : 'Counts are tokenized with o200k_base, a proxy for models that do not publish their tokenizer. Harness-reported bucket counts, where present, are used in preference and are exact.',
      },
      rates: ratesInfo,
      harnesses: perHarness,
      sources,
    }
  }
}
