// Content-serving helpers: clipping, content sources, the transport index, turn
// resolution and history extraction. Split from `./bridge.ts`, which re-exports
// every name here that it exported before.

import { createHash } from 'node:crypto'
import { decompressColumn } from '../canon/compressed-column.js'
import { CANONICAL_CONTENT_KEYS, type CanonicalContent, type CanonicalContentKey, type CanonicalRecord, type ContentPart, type NotMeasurable, notMeasurable } from '../canon/types.js'
import { type SessionContentPart, type SessionPayload } from './bridge-types.js'

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

export type ContentSourceRecord = {
  spanId: string
  sessionKey: string | null
  /** The record's parent span; the quarantined packet a turn may point at (B7). */
  parentSpanId: string | null
  parts: ContentPart[]
  /**
   * Captured timestamp of the span, when the seam carries one. Lets the
   * turn-content reader scope session prompts to those captured at or
   * before the turn being shown without a second lookup per span.
   */
  timestamp?: string
}

/**
 * The fields `assembleTurnContent` reads off a turn, whether it arrives as a
 * session-payload entry (`SessionPayload.turns` is `unknown[]`) or as a
 * canonical record carrying an ingest-specific index or model. Each one is
 * still checked at runtime before use; this only names the shape being probed.
 */
export type TurnDescriptor = {
  index?: number
  /**
   * Legacy 1-based turn number some payload rows carry instead of `index`.
   * Unknown at the boundary; `turnTransportIndexOf` checks before use.
   */
  turn?: unknown
  spanId?: string
  model?: string
  /** The projected skills of this turn (names); read by the skills entry, never trusted blind. */
  skills_listed?: unknown
  skills_invoked?: unknown
}

/**
 * One row's 0-based transport identity (issue #184): an explicit finite
 * `index` wins; otherwise a finite legacy 1-based `turn` resolves as
 * `turn - 1`. Anything else is no identity — the row is only reachable
 * positionally.
 */
export function turnTransportIndexOf(item: TurnDescriptor): number | undefined {
  if (typeof item.index === 'number' && Number.isFinite(item.index)) return item.index
  if (typeof item.turn === 'number' && Number.isFinite(item.turn)) return item.turn - 1
  return undefined
}

/** The turn's skills entry for the content route (T2), as one turn of the content response. */
export type TurnSkillsContent = {
  /** What the turn had access to; each with its row's first observed description as `text` when one exists. */
  listed: Array<{ name: string; text?: string }> | NotMeasurable
  /** The distinct skills the turn itself invoked; `[]` is a measured none. */
  invoked: string[] | NotMeasurable
}

export const isRecordValue = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

export const isStringArray = (value: unknown): value is string[] =>
  Array.isArray(value) && value.every((item) => typeof item === 'string')

/**
 * A turn's skills entry, read from the already-loaded session payload only.
 * The narrow turn-content seam must not walk session records, so the payload
 * carries what the projection already derived: `skills.rows[].description`
 * and each turn's `skills_listed` / `skills_invoked`. A payload projected
 * before skills existed has no `skills` key and is declared so.
 *
 * Known limit (accepted): `text` is the skill's first observed description in
 * the session. A later listing that changes the description does not alter the
 * text served for earlier turns.
 */
export function skillsContentFromPayload(
  payload: SessionPayload | null | undefined,
  turn: TurnDescriptor | undefined,
): TurnSkillsContent | NotMeasurable {
  const skills = payload?.skills
  if (!isRecordValue(skills)) return notMeasurable('session not yet projected with skills')
  if (skills.availability === 'not_measurable') {
    return notMeasurable(typeof skills.reason === 'string' ? skills.reason : 'Skills are not measurable for this session.')
  }

  const descriptions = new Map<string, string>()
  if (Array.isArray(skills.rows)) {
    for (const row of skills.rows) {
      if (isRecordValue(row) && typeof row.name === 'string' && typeof row.description === 'string') {
        descriptions.set(row.name, row.description)
      }
    }
  }

  const listed: TurnSkillsContent['listed'] = isStringArray(turn?.skills_listed)
    ? turn.skills_listed.map((name) => {
        const text = descriptions.get(name)
        return text === undefined ? { name } : { name, text }
      })
    : notMeasurable('This turn has no skill listing in the projected session, so the skills it had access to are not measurable.')

  const invoked: TurnSkillsContent['invoked'] = isStringArray(turn?.skills_invoked)
    ? [...turn.skills_invoked]
    : notMeasurable('Skill invocations are not measured for this session, so the skills this turn invoked are not measurable.')

  return { listed, invoked }
}

/**
 * Index of explicit 0-based transport identities over one ordered turn list,
 * built in a single pass. First identity wins on duplicates, matching the
 * historic `find` order.
 */
export function buildTransportIndex<T>(
  items: readonly T[],
  identityOf: (item: T) => number | undefined,
): Map<number, T> {
  const byIdentity = new Map<number, T>()
  for (const item of items) {
    const id = identityOf(item)
    if (id !== undefined && !byIdentity.has(id)) byIdentity.set(id, item)
  }
  return byIdentity
}

/**
 * Strict turn resolution over one ordered list: explicit identity first,
 * array position only for the row that carries neither `index` nor `turn`.
 * Anything else resolves to nothing — never a neighboring turn (issue #184).
 */
