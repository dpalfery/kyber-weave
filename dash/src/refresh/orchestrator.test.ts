// Harness-source refresh contract: one job per registry descriptor, isolated
// failures, no Gemini harness, and writes proportional to changed units.

import { afterEach, describe, expect, it } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import type { ParsedProviderCall, Provider, SessionSource } from '../synth/provider.js'
import { PROVIDER_PARSE_ERROR } from '../synth/provider.js'
import { CanonStore } from '../canon/store.js'
import { descriptorFor } from './registry.js'
import type { NativeUnit } from './source-reader.js'
import { refreshHarnessSources } from './orchestrator.js'
import { formatRefreshReport } from './report.js'

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
})

function temporaryStore(): CanonStore {
  const root = mkdtempSync(join(tmpdir(), 'kyber-local-refresh-'))
  temporaryRoots.push(root)
  return new CanonStore(join(root, 'canon.db'))
}

function source(provider: string, name: string): SessionSource {
  return {
    path: `/native/${provider}/${name}.jsonl`,
    project: provider === 'antigravity' ? 'antigravity' : 'kyber-weave',
    provider,
  }
}

function call(provider: string, sessionId: string): ParsedProviderCall {
  return {
    provider,
    model: provider === 'antigravity' ? 'claude-sonnet-4.5' : 'gemini-2.5-pro',
    inputTokens: 1_200,
    outputTokens: 240,
    cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0,
    cachedInputTokens: 0,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0,
    tools: [],
    bashCommands: [],
    timestamp: '2026-09-06T14:00:00.000Z',
    speed: 'standard',
    deduplicationKey: `${provider}:${sessionId}:turn-1`,
    userMessage: `native ${provider} turn`,
    sessionId,
  }
}

function nativeProvider(
  name: string,
  sources: readonly SessionSource[],
  parsedByPath: ReadonlyMap<string, readonly ParsedProviderCall[] | Error>,
): Provider {
  return {
    name,
    displayName: name,
    modelDisplayName: (model) => model,
    toolDisplayName: (tool) => tool,
    discoverSessions: async () => [...sources],
    createSessionParser(sessionSource) {
      return {
        async *parse(): AsyncGenerator<ParsedProviderCall> {
          const parsed = parsedByPath.get(sessionSource.path)
          if (parsed instanceof Error) throw parsed
          yield* parsed ?? []
        },
      }
    },
  }
}

function descriptors(...ids: string[]) {
  return ids.map((id) => {
    const descriptor = descriptorFor(id)
    if (descriptor === undefined) throw new Error(`missing descriptor ${id}`)
    return descriptor
  })
}

function unit(harnessId: string, name: string, parsed: ParsedProviderCall[]): NativeUnit {
  const session = source(harnessId, name)
  return {
    harnessId,
    sourceKey: `${harnessId}:${name}`,
    source: session,
    status: 'new',
    revision: {
      fingerprint: { dev: 1, ino: 1, mtimeMs: 1, sizeBytes: 1 },
      token: '1:1:1:1',
    },
    envelopes: parsed.map((entry) => ({
      harnessId,
      sourceKey: `${harnessId}:${name}`,
      source: session,
      nativeSessionId: entry.sessionId,
      timestamp: entry.timestamp,
      call: entry,
      revisionToken: '1:1:1:1',
    })),
    problems: [],
  }
}

