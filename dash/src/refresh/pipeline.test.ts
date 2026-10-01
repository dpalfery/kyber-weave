import { mkdtempSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { SessionSource } from '../providers/types.js'
import { Synthesizer } from '../synth/synth.js'
import { loadClaudeCalls } from '../synth/readers/claude.js'
import { fixtureProvider } from './fixtures/integration-harness.js'
import { refreshHarnessSources } from './orchestrator.js'
import { descriptorFor, sourceKeyFor } from './registry.js'
import { revisionTokenFor } from './source-reader.js'

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) {
    rmSync(root, { recursive: true, force: true })
  }
})

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-refresh-pipeline-test-'))
  temporaryRoots.push(dir)
  return dir
}

describe('refresh pipeline: parser contract version & checkpoint invalidation', () => {
  it('detects stale checkpoint with older parser_contract_version, re-parses source transcripts, and populates tool.invoke records', async () => {
    const root = tempDir()
    const dbPath = join(root, 'canon.db')
    const store = new CanonStore(dbPath)

    try {
      // 1. Arrange a Claude Code session JSONL containing a tool call.
      const transcriptPath = join(root, 'claude-session.jsonl')
      const transcriptLines = [
        JSON.stringify({
          type: 'assistant',
          sessionId: 'session-contract-invalidation-1',
          uuid: 'turn-1',
          timestamp: '2026-09-06T10:00:00.000Z',
          entrypoint: 'cli',
          message: {
            model: 'claude-sonnet-4-5',
            usage: { input_tokens: 100, output_tokens: 25 },
            content: [
              {
                type: 'tool_use',
                id: 'tu_bash_1',
                name: 'Bash',
                input: { command: 'git status' },
              },
            ],
          },
        }),
      ]
      writeFileSync(transcriptPath, transcriptLines.join('\n') + '\n', 'utf8')

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
        project: 'claude',
      }
      const sourceKey = sourceKeyFor('claude-cli', sessionSource)

      // 2. Pre-seed the DB as if it were ingested under legacy parser_contract_version '1'
      // before tool extraction was supported (only the parent llm.invoke record was committed).
      const [parentCall] = loadClaudeCalls(transcriptPath)
      expect(parentCall).toBeDefined()

      const [legacyTurnRecord] = new Synthesizer().synthesizeEnvelopes([
        {
          harnessId: 'claude-cli',
          sourceKey,
          nativeSessionId: parentCall!.sessionId,
          nativeRecordId: parentCall!.turnId,
          call: parentCall!,
          sourceRevision: revisionToken,
        },
      ])
      expect(legacyTurnRecord).toBeDefined()
      expect(legacyTurnRecord!.op).toBe('llm.invoke')

      store.commitSourceUnit({
        records: [legacyTurnRecord!],
        provenance: [
          {
            spanId: legacyTurnRecord!.spanId,
            harnessId: 'claude-cli',
            sourceKey,
            nativeSessionId: legacyTurnRecord!.sessionId ?? null,
            nativeRecordId: parentCall!.turnId ?? null,
            sourceRevision: revisionToken,
            parserVersion: '1',
            importedAtUtc: '2026-09-06T10:00:00.000Z',
            locationToken: '~/.claude/projects',
          },
        ],
        checkpoint: {
          harnessId: 'claude-cli',
          sourceKey,
          providerId: 'claude',
          parserId: 'claude',
          parserContractVersion: '1',
          format: 'jsonl',
          sourceRootLabel: '~/.claude/projects',
          revisionToken,
          coveredFromUtc: '2026-08-01T00:00:00.000Z',
          coveredThroughUtc: '2026-09-20T00:00:00.000Z',
          lastAttemptUtc: '2026-09-06T10:00:00.000Z',
          lastSuccessUtc: '2026-09-06T10:00:00.000Z',
          lastStatus: 'ok',
          lastErrorCode: null,
          unitCount: 1,
          recordCount: 1,
        },
      })

      // Baseline verification
      const recordsBefore = store.listAll()
      expect(recordsBefore).toHaveLength(1)
      expect(recordsBefore[0]!.op).toBe('llm.invoke')
      expect(store.listAll().filter((r) => r.op === 'tool.invoke')).toHaveLength(0)

      const initialCheckpoint = store.getSourceCheckpoint('claude-cli', sourceKey)
      expect(initialCheckpoint).toBeDefined()
      expect(initialCheckpoint?.parserContractVersion).toBe('1')

      // 3. Run refresh using the registered claude-cli descriptor
      const descriptor = descriptorFor('claude-cli')
      expect(descriptor).toBeDefined()

      const provider = fixtureProvider('claude', [sessionSource], (s) => loadClaudeCalls(s.path))

      const report = await refreshHarnessSources(store, {
        getAllProviders: async () => [provider],
        descriptors: [descriptor!],
        jobConcurrency: 1,
        writerCapacity: 1,
        commandStartedAt: new Date('2026-09-12T18:00:00.000Z'),
        parseAllSessions: async () => undefined,
      })

      // 4. Assert: The refresh must detect the stale checkpoint ('1' < '2'),
      // re-parse and synthesize the source, backpopulate child tool.invoke record,
      // and advance the checkpoint's parser_contract_version to '2'.
      const updatedCheckpoint = store.getSourceCheckpoint('claude-cli', sourceKey)
      expect(updatedCheckpoint).toBeDefined()
      expect(updatedCheckpoint?.parserContractVersion).toBe('2')
      expect(updatedCheckpoint?.lastStatus).toBe('ok')

      const claudeRow = report.rows.find((r) => r.harnessId === 'claude-cli')
      expect(claudeRow).toBeDefined()
      expect(claudeRow?.created).toBe(1)
      expect(claudeRow?.skipped).toBe(0)

      const allRecordsAfter = store.listAll()
      expect(allRecordsAfter).toHaveLength(2)

      const toolRecords = allRecordsAfter.filter((r) => r.op === 'tool.invoke')
      expect(toolRecords).toHaveLength(1)
      expect(toolRecords[0]!.name).toBe('Bash')
      expect(toolRecords[0]!.parentSpanId).toBe(legacyTurnRecord!.spanId)
      // Unmatched trailing tool call in transcript has status 'unset'
      expect(toolRecords[0]!.status).toBe('unset')
      expect(toolRecords[0]!.raw).toMatchObject({
        arguments: { command: 'git status' },
      })
    } finally {
      store.close()
    }
  })

  it('skips re-parsing when checkpoint parser_contract_version matches current descriptor version and file is unchanged', async () => {
    const root = tempDir()
    const dbPath = join(root, 'canon.db')
    const store = new CanonStore(dbPath)

    try {
      const transcriptPath = join(root, 'claude-session.jsonl')
      writeFileSync(
        transcriptPath,
        JSON.stringify({
          type: 'assistant',
          sessionId: 'session-skip-unchanged',
          uuid: 'turn-1',
          timestamp: '2026-09-06T10:00:00.000Z',
          entrypoint: 'cli',
          message: {
            model: 'claude-sonnet-4-5',
            usage: { input_tokens: 10, output_tokens: 5 },
          },
        }) + '\n',
        'utf8',
      )

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
        project: 'claude',
      }
      const sourceKey = sourceKeyFor('claude-cli', sessionSource)
      const descriptor = descriptorFor('claude-cli')
      expect(descriptor).toBeDefined()

      const [parentCall] = loadClaudeCalls(transcriptPath)
      const [legacyTurnRecord] = new Synthesizer().synthesizeEnvelopes([
        {
          harnessId: 'claude-cli',
          sourceKey,
          nativeSessionId: parentCall!.sessionId,
          nativeRecordId: parentCall!.turnId,
          call: parentCall!,
          sourceRevision: revisionToken,
        },
      ])

      // Seed with current descriptor's parserContractVersion
      store.commitSourceUnit({
        records: [legacyTurnRecord!],
        provenance: [
          {
            spanId: legacyTurnRecord!.spanId,
            harnessId: 'claude-cli',
            sourceKey,
            nativeSessionId: legacyTurnRecord!.sessionId ?? null,
            nativeRecordId: parentCall!.turnId ?? null,
            sourceRevision: revisionToken,
            parserVersion: descriptor!.parserContractVersion,
            importedAtUtc: '2026-09-06T10:00:00.000Z',
            locationToken: '~/.claude/projects',
          },
        ],
        checkpoint: {
          harnessId: 'claude-cli',
          sourceKey,
          providerId: 'claude',
          parserId: 'claude',
          parserContractVersion: descriptor!.parserContractVersion,
          format: 'jsonl',
          sourceRootLabel: '~/.claude/projects',
          revisionToken,
          coveredFromUtc: '2026-08-01T00:00:00.000Z',
          coveredThroughUtc: '2026-09-20T00:00:00.000Z',
          lastAttemptUtc: '2026-09-06T10:00:00.000Z',
          lastSuccessUtc: '2026-09-06T10:00:00.000Z',
          lastStatus: 'ok',
          lastErrorCode: null,
          unitCount: 1,
          recordCount: 1,
        },
      })

      const provider = fixtureProvider('claude', [sessionSource], (s) => loadClaudeCalls(s.path))

      const report = await refreshHarnessSources(store, {
        getAllProviders: async () => [provider],
        descriptors: [descriptor!],
        jobConcurrency: 1,
        writerCapacity: 1,
        commandStartedAt: new Date('2026-09-12T18:00:00.000Z'),
        parseAllSessions: async () => undefined,
      })

      const claudeRow = report.rows.find((r) => r.harnessId === 'claude-cli')
      expect(claudeRow).toBeDefined()
      expect(claudeRow?.skipped).toBe(1)
      expect(claudeRow?.created).toBe(0)
    } finally {
      store.close()
    }
  })
  it("scopes parserContractVersion 2 specifically to Claude descriptors while other harnesses remain at 1", () => {
    const claudeCli = descriptorFor("claude-cli")
    const claudeDesktop = descriptorFor("claude-desktop")
    expect(claudeCli?.parserContractVersion).toBe("2")
    expect(claudeDesktop?.parserContractVersion).toBe("2")

    const codex = descriptorFor("codex-cli")
    const copilot = descriptorFor("copilot-cli")
    const cursor = descriptorFor("cursor")
    expect(codex?.parserContractVersion).toBe("1")
    expect(copilot?.parserContractVersion).toBe("1")
    expect(cursor?.parserContractVersion).toBe("1")
  })
});
