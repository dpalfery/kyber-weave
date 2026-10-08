// P2.R — rollup guards: window provenance requires a usable limit; compaction
// pressure must not silently divide by the 200K default when nothing named a window.

import { describe, expect, it } from 'vitest'

import {
  compactionPressure,
  DEFAULT_CONTEXT_WINDOW_LIMIT,
  isSignalMeasurable,
} from '../analysis/signals.js'
import { digestSessionPayloads, evidenceOf } from './harnesses.js'
import type { AsadSessionPayload } from './sessions.js'

function sessionPayload(
  context: Record<string, unknown>,
): AsadSessionPayload {
  return {
    session_id: 'sess-guard',
    harness: 'claude-code',
    summary: { turn_count: 1, total_input: 1000, total_output: 100 },
    context,
  } as AsadSessionPayload
}

describe('P2.R: digestSessionPayloads window source guard', () => {
  it('does not record a window source when the claim names no finite positive limit', () => {
    const digest = digestSessionPayloads([
      sessionPayload({
        measurable: true,
        contextLimitSource: 'reported',
        turns: [{ index: 1, pressure: 0.4 }],
      }),
      sessionPayload({
        measurable: true,
        contextLimitSource: 'declared',
        contextLimit: 0,
        turns: [{ index: 1, pressure: 0.2 }],
      }),
    ])

    const { windowSources } = evidenceOf(digest)
    expect(windowSources.has('reported')).toBe(false)
    expect(windowSources.has('declared')).toBe(false)
    expect([...windowSources]).toEqual([])
  })

  it('records a window source only alongside a finite positive context limit', () => {
    const digest = digestSessionPayloads([
      sessionPayload({
        measurable: true,
        contextLimit: 200_000,
        contextLimitSource: 'reported',
        turns: [{ index: 1, pressure: 0.25 }],
      }),
    ])

    expect([...evidenceOf(digest).windowSources]).toEqual(['reported'])
  })
})

describe('P2.R: compactionPressure without window provenance', () => {
  it('refuses measurement when no source and no limit would fall back to the 200K default', () => {
    const res = compactionPressure({ peakInputTokens: 100_000 })

    expect(res.status).toBe('not_measurable')
    expect(isSignalMeasurable(res)).toBe(false)
    if (res.status === 'not_measurable') {
      expect(res.reason).toMatch(/context window|unmeasurable|no source/i)
    }

    const withExplicitDefaultLimit = compactionPressure({
      peakInputTokens: 100_000,
      contextLimit: DEFAULT_CONTEXT_WINDOW_LIMIT,
    })
    expect(withExplicitDefaultLimit.status).toBe('measured')
    if (isSignalMeasurable(withExplicitDefaultLimit)) {
      expect(withExplicitDefaultLimit.denominator).toBe(DEFAULT_CONTEXT_WINDOW_LIMIT)
    }
  })
})
