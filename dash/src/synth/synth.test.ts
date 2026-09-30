import { createHash } from 'node:crypto'
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

import { allProviderNames } from '../providers/index.js'
import type { ParsedProviderCall } from '../providers/types.js'
import { tokenValidator } from '../canon/adapters/quarantine.js'
import { validateTokens, type CanonicalRecord, type ContentPart } from '../canon/types.js'
import type { ReaderToolCall, ReaderToolResult, ReaderTurn, SourceRecordEnvelope } from './readers/types.js'
import {
  DEFAULT_CONVENTION,
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
    const names = allProviderNames().filter((provider) => provider !== 'gemini' && provider !== 'vercel-gateway')
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
      expect(records.map((record) => record.harness)).not.toContain('gemini')
      expect(records).toHaveLength(corpus().filter((item) => item.provider !== 'gemini').length)
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
    expect(serial.map((record) => record.spanId)).toEqual(
      calls.filter((item) => item.provider !== 'gemini').map((item) => spanIdFor(item)),
    )
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
  it('declares Claude’s unavailable buckets while retaining its stored conversation and results', () => {
    const record = synthesizeCall(call())
    expect(record.measurability).toEqual(measurabilityFor('claude'))
    expect(Object.keys(record.measurability ?? {}).sort()).toEqual(
      ['schema_cost', 'system_prompt', 'tool_definitions'],
    )
    for (const availability of Object.values(record.measurability ?? {})) {
      expect(availability).toMatchObject({ availability: 'not_measurable' })
    }
    // Undeclared means available: Claude files genuinely retain these buckets.
    expect(record.measurability?.['conversation_history']).toBeUndefined()
    expect(record.measurability?.['tool_result_content']).toBeUndefined()
  })

  it('claims no content: a fragment is not a bucket (R7.6)', () => {
    const record = synthesizeCall(call({ userMessage: 'some fragment of the turn' }))
    expect(record.content).toEqual({})
  })

  it('omits Gemini as a harness rather than declaring its cache-creation gap on a gemini row', () => {
    expect(new Synthesizer().synthesize([call({ provider: 'gemini' })])).toEqual([])
    const antigravity = synthesizeCall(call({ provider: 'antigravity', model: 'gemini-2.5-pro' }))
    expect(antigravity.harness).toBe('antigravity')
    expect(antigravity.harness).not.toBe('gemini')
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

// ---------------------------------------------------------------------------
// T4 — identity-aware synthesis (split harness, provenance, no Gemini harness)
// ---------------------------------------------------------------------------

function envelope(
  spec: Partial<SourceRecordEnvelope> & {
    call?: ParsedProviderCall
  } = {},
): SourceRecordEnvelope {
  const parsed = spec.call ?? call({ provider: 'antigravity', deduplicationKey: 'antigravity:s-1:m-1' })
  return {
    harnessId: spec.harnessId ?? 'antigravity-cli',
    sourceKey: spec.sourceKey ?? 'antigravity-cli:s-1',
    nativeSessionId: spec.nativeSessionId ?? parsed.sessionId,
    nativeRecordId: Object.prototype.hasOwnProperty.call(spec, 'nativeRecordId') ? spec.nativeRecordId : 'm-1',
    sourceRevision: spec.sourceRevision ?? 'rev-1',
    parserContractVersion: spec.parserContractVersion ?? '1',
    importedAt: spec.importedAt ?? '2026-09-12T00:00:00.000Z',
    locationToken: spec.locationToken ?? '~/.gemini/antigravity-cli',
    call: parsed,
    ...(spec.readerTurn !== undefined ? { readerTurn: spec.readerTurn } : {}),
    ...(spec.recordDigest !== undefined ? { recordDigest: spec.recordDigest } : {}),
  }
}

describe('T4 — identity-aware synthesis', () => {
  it('stamps classified harness and provenance without changing token validation', () => {
    const record = synthesizeCall(call({ provider: 'antigravity' }), undefined, undefined, envelope())
    expect(record.harness).toBe('antigravity-cli')
    expect(record.harness).not.toBe('gemini')
    expect(record.spanId).toBe('synth:antigravity-cli:s-1:m-1')
    expect(record.traceId).toBe('synth:antigravity-cli:s-1')
    const provenance = (record.raw as { provenance?: Record<string, string> }).provenance
    expect(provenance).toMatchObject({
      harnessId: 'antigravity-cli',
      sourceKey: 'antigravity-cli:s-1',
      nativeSessionId: 's-1',
      nativeRecordId: 'm-1',
      sourceRevision: 'rev-1',
      parserContractVersion: '1',
      locationToken: '~/.gemini/antigravity-cli',
    })
    expect(validateTokens(record.tokens, record.spanId)).toEqual({ valid: true })
    expect(tokenValidator(record)).toBeUndefined()
  })

  it('keeps split-surface span ids stable across an unchanged rerun', () => {
    const first = new Synthesizer().synthesizeEnvelopes([envelope()])
    const second = new Synthesizer().synthesizeEnvelopes([envelope()])
    expect(second).toEqual(first)
    expect(second[0]?.spanId).toBe(first[0]?.spanId)
  })

  it('updates a changed record in place: same span id, new counters', () => {
    const original = envelope()
    const changed = envelope({
      call: call({
        provider: 'antigravity',
        inputTokens: 2_000,
        deduplicationKey: 'antigravity:s-1:m-1',
      }),
    })
    const [before] = new Synthesizer().synthesizeEnvelopes([original])
    const [after] = new Synthesizer().synthesizeEnvelopes([changed])
    expect(after?.spanId).toBe(before?.spanId)
    expect(after?.tokens.freshInput).toBe(2_000)
    expect(after?.tokens.freshInput).not.toBe(before?.tokens.freshInput)
  })

  it('never persists Gemini as a harness, even when the model is Gemini', () => {
    const geminiCall = call({ provider: 'gemini', model: 'gemini-2.5-pro', deduplicationKey: 'gemini:s-1:m-1' })
    expect(new Synthesizer().synthesize([geminiCall]).map((record) => record.harness)).not.toContain('gemini')
    expect(
      new Synthesizer()
        .synthesizeEnvelopes([envelope({ harnessId: 'gemini', call: geminiCall, sourceKey: 'gemini:s-1' })])
        .map((record) => record.harness),
    ).not.toContain('gemini')
    const antigravityGemini = synthesizeCall(
      call({ provider: 'antigravity', model: 'gemini-2.5-pro' }),
      undefined,
      undefined,
      envelope({ harnessId: 'antigravity' }),
    )
    expect(antigravityGemini.harness).toBe('antigravity')
    expect(antigravityGemini.name).toContain('gemini-2.5-pro')
  })

  it('does not guess a Copilot client surface from the provider name alone', () => {
    const record = synthesizeCall(call({ provider: 'copilot', deduplicationKey: 'copilot:s-1:m-1' }))
    expect(record.harness).toBe('copilot')
    expect(record.harness).not.toBe('copilot-cli')
    expect(record.harness).not.toBe('copilot-vscode')
  })

  it('derives a refresh-time-free digest when the native record id is absent', () => {
    const first = envelope({
      nativeRecordId: undefined,
      call: call({ provider: 'pi', turnId: undefined, deduplicationKey: 'pi:s-1', sessionId: 's-1' }),
    })
    const second = envelope({
      nativeRecordId: undefined,
      importedAt: '2026-09-12T23:00:00.000Z',
      call: call({ provider: 'pi', turnId: undefined, deduplicationKey: 'pi:s-1', sessionId: 's-1' }),
    })
    const [a] = new Synthesizer().synthesizeEnvelopes([first])
    const [b] = new Synthesizer().synthesizeEnvelopes([second])
    expect(a?.spanId).toBe(b?.spanId)
    expect(a?.spanId).toMatch(/^synth:antigravity-cli:s-1:[0-9a-f]{16}$/)
  })
})

// ---------------------------------------------------------------------------
// Task 5 — Child tool.invoke span generation & result truncation (Issue #180)
// ---------------------------------------------------------------------------

type ToolInvokeRecord = CanonicalRecord & {
  attributes?: Record<string, unknown>
}

type TruncatedContentPart = ContentPart & {
  truncated?: boolean
}

describe('Task 5 — child tool.invoke span generation and result truncation', () => {
  it('emits parent llm.invoke record and one child tool.invoke record per tool invocation', () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: 'claude',
      sessionId: 's-t5-1',
      deduplicationKey: 'claude:s-t5-1:t-1',
    })
    const toolCalls: ReaderToolCall[] = [
      { id: 'tu_bash', name: 'Bash', arguments: { command: 'git status' } },
      { id: 'tu_read', name: 'Read', arguments: { path: 'src/synth/synth.ts' } },
    ]
    const toolResults: ReaderToolResult[] = [
      { toolCallId: 'tu_bash', content: 'clean working tree' },
      { toolCallId: 'tu_read', content: 'export class Synthesizer {}' },
    ]
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls,
      toolResults,
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])

    // Should return 3 CanonicalRecords: 1 parent llm.invoke + 2 child tool.invoke
    expect(records).toHaveLength(3)

    const parent = records.find((r) => r.op === 'llm.invoke')
    expect(parent).toBeDefined()
    expect(parent?.parentSpanId).toBeNull()

    const toolRecords = records.filter((r) => r.op === 'tool.invoke') as ToolInvokeRecord[]
    expect(toolRecords).toHaveLength(2)

    // First child: Bash
    const bashRecord = toolRecords.find((r) => r.name === 'Bash')
    expect(bashRecord).toBeDefined()
    expect(bashRecord?.parentSpanId).toBe(parent!.spanId)
    const bashHash = createHash('sha256').update(`tu_bash:Bash:${JSON.stringify({ command: 'git status' })}`).digest('hex').slice(0, 12)
    expect(bashRecord?.spanId).toBe(`${parent!.spanId}-t${bashHash}`)
    expect(bashRecord?.attributes?.['gen_ai.tool.name']).toBe('Bash')
    expect(bashRecord?.attributes?.['gen_ai.tool.call_id']).toBe('tu_bash')
    expect(bashRecord?.attributes?.['gen_ai.tool.status']).toBe('ok')
    expect((bashRecord?.raw as Record<string, unknown>)?.['arguments']).toEqual({ command: 'git status' })
    expect((bashRecord?.raw as Record<string, unknown>)?.['result']).toBe('clean working tree')

    // Second child: Read
    const readRecord = toolRecords.find((r) => r.name === 'Read')
    expect(readRecord).toBeDefined()
    expect(readRecord?.parentSpanId).toBe(parent!.spanId)
    const readHash = createHash('sha256').update(`tu_read:Read:${JSON.stringify({ path: 'src/synth/synth.ts' })}`).digest('hex').slice(0, 12)
    expect(readRecord?.spanId).toBe(`${parent!.spanId}-t${readHash}`)
    expect(readRecord?.attributes?.['gen_ai.tool.name']).toBe('Read')
    expect(readRecord?.attributes?.['gen_ai.tool.call_id']).toBe('tu_read')
    expect(readRecord?.attributes?.['gen_ai.tool.status']).toBe('ok')
    expect((readRecord?.raw as Record<string, unknown>)?.['arguments']).toEqual({ path: 'src/synth/synth.ts' })
    expect((readRecord?.raw as Record<string, unknown>)?.['result']).toBe('export class Synthesizer {}')
  })

  it('emits child tool.invoke records through synthesizeEnvelopes', () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: 'claude',
      sessionId: 's-env-tool',
      deduplicationKey: 'claude:s-env-tool:t-1',
    })
    const env = envelope({
      call: parsedCall,
      readerTurn: {
        parts: [],
        toolCalls: [
          { id: 'tu_ls', name: 'Bash', arguments: { command: 'ls' } },
        ],
        toolResults: [
          { toolCallId: 'tu_ls', content: 'file1\nfile2' },
        ],
      },
    })

    const records = synthesizer.synthesizeEnvelopes([env])
    expect(records).toHaveLength(2)

    const child = records.find((r) => r.op === 'tool.invoke') as ToolInvokeRecord | undefined
    expect(child).toBeDefined()
    expect(child?.name).toBe('Bash')
    expect(child?.attributes?.['gen_ai.tool.name']).toBe('Bash')
    expect(child?.attributes?.['gen_ai.tool.call_id']).toBe('tu_ls')
    expect(child?.attributes?.['gen_ai.tool.status']).toBe('ok')
    expect((child?.raw as Record<string, unknown>)?.['arguments']).toEqual({ command: 'ls' })
    expect((child?.raw as Record<string, unknown>)?.['result']).toBe('file1\nfile2')
  })

  it('truncates large tool results (>64KB) in parts and preserves original byte count in attributes', () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: 'claude',
      sessionId: 's-t5-trunc',
      deduplicationKey: 'claude:s-t5-trunc:t-1',
    })
    const largeContent = 'Z'.repeat(100_000)
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls: [
        { id: 'tu_big', name: 'Bash', arguments: { command: 'cat massive.txt' } },
      ],
      toolResults: [
        { toolCallId: 'tu_big', content: largeContent },
      ],
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])
    const toolRecord = records.find((r) => r.op === 'tool.invoke') as ToolInvokeRecord | undefined

    expect(toolRecord).toBeDefined()
    expect(toolRecord?.attributes?.['gen_ai.tool.result_bytes']).toBe(100_000)

    const part = toolRecord?.parts?.find((p) => p.part === 'tool_result_content') as TruncatedContentPart | undefined
    expect(part).toBeDefined()
    expect(part?.text).toHaveLength(65_536)
    expect(part?.text).toBe('Z'.repeat(65_536))
    expect(part?.truncated).toBe(true)
  })

  it('records gen_ai.tool.status strictly from isError without inferring error from result text', () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: 'claude',
      sessionId: 's-t5-status',
      deduplicationKey: 'claude:s-t5-status:t-1',
    })
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls: [
        { id: 'tu_err', name: 'Bash', arguments: { command: 'false' } },
        { id: 'tu_text_has_error', name: 'Bash', arguments: { command: 'echo "SyntaxError: fake error"' } },
        { id: 'tu_default', name: 'Read', arguments: { path: 'README.md' } },
      ],
      toolResults: [
        { toolCallId: 'tu_err', content: 'Command failed with exit code 1', isError: true },
        { toolCallId: 'tu_text_has_error', content: 'SyntaxError: fake error', isError: false },
        { toolCallId: 'tu_default', content: '# Documentation' },
      ],
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])
    const toolRecords = records.filter((r) => r.op === 'tool.invoke') as ToolInvokeRecord[]

    expect(toolRecords).toHaveLength(3)

    const errSpan = toolRecords.find((r) => r.attributes?.['gen_ai.tool.call_id'] === 'tu_err')
    expect(errSpan?.attributes?.['gen_ai.tool.status']).toBe('error')

    const falseErrSpan = toolRecords.find((r) => r.attributes?.['gen_ai.tool.call_id'] === 'tu_text_has_error')
    expect(falseErrSpan?.attributes?.['gen_ai.tool.status']).toBe('ok')

    const defaultSpan = toolRecords.find((r) => r.attributes?.['gen_ai.tool.call_id'] === 'tu_default')
    expect(defaultSpan?.attributes?.['gen_ai.tool.status']).toBe('ok')
  })

  it('does not invent or inject tool_definitions parts when toolsOffered is undefined', () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: 'claude',
      sessionId: 's-t5-unobs',
      deduplicationKey: 'claude:s-t5-unobs:t-1',
    })
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls: [
        { id: 'tu_1', name: 'Bash', arguments: { command: 'pwd' } },
      ],
      toolResults: [
        { toolCallId: 'tu_1', content: '/workspace' },
      ],
      toolsOffered: undefined,
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])
    const parent = records.find((r) => r.op === 'llm.invoke')
    expect(parent).toBeDefined()
    expect(parent?.parts?.some((p) => p.part === 'tool_definitions')).toBe(false)
    expect(parent?.content.tool_definitions).toBeUndefined()
  })
  it("pairs tool results across split-turn boundaries in a multi-turn session", () => {
    const synthesizer = new Synthesizer()
    const call1 = call({
      provider: "claude",
      sessionId: "s-split-1",
      deduplicationKey: "claude:s-split-1:turn-1",
    })
    const call2 = call({
      provider: "claude",
      sessionId: "s-split-1",
      deduplicationKey: "claude:s-split-1:turn-2",
    })

    // Turn 1 has the tool invocation
    const turn1: ReaderTurn = {
      parts: [],
      toolCalls: [
        { id: "tu_split_1", name: "Bash", arguments: { command: "npm test" } },
      ],
      toolResults: [],
    }

    // Turn 2 has the tool result corresponding to Turn 1's tool invocation
    const turn2: ReaderTurn = {
      parts: [],
      toolCalls: [],
      toolResults: [
        { toolCallId: "tu_split_1", content: "FAIL: 1 test failed", isError: true },
      ],
    }

    const records = synthesizer.synthesize([call1, call2], [turn1, turn2])
    const toolRecord = records.find((r) => r.op === "tool.invoke") as ToolInvokeRecord | undefined

    expect(toolRecord).toBeDefined()
    expect(toolRecord?.name).toBe("Bash")
    // Result content, byte count, and error status must be populated from Turn 2's result
    expect((toolRecord?.raw as Record<string, unknown>)?.["result"]).toBe("FAIL: 1 test failed")
    expect(toolRecord?.attributes?.["gen_ai.tool.result_bytes"]).toBe("FAIL: 1 test failed".length)
    expect(toolRecord?.attributes?.["gen_ai.tool.status"]).toBe("error")
    const resultPart = toolRecord?.parts?.find((p) => p.part === "tool_result_content")
    expect(resultPart?.text).toBe("FAIL: 1 test failed")
  })

  it("merges tool attributes into record.raw so they persist across store round-trip", () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: "claude",
      sessionId: "s-attr-persist",
      deduplicationKey: "claude:s-attr-persist:t-1",
    })
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls: [
        { id: "tu_persist", name: "Bash", arguments: { command: "echo hi" } },
      ],
      toolResults: [
        { toolCallId: "tu_persist", content: "hi\n", isError: false },
      ],
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])
    const toolRecord = records.find((r) => r.op === "tool.invoke")

    expect(toolRecord).toBeDefined()
    const raw = toolRecord?.raw as Record<string, unknown>
    expect(raw).toBeDefined()
    expect(raw["gen_ai.tool.name"]).toBe("Bash")
    expect(raw["gen_ai.tool.call_id"]).toBe("tu_persist")
    expect(raw["gen_ai.tool.status"]).toBe("ok")
    expect(raw["gen_ai.tool.result_bytes"]).toBe(3)
  })
  it("truncates multi-byte UTF-8 tool results at code-point boundary when byte length > 64KB (Thread 1)", () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: "claude",
      sessionId: "s-t5-emoji",
      deduplicationKey: "claude:s-t5-emoji:t-1",
    })
    // 20,000 emoji: 40,000 UTF-16 code units, but 80,000 UTF-8 bytes (> 64KB)
    const emojiContent = "🚀".repeat(20_000)
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls: [
        { id: "tu_emoji", name: "Bash", arguments: { command: "cat emoji.txt" } },
      ],
      toolResults: [
        { toolCallId: "tu_emoji", content: emojiContent },
      ],
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])
    const toolRecord = records.find((r) => r.op === "tool.invoke") as ToolInvokeRecord | undefined

    expect(toolRecord).toBeDefined()
    expect(toolRecord?.attributes?.["gen_ai.tool.result_bytes"]).toBe(80_000)

    const part = toolRecord?.parts?.find((p) => p.part === "tool_result_content") as TruncatedContentPart | undefined
    expect(part).toBeDefined()
    expect(part?.truncated).toBe(true)
    const partBytes = Buffer.byteLength(part!.text, "utf8")
    expect(partBytes).toBeLessThanOrEqual(65_536)
    expect(partBytes).toBe(65_536)
    expect(part?.text).toBe("🚀".repeat(16_384))
  })

  it("bounds raw.result to bounded part text to avoid persisting multi-megabyte payloads (Thread 2)", () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: "claude",
      sessionId: "s-t5-raw-bound",
      deduplicationKey: "claude:s-t5-raw-bound:t-1",
    })
    const largeContent = "X".repeat(200_000)
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls: [
        { id: "tu_huge", name: "Bash", arguments: { command: "cat huge.txt" } },
      ],
      toolResults: [
        { toolCallId: "tu_huge", content: largeContent },
      ],
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])
    const toolRecord = records.find((r) => r.op === "tool.invoke") as ToolInvokeRecord | undefined

    expect(toolRecord).toBeDefined()
    expect(toolRecord?.attributes?.["gen_ai.tool.result_bytes"]).toBe(200_000)
    const raw = toolRecord?.raw as Record<string, unknown>
    expect(raw["result"]).toBe("X".repeat(65_536))
    expect((raw["result"] as string).length).toBe(65_536)
  })

  it("marks tool call with no matched result as unset status (Thread 5)", () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: "claude",
      sessionId: "s-t5-unmatched",
      deduplicationKey: "claude:s-t5-unmatched:t-1",
    })
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls: [
        { id: "tu_unmatched", name: "Bash", arguments: { command: "sleep 100" } },
      ],
      toolResults: [],
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])
    const toolRecord = records.find((r) => r.op === "tool.invoke") as ToolInvokeRecord | undefined

    expect(toolRecord).toBeDefined()
    expect(toolRecord?.status).toBe("unset")
    expect(toolRecord?.attributes?.["gen_ai.tool.status"]).toBe("unset")
    expect((toolRecord?.raw as Record<string, unknown>)?.[ "gen_ai.tool.status"]).toBe("unset")
  })
  it("derives stable child span id from tool identity so regrouped turns retain identical span ids (Thread 8)", () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: "claude",
      sessionId: "s-t8-stable",
      deduplicationKey: "claude:s-t8-stable:t-1",
    })
    const toolA = { id: "tu_a", name: "Bash", arguments: { command: "ls" } }
    const toolB = { id: "tu_b", name: "Read", arguments: { path: "a.txt" } }

    const turn1: ReaderTurn = {
      parts: [],
      toolCalls: [toolA, toolB],
      toolResults: [],
    }
    const turn2: ReaderTurn = {
      parts: [],
      toolCalls: [toolB, toolA],
      toolResults: [],
    }

    const records1 = synthesizer.synthesize([parsedCall], [turn1])
    const records2 = synthesizer.synthesize([parsedCall], [turn2])

    const a1 = records1.find((r) => r.name === "Bash")
    const a2 = records2.find((r) => r.name === "Bash")
    const b1 = records1.find((r) => r.name === "Read")
    const b2 = records2.find((r) => r.name === "Read")

    expect(a1?.spanId).toBe(a2?.spanId)
    expect(b1?.spanId).toBe(b2?.spanId)
    expect(a1?.spanId).not.toBe(b1?.spanId)
  })

  it("bounds raw.arguments to MAX_TOOL_ARGUMENTS_BYTES (64KB) and records gen_ai.tool.arguments_bytes (Thread 9)", () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: "claude",
      sessionId: "s-t9-args-bound",
      deduplicationKey: "claude:s-t9-args-bound:t-1",
    })
    const largeArgs = "Y".repeat(150_000)
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls: [
        { id: "tu_huge_args", name: "Write", arguments: largeArgs },
      ],
      toolResults: [],
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])
    const toolRecord = records.find((r) => r.op === "tool.invoke") as ToolInvokeRecord | undefined

    expect(toolRecord).toBeDefined()
    expect(toolRecord?.attributes?.["gen_ai.tool.arguments_bytes"]).toBe(150_000)
    const raw = toolRecord?.raw as Record<string, unknown>
    const boundArgs = raw["arguments"] as string
    expect(Buffer.byteLength(boundArgs, "utf8")).toBeLessThanOrEqual(65_536)
  })

  it("derives duration from paired toolCall and toolResult timestamps or marks unmeasured (Thread 10)", () => {
    const synthesizer = new Synthesizer()
    const parsedCall = call({
      provider: "claude",
      sessionId: "s-t10-duration",
      deduplicationKey: "claude:s-t10-duration:t-1",
    })
    const readerTurn: ReaderTurn = {
      parts: [],
      toolCalls: [
        {
          id: "tu_timed",
          name: "Bash",
          arguments: { command: "sleep 2" },
          timestamp: "2026-09-01T12:00:00.000Z",
        },
        {
          id: "tu_untimed",
          name: "Read",
          arguments: { path: "file.txt" },
        },
      ],
      toolResults: [
        {
          toolCallId: "tu_timed",
          content: "ok",
          timestamp: "2026-09-01T12:00:02.500Z",
        },
        {
          toolCallId: "tu_untimed",
          content: "file content",
        },
      ],
    }

    const records = synthesizer.synthesize([parsedCall], [readerTurn])
    const timedSpan = records.find((r) => r.name === "Bash")
    const untimedSpan = records.find((r) => r.name === "Read")

    expect(timedSpan?.durationMs).toBe(2500)
    expect(timedSpan?.measurability?.duration).toBe("derived")

    expect(untimedSpan?.durationMs).toBe(0)
    expect(typeof untimedSpan?.measurability?.duration).toBe("object")
    expect((untimedSpan?.measurability?.duration as { availability: string })?.availability).toBe("not_measurable")
  })
});
