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
