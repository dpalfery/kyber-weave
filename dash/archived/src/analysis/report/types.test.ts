// The one rule `formatMeasured` exists to keep: an absent figure never renders as `0`
// (R14.1, R11.10).
//
// The failure this guards against is a plausible-looking zero. Every surface in this
// product reads the same `ContextReport`, so a formatter that turned an unmeasurable
// bucket into `0` would put a false measurement on the tray, in the terminal and in the
// Markdown at once, and nothing downstream could tell it from a real zero. The inverse
// matters just as much: a genuine `0` is a measurement and must render as one, which is
// why "render `—` when falsy" is the wrong implementation and is tested against here.

import { describe, expect, it } from 'vitest'

import { NOT_MEASURABLE, formatMeasured, measured, unmeasurable, type Measured } from './types.js'

describe('formatMeasured (R14.1)', () => {
  it('renders a measured value', () => {
    expect(formatMeasured(measured(1234))).toBe('1234')
  })

  it('renders a measured zero as 0, because a real zero is a measurement', () => {
    expect(formatMeasured(measured(0))).toBe('0')
  })

  it('renders an unmeasurable figure as the dash and its reason, never 0', () => {
    const out = formatMeasured(unmeasurable('harness reports no context window'))
    expect(out).toBe(`${NOT_MEASURABLE} (harness reports no context window)`)
    expect(out).not.toContain('0')
  })

  it('never renders 0 for any unmeasurable figure, whatever the reason says', () => {
    for (const reason of ['no counter', 'not sampled', 'window is 0', '0 sessions', '']) {
      const out = formatMeasured(unmeasurable(reason))
      expect(out.startsWith(NOT_MEASURABLE)).toBe(true)
      expect(out).not.toBe('0')
    }
  })

  it('appends a unit only when the figure carries one', () => {
    expect(formatMeasured(measured(512, 'tokens'))).toBe('512 tokens')
    expect(formatMeasured(measured(512))).toBe('512')
  })

  it('leaves the unit off an unmeasurable figure, which has no quantity to qualify', () => {
    expect(formatMeasured(unmeasurable('not sampled'))).toBe(`${NOT_MEASURABLE} (not sampled)`)
  })

  it('delegates value formatting but keeps the absent branch its own', () => {
    const asPercent = (n: number): string => `${Math.round(n * 100)}%`
    expect(formatMeasured(measured(0.72), asPercent)).toBe('72%')
    // A formatter that would have rendered the absent case as 0% never sees it.
    expect(formatMeasured(unmeasurable('no window'), asPercent)).toBe(`${NOT_MEASURABLE} (no window)`)
  })

  it('formats a non-numeric measured value through the same path', () => {
    const flag: Measured<boolean> = measured(false)
    expect(formatMeasured(flag)).toBe('false')
  })
})

describe('Measured constructors', () => {
  it('measured carries the value and omits unit when not given', () => {
    expect(measured(7)).toEqual({ value: 7 })
    expect(measured(7, 'tokens')).toEqual({ value: 7, unit: 'tokens' })
  })

  it('unmeasurable carries a null value and the reason, per the JSON contract (R11.10)', () => {
    expect(unmeasurable('no counter')).toEqual({ value: null, reason: 'no counter' })
  })

  it('serialises the absent case as null with a reason, not as a missing key', () => {
    const json = JSON.parse(JSON.stringify({ pressure: unmeasurable('no window') }))
    expect(json.pressure).toEqual({ value: null, reason: 'no window' })
    expect(Object.hasOwn(json.pressure, 'value')).toBe(true)
  })
})
