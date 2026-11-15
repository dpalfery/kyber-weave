// T2 (R1): the pure `<available_skills>` parser shared by the plugin (T2)
// and the adapter (T7). Shape pinned in the V2 fixtures
// (fixtures/v2/runA.context.json): one `<skill>` per skill with `<id>`, `<name>`,
// `<description>`; a mid-session change re-emits the block after a
// "The available skills have changed" message.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { capUtf8, headForCap, normalizeSkillId, parseAvailableSkills } from './skills-block.js'

const skill = (id: string): string =>
  `  <skill>\n    <id>${id}</id>\n    <name>${id}</name>\n    <description>does ${id}</description>\n  </skill>`

const block = (...ids: string[]): string =>
  `<available_skills>\n${ids.map(skill).join('\n')}\n</available_skills>`

describe('parseAvailableSkills', () => {
  it('returns the <id> values in listing order', () => {
    expect(parseAvailableSkills(`You are opencode.\n${block('review', 'deploy', 'plan')}\nMore text.`)).toEqual({
      ok: true,
      ids: ['review', 'deploy', 'plan'],
    })
  })

  it('trims whitespace around an id', () => {
    const text = '<available_skills>\n<skill>\n<id>  spaced  </id>\n<name>n</name>\n</skill>\n</available_skills>'
    expect(parseAvailableSkills(text)).toEqual({ ok: true, ids: ['spaced'] })
  })

  it('does not take ids from <name> or <description> children', () => {
    const text = '<available_skills>\n<skill>\n<id>real</id>\n<name>other</name>\n<description><id>fake</id></description>\n</skill>\n</available_skills>'
    const result = parseAvailableSkills(text)
    expect(result).toEqual({ ok: true, ids: ['real'] })
  })

  it('treats an empty block as a measured empty listing', () => {
    expect(parseAvailableSkills('<available_skills>\n</available_skills>')).toEqual({ ok: true, ids: [] })
  })

  it('lets the last block supersede an earlier one (changed-skills re-emission)', () => {
    const text = [
      block('old-a', 'old-b'),
      'The available skills have changed. This list supersedes the previous available skills list.',
      block('new-a'),
    ].join('\n')
    expect(parseAvailableSkills(text)).toEqual({ ok: true, ids: ['new-a'] })
  })

  it('names the failure when there is no block', () => {
    expect(parseAvailableSkills('no skills here')).toEqual({ ok: false, reason: 'no-block' })
    expect(parseAvailableSkills('')).toEqual({ ok: false, reason: 'no-block' })
  })

  it('names a malformed block instead of returning an empty list', () => {
    expect(parseAvailableSkills('<available_skills>\n<skill>\n<id>a</id>\n</skill>')).toEqual({
      ok: false,
      reason: 'malformed-block',
    })
    expect(parseAvailableSkills('<available_skills>\n<skill>\n<name>no id</name>\n</skill>\n</available_skills>')).toEqual({
      ok: false,
      reason: 'malformed-block',
    })
    expect(parseAvailableSkills('<available_skills>\n<skill>\n<id>  </id>\n</skill>\n</available_skills>')).toEqual({
      ok: false,
      reason: 'malformed-block',
    })
    expect(parseAvailableSkills('<available_skills>\n<skill>\n<id>a</id>\n</available_skills>')).toEqual({
      ok: false,
      reason: 'malformed-block',
    })
  })
})

describe('normalizeSkillId', () => {
  it('trims surrounding whitespace and treats nothing left, or a non-string, as absent', () => {
    expect(normalizeSkillId('  skill-a\n')).toBe('skill-a')
    expect(normalizeSkillId('   ')).toBeUndefined()
    expect(normalizeSkillId(7)).toBeUndefined()
  })

  it('cuts an id to 1024 UTF-8 bytes, in the listing as well as from the host', () => {
    expect(Buffer.byteLength(normalizeSkillId(` ${'x'.repeat(5_000_000)}`) ?? '', 'utf8')).toBe(1024)
    const listed = parseAvailableSkills(block('y'.repeat(100_000)))
    expect(listed.ok && listed.ids[0] !== undefined && Buffer.byteLength(listed.ids[0], 'utf8')).toBe(1024)
  })
})

describe('headForCap and capUtf8', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('never leaves half a surrogate pair at the cut', () => {
    const text = `${'a'.repeat(9)}\u{1F600}${'b'.repeat(20)}`
    expect(headForCap(text, 10)).toBe('a'.repeat(9))
    expect(headForCap('short', 10)).toBe('short')
    expect(capUtf8(text, 10)).toBe('a'.repeat(9))
    expect(capUtf8(text, 13)).toBe(`${'a'.repeat(9)}\u{1F600}`)
  })

  it('does not encode the whole of a long string to cut it', () => {
    const encode = vi.spyOn(Buffer, 'from')
    const cut = capUtf8('z'.repeat(5_000_000), 1024)
    const widest = Math.max(0, ...encode.mock.calls.map(([value]) => (typeof value === 'string' ? value.length : 0)))
    expect(Buffer.byteLength(cut, 'utf8')).toBe(1024)
    expect(widest).toBeLessThan(100_000)
  })
})
