import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, describe, expect, it } from 'vitest'

import {
  compactionPressure,
  computeSignals,
  contextReuse,
  contextReuseRatio,
  duplicateCallRate,
  delegationOverhead,
  hashNormalized,
  isSignalMeasurable,
  normalizeWhitespace,
  oversizedResults,
  oversizedResultShare,
  prefixStability,
  cachePrefixStability,
  skillUtilisation,
  toolYield,
} from '../kyber/analysis/signals.js'
import {
  CanonStore,
  DETECTOR_VERSION,
  SCHEMA_VERSION,
} from '../kyber/canon/store.js'
import { notMeasurable, type CanonicalRecord, type Measurability } from '../kyber/canon/types.js'

const tempDirs: string[] = []

function tempStorePath(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-signals-test-'))
  tempDirs.push(dir)
  return join(dir, 'canon.db')
}

afterEach(() => {
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()!
    rmSync(dir, { recursive: true, force: true })
  }
})

describe('Signal 1: contextReuse / contextReuseRatio (Task F1 / ADR 0009 / ADR 0011)', () => {
  it('computes cache reuse ratio from input and cache token counters', () => {
    const res = contextReuse({
      freshInput: 100,
      cacheRead: 300,
      cacheCreation: 0,
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.75)
      expect(res.measurementClass).toBe('deterministic')
      expect(res.confidence).toBe('high')
      expect(res.numerator).toBe(300)
      expect(res.denominator).toBe(400)
    }
  })

  it('supports contextReuseRatio as an identical alias', () => {
    const res = contextReuseRatio({
      freshInput: 50,
      cacheRead: 50,
    })
    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.5)
    }
  })

  it('handles 100% cache hit and 0% cache hit edge cases cleanly', () => {
    const fullHit = contextReuse({ freshInput: 0, cacheRead: 500 })
    expect(isSignalMeasurable(fullHit)).toBe(true)
    if (isSignalMeasurable(fullHit)) expect(fullHit.value).toBe(1.0)

    const zeroHit = contextReuse({ freshInput: 500, cacheRead: 0 })
    expect(isSignalMeasurable(zeroHit)).toBe(true)
    if (isSignalMeasurable(zeroHit)) expect(zeroHit.value).toBe(0.0)
  })

  it('aggregates across turns when turns are provided', () => {
    const res = contextReuse({
      turns: [
        { freshInput: 100, cacheRead: 100 },
        { freshInput: 20, cacheRead: 180 },
      ],
    })
    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.numerator).toBe(280)
      expect(res.denominator).toBe(400)
      expect(res.value).toBe(0.7)
    }
  })

  it('emits not_measurable when total input tokens are zero (never divide by zero or emit NaN)', () => {
    const res = contextReuse({
      freshInput: 0,
      cacheRead: 0,
      cacheCreation: 0,
    })
    expect(res.status).toBe('not_measurable')
    expect((res as { reason: string }).reason).toContain('No measurable input tokens')
  })

  it('emits not_measurable for unmeasurable harnesses without cache telemetry (Cursor, Aider) - never fabricated 0', () => {
    const cursorRes = contextReuse({
      harness: 'cursor',
      freshInput: 100,
      cacheRead: 0,
    })
    expect(cursorRes.status).toBe('not_measurable')
    expect((cursorRes as { reason: string }).reason).toContain('without cache read/write counters')

    const aiderRes = contextReuse({
      harness: 'aider',
    })
    expect(aiderRes.status).toBe('not_measurable')
  })

  it('respects explicit measurability declaration overrides', () => {
    const measurability: Measurability = {
      cache_read: notMeasurable('Custom gap: proxy dropped cache headers'),
    }
    const res = contextReuse({
      measurability,
      freshInput: 100,
      cacheRead: 50,
    })
    expect(res.status).toBe('not_measurable')
    expect((res as { reason: string }).reason).toBe('Custom gap: proxy dropped cache headers')
  })
})

