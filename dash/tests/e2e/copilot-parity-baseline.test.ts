// Unit contracts for the sanitized Copilot parity baseline and cross-harness parity matrix.
//
// These assertions pin the two things every later parity test relies on: the observed
// `~/.kyberdash/canon.db` Copilot shape (schema version 30) with zero committed prompts or
// paths, and the canonical harness set that matches the file fixtures and stored OTLP
// contracts already exercised by the isolated Playwright suite.
import { describe, expect, it } from 'vitest'

import {
  ALL_COPILOT_PARITY_DIMENSIONS,
  COPILOT_DB_BASELINE_SNAPSHOT,
  HARNESS_PARITY_MATRIX,
  assertNoRawCanonDbLeak,
  getParityExpectation,
  listParityExpectations,
  normalizeParityHarnessId,
  type CopilotParityDimension,
  type HarnessParityExpectation,
} from '../../e2e/copilot-parity-baseline.js'
import { FILE_HARNESS_FIXTURES } from '../../e2e/file-harness-contracts.js'
import {
  COPILOT_BASELINE_CONTRACT,
  STORED_OTLP_HARNESS_CONTRACTS,
  buildCopilotBaselineFixture,
  type OtlpSpanWire,
} from '../../e2e/otel-harness-payloads.js'

const NON_COLLECTABLE_REGISTRY_ENTRIES = ['copilot-agent', 'claude-unclassified', 'codex-desktop'] as const
const EXCLUDED_HARNESSES = ['windsurf', 'aider', 'gemini'] as const
const ALIAS_FOLDS: ReadonlyArray<readonly [raw: string, canonical: string]> = [
  ['claude-desktop', 'claude-code'],
  ['claude', 'claude-unclassified'],
  ['copilot-chat', 'copilot-vscode'],
  ['github-copilot', 'copilot'],
  ['cursor-agent', 'cursor'],
  ['cascade', 'windsurf'],
  ['roo', 'roo-code'],
  ['roo-cline', 'roo-code'],
  ['openai-codex', 'codex-unclassified'],
  ['agy', 'antigravity'],
  ['kilo', 'kilo-shared-runtime'],
  ['kilo-code', 'kilo-shared-runtime'],
  ['kilo-anything-new', 'kilo-shared-runtime'],
  ['kimicode', 'kimi-code'],
  ['command-code', 'commandcode'],
]
const PRESERVED_IDS = [
  'claude-cli',
  'copilot-cli',
  'copilot-vscode',
  'copilot-jetbrains',
  'copilot-agent',
  'codex-cli',
  'codex-desktop',
  'codex-unclassified',
  'antigravity-cli',
  'antigravity-ide',
  'kilo-vscode-legacy',
]

function attrString(span: OtlpSpanWire, key: string): string | undefined {
  const attribute = span.attributes.find((candidate) => candidate.key === key)
  if (attribute === undefined) return undefined
  return 'stringValue' in attribute.value ? attribute.value.stringValue : undefined
}

function hasAttribute(span: OtlpSpanWire, key: string): boolean {
  return span.attributes.some((attribute) => attribute.key === key)
}

function allSpans(): readonly OtlpSpanWire[] {
  return buildCopilotBaselineFixture().tracePayload.resourceSpans[0].scopeSpans[0].spans
}

