// The split-read contract: one definition of the statements, the parsers and
// the shape decision the two session readers share.
//
// WHY: a session's payload is read twice in this tree — by the store, for the
// ASAD contract the dashboard reads, and by the bridge, for the routes — and
// the two reads must answer alike. What they read *with* was not shared: the
// SQL, the JSON parsers and the rule that decides where a session's turns
// come from were copied into both files. Two copies meant a fix to the null or
// empty handling, or to the split read's SQL, had to be made twice, and a
// test that covered one copy said nothing about the other.
//
// WHAT is shared here: the three statements, the parsers for the text
// SQLite's JSON functions return, the turn row's decode, and the shape
// decision. These are the rules, and a rule with two copies is a rule that can
// be half-applied.
//
// WHAT stays in each reader: the plumbing. The store reads through its own
// handle and the bridge through the handle it was given, and either may be a
// handle that cannot serve the split read at all — no JSON functions in its
// SQLite build, or a schema the fragments do not fit — in which case that
// reader falls back to the whole payload. Which handle a reader holds, and
// whether the turn table exists on it, are facts about the handle rather than
// about the contract, so they stay where they are.
//
// The readers stay two for the same reason: the contract is one, but the
// question is asked of different handles, and a single reader could not answer
// for a handle it does not own.
//
// It lives in `canon/` as a leaf: it imports the compressed-column codec and
// nothing else, so neither the store nor the bridge can close an import cycle
// through it. The codec is a leaf too, for the same reason — the store imports
// this module, so this module cannot import the store.

import { decompressColumn } from './compressed-column.js'

/**
 * One session's payload without its turns, and the turns it still carries.
 *
 * <remarks>
 * The metadata read of the split: `json_remove` takes the turns out of the
 * blob inside SQLite, so the session's metadata crosses into JS without them
 * — the turns were the blob's bulk (15.3 MB for a 10,933-turn session).
 *
 * The `json_type`/`json_extract` pair is the reader's split test. A payload
 * that still carries a `turns` array is a row written before the split, or by
 * a build that still writes whole payloads, and its turns are read from
 * there; a payload without one has had its turns split out, and they live in
 * `session_turn`. The whole blob never crosses into JS either way: the
 * payload arrives without its turns, and the turns arrive as their own
 * fragment.
 */
export const SESSION_PAYLOAD_FRAGMENTS_SELECT =
  "SELECT json_remove(payload, '$.turns') AS payload_json, " +
  "json_type(payload, '$.turns') AS turns_type, " +
  "json_extract(payload, '$.turns') AS turns_json " +
  'FROM session WHERE session_id = ?'

/**
 * One session's turns, read from the turns table in one range scan.
 *
 * <remarks>
 * A session's turns live in `session_turn`, one row each, indexed by
 * `(session_id, turn_index)`. One statement therefore serves every turn a
 * session holds, however many it has: a per-turn query would scale the
 * statement count with the turn count, and the corpus's largest session holds
 * 10,933 of them.
 *
 * `ORDER BY turn_index` restores the projection's order — the index is the
 * turn's own, so the rows come back in the order the turns were built.
 */
export const SESSION_TURNS_SELECT =
  'SELECT turn_index, turn_json FROM session_turn WHERE session_id = ? ORDER BY turn_index'

/**
 * One session's whole payload.
 *
 * <remarks>
 * The last resort: a handle that cannot serve the split read — no JSON
 * functions in its SQLite build, or a schema the fragments do not fit — is
 * served from the blob it holds, exactly as the pre-split reader served it.
 */
export const SESSION_PAYLOAD_SELECT = 'SELECT payload FROM session WHERE session_id = ?'

/**
 * A JSON value that is an object, or undefined when it is not one.
 *
 * <remarks>
 * A value that is not a JSON object is no object: absent text, a JSON array
 * and a value that does not parse all read as absent, never as an empty
 * object — an empty object would read as "measured, and there was nothing
 * there".
 */
export function jsonObjectOf(value: unknown): Record<string, unknown> | undefined {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return undefined
  return value as Record<string, unknown>
}

/**
 * One session's payload, parsed from the text its row stores.
 *
 * <remarks>
 * A payload that is not a JSON object must not take the whole upgrade down
 * with it: the row is left exactly as it is. So a payload that does not parse
 * — or that parses into something other than an object — reads as absent
 * rather than as a fabricated empty object.
 */
export function parseJsonObject(value: unknown): Record<string, unknown> | undefined {
  if (typeof value !== 'string' || value === '') return undefined
  try {
    const parsed = JSON.parse(value) as unknown
    return jsonObjectOf(parsed)
  } catch {
    return undefined
  }
}

/**
 * One JSON array, parsed from the text SQLite's JSON functions return.
 *
 * <remarks>
 * A value that is not an array is no array: absent text, a JSON object and a
 * text that does not parse all read as absent. An empty array is an array —
 * a session with no turns is a fact about the session, not a missing one.
 */
export function parseJsonArray(value: unknown): unknown[] | undefined {
  if (typeof value !== 'string' || value === '') return undefined
  try {
    const parsed = JSON.parse(value) as unknown
    return Array.isArray(parsed) ? parsed : undefined
  } catch {
    return undefined
  }
}

/**
 * One turn row's stored value, as the turn object it holds.
 *
 * <remarks>
 * A turn row holds the deflated turn, and the plain JSON a row written before
 * the compression landed holds. Both are read, so the bridge's reader and the
 * store's reader answer alike — and a turn that cannot be read is no turn,
 * never an empty object.
 */
export function turnObjectOf(value: unknown): Record<string, unknown> | undefined {
  return jsonObjectOf(decompressColumn(value))
}

/**
 * One session's turns, from the split test and the rows that hold them.
 *
 * <remarks>
 * The shape decision, in one place: the payload's own turns when it still
 * carries them, the split rows when they have been moved out, and an empty
 * list when the session has no turns anywhere.
 *
 * The payload's own turns are the fresh ones — a row written before the split,
 * or by a build that still writes whole payloads, carries the turns it was
 * written with, and a re-write by such a build leaves the split rows behind it
 * stale. A payload without a `turns` array has had its turns split out, and
 * they live in `session_turn`.
 *
 * A session with no turns anywhere is served with an empty list: the
 * projection always writes the key, so a payload without one would read as
 * "not measured" where the session measured none.
 *
 * `turnsType` is the `json_type` of the payload's `$.turns` and `turnsJson`
 * is the text `json_extract` took out of it; `splitTurns` is what the reader
 * read from the turn table, or `undefined` when that handle carries no turn
 * table at all.
 */
export function sessionTurnsOf(
  turnsType: unknown,
  turnsJson: unknown,
  splitTurns: Array<Record<string, unknown>> | undefined,
): unknown[] {
  const inlineTurns = turnsType === 'array' ? parseJsonArray(turnsJson) : undefined
  return inlineTurns ?? splitTurns ?? []
}
