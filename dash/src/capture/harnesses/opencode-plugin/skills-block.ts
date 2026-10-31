// Pure parser for OpenCode's `<available_skills>` system-prompt block.
//
// Shape pinned by the captured system prompt in `fixtures/v2/runA.context.json`: one
// `<skill>` element per skill with `<id>`, `<name>`, `<description>` children.
// When skills change mid-session OpenCode re-emits the block after "The
// available skills have changed…", and the later block supersedes the earlier.
//
// Shared by the plugin (bundled: no imports) and the adapter. Absent
// is not empty: a missing or malformed block is a named failure, never `[]`.

const BLOCK_OPEN = '<available_skills>'
const BLOCK_CLOSE = '</available_skills>'

/** Why no listing could be read; named so callers never confuse it with an empty listing. */
export type SkillsBlockFailure = 'no-block' | 'malformed-block'

export type SkillsBlockResult =
  | { ok: true; ids: string[] }
  | { ok: false; reason: SkillsBlockFailure }

const SKILL_ELEMENT = /<skill>([\s\S]*?)<\/skill>/g
const SKILL_OPEN = /<skill>/g
const SKILL_CLOSE = /<\/skill>/g
const DESCRIPTION_ELEMENT = /<description>[\s\S]*?<\/description>/g
const ID_ELEMENT = /<id>([\s\S]*?)<\/id>/

// ---------------------------------------------------------------------------------------------
// Bounded text. This module is the one import-free leaf both the plugin and the adapter share, so
// the primitive that cuts a host string to a byte cap lives here and `mapper.ts` imports it.

/**
 * Cap, in UTF-8 bytes, on each host string kept as a short label: the session label, request kind,
 * agent, model, finish reason, tool name, error type, skill id, directory and the like. A longer
 * value is cut, never sent whole and never retained whole.
 */
export const MAX_FIELD_BYTES = 1024

/**
 * The head of `text` that can hold the first `maxBytes` of its UTF-8. Every UTF-16 unit takes at
 * least one byte, so the first `maxBytes` units already cover every byte that can be kept and the
 * rest of a huge string is never copied or encoded. A high surrogate left at the end, whose pair
 * the cut would have split, is dropped. Text no longer than `maxBytes` units is returned as is.
 */
export function headForCap(text: string, maxBytes: number): string {
  if (text.length <= maxBytes) return text
  const head = text.slice(0, maxBytes)
  const last = head.charCodeAt(head.length - 1)
  return last >= 0xd800 && last <= 0xdbff ? head.slice(0, -1) : head
}

/**
 * `text` limited to `maxBytes` of UTF-8, cut on a character boundary. Text under the limit is
 * returned unchanged, so its code units (surrogate pairs, CRLF) survive exactly. A cut result is
 * always freshly encoded, so it does not keep the host string alive behind a short length.
 */
export function capUtf8(text: string, maxBytes: number): string {
  const head = headForCap(text, maxBytes)
  if (head === text && Buffer.byteLength(text, 'utf8') <= maxBytes) return text
  const bytes = Buffer.from(head, 'utf8')
  let end = Math.min(maxBytes, bytes.length)
  // A byte of the form 10xxxxxx continues a character; step back to the byte that starts it.
  while (end > 0 && end < bytes.length && ((bytes[end] ?? 0) & 0xc0) === 0x80) end -= 1
  return bytes.toString('utf8', 0, end)
}

/**
 * One spelling of a skill id for every source (the listing, the prompt, a skill tool call, an
 * activation event): the string with surrounding whitespace removed and cut to `MAX_FIELD_BYTES`,
 * or absent when nothing is left.
 */
export function normalizeSkillId(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined
  const id = value.trim()
  return id === '' ? undefined : capUtf8(id, MAX_FIELD_BYTES)
}

function count(pattern: RegExp, text: string): number {
  return text.match(pattern)?.length ?? 0
}

/**
 * Read the skill `<id>` values, in listing order, from the last
 * `<available_skills>` block in `text`. A measured empty block returns
 * `{ ok: true, ids: [] }`; no block or an unreadable one returns a named failure.
 */
export function parseAvailableSkills(text: string): SkillsBlockResult {
  const open = text.lastIndexOf(BLOCK_OPEN)
  if (open === -1) return { ok: false, reason: 'no-block' }
  const bodyStart = open + BLOCK_OPEN.length
  const close = text.indexOf(BLOCK_CLOSE, bodyStart)
  if (close === -1) return { ok: false, reason: 'malformed-block' }
  const body = text.slice(bodyStart, close)

  if (count(SKILL_OPEN, body) !== count(SKILL_CLOSE, body)) {
    return { ok: false, reason: 'malformed-block' }
  }
  const ids: string[] = []
  for (const element of body.matchAll(SKILL_ELEMENT)) {
    // An `<id>` inside a description is prose, not the skill id.
    const withoutDescription = (element[1] ?? '').replace(DESCRIPTION_ELEMENT, '')
    const id = normalizeSkillId(ID_ELEMENT.exec(withoutDescription)?.[1])
    if (id === undefined) return { ok: false, reason: 'malformed-block' }
    ids.push(id)
  }
  if (ids.length !== count(SKILL_OPEN, body)) return { ok: false, reason: 'malformed-block' }
  return { ok: true, ids }
}
