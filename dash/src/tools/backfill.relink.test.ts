// T2 of docs/archive/plans/2026-10-19-claude-desktop-request-body-correlation.md (D5).
//
// Claude Code Desktop api_request_body logs that the live matcher quarantined
// before their model-call span arrived are relinked once, at OTLP service
// start. These tests pin the observable outcome of that pass: parts land on
// the matched call, the quarantined row leaves quarantine, the log is marked
// enriched, and the rows the pass cannot place stay exactly where they were.

import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { OtlpLog } from '../canon/log-ingest.js'
import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord, ContentPart } from '../canon/types.js'
import { decodeOtlpJson, decodeOtlpLogJson } from '../otel/receiver.js'
import { relinkQuarantinedClaudeLogs } from './backfill.js'

/** The reason the live path writes when a log expires unmatched (log-ingest.ts). */
const QUARANTINE_REASON = 'identified log remained unmatched past pending expiry'
const CLAUDE_HARNESS = 'claude-code'
const CLAUDE_EVENT_NAME = 'claude_code.api_request_body'
/** The stored cursor (D5). Its presence proves the pass records where it stopped. */
const WATERMARK_KEY = 'claude_body_relink_watermark'
const DAY_MS = 24 * 60 * 60 * 1000
const RETENTION_MS = 14 * DAY_MS

/** The clock the pass is run against. Passed in as `now`, never read from the wall clock. */
const NOW = Date.parse('2026-10-19T13:00:00.000Z')
/** Base instant for the synthetic calls; well inside the retention window. */
const T0 = Date.parse('2026-10-19T12:30:00.000Z')

const SYNTH_SESSION = 'session-relink-synthetic'
const OTHER_SESSION = 'session-relink-other'
const SYNTH_TRACE_A = '0ab1c2d3e4f506170ab1c2d3e4f50617'
const SYNTH_TRACE_B = '1ab1c2d3e4f506171ab1c2d3e4f50617'
const SYNTH_TRACE_C = '2ab1c2d3e4f506172ab1c2d3e4f50617'

/** Span ids in the T0 Desktop fixture (traces.otlp.json). */
const FIXTURE_MAIN_CALL_FIRST = '0000000000000002'
const FIXTURE_MAIN_CALL_SECOND = '0000000000000003'
const FIXTURE_MAIN_CALL_THIRD = '0000000000000004'
const FIXTURE_SUBAGENT_CALL_A = '0000000000000007'
const FIXTURE_SUBAGENT_CALL_B = '0000000000000008'
const FIXTURE_INTERACTION_TWO_CALL = '000000000000000c'

/** Indexes into the T0 Desktop log fixture (logs.otlp.json), in file order. */
const FIXTURE_LOG_MAIN_FIRST = 0
const FIXTURE_LOG_SUBAGENT_A = 2

const FIXTURE_DIR = new URL('../otel/fixtures/claude-desktop/', import.meta.url)

const iso = (ms: number): string => new Date(ms).toISOString()

function readFixture(name: string): string {
  return readFileSync(new URL(name, FIXTURE_DIR), 'utf8')
}

/**
 * The T0 Desktop logs. The decoder still reads only `session_id` (the receiver
 * key fix is T1's), so the session the receiver will write is taken here from
 * `session.id` to seed the rows the live path would have quarantined.
 */
function fixtureLogs(): OtlpLog[] {
  return decodeOtlpLogJson(readFixture('logs.otlp.json')).map((log) => {
    const sessionId = log.attributes['session.id']
    return { ...log, sessionId: typeof sessionId === 'string' ? sessionId : null }
  })
}

function fixtureLog(index: number): OtlpLog {
  const log = fixtureLogs()[index]
  if (log === undefined) throw new Error(`T0 fixture log ${index} is missing`)
  return log
}

/** The `llm_request` spans of the T0 Desktop trace, as the model-call records they become. */
function fixtureCalls(): CanonicalRecord[] {
  return decodeOtlpJson(readFixture('traces.otlp.json'))
    .filter((span) => span.name === 'claude_code.llm_request')
    .map((span) => {
      const sessionId = span.attributes['session.id']
      return callRecord(
        span.spanId,
        span.traceId,
        span.timestamp,
        span.durationMs,
        typeof sessionId === 'string' ? sessionId : null,
      )
    })
}

