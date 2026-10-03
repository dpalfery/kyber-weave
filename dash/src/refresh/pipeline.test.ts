import { mkdirSync, mkdtempSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { createKiloCodeProvider } from '../providers/kilo-code.js'
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
  it("scopes parserContractVersion bumps to Claude (3), Codex/Kilo (2), and leaves others at 1", () => {
    const claudeCli = descriptorFor("claude-cli")
    const claudeDesktop = descriptorFor("claude-desktop")
    expect(claudeCli?.parserContractVersion).toBe("3")
    expect(claudeDesktop?.parserContractVersion).toBe("3")

    const codex = descriptorFor("codex-cli")
    const kilo = descriptorFor("kilo-shared-runtime")
    const copilot = descriptorFor("copilot-cli")
    const cursor = descriptorFor("cursor")
    expect(codex?.parserContractVersion).toBe("2")
    expect(kilo?.parserContractVersion).toBe("2")
    expect(copilot?.parserContractVersion).toBe("1")
    expect(cursor?.parserContractVersion).toBe("1")
  })

  it("re-reads transcript files and advances parserContractVersion from 2 to 3 for claude-desktop (#232)", async () => {
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
      expect(updatedCheckpoint?.parserContractVersion).toBe('3')
      expect(updatedCheckpoint?.lastStatus).toBe('ok')

      const claudeRow = report.rows.find((r) => r.harnessId === 'claude-desktop')
      expect(claudeRow).toBeDefined()
      expect(claudeRow?.changed).toBe(1)
      expect(claudeRow?.skipped).toBe(0)
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
