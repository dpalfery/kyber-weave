// The projection's parts discipline: a projection must not hold a session's
// decompressed parts at once.
//
// WHY this contract exists: the engine's `JobHost` tick runs a full
// canonical-store re-projection every 30 s, and the used heap oscillated
// 100 MB -> 3,138.9 MB -> 100 MB every ~30 s. The holder was measured, not
// inferred: the JavaScript heap, not the store file (the store is not mapped
// into the engine; `PRAGMA mmap_size` is 0), and not a leak — no monotonic
// growth, a large TRANSIENT working set.
//
// The tree has already solved this shape three times, with the same pattern:
// `iterateSessions` became a generator so a caller can stream sessions instead
// of materializing them (a 4.4 GB peak), `defineLazy` made a record's
// expensive field materialize on demand (a 3.5 GB peak), and the payload
// reduction that landed last keeps only the attributes the derived views read
// and releases the payload (peak RSS 1,199 MB -> 540 MB on a 5,000-record /
// 468 MB corpus; live-set bound 241 -> <= 2).
//
// What was left is the `parts_json` column. A record's parts carry the turn's
// text: the context analysis buckets each measured turn's parts, the skills
// listing reads the system prompt, and the tool definitions are ranked by
// their text. `defineLazy` made the parts lazy, but it CACHES what it
// computed, so the first reader of a record's parts kept them alive for as
// long as the record — and the record lives as long as the session. On the
// live corpus that is ~10,933 x 64 KB, about 1.4 GB as UTF-16.
//
// Three pins, none of them a duration or a heap threshold:
//   1. The projection's OUTPUT is pinned: the derived tables, the context
//      analysis buckets, the tool definitions, the skills listing and the
//      published timeline are byte-identical to what the same corpus derived
//      before. The pin moved ONCE, on 2026-10-21, for the Antigravity
//      transcript reason copy: the two leaves `$.sessions.0.payload.skills.rows.0.invocations.reason`
//      and `$.sessions.0.payload.skills.rows.1.invocations.reason` now read
//      "Skill invocations are read from the conversation transcript, and none
//      was available for this session" instead of the earlier wording. A
//      structural diff of the derived output, main against the branch, shows no
//      other leaf differs; everything else is unchanged. This is the
//      anti-regression pin.
//   2. The projection holds ONE record's decompressed parts at a time: the
//      number of simultaneously-live decompressed parts sets stays bounded,
//      measured by instrumenting the parts materialization path.
//   3. The readers still see every value they need: the buckets, the tool
//      definitions and the skills listing are right for a corpus whose parts
//      carry long, unicode and empty text, and for a record with no parts.

import { createHash } from 'node:crypto'

import { afterEach, describe, expect, it, vi } from 'vitest'

import { projectCanonicalStore } from './projection.js'
import { CanonStore } from './store.js'
import type { CanonicalRecord, ContentPart, CostStatus, TokenUsage } from './types.js'

const SESSION_KEY = 'parts-session'
const HARNESS = 'antigravity'

/**
 * The cost status the corpus carries.
 *
 * A status the projection does not model, so the corpus does not depend on
 * the bundled pricing table: the digest below is a statement about the
 * projection's own derivation, not about what a model happens to cost
 * today.
 */
const CORPUS_COST_STATUS = 'reported' as unknown as CostStatus

/** A part's text long enough to be a working set rather than a label. */
const LONG_TEXT = 'the quick brown fox jumps over the lazy dog. '.repeat(512)

/** Unicode the tokenizer and the readers must both survive. */
const UNICODE_TEXT = 'ünïcödé — 日本語 — 🌍 — ✓'

/**
 * The tool catalogue one `tool_definitions` part carries: a JSON array, which
 * the schema ranking splits into one entry per tool.
 */
const TOOL_CATALOGUE = JSON.stringify([
  { name: 'read_file', description: 'Read a file from disk.' },
  { name: 'write_file', description: 'Write a file to disk.' },
])

/**
 * The skills listing the system prompt carries: the `<available_skills>`
 * block the listing reader parses for names and descriptions.
 */
const SKILLS_BLOCK = [
  '<available_skills>',
  '<skill><name>kyber-weave-docs</name><description>Write conformant documentation.</description></skill>',
  '<skill><name>conductor</name><description>Coordinate a delivery.</description></skill>',
  '</available_skills>',
].join('\n')

function usage(): TokenUsage {
  return {
    freshInput: 900,
    cacheRead: 0,
    cacheCreation: 0,
    output: 120,
    reportedInput: 900,
    reportedOutput: 120,
  }
}

/**
 * One turn's parts, in the shape a harness sends them: the system prompt, the
 * conversation history (with the instruction fences the composition strips),
 * the tool catalogue and a tool result.
 */
