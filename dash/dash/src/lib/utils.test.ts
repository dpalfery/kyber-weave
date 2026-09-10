import { describe, expect, it } from 'vitest'

import { fmtRunTimestamp, shortRunId } from './utils'

describe('shortRunId', () => {
  // Regression: the runs table truncated with `runId.slice(0, 10)`. Every
  // derived id begins `derived:<harness>:`, so all 56 copilot runs rendered as
  // the identical string `derived:co` — a column that identified nothing.
  it('keeps runs on one harness distinguishable', () => {
    const a = shortRunId('derived:copilot:dc7cd0b51e77e3480ea252953c517398', 'copilot')
    const b = shortRunId('derived:copilot:5db27367678116fd806226f0afe7cacd', 'copilot')

    expect(a).not.toEqual(b)
    expect(a).not.toContain('derived')
    expect(a.startsWith('derived:co')).toBe(false)
  })

  it('drops the redundant derived prefix that the grouping column already states', () => {
    expect(shortRunId('derived:copilot:kyber-weave:1788414598871', 'copilot')).toContain('kyber')
  })

  it('strips the harness segment even when the harness is not supplied', () => {
    expect(shortRunId('derived:gemini:abc123')).toBe('abc123')
  })

  it('leaves an explicit harness-emitted id alone when it is already short', () => {
    expect(shortRunId('run-42', 'copilot')).toBe('run-42')
  })

  it('elides the middle rather than a whole end, so both ends stay readable', () => {
    const short = shortRunId('derived:copilot:0123456789abcdefghijklmnop', 'copilot')
    expect(short).toContain('…')
    expect(short.startsWith('012345678')).toBe(true)
    expect(short.endsWith('hijklmnop'.slice(-8))).toBe(true)
  })

  it('strips the synthesized trace prefix that file-sourced runs share', () => {
    const a = shortRunId('derived:claude-code:synth:claude:d625dd4c-74ef-4cc6-8efe-4828a71a7541', 'claude-code')
    const b = shortRunId('derived:claude-code:synth:claude:a20aa21b-8290-45f1-9c11-0000e4598e54', 'claude-code')
    expect(a).not.toContain('synth')
    expect(a).not.toEqual(b)
  })

  it('never returns an empty label', () => {
    expect(shortRunId('derived:copilot:', 'copilot')).toBe('derived:copilot:')
  })
})

describe('fmtRunTimestamp', () => {
  it('splits a recorded start into date and time halves with the ISO form for hover', () => {
    const out = fmtRunTimestamp('2026-09-05T02:32:54.568Z')
    expect(out.date).not.toBe('—')
    expect(out.time).not.toBe('')
    expect(out.full).toBe('2026-09-05T02:32:54.568Z')
  })

  it('states absence instead of fabricating a date', () => {
    for (const absent of [null, undefined, '']) {
      const out = fmtRunTimestamp(absent)
      expect(out.date).toBe('—')
      expect(out.time).toBe('')
      expect(out.full).toMatch(/No start timestamp/)
    }
  })

  it('reports an unparseable timestamp as unparseable rather than as Invalid Date', () => {
    const out = fmtRunTimestamp('not-a-date')
    expect(out.date).toBe('—')
    expect(out.full).toContain('not-a-date')
  })
})
