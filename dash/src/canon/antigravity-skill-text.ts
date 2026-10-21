// Antigravity lists skills by name only (`Active Skills: a, b`), so the
// description a skill row shows is read from the skill's own SKILL.md on this
// machine. A skill no root holds has no description, never an invented one.

import { closeSync, fstatSync, openSync, readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

/** A SKILL.md over this size has no description read from it: a skill file is a short document. */
export const MAX_SKILL_FILE_BYTES = 256 * 1024

const APP_DIRS = ['antigravity', 'antigravity-ide', 'antigravity-cli'] as const
/**
 * Descriptions read so far, by SKILL.md path. An entry is good only while the file's
 * mtime and size are unchanged, so an edited SKILL.md is read again (F4). A miss is
 * never stored: an absent file is re-checked on a later call, and that costs one stat.
 */
const cache = new Map<string, { mtimeMs: number; size: number; description: string | undefined }>()

function skillRoots(): string[] {
  const gemini = join(homedir(), '.gemini')
  return [
    join(gemini, 'config', 'skills'),
    ...APP_DIRS.flatMap((dir) => [join(gemini, dir, 'skills'), join(gemini, dir, 'builtin', 'skills')]),
  ]
}

/** The `description` of a SKILL.md frontmatter, folded scalars joined with single spaces. */
export function skillDescriptionOf(markdown: string): string | undefined {
  const front = /^---\r?\n([\s\S]*?)\r?\n---/.exec(markdown)?.[1]
  if (front === undefined) return undefined
  const lines = front.split(/\r?\n/)
  const at = lines.findIndex((line) => /^description:/.test(line))
  if (at < 0) return undefined
  const inline = lines[at]!.slice('description:'.length).trim()
  const folded = /^[>|][+-]?$/.test(inline)
  const parts = folded || inline === '' ? [] : [inline.replace(/^(['"])(.*)\1$/, '$2')]
  for (const line of lines.slice(at + 1)) {
    if (line.length > 0 && !/^\s/.test(line)) break
    if (line.trim().length > 0) parts.push(line.trim())
  }
  const text = parts.join(' ').trim()
  return text.length > 0 ? text : undefined
}

/** The description one SKILL.md file holds, read through the mtime-keyed cache. */
function descriptionAt(path: string): string | undefined {
  // Open once and check the open handle, never the path, so the file cannot change
  // between the check and the read.
  let fd: number
  try {
    fd = openSync(path, 'r')
  } catch {
    return undefined
  }
  try {
    const stats = fstatSync(fd)
    if (stats.size > MAX_SKILL_FILE_BYTES) return undefined
    const hit = cache.get(path)
    if (hit !== undefined && hit.mtimeMs === stats.mtimeMs && hit.size === stats.size) return hit.description
    const description = skillDescriptionOf(readFileSync(fd, 'utf8'))
    cache.set(path, { mtimeMs: stats.mtimeMs, size: stats.size, description })
    return description
  } catch {
    return undefined
  } finally {
    closeSync(fd)
  }
}

/** The description of a named Antigravity skill, or undefined when no SKILL.md holds one. */
export function antigravitySkillDescription(name: string): string | undefined {
  // `.` and `..` match the name pattern, but join() resolves them to a SKILL.md above
  // the skill, so they are refused as names, as skillNameFromPath refuses them (F4).
  if (name === '.' || name === '..' || !/^[\w.-]+$/.test(name)) return undefined
  for (const root of skillRoots()) {
    const found = descriptionAt(join(root, name, 'SKILL.md'))
    if (found !== undefined) return found
  }
  return undefined
}
