// Shared fixture: harness-collection matrix into a temp canon.db (never ~/.kyberdash/canon.db).

import { mkdtempSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { join } from 'node:path'

import { buildHarnessRollup } from '../src/canon/harnesses.js'
import { CanonStore } from '../src/canon/store.js'

export const USER_CANON = join(homedir(), '.kyberdash', 'canon.db')

export const MEASURED_HARNESS = 'claude-code'
// Plugin row: visible not_measurable session (not the native Effect quarantine/no-session row). Direct store upsert exercises rendering only, bypassing the adapter vote.
export const HONEST_EMPTY_HARNESS = 'opencode'
export const ZERO_RECORD_HARNESS = 'droid'

// C1 measured-opencode round trip: same harness id as the honest-empty plugin
// row (`opencode`), second session/run pair carrying model identity, token
// usage and a tool invocation through the T8 direct-upsert projection seam.
// The honest-empty session stays byte-identical; the harness now holds both,
// so HarnessDetail still reports unmeasurable dimensions (skill/continuity)
// while the measured run drills into model/tokens/tools/cost.
export const MEASURED_OPENCODE_SESSION_ID = 'hc-opencode-measured-session-1'
export const MEASURED_OPENCODE_RUN_ID = 'hc-opencode-measured-run-1'
export const MEASURED_OPENCODE_EXEC_ID = 'hc-opencode-measured-exec-1'
export const MEASURED_OPENCODE_MODEL = 'opencode-go/muse-spark-1.3-contributor'

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

    store.upsertSession({
      sessionId: 'hc-honest-session-1',
      harness: HONEST_EMPTY_HARNESS,
      label: 'harness-collection honest-empty session',
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

    // C1 measured-opencode: adapter-shaped session (model identity + token
    // usage + tool invocation) projected through the same direct-upsert seam.
    // Tokens mirror the A2 current-shape opencode numbers (18044 in / 71 out /
    // 113 cache-read) so the figures are real measured counters, never zeros.
    // Cost carries an explicit published basis with a real value so the
    // dashboard resolves a basis instead of "no published rate".
    store.upsertSession({
      sessionId: MEASURED_OPENCODE_SESSION_ID,
      harness: HONEST_EMPTY_HARNESS,
      label: 'harness-collection measured opencode session',
      started: '2026-09-06T12:00:00.000Z',
      ended: '2026-09-06T12:05:00.000Z',
      payload: {
        id: MEASURED_OPENCODE_SESSION_ID,
        session_id: MEASURED_OPENCODE_SESSION_ID,
        harness: HONEST_EMPTY_HARNESS,
        context: {
          measurable: true,
          contextLimit: 200000,
          contextLimitSource: 'reported',
          turns: [{ pressure: 0.38, headroom: 124000 }],
        },
        summary: {
          turn_count: 1,
          total_input: 18044,
          total_output: 71,
          total_cache_read: 113,
          total_cache_creation: 0,
          models: [MEASURED_OPENCODE_MODEL],
          cost: { status: 'priced', value: 0.012, basis: 'published', currency: 'USD' },
          tool_calls: 1,
          tools_invoked: ['Bash'],
        },
        turns: [
          {
            index: 0,
            spanId: 'hc-opencode-measured-turn-0',
            timestamp: '2026-09-06T12:05:00.000Z',
            model: MEASURED_OPENCODE_MODEL,
            input: 18044,
            output: 71,
            cache_read: 113,
            cache_creation: 0,
          },
        ],
        tools: [{ name: 'Bash', schema_tokens: 0, invocations: 1, turns_resident: 1, errors: 0 }],
      },
    })

    store.upsertRun({
      runId: MEASURED_OPENCODE_RUN_ID,
      harness: HONEST_EMPTY_HARNESS,
      label: 'harness-collection measured opencode run',
      groupingBasis: 'explicit',
      started: '2026-09-06T12:00:00.000Z',
      ended: '2026-09-06T12:05:00.000Z',
      executionCount: 1,
    })

    store.upsertExecution({
      executionId: MEASURED_OPENCODE_EXEC_ID,
      runId: MEASURED_OPENCODE_RUN_ID,
      sessionId: MEASURED_OPENCODE_SESSION_ID,
      harness: HONEST_EMPTY_HARNESS,
      isRoot: true,
      started: '2026-09-06T12:00:00.000Z',
      ended: '2026-09-06T12:05:00.000Z',
      parentLinkage: 'measured',
    })

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