describe('COPILOT_DB_BASELINE_SNAPSHOT', () => {
  it('pins the observed canon.db schema and detector versions', () => {
    expect(COPILOT_DB_BASELINE_SNAPSHOT.harness).toBe('copilot')
    expect(COPILOT_DB_BASELINE_SNAPSHOT.sourceKind).toBe('otlp')
    expect(COPILOT_DB_BASELINE_SNAPSHOT.schemaVersion).toBe(30)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.detectorVersion).toBe(8)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.projectionSchemaVersion).toBe(30)
  })

  it('pins the observed Copilot OTLP collection dimensions', () => {
    expect(COPILOT_DB_BASELINE_SNAPSHOT.sessions).toBe(3)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.runs).toBe(3)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.turns).toBe(118)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.recordsAtSnapshot).toBe(316)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.toolDefinitions.rows).toBe(1_090)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.toolDefinitions.turnsWithDefinitions).toBe(19)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.agentInvocations).toBe(6)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.skills).toBe(16)
  })

  it('pins measured context, schema, and token projection at 100%', () => {
    expect(COPILOT_DB_BASELINE_SNAPSHOT.tokensRecordedPercent).toBe(100)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.projection.contextPercent).toBe(100)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.projection.schemaPercent).toBe(100)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.projection.tokensPercent).toBe(100)
  })

  it('pins the system prompt carry-forward and 65,536-byte truncation threshold', () => {
    expect(COPILOT_DB_BASELINE_SNAPSHOT.systemPrompt.storedParts).toBe(25)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.systemPrompt.projectedTurns).toBe(118)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.systemPrompt.truncatedAtBytes).toBe(65_536)
    expect(COPILOT_DB_BASELINE_SNAPSHOT.systemPrompt.truncatedParts).toBe(23)
  })

  it('records the content_part_withheld gap markers and missing tool text', () => {
    const { contentPartWithheld, gaps } = COPILOT_DB_BASELINE_SNAPSHOT
    expect(contentPartWithheld.conversationHistory).toEqual({ withheld: 193, total: 205 })
    expect(contentPartWithheld.toolResult).toEqual({ withheld: 183, total: 183 })
    expect(gaps).toContain('content_part_withheld')
    expect(gaps).toContain('tool_call_input_text_absent')
    expect(gaps).toContain('tool_call_result_text_absent')
  })

  it('encodes the dual Copilot CLI file-transcript shape from CopilotCliMapper', () => {
    const copilotCli = COPILOT_DB_BASELINE_SNAPSHOT.copilotCli
    expect(copilotCli.harness).toBe('copilot-cli')
    expect(copilotCli.systemPromptBucket).toBe('system_prompt')
    expect(copilotCli.conversationHistoryBucket).toBe('conversation_history')
    expect(copilotCli.toolResultBucket).toBe('tool_result_content')
    expect(copilotCli.emitsLlmInvokePerAssistantMessage).toBe(true)
    expect(copilotCli.emitsToolInvokeWhenExecutionStartedOrCompleted).toBe(true)
    expect(copilotCli.subagentRecordsFromDelegationCalls).toBe(true)
    expect(copilotCli.toolResultWithheldWhenDocument).toBe(true)
  })

  it('commits no raw prompts, titles, paths, or PII', () => {
    expect(() => assertNoRawCanonDbLeak(COPILOT_DB_BASELINE_SNAPSHOT)).not.toThrow()
  })
})

describe('normalizeParityHarnessId', () => {
  for (const [raw, canonical] of ALIAS_FOLDS) {
    it(`folds ${raw} to ${canonical}`, () => {
      expect(normalizeParityHarnessId(raw)).toBe(canonical)
    })
  }

  it('keeps split client surfaces distinct', () => {
    for (const preserved of PRESERVED_IDS) {
      expect(normalizeParityHarnessId(preserved)).toBe(preserved)
    }
  })

  it('keeps the canonical base identifiers stable', () => {
    for (const stable of [
      'claude-code',
      'cursor',
      'windsurf',
      'roo-code',
      'cline',
      'cline-cli',
      'aider',
      'codex',
      'gemini',
      'antigravity',
      'opencode',
      'kilo-shared-runtime',
      'kimi-code',
      'commandcode',
    ]) {
      expect(normalizeParityHarnessId(stable)).toBe(stable)
    }
  })

  it('trims surrounding whitespace and lowercases ASCII before folding', () => {
    expect(normalizeParityHarnessId('  Claude-Desktop  ')).toBe('claude-code')
    expect(normalizeParityHarnessId(' CLAUDE-CLI ')).toBe('claude-cli')
    expect(normalizeParityHarnessId(' KILO ')).toBe('kilo-shared-runtime')
  })

  it('lowercases only ASCII characters, matching ToAsciiLower', () => {
    expect(normalizeParityHarnessId('ÉCLAIR')).toBe('Éclair')
  })
})

