import { afterAll, describe, expect, it } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'

import { CanonStore, SCHEMA_VERSION, compressRaw } from './store.js'
import { TOKEN_SUM_MISMATCH, type CanonicalRecord, type TokenUsage } from './types.js'

// The measured floor the store exists to break (R12.4): 2.9 GB across 37,623
// spans is roughly 78 KB of raw payload per span, uncompressed. A record whose
// raw column costs even 80% of that has not been bounded in any meaningful
// sense, so the budget is asserted with margin.
const MEASURED_BYTES_PER_SPAN = 78 * 1024
const RAW_BUDGET_BYTES = MEASURED_BYTES_PER_SPAN * 0.8

const tempRoots: string[] = []

function tempStorePath(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-canon-store-'))
  tempRoots.push(root)
  return join(root, 'canon.db')
}

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

function usage(overrides: Partial<TokenUsage> = {}): TokenUsage {
  return {
    freshInput: 120,
    cacheRead: 800,
    cacheCreation: 45,
    output: 50,
    reasoning: 12,
    reportedInput: 965,
    reportedOutput: 50,
    ...overrides,
  }
}

function record(overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: 'span-1',
    traceId: 'trace-1',
    parentSpanId: 'span-0',
    source: 'pi:agent-7f3',
    harness: 'pi',
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-08-29T12:00:00.000Z',
    durationMs: 1250,
    status: 'ok',
    tokens: usage(),
    content: { system_prompt: 'You are a coding agent.' },
    cost: {
      basis: 'published',
      status: 'priced',
      value: 0.01234,
      currency: 'USD',
      byModel: { 'claude-sonnet-4-5': 0.01234 },
    },
    measurability: { input_tokens: 'measured', reasoning_tokens: 'not_measurable' },
    ...overrides,
  }
}

/// Deterministic OTLP-shaped payload grown to roughly `targetBytes` of JSON.
/// The repetition — attribute keys, prompt scaffolding — is exactly what real
/// telemetry exhibits, and exactly what deflate collapses.
function telemetryPayload(targetBytes: number, prompt: string): Record<string, unknown> {
  const spans: Record<string, unknown>[] = []
  const payload = {
    resourceSpans: [
      {
        resource: { attributes: [{ key: 'service.name', value: { stringValue: 'codeburn' } }] },
        scopeSpans: [{ spans }],
      },
    ],
  }
  while (JSON.stringify(payload).length < targetBytes) {
    spans.push({
      traceId: '0af7651916cd43dd8448eb211c80319c',
      name: 'gen_ai.client.chat',
      kind: 'INTERNAL',
      startTimeUnixNano: '1756478400000000000',
      attributes: [
        { key: 'gen_ai.request.model', value: { stringValue: 'claude-sonnet-4-5' } },
        { key: 'gen_ai.usage.input_tokens', value: { intValue: '120' } },
        { key: 'gen_ai.usage.output_tokens', value: { intValue: '50' } },
        { key: 'gen_ai.prompt', value: { stringValue: prompt } },
      ],
    })
  }
  return payload
}

/// Conversation-history-shaped payload: fewer attributes, long recurring
/// turns. A different mix of the same bytes, so the budget is not passed on
/// the strength of one favorable shape alone.
function conversationPayload(targetBytes: number): Record<string, unknown> {
  const turns: string[] = []
  const payload = {
    session: { harness: 'pi', source: 'pi:agent-7f3' },
    conversation_history: turns,
  }
  const turn = [
    'user: Read the failing test and make it pass without weakening the assertion.',
    'assistant: I located the regression in the store upsert path and added the missing',
    'transaction boundary so a burst commits as one unit.',
  ].join(' ')
  while (JSON.stringify(payload).length < targetBytes) turns.push(turn)
  return payload
}

describe('CanonStore schema and metadata', () => {
  it('builds the empty store on first use and records the schema version', () => {
    const store = new CanonStore(':memory:')
    expect(store.count()).toBe(0)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(store.getMetadata('nothing-recorded-under-this-key')).toBeUndefined()
  })

  it('persists across reopen: the database file is the store', () => {
    const path = tempStorePath()
    const first = new CanonStore(path)
    first.upsert(record())
    first.close()

    const reopened = new CanonStore(path)
    expect(reopened.count()).toBe(1)
    expect(reopened.get('span-1')).toEqual(record())
    reopened.close()
  })

  it('refuses to open a store built under a different schema version', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)
    store.close()

    // Simulate a foreign build having written the store: the version row no
    // longer matches what this build understands, and that must be detectable
    // rather than misread (design.md, "Data Models").
    const foreign = new DatabaseSync(path)
    foreign.prepare("UPDATE metadata SET value = '999' WHERE key = 'schema_version'").run()
    foreign.close()

    expect(() => new CanonStore(path)).toThrow(/schema version/)
  })
})

