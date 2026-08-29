// Regression suite for #1075 (reported by chr-evensen).
//
// Two independent codex pricing bugs, each with the site that would silently
// drift from its twin if only one half were reverted:
//
//   A. reasoning_output_tokens is a SUBSET of output_tokens (OpenAI bills
//      reasoning as part of output; every token_count event in a 134k-event
//      corpus satisfies input + output == total), but codeburn added the two.
//      Priced in TWO places -- the fresh parse in src/providers/codex.ts and
//      the cache-rehydration re-price in src/parser.ts -- plus three display
//      sums. Both cost sites now go through billableOutputTokens(). The
//      cache-rehydration half lives in codex-pricing-1075-rehydrate.test.ts,
//      which needs CODEX_HOME set before the provider module is evaluated.
//
//   B. cache_write_input_tokens was never read. It is now carved out of the
//      uncached-input bucket, but ONLY on models whose pricing source carries
//      an explicit cache-write rate: buildCosts() fabricates 1.25x input when
//      the source omits one, which is right for Anthropic but would invent a
//      surcharge OpenAI never charged on every pre-5.6 model.

import { mkdir, mkdtemp, readFile, rm, writeFile } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { aggregateAudit } from '../src/audit-report.js'
import { aggregateModels } from '../src/models-report.js'
import { clearCodexMemCaches, readCachedCodexResults } from '../src/codex-cache.js'
import { currentTzKey, ensureCacheHydrated, toDateString, type DailyEntry } from '../src/daily-cache.js'
import { createCodexProvider } from '../src/providers/codex.js'
import type { ParsedProviderCall } from '../src/providers/types.js'
import type {
  ClassifiedTurn,
  ParsedApiCall,
  ProjectSummary,
  SessionSummary,
  TaskCategory,
  TokenUsage,
} from '../src/types.js'

// Snapshot ground truth (src/data/litellm-snapshot.json), USD per token:
//   gpt-5.6-terra  input 2e-6   output 12e-6  cacheWrite 2.5e-6 (EXPLICIT)  cacheRead 2e-7
//   gpt-5.5        input 5e-6   output 30e-6  cacheWrite null (fabricated)  cacheRead 5e-7
const TERRA = { input: 2e-6, output: 12e-6, cacheWrite: 2.5e-6, cacheRead: 2e-7 }
const GPT55 = { input: 5e-6, output: 30e-6, cacheRead: 5e-7 }

let tmpDir: string
beforeEach(async () => { tmpDir = await mkdtemp(join(tmpdir(), 'codex-1075-')) })
afterEach(async () => { await rm(tmpDir, { recursive: true, force: true }) })

type Usage = {
  input_tokens: number
  cached_input_tokens?: number
  cache_write_input_tokens?: number
  output_tokens: number
  reasoning_output_tokens?: number
}

async function parseOneEvent(model: string, usage: Usage): Promise<ParsedProviderCall> {
  const total = usage.input_tokens + usage.output_tokens
  const sessionDir = join(tmpDir, 'sessions', '2026', '08', '16')
  await mkdir(sessionDir, { recursive: true })
  const filePath = join(sessionDir, `rollout-${model}-${Math.random().toString(36).slice(2)}.jsonl`)
  await writeFile(filePath, [
    JSON.stringify({
      type: 'session_meta',
      timestamp: '2026-08-16T10:00:00Z',
      payload: { cwd: '/Users/t/p', originator: 'codex-cli', session_id: 's1075', model },
    }),
    JSON.stringify({
      type: 'event_msg',
      timestamp: '2026-08-16T10:01:00Z',
      payload: {
        type: 'token_count',
        info: { model, last_token_usage: { ...usage, total_tokens: total }, total_token_usage: { ...usage, total_tokens: total } },
      },
    }),
  ].join('\n') + '\n')

  const provider = createCodexProvider(tmpDir)
  const parser = provider.createSessionParser({ path: filePath, project: 'test', provider: 'codex' }, new Set())
  const calls: ParsedProviderCall[] = []
  for await (const call of parser.parse()) calls.push(call)
  expect(calls).toHaveLength(1)
  return calls[0]!
}

