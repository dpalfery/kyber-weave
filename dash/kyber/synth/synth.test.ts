// Tests for the span synthesizer (task 9.1; R1.1, R1.4, R1.5). The three
// acceptance criteria are the three load-bearing describe blocks below:
//
//   * R1.1 — every provider upstream supports synthesizes with zero
//     configuration, pinned against upstream's own `allProviderNames()` so a
//     new upstream provider fails the convention-table drift check instead
//     of silently taking the default.
//   * R1.4 — synthesis runs with `fetch` stubbed to throw: no API key, no
//     proxy, no network call, no agent-tool wrapper — the corpus is the
//     machine's own files, already parsed.
//   * R1.5 — the parallel cold-parse arrival pattern (chunked, resolved
//     concurrently, assembled in submission order) deep-equals the serial
//     path, across chunk sizes and input permutations, so the equality is a
//     tested property of `synthesizeCall`'s purity rather than an accident
//     of one fixture's shape.
//
// Alongside them: the R4.2 convention conversion (including the
// inverted-convention case failing loudly through `validateTokens`, the
// check that exists precisely because this conversion once went wrong), the
// R3.2 identity scheme (span id = upstream's deduplication key, extended,
// which is what makes re-synthesis idempotent), the R5.x cost bases, and the
// R7.6/R8.5/R10.2 measurability declarations.

import { describe, expect, it, vi } from 'vitest'

import { allProviderNames } from '../../src/providers/index.js'
import type { ParsedProviderCall } from '../../src/providers/types.js'
import { tokenValidator } from '../canon/adapters/quarantine.js'
import { validateTokens } from '../canon/types.js'
import {
  DEFAULT_CONVENTION,
  FILE_SOURCE_UNMEASURABLE,
  PROVIDER_CONVENTIONS,
  Synthesizer,
  conventionFor,
  costBlockFor,
  measurabilityFor,
  spanIdFor,
  synthesizeCall,
  type TokenConvention,
} from './synth.js'

// ---------------------------------------------------------------------------
// Fixture kit
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

/**
 * A corpus large enough to span several chunks under every chunk size the
 * parity test uses: multiple providers (each convention row exercised),
 * multiple sessions (distinct trace ids), and varied token shapes.
 */
function corpus(): ParsedProviderCall[] {
  const providers = ['claude', 'codex', 'gemini', 'pi', 'copilot'] as const
  const calls: ParsedProviderCall[] = []
  for (const [providerIndex, provider] of providers.entries()) {
    for (let session = 0; session < 3; session++) {
      for (let turn = 0; turn < 9; turn++) {
        calls.push(
          call({
            provider,
            model: `${provider}-model-1`,
            inputTokens: 500 + turn * 37 + providerIndex,
            outputTokens: 120 + turn * 11,
            cacheCreationInputTokens: provider === 'gemini' ? 0 : 40 + turn,
            cacheReadInputTokens: 900 + turn * 53,
            cachedInputTokens: provider === 'claude' ? 0 : 900 + turn * 53,
            reasoningTokens: provider === 'codex' ? 30 + turn : 0,
            costUSD: provider === 'gemini' ? 0 : 0.004 + turn * 0.001,
            timestamp: new Date(Date.UTC(2026, 7, 29, 12, 0, turn * 30)).toISOString(),
            deduplicationKey: `${provider}:session-${session}:turn-${turn}`,
            sessionId: `session-${session}`,
          }),
        )
      }
    }
  }
  return calls
}

// ---------------------------------------------------------------------------
// R1.1 — every provider, no configuration
// ---------------------------------------------------------------------------

