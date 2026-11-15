// P2.6 — Antigravity statusline bridge context window (G1-Q2 = (b)).
//
// The user's bridge posts OTLP chat spans; when the bridge carries
// `declaredContextWindow` the session names a declared window. Without it the
// bridge must not invent a context window (no 200K default, no catalog).

import { describe, expect, it } from 'vitest'

import { antigravityAdapter } from './adapters/antigravity.js'
import { rawSpan } from './adapters/testing.js'
import { contextLimitOf, DECLARED_CONTEXT_LIMIT_KEY } from './context-window.js'
import bridgeWithDeclared from './fixtures/agy-bridge/bridge-with-declared-window.json' with { type: 'json' }
import bridgeBase from './fixtures/agy-bridge/bridge-base.json' with { type: 'json' }

const DECLARED_WINDOW = 1_048_576

function bridgeRecord(attributes: Record<string, unknown>) {
  return antigravityAdapter.normalize(
    rawSpan({
      spanId: 'bridge-span-1',
      traceId: 'bridge-trace-1',
      source: 'agy',
      attributes,
    }),
  )
}

describe('Antigravity bridge context window (P2.6)', () => {
  it('resolves a bridge span with declaredContextWindow as declared', () => {
    const record = bridgeRecord(bridgeWithDeclared as Record<string, unknown>)
    expect(record.raw).toMatchObject({ [DECLARED_CONTEXT_LIMIT_KEY]: DECLARED_WINDOW })
    expect(contextLimitOf([record])).toEqual({
      contextLimit: DECLARED_WINDOW,
      contextLimitSource: 'declared',
    })
  })

  it('yields no window when the bridge span omits declaredContextWindow', () => {
    const record = bridgeRecord(bridgeBase as Record<string, unknown>)
    expect(record.raw).not.toHaveProperty(DECLARED_CONTEXT_LIMIT_KEY)
    const window = contextLimitOf([record])
    expect(window.contextLimitSource).not.toBe('declared')
    expect(window.contextLimitSource).not.toBe('reported')
    // The bridge emits no harness-reported window; the 200K default must not stand in.
    expect(window.contextLimitSource).not.toBe('default')
  })
})
