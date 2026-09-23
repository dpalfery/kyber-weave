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

import { describe, expect, it } from 'vitest'

import { ingestBatch } from './ingest.js'
import { CanonicalProjectionScheduler, projectCanonicalStore } from './projection.js'
import type { BuildSessionsReport } from './sessions.js'
import { CanonStore } from './store.js'
import type { OtlpSpan } from '../otel/receiver.js'

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