describe('R1.1 — first run with no configuration covers every provider', () => {
  it('synthesizes a valid record for every provider the upstream parser supports', () => {
    const synthesizer = new Synthesizer() // no options: the first-run path
    const names = allProviderNames()
    expect(names.length).toBeGreaterThan(30)

    const records = synthesizer.synthesize(
      names.map((provider) =>
        call({
          provider,
          deduplicationKey: `${provider}:s:m`,
          sessionId: 's',
        }),
      ),
    )

    expect(records).toHaveLength(names.length)
    for (const [i, provider] of names.entries()) {
      const record = records[i]!
      expect(record.harness).toBe(provider)
      // The record must hold as stored: disjoint classes reconciling with
      // the reported input (R4.1) — the convention row was applied, not skipped.
      expect(validateTokens(record.tokens, record.spanId)).toEqual({ valid: true })
      expect(tokenValidator(record)).toBeUndefined()
    }
  })

  it('declares an explicit convention row for every upstream provider (drift check)', () => {
    // A new upstream provider lands with no row and fails here, so adding
    // provider #42 is a deliberate act: measure its parser, add the row.
    for (const name of allProviderNames()) {
      expect(PROVIDER_CONVENTIONS.has(name), `no convention row for ${name}`).toBe(true)
    }
    // The table carries no names upstream does not — a renamed provider must
    // not leave a stale row behind.
    for (const name of PROVIDER_CONVENTIONS.keys()) {
      expect(allProviderNames()).toContain(name)
    }
  })

  it('falls back to the documented default for a name without a row', () => {
    expect(conventionFor('brand-new-provider')).toBe(DEFAULT_CONVENTION)
    // And the fallback synthesizes rather than failing the first run.
    const [record] = new Synthesizer().synthesize([
      call({ provider: 'brand-new-provider', deduplicationKey: 'x:s:m' }),
    ])
    expect(record?.harness).toBe('brand-new-provider')
    expect(validateTokens(record!.tokens)).toEqual({ valid: true })
  })
})

// ---------------------------------------------------------------------------
// R1.4 — offline by construction
// ---------------------------------------------------------------------------

