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
    const id = ID_ELEMENT.exec(withoutDescription)?.[1]?.trim()
    if (id === undefined || id === '') return { ok: false, reason: 'malformed-block' }
    ids.push(id)
  }
  if (ids.length !== count(SKILL_OPEN, body)) return { ok: false, reason: 'malformed-block' }
  return { ok: true, ids }
}
