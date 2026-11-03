// The projection gate must notice a schema change.
//
// WHY: the gate's four inputs -- the records-write marker, the detector
// version, the parser contracts and the pricing fingerprint -- all describe
// the CORPUS and the RULES. None of them describes the store's SHAPE. So a
// schema bump changed none of them, and the first maintenance tick after a
// store migrated saw "nothing changed" and skipped the projection -- leaving
// a corpus the new schema had just reshaped sitting on whatever the old
// shape left behind, indefinitely.
//
// That is not hypothetical: the v22 migration added the session turn table
// and the context-limit column, and the projection that was supposed to
// re-derive the corpus after it never ran, because the gate said the corpus
// had not moved.
//
// The schema version is the fifth input: the version the store carries now,
// against the version the last projection saw. A difference means the store
// was reshaped since, so the derived tables describe a shape that no longer
// exists and must be rebuilt.
//
// The counterpart is pinned too: a gate that always reported "stale" would
// rebuild the corpus on every maintenance tick, which is the cost the gate
// exists to avoid.

import { afterEach, describe, expect, it, vi } from 'vitest'

import { runMaintenancePass } from '../refresh/folder-import.js'
import { projectCanonicalStore, projectionIsCurrent } from './projection.js'
import { CanonStore } from './store.js'
import type { CanonicalRecord, TokenUsage } from './types.js'

/** The metadata key the schema version is recorded under. */
const PROJECTION_SCHEMA_VERSION_KEY = 'projection_schema_version'

/** The four inputs the gate already had, each under its own metadata key. */
const PROJECTION_RECORDS_MARKER_KEY = 'projection_records_marker'
const PROJECTION_PARSER_CONTRACTS_KEY = 'projection_parser_contracts'
const PROJECTION_PRICING_KEY = 'projection_pricing'

function usage(): TokenUsage {
  return {
    freshInput: 10,
    cacheRead: 0,
    cacheCreation: 0,
    output: 4,
    reportedInput: 10,
    reportedOutput: 4,
  }
}

function record(spanId: string): CanonicalRecord {
  return {
    spanId,
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    source: 'pi:agent-7f3',
    harness: 'pi',
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-10-20T00:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: usage(),
    content: { system_prompt: 'content' },
    cost: { basis: 'published', status: 'priced', value: 0.01, currency: 'USD' },
    raw: { attributes: { keep: spanId } },
  }
}

const stores: CanonStore[] = []

function seedStore(spanIds: readonly string[]): CanonStore {
  const store = new CanonStore(':memory:')
  stores.push(store)
  store.upsertMany(spanIds.map((spanId) => record(spanId)))
  return store
}

afterEach(() => {
  vi.restoreAllMocks()
  for (const store of stores.splice(0)) store.close()
})

describe('the projection gate watches the store schema version', () => {
  it('reports the projection current when nothing moved, schema version included', async () => {
    // The skip behaviour is the gate's point: a maintenance tick that rebuilt
    // the corpus every time would cost ~26 s of CPU per tick over a real
    // corpus. The schema version is a fifth input, not a replacement for the
    // four: with them all unmoved, the gate still says "current".
    const store = seedStore(['schema-current'])

    await projectCanonicalStore(store)

    expect(projectionIsCurrent(store)).toBe(true)
  })

  it('reports the projection stale when the store schema version moved', async () => {
    // The corpus did not move, the detector version did not move, the parser
    // contracts did not move, the pricing table did not move -- the store was
    // reshaped. The derived tables now describe a shape that no longer
    // exists, so the gate must report them stale.
    const store = seedStore(['schema-moved'])

    await projectCanonicalStore(store)
    expect(projectionIsCurrent(store)).toBe(true)

    // The store carries a different schema version than the last projection
    // saw -- the shape moved under the derived tables.
    store.setMetadata('schema_version', '23')

    expect(projectionIsCurrent(store)).toBe(false)
  })

  it('reports the projection stale for a store stamped with the previous detector generation', async () => {
    // T6 (measurability) and T8 (observed parts) changed derived output, so a
    // store projected by generation 4 must re-project (D13).
    const store = seedStore(['detector-4'])

    await projectCanonicalStore(store)
    expect(projectionIsCurrent(store)).toBe(true)

    store.setDetectorVersion(4)

    expect(projectionIsCurrent(store)).toBe(false)
  })

  it('reports the projection stale when the recorded schema version lags the store', async () => {
    // The same comparison from the other side: the state the last projection
    // recorded names an older schema than the store carries. This is the
    // deployment shape -- the corpus was projected by the previous build, the
    // store then migrated, and nothing about the corpus itself moved.
    const store = seedStore(['schema-lagged'])

    await projectCanonicalStore(store)
    expect(projectionIsCurrent(store)).toBe(true)

    store.setMetadata(PROJECTION_SCHEMA_VERSION_KEY, '21')

    expect(projectionIsCurrent(store)).toBe(false)
  })

  it('reports the projection stale for a store whose projection predates the schema input', async () => {
    // A store projected by a build that had no schema-version input recorded
    // nothing: the key is absent, and absent means "unknown", which must
    // read as stale rather than as current.
    const store = seedStore(['schema-absent'])

    await projectCanonicalStore(store)
    expect(projectionIsCurrent(store)).toBe(true)

    store.getDatabase().prepare('DELETE FROM metadata WHERE key = ?').run(PROJECTION_SCHEMA_VERSION_KEY)

    expect(projectionIsCurrent(store)).toBe(false)
  })

  it('records the schema version alongside the four inputs it already had', async () => {
    // The recorded state grows a fifth entry; the four it already had stay
    // exactly where they were, with the values the gate compares against.
    const store = seedStore(['schema-recorded'])

    await projectCanonicalStore(store)

    expect(store.getMetadata(PROJECTION_RECORDS_MARKER_KEY)).toBe(String(store.recordsWriteMarker()))
    expect(store.getMetadata(PROJECTION_PARSER_CONTRACTS_KEY)).toBeDefined()
    expect(store.getMetadata(PROJECTION_PRICING_KEY)).toBeDefined()
    expect(store.getMetadata(PROJECTION_SCHEMA_VERSION_KEY)).toBe(store.getMetadata('schema_version'))
  })

  it('re-projects after the store schema moved, and skips again once it did', async () => {
    // The symptom, end to end: the maintenance pass skipped the projection
    // after a schema change, and the corpus stayed as the old shape left it.
    const store = seedStore(['schema-maintenance'])
    const upserts = vi.spyOn(CanonStore.prototype, 'upsertSession')

    await runMaintenancePass(store, new Date('2026-10-20T00:00:00.000Z'))
    expect(upserts.mock.calls.length).toBeGreaterThan(0)

    // Nothing moved: the pass does no projection work.
    upserts.mockClear()
    await runMaintenancePass(store, new Date('2026-10-20T01:00:00.000Z'))
    expect(upserts).not.toHaveBeenCalled()

    // The store was reshaped: the pass projects again.
    store.setMetadata('schema_version', '23')
    upserts.mockClear()
    await runMaintenancePass(store, new Date('2026-10-20T02:00:00.000Z'))
    expect(upserts.mock.calls.length).toBeGreaterThan(0)

    // And the projection recorded the version it saw, so the next tick skips.
    upserts.mockClear()
    await runMaintenancePass(store, new Date('2026-10-20T03:00:00.000Z'))
    expect(upserts).not.toHaveBeenCalled()
  })
})