describe('R1.4 — no key, proxy, network call, or agent-tool wrapper', () => {
  it('synthesizes with fetch stubbed to throw', () => {
    const fetchStub = vi.fn(() => {
      throw new Error('R1.4: synthesis must not touch the network')
    })
    vi.stubGlobal('fetch', fetchStub)

    try {
      const records = new Synthesizer().synthesize(corpus())
      expect(records).toHaveLength(corpus().length)
      expect(fetchStub).not.toHaveBeenCalled()
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('consults nothing but the calls: the same input gives the same output', () => {
    const synthesizer = new Synthesizer()
    const first = synthesizer.synthesize(corpus())
    const second = synthesizer.synthesize(corpus())
    expect(second).toEqual(first)
  })
})

// ---------------------------------------------------------------------------
// R1.5 — parallel cold-parse path ≡ serial path
// ---------------------------------------------------------------------------

describe('R1.5 — parallel and serial produce identical output', () => {
  it('deep-equals serial output for the same corpus', async () => {
    const calls = corpus()
    const synthesizer = new Synthesizer()
    const serial = synthesizer.synthesizeSerial(calls)
    const parallel = await synthesizer.synthesizeParallel(calls)
    expect(parallel).toEqual(serial)
  })

  it('deep-equals serial output across chunk sizes, including chunk > corpus', async () => {
    const calls = corpus()
    const serial = new Synthesizer().synthesizeSerial(calls)
    for (const chunkSize of [1, 7, calls.length - 1, calls.length, calls.length + 5]) {
      const parallel = await new Synthesizer({ chunkSize }).synthesizeParallel(calls)
      expect(parallel, `chunkSize ${chunkSize}`).toEqual(serial)
    }
  })

  it('keeps every record position-independent: a permutation yields the same records by span id', () => {
    const calls = corpus()
    const bySpanId = new Map(
      new Synthesizer().synthesize(calls).map((record) => [record.spanId, record]),
    )

    const reversed = [...calls].reverse()
    const recordsFromReversed = new Synthesizer().synthesize(reversed)
    expect(recordsFromReversed).toHaveLength(bySpanId.size)
    for (const record of recordsFromReversed) {
      expect(record).toEqual(bySpanId.get(record.spanId))
    }
  })

  it('preserves input order in the output of both paths', async () => {
    const calls = corpus()
    const synthesizer = new Synthesizer()
    const serial = synthesizer.synthesizeSerial(calls)
    expect(serial.map((record) => record.spanId)).toEqual(calls.map(spanIdFor))
    const parallel = await synthesizer.synthesizeParallel(calls)
    expect(parallel.map((record) => record.spanId)).toEqual(serial.map((record) => record.spanId))
  })
})

// ---------------------------------------------------------------------------
// R4.2 — token conventions converted on the way in
// ---------------------------------------------------------------------------

describe('token conversion (R4.2)', () => {
  it('takes fresh input as claimed and reassembles the reported total (exclusive rows)', () => {
    const record = synthesizeCall(
      call({
        inputTokens: 1_000,
        cacheCreationInputTokens: 120,
        cacheReadInputTokens: 3_800,
        cachedInputTokens: 0, // claude leaves the mirror at zero
        outputTokens: 240,
        reasoningTokens: 96,
      }),
    )
    expect(record.tokens).toEqual({
      freshInput: 1_000,
      cacheRead: 3_800,
      cacheCreation: 120,
      output: 240,
      reasoning: 96,
      reportedInput: 1_000 + 3_800 + 120,
      reportedOutput: 240,
    })
    expect(validateTokens(record.tokens)).toEqual({ valid: true })
  })

  it('reads the mirrored cache-read field only when the authoritative one is zero', () => {
    const mirrorOnly = synthesizeCall(
      call({
        provider: 'pi',
        cacheReadInputTokens: 0, // a parser variant that only sets the mirror
        cachedInputTokens: 2_000,
      }),
    )
    expect(mirrorOnly.tokens.cacheRead).toBe(2_000)
    expect(mirrorOnly.tokens.freshInput).toBe(1_000)
  })

  it('subtracts the cache classes for an inclusive row and still reconciles', () => {
    // The inclusive conversion, exercised positively: a harness whose input
    // counter totals fresh + cache classes reconciles after subtraction.
    const inclusive = new Map<string, TokenConvention>([['inclusive-provider', 'inclusive']])
    const record = synthesizeCall(
      call({
        provider: 'inclusive-provider',
        inputTokens: 4_920, // 1_000 fresh + 3_800 read + 120 creation
        cacheCreationInputTokens: 120,
        cacheReadInputTokens: 3_800,
      }),
      inclusive,
    )
    expect(record.tokens.freshInput).toBe(1_000)
    expect(record.tokens.reportedInput).toBe(4_920)
    expect(validateTokens(record.tokens)).toEqual({ valid: true })
  })

  it('fails loudly when the inverted convention is applied — R4.2’s measured failure', () => {
    // Feed exclusive-shaped counters (input EXCLUDES cache) through the
    // inclusive conversion: fresh goes negative and the record validator
    // rejects it. This is the exact miscount R4.2 exists to catch — clamping
    // the subtraction would turn it into a silently underpriced record.
    const inverted = new Map<string, TokenConvention>([['claude', 'inclusive']])
    const record = synthesizeCall(
      call({
        provider: 'claude',
        inputTokens: 1_000, // fresh-only, but read as if cache-inclusive
        cacheReadInputTokens: 3_800,
        cacheCreationInputTokens: 120,
      }),
      inverted,
    )
    expect(record.tokens.freshInput).toBe(1_000 - 3_800 - 120)
    const problem = tokenValidator(record)
    expect(problem?.code).toBe('TOKEN_NEGATIVE_FRESH')
    expect(problem?.location).toBe(record.spanId)
  })
})

// ---------------------------------------------------------------------------
// R3.2 — identity extends upstream's deduplication key
// ---------------------------------------------------------------------------

describe('identity scheme (R3.2 groundwork)', () => {
  it('derives the span id from upstream’s deduplication key, namespaced', () => {
    const record = synthesizeCall(
      call({ provider: 'claude', sessionId: 's-9', deduplicationKey: 'claude:s-9:t-4' }),
    )
    expect(record.spanId).toBe('synth:claude:s-9:t-4')
    expect(record.traceId).toBe('synth:claude:s-9')
  })

  it('keys the trace by provider and session, so sessions never merge', () => {
    const a = synthesizeCall(call({ sessionId: 's-1', deduplicationKey: 'claude:s-1:m-1' }))
    const b = synthesizeCall(call({ sessionId: 's-2', deduplicationKey: 'claude:s-2:m-1' }))
    const otherProvider = synthesizeCall(
      call({ provider: 'pi', sessionId: 's-1', deduplicationKey: 'pi:s-1:m-1' }),
    )
    expect(a.traceId).not.toBe(b.traceId)
    expect(a.traceId).not.toBe(otherProvider.traceId)
  })

  it('makes re-synthesis idempotent: the same call yields the same span id', () => {
    // One session arriving through two paths lands on one identity for the
    // store's idempotent upsert to collapse (the task 9.3 case, held open
    // by this scheme rather than closed by a second dedup mechanism).
    const once = synthesizeCall(call())
    const twice = synthesizeCall(call())
    expect(twice.spanId).toBe(once.spanId)
    expect(twice).toEqual(once)
  })
})

// ---------------------------------------------------------------------------
// Cost bases (R5.1, R5.2, R5.4)
// ---------------------------------------------------------------------------

describe('cost blocks (R5.1, R5.2, R5.4)', () => {
  it('carries a measured figure verbatim on the harness basis, with its model', () => {
    expect(costBlockFor(call({ costUSD: 0.0123, costIsEstimated: false }))).toEqual({
      basis: 'harness',
      status: 'priced',
      value: 0.0123,
      currency: 'USD',
      byModel: { 'claude-sonnet-4.5': 0.0123 },
    })
    // Absent flag reads the same way: the parsed-call default is a
    // provider-reported figure unless upstream said otherwise.
    expect(costBlockFor(call({ costUSD: 0.5 })).basis).toBe('harness')
  })

  it('labels upstream’s rate-table estimate as published, not harness-reported', () => {
    const block = costBlockFor(call({ costUSD: 0.0456, costIsEstimated: true }))
    expect(block.basis).toBe('published')
    expect(block.status).toBe('priced')
  })

  it('renders a zero figure as no published rate, never a priced $0.00 (R5.4)', () => {
    expect(costBlockFor(call({ costUSD: 0 }))).toEqual({ basis: 'unknown', status: 'no_rate' })
    // A non-finite figure upstream failed to compute is absent, not priced.
    expect(costBlockFor(call({ costUSD: Number.NaN })).status).toBe('no_rate')
  })
})

// ---------------------------------------------------------------------------
// Measurability declarations (R7.6, R8.5, R10.2)
// ---------------------------------------------------------------------------

describe('measurability declarations for the file-sourced path', () => {
  it('declares schema ranking and every content metric not measurable — never zero', () => {
    const record = synthesizeCall(call())
    expect(record.measurability).toEqual(measurabilityFor('claude'))
    // The declared set: schema cost, plus all five canonical content keys
    // (tool_definitions among them — the schema-ranking input of R8.5).
    expect(Object.keys(record.measurability ?? {}).sort()).toEqual(
      [...FILE_SOURCE_UNMEASURABLE].sort(),
    )
    for (const availability of Object.values(record.measurability ?? {})) {
      expect(availability).toBe('not_measurable')
    }
  })

  it('claims no content: a fragment is not a bucket (R7.6)', () => {
    const record = synthesizeCall(call({ userMessage: 'some fragment of the turn' }))
    expect(record.content).toEqual({})
  })

  it('adds per-provider counter gaps — gemini has no cache-creation counter', () => {
    const gemini = synthesizeCall(call({ provider: 'gemini' }))
    expect(gemini.measurability?.['cache_creation']).toBe('not_measurable')
    // The same metric stays undeclared for a provider that measures it —
    // absence from the map means measured, the vocabulary compare.ts reads.
    const claude = synthesizeCall(call())
    expect(claude.measurability?.['cache_creation']).toBeUndefined()
  })
})

// ---------------------------------------------------------------------------
// Record shape
// ---------------------------------------------------------------------------

describe('canonical record shape', () => {
  it('maps the parsed call onto the canonical fields', () => {
    const record = synthesizeCall(
      call({
        provider: 'codex',
        model: 'gpt-5.2-codex',
        timestamp: '2026-08-30T09:15:00.000Z',
        activeDurationMs: 2_450,
        deduplicationKey: 'codex:s-1:t-0',
        sessionId: 's-1',
      }),
    )
    expect(record.source).toBe('codeburn/codex')
    expect(record.harness).toBe('codex')
    expect(record.name).toBe('codex:gpt-5.2-codex')
    expect(record.op).toBe('llm.invoke')
    expect(record.kind).toBe('internal')
    expect(record.timestamp).toBe('2026-08-30T09:15:00.000Z')
    expect(record.durationMs).toBe(2_450)
    expect(record.status).toBe('unspecified')
    expect(record.parentSpanId).toBeNull()
    expect(record.raw).toBeDefined()
  })

  it('defaults duration to zero when the provider records none', () => {
    expect(synthesizeCall(call({ activeDurationMs: undefined })).durationMs).toBe(0)
  })
})
