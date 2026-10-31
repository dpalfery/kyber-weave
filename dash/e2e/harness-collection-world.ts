// Shared fixture: harness-collection matrix into a temp canon.db (never ~/.kyberdash/canon.db).
//
// Two kinds of row live here, and only one of them is capture proof:
// - OpenCode 2.2.1 run A (fixtures/v2/runA) is replayed through the production modules: plugin ->
//   OTLP JSON body -> receiver decode -> canonical ingest -> projection. It is the only OpenCode row
//   in this world that proves capture, and no session, run or execution row of it is written by hand.
// - Every other row is a rendering-only seed, written straight into the store. A seed gives the
//   dashboard a shape to render; it says nothing about what a harness captured.

import { mkdtempSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { join } from 'node:path'

import { buildHarnessRollup } from '../src/canon/harnesses.js'
import { ingestBatch } from '../src/canon/ingest.js'
import { projectCanonicalStore } from '../src/canon/projection.js'
import { CanonStore } from '../src/canon/store.js'
import { KYBERDASH_OPENCODE_CONTEXT_ENV } from '../src/capture/harnesses/opencode-plugin/consent.js'
import { createSetup } from '../src/capture/harnesses/opencode-plugin/entry.js'
import {
  OWNED_SESSION_ID,
  consentPathIn,
  createHost,
  receiver,
  replayRunA,
  start,
  stopPlugin,
} from '../src/capture/harnesses/opencode-plugin/testing.js'
import { decodeOtlpJson } from '../src/otel/receiver.js'

export const USER_CANON = join(homedir(), '.kyberdash', 'canon.db')

export const MEASURED_HARNESS = 'claude-code'
// The honest-empty row is a rendering-only seed on the `opencode` harness, the same harness the replay writes.
export const HONEST_EMPTY_HARNESS = 'opencode'
export const ZERO_RECORD_HARNESS = 'droid'
/** The session the production replay writes for OpenCode run A (fixtures/v2/runA). */
export const OPENCODE_REPLAY_SESSION_ID = OWNED_SESSION_ID

export type CollectedWorld = {
  root: string
  dbPath: string
  store: CanonStore
}

export function createTempRoot(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-harness-collection-'))
  if (!root.startsWith(tmpdir())) {
    throw new Error(`harness-collection temp root escaped tmpdir: ${root}`)
  }
  if (root.includes('.kyberdash')) {
    throw new Error(`harness-collection temp root must not sit under .kyberdash: ${root}`)
  }
  return root
}

export async function refreshTempCanon(root = createTempRoot()): Promise<CollectedWorld> {
  const dbPath = join(root, 'canon.db')
  if (dbPath === USER_CANON) {
    throw new Error('refusing to use the user canon.db as a harness-collection write target')
  }

  const store = new CanonStore(dbPath)

  // The fixture owns an open handle: seeding the second harness can throw (a
  // schema or constraint change), and without the close the failure leaks the
  // store and its file lock into the rest of the run.
  try {
    await replayOpencodeRunA(store, root)
    // Projection rebuilds the derived rows from `records` and would prune a seeded row that has no
    // records behind it, so the rendering-only seeds are written after it.
    seedMeasuredClaudeCode(store)
    seedRenderingOnlyOpencode(store)

    buildHarnessRollup(store)
    // Bounded zero-record seed: droid never gains a session or run, but its
    // rollup row must exist so /harness/droid renders honest-empty
    // (No runs recorded) instead of the missing-rollup state.
    buildHarnessRollup(store, ZERO_RECORD_HARNESS)

    return { root, dbPath, store }
  } catch (error) {
    store.close()
    throw error
  }
}

/**
 * Replays OpenCode 2.2.1 run A through the production modules, in capture order. Content consent is
 * on through the env override, so the session carries the consent-on figures. Nothing here writes a
 * session, run or execution row: the projection derives them from the ingested spans.
 */
async function replayOpencodeRunA(store: CanonStore, root: string): Promise<void> {
  const wire = receiver()
  const host = createHost()
  await stage('plugin', async () => {
    const cleanup = await start(
      createSetup({
        fetchFn: wire.fetch,
        env: { [KYBERDASH_OPENCODE_CONTEXT_ENV]: '1' },
        consentPath: consentPathIn(root),
      }),
      host,
    )
    await replayRunA(host)
    await stopPlugin(cleanup)
  })
  if (wire.accepted.length === 0) {
    throw new Error('OpenCode round trip stopped at the plugin stage: no OTLP body was accepted')
  }

  const spans = await stage('receiver decode', () =>
    wire.accepted.flatMap((body) => decodeOtlpJson(JSON.stringify(body))),
  )
  await stage('canonical ingest', () => ingestBatch(spans, store))
  await stage('projection', () => projectCanonicalStore(store))
}

/** Names the stage that failed, so the boot's error says which production module broke first. */
async function stage<T>(name: string, work: () => T | Promise<T>): Promise<T> {
  try {
    return await work()
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    throw new Error(`OpenCode round trip stopped at the ${name} stage: ${reason}`, { cause: error })
  }
}

/** Rendering-only seed: a measured claude-code session, run and execution written straight into the store. */
function seedMeasuredClaudeCode(store: CanonStore): void {
  store.upsertSession({
    sessionId: 'hc-measured-session-1',
    harness: MEASURED_HARNESS,
    label: 'harness-collection measured session',
    started: '2026-09-06T10:00:00.000Z',
    ended: '2026-09-06T10:05:00.000Z',
    payload: {
      id: 'hc-measured-session-1',
      session_id: 'hc-measured-session-1',
      harness: MEASURED_HARNESS,
      context: {
        measurable: true,
        contextLimit: 200000,
        contextLimitSource: 'reported',
        turns: [
          { pressure: 0.42, headroom: 116000 },
          { pressure: 0.35, headroom: 130000 },
        ],
      },
      summary: {
        turn_count: 2,
        total_input: 2000,
        total_output: 500,
        total_cache_read: 800,
        cost: { status: 'priced', value: 0.012, basis: 'published', currency: 'USD' },
      },
    },
  })

  store.upsertRun({
    runId: 'hc-measured-run-1',
    harness: MEASURED_HARNESS,
    label: 'harness-collection measured run',
    groupingBasis: 'explicit',
    started: '2026-09-06T10:00:00.000Z',
    ended: '2026-09-06T10:05:00.000Z',
    executionCount: 1,
  })

  store.upsertExecution({
    executionId: 'hc-measured-exec-1',
    runId: 'hc-measured-run-1',
    sessionId: 'hc-measured-session-1',
    harness: MEASURED_HARNESS,
    isRoot: true,
    started: '2026-09-06T10:00:00.000Z',
    ended: '2026-09-06T10:05:00.000Z',
    parentLinkage: 'measured',
  })
}

/**
 * rendering-only seed — not capture proof. The honest-empty `opencode` row is a visible,
 * unmeasurable session written straight into the store, so the dashboard has an unmeasurable row to
 * render. It says nothing about what OpenCode captured; the replay in `replayOpencodeRunA` does that.
 */
function seedRenderingOnlyOpencode(store: CanonStore): void {
  store.upsertSession({
    sessionId: 'hc-honest-session-1',
    harness: HONEST_EMPTY_HARNESS,
    label: 'rendering-only seed — not capture proof',
    started: '2026-09-06T11:00:00.000Z',
    ended: '2026-09-06T11:02:00.000Z',
    payload: {
      id: 'hc-honest-session-1',
      session_id: 'hc-honest-session-1',
      harness: HONEST_EMPTY_HARNESS,
      context: { measurable: false },
      summary: { turn_count: 1 },
    },
  })

  store.upsertRun({
    runId: 'hc-honest-run-1',
    harness: HONEST_EMPTY_HARNESS,
    label: 'harness-collection honest-empty run',
    groupingBasis: 'explicit',
    started: '2026-09-06T11:00:00.000Z',
    ended: '2026-09-06T11:02:00.000Z',
    executionCount: 1,
  })

  store.upsertExecution({
    executionId: 'hc-honest-exec-1',
    runId: 'hc-honest-run-1',
    sessionId: 'hc-honest-session-1',
    harness: HONEST_EMPTY_HARNESS,
    isRoot: true,
    started: '2026-09-06T11:00:00.000Z',
    ended: '2026-09-06T11:02:00.000Z',
    parentLinkage: 'measured',
  })
}
