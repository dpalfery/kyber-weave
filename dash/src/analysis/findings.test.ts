import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import type { IncomingMessage, ServerResponse } from 'node:http'
import crypto from 'node:crypto'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { computeRankScore, detectCompactionHazard, detectDormantToolSchema, detectDuplicateToolCall, detectFindings, detectInactiveSkillReference, detectOversizedToolResult, detectPrefixCacheBreak, detectUnboundedDelegation, extractToolDefinitionsFromRecord, lintRecommendationD8, rankFindings, type Finding } from './findings.js'
import {
  CanonStore,
  SCHEMA_VERSION,
} from '../canon/store.js'
import type { CanonicalRecord, ContentPart } from '../canon/types.js'
import { buildHarnessRollup } from '../canon/harnesses.js'
import { KyberBridge } from '../server/bridge.js'
import { handleKyberRequest } from '../server/routes.js'

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
type MockRecordOverrides = Omit<Partial<CanonicalRecord>, 'parts'> & {
  parts?: readonly (ContentPart & { truncated?: boolean })[]
  attributes?: Record<string, unknown>
}

// Helper generating a minimal mock CanonicalRecord
function makeMockRecord(
  overrides: MockRecordOverrides = {},
): CanonicalRecord & { attributes?: Record<string, unknown> } {
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
    ...(overrides.attributes ? { attributes: overrides.attributes } : {}),
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
    expect(typeof finding.errorBar!.lower).toBe('number')
    expect(typeof finding.errorBar!.upper).toBe('number')
    expect(finding.errorBar!.lower).toBeLessThanOrEqual(finding.errorBar!.upper)
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
    // Text-only tool_definitions parts carry no harness-reported size: the
    // finding stays a coverage gap rather than inventing length/4 tokens.
    expect(f.estimatedWasteTokens).toBeUndefined()
    expect(f.errorBar).toBeUndefined()
    expect(f.measurementClass).toBe('coverage-gap')
  })

  it('does NOT treat text-derived schema sizes from records as measured deterministic waste', () => {
    const defText = JSON.stringify([{ name: 'unused_linter', description: 'runs linter' }])
    const turns = [1, 2, 3].map((n) =>
      makeMockRecord({
        spanId: `turn-${n}`,
        op: 'llm.invoke',
        parts: [{ part: 'tool_definitions', text: defText }],
      }),
    )

    const findings = detectDormantToolSchema({ records: turns })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBeUndefined()
    expect(findings[0]!.errorBar).toBeUndefined()
    expect(findings[0]!.measurementClass).toBe('coverage-gap')
    expect(findings[0]!.rankScore).toBe(0)
    expect(findings[0]!.measurementClass).not.toBe('deterministic')
    // length/4 of the serialized definition must not leak into the estimate
    const invented = Math.max(1, Math.ceil(JSON.stringify({ name: 'unused_linter', description: 'runs linter' }).length / 4)) * 3
    expect(findings[0]!.estimatedWasteTokens).not.toBe(invented)
  })

  it('multiplies a harness-reported part.tokens schema size across resident turns from records', () => {
    const defText = JSON.stringify([{ name: 'unused_linter', description: 'runs linter' }])
    const turns = [1, 2, 3].map((n) =>
      makeMockRecord({
        spanId: `turn-${n}`,
        op: 'llm.invoke',
        parts: [{ part: 'tool_definitions', text: defText, tokens: 200 }],
      }),
    )

    const findings = detectDormantToolSchema({ records: turns })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBe(600)
    expect(findings[0]!.measurementClass).toBe('deterministic')
    expect(findings[0]!.errorBar).toEqual({ lower: 480, upper: 720 })
  })

  it('does NOT attribute a measured part count when the catalogue array has unnamed siblings', () => {
    // One named tool plus an unnamed entry: the harness count covers the whole
    // blob and must not be pinned on the named tool alone.
    const defText = JSON.stringify([{ name: 'unused_linter' }, { description: 'unnamed sibling' }])
    const record = makeMockRecord({
      spanId: 'turn-1',
      op: 'llm.invoke',
      parts: [{ part: 'tool_definitions', text: defText, tokens: 200 }],
    })

    expect(extractToolDefinitionsFromRecord(record)).toEqual([{ name: 'unused_linter' }])

    const turns = [1, 2, 3].map((n) =>
      makeMockRecord({
        spanId: `turn-${n}`,
        op: 'llm.invoke',
        parts: [{ part: 'tool_definitions', text: defText, tokens: 200 }],
      }),
    )
    const findings = detectDormantToolSchema({ records: turns })
    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBeUndefined()
    expect(findings[0]!.measurementClass).toBe('coverage-gap')
  })

  it('merges measured toolDefinitions counts into residency already built from unmeasured records', () => {
    const defText = JSON.stringify([{ name: 'unused_linter' }])
    const turns = [1, 2, 3].map((n) =>
      makeMockRecord({
        spanId: `turn-${n}`,
        op: 'llm.invoke',
        parts: [{ part: 'tool_definitions', text: defText }],
      }),
    )

    const findings = detectDormantToolSchema({
      records: turns,
      toolDefinitions: [{ name: 'unused_linter', tokens: 200 }],
    })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBe(600)
    expect(findings[0]!.measurementClass).toBe('deterministic')
    expect(findings[0]!.errorBar).toEqual({ lower: 480, upper: 720 })
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

  it('flags uninvoked tools when session declares tools but only child tool.invoke records invoke a subset', () => {
    const tools = [
      { name: 'Bash', description: 'Run bash commands' },
      { name: 'Read', description: 'Read file contents' },
      { name: 'Write', description: 'Write file contents' },
      { name: 'Glob', description: 'Find matching files' },
    ]
    const toolDefsPart = {
      part: 'tool_definitions' as const,
      text: JSON.stringify(tools),
    }

    const turn1 = makeMockRecord({
      spanId: 'turn-1',
      op: 'llm.invoke',
      parts: [toolDefsPart],
    })
    const childInvoke = makeMockRecord({
      spanId: 'tool-invoke-1',
      parentSpanId: 'turn-1',
      op: 'tool.invoke',
      name: 'tool.invoke',
      attributes: {
        'gen_ai.tool.name': 'Bash',
      },
      raw: {
        'gen_ai.tool.name': 'Bash',
        arguments: { command: 'echo hello' },
      },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-2',
      op: 'llm.invoke',
      parts: [toolDefsPart],
    })
    const turn3 = makeMockRecord({
      spanId: 'turn-3',
      op: 'llm.invoke',
      parts: [toolDefsPart],
    })

    const findings = detectDormantToolSchema({
      records: [turn1, childInvoke, turn2, turn3],
    })

    const flaggedTools = findings.map((f) => f.title)
    expect(findings.length).toBe(3)
    expect(flaggedTools.some((t) => t.includes('"Bash"'))).toBe(false)
    expect(flaggedTools.some((t) => t.includes('"Read"'))).toBe(true)
    expect(flaggedTools.some((t) => t.includes('"Write"'))).toBe(true)
    expect(flaggedTools.some((t) => t.includes('"Glob"'))).toBe(true)
  })

  it('does NOT fabricate 150 tokens when schema size cannot be derived, marking estimatedWasteTokens undefined and measurementClass coverage-gap', () => {
    const findings = detectDormantToolSchema({
      sessionId: 'session-1',
      turnsCount: 4,
      toolDefinitions: [{ name: 'unused_linter' }],
    })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBeUndefined()
    expect(findings[0]!.errorBar).toBeUndefined()
    expect(findings[0]!.measurementClass).toBe('coverage-gap')
    expect(findings[0]!.rankScore).toBe(0)
    expect(findings[0]!.estimatedWasteTokens).not.toBe(600)
  })

  it('multiplies a measured schema size across resident turns', () => {
    const findings = detectDormantToolSchema({
      sessionId: 'session-1',
      turnsCount: 4,
      toolDefinitions: [{ name: 'unused_linter', tokens: 200 }],
    })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBe(800)
    expect(findings[0]!.measurementClass).toBe('deterministic')
    expect(findings[0]!.errorBar).toEqual({ lower: 640, upper: 960 })
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

  it('does NOT flag identical tool calls across different sessions', () => {
    const call1 = makeMockRecord({
      spanId: 'tool-call-s1',
      sessionId: 'session-1',
      op: 'tool.invoke',
      name: 'git_status',
      raw: { arguments: {} },
    })
    const call2 = makeMockRecord({
      spanId: 'tool-call-s2',
      sessionId: 'session-2',
      op: 'tool.invoke',
      name: 'git_status',
      raw: { arguments: {} },
    })

    const findings = detectDuplicateToolCall({
      records: [call1, call2],
    })

    expect(findings.length).toBe(0)
  })

  it('derives waste tokens from gen_ai.tool.result_bytes when reported tokens are 0', () => {
    const call1 = makeMockRecord({
      spanId: 'tool-call-1',
      sessionId: 'session-1',
      op: 'tool.invoke',
      name: 'read_file',
      raw: { arguments: { path: '/src/main.ts' } },
      tokens: { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 },
      attributes: { 'gen_ai.tool.result_bytes': 12000 },
    })
    const call2 = makeMockRecord({
      spanId: 'tool-call-2',
      sessionId: 'session-1',
      op: 'tool.invoke',
      name: 'read_file',
      raw: { arguments: { path: '/src/main.ts' }, 'gen_ai.tool.result_bytes': 12000 },
      tokens: { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 },
      attributes: { 'gen_ai.tool.result_bytes': 12000 },
    })

    const findings = detectDuplicateToolCall({
      records: [call1, call2],
    })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBe(3000)
    expect(findings[0]!.sessionId).toBe('session-1')
  })

  it('does NOT fabricate 250 tokens when tokens cannot be derived, marking estimatedWasteTokens undefined and measurementClass coverage-gap', () => {
    const call1 = makeMockRecord({
      spanId: 'tool-call-1',
      sessionId: 'session-1',
      op: 'tool.invoke',
      name: 'read_file',
      raw: { arguments: { path: '/src/main.ts' } },
      tokens: { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 },
    })
    const call2 = makeMockRecord({
      spanId: 'tool-call-2',
      sessionId: 'session-1',
      op: 'tool.invoke',
      name: 'read_file',
      raw: { arguments: { path: '/src/main.ts' } },
      tokens: { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 },
    })

    const findings = detectDuplicateToolCall({
      records: [call1, call2],
    })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBeUndefined()
    expect(findings[0]!.measurementClass).toBe('coverage-gap')
    expect(findings[0]!.rankScore).toBe(0)
  })

  it('keys duplicates on the full-arguments hash when present, not the truncated stored prefix', () => {
    const zeroed = { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 }
    // Both records store the same 64KB-truncated arguments object; only the
    // producer hash distinguishes the full pre-truncation arguments.
    const truncatedArgs = { path: 'src/file.ts', content: 'Z'.repeat(1000) }
    const mk = (spanId: string, argsHash: string) =>
      makeMockRecord({
        spanId,
        sessionId: 'session-1',
        op: 'tool.invoke',
        name: 'Write',
        raw: {
          arguments: truncatedArgs,
          'gen_ai.tool.arguments_hash': argsHash,
          'gen_ai.tool.arguments_truncated': true,
        },
        tokens: zeroed,
        parts: [],
      })

    // Different full arguments sharing a truncated prefix: not duplicates.
    expect(detectDuplicateToolCall({ records: [mk('a', 'hash-one'), mk('b', 'hash-two')] })).toHaveLength(0)
    // Same full-argument hash: a genuine duplicate.
    expect(detectDuplicateToolCall({ records: [mk('a', 'hash-same'), mk('b', 'hash-same')] })).toHaveLength(1)
  })

  it('detects duplicate child tool.invoke records within session turns', () => {
    const turn1 = makeMockRecord({
      spanId: 'llm-turn-1',
      op: 'llm.invoke',
    })
    const child1 = makeMockRecord({
      spanId: 'llm-turn-1-t0',
      parentSpanId: 'llm-turn-1',
      op: 'tool.invoke',
      name: 'tool.invoke',
      attributes: {
        'gen_ai.tool.name': 'read_file',
      },
      raw: {
        'gen_ai.tool.name': 'read_file',
        arguments: { path: '/src/main.ts' },
      },
      tokens: { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 },
    })
    const turn2 = makeMockRecord({
      spanId: 'llm-turn-2',
      op: 'llm.invoke',
    })
    const child2 = makeMockRecord({
      spanId: 'llm-turn-2-t0',
      parentSpanId: 'llm-turn-2',
      op: 'tool.invoke',
      name: 'tool.invoke',
      attributes: {
        'gen_ai.tool.name': 'read_file',
      },
      raw: {
        'gen_ai.tool.name': 'read_file',
        arguments: { path: '/src/main.ts' },
      },
      tokens: { freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 0, reportedOutput: 0 },
    })

    const findings = detectDuplicateToolCall({
      records: [turn1, child1, turn2, child2],
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('duplicate-tool-call')
    expect(f.confidence).toBe('deterministic')
    expect(f.title).toContain('read_file')
    expect(f.title).not.toContain('tool.invoke')
    expect(f.evidenceLinks.length).toBeGreaterThanOrEqual(2)
    expect(f.evidenceLinks[0]?.spanId).toBe('llm-turn-1-t0')
    expect(f.evidenceLinks[1]?.spanId).toBe('llm-turn-2-t0')
  })
  it("hashes tool call arguments with sha256 when building duplicate detection keys", () => {
    const createHashSpy = vi.spyOn(crypto, "createHash")

    const call1 = makeMockRecord({
      spanId: "tool-call-1",
      op: "tool.invoke",
      name: "write_file",
      raw: { arguments: { path: "/tmp/test.txt", content: "large content payload ".repeat(100) } },
    })
    const call2 = makeMockRecord({
      spanId: "tool-call-2",
      op: "tool.invoke",
      name: "write_file",
      raw: { arguments: { path: "/tmp/test.txt", content: "large content payload ".repeat(100) } },
    })

    const findings = detectDuplicateToolCall({
      records: [call1, call2],
    })

    expect(findings.length).toBe(1)
    expect(createHashSpy).toHaveBeenCalledWith("sha256")
    createHashSpy.mockRestore()
  })

})

describe('Detector 3: oversized-tool-result', () => {
  it('reads result_bytes from raw when attributes are present but carry no byte count', () => {
    // The hand-rolled attribute read used to stop at a present-but-keyless
    // attributes map and never consult raw. The truncated part means the
    // content in hand cannot size the result, so the raw fallback must run.
    const toolRecord = makeMockRecord({
      spanId: 'tool-res-raw-bytes',
      op: 'tool.invoke',
      name: 'fetch_api',
      attributes: { 'gen_ai.tool.name': 'fetch_api' },
      parts: [{ part: 'tool_result_content', text: 'short', truncated: true }],
      raw: { result: 'short', 'gen_ai.tool.result_bytes': 20000 },
    })

    const findings = detectOversizedToolResult({ records: [toolRecord] })

    expect(findings.length).toBe(1)
    expect(findings[0]!.title).toContain('20000 bytes')
  })

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

  it('flags child tool.invoke record when original unclipped result exceeds 100KB even if parts is truncated to 64KB', () => {
    const unclippedBytes = 120 * 1024 // 120KB > 100KB
    const truncated64k = 'a'.repeat(64 * 1024) // 64KB truncated part

    const turn = makeMockRecord({
      spanId: 'llm-turn-1',
      op: 'llm.invoke',
    })
    const toolRecord = makeMockRecord({
      spanId: 'tool-child-1',
      parentSpanId: 'llm-turn-1',
      op: 'tool.invoke',
      name: 'fetch_logs',
      parts: [
        {
          part: 'tool_result_content',
          text: truncated64k,
          truncated: true,
          tokens: 16_384,
        },
      ],
      attributes: {
        'gen_ai.tool.name': 'fetch_logs',
        'gen_ai.tool.result_bytes': unclippedBytes,
      },
      raw: {
        'gen_ai.tool.name': 'fetch_logs',
        'gen_ai.tool.result_bytes': unclippedBytes,
        result: truncated64k,
      },
    })

    const findings = detectOversizedToolResult({
      records: [turn, toolRecord],
      tokenThreshold: 25 * 1024,
      charThreshold: 100 * 1024,
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('oversized-tool-result')
    expect(f.confidence).toBe('deterministic')
    expect(f.evidenceLinks[0]?.spanId).toBe('tool-child-1')
    expect(f.estimatedWasteTokens).toBeGreaterThanOrEqual(5_000)
    expect(f.mechanism).toContain('fetch_logs')
  })

  it('differentiates UTF-8 multi-byte characters from byte limits and reports bytes', () => {
    const multiByteText = '日'.repeat(5000)
    const toolRecord = makeMockRecord({
      spanId: 'tool-res-multibyte',
      op: 'tool.invoke',
      name: 'fetch_data',
      parts: [
        {
          part: 'tool_result_content',
          text: multiByteText,
          tokens: 1500,
        },
      ],
    })

    const findings = detectOversizedToolResult({
      records: [toolRecord],
      tokenThreshold: 2000,
      charThreshold: 8000,
      byteThreshold: 8000,
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.title).toContain('bytes exceeding budget')
    expect(f.title).not.toContain('characters')
    expect(f.mechanism).toContain('15000 bytes')
  })

  it('does NOT flag tool result that exactly matches thresholds (strictly >)', () => {
    const text = 'a'.repeat(8000)
    const toolRecord = makeMockRecord({
      spanId: 'tool-res-exact',
      op: 'tool.invoke',
      name: 'fetch_data',
      parts: [
        {
          part: 'tool_result_content',
          text,
          tokens: 2000,
        },
      ],
    })

    const findings = detectOversizedToolResult({
      records: [toolRecord],
      tokenThreshold: 2000,
      charThreshold: 8000,
      byteThreshold: 8000,
    })

    expect(findings.length).toBe(0)
  })

  it('reports the character arm when charThreshold is tighter than the byte threshold', () => {
    // ASCII content: 200 characters == 200 bytes, so the byte arm (8000)
    // stays quiet and the character arm (100) fires with its own unit.
    const toolRecord = makeMockRecord({
      spanId: 'tool-res-chars',
      op: 'tool.invoke',
      name: 'fetch_api',
      parts: [{ part: 'tool_result_content', text: 'x'.repeat(200), tokens: 50 }],
    })

    const findings = detectOversizedToolResult({
      records: [toolRecord],
      tokenThreshold: 2000,
      charThreshold: 100,
      byteThreshold: 8000,
    })

    expect(findings.length).toBe(1)
    expect(findings[0]!.title).toContain('200 characters')
  })

  it('does NOT access lazy record.raw when record.name already holds the tool name', () => {
    let rawAccessed = false
    const toolRecord = makeMockRecord({
      spanId: 'tool-res-lazy',
      op: 'tool.invoke',
      name: 'my_tool',
      parts: [
        {
          part: 'tool_result_content',
          text: 'a'.repeat(9000),
        },
      ],
    })
    Object.defineProperty(toolRecord, 'raw', {
      get() {
        rawAccessed = true
        return {}
      },
    })

    const findings = detectOversizedToolResult({
      records: [toolRecord],
    })

    expect(findings.length).toBe(1)
    expect(rawAccessed).toBe(false)
  })

  it('detectFindings fires oversized-tool-result when child tool.invoke has gen_ai.tool.result_bytes exceeding 100KB', () => {
    const unclippedBytes = 150 * 1024 // 150KB
    const turn = makeMockRecord({ spanId: 'turn-1', op: 'llm.invoke' })
    const toolRecord = makeMockRecord({
      spanId: 'tool-child-2',
      parentSpanId: 'turn-1',
      op: 'tool.invoke',
      name: 'Bash',
      parts: [
        {
          part: 'tool_result_content',
          text: 'x'.repeat(64 * 1024),
          truncated: true,
        },
      ],
      attributes: {
        'gen_ai.tool.name': 'Bash',
        'gen_ai.tool.result_bytes': unclippedBytes,
      },
      raw: {
        'gen_ai.tool.name': 'Bash',
        'gen_ai.tool.result_bytes': unclippedBytes,
        result: 'x'.repeat(64 * 1024),
      },
    })

    const allFindings = detectFindings({
      records: [turn, toolRecord],
      oversizedToolResult: {
        tokenThreshold: 25 * 1024,
        charThreshold: 100 * 1024,
      },
    })

    const oversizedFinding = allFindings.find((f) => f.detectorId === 'oversized-tool-result')
    expect(oversizedFinding).toBeDefined()
    expect(oversizedFinding?.evidenceLinks[0]?.spanId).toBe('tool-child-2')
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

  it('does NOT fabricate 500 tokens when fresh input cannot be derived, marking estimatedWasteTokens undefined and measurementClass coverage-gap', () => {
    const findings = detectPrefixCacheBreak({
      sessionId: 'session-1',
      turns: [
        { spanId: 'llm-turn-1', turnIndex: 0, prefixText: 'System prompt v1. timestamp A. Act as expert.' },
        { spanId: 'llm-turn-2', turnIndex: 1, prefixText: 'System prompt v1. timestamp B. Act as expert.' },
      ],
    })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBeUndefined()
    expect(findings[0]!.errorBar).toBeUndefined()
    expect(findings[0]!.measurementClass).toBe('coverage-gap')
    expect(findings[0]!.rankScore).toBe(0)
  })

  it('uses the measured fresh-input size without a 150-token floor', () => {
    const findings = detectPrefixCacheBreak({
      sessionId: 'session-1',
      turns: [
        { spanId: 'llm-turn-1', turnIndex: 0, prefixText: 'System prompt v1. timestamp A. Act as expert.', freshInput: 80 },
        { spanId: 'llm-turn-2', turnIndex: 1, prefixText: 'System prompt v1. timestamp B. Act as expert.', freshInput: 80 },
      ],
    })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBe(80)
    expect(findings[0]!.measurementClass).toBe('deterministic')
    expect(findings[0]!.errorBar).toEqual({ lower: 64, upper: 100 })
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

  it('emits no finding when no source reported a window (issue #181)', () => {
    // 190,000 tokens is 95% of the 200,000 fallback, but the fallback is a
    // guess, not a measurement: an unknown window must surface as unknown
    // (honest unobservability), never as a deterministic 95% finding.
    const turn1 = makeMockRecord({
      spanId: 'turn-early',
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-peak',
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })

    expect(makeMockRecord({}).raw).toBeUndefined()
    const findings = detectCompactionHazard({ records: [turn1, turn2] })

    expect(findings).toHaveLength(0)
  })

  it('downgrades a peak above the reported window instead of claiming a percentage (issue #181)', () => {
    // A 1,278,417-token "single-turn peak" against a reported 200,000
    // window (639%) is physically impossible for one turn's context: the
    // attribution aggregates child calls. The spend is real signal, but the
    // single-turn reading is not deterministic.
    const turn = makeMockRecord({
      spanId: 'turn-peak',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 1_278_417, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 1_278_417, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({ records: [turn] })

    expect(findings).toHaveLength(1)
    const f = findings[0]!
    expect(f.confidence).toBe('heuristic')
    expect(f.measurementClass).toBe('inferred')
    expect(f.mechanism).toMatch(/aggregat/i)
    // The title must not print the impossible percentage as a claim: 639%
    // of a window is the attribution failure, not a measurement (review).
    expect(f.title).not.toMatch(/639%/)
    expect(f.title).toMatch(/exceeds/i)
    expect(f.payload?.contextLimitSource).toBe('reported')
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

  it('derives the context window from the reported gen_ai.request.max_context_tokens attribute', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-early',
      op: 'llm.invoke',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-peak',
      op: 'llm.invoke',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 900_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 900_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [turn1, turn2],
    })

    // 85% of the reported 1,000,000 window is 850,000; the 900,000 peak
    // exceeds it by 50,000 — not the 730,000 the fixed 200k default implies.
    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.detectorId).toBe('compaction-hazard')
    expect(f.estimatedWasteTokens).toBe(50_000)
    expect(f.mechanism).toContain('90%')
    expect(f.mechanism).toMatch(/1,?000,000/)
  })

  it('derives the window per session and attributes the finding to the session that fired', () => {
    const sessionATurn1 = makeMockRecord({
      spanId: 'session-a-early',
      op: 'llm.invoke',
      sessionId: 'session-a',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const sessionATurn2 = makeMockRecord({
      spanId: 'session-a-peak',
      op: 'llm.invoke',
      sessionId: 'session-a',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })
    const sessionBTurn1 = makeMockRecord({
      spanId: 'session-b-early',
      op: 'llm.invoke',
      sessionId: 'session-b',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const sessionBTurn2 = makeMockRecord({
      spanId: 'session-b-peak',
      op: 'llm.invoke',
      sessionId: 'session-b',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [sessionATurn1, sessionATurn2, sessionBTurn1, sessionBTurn2],
    })

    // 190,000 tokens exceed 85% of session-a's reported 200,000 window but
    // stay far under session-b's reported 1,000,000, so only session-a fires.
    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.sessionId).toBe('session-a')
    expect(findings.some((finding) => finding.sessionId === 'session-b')).toBe(false)
    expect(f.estimatedWasteTokens).toBe(20_000) // 190k - 170k (85% of 200k)
    expect(f.payload?.contextLimit).toBe(200_000)
    expect(f.payload?.contextLimitSource).toBe('reported')
  })

  it('emits one finding per session when both sessions exceed their own thresholds', () => {
    const sessionATurn1 = makeMockRecord({
      spanId: 'session-a-early',
      op: 'llm.invoke',
      sessionId: 'session-a',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const sessionATurn2 = makeMockRecord({
      spanId: 'session-a-peak',
      op: 'llm.invoke',
      sessionId: 'session-a',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })
    const sessionBTurn1 = makeMockRecord({
      spanId: 'session-b-early',
      op: 'llm.invoke',
      sessionId: 'session-b',
      raw: { 'gen_ai.request.max_context_tokens': 500_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const sessionBTurn2 = makeMockRecord({
      spanId: 'session-b-peak',
      op: 'llm.invoke',
      sessionId: 'session-b',
      raw: { 'gen_ai.request.max_context_tokens': 500_000 },
      tokens: { freshInput: 450_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 450_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [sessionATurn1, sessionATurn2, sessionBTurn1, sessionBTurn2],
    })

    // Both sessions clear their own 85% threshold — 190,000 over 170,000
    // (85% of 200,000) and 450,000 over 425,000 (85% of 500,000) — so each
    // gets its own finding measured against its own window. Collapsing the
    // groups back to run-level derivation would emit a single finding whose
    // numbers belong to whichever record happened to come first.
    expect(findings.length).toBe(2)
    expect(new Set(findings.map((finding) => finding.sessionId)).size).toBe(2)
    const sessionAFinding = findings.find((finding) => finding.sessionId === 'session-a')
    const sessionBFinding = findings.find((finding) => finding.sessionId === 'session-b')
    expect(sessionAFinding).toBeDefined()
    expect(sessionBFinding).toBeDefined()
    expect(sessionAFinding?.estimatedWasteTokens).toBe(20_000) // 190k - 170k (85% of 200k)
    expect(sessionBFinding?.estimatedWasteTokens).toBe(25_000) // 450k - 425k (85% of 500k)
    expect(sessionAFinding?.payload?.contextLimit).toBe(200_000)
    expect(sessionAFinding?.payload?.contextLimitSource).toBe('reported')
    expect(sessionBFinding?.payload?.contextLimit).toBe(500_000)
    expect(sessionBFinding?.payload?.contextLimitSource).toBe('reported')
  })

  it('groups records that carry no session id under their trace id, the key the store uses', () => {
    // Store-path shape: sessions are keyed by COALESCE(session_id, trace_id),
    // so a harness that never emits a session id still forms a session — keyed
    // by its trace id — and one run can carry several of them. Collapsing such
    // records into one placeholder bucket would measure them all against
    // whichever session reported a window first and stamp the finding with a
    // sessionId that matches no session row.
    const traceATurn1 = makeMockRecord({
      spanId: 'trace-a-early',
      traceId: 'trace-a',
      sessionId: '', // mirrors the absent session id the store path produces
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const traceATurn2 = makeMockRecord({
      spanId: 'trace-a-peak',
      traceId: 'trace-a',
      sessionId: '',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })
    const traceBTurn1 = makeMockRecord({
      spanId: 'trace-b-early',
      traceId: 'trace-b',
      sessionId: '',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const traceBTurn2 = makeMockRecord({
      spanId: 'trace-b-peak',
      traceId: 'trace-b',
      sessionId: '',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 195_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 195_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [traceATurn1, traceATurn2, traceBTurn1, traceBTurn2],
    })

    // trace-a's 190,000-token peak crosses 85% of its own 200,000 window while
    // trace-b's 195,000 stays far under its 1,000,000 — so only trace-a fires,
    // named by its canonical session key (the trace id), not a placeholder.
    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.sessionId).toBe('trace-a')
    expect(f.estimatedWasteTokens).toBe(20_000) // 190k - 170k (85% of trace-a's window)
    expect(f.payload?.contextLimit).toBe(200_000)
    expect(f.payload?.contextLimitSource).toBe('reported')
  })

  it('prefixes the canonical harness when one session key spans several harnesses', () => {
    // buildSessions and buildRuns name a session key shared by multiple
    // canonical harnesses `${harness}:${key}`; a finding for that session has
    // to carry the same id or it matches no session row.
    const claudeTurn1 = makeMockRecord({
      spanId: 'claude-early',
      sessionId: 'shared-sess',
      harness: 'claude', // canonicalizes to claude-unclassified
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const claudeTurn2 = makeMockRecord({
      spanId: 'claude-peak',
      sessionId: 'shared-sess',
      harness: 'claude',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })
    const cursorTurn1 = makeMockRecord({
      spanId: 'cursor-early',
      sessionId: 'shared-sess',
      harness: 'cursor',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const cursorTurn2 = makeMockRecord({
      spanId: 'cursor-peak',
      sessionId: 'shared-sess',
      harness: 'cursor',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [claudeTurn1, claudeTurn2, cursorTurn1, cursorTurn2],
    })

    // Only the claude-side group crosses 85% of its window, and it is stored
    // under the same prefixed id the session row uses.
    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.sessionId).toBe('claude-unclassified:shared-sess')
    expect(f.estimatedWasteTokens).toBe(20_000)
    expect(f.payload?.contextLimit).toBe(200_000)
    expect(f.id).toBe(`finding-compaction-hazard-${f.sessionId}-claude-peak`)
  })

  it('counts non-invocation records when deciding the prefixed canonical session id', () => {
    // buildSessions and buildRuns group every record of a key through
    // groupByCanonicalHarness, not just the turns: a key whose second
    // canonical harness appears only on a non-invocation span is still stored
    // as `${harness}:${key}`, so the finding for the invocation-side group
    // has to carry that same prefixed id.
    const claudeTurn1 = makeMockRecord({
      spanId: 'claude-early',
      sessionId: 'mixed-sess',
      harness: 'claude',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const claudeTurn2 = makeMockRecord({
      spanId: 'claude-peak',
      sessionId: 'mixed-sess',
      harness: 'claude',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })
    const cursorSpan = makeMockRecord({
      spanId: 'cursor-span',
      sessionId: 'mixed-sess',
      harness: 'cursor',
      op: 'tool.invoke',
      name: 'read_file',
    })

    const findings = detectCompactionHazard({
      records: [claudeTurn1, claudeTurn2, cursorSpan],
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.sessionId).toBe('claude-unclassified:mixed-sess')
    expect(f.estimatedWasteTokens).toBe(20_000)
  })

  it('does not count harness identities the builders exclude when prefixing', () => {
    // Gemini is excluded from canonical grouping (canonicalHarnessId returns
    // null and groupByCanonicalHarness drops it), so a key holding claude
    // invocations and a gemini invocation is stored unprefixed — the gemini
    // span counts toward no session row and must not force a prefix here.
    const claudeTurn1 = makeMockRecord({
      spanId: 'claude-early',
      sessionId: 'mixed-sess',
      harness: 'claude',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const claudeTurn2 = makeMockRecord({
      spanId: 'claude-peak',
      sessionId: 'mixed-sess',
      harness: 'claude',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })
    const geminiTurn = makeMockRecord({
      spanId: 'gemini-turn',
      sessionId: 'mixed-sess',
      harness: 'gemini',
      tokens: { freshInput: 1000, cacheRead: 0, cacheCreation: 0, output: 50, reportedInput: 1000, reportedOutput: 50 },
    })

    const findings = detectCompactionHazard({
      records: [claudeTurn1, claudeTurn2, geminiTurn],
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.sessionId).toBe('mixed-sess')
    expect(f.estimatedWasteTokens).toBe(20_000)
  })

  it('does not let a noncanonical turn become the group peak', () => {
    // A gemini span sharing the session key belongs to no canonical session
    // row: groupByCanonicalHarness drops it, so the builders could never
    // produce this session's findings from it. Letting its turn into the
    // group would fire a claude finding from a peak the claude session
    // never reached.
    const claudeTurn = makeMockRecord({
      spanId: 'claude-turn',
      sessionId: 'mixed-sess',
      harness: 'claude',
      tokens: { freshInput: 150_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 150_000, reportedOutput: 100 },
    })
    const geminiTurn = makeMockRecord({
      spanId: 'gemini-turn',
      sessionId: 'mixed-sess',
      harness: 'gemini',
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [claudeTurn, geminiTurn],
    })

    // 150,000 stays under 85% of the 200,000 default; the 190,000 gemini
    // reading is not this session's peak because the session was never
    // gemini's.
    expect(findings.length).toBe(0)
  })

  it('derives the group window from canonical turns only', () => {
    // Same contamination, window side: a gemini turn that reports a window
    // must not win the first-reported race for a group it does not belong
    // to, or the claude session is measured against a million tokens it
    // never had.
    const geminiTurn = makeMockRecord({
      spanId: 'gemini-turn',
      sessionId: 'mixed-sess',
      harness: 'gemini',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 1000, cacheRead: 0, cacheCreation: 0, output: 50, reportedInput: 1000, reportedOutput: 50 },
    })
    const claudeTurn1 = makeMockRecord({
      spanId: 'claude-early',
      sessionId: 'mixed-sess',
      harness: 'claude',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const claudeTurn2 = makeMockRecord({
      spanId: 'claude-peak',
      sessionId: 'mixed-sess',
      harness: 'claude',
      raw: { 'gen_ai.request.max_context_tokens': 200_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [geminiTurn, claudeTurn1, claudeTurn2],
    })

    // The claude session fires against its own reported 200,000 window:
    // 190,000 is 20,000 over the 85% line — not 19% of a borrowed window.
    // (Issue #181: had no turn reported a window, there would be no finding
    // at all rather than a default-window one.)
    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.sessionId).toBe('mixed-sess')
    expect(f.estimatedWasteTokens).toBe(20_000)
    expect(f.payload?.contextLimit).toBe(200_000)
    expect(f.payload?.contextLimitSource).toBe('reported')
  })

  it('does NOT flag a session whose reported window keeps its peak below 85%', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-early',
      op: 'llm.invoke',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-below-threshold',
      op: 'llm.invoke',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [turn1, turn2],
    })

    // 190,000 is 19% of the reported window — only the fixed 200k default
    // would read it as a hazard.
    expect(findings.length).toBe(0)
  })

  it('explicit contextLimit overrides the window derived from record attributes', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-early',
      op: 'llm.invoke',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-peak',
      op: 'llm.invoke',
      raw: { 'gen_ai.request.max_context_tokens': 1_000_000 },
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [turn1, turn2],
      contextLimit: 200_000, // 85% is 170,000; 190,000 exceeds it despite the reported window
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.estimatedWasteTokens).toBe(20_000)
    expect(f.mechanism).toMatch(/200,?000/)
  })

  it('marks a finding measured against a reported window with contextLimitSource "reported"', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-early',
      op: 'llm.invoke',
      raw: { 'gen_ai.request.max_context_tokens': 500_000 },
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-peak',
      op: 'llm.invoke',
      raw: { 'gen_ai.request.max_context_tokens': 500_000 },
      tokens: { freshInput: 450_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 450_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [turn1, turn2],
    })

    expect(findings.length).toBe(1)
    const f = findings[0]!
    expect(f.estimatedWasteTokens).toBe(25_000) // 450k - 425k (85% of 500k)
    expect(f.payload?.contextLimit).toBe(500_000)
    expect(f.payload?.contextLimitSource).toBe('reported')
  })

  it('emits no finding when only the default window would apply (issue #181)', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-early',
      op: 'llm.invoke',
      tokens: { freshInput: 100_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 100_000, reportedOutput: 100 },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-peak',
      op: 'llm.invoke',
      tokens: { freshInput: 190_000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 190_000, reportedOutput: 100 },
    })

    const findings = detectCompactionHazard({
      records: [turn1, turn2],
    })

    // No record reports a window, so there is no honest denominator: the
    // detector stays silent instead of presenting the 200,000 fallback as a
    // measurement (honest unobservability, issue #181).
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

  it('does NOT fabricate 120 tokens when skill-block size cannot be derived, marking estimatedWasteTokens undefined and measurementClass coverage-gap', () => {
    const findings = detectInactiveSkillReference({
      sessionId: 'session-1',
      referencedSkills: ['docker_deploy'],
      executedSkills: [],
    })

    expect(findings.length).toBe(1)
    expect(findings[0]!.estimatedWasteTokens).toBeUndefined()
    expect(findings[0]!.errorBar).toBeUndefined()
    expect(findings[0]!.measurementClass).toBe('coverage-gap')
    expect(findings[0]!.rankScore).toBe(0)
    expect(findings[0]!.confidence).toBe('heuristic')
  })

  it('multiplies a measured skill-block size across resident turns', () => {
    const turn1 = makeMockRecord({
      spanId: 'turn-skill-1',
      op: 'llm.invoke',
      content: { system_prompt: '<skill name="docker_deploy">Deploy containers to swarm</skill>' },
    })
    const turn2 = makeMockRecord({
      spanId: 'turn-skill-2',
      op: 'llm.invoke',
      content: { system_prompt: '<skill name="docker_deploy">Deploy containers to swarm</skill>' },
    })

    const findings = detectInactiveSkillReference({
      records: [turn1, turn2],
      executedSkills: [],
      skillTokens: { docker_deploy: 80 },
    })

    expect(findings.length).toBe(1)
    // turnsCount = max(record count, 3) stays the measured-path formula
    expect(findings[0]!.estimatedWasteTokens).toBe(240)
    expect(findings[0]!.measurementClass).toBe('inferred')
    expect(findings[0]!.errorBar).toEqual({ lower: 120, upper: 360 })
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

    const req = { method: 'GET' } as unknown as IncomingMessage
    const res = {
      writeHead: (status: number) => {
        statusCode = status
      },
      end: (data: string) => {
        responseBody = data
      },
    } as unknown as ServerResponse

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
    } as unknown as ServerResponse

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

  it('filters by detector and harness server-side with paged envelope (issue #191)', () => {
    // 30 findings: 12 duplicate-tool-call, 10 compaction-hazard,
    // 8 dormant-tool-schema, alternating cursor / claude-code owners.
    const path = tempStorePath()
    const store = new CanonStore(path)
    const detectors = [
      ...Array<string>(12).fill('duplicate-tool-call'),
      ...Array<string>(10).fill('compaction-hazard'),
      ...Array<string>(8).fill('dormant-tool-schema'),
    ]
    detectors.forEach((detectorId, i) => {
      store.upsertFinding({
        id: `f-page-${i}`,
        detectorId: detectorId as Finding['detectorId'],
        title: `Finding ${i}`,
        mechanism: 'm',
        evidenceLinks: [],
        confidence: 'deterministic',
        estimatedWasteTokens: 100,
        recommendation: 'r',
        errorBar: { lower: 80, upper: 120 },
        outcomeRiskCaveat: 'c',
        rankScore: 1000 - i,
        payload: { harness: i % 2 === 0 ? 'Cursor' : 'claude-code' },
      })
    })
    const bridge = new KyberBridge({ canonPath: path, store })

    const page = bridge.listFindingsPage({ detector: 'duplicate-tool-call' })
    expect(page.findings).toHaveLength(12)
    expect(page.total).toBe(12)
    // Counts stay scoped to harness/run/session but never to the detector
    // being browsed (review): narrowing the list must not evaporate the
    // chips that narrow it.
    expect(page.detectorCounts).toMatchObject({
      'duplicate-tool-call': 12,
      'compaction-hazard': 10,
      'dormant-tool-schema': 8,
    })

    const harnessed = bridge.listFindingsPage({ harness: 'cursor' })
    expect(harnessed.total).toBe(15)
    expect(harnessed.findings.every((f) => (f as unknown as { harness?: unknown }).harness?.toString().toLowerCase() === 'cursor')).toBe(true)

    const second = bridge.listFindingsPage({ limit: 10, offset: 10 })
    expect(second.findings).toHaveLength(10)
    expect(second.total).toBe(30)
    expect(second.limit).toBe(10)
    expect(second.offset).toBe(10)
    expect(second.findings[0]?.id).toBe('f-page-10')
    const counted = Object.values(second.detectorCounts).reduce((a, b) => a + b, 0)
    expect(counted).toBe(30)

    // The envelope is additive: legacy `findings` readers keep working.
    expect(Array.isArray(second.findings)).toBe(true)
    expect(second.findings[0]?.detectorId).toBe('duplicate-tool-call')

    bridge.close()
    store.close()
  })

  it('counts sessions with an unknown context window (issue #191, condition 3)', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)
    const session = (id: string, source: string | undefined) =>
      store.upsertSession({
        sessionId: id,
        harness: 'cursor',
        payload: {
          context: {
            measurable: true,
            contextLimit: 200_000,
            ...(source === undefined ? {} : { contextLimitSource: source }),
            turns: [],
          },
        },
      })
    session('s-known', 'reported')
    session('s-unknown-1', 'default')
    session('s-unknown-2', 'default')
    session('s-legacy', undefined)
    // Production serves the count off built rollups (review); the session
    // scan below is the fresh-store fallback.
    buildHarnessRollup(store)
    const bridge = new KyberBridge({ canonPath: path, store })

    expect(bridge.listFindingsPage({}).unknownWindowSessions).toBe(2)
    expect(bridge.listFindingsPage({ harness: 'cursor' }).unknownWindowSessions).toBe(2)
    expect(bridge.listFindingsPage({ harness: 'claude-code' }).unknownWindowSessions).toBe(0)

    bridge.close()
    store.close()
  })

  it('falls back to the session scan when rollups are stale (review M2)', () => {
    // A rollup sum is exact only when rollups cover every session: a session
    // recorded after the last build must still be counted, never silently
    // dropped from a partial sum wearing an exact label.
    const path = tempStorePath()
    const store = new CanonStore(path)
    const session = (id: string) =>
      store.upsertSession({
        sessionId: id,
        harness: 'cursor',
        payload: { context: { measurable: true, contextLimit: 200_000, contextLimitSource: 'default', turns: [] } },
      })
    session('s-old')
    buildHarnessRollup(store)
    session('s-new')
    const bridge = new KyberBridge({ canonPath: path, store })

    expect(bridge.listFindingsPage({}).unknownWindowSessions).toBe(2)
    expect(bridge.listFindingsPage({ harness: 'cursor' }).unknownWindowSessions).toBe(2)

    bridge.close()
    store.close()
  })

  it('normalizes folded front-end names before filtering (review)', () => {
    // `cursor-agent` is `cursor` at the derived layer (issue #182): a query
    // under the legacy name must answer under the folded owner in every half
    // of the envelope — findings and counts alike.
    const path = tempStorePath()
    const store = new CanonStore(path)
    store.upsertFinding({
      id: 'f-fold-1',
      detectorId: 'duplicate-tool-call',
      title: 't',
      mechanism: 'm',
      evidenceLinks: [],
      confidence: 'deterministic',
      estimatedWasteTokens: 100,
      recommendation: 'r',
      errorBar: { lower: 80, upper: 120 },
      outcomeRiskCaveat: 'c',
      rankScore: 100,
      payload: { harness: 'cursor' },
    })
    const bridge = new KyberBridge({ canonPath: path, store })

    expect(bridge.listFindingsPage({ harness: 'cursor-agent' }).total).toBe(1)
    expect(bridge.listFindingsPage({ harness: 'cursor-agent' }).unknownWindowSessions).toBe(
      bridge.listFindingsPage({ harness: 'cursor' }).unknownWindowSessions,
    )

    bridge.close()
    store.close()
  })

  it('scopes the unknown-window count to the selected run and session (review)', () => {
    // `/findings?sessionId=s-known` must not report unknown-window sessions
    // from the rest of the workspace: the count is "in scope", matching
    // the narrowed findings.
    const path = tempStorePath()
    const store = new CanonStore(path)
    const session = (id: string, source: string) =>
      store.upsertSession({
        sessionId: id,
        harness: 'cursor',
        payload: { context: { measurable: true, contextLimit: 200_000, contextLimitSource: source, turns: [] } },
      })
    session('s-known', 'reported')
    session('s-unknown', 'default')
    store.upsertRun({
      runId: 'run-1',
      harness: 'cursor',
      groupingBasis: 'derived',
      groupingRule: 'session_fallback',
      executionCount: 2,
    })
    for (const [executionId, sessionId] of [['exec-1', 's-known'], ['exec-2', 's-unknown']] as const) {
      store.upsertExecution({
        executionId,
        runId: 'run-1',
        sessionId,
        harness: 'cursor',
        isRoot: true,
        parentLinkage: 'measured',
      })
    }
    const bridge = new KyberBridge({ canonPath: path, store })

    expect(bridge.listFindingsPage({ sessionId: 's-known' }).unknownWindowSessions).toBe(0)
    expect(bridge.listFindingsPage({ sessionId: 's-unknown' }).unknownWindowSessions).toBe(1)
    // A harness that does not own the session scopes the count to zero,
    // matching the (also empty) narrowed findings (review S3a).
    expect(bridge.listFindingsPage({ sessionId: 's-unknown', harness: 'claude-code' }).unknownWindowSessions).toBe(0)
    expect(bridge.listFindingsPage({ sessionId: 's-unknown', harness: 'cursor' }).unknownWindowSessions).toBe(1)
    // A session id the store never held scopes to zero (absent, review).
    expect(bridge.listFindingsPage({ sessionId: 'no-such-session', harness: 'cursor' }).unknownWindowSessions).toBe(0)
    expect(bridge.listFindingsPage({ runId: 'run-1' }).unknownWindowSessions).toBe(1)
    expect(bridge.listFindingsPage({}).unknownWindowSessions).toBe(1)
    // The run scope folds legacy front-end names the same way (review).
    expect(bridge.listFindingsPage({ runId: 'run-1', harness: 'cursor-agent' }).unknownWindowSessions).toBe(1)
    expect(bridge.listFindingsPage({ runId: 'run-1', harness: 'claude-code' }).unknownWindowSessions).toBe(0)

    bridge.close()
    store.close()
  })
})

describe('unknown-window coverage guard (issue #191, review round 2)', () => {
  const session = (store: CanonStore, id: string, harness: string, source: string) =>
    store.upsertSession({
      sessionId: id,
      harness,
      payload: { context: { measurable: true, contextLimit: 200_000, contextLimitSource: source, turns: [] } },
    })

  it('does not report one harness rollup as the whole-workspace total', () => {
    // `buildHarnessRollup` takes a single harness, so a store can hold cursor
    // and claude sessions with only a cursor rollup between them. Summing the
    // rollups would answer "2" — cursor's count — for the entire workspace.
    const path = tempStorePath()
    const store = new CanonStore(path)
    session(store, 's-cursor-1', 'cursor', 'default')
    session(store, 's-cursor-2', 'cursor', 'default')
    session(store, 's-claude-1', 'claude-code', 'default')
    buildHarnessRollup(store, 'cursor')
    const bridge = new KyberBridge({ canonPath: path, store })

    // The workspace total is 3, not cursor's 2.
    expect(bridge.listFindingsPage({}).unknownWindowSessions).toBe(3)

    bridge.close()
    store.close()
  })

  it('still reads the rollups when they do cover every stored harness', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)
    session(store, 's-cursor-1', 'cursor', 'default')
    session(store, 's-claude-1', 'claude-code', 'default')
    buildHarnessRollup(store)
    const bridge = new KyberBridge({ canonPath: path, store })

    expect(bridge.listFindingsPage({}).unknownWindowSessions).toBe(2)

    bridge.close()
    store.close()
  })

  it('counts a legacy run-scoped harness name under its folded owner', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)
    session(store, 's-cursor-1', 'cursor-agent', 'default')
    store.upsertRun({
      runId: 'run-legacy',
      harness: 'cursor-agent',
      groupingBasis: 'derived',
      groupingRule: 'session_fallback',
      executionCount: 1,
    })
    store.upsertExecution({
      executionId: 'exec-legacy',
      runId: 'run-legacy',
      sessionId: 's-cursor-1',
      harness: 'cursor-agent',
      isRoot: true,
      parentLinkage: 'measured',
    })
    const bridge = new KyberBridge({ canonPath: path, store })

    // `cursor-agent` is `cursor` at the derived layer: the run scope must not
    // silently answer 0 for the legacy name.
    expect(bridge.listFindingsPage({ runId: 'run-legacy', harness: 'cursor-agent' }).unknownWindowSessions).toBe(1)
    expect(bridge.listFindingsPage({ runId: 'run-legacy', harness: 'cursor' }).unknownWindowSessions).toBe(1)

    bridge.close()
    store.close()
  })
})

describe('Findings route paging validation (council review)', () => {
  it('rejects malformed limit/offset instead of silently truncating', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)
    const bridge = new KyberBridge({ canonPath: path, store })
    const invoke = (href: string): { status: number; body: string } => {
      let status = 0
      let body = ''
      const req = { method: 'GET' } as unknown as import('node:http').IncomingMessage
      const res = {
        writeHead: (code: number) => {
          status = code
        },
        end: (data: string) => {
          body = data
        },
      } as unknown as import('node:http').ServerResponse
      handleKyberRequest(req, res, new URL(href), bridge)
      return { status, body }
    }

    expect(invoke('http://localhost:3000/api/kyber/findings?limit=10abc').status).toBe(400)
    expect(invoke('http://localhost:3000/api/kyber/findings?offset=-5').status).toBe(400)
    expect(invoke('http://localhost:3000/api/kyber/findings?limit=10&offset=0').status).toBe(200)
    expect(invoke('http://localhost:3000/api/kyber/findings').status).toBe(200)

    bridge.close()
    store.close()
  })
})
