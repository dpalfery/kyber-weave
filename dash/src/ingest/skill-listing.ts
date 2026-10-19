// The skill listing Claude Code writes into a transcript (T10).
//
// Claude Code records the skills a session had available as an `attachment`
// line of type `skill_listing`. Its `content` is one `- name: description` line
// per skill; `names` repeats the names; `isInitial` marks the session's first
// listing. This module is the single normalizer for that attachment: the normal
// JSON path (compactEntry), the large-line scanner (parseLargeJsonl), and the
// provider loader (loadClaudeCalls) all call it, so they cannot disagree.

export const SKILL_LISTING_ATTACHMENT_TYPE = 'skill_listing'

export type SkillListingEntry = { name: string; description?: string }

export type SkillListing = { isInitial: boolean; skills: SkillListingEntry[] }

/**
 * Narrow a `skill_listing` attachment to a SkillListing. Accepts the raw
 * attachment (`content`, `names`, `isInitial`) and the already-narrowed form
 * (`skills`), so applying it twice gives the same result.
 *
 * Returns undefined when the attachment is not a skill listing, or when it
 * yields no entries: absent, never an empty listing.
 */
export function skillListingFromAttachment(attachment: unknown): SkillListing | undefined {
  if (attachment === null || typeof attachment !== 'object' || Array.isArray(attachment)) return undefined
  const a = attachment as Record<string, unknown>
  if (a['type'] !== SKILL_LISTING_ATTACHMENT_TYPE) return undefined

  const isInitial = a['isInitial'] === true
  const skills = Array.isArray(a['skills'])
    ? entriesFromNarrowed(a['skills'])
    : entriesFromContent(a['content']) ?? entriesFromNames(a['names'])
  if (skills === undefined || skills.length === 0) return undefined
  return { isInitial, skills }
}

/**
 * Parse `- name: description` content lines. A line with no `: ` separator is a
 * name-only entry. Lines not starting with `- ` are skipped. Returns undefined
 * when no line parses, so the caller can fall back to `names`.
 */
function entriesFromContent(content: unknown): SkillListingEntry[] | undefined {
  if (typeof content !== 'string') return undefined
  const entries: SkillListingEntry[] = []
  for (const line of content.split('\n')) {
    if (!line.startsWith('- ')) continue
    const body = line.slice(2)
    const separator = body.indexOf(': ')
    const name = separator === -1 ? body.replace(/:$/, '') : body.slice(0, separator)
    if (name === '') continue
    if (separator === -1) {
      entries.push({ name })
      continue
    }
    const description = body.slice(separator + 2)
    entries.push(description === '' ? { name } : { name, description })
  }
  return entries.length > 0 ? entries : undefined
}

function entriesFromNames(names: unknown): SkillListingEntry[] | undefined {
  if (!Array.isArray(names)) return undefined
  const entries: SkillListingEntry[] = []
  for (const name of names) {
    if (typeof name === 'string' && name !== '') entries.push({ name })
  }
  return entries.length > 0 ? entries : undefined
}

function entriesFromNarrowed(skills: unknown[]): SkillListingEntry[] | undefined {
  const entries: SkillListingEntry[] = []
  for (const skill of skills) {
    if (skill === null || typeof skill !== 'object' || Array.isArray(skill)) continue
    const s = skill as Record<string, unknown>
    if (typeof s['name'] !== 'string' || s['name'] === '') continue
    const description = s['description']
    entries.push(typeof description === 'string' && description !== ''
      ? { name: s['name'], description }
      : { name: s['name'] })
  }
  return entries.length > 0 ? entries : undefined
}
