import { describe, expect, it } from 'vitest'

import {
  classifyContextItem,
  classifySessionContext,
  classifyTurnContext,
  EVIDENCE_OF_USE_LEVELS,
  extractFilePaths,
  extractSymbols,
  extractToolNames,
  extractVocabulary,
  type ClassificationRule,
  type ClassifiedContextItem,
  type ContextItem,
  type EvidenceOfUse,
} from '../kyber/analysis/classify.js'
import {
  analyzeContext,
  type ContextPart,
  type ContextTurn,
} from '../kyber/analysis/context.js'
import { notMeasurable, type CanonicalContentKey } from '../kyber/canon/types.js'

function makeItem(
  part: CanonicalContentKey | 'residual',
  text: string,
  tokens?: number,
  overrides: Partial<ContextItem> = {}
): ContextItem {
  return {
    part,
    text,
    tokens: tokens ?? Math.ceil(text.length / 4),
    bytes: Buffer.byteLength(text, 'utf8'),
    ...overrides,
  }
}

describe('Context-Item Classification (Decision D15 / Task F2)', () => {
  describe('Criterion 1: Strict vocabulary & Decision D15 compliance', () => {
    it('uses only telemetry-grounded evidence of use: strong, weak, none, unobserved', () => {
      expect(EVIDENCE_OF_USE_LEVELS).toEqual(['strong', 'weak', 'none', 'unobserved'])

      const item = makeItem('tool_definitions', '{"name": "test_tool"}')
      const result = classifyContextItem(item, { invokedTools: ['test_tool'] })

      expect(EVIDENCE_OF_USE_LEVELS).toContain(result.evidence)
      expect(['necessary', 'questionable', 'avoidable']).not.toContain(result.evidence)
    })

    it('never emits value verdicts on any classification output', () => {
      const parts: ContextItem[] = [
        makeItem('tool_definitions', '{"name": "tool_a"}'),
        makeItem('instruction_context', 'General guidelines for project'),
        makeItem('conversation_history', 'User: hello'),
      ]

      const classified = classifyTurnContext({
        parts,
        inputTokens: 200,
      })

      const allVerdicts = classified.items.map((i) => i.evidence)
      for (const verdict of allVerdicts) {
        expect(['strong', 'weak', 'none', 'unobserved']).toContain(verdict)
        expect(['necessary', 'questionable', 'avoidable']).not.toContain(verdict)
      }

      // Check bucket keys
      const bucketKeys = Object.keys(classified.byEvidence)
      expect(bucketKeys.sort()).toEqual(['none', 'strong', 'unobserved', 'weak'])
    })

    it('every classification carries the rule and reason that produced it', () => {
      const item = makeItem('tool_definitions', '{"name": "fetch_data"}')
      const classified = classifyContextItem(item, { invokedTools: ['fetch_data'] })

      expect(classified.rule).toBe('tool_invoked')
      expect(classified.reason).toContain('fetch_data')
      expect(typeof classified.reason).toBe('string')
      expect(classified.reason.length).toBeGreaterThan(0)
    })
  })

  describe('Criterion 2: Classification rules', () => {
    describe('Tool definitions', () => {
      it('classifies tool called in current turn as strong (rule: tool_invoked)', () => {
        const item = makeItem(
          'tool_definitions',
          '{"name": "read_file", "description": "Read file from disk"}',
          50,
          { name: 'read_file' }
        )
        const res = classifyContextItem(item, {
          invokedTools: ['read_file'],
        })

        expect(res.evidence).toBe('strong')
        expect(res.rule).toBe('tool_invoked')
        expect(res.reason).toContain('directly invoked in current turn')
      })

      it('classifies tool called in subsequent turn as strong (rule: tool_invoked_subsequent)', () => {
        const item = makeItem(
          'tool_definitions',
          '{"name": "write_file", "description": "Write file to disk"}',
          50,
          { name: 'write_file' }
        )
        const res = classifyContextItem(item, {
          invokedTools: [],
          subsequentInvokedTools: ['write_file'],
        })

        expect(res.evidence).toBe('strong')
        expect(res.rule).toBe('tool_invoked_subsequent')
        expect(res.reason).toContain('invoked in subsequent turn')
      })

      it('classifies tool called in session as strong (rule: tool_invoked_in_session)', () => {
        const item = makeItem(
          'tool_definitions',
          '{"name": "execute_bash", "description": "Run shell command"}',
          60,
          { name: 'execute_bash' }
        )
        const res = classifyContextItem(item, {
          sessionInvokedTools: ['execute_bash'],
        })

        expect(res.evidence).toBe('strong')
        expect(res.rule).toBe('tool_invoked_in_session')
      })

      it('extracts tool names from JSON schema text when item.name is omitted', () => {
        const item = makeItem(
          'tool_definitions',
          JSON.stringify({ name: 'grep_search', parameters: { query: 'string' } })
        )
        const res = classifyContextItem(item, {
          invokedTools: ['grep_search'],
        })

        expect(res.evidence).toBe('strong')
        expect(res.rule).toBe('tool_invoked')
      })

      it('classifies tool never invoked, cited, or matched as none (rule: never_invoked_or_matched)', () => {
        const item = makeItem(
          'tool_definitions',
          '{"name": "unused_database_tool", "description": "Perform SQL migrations"}',
          100,
          { name: 'unused_database_tool' }
        )
        const res = classifyContextItem(item, {
          invokedTools: ['read_file'],
          queries: ['Please format the markdown table'],
          responses: ['Here is the formatted table.'],
        })

        expect(res.evidence).toBe('none')
        expect(res.rule).toBe('never_invoked_or_matched')
        expect(res.reason).toContain('uninvoked, uncited, and unmatched')
      })
    })

    describe('Instruction files & context', () => {
      it('classifies directly cited instruction file as strong (rule: direct_citation)', () => {
        const item = makeItem(
          'instruction_context',
          '# Agent Guidelines\nAlways write tests before code.',
          80,
          { name: 'AGENTS.md', filePath: 'AGENTS.md' }
        )
        const res = classifyContextItem(item, {
          citations: ['AGENTS.md'],
        })

        expect(res.evidence).toBe('strong')
        expect(res.rule).toBe('direct_citation')
      })

      it('classifies instruction file sharing domain vocabulary as weak (rule: vocabulary_overlap)', () => {
        const item = makeItem(
          'instruction_context',
          'Database migration guidelines: ensure schema changes are backward compatible and migration transactions are isolated.',
          120
        )
        const res = classifyContextItem(item, {
          queries: ['We need to update the database schema with a backward compatible migration'],
          responses: ['I will prepare the migration transaction.'],
        })

        expect(res.evidence).toBe('weak')
        expect(res.rule).toBe('vocabulary_overlap')
        expect(res.reason).toContain('Shared vocabulary')
      })

      it('classifies instruction file with file path reference in query as weak (rule: path_reference)', () => {
        const item = makeItem(
          'instruction_context',
          'Configured project rules from .cursor/rules/telemetry.md',
          50,
          { filePath: '.cursor/rules/telemetry.md' }
        )
        const res = classifyContextItem(item, {
          queries: ['Please review the guidelines in .cursor/rules/telemetry.md'],
        })

        expect(res.evidence).toBe('weak')
        expect(res.rule).toBe('path_reference')
      })

      it('classifies instruction context with no invocations, citations, or matches as none', () => {
        const item = makeItem(
          'instruction_context',
          'Legacy COBOL compilation flags and mainframe job control language cards.',
          200
        )
        const res = classifyContextItem(item, {
          queries: ['Add styling to the navbar button'],
          responses: ['Added CSS background-color.'],
        })

        expect(res.evidence).toBe('none')
        expect(res.rule).toBe('never_invoked_or_matched')
      })
    })

    describe('Code symbols, file paths, and execution matches', () => {
      it('classifies symbol reference in queries or responses as weak (rule: symbol_reference)', () => {
        const item = makeItem(
          'tool_result_content',
          'export function analyzeContext(turns: ContextTurn[]): ContextAnalysis { return {}; }',
          150
        )
        const res = classifyContextItem(item, {
          queries: ['Where is analyzeContext defined and how is it called?'],
        })

        expect(res.evidence).toBe('weak')
        expect(res.rule).toBe('symbol_reference')
        expect(res.reason).toContain('analyzeContext')
      })

      it('classifies file path in queries or responses as weak (rule: path_reference)', () => {
        const item = makeItem(
          'tool_result_content',
          '// File: src/components/ContextInspector.tsx\nexport const ContextInspector = () => null;',
          100
        )
        const res = classifyContextItem(item, {
          queries: ['Check the ContextInspector.tsx implementation'],
        })

        expect(res.evidence).toBe('weak')
        expect(res.rule).toBe('path_reference')
      })

      it('classifies executed command match as strong (rule: execution_match)', () => {
        const item = makeItem(
          'tool_result_content',
          'Output of git status --porcelain:\nM package.json\n?? newfile.ts',
          50
        )
        const res = classifyContextItem(item, {
          executedCommands: ['git status --porcelain'],
        })

        expect(res.evidence).toBe('strong')
        expect(res.rule).toBe('execution_match')
      })

      it('classifies active tool result content as strong (rule: tool_result_invoked)', () => {
        const item = makeItem(
          'tool_result_content',
          'Contents of file /etc/hosts',
          40,
          { name: 'read_file' }
        )
        const res = classifyContextItem(item, {
          invokedTools: ['read_file'],
        })

        expect(res.evidence).toBe('strong')
        expect(res.rule).toBe('tool_result_invoked')
      })
    })

    describe('Unobserved telemetry states', () => {
      it('classifies unmeasured bucket as unobserved (never merged with none)', () => {
        const item = makeItem('tool_definitions', '{"name": "mcp_tool"}', 100, {
          isUnmeasured: true,
        })
        const res = classifyContextItem(item)

        expect(res.evidence).toBe('unobserved')
        expect(res.rule).toBe('unmeasured_bucket')
        expect(res.evidence).not.toBe('none')
      })

      it('classifies harness declared not_measurable bucket as unobserved', () => {
        const item = makeItem('system_prompt', 'You are a helpful assistant', 50)
        const res = classifyContextItem(item, {
          measurability: {
            system_prompt: notMeasurable('Harness omits system prompt telemetry'),
          },
        })

        expect(res.evidence).toBe('unobserved')
        expect(res.rule).toBe('unmeasured_bucket')
        expect(res.reason).toContain('declared not measurable')
      })

      it('classifies binary content as unobserved', () => {
        const item = makeItem('tool_result_content', '[binary image payload]', 250, {
          isBinary: true,
        })
        const res = classifyContextItem(item)

        expect(res.evidence).toBe('unobserved')
        expect(res.rule).toBe('binary_content')
      })

      it('classifies unexported context (billed tokens, empty text) as unobserved', () => {
        const item = makeItem('system_prompt', '', 500)
        const res = classifyContextItem(item)

        expect(res.evidence).toBe('unobserved')
        expect(res.rule).toBe('unexported_context')
      })
    })
  })

  describe('Criterion 3: Unattributed residual isolation & bucket accounting', () => {
    it('isolates unattributed residuals into a dedicated unobserved block without distributing across items', () => {
      const parts: ContextItem[] = [
        makeItem('tool_definitions', '{"name": "tool_active"}', 1_000, { name: 'tool_active' }),
        makeItem('tool_definitions', '{"name": "tool_dormant"}', 500, { name: 'tool_dormant' }),
        makeItem('instruction_context', 'Architecture guidelines for system telemetry', 1_500),
      ]
      // Reported input tokens = 5,000; Reconstructed tokens = 3,000; Gap = 2,000
      const turnContext = classifyTurnContext(
        {
          parts,
          inputTokens: 5_000,
          invokedTools: ['tool_active'],
          queries: ['Review telemetry architecture'],
        }
      )

      // Reconstructed sum
      expect(turnContext.reconstructedTokens).toBe(3_000)
      expect(turnContext.totalInputTokens).toBe(5_000)

      // Residual is isolated
      expect(turnContext.residual.isolated).toBe(true)
      expect(turnContext.residual.tokens).toBe(2_000)
      expect(turnContext.residual.evidence).toBe('unobserved')
      expect(turnContext.residual.rule).toBe('unattributed_residual')
      expect(turnContext.residual.tokenFraction).toBe(2_000 / 5_000)

      // The residual item is present in items and classified as unobserved
      const residualItem = turnContext.items.find((i) => i.rule === 'unattributed_residual')
      expect(residualItem).toBeDefined()
      expect(residualItem?.tokens).toBe(2_000)
      expect(residualItem?.evidence).toBe('unobserved')

      // Resident items are NOT altered or padded by the residual
      expect(turnContext.items[0].tokens).toBe(1_000)
      expect(turnContext.items[1].tokens).toBe(500)
      expect(turnContext.items[2].tokens).toBe(1_500)

      // Total items = 3 parts + 1 isolated residual
      expect(turnContext.items).toHaveLength(4)
    })

    it('computes exact token counts, token fractions, byte counts, and byte fractions per evidence bucket', () => {
      const parts: ContextItem[] = [
        // Strong: 1,000 tokens, 4,000 bytes
        makeItem('tool_definitions', '{"name": "called_tool"}', 1_000, {
          name: 'called_tool',
          bytes: 4_000,
        }),
        // Weak: 1,500 tokens, 6,000 bytes
        makeItem('instruction_context', 'Telemetry streaming guidelines and telemetry schemas', 1_500, {
          bytes: 6_000,
        }),
        // None: 500 tokens, 2,000 bytes
        makeItem('tool_definitions', '{"name": "unused_tool"}', 500, {
          name: 'unused_tool',
          bytes: 2_000,
        }),
      ]
      // Reported input = 4,000 tokens (reconstructed = 3,000; residual = 1,000 unobserved tokens)
      const classified = classifyTurnContext({
        parts,
        inputTokens: 4_000,
        invokedTools: ['called_tool'],
        queries: ['We need to update telemetry streaming'],
      })

      const { strong, weak, none, unobserved } = classified.byEvidence

      // Token counts
      expect(strong.tokens).toBe(1_000)
      expect(weak.tokens).toBe(1_500)
      expect(none.tokens).toBe(500)
      expect(unobserved.tokens).toBe(1_000) // The isolated residual

      // Token fractions sum to 1.0 (100% of reported input)
      expect(strong.tokenFraction).toBe(1_000 / 4_000)
      expect(weak.tokenFraction).toBe(1_500 / 4_000)
      expect(none.tokenFraction).toBe(500 / 4_000)
      expect(unobserved.tokenFraction).toBe(1_000 / 4_000)

      const sumTokenFractions =
        strong.tokenFraction + weak.tokenFraction + none.tokenFraction + unobserved.tokenFraction
      expect(sumTokenFractions).toBeCloseTo(1.0, 5)

      // Byte counts and fractions (total reconstructed bytes = 12,000)
      expect(classified.totalBytes).toBe(12_000)
      expect(strong.bytes).toBe(4_000)
      expect(weak.bytes).toBe(6_000)
      expect(none.bytes).toBe(2_000)
      expect(unobserved.bytes).toBe(0) // Unexported residual has 0 bytes

      expect(strong.byteFraction).toBe(4_000 / 12_000)
      expect(weak.byteFraction).toBe(6_000 / 12_000)
      expect(none.byteFraction).toBe(2_000 / 12_000)
      expect(unobserved.byteFraction).toBe(0)

      const sumByteFractions =
        strong.byteFraction + weak.byteFraction + none.byteFraction + unobserved.byteFraction
      expect(sumByteFractions).toBeCloseTo(1.0, 5)
    })

    it('handles zero residual when reconstructed tokens exactly equal input tokens', () => {
      const parts: ContextItem[] = [
        makeItem('system_prompt', 'System instructions', 100, { bytes: 400 }),
      ]
      const classified = classifyTurnContext({
        parts,
        inputTokens: 100,
      })

      expect(classified.residual.tokens).toBe(0)
      expect(classified.residual.tokenFraction).toBe(0)
      expect(classified.items).toHaveLength(1)
      expect(classified.byEvidence.unobserved.tokens).toBe(0)
    })

    it('handles zero input tokens without division by zero NaN', () => {
      const classified = classifyTurnContext({
        parts: [],
        inputTokens: 0,
      })

      expect(classified.totalInputTokens).toBe(0)
      expect(classified.residual.tokens).toBe(0)
      expect(classified.residual.tokenFraction).toBe(0)
      expect(classified.byEvidence.strong.tokenFraction).toBe(0)
      expect(classified.byEvidence.weak.byteFraction).toBe(0)
    })
  })

  describe('Criterion 4: Multi-turn session classification across time', () => {
    it('classifies tools called in subsequent turns as strong on earlier turns', () => {
      const toolSchemas = [
        makeItem('tool_definitions', '{"name": "fetch_user"}', 100, { name: 'fetch_user' }),
        makeItem('tool_definitions', '{"name": "update_user"}', 120, { name: 'update_user' }),
        makeItem('tool_definitions', '{"name": "delete_all"}', 150, { name: 'delete_all' }),
      ]

      const sessionTurns = [
        // Turn 0: fetch_user is called now, update_user will be called in Turn 1
        {
          parts: toolSchemas,
          inputTokens: 500,
          invokedTools: ['fetch_user'],
        },
        // Turn 1: update_user is called now
        {
          parts: toolSchemas,
          inputTokens: 500,
          invokedTools: ['update_user'],
        },
        // Turn 2: no tools called; delete_all was NEVER called in session
        {
          parts: toolSchemas,
          inputTokens: 500,
          invokedTools: [],
        },
      ]

      const sessionClassifications = classifySessionContext({ turns: sessionTurns })
      expect(sessionClassifications).toHaveLength(3)

      // Turn 0 checks:
      const turn0Items = sessionClassifications[0].items
      const turn0Fetch = turn0Items.find((i) => i.name === 'fetch_user')!
      const turn0Update = turn0Items.find((i) => i.name === 'update_user')!
      const turn0Delete = turn0Items.find((i) => i.name === 'delete_all')!

      // fetch_user called in this turn -> strong (tool_invoked)
      expect(turn0Fetch.evidence).toBe('strong')
      expect(turn0Fetch.rule).toBe('tool_invoked')

      // update_user called in subsequent turn -> strong (tool_invoked_subsequent)
      expect(turn0Update.evidence).toBe('strong')
      expect(turn0Update.rule).toBe('tool_invoked_subsequent')

      // delete_all never called in any turn -> none (never_invoked_or_matched)
      expect(turn0Delete.evidence).toBe('none')
      expect(turn0Delete.rule).toBe('never_invoked_or_matched')

      // Turn 1 checks:
      const turn1Items = sessionClassifications[1].items
      const turn1Fetch = turn1Items.find((i) => i.name === 'fetch_user')!
      const turn1Update = turn1Items.find((i) => i.name === 'update_user')!
      const turn1Delete = turn1Items.find((i) => i.name === 'delete_all')!

      // fetch_user called in session -> strong (tool_invoked_in_session)
      expect(turn1Fetch.evidence).toBe('strong')
      expect(turn1Fetch.rule).toBe('tool_invoked_in_session')

      // update_user called in this turn -> strong (tool_invoked)
      expect(turn1Update.evidence).toBe('strong')
      expect(turn1Update.rule).toBe('tool_invoked')

      // delete_all still none
      expect(turn1Delete.evidence).toBe('none')

      // Turn 2 checks:
      const turn2Items = sessionClassifications[2].items
      const turn2Delete = turn2Items.find((i) => i.name === 'delete_all')!
      expect(turn2Delete.evidence).toBe('none')
      expect(turn2Delete.rule).toBe('never_invoked_or_matched')
    })
  })

  describe('Integration with analyzeContext (analysis/context.ts)', () => {
    it('attaches turn context classification when classify option is passed', () => {
      const parts: ContextPart[] = [
        { part: 'tool_definitions', text: '{"name": "grep"}', tokens: 200 },
        { part: 'instruction_context', text: 'Telemetry format rules', tokens: 300 },
      ]

      const turns: ContextTurn[] = [
        { parts, inputTokens: 600, freshInput: 600 },
      ]

      const analysis = analyzeContext(turns, {
        contextLimit: 100_000,
        classify: {
          invokedTools: ['grep'],
        },
      })

      expect(analysis.measurable).toBe(true)
      if (analysis.measurable) {
        const turn1 = analysis.turns[0]
        expect(turn1.classification).toBeDefined()
        expect(turn1.classification?.totalInputTokens).toBe(600)
        expect(turn1.classification?.residual.tokens).toBe(100)
        expect(turn1.classification?.residual.isolated).toBe(true)

        const grepItem = turn1.classification?.items.find((i) => i.name === 'grep')
        expect(grepItem?.evidence).toBe('strong')
        expect(grepItem?.rule).toBe('tool_invoked')
      }
    })
  })

  describe('Extractor unit tests', () => {
    it('extractToolNames parses schemas and function declarations', () => {
      const schemaText = `{"name": "mcp__github__get_issue"}, tool execute_sql`
      const names = extractToolNames(schemaText, 'explicit_tool')

      expect(names).toContain('mcp__github__get_issue')
      expect(names).toContain('execute_sql')
      expect(names).toContain('explicit_tool')
    })

    it('extractFilePaths parses posix and windows paths', () => {
      const text = 'Check src/utils/format.ts and lib/parser.go and tests/fixtures/test.json'
      const paths = extractFilePaths(text)

      expect(paths).toContain('src/utils/format.ts')
      expect(paths).toContain('lib/parser.go')
      expect(paths).toContain('tests/fixtures/test.json')
    })

    it('extractSymbols captures functions, classes, types, and PascalCase identifiers', () => {
      const text = `
        export function classifyTurnContext() {}
        export class ContextManager {}
        export type EvidenceLevel = 'strong'
        const activeRecords = []
      `
      const symbols = extractSymbols(text)

      expect(symbols).toContain('classifyTurnContext')
      expect(symbols).toContain('ContextManager')
      expect(symbols).toContain('EvidenceLevel')
      expect(symbols).toContain('activeRecords')
    })

    it('extractVocabulary strips stop words and normalizes tokens', () => {
      const text = 'The telemetry pipeline evaluates context classification with strict observability'
      const vocab = extractVocabulary(text)

      expect(vocab.has('the')).toBe(false)
      expect(vocab.has('with')).toBe(false)
      expect(vocab.has('telemetry')).toBe(true)
      expect(vocab.has('pipeline')).toBe(true)
      expect(vocab.has('observability')).toBe(true)
    })
  })
})
