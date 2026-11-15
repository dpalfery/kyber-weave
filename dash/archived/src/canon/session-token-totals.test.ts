import { describe, expect, it } from 'vitest'

import { CanonStore } from './store.js'

// Contract for the sessionTokenTotals read path: the `session` table carries
// two nullable, DEFAULT-less materialized columns (`summary_json`, the
// serialized `$.summary` object, and `problems_count`), and `upsertSession`
// already writes them. The reader must consult `summary_json` first and fall
// back to the payload-based `json_extract` query per row only when that row's
// `summary_json` is NULL (a legacy row that predates materialization, or a
// payload that genuinely had no summary).
//
// The writer keeps the column and the payload in agreement, so on a natural
// corpus the column-first read and the payload read return the same numbers —
// which means agreement alone cannot observe which source the reader consults.
// The first test below therefore diverges the two sources the way only a test
// can (rewriting the payload while leaving `summary_json` intact) to pin the
// read order. That divergence is a test instrument, never a production state.

function upsertMeasured(store: CanonStore, sessionId: string, input: number, output: number): void {
  store.upsertSession({
    sessionId,
    harness: 'cursor',
    label: null,
    isSubagent: false,
    parentSession: null,
    agentName: null,
    repo: null,
    branch: null,
    started: null,
    ended: null,
    payload: { summary: { total_input: input, total_output: output } },
  })
}

function summaryJsonOf(store: CanonStore, sessionId: string): string | null {
  const row = store
    .tokenCache()
    .prepare('SELECT summary_json AS summaryJson FROM session WHERE session_id = ?')
    .get(sessionId) as { summaryJson: string | null } | undefined
  return row?.summaryJson ?? null
}

describe('sessionTokenTotals reads the materialized summary column', () => {
  it('serves materialized totals from summary_json when the payload no longer carries a summary', () => {
    // RED for the column-first change: the current implementation reads the
    // payload with a per-row `json_extract`, so once the payload's summary is
    // gone it reports unknown even though the materialized column holds the
    // totals. After the change the populated column wins and this passes.
    const store = new CanonStore(':memory:')
    upsertMeasured(store, 'sess-materialized', 120, 34)

    store
      .tokenCache()
      .prepare('UPDATE session SET payload = ? WHERE session_id = ?')
      .run(JSON.stringify({ note: 'payload rewritten without a summary' }), 'sess-materialized')

    expect(summaryJsonOf(store, 'sess-materialized')).not.toBeNull()
    expect(store.sessionTokenTotals('sess-materialized')).toEqual({ input: 120, output: 34 })
    store.close()
  })

  it('keeps the materialized column in agreement with the payload-derived totals', () => {
    // Writer-side invariant the column-first reader relies on: what
    // `upsertSession` materializes must equal what the payload-derived query
    // would produce for the same payload, so neither source drifts.
    const store = new CanonStore(':memory:')
    upsertMeasured(store, 'sess-agree', 120, 34)

    const column = JSON.parse(summaryJsonOf(store, 'sess-agree')!) as {
      total_input: number
      total_output: number
    }
    expect(column).toMatchObject({ total_input: 120, total_output: 34 })
    expect(store.sessionTokenTotals('sess-agree')).toEqual({
      input: column.total_input,
      output: column.total_output,
    })
    store.close()
  })

  it('falls back to the payload for a legacy row whose summary_json is NULL', () => {
    // A row written before materialization existed carries NULL, never an
    // empty object or a zero count: the reader must derive from the payload.
    const store = new CanonStore(':memory:')
    store
      .tokenCache()
      .prepare('INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)')
      .run(
        'sess-legacy',
        'cursor',
        JSON.stringify({ summary: { total_input: 10, total_output: 20 } }),
      )

    expect(summaryJsonOf(store, 'sess-legacy')).toBeNull()
    expect(store.sessionTokenTotals('sess-legacy')).toEqual({ input: 10, output: 20 })
    store.close()
  })

  it('keeps a not_measurable total as undefined, never 0 and never null', () => {
    // Honesty property: a harness that exports no token measurement leaves an
    // object shaped as not_measurable where a number would sit. Coercing it to
    // 0 would invent a measured zero for the delegation denominator, and null
    // would change the "no measurement" signal the caller distinguishes — so
    // the accessor reports unknown as undefined. This holds whether the row is
    // materialized (as here, via upsertSession) or legacy.
    const store = new CanonStore(':memory:')
    store.upsertSession({
      sessionId: 'sess-unmeasured',
      harness: 'cursor',
      label: null,
      isSubagent: false,
      parentSession: null,
      agentName: null,
      repo: null,
      branch: null,
      started: null,
      ended: null,
      payload: {
        summary: {
          total_input: { availability: 'not_measurable', reason: 'no counter exported' },
          total_output: 7,
        },
      },
    })

    const totals = store.sessionTokenTotals('sess-unmeasured')
    expect(totals).toBeUndefined()
    expect(totals).not.toBe(0)
    expect(totals).not.toBe(null)
    store.close()
  })

  it('applies the fallback per row in a mixed corpus of materialized and legacy rows', () => {
    // The fallback is per row, not per query: legacy rows derive from the
    // payload while materialized rows are unaffected, and unknown stays
    // unknown alongside both.
    const store = new CanonStore(':memory:')
    upsertMeasured(store, 'sess-new', 120, 34)
    store
      .tokenCache()
      .prepare('INSERT INTO session (session_id, harness, payload) VALUES (?, ?, ?)')
      .run(
        'sess-old',
        'cursor',
        JSON.stringify({ summary: { total_input: 10, total_output: 20 } }),
      )
    store.upsertSession({
      sessionId: 'sess-unmeasured',
      harness: 'cursor',
      label: null,
      isSubagent: false,
      parentSession: null,
      agentName: null,
      repo: null,
      branch: null,
      started: null,
      ended: null,
      payload: {
        summary: {
          total_input: { availability: 'not_measurable', reason: 'no counter exported' },
          total_output: 7,
        },
      },
    })

    expect(summaryJsonOf(store, 'sess-old')).toBeNull()
    expect(store.sessionTokenTotals('sess-new')).toEqual({ input: 120, output: 34 })
    expect(store.sessionTokenTotals('sess-old')).toEqual({ input: 10, output: 20 })
    expect(store.sessionTokenTotals('sess-unmeasured')).toBeUndefined()
    expect(store.sessionTokenTotals('sess-missing')).toBeUndefined()
    store.close()
  })
})
