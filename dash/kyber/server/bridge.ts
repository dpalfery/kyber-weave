// SQLite Query Bridge for KyberDash (spec: docs/plans/2026-09-03-kyberdash-agent-session-analysis-integration.md, Task 1).
// Provides unified read access to ~/.kyberdash/canon.db (primary/live) and
// agent-session-analysis-dashboard/sessions.db (secondary/historical fallback).

import { existsSync, readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { createRequire } from 'node:module'
import { APPROXIMATE_TOKENIZER, tokenizerName } from '../canon/tokens.js'
import { sumCosts } from '../canon/cost.js'
import { CanonStore, decompressRaw } from '../canon/store.js'
import {
  CANONICAL_CONTENT_KEYS,
  type CanonicalContent,
  type CanonicalContentKey,
  type CanonicalRecord,
  type ContentPart,
  type CostBlock,
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
  sessionsPath?: string
  ratesPath?: string
  canonDb?: DatabaseSync
  sessionsDb?: DatabaseSync
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

interface SummaryRow {
  session_id: string
  summary_json?: string | object | null
  payload?: string | null
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

const countFormat = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 })
const shareFormat = new Intl.NumberFormat('en-US', {
  style: 'percent',
  maximumFractionDigits: 1,
})

function formatCurrency(val: number, cur = 'USD'): string {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: cur }).format(val)
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

export class KyberBridge {
  private canonDb?: DatabaseSync
  private sessionsDb?: DatabaseSync
  private readonly store?: CanonStore
  readonly canonPath: string
  readonly sessionsPath: string | undefined
  readonly ratesPath: string | undefined

