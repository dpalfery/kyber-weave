// Antigravity lists skills by name only (`Active Skills: a, b`), so the
// description a skill row shows is read from the skill's own SKILL.md on this
// machine. A skill no root holds has no description, never an invented one.

import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

const APP_DIRS = ['antigravity', 'antigravity-ide', 'antigravity-cli'] as const
const cache = new Map<string, string | undefined>()

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

/** The description of a named Antigravity skill, or undefined when no SKILL.md holds one. */
export function antigravitySkillDescription(name: string): string | undefined {
  if (!/^[\w.-]+$/.test(name)) return undefined
  if (cache.has(name)) return cache.get(name)
  let found: string | undefined
  for (const root of skillRoots()) {
    try {
      found = skillDescriptionOf(readFileSync(join(root, name, 'SKILL.md'), 'utf8'))
    } catch {
      continue
    }
    if (found !== undefined) break
  }
  cache.set(name, found)
  return found
}