describe('CanonStore round trip', () => {
  it('returns the stored record whole: tokens, content, cost, measurability, raw', () => {
    const store = new CanonStore(':memory:')
    const raw = telemetryPayload(5 * 1024, 'Find the bug and fix it with tests.')
    const stored = record({ raw })

    store.upsert(stored)
    expect(store.get('span-1')).toEqual(stored)
  })

  it('round-trips a record with no raw payload and no measurability', () => {
    const store = new CanonStore(':memory:')
    const stored = record({ raw: undefined, measurability: undefined, traceId: null, parentSpanId: null })

    store.upsert(stored)
    expect(store.get('span-1')).toEqual(stored)
  })

  it('normalizes a Date timestamp to its ISO string', () => {
    const store = new CanonStore(':memory:')
    const at = new Date('2026-08-29T12:00:00.000Z')
    store.upsert(record({ timestamp: at }))

    const fetched = store.get('span-1')
    expect(fetched?.timestamp).toBe(at.toISOString())
  })

  it('round-trips raw payloads that are JSON-hostile on the surface: numbers, null, nested arrays', () => {
    const store = new CanonStore(':memory:')
    const raw = { count: 7, nested: [null, true, { deep: ['a', 0.5] }] }
    store.upsert(record({ raw }))

    expect(store.get('span-1')?.raw).toEqual(raw)
  })
})

describe('CanonStore raw compression (R12.4)', () => {
  it('stores a ~5 KB raw payload in fewer bytes than its JSON', () => {
    const store = new CanonStore(':memory:')
    const raw = telemetryPayload(5 * 1024, 'Find the bug and fix it with tests.')
    const uncompressed = Buffer.byteLength(JSON.stringify(raw))
    expect(uncompressed).toBeGreaterThanOrEqual(5 * 1024)

    store.upsert(record({ raw }))
    const stored = store.storedRawBytes('span-1')
    expect(stored).not.toBeNull()
    expect(stored as number).toBeLessThan(5 * 1024)
    expect(stored as number).toBeLessThan(uncompressed)
  })

  it.each([
    {
      shape: 'OTLP-style attributes repeated across spans',
      raw: telemetryPayload(MEASURED_BYTES_PER_SPAN, 'Find the bug and fix it with tests.'),
    },
    {
      shape: 'conversation history with recurring turns',
      raw: conversationPayload(MEASURED_BYTES_PER_SPAN),
    },
  ])('bounds stored bytes per record under the budget: $shape', ({ raw }) => {
    const store = new CanonStore(':memory:')
    const uncompressed = Buffer.byteLength(JSON.stringify(raw))
    expect(uncompressed).toBeGreaterThanOrEqual(MEASURED_BYTES_PER_SPAN)

    store.upsert(record({ spanId: 'span-budget', raw }))
    const stored = store.storedRawBytes('span-budget')
    expect(stored).not.toBeNull()
    // The measured corpus paid ~78 KB per span for exactly this payload; the
    // store must keep meaningfully less than that (R12.4).
    expect(stored as number).toBeLessThan(RAW_BUDGET_BYTES)
    expect(stored as number).toBeLessThan(uncompressed)
  })

  it('compressRaw is content-addressable by value: identical payloads, identical bytes', () => {
    const raw = telemetryPayload(2 * 1024, 'Deterministic prompt.')
    expect(Buffer.from(compressRaw(raw))).toEqual(Buffer.from(compressRaw(raw)))
  })
})

