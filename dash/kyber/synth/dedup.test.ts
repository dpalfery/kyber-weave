// Tests for cross-path deduplication (task 9.3; R3.1–R3.3). The three
// acceptance criteria are the three load-bearing describe blocks below:
//
//   * R3.2 — the collapse runs on ONE key: upstream's cross-provider
//     deduplication key, already carried into identity by task 9.1's
//     synthesizer. A synthesized record's key is read verbatim off its trace
//     id; an OTLP-sourced record derives the SAME string from the same two
//     facts (provider + session id). There is no second key scheme to find.
//   * R3.1 — the integration case: one session synthesized through the file
//     path and normalized through the OTLP path, deduplicated, persisted
//     through the store's idempotent `upsertMany`, and counted exactly once.
//   * R3.3 — on disagreement the richer source wins and the disagreement is
//     recorded as a `DEDUP_DISAGREEMENT` problem carrying both sides'
//     figures — never silently discarded, and never a second record.
//
// The OTLP side is exercised for real: the fixture is an
// `ExportTraceServiceRequest` decoded through the receiver's own JSON
// decoder, then normalized through the shared task-5 cores
// (`readUsageCounters`, `exclusiveConvention`, `canonicalContent`) the way
// the adapters' `baseRecord` does — attributes stay the record's `raw`,
// which is where `deduplicationKeyFor` reads the session identity from.

import { describe, expect, it } from 'vitest'

import type { ParsedProviderCall } from '../../src/providers/types.js'
import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import {
  canonicalContent,
  exclusiveConvention,
  readUsageCounters,
} from '../canon/adapters/copilot.js'
import { DEFAULT_GROUP_ATTRIBUTE } from '../otel/aspire.js'
import { decodeOtlpJson, type OtlpSpan } from '../otel/receiver.js'
import { DEDUP_DISAGREEMENT, deduplicate, deduplicationKeyFor, joinOtelAndFileTurn } from './dedup.js'
import { Synthesizer, synthesizeCall, traceIdFor } from './synth.js'

// ---------------------------------------------------------------------------
// Fixture kit — the file path
// ---------------------------------------------------------------------------

/** A complete upstream `ParsedProviderCall`, with the spec's fields defaulted. */
function call(spec: Partial<ParsedProviderCall> = {}): ParsedProviderCall {
  return {
    provider: 'claude',
    model: 'claude-sonnet-4.5',
    inputTokens: 1_000,
    outputTokens: 240,
    cacheCreationInputTokens: 120,
    cacheReadInputTokens: 3_800,
    cachedInputTokens: 3_800,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0.0123,
    tools: ['Read', 'Bash'],
    bashCommands: [],
    timestamp: '2026-08-29T12:00:00.000Z',
    speed: 'standard',
    deduplicationKey: 'claude:s-1:m-1',
    userMessage: 'run the parity check',
    sessionId: 's-1',
    ...spec,
  }
}

/** The file path: the session's calls synthesized into canonical records. */
function fileSession(calls: ParsedProviderCall[]): CanonicalRecord[] {
  return new Synthesizer().synthesize(calls)
}

// ---------------------------------------------------------------------------
// Fixture kit — the OTLP path
// ---------------------------------------------------------------------------

const TRACE_ID = '4bf92f3577b34da6a3ce929d0e0e4736'
const SPAN_ID_1 = '00f067aa0ba902b7'
const SPAN_ID_2 = '00f067aa0ba902b8'
const SPAN_ID_3 = '00f067aa0ba902b9'

type SpanAttributes = Record<string, string | number>

/**
 * One span's `ExportTraceServiceRequest`, as a harness that both writes
 * session files and exports OTLP would emit for its turn. Numbers ride as
 * `doubleValue`, ids as plain hex — the forms the receiver's decoder
 * accepts from hand-rolled collectors.
 */
function otlpExport(spanId: string, attributes: SpanAttributes): string {
  return JSON.stringify({
    resourceSpans: [
      {
        resource: {
          attributes: [{ key: 'service.name', value: { stringValue: 'claude-code' } }],
        },
        scopeSpans: [
          {
            scope: { name: 'claude-code-otel' },
            spans: [
              {
                traceId: TRACE_ID,
                spanId,
                parentSpanId: '',
                name: 'chat turn',
                startTimeUnixNano: '1756468800000000000',
                endTimeUnixNano: '1756468804500000000',
                attributes: Object.entries(attributes).map(([key, value]) => ({
                  key,
                  value:
                    typeof value === 'number' ? { doubleValue: value } : { stringValue: value },
                })),
              },
            ],
          },
        ],
      },
    ],
  })
}

