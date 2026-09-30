import { describe, expect, it } from 'vitest'

import { buildSessions } from '../canon/sessions.js'
import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import { renormalizeRecords } from './backfill.js'

const RETAINED_TRACE = 'ffffffffffffffffffffffffffffffff'
const NOISE_TRACE = '99999999999999999999999999999999'
const RETAINED_SPAN = 'ffff000000000001'
const NOISE_SPAN = '9999000000000001'

const retainedParts = [
  { part: 'conversation_history' as const, text: 'synthetic retained context', order: 0 },
]

function staleRecord(record: Partial<CanonicalRecord> & Pick<CanonicalRecord, 'spanId' | 'traceId'>): CanonicalRecord {
  return {
    parentSpanId: null,
    source: 'historical-export',
    harness: 'copilot',
    name: 'historical span',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-01-01T00:00:00.000Z',
    durationMs: 10,
    status: 'ok',
    tokens: {
      freshInput: 5,
      cacheRead: 0,
      cacheCreation: 0,
      output: 2,
      reportedInput: 5,
      reportedOutput: 2,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    ...record,
  }
}

describe('renormalizeRecords — retained raw evidence', () => {
  it('reclassifies through ingest, preserving model content while removing noise and pruning its session', async () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([
      staleRecord({
        spanId: RETAINED_SPAN,
        traceId: RETAINED_TRACE,
        name: 'gemini.statusline.request',
        content: { conversation_history: 'synthetic retained context' },
        parts: retainedParts,
        raw: {
          'copilot_chat.turn.id': 'turn-retained',
          'gen_ai.usage.input_tokens': 5,
          'gen_ai.usage.output_tokens': 2,
          'gen_ai.prompt': 'synthetic retained context',
        },
      }),
      staleRecord({
        spanId: NOISE_SPAN,
        traceId: NOISE_TRACE,
        name: 'GlobalHttpApi.health',
        content: { conversation_history: 'synthetic historical noise' },
        raw: { 'gen_ai.system': 'gemini' },
      }),
    ])

    // The derived cache predates the repair, so its noise-only session exists
    // before raw evidence is reclassified.
    await buildSessions(store)
    expect(store.getSessionPayload(NOISE_TRACE)).toBeDefined()

    renormalizeRecords(store)
    const rebuild = await buildSessions(store)

    expect(store.get(RETAINED_SPAN)?.harness).toBe('copilot')
    expect(store.get(RETAINED_SPAN)?.content).toEqual({ conversation_history: 'synthetic retained context' })
    expect(store.get(RETAINED_SPAN)?.parts).toEqual(retainedParts)
    expect(store.get(NOISE_SPAN)).toBeUndefined()
    expect(store.getQuarantine(NOISE_SPAN)).toEqual({
      spanId: NOISE_SPAN,
      namespaces: ['gen_ai'],
      reason: 'non-model span',
    })
    // Noise is gone, and the retained span is now attributed from its Copilot
    // evidence. The copilot-misattributed session built before reclassify is
    // pruned and rebuilt from canonical records.
    expect(rebuild.pruned).toBe(1)
    expect(store.getSessionPayload(NOISE_TRACE)).toBeUndefined()
    expect(store.getSessionPayload(RETAINED_TRACE)).toBeDefined()

    store.close()
  })

  it('removes a legacy excluded Gemini harness identity during renormalization', async () => {
    const store = new CanonStore(':memory:')
    const spanId = 'legacy-gemini-span'
    const traceId = 'legacy-gemini-trace'
    store.upsert(staleRecord({
      spanId,
      traceId,
      harness: 'gemini',
      raw: {
        'gen_ai.system': 'gemini',
        'gen_ai.usage.input_tokens': 5,
        'gen_ai.usage.output_tokens': 2,
        'gen_ai.prompt': 'synthetic excluded-model content',
      },
    }))

    await buildSessions(store)
    renormalizeRecords(store)

    expect(store.get(spanId)).toBeUndefined()
    expect(store.listAll().every((record) => record.harness !== 'gemini')).toBe(true)
    expect(store.getQuarantine(spanId)).toEqual(expect.objectContaining({
      spanId,
      reason: expect.stringMatching(/excluded|unclaimed/i),
    }))
    expect(store.getSessionPayload(traceId)).toBeUndefined()
    store.close()
  })

  it('re-attributes stored agy rows carrying the Antigravity agent identity', async () => {
    // Issue #195: the live corpus shape — labeled `gemini`, raw carrying
    // both `gen_ai.agent.name: 'antigravity'` and `gen_ai.system: 'gemini'`
    // — re-votes to `antigravity` with content preserved, while a genuine
    // agent-name-less Gemini row is still quarantined as excluded_harness.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      staleRecord({
        spanId: 'agy-span-1',
        traceId: 'agy-trace-1',
        source: 'agy',
        harness: 'gemini',
        content: { conversation_history: 'synthetic agy context' },
        raw: {
          'gen_ai.agent.name': 'antigravity',
          'gen_ai.system': 'gemini',
          'gen_ai.usage.input_tokens': 1_200,
          'gen_ai.usage.output_tokens': 150,
          'gen_ai.session.id': 'agy-session-1',
        },
      }),
      staleRecord({
        spanId: 'gemini-span-1',
        traceId: 'gemini-trace-1',
        source: 'gemini-cli',
        harness: 'gemini',
        raw: {
          'gen_ai.system': 'gemini',
          'gen_ai.usage.input_tokens': 5,
          'gen_ai.usage.output_tokens': 2,
        },
      }),
    ])

    await buildSessions(store)
    const report = renormalizeRecords(store)
    const rebuild = await buildSessions(store)

    expect(report.reattributed).toBeGreaterThanOrEqual(1)
    expect(store.get('agy-span-1')?.harness).toBe('antigravity')
    expect(store.get('agy-span-1')?.content).toEqual({
      conversation_history: 'synthetic agy context',
    })
    expect(store.get('gemini-span-1')).toBeUndefined()
    expect(store.getQuarantine('gemini-span-1')).toEqual(expect.objectContaining({
      spanId: 'gemini-span-1',
      // Exact reason: a regression that bypasses Gemini detection must not
      // pass by relabeling the row `unclaimed`.
      reason: 'excluded_harness',
    }))
    expect(store.listAll().every((record) => record.harness !== 'gemini')).toBe(true)
    expect(rebuild.built).toBeGreaterThanOrEqual(1)
    // The re-ingested record names its conversation via `gen_ai.session.id`,
    // so the session keys on the harness conversation, not the trace.
    expect(store.getSessionPayload('agy-session-1')).toBeDefined()
    expect(store.getSessionPayload('gemini-trace-1')).toBeUndefined()
    store.close()
  })

  it('restricts renormalization to the requested sources, leaving other traces untouched', async () => {
    // Issue #195: the live repair must touch only `agy` traces — an
    // unscoped pass would quarantine file-sourced rows (which carry no OTLP
    // fingerprint) as unclaimed.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      staleRecord({
        spanId: 'agy-span-1',
        traceId: 'agy-trace-1',
        source: 'agy',
        harness: 'gemini',
        raw: {
          'gen_ai.agent.name': 'antigravity',
          'gen_ai.system': 'gemini',
          'gen_ai.usage.input_tokens': 1_200,
          'gen_ai.usage.output_tokens': 150,
        },
      }),
      staleRecord({
        spanId: 'file-span-1',
        traceId: 'file-trace-1',
        source: 'codeburn/antigravity-cli',
        harness: 'antigravity-cli',
        raw: { provider: 'antigravity', sessionId: 'file-session-1' },
      }),
    ])

    const report = renormalizeRecords(store, { sources: ['agy'] })

    expect(report.traces).toBe(1)
    expect(store.get('agy-span-1')?.harness).toBe('antigravity')
    expect(store.get('file-span-1')?.harness).toBe('antigravity-cli')
    expect(store.getQuarantine('file-span-1')).toBeUndefined()
    store.close()
  })

  it('leaves excluded-harness rows and sessions from other sources alone on a scoped run', async () => {
    // The `--source` contract: only requested traces are remediated. A
    // genuine gemini row from another source — and its derived session —
    // must survive a scoped run; the tail sweep and session purge are
    // source-aware too.
    const store = new CanonStore(':memory:')
    store.upsertMany([
      staleRecord({
        spanId: 'agy-span-1',
        traceId: 'agy-trace-1',
        source: 'agy',
        harness: 'gemini',
        raw: {
          'gen_ai.agent.name': 'antigravity',
          'gen_ai.system': 'gemini',
          'gen_ai.usage.input_tokens': 1_200,
          'gen_ai.usage.output_tokens': 150,
        },
      }),
      staleRecord({
        spanId: 'other-gemini-span-1',
        traceId: 'other-gemini-trace-1',
        source: 'gemini-cli',
        harness: 'gemini',
        raw: {
          'gen_ai.system': 'gemini',
          'gen_ai.usage.input_tokens': 5,
          'gen_ai.usage.output_tokens': 2,
        },
      }),
    ])
    store.upsertSession({
      sessionId: 'other-gemini-trace-1',
      harness: 'gemini',
      payload: { sessionId: 'other-gemini-trace-1', harness: 'gemini' },
    })

    const report = renormalizeRecords(store, { sources: ['agy'] })

    expect(report.traces).toBe(1)
    expect(store.get('agy-span-1')?.harness).toBe('antigravity')
    expect(store.get('other-gemini-span-1')?.harness).toBe('gemini')
    expect(store.getQuarantine('other-gemini-span-1')).toBeUndefined()
    expect(store.getSessionPayload('other-gemini-trace-1')).toBeDefined()
    store.close()
  })

  it('removes mixed-harness Gemini projected sessions (${harness}:${rawId}) during renormalization', async () => {
    const store = new CanonStore(':memory:')
    const traceId = 'mixed-trace-with-gemini'
    const geminiSpan = 'gemini-span-1'
    const claudeSpan = 'claude-span-1'

    store.upsert(staleRecord({
      spanId: geminiSpan,
      traceId,
      harness: 'gemini',
      raw: {
        'gen_ai.system': 'gemini',
        'gen_ai.usage.input_tokens': 5,
        'gen_ai.usage.output_tokens': 2,
        'gen_ai.prompt': 'gemini turn in mixed trace',
      },
    }))

    store.upsert(staleRecord({
      spanId: claudeSpan,
      traceId,
      harness: 'claude',
      raw: {
        'anthropic.model': 'claude-3-5-sonnet',
        'gen_ai.system': 'anthropic',
      },
    }))

    // Seed historical session projections. In historical stores where mixed traces
    // were projected, session IDs took the form `${harness}:${rawId}`.
    store.upsertSession({
      sessionId: `gemini:${traceId}`,
      harness: 'gemini',
      payload: { sessionId: `gemini:${traceId}`, harness: 'gemini' },
    })
    store.upsertSession({
      sessionId: `claude:${traceId}`,
      harness: 'claude',
      payload: { sessionId: `claude:${traceId}`, harness: 'claude' },
    })
    expect(store.getSessionPayload(`gemini:${traceId}`)).toBeDefined()
    expect(store.getSessionPayload(`claude:${traceId}`)).toBeDefined()

    renormalizeRecords(store)

    // The mixed Gemini projected session MUST be removed during renormalization
    expect(store.getSessionPayload(`gemini:${traceId}`)).toBeUndefined()
    expect(store.getSessionPayload(traceId)).toBeUndefined()

    // And rebuilding projections cleanly re-projects the remaining Claude record without Gemini
    await buildSessions(store)
    expect(store.getSessionPayload(`gemini:${traceId}`)).toBeUndefined()
    expect(store.listSessions().some((s) => s.harness === 'gemini')).toBe(false)
    store.close()
  })
})
