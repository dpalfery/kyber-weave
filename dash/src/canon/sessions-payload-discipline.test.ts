// The projection's payload discipline: a projection must not hold a whole
// session's decompressed records at once.
//
// WHY this contract exists: the engine's `JobHost` tick runs a full
// canonical-store re-projection every 30 s, and that projection was CPU- and
// memory-bound: on the live corpus the engine sat at 109 % CPU and 2.5 GB
// RSS, with the used heap oscillating 100 MB -> 3,138.9 MB -> 100 MB every
// ~30 s. The holder was the JavaScript heap, not the store file (the store is
// not mapped into the engine), and the allocation sampler's hot frames were
// `buildSessions -> buildSessionRow -> recordsForSession -> toRecord ->
// decompressRaw`: the largest session held 10,933 `llm.invoke` records whose
// payloads inflated and parsed to the 1.5-2.5 GB the heap spiked to.
//
// The tree has already solved this shape twice, with the same pattern:
// `iterateSessions` became a generator so a caller can stream sessions
// instead of materializing them (a 4.4 GB peak), and `defineLazy` made a
// record's expensive field materialize on demand (a 3.5 GB peak). This
// contract pins the same discipline for a session's payloads.
//
// Four pins, none of them a duration or a heap threshold:
//   1. The projection's OUTPUT is unchanged: the derived session row for a
//      large session is byte-identical to the row the same records derive
//      with their whole payloads in place. This is the anti-regression pin;
//      it passes before and after the change.
//   2. The projection holds ONE record's payload at a time: the number of
//      simultaneously-live decompressed payloads stays bounded, measured by
//      instrumenting the decompression path.
//   3. The payload's bulk does not reach the derived tables: a corpus whose
//      payloads carry a large value derives exactly what the same corpus
//      derives with a short value in its place.
//   4. The timeline's evidence reading survives the reduction, whether the
//      evidence rides on a short attribute or on the payload itself.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { buildSessionRow } from './sessions.js'
import { projectCanonicalStore } from './projection.js'
import { CanonStore } from './store.js'
import { activeTokenizer, createCachedCounter, loadO200kCounter } from './tokens.js'
import type { CanonicalRecord, TokenUsage } from './types.js'

const LARGE_SESSION_KEY = 'large-session'
const SMALL_SESSION_KEY = 'small-session'
const EVIDENCE_SESSION_KEY = 'evidence-session'

/** The payload value the reduction is expected to drop: over the limit, and not an attribute. */
const BULK_ATTRIBUTE = 'gen_ai.input.messages'

/** A payload value no reader looks up, so its length is free to change. */
const BULK_TEXT = 'the quick brown fox jumps over the lazy dog. '

/**
 * The measured corpus's payload shape.
 *
 * The antigravity front end carries the model's bulk (the system
 * instructions, the conversation, the rules) under attributes no reader asks
 * for, and the evidence word "subagent" rides on the branch name — a short
 * attribute the reduction keeps.
 */
const MEASURED_PAYLOAD: Record<string, unknown> = {
  'gen_ai.operation.name': 'chat',
  'gen_ai.system': 'gemini',
  'gen_ai.provider.name': 'google',
  'gen_ai.request.model': 'Gemini 3.8 Flash (Medium)',
  'gen_ai.response.model': 'Gemini 3.8 Flash (Medium)',
  'gen_ai.agent.name': 'antigravity',
  'gen_ai.request.temperature': 0.2,
  'gen_ai.request.max_tokens': 8192,
  'vcs.repository.name': 'kyber-weave',
  'vcs.ref.head.name': 'feat/sessions-subagents-and-names',
  'gen_ai.tool.definitions': '[{"name":"define_subagent"}]',
  'gen_ai.usage.sys_tokens': 5800,
  'gen_ai.usage.msg_tokens': 10750,
}

function usage(): TokenUsage {
  return {
    freshInput: 120,
    cacheRead: 0,
    cacheCreation: 0,
    output: 30,
    reportedInput: 120,
    reportedOutput: 30,
  }
}

/**
 * One session's records: model calls whose payload carries a large value.
 *
 * The shape is the measured one — an attribute map with the harness's payload
 * under a key no reader looks up — so the corpus exercises the reduction
 * rather than a payload the projection reads anyway.
 */