/** A Claude model call as the store holds it before any body is relinked. */
function callRecord(
  spanId: string,
  traceId: string,
  timestamp: string,
  durationMs: number,
  sessionId: string | null = SYNTH_SESSION,
): CanonicalRecord {
  return {
    spanId,
    traceId,
    parentSpanId: null,
    source: 'synthetic-test',
    harness: CLAUDE_HARNESS,
    sessionId,
    name: 'claude_code.llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp,
    durationMs,
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

/** An api_request_body log as the Desktop sender emits it: the event name is the body. */
function bodyLog(
  logId: string,
  traceId: string | null,
  spanId: string | null,
  atMs: number,
  requestJson: string,
  sessionId: string | null = SYNTH_SESSION,
): OtlpLog {
  return {
    logId,
    traceId,
    spanId,
    sessionId,
    timestamp: iso(atMs),
    body: CLAUDE_EVENT_NAME,
    attributes: { 'event.name': 'api_request_body', body: requestJson },
    resource: { 'service.name': 'claude-code-desktop' },
    scope: { name: 'com.anthropic.claude_code.events' },
  }
}

/** A minimal request JSON with a system prompt and one user message. Text is synthetic. */
function requestBody(system: string, extra: Record<string, unknown> = {}): string {
  return JSON.stringify({
    model: 'synthetic-model',
    system: [{ type: 'text', text: system }],
    messages: [{ role: 'user', content: 'Synthetic user message.' }],
    stream: true,
    ...extra,
  })
}

/** The live path's quarantine write for a Claude log that expired unmatched. */
function quarantine(store: CanonStore, log: OtlpLog): void {
  store.quarantineLog(log, QUARANTINE_REASON, CLAUDE_HARNESS)
}

function partsOf(store: CanonStore, spanId: string): readonly ContentPart[] {
  return store.get(spanId)?.parts ?? []
}

function bucketsOf(store: CanonStore, spanId: string): string[] {
  return partsOf(store, spanId).map((part) => part.part)
}

function systemPromptOf(store: CanonStore, spanId: string): string[] {
  return partsOf(store, spanId)
    .filter((part) => part.part === 'system_prompt')
    .map((part) => part.text)
}

/** The reasons a record declares for the metrics it cannot measure. */
function reasonsOf(record: CanonicalRecord | undefined): string[] {
  return Object.values(record?.measurability ?? {}).flatMap((availability) =>
    typeof availability === 'object' ? [availability.reason] : [],
  )
}

/** The log id the later-body claim names, read from the key D20 writes under `raw`. */
function claimedLogId(store: CanonStore, spanId: string): unknown {
  const raw = store.get(spanId)?.raw
  if (raw === null || typeof raw !== 'object') return undefined
  const claim = (raw as Record<string, unknown>)['kyber.claude_request_body']
  if (claim === null || typeof claim !== 'object') return undefined
  return (claim as Record<string, unknown>).logId
}

/** Everything a second pass must leave unchanged. */
function snapshot(store: CanonStore, spanIds: readonly string[], logIds: readonly string[]) {
  return {
    quarantined: store.quarantinedLogCount(),
    enriched: logIds.map((logId) => store.isLogEnriched(logId)),
    parts: spanIds.map((spanId) => partsOf(store, spanId)),
    measurability: spanIds.map((spanId) => store.get(spanId)?.measurability ?? null),
  }
}

describe('relinkQuarantinedClaudeLogs — one-time relink of quarantined api_request_body logs (D5)', () => {
  let store: CanonStore

  beforeEach(() => {
    store = new CanonStore(':memory:')
  })

  afterEach(() => {
    vi.restoreAllMocks()
    store.close()
  })

  it('relinks a Desktop main-thread body to the interaction-stamped call and clears its quarantined row', () => {
    store.upsertMany(fixtureCalls())
    const log = fixtureLog(FIXTURE_LOG_MAIN_FIRST)
    quarantine(store, log)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(store.getQuarantinedLog(log.logId)).toBeUndefined()
    expect(store.isLogEnriched(log.logId)).toBe(true)
    expect(systemPromptOf(store, FIXTURE_MAIN_CALL_FIRST).length).toBeGreaterThan(0)
    // Only the matched call changes: the later main-thread call never saw this body.
    expect(bucketsOf(store, FIXTURE_MAIN_CALL_SECOND)).toEqual([])
  })

  it('relinks a sub-agent body stamped with tool.execution to the call under that span, with its tools', () => {
    store.upsertMany(fixtureCalls())
    const log = fixtureLog(FIXTURE_LOG_SUBAGENT_A)
    quarantine(store, log)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(store.getQuarantinedLog(log.logId)).toBeUndefined()
    expect(bucketsOf(store, FIXTURE_SUBAGENT_CALL_A)).toEqual(
      expect.arrayContaining(['system_prompt', 'tool_definitions']),
    )
    // The sibling sub-agent call under the same tool.execution is a different call.
    expect(bucketsOf(store, FIXTURE_SUBAGENT_CALL_B)).toEqual([])
    expect(bucketsOf(store, FIXTURE_MAIN_CALL_SECOND)).toEqual([])
  })

  it('relinks every T0 Desktop body in one run and leaves no row quarantined', () => {
    store.upsertMany(fixtureCalls())
    const logs = fixtureLogs()
    for (const log of logs) quarantine(store, log)
    expect(store.quarantinedLogCount()).toBe(logs.length)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(store.quarantinedLogCount()).toBe(0)
    for (const log of logs) expect(store.isLogEnriched(log.logId)).toBe(true)
    // The context-free body (session only) lands on the nearest call in its session (D13).
    expect(systemPromptOf(store, FIXTURE_MAIN_CALL_THIRD).length).toBeGreaterThan(0)
    // The truncated body does not parse, so the call declares why, naming the sender limit.
    expect(reasonsOf(store.get(FIXTURE_INTERACTION_TWO_CALL))).toEqual(
      expect.arrayContaining([expect.stringContaining('CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH')]),
    )
  })

  it('leaves a body quarantined when two calls are equally near it', () => {
    store.upsertMany([
      callRecord('a0000000000000a1', SYNTH_TRACE_A, iso(T0), 1000),
      callRecord('a0000000000000a2', SYNTH_TRACE_A, iso(T0 + 200), 1000),
    ])
    const tied = bodyLog('log-relink-tie', SYNTH_TRACE_A, null, T0 + 100, requestBody('Synthetic tied body.'))
    quarantine(store, tied)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(store.getQuarantinedLog(tied.logId)).toBeDefined()
    expect(store.isLogEnriched(tied.logId)).toBe(false)
    expect(systemPromptOf(store, 'a0000000000000a1')).toEqual([])
    expect(systemPromptOf(store, 'a0000000000000a2')).toEqual([])
  })

  it('leaves a body quarantined when its nearest call had already ended before the body was logged', () => {
    store.upsert(callRecord('a0000000000000b1', SYNTH_TRACE_A, iso(T0), 100))
    const late = bodyLog('log-relink-after-end', SYNTH_TRACE_A, null, T0 + 500, requestBody('Synthetic late body.'))
    quarantine(store, late)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(store.getQuarantinedLog(late.logId)).toBeDefined()
    expect(store.isLogEnriched(late.logId)).toBe(false)
    expect(systemPromptOf(store, 'a0000000000000b1')).toEqual([])
  })

  it('leaves a body quarantined when no call in its trace starts within two seconds', () => {
    store.upsert(callRecord('a0000000000000c1', SYNTH_TRACE_A, iso(T0 + 5_000), 60_000))
    const distant = bodyLog('log-relink-distant', SYNTH_TRACE_A, null, T0, requestBody('Synthetic distant body.'))
    quarantine(store, distant)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(store.getQuarantinedLog(distant.logId)).toBeDefined()
    expect(systemPromptOf(store, 'a0000000000000c1')).toEqual([])
  })

  it('matches a context-free Claude body to the nearest call in its own session when it carries no trace', () => {
    store.upsertMany([
      callRecord('a0000000000000d1', SYNTH_TRACE_B, iso(T0), 1000, SYNTH_SESSION),
      callRecord('a0000000000000d2', SYNTH_TRACE_C, iso(T0 + 100), 1000, OTHER_SESSION),
    ])
    const contextFree = bodyLog('log-relink-context-free', null, null, T0 + 100, requestBody('Synthetic session body.'))
    quarantine(store, contextFree)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(store.getQuarantinedLog(contextFree.logId)).toBeUndefined()
    expect(store.isLogEnriched(contextFree.logId)).toBe(true)
    expect(systemPromptOf(store, 'a0000000000000d1')).toHaveLength(1)
    // A call at the same instant in another session is never a candidate.
    expect(systemPromptOf(store, 'a0000000000000d2')).toEqual([])
  })

  it('gives two bodies nearest to one call to the later body by log timestamp, and neither stays quarantined', () => {
    store.upsert(callRecord('a0000000000000e1', SYNTH_TRACE_A, iso(T0), 1000))
    const earlier = bodyLog('log-relink-earlier', SYNTH_TRACE_A, null, T0 + 100, requestBody('Synthetic earlier system prompt.'))
    const later = bodyLog('log-relink-later', SYNTH_TRACE_A, null, T0 + 400, requestBody('Synthetic later system prompt.'))
    // The later body is quarantined first, so a pass that applies rows in storage
    // order would let the earlier body overwrite it. The log timestamp decides.
    quarantine(store, later)
    quarantine(store, earlier)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(systemPromptOf(store, 'a0000000000000e1')).toEqual(['Synthetic later system prompt.'])
    expect(claimedLogId(store, 'a0000000000000e1')).toBe(later.logId)
    expect(store.getQuarantinedLog(earlier.logId)).toBeUndefined()
    expect(store.getQuarantinedLog(later.logId)).toBeUndefined()
    expect(store.isLogEnriched(earlier.logId)).toBe(true)
    expect(store.isLogEnriched(later.logId)).toBe(true)
  })

  it('skips a body whose call is older than the 14-day content-retention window, and relinks one inside it', () => {
    const staleAt = NOW - RETENTION_MS - DAY_MS
    const recentAt = NOW - RETENTION_MS + DAY_MS
    const stale = bodyLog('log-relink-stale', SYNTH_TRACE_A, null, staleAt + 100, requestBody('Synthetic stale body.'))
    const recent = bodyLog('log-relink-recent', SYNTH_TRACE_B, null, recentAt + 100, requestBody('Synthetic recent body.'))
    store.upsertMany([
      callRecord('a0000000000000f1', SYNTH_TRACE_A, iso(staleAt), 1000),
      callRecord('a0000000000000f2', SYNTH_TRACE_B, iso(recentAt), 1000),
    ])
    quarantine(store, stale)
    quarantine(store, recent)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    // The recent body proves the pass ran; the stale one stays quarantined as raw evidence.
    expect(store.getQuarantinedLog(recent.logId)).toBeUndefined()
    expect(systemPromptOf(store, 'a0000000000000f2')).toHaveLength(1)
    expect(store.getQuarantinedLog(stale.logId)).toBeDefined()
    expect(store.isLogEnriched(stale.logId)).toBe(false)
    expect(systemPromptOf(store, 'a0000000000000f1')).toEqual([])
  })

  it('is idempotent: a second run over the same store changes nothing', () => {
    store.upsertMany(fixtureCalls())
    const matched = [fixtureLog(FIXTURE_LOG_MAIN_FIRST), fixtureLog(FIXTURE_LOG_SUBAGENT_A)]
    const unmatched = bodyLog('log-relink-unmatched', SYNTH_TRACE_C, null, T0, requestBody('Synthetic unmatched body.'))
    for (const log of [...matched, unmatched]) quarantine(store, log)
    const spanIds = [FIXTURE_MAIN_CALL_FIRST, FIXTURE_SUBAGENT_CALL_A, FIXTURE_MAIN_CALL_SECOND]
    const logIds = [...matched, unmatched].map((log) => log.logId)

    relinkQuarantinedClaudeLogs(store, { now: NOW })
    const afterFirst = snapshot(store, spanIds, logIds)
    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(snapshot(store, spanIds, logIds)).toEqual(afterFirst)
    expect(store.getQuarantinedLog(unmatched.logId)).toBeDefined()
  })

  it('resumes from its stored watermark after a restart, so an unmatched row it already passed is not re-read', () => {
    const dir = mkdtempSync(join(tmpdir(), 'kw-relink-watermark-'))
    const path = join(dir, 'canon.db')
    const unmatched = bodyLog('log-relink-passed', SYNTH_TRACE_C, null, T0, requestBody('Synthetic passed body.'))
    const matchedNow = bodyLog('log-relink-matched-first', SYNTH_TRACE_A, null, T0 + 100, requestBody('Synthetic first run body.'))
    try {
      const first = new CanonStore(path)
      // The unmatched row is stored first, so it sits below the cursor the run leaves behind.
      quarantine(first, unmatched)
      first.upsert(callRecord('a0000000000000a9', SYNTH_TRACE_A, iso(T0), 1000))
      quarantine(first, matchedNow)
      relinkQuarantinedClaudeLogs(first, { now: NOW })
      expect(first.getQuarantinedLog(unmatched.logId)).toBeDefined()
      first.close()

      const restarted = new CanonStore(path)
      try {
        expect(restarted.getMetadata(WATERMARK_KEY)).toBeDefined()
        // Each piece below arrives after the first run and sits above the cursor
        // only if the pass advanced it. The unmatched row is below the cursor,
        // so the call that would now match it must not be read on restart.
        restarted.upsert(callRecord('a0000000000000b9', SYNTH_TRACE_C, iso(T0), 1000))
        const later = bodyLog('log-relink-after-restart', SYNTH_TRACE_B, null, T0 + 100, requestBody('Synthetic later body.'))
        restarted.upsert(callRecord('a0000000000000c9', SYNTH_TRACE_B, iso(T0), 1000))
        quarantine(restarted, later)

        relinkQuarantinedClaudeLogs(restarted, { now: NOW })

        expect(restarted.isLogEnriched(later.logId)).toBe(true)
        expect(systemPromptOf(restarted, 'a0000000000000c9')).toHaveLength(1)
        expect(restarted.getQuarantinedLog(unmatched.logId)).toBeDefined()
        expect(restarted.isLogEnriched(unmatched.logId)).toBe(false)
        expect(systemPromptOf(restarted, 'a0000000000000b9')).toEqual([])
      } finally {
        restarted.close()
      }
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('skips a corrupt quarantined payload, relinks the rows around it and advances the watermark past it', () => {
    const warnings: unknown[][] = []
    const spy = vi.spyOn(console, 'warn').mockImplementation((...args: unknown[]) => {
      warnings.push(args)
    })
    try {
      store.upsert(callRecord('a0000000000000d1', SYNTH_TRACE_A, iso(T0), 1000))
      store.upsert(callRecord('a0000000000000d2', SYNTH_TRACE_B, iso(T0), 1000))
      const corruptFirst = bodyLog('log-relink-corrupt-first', SYNTH_TRACE_C, null, T0 + 50, requestBody('Synthetic corrupt one.'))
      const matchedA = bodyLog('log-relink-after-corrupt-a', SYNTH_TRACE_A, null, T0 + 100, requestBody('Synthetic body A.'))
      const matchedB = bodyLog('log-relink-after-corrupt-b', SYNTH_TRACE_B, null, T0 + 120, requestBody('Synthetic body B.'))
      const corruptLast = bodyLog('log-relink-corrupt-last', SYNTH_TRACE_C, null, T0 + 150, requestBody('Synthetic corrupt two.'))
      for (const log of [corruptFirst, matchedA, matchedB, corruptLast]) quarantine(store, log)
      const db = (store as unknown as { db: { prepare(sql: string): { run(...args: unknown[]): unknown; get(...args: unknown[]): unknown } } }).db
      for (const log of [corruptFirst, corruptLast]) {
        db.prepare('UPDATE quarantined_logs SET payload = ? WHERE log_id = ?').run('{not json', log.logId)
      }
      const lastRowid = (db.prepare('SELECT rowid AS r FROM quarantined_logs WHERE log_id = ?').get(corruptLast.logId) as { r: number }).r

      const report = relinkQuarantinedClaudeLogs(store, { now: NOW })

      expect(report.relinked).toBe(2)
      expect(store.isLogEnriched(matchedA.logId)).toBe(true)
      expect(store.isLogEnriched(matchedB.logId)).toBe(true)
      expect(systemPromptOf(store, 'a0000000000000d1')).toHaveLength(1)
      expect(systemPromptOf(store, 'a0000000000000d2')).toHaveLength(1)
      // The unreadable rows stay as evidence and the pass moves past them.
      expect(store.quarantinedLogCount()).toBe(2)
      expect(store.getMetadata(WATERMARK_KEY)).toBe(String(lastRowid))
      // One warning, naming the count and the log ids, never the payload.
      expect(warnings).toHaveLength(1)
      const text = warnings[0]?.map(String).join(' ') ?? ''
      expect(text).toContain('2')
      expect(text).toContain(corruptFirst.logId)
      expect(text).not.toContain('not json')
    } finally {
      spy.mockRestore()
    }
  })

  it('pages by cumulative payload bytes, returning at least one row per page', () => {
    for (let i = 0; i < 5; i += 1) {
      quarantine(store, bodyLog(`log-relink-bytes-${i}`, SYNTH_TRACE_C, null, T0 + i, requestBody(`Synthetic bytes ${i}.`)))
    }
    const unlimited = store.quarantinedLogsAfter(CLAUDE_HARNESS, 0, 200, Number.MAX_SAFE_INTEGER)
    expect(unlimited).toHaveLength(5)
    const capped = store.quarantinedLogsAfter(CLAUDE_HARNESS, 0, 200, 1)
    expect(capped).toHaveLength(1)
    expect(capped[0]?.rowid).toBe(unlimited[0]?.rowid)
    const sizeOfTwo = JSON.stringify(unlimited[0]?.log).length + JSON.stringify(unlimited[1]?.log).length
    expect(store.quarantinedLogsAfter(CLAUDE_HARNESS, 0, 200, sizeOfTwo + 1).length).toBeLessThan(5)
  })

  it('relinks everything across many byte-capped pages and a second run reads nothing', () => {
    const logs: OtlpLog[] = []
    for (let i = 0; i < 6; i += 1) {
      const trace = `${i}cb1c2d3e4f506170ab1c2d3e4f50617`
      store.upsert(callRecord(`a0000000000000e${i}`, trace, iso(T0), 1000))
      logs.push(bodyLog(`log-relink-paged-${i}`, trace, null, T0 + 100 + i, requestBody(`Synthetic paged ${i}.`)))
    }
    const unmatched = bodyLog('log-relink-paged-unmatched', SYNTH_TRACE_C, null, T0 + 500, requestBody('Synthetic unmatched.'))
    for (const log of [...logs, unmatched]) quarantine(store, log)

    const first = relinkQuarantinedClaudeLogs(store, { now: NOW, pageByteBudget: 1 })

    expect(first).toEqual({ scanned: 7, relinked: 6 })
    for (const log of logs) expect(store.isLogEnriched(log.logId)).toBe(true)
    expect(store.getQuarantinedLog(unmatched.logId)).toBeDefined()
    const spanIds = logs.map((_, i) => `a0000000000000e${i}`)
    const before = snapshot(store, spanIds, logs.map((log) => log.logId))

    const second = relinkQuarantinedClaudeLogs(store, { now: NOW, pageByteBudget: 1 })

    expect(second).toEqual({ scanned: 0, relinked: 0 })
    expect(snapshot(store, spanIds, logs.map((log) => log.logId))).toEqual(before)
  })

  it('isolates a log whose enrichment throws: later rows still relink, the poison row stays quarantined, one warning names ids only', () => {
    const logs: OtlpLog[] = []
    for (let i = 0; i < 3; i += 1) {
      const trace = `${i}db1c2d3e4f506170ab1c2d3e4f50617`
      store.upsert(callRecord(`a0000000000000d${i}`, trace, iso(T0), 1000))
      logs.push(bodyLog(`log-relink-iso-${i}`, trace, null, T0 + 100 + i, requestBody(`Synthetic isolated ${i}.`)))
    }
    for (const log of logs) quarantine(store, log)
    const original = store.setContent.bind(store)
    vi.spyOn(store, 'setContent').mockImplementation((spanId, ...rest) => {
      if (spanId === 'a0000000000000d1') throw new Error('Synthetic poison payload text')
      return original(spanId, ...rest)
    })
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)

    const report = relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(report.relinked).toBe(2)
    expect(store.getQuarantinedLog('log-relink-iso-1')).toBeDefined()
    expect(store.isLogEnriched('log-relink-iso-1')).toBe(false)
    expect(store.getQuarantinedLog('log-relink-iso-0')).toBeUndefined()
    expect(store.getQuarantinedLog('log-relink-iso-2')).toBeUndefined()
    const failureWarnings = warn.mock.calls.map((args) => args.map(String).join(' ')).filter((line) => /failed/i.test(line))
    expect(failureWarnings).toHaveLength(1)
    expect(failureWarnings[0]).toContain('1')
    expect(failureWarnings[0]).toContain('log-relink-iso-1')
    expect(failureWarnings[0]).not.toContain('poison payload')
  })

  it('persists the watermark after every page, not only at the end', () => {
    for (let i = 0; i < 3; i += 1) {
      quarantine(store, bodyLog(`log-relink-wm-${i}`, SYNTH_TRACE_C, null, T0 + i, requestBody(`Synthetic wm ${i}.`)))
    }
    const writes: string[] = []
    const original = store.setMetadata.bind(store)
    vi.spyOn(store, 'setMetadata').mockImplementation((key, value) => {
      if (key === WATERMARK_KEY) writes.push(value)
      return original(key, value)
    })

    relinkQuarantinedClaudeLogs(store, { now: NOW, pageByteBudget: 1 })

    // Three one-row pages, each persisted; the highest surviving rowid never goes backwards.
    expect(writes.length).toBeGreaterThanOrEqual(3)
    expect(writes.map(Number)).toEqual([...writes.map(Number)].sort((a, b) => a - b))
    expect(store.getMetadata(WATERMARK_KEY)).toBe(writes[writes.length - 1])
  })

  it('treats a record with an unparseable timestamp as expired: the 14-day cutoff does not fail open', () => {
    store.upsert(callRecord('a0000000000000h1', SYNTH_TRACE_A, 'not-a-timestamp', 1000))
    const log = bodyLog('log-relink-bad-ts', SYNTH_TRACE_A, 'a0000000000000h1', T0 + 100, requestBody('Synthetic bad ts body.'))
    quarantine(store, log)

    const report = relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(report.relinked).toBe(0)
    expect(store.getQuarantinedLog(log.logId)).toBeDefined()
    expect(store.isLogEnriched(log.logId)).toBe(false)
    expect(systemPromptOf(store, 'a0000000000000h1')).toEqual([])
  })

  it('leaves quarantined rows that are not Claude request bodies untouched', () => {
    const copilotCall: CanonicalRecord = {
      ...callRecord('a0000000000000g1', SYNTH_TRACE_A, iso(T0), 1000, null),
      harness: 'copilot',
      name: 'copilot.chat',
    }
    store.upsert(copilotCall)
    const otherLog: OtlpLog = {
      logId: 'log-relink-non-claude',
      traceId: SYNTH_TRACE_A,
      spanId: null,
      sessionId: null,
      timestamp: iso(T0 + 100),
      body: 'copilot.chat.turn',
      attributes: { system: 'Synthetic copilot system text.' },
      resource: { 'service.name': 'copilot' },
      scope: { name: 'copilot.telemetry' },
    }
    store.quarantineLog(otherLog, QUARANTINE_REASON)

    relinkQuarantinedClaudeLogs(store, { now: NOW })

    expect(store.getQuarantinedLog(otherLog.logId)).toBeDefined()
    expect(store.isLogEnriched(otherLog.logId)).toBe(false)
    expect(store.get('a0000000000000g1')?.harness).toBe('copilot')
    expect(bucketsOf(store, 'a0000000000000g1')).toEqual([])
  })
})
