// F8 regression: pending-log reconciliation repeatedly loaded every payload
// with SELECT/all and parsed all bodies, once per span batch (ingest replay),
// once per log batch (dedup map), and every five seconds (expiry). Claude
// request bodies may wait fifteen minutes, so a busy sender can park megabytes
// behind those snapshots. The fix is metadata-only expiry/dedup SQL plus
// targeted or byte-bounded reads: only expired rows' payloads are ever parsed
// for expiry, only matching rows' for correlation, and replay walks
// byte-bounded pages in log_id order.

import { describe, expect, it, vi } from 'vitest'

import type { OtlpSpan } from '../otel/receiver.js'
import { ingestBatch } from './ingest.js'
import { PENDING_INTERACTION_PROMPT } from './log-ingest.js'
import {
  CLAUDE_REQUEST_BODY_PENDING_TTL_MS,
  DEFAULT_PENDING_TTL_MS,
  ingestLogBatch,
  reconcileExpiredPending,
  type OtlpLog,
} from './log-ingest.js'
import { CanonStore } from './store.js'
import type { CanonicalRecord } from './types.js'

function pendingLog(logId: string, overrides: Partial<OtlpLog & { pendingSince?: number }> = {}): OtlpLog & { pendingSince?: number } {
  return {
    logId,
    traceId: '0af7651916cd43dd8448eb211c80319c',
    spanId: 'b7ad6b7169203331',
    sessionId: null,
    timestamp: '2026-08-29T12:00:00.000Z',
    body: `body of ${logId}`,
    attributes: {},
    resource: {},
    scope: {},
    ...overrides,
  }
}

/** A large-payload pending log: the shape that made full snapshots heavy. */
function heavyLog(logId: string, bodyBytes: number, pendingSince?: number): OtlpLog & { pendingSince?: number } {
  return pendingLog(logId, {
    body: 'x'.repeat(bodyBytes),
    ...(pendingSince === undefined ? {} : { pendingSince }),
  })
}