function sessionRecords(
  key: string,
  count: number,
  bulkLength: number,
  options?: { measured?: boolean; evidenceInBulk?: boolean },
): CanonicalRecord[] {
  const bulk = BULK_TEXT.repeat(Math.ceil(bulkLength / BULK_TEXT.length)).slice(0, bulkLength)
  return Array.from({ length: count }, (_unused, index) => ({
    spanId: `${key}-span-${index}`,
    traceId: `${key}-trace`,
    parentSpanId: null,
    source: 'otlp:collector',
    harness: 'antigravity',
    sessionId: key,
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: new Date(Date.UTC(2026, 0, 1, 0, 0, index)).toISOString(),
    durationMs: 12,
    status: 'ok',
    tokens: usage(),
    content: {},
    cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
    raw: {
      ...(options?.measured === true ? MEASURED_PAYLOAD : {}),
      'gen_ai.request.model': 'claude-opus-4-5',
      'gen_ai.response.model': 'claude-opus-4-5',
      'gen_ai.agent.name': 'antigravity',
      [BULK_ATTRIBUTE]: options?.evidenceInBulk === true ? `${bulk} the title of the run` : bulk,
    },
  }))
}

const stores: CanonStore[] = []

function seedStore(records: readonly CanonicalRecord[]): CanonStore {
  const store = new CanonStore(':memory:')
  stores.push(store)
  store.upsertMany(records)
  return store
}

afterEach(() => {
  vi.restoreAllMocks()
  for (const store of stores.splice(0)) store.close()
})

/**
 * Count how many decompressed payloads are live at once.
 *
 * The count is taken at the decompression path itself: a record's payload is
 * a lazy property that caches what it computed, so a payload that is read
 * and never replaced stays alive for as long as the record does. The
 * instrumentation counts a payload as live from the moment it is read until
 * the moment the record's payload is replaced — which is exactly the window
 * the projection is supposed to keep short.
 */
type PayloadStats = { live: number; maxLive: number; materialized: number }

function instrumentPayloads(record: CanonicalRecord, stats: PayloadStats): CanonicalRecord {
  const descriptor = Object.getOwnPropertyDescriptor(record, 'raw')
  if (descriptor?.get === undefined) return record
  let materialized = false
  let replaced = false
  Object.defineProperty(record, 'raw', {
    enumerable: true,
    configurable: true,
    get(): unknown {
      if (!materialized) {
        materialized = true
        stats.materialized += 1
        stats.live += 1
        stats.maxLive = Math.max(stats.maxLive, stats.live)
      }
      return descriptor.get!.call(record)
    },
    set(next: unknown): void {
      if (materialized && !replaced) {
        replaced = true
        stats.live -= 1
      }
      descriptor.set!.call(record, next)
    },
  })
  return record
}

/**
 * Instrument every accessor the projection reads a session's records through.
 *
 * The projection reads a session's records through two accessors: the
 * streaming one that hands over the reduced attributes, and the plain one
 * the run and finding phases read. Both are instrumented, so the count covers
 * every phase of the projection.
 */
function watchPayloads(): PayloadStats {
  const stats: PayloadStats = { live: 0, maxLive: 0, materialized: 0 }

  const recordsForSession = CanonStore.prototype.recordsForSession
  vi.spyOn(CanonStore.prototype, 'recordsForSession').mockImplementation(function (
    this: CanonStore,
    key: string,
  ) {
    return recordsForSession.call(this, key).map((record) => instrumentPayloads(record, stats))
  })

  const projected = (CanonStore.prototype as unknown as {
    recordsForSessionProjected?: (
      key: string,
    ) => Generator<{ record: CanonicalRecord; attributes: Record<string, unknown> }>
  }).recordsForSessionProjected
  if (projected !== undefined) {
    vi.spyOn(
      CanonStore.prototype as unknown as {
        recordsForSessionProjected: (
          key: string,
        ) => Generator<{ record: CanonicalRecord; attributes: Record<string, unknown> }>
      },
      'recordsForSessionProjected',
    ).mockImplementation(function* (this: CanonStore, key: string) {
      for (const entry of projected.call(this, key)) {
        yield { ...entry, record: instrumentPayloads(entry.record, stats) }
      }
    })
  }

  return stats
}

/** The derived tables' whole content, as one comparable string. */
function derivedSnapshot(store: CanonStore): string {
  return JSON.stringify({
    sessions: store.builtSessionIds().map((sessionId) => ({
      sessionId,
      payload: store.getSessionPayload(sessionId),
    })),
    runs: store.listRuns(),
    executions: store.listExecutions(),
    rollups: store.listHarnessRollups(),
    findings: store.listFindings(),
  })
}

