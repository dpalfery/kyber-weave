// The shared canonical-projection coordinator contract (plan: "Canonical
// projection correction", T20 → T21). Static dot-folder refresh and live OTLP
// are separate ingress adapters over one canonical store; these tests pin the
// seam that keeps both on one projection: `projectCanonicalStore` is the only
// full projection (over `buildSessions()`), and `CanonicalProjectionScheduler`
// is the serialized, coalescing runner the live collector uses so accepted
// work becomes derived sessions without a static refresh — never two
// projections at once, never a trailing storm, never a canonical record lost
// to a projection failure, and never a store closed while a projection is
// still draining.
//
// Pinned scheduler semantics (T21 implements, these tests enforce):
//   * `request()` marks work dirty and runs it immediately — no timer to
//     advance, no debounce to expire.
//   * One pass runs at a time. Dirtiness arriving mid-pass is coalesced into
//     exactly one trailing pass, however many requests arrived.
//   * A failed pass settles its requests, reports through `onError`, and
//     leaves the work dirty: the committed record is untouched and the NEXT
//     request retries — there is no automatic retry loop.
//   * `drain()`/`close()` resolve only once the scheduler is quiescent — no
//     pass in flight and no trailing pass still owed.

import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import type { DatabaseSync } from 'node:sqlite'

import { ingestBatch } from './ingest.js'
import { CanonicalProjectionScheduler, projectCanonicalStore } from './projection.js'
import type { BuildSessionsReport } from './sessions.js'
import { CanonStore, SCHEMA_VERSION } from './store.js'
import type { OtlpSpan } from '../otel/receiver.js'
import { loadPricing, setModelAliases, setPriceOverrides } from '../pricing/models.js'
import { priceCopilotTurn } from './copilot-rates.js'
import { pricePublishedTurn } from './published-pricing.js'
import type { CanonicalRecord, CostBlock, TokenUsage } from './types.js'

const TRACE_ID = '0af7651916cd43dd8448eb211c80319c'
const SPAN_ID = 'b7ad6b7169203331'

/**
 * A span the Claude Code adapter's attribute fingerprint claims (R6.2): bare
 * token counters plus a vendor attribute. `ingestBatch` accepts it, so a
 * store seeded with it holds a committed canonical record that only a
 * projection can turn into a derived session. Claude Code exports no session
 * attribute on this span, so the derived session is keyed by the trace id.
 */
function claimableSpan(): OtlpSpan {
  return {
    traceId: TRACE_ID,
    spanId: SPAN_ID,
    parentSpanId: null,
    name: 'llm_request',
    kind: 'client',
    startTimeUnixNano: '1756980000000000000',
    endTimeUnixNano: '1756980000100000000',
    timestamp: '2026-09-04T10:00:00.000Z',
    durationMs: 100,
    status: { code: 'ok' },
    resource: { 'service.name': 'projection-fixture' },
    scope: {},
    attributes: {
      'claude.deployment_mode': '1p',
      input_tokens: 10,
      output_tokens: 5,
    },
  }
}

/** A pass-shaped return value for projectors whose purpose is timing, not derivation. */
const NO_OP_REPORT: BuildSessionsReport = { built: 0, skipped: 0, pruned: 0, rollups: 0, findings: 0 }

type Deferred = { resolve: () => void; promise: Promise<void> }

function deferred(): Deferred {
  let resolve!: () => void
  const promise = new Promise<void>((settle) => {
    resolve = settle
  })
  return { resolve, promise }
}

/**
 * A projector the test gates by hand: each pass records its start, then waits
 * for the test to open that pass's gate. That is what makes single-flight and
 * trailing-pass counts assertable without sleeps — an in-flight projection
 * stays in flight for exactly as long as the test decides.
 */