describe('Signal 2: prefixStability / cachePrefixStability (Task F1 / Whitespace Hashing)', () => {
  it('computes deterministic stability across sequential turns with whitespace normalization', () => {
    const res = prefixStability({
      turns: [
        { prefixText: 'You are a   helpful coding assistant.\nRules:\n1. Be concise.' },
        { prefixText: 'You are a helpful coding assistant.\nRules:\n1. Be concise. ' },
        { prefixText: 'You are a helpful coding assistant.\nRules:\n1. Be concise.' },
      ],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(1.0)
      expect(res.measurementClass).toBe('deterministic')
      expect(res.metadata?.normalisation).toBe('whitespace-collapsed-sha256')
      expect(res.numerator).toBe(2)
      expect(res.denominator).toBe(2)
    }
  })

  it('detects partial prefix instability when prompt prefix changes across turns', () => {
    const res = cachePrefixStability({
      turns: [
        { prefixText: 'Prefix V1' },
        { prefixText: 'Prefix V1' },
        { prefixText: 'Prefix V2 with timestamp 12:00:01' },
      ],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.numerator).toBe(1) // Transition 1->2 is stable, 2->3 unstable
      expect(res.denominator).toBe(2)
      expect(res.value).toBe(0.5)
    }
  })

  it('extracts prefix from structured parts if prefixText is omitted', () => {
    const res = prefixStability({
      turns: [
        {
          parts: [
            { part: 'system_prompt', text: 'System prompt alpha' },
            { part: 'instruction_context', text: 'Instruction context' },
            { part: 'conversation_history', text: 'User message 1' },
          ],
        },
        {
          parts: [
            { part: 'system_prompt', text: 'System prompt alpha' },
            { part: 'instruction_context', text: 'Instruction context' },
            { part: 'conversation_history', text: 'User message 2' },
          ],
        },
      ],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(1.0)
      expect(res.measurementClass).toBe('deterministic')
    }
  })

  it('emits not_measurable for single-turn sessions (no sequential transitions to evaluate)', () => {
    const res = prefixStability({
      turns: [{ prefixText: 'Only one turn' }],
    })
    expect(res.status).toBe('not_measurable')
    expect((res as { reason: string }).reason).toContain('At least two turns are required')
  })

  it('degrades to counter-only fallback with label detect-but-cannot-locate when prefix bytes are absent', () => {
    // For harnesses like claude-code or roo-code where prefix bytes are not_measurable but cache counters exist
    const res = prefixStability({
      harness: 'claude-code',
      totalInput: 1000,
      cacheRead: 800,
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.status).toBe('derived')
      expect(res.value).toBe(0.8)
      expect(res.measurementClass).toBe('inferred')
      expect(res.fallback).toBe('detect-but-cannot-locate')
      expect(res.confidence).toBe('medium')
    }
  })

  it('emits not_measurable when neither prefix bytes nor cache counter fallback are available (Cursor)', () => {
    const res = prefixStability({
      harness: 'cursor',
    })
    expect(res.status).toBe('not_measurable')
    expect((res as { reason: string }).reason).toContain('without multi-turn prefix reconstruction')
  })

  it('normalizes internal and outer whitespace deterministically with SHA-256', () => {
    const a = normalizeWhitespace('  hello \t\n  world   ')
    const b = normalizeWhitespace('hello world')
    expect(a).toBe('hello world')
    expect(b).toBe('hello world')
    expect(hashNormalized(a)).toBe(hashNormalized(b))
  })
})

describe('Signal 3: toolYield (Task F1 / Weak Credit vs Strong Debit / Negative Information)', () => {
  it('computes yield as ratio of distinct invoked tools to defined tools', () => {
    const res = toolYield({
      definedTools: ['read_file', 'edit_file', 'list_dir', 'search_web'],
      invokedTools: ['read_file', 'read_file', 'edit_file'],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.5) // 2 distinct invoked out of 4 defined
      expect(res.numerator).toBe(2)
      expect(res.denominator).toBe(4)
      expect(res.measurementClass).toBe('deterministic')
      expect(res.metadata?.creditedTools).toEqual(['read_file', 'edit_file'])
      expect(res.metadata?.unusedTools).toEqual(['list_dir', 'search_web'])
    }
  })

  it('credits tools on weak evidence (even 1 call is credited)', () => {
    const res = toolYield({
      definedTools: ['git_status'],
      invokedTools: ['git_status'],
    })
    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(1.0)
      expect(res.metadata?.creditedTools).toContain('git_status')
    }
  })

  it('debits tools only on strong evidence (resident in schema and never invoked)', () => {
    const res = toolYield({
      definedTools: ['expensive_analysis_tool', 'cheap_echo'],
      invokedTools: ['cheap_echo'],
    })
    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.5)
      expect(res.metadata?.unusedTools).toEqual(['expensive_analysis_tool'])
    }
  })

  it('explicitly asserts and acknowledges under-counting of negative-information reads in metadata', () => {
    const res = toolYield({
      definedTools: ['danger_delete_database', 'safe_query'],
      invokedTools: ['safe_query'],
    })
    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      // Must flag undercountsNegativeInformation: true per Task F1 acceptance criteria
      expect(res.metadata?.undercountsNegativeInformation).toBe(true)
      expect(res.metadata?.undercountingRationale).toContain('negative-information reads')
    }
  })

  it('emits not_measurable when tool definitions are absent from telemetry (Cursor, Codex, pi)', () => {
    const cursorRes = toolYield({ harness: 'cursor' })
    expect(cursorRes.status).toBe('not_measurable')
    expect((cursorRes as { reason: string }).reason).toContain('does not export tool definition schemas')

    const emptyRes = toolYield({ definedTools: [] })
    expect(emptyRes.status).toBe('not_measurable')
  })
})