  constructor(options?: KyberBridgeOptions) {
    this.canonPath =
      options?.canonPath ??
      process.env.KYBER_CANON_DB ??
      join(homedir(), '.kyberdash', 'canon.db')

    this.sessionsPath =
      options?.sessionsPath ??
      process.env.AGENTDASH_DB ??
      process.env.KYBER_DB ??
      undefined

    this.ratesPath =
      options?.ratesPath ??
      (this.sessionsPath
        ? join(dirname(this.sessionsPath), 'rates.json')
        : undefined)

    this.store = options?.store

    if (options?.canonDb) {
      this.canonDb = options.canonDb
      try {
        this.canonDb.exec('PRAGMA busy_timeout = 5000')
      } catch {}
    } else {
      this.canonDb = this.openDb(this.canonPath)
    }

    if (options?.sessionsDb) {
      this.sessionsDb = options.sessionsDb
      try {
        this.sessionsDb.exec('PRAGMA busy_timeout = 5000')
      } catch {}
    } else if (this.sessionsPath !== undefined) {
      this.sessionsDb = this.openDb(this.sessionsPath)
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
    try {
      this.sessionsDb?.close()
    } catch {}
    this.canonDb = undefined
    this.sessionsDb = undefined
  }

  /**
   * List all available agent sessions across canon.db and sessions.db.
   * Sessions are combined, deduplicated by session_id, sorted by started DESC,
   * and sliced to limit if specified.
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

    // 2. Query secondary sessions.db `session` table if available
    if (this.hasTable(this.sessionsDb, 'session')) {
      try {
        let rows: SessionDbRow[] = []
        try {
          rows = this.sessionsDb!
            .prepare(
              'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, ' +
                "json_extract(payload, '$.summary') as summary_json, " +
                "json_array_length(json_extract(payload, '$.problems')) as problems_count " +
                'FROM session ORDER BY started DESC'
            )
            .all() as unknown as SessionDbRow[]
        } catch {
          rows = this.sessionsDb!
            .prepare(
              'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, repo, branch, started, ended, payload ' +
                'FROM session ORDER BY started DESC'
            )
            .all() as unknown as SessionDbRow[]
        }

        for (const row of rows) {
          if (seenIds.has(row.session_id)) continue
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
        console.warn('[KyberBridge] Failed querying session table in sessions.db:', err)
      }
    }

    // 3. Query canon.db `records` table for sessions not yet in list.
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
   * Primary canon.db is queried first, with fallback to sessions.db.
   * Strings longer than maxLen are safely truncated to prevent oversized JSON responses.
   */
  getSessionPayload<T = SessionPayload>(sessionId: string): T | null {
    if (!sessionId) return null

    // 1. Primary: canon.db
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

    // 2. Fallback: sessions.db
    if (this.hasTable(this.sessionsDb, 'session')) {
      try {
        const row = this.sessionsDb!
          .prepare('SELECT payload FROM session WHERE session_id = ?')
          .get(sessionId) as { payload: string } | undefined
        if (row && typeof row.payload === 'string') {
          const parsed = JSON.parse(row.payload) as unknown
          return _clip(parsed) as T
        }
      } catch (err) {
        console.warn(`[KyberBridge] Error reading session payload for ${sessionId}:`, err)
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

    const records = this.loadContentRecords(sessionId, options.spanId)
    if (records.length === 0 && !this.sessionKnown(sessionId)) {
      return null
    }

    const partFilter =
      options.part !== undefined && options.part !== '' && isCanonicalPart(options.part)
        ? options.part
        : options.part !== undefined && options.part !== ''
          ? false
          : undefined

    const assembled: SessionContentPart[] = []
    for (const record of records) {
      for (const piece of record.parts) {
        if (partFilter === false) continue
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

    const result: SessionContentResult = {
      sessionId,
      parts: applyContentBudget(assembled, CONTENT_RESPONSE_BUDGET),
    }
    if (options.spanId) result.spanId = options.spanId
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
    for (const db of [this.canonDb, this.sessionsDb]) {
      if (!this.hasTable(db, 'session')) continue
      try {
        const row = db!.prepare('SELECT 1 FROM session WHERE session_id = ?').get(sessionId)
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
  getComparisonTable(): ComparisonTableResult {
    // 1. Check if a precomputed `compare` table exists in either DB
    for (const db of [this.sessionsDb, this.canonDb]) {
      if (this.hasTable(db, 'compare')) {
        try {
          const row = db!.prepare('SELECT data FROM compare LIMIT 1').get() as
            | { data: string }
            | undefined
          if (row && typeof row.data === 'string') {
            const parsed = JSON.parse(row.data) as {
              harnesses?: string[]
              rows?: MetricRow[]
              problems?: Array<{ severity: string; code: string; message: string }>
            }
            if (Array.isArray(parsed.harnesses) && Array.isArray(parsed.rows)) {
              return {
                harnesses: parsed.harnesses,
                rows: parsed.rows,
                problems: Array.isArray(parsed.problems) ? parsed.problems : [],
              }
            }
          }
        } catch {}
      }
    }

    // 2. Compute comparison matrix from canon.db and sessions.db
    const dbs = [this.canonDb, this.sessionsDb].filter((d): d is DatabaseSync => Boolean(d))
    const harnessesSet = new Set<string>()
    for (const db of dbs) {
      if (this.hasTable(db, 'session')) {
        try {
          const harnessRows = db
            .prepare('SELECT DISTINCT harness FROM session WHERE harness IS NOT NULL')
            .all() as Array<{ harness: string }>
          for (const r of harnessRows) {
            if (r.harness) harnessesSet.add(r.harness)
          }
        } catch {}
      }
    }

    if (harnessesSet.size > 0) {
      try {
        const availableHarnesses = [...harnessesSet]
        // Prefer stable display order copilot -> gemini -> pi -> others
        const preferredOrder = ['copilot', 'gemini', 'pi']
        const harnesses = [
          ...preferredOrder.filter((h) => availableHarnesses.includes(h)),
          ...availableHarnesses.filter((h) => !preferredOrder.includes(h)),
        ]

        const summaries: Record<string, ParsedSummary> = {}

        for (const h of harnesses) {
          // Check for precomputed aggregate session `__all__:<harness>` across DBs
          for (const db of dbs) {
            if (summaries[h] || !this.hasTable(db, 'session')) continue
            try {
              const aggRow = db
                .prepare(
                  "SELECT json_extract(payload, '$.summary') as summary_json FROM session WHERE session_id = ?"
                )
                .get(`__all__:${h}`) as { summary_json?: string | object | null } | undefined

              if (aggRow?.summary_json !== undefined && aggRow?.summary_json !== null) {
                if (typeof aggRow.summary_json === 'string') {
                  summaries[h] = JSON.parse(aggRow.summary_json) as ParsedSummary
                } else if (typeof aggRow.summary_json === 'object') {
                  summaries[h] = aggRow.summary_json as ParsedSummary
                }
              }
            } catch {
              const aggRow = db
                .prepare('SELECT payload FROM session WHERE session_id = ?')
                .get(`__all__:${h}`) as { payload: string } | undefined
              if (aggRow && typeof aggRow.payload === 'string') {
                try {
                  const parsed = JSON.parse(aggRow.payload) as { summary?: ParsedSummary }
                  if (parsed.summary) {
                    summaries[h] = parsed.summary
                  }
                } catch {}
              }
            }
          }

          // Fallback to aggregating individual sessions across canonDb and sessionsDb
          // Optimized with json_extract to avoid reading full payload blobs
          if (!summaries[h]) {
            const seenSessIds = new Set<string>()
            let turns = 0
            let totalInput = 0
            let totalOutput = 0
            let totalCacheRead = 0
            let totalCacheCreation = 0
            let costUsd: number | null = null
            let costBasis: string | null = null

            for (const db of dbs) {
              if (!this.hasTable(db, 'session')) continue
              let sessRows: SummaryRow[] = []
              try {
                sessRows = db
                  .prepare(
                    "SELECT session_id, json_extract(payload, '$.summary') as summary_json FROM session WHERE harness = ? AND session_id NOT LIKE '__all__%'"
                  )
                  .all(h) as unknown as SummaryRow[]
              } catch {
                sessRows = db
                  .prepare(
                    "SELECT session_id, payload FROM session WHERE harness = ? AND session_id NOT LIKE '__all__%'"
                  )
                  .all(h) as unknown as SummaryRow[]
              }

              for (const sr of sessRows) {
                if (seenSessIds.has(sr.session_id)) continue
                seenSessIds.add(sr.session_id)
                try {
                  let s: ParsedSummary | null = null
                  if (sr.summary_json !== undefined && sr.summary_json !== null) {
                    if (typeof sr.summary_json === 'string') {
                      s = JSON.parse(sr.summary_json) as ParsedSummary
                    } else if (typeof sr.summary_json === 'object') {
                      s = sr.summary_json as ParsedSummary
                    }
                  } else if (typeof sr.payload === 'string') {
                    const parsed = JSON.parse(sr.payload) as { summary?: ParsedSummary }
                    s = parsed.summary ?? null
                  }
                  if (s) {
                    turns += Number(s.turn_count) || 0
                    totalInput += Number(s.total_input) || 0
                    totalOutput += Number(s.total_output) || 0
                    totalCacheRead += Number(s.total_cache_read) || 0
                    totalCacheCreation += Number(s.total_cache_creation) || 0
                    if (s.cost?.usd != null) {
                      costUsd = (costUsd ?? 0) + Number(s.cost.usd)
                      costBasis = costBasis ?? s.cost.basis ?? null
                    }
                  }
                } catch {}
              }
            }

            summaries[h] = {
              turn_count: turns,
              total_input: totalInput,
              total_output: totalOutput,
              total_cache_read: totalCacheRead,
              total_cache_creation: totalCacheCreation,
              cost: { usd: costUsd, basis: costBasis },
            }
          }
        }

          // Build Comparison Rows
          const rows: MetricRow[] = []
          const problems: Array<{ severity: string; code: string; message: string }> = []

          // 1. tokens_per_turn
          const tptCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const s = summaries[h] || {}
            const turns = Number(s.turn_count) || 0
            const totalTokens = (Number(s.total_input) || 0) + (Number(s.total_output) || 0)
            if (turns > 0) {
              const val = totalTokens / turns
              tptCells[h] = {
                measurable: true,
                availability: 'measured',
                value: val,
                render: countFormat.format(val),
              }
            } else {
              tptCells[h] = {
                measurable: true,
                availability: 'measured',
                render: 'no turns',
              }
            }
          }
          rows.push({
            metric: 'tokens_per_turn',
            kind: 'per_turn',
            label: 'Tokens per turn',
            unit: 'tokens',
            cells: tptCells,
          })

          // 2. input_tokens_per_turn
          const inCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const s = summaries[h] || {}
            const turns = Number(s.turn_count) || 0
            const input = Number(s.total_input) || 0
            if (turns > 0) {
              const val = input / turns
              inCells[h] = {
                measurable: true,
                availability: 'measured',
                value: val,
                render: countFormat.format(val),
              }
            } else {
              inCells[h] = { measurable: true, availability: 'measured', render: 'no turns' }
            }
          }
          rows.push({
            metric: 'input_tokens_per_turn',
            kind: 'per_turn',
            label: 'Input tokens per turn',
            unit: 'tokens',
            cells: inCells,
          })

          // 3. output_tokens_per_turn
          const outCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const s = summaries[h] || {}
            const turns = Number(s.turn_count) || 0
            const output = Number(s.total_output) || 0
            if (turns > 0) {
              const val = output / turns
              outCells[h] = {
                measurable: true,
                availability: 'measured',
                value: val,
                render: countFormat.format(val),
              }
            } else {
              outCells[h] = { measurable: true, availability: 'measured', render: 'no turns' }
            }
          }
          rows.push({
            metric: 'output_tokens_per_turn',
            kind: 'per_turn',
            label: 'Output tokens per turn',
            unit: 'tokens',
            cells: outCells,
          })

          // 4. fresh_input_per_turn
          const freshCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const s = summaries[h] || {}
            const turns = Number(s.turn_count) || 0
            const totalInput = Number(s.total_input) || 0
            const cacheRead = Number(s.total_cache_read) || 0
            const cacheCreation = Number(s.total_cache_creation) || 0
            const fresh = Math.max(0, totalInput - cacheRead - cacheCreation)
            if (turns > 0) {
              const val = fresh / turns
              freshCells[h] = {
                measurable: true,
                availability: 'measured',
                value: val,
                render: countFormat.format(val),
              }
            } else {
              freshCells[h] = { measurable: true, availability: 'measured', render: 'no turns' }
            }
          }
          rows.push({
            metric: 'fresh_input_per_turn',
            kind: 'per_turn',
            label: 'Fresh input per turn',
            unit: 'tokens',
            cells: freshCells,
          })

          // 5. cache_read_share_per_turn
          const cacheShareCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const s = summaries[h] || {}
            const totalInput = Number(s.total_input) || 0
            const cacheRead = Number(s.total_cache_read) || 0
            if (totalInput > 0) {
              const val = cacheRead / totalInput
              cacheShareCells[h] = {
                measurable: true,
                availability: 'measured',
                value: val,
                render: shareFormat.format(val),
              }
            } else {
              cacheShareCells[h] = {
                measurable: true,
                availability: 'measured',
                render: 'no input',
              }
            }
          }
          rows.push({
            metric: 'cache_read_share_per_turn',
            kind: 'per_turn',
            label: 'Cache-read share of input',
            unit: 'share',
            cells: cacheShareCells,
          })

          // 6. schema_cost_per_turn
          const schemaCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const s = summaries[h] || {}
            // Pi and Gemini do not export tool schemas -> R10.2: not measurable, never zero
            if (h === 'pi' || h === 'gemini' || s.schema_tokens_per_turn == null) {
              schemaCells[h] = {
                measurable: false,
                availability: 'not_measurable',
                render: 'not measurable',
              }
            } else {
              const val = Number(s.schema_tokens_per_turn)
              schemaCells[h] = {
                measurable: true,
                availability: 'derived',
                value: val,
                render: `~${countFormat.format(val)} (derived, lower bound)`,
              }
            }
          }
          rows.push({
            metric: 'schema_cost_per_turn',
            kind: 'per_turn',
            label: 'Tool-schema tokens per turn',
            unit: 'tokens',
            cells: schemaCells,
          })

          // Cost comparisons across harnesses
          const costBases = new Set<string>()
          for (const h of harnesses) {
            const b = summaries[h]?.cost?.basis
            if (b && typeof b === 'string') costBases.add(b)
          }

          let costRefusalMessage: string | null = null
          if (costBases.size > 1) {
            const listBases = [...costBases].sort().join(', ')
            costRefusalMessage = `not comparable: cost bases differ (${listBases}); declare one basis to compare through`
            problems.push({
              severity: 'warning',
              code: 'cost_basis_mismatch',
              message: `cost figures sit on more than one basis (${listBases}); refusing to compare them directly`,
            })
          }

          // 7. cost_per_turn
          const costPerTurnCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const s = summaries[h] || {}
            const cost = s.cost
            const turns = Number(s.turn_count) || 0
            if (costRefusalMessage && cost?.usd != null) {
              costPerTurnCells[h] = {
                measurable: true,
                availability: 'measured',
                render: costRefusalMessage,
              }
            } else if (cost?.usd != null && turns > 0) {
              const perTurnUsd = Number(cost.usd) / turns
              costPerTurnCells[h] = {
                measurable: true,
                availability: 'measured',
                value: perTurnUsd,
                basis: cost.basis ?? undefined,
                currency: 'USD',
                render: formatCurrency(perTurnUsd),
              }
            } else {
              costPerTurnCells[h] = {
                measurable: true,
                availability: 'measured',
                render: cost?.status === 'out_of_scope' ? 'out of scope' : 'no published rate',
              }
            }
          }
          rows.push({
            metric: 'cost_per_turn',
            kind: 'per_turn',
            label: 'Cost per turn',
            unit: 'currency',
            cells: costPerTurnCells,
          })

          // 8. turns
          const turnsCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const turns = Number(summaries[h]?.turn_count) || 0
            turnsCells[h] = {
              measurable: true,
              availability: 'measured',
              value: turns,
              render: countFormat.format(turns),
            }
          }
          rows.push({
            metric: 'turns',
            kind: 'total',
            label: 'Turns',
            unit: 'count',
            cells: turnsCells,
          })

          // 9. total_tokens
          const totalTokensCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const s = summaries[h] || {}
            const total = (Number(s.total_input) || 0) + (Number(s.total_output) || 0)
            totalTokensCells[h] = {
              measurable: true,
              availability: 'measured',
              value: total,
              render: countFormat.format(total),
            }
          }
          rows.push({
            metric: 'total_tokens',
            kind: 'total',
            label: 'Total tokens',
            unit: 'tokens',
            cells: totalTokensCells,
          })

          // 10. total_cost
          const totalCostCells: Record<string, MetricCell> = {}
          for (const h of harnesses) {
            const s = summaries[h] || {}
            const cost = s.cost
            if (costRefusalMessage && cost?.usd != null) {
              totalCostCells[h] = {
                measurable: true,
                availability: 'measured',
                render: costRefusalMessage,
              }
            } else if (cost?.usd != null) {
              const usdVal = Number(cost.usd)
              totalCostCells[h] = {
                measurable: true,
                availability: 'measured',
                value: usdVal,
                basis: cost.basis ?? undefined,
                currency: 'USD',
                render: formatCurrency(usdVal),
              }
            } else {
              totalCostCells[h] = {
                measurable: true,
                availability: 'measured',
                render: cost?.status === 'out_of_scope' ? 'out of scope' : 'no published rate',
              }
            }
          }
          rows.push({
            metric: 'total_cost',
            kind: 'total',
            label: 'Total cost',
            unit: 'currency',
            cells: totalCostCells,
          })

          return { harnesses, rows, problems }
        } catch (err) {
          console.warn('[KyberBridge] Failed computing comparison table:', err)
        }
      }