describe('CanonStore idempotency (R2.5)', () => {
  const corpus: CanonicalRecord[] = [
    record({ spanId: 'span-a', traceId: null, parentSpanId: null }),
    record({ spanId: 'span-b', parentSpanId: 'span-a', raw: telemetryPayload(2048, 'Turn one.') }),
    record({ spanId: 'span-c', parentSpanId: 'span-a', raw: telemetryPayload(4096, 'Turn two.') }),
    record({ spanId: 'span-d', parentSpanId: 'span-c', cost: { basis: 'unknown', status: 'no_rate' } }),
    record({ spanId: 'span-e', parentSpanId: 'span-b', measurability: undefined }),
  ]

  it('ingesting the same corpus twice changes no stored record', () => {
    const store = new CanonStore(':memory:')
    store.upsertMany(corpus)
    const afterFirst = corpus.map((r) => store.get(r.spanId))
    const bytesAfterFirst = corpus.map((r) => store.storedRawBytes(r.spanId))

    store.upsertMany(corpus)
    expect(store.count()).toBe(corpus.length)
    for (const [index, r] of corpus.entries()) {
      expect(store.get(r.spanId)).toEqual(afterFirst[index])
      expect(store.get(r.spanId)).toEqual(r)
      expect(store.storedRawBytes(r.spanId)).toBe(bytesAfterFirst[index])
    }
  })

  it('re-upserting one span replaces its row rather than duplicating it', () => {
    const store = new CanonStore(':memory:')
    store.upsert(record({ spanId: 'span-a', status: 'ok' }))
    store.upsert(record({ spanId: 'span-a', status: 'error' }))

    expect(store.count()).toBe(1)
    expect(store.get('span-a')?.status).toBe('error')
  })
})

describe('CanonStore batch ingest (R2.5)', () => {
  it('commits a burst as one batch and drops nothing', () => {
    const store = new CanonStore(':memory:')
    const burst: CanonicalRecord[] = Array.from({ length: 250 }, (_, i) =>
      record({
        spanId: `span-burst-${i}`,
        parentSpanId: i === 0 ? null : `span-burst-${i - 1}`,
        raw: telemetryPayload(1024, `Burst turn ${i % 7}.`),
      }),
    )

    store.upsertMany(burst)
    expect(store.count()).toBe(burst.length)
    // Spot-check across the whole batch: "never dropped" means the end of the
    // burst is as present as the beginning.
    for (const i of [0, 1, 49, 125, 200, 249]) {
      expect(store.get(`span-burst-${i}`)).toEqual(burst[i])
    }
  })

  it('treats an empty batch as a no-op', () => {
    const store = new CanonStore(':memory:')
    store.upsertMany([])
    expect(store.count()).toBe(0)
  })
})

describe('CanonStore quarantine, problems, and ingest log', () => {
  it('holds a quarantined span with its namespaces and reason', () => {
    const store = new CanonStore(':memory:')
    store.quarantine('span-1', ['pi', 'claude-code'], 'unresolved parent span')

    expect(store.getQuarantine('span-1')).toEqual({
      spanId: 'span-1',
      namespaces: ['pi', 'claude-code'],
      reason: 'unresolved parent span',
    })
    expect(store.getQuarantine('span-absent')).toBeUndefined()
  })

  it('re-quarantining a span replaces the entry', () => {
    const store = new CanonStore(':memory:')
    store.quarantine('span-1', ['pi'], 'unresolved parent span')
    store.quarantine('span-1', ['copilot'], 'token identity does not reconcile')

    const entry = store.getQuarantine('span-1')
    expect(entry?.namespaces).toEqual(['copilot'])
    expect(entry?.reason).toBe('token identity does not reconcile')
  })

  it('records problems and narrows them by span', () => {
    const store = new CanonStore(':memory:')
    store.recordProblem({
      spanId: 'span-1',
      severity: 'error',
      code: TOKEN_SUM_MISMATCH,
      message: 'fresh_input + cache_read + cache_creation (150) does not reconcile with reported_input (151)',
    })
    store.recordProblem({
      spanId: 'span-2',
      severity: 'warning',
      code: 'PARENT_UNRESOLVED',
      message: 'parent span id names no stored record',
      location: 'span-2',
    })

    expect(store.getProblems()).toHaveLength(2)
    expect(store.getProblems('span-1')).toEqual([
      {
        spanId: 'span-1',
        severity: 'error',
        code: TOKEN_SUM_MISMATCH,
        message: expect.stringContaining('reported_input'),
        location: undefined,
      },
    ])
    expect(store.getProblems('span-2')[0]?.location).toBe('span-2')
    expect(store.getProblems('span-absent')).toEqual([])
  })

  it('appends one ingest log row per ingest run', () => {
    const store = new CanonStore(':memory:')
    store.logIngest('pi:agent-7f3', 30)
    store.logIngest('pi:agent-7f3', 30)

    const log = store.getIngestLog()
    expect(log).toHaveLength(2)
    for (const entry of log) {
      expect(entry.source).toBe('pi:agent-7f3')
      expect(entry.count).toBe(30)
      expect(entry.timestamp).toMatch(/^\d{4}-\d{2}-\d{2}T/)
    }
  })
})