describe('Signal 4: duplicateCallRate (Task F1 / Redundancy Detection)', () => {
  it('detects duplicate tool calls with matching name and arguments', () => {
    const res = duplicateCallRate({
      calls: [
        { name: 'read_file', arguments: { path: 'src/index.ts' } },
        { name: 'list_dir', arguments: { dir: 'src' } },
        { name: 'read_file', arguments: { path: 'src/index.ts' } }, // Duplicate 1
        { name: 'read_file', arguments: { path: 'src/index.ts' } }, // Duplicate 2
      ],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.5) // 2 duplicates out of 4 calls
      expect(res.numerator).toBe(2)
      expect(res.denominator).toBe(4)
      expect(res.metadata?.uniqueCalls).toBe(2)
      expect(res.metadata?.duplicateCalls).toBe(2)
    }
  })

  it('normalizes whitespace in JSON arguments when checking for duplicates', () => {
    const res = duplicateCallRate({
      calls: [
        { name: 'grep_search', arguments: '{"query":"pattern","path":"src"}' },
        { name: 'grep_search', arguments: '{\n  "query": "pattern",\n  "path": "src"\n}' },
      ],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.5)
      expect(res.numerator).toBe(1)
    }
  })

  it('reports 0.0 when all calls are unique', () => {
    const res = duplicateCallRate({
      calls: [
        { name: 'read_file', arguments: { path: 'a.ts' } },
        { name: 'read_file', arguments: { path: 'b.ts' } },
      ],
    })
    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.0)
    }
  })

  it('emits not_measurable when no tool calls occurred in session', () => {
    const res = duplicateCallRate({ calls: [] })
    expect(res.status).toBe('not_measurable')
    expect((res as { reason: string }).reason).toContain('No tool invocations observed')
  })
})