describe("the projection's payload discipline", () => {
  it('derives the same session row with a large payload as with the whole payload in place', async () => {
    // The anti-regression pin: the session row the projection derives must be
    // byte-identical to the row the same records derive with their payloads
    // untouched. The reference is built by hand over the store's records —
    // the path the projection took before the reduction — so a difference
    // here is a difference in what the dashboard shows.
    const count = 240
    const bulkLength = 96 * 1024
    const store = seedStore([
      ...sessionRecords(LARGE_SESSION_KEY, count, bulkLength),
      ...sessionRecords(SMALL_SESSION_KEY, 3, 128),
    ])

    await projectCanonicalStore(store)

    const counter = createCachedCounter(
      store.tokenCache(),
      await activeTokenizer(),
      await loadO200kCounter(),
    )
    const identities = store.sessionIdentities()
    const sessionId = identities.claim(LARGE_SESSION_KEY, 'antigravity')
    const expected = buildSessionRow(
      sessionId,
      store.recordsForSession(LARGE_SESSION_KEY),
      counter.count,
      store,
      { isSubagent: false, parentSession: null },
    )

    // The store splits the turns out of the payload (schema v22) and puts
    // them back on read, so the stored payload is compared by value, not by
    // key order: the split moves the `turns` key.
    expect(store.getSessionPayload(sessionId)).toEqual(expected.payload)
    expect(expected.payload).toBeDefined()
  })

  it('derives the same tables from a corpus whose payloads carry bulk', async () => {
    // The bulk must not reach the derived tables: the same corpus with a
    // short value in the payload's place derives exactly the same sessions,
    // runs, rollups and findings. This is the reduction's whole claim.
    const withBulk = seedStore([
      ...sessionRecords(LARGE_SESSION_KEY, 40, 64 * 1024),
      ...sessionRecords(SMALL_SESSION_KEY, 3, 128),
    ])
    const withoutBulk = seedStore([
      ...sessionRecords(LARGE_SESSION_KEY, 40, 128),
      ...sessionRecords(SMALL_SESSION_KEY, 3, 128),
    ])

    await projectCanonicalStore(withBulk)
    await projectCanonicalStore(withoutBulk)

    expect(derivedSnapshot(withBulk)).toBe(derivedSnapshot(withoutBulk))
  })

  it('holds one session\'s decompressed payloads at a time', async () => {
    // The structural pin: the number of simultaneously-live decompressed
    // payloads must stay bounded no matter how large the session is. Before
    // the reduction every record's payload was read and kept, so the count
    // grew with the session; the session here is 240 model calls whose
    // payloads inflate to 23 MB.
    const payloadBytes = 96 * 1024
    const store = seedStore(sessionRecords(LARGE_SESSION_KEY, 240, payloadBytes))
    const stats = watchPayloads()

    await projectCanonicalStore(store)

    // Every record's payload was read — the reduction reads it to reduce it —
    // and the projection never held more than a couple of them at once.
    expect(stats.materialized).toBeGreaterThan(0)
    expect(stats.maxLive).toBeLessThanOrEqual(2)
    // The heap bound, derived from the same count rather than from a
    // threshold on the process: the decompressed telemetry the projection
    // holds at once stays a couple of payloads' worth, where the session
    // carries 23 MB of it. Before the reduction the count grew with the
    // session and this figure was the whole corpus.
    expect(stats.maxLive * payloadBytes).toBeLessThan(2 * 1024 * 1024)
  })

  it('keeps the reading of a record whose evidence rides on a short attribute', async () => {
    // The measured corpus's shape: the model call's payload carries the
    // harness's bulk (the system instructions, the conversation, the rules)
    // under keys no reader asks for, and the evidence — the word "subagent" —
    // rides on a short attribute (the branch name). The reduction drops the
    // bulk and keeps the evidence, so the record's place in the timeline is
    // the same and the working set stays bounded.
    const store = seedStore(sessionRecords(LARGE_SESSION_KEY, 40, 32 * 1024, { measured: true }))
    const stats = watchPayloads()

    await projectCanonicalStore(store)

    const sessionId = store.sessionIdentities().claim(LARGE_SESSION_KEY, 'antigravity')
    const payload = store.getSessionPayload(sessionId) as { timeline?: unknown[] }
    expect(payload.timeline?.length).toBeGreaterThan(0)
    expect(JSON.stringify(payload)).toContain('"isSubagent":true')
    expect(stats.maxLive).toBeLessThanOrEqual(2)
  })

  it('keeps the whole payload of a record whose evidence rides on the payload itself', async () => {
    // The counterpart: when the only evidence a record carries rides on a
    // value the reduction would drop, the record keeps its whole payload
    // rather than change its place in the timeline. The projection's output
    // stays the same; that record's payload is simply not reduced.
    const store = seedStore(
      sessionRecords(EVIDENCE_SESSION_KEY, 1, 32 * 1024, { evidenceInBulk: true }),
    )

    await projectCanonicalStore(store)

    const sessionId = store.sessionIdentities().claim(EVIDENCE_SESSION_KEY, 'antigravity')
    const payload = store.getSessionPayload(sessionId)
    // The auxiliary reading survived: the record is still auxiliary activity,
    // exactly as it was with its whole payload in place.
    expect(JSON.stringify(payload)).toContain('"isAuxiliary":true')
  })
})
