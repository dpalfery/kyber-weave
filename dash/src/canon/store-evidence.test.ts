// Evidence preservation: the v17→v18 migration adds `quarantine.raw_payload` and
// `problems.stack` without backfilling (old rows read as unavailable, never invented),
// and new writes round-trip their exact incoming evidence plus harness scoping.

import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createRequire } from 'node:module'
import { afterAll, describe, expect, it } from 'vitest'

import { CanonStore, SCHEMA_VERSION } from './store.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

const tempRoots: string[] = []

afterAll(() => {
  for (const root of tempRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

function tempStorePath(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-canon-evidence-'))
  tempRoots.push(root)
  return join(root, 'canon.db')
}

describe('store migration to the current schema: incoming-evidence columns', () => {
  it('pins SCHEMA_VERSION at 21 (evidence columns plus harness scoping)', () => {
    expect(SCHEMA_VERSION).toBe(22)
  })

  it('adds the evidence columns to a v17 store without touching existing rows', () => {
    const path = tempStorePath()
    const v17 = new DatabaseSync(path)
    v17.exec(`
      CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
      INSERT INTO metadata (key, value) VALUES ('schema_version', '17');
      CREATE TABLE quarantine (
        span_id TEXT PRIMARY KEY,
        namespaces TEXT NOT NULL,
        reason TEXT NOT NULL,
        source TEXT,
        name TEXT,
        timestamp TEXT
      );
      INSERT INTO quarantine (span_id, namespaces, reason, source, name, timestamp)
        VALUES ('old-span', '["gen_ai"]', 'unclaimed', 'opencode', 'Session.load', '2026-10-10T20:05:54.000Z');
      CREATE TABLE problems (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        span_id TEXT,
        problem_key TEXT NOT NULL DEFAULT '',
        severity TEXT NOT NULL,
        code TEXT NOT NULL,
        message TEXT NOT NULL,
        location TEXT,
        session_id TEXT,
        harness TEXT,
        timestamp TEXT
      );
      INSERT INTO problems (span_id, problem_key, severity, code, message, harness)
        VALUES ('old-span', '["span-problem","old-span","X",null]', 'error', 'X', 'old exact message', 'copilot');
    `)
    v17.close()

    const migrated = new CanonStore(path)
    try {
      expect(migrated.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))

      // Old rows read back with their facts intact and no evidence claimed.
      const oldQ = migrated.getQuarantine('old-span')
      expect(oldQ?.reason).toBe('unclaimed')
      expect(oldQ?.source).toBe('opencode')
      expect(oldQ?.rawPayload).toBeUndefined()
      const oldP = migrated.getProblems('old-span')
      expect(oldP).toHaveLength(1)
      expect(oldP[0]?.message).toBe('old exact message')
      expect(oldP[0]?.stack).toBeUndefined()

      // New writes on the migrated store carry evidence.
      const payload = JSON.stringify({ spanId: 'new-span', name: 'Session.load' })
      migrated.quarantine('new-span', ['session'], 'non-model span', {
        source: 'opencode',
        name: 'Session.load',
        timestamp: '2026-10-10T20:05:54.000Z',
        rawPayload: payload,
      })
      expect(migrated.getQuarantine('new-span')?.rawPayload).toBe(payload)
      migrated.recordProblem({
        spanId: 'new-span',
        severity: 'error',
        code: 'ADAPTER_NORMALIZE_FAILED',
        message: 'boom',
        stack: 'Error: boom\n    at test',
        location: 'new-span',
        harness: 'opencode',
      })
      expect(migrated.getProblems('new-span')[0]?.stack).toBe('Error: boom\n    at test')
    } finally {
      migrated.close()
    }
  })
})

describe('store quarantine/problems harness scoping and evidence', () => {
  function seeded(): CanonStore {
    const store = new CanonStore(':memory:')
    store.quarantine('q-op', ['session'], 'non-model span', { source: 'opencode', name: 'Session.load' })
    store.quarantine('q-co', ['copilot_chat'], 'unclaimed', { source: 'copilot', name: 'chat' })
    store.quarantine('q-cl', ['claude'], 'unclaimed', { source: 'claude-desktop', name: 'chat' })
    store.quarantine('q-un', [], 'unclaimed', { name: 'orphan' })
    store.recordProblem({ spanId: 's1', severity: 'error', code: 'A', message: 'a', harness: 'copilot' })
    store.recordProblem({ spanId: 's2', severity: 'error', code: 'B', message: 'b', harness: 'opencode' })
    store.recordProblem({ spanId: 's3', severity: 'warning', code: 'C', message: 'c' })
    return store
  }

  it('scopes quarantine lists and counts, including twin spellings and unknown', () => {
    const store = seeded()
    try {
      expect(store.listQuarantineFiltered({ limit: 100, offset: 0 }).map((e) => e.spanId).sort()).toEqual([
        'q-cl',
        'q-co',
        'q-op',
        'q-un',
      ])
      expect(store.listQuarantineFiltered({ harness: 'opencode', limit: 100, offset: 0 }).map((e) => e.spanId)).toEqual([
        'q-op',
      ])
      expect(store.listQuarantineFiltered({ harness: 'claude-code', limit: 100, offset: 0 }).map((e) => e.spanId)).toEqual([
        'q-cl',
      ])
      expect(store.listQuarantineFiltered({ harness: 'unknown', limit: 100, offset: 0 }).map((e) => e.spanId)).toEqual([
        'q-un',
      ])
      expect(store.countQuarantineFiltered('copilot')).toBe(1)
      expect(store.countQuarantineFiltered('unknown')).toBe(1)
      expect(store.countQuarantineFiltered()).toBe(4)
    } finally {
      store.close()
    }
  })

  it('scopes problem lists and counts, and reads one problem by id', () => {
    const store = seeded()
    try {
      expect(store.listProblemsFiltered({ harness: 'copilot', limit: 100, offset: 0 }).map((p) => p.code)).toEqual(['A'])
      expect(store.listProblemsFiltered({ harness: 'unknown', limit: 100, offset: 0 }).map((p) => p.code)).toEqual(['C'])
      expect(store.countProblemsFiltered('opencode')).toBe(1)
      expect(store.countProblemsFiltered()).toBe(3)
      const [first] = store.listProblemsFiltered({ limit: 1, offset: 0 })
      expect(store.getProblem(first!.id)?.code).toBe(first!.code)
      expect(store.getProblem(999_999_999)).toBeUndefined()
    } finally {
      store.close()
    }
  })
})