// ── Fix A: reasoning is already inside output ─────────────────────────────

describe('#1075 A - reasoning is not billed on top of output', () => {
  it('prices a fresh codex parse from output_tokens alone', async () => {
    const call = await parseOneEvent('gpt-5.5', {
      input_tokens: 1000,
      cached_input_tokens: 200,
      output_tokens: 1000,
      reasoning_output_tokens: 400,
    })

    // 800 uncached input + 200 cached + 1000 output. The 400 reasoning tokens
    // are INSIDE the 1000, so they must not be priced again.
    const expected = 800 * GPT55.input + 200 * GPT55.cacheRead + 1000 * GPT55.output
    expect(call.costUSD).toBeCloseTo(expected, 12)
    // Guard the direction: the pre-fix arithmetic charged 1400 output tokens.
    const preFix = 800 * GPT55.input + 200 * GPT55.cacheRead + 1400 * GPT55.output
    expect(call.costUSD).toBeLessThan(preFix)
    // The raw fields are still reported untouched; only the pricing changed.
    expect(call.outputTokens).toBe(1000)
    expect(call.reasoningTokens).toBe(400)
  })

  it('does not double-count reasoning in the displayed output tokens', async () => {
    const codex = makeApiCall('codex', 'gpt-5.5', { outputTokens: 1000, reasoningTokens: 400 })
    // A provider that really does report reasoning as a separate bucket keeps
    // the additive behaviour, so this is a codex carve-out and not a blanket
    // change to every display sum. Gemini documents "thoughts" as genuinely
    // separate from output (src/providers/gemini.ts), unlike codex/claude.
    const additive = makeApiCall('gemini', 'gemini-2.5-pro', { outputTokens: 1000, reasoningTokens: 400 })
    const projects = [makeProject([codex, additive])]

    const auditRows = await aggregateAudit(projects)
    expect(auditRows.find(r => r.provider === 'codex')!.displayed.outputTokens).toBe(1000)
    expect(auditRows.find(r => r.provider === 'gemini')!.displayed.outputTokens).toBe(1400)

    const modelRows = await aggregateModels(projects)
    expect(modelRows.find(r => r.provider === 'codex')!.outputTokens).toBe(1000)
    expect(modelRows.find(r => r.provider === 'gemini')!.outputTokens).toBe(1400)
  })
})

// ── Fix B: cache_write_input_tokens, guarded ──────────────────────────────

