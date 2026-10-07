import { mkdirSync, mkdtempSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CONTENT_RETENTION_DAYS } from '../canon/retention.js'
import { CanonStore } from '../canon/store.js'
import { createKiloCodeProvider } from '../providers/kilo-code.js'
import type { SessionSource } from '../providers/types.js'
import { Synthesizer } from '../synth/synth.js'
import { loadClaudeCalls } from '../synth/readers/claude.js'
import { fixtureProvider } from './fixtures/integration-harness.js'
import { DEFAULT_HISTORY_WEEKS, refreshHarnessSources } from './orchestrator.js'
import { descriptorFor, sourceKeyFor } from './registry.js'
import { revisionTokenFor, utcHistoryWindow } from './source-reader.js'

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
      expect(updatedCheckpoint?.parserContractVersion).toBe(descriptor!.parserContractVersion)
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
  it("scopes parserContractVersion bumps to Claude (4), Codex/Kilo (2), and leaves others at 1", () => {
    const claudeCli = descriptorFor("claude-cli")
    const claudeDesktop = descriptorFor("claude-desktop")
    expect(claudeCli?.parserContractVersion).toBe("4")
    expect(claudeDesktop?.parserContractVersion).toBe("4")

    const codex = descriptorFor("codex-cli")
    const kilo = descriptorFor("kilo-shared-runtime")
    const copilot = descriptorFor("copilot-cli")
    const cursor = descriptorFor("cursor")
    expect(codex?.parserContractVersion).toBe("2")
    expect(kilo?.parserContractVersion).toBe("2")
    expect(copilot?.parserContractVersion).toBe("1")
    expect(cursor?.parserContractVersion).toBe("1")
  })

  it("re-reads transcript files and advances parserContractVersion from 2 to 4 for claude-desktop (#232)", async () => {
    const root = tempDir()
    const dbPath = join(root, 'canon.db')
    const store = new CanonStore(dbPath)
    try {
      const transcriptPath = join(root, 'paired-session.jsonl')
      writeFileSync(
        transcriptPath,
        JSON.stringify({
          type: 'assistant',
          sessionId: 'paired-session',
          uuid: 'turn-1',
          timestamp: '2026-09-06T10:00:00.000Z',
          entrypoint: 'claude-desktop',
          message: {
            model: 'claude-sonnet-4-5',
            usage: { input_tokens: 11, output_tokens: 7 },
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
        project: 'test-project',
        sourceKind: 'claude-desktop',
      }
      const sourceKey = sourceKeyFor('claude-desktop', sessionSource)

      const [parentCall] = loadClaudeCalls(transcriptPath)
      const [legacyTurnRecord] = new Synthesizer().synthesizeEnvelopes([
        {
          harnessId: 'claude-desktop',
          sourceKey,
          nativeSessionId: parentCall!.sessionId,
          nativeRecordId: parentCall!.turnId,
          call: parentCall!,
          sourceRevision: revisionToken,
        },
      ])

      store.commitSourceUnit({
        records: [legacyTurnRecord!],
        provenance: [
          {
            spanId: legacyTurnRecord!.spanId,
            harnessId: 'claude-desktop',
            sourceKey,
            nativeSessionId: legacyTurnRecord!.sessionId ?? null,
            nativeRecordId: parentCall!.turnId ?? null,
            sourceRevision: revisionToken,
            parserVersion: '2',
            importedAtUtc: '2026-09-06T10:00:00.000Z',
            locationToken: '~/.claude/projects',
          },
        ],
        checkpoint: {
          harnessId: 'claude-desktop',
          sourceKey,
          providerId: 'claude',
          parserId: 'claude',
          parserContractVersion: '2',
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

      const initialCheckpoint = store.getSourceCheckpoint('claude-desktop', sourceKey)
      expect(initialCheckpoint?.parserContractVersion).toBe('2')

      const descriptor = descriptorFor('claude-desktop')
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

      const updatedCheckpoint = store.getSourceCheckpoint('claude-desktop', sourceKey)
      expect(updatedCheckpoint?.parserContractVersion).toBe('4')
      expect(updatedCheckpoint?.lastStatus).toBe('ok')

      const claudeRow = report.rows.find((r) => r.harnessId === 'claude-desktop')
      expect(claudeRow).toBeDefined()
      expect(claudeRow?.changed).toBe(1)
      expect(claudeRow?.skipped).toBe(0)
    } finally {
      store.close()
    }
  })

  it("re-synthesizes stale same-span claude-desktop rows with parts after parserContractVersion 3→4 (#216)", async () => {
    const root = tempDir()
    const dbPath = join(root, 'canon.db')
    const store = new CanonStore(dbPath)
    try {
      // Seed outside the run window but inside the 14-day content-retention
      // floor. historyWeeks:1 → window starts 2026-09-05; retention cutoff is
      // 2026-08-29. A default 2-week window coincides with retention, so the
      // out-of-window gap would be purged before this assertion (#276).
      const transcriptPath = join(root, 'desktop-parts-session.jsonl')
      writeFileSync(
        transcriptPath,
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desktop-parts-session',
          uuid: 'turn-1',
          timestamp: '2026-09-01T10:00:00.000Z',
          entrypoint: 'claude-desktop',
          message: {
            model: 'claude-sonnet-4-5',
            usage: { input_tokens: 11, output_tokens: 7 },
            content: [{ type: 'text', text: 'hello from desktop' }],
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
        project: 'test-project',
        sourceKind: 'claude-desktop',
      }
      const sourceKey = sourceKeyFor('claude-desktop', sessionSource)

      // Counters-only seed: no readerTurn, so parts stay absent — the historical
      // shape left by pre-v4 ingest at the same span id.
      const [parentCall] = loadClaudeCalls(transcriptPath)
      const [legacyTurnRecord] = new Synthesizer().synthesizeEnvelopes([
        {
          harnessId: 'claude-desktop',
          sourceKey,
          nativeSessionId: parentCall!.sessionId,
          nativeRecordId: parentCall!.turnId,
          call: parentCall!,
          sourceRevision: revisionToken,
        },
      ])
      expect(legacyTurnRecord!.parts ?? []).toHaveLength(0)

      store.commitSourceUnit({
        records: [legacyTurnRecord!],
        provenance: [
          {
            spanId: legacyTurnRecord!.spanId,
            harnessId: 'claude-desktop',
            sourceKey,
            nativeSessionId: legacyTurnRecord!.sessionId ?? null,
            nativeRecordId: parentCall!.turnId ?? null,
            sourceRevision: revisionToken,
            parserVersion: '3',
            importedAtUtc: '2026-09-01T10:00:00.000Z',
            locationToken: '~/.claude/projects',
          },
        ],
        checkpoint: {
          harnessId: 'claude-desktop',
          sourceKey,
          providerId: 'claude',
          parserId: 'claude',
          parserContractVersion: '3',
          format: 'jsonl',
          sourceRootLabel: '~/.claude/projects',
          revisionToken,
          coveredFromUtc: '2026-08-01T00:00:00.000Z',
          coveredThroughUtc: '2026-09-20T00:00:00.000Z',
          lastAttemptUtc: '2026-09-01T10:00:00.000Z',
          lastSuccessUtc: '2026-09-01T10:00:00.000Z',
          lastStatus: 'ok',
          lastErrorCode: null,
          unitCount: 1,
          recordCount: 1,
        },
      })

      const initialCheckpoint = store.getSourceCheckpoint('claude-desktop', sourceKey)
      expect(initialCheckpoint?.parserContractVersion).toBe('3')

      const descriptor = descriptorFor('claude-desktop')
      expect(descriptor).toBeDefined()
      expect(descriptor!.parserContractVersion).toBe('4')

      const provider = fixtureProvider('claude', [sessionSource], (s) => loadClaudeCalls(s.path))
      // Version-repair must widen before the concurrent first pass so the
      // transcript is parsed once, not twice (narrow then sequential widen).
      const started = new Date('2026-09-12T18:00:00.000Z')
      const parseCalls = vi.fn(async (_provider, source, _dateRange) => loadClaudeCalls(source.path))

      const report = await refreshHarnessSources(
        store,
        {
          getAllProviders: async () => [provider],
          descriptors: [descriptor!],
          jobConcurrency: 1,
          writerCapacity: 1,
          commandStartedAt: started,
          parseAllSessions: async () => undefined,
          parseCalls,
        },
        { historyWeeks: 1 },
      )

      const updatedCheckpoint = store.getSourceCheckpoint('claude-desktop', sourceKey)
      expect(updatedCheckpoint?.parserContractVersion).toBe('4')
      expect(updatedCheckpoint?.lastStatus).toBe('ok')
      // Checkpoint keeps the claimed prior floor; the parse slice is clamped to
      // the content-retention window (#276).
      expect(updatedCheckpoint?.coveredFromUtc).toBe('2026-08-01T00:00:00.000Z')
      expect(parseCalls).toHaveBeenCalledTimes(1)
      expect(parseCalls.mock.calls[0]![2]).toEqual({
        start: new Date(started.getTime() - CONTENT_RETENTION_DAYS * 24 * 60 * 60 * 1000),
        end: started,
      })

      const claudeRow = report.rows.find((r) => r.harnessId === 'claude-desktop')
      expect(claudeRow).toBeDefined()
      expect(claudeRow?.changed).toBe(1)
      expect(claudeRow?.skipped).toBe(0)
      // Repair contract: version-invalidated refresh must upsert the corrected
      // payload over the stale same-span row, not only bump the checkpoint.
      expect(claudeRow?.updated).toBe(1)

      const refreshed = store.get(legacyTurnRecord!.spanId)
      expect(refreshed?.parts?.length).toBeGreaterThan(0)
      expect(refreshed?.parts?.some((part) => part.text === 'hello from desktop')).toBe(true)
    } finally {
      store.close()
    }
  })

  it('parses a version-invalidated unit once under the widened repair range (#276)', async () => {
    const root = tempDir()
    const dbPath = join(root, 'canon.db')
    const store = new CanonStore(dbPath)
    try {
      const started = new Date('2026-09-12T18:00:00.000Z')
      const transcriptPath = join(root, 'desktop-once-parse.jsonl')
      writeFileSync(
        transcriptPath,
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desktop-once-parse',
          uuid: 'turn-1',
          timestamp: '2026-09-01T10:00:00.000Z',
          entrypoint: 'claude-desktop',
          message: {
            model: 'claude-sonnet-4-5',
            usage: { input_tokens: 11, output_tokens: 7 },
            content: [{ type: 'text', text: 'hello from desktop' }],
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
        project: 'test-project',
        sourceKind: 'claude-desktop',
      }
      const sourceKey = sourceKeyFor('claude-desktop', sessionSource)
      const [parentCall] = loadClaudeCalls(transcriptPath)
      const [legacyTurnRecord] = new Synthesizer().synthesizeEnvelopes([
        {
          harnessId: 'claude-desktop',
          sourceKey,
          nativeSessionId: parentCall!.sessionId,
          nativeRecordId: parentCall!.turnId,
          call: parentCall!,
          sourceRevision: revisionToken,
        },
      ])
      expect(legacyTurnRecord!.parts ?? []).toHaveLength(0)

      store.commitSourceUnit({
        records: [legacyTurnRecord!],
        provenance: [
          {
            spanId: legacyTurnRecord!.spanId,
            harnessId: 'claude-desktop',
            sourceKey,
            nativeSessionId: legacyTurnRecord!.sessionId ?? null,
            nativeRecordId: parentCall!.turnId ?? null,
            sourceRevision: revisionToken,
            parserVersion: '3',
            importedAtUtc: '2026-09-01T10:00:00.000Z',
            locationToken: '~/.claude/projects',
          },
        ],
        checkpoint: {
          harnessId: 'claude-desktop',
          sourceKey,
          providerId: 'claude',
          parserId: 'claude',
          parserContractVersion: '3',
          format: 'jsonl',
          sourceRootLabel: '~/.claude/projects',
          revisionToken,
          coveredFromUtc: '2026-08-01T00:00:00.000Z',
          coveredThroughUtc: '2026-09-20T00:00:00.000Z',
          lastAttemptUtc: '2026-09-01T10:00:00.000Z',
          lastSuccessUtc: '2026-09-01T10:00:00.000Z',
          lastStatus: 'ok',
          lastErrorCode: null,
          unitCount: 1,
          recordCount: 1,
        },
      })

      const descriptor = descriptorFor('claude-desktop')
      const provider = fixtureProvider('claude', [sessionSource], (s) => loadClaudeCalls(s.path))
      const parseStarts: Date[] = []
      const report = await refreshHarnessSources(
        store,
        {
          getAllProviders: async () => [provider],
          descriptors: [descriptor!],
          jobConcurrency: 1,
          writerCapacity: 1,
          commandStartedAt: started,
          parseAllSessions: async () => undefined,
          parseCalls: async (_provider, source, dateRange) => {
            parseStarts.push(dateRange.start)
            return loadClaudeCalls(source.path)
          },
        },
        { historyWeeks: 1 },
      )

      const retentionFloor = new Date(started.getTime() - CONTENT_RETENTION_DAYS * 24 * 60 * 60 * 1000)
      const weekWindowStart = new Date(started.getTime() - 7 * 24 * 60 * 60 * 1000)
      expect(parseStarts).toHaveLength(1)
      expect(parseStarts[0]!.getTime()).toBe(retentionFloor.getTime())
      expect(parseStarts[0]!.getTime()).toBeLessThan(weekWindowStart.getTime())

      const claudeRow = report.rows.find((row) => row.harnessId === 'claude-desktop')
      expect(claudeRow?.updated).toBe(1)
      const refreshed = store.get(legacyTurnRecord!.spanId)
      expect(refreshed?.parts?.some((part) => part.text === 'hello from desktop')).toBe(true)
    } finally {
      store.close()
    }
  })

  it('does not widen the version-repair parse under default --history-weeks (#276)', async () => {
    const root = tempDir()
    const dbPath = join(root, 'canon.db')
    const store = new CanonStore(dbPath)
    try {
      const started = new Date('2026-09-12T18:00:00.000Z')
      const transcriptPath = join(root, 'desktop-default-window.jsonl')
      writeFileSync(
        transcriptPath,
        JSON.stringify({
          type: 'assistant',
          sessionId: 'desktop-default-window',
          uuid: 'turn-1',
          timestamp: '2026-09-06T10:00:00.000Z',
          entrypoint: 'claude-desktop',
          message: {
            model: 'claude-sonnet-4-5',
            usage: { input_tokens: 11, output_tokens: 7 },
            content: [{ type: 'text', text: 'hello from desktop' }],
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
        project: 'test-project',
        sourceKind: 'claude-desktop',
      }
      const sourceKey = sourceKeyFor('claude-desktop', sessionSource)
      const [parentCall] = loadClaudeCalls(transcriptPath)
      const [legacyTurnRecord] = new Synthesizer().synthesizeEnvelopes([
        {
          harnessId: 'claude-desktop',
          sourceKey,
          nativeSessionId: parentCall!.sessionId,
          nativeRecordId: parentCall!.turnId,
          call: parentCall!,
          sourceRevision: revisionToken,
        },
      ])

      store.commitSourceUnit({
        records: [legacyTurnRecord!],
        provenance: [
          {
            spanId: legacyTurnRecord!.spanId,
            harnessId: 'claude-desktop',
            sourceKey,
            nativeSessionId: legacyTurnRecord!.sessionId ?? null,
            nativeRecordId: parentCall!.turnId ?? null,
            sourceRevision: revisionToken,
            parserVersion: '3',
            importedAtUtc: '2026-09-06T10:00:00.000Z',
            locationToken: '~/.claude/projects',
          },
        ],
        checkpoint: {
          harnessId: 'claude-desktop',
          sourceKey,
          providerId: 'claude',
          parserId: 'claude',
          parserContractVersion: '3',
          format: 'jsonl',
          sourceRootLabel: '~/.claude/projects',
          revisionToken,
          coveredFromUtc: '2026-06-01T00:00:00.000Z',
          coveredThroughUtc: '2026-09-20T00:00:00.000Z',
          lastAttemptUtc: '2026-09-06T10:00:00.000Z',
          lastSuccessUtc: '2026-09-06T10:00:00.000Z',
          lastStatus: 'ok',
          lastErrorCode: null,
          unitCount: 1,
          recordCount: 1,
        },
      })

      const descriptor = descriptorFor('claude-desktop')
      const provider = fixtureProvider('claude', [sessionSource], (s) => loadClaudeCalls(s.path))
      const parseStarts: Date[] = []
      await refreshHarnessSources(
        store,
        {
          getAllProviders: async () => [provider],
          descriptors: [descriptor!],
          jobConcurrency: 1,
          writerCapacity: 1,
          commandStartedAt: started,
          parseAllSessions: async () => undefined,
          parseCalls: async (_provider, source, dateRange) => {
            parseStarts.push(dateRange.start)
            return loadClaudeCalls(source.path)
          },
        },
      )

      const defaultWindow = utcHistoryWindow(started, DEFAULT_HISTORY_WEEKS)
      expect(parseStarts).toHaveLength(1)
      expect(parseStarts[0]!.getTime()).toBe(defaultWindow.start.getTime())
      expect(store.getSourceCheckpoint('claude-desktop', sourceKey)?.coveredFromUtc).toBe(
        '2026-06-01T00:00:00.000Z',
      )
    } finally {
      store.close()
    }
  })

  it('does not re-ingest parser-contract rows older than the content-retention floor (#276)', async () => {
    const root = tempDir()
    const dbPath = join(root, 'canon.db')
    const store = new CanonStore(dbPath)
    try {
      const started = new Date('2026-09-12T18:00:00.000Z')
      const transcriptPath = join(root, 'desktop-retention-clamp.jsonl')
      writeFileSync(
        transcriptPath,
        [
          JSON.stringify({
            type: 'assistant',
            sessionId: 'desktop-retention-clamp',
            uuid: 'turn-june',
            timestamp: '2026-06-15T10:00:00.000Z',
            entrypoint: 'claude-desktop',
            message: {
              model: 'claude-sonnet-4-5',
              usage: { input_tokens: 11, output_tokens: 7 },
              content: [{ type: 'text', text: 'hello from june' }],
            },
          }),
          JSON.stringify({
            type: 'assistant',
            sessionId: 'desktop-retention-clamp',
            uuid: 'turn-sept',
            timestamp: '2026-09-01T10:00:00.000Z',
            entrypoint: 'claude-desktop',
            message: {
              model: 'claude-sonnet-4-5',
              usage: { input_tokens: 11, output_tokens: 7 },
              content: [{ type: 'text', text: 'hello from september' }],
            },
          }),
        ].join('\n') + '\n',
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
        project: 'test-project',
        sourceKind: 'claude-desktop',
      }
      const sourceKey = sourceKeyFor('claude-desktop', sessionSource)
      const calls = loadClaudeCalls(transcriptPath)
      expect(calls).toHaveLength(2)
      const [juneCall, septCall] = calls
      const [juneRecord, septRecord] = new Synthesizer().synthesizeEnvelopes(
        [juneCall, septCall].map((call) => ({
          harnessId: 'claude-desktop' as const,
          sourceKey,
          nativeSessionId: call!.sessionId,
          nativeRecordId: call!.turnId,
          call: call!,
          sourceRevision: revisionToken,
        })),
      )
      expect(juneRecord!.parts ?? []).toHaveLength(0)
      expect(septRecord!.parts ?? []).toHaveLength(0)

      store.commitSourceUnit({
        records: [juneRecord!, septRecord!],
        provenance: [
          {
            spanId: juneRecord!.spanId,
            harnessId: 'claude-desktop',
            sourceKey,
            nativeSessionId: juneRecord!.sessionId ?? null,
            nativeRecordId: juneCall!.turnId ?? null,
            sourceRevision: revisionToken,
            parserVersion: '3',
            importedAtUtc: '2026-06-15T10:00:00.000Z',
            locationToken: '~/.claude/projects',
          },
          {
            spanId: septRecord!.spanId,
            harnessId: 'claude-desktop',
            sourceKey,
            nativeSessionId: septRecord!.sessionId ?? null,
            nativeRecordId: septCall!.turnId ?? null,
            sourceRevision: revisionToken,
            parserVersion: '3',
            importedAtUtc: '2026-09-01T10:00:00.000Z',
            locationToken: '~/.claude/projects',
          },
        ],
        checkpoint: {
          harnessId: 'claude-desktop',
          sourceKey,
          providerId: 'claude',
          parserId: 'claude',
          parserContractVersion: '3',
          format: 'jsonl',
          sourceRootLabel: '~/.claude/projects',
          revisionToken,
          coveredFromUtc: '2026-06-01T00:00:00.000Z',
          coveredThroughUtc: '2026-09-20T00:00:00.000Z',
          lastAttemptUtc: '2026-06-15T10:00:00.000Z',
          lastSuccessUtc: '2026-06-15T10:00:00.000Z',
          lastStatus: 'ok',
          lastErrorCode: null,
          unitCount: 1,
          recordCount: 2,
        },
      })

      const descriptor = descriptorFor('claude-desktop')
      const provider = fixtureProvider('claude', [sessionSource], (s) => loadClaudeCalls(s.path))
      const report = await refreshHarnessSources(
        store,
        {
          getAllProviders: async () => [provider],
          descriptors: [descriptor!],
          jobConcurrency: 1,
          writerCapacity: 1,
          commandStartedAt: started,
          parseAllSessions: async () => undefined,
        },
        { historyWeeks: 1 },
      )

      const claudeRow = report.rows.find((row) => row.harnessId === 'claude-desktop')
      expect(claudeRow?.updated).toBe(1)
      expect(store.getSourceCheckpoint('claude-desktop', sourceKey)?.coveredFromUtc).toBe(
        '2026-06-01T00:00:00.000Z',
      )

      const juneRefreshed = store.get(juneRecord!.spanId)
      const septRefreshed = store.get(septRecord!.spanId)
      expect(juneRefreshed?.parts ?? []).toHaveLength(0)
      expect(septRefreshed?.parts?.some((part) => part.text === 'hello from september')).toBe(true)
    } finally {
      store.close()
    }
  })

  it('re-reads kilo.db shared-runtime sessions and advances parserContractVersion from 1 to 2, populating records (#227)', async () => {
    const root = tempDir()
    const dbPath = join(root, 'canon.db')
    const store = new CanonStore(dbPath)

    const prevXdg = process.env['XDG_DATA_HOME']
    process.env['XDG_DATA_HOME'] = root

    try {
      const kiloDir = join(root, 'kilo')
      mkdirSync(kiloDir, { recursive: true })
      const kiloDbPath = join(kiloDir, 'kilo.db')

      const { createRequire } = await import('node:module')
      const req = createRequire(import.meta.url)
      const { DatabaseSync } = req('node:sqlite') as {
        DatabaseSync: new (path: string) => {
          exec(sql: string): void
          prepare(sql: string): { run(...params: unknown[]): void }
          close(): void
        }
      }

      const db = new DatabaseSync(kiloDbPath)
      db.exec(`
        CREATE TABLE session (
          id TEXT PRIMARY KEY, project_id TEXT NOT NULL, parent_id TEXT,
          slug TEXT NOT NULL, directory TEXT NOT NULL, title TEXT NOT NULL,
          version TEXT NOT NULL, time_created INTEGER, time_updated INTEGER,
          time_archived INTEGER
        );
        CREATE TABLE message (
          id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
          time_created INTEGER, time_updated INTEGER, data TEXT NOT NULL
        );
        CREATE TABLE part (
          id TEXT PRIMARY KEY, message_id TEXT NOT NULL,
          session_id TEXT NOT NULL, time_created INTEGER,
          time_updated INTEGER, data TEXT NOT NULL
        );
      `)

      const now = Date.now() - 3600000 // 1 hour ago (within refresh window)
      db.prepare(`
        INSERT INTO session (id, project_id, parent_id, slug, directory, title, version, time_created, time_updated, time_archived)
        VALUES ('sess-kilo-issue227', 'proj-1', NULL, 'slug-1', '/Users/hal/myproject', 'Kilo session 1', '1.0', ?, ?, NULL)
      `).run(now, now)

      db.prepare(`
        INSERT INTO message (id, session_id, time_created, time_updated, data)
        VALUES ('msg-u1', 'sess-kilo-issue227', ?, ?, ?)
      `).run(now, now, JSON.stringify({ role: 'user' }))

      db.prepare(`
        INSERT INTO part (id, message_id, session_id, time_created, time_updated, data)
        VALUES ('part-u1', 'msg-u1', 'sess-kilo-issue227', ?, ?, ?)
      `).run(now, now, JSON.stringify({ type: 'markdown', text: 'Help fix issue 227' }))

      db.prepare(`
        INSERT INTO message (id, session_id, time_created, time_updated, data)
        VALUES ('msg-a1', 'sess-kilo-issue227', ?, ?, ?)
      `).run(now + 1000, now + 1000, JSON.stringify({
        role: 'assistant',
        modelID: 'anthropic/claude-3-5-sonnet',
        tokens_input: 150,
        tokens_output: 45,
        tokens_reasoning: 0,
        tokens_cache_read: 0,
        tokens_cache_write: 0,
      }))

      db.prepare(`
        INSERT INTO part (id, message_id, session_id, time_created, time_updated, data)
        VALUES ('part-a1', 'msg-a1', 'sess-kilo-issue227', ?, ?, ?)
      `).run(now + 1000, now + 1000, JSON.stringify({ type: 'markdown', text: 'Inspecting KiloCode token extractors.' }))

      db.close()

      const provider = createKiloCodeProvider()
      const sessions = await provider.discoverSessions()
      const kiloSession = sessions.find((s) => s.path.includes('sess-kilo-issue227'))
      expect(kiloSession).toBeDefined()

      const sourceKey = sourceKeyFor('kilo-shared-runtime', kiloSession!)
      const descriptor = descriptorFor('kilo-shared-runtime')
      expect(descriptor).toBeDefined()
      expect(descriptor?.parserContractVersion).toBe('2')

      // Pre-seed a stale checkpoint with legacy contract version '1' and 3 claimed records
      // but 0 actual records in the database (reproducing the issue #227 state).
      store.commitSourceUnit({
        records: [],
        provenance: [],
        checkpoint: {
          harnessId: 'kilo-shared-runtime',
          sourceKey,
          providerId: 'kilo-code',
          parserId: 'kilo-code',
          parserContractVersion: '1',
          format: 'sqlite',
          sourceRootLabel: '~/.local/share/kilo/kilo.db',
          revisionToken: 'legacy-revision-token',
          coveredFromUtc: new Date(now - 7 * 86400000).toISOString(),
          coveredThroughUtc: new Date(now + 86400000).toISOString(),
          lastAttemptUtc: new Date(now).toISOString(),
          lastSuccessUtc: new Date(now).toISOString(),
          lastStatus: 'ok',
          lastErrorCode: null,
          unitCount: 1,
          recordCount: 3, // Claims 3 records, but 0 in store.records
        },
      })

      const initialCheckpoint = store.getSourceCheckpoint('kilo-shared-runtime', sourceKey)
      expect(initialCheckpoint?.parserContractVersion).toBe('1')
      expect(initialCheckpoint?.recordCount).toBe(3)
      expect(store.listAll().filter((r) => r.harness === 'kilo-shared-runtime')).toHaveLength(0)

      const report = await refreshHarnessSources(store, {
        getAllProviders: async () => [provider],
        descriptors: [descriptor!],
        jobConcurrency: 1,
        writerCapacity: 1,
        commandStartedAt: new Date(),
        parseAllSessions: async () => undefined,
      })

      const updatedCheckpoint = store.getSourceCheckpoint('kilo-shared-runtime', sourceKey)
      expect(updatedCheckpoint?.parserContractVersion).toBe('2')
      expect(updatedCheckpoint?.lastStatus).toBe('ok')
      expect(updatedCheckpoint?.recordCount).toBe(1)

      const records = store.listAll().filter((r) => r.harness === 'kilo-shared-runtime')
      expect(records).toHaveLength(1)
      expect(records[0]?.tokens?.freshInput).toBe(150)
      expect(records[0]?.tokens?.output).toBe(45)

      const kiloRow = report.rows.find((r) => r.harnessId === 'kilo-shared-runtime')
      expect(kiloRow).toBeDefined()
      expect(kiloRow?.changed).toBe(1)
      expect(kiloRow?.created).toBe(1)
    } finally {
      if (prevXdg === undefined) {
        delete process.env['XDG_DATA_HOME']
      } else {
        process.env['XDG_DATA_HOME'] = prevXdg
      }
      store.close()
    }
  })
});
