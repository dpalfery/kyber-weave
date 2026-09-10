import { describe, it, expect } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import {
  Scorecard,
  formatDimensionDisplay,
  ORDERED_DIMENSION_KEYS,
  DIMENSION_METADATA,
} from './Scorecard'
import { FindingList, getFindingRankScore } from './FindingList'
import { HierarchyBreadcrumb } from './HierarchyBreadcrumb'
import { ContextPressureStrip } from './ContextPressureStrip'
import { BaselineSelect } from './BaselineSelect'
import { Attention } from '../../pages/Attention'
import { HarnessDetail } from '../../pages/HarnessDetail'
import { RunDetail } from '../../pages/RunDetail'
import type {
  ScorecardData,
  KyberFinding,
  KyberRunDetail,
} from '../../lib/kyberApi'

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
    },
  })
}

function renderWithQuery(ui: React.ReactElement) {
  const queryClient = createTestQueryClient()
  return renderToStaticMarkup(
    <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>,
  )
}

describe('Scorecard Component (Decision D3 & D9 Compliance)', () => {
  const mockScorecardData: ScorecardData = {
    dimensions: {
      contextHygiene: {
        key: 'contextHygiene',
        name: 'Context Hygiene',
        value: 0.78,
        formatted: '78%',
        status: 'measured',
        measurementClass: 'deterministic',
      },
      cacheEfficiency: {
        key: 'cacheEfficiency',
        name: 'Cache Efficiency',
        value: 0.85,
        formatted: '85%',
        status: 'measured',
        measurementClass: 'inferred',
      },
      toolYield: {
        key: 'toolYield',
        name: 'Tool Yield',
        value: 3.4,
        formatted: '3.4x',
        status: 'measured',
      },
      skillUtilisation: {
        key: 'skillUtilisation',
        name: 'Skill Utilisation',
        status: 'not_measurable',
        reason: 'Skill activation unobserved on this harness per Decision D16',
        measurementClass: 'coverage-gap',
      },
      delegationOverhead: {
        key: 'delegationOverhead',
        name: 'Delegation Overhead',
        value: 0.12,
        formatted: '12%',
        status: 'measured',
      },
      continuity: {
        key: 'continuity',
        name: 'Continuity',
        value: 0.98,
        formatted: '98%',
        status: 'measured',
      },
    },
    secondaryCost: {
      costUsd: 0.42,
      basis: 'derived_estimate',
      status: 'derived',
    },
  }

  it('renders all 6 diagnostic dimensions individually', () => {
    const html = renderToStaticMarkup(<Scorecard data={mockScorecardData} />)

    // All 6 dimension names must be present in the output
    expect(html).toContain('Context Hygiene')
    expect(html).toContain('Cache Efficiency')
    expect(html).toContain('Tool Yield')
    expect(html).toContain('Skill Utilisation')
    expect(html).toContain('Delegation Overhead')
    expect(html).toContain('Continuity')

    // Measured values must be formatted
    expect(html).toContain('78%')
    expect(html).toContain('85%')
    expect(html).toContain('3.4x')
    expect(html).toContain('12%')
    expect(html).toContain('98%')
  })

  it('Decision D3 compliance: NEVER calculates or displays a composite single efficiency score', () => {
    const html = renderToStaticMarkup(<Scorecard data={mockScorecardData} />)

    // All six independent dimensions remain visible without a composite score.
    expect(html).toContain('data-testid="dimension-contextHygiene"')
    expect(html).toContain('data-testid="dimension-continuity"')

    // Must NOT contain composite score terminology or overall grades
    expect(html).not.toMatch(/composite score/i)
    expect(html).not.toMatch(/overall efficiency score/i)
    expect(html).not.toMatch(/single score/i)
    expect(html).not.toMatch(/overall grade/i)
    expect(html).not.toMatch(/efficiency grade/i)

    // Calculate what a naive composite average would be: (78 + 85 + 12 + 98)/4 = 68.25%
    // Verify that this naive composite is NOT displayed anywhere
    expect(html).not.toContain('68.25%')
    expect(html).not.toContain('68%')
  })

  it('Decision D9 compliance: Cost is displayed only as secondary derived metric', () => {
    const html = renderToStaticMarkup(<Scorecard data={mockScorecardData} />)

    // Must contain secondary cost indicator with derived marker
    expect(html).toContain('secondary-cost')
    expect(html).toContain('$0.42')
    expect(html).toContain('(derived)')

    // Cost must not be a primary header or primary value
    expect(html).not.toMatch(/<h[1-2][^>]*>\$0\.42<\/h[1-2]>/)
  })

  it('Requirement 4: Unmeasurable telemetry renders dashes (—) with tooltip explanations, never 0 or 100%', () => {
    const unmeasuredData: ScorecardData = {
      dimensions: {
        contextHygiene: {
          key: 'contextHygiene',
          name: 'Context Hygiene',
          status: 'not_measurable',
          reason: 'Context window composition unrecorded',
        },
        cacheEfficiency: {
          key: 'cacheEfficiency',
          name: 'Cache Efficiency',
          status: 'not_measurable',
          reason: 'Harness does not export cache counters',
        },
        toolYield: {
          key: 'toolYield',
          name: 'Tool Yield',
          status: 'not_measurable',
          reason: 'Tool schemas absent',
        },
        skillUtilisation: {
          key: 'skillUtilisation',
          name: 'Skill Utilisation',
          status: 'not_measurable',
          reason: 'Skill activation unobserved',
        },
        delegationOverhead: {
          key: 'delegationOverhead',
          name: 'Delegation Overhead',
          status: 'not_measurable',
          reason: 'Single-agent execution',
        },
        continuity: {
          key: 'continuity',
          name: 'Continuity',
          status: 'not_measurable',
          reason: 'User corrections unrecorded',
        },
      },
    }

    const html = renderToStaticMarkup(<Scorecard data={unmeasuredData} />)

    // Em-dash must be present for unmeasurable dimensions
    expect(html).toContain('—')

    // Explanations must be present in titles or text
    expect(html).toContain('Harness does not export cache counters')
    expect(html).toContain('Context window composition unrecorded')

    // Unmeasured dimensions must NOT be rendered as 0 or 100%
    const unmeasurableElements = html.match(/data-testid="dimension-unmeasurable"[^>]*>([^<]+)</g)
    expect(unmeasurableElements).not.toBeNull()
    unmeasurableElements?.forEach((el) => {
      expect(el).toContain('—')
      expect(el).not.toContain('>0<')
      expect(el).not.toContain('>0%<')
      expect(el).not.toContain('>100%<')
    })
  })

  it('renders clean fallback when no data is supplied', () => {
    const html = renderToStaticMarkup(<Scorecard />)

    // Renders all 6 dimensions with dashes
    expect(html).toContain('Context Hygiene')
    expect(html).toContain('Cache Efficiency')
    expect(html).toContain('Tool Yield')
    expect(html).toContain('Skill Utilisation')
    expect(html).toContain('Delegation Overhead')
    expect(html).toContain('Continuity')
    expect(html).toContain('—')
  })

  it('formatDimensionDisplay formats correctly', () => {
    // Measured percent
    const d1 = formatDimensionDisplay({
      key: 'contextHygiene',
      name: 'Context Hygiene',
      value: 0.82,
      status: 'measured',
    })
    expect(d1.display).toBe('82%')
    expect(d1.isUnmeasurable).toBe(false)

    // Measured count / multiplier
    const d2 = formatDimensionDisplay({
      key: 'toolYield',
      name: 'Tool Yield',
      value: 4.5,
      status: 'measured',
    })
    expect(d2.display).toBe('4.50')

    // Unmeasurable with reason
    const d3 = formatDimensionDisplay({
      key: 'cacheEfficiency',
      name: 'Cache Efficiency',
      status: 'not_measurable',
      reason: 'No cache headers',
    })
    expect(d3.display).toBe('—')
    expect(d3.isUnmeasurable).toBe(true)
    expect(d3.reason).toBe('No cache headers')
  })
})

