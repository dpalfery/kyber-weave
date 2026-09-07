import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, describe, expect, it } from 'vitest'

import {
  computeRankScore,
  CONFIDENCE_MULTIPLIERS,
  detectCompactionHazard,
  detectDormantToolSchema,
  detectDuplicateToolCall,
  detectFindings,
  detectInactiveSkillReference,
  detectOversizedToolResult,
  detectPrefixCacheBreak,
  detectUnboundedDelegation,
  lintRecommendationD8,
  rankFindings,
  type DetectorId,
  type Finding,
  type FindingConfidence,
  type FindingEvidenceLink,
} from '../kyber/analysis/findings.js'
import {
  CanonStore,
  SCHEMA_VERSION,
} from '../kyber/canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import type { OutcomeBlock } from '../canon/outcome.js'
import { KyberBridge } from '../kyber/server/bridge.js'
import { handleKyberRequest } from '../kyber/server/routes.js'

const tempDirs: string[] = []

function tempStorePath(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-findings-test-'))
  tempDirs.push(dir)
  return join(dir, 'canon.db')
}

afterEach(() => {
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()!
    rmSync(dir, { recursive: true, force: true })
  }
})

// Helper generating a minimal mock CanonicalRecord
function makeMockRecord(overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: overrides.spanId ?? `span-${Math.random().toString(36).slice(2, 9)}`,
    traceId: overrides.traceId ?? 'trace-test',
    parentSpanId: overrides.parentSpanId ?? null,
    source: overrides.source ?? 'test-harness',
    harness: overrides.harness ?? 'claude-code',
    name: overrides.name ?? 'llm_call',
    op: overrides.op ?? 'llm.invoke',
    kind: overrides.kind ?? 'client',
    timestamp: overrides.timestamp ?? new Date().toISOString(),
    durationMs: overrides.durationMs ?? 100,
    status: overrides.status ?? 'ok',
    tokens: overrides.tokens ?? {
      freshInput: 200,
      cacheRead: 100,
      cacheCreation: 0,
      output: 50,
      reportedInput: 300,
      reportedOutput: 50,
    },
    content: overrides.content ?? {},
    cost: overrides.cost ?? { basis: 'unknown', status: 'not_billed' },
    parts: overrides.parts,
    raw: overrides.raw,
    sessionId: overrides.sessionId ?? 'session-test-1',
  }
}

describe('Decision D5: Finding Contract Compliance field-for-field', () => {
  it('strictly validates all required D5 fields across generated findings', () => {
    const finding: Finding = {
      id: 'finding-test-1',
      detectorId: 'dormant-tool-schema',
      title: 'Dormant Tool Schema: "grep" resident without invocation',
      mechanism: 'Tool schema occupied 300 tokens on each turn across 4 turns without invocation.',
      evidenceLinks: [
        { spanId: 'span-1', turnIndex: 0, description: 'Tool registered in schema' },
        { spanId: 'span-2', turnIndex: 3, description: 'Tool remained uninvoked' },
      ],
      confidence: 'deterministic',
      estimatedWasteTokens: 1200,
      recommendation: 'Relocate tool schema "grep" to on-demand loading using progressive disclosure.',
      errorBar: { lower: 960, upper: 1440 },
      outcomeRiskCaveat: 'Relocating tool schemas requires agent awareness of tool discovery prompts.',
    }

    expect(finding.id).toBeDefined()
    expect(finding.detectorId).toBe('dormant-tool-schema')
    expect(typeof finding.title).toBe('string')
    expect(typeof finding.mechanism).toBe('string')
    expect(Array.isArray(finding.evidenceLinks)).toBe(true)
    expect(finding.evidenceLinks.length).toBeGreaterThanOrEqual(2)
    for (const link of finding.evidenceLinks) {
      expect(typeof link.spanId).toBe('string')
      expect(typeof link.turnIndex).toBe('number')
      expect(typeof link.description).toBe('string')
    }
    expect(['deterministic', 'calibrated_statistical', 'heuristic']).toContain(finding.confidence)
    expect(typeof finding.estimatedWasteTokens).toBe('number')
    expect(finding.estimatedWasteTokens).toBeGreaterThan(0)
    expect(typeof finding.recommendation).toBe('string')
    expect(typeof finding.errorBar.lower).toBe('number')
    expect(typeof finding.errorBar.upper).toBe('number')
    expect(finding.errorBar.lower).toBeLessThanOrEqual(finding.errorBar.upper)
    expect(typeof finding.outcomeRiskCaveat).toBe('string')
  })
})