function gatedProjector(): {
  project: () => Promise<BuildSessionsReport>
  readonly passes: number
  started: (pass: number) => Promise<void>
  open: (pass: number) => void
} {
  const gates = new Map<number, Deferred>()
  const starts = new Map<number, Deferred>()
  let passes = 0
  const slot = (map: Map<number, Deferred>, pass: number): Deferred => {
    const existing = map.get(pass)
    if (existing !== undefined) return existing
    const created = deferred()
    map.set(pass, created)
    return created
  }
  return {
    async project(): Promise<BuildSessionsReport> {
      passes += 1
      slot(starts, passes).resolve()
      await slot(gates, passes).promise
      return { ...NO_OP_REPORT }
    },
    get passes() {
      return passes
    },
    started: (pass) => slot(starts, pass).promise,
    open: (pass) => slot(gates, pass).resolve(),
  }
}

describe('projectCanonicalStore', () => {
  it('derives the session cache from accepted records over the shared buildSessions pipeline', async () => {
    const store = new CanonStore(':memory:')
    try {
      expect(ingestBatch([claimableSpan()], store)).toMatchObject({
        accepted: 1,
        quarantined: 0,
        rejected: 0,
      })
      // Records only: nothing is derived until a projection runs.
      expect(store.getSessionPayload(TRACE_ID)).toBeUndefined()

      const report = await projectCanonicalStore(store)

      expect(report.built).toBe(1)
      expect(store.getSessionPayload(TRACE_ID)).toMatchObject({ harness: 'claude-code' })
    } finally {
      store.close()
    }
  })
})

describe('CanonicalProjectionScheduler', () => {
  it('projects accepted work immediately through the shared projection, with no timer to advance', async () => {
    const store = new CanonStore(':memory:')
    // No `project` override: the scheduler's default projector must BE the
    // shared entry point, or the derived session below cannot exist.
    const scheduler = new CanonicalProjectionScheduler({ store })
    try {
      ingestBatch([claimableSpan()], store)

      // No fake timers are running and none are advanced: this await settling
      // on its own is the proof the request ran immediately rather than on a
      // debounce or interval.
      await scheduler.request()

      expect(store.getSessionPayload(TRACE_ID)).toMatchObject({ harness: 'claude-code' })
    } finally {
      await scheduler.close()
      store.close()
    }
  })

  it('never overlaps projections and coalesces in-flight dirtiness into exactly one trailing pass', async () => {
    const store = new CanonStore(':memory:')
    const gated = gatedProjector()
    const scheduler = new CanonicalProjectionScheduler({ store, project: gated.project })
    try {
      const first = scheduler.request()
      await gated.started(1) // pass 1 is in flight and gated open
      const second = scheduler.request()
      const third = scheduler.request()

      // Single-flight: pass 1 is still in flight and two more requests have
      // arrived, yet no second projection has started.
      expect(gated.passes).toBe(1)

      gated.open(1)
      gated.open(2) // the one trailing pass may run straight through
      await Promise.all([first, second, third])

      // However many requests arrived mid-flight, they cost exactly one
      // trailing pass — not one per request, and not zero.
      expect(gated.passes).toBe(2)
    } finally {
      await scheduler.close()
      store.close()
    }
  })

  it('keeps the committed record and retries the retained dirty work after a projection failure', async () => {
    const store = new CanonStore(':memory:')
    const reported: unknown[] = []
    let failNextPass = true
    let passes = 0
    const scheduler = new CanonicalProjectionScheduler({
      store,
      async project(projectionStore) {
        passes += 1
        if (failNextPass) throw new Error('projection pass failed (fixture)')
        return await projectCanonicalStore(projectionStore)
      },
      onError: (error) => {
        reported.push(error)
      },
    })
    try {
      ingestBatch([claimableSpan()], store)

      // The failed pass settles its request and is reported rather than
      // thrown: the writer's ingest callback must not be crashed by a
      // derivation error.
      await scheduler.request()
      expect(passes).toBe(1)
      expect(reported).toEqual([expect.objectContaining({ message: 'projection pass failed (fixture)' })])

      // A projection failure rolls nothing back: the accepted canonical
      // record is still committed, and nothing was derived by the failed pass.
      expect(store.get(SPAN_ID)).toBeDefined()
      expect(store.getSessionPayload(TRACE_ID)).toBeUndefined()

      failNextPass = false
      // No new record arrived — the retried pass is the retained dirty work
      // itself, and it derives the session the failed pass could not.
      await scheduler.request()
      expect(passes).toBe(2)
      expect(store.getSessionPayload(TRACE_ID)).toMatchObject({ harness: 'claude-code' })
    } finally {
      await scheduler.close()
      store.close()
    }
  })

  it('drains an in-flight projection and its trailing pass before close resolves', async () => {
    const store = new CanonStore(':memory:')
    const gated = gatedProjector()
    const scheduler = new CanonicalProjectionScheduler({ store, project: gated.project })

    const first = scheduler.request()
    await gated.started(1) // pass 1 is in flight and gated open
    const later = scheduler.request() // dirtiness arriving while pass 1 is in flight

    let closed = false
    const closing = scheduler.close()
    void closing.then(() => {
      closed = true
    })

    gated.open(1) // pass 1 completes; the trailing pass must still run
    await gated.started(2)
    // The trailing pass is gated in flight, so close has no business having
    // resolved after only the first pass.
    expect(closed).toBe(false)

    gated.open(2)
    await closing

    expect(closed).toBe(true)
    expect(gated.passes).toBe(2)
    await first
    await later
    store.close()
  })
})