function storedRecord(spanId: string, traceId: string): CanonicalRecord {
  return {
    spanId,
    traceId,
    parentSpanId: null,
    source: 'synthetic',
    harness: 'pi',
    name: 'synthetic call',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-08-29T12:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: {
      freshInput: 10,
      cacheRead: 0,
      cacheCreation: 0,
      output: 5,
      reportedInput: 10,
      reportedOutput: 5,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
  }
}

describe('F8 pending-log metadata reads', () => {
  it('lists metadata without payloads, including sizes and correlation keys', () => {
    const store = new CanonStore(':memory:')
    try {
      store.addPendingLog(pendingLog('log-a', { pendingSince: 1_000 }), 'claude-code')
      store.addPendingLog(pendingLog('log-b', { traceId: null, spanId: null }))

      const metadata = store.pendingLogMetadata()
      expect(metadata.map((row) => row.logId).sort()).toEqual(['log-a', 'log-b'])
      const rowA = metadata.find((row) => row.logId === 'log-a')!
      expect(rowA.harness).toBe('claude-code')
      expect(rowA.pendingSince).toBe(1_000)
      expect(rowA.traceId).toBe('0af7651916cd43dd8448eb211c80319c')
      expect(rowA.spanId).toBe('b7ad6b7169203331')
      expect(rowA.bytes).toBeGreaterThan(0)
      // Metadata carries no payload: the body text is never read for this.
      expect(Object.keys(rowA).sort()).toEqual(
        ['bytes', 'harness', 'logId', 'pendingSince', 'sessionId', 'spanId', 'traceId'],
      )
    } finally {
      store.close()
    }
  })

  it('expires only expired rows by TTL, honoring the longer Claude request-body wait', () => {
    const store = new CanonStore(':memory:')
    try {
      const now = 10_000_000
      const claudeBody = (logId: string, pendingSince: number): OtlpLog & { pendingSince?: number } =>
        pendingLog(logId, {
          pendingSince,
          body: 'claude_code.api_request_body',
          attributes: { body: JSON.stringify({ model: 'claude-opus-4-6' }) },
        })
      store.addPendingLog(pendingLog('ordinary-expired', { pendingSince: now - DEFAULT_PENDING_TTL_MS - 1 }))
      store.addPendingLog(pendingLog('ordinary-fresh', { pendingSince: now }))
      store.addPendingLog(claudeBody('claude-young', now - DEFAULT_PENDING_TTL_MS - 1))
      store.addPendingLog(
        claudeBody('claude-old', now - CLAUDE_REQUEST_BODY_PENDING_TTL_MS - 1),
      )

      const expired = store.takeExpiredPendingLogs(now, DEFAULT_PENDING_TTL_MS, CLAUDE_REQUEST_BODY_PENDING_TTL_MS)
      expect(expired.map((row) => row.logId).sort()).toEqual(['claude-old', 'ordinary-expired'])

      // The reconcile path quarantines exactly the expired rows and keeps the rest.
      const quarantined = reconcileExpiredPending(store, now, DEFAULT_PENDING_TTL_MS)
      expect(quarantined).toBe(2)
      expect(store.pendingLogMetadata().map((row) => row.logId).sort()).toEqual([
        'claude-young',
        'ordinary-fresh',
      ])
    } finally {
      store.close()
    }
  })

  it('leaves a large live backlog untouched while expiring only backdated rows', () => {
    const store = new CanonStore(':memory:')
    try {
      const now = 20_000_000
      for (let i = 0; i < 200; i++) {
        store.addPendingLog(heavyLog(`live-${i}`, 32 * 1024, now), i % 2 === 0 ? 'claude-code' : undefined)
      }
      store.addPendingLog(heavyLog('expired-1', 32 * 1024, now - DEFAULT_PENDING_TTL_MS - 1))
      store.addPendingLog(heavyLog('expired-2', 32 * 1024, now - DEFAULT_PENDING_TTL_MS - 1))
      expect(store.pendingLogMetadata()).toHaveLength(202)

      // Only the two backdated rows are expired — the 200 live bodies are
      // never parsed for this decision.
      const expired = store.takeExpiredPendingLogs(now, DEFAULT_PENDING_TTL_MS, CLAUDE_REQUEST_BODY_PENDING_TTL_MS)
      expect(expired.map((row) => row.logId).sort()).toEqual(['expired-1', 'expired-2'])

      const quarantined = reconcileExpiredPending(store, now, DEFAULT_PENDING_TTL_MS)
      expect(quarantined).toBe(2)
      expect(store.pendingLogMetadata()).toHaveLength(200)
    } finally {
      store.close()
    }
  })
})

describe('F8 byte-bounded pending-log pages', () => {
  it('walks the backlog in log_id order without ever holding more than one page of payloads', () => {
    const store = new CanonStore(':memory:')
    try {
      for (let i = 0; i < 10; i++) {
        store.addPendingLog(heavyLog(`page-${String(i).padStart(2, '0')}`, 10 * 1024))
      }

      const seen: string[] = []
      const pageBytes: number[] = []
      let after = ''
      for (;;) {
        const page = store.pendingLogsPage(after, 1_000, 25 * 1024)
        if (page.length === 0) break
        const payloadBytes = page.reduce((total, row) => total + JSON.stringify(row.log).length, 0)
        pageBytes.push(payloadBytes)
        // Every page but a single oversized row stays within the byte bound.
        expect(payloadBytes <= 25 * 1024 || page.length === 1).toBe(true)
        seen.push(...page.map((row) => row.logId))
        after = page[page.length - 1]!.logId
      }

      // The paged walk covers exactly the full snapshot, in the same order.
      expect(seen).toEqual(
        (store.getPendingLogs() as { logId: string }[]).map((log) => log.logId),
      )
      expect(seen).toHaveLength(10)
      expect(pageBytes.length).toBeGreaterThan(1)
    } finally {
      store.close()
    }
  })
})

describe('F8 targeted pending-log correlation', () => {
  it('consumes only rows matching by span or session, leaving the rest', () => {
    const store = new CanonStore(':memory:')
    try {
      // Same span, other trace: the span-id clause still matches.
      store.addPendingLog(pendingLog('match-span', { traceId: 'other-trace', spanId: 'span-s' }))
      store.addPendingLog(pendingLog('match-trace-span', { traceId: 'trace-t', spanId: 'span-s' }))
      store.addPendingLog(pendingLog('match-session', { traceId: null, spanId: null, sessionId: 'session-9' }))
      store.addPendingLog(pendingLog('no-match', { traceId: 'trace-x', spanId: 'span-x' }))
      store.addPendingLog(pendingLog('other-session', { traceId: null, spanId: null, sessionId: 'session-z' }))

      const matched = store.consumePendingLogs('trace-t', 'span-s', 'session-9')
      expect((matched as { logId: string }[]).map((log) => log.logId).sort()).toEqual([
        'match-session',
        'match-span',
        'match-trace-span',
      ])
      expect(store.pendingLogMetadata().map((row) => row.logId).sort()).toEqual([
        'no-match',
        'other-session',
      ])
    } finally {
      store.close()
    }
  })

  it('replays a large pending backlog through bounded pages when a span batch lands', () => {
    const store = new CanonStore(':memory:')
    try {
      store.upsert(storedRecord('reward-span', 'reward-trace'))
      // Three pending logs name the stored record; 100 heavy ones name
      // nothing. The replay must enrich exactly the three, whatever the
      // backlog around them weighs.
      for (const n of ['hit-1', 'hit-2', 'hit-3']) {
        store.addPendingLog(pendingLog(n, { traceId: 'reward-trace', spanId: 'reward-span' }))
      }
      for (let i = 0; i < 100; i++) {
        store.addPendingLog(heavyLog(`bulk-${String(i).padStart(3, '0')}`, 16 * 1024))
      }

      // A junk span batch: nothing to accept, but the replay still runs.
      const junk = {
        traceId: 'junk-trace',
        spanId: 'junk-span',
        parentSpanId: null,
        name: 'junk',
        kind: 'internal',
        startTimeUnixNano: '1756478400000000000',
        endTimeUnixNano: '1756478400100000000',
        timestamp: '2026-08-29T12:00:00.000Z',
        durationMs: 1,
        status: { code: 'unset' },
        attributes: {},
        resource: {},
        scope: {},
      } as unknown as OtlpSpan
      void ingestBatch([junk], store)

      expect(store.isLogEnriched('hit-1')).toBe(true)
      expect(store.isLogEnriched('hit-2')).toBe(true)
      expect(store.isLogEnriched('hit-3')).toBe(true)
      expect(store.pendingLogMetadata()).toHaveLength(100)
    } finally {
      store.close()
    }
  })

  it('finds a parked interaction prompt by its marker without scanning unrelated payloads', () => {
    const store = new CanonStore(':memory:')
    try {
      for (let i = 0; i < 50; i++) {
        store.addPendingLog(heavyLog(`bulk-${i}`, 16 * 1024))
      }
      store.addPendingLog(
        pendingLog('prompt-1', {
          traceId: 'prompt-trace',
          spanId: 'prompt-span',
          sessionId: null,
          timestamp: '2026-08-29T12:00:00.000Z',
          body: null,
          attributes: { [PENDING_INTERACTION_PROMPT]: 'user prompt text' },
        }),
        'claude-code',
      )

      const prompts = store.pendingInteractionPrompts()
      expect(prompts.map((row) => row.logId)).toEqual(['prompt-1'])
      expect(prompts[0]).toMatchObject({
        traceId: 'prompt-trace',
        spanId: 'prompt-span',
        prompt: 'user prompt text',
      })
    } finally {
      store.close()
    }
  })

  it('dedups a log batch against pending metadata without reading bodies', () => {
    const store = new CanonStore(':memory:')
    try {
      const since = 5_000_000
      store.addPendingLog(pendingLog('known-pending', { pendingSince: since }))
      // Re-pends the same log id: the original arrival time survives, read
      // from metadata rather than a full-body duplicate map.
      const outcome = ingestLogBatch([pendingLog('known-pending', { pendingSince: since + 999 })], store, {
        now: () => since + 1_000,
      })
      expect(outcome).toMatchObject({ enriched: 0, pending: 1, quarantined: 0 })
      expect(store.pendingLogMetadata().find((row) => row.logId === 'known-pending')?.pendingSince).toBe(since)
    } finally {
      store.close()
    }
  })
})

describe('F8 replay hoists batch-wide scans out of the page loop', () => {
  it('reconciles and snapshots dedup once per batch while paging a multi-page backlog', () => {
    const store = new CanonStore(':memory:')
    try {
      // Six 1 MiB bodies exceed the 4 MiB replay byte cap, so the replay
      // must walk at least two pages. None match a record, so every row is
      // re-pended — the path that reads the dedup snapshot. Fresh arrivals
      // so the pre-replay reconcile keeps (not quarantines) them for paging.
      const since = Date.now()
      for (let i = 0; i < 6; i++) {
        store.addPendingLog(heavyLog(`bulk-${String(i).padStart(2, '0')}`, 1024 * 1024, since))
      }
      const metadataSpy = vi.spyOn(store, 'pendingLogMetadata')
      const expiredSpy = vi.spyOn(store, 'takeExpiredPendingLogs')
      const pageSpy = vi.spyOn(store, 'pendingLogsPage')

      const junk = {
        traceId: 'junk-trace',
        spanId: 'junk-span',
        parentSpanId: null,
        name: 'junk',
        kind: 'internal',
        startTimeUnixNano: '1756478400000000000',
        endTimeUnixNano: '1756478400100000000',
        timestamp: '2026-08-29T12:00:00.000Z',
        durationMs: 1,
        status: { code: 'unset' },
        attributes: {},
        resource: {},
        scope: {},
      } as unknown as OtlpSpan
      void ingestBatch([junk], store)

      // Batch-wide work ran once: one expiry reconciliation (one expired
      // selection) and one dedup metadata snapshot — not once per page.
      // `pendingLogMetadata` may be read again by the assertions below;
      // only the replay's own scans are counted here, so the count is taken
      // before any follow-up reads.
      expect(expiredSpy).toHaveBeenCalledTimes(1)
      expect(metadataSpy).toHaveBeenCalledTimes(1)
      // But the backlog itself was paged: more than one page read proves the
      // backlog did not fit in a single page.
      expect(pageSpy.mock.calls.length).toBeGreaterThan(1)
      metadataSpy.mockRestore()
      expiredSpy.mockRestore()
      pageSpy.mockRestore()

      // `pendingSince` semantics survive the hoist: re-pends kept the same
      // `logId`, so the original arrival time — not the replay time — rides
      // every row.
      for (const row of store.pendingLogMetadata()) {
        expect(row.pendingSince).toBe(since)
      }
      expect(store.pendingLogMetadata()).toHaveLength(6)
    } finally {
      store.close()
    }
  })

  it('keeps bounded pages: every page holds at most one oversized row past the cap', () => {
    const store = new CanonStore(':memory:')
    try {
      store.upsert(storedRecord('reward-span', 'reward-trace'))
      // Two enrichable rows plus a heavy backlog around them: the replay
      // must enrich exactly the matches however the pages split.
      for (const n of ['hit-1', 'hit-2']) {
        store.addPendingLog(pendingLog(n, { traceId: 'reward-trace', spanId: 'reward-span' }))
      }
      for (let i = 0; i < 6; i++) {
        store.addPendingLog(heavyLog(`bulk-${String(i).padStart(2, '0')}`, 1024 * 1024))
      }

      const pageSizes: number[] = []
      const originalPage = store.pendingLogsPage.bind(store)
      vi.spyOn(store, 'pendingLogsPage').mockImplementation((after, limit, maxBytes) => {
        const page = originalPage(after, limit, maxBytes)
        if (page.length > 0) {
          pageSizes.push(page.reduce((total, row) => total + JSON.stringify(row.log).length, 0))
        }
        return page
      })

      const junk = {
        traceId: 'junk-trace',
        spanId: 'junk-span',
        parentSpanId: null,
        name: 'junk',
        kind: 'internal',
        startTimeUnixNano: '1756478400000000000',
        endTimeUnixNano: '1756478400100000000',
        timestamp: '2026-08-29T12:00:00.000Z',
        durationMs: 1,
        status: { code: 'unset' },
        attributes: {},
        resource: {},
        scope: {},
      } as unknown as OtlpSpan
      void ingestBatch([junk], store)

      // Multi-page replay with bounded pages: at least two pages, each
      // within the byte cap unless it holds a single oversized row.
      expect(pageSizes.length).toBeGreaterThan(1)
      for (const bytes of pageSizes) {
        expect(bytes <= 4 * 1024 * 1024).toBe(true)
      }
      // Partial-commit semantics: the two matches enriched (and left the
      // backlog) even though the replay spanned pages; the rest stayed pending.
      expect(store.isLogEnriched('hit-1')).toBe(true)
      expect(store.isLogEnriched('hit-2')).toBe(true)
      expect(store.pendingLogMetadata()).toHaveLength(6)
    } finally {
      store.close()
    }
  })
})