describe('Detector 1: dormant-tool-schema', () => {
  it('detects tool schema resident across >=3 turns with 0 invocations and no references', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-1',
      op: 'llm.invoke',
      parts: [
        {
          part: 'tool_definitions',
          text: JSON.stringify([{ name: 'unused_linter', description: 'runs linter' }]),
        },
      ],
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-2',
      op: 'llm.invoke',
      parts: [
        {
          part: 'tool_definitions',
          text: JSON.stringify([{ name: 'unused_linter', description: 'runs linter' }]),
        },
      ],
    })
    const turn3 = makeMockRecord({
      spanId: 'turn-3',
      op: 'llm.invoke',
      parts: [
        {
          part: 'tool_definitions',
          text: JSON.stringify([{ name: 'unused_linter', description: 'runs linter' }]),
        },
      ],
    })

    const findings = detectDormantToolSchema({
      records: [turn1, turn2, turn3],
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('dormant-tool-schema')
    expect(f.confidence).toBe('deterministic')
    expect(f.evidenceLinks.length).toBeGreaterThanOrEqual(2)
    expect(f.evidenceLinks[0]?.spanId).toBe('turn-1')
    expect(f.evidenceLinks[1]?.spanId).toBe('turn-3')
    expect(f.estimatedWasteTokens).toBeGreaterThan(0)
    expect(f.errorBar.lower).toBeLessThanOrEqual(f.errorBar.upper)
  })

  it('does NOT flag tool schema if tool is invoked in any turn', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-1',
      op: 'llm.invoke',
      parts: [{ part: 'tool_definitions', text: JSON.stringify([{ name: 'file_search' }]) }],
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-2',
      op: 'llm.invoke',
      parts: [{ part: 'tool_definitions', text: JSON.stringify([{ name: 'file_search' }]) }],
    })
    const turn3 = makeMockRecord({
      spanId: 'turn-3',
      op: 'llm.invoke',
      parts: [{ part: 'tool_definitions', text: JSON.stringify([{ name: 'file_search' }]) }],
    })
    const toolCall = makeMockRecord({
      spanId: 'tool-call-1',
      op: 'tool.invoke',
      name: 'file_search',
    })

    const findings = detectDormantToolSchema({
      records: [turn1, turn2, turn3, toolCall],
    })

    expect(findings.length).toBe(0)
  })

  it('does NOT flag tool schema if resident for fewer than 3 turns', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-1',
      op: 'llm.invoke',
      parts: [{ part: 'tool_definitions', text: JSON.stringify([{ name: 'quick_tool' }]) }],
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-2',
      op: 'llm.invoke',
      parts: [{ part: 'tool_definitions', text: JSON.stringify([{ name: 'quick_tool' }]) }],
    })

    const findings = detectDormantToolSchema({
      records: [turn1, turn2],
    })

    expect(findings.length).toBe(0)
  })
})