describe('Signal 5: oversizedResults / oversizedResultShare (Task F1 / Length Thresholds)', () => {
  it('flags tool results exceeding token threshold (default 2000)', () => {
    const res = oversizedResults({
      results: [
        { toolName: 'bash', content: 'short output', tokens: 500 },
        { toolName: 'cat', content: 'huge file dump', tokens: 3500 }, // Oversized
        { toolName: 'grep', content: 'two matches', tokens: 800 },
      ],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(1 / 3)
      expect(res.numerator).toBe(1)
      expect(res.denominator).toBe(3)
      const flagged = res.metadata?.flagged as { toolName?: string; tokens?: number }[]
      expect(flagged).toHaveLength(1)
      expect(flagged[0]?.toolName).toBe('cat')
    }
  })

  it('supports oversizedResultShare as an alias', () => {
    const res = oversizedResultShare({
      results: [{ content: 'x'.repeat(10000) }], // Exceeds default 8000 char threshold
    })
    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(1.0)
    }
  })

  it('respects custom token or character threshold overrides', () => {
    const res = oversizedResults({
      results: [
        { toolName: 'ls', content: 'file.txt', tokens: 20 },
        { toolName: 'status', content: 'ready', tokens: 60 },
      ],
      defaultTokenThreshold: 50,
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.5) // 1 out of 2 exceeds 50 tokens
    }
  })

  it('emits not_measurable when no tool results exist in session', () => {
    const res = oversizedResults({ results: [] })
    expect(res.status).toBe('not_measurable')
    expect((res as { reason: string }).reason).toContain('No tool results captured')
  })
})

describe('Signal 6: compactionPressure (Task F1 / Context Window & Risk)', () => {
  it('measures peak input consumption relative to context limit', () => {
    const res = compactionPressure({
      peakInputTokens: 160_000,
      contextLimit: 200_000,
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.8)
      expect(res.numerator).toBe(160_000)
      expect(res.denominator).toBe(200_000)
      expect(res.metadata?.compactionRisk).toBe('high')
      expect(res.metadata?.remainingTokens).toBe(40_000)
    }
  })

  it('evaluates compaction risk tiers: low, moderate, high, critical', () => {
    const low = compactionPressure({ peakInputTokens: 80_000, contextLimit: 200_000 })
    expect(isSignalMeasurable(low) && low.metadata?.compactionRisk).toBe('low')

    const mod = compactionPressure({ peakInputTokens: 120_000, contextLimit: 200_000 })
    expect(isSignalMeasurable(mod) && mod.metadata?.compactionRisk).toBe('moderate')

    const high = compactionPressure({ peakInputTokens: 160_000, contextLimit: 200_000 })
    expect(isSignalMeasurable(high) && high.metadata?.compactionRisk).toBe('high')

    const crit = compactionPressure({ peakInputTokens: 190_000, contextLimit: 200_000 })
    expect(isSignalMeasurable(crit) && crit.metadata?.compactionRisk).toBe('critical')
  })

  it('computes peak from turns list if peakInputTokens not supplied directly', () => {
    const res = compactionPressure({
      turns: [{ inputTokens: 50_000 }, { inputTokens: 150_000 }, { inputTokens: 100_000 }],
      contextLimit: 200_000,
    })
    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.75)
    }
  })

  it('emits not_measurable when input tokens are zero or missing', () => {
    const res = compactionPressure({ peakInputTokens: 0 })
    expect(res.status).toBe('not_measurable')
    expect((res as { reason: string }).reason).toContain('No positive token counts')
  })

  it('emits not_measurable when contextLimit is invalid', () => {
    const res = compactionPressure({ peakInputTokens: 1000, contextLimit: 0 })
    expect(res.status).toBe('not_measurable')
  })
})