function turnParts(index: number): ContentPart[] {
  return [
    { part: 'system_prompt', text: `${SKILLS_BLOCK}\n${LONG_TEXT}`, order: 0 },
    {
      part: 'conversation_history',
      text: `turn ${index}: <system-reminder>keep going</system-reminder> and the user's request`,
      order: 1,
    },
    { part: 'tool_definitions', text: TOOL_CATALOGUE, server: 'mcp-files', order: 2 },
    { part: 'tool_result_content', text: `result ${index}: ${UNICODE_TEXT}`, order: 3 },
  ]
}

/**
 * The corpus: a session of model calls whose parts carry the text the readers
 * consume, plus a record with no parts at all and a turn whose parts are
 * empty.
 */
function corpus(count: number): CanonicalRecord[] {
  const records: CanonicalRecord[] = []
  for (let index = 0; index < count; index++) {
    records.push({
      spanId: `${SESSION_KEY}-turn-${index}`,
      traceId: `${SESSION_KEY}-trace`,
      parentSpanId: null,
      source: 'otlp:collector',
      harness: HARNESS,
      sessionId: SESSION_KEY,
      name: 'chat turn',
      op: 'llm.invoke',
      kind: 'internal',
      timestamp: new Date(Date.UTC(2026, 0, 1, 0, 0, index)).toISOString(),
      durationMs: 12,
      status: 'ok',
      tokens: usage(),
      content: {},
      cost: { basis: 'harness', status: CORPUS_COST_STATUS, value: 0.01, currency: 'USD' },
      parts: turnParts(index),
    })
  }
  // A record with no parts at all: the readers must not read its absence as
  // a failure to capture.
  records.push({
    spanId: `${SESSION_KEY}-tool-0`,
    traceId: `${SESSION_KEY}-trace`,
    parentSpanId: `${SESSION_KEY}-turn-0`,
    source: 'otlp:collector',
    harness: HARNESS,
    sessionId: SESSION_KEY,
    name: 'read_file',
    op: 'tool.invoke',
    kind: 'internal',
    timestamp: new Date(Date.UTC(2026, 0, 1, 0, 1, 0)).toISOString(),
    durationMs: 4,
    status: 'ok',
    tokens: usage(),
    content: { tool_result_content: 'ok' },
    cost: { basis: 'harness', status: CORPUS_COST_STATUS, value: 0, currency: 'USD' },
  })
  // A turn whose parts carry no text at all: an empty part is not structure.
  records.push({
    spanId: `${SESSION_KEY}-turn-empty`,
    traceId: `${SESSION_KEY}-trace`,
    parentSpanId: null,
    source: 'otlp:collector',
    harness: HARNESS,
    sessionId: SESSION_KEY,
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: new Date(Date.UTC(2026, 0, 1, 0, 2, 0)).toISOString(),
    durationMs: 12,
    status: 'ok',
    tokens: usage(),
    content: {},
    cost: { basis: 'harness', status: CORPUS_COST_STATUS, value: 0.01, currency: 'USD' },
    parts: [{ part: 'conversation_history', text: '', order: 0 }],
  })
  return records
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
 * Count how many decompressed parts sets are live at once.
 *
 * The count is taken at the parts accessor itself: the parts are a lazy
 * property that caches what it computed, so a parts set that is read and
 * never released stays alive for as long as its record. The instrumentation
 * counts a parts set as live from the moment it is read until the moment the
 * record's parts are replaced — which is exactly the window the projection is
 * supposed to keep short.
 */
type PartsStats = { live: number; maxLive: number; materialized: number }

function instrumentParts(record: CanonicalRecord, stats: PartsStats): CanonicalRecord {
  const descriptor = Object.getOwnPropertyDescriptor(record, 'parts')
  if (descriptor?.get === undefined) return record
  let materialized = false
  Object.defineProperty(record, 'parts', {
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
      if (materialized) {
        // The parts were released: the set is no longer live, and a later
        // read materializes a fresh one.
        materialized = false
        stats.live -= 1
      }
      descriptor.set!.call(record, next)
    },
  })
  return record
}

/**
 * Instrument every record the session projection reads.
 *
 * The projection reads a session's records through the streaming accessor
 * that hands over the reduced attributes, so that is the one instrumented:
 * the count covers the phase the contract is about — the session's own
 * lifetime. The run and finding phases load their own records and are
 * counted separately (see the phase's own working set).
 */
function watchParts(): PartsStats {
  const stats: PartsStats = { live: 0, maxLive: 0, materialized: 0 }

  const projected = CanonStore.prototype.recordsForSessionProjected
  vi.spyOn(CanonStore.prototype, 'recordsForSessionProjected').mockImplementation(function* (
    this: CanonStore,
    key: string,
  ) {
    for (const entry of projected.call(this, key)) {
      yield { ...entry, record: instrumentParts(entry.record, stats) }
    }
  })

  return stats
}

