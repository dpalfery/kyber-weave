// Clean re-ingest tests (task 8.2, requirement 15.3). The fixture below is
// two existing span exports — OTLP JSON request bodies of the shape the old
// collectors wrote, decoded through the receiver exactly as re-ingest
// consumes them. The acceptance shape: the corpus a fresh `:memory:` store
// is rebuilt into from those exports reaches the same content-free parity
// digest as the original corpus (R15.1's digest over R15.3's
// reconstruction), and holds exactly the original corpus's records — the
// Python pipeline's derived store is not migrated, it is rebuilt.
//
// The fixture corpus is shaped to drive every reconstruction path once: a pi
// trace (cache-exclusive turn, a child tool span, a second turn), a
// Copilot cache-inclusive turn, a GenAI-only span alone in its trace
// (fingerprint below threshold, nothing to inherit from → quarantine, R6.1),
// and a Copilot turn whose inverted decomposition cannot hold (fresh input
// −400 → rejected with a persisted problem, R4.4).

import { describe, expect, it } from 'vitest'

import { SCHEMA_VERSION, CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import { TOKEN_NEGATIVE_FRESH } from '../canon/types.js'
import { decodeOtlpJson, type OtlpSpan } from '../otel/receiver.js'
import { compareDigests, computeDigest } from './parity.js'
import { reingestFromExports } from './reingest.js'

// ---------------------------------------------------------------------------
// Fixture exports — OTLP JSON bodies, decoded through the receiver
// ---------------------------------------------------------------------------

/** 2025-08-29T14:40:00.000Z, the receiver tests' epoch. */
const BASE_MS = 1_756_478_400_000

const TRACE_PI = '11111111111111111111111111111111'
const TRACE_COPILOT = '22222222222222222222222222222222'
const TRACE_ORPHAN = '33333333333333333333333333333333'
const TRACE_INVALID = '44444444444444444444444444444444'

const S_PI_TURN_1 = 'aaaa000000000001'
const S_PI_TOOL = 'aaaa000000000002'
const S_PI_TURN_2 = 'aaaa000000000003'
const S_COPILOT_TURN = 'bbbb000000000001'
const S_ORPHAN = 'cccc000000000001'
const S_INVALID = 'dddd000000000001'

type FixtureSpan = {
  source: string
  traceId: string
  spanId: string
  parentSpanId?: string
  name: string
  startMs: number
  durationMs: number
  attributes: Record<string, string | number>
}

/** Canonical proto3 JSON wire form: hex ids, enum names, int64 as strings. */
function fixtureSpan(span: FixtureSpan): Record<string, unknown> {
  return {
    traceId: span.traceId,
    spanId: span.spanId,
    ...(span.parentSpanId === undefined ? {} : { parentSpanId: span.parentSpanId }),
    name: span.name,
    kind: 'SPAN_KIND_INTERNAL',
    startTimeUnixNano: `${span.startMs}000000`,
    endTimeUnixNano: `${span.startMs + span.durationMs}000000`,
    attributes: Object.entries(span.attributes).map(([key, value]) => ({
      key,
      value: typeof value === 'number' ? { intValue: String(value) } : { stringValue: value },
    })),
    status: { code: 'STATUS_CODE_OK' },
  }
}

/** One export batch as the old collectors wrote them: spans grouped per source process. */
function exportRequest(spans: FixtureSpan[]): string {
  const bySource = new Map<string, FixtureSpan[]>()
  for (const span of spans) {
    const group = bySource.get(span.source)
    if (group === undefined) bySource.set(span.source, [span])
    else group.push(span)
  }
  return JSON.stringify({
    resourceSpans: [...bySource.entries()].map(([source, group]) => ({
      resource: {
        attributes: [{ key: 'service.name', value: { stringValue: source } }],
      },
      scopeSpans: [
        { scope: { name: 'kyberdash-reingest-fixture' }, spans: group.map(fixtureSpan) },
      ],
    })),
  })
}

/** A pi turn: the cache-EXCLUSIVE convention — the input counter is fresh input alone. */
function piTurn(spec: {
  spanId: string
  parentSpanId?: string
  name: string
  startMs: number
  durationMs: number
  freshInput: number
  cacheRead?: number
  cacheCreation?: number
  output?: number
  prompt?: string
}): FixtureSpan {
  return {
    source: 'pi-abc123',
    traceId: TRACE_PI,
    spanId: spec.spanId,
    ...(spec.parentSpanId === undefined ? {} : { parentSpanId: spec.parentSpanId }),
    name: spec.name,
    startMs: spec.startMs,
    durationMs: spec.durationMs,
    attributes: {
      'pi.session.id': 's-9f2',
      'gen_ai.usage.input_tokens': spec.freshInput,
      ...(spec.cacheRead === undefined
        ? {}
        : { 'gen_ai.usage.cache_read.input_tokens': spec.cacheRead }),
      ...(spec.cacheCreation === undefined
        ? {}
        : { 'gen_ai.usage.cache_creation.input_tokens': spec.cacheCreation }),
      ...(spec.output === undefined ? {} : { 'gen_ai.usage.output_tokens': spec.output }),
      ...(spec.prompt === undefined ? {} : { 'gen_ai.prompt': spec.prompt }),
    },
  }
}

// Batch 1 — one pi session trace (turn, child tool span, second turn) and
// one Copilot turn, as the two resource spans of one export request.
const BATCH_1 = exportRequest([
  piTurn({
    spanId: S_PI_TURN_1,
    name: 'pi.agent.chat',
    startMs: BASE_MS,
    durationMs: 1_200,
    freshInput: 1_000,
    cacheRead: 2_000,
    cacheCreation: 500,
    output: 800,
    prompt: 'H1',
  }),
  {
    source: 'pi-abc123',
    traceId: TRACE_PI,
    spanId: S_PI_TOOL,
    parentSpanId: S_PI_TURN_1,
    name: 'pi.tool.read_file',
    startMs: BASE_MS + 1_200,
    durationMs: 50,
    attributes: { 'pi.session.id': 's-9f2', 'gen_ai.tool.name': 'read_file' },
  },
  {
    source: 'copilot-ws-1',
    traceId: TRACE_COPILOT,
    spanId: S_COPILOT_TURN,
    name: 'copilot.chat.turn',
    startMs: BASE_MS + 50,
    durationMs: 800,
    attributes: {
      'codeburn.provider': 'github-copilot',
      'gen_ai.usage.input_tokens': 4_000,
      'gen_ai.usage.cache_read.input_tokens': 1_000,
      'gen_ai.usage.output_tokens': 300,
      'gen_ai.prompt': 'C1',
    },
  },
  piTurn({
    spanId: S_PI_TURN_2,
    parentSpanId: S_PI_TURN_1,
    name: 'pi.agent.chat',
    startMs: BASE_MS + 1_250,
    durationMs: 900,
    freshInput: 1_500,
    output: 600,
  }),
])

// Batch 2 — a later export: a GenAI-only span alone in its trace (partial
// fingerprint scores 0.4, below the 0.6 threshold, and this batch holds no
// same-source group to inherit from → quarantine), and a Copilot turn whose
// decomposition cannot hold (input 100 against cache read 500 → fresh input
// −400 → rejected with a persisted problem).
const BATCH_2 = exportRequest([
  {
    source: 'orphan-exporter',
    traceId: TRACE_ORPHAN,
    spanId: S_ORPHAN,
    name: 'tool.exec',
    startMs: BASE_MS + 2_000,
    durationMs: 10,
    attributes: { 'gen_ai.usage.input_tokens': 512 },
  },
  {
    source: 'copilot-ws-1',
    traceId: TRACE_INVALID,
    spanId: S_INVALID,
    name: 'copilot.chat.turn',
    startMs: BASE_MS + 2_100,
    durationMs: 100,
    attributes: {
      'codeburn.provider': 'github-copilot',
      'gen_ai.usage.input_tokens': 100,
      'gen_ai.usage.cache_read.input_tokens': 500,
    },
  },
])

/** The exports as re-ingest consumes them: receiver-decoded batches. */
const EXPORTS: OtlpSpan[][] = [decodeOtlpJson(BATCH_1), decodeOtlpJson(BATCH_2)]

/** Span ids of the corpus the fixture exports store, in corpus order. */
const STORED_SPAN_IDS = [S_PI_TURN_1, S_PI_TOOL, S_COPILOT_TURN, S_PI_TURN_2]

/** The corpus a store rebuilt from the fixture holds, read back in corpus order. */
function corpusOf(store: CanonStore): CanonicalRecord[] {
  const corpus: CanonicalRecord[] = []
  for (const spanId of STORED_SPAN_IDS) {
    const record = store.get(spanId)
    if (record !== undefined) corpus.push(record)
  }
  return corpus
}

// Hand-computed over the stored corpus: 1_000+1_500 fresh (pi turns) and
// 4_000−1_000 (Copilot's inclusive conversion), 2_000+1_000 cache read,
// 500 cache creation, 800+600+300 output, 0 for the tool span.
const EXPECTED_TOKEN_STATS = {
  totalTokens: 10_700,
  freshInput: 5_500,
  cacheRead: 3_000,
  cacheCreation: 500,
  output: 1_700,
}

// ---------------------------------------------------------------------------
// R15.3 — the corpus is reconstructible; the derived store is not carried
// ---------------------------------------------------------------------------

describe('reingestFromExports — rebuilding the corpus from existing exports (R15.3)', () => {
  it('a fresh store rebuilt from the exports reaches the same digest as the original corpus', async () => {
    // The original corpus: what the exports built in the first place.
    const original = new CanonStore(':memory:')
    await reingestFromExports(EXPORTS, original)
    const originalCorpus = corpusOf(original)
    expect(originalCorpus).toHaveLength(4)
    const originalDigest = computeDigest(originalCorpus)

    // The reconstruction: same exports, a fresh store, nothing carried
    // forward from the old derived store.
    const rebuilt = new CanonStore(':memory:')
    await reingestFromExports(EXPORTS, rebuilt)
    const rebuiltCorpus = corpusOf(rebuilt)
    const rebuiltDigest = computeDigest(rebuiltCorpus)

    // Same corpus, same digest — section for section, empty diff (R15.3).
    const comparison = compareDigests(originalDigest, rebuiltDigest)
    expect(comparison.equal, comparison.diff.join('\n')).toBe(true)
    expect(rebuiltDigest).toEqual(originalDigest)

    // The equality is not vacuous: the digest is the corpus the fixtures
    // encode, every token class where the conventions put it.
    expect(rebuiltDigest.recordCount).toBe(4)
    expect(rebuiltDigest.tokenStats).toEqual(EXPECTED_TOKEN_STATS)
    expect(rebuiltDigest.timeline.spans).toBe(4)

    // The fresh store holds exactly the original corpus — same records,
    // same count, none lost and none invented by the rebuild.
    expect(rebuilt.count()).toBe(originalCorpus.length)
    expect(rebuiltCorpus).toEqual(originalCorpus)

    // A rebuilt store is self-describing: the schema version it stamped at
    // construction is detectable (design.md, migration), so a store rebuilt
    // under this schema is never silently misread.
    expect(rebuilt.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))

    original.close()
    rebuilt.close()
  })

  it('rebuilds through the normalization layer — attribution, conventions, timing, quarantine, validation', async () => {
    const store = new CanonStore(':memory:')
    await reingestFromExports(EXPORTS, store)

    // pi's cache-exclusive convention: fresh input as claimed, the reported
    // total reassembled from the classes (R4.2)...
    const turn1 = store.get(S_PI_TURN_1)
    expect(turn1?.harness).toBe('pi')
    expect(turn1?.source).toBe('pi-abc123')
    expect(turn1?.op).toBe('llm.invoke')
    expect(turn1?.tokens).toEqual({
      freshInput: 1_000,
      cacheRead: 2_000,
      cacheCreation: 500,
      output: 800,
      reportedInput: 3_500,
      reportedOutput: 800,
    })

    // ...Copilot's cache-inclusive one the other way: fresh recovered by
    // subtraction, the counter's total kept as claimed...
    const copilotTurn = store.get(S_COPILOT_TURN)
    expect(copilotTurn?.harness).toBe('copilot')
    expect(copilotTurn?.tokens).toEqual({
      freshInput: 3_000,
      cacheRead: 1_000,
      cacheCreation: 0,
      output: 300,
      reportedInput: 4_000,
      reportedOutput: 300,
    })

    // ...the attribute-less tool span claimed by its group's vote and mapped
    // to the canonical tool op...
    const tool = store.get(S_PI_TOOL)
    expect(tool?.harness).toBe('pi')
    expect(tool?.op).toBe('tool.invoke')

    // ...and the timing the RawSpan seam cannot carry grown from the export.
    expect(turn1?.timestamp).toBe('2025-08-29T14:40:00.000Z')
    expect(turn1?.durationMs).toBe(1_200)
    expect(turn1?.status).toBe('ok')
    expect(copilotTurn?.timestamp).toBe('2025-08-29T14:40:00.050Z')

    // The GenAI-only span had no claimant in its batch: quarantined with the
    // namespace it actually carried, never guessed into a harness (R6.1).
    expect(store.listQuarantine()).toEqual([
      { spanId: S_ORPHAN, namespaces: ['gen_ai'], reason: 'unclaimed' },
    ])

    // The inverted Copilot decomposition was rejected, its problem persisted
    // for the problems view — not logged and lost, not stored (R4.4).
    expect(store.get(S_INVALID)).toBeUndefined()
    const problems = store.getProblems()
    expect(problems).toHaveLength(1)
    expect(problems[0].spanId).toBe(S_INVALID)
    expect(problems[0].severity).toBe('error')
    expect(problems[0].code).toBe(TOKEN_NEGATIVE_FRESH)

    // One audit entry for the run, with the records it stored.
    expect(store.getIngestLog()).toEqual([
      { source: 'span-exports', count: 4, timestamp: expect.any(String) },
    ])

    store.close()
  })

  it('re-running the reconstruction lands on the same rows (R2.5)', async () => {
    const store = new CanonStore(':memory:')
    await reingestFromExports(EXPORTS, store)
    const first = computeDigest(corpusOf(store))

    await reingestFromExports(EXPORTS, store)

    // span_id is the primary key: the corpus is unchanged, and so is its
    // digest. Quarantine replaces its entry; the append-only problem log
    // records the re-surfaced rejection again, as any re-ingest would.
    expect(store.count()).toBe(4)
    expect(computeDigest(corpusOf(store))).toEqual(first)
    expect(store.listQuarantine()).toHaveLength(1)
    expect(store.getProblems()).toHaveLength(2)

    store.close()
  })

  it('an empty export set stores nothing and still logs its run', async () => {
    const store = new CanonStore(':memory:')
    await reingestFromExports([], store)

    expect(store.count()).toBe(0)
    expect(store.getIngestLog()).toEqual([
      { source: 'span-exports', count: 0, timestamp: expect.any(String) },
    ])

    store.close()
  })

  it('an export batch is the unit of attribution, as a live OTLP request is', async () => {
    // Within one export, a group's vote covers its weak-fingerprint member:
    // 1.0 and 0.4 average to 0.7, over the threshold, and both land as pi.
    const together = exportRequest([
      piTurn({
        spanId: S_PI_TURN_1,
        name: 'pi.agent.chat',
        startMs: BASE_MS,
        durationMs: 100,
        freshInput: 10,
        output: 5,
      }),
      {
        source: 'pi-abc123',
        traceId: TRACE_PI,
        spanId: S_PI_TOOL,
        parentSpanId: S_PI_TURN_1,
        name: 'pi.tool.read_file',
        startMs: BASE_MS + 100,
        durationMs: 10,
        attributes: { 'gen_ai.usage.input_tokens': 0 },
      },
    ])

    // The same weak-fingerprint span in a later export, alone in its trace,
    // has no same-source group in its batch to inherit from — the batch
    // boundary is where the vote ends, exactly as a request boundary is for
    // live ingest — so it is quarantined (R6.1), not attributed.
    const later = exportRequest([
      {
        source: 'pi-abc123',
        traceId: TRACE_COPILOT,
        spanId: S_COPILOT_TURN,
        name: 'tool.exec',
        startMs: BASE_MS + 200,
        durationMs: 10,
        attributes: { 'gen_ai.usage.input_tokens': 20 },
      },
    ])

    const store = new CanonStore(':memory:')
    await reingestFromExports([decodeOtlpJson(together), decodeOtlpJson(later)], store)

    expect(store.get(S_PI_TOOL)?.harness).toBe('pi')
    expect(store.listQuarantine().map((entry) => entry.spanId)).toEqual([S_COPILOT_TURN])

    store.close()
  })
})
