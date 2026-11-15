/**
 * The session payload carries a server-computed cost-basis mismatch.
 *
 * Architecture rule R1: the web dashboard is a display layer and must not
 * import engine value code from `dash/src`. Today `SessionCostPanel` calls
 * `sumCosts` in the browser to decide whether a session's cost figures share a
 * basis. T20 moves that decision to the server and the panel renders whatever
 * this field carries, so the payload is where the answer has to live.
 *
 * FIELD AND SHAPE PINNED HERE — `SessionPayload.costBasisMismatch`:
 *
 *   null   when the session's cost blocks agree on one basis, or when nothing
 *          was summed at all (no priced figure is not a mismatch).
 *
 *   {
 *     code: 'COST_BASIS_MISMATCH',
 *     message: string,     // the problem text, rendered verbatim by the panel
 *     bases: string[],     // every basis found across the session's cost blocks, sorted
 *     totalsByBasis: Record<string, number | null>,
 *                          // one summed figure per basis — the two totals a
 *                          // display layer must never blend into one figure
 *   }
 *
 * `null` rather than an absent key: "no mismatch" and "nothing was computed"
 * are different claims, and a panel reading the field should never have to
 * guess which one an omitted key meant.
 *
 * These tests drive the payload the way production does: records into
 * `CanonStore`, `buildSessions`, then `getSessionPayload`. Persistence of a
 * session row goes through `buildSessions` only — it is what computes the
 * verdict and writes it onto the row — so that is the only path worth pinning.
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { buildSessions } from '../canon/sessions.js'
import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord, CostBlock } from '../canon/types.js'
import { KyberBridge, type SessionPayload } from './bridge.js'

type Mismatch = {
  code: string
  message: string
  bases: string[]
  totalsByBasis: Record<string, number | null>
}

/** Read the pinned field off a served payload, failing loudly when it is absent. */
function readMismatch(payload: SessionPayload | null): Mismatch | null {
  expect(payload, 'session payload must be served').not.toBeNull()
  const field = (payload as { costBasisMismatch?: unknown }).costBasisMismatch
  // The field must be present — `undefined` means the server never computed
  // it, which is the defect these tests exist to catch. Distinguishing absent
  // from null here is the point of the comment in the file header.
  expect(field, 'payload must carry costBasisMismatch (null or an object)').not.toBeUndefined()
  return field as Mismatch | null
}

/**
 * `kimi` is deliberately neither a published-table nor a Copilot harness, so
 * `repriceTurns` leaves each record's block exactly as written. A test about
 * which bases a session ended up with must not have the pricing tables rewrite
 * its fixtures underneath it.
 *
 * It is a fixture-only choice, not a realistic one: the real case this stands in
 * for is a `claude-code` session with one turn the harness reported a figure for
 * and one turn a published table priced. Here neither fixture turn gets repriced,
 * so the harness/published split below is exactly the one asserted.
 */
const HARNESS = 'kimi'

function turn(spanId: string, cost: CostBlock): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-mismatch',
    parentSpanId: null,
    sessionId: 'sess-mismatch',
    source: 'synthetic',
    harness: HARNESS,
    name: `turn ${spanId}`,
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-09-04T12:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: { freshInput: 100, cacheRead: 0, cacheCreation: 0, output: 50, reportedInput: 100, reportedOutput: 50 },
    content: {},
    cost,
  }
}

describe('session payload: server-computed cost-basis mismatch', () => {
  let tempDir: string
  let store: CanonStore
  let bridge: KyberBridge

  beforeEach(() => {
    tempDir = mkdtempSync(join(tmpdir(), 'kyber-session-cost-mismatch-'))
    store = new CanonStore(join(tempDir, 'canon.db'))
    bridge = new KyberBridge({ canonPath: ':memory:', store })
  })

  afterEach(() => {
    bridge.close()
    store.close()
    rmSync(tempDir, { recursive: true, force: true })
  })

  describe('built from canonical records (the projection path)', () => {
    it('reports the mismatch when per-turn costs do not share a basis', async () => {
      // Two turns priced on different bases: the harness reported one figure and
      // a published table priced the other. They cannot be summed into one
      // session total, which is exactly what the browser used to work out.
      store.upsertMany([
        turn('turn-harness', { basis: 'harness', status: 'priced', value: 2.5, currency: 'USD' }),
        turn('turn-published', { basis: 'published', status: 'priced', value: 0.75, currency: 'USD' }),
      ])
      await buildSessions(store)

      const mismatch = readMismatch(bridge.getSessionPayload('sess-mismatch'))

      expect(mismatch).not.toBeNull()
      expect(mismatch!.code).toBe('COST_BASIS_MISMATCH')
      // Both bases are named, and named in sorted order so the field is stable
      // across runs rather than dependent on record arrival order.
      expect(mismatch!.bases).toEqual(['harness', 'published'])
      // The two totals a display layer must never blend: each basis' own sum.
      expect(mismatch!.totalsByBasis).toEqual({ harness: 2.5, published: 0.75 })
      expect(mismatch!.message).toContain('harness')
      expect(mismatch!.message).toContain('published')
      expect(mismatch!.message).toContain('refusing to blend')
    })

    it('reports no mismatch for a consistent session', async () => {
      store.upsertMany([
        turn('turn-a', { basis: 'published', status: 'priced', value: 1.5, currency: 'USD' }),
        turn('turn-b', { basis: 'published', status: 'priced', value: 1.5, currency: 'USD' }),
      ])
      await buildSessions(store)

      const payload = bridge.getSessionPayload('sess-mismatch')
      const mismatch = readMismatch(payload)

      // Explicit null, not absent: the server computed an answer and the answer
      // was "these agree".
      expect(mismatch).toBeNull()
      // The session total is present and equals the per-turn sum, so the null is
      // a real agreement rather than a session with nothing to compare.
      expect(payload).not.toBeNull()
      const summary = (payload as { summary?: { cost?: CostBlock } }).summary
      expect(summary?.cost?.value).toBe(3)
    })

    it('reports no mismatch when a session has no priced figure at all', async () => {
      // An unpriced session is not a basis mismatch: absent figures must not be
      // read as figures that disagree (R5.4 — a missing rate is not a $0.00).
      store.upsertMany([turn('turn-unpriced', { basis: 'unknown', status: 'no_rate' })])
      await buildSessions(store)

      expect(readMismatch(bridge.getSessionPayload('sess-mismatch'))).toBeNull()
    })
  })
})