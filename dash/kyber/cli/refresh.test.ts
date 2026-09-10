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

describe('refreshLocalProviders — write volume', () => {
  // Regression: the loop handed every OTLP record to `deduplicate` for each
  // source. `deduplicate` returns the complete merged corpus, including the
  // OTLP rows no file record touched, so each source re-upserted the entire
  // OTLP side — measured at 23,695 rows rewritten to ingest one 28-record
  // transcript, ~52M upserts across a real machine's ~2,200 sources. Each of
  // those writes recompresses its payload, which is what made a full refresh
  // take hours. Writes must stay proportional to what was actually parsed.
  it('does not rewrite the OTLP side once per source', async () => {
    const store = temporaryStore()
    try {
      // An OTLP-sourced record sharing no session with anything discovered.
      store.upsertMany([
        {
          spanId: 'aa11bb22cc33dd44',
          traceId: 'ff00ee11dd22cc33bb44aa5566778899',
          parentSpanId: null,
          source: 'gemini',
          harness: 'gemini',
          name: 'gemini:gemini-2.5-pro',
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

      // A provider with no registered content reader, so the fixture's
      // non-existent paths do not turn into read failures.
      const sources = [source('gemini', 'a'), source('gemini', 'b'), source('gemini', 'c')]
      const parsed = new Map(
        sources.map((s, i) => [s.path, [call('gemini', `gemini-session-${i}`)]] as const),
      )
      const provider = nativeProvider('gemini', sources, parsed)

      const report = await refreshLocalProviders(store, {
        getAllProviders: async () => [provider],
      })

      expect(report.sources).toBe(3)
      expect(report.accepted).toBe(3)

      // The unrelated OTLP row is still there, untouched — not rewritten three
      // times over, once per discovered source.
      expect(store.storedRawBytes('aa11bb22cc33dd44')).toBe(untouchedBefore)
      const otlp = store.listOtlpSourced()
      expect(otlp).toHaveLength(1)
      expect(otlp[0]!.spanId).toBe('aa11bb22cc33dd44')

      // And the parsed calls did land.
      expect(store.listAll()).toHaveLength(4)
    } finally {
      store.close()
    }
  })

  // The user-visible symptom of the cross-path bug: the Claude Code harness
  // listed two runs for one conversation, one per ingest path. Two separate
  // defects produced it — the dedup key spelled the harness differently on each
  // side, and a synthesized record carried no `sessionId`, so `sessionKeys()`
  // fell back to the namespaced trace id and grouped the two paths apart even
  // once their records had been matched.
  it('derives ONE run for a session both paths describe, not one per path', async () => {
    const store = temporaryStore()
    try {
      store.upsertMany([
        {
          spanId: 'cc33dd44ee55ff66',
          traceId: 'bb00cc11dd22ee33ff44005566778899',
          parentSpanId: null,
          source: 'claude-code-desktop',
          harness: 'claude-code',
          sessionId: 'both-paths-session',
          name: 'claude-code:claude-opus-5',
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

      // The file path sees more turns than the collector caught — the ordinary
      // case when the collector was started mid-session. The extra turns must
      // still land in the same run.
      const sources = [source('claude', 'both')]
      const provider = nativeProvider(
        'claude',
        sources,
        new Map([[
          sources[0]!.path,
          [0, 1, 2].map((turn) => ({
            ...call('claude', 'both-paths-session'),
            deduplicationKey: `claude:both-paths-session:turn-${turn}`,
          })),
        ]]),
      )

      await refreshLocalProviders(store, { getAllProviders: async () => [provider] })

      // Three distinct file turns against the collector's one, so the run
      // genuinely spans records from both paths rather than collapsing to one.
      expect(store.listAll().length).toBeGreaterThan(1)
      expect(store.listRuns('claude-code')).toHaveLength(1)
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
          harness: 'claude-code',
          name: 'claude-code:claude-opus-5',
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
          // The file path will call this provider `claude`; the OTLP path
          // votes `claude-code`. The collapse must still find them.
          raw: { 'session.id': 'shared-session' },
        },
      ])

      const sources = [source('claude', 'shared')]
      const provider = nativeProvider(
        'claude',
        sources,
        new Map([[sources[0]!.path, [call('claude', 'shared-session')]]]),
      )

      await refreshLocalProviders(store, { getAllProviders: async () => [provider] })

      // One turn, not two: the two paths describe the same work.
      const all = store.listAll()
      expect(all).toHaveLength(1)
      expect(all[0]!.spanId).toBe('bb22cc33dd44ee55')
    } finally {
      store.close()
    }
  })
})