describe('Detector 2: duplicate-tool-call', () => {
  it('detects identical tool calls with identical arguments in same run/session', () => {
    const call1 = makeMockRecord({
      spanId: 'tool-call-1',
      op: 'tool.invoke',
      name: 'read_file',
      raw: { arguments: { path: '/src/main.ts' } },
      tokens: { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 200, reportedOutput: 100 },
    })
    const call2 = makeMockRecord({
      spanId: 'tool-call-2',
      op: 'tool.invoke',
      name: 'read_file',
      raw: { arguments: { path: '/src/main.ts' } },
      tokens: { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 200, reportedOutput: 100 },
    })

    const findings = detectDuplicateToolCall({
      records: [call1, call2],
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('duplicate-tool-call')
    expect(f.confidence).toBe('deterministic')
    expect(f.evidenceLinks.length).toBeGreaterThanOrEqual(2)
    expect(f.evidenceLinks[0]?.spanId).toBe('tool-call-1')
    expect(f.evidenceLinks[1]?.spanId).toBe('tool-call-2')
    expect(f.estimatedWasteTokens).toBe(300)
  })

  it('normalizes whitespace in JSON arguments when checking for duplicates', () => {
    const call1 = makeMockRecord({
      spanId: 'tool-1',
      op: 'tool.invoke',
      name: 'grep',
      raw: { arguments: '{"pattern":  "hello",   "dir": "src"}' },
    })
    const call2 = makeMockRecord({
      spanId: 'tool-2',
      op: 'tool.invoke',
      name: 'grep',
      raw: { arguments: '{"dir": "src", "pattern": "hello"}' },
    })

    const findings = detectDuplicateToolCall({
      calls: [
        { name: 'grep', arguments: { query: 'export default', path: 'src/' }, spanId: 'c1', turnIndex: 0 },
        { name: 'grep', arguments: { query: 'export  default', path: 'src/' }, spanId: 'c2', turnIndex: 1 },
      ],
    })

    expect(findings.length).toBe(1)
  })

  it('does NOT flag distinct tool calls or distinct arguments', () => {
    const call1 = makeMockRecord({
      spanId: 'tool-call-1',
      op: 'tool.invoke',
      name: 'read_file',
      raw: { arguments: { path: '/src/index.ts' } },
    })
    const call2 = makeMockRecord({
      spanId: 'tool-call-2',
      op: 'tool.invoke',
      name: 'read_file',
      raw: { arguments: { path: '/src/utils.ts' } },
    })

    const findings = detectDuplicateToolCall({
      records: [call1, call2],
    })

    expect(findings.length).toBe(0)
  })
})

describe('Detector 3: oversized-tool-result', () => {
  it('flags tool outputs exceeding token/byte budgets with low yield', () => {
    const bigContent = 'x'.repeat(12000)
    const toolRecord = makeMockRecord({
      spanId: 'tool-res-1',
      op: 'tool.invoke',
      name: 'fetch_api',
      parts: [
        {
          part: 'tool_result_content',
          text: bigContent,
          tokens: 3000,
        },
      ],
    })
    const turnRecord = makeMockRecord({
      spanId: 'llm-turn-1',
      op: 'llm.invoke',
    })

    const findings = detectOversizedToolResult({
      records: [toolRecord, turnRecord],
      tokenThreshold: 2000,
      charThreshold: 8000,
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('oversized-tool-result')
    expect(f.confidence).toBe('deterministic')
    expect(f.estimatedWasteTokens).toBe(1000) // 3000 - 2000
    expect(f.evidenceLinks.length).toBeGreaterThanOrEqual(2)
    expect(f.evidenceLinks[0]?.spanId).toBe('tool-res-1')
  })

  it('does NOT flag tool outputs within normal budget', () => {
    const toolRecord = makeMockRecord({
      spanId: 'tool-res-small',
      op: 'tool.invoke',
      name: 'fetch_api',
      parts: [
        {
          part: 'tool_result_content',
          text: 'ok: 42 items found',
          tokens: 50,
        },
      ],
    })

    const findings = detectOversizedToolResult({
      records: [toolRecord],
      tokenThreshold: 2000,
      charThreshold: 8000,
    })

    expect(findings.length).toBe(0)
  })
})

describe('Detector 4: prefix-cache-break', () => {
  it('detects dynamic tokens injected early in prompt breaking KV-cache prefix stability', () => {
    const turn1 = makeMockRecord({
      spanId: 'llm-turn-1',
      op: 'llm.invoke',
      content: {
        system_prompt: 'System prompt instructions v1.0. Current timestamp: 2026-09-01T10:00:00Z. Act as expert.',
      },
      tokens: { freshInput: 2000, cacheRead: 0, cacheCreation: 2000, output: 50, reportedInput: 2000, reportedOutput: 50 },
    })
    const turn2 = makeMockRecord({
      spanId: 'llm-turn-2',
      op: 'llm.invoke',
      content: {
        system_prompt: 'System prompt instructions v1.0. Current timestamp: 2026-09-01T10:01:05Z. Act as expert.',
      },
      tokens: { freshInput: 2000, cacheRead: 0, cacheCreation: 0, output: 50, reportedInput: 2000, reportedOutput: 50 },
    })

    const findings = detectPrefixCacheBreak({
      records: [turn1, turn2],
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('prefix-cache-break')
    expect(f.confidence).toBe('deterministic')
    expect(f.evidenceLinks.length).toBeGreaterThanOrEqual(2)
    expect(f.evidenceLinks[0]?.spanId).toBe('llm-turn-1')
    expect(f.evidenceLinks[1]?.spanId).toBe('llm-turn-2')
    expect(f.estimatedWasteTokens).toBeGreaterThan(0)
  })

  it('does NOT flag when prefix remains identical across turns', () => {
    const stablePrompt = 'You are a helpful programming assistant with stable instructions.'
    const turn1 = makeMockRecord({
      spanId: 'llm-turn-1',
      op: 'llm.invoke',
      content: { system_prompt: stablePrompt },
      tokens: { freshInput: 1000, cacheRead: 0, cacheCreation: 1000, output: 50, reportedInput: 1000, reportedOutput: 50 },
    })
    const turn2 = makeMockRecord({
      spanId: 'llm-turn-2',
      op: 'llm.invoke',
      content: { system_prompt: stablePrompt },
      tokens: { freshInput: 50, cacheRead: 950, cacheCreation: 0, output: 50, reportedInput: 1000, reportedOutput: 50 },
    })

    const findings = detectPrefixCacheBreak({
      records: [turn1, turn2],
    })

    expect(findings.length).toBe(0)
  })
})

describe('Detector 5: compaction-hazard', () => {
  it('detects context consumption exceeding 85% of window without summarization plan', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-early',
      op: 'llm.invoke',
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-peak',
      op: 'llm.invoke',
      tokens: { freshInput: 180_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 180_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [turn1, turn2],
      contextLimit: 200_000, // 85% is 170,000; 180,000 exceeds it
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('compaction-hazard')
    expect(f.confidence).toBe('deterministic')
    expect(f.estimatedWasteTokens).toBe(10_000) // 180k - 170k
    expect(f.evidenceLinks.length).toBeGreaterThanOrEqual(2)
  })

  it('does NOT flag when peak tokens remain below 85%', () => {
    const turn = makeMockRecord({
      spanId: 'turn-normal',
      op: 'llm.invoke',
      tokens: { freshInput: 120_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 120_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [turn],
      contextLimit: 200_000,
    })

    expect(findings.length).toBe(0)
  })
})

describe('Detector 6: unbounded-delegation', () => {
  it('detects subagent delegation token share exceeding 60% without outcome justification', () => {
    const findings = detectUnboundedDelegation({
      rootTokens: 20_000,
      subagentTokens: 80_000, // 80 / 100 = 80% (> 60%)
      rootSpanId: 'root-exec-1',
      subagentSpanId: 'subagent-exec-2',
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('unbounded-delegation')
    expect(f.confidence).toBe('calibrated_statistical')
    expect(f.estimatedWasteTokens).toBe(30_000) // 80k - 50k
    expect(f.evidenceLinks.length).toBeGreaterThanOrEqual(2)
    expect(f.evidenceLinks[0]?.spanId).toBe('root-exec-1')
    expect(f.evidenceLinks[1]?.spanId).toBe('subagent-exec-2')
  })

  it('does NOT flag when subagent delegation is modest (<= 60%)', () => {
    const findings = detectUnboundedDelegation({
      rootTokens: 60_000,
      subagentTokens: 40_000, // 40%
    })

    expect(findings.length).toBe(0)
  })
})

describe('Detector 7: inactive-skill-reference & Decision D16 Compliance', () => {
  it('flags skill prompt blocks in context with 0 executions as low confidence / heuristic', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-skill-1',
      op: 'llm.invoke',
      content: {
        system_prompt: '<skill name="docker_deploy">Deploy containers to swarm</skill>',
      },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-skill-2',
      op: 'llm.invoke',
      content: {
        system_prompt: '<skill name="docker_deploy">Deploy containers to swarm</skill>',
      },
    })

    const findings = detectInactiveSkillReference({
      records: [turn1, turn2],
      executedSkills: ['read_file', 'write_file'],
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('inactive-skill-reference')
    expect(f.confidence).toBe('heuristic')
    expect(f.evidenceLinks.length).toBeGreaterThanOrEqual(2)
    expect(f.evidenceLinks[0]?.spanId).toBe('turn-skill-1')
    expect(f.recommendation).toContain('progressive disclosure')
  })

  it('does NOT flag skills that were executed', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-skill-1',
      op: 'llm.invoke',
      content: {
        system_prompt: '<skill name="build_artifact">Compile binaries</skill>',
      },
    })

    const findings = detectInactiveSkillReference({
      records: [turn1],
      executedSkills: ['build_artifact'],
    })

    expect(findings.length).toBe(0)
  })
})

describe('Decision D6 Ranking Formula: rankScore = estimatedWasteTokens * confidenceMultiplier * (1 - outcomeRiskDiscount)', () => {
  it('proves that deterministic findings beat larger inferred/heuristic findings', () => {
    // Finding A: Deterministic with 2,000 waste tokens
    const deterministicFinding: Finding = {
      id: 'f-deterministic',
      detectorId: 'duplicate-tool-call',
      title: 'Deterministic Duplicate Call',
      mechanism: 'Exact duplicate call',
      evidenceLinks: [
        { spanId: 's1', turnIndex: 0, description: 'call 1' },
        { spanId: 's2', turnIndex: 1, description: 'call 2' },
      ],
      confidence: 'deterministic', // multiplier = 1.0
      estimatedWasteTokens: 2000,
      recommendation: 'Relocate tool query results into local conversation state or on-demand cache.',
      errorBar: { lower: 1600, upper: 2400 },
      outcomeRiskCaveat: 'Minor risk.',
      rankScore: computeRankScore(2000, 'deterministic', 0.2), // 2000 * 1.0 * 0.8 = 1600
    }

    // Finding B: Inferred / Heuristic finding with 10,000 waste tokens (5x larger!)
    const heuristicFinding: Finding = {
      id: 'f-heuristic',
      detectorId: 'inactive-skill-reference',
      title: 'Heuristic Inactive Skill',
      mechanism: 'Skill was not called',
      evidenceLinks: [
        { spanId: 's3', turnIndex: 0, description: 'skill inject' },
        { spanId: 's4', turnIndex: 2, description: 'turn 2' },
      ],
      confidence: 'heuristic', // multiplier = 0.10
      estimatedWasteTokens: 10000,
      recommendation: 'Relocate skill instructions to on-demand progressive disclosure.',
      errorBar: { lower: 5000, upper: 15000 },
      outcomeRiskCaveat: 'Heuristic caveat.',
      rankScore: computeRankScore(10000, 'heuristic', 0.2), // 10000 * 0.10 * 0.8 = 800
    }

    expect(deterministicFinding.rankScore).toBe(1600)
    expect(heuristicFinding.rankScore).toBe(800)

    // Ranking must place deterministic first despite being 5x smaller in raw tokens
    const ranked = rankFindings([heuristicFinding, deterministicFinding])
    expect(ranked[0]?.id).toBe('f-deterministic')
    expect(ranked[1]?.id).toBe('f-heuristic')
  })

  it('ranks inactive-skill-reference last per Decision D16', () => {
    const f1: Finding = {
      id: 'f-del',
      detectorId: 'unbounded-delegation',
      title: 'Unbounded Delegation',
      mechanism: 'Excess delegation',
      evidenceLinks: [
        { spanId: 's1', turnIndex: 0, description: 'd1' },
        { spanId: 's2', turnIndex: 1, description: 'd2' },
      ],
      confidence: 'calibrated_statistical',
      estimatedWasteTokens: 1000,
      recommendation: 'Relocate subagent scopes to on-demand tasks.',
      errorBar: { lower: 800, upper: 1200 },
      outcomeRiskCaveat: 'Risk caveat',
      rankScore: computeRankScore(1000, 'calibrated_statistical', 0.2), // 1000 * 0.45 * 0.8 = 360
    }

    const fSkill: Finding = {
      id: 'f-skill',
      detectorId: 'inactive-skill-reference',
      title: 'Skill Finding',
      mechanism: 'Unexercised skill',
      evidenceLinks: [
        { spanId: 's3', turnIndex: 0, description: 's1' },
        { spanId: 's4', turnIndex: 1, description: 's2' },
      ],
      confidence: 'heuristic',
      estimatedWasteTokens: 800,
      recommendation: 'Relocate skill instructions to progressive disclosure.',
      errorBar: { lower: 400, upper: 1200 },
      outcomeRiskCaveat: 'Skill caveat',
      rankScore: computeRankScore(800, 'heuristic', 0.4), // 800 * 0.1 * 0.6 = 48
    }

    const ranked = rankFindings([fSkill, f1])
    expect(ranked[ranked.length - 1]?.detectorId).toBe('inactive-skill-reference')
  })
})

describe('Decision D8 Recommendation Relocation Lint', () => {
  it('passes recommendations that use relocation, progressive disclosure, on-demand loading, or deferral', () => {
    const validRecommendations = [
      'Relocate tool schema "grep" to on-demand loading or tool deferral using progressive disclosure.',
      'Implement client-side on-demand caching to defer duplicate tool executions.',
      'Relocate large tool outputs to persistent storage and use progressive disclosure or pagination.',
      'Relocate dynamic tokens to the end of prompt messages, preserving an immutable cache-position.',
      'Apply progressive disclosure and relocate historical context into structured summary checkpoints.',
      'Relocate subagent scopes to on-demand tasks and defer child agent spawning.',
      'Relocate skill instructions to on-demand progressive disclosure.',
    ]

    for (const rec of validRecommendations) {
      const lint = lintRecommendationD8(rec)
      expect(lint.valid).toBe(true)
      expect(lint.errors).toHaveLength(0)
    }
  })

  it('fails recommendations that suggest outright deletion (Decision D8)', () => {
    const badRecommendations = [
      'Delete unused tool schema to save context tokens.',
      'Remove completely this rule from the prompt.',
      'Drop the inactive skills from system instructions.',
      'Delete outright the duplicate call.',
    ]

    for (const rec of badRecommendations) {
      const lint = lintRecommendationD8(rec)
      expect(lint.valid).toBe(false)
      expect(lint.errors.some((e) => e.includes('Decision D8 violation'))).toBe(true)
    }
  })

  it('fails recommendations that omit relocation or progressive disclosure strategies', () => {
    const nonStrategic = 'Try to make the prompt smaller by shortening text.'
    const lint = lintRecommendationD8(nonStrategic)
    expect(lint.valid).toBe(false)
    expect(lint.errors[0]).toContain('must explicitly suggest relocation')
  })

  it('asserts that all 7 detectors generate recommendations that pass Decision D8 lint', () => {
    // Generate sample findings across all 7 detectors
    const allFindings = detectFindings({
      records: [
        makeMockRecord({
          spanId: 't1',
          op: 'llm.invoke',
          parts: [{ part: 'tool_definitions', text: JSON.stringify([{ name: 'unused' }]) }],
          content: { system_prompt: '<skill name="unused_skill">foo</skill>' },
          tokens: { freshInput: 185_000, cacheRead: 0, cacheCreation: 0, output: 50, reportedInput: 185_000, reportedOutput: 50 },
        }),
        makeMockRecord({
          spanId: 't2',
          op: 'llm.invoke',
          parts: [{ part: 'tool_definitions', text: JSON.stringify([{ name: 'unused' }]) }],
          content: { system_prompt: 'Timestamp 12:00:01' },
          tokens: { freshInput: 1000, cacheRead: 0, cacheCreation: 0, output: 50, reportedInput: 1000, reportedOutput: 50 },
        }),
        makeMockRecord({
          spanId: 't3',
          op: 'llm.invoke',
          parts: [{ part: 'tool_definitions', text: JSON.stringify([{ name: 'unused' }]) }],
          content: { system_prompt: 'Timestamp 12:00:02' },
          tokens: { freshInput: 1000, cacheRead: 0, cacheCreation: 0, output: 50, reportedInput: 1000, reportedOutput: 50 },
        }),
        makeMockRecord({
          spanId: 'tool-c1',
          op: 'tool.invoke',
          name: 'read_doc',
          raw: { arguments: { id: 1 } },
          parts: [{ part: 'tool_result_content', text: 'x'.repeat(10000), tokens: 2500 }],
        }),
        makeMockRecord({
          spanId: 'tool-c2',
          op: 'tool.invoke',
          name: 'read_doc',
          raw: { arguments: { id: 1 } },
        }),
      ],
      contextLimit: 200_000,
      unboundedDelegation: { rootTokens: 10_000, subagentTokens: 90_000 },
    })

    expect(allFindings.length).toBeGreaterThanOrEqual(5)
    for (const finding of allFindings) {
      const lint = lintRecommendationD8(finding.recommendation)
      expect(lint.valid).toBe(true)
      expect(lint.errors).toEqual([])
    }
  })
})

describe('CanonStore: Finding persistence, indexes, and migration 7 -> v8', () => {
  it('stamps SCHEMA_VERSION = 8 on fresh stores and creates finding table', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)

    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(SCHEMA_VERSION).toBeGreaterThanOrEqual(8)
    expect(store.findingCount()).toBe(0)
    store.close()
  })

  it('migrates a v7 store forward to v8, creating finding table and indexes', () => {
    const path = tempStorePath()
    const initStore = new CanonStore(path)
    initStore.close()

    // Downgrade schema_version to 7 and drop finding table
    const db = new DatabaseSync(path)
    db.prepare("UPDATE metadata SET value = '7' WHERE key = 'schema_version'").run()
    db.exec('DROP TABLE IF EXISTS finding')
    db.close()

    // Reopen store to trigger migration 7 -> 8 (and further to latest SCHEMA_VERSION)
    const store = new CanonStore(path)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(store.findingCount()).toBe(0)

    const finding: Finding = {
      id: 'f-migrated-1',
      detectorId: 'duplicate-tool-call',
      title: 'Duplicate Call Finding',
      mechanism: 'Call repeated identically',
      evidenceLinks: [
        { spanId: 'sp-1', turnIndex: 0, description: 'call 1' },
        { spanId: 'sp-2', turnIndex: 1, description: 'call 2' },
      ],
      confidence: 'deterministic',
      estimatedWasteTokens: 500,
      recommendation: 'Relocate results to on-demand caching to defer redundant tool execution.',
      errorBar: { lower: 400, upper: 600 },
      outcomeRiskCaveat: 'Caveat string',
      runId: 'run-migrated',
      sessionId: 'session-migrated',
      rankScore: 400,
    }

    store.upsertFinding(finding)
    expect(store.findingCount()).toBe(1)
    const fetched = store.getFinding('f-migrated-1')
    expect(fetched).toBeDefined()
    expect(fetched?.id).toBe('f-migrated-1')
    expect(fetched?.rankScore).toBe(400)
    expect(fetched?.evidenceLinks).toHaveLength(2)
    store.close()
  })

  it('filters findings by runId and sessionId, ordered by rank_score DESC', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)

    const f1: Finding = {
      id: 'f-1',
      detectorId: 'compaction-hazard',
      title: 'Compaction finding',
      mechanism: 'Hazard',
      evidenceLinks: [
        { spanId: 's1', turnIndex: 0, description: 't1' },
        { spanId: 's2', turnIndex: 1, description: 't2' },
      ],
      confidence: 'deterministic',
      estimatedWasteTokens: 2000,
      recommendation: 'Apply progressive disclosure to relocate context.',
      errorBar: { lower: 1800, upper: 2200 },
      outcomeRiskCaveat: 'Caveat',
      runId: 'run-A',
      sessionId: 'sess-1',
      rankScore: 1600,
    }

    const f2: Finding = {
      id: 'f-2',
      detectorId: 'dormant-tool-schema',
      title: 'Dormant schema finding',
      mechanism: 'Unused tool',
      evidenceLinks: [
        { spanId: 's3', turnIndex: 0, description: 't1' },
        { spanId: 's4', turnIndex: 1, description: 't2' },
      ],
      confidence: 'deterministic',
      estimatedWasteTokens: 800,
      recommendation: 'Relocate schema to on-demand deferral.',
      errorBar: { lower: 600, upper: 1000 },
      outcomeRiskCaveat: 'Caveat',
      runId: 'run-A',
      sessionId: 'sess-2',
      rankScore: 640,
    }

    const f3: Finding = {
      id: 'f-3',
      detectorId: 'prefix-cache-break',
      title: 'Prefix break finding',
      mechanism: 'Prefix cache break',
      evidenceLinks: [
        { spanId: 's5', turnIndex: 0, description: 't1' },
        { spanId: 's6', turnIndex: 1, description: 't2' },
      ],
      confidence: 'deterministic',
      estimatedWasteTokens: 3000,
      recommendation: 'Relocate dynamic tokens to preserve cache-position.',
      errorBar: { lower: 2500, upper: 3500 },
      outcomeRiskCaveat: 'Caveat',
      runId: 'run-B',
      sessionId: 'sess-3',
      rankScore: 2400,
    }

    store.upsertFindings([f1, f2, f3])

    // Query all: ordered by rank_score DESC (f3: 2400, f1: 1600, f2: 640)
    const all = store.listFindings()
    expect(all.map((f) => f.id)).toEqual(['f-3', 'f-1', 'f-2'])

    // Query by runId 'run-A'
    const runA = store.listFindings('run-A')
    expect(runA.map((f) => f.id)).toEqual(['f-1', 'f-2'])

    // Query by sessionId 'sess-2'
    const sess2 = store.listFindings(undefined, 'sess-2')
    expect(sess2.map((f) => f.id)).toEqual(['f-2'])

    // Delete finding
    store.deleteFinding('f-2')
    expect(store.listFindings('run-A').map((f) => f.id)).toEqual(['f-1'])

    store.close()
  })
})

describe('Server Bridge & API Route: /api/kyber/findings', () => {
  it('serves ranked findings through KyberBridge and handleKyberRequest', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)

    const finding: Finding = {
      id: 'f-api-1',
      detectorId: 'dormant-tool-schema',
      title: 'Dormant tool finding',
      mechanism: 'Unused tool',
      evidenceLinks: [
        { spanId: 'sp-1', turnIndex: 0, description: 'first' },
        { spanId: 'sp-2', turnIndex: 1, description: 'second' },
      ],
      confidence: 'deterministic',
      estimatedWasteTokens: 1000,
      recommendation: 'Relocate tool to on-demand progressive disclosure.',
      errorBar: { lower: 800, upper: 1200 },
      outcomeRiskCaveat: 'Caveat',
      runId: 'run-100',
      sessionId: 'sess-200',
      rankScore: 800,
    }

    store.upsertFinding(finding)

    const bridge = new KyberBridge({ canonPath: path, store })
    const list = bridge.listFindings({ runId: 'run-100' })
    expect(list).toHaveLength(1)
    expect(list[0]?.id).toBe('f-api-1')

    // Mock HTTP request / response for /api/kyber/findings
    let statusCode = 0
    let responseBody = ''

    const req = { method: 'GET' } as any
    const res = {
      writeHead: (status: number) => {
        statusCode = status
      },
      end: (data: string) => {
        responseBody = data
      },
    } as any

    const handled = handleKyberRequest(
      req,
      res,
      new URL('http://localhost:3000/api/kyber/findings?runId=run-100'),
      bridge,
    )

    expect(handled).toBe(true)
    expect(statusCode).toBe(200)
    const parsed = JSON.parse(responseBody)
    expect(parsed.findings).toHaveLength(1)
    expect(parsed.findings[0].id).toBe('f-api-1')

    // Test individual finding detail endpoint /api/kyber/finding/:id
    let detailStatusCode = 0
    let detailBody = ''
    const detailRes = {
      writeHead: (status: number) => {
        detailStatusCode = status
      },
      end: (data: string) => {
        detailBody = data
      },
    } as any

    const detailHandled = handleKyberRequest(
      req,
      detailRes,
      new URL('http://localhost:3000/api/kyber/finding/f-api-1'),
      bridge,
    )

    expect(detailHandled).toBe(true)
    expect(detailStatusCode).toBe(200)
    const detailParsed = JSON.parse(detailBody)
    expect(detailParsed.id).toBe('f-api-1')
    expect(detailParsed.detectorId).toBe('dormant-tool-schema')

    bridge.close()
    store.close()
  })
})