describe('Signal 7: delegationOverhead (Task F1 / Subagent Workflow Ratio)', () => {
  it('computes ratio of subagent execution tokens to total workflow tokens', () => {
    const res = delegationOverhead({
      rootTokens: 20_000,
      subagentTokens: 30_000,
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.6)
      expect(res.numerator).toBe(30_000)
      expect(res.denominator).toBe(50_000)
      expect(res.measurementClass).toBe('deterministic')
    }
  })

  it('returns 0.0 for single-agent workflows with verified structure', () => {
    const res = delegationOverhead({
      rootTokens: 10_000,
      subagentTokens: 0,
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBe(0.0)
    }
  })

  it('aggregates executions tree when executions are provided', () => {
    const res = delegationOverhead({
      executions: [
        { isRoot: true, tokens: 25_000 },
        { isRoot: false, tokens: 10_000 },
        { isRoot: false, tokens: 15_000 },
      ],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.numerator).toBe(25_000)
      expect(res.denominator).toBe(50_000)
      expect(res.value).toBe(0.5)
      expect(res.metadata?.subagentCount).toBe(2)
    }
  })

  it('emits not_measurable when harness does not export execution structure (Cursor, Aider, Codex) - never 0', () => {
    const cursorRes = delegationOverhead({ harness: 'cursor' })
    expect(cursorRes.status).toBe('not_measurable')
    expect((cursorRes as { reason: string }).reason).toContain('does not export execution hierarchy')

    const codexRes = delegationOverhead({ harness: 'codex' })
    expect(codexRes.status).toBe('not_measurable')
  })
})

describe('Signal 8: skillUtilisation (Task F1 / Decision D16 Compliance)', () => {
  it('stamps result at low confidence and inferred measurement class per Decision D16', () => {
    const res = skillUtilisation({
      referencedSkills: ['code-review', 'commit-writer', 'database-migrator'],
      executedSkills: ['code-review'],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.value).toBeCloseTo(0.3333, 3)
      expect(res.measurementClass).toBe('inferred')
      // Must be stamped low confidence per Decision D16
      expect(res.confidence).toBe('low')
      expect(res.confidenceBasis).toContain('Decision D16')
      expect(res.metadata?.d16RankedLast).toBe(true)
      expect(res.metadata?.executedSkills).toEqual(['code-review'])
      expect(res.metadata?.unexecutedSkills).toEqual(['commit-writer', 'database-migrator'])
    }
  })

  it('extracts skill references from prompt context using skill tags or prefixes', () => {
    const contextContent = `
      You have access to the following skills:
      <skill name="git-sync">Sync git branch</skill>
      <skill name="vitest-runner">Run vitest</skill>
      skill: lint-fixer
    `
    const res = skillUtilisation({
      contextContent,
      executedSkills: ['git-sync'],
    })

    expect(isSignalMeasurable(res)).toBe(true)
    if (isSignalMeasurable(res)) {
      expect(res.numerator).toBe(1)
      expect(res.denominator).toBe(3)
      expect(res.metadata?.referencedSkills).toEqual(['git-sync', 'vitest-runner', 'lint-fixer'])
    }
  })

  it('emits not_measurable when no skill references or catalogue exist in context', () => {
    const res = skillUtilisation({
      contextContent: 'Just a plain conversation with no skills mentioned.',
    })
    expect(res.status).toBe('not_measurable')
    expect((res as { reason: string }).reason).toContain('No skill references or definitions')
  })
})

describe('computeSignals orchestrator (Task F1)', () => {
  it('computes all 8 pure signals in a single unified pass with pure zero-I/O execution', () => {
    const records: CanonicalRecord[] = [
      {
        spanId: 's-1',
        traceId: 't-1',
        parentSpanId: null,
        source: 'copilot',
        harness: 'copilot',
        name: 'turn-1',
        op: 'llm.invoke',
        kind: 'client',
        timestamp: '2026-09-05T12:00:00.000Z',
        durationMs: 500,
        status: 'ok',
        tokens: {
          freshInput: 50,
          cacheRead: 150,
          cacheCreation: 0,
          output: 20,
          reportedInput: 200,
          reportedOutput: 20,
        },
        content: {
          system_prompt: 'System instructions',
        },
        parts: [
          { part: 'system_prompt', text: 'System instructions' },
          { part: 'tool_definitions', text: JSON.stringify([{ name: 'grep' }, { name: 'edit' }]) },
        ],
        cost: { basis: 'published', status: 'priced', value: 0.001 },
      },
      {
        spanId: 's-2',
        traceId: 't-1',
        parentSpanId: 's-1',
        source: 'copilot',
        harness: 'copilot',
        name: 'grep',
        op: 'tool.invoke',
        kind: 'internal',
        timestamp: '2026-09-05T12:00:01.000Z',
        durationMs: 100,
        status: 'ok',
        tokens: { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 },
        content: {},
        parts: [{ part: 'tool_result_content', text: 'grep matches line 42' }],
        cost: { basis: 'unknown', status: 'not_billed' },
      },
      {
        spanId: 's-3',
        traceId: 't-1',
        parentSpanId: 's-1',
        source: 'copilot',
        harness: 'copilot',
        name: 'turn-2',
        op: 'llm.invoke',
        kind: 'client',
        timestamp: '2026-09-05T12:00:02.000Z',
        durationMs: 400,
        status: 'ok',
        tokens: {
          freshInput: 20,
          cacheRead: 180,
          cacheCreation: 0,
          output: 25,
          reportedInput: 200,
          reportedOutput: 25,
        },
        content: {
          system_prompt: 'System instructions',
        },
        parts: [{ part: 'system_prompt', text: 'System instructions' }],
        cost: { basis: 'published', status: 'priced', value: 0.001 },
      },
    ]

    const signals = computeSignals({
      harness: 'copilot',
      records,
      contextLimit: 200_000,
    })

    // Assert all 8 signals are returned
    expect(signals.contextReuse).toBeDefined()
    expect(signals.prefixStability).toBeDefined()
    expect(signals.toolYield).toBeDefined()
    expect(signals.duplicateCallRate).toBeDefined()
    expect(signals.oversizedResults).toBeDefined()
    expect(signals.compactionPressure).toBeDefined()
    expect(signals.delegationOverhead).toBeDefined()
    expect(signals.skillUtilisation).toBeDefined()

    // Verify values
    if (isSignalMeasurable(signals.contextReuse)) {
      expect(signals.contextReuse.value).toBe(330 / 400) // (150+180) / 400 = 0.825
    }
    if (isSignalMeasurable(signals.prefixStability)) {
      expect(signals.prefixStability.value).toBe(1.0)
    }
    if (isSignalMeasurable(signals.toolYield)) {
      expect(signals.toolYield.value).toBe(0.5) // grep called, edit not called
    }
    if (isSignalMeasurable(signals.duplicateCallRate)) {
      expect(signals.duplicateCallRate.value).toBe(0.0) // 1 call total
    }
    if (isSignalMeasurable(signals.compactionPressure)) {
      expect(signals.compactionPressure.value).toBe(200 / 200_000)
    }
  })
})

describe('CanonStore detector_version stamp & Decision D17 compliance', () => {
  it('stamps fresh stores with SCHEMA_VERSION and DETECTOR_VERSION', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)

    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(store.getMetadata('detector_version')).toBe(String(DETECTOR_VERSION))
    expect(store.getDetectorVersion()).toBe(DETECTOR_VERSION)
    expect(store.hasCurrentDetectorVersion()).toBe(true)
    expect(store.isDetectorOutdated()).toBe(false)
    store.close()
  })

  it('detects outdated detector_version and flags that recomputation is required', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)
    expect(store.isDetectorOutdated(DETECTOR_VERSION)).toBe(false)

    // Simulate an older detector version stamped in metadata
    store.setDetectorVersion(0)
    expect(store.getDetectorVersion()).toBe(0)
    expect(store.hasCurrentDetectorVersion()).toBe(false)
    // When detector_version is older, isDetectorOutdated is true -> forces recomputation
    expect(store.isDetectorOutdated()).toBe(true)

    // Bump detector_version to current
    store.setDetectorVersion(DETECTOR_VERSION)
    expect(store.isDetectorOutdated()).toBe(false)
    expect(store.hasCurrentDetectorVersion()).toBe(true)
    store.close()
  })

  it('migrates a v6 store forward and stamps detector_version cleanly', () => {
    const path = tempStorePath()
    
    // Create an initialized store, then roll back schema_version to 6 and remove detector_version
    const initStore = new CanonStore(path)
    initStore.close()

    const db = new DatabaseSync(path)
    db.prepare("UPDATE metadata SET value = '6' WHERE key = 'schema_version'").run()
    db.prepare("DELETE FROM metadata WHERE key = 'detector_version'").run()
    db.close()

    // Opening with CanonStore executes migration forward
    const store = new CanonStore(path)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(SCHEMA_VERSION).toBeGreaterThanOrEqual(7)
    expect(store.getMetadata('detector_version')).toBe(String(DETECTOR_VERSION))
    expect(store.getDetectorVersion()).toBe(DETECTOR_VERSION)
    expect(store.hasCurrentDetectorVersion()).toBe(true)
    store.close()
  })
})