    return { harnesses: [], rows: [], problems: [] }
  }

  /**
   * Return quarantined spans from canon.db and sessions.db.
   * Primary canon.db is queried first, merged with sessions.db, deduplicated by span_id.
   */
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

    // 2. Secondary: sessions.db
    if (this.hasTable(this.sessionsDb, 'quarantine')) {
      try {
        let rows: QuarantineDbRow[] = []
        try {
          rows = this.sessionsDb!
            .prepare(
              'SELECT span_id, source, name, namespaces, reason, seen_at ' +
                'FROM quarantine ORDER BY seen_at DESC'
            )
            .all() as unknown as QuarantineDbRow[]
        } catch {
          rows = this.sessionsDb!
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
        console.warn('[KyberBridge] Failed querying quarantine from sessions.db:', err)
      }
    }

    if (typeof limit === 'number' && limit > 0) {
      return results.slice(0, Math.floor(limit))
    }
    return results
  }

  /**
   * Return recorded validation errors, token reconciliation mismatches, and anomalies.
   * Primary canon.db is queried first, merged with sessions.db, deduplicated by span_id/id.
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

    // 2. Secondary: sessions.db ('problem' or 'problems' table)
    const sessionsTable = this.hasTable(this.sessionsDb, 'problem')
      ? 'problem'
      : this.hasTable(this.sessionsDb, 'problems')
        ? 'problems'
        : null

    if (sessionsTable) {
      try {
        let rows: ProblemDbRow[] = []
        try {
          rows = this.sessionsDb!
            .prepare(
              `SELECT id, session_id, span_id, severity, code, message, at, harness FROM ${sessionsTable} ORDER BY id DESC`
            )
            .all() as unknown as ProblemDbRow[]
        } catch {
          rows = this.sessionsDb!
            .prepare(
              `SELECT id, span_id, severity, code, message, location FROM ${sessionsTable} ORDER BY id DESC`
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
        console.warn('[KyberBridge] Failed querying problems from sessions.db:', err)
      }
    }

    if (typeof limit === 'number' && limit > 0) {
      return results.slice(0, Math.floor(limit))
    }
    return results
  }

  /**
   * Return metadata: rate definitions, tokenizer info, span/quarantine counts, and harness presence.
   */
  getMeta(): KyberMetaResult {
    let spanCount = 0
    let quarantinedCount = 0

    // Count spans
    if (this.hasTable(this.sessionsDb, 'span')) {
      try {
        const r = this.sessionsDb!.prepare('SELECT COUNT(*) as c FROM span').get() as { c: number }
        spanCount += Number(r.c) || 0
      } catch {}
    }
    if (this.hasTable(this.canonDb, 'records')) {
      try {
        const r = this.canonDb!.prepare('SELECT COUNT(*) as c FROM records').get() as { c: number }
        spanCount += Number(r.c) || 0
      } catch {}
    }

    // Count quarantine
    if (this.hasTable(this.sessionsDb, 'quarantine')) {
      try {
        const r = this.sessionsDb!.prepare('SELECT COUNT(*) as c FROM quarantine').get() as {
          c: number
        }
        quarantinedCount += Number(r.c) || 0
      } catch {}
    }
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

    // Harnesses presence from meta table
    const perHarness: Record<string, unknown> = {}
    if (this.hasTable(this.sessionsDb, 'meta')) {
      try {
        const rows = this.sessionsDb!
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

    // Ingest sources from ingest_log
    const sources: Array<{ origin: string; seen: number; new: number }> = []
    if (this.hasTable(this.sessionsDb, 'ingest_log')) {
      try {
        const rows = this.sessionsDb!
          .prepare(
            'SELECT origin, SUM(spans_seen) as seen, SUM(spans_new) as new ' +
              'FROM ingest_log GROUP BY origin ORDER BY MIN(id)'
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
    } else if (this.hasTable(this.canonDb, 'ingest_log')) {
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
