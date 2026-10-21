import { mkdtemp, mkdir, rm, utimes, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'

import { antigravitySkillDescription, skillDescriptionOf } from './antigravity-skill-text.js'

// Synthetic SKILL.md content only. The skill roots live under a temp HOME that the
// `homedir` mock returns, so the test never reads the developer's own ~/.gemini.

const homeRoot = vi.hoisted(() => ({ dir: '' }))

vi.mock('node:os', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:os')>()
  return { ...actual, homedir: () => homeRoot.dir }
})

async function writeSkill(dir: string, markdown: string): Promise<void> {
  await mkdir(dir, { recursive: true })
  await writeFile(join(dir, 'SKILL.md'), markdown)
}

describe('skillDescriptionOf', () => {
  it('reads an inline description', () => {
    expect(skillDescriptionOf('---\nname: a\ndescription: One line of text.\n---\nbody')).toBe('One line of text.')
  })

  it('keeps a colon inside an unquoted inline description', () => {
    expect(skillDescriptionOf('---\ndescription: Use when: the user asks\n---\n')).toBe('Use when: the user asks')
  })

  it('strips one pair of matching double or single quotes', () => {
    expect(skillDescriptionOf('---\ndescription: "Quoted: with a colon"\n---\n')).toBe('Quoted: with a colon')
    expect(skillDescriptionOf("---\ndescription: 'Single quoted'\n---\n")).toBe('Single quoted')
  })

  it('joins a folded > block with single spaces', () => {
    const markdown = '---\ndescription: >\n  first part\n  second part\nother: x\n---\n'
    expect(skillDescriptionOf(markdown)).toBe('first part second part')
  })

  it('joins a literal | block with single spaces too, and honours a >- chomping indicator', () => {
    expect(skillDescriptionOf('---\ndescription: |\n  line one\n  line two\n---\n')).toBe('line one line two')
    expect(skillDescriptionOf('---\ndescription: >-\n  kept\n---\n')).toBe('kept')
  })

  it('joins an indented continuation of a plain inline description', () => {
    expect(skillDescriptionOf('---\ndescription: plain start\n  continued here\n---\n')).toBe('plain start continued here')
  })

  it('reads a description from a CRLF frontmatter', () => {
    expect(skillDescriptionOf('---\r\ndescription: crlf text\r\n---\r\nbody\r\n')).toBe('crlf text')
  })

  it('is undefined when the frontmatter has no description', () => {
    expect(skillDescriptionOf('---\nname: a\n---\nbody')).toBeUndefined()
  })

  it('is undefined when the description is empty', () => {
    expect(skillDescriptionOf('---\ndescription:\n---\n')).toBeUndefined()
  })

  it('is undefined when there is no frontmatter at all', () => {
    expect(skillDescriptionOf('# A heading\ndescription: not frontmatter')).toBeUndefined()
  })
})

describe('antigravitySkillDescription', () => {
  beforeAll(async () => {
    homeRoot.dir = await mkdtemp(join(tmpdir(), 'agy-skill-text-'))
    await writeSkill(
      join(homeRoot.dir, '.gemini', 'config', 'skills', 'synthetic-alpha'),
      '---\nname: synthetic-alpha\ndescription: Synthetic alpha description.\n---\nBody\n',
    )
    // Reached by the name `.` (the skills root's own SKILL.md) and by `..` (the
    // config folder's SKILL.md, one level above the skills root). Neither may be read.
    await writeSkill(
      join(homeRoot.dir, '.gemini', 'config', 'skills'),
      '---\ndescription: Trap at the skills root, reached by the name dot.\n---\n',
    )
    await writeSkill(
      join(homeRoot.dir, '.gemini', 'config'),
      '---\ndescription: Trap above the skills root, reached by the name dot-dot.\n---\n',
    )
    // Reachable only by traversal: `<root>/../x/SKILL.md` from the config skills root.
    await writeSkill(
      join(homeRoot.dir, '.gemini', 'config', 'x'),
      '---\ndescription: Traversal trap that must never be returned.\n---\n',
    )
    await writeSkill(
      join(homeRoot.dir, '.gemini', 'config', 'skills', 'synthetic-shared'),
      '---\ndescription: From the config root.\n---\n',
    )
    await writeSkill(
      join(homeRoot.dir, '.gemini', 'antigravity', 'builtin', 'skills', 'synthetic-shared'),
      '---\ndescription: From the builtin root.\n---\n',
    )
    await writeSkill(
      join(homeRoot.dir, '.gemini', 'antigravity-cli', 'builtin', 'skills', 'synthetic-builtin'),
      '---\ndescription: Found only under an app builtin root.\n---\n',
    )
  })

  afterAll(async () => {
    await rm(homeRoot.dir, { recursive: true, force: true })
  })

  it('resolves a skill from the user skills root under the HOME it is given', () => {
    expect(antigravitySkillDescription('synthetic-alpha')).toBe('Synthetic alpha description.')
  })

  it('falls back to an app builtin root when the user root does not hold the skill', () => {
    expect(antigravitySkillDescription('synthetic-builtin')).toBe('Found only under an app builtin root.')
  })

  it('prefers the user skills root over an app builtin root that holds the same name', () => {
    expect(antigravitySkillDescription('synthetic-shared')).toBe('From the config root.')
  })

  it('is undefined for a skill no root holds', () => {
    expect(antigravitySkillDescription('synthetic-missing')).toBeUndefined()
  })

  it('is undefined for a traversal name, and never reads the file it would reach', () => {
    expect(antigravitySkillDescription('../x')).toBeUndefined()
    expect(antigravitySkillDescription('a b')).toBeUndefined()
  })

  it('is undefined for the names dot and dot-dot, which resolve to a SKILL.md above the name they should address', () => {
    // `join(root, '.', 'SKILL.md')` is the skills root's own SKILL.md, and
    // `join(root, '..', 'SKILL.md')` is one level above it. Both traps hold text.
    expect(antigravitySkillDescription('.')).toBeUndefined()
    expect(antigravitySkillDescription('..')).toBeUndefined()
  })

  it('finds a SKILL.md written after an earlier miss for the same name', async () => {
    expect(antigravitySkillDescription('synthetic-late')).toBeUndefined()

    await writeSkill(
      join(homeRoot.dir, '.gemini', 'config', 'skills', 'synthetic-late'),
      '---\ndescription: Written after the first miss.\n---\n',
    )

    expect(antigravitySkillDescription('synthetic-late')).toBe('Written after the first miss.')
  })

  it('picks up an edited description once the SKILL.md mtime changes', async () => {
    const dir = join(homeRoot.dir, '.gemini', 'config', 'skills', 'synthetic-edited')
    await writeSkill(dir, '---\ndescription: First wording.\n---\n')
    expect(antigravitySkillDescription('synthetic-edited')).toBe('First wording.')

    await writeFile(join(dir, 'SKILL.md'), '---\ndescription: Second wording.\n---\n')
    // An explicit later mtime, so the change is visible without relying on clock resolution.
    const later = new Date(Date.now() + 60_000)
    await utimes(join(dir, 'SKILL.md'), later, later)

    expect(antigravitySkillDescription('synthetic-edited')).toBe('Second wording.')
  })
})