describe('HierarchyBreadcrumb (6-Level Spine Navigation)', () => {
  it('renders Level 1: All Harnesses as active root', () => {
    const html = renderToStaticMarkup(<HierarchyBreadcrumb />)
    expect(html).toContain('hierarchy-breadcrumb')
    expect(html).toContain('breadcrumb-attention')
    expect(html).toContain('All Harnesses')
    expect(html).toContain('aria-current="page"')
  })

  it('renders Level 2: Harness breadcrumb', () => {
    const html = renderToStaticMarkup(
      <HierarchyBreadcrumb
        harness="claude"
        onSelectAll={() => {}}
      />,
    )
    expect(html).toContain('breadcrumb-attention')
    expect(html).toContain('breadcrumb-harness')
    expect(html).toContain('claude')
  })

  it('renders Level 3: Run breadcrumb', () => {
    const html = renderToStaticMarkup(
      <HierarchyBreadcrumb
        harness="copilot"
        runId="run-123456789"
        onSelectAll={() => {}}
        onSelectHarness={() => {}}
      />,
    )
    expect(html).toContain('breadcrumb-attention')
    expect(html).toContain('breadcrumb-harness')
    expect(html).toContain('breadcrumb-run')
    expect(html).toContain('run-1234')
  })

  it('renders complete 6-level spine: All -> Harness -> Run -> Exec -> Turn -> Item', () => {
    const html = renderToStaticMarkup(
      <HierarchyBreadcrumb
        harness="gemini"
        runId="run-abc"
        executionId="exec-xyz"
        turnIndex={4}
        itemKey="System Prompt"
      />,
    )
    expect(html).toContain('breadcrumb-attention')
    expect(html).toContain('breadcrumb-harness')
    expect(html).toContain('breadcrumb-run')
    expect(html).toContain('breadcrumb-execution')
    expect(html).toContain('breadcrumb-turn')
    expect(html).toContain('breadcrumb-item')
    expect(html).toContain('Turn 4')
    expect(html).toContain('System Prompt')
  })
})

