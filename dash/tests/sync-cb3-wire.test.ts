/**
 * CB-3 wire tests (Teams boundary spec section 7): the additive usage-span
 * attributes and the codeburn.coverage_through batch marker.
 *
 * Golden-pins the exact attribute set for a fully-proven span plus every
 * absent-when-unproven case (no lineage, unresolvable lineage, undecidable
 * subscription, zero cache tokens, untrusted watermark value).
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { mkdtemp, rm } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import {
  buildOtlpPayload,
  deriveTraceId,
  type CallWithSession,
  type OtlpAttribute,
} from '../src/sync/otlp.js'
import type { ParsedApiCall, ProjectSummary, TokenUsage } from '../src/types.js'
import type { PlanMap } from '../src/config.js'

// ── Helpers ───────────────────────────────────────────────────────────

function makeUsage(overrides?: Partial<TokenUsage>): TokenUsage {
  return {
    inputTokens: 1000,
    outputTokens: 500,
    cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0,
    cachedInputTokens: 0,
    reasoningTokens: 0,
    webSearchRequests: 0,
    ...overrides,
  }
}

function makeCall(overrides?: Partial<ParsedApiCall>): ParsedApiCall {
  return {
    provider: 'claude',
    model: 'claude-sonnet-4-6',
    usage: makeUsage(),
    costUSD: 0.05,
    tools: [],
    mcpTools: [],
    skills: [],
    subagentTypes: [],
    hasAgentSpawn: false,
    hasPlanMode: false,
    speed: 'standard',
    timestamp: '2026-07-10T10:00:00.000Z',
    bashCommands: [],
    deduplicationKey: 'cb3:key:1',
    ...overrides,
  }
}

function spanAttrs(payload: ReturnType<typeof buildOtlpPayload>, index = 0): OtlpAttribute[] {
  return payload.resourceSpans[0]!.scopeSpans[0]!.spans[index]!.attributes
}

function attrKeys(attrs: OtlpAttribute[]): string[] {
  return attrs.map(a => a.key)
}

// ── buildOtlpPayload: CB-3 attributes ────────────────────────────────

describe('buildOtlpPayload CB-3 attributes', () => {
  // GOLDEN - the exact attribute set (and order) of a fully-proven span.
  // Add-only contract: receivers ignoring unknown keys must lose nothing, so
  // new keys append after the v1 set and none of the v1 keys move.
  it('golden: fully-proven span carries the exact CB-3 attribute set', () => {
    const cws: CallWithSession = {
      call: makeCall({
        usage: makeUsage({ cacheReadInputTokens: 800, cacheCreationInputTokens: 200 }),
      }),
      sessionId: 'root-session',
      project: 'my-project',
      workingDirectory: '/workspace/my-project',
      session: {
        callCount: 3,
        durationMs: 61_000,
        workUnitId: deriveTraceId('root-session'),
        sessionRole: 'root',
        subscriptionCovered: true,
      },
    }
    const payload = buildOtlpPayload([cws], { coverageThrough: '2026-08-24' })

    expect(payload.resourceSpans[0]!.resource.attributes).toEqual([
      { key: 'codeburn.device_id', value: { stringValue: expect.stringMatching(/^[0-9a-f]{16}$/) } },
      { key: 'codeburn.coverage_through', value: { stringValue: '2026-08-24' } },
    ])

    expect(spanAttrs(payload)).toEqual([
      { key: 'ai.provider', value: { stringValue: 'claude' } },
      { key: 'ai.model', value: { stringValue: 'claude-sonnet-4-6' } },
      { key: 'ai.input_tokens', value: { intValue: '1000' } },
      // claude bills reasoning inside output, so billable output stays 500
      { key: 'ai.output_tokens', value: { intValue: '500' } },
      { key: 'ai.cost_usd', value: { doubleValue: 0.05 } },
      { key: 'ai.speed', value: { stringValue: 'standard' } },
      { key: 'ai.project', value: { stringValue: 'my-project' } },
      { key: 'ai.cost_estimated', value: { boolValue: false } },
      { key: 'ai.work_unit_id', value: { stringValue: deriveTraceId('root-session') } },
      { key: 'ai.session_role', value: { stringValue: 'root' } },
      { key: 'ai.lineage_evidence', value: { stringValue: 'provider-recorded' } },
      { key: 'ai.cache_read_tokens', value: { intValue: '800' } },
      { key: 'ai.cache_write_tokens', value: { intValue: '200' } },
      { key: 'ai.call_count', value: { intValue: '3' } },
      { key: 'ai.session_duration_ms', value: { intValue: '61000' } },
      { key: 'ai.subscription_covered', value: { boolValue: true } },
    ])
  })

  it('no session context: none of the session-level attributes appear', () => {
    const payload = buildOtlpPayload([{ call: makeCall(), sessionId: 's1' }])
    const keys = attrKeys(spanAttrs(payload))
    for (const key of [
      'ai.work_unit_id', 'ai.session_role', 'ai.lineage_evidence',
      'ai.call_count', 'ai.session_duration_ms', 'ai.subscription_covered',
      'ai.cache_read_tokens', 'ai.cache_write_tokens',
    ]) {
      expect(keys).not.toContain(key)
    }
  })

  it('lineage trio falls together when any value fails sanitization', () => {
    const payload = buildOtlpPayload([{
      call: makeCall(),
      sessionId: 's1',
      session: { callCount: 1, workUnitId: 'bad id with spaces', sessionRole: 'root' },
    }])
    const keys = attrKeys(spanAttrs(payload))
    expect(keys).not.toContain('ai.work_unit_id')
    expect(keys).not.toContain('ai.session_role')
    expect(keys).not.toContain('ai.lineage_evidence')
    // Unrelated proven fields still emit
    expect(keys).toContain('ai.call_count')
  })

  it('cache_read uses the billable-consistent max of both vocabularies', () => {
    const payload = buildOtlpPayload([{
      call: makeCall({ usage: makeUsage({ cacheReadInputTokens: 800, cachedInputTokens: 900 }) }),
      sessionId: 's1',
    }])
    expect(spanAttrs(payload)).toContainEqual({ key: 'ai.cache_read_tokens', value: { intValue: '900' } })
  })

  it('zero cache tokens are absent, not sent as zero', () => {
    const payload = buildOtlpPayload([{ call: makeCall(), sessionId: 's1' }])
    const keys = attrKeys(spanAttrs(payload))
    expect(keys).not.toContain('ai.cache_read_tokens')
    expect(keys).not.toContain('ai.cache_write_tokens')
  })

  it('coverage_through omitted without the option and on an unsafe value', () => {
    const without = buildOtlpPayload([{ call: makeCall(), sessionId: 's1' }])
    expect(attrKeys(without.resourceSpans[0]!.resource.attributes)).toEqual(['codeburn.device_id'])

    const unsafe = buildOtlpPayload([{ call: makeCall(), sessionId: 's1' }], { coverageThrough: '/etc/passwd' })
    expect(attrKeys(unsafe.resourceSpans[0]!.resource.attributes)).toEqual(['codeburn.device_id'])
  })

  it('identity is unchanged: traceId/spanId ignore every new field', () => {
    const bare = buildOtlpPayload([{ call: makeCall(), sessionId: 's1' }])
    const full = buildOtlpPayload([{
      call: makeCall(),
      sessionId: 's1',
      session: {
        callCount: 9,
        durationMs: 1000,
        workUnitId: deriveTraceId('s1'),
        sessionRole: 'root',
        subscriptionCovered: false,
      },
    }])
    const bareSpan = bare.resourceSpans[0]!.scopeSpans[0]!.spans[0]!
    const fullSpan = full.resourceSpans[0]!.scopeSpans[0]!.spans[0]!
    expect(fullSpan.traceId).toBe(bareSpan.traceId)
    expect(fullSpan.spanId).toBe(bareSpan.spanId)
  })
})

// ── collectUnsentCalls: session wire context ─────────────────────────

let tmpDir: string
const originalHome = process.env.HOME

beforeEach(async () => {
  tmpDir = await mkdtemp(join(tmpdir(), 'codeburn-cb3-'))
  process.env.HOME = tmpDir
  process.env.XDG_CACHE_HOME = join(tmpDir, '.cache')
})

afterEach(async () => {
  process.env.HOME = originalHome
  await rm(tmpDir, { recursive: true, force: true })
})

function makeSession(overrides: Record<string, unknown>): ProjectSummary['sessions'][number] {
  return {
    sessionId: 'sess-1',
    project: 'proj',
    firstTimestamp: '2026-07-10T10:00:00.000Z',
    lastTimestamp: '2026-07-10T10:05:00.000Z',
    turns: [{ assistantCalls: [makeCall({ deduplicationKey: `cb3:${Math.random()}` })], timestamp: '2026-07-10T10:00:00.000Z' }],
    ...overrides,
  } as unknown as ProjectSummary['sessions'][number]
}

function makeProject(sessions: ProjectSummary['sessions'][number][]): ProjectSummary[] {
  return [{ project: 'proj', sessions }] as unknown as ProjectSummary[]
}

async function collect(projects: ProjectSummary[], plans?: PlanMap) {
  const { collectUnsentCalls } = await import('../src/sync/push.js')
  return collectUnsentCalls(projects, Date.now(), plans ? { plans } : undefined)
}

describe('collectUnsentCalls session wire context', () => {
  it('root lineage: workUnitId is deriveTraceId of the session itself', async () => {
    const { allCalls } = await collect(makeProject([
      makeSession({ sessionId: 'root-1', lineage: { role: 'root', evidence: 'provider-recorded' } }),
    ]))
    expect(allCalls[0]!.session).toMatchObject({
      workUnitId: deriveTraceId('root-1'),
      sessionRole: 'root',
    })
  })

  it('child lineage with in-range parent folds under the parent work unit', async () => {
    const { allCalls } = await collect(makeProject([
      makeSession({ sessionId: 'root-1', lineage: { role: 'root', evidence: 'provider-recorded' } }),
      makeSession({ sessionId: 'child-1', lineage: { role: 'child', evidence: 'provider-recorded', parentSessionId: 'root-1' } }),
    ]))
    const child = allCalls.find(c => c.sessionId === 'child-1')!
    expect(child.session).toMatchObject({ workUnitId: deriveTraceId('root-1'), sessionRole: 'child' })
  })

  it('child lineage naming an out-of-range parent emits nothing (fail closed)', async () => {
    const { allCalls } = await collect(makeProject([
      makeSession({ sessionId: 'child-1', lineage: { role: 'child', evidence: 'provider-recorded', parentSessionId: 'gone' } }),
    ]))
    expect(allCalls[0]!.session?.workUnitId).toBeUndefined()
    expect(allCalls[0]!.session?.sessionRole).toBeUndefined()
  })

  it('no lineage: none of the three lineage fields', async () => {
    const { allCalls } = await collect(makeProject([makeSession({})]))
    expect(allCalls[0]!.session?.workUnitId).toBeUndefined()
    expect(allCalls[0]!.session?.sessionRole).toBeUndefined()
  })

  it('call_count counts every span the session contributes', async () => {
    const session = makeSession({
      turns: [
        { assistantCalls: [makeCall({ deduplicationKey: 'cb3:a' }), makeCall({ deduplicationKey: 'cb3:b' })], timestamp: '2026-07-10T10:00:00.000Z' },
        { assistantCalls: [makeCall({ deduplicationKey: 'cb3:c' })], timestamp: '2026-07-10T10:01:00.000Z' },
      ],
    })
    const { allCalls } = await collect(makeProject([session]))
    expect(allCalls).toHaveLength(3)
    for (const c of allCalls) expect(c.session?.callCount).toBe(3)
  })

  it('duration derives from provider-recorded first/last timestamps', async () => {
    const { allCalls } = await collect(makeProject([makeSession({})]))
    expect(allCalls[0]!.session?.durationMs).toBe(5 * 60 * 1000)
  })

  it('duration omitted when timestamps are missing or unordered', async () => {
    const missing = await collect(makeProject([makeSession({ firstTimestamp: '', lastTimestamp: '' })]))
    expect(missing.allCalls[0]!.session?.durationMs).toBeUndefined()

    const reversed = await collect(makeProject([
      makeSession({ firstTimestamp: '2026-07-10T10:05:00.000Z', lastTimestamp: '2026-07-10T10:00:00.000Z' }),
    ]))
    expect(reversed.allCalls[0]!.session?.durationMs).toBeUndefined()
  })

  it('subscription_covered: plan for the call provider decides true', async () => {
    const plans: PlanMap = {
      claude: { id: 'claude-pro', monthlyUsd: 20, provider: 'claude', setAt: '2026-07-01T00:00:00Z' },
    }
    const { allCalls } = await collect(makeProject([makeSession({})]), plans)
    expect(allCalls[0]!.session?.subscriptionCovered).toBe(true)
  })

  it('subscription_covered: no plan, unproxied cwd decides false', async () => {
    const { allCalls } = await collect(makeProject([makeSession({ workingDirectory: '/workspace/proj' })]))
    expect(allCalls[0]!.session?.subscriptionCovered).toBe(false)
  })

  it('subscription_covered: no plan and no cwd omits the attribute', async () => {
    const { allCalls } = await collect(makeProject([makeSession({})]))
    expect(allCalls[0]!.session?.subscriptionCovered).toBeUndefined()
  })

  it("subscription_covered: a plan recorded as 'none' is not coverage", async () => {
    const plans: PlanMap = {
      claude: { id: 'none', monthlyUsd: 0, provider: 'claude', setAt: '2026-07-01T00:00:00Z' },
    }
    const { allCalls } = await collect(makeProject([makeSession({ workingDirectory: '/workspace/proj' })]), plans)
    expect(allCalls[0]!.session?.subscriptionCovered).toBe(false)
  })
})