export function lookupTransport<T>(
  items: readonly T[] | undefined,
  byIdentity: Map<number, T> | undefined,
  identityOf: (item: T) => number | undefined,
  turnIndex: number,
): T | undefined {
  const direct = byIdentity?.get(turnIndex)
  if (direct !== undefined) return direct
  const positional = items !== undefined && turnIndex >= 0 && turnIndex < items.length ? items[turnIndex] : undefined
  if (positional !== undefined && identityOf(positional) === undefined) return positional
  return undefined
}

/**
 * Normalized turn-resolution row: the span an index resolves to plus its
 * explicit 0-based identity (`undefined` for position-only rows), with the
 * one-pass identity map beside the ordered entries. Built once per response
 * and shared by the target lookup and every preceding-turn lookup.
 */
export type TurnResolutionEntry = {
  spanId: string
  model?: string
  identity: number | undefined
}

export type TurnResolutionPool = {
  entries: TurnResolutionEntry[]
  byIdentity: Map<number, TurnResolutionEntry>
}

/**
 * Per-metric measurability as a payload carries it, under either the flat
 * `measurability` map or `context.first.buckets`. `SessionPayload` types both
 * as `unknown`, so the read that consults them names the shape here.
 */
export type MeasurabilityEntry = { availability?: string; reason?: string }
export type MeasurabilityMap = Record<string, MeasurabilityEntry | undefined>

/**
 * Renders a request-supplied identifier for a log line. Control characters are
 * stripped so a crafted id cannot forge log entries, and the result is
 * truncated. Callers pass it as a printf argument, never as the format string.
 */
export function logId(value: string): string {
  return value.replace(/[^\x20-\x7e]/g, '?').slice(0, 120)
}

/**
 * Sanitizes an unknown caught value for a log line. Extracts the message from
 * an Error, or converts any other value to a string, then strips CR/LF and
 * other control characters so a crafted database error cannot forge log entries.
 */
export function logErr(value: unknown): string {
  const raw = value instanceof Error ? value.message : String(value)
  return raw.replace(/[^\x20-\x7e]/g, '?').slice(0, 500)
}

/**
 * Stable hash of exact source bytes for recurrence comparison. Hashes (not
 * texts) are retained per earlier part so a long session keeps one small
 * digest per captured part instead of every earlier byte in memory.
 */
export function sha256Hex(text: string): string {
  return createHash('sha256').update(text, 'utf8').digest('hex')
}


export function isCanonicalPart(value: string): value is CanonicalContentKey {
  return (CANONICAL_CONTENT_KEYS as readonly string[]).includes(value)
}

export function sessionKeyOf(record: Pick<CanonicalRecord, 'sessionId' | 'traceId'>): string | null {
  return record.sessionId ?? record.traceId ?? null
}

export function partsFromRecord(record: CanonicalRecord): ContentPart[] {
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

export function partsFromRow(row: Record<string, unknown>): ContentPart[] {
  if (row.parts_json !== null && row.parts_json !== undefined) {
    // The column holds the deflated parts, and the plain JSON a row written
    // before the compression landed holds. Both are read here, so a pre-change
    // row keeps its parts — the fall-through to `content_json` below would
    // lose them, because that column is '{}' on a parts-carrying row.
    const parts = decompressColumn(row.parts_json)
    if (Array.isArray(parts) && parts.length > 0) {
      return [...parts].sort((a, b) => (a.order ?? 0) - (b.order ?? 0))
    }
    // A column that holds nothing usable falls through to content_json — a
    // corrupt blob is not an empty session.
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

/** A record row's parent span id, or null when it carries none. */
export function parentSpanIdOf(row: Record<string, unknown>): string | null {
  return row.parent_span_id === null || row.parent_span_id === undefined
    ? null
    : String(row.parent_span_id)
}

export function toContentSource(record: CanonicalRecord): ContentSourceRecord {
  return {
    spanId: record.spanId,
    sessionKey: sessionKeyOf(record),
    parentSpanId: record.parentSpanId,
    parts: partsFromRecord(record),
    timestamp: normalizeRecordTimestamp(record.timestamp),
  }
}

/**
 * A canonical timestamp as the narrow stored string. A `Date` round-trips
 * through ISO; anything else is unknown rather than invented.
 */
export function normalizeRecordTimestamp(value: CanonicalRecord['timestamp'] | unknown): string | undefined {
  if (typeof value === 'string') return value
  if (value instanceof Date && !Number.isNaN(value.getTime())) return value.toISOString()
  return undefined
}

/**
 * Temporal gate for shared session prompts: only a prompt provably captured
 * after the shown turn is withheld. Both timestamps parse and the candidate
 * is strictly later — anything missing or unparseable stays included,
 * because the reader cannot prove it is future evidence and dropping it
 * would invent a claim the corpus cannot support. Equal timestamps stay
 * included for the same reason: millisecond equality cannot order two
 * captures, so the limitation is documented, not decided.
 */
export function sessionPromptAtOrBefore(candidate: string | undefined, target: string | undefined): boolean {
  if (candidate === undefined || target === undefined) return true
  const candidateMs = Date.parse(candidate)
  const targetMs = Date.parse(target)
  if (!Number.isFinite(candidateMs) || !Number.isFinite(targetMs)) return true
  return candidateMs <= targetMs
}

export function applyContentBudget(
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

export function extractMessagesFromHistory(text: string): { userMessages: string[]; assistantMessages: string[] } {
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
