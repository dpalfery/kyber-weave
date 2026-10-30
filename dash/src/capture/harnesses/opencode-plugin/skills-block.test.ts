// T2 (R1): the pure `<available_skills>` parser shared by the plugin (T2)
// and the adapter (T7). Shape pinned in the V2 fixtures
// (fixtures/v2/runA.context.json): one `<skill>` per skill with `<id>`, `<name>`,
// `<description>`; a mid-session change re-emits the block after a
// "The available skills have changed" message.

import { describe, expect, it } from 'vitest'

import { parseAvailableSkills } from './skills-block.js'

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