// Issue #186 Defect B, decision U9 (plan T11 RED -> T12 GREEN): projection-time repricing.
// `buildSessions` reprices every turn record that carries no harness-reported cost and writes the
// changed `cost_json` back to the record, so the list, the cost tile and the report agree. No schema
// bump. Assumed hook (pinned for T12): inside `buildSessions`, before sessions are summed, per turn
// record (op 'llm.invoke'), model read from `record.raw` via the existing MODEL_KEYS lookup.
describe('projectCanonicalStore: projection-time repricing (issue #186, U9)', () => {
  const STALE: CostBlock = { basis: 'unknown', status: 'no_rate' }
  const TOK: TokenUsage = {
    freshInput: 100_000,
    cacheRead: 200_000,
    cacheCreation: 10_000,
    output: 20_000,
    reportedInput: 310_000,
    reportedOutput: 20_000,
  }

  beforeAll(async () => {
    await loadPricing()
  })
  afterEach(() => {
    setPriceOverrides({})
    setModelAliases({})
  })

  function turn(id: string, session: string, harness: string, model: string, cost: CostBlock, minute = 0): CanonicalRecord {
    return {
      spanId: id,
      traceId: `trace-${session}`,
      parentSpanId: null,
      sessionId: session,
      source: 'synthetic',
      harness,
      name: `turn ${id}`,
      op: 'llm.invoke',
      kind: 'client',
      timestamp: `2026-09-04T10:${String(minute).padStart(2, '0')}:00.000Z`,
      durationMs: 100,
      status: 'ok',
      tokens: TOK,
      content: {},
      cost,
      raw: { model },
    }
  }

  async function projectOnce(records: CanonicalRecord[]): Promise<CanonStore> {
    const store = new CanonStore(':memory:')
    store.upsertMany(records)
    await projectCanonicalStore(store)
    return store
  }

  const sessionCost = (store: CanonStore, id: string) =>
    (store.getSessionPayload(id) as { summary: { cost: CostBlock } }).summary.cost

  // U9: the pricing change itself adds no migration. The merged tree is at 15
  // via the parallel ingest-coverage 14→15 step (history_weeks), not via pricing.
  it('keeps SCHEMA_VERSION at 15 (U9: no schema bump from pricing)', () => {
    expect(SCHEMA_VERSION).toBe(15)
  })

  it('reprices a stale {unknown,no_rate} claude-code turn, rewrites cost_json, and the session cost agrees', async () => {
    const store = await projectOnce([turn('cc-1', 'cc-s', 'claude-code', 'claude-opus-5', STALE)])
    try {
      const expected = pricePublishedTurn(TOK, 'claude-opus-5', 'claude-code')
      expect(expected.status).toBe('priced')
      expect(store.get('cc-1')?.cost).toMatchObject({ basis: 'published', status: 'priced' })
      expect(store.get('cc-1')?.cost.value).toBeCloseTo(expected.value!, 10)
      expect(sessionCost(store, 'cc-s')).toMatchObject({ basis: 'published', status: 'priced' })
      expect(sessionCost(store, 'cc-s').value).toBeCloseTo(expected.value!, 10)
      // The cost tile reads cost_json through costContributionsForSessions; it must agree.
      const [contribution] = store.costContributionsForSessions(['cc-s'])
      expect(contribution).toMatchObject({ basis: 'published', status: 'priced' })
      expect(contribution.value).toBeCloseTo(expected.value!, 10)
    } finally {
      store.close()
    }
  })

  it('reprices codex and copilot-family turns with their own pricers', async () => {
    const store = await projectOnce([
      turn('cx-1', 'cx-s', 'codex', 'gpt-5.6-luna', STALE),
      turn('cp-1', 'cp-s', 'copilot-cli', 'claude-sonnet-5-5', STALE),
    ])
    try {
      const codex = pricePublishedTurn(TOK, 'gpt-5.6-luna', 'codex')
      const copilot = priceCopilotTurn(TOK, 'claude-sonnet-5-5', 'copilot-cli')
      expect(codex.status).toBe('priced')
      expect(copilot.status).toBe('priced')
      expect(store.get('cx-1')?.cost.value).toBeCloseTo(codex.value!, 10)
      expect(store.get('cp-1')?.cost.value).toBeCloseTo(copilot.value!, 10)
      // The two tables differ: a copilot turn is never priced at API list rates (R5.3).
      expect(sessionCost(store, 'cp-s').value).toBeCloseTo(copilot.value!, 10)
    } finally {
      store.close()
    }
  })

  it('marks a session with priced and unpriced published turns as partial', async () => {
    const store = await projectOnce([
      turn('mx-1', 'mx-s', 'claude-code', 'claude-opus-5', STALE, 0),
      turn('mx-2', 'mx-s', 'claude-code', 'totally-fictional-model-x', STALE, 1),
    ])
    try {
      const priced = pricePublishedTurn(TOK, 'claude-opus-5', 'claude-code')
      expect(store.get('mx-2')?.cost).toMatchObject({ basis: 'published', status: 'no_rate' })
      expect(sessionCost(store, 'mx-s')).toMatchObject({ basis: 'published', status: 'partial' })
      expect(sessionCost(store, 'mx-s').value).toBeCloseTo(priced.value!, 10)
    } finally {
      store.close()
    }
  })

  it('never rewrites a harness-reported cost (R5.2) and leaves other harnesses on their current figures', async () => {
    const harnessCost: CostBlock = { basis: 'harness', status: 'priced', value: 1.25, currency: 'USD' }
    const store = await projectOnce([
      turn('hr-1', 'hr-s', 'antigravity-cli', 'claude-opus-5', harnessCost),
      turn('hr-2', 'hr-cc', 'claude-code', 'claude-opus-5', harnessCost),
      turn('zc-1', 'zc-s', 'zcode', 'claude-opus-5', STALE),
    ])
    try {
      expect(store.get('hr-1')?.cost).toEqual(harnessCost)
      expect(store.get('hr-2')?.cost).toEqual(harnessCost)
      expect(store.get('zc-1')?.cost).toEqual(STALE)
    } finally {
      store.close()
    }
  })

  it('keeps a fictional model at "no published rate" until a price override is set, then prices it on the next projection', async () => {
    const store = await projectOnce([turn('ov-1', 'ov-s', 'claude-code', 'fictional-unpublished-model', STALE)])
    try {
      expect(store.get('ov-1')?.cost).toMatchObject({ basis: 'published', status: 'no_rate' })
      setPriceOverrides({ 'fictional-unpublished-model': { input: 3, output: 15 } })
      await projectCanonicalStore(store)
      const after = store.get('ov-1')?.cost
      expect(after).toMatchObject({ basis: 'published', status: 'priced' })
      expect(after?.value).toBeGreaterThan(0)
      expect(sessionCost(store, 'ov-s')).toMatchObject({ status: 'priced' })
    } finally {
      store.close()
    }
  })

  it('reprojects an already-correct store idempotently with no spurious records writes', async () => {
    const store = await projectOnce([
      turn('id-1', 'id-s', 'claude-code', 'claude-opus-5', STALE),
      turn('id-2', 'id-s2', 'copilot', 'claude-sonnet-5-5', STALE),
    ])
    try {
      const db = (store as unknown as { db: DatabaseSync }).db
      const before = store.get('id-1')?.cost
      db.exec('CREATE TABLE write_log (span_id TEXT)')
      db.exec('CREATE TRIGGER log_records_update AFTER UPDATE ON records BEGIN INSERT INTO write_log VALUES (NEW.span_id); END')
      await projectCanonicalStore(store)
      const writes = db.prepare('SELECT COUNT(*) AS n FROM write_log').get() as { n: number }
      expect(writes.n).toBe(0)
      expect(store.get('id-1')?.cost).toEqual(before)
    } finally {
      store.close()
    }
  })

  it('writes a session\'s repriced blocks with one setCosts call and never calls setCost (PR #225 4149313577)', async () => {
    const setCosts = vi.spyOn(CanonStore.prototype, 'setCosts')
    // T2 may delete `setCost` outright; spy only while it still exists (removal also satisfies "never called").
    const legacy = CanonStore.prototype as unknown as { setCost?: () => void }
    const setCost = typeof legacy.setCost === 'function' ? vi.spyOn(legacy as { setCost: () => void }, 'setCost') : undefined
    try {
      const store = await projectOnce([
        turn('b-1', 'b-s', 'claude-code', 'claude-opus-5', STALE, 0),
        turn('b-2', 'b-s', 'claude-code', 'claude-opus-5', STALE, 1),
        turn('b-3', 'b-s', 'claude-code', 'claude-opus-5', STALE, 2),
      ])
      try {
        expect(setCosts).toHaveBeenCalledTimes(1)
        const changes = setCosts.mock.calls[0][0]
        expect(changes.map((c) => c.spanId).sort()).toEqual(['b-1', 'b-2', 'b-3'])
        expect(changes.every((c) => c.cost.status === 'priced')).toBe(true)
        if (setCost) expect(setCost).not.toHaveBeenCalled()
        // A second projection finds nothing to change and writes nothing.
        setCosts.mockClear()
        const db = (store as unknown as { db: DatabaseSync }).db
        db.exec('CREATE TABLE write_log2 (span_id TEXT)')
        db.exec('CREATE TRIGGER log_records_update2 AFTER UPDATE ON records BEGIN INSERT INTO write_log2 VALUES (NEW.span_id); END')
        await projectCanonicalStore(store)
        expect((db.prepare('SELECT COUNT(*) AS n FROM write_log2').get() as { n: number }).n).toBe(0)
        for (const call of setCosts.mock.calls) expect(call[0]).toHaveLength(0)
      } finally {
        store.close()
      }
    } finally {
      setCosts.mockRestore()
      setCost?.mockRestore()
    }
  })

  it('stores a model-less turn as {published, no_rate} and totals the session as partial, not COST_BASIS_MISMATCH (PR #225 4149313591)', async () => {
    const modelless: CanonicalRecord = { ...turn('nm-2', 'nm-s', 'claude-code', 'unused', STALE, 1), raw: {} }
    const store = await projectOnce([turn('nm-1', 'nm-s', 'claude-code', 'claude-opus-5', STALE, 0), modelless])
    try {
      const priced = pricePublishedTurn(TOK, 'claude-opus-5', 'claude-code')
      expect(store.get('nm-2')?.cost).toMatchObject({ basis: 'published', status: 'no_rate' })
      const cost = sessionCost(store, 'nm-s')
      expect(cost).toMatchObject({ basis: 'published', status: 'partial' })
      expect(JSON.stringify(cost)).not.toContain('COST_BASIS_MISMATCH')
      expect(cost.value).toBeCloseTo(priced.value!, 10)
    } finally {
      store.close()
    }
  })
})