describe('FindingList (Decision D6 Ranking & D8 Recommendations)', () => {
  const sampleFindings: KyberFinding[] = [
    {
      id: 'f-inferred-large',
      detectorId: 'prefix-cache-break',
      title: 'Inferred Large Cache Break',
      mechanism: 'Cache prefix shift inferred from byte hash differences',
      evidenceLinks: [
        { spanId: 'span-1', turnIndex: 1, description: 'Turn 1 prefix hash' },
        { spanId: 'span-2', turnIndex: 2, description: 'Turn 2 prefix hash mismatch' },
      ],
      confidence: 'heuristic', // 0.10 multiplier
      estimatedWasteTokens: 50_000,
      recommendation: 'Move dynamic user context after static system prompts per Decision D8.',
      errorBar: { lower: 30_000, upper: 70_000 },
      outcomeRiskCaveat: 'Reordering prompts may affect older model completions.',
      rankScore: 50_000 * 0.10, // 5,000
    },
    {
      id: 'f-deterministic-small',
      detectorId: 'duplicate-tool-call',
      title: 'Deterministic Duplicate Tool Calls',
      mechanism: 'Identical byte-for-byte read call executed twice in adjacent turns',
      evidenceLinks: [
        { spanId: 'span-3', turnIndex: 2, description: 'First read call' },
        { spanId: 'span-4', turnIndex: 3, description: 'Duplicate read call' },
      ],
      confidence: 'deterministic', // 1.0 multiplier
      estimatedWasteTokens: 10_000,
      recommendation: 'Defer second read or cache tool result in agent context memory.',
      errorBar: { lower: 10_000, upper: 10_000 },
      outcomeRiskCaveat: 'Fresh file edits may require invalidating cached tool results.',
      rankScore: 10_000 * 1.0, // 10,000
    },
  ]

  it('Decision D6 ranking: Deterministic smaller finding outranks large inferred finding', () => {
    // D6 Formula: 10k deterministic (score: 10,000) beats 50k heuristic (score: 5,000)
    const scoreDeterministic = getFindingRankScore(sampleFindings[1])
    const scoreHeuristic = getFindingRankScore(sampleFindings[0])
    expect(scoreDeterministic).toBeGreaterThan(scoreHeuristic)

    const html = renderToStaticMarkup(<FindingList findings={sampleFindings} />)

    // Deterministic finding card must appear before inferred finding card in the DOM
    const indexDeterministic = html.indexOf('finding-card-f-deterministic-small')
    const indexInferred = html.indexOf('finding-card-f-inferred-large')

    expect(indexDeterministic).toBeGreaterThan(-1)
    expect(indexInferred).toBeGreaterThan(-1)
    expect(indexDeterministic).toBeLessThan(indexInferred)
  })

  it('renders Decision D8 recommendation and outcome risk caveat', () => {
    const html = renderToStaticMarkup(<FindingList findings={sampleFindings} />)

    // Recommendation heading remains visible without implementation provenance.
    expect(html).toContain('Recommendation')
    expect(html).toContain('Move dynamic user context after static system prompts')

    // Outcome risk caveat check
    expect(html).toContain('Outcome Risk Caveat')
    expect(html).toContain('Reordering prompts may affect older model completions')
  })

  it('renders deep-links to turn numbers', () => {
    const html = renderToStaticMarkup(<FindingList findings={sampleFindings} />)
    expect(html).toContain('evidence-link-turn-1')
    expect(html).toContain('Turn #1')
    expect(html).toContain('Turn #2')
  })

  it('renders empty fallback cleanly when no findings exist', () => {
    const html = renderToStaticMarkup(<FindingList findings={[]} />)
    expect(html).toContain('finding-list-empty')
    expect(html).toContain('No findings detected')
  })
})