describe('refreshHarnessSources', () => {
  it('persists split harness jobs, records a parse failure, and still derives the healthy harness', async () => {
    const store = temporaryStore()
    try {
      const antigravitySource = source('antigravity', 'conversation')
      const geminiSource = source('gemini', 'quota-chat')
      const brokenSource = source('pi', 'corrupt')
      const parseFailure = Object.assign(new SyntaxError('unexpected end of JSON'), { file: brokenSource.path })

      const report = await refreshHarnessSources(store, {
        getAllProviders: async () => [
          nativeProvider('antigravity', [antigravitySource], new Map([
            [antigravitySource.path, [call('antigravity', 'agy-session')]],
          ])),
          nativeProvider('gemini', [geminiSource], new Map([
            [geminiSource.path, [call('gemini', 'gemini-session')]],
          ])),
          nativeProvider('pi', [brokenSource], new Map([
            [brokenSource.path, parseFailure],
          ])),
        ],
        descriptors: descriptors('antigravity', 'pi', 'codex-cli'),
        jobConcurrency: 2,
        writerCapacity: 2,
        commandStartedAt: new Date('2026-09-12T00:00:00.000Z'),
        parseAllSessions: async () => undefined,
      })

      expect(report.rows.map((row) => row.harnessId)).toEqual(['antigravity', 'pi', 'codex-cli'])
      expect(report.rows.find((row) => row.harnessId === 'codex-cli')?.status).toBe('unavailable')
      expect(report.rows.find((row) => row.harnessId === 'pi')?.status).toBe('failed')
      expect(report.rows.find((row) => row.harnessId === 'antigravity')?.status).toBe('ok')
      expect(report.exitCode).toBe(1)

      expect(store.listAll().map((record) => record.harness).sort()).toEqual(['antigravity'])
      expect(store.listAll().some((record) => record.harness === 'gemini')).toBe(false)
      expect(store.getProblems().some((problem) => problem.code === PROVIDER_PARSE_ERROR)).toBe(true)
      expect(store.listSessions().map((session) => session.harness)).toEqual(['antigravity'])
      expect(store.listRuns().map((run) => run.harness)).toEqual(['antigravity'])
      expect(store.getHarnessRollup('antigravity')).toMatchObject({ harness: 'antigravity', sampleCount: 1 })
      expect(store.getHarnessRollup('gemini')).toBeUndefined()
      expect(formatRefreshReport(report)).not.toMatch(/\/native\//)
    } finally {
      store.close()
    }
  })

  it('does not cancel a healthy job when a sibling harness fails', async () => {
    const store = temporaryStore()
    const started: string[] = []
    try {
      const report = await refreshHarnessSources(store, {
        getAllProviders: async () => [],
        descriptors: descriptors('antigravity', 'pi'),
        jobConcurrency: 2,
        commandStartedAt: new Date('2026-09-12T00:00:00.000Z'),
        parseAllSessions: async () => undefined,
        iterateNativeUnits: async (harnessId) => {
          started.push(harnessId)
          if (harnessId === 'pi') {
            await new Promise((resolve) => setTimeout(resolve, 15))
            throw new Error('pi unreadable')
          }
          await new Promise((resolve) => setTimeout(resolve, 40))
          return [unit('antigravity', 'conversation', [call('antigravity', 'agy-session')])]
        },
      })
      expect(started.sort()).toEqual(['antigravity', 'pi'])
      expect(report.rows.find((row) => row.harnessId === 'antigravity')?.status).toBe('ok')
      expect(report.rows.find((row) => row.harnessId === 'pi')?.status).toBe('failed')
      expect(store.listAll()).toHaveLength(1)
    } finally {
      store.close()
    }
  })

  it('counts an unchanged rerun as zero new writes', async () => {
    const store = temporaryStore()
    let passes = 0
    try {
      const dependencies = {
        getAllProviders: async () => [],
        descriptors: descriptors('pi'),
        jobConcurrency: 1,
        commandStartedAt: new Date('2026-09-12T00:00:00.000Z'),
        parseAllSessions: async () => undefined,
        iterateNativeUnits: async (): Promise<NativeUnit[]> => {
          passes += 1
          if (passes === 1) return [unit('pi', 'session', [call('pi', 'pi-session')])]
          return [{
            ...unit('pi', 'session', [call('pi', 'pi-session')]),
            status: 'unchanged' as const,
            envelopes: [],
          }]
        },
      }
      const first = await refreshHarnessSources(store, dependencies)
      const second = await refreshHarnessSources(store, dependencies)
      expect(first.rows[0]).toMatchObject({ status: 'ok', created: 1, updated: 0 })
      expect(second.rows[0]).toMatchObject({ status: 'unchanged', created: 0, updated: 0 })
      expect(store.listAll()).toHaveLength(1)
    } finally {
      store.close()
    }
  })

  it('does not count already-covered records as Updated when --history-weeks expands', async () => {
    const store = temporaryStore()
    const started = new Date('2026-09-12T18:00:00.000Z')
    let historyWeeks = 2
    const recent = {
      ...call('pi', 'pi-cross'),
      timestamp: '2026-09-06T10:00:00.000Z',
      deduplicationKey: 'pi:pi-cross:recent',
      userMessage: 'recent pi turn',
    }
    const older = [
      {
        ...call('pi', 'pi-cross'),
        timestamp: '2026-08-10T12:00:00.000Z',
        deduplicationKey: 'pi:pi-cross:older-1',
        userMessage: 'older pi turn 1',
      },
      {
        ...call('pi', 'pi-cross'),
        timestamp: '2026-08-20T12:00:00.000Z',
        deduplicationKey: 'pi:pi-cross:older-2',
        userMessage: 'older pi turn 2',
      },
    ]
    try {
      const dependencies = {
        getAllProviders: async () => [],
        descriptors: descriptors('pi'),
        jobConcurrency: 1,
        commandStartedAt: started,
        parseAllSessions: async () => undefined,
        iterateNativeUnits: async (): Promise<NativeUnit[]> => {
          const parsed = historyWeeks === 2 ? [recent] : [...older, recent]
          return [unit('pi', 'session', parsed)]
        },
      }
      const first = await refreshHarnessSources(store, dependencies, { historyWeeks: 2 })
      expect(first.rows[0]).toMatchObject({ created: 1, updated: 0 })
      const revision = store.listSourceCheckpoints('pi')[0]?.revisionToken
      historyWeeks = 6
      const second = await refreshHarnessSources(store, dependencies, { historyWeeks: 6 })
      expect(second.historyWeeks).toBe(6)
      expect(second.rows[0]).toMatchObject({ created: 2, updated: 0 })
      expect(store.listAll()).toHaveLength(3)
      const checkpoint = store.listSourceCheckpoints('pi')[0]
      expect(checkpoint?.revisionToken).toBe(revision)
      expect(checkpoint?.coveredFromUtc).toBe(new Date(started.getTime() - 6 * 7 * 24 * 60 * 60 * 1000).toISOString())
    } finally {
      store.close()
    }
  })
})

describe('refreshHarnessSources — write volume', () => {
  it('does not rewrite the OTLP side once per source', async () => {
    const store = temporaryStore()
    try {
      store.upsertMany([
        {
          spanId: 'aa11bb22cc33dd44',
          traceId: 'ff00ee11dd22cc33bb44aa5566778899',
          parentSpanId: null,
          source: 'gemini',
          harness: 'cursor',
          name: 'cursor:gpt-5',
          op: 'llm.invoke',
          kind: 'internal',
          timestamp: '2026-09-06T13:00:00.000Z',
          durationMs: 10,
          status: 'ok',
          tokens: {
            freshInput: 5,
            cacheRead: 0,
            cacheCreation: 0,
            output: 5,
            reportedInput: 5,
            reportedOutput: 5,
          },
          content: {},
          cost: { basis: 'unknown', status: 'no_rate' },
          raw: { 'session.id': 'untouched-otlp-session' },
        },
      ])

      const untouchedBefore = store.storedRawBytes('aa11bb22cc33dd44')
      const sources = [source('pi', 'a'), source('pi', 'b'), source('pi', 'c')]
      const parsed = new Map(
        sources.map((entry, index) => [entry.path, [call('pi', `pi-session-${index}`)]] as const),
      )

      const report = await refreshHarnessSources(store, {
        getAllProviders: async () => [nativeProvider('pi', sources, parsed)],
        descriptors: descriptors('pi'),
        jobConcurrency: 1,
        commandStartedAt: new Date('2026-09-12T00:00:00.000Z'),
        parseAllSessions: async () => undefined,
      })

      expect(report.rows[0]?.units).toBe(3)
      expect(store.storedRawBytes('aa11bb22cc33dd44')).toBe(untouchedBefore)
      expect(store.listOtlpSourced()).toHaveLength(1)
      expect(store.listAll()).toHaveLength(4)
    } finally {
      store.close()
    }
  })

  it('derives ONE run for a session both paths describe, not one per path', async () => {
    const store = temporaryStore()
    try {
      store.upsertMany([
        {
          spanId: 'cc33dd44ee55ff66',
          traceId: 'bb00cc11dd22ee33ff44005566778899',
          parentSpanId: null,
          source: 'claude-code-desktop',
          harness: 'claude-cli',
          sessionId: 'both-paths-session',
          name: 'claude-cli:claude-opus-5',
          op: 'llm.invoke',
          kind: 'internal',
          timestamp: '2026-09-06T14:00:00.000Z',
          durationMs: 10,
          status: 'ok',
          tokens: {
            freshInput: 1_200,
            cacheRead: 0,
            cacheCreation: 0,
            output: 240,
            reportedInput: 1_200,
            reportedOutput: 240,
          },
          content: {},
          cost: { basis: 'unknown', status: 'no_rate' },
          raw: { 'session.id': 'both-paths-session' },
        },
      ])

      const sources = [source('claude', 'both')]
      const provider = nativeProvider(
        'claude',
        sources,
        new Map([[
          sources[0]!.path,
          [0, 1, 2].map((turn) => ({
            ...call('claude', 'both-paths-session'),
            provider: 'claude-cli',
            deduplicationKey: `claude-cli:both-paths-session:turn-${turn}`,
            turnId: `turn-${turn}`,
          })),
        ]]),
      )

      await refreshHarnessSources(store, {
        getAllProviders: async () => [provider],
        descriptors: descriptors('claude-cli'),
        jobConcurrency: 1,
        commandStartedAt: new Date('2026-09-12T00:00:00.000Z'),
        parseAllSessions: async () => undefined,
        peekEvidence: async () => ({ entrypoint: 'cli' }),
      })

      expect(store.listAll().length).toBeGreaterThan(1)
      expect(store.listRuns('claude-cli')).toHaveLength(1)
      expect(store.listRuns()).toHaveLength(1)
    } finally {
      store.close()
    }
  })

  it('still collapses an OTLP session that a discovered source also describes', async () => {
    const store = temporaryStore()
    try {
      store.upsertMany([
        {
          spanId: 'bb22cc33dd44ee55',
          traceId: 'aa00bb11cc22dd33ee44ff5566778899',
          parentSpanId: null,
          source: 'claude-code-desktop',
          harness: 'claude-cli',
          sessionId: 'shared-session',
          name: 'claude-cli:claude-opus-5',
          op: 'llm.invoke',
          kind: 'internal',
          timestamp: '2026-09-06T14:00:00.000Z',
          durationMs: 10,
          status: 'ok',
          tokens: {
            freshInput: 1_200,
            cacheRead: 0,
            cacheCreation: 0,
            output: 240,
            reportedInput: 1_200,
            reportedOutput: 240,
          },
          content: {},
          cost: { basis: 'unknown', status: 'no_rate' },
          raw: { 'session.id': 'shared-session', turnId: 'turn-1' },
        },
      ])

      const sources = [source('claude', 'shared')]
      const provider = nativeProvider(
        'claude',
        sources,
        new Map([[sources[0]!.path, [{
          ...call('claude', 'shared-session'),
          provider: 'claude-cli',
          turnId: 'turn-1',
          deduplicationKey: 'claude-cli:shared-session:turn-1',
        }]]]),
      )

      await refreshHarnessSources(store, {
        getAllProviders: async () => [provider],
        descriptors: descriptors('claude-cli'),
        jobConcurrency: 1,
        commandStartedAt: new Date('2026-09-12T00:00:00.000Z'),
        parseAllSessions: async () => undefined,
        peekEvidence: async () => ({ entrypoint: 'cli' }),
      })

      const all = store.listAll()
      expect(all).toHaveLength(1)
      expect(all[0]!.spanId).toBe('bb22cc33dd44ee55')
    } finally {
      store.close()
    }
  })
})