describe('HARNESS_PARITY_MATRIX', () => {
  it('lists every harness exactly once', () => {
    const ids = HARNESS_PARITY_MATRIX.map((entry) => entry.harness)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('matches the file fixtures, OTLP contracts, zcode, non-collectable entries, and exclusions', () => {
    const fileHarnessIds = FILE_HARNESS_FIXTURES.map((fixture) => fixture.contract.harness)
    expect(new Set(fileHarnessIds).size).toBe(18)

    const otlpHarnessIds = STORED_OTLP_HARNESS_CONTRACTS.map((contract) => contract.harness)
    expect(new Set(otlpHarnessIds).size).toBe(7)

    const expected = new Set<string>([
      ...fileHarnessIds,
      ...otlpHarnessIds,
      'zcode',
      ...NON_COLLECTABLE_REGISTRY_ENTRIES,
      ...EXCLUDED_HARNESSES,
    ])
    expect(expected.size).toBe(28)
    expect(new Set(HARNESS_PARITY_MATRIX.map((entry) => entry.harness))).toEqual(expected)
  })

  it('marks every file fixture as a non-OTLP local source', () => {
    const fileHarnessIds = new Set(FILE_HARNESS_FIXTURES.map((fixture) => fixture.contract.harness))
    for (const entry of HARNESS_PARITY_MATRIX) {
      if (!fileHarnessIds.has(entry.harness)) continue
      expect(entry.supportTier).toBe('local-files')
      expect(entry.sourceKind).toBe('file')
    }
  })

  it('marks OTLP-only harnesses as testable OTLP sources', () => {
    const fileHarnessIds = new Set(FILE_HARNESS_FIXTURES.map((fixture) => fixture.contract.harness))
    const otlpHarnessIds = new Set(STORED_OTLP_HARNESS_CONTRACTS.map((contract) => contract.harness))
    for (const harness of otlpHarnessIds) {
      const entry = getParityExpectation(harness)
      expect(entry.testable).toBe(true)
      if (!fileHarnessIds.has(harness)) {
        expect(entry.supportTier).toBe('otlp-only')
        expect(entry.sourceKind).toBe('otlp')
      }
    }
  })

  it('records the non-collectable registry entries as untestable', () => {
    for (const harness of NON_COLLECTABLE_REGISTRY_ENTRIES) {
      expect(getParityExpectation(harness).testable).toBe(false)
    }
  })

  it('records the excluded harnesses with the excluded tier', () => {
    for (const harness of EXCLUDED_HARNESSES) {
      const entry = getParityExpectation(harness)
      expect(entry.supportTier).toBe('excluded')
      expect(entry.testable).toBe(false)
    }
  })

  it('gives the Copilot baseline every content dimension', () => {
    const copilot: HarnessParityExpectation = getParityExpectation('copilot')
    const contentDimensions: readonly CopilotParityDimension[] = ALL_COPILOT_PARITY_DIMENSIONS.filter(
      (dimension) => dimension !== 'ingest-diagnostic',
    )
    for (const dimension of contentDimensions) {
      expect(copilot.expectedDimensions).toContain(dimension)
    }
  })

  it('uses only known dimensions and lowercase gap markers', () => {
    const known = new Set<string>(ALL_COPILOT_PARITY_DIMENSIONS)
    for (const entry of HARNESS_PARITY_MATRIX) {
      for (const dimension of entry.expectedDimensions) {
        expect(known.has(dimension)).toBe(true)
      }
      for (const gap of entry.knownGaps) {
        expect(gap).toMatch(/^[a-z0-9_:-]+$/)
      }
    }
  })

  it('uses canonical lowercase harness identifiers', () => {
    for (const entry of HARNESS_PARITY_MATRIX) {
      expect(entry.harness).toBe(entry.harness.trim().toLowerCase())
    }
  })

  it('commits no raw canon.db content', () => {
    expect(() => assertNoRawCanonDbLeak(HARNESS_PARITY_MATRIX)).not.toThrow()
  })
})

describe('listParityExpectations', () => {
  it('returns the whole matrix with no filter', () => {
    expect(listParityExpectations()).toHaveLength(HARNESS_PARITY_MATRIX.length)
  })

  it('filters to the 22 HTTP-testable harnesses', () => {
    const testable = listParityExpectations({ testableOnly: true })
    expect(testable).toHaveLength(22)
    expect(testable.every((entry) => entry.testable)).toBe(true)
  })

  it('filters by support tier', () => {
    const classified = listParityExpectations({ supportTier: 'classified-only' })
    expect(classified.map((entry) => entry.harness).sort()).toEqual(['claude-unclassified', 'codex-desktop'])
  })

  it('filters by source kind', () => {
    expect(listParityExpectations({ sourceKind: 'file' })).toHaveLength(18)
    expect(listParityExpectations({ sourceKind: 'otlp', testableOnly: true })).toHaveLength(3)
  })

  it('combines filters with AND and can return an empty set', () => {
    expect(listParityExpectations({ sourceKind: 'otlp', supportTier: 'unevidenced' })).toEqual([])
  })

  it('returns a fresh array each call', () => {
    const first = listParityExpectations()
    const second = listParityExpectations()
    expect(first).not.toBe(second)
    expect(first).toEqual(second)
  })
})

describe('getParityExpectation', () => {
  it('returns a matrix entry by canonical id', () => {
    expect(getParityExpectation('copilot').harness).toBe('copilot')
  })

  it('normalizes an alias before lookup', () => {
    expect(getParityExpectation('  Claude-Desktop  ').harness).toBe('claude-code')
    expect(getParityExpectation('copilot-chat').harness).toBe('copilot-vscode')
  })

  it('refuses an empty id', () => {
    expect(() => getParityExpectation('')).toThrow(/Unknown parity harness: /)
  })

  it('refuses an unknown id', () => {
    expect(() => getParityExpectation('not-a-harness')).toThrow(/Unknown parity harness: not-a-harness/)
  })
})

describe('assertNoRawCanonDbLeak', () => {
  it('refuses a forbidden raw-content key', () => {
    expect(() => assertNoRawCanonDbLeak({ prompt: 'verbatim user prompt' })).toThrow(
      /Parity fixture violates non-PII boundary: /,
    )
  })

  it('refuses nested raw content', () => {
    expect(() => assertNoRawCanonDbLeak({ session: { title: 'My private project' } })).toThrow(
      /Parity fixture violates non-PII boundary: /,
    )
  })

  it('refuses a home directory path value', () => {
    expect(() => assertNoRawCanonDbLeak({ note: '/Users/dave/private/transcript.jsonl' })).toThrow(
      /Parity fixture violates non-PII boundary: /,
    )
  })

  it('refuses an email address value', () => {
    expect(() => assertNoRawCanonDbLeak({ capturedBy: 'dave@example.com' })).toThrow(
      /Parity fixture violates non-PII boundary: /,
    )
  })
})

describe('buildCopilotBaselineFixture', () => {
  it('defaults the source mode to primary', () => {
    expect(buildCopilotBaselineFixture().sourceMode).toBe('primary')
  })

  it('honours an explicit secondary source mode', () => {
    expect(buildCopilotBaselineFixture({ sourceMode: 'secondary' }).sourceMode).toBe('secondary')
  })

  it('defaults the session id to the pinned baseline contract', () => {
    const fixture = buildCopilotBaselineFixture()
    expect(fixture.harness).toBe('copilot')
    expect(fixture.sessionId).toBe(COPILOT_BASELINE_CONTRACT.sessionId)
    expect(fixture.turns).toBe(COPILOT_BASELINE_CONTRACT.turns)
  })

  it('builds one valid OTLP trace payload', () => {
    const fixture = buildCopilotBaselineFixture()
    expect(fixture.tracePayload.resourceSpans).toHaveLength(1)
    const spans = fixture.tracePayload.resourceSpans[0].scopeSpans[0].spans
    expect(spans.length).toBeGreaterThan(0)
    for (const span of spans) {
      expect(span.traceId).toMatch(/^[0-9a-f]{32}$/)
      expect(span.spanId).toMatch(/^[0-9a-f]{16}$/)
      expect(span.startTimeUnixNano).toMatch(/^\d+$/)
      expect(span.endTimeUnixNano).toMatch(/^\d+$/)
    }
  })

  it('builds valid OTLP log payloads', () => {
    const fixture = buildCopilotBaselineFixture()
    const logPayload = fixture.logPayload
    expect(logPayload).not.toBeNull()
    if (logPayload === null) throw new Error('baseline fixture must send logs')
    expect(logPayload.resourceLogs).toHaveLength(1)
    const records = logPayload.resourceLogs[0].scopeLogs[0].logRecords
    expect(records.length).toBeGreaterThan(0)
    for (const record of records) {
      expect(record.timeUnixNano).toMatch(/^\d+$/)
    }
  })

  it('carries the full-content dimensions the OTLP mapper reads', () => {
    const spans = allSpans()
    expect(spans.some((span) => attrString(span, 'gen_ai.system_instructions') !== undefined)).toBe(true)
    expect(spans.some((span) => attrString(span, 'gen_ai.input.messages') !== undefined)).toBe(true)
    expect(spans.some((span) => attrString(span, 'gen_ai.tool.definitions') !== undefined)).toBe(true)
    expect(spans.some((span) => attrString(span, 'gen_ai.operation.name') === 'invoke_agent')).toBe(true)
    expect(spans.some((span) => attrString(span, 'gen_ai.agent.name') === 'explore')).toBe(true)
    expect(spans.some((span) => attrString(span, 'gen_ai.tool.mcp.server') !== undefined)).toBe(true)
    expect(spans.some((span) => hasAttribute(span, 'kyber.skills.invoked'))).toBe(true)
    expect(
      spans.some(
        (span) =>
          attrString(span, 'gen_ai.tool.name') !== undefined &&
          attrString(span, 'gen_ai.tool.call.arguments') !== undefined,
      ),
    ).toBe(true)
  })

  it('encodes the IsDocument content_part_withheld case', () => {
    const fixture = buildCopilotBaselineFixture()
    expect(fixture.withheldCase.expectedBucket).toBe('content_part_withheld')
    expect(fixture.withheldCase.expectedPlainText).toBe('')

    const withheldSpan = fixture.tracePayload.resourceSpans[0].scopeSpans[0].spans.find(
      (span) => span.name === fixture.withheldCase.spanName,
    )
    expect(withheldSpan).toBeDefined()
    if (withheldSpan === undefined) return

    const resultText = attrString(withheldSpan, fixture.withheldCase.resultAttribute)
    expect(typeof resultText).toBe('string')
    const parsed: unknown = JSON.parse(resultText ?? 'null')
    const isDocument = Array.isArray(parsed) || (typeof parsed === 'object' && parsed !== null)
    expect(isDocument).toBe(true)
  })

  it('keeps the pinned baseline contract fully measurable', () => {
    expect(COPILOT_BASELINE_CONTRACT.harness).toBe('copilot')
    expect(COPILOT_BASELINE_CONTRACT.tokensMeasurable).toBe(true)
    expect(COPILOT_BASELINE_CONTRACT.contextMeasurable).toBe(true)
    expect(COPILOT_BASELINE_CONTRACT.schemaMeasurable).toBe(true)
    expect(COPILOT_BASELINE_CONTRACT.skillsMeasurable).toBe(true)
    expect(COPILOT_BASELINE_CONTRACT.perTurnTokensMeasurable).toBe(true)
    expect(COPILOT_BASELINE_CONTRACT.sendsLogs).toBe(true)
  })

  it('leaves the existing stored OTLP contracts untouched', () => {
    expect(new Set(STORED_OTLP_HARNESS_CONTRACTS.map((contract) => contract.harness)).size).toBe(7)
    expect(STORED_OTLP_HARNESS_CONTRACTS.some((contract) => contract.sessionId === COPILOT_BASELINE_CONTRACT.sessionId)).toBe(
      false,
    )
  })
})
