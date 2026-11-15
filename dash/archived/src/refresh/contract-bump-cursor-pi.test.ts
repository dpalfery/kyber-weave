// T10 — parser contract bumps for Cursor and Pi (ADR 0016 repair contract).
// T5 taught the Cursor parser to pair token bubbles via pairingId and T7 gave
// the Pi parser a declared context window from models-store.json. Installs
// other than the owner's already hold source checkpoints written under
// contract '1'; `dash refresh` must treat those checkpoints as stale and
// re-synthesize the units. The owner resets per D1, so the bump in
// parserContractVersionFor — not a migration — is the repair.
// Fixture-backed and synthetic: temp canon.db, invented sessions, never
// ~/.kyberdash/canon.db.

import { mkdirSync, mkdtempSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import type { SessionSource } from '../providers/types.js'
import { Synthesizer } from '../synth/synth.js'
import {
  COMMAND_STARTED_AT,
  fixtureProvider,
  jsonlLoader,
  parsedCall,
  sqliteLoader,
  writeCursorVirtualDb,
  type FixtureTurn,
} from './fixtures/integration-harness.js'
import { refreshHarnessSources } from './orchestrator.js'
import { descriptorFor, parserContractVersionFor, sourceKeyFor } from './registry.js'
import { revisionTokenFor } from './source-reader.js'

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-contract-bump-t10-'))
  temporaryRoots.push(dir)
  return dir
}

const CURSOR_TURNS: FixtureTurn[] = [
  {
    sessionId: 'session-cursor-contract-bump',
    turnId: 'turn-cursor-1',
    timestamp: '2026-09-06T12:00:00.000Z',
    model: 'claude-sonnet-4-5',
    inputTokens: 40,
    outputTokens: 12,
  },
]

const PI_TURNS: FixtureTurn[] = [
  {
    sessionId: 'session-pi-contract-bump',
    turnId: 'turn-pi-1',
    timestamp: '2026-09-06T13:00:00.000Z',
    model: 'claude-sonnet-4-5',
    inputTokens: 21,
    outputTokens: 6,
  },
]

// Pre-seed the store the way an install that predates the bump looks: one
// synthesized unit plus a checkpoint stamped with the legacy contract '1' and
// the revision token the unchanged source still fingerprints to. Only the
// parser contract differs after the bump, so the refresh must invalidate on
// the version alone.
function seedStaleUnit(
  store: CanonStore,
  harnessId: string,
  source: SessionSource,
  turns: readonly FixtureTurn[],
): { sourceKey: string; seeded: CanonicalRecord } {
  const descriptor = descriptorFor(harnessId)
  if (descriptor === undefined) throw new Error(`missing descriptor ${harnessId}`)
  const stat = statSync(source.path)
  const revisionToken = revisionTokenFor({
    dev: stat.dev,
    ino: stat.ino,
    mtimeMs: stat.mtimeMs,
    sizeBytes: stat.size,
  })
  const sourceKey = sourceKeyFor(harnessId, source)
  const [call] = turns.map((turn) => parsedCall(turn, source.provider))
  expect(call).toBeDefined()
  const [seeded] = new Synthesizer().synthesizeEnvelopes([
    {
      harnessId,
      sourceKey,
      nativeSessionId: call!.sessionId,
      nativeRecordId: call!.turnId,
      call: call!,
      sourceRevision: revisionToken,
    },
  ])
  expect(seeded).toBeDefined()

  store.commitSourceUnit({
    records: [seeded!],
    provenance: [
      {
        spanId: seeded!.spanId,
        harnessId,
        sourceKey,
        nativeSessionId: seeded!.sessionId ?? null,
        nativeRecordId: call!.turnId ?? null,
        sourceRevision: revisionToken,
        parserVersion: '1',
        importedAtUtc: '2026-09-06T14:00:00.000Z',
        locationToken: descriptor.sourceRootLabel,
      },
    ],
    checkpoint: {
      harnessId,
      sourceKey,
      providerId: descriptor.providerName,
      parserId: descriptor.providerName,
      parserContractVersion: '1',
      format: descriptor.nativeFormat,
      sourceRootLabel: descriptor.sourceRootLabel,
      revisionToken,
      coveredFromUtc: '2026-08-01T00:00:00.000Z',
      coveredThroughUtc: '2026-09-20T00:00:00.000Z',
      lastAttemptUtc: '2026-09-06T14:00:00.000Z',
      lastSuccessUtc: '2026-09-06T14:00:00.000Z',
      lastStatus: 'ok',
      lastErrorCode: null,
      unitCount: 1,
      recordCount: 1,
    },
  })
  return { sourceKey, seeded: seeded! }
}