/** The turn attributes the harness exports for its session `s-1`. */
function turnAttributes(spec: Record<string, string | number | undefined> = {}): SpanAttributes {
  const attributes: SpanAttributes = {
    [DEFAULT_GROUP_ATTRIBUTE]: 's-1',
    'gen_ai.usage.input_tokens': 1_000,
    'gen_ai.usage.cache_read.input_tokens': 3_800,
    'gen_ai.usage.cache_creation.input_tokens': 120,
    'gen_ai.usage.output_tokens': 240,
    'codeburn.cost_usd': 0.0123,
    ...spec,
  }
  // An absent counter is spelled by omitting the attribute, not by nulling it.
  for (const [key, value] of Object.entries(attributes)) {
    if (value === undefined) delete attributes[key]
  }
  return attributes
}

/**
 * The OTLP→canonical seam task 5's normalization layer owns, rebuilt here
 * just enough to feed `deduplicate` from a genuinely decoded span: token
 * counters convert on the way in through the shared exclusive-convention
 * core, content maps onto canonical keys, the harness is stamped, and the
 * span's attributes stay the record's `raw` (the adapters' `baseRecord`
 * convention — and the payload `deduplicationKeyFor` reads).
 */
function normalizeOtlpSpan(span: OtlpSpan, harness: string): CanonicalRecord {
  const counters = readUsageCounters(span.attributes)
  const costUsd = span.attributes['codeburn.cost_usd']
  return {
    spanId: span.spanId,
    traceId: span.traceId,
    parentSpanId: span.parentSpanId,
    source: 'claude-code',
    harness,
    name: span.name,
    op: 'llm.invoke',
    kind: span.kind,
    timestamp: span.timestamp,
    durationMs: span.durationMs,
    status: span.status.code,
    tokens: exclusiveConvention({
      input: counters.input,
      cacheRead: counters.cacheRead,
      cacheCreation: counters.cacheCreation,
      output: counters.output,
      ...(counters.reasoning > 0 ? { reasoning: counters.reasoning } : {}),
    }),
    content: canonicalContent(span.attributes),
    cost:
      typeof costUsd === 'number' && costUsd !== 0
        ? { basis: 'harness', status: 'priced', value: costUsd, currency: 'USD' }
        : { basis: 'unknown', status: 'no_rate' },
    raw: span.attributes,
  }
}

function otlpSession(spanId: string, attributes: SpanAttributes): CanonicalRecord[] {
  const [span] = decodeOtlpJson(otlpExport(spanId, attributes))
  return [normalizeOtlpSpan(span!, 'claude')]
}

// ---------------------------------------------------------------------------
// R3.2 — one key: upstream's deduplication key, extended
// ---------------------------------------------------------------------------

describe('identity (R3.2 — upstream’s key, extended, not a second mechanism)', () => {
  it('reads a synthesized record’s key verbatim from its trace id', () => {
    const [record] = fileSession([call()])
    expect(deduplicationKeyFor(record!)).toBe(record!.traceId)
    expect(record!.traceId).toBe('synth:claude:s-1')
  })

  it('derives the same key for the OTLP record of the same session', () => {
    const [synthRecord] = fileSession([call()])
    const [otlpRecord] = otlpSession(SPAN_ID_1, turnAttributes())
    // Both paths land on `synth:<provider>:<session>` — traceIdFor's own
    // shape, applied to the same provider and session identity.
    expect(deduplicationKeyFor(otlpRecord!)).toBe('synth:claude:s-1')
    expect(deduplicationKeyFor(otlpRecord!)).toBe(deduplicationKeyFor(synthRecord!))
    expect(deduplicationKeyFor(otlpRecord!)).toBe(traceIdFor(call()))
  })

  it('claims no cross-path identity for an OTLP span without a session attribute', () => {
    const [noSession] = otlpSession(SPAN_ID_1, {
      'gen_ai.usage.output_tokens': 240,
    })
    expect(deduplicationKeyFor(noSession!)).toBeNull()
    // An empty session id cannot ride a wire payload (an empty AnyValue is
    // invalid OTLP JSON and the receiver rejects it), so the empty case is
    // built on the record directly: it claims nothing, like a missing one.
    const [wired] = otlpSession(SPAN_ID_1, turnAttributes())
    const emptied: CanonicalRecord = {
      ...wired!,
      raw: { ...(wired!.raw as Record<string, unknown>), [DEFAULT_GROUP_ATTRIBUTE]: '' },
    }
    expect(deduplicationKeyFor(emptied)).toBeNull()
  })

  it('claims no identity for a non-synthesized record whose raw carries no attributes', () => {
    const record: CanonicalRecord = {
      ...synthesizeCall(call()),
      spanId: SPAN_ID_1, // hex: takes the OTLP branch, not the synth branch
      raw: undefined,
    }
    expect(deduplicationKeyFor(record)).toBeNull()
  })

  it('claims no identity for a synthesized record detached from its trace', () => {
    const orphan: CanonicalRecord = { ...synthesizeCall(call()), traceId: null }
    expect(deduplicationKeyFor(orphan)).toBeNull()
  })
})