/** The derived tables' whole content, as one comparable string. */
function derivedSnapshot(store: CanonStore): string {
  return JSON.stringify({
    sessions: store
      .builtSessionIds()
      .sort()
      .map((sessionId) => ({ sessionId, payload: store.getSessionPayload(sessionId) })),
    runs: store.listRuns(),
    executions: store.listExecutions(),
    rollups: store.listHarnessRollups(),
    findings: store.listFindings(),
  })
}

/** The session's payload, as the dashboard reads it. */
function sessionPayload(store: CanonStore): Record<string, unknown> {
  const sessionId = store.builtSessionIds()[0]
  expect(sessionId).toBeDefined()
  return store.getSessionPayload(sessionId!) as Record<string, unknown>
}

describe("the projection's parts discipline", () => {
  it('derives the same tables, buckets, tool definitions, skills listing and timeline', async () => {
    // The anti-regression pin: the projection's whole output for a corpus
    // whose parts carry text is byte-identical to the output the same corpus
    // derived before, except for the two Antigravity skill-invocation reason
    // leaves noted in the file header (re-pinned 2026-10-21). The digest is
    // over the derived sessions, runs, executions, rollups and findings — the
    // tables the projection writes.
    const store = seedStore(corpus(6))

    await projectCanonicalStore(store)

    const digest = createHash('sha256').update(derivedSnapshot(store)).digest('hex')
    // The digest of the derived tables for this corpus as of 2026-10-21. It
    // differs from the earlier pin only in `$.sessions.0.payload.skills.rows.{0,1}.invocations.reason`,
    // the Antigravity transcript reason copy in dash/src/canon/skills.ts
    // `extractInvokedSkills`; every other leaf matches the earlier output.
    expect(digest).toBe(
      '2bcd880430f11af2dd23abfbb9fe7c28c1765509edcaccbb0129d3ad4742bc22',
    )
  })

  it('holds one record\'s decompressed parts at a time', async () => {
    // The structural pin: the number of simultaneously-live decompressed
    // parts sets must stay bounded no matter how large the session is. Before
    // the change every record's parts were read and kept, so the count grew
    // with the session.
    const store = seedStore(corpus(40))
    const stats = watchParts()

    await projectCanonicalStore(store)

    // Every record's parts were read — the readers consume the text — and the
    // projection never held more than a couple of them at once.
    expect(stats.materialized).toBeGreaterThan(0)
    expect(stats.maxLive).toBeLessThanOrEqual(2)
    // The heap bound, derived from the same count rather than from a
    // threshold on the process: the decompressed telemetry the projection
    // holds at once stays a couple of parts sets' worth, where the session
    // carries tens of megabytes of it.
    expect(stats.maxLive * LONG_TEXT.length).toBeLessThan(2 * LONG_TEXT.length)
  })

  it('reads every value the readers need from long, unicode and empty parts', async () => {
    // The readers' own pin: the buckets, the tool definitions and the skills
    // listing are right for a corpus whose parts carry long text, unicode and
    // an empty part, and for a record with no parts at all.
    const store = seedStore(corpus(3))

    await projectCanonicalStore(store)

    const payload = sessionPayload(store)
    const context = payload.context as {
      first: { buckets: Record<string, number>; reported_input: number }
      last: { buckets: Record<string, number>; reported_input: number }
    }
    const tools = payload.tools as Array<{ name?: string; server?: string; schema_tokens: number }>
    const skills = payload.skills as { measurable: boolean; rows: Array<{ name: string; listed: boolean }> }

    // The buckets: the system prompt, the history, the tool definitions and
    // the tool result are all accounted for, and the instruction fence the
    // history carried was stripped into the instruction bucket.
    expect(Object.keys(context.first.buckets).sort()).toEqual([
      'conversation_history',
      'instruction_context',
      'system_prompt',
      'tool_definitions',
      'tool_result_content',
    ])
    expect(context.first.buckets.system_prompt).toBeGreaterThan(0)
    expect(context.first.buckets.conversation_history).toBeGreaterThan(0)
    expect(context.first.buckets.tool_definitions).toBeGreaterThan(0)
    expect(context.first.buckets.tool_result_content).toBeGreaterThan(0)
    expect(context.first.buckets.instruction_context).toBeGreaterThan(0)
    expect(context.first.reported_input).toBe(900)

    // The tool definitions: the JSON array is split into one entry per tool,
    // and the ground-truth server the part carried is kept.
    const named = tools.filter((tool) => tool.name !== undefined)
    expect(named.map((tool) => tool.name).sort()).toEqual(['read_file', 'write_file'])
    for (const tool of named) {
      expect(tool.server).toBe('mcp-files')
      expect(tool.schema_tokens).toBeGreaterThan(0)
    }

    // The skills listing: the `<available_skills>` block the system prompt
    // carried is read, with both names.
    expect(skills.measurable).toBe(true)
    expect(skills.rows.map((row) => row.name).sort()).toEqual([
      'conductor',
      'kyber-weave-docs',
    ])
    for (const row of skills.rows) expect(row.listed).toBe(true)
  })
})