describe('#1075 B - cache_write_input_tokens', () => {
  it('prices cache writes at the explicit rate on gpt-5.6-terra', async () => {
    const call = await parseOneEvent('gpt-5.6-terra', {
      input_tokens: 1000,
      cached_input_tokens: 200,
      cache_write_input_tokens: 300,
      output_tokens: 100,
    })

    expect(call.inputTokens).toBe(500)
    expect(call.cacheCreationInputTokens).toBe(300)
    expect(call.cacheReadInputTokens).toBe(200)
    const expected =
      500 * TERRA.input +
      300 * TERRA.cacheWrite +
      200 * TERRA.cacheRead +
      100 * TERRA.output
    expect(expected).toBeCloseTo(0.00299, 12)
    expect(call.costUSD).toBeCloseTo(expected, 12)
  })

  it('THE GUARD: leaves cache writes in the input bucket when the model has no explicit rate', async () => {
    // gpt-5.5 carries `null` for cache_creation_input_token_cost, so
    // buildCosts fabricates 1.25x input for it. OpenAI charges nothing extra
    // to write cache before gpt-5.6, so routing these tokens through that
    // fabricated rate would invent a surcharge. Cost must be byte-identical to
    // the pre-fix number. Delete the guard and this test fails.
    const withWrite = await parseOneEvent('gpt-5.5', {
      input_tokens: 1000,
      cached_input_tokens: 200,
      cache_write_input_tokens: 300,
      output_tokens: 100,
    })
    const withoutWrite = await parseOneEvent('gpt-5.5', {
      input_tokens: 1000,
      cached_input_tokens: 200,
      output_tokens: 100,
    })

    expect(withWrite.inputTokens).toBe(800)
    expect(withWrite.cacheCreationInputTokens).toBe(0)
    const expected = 800 * GPT55.input + 200 * GPT55.cacheRead + 100 * GPT55.output
    expect(withWrite.costUSD).toBeCloseTo(expected, 12)
    expect(withWrite.costUSD).toBeCloseTo(withoutWrite.costUSD, 12)
    // The fabricated rate is 1.25 x 5e-6; make sure not a cent of it landed.
    expect(withWrite.costUSD).toBeLessThan(expected + 300 * GPT55.input * 1.25)
  })

  it('clamps a cache-write count larger than the uncached input', async () => {
    const call = await parseOneEvent('gpt-5.6-terra', {
      input_tokens: 1000,
      cached_input_tokens: 200,
      cache_write_input_tokens: 5000,
      output_tokens: 100,
    })

    expect(call.inputTokens).toBe(0)
    expect(call.cacheCreationInputTokens).toBe(800)
    expect(call.costUSD).toBeCloseTo(800 * TERRA.cacheWrite + 200 * TERRA.cacheRead + 100 * TERRA.output, 12)
  })
})

// ── Cache invalidation: a cost change must not be served from stale bytes ──

describe('#1075 cache invalidation', () => {
  it('discards a v10 codex results cache (it stores costUSD verbatim)', async () => {
    const cacheDir = join(tmpDir, 'cache')
    await mkdir(cacheDir, { recursive: true })
    const sessionFile = join(tmpDir, 'rollout-stale.jsonl')
    await writeFile(sessionFile, '{}\n')

    const { statSync } = await import('fs')
    const s = statSync(sessionFile)
    const stale: ParsedProviderCall = {
      provider: 'codex',
      model: 'gpt-5.5',
      inputTokens: 800,
      outputTokens: 1000,
      cacheCreationInputTokens: 0,
      cacheReadInputTokens: 200,
      cachedInputTokens: 200,
      reasoningTokens: 400,
      webSearchRequests: 0,
      costUSD: 0.0445, // the pre-fix, reasoning-double-counted number
      tools: [],
      bashCommands: [],
      timestamp: '2026-08-16T10:01:00Z',
      speed: 'standard',
      deduplicationKey: 'codex:stale',
    }
    await writeFile(join(cacheDir, 'codex-results.json'), JSON.stringify({
      version: 10,
      files: { [sessionFile]: { dev: s.dev, ino: s.ino, mtimeMs: s.mtimeMs, sizeBytes: s.size, project: 'p', calls: [stale] } },
    }))

    const prevCacheDir = process.env['CODEBURN_CACHE_DIR']
    process.env['CODEBURN_CACHE_DIR'] = cacheDir
    try {
      clearCodexMemCaches()
      // Revert CODEX_CACHE_VERSION to 10 and this returns the stale $0.0445 call.
      expect(await readCachedCodexResults(sessionFile)).toBeNull()
    } finally {
      if (prevCacheDir === undefined) delete process.env['CODEBURN_CACHE_DIR']; else process.env['CODEBURN_CACHE_DIR'] = prevCacheDir
    }
  })

  it('re-derives days finalized at daily-cache v20', async () => {
    const cacheRoot = join(tmpDir, 'daily')
    await mkdir(cacheRoot, { recursive: true })
    const prevCacheDir = process.env['CODEBURN_CACHE_DIR']
    process.env['CODEBURN_CACHE_DIR'] = cacheRoot
    try {
      const date = toDateString(new Date(Date.now() - 7 * 24 * 60 * 60 * 1000))
      const yesterday = toDateString(new Date(Date.now() - 24 * 60 * 60 * 1000))
      const oldPath = join(cacheRoot, 'daily-cache.v20.json')
      const oldCache = {
        version: 20,
        savingsConfigHash: 'cfg',
        tzKey: currentTzKey(),
        lastComputedDate: yesterday,
        days: [codexDay(date, 99)],
        complete: true,
        watermarkTrusted: true,
      }
      await writeFile(oldPath, JSON.stringify(oldCache))

      let parseCount = 0
      const hydrated = await ensureCacheHydrated(
        async () => { parseCount++; return [] },
        () => [codexDay(date, 2)],
        'cfg',
        () => true,
      )

      // Drop MIN_SUPPORTED_VERSION back to 20 and the v20 day is trusted as-is,
      // so parseCount stays 0 and the day keeps its overstated $99.
      expect(parseCount).toBe(1)
      expect(hydrated.days.find(d => d.date === date)?.cost).toBe(2)
      expect(JSON.parse(await readFile(oldPath, 'utf8'))).toEqual(oldCache)
    } finally {
      if (prevCacheDir === undefined) delete process.env['CODEBURN_CACHE_DIR']; else process.env['CODEBURN_CACHE_DIR'] = prevCacheDir
    }
  })
})