// ---------------------------------------------------------------------------
// R3.1 — one session through both paths counted exactly once
// ---------------------------------------------------------------------------

describe('R3.1 — a session observed through both paths is counted once', () => {
  it('ingests through both paths and stores one record with one count of tokens and cost', () => {
    const store = new CanonStore(':memory:')
    try {
      // The same session (claude, s-1), described twice: once as the file
      // the parser reads, once as the span the harness exported.
      const fileRecords = fileSession([call()]).map((record) => ({
        ...record,
        parts: [{ part: 'conversation_history' as const, text: 'file-only turn content', order: 0 }],
        content: { conversation_history: 'file-only turn content' },
      }))
      const otlpRecords = otlpSession(
        SPAN_ID_1,
        turnAttributes({
          'gen_ai.usage.input_tokens': 990, // the paths disagree on fresh input
          'gen_ai.usage.reasoning_tokens': 96, // only the OTLP path reports reasoning
          'gen_ai.prompt': 'why is the parity gate red?', // only the OTLP path has content
        }),
      )

      const kept = deduplicate(fileRecords, otlpRecords, store)
      store.upsertMany(kept)

      // One identity: one record survives the collapse and one row lands.
      expect(kept).toHaveLength(1)
      expect(store.count()).toBe(1)

      // The richer source won: the OTLP record carries the reasoning
      // counter and the content the file path can never supply.
      const stored = store.get(kept[0]!.spanId)
      expect(stored?.spanId).toBe(otlpRecords[0]!.spanId)

      // Turns, tokens and cost counted once: the stored figures are the
      // winner's own decomposition, not the sum of both paths' versions.
      expect(stored?.tokens).toEqual({
        freshInput: 990,
        cacheRead: 3_800,
        cacheCreation: 120,
        output: 240,
        reasoning: 96,
        reportedInput: 4_910,
        reportedOutput: 240,
      })
      expect(stored?.cost).toEqual({
        basis: 'harness',
        status: 'priced',
        value: 0.0123,
        currency: 'USD',
      })
      // D7: the OTLP row had no structured parts, so its counters stay
      // authoritative while file-derived content fills the empty bucket.
      expect(stored?.parts).toEqual(fileRecords[0]!.parts)
      expect(stored?.content).toEqual(fileRecords[0]!.content)

      // And the collapse is idempotent end to end: re-ingesting the same
      // collapsed output rewrites the same row (R2.5).
      store.upsertMany(kept)
      expect(store.count()).toBe(1)
    } finally {
      store.close()
    }
  })

  it('keeps OTel parts when both paths supplied structured content', () => {
    const fileRecord: CanonicalRecord = {
      ...synthesizeCall(call()),
      parts: [{ part: 'conversation_history', text: 'file content', order: 0 }],
      content: { conversation_history: 'file content' },
    }
    const otlpRecord: CanonicalRecord = {
      ...fileRecord,
      spanId: SPAN_ID_1,
      source: 'claude-code',
      parts: [{ part: 'conversation_history', text: 'OTel content', order: 0 }],
      content: { conversation_history: 'OTel content' },
    }

    const joined = joinOtelAndFileTurn(otlpRecord, fileRecord)

    expect(joined.tokens).toEqual(otlpRecord.tokens)
    expect(joined.parts).toEqual(otlpRecord.parts)
    expect(joined.content).toEqual(otlpRecord.content)
  })

  it('keeps every turn of a session only one path saw — turns are not duplicates', () => {
    const store = new CanonStore(':memory:')
    try {
      const fileRecords = fileSession([
        call({ deduplicationKey: 'claude:s-1:m-1' }),
        call({ deduplicationKey: 'claude:s-1:m-2', inputTokens: 1_100 }),
        call({ deduplicationKey: 'claude:s-1:m-3', inputTokens: 1_200 }),
      ])

      const kept = deduplicate(fileRecords, [], store)
      store.upsertMany(kept)

      expect(kept).toHaveLength(3)
      expect(store.count()).toBe(3)
      expect(store.getProblems()).toEqual([])
      expect([...kept].sort((a, b) => a.spanId.localeCompare(b.spanId))).toEqual(
        [...fileRecords].sort((a, b) => a.spanId.localeCompare(b.spanId)),
      )
    } finally {
      store.close()
    }
  })

  it('keeps the OTel rows for a both-paths session, counted once', () => {
    const store = new CanonStore(':memory:')
    try {
      const turns = [0, 1, 2]
      const fileRecords = fileSession(
        turns.map((turn) =>
          call({
            deduplicationKey: `claude:s-1:m-${turn + 1}`,
            inputTokens: 1_000 + turn * 100,
            timestamp: new Date(Date.UTC(2026, 7, 29, 12, turn)).toISOString(),
          }),
        ),
      )
      const otlpRecords = turns.map((turn, index) =>
        otlpSession(
          [SPAN_ID_1, SPAN_ID_2, SPAN_ID_3][index]!,
          turnAttributes({ 'gen_ai.usage.input_tokens': 1_000 + turn * 100 }),
        )[0]!,
      )

      const kept = deduplicate(fileRecords, otlpRecords, store)
      store.upsertMany(kept)

      // D7 keeps the OTel accounting rows — not the file rows — while still
      // collapsing the session to three distinct turns.
      expect(kept).toHaveLength(3)
      expect(store.count()).toBe(3)
      for (const record of kept) {
        expect(record.spanId).toMatch(/^[0-9a-f]+$/)
      }

      // The paths reported identical values, so the collapse is silent.
      expect(store.getProblems()).toEqual([])

      // Tokens counted once: three file turns' fresh input, never doubled.
      const stored = kept.map((record) => store.get(record.spanId))
      const totalFreshInput = stored.reduce((sum, record) => sum + record!.tokens.freshInput, 0)
      expect(totalFreshInput).toBe(1_000 + 1_100 + 1_200)
    } finally {
      store.close()
    }
  })

  it('lets an OTLP span with no session identity through instead of collapsing it', () => {
    const store = new CanonStore(':memory:')
    try {
      const fileRecords = fileSession([call()])
      const [unidentified] = otlpSession(SPAN_ID_1, {
        // Same session's counters, but the span carries no `session.id`:
        // it claims no cross-path identity and must not be force-collapsed.
        'gen_ai.usage.input_tokens': 1_000,
        'gen_ai.usage.cache_read.input_tokens': 3_800,
        'gen_ai.usage.cache_creation.input_tokens': 120,
        'gen_ai.usage.output_tokens': 240,
      })

      const kept = deduplicate(fileRecords, [unidentified!], store)
      store.upsertMany(kept)

      expect(kept).toHaveLength(2)
      expect(store.count()).toBe(2)
      expect(store.getProblems()).toEqual([])
    } finally {
      store.close()
    }
  })

  it('passes an OTLP-only corpus through untouched', () => {
    const store = new CanonStore(':memory:')
    try {
      const otlpRecords = otlpSession(SPAN_ID_1, turnAttributes())
      const kept = deduplicate([], otlpRecords, store)
      expect(kept).toEqual(otlpRecords)
      store.upsertMany(kept)
      expect(store.count()).toBe(1)
      expect(store.getProblems()).toEqual([])
    } finally {
      store.close()
    }
  })
})

