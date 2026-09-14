import { afterEach, describe, expect, it, vi } from 'vitest'
import { copyFileSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import type { FileFingerprint } from '../../src/session-cache.js'
import type { DateRange } from '../../src/types.js'
import type { ParsedProviderCall, Provider, SessionSource } from '../../src/providers/types.js'

import { classifySessionSource } from './registry.js'
import {
  iterateNativeUnits,
  utcHistoryWindow,
  type SourceReaderDependencies,
} from './source-reader.js'

const fixtureDir = join(dirname(fileURLToPath(import.meta.url)), 'fixtures')
const temporaryRoots: string[] = []

afterEach(() => {
  vi.restoreAllMocks()
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

function tempRoot(): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-source-reader-'))
  temporaryRoots.push(root)
  return root
}

function range(): DateRange {
  return {
    start: new Date('2026-09-01T00:00:00.000Z'),
    end: new Date('2026-09-12T23:59:59.999Z'),
  }
}

function fingerprint(overrides: Partial<FileFingerprint> = {}): FileFingerprint {
  return { dev: 1, ino: 1, mtimeMs: 1, sizeBytes: 10, ...overrides }
}

function call(overrides: Partial<ParsedProviderCall> & Pick<ParsedProviderCall, 'provider' | 'sessionId' | 'timestamp'>): ParsedProviderCall {
  return {
    model: 'test-model',
    inputTokens: 1,
    outputTokens: 1,
    cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0,
    cachedInputTokens: 0,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0,
    tools: [],
    bashCommands: [],
    speed: 'standard',
    deduplicationKey: `${overrides.provider}:${overrides.sessionId}:${overrides.timestamp}`,
    userMessage: '',
    ...overrides,
  }
}

function provider(name: string, sources: SessionSource[], parsedByPath: ReadonlyMap<string, readonly ParsedProviderCall[]>): Provider {
  return {
    name,
    displayName: name,
    modelDisplayName: model => model,
    toolDisplayName: tool => tool,
    discoverSessions: async () => [...sources],
    createSessionParser(source) {
      return {
        async *parse(): AsyncGenerator<ParsedProviderCall> {
          yield* parsedByPath.get(source.path) ?? []
        },
      }
    },
  }
}

function emptyClaudeProvider(sources: SessionSource[]): Provider {
  return {
    name: 'claude',
    displayName: 'Claude Code',
    modelDisplayName: model => model,
    toolDisplayName: tool => tool,
    discoverSessions: async () => [...sources],
    createSessionParser() {
      return { async *parse(): AsyncGenerator<ParsedProviderCall> {} }
    },
  }
}

async function readHarness(
  harnessId: string,
  providers: Provider[],
  extras: Partial<SourceReaderDependencies> = {},
) {
  return iterateNativeUnits(harnessId, {
    providers,
    dateRange: range(),
    fingerprintFile: async () => fingerprint(),
    ...extras,
  })
}

describe('utcHistoryWindow', () => {
  it('builds a closed UTC interval of N weeks ending at command start', () => {
    const started = new Date('2026-09-12T18:00:00.000Z')
    expect(utcHistoryWindow(started, 2)).toEqual({
      start: new Date('2026-08-29T18:00:00.000Z'),
      end: started,
    })
  })
})

describe('iterateNativeUnits', () => {
  it('slices a cross-cutoff chat to in-window records and quarantines future-dated turns', async () => {
    const source: SessionSource = {
      path: '/native/pi/cross-cutoff.jsonl',
      project: 'kyber',
      provider: 'pi',
    }
    const units = await readHarness('pi', [
      provider('pi', [source], new Map([
        [source.path, [
          call({ provider: 'pi', sessionId: 'chat-1', timestamp: '2026-08-20T12:00:00.000Z' }),
          call({ provider: 'pi', sessionId: 'chat-1', timestamp: '2026-09-05T12:00:00.000Z', turnId: 'in-window' }),
          call({ provider: 'pi', sessionId: 'chat-1', timestamp: '2026-09-20T12:00:00.000Z', turnId: 'future' }),
          call({ provider: 'pi', sessionId: 'chat-1', timestamp: 'not-a-date', turnId: 'bad' }),
          call({ provider: 'pi', sessionId: 'chat-1', timestamp: '', turnId: 'missing' }),
        ]],
      ])),
    ])

    expect(units).toHaveLength(1)
    expect(units[0]!.envelopes.map(envelope => envelope.timestamp)).toEqual(['2026-09-05T12:00:00.000Z'])
    expect(units[0]!.envelopes[0]!.nativeSessionId).toBe('chat-1')
    expect(units[0]!.problems.map(problem => problem.code).sort()).toEqual([
      'FUTURE_DATED',
      'MALFORMED_TIMESTAMP',
      'MISSING_TIMESTAMP',
    ])
  })

  it('recovers Claude through the special parse seam and directory expansion, not the empty parser', async () => {
    const root = tempRoot()
    const projectDir = join(root, '.claude', 'projects', 'app')
    mkdirSync(projectDir, { recursive: true })
    const transcript = join(projectDir, 'sess.jsonl')
    copyFileSync(join(fixtureDir, 'claude-cli-session.jsonl'), transcript)

    const parseAllSessions = vi.fn(async (_dateRange?: DateRange, providerFilter?: string) => {
      expect(providerFilter).toBe('claude')
      return []
    })

    const units = await readHarness('claude-cli', [
      emptyClaudeProvider([{
        path: projectDir,
        project: 'app',
        provider: 'claude',
        sourceKind: 'claude-config',
      }]),
    ], { parseAllSessions })

    expect(parseAllSessions).toHaveBeenCalledTimes(1)
    expect(units).toHaveLength(1)
    expect(units[0]!.source.path).toBe(transcript)
    expect(units[0]!.harnessId).toBe('claude-cli')
    expect(units[0]!.envelopes).toHaveLength(1)
    expect(units[0]!.envelopes[0]!.call.sessionId).toBe('fixture-claude-cli')
  })

  it('splits Codex by originator and leaves t3code_desktop unclassified', async () => {
    const root = tempRoot()
    const sessions = join(root, '.codex', 'sessions')
    mkdirSync(sessions, { recursive: true })
    const cliPath = join(sessions, 'cli.jsonl')
    const desktopPath = join(sessions, 'desktop.jsonl')
    const unknownPath = join(sessions, 't3.jsonl')
    copyFileSync(join(fixtureDir, 'codex-cli.jsonl'), cliPath)
    copyFileSync(join(fixtureDir, 'codex-desktop.jsonl'), desktopPath)
    copyFileSync(join(fixtureDir, 'codex-t3code-desktop.jsonl'), unknownPath)

    const sources: SessionSource[] = [
      { path: cliPath, project: 'p', provider: 'codex' },
      { path: desktopPath, project: 'p', provider: 'codex' },
      { path: unknownPath, project: 'p', provider: 'codex' },
    ]
    const parsed = new Map(sources.map(source => [source.path, [
      call({ provider: 'codex', sessionId: source.path, timestamp: '2026-09-06T00:00:00.000Z' }),
    ]] as const))
    const providers = [provider('codex', sources, parsed)]

    const cli = await readHarness('codex-cli', providers)
    const desktop = await readHarness('codex-desktop', providers)
    const unclassified = await readHarness('codex-unclassified', providers)

    expect(cli.map(unit => unit.source.path)).toEqual([cliPath])
    expect(desktop.map(unit => unit.source.path)).toEqual([desktopPath])
    expect(unclassified.map(unit => unit.source.path)).toEqual([unknownPath])
    expect(classifySessionSource({
      source: sources[2]!,
      originator: 't3code_desktop',
    })).toEqual({ outcome: 'harness', harnessId: 'codex-unclassified' })
  })

  it('splits Antigravity by native root', async () => {
    const sources: SessionSource[] = [
      { path: '/home/u/.gemini/antigravity/a.pb', project: 'antigravity', provider: 'antigravity' },
      { path: '/home/u/.gemini/antigravity-cli/b.pb', project: 'antigravity-cli', provider: 'antigravity' },
      { path: '/home/u/.gemini/antigravity-ide/c.pb', project: 'antigravity-ide', provider: 'antigravity' },
    ]
    const parsed = new Map(sources.map(source => [source.path, [
      call({ provider: 'antigravity', sessionId: source.project, timestamp: '2026-09-06T00:00:00.000Z' }),
    ]] as const))
    const providers = [provider('antigravity', sources, parsed)]

    expect((await readHarness('antigravity', providers)).map(unit => unit.source.path)).toEqual([sources[0]!.path])
    expect((await readHarness('antigravity-cli', providers)).map(unit => unit.source.path)).toEqual([sources[1]!.path])
    expect((await readHarness('antigravity-ide', providers)).map(unit => unit.source.path)).toEqual([sources[2]!.path])
  })

  it('splits Copilot by sourceType', async () => {
    const sources: SessionSource[] = [
      { path: '/tmp/.copilot/a.jsonl', project: 'p', provider: 'copilot', sourceType: 'jsonl' },
      { path: '/tmp/store.db', project: 'p', provider: 'copilot', sourceType: 'session-store' },
      { path: '/tmp/x.chatSession', project: 'p', provider: 'copilot', sourceType: 'chatsession' },
      { path: '/tmp/t.json', project: 'p', provider: 'copilot', sourceType: 'transcript' },
      { path: '/tmp/jb', project: 'p', provider: 'copilot', sourceType: 'jetbrains' },
      { path: '/tmp/agent-traces.db', project: 'p', provider: 'copilot', sourceType: 'otel' },
    ]
    const parsed = new Map(sources.map(source => [source.path, [
      call({ provider: 'copilot', sessionId: source.path, timestamp: '2026-09-06T00:00:00.000Z' }),
    ]] as const))
    const providers = [provider('copilot', sources, parsed)]

    expect((await readHarness('copilot-cli', providers)).map(unit => unit.source.sourceType).sort())
      .toEqual(['jsonl', 'session-store'])
    expect((await readHarness('copilot-vscode', providers)).map(unit => unit.source.sourceType).sort())
      .toEqual(['chatsession', 'transcript'])
    expect((await readHarness('copilot-jetbrains', providers)).map(unit => unit.source.sourceType))
      .toEqual(['jetbrains'])
    expect((await readHarness('copilot-agent', providers)).map(unit => unit.source.sourceType))
      .toEqual(['otel'])
  })

  it('splits Kiro by path', async () => {
    const sources: SessionSource[] = [
      { path: '/home/u/.kiro/sessions/cli/s.jsonl', project: 'cli', provider: 'kiro' },
      { path: '/home/u/.kiro/sessions/proj/s.jsonl', project: 'ide', provider: 'kiro' },
    ]
    const parsed = new Map(sources.map(source => [source.path, [
      call({ provider: 'kiro', sessionId: source.project, timestamp: '2026-09-06T00:00:00.000Z' }),
    ]] as const))
    const providers = [provider('kiro', sources, parsed)]

    expect((await readHarness('kiro-cli', providers)).map(unit => unit.source.path)).toEqual([sources[0]!.path])
    expect((await readHarness('kiro-ide', providers)).map(unit => unit.source.path)).toEqual([sources[1]!.path])
  })

  it('keeps Kilo on the shared runtime fallback and does not guess a client', async () => {
    const sources: SessionSource[] = [
      { path: '/home/u/.local/share/kilo/kilo.db:session-1', project: 'shared', provider: 'kilo-code' },
      { path: '/home/u/globalStorage/kilocode.kilo-code/tasks/1', project: 'legacy', provider: 'kilo-code' },
    ]
    const parsed = new Map(sources.map(source => [source.path, [
      call({ provider: 'kilo-code', sessionId: source.project, timestamp: '2026-09-06T00:00:00.000Z' }),
    ]] as const))
    const providers = [provider('kilo-code', sources, parsed)]

    const shared = await readHarness('kilo-shared-runtime', providers)
    const legacy = await readHarness('kilo-vscode-legacy', providers)
    expect(shared.map(unit => unit.source.path)).toEqual([sources[0]!.path])
    expect(legacy.map(unit => unit.source.path)).toEqual([sources[1]!.path])
    expect(shared[0]!.harnessId).toBe('kilo-shared-runtime')
  })

  it('detects unchanged vs changed native units from fingerprints', async () => {
    const source: SessionSource = {
      path: '/native/pi/unit.jsonl',
      project: 'kyber',
      provider: 'pi',
      sourceId: 'pi-1',
    }
    const providers = [provider('pi', [source], new Map([
      [source.path, [call({ provider: 'pi', sessionId: 's', timestamp: '2026-09-06T00:00:00.000Z' })]],
    ]))]
    const previous = fingerprint({ mtimeMs: 100, sizeBytes: 40 })
    const previousFingerprints = new Map([['pi:pi-1', previous]])

    const unchanged = await readHarness('pi', providers, {
      previousFingerprints,
      fingerprintFile: async () => previous,
    })
    expect(unchanged[0]!.status).toBe('unchanged')
    expect(unchanged[0]!.envelopes).toEqual([])

    const changed = await readHarness('pi', providers, {
      previousFingerprints,
      fingerprintFile: async () => fingerprint({ mtimeMs: 200, sizeBytes: 80 }),
    })
    expect(changed[0]!.status).toBe('changed')
    expect(changed[0]!.envelopes).toHaveLength(1)
  })

  it('iterates native units with a bounded worker pool', async () => {
    const sources: SessionSource[] = Array.from({ length: 6 }, (_, index) => ({
      path: `/native/pi/${index}.jsonl`,
      project: 'kyber',
      provider: 'pi',
      sourceId: `u${index}`,
    }))
    let inflight = 0
    let maxInflight = 0
    const parseCalls = vi.fn(async (_provider: Provider, source: SessionSource) => {
      inflight += 1
      maxInflight = Math.max(maxInflight, inflight)
      await new Promise(resolve => setTimeout(resolve, 25))
      inflight -= 1
      return [call({ provider: 'pi', sessionId: source.sourceId ?? source.path, timestamp: '2026-09-06T00:00:00.000Z' })]
    })

    const units = await readHarness('pi', [provider('pi', sources, new Map())], {
      concurrency: 2,
      parseCalls,
    })

    expect(units).toHaveLength(6)
    expect(maxInflight).toBeLessThanOrEqual(2)
    expect(maxInflight).toBeGreaterThan(1)
    expect(units.map(unit => unit.sourceKey)).toEqual(sources.map(source => `pi:${source.sourceId}`))
  })
})