describe('Pages Smoke Rendering: Attention, HarnessDetail, RunDetail', () => {
  it('renders Attention page (Level 1)', () => {
    const html = renderWithQuery(
      <Attention
        initialHarnesses={[
          {
            harness: 'claude',
            name: 'Claude Code',
            sampleCount: 12,
            contextPressureMedian: 0.65,
            cacheHitRate: 0.82,
            fieldCoverage: 0.83,
            measurability: { token_usage: 'measured' },
          },
        ]}
      />,
    )

    expect(html).toContain('Workspace Attention')
    expect(html).toContain('Workspace Diagnostic Scorecard')
    expect(html).toContain('harness-health-table')
    expect(html).toContain('Claude Code')
  })

  it('renders HarnessDetail page (Level 2)', () => {
    const html = renderWithQuery(
      <HarnessDetail
        harnessId="copilot"
        initialHarness={{
          harness: 'copilot',
          name: 'GitHub Copilot',
          sampleCount: 8,
          contextPressureMedian: 0.72,
          cacheHitRate: null,
          fieldCoverage: 0.67,
          measurability: { cache_hit_rate: 'not_measurable' },
        }}
        initialRuns={[
          {
            runId: 'run-copilot-001',
            harness: 'copilot',
            label: 'Refactor Auth Service',
            groupingBasis: 'explicit',
            executionCount: 2,
            turnCount: 6,
            costUsd: 0.25,
            outcome: { status: 'success', exitCode: 0 },
          },
        ]}
      />,
    )

    expect(html).toContain('Level 2: Harness')
    expect(html).toContain('GitHub Copilot')
    expect(html).toContain('harness-runs-table')
    expect(html).toContain('run-copilot-001')
    expect(html).toContain('Refactor Auth Service')
  })

  it('renders RunDetail page (Level 3)', () => {
    const mockRunDetail: KyberRunDetail = {
      runId: 'run-999',
      harness: 'claude',
      label: 'Optimize SQLite Indexing',
      groupingBasis: 'derived',
      groupingRule: 'working-directory + bounded time-gap',
      workingDirectory: '/Users/dave/repo',
      executionCount: 2,
      turnCount: 4,
      costUsd: 0.15,
      outcome: { status: 'success', exitCode: 0, testDelta: { passed: 4, failed: 0 } },
      executionTree: [
        {
          executionId: 'exec-root',
          runId: 'run-999',
          harness: 'claude',
          agentName: 'Planner Agent',
          isRoot: true,
          turnCount: 2,
          children: [
            {
              executionId: 'exec-child',
              runId: 'run-999',
              parentExecutionId: 'exec-root',
              harness: 'claude',
              agentName: 'Coder Subagent',
              isRoot: false,
              turnCount: 2,
            },
          ],
        },
      ],
      executions: [
        {
          executionId: 'exec-root',
          runId: 'run-999',
          harness: 'claude',
          agentName: 'Planner Agent',
          isRoot: true,
          turnCount: 2,
        },
        {
          executionId: 'exec-child',
          runId: 'run-999',
          parentExecutionId: 'exec-root',
          harness: 'claude',
          agentName: 'Coder Subagent',
          isRoot: false,
          turnCount: 2,
        },
      ],
      findings: [],
      turns: [
        { turnIndex: 1, model: 'claude-3-7-sonnet', tokens: 12000, contextPressure: 0.45 },
        { turnIndex: 2, model: 'claude-3-7-sonnet', tokens: 18000, contextPressure: 0.65 },
      ],
    }

    const html = renderWithQuery(<RunDetail runId="run-999" initialRun={mockRunDetail} />)

    expect(html).toContain('Level 3: Run')
    expect(html).toContain('Run run-999')
    expect(html).toContain('derived run')
    expect(html).toContain('execution-tree-panel')
    expect(html).toContain('Planner Agent')
    expect(html).toContain('Coder Subagent')
    expect(html).toContain('turns-panel')
    expect(html).toContain('#1')
    expect(html).toContain('#2')
  })
})

