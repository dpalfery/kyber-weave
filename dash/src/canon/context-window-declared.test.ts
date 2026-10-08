// T2 (Seam 1: declared context-window provenance) — RED contract.
// A harness-declared window is provenance of its own: reported beats
// declared, declared beats the default, and the compaction-hazard detector
// fires against a declared window as an inferred reading (D12).

import { describe, expect, it } from 'vitest'

import { detectCompactionHazard } from '../analysis/findings.js'
import type { ParsedProviderCall } from '../providers/types.js'
import { synthesizeCall } from '../synth/synth.js'
import type { ReaderTurn } from '../synth/readers/types.js'
import { DEFAULT_CONTEXT_LIMIT, contextLimitOf } from './context-window.js'
import { tokens, turn } from './fixtures/records.js'

/** The dedicated declared key: distinct from every CONTEXT_LIMIT_KEYS entry. */
const DECLARED_KEY = 'declaredContextWindow'

function declaredTurn(spanId: string, window: number, reportedInput: number) {
  return turn(spanId, [], {
    op: 'llm.invoke',
    tokens: tokens({ freshInput: reportedInput, reportedInput }),
    raw: { [DECLARED_KEY]: window },
  })
}

function baseCall(): ParsedProviderCall {
  return {
    provider: 'claude',
    model: 'claude-sonnet-4.5',
    inputTokens: 1_000,
    outputTokens: 240,
    cacheCreationInputTokens: 120,
    cacheReadInputTokens: 3_800,
    cachedInputTokens: 3_800,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0.0123,
    tools: ['Read'],
    bashCommands: [],
    timestamp: '2026-08-29T12:00:00.000Z',
    speed: 'standard',
    deduplicationKey: 'claude:s-1:m-1',
    userMessage: 'run the parity check',
    sessionId: 's-1',
  }
}

describe('declared context-window provenance (T2)', () => {
  it('reads a declared-only record as a declared window', () => {
    const window = contextLimitOf([declaredTurn('decl-1', 100_000, 10_000)])
    expect(window).toEqual({ contextLimit: 100_000, contextLimitSource: 'declared' })
  })

  it('prefers a reported window over a declared one on the same record', () => {
    const record = turn('both-1', [], {
      op: 'llm.invoke',
      tokens: tokens({ freshInput: 10_000, reportedInput: 10_000 }),
      raw: { contextWindow: 200_000, [DECLARED_KEY]: 100_000 },
    })
    const window = contextLimitOf([record])
    expect(window).toEqual({ contextLimit: 200_000, contextLimitSource: 'reported' })
  })

  it('prefers a reported window even when the declared record comes first', () => {
    const window = contextLimitOf([
      declaredTurn('decl-first', 100_000, 10_000),
      turn('rep-second', [], {
        op: 'llm.invoke',
        tokens: tokens({ freshInput: 10_000, reportedInput: 10_000 }),
        raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      }),
    ])
    expect(window).toEqual({ contextLimit: 200_000, contextLimitSource: 'reported' })
  })

  it('keeps the first-record rule within the declared class', () => {
    const window = contextLimitOf([
      declaredTurn('decl-a', 100_000, 10_000),
      declaredTurn('decl-b', 300_000, 10_000),
    ])
    expect(window).toEqual({ contextLimit: 100_000, contextLimitSource: 'declared' })
  })

  it('falls back to the unchanged default when nothing names a window', () => {
    const record = turn('bare-1', [], {
      op: 'llm.invoke',
      tokens: tokens({ freshInput: 10_000, reportedInput: 10_000 }),
    })
    expect(contextLimitOf([record])).toEqual({
      contextLimit: DEFAULT_CONTEXT_LIMIT,
      contextLimitSource: 'default',
    })
  })

  it('carries ReaderTurn.declaredContextWindow into raw under the declared key only', () => {
    const readerTurn = { parts: [], declaredContextWindow: 128_000 } as ReaderTurn
    const record = synthesizeCall(baseCall(), undefined, readerTurn)
    expect(record.raw as Record<string, unknown>).toMatchObject({ [DECLARED_KEY]: 128_000 })
    expect(record.raw).not.toHaveProperty('contextWindow')
  })

  it('fires the compaction-hazard finding against a declared window as inferred (D12)', () => {
    // 85% of the declared 100,000 window is 85,000; the 90,000 peak
    // exceeds it by 5,000 — measured spend, inferred percentage.
    const findings = detectCompactionHazard({
      records: [declaredTurn('decl-early', 100_000, 50_000), declaredTurn('decl-peak', 100_000, 90_000)],
    })
    expect(findings).toHaveLength(1)
    const finding = findings[0]!
    expect(finding.measurementClass).toBe('inferred')
    expect(finding.mechanism).toMatch(/declared window/i)
    expect(finding.payload?.contextLimitSource).toBe('declared')
  })

  it('still yields no finding for a default-window session', () => {
    const bare = (spanId: string, reportedInput: number) =>
      turn(spanId, [], {
        op: 'llm.invoke',
        tokens: tokens({ freshInput: reportedInput, reportedInput }),
      })
    const findings = detectCompactionHazard({
      records: [bare('bare-early', 100_000), bare('bare-peak', 190_000)],
    })
    expect(findings).toHaveLength(0)
  })
})