// ── fixtures ──────────────────────────────────────────────────────────────

function makeApiCall(provider: string, model: string, usage: Partial<TokenUsage>): ParsedApiCall {
  return {
    provider,
    model,
    usage: {
      inputTokens: 0,
      outputTokens: 0,
      cacheCreationInputTokens: 0,
      cacheReadInputTokens: 0,
      cachedInputTokens: 0,
      reasoningTokens: 0,
      webSearchRequests: 0,
      ...usage,
    },
    costUSD: 0,
    tools: [],
    mcpTools: [],
    skills: [],
    hasAgentSpawn: false,
    hasPlanMode: false,
    speed: 'standard',
    timestamp: '2026-08-16T00:00:00.000Z',
    bashCommands: [],
    deduplicationKey: `${provider}-${model}`,
  }
}

function makeProject(calls: ParsedApiCall[]): ProjectSummary {
  const turn: ClassifiedTurn = {
    userMessage: 't',
    assistantCalls: calls,
    timestamp: '2026-08-16T00:00:00.000Z',
    sessionId: 's1',
    category: 'feature' as TaskCategory,
    retries: 0,
    hasEdits: false,
  }
  const session: SessionSummary = {
    sessionId: 's1',
    project: 'p',
    firstTimestamp: '2026-08-16T00:00:00.000Z',
    lastTimestamp: '2026-08-16T00:00:00.000Z',
    totalCostUSD: 0,
    totalInputTokens: 0,
    totalOutputTokens: 0,
    totalCacheReadTokens: 0,
    totalCacheWriteTokens: 0,
    apiCalls: 0,
    turns: [turn],
    modelBreakdown: {},
    toolBreakdown: {},
    mcpBreakdown: {},
    bashBreakdown: {},
    categoryBreakdown: {} as SessionSummary['categoryBreakdown'],
    skillBreakdown: {},
  }
  return { project: 'p', projectPath: 'p', sessions: [session], totalCostUSD: 0, totalApiCalls: 0 }
}

function codexDay(date: string, cost: number): DailyEntry {
  const tokens = { inputTokens: 100, outputTokens: 20, cacheReadTokens: 30, cacheWriteTokens: 0 }
  return {
    date,
    cost,
    savingsUSD: 0,
    calls: 1,
    sessions: 1,
    ...tokens,
    editTurns: 0,
    oneShotTurns: 0,
    models: { 'GPT-5.5': { calls: 1, cost, savingsUSD: 0, ...tokens } },
    categories: {},
    providers: { codex: { calls: 1, cost, savingsUSD: 0, sessions: 1, ...tokens } },
  }
}
