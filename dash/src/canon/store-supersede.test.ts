import { describe, expect, it } from 'vitest'

import { CanonStore } from './store.js'
import type { RecordProvenance, SourceCheckpoint } from './source-state.js'
import { type CanonicalRecord, type TokenUsage } from './types.js'

const HARNESS = 'pi'
const SOURCE_KEY = 'session:agent-7f3'
const OTHER_SOURCE_KEY = 'session:other-9'
const T0 = '2026-08-29T11:00:00.000Z'
const T1 = '2026-08-29T12:00:00.000Z'
const IMPORTED = '2026-09-12T00:00:01.000Z'

function usage(): TokenUsage {
  return {
    freshInput: 120,
    cacheRead: 800,
    cacheCreation: 45,
    output: 50,
    reasoning: 12,
    reportedInput: 965,
    reportedOutput: 50,
  }
}

/** An `llm.invoke` row. A hash-backed one carries `raw.provenance.recordDigest`. */
function llmCall(
  spanId: string,
  timestamp: string,
  options: { hashBacked: boolean; sourceKey?: string },
): CanonicalRecord {
  return {
    spanId,
    traceId: `synth:${HARNESS}:agent-7f3`,
    parentSpanId: null,
    source: 'pi:agent-7f3',
    harness: HARNESS,
    sessionId: 'agent-7f3',
    name: 'pi:model',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp,
    durationMs: 0,
    status: 'ok',
    tokens: usage(),
    content: {},
    cost: {
      basis: 'published',
      status: 'priced',
      value: 0.01,
      currency: 'USD',
      byModel: { 'model-1': 0.01 },
    },
    raw: {
      provenance: options.hashBacked
        ? { recordDigest: `digest-${spanId}` }
        : { nativeRecordId: `turn-${spanId}` },
    },
  }
}

/** A tool child of an `llm.invoke` row, linked by `parentSpanId`. */
function toolChild(spanId: string, parentSpanId: string, timestamp: string): CanonicalRecord {
  return {
    spanId,
    traceId: `synth:${HARNESS}:agent-7f3`,
    parentSpanId,
    source: 'pi:agent-7f3',
    harness: HARNESS,
    sessionId: 'agent-7f3',
    name: 'Read',
    op: 'tool.invoke',
    kind: 'internal',
    timestamp,
    durationMs: 0,
    status: 'ok',
    tokens: usage(),
    content: {},
    cost: {
      basis: 'published',
      status: 'priced',
      value: 0,
      currency: 'USD',
      byModel: {},
    },
    raw: {},
  }
}

function provenance(spanId: string, sourceKey: string): RecordProvenance {
  return {
    spanId,
    harnessId: HARNESS,
    sourceKey,
    nativeSessionId: 'agent-7f3',
    nativeRecordId: `turn-${spanId}`,
    sourceRevision: 'inode:1:mtime:100:size:40',
    parserVersion: '1',
    importedAtUtc: IMPORTED,
    locationToken: 'pi-sessions/agent-7f3.jsonl',
  }
}

function checkpoint(sourceKey: string): SourceCheckpoint {
  return {
    harnessId: HARNESS,
    sourceKey,
    providerId: HARNESS,
    parserId: 'pi-jsonl',
    parserContractVersion: '1',
    format: 'jsonl',
    sourceRootLabel: '~/.pi/agent/sessions',
    revisionToken: 'inode:1:mtime:100:size:40',
    coveredFromUtc: '2026-08-29T00:00:00.000Z',
    coveredThroughUtc: '2026-09-12T00:00:00.000Z',
    lastAttemptUtc: '2026-09-12T00:00:00.000Z',
    lastSuccessUtc: '2026-09-12T00:00:01.000Z',
    lastStatus: 'ok',
    lastErrorCode: null,
    unitCount: 1,
    recordCount: 1,
  }
}

/** Commits one source unit: its rows, their provenance, and its checkpoint. */
function commitUnit(
  store: CanonStore,
  sourceKey: string,
  records: readonly CanonicalRecord[],
): void {
  store.commitSourceUnit({
    records,
    provenance: records.map((record) => provenance(record.spanId, sourceKey)),
    checkpoint: checkpoint(sourceKey),
  })
}

describe('commitSourceUnit supersedes hash-backed copies', () => {
  it('deletes an older hash-backed copy at the same instant, with its tool child and provenance', () => {
    const store = new CanonStore(':memory:')

    // The copies a previous refresh left behind, each seeded in its own unit.
    commitUnit(store, SOURCE_KEY, [llmCall('span-hash-t0', T0, { hashBacked: true })])
    commitUnit(store, OTHER_SOURCE_KEY, [llmCall('span-other-t1', T1, { hashBacked: true })])
    commitUnit(store, SOURCE_KEY, [llmCall('span-stable-t1', T1, { hashBacked: false })])
    commitUnit(store, SOURCE_KEY, [
      llmCall('span-old', T1, { hashBacked: true }),
      toolChild('tool-old', 'span-old', T1),
    ])

    // The refresh that re-mints the call at T1 under a new hash id.
    commitUnit(store, SOURCE_KEY, [llmCall('span-new', T1, { hashBacked: true })])

    expect(store.get('span-old'), 'the superseded copy must be deleted').toBeUndefined()
    expect(store.get('tool-old'), 'its tool child must be deleted with it').toBeUndefined()
    expect(store.getRecordProvenance('span-old'), 'its provenance must be deleted').toBeUndefined()
    expect(store.getRecordProvenance('tool-old'), 'the child provenance must be deleted').toBeUndefined()

    expect(store.get('span-new')?.spanId).toBe('span-new')
    expect(store.get('span-stable-t1')?.spanId, 'a stable-id row at T1 is kept').toBe('span-stable-t1')
    expect(store.get('span-hash-t0')?.spanId, 'a hash-backed row at T0 is kept').toBe('span-hash-t0')
    expect(
      store.get('span-other-t1')?.spanId,
      'a hash-backed row at T1 under another source key is kept',
    ).toBe('span-other-t1')
    store.close()
  })
})
