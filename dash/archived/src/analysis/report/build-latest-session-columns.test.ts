import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../../canon/store.js'
import { KyberBridge } from '../../server/bridge.js'
import { buildContextReport } from './build.js'

// RED contract for the report's latest-session read (change C):
// `buildLatestSession` (build.ts) calls `bridge.getSessionPayload` for the
// latest session in scope on every `/api/kyber/report` request — one full
// payload read (up to 15.3 MB) per request, on a route the tray polls. The
// change builds the latest-session block from columns instead, while the
// report response stays byte-identical — the tray consumes it verbatim.
//
// Why a real store, not the stub bridge `build.test.ts` uses: the derivation
// is already covered by those stubs, but the SQL pin needs the real read
// path — only a real `CanonStore` plus a real `KyberBridge` issues the
// statements GREEN must stop issuing. Nothing downstream is recomputed here;
// the expected block below is today's derivation, transcribed field by field.

const NOW = new Date('2026-10-20T12:00:00.000Z')
const LATEST_STARTED = '2026-10-20T11:00:00.000Z'
const LATEST_ENDED = '2026-10-20T11:55:00.000Z'
const OLDER_STARTED = '2026-10-18T10:00:00.000Z'
const OLDER_ENDED = '2026-10-18T11:00:00.000Z'

function latestPayload(): Record<string, unknown> {
  return {
    id: 'sess-latest',
    harness: 'copilot',
    summary: {
      turn_count: 4,
      request_count: 4,
      total_input: 800,
      total_output: 400,
      cost: { basis: 'published', status: 'priced', value: 0.04, currency: 'USD' },
      models: ['gpt-4o'],
    },
    problems: [],
    context: {
      measurable: true,
      contextLimit: 200000,
      contextLimitSource: 'reported',
      turns: [
        {
          index: 3,
          pressure: 0.42,
          buckets: {
            system_prompt: 100,
            tool_definitions: 200,
            instruction_context: 300,
            conversation_history: 400,
            tool_result_content: 500,
          },
          residual: { tokens: 25 },
          toolDefinitionsByServer: { 'built-in': 150 },
        },
      ],
      last: { reported_input: 84000, buckets: {} },
      flaggedTurns: [1],
    },
    turns: [{ index: 3 }],
  }
}

function seedReportStore(store: CanonStore): void {
  store.upsertSession({
    sessionId: 'sess-older',
    harness: 'copilot',
    label: 'Older Session',
    repo: 'kyber-weave',
    branch: 'main',
    started: OLDER_STARTED,
    ended: OLDER_ENDED,
    payload: {
      id: 'sess-older',
      harness: 'copilot',
      summary: { turn_count: 2, request_count: 2, total_input: 200, total_output: 100 },
      problems: [],
    },
  })
  store.upsertSession({
    sessionId: 'sess-latest',
    harness: 'copilot',
    label: 'Latest Session',
    repo: 'kyber-weave',
    branch: 'main',
    started: LATEST_STARTED,
    ended: LATEST_ENDED,
    payload: latestPayload(),
  })
}

// Today's derivation for the fixture above, transcribed from `extractContext`
// + `buildLatestSession`: a reported 200k window, the last analysis turn's
// measured buckets and residual, the flagged turn carried through.
const EXPECTED_LATEST_SESSION = {
  sessionId: 'sess-latest',
  harness: 'copilot',
  project: 'kyber-weave',
  lastActivityAt: LATEST_ENDED,
  turnCount: 4,
  view: 'session/sess-latest',
  latestTurn: {
    index: 3,
    pressure: { value: 0.42 },
    contextWindow: { value: 200000, unit: 'tokens' },
    buckets: {
      system_prompt: { value: 100, unit: 'tokens' },
      tool_definitions: { value: 200, unit: 'tokens' },
      instruction_context: { value: 300, unit: 'tokens' },
      conversation_history: { value: 400, unit: 'tokens' },
      tool_result_content: { value: 500, unit: 'tokens' },
    },
    residual: { value: 25, unit: 'tokens' },
    cacheInvalidation: false,
  },
  toolDefinitionSources: [{ source: 'built-in', tokens: { value: 150, unit: 'tokens' } }],
  cacheInvalidationTurns: [1],
}

function sessionStatements(calls: unknown[][]): string[] {
  return calls
    .map((args) => args[0])
    .filter((sql): sql is string => typeof sql === 'string')
    .filter((sql) => sql.includes('FROM session'))
}

/** A whole-payload select: the bare `payload` column or `*` off `session`. */
function selectsWholePayload(sql: string): boolean {
  return /SELECT\s+payload\b/i.test(sql) || /SELECT\s+\*/i.test(sql)
}

describe('buildContextReport latestSession without the payload blob (change C)', () => {
  it('serves the latestSession block with the same fields and figures', () => {
    // Characterization: passes today — the derivation is correct, only the
    // read is not. After GREEN the column-built block must equal this, field
    // for field, because the tray consumes the report verbatim.
    const store = new CanonStore(':memory:')
    try {
      seedReportStore(store)
      const bridge = new KyberBridge({
        store,
        ratesPath: join(tmpdir(), 'nonexistent-report-rates.json'),
      })
      try {
        const report = buildContextReport(
          bridge,
          { days: 7 },
          { sections: ['latestSession'], now: NOW },
        )
        expect(report.latestSession).toEqual(EXPECTED_LATEST_SESSION)
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('builds the latestSession block without selecting the session whole payload blob', () => {
    // RED: `buildLatestSession` reads the latest session via
    // `getSessionPayload`, which issues `SELECT payload FROM session`. After
    // GREEN no `FROM session` statement may return the blob. Scoped to the
    // `latestSession` section so the only payload read in play is the
    // latest session's own — the pin is exactly change C's "no full-payload
    // read for the latest session".
    const store = new CanonStore(':memory:')
    try {
      seedReportStore(store)
      const bridge = new KyberBridge({
        store,
        ratesPath: join(tmpdir(), 'nonexistent-report-rates.json'),
      })
      const spy = vi.spyOn(store.getDatabase(), 'prepare')
      try {
        const report = buildContextReport(
          bridge,
          { days: 7 },
          { sections: ['latestSession'], now: NOW },
        )
        expect(report.latestSession?.sessionId).toBe('sess-latest')
      } finally {
        const calls = [...spy.mock.calls]
        spy.mockRestore()
        bridge.close()
        const probed = sessionStatements(calls)
        expect(probed.length).toBeGreaterThan(0)
        expect(probed.filter(selectsWholePayload)).toEqual([])
      }
    } finally {
      store.close()
    }
  })

  it('serves the same latestSession block inside the full default report', () => {
    // Characterization: the tray requests the default sections, so the
    // latest-session block pinned above must survive the full document —
    // unchanged by the coverage, findings, harness, and cost sections around
    // it — today and after GREEN.
    const store = new CanonStore(':memory:')
    try {
      seedReportStore(store)
      const bridge = new KyberBridge({
        store,
        ratesPath: join(tmpdir(), 'nonexistent-report-rates.json'),
      })
      try {
        const report = buildContextReport(bridge, { days: 7 }, { now: NOW })
        expect(report.latestSession).toEqual(EXPECTED_LATEST_SESSION)
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })
})
