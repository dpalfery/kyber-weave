// The session row shapes, the session SELECT statements and the helpers that
// read a session row. Split from `./bridge.ts`, which re-exports every name here
// that it exported before.

import { type SessionTurnFigures } from './bridge-types.js'


export interface SessionDbRow {
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

/**
 * One row's summary, as an object.
 *
 * <remarks>
 * The stored summary is a JSON string; the served shape is the object it
 * encodes. A summary that is not an object, or that does not parse, is an
 * absent summary — never a fabricated one.
 * </remarks>
 */
export function parseSummaryObject(value: string | object | null | undefined): Record<string, unknown> {
  if (typeof value === 'object' && value !== null) return value as Record<string, unknown>
  if (typeof value !== 'string' || value === '') return {}
  try {
    const parsed = JSON.parse(value) as unknown
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return {}
    return parsed as Record<string, unknown>
  } catch {
    return {}
  }
}

/**
 * One JSON value, parsed from the text SQLite's JSON functions return.
 *
 * <remarks>
 * `NULL` — an absent path — is no value at all. A JSON `null` is a value and
 * stays one, so a timeline that is null is served as null rather than as
 * absent.
 */
export function parseJsonValue(value: unknown): unknown {
  if (typeof value !== 'string') return undefined
  try {
    return JSON.parse(value) as unknown
  } catch {
    return undefined
  }
}

/**
 * One turn row's measured counters.
 *
 * <remarks>
 * The counters are taken as the row holds them. A counter the row does not
 * carry stays absent, because a zero would claim the harness measured
 * nothing — and absence is what the row actually says.
 */
export function turnFiguresOf(turn: Record<string, unknown>, turnIndex: number): SessionTurnFigures {
  const index = turn['index']
  return {
    index: typeof index === 'number' && Number.isFinite(index) ? index : turnIndex,
    input: turnCounter(turn, 'input'),
    output: turnCounter(turn, 'output'),
    fresh: turnCounter(turn, 'fresh'),
    cache_read: turnCounter(turn, 'cache_read'),
  }
}

/** One measured counter, or `undefined` when the row carries no finite one. */
export function turnCounter(turn: Record<string, unknown>, key: string): number | undefined {
  const value = turn[key]
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

/**
 * One row's summary and problem count, read out of its own payload.
 *
 * <remarks>
 * The reading the session list already makes for a row it cannot serve from
 * the materialized columns, narrowed to one row.
 * </remarks>
 */
export function summaryFromPayload(payload: string | null | undefined): {
  summary: Record<string, unknown>
  problems_count: number
} {
  if (typeof payload !== 'string' || payload === '') return { summary: {}, problems_count: 0 }
  try {
    const parsed = JSON.parse(payload) as { summary?: unknown; problems?: unknown } | null
    if (parsed === null || typeof parsed !== 'object') return { summary: {}, problems_count: 0 }
    return {
      summary: parseSummaryObject(parsed.summary as string | object | null | undefined),
      problems_count: Array.isArray(parsed.problems) ? parsed.problems.length : 0,
    }
  } catch {
    return { summary: {}, problems_count: 0 }
  }
}

/**
 * Whether a row is missing either of the two materialized summary columns.
 *
 * <remarks>
 * NULL is the writer's per-column "not materialized" sentinel, so a row is
 * never whole or empty: a payload carrying a problems array and no summary is
 * written as a NULL summary beside a real count, and a payload carrying a
 * summary and no problems array is written as a NULL count beside a real
 * summary. A row is complete only when it carries both columns.
 */
export function isMissingSessionColumn(row: SessionDbRow): boolean {
  return (
    row.summary_json === null ||
    row.summary_json === undefined ||
    row.problems_count === null ||
    row.problems_count === undefined
  )
}

/**
 * Fill a row's missing materialized columns from a legacy reading.
 *
 * <remarks>
 * Only what the row is missing is filled in: a materialized value is never
 * replaced by the payload's, and a value the payload does not carry leaves
 * the row's own NULL in place. The session list and the bounded summary route
 * both merge through this, so the two surfaces cannot drift apart on what a
 * NULL column means.
 */
export function fillMissingSessionColumns(
  row: SessionDbRow,
  legacy: { summary_json?: string | object | null; problems_count?: number | null },
): void {
  if (row.summary_json === null || row.summary_json === undefined) {
    row.summary_json = legacy.summary_json ?? null
  }
  if (row.problems_count === null || row.problems_count === undefined) {
    row.problems_count = legacy.problems_count ?? null
  }
}

/**
 * The session list, read from the materialized columns only.
 *
 * <remarks>
 * `summary_json` and `problems_count` carry the two facts the list needs out
 * of the payload blob (the serialized `$.summary`, and the length of
 * `$.problems`), so a materialized corpus is served without reading a byte of
 * `payload`. The statement is column-only on purpose: naming `payload` here
 * at all — even inside `json_extract` — makes SQLite read and parse the whole
 * blob of every row, which is what made a list request cost the size of the
 * payload store.
 * </remarks>
 */
export const SESSION_LIST_SELECT =
  'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, ' +
  'repo, branch, started, ended, summary_json, problems_count ' +
  'FROM session ORDER BY started DESC'

/**
 * The legacy subset: rows missing a materialized value.
 *
 * <remarks>
 * The predicate is the load-bearing part: it confines the payload read to the
 * rows that are not fully materialized, so a corpus where every row carries
 * both values never issues this statement at all. It covers a row missing
 * *either* value, because NULL is the writer's per-column "not materialized"
 * sentinel — a payload with a problems array and no summary is written as a
 * NULL summary beside a real count, and a payload with no problems array is
 * written as a NULL count beside a real summary. The extracted values are
 * merged back into the primary rows by `session_id`, and a value the row
 * already carries is never overwritten.
 * </remarks>
 */
export const SESSION_LIST_LEGACY_SELECT =
  "SELECT session_id, json_extract(payload, '$.summary') AS summary_json, " +
  "json_array_length(json_extract(payload, '$.problems')) AS problems_count " +
  'FROM session WHERE summary_json IS NULL OR problems_count IS NULL'

/**
 * The last resort: the whole payload, parsed in JS.
 *
 * <remarks>
 * Taken when the SQLite build has no JSON functions (the legacy fallback
 * cannot run) or when the schema predates the materialized columns (the
 * primary statement cannot run). Unchanged from the pre-materialization
 * reader, so a database the fast path cannot serve still lists.
 * </remarks>
 */
export const SESSION_LIST_PAYLOAD_SELECT =
  'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, ' +
  'repo, branch, started, ended, payload ' +
  'FROM session ORDER BY started DESC'

/**
 * One session's identity columns and its materialized summary.
 *
 * <remarks>
 * The narrow read the bounded summary is built on. `payload` is not named
 * here: a session's blob is unbounded — 15.3 MB for a 10,933-turn session —
 * and this read answers from the columns alone.
 * </remarks>
 */
export const SESSION_SUMMARY_SELECT =
  'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, ' +
  'started, ended, summary_json, problems_count ' +
  'FROM session WHERE session_id = ?'

/**
 * The legacy subset for one row: the summary and the problem count read out
 * of that row's own payload.
 *
 * <remarks>
 * Transitional, and scoped to the requested id — the `WHERE` clause is that
 * same single id, so this reads one row's payload and never the table's. It
 * is issued only for a row that is missing *either* materialized value, since
 * NULL is the writer's per-column "not materialized" sentinel, so a fully
 * materialized corpus never runs it. `json_extract` keeps the blob inside
 * SQLite: only the extracted fragment crosses into JS.
 * </remarks>
 */
export const SESSION_SUMMARY_LEGACY_SELECT =
  "SELECT json_extract(payload, '$.summary') AS summary_json, " +
  "json_array_length(json_extract(payload, '$.problems')) AS problems_count " +
  'FROM session WHERE session_id = ?'

/**
 * The last resort for one row: its payload, parsed in JS.
 *
 * <remarks>
 * Taken when the schema predates the materialized columns (the narrow read
 * cannot run) or when the SQLite build has no JSON functions (the legacy
 * extraction cannot run). One row only, so it is never worse than the detail
 * route's whole-payload read.
 * </remarks>
 */
export const SESSION_SUMMARY_PAYLOAD_SELECT =
  'SELECT session_id, harness, label, is_subagent, parent_session, agent_name, ' +
  'started, ended, payload ' +
  'FROM session WHERE session_id = ?'

/**
 * One turn row, read from the turns table.
 *
 * <remarks>
 * A session's turns live in `session_turn`, one row each, indexed by
 * `(session_id, turn_index)`. One turn is therefore a seek — not the session
 * blob, which held 10,933 turn rows and 15.3 MB for the corpus's largest
 * session.
 */
export const SESSION_TURN_SELECT =
  'SELECT turn_json FROM session_turn WHERE session_id = ? AND turn_index = ?'

/**
 * Whether a session has turn rows of its own.
 *
 * <remarks>
 * The load-bearing part of the legacy fallback below: a session whose turns
 * are split out no longer carries them in its payload, so an index with no
 * row is simply out of range — and saying so must not cost a payload read.
 */
export const SESSION_TURN_ROWS_SELECT =
  'SELECT 1 AS present FROM session_turn WHERE session_id = ? LIMIT 1'

/**
 * The legacy turn read: one turn out of a payload that still carries its turns.
 *
 * <remarks>
 * `json_each` walks the payload's own turn array inside SQLite, so only the
 * one matching turn crosses into JS. The identity rule is the writer's own: a
 * turn that names an `index` is that turn, and a turn that names none is its
 * position — the rule the turns table itself was built with, so the two paths
 * answer for the same turn.
 *
 * The array check is load-bearing: a `turns` key that is not an array is a
 * malformed payload, and walking it as one would answer for a turn that does
 * not exist.
 */
export const SESSION_TURN_LEGACY_SELECT =
  'SELECT t.value AS turn_json FROM session s, json_each(json_extract(s.payload, \'$.turns\')) AS t ' +
  "WHERE s.session_id = ? AND json_type(s.payload, '$.turns') = 'array' AND COALESCE(" +
  "CASE WHEN json_type(t.value, '$.index') IN ('integer', 'real') " +
  "THEN CAST(json_extract(t.value, '$.index') AS INTEGER) END, " +
  'CAST(t.key AS INTEGER)) = ? LIMIT 1'

/**
 * Whether a derived session row exists.
 *
 * <remarks>
 * Membership only: a seek on the session table's primary key, and the whole
 * of what "does this session exist" needs. The session's content is not part
 * of that answer — a reader that took it from the payload would decompress
 * the blob and reassemble every turn of the session out of `session_turn` to
 * learn what the row's key already carries.
 */
export const SESSION_EXISTS_SELECT = 'SELECT 1 FROM session WHERE session_id = ?'

/**
 * One session's timeline, read as a narrow fragment of its payload.
 *
 * <remarks>
 * `->` hands over the JSON text of the value at the path, so the timeline
 * crosses into JS and the blob around it does not — and the text is the
 * value's own JSON, so a timeline of any JSON type is served byte for byte
 * as the whole-payload read served it. The timeline is session metadata: it
 * lives beside the turns, not inside them, so it survives the payload split
 * intact.
 */
export const SESSION_TIMELINE_SELECT =
  "SELECT (payload -> '$.timeline') AS timeline_json FROM session WHERE session_id = ?"

/**
 * One session's context analysis, read as narrow fragments of its payload.
 *
 * <remarks>
 * Both fragments are taken by SQLite's JSON functions: the persisted context
 * block, and the index of the session's last turn. The blob around them never
 * crosses into JS.
 */
export const SESSION_CONTEXT_SELECT =
  "SELECT json_extract(payload, '$.context') AS context_json, " +
  "json_extract(payload, '$.turns[#-1].index') AS last_turn_index " +
  'FROM session WHERE session_id = ?'

export interface QuarantineDbRow {
  span_id: string
  source?: string | null
  name?: string | null
  namespaces?: string | null
  reason?: string | null
  seen_at?: number | string | null
  timestamp?: string | null
}

export interface ProblemDbRow {
  id: number | string
  session_id?: string | null
  span_id?: string | null
  severity: string
  code: string
  message: string
  at?: number | string | null
  timestamp?: string | null
  harness?: string | null
  location?: string | null
}

export interface IngestLogRow {
  origin: string
  seen?: number | null
  new?: number | null
}