// ---------------------------------------------------------------------------
// R3.3 — prefer the richer source, record the disagreement
// ---------------------------------------------------------------------------

describe('R3.3 — D7 precedence, disagreement recorded', () => {
  it('keeps the richer OTLP side and records the disagreement as a problem', () => {
    const store = new CanonStore(':memory:')
    try {
      const fileRecords = fileSession([call()])
      const otlpRecords = otlpSession(
        SPAN_ID_1,
        turnAttributes({
          'gen_ai.usage.input_tokens': 990,
          'gen_ai.usage.reasoning_tokens': 96,
          'gen_ai.prompt': 'why is the parity gate red?',
        }),
      )

      const kept = deduplicate(fileRecords, otlpRecords, store)

      expect(kept).toHaveLength(1)
      expect(kept[0]!.spanId).toBe(otlpRecords[0]!.spanId)
      // The richer side's content survives the collapse.
      expect(kept[0]!.content.conversation_history).toBe('why is the parity gate red?')

      const problems = store.getProblems()
      expect(problems).toHaveLength(1)
      const problem = problems[0]!
      expect(problem.code).toBe(DEDUP_DISAGREEMENT)
      expect(problem.severity).toBe('warning')
      expect(problem.location).toBe('synth:claude:s-1')
      expect(problem.spanId).toBe(kept[0]!.spanId)
      // Both sides' figures are in the message — the dropped side's values
      // are recorded, not discarded with its records.
      expect(problem.message).toContain('1000')
      expect(problem.message).toContain('990')
      // The problem is a content-free audit trail: ids and numbers only.
      expect(problem.message).not.toContain('parity gate')
    } finally {
      store.close()
    }
  })

  it('keeps the OTel side even when the file path carries more, and records the disagreement', () => {
    const store = new CanonStore(':memory:')
    try {
      const fileRecords = fileSession([call()])
      const otlpRecords = otlpSession(
        SPAN_ID_1,
        // Same shape as the file record (no reasoning, no content, no cost
        // attribution) but a disagreeing fresh-input counter and a smaller
        // raw payload: the file path is the richer source.
        turnAttributes({ 'gen_ai.usage.input_tokens': 990, 'codeburn.cost_usd': undefined }),
      )

      const kept = deduplicate(fileRecords, otlpRecords, store)
      store.upsertMany(kept)

      expect(kept).toHaveLength(1)
      expect(kept[0]!.spanId).toBe(SPAN_ID_1)
      expect(store.count()).toBe(1)

      const problems = store.getProblems()
      expect(problems).toHaveLength(1)
      expect(problems[0]!.code).toBe(DEDUP_DISAGREEMENT)
      expect(problems[0]!.spanId).toBe(SPAN_ID_1)
      expect(problems[0]!.message).toContain('990')
    } finally {
      store.close()
    }
  })

  it('collapses silently when the two paths agree on every value', () => {
    const store = new CanonStore(':memory:')
    try {
      const fileRecords = fileSession([call()])
      const otlpRecords = otlpSession(SPAN_ID_1, turnAttributes())

      const kept = deduplicate(fileRecords, otlpRecords, store)
      store.upsertMany(kept)

      expect(kept).toHaveLength(1)
      expect(store.count()).toBe(1)
      // Agreement is a non-event: no problem is recorded for it.
      expect(store.getProblems()).toEqual([])
      const stored = store.get(kept[0]!.spanId)
      expect(stored?.tokens.freshInput).toBe(1_000)
      expect(stored?.tokens.reportedInput).toBe(4_920)
    } finally {
      store.close()
    }
  })

  it('records the disagreement once per session, not once per disagreeing value', () => {
    const store = new CanonStore(':memory:')
    try {
      const fileRecords = fileSession([call()])
      const otlpRecords = otlpSession(
        SPAN_ID_1,
        turnAttributes({
          'gen_ai.usage.input_tokens': 990, // disagrees
          'gen_ai.usage.output_tokens': 260, // disagrees again
        }),
      )

      deduplicate(fileRecords, otlpRecords, store)

      const problems = store.getProblems()
      expect(problems).toHaveLength(1)
      // The message names the first disagreement but carries both sides'
      // full figures, so every differing value is auditable from one row.
      expect(problems[0]!.message).toContain('fresh input (1000 vs 990)')
      expect(problems[0]!.message).toContain('output=240')
      expect(problems[0]!.message).toContain('output=260')
    } finally {
      store.close()
    }
  })
})