describe('ContextPressureStrip & BaselineSelect', () => {
  it('verifies ORDERED_DIMENSION_KEYS defines the 6 diagnostic dimensions', () => {
    expect(ORDERED_DIMENSION_KEYS).toHaveLength(6)
    expect(ORDERED_DIMENSION_KEYS).toEqual([
      'contextHygiene',
      'cacheEfficiency',
      'toolYield',
      'skillUtilisation',
      'delegationOverhead',
      'continuity',
    ])
    expect(Object.keys(DIMENSION_METADATA)).toHaveLength(6)
  })

  it('renders ContextPressureStrip with values and unmeasurable states', () => {
    const measuredHtml = renderToStaticMarkup(
      <ContextPressureStrip pressureMedian={0.65} pressureP95={0.88} />,
    )
    expect(measuredHtml).toContain('Context Pressure')
    expect(measuredHtml).toContain('65%')
    expect(measuredHtml).toContain('88%')

    const unmeasuredHtml = renderToStaticMarkup(
      <ContextPressureStrip isUnmeasurable unmeasuredReason="Window size unrecorded" />,
    )
    expect(unmeasuredHtml).toContain('Window size unrecorded')
    expect(unmeasuredHtml).toContain('—')
  })

  it('renders BaselineSelect with default baseline options', () => {
    const html = renderToStaticMarkup(<BaselineSelect value="harness_median" />)
    expect(html).toContain('baseline-select')
    expect(html).toContain('Harness Median')
    expect(html).toContain('Workspace Median (All Harnesses)')
  })
})
