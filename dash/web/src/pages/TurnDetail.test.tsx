import { describe, expect, it } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { TurnDetail, asMeasuredFigure, findSessionTurnRow, resolveTurnSessionId, turnRowFigures } from './TurnDetail.js'
import type { KyberSessionTurnRow } from '../lib/kyberApi.js'

// The turn surface asked the content route for a run id. Every derived run is
// keyed `derived:<harness>:<session>` — 1,455 of 1,455 on the measured corpus —
// so the route 404'd for all of them and the page rendered "Session or turn
// content not found" with no context bands.
describe('resolveTurnSessionId', () => {
  const run = {
    executions: [
      { executionId: 'exec-1', sessionId: 'sess-1' },
      { executionId: 'exec-2', sessionId: 'sess-2' },
    ],
  }

  it('resolves the named execution to its session', () => {
    expect(resolveTurnSessionId(run, 'exec-2')).toBe('sess-2')
  })

  it('falls back to the run\'s first execution when the link names none', () => {
    // A turn reached from a finding carries no execution.
    expect(resolveTurnSessionId(run, undefined)).toBe('sess-1')
  })

  it('never returns a run id', () => {
    const derived = { executions: [{ executionId: 'exec-1', sessionId: 'sess-1' }] }

    expect(resolveTurnSessionId(derived, undefined)).not.toMatch(/^derived:/)
  })

  it('uses the execution id when the execution recorded no session', () => {
    const noSession = { executions: [{ executionId: 'exec-only' }] }

    expect(resolveTurnSessionId(noSession, 'exec-only')).toBe('exec-only')
  })

  it('keeps the requested execution when it is not in the run yet', () => {
    // The run query may not have resolved; the execution id is still the better
    // guess than the run id, since executions and sessions share their key.
    expect(resolveTurnSessionId(undefined, 'exec-9')).toBe('exec-9')
  })

  it('is undefined when nothing names an execution', () => {
    expect(resolveTurnSessionId(undefined, undefined)).toBeUndefined()
    expect(resolveTurnSessionId({ executions: [] }, undefined)).toBeUndefined()
  })
})

// The inspector header's measured counters must describe the same 0-based turn
// the content route resolved — never a neighbor (issue #184).
describe('findSessionTurnRow', () => {
  const rows: KyberSessionTurnRow[] = [
    { index: 0, input: 850, output: 120, fresh: 40, cache_read: 810 },
    { index: 1, input: 900, output: 130, fresh: 50, cache_read: 860 },
  ]

  it('resolves a 0-based index to exactly that row', () => {
    expect(findSessionTurnRow(rows, 1)?.input).toBe(900)
  })

  it('resolves a legacy 1-based `turn` row via `turn - 1`', () => {
    const legacy: KyberSessionTurnRow[] = [
      { index: 0, input: 1 },
      { turn: 2, input: 2 } as KyberSessionTurnRow,
    ]
    expect(findSessionTurnRow(legacy, 1)?.input).toBe(2)
  })

  it('returns undefined past the end instead of the last row', () => {
    expect(findSessionTurnRow(rows, 2)).toBeUndefined()
    expect(findSessionTurnRow(undefined, 0)).toBeUndefined()
  })
})

describe('turnRowFigures', () => {
  it('passes measured counters through and nulls anything unmeasured', () => {
    expect(
      turnRowFigures({ index: 0, input: 850, output: 120, fresh: 40, cache_read: 810 }),
    ).toEqual({ input: 850, output: 120, fresh: 40, cacheRead: 810 })
    expect(turnRowFigures(undefined)).toBeUndefined()
  })

  it('asMeasuredFigure accepts only finite numbers', () => {
    expect(asMeasuredFigure(42)).toBe(42)
    expect(asMeasuredFigure(undefined)).toBeNull()
    expect(asMeasuredFigure(null)).toBeNull()
    expect(asMeasuredFigure('850')).toBeNull()
    expect(asMeasuredFigure(NaN)).toBeNull()
  })
})

// Issue #184: transport is 0-based `turnIndex`; the human-facing header is 1-based.
describe('TurnDetail header numbering', () => {
  it('renders a 1-based Turn heading for a 0-based turnIndex', () => {
    const qc = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    })
    const html = renderToStaticMarkup(
      React.createElement(
        QueryClientProvider,
        { client: qc },
        React.createElement(TurnDetail, { runId: 'run-184', turnIndex: 5 }),
      ),
    )

    expect(html).toContain('data-testid="page-turn"')
    expect(html).toContain('Turn 6')
  })
})
