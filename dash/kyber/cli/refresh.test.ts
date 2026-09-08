// End-to-end contract for the local-provider refresh lifecycle. The command
// must turn native provider sources into the same durable corpus that the
// dashboard reads; a parser that merely returns calls is not a refresh.

import { afterEach, describe, expect, it } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import type { ParsedProviderCall, Provider, SessionSource } from '../synth/provider.js'
import { PROVIDER_PARSE_ERROR } from '../synth/provider.js'
import { CanonStore } from '../canon/store.js'
import { refreshLocalProviders } from './refresh.js'

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
    project: 'kyber-weave',
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

describe('refreshLocalProviders', () => {
  it('persists discovered native calls, records a provider parse failure, then derives sessions, runs, executions, and rollups', async () => {
    const store = temporaryStore()
    try {
      const antigravitySource = source('antigravity', 'conversation')
      const geminiSource = source('gemini', 'quota-chat')
      const brokenSource = source('pi', 'corrupt')
      const parseFailure = Object.assign(new SyntaxError('unexpected end of JSON'), { file: brokenSource.path })

      const report = await refreshLocalProviders(store, {
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
      })

      // Discovery and synthesis have durable results, not just a parsed-call
      // array that disappears when the CLI exits.
      expect(report).toMatchObject({
        providers: 3,
        sources: 3,
        synthesized: 2,
        accepted: 2,
        problems: 1,
        sessions: { built: 2, skipped: 0, pruned: 0 },
      })
      expect(store.listAll()).toHaveLength(2)
      expect(store.listAll().map((record) => record.source).sort()).toEqual([
        'codeburn/antigravity',
        'codeburn/gemini',
      ])

      // A native Antigravity directory is an Antigravity harness even if it
      // used a Claude model. A generic Gemini source remains Gemini; it is
      // never silently relabelled as Antigravity.
      expect(store.listAll().map((record) => [record.harness, record.name]).sort()).toEqual([
        ['antigravity', 'antigravity:claude-sonnet-4.5'],
        ['gemini', 'gemini:gemini-2.5-pro'],
      ])

      // Provider-local failures are visible but do not prevent healthy
      // providers from reaching every dashboard-derived table.
      expect(store.getProblems()).toEqual([
        expect.objectContaining({
          spanId: `provider:pi:${brokenSource.path}`,
          code: PROVIDER_PARSE_ERROR,
          location: brokenSource.path,
        }),
      ])
      expect(store.listSessions().map((session) => session.harness).sort()).toEqual(['antigravity', 'gemini'])
      expect(store.listRuns().map((run) => run.harness).sort()).toEqual(['antigravity', 'gemini'])
      expect(store.listExecutions().map((execution) => execution.harness).sort()).toEqual(['antigravity', 'gemini'])
      expect(store.getHarnessRollup('antigravity')).toMatchObject({ harness: 'antigravity', sampleCount: 1 })
      expect(store.getHarnessRollup('gemini')).toMatchObject({ harness: 'gemini', sampleCount: 1 })
      expect(report.rollups).toBe(store.harnessRollupCount())
    } finally {
      store.close()
    }
  })
})
