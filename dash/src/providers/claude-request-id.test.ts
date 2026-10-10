import { readFileSync, statSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { fixtureProvider } from '../refresh/fixtures/integration-harness.js'
import { refreshHarnessSources } from '../refresh/orchestrator.js'
import { descriptorFor, parserContractVersionFor, sourceKeyFor } from '../refresh/registry.js'
import { revisionTokenFor } from '../refresh/source-reader.js'
import type { ParsedProviderCall, SessionSource } from './types.js'
import { loadClaudeCalls } from './claude.js'

const FIXTURE_DIR = join(dirname(fileURLToPath(import.meta.url)), '../canon/fixtures/claude-request-id')

describe('loadClaudeCalls stamps transcript requestId (P2.0)', () => {
  it('copies top-level requestId onto each parsed call', () => {
    const transcriptPath = join(FIXTURE_DIR, 'transcript-req-a.jsonl')
    const calls = loadClaudeCalls(transcriptPath)

    expect(calls).toHaveLength(1)
    expect((calls[0] as ParsedProviderCall & { requestId?: string }).requestId).toBe(
      'req_A01Synthetic0001',
    )
    expect(calls[0]!.turnId).toBe('msg_B01Synthetic0002')
  })
})

describe('Claude parser contract version 5 re-reads stale checkpoints (P2.0)', () => {
  it('declares parser contract version 5 for Claude harnesses', () => {
    expect(parserContractVersionFor({ providerName: 'claude', harnessId: 'claude-code' })).toBe('5')
    expect(parserContractVersionFor({ providerName: 'other', harnessId: 'claude-desktop' })).toBe('5')
  })

  it('re-reads a claude-desktop source left on contract 4 after the bump', async () => {
    const root = mkdtempSync(join(tmpdir(), 'claude-req-id-contract-'))
    const dbPath = join(root, 'canon.db')
    const store = new CanonStore(dbPath)
    try {
      const transcriptPath = join(root, 'stale-checkpoint.jsonl')
      writeFileSync(transcriptPath, readFileSync(join(FIXTURE_DIR, 'stale-checkpoint-transcript.jsonl'), 'utf8'), 'utf8')

      const stat = statSync(transcriptPath)
      const revisionToken = revisionTokenFor({
        dev: stat.dev,
        ino: stat.ino,
        mtimeMs: stat.mtimeMs,
        sizeBytes: stat.size,
      })

      const sessionSource: SessionSource = {
        path: transcriptPath,
        provider: 'claude',
        project: 'test-project',
        sourceKind: 'claude-desktop',
      }
      const sourceKey = sourceKeyFor('claude-desktop', sessionSource)

      store.commitSourceUnit({
        records: [],
        provenance: [],
        checkpoint: {
          harnessId: 'claude-desktop',
          sourceKey,
          providerId: 'claude',
          parserId: 'claude',
          parserContractVersion: '4',
          format: 'jsonl',
          sourceRootLabel: '~/.claude/projects',
          revisionToken,
          coveredFromUtc: '2026-08-01T00:00:00.000Z',
          coveredThroughUtc: '2026-09-20T00:00:00.000Z',
          lastAttemptUtc: '2026-10-02T14:00:00.000Z',
          lastSuccessUtc: '2026-10-02T14:00:00.000Z',
          lastStatus: 'ok',
          lastErrorCode: null,
          unitCount: 0,
          recordCount: 0,
        },
      })

      expect(store.getSourceCheckpoint('claude-desktop', sourceKey)?.parserContractVersion).toBe('4')

      const descriptor = descriptorFor('claude-desktop')
      expect(descriptor).toBeDefined()

      const report = await refreshHarnessSources(store, {
        getAllProviders: async () => [
          fixtureProvider('claude', [sessionSource], (s) => loadClaudeCalls(s.path)),
        ],
        descriptors: [descriptor!],
        jobConcurrency: 1,
        writerCapacity: 1,
        commandStartedAt: new Date('2026-10-07T18:00:00.000Z'),
        parseAllSessions: async () => undefined,
      })

      const updated = store.getSourceCheckpoint('claude-desktop', sourceKey)
      expect(updated?.parserContractVersion).toBe('5')
      expect(updated?.lastStatus).toBe('ok')
      expect(report.rows.find((row) => row.harnessId === 'claude-desktop')?.changed).toBeGreaterThan(0)
    } finally {
      store.close()
      rmSync(root, { recursive: true, force: true })
    }
  })
})