async function refreshHarness(
  store: CanonStore,
  harnessId: string,
  source: SessionSource,
): Promise<ReturnType<typeof refreshHarnessSources>> {
  const descriptor = descriptorFor(harnessId)
  expect(descriptor).toBeDefined()
  return refreshHarnessSources(store, {
    getAllProviders: async () => [fixtureProvider(source.provider, [source], (unit) => {
      const loader = unit.path.endsWith('.vscdb') ? sqliteLoader : jsonlLoader
      return loader(unit)
    })],
    descriptors: [descriptor!],
    jobConcurrency: 1,
    writerCapacity: 1,
    commandStartedAt: COMMAND_STARTED_AT,
    parseAllSessions: async () => undefined,
  })
}

describe('parser contract bumps re-read stale Cursor/Pi checkpoints (ADR 0016 repair)', () => {
  it('re-synthesizes a cursor unit whose checkpoint was written under contract 1', async () => {
    const root = tempDir()
    const store = new CanonStore(join(root, 'canon.db'))
    try {
      const dbPath = writeCursorVirtualDb(join(root, 'cursor', 'state.vscdb'), CURSOR_TURNS)
      const source: SessionSource = { path: dbPath, provider: 'cursor', project: 'contract-bump' }
      const { sourceKey, seeded } = seedStaleUnit(store, 'cursor', source, CURSOR_TURNS)

      // The bump itself: after T5 changed what the Cursor parser extracts
      // (pairingId token-bubble pairing), the contract may not stay at '1'.
      expect(parserContractVersionFor({ providerName: 'cursor', harnessId: 'cursor' })).not.toBe('1')
      const descriptor = descriptorFor('cursor')
      expect(descriptor?.parserContractVersion).not.toBe('1')

      const report = await refreshHarness(store, 'cursor', source)

      // The contract-'1' checkpoint must not be reused: the unit is re-read
      // and re-synthesized, not skipped.
      const row = report.rows.find((r) => r.harnessId === 'cursor')
      expect(row).toBeDefined()
      expect(row?.skipped).toBe(0)
      expect((row?.created ?? 0) + (row?.updated ?? 0)).toBeGreaterThan(0)

      const checkpoint = store.getSourceCheckpoint('cursor', sourceKey)
      expect(checkpoint?.parserContractVersion).toBe(descriptor?.parserContractVersion)
      expect(checkpoint?.parserContractVersion).not.toBe('1')
      expect(checkpoint?.lastStatus).toBe('ok')
      // The re-read upserted the same span rather than duplicating it.
      expect(store.listAll().filter((r) => r.spanId === seeded.spanId)).toHaveLength(1)
    } finally {
      store.close()
    }
  })

  it('re-synthesizes a pi unit whose checkpoint was written under contract 1', async () => {
    const root = tempDir()
    const store = new CanonStore(join(root, 'canon.db'))
    try {
      const transcriptPath = join(root, 'pi', 'session.jsonl')
      mkdirSync(join(root, 'pi'), { recursive: true })
      writeFileSync(
        transcriptPath,
        PI_TURNS.map((turn) =>
          JSON.stringify({
            sessionId: turn.sessionId,
            turnId: turn.turnId,
            timestamp: turn.timestamp,
            model: turn.model,
          }),
        ).join('\n') + '\n',
        'utf8',
      )
      const source: SessionSource = { path: transcriptPath, provider: 'pi', project: 'contract-bump' }
      const { sourceKey, seeded } = seedStaleUnit(store, 'pi', source, PI_TURNS)

      // The bump itself: after T7 changed what the Pi parser extracts
      // (declared window from models-store.json), the contract may not stay
      // at '1'.
      expect(parserContractVersionFor({ providerName: 'pi', harnessId: 'pi' })).not.toBe('1')
      const descriptor = descriptorFor('pi')
      expect(descriptor?.parserContractVersion).not.toBe('1')

      const report = await refreshHarness(store, 'pi', source)

      const row = report.rows.find((r) => r.harnessId === 'pi')
      expect(row).toBeDefined()
      expect(row?.skipped).toBe(0)
      expect((row?.created ?? 0) + (row?.updated ?? 0)).toBeGreaterThan(0)

      const checkpoint = store.getSourceCheckpoint('pi', sourceKey)
      expect(checkpoint?.parserContractVersion).toBe(descriptor?.parserContractVersion)
      expect(checkpoint?.parserContractVersion).not.toBe('1')
      expect(checkpoint?.lastStatus).toBe('ok')
      expect(store.listAll().filter((r) => r.spanId === seeded.spanId)).toHaveLength(1)
    } finally {
      store.close()
    }
  })
})
