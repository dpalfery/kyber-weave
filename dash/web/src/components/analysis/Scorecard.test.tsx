import { describe, it, expect } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import {
  Scorecard,
  formatDimensionDisplay,
  ORDERED_DIMENSION_KEYS,
  DIMENSION_METADATA,
} from './Scorecard.js'
import { FindingList, getFindingRankScore } from './FindingList.js'
import { HierarchyBreadcrumb } from './HierarchyBreadcrumb.js'
import { ContextPressureStrip } from './ContextPressureStrip.js'
import { BaselineSelect } from './BaselineSelect.js'
import { ScorecardMatrix } from './ScorecardMatrix.js'
import { ContextDoctor } from '../../pages/ContextDoctor.js'
import { HarnessDetail } from '../../pages/HarnessDetail.js'
import { RunDetail } from '../../pages/RunDetail.js'
import type {
  ScorecardData,
  KyberFinding,
  KyberRunDetail,
} from '../../lib/kyberApi.js'

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
    expect(html).toContain('breadcrumb-context-doctor')
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
    expect(html).toContain('breadcrumb-context-doctor')
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
    expect(html).toContain('breadcrumb-context-doctor')
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
    expect(html).toContain('breadcrumb-context-doctor')
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

  it('omits the heading element when no title is supplied (review)', () => {
    // An empty <h3> is a nameless heading in the accessibility tree.
    const html = renderToStaticMarkup(<FindingList findings={[]} title="" />)
    expect(html).not.toContain('<h3')
  })
})

describe('Pages Smoke Rendering: Context Doctor, HarnessDetail, RunDetail', () => {
  it('renders Context Doctor page (Level 1)', () => {
    const html = renderWithQuery(
      <ContextDoctor
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
        initialFindings={{ findings: [], total: 0, offset: 0, detectorCounts: {}, unknownWindowSessions: 0 }}
      />,
    )

    expect(html).toContain('Context Doctor')
    expect(html).toContain('finding-list')
    expect(html).toContain('Highest-Leverage Workspace Findings')
    expect(html).toContain('scorecard-matrix')
    expect(html).toContain('Claude Code')
    expect(html.indexOf('finding-list')).toBeLessThan(html.indexOf('scorecard-matrix'))
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
    // Issue #184: 0-based `turnIndex` 1/2 render as human-facing `Turn #2`/`Turn #3`.
    expect(html).toContain('Turn #2')
    expect(html).toContain('Turn #3')
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

  it('names the compaction threshold matching the detector contract (issue #181)', () => {
    // The detector fires at 85% of the window (`detectCompactionHazard`); the
    // gauge caption must agree with the finding text, not say ~75%.
    const measuredHtml = renderToStaticMarkup(
      <ContextPressureStrip pressureMedian={0.65} pressureP95={0.88} />,
    )
    expect(measuredHtml).toContain('Compaction threshold (~85%)')
    expect(measuredHtml).not.toContain('~75%')
  })

  it('renders BaselineSelect with default baseline options', () => {
    const html = renderToStaticMarkup(<BaselineSelect value="harness_median" />)
    expect(html).toContain('baseline-select')
    expect(html).toContain('Harness Median')
    expect(html).toContain('Workspace Median (All Harnesses)')
  })
})

describe('ScorecardMatrix honesty (issues #189/#199, T10)', () => {
  const windowed = {
    historyWeeks: 2 as number | null,
    coveredFrom: '2026-09-16T00:00:00.000Z',
    coveredThrough: '2026-09-30T00:00:00.000Z',
  }

  it('renders the coverage window banner, never a bare matrix', () => {
    const html = renderToStaticMarkup(
      <ScorecardMatrix
        rows={[{ harness: 'pi', name: 'Pi', sampleCount: 2 }]}
        coverage={windowed}
      />,
    )
    expect(html).toContain('Coverage: last 2 weeks')
  })

  it('states an unknown window honestly instead of inventing one', () => {
    const html = renderToStaticMarkup(
      <ScorecardMatrix
        rows={[{ harness: 'pi', name: 'Pi', sampleCount: 2 }]}
        coverage={{ historyWeeks: null, coveredFrom: null, coveredThrough: null }}
      />,
    )
    expect(html).toContain('Coverage window unknown')
    expect(html).not.toContain('Coverage: last 2 weeks')
  })

  it('distinguishes a fresh store from a legacy tracked run in the banner', () => {
    // Review PR #230 (copilot numsQ): historyWeeks null means two different
    // things — carry lastSuccessAt so a fresh store never reads as a legacy run.
    const fresh = renderToStaticMarkup(
      <ScorecardMatrix
        rows={[{ harness: 'pi', name: 'Pi', sampleCount: 2 }]}
        coverage={{ historyWeeks: null, coveredFrom: null, coveredThrough: null, lastSuccessAt: null }}
      />,
    )
    expect(fresh).toContain('no successful refresh recorded')
    expect(fresh).not.toContain('recorded before window tracking')

    const legacy = renderToStaticMarkup(
      <ScorecardMatrix
        rows={[{ harness: 'pi', name: 'Pi', sampleCount: 2 }]}
        coverage={{
          historyWeeks: null,
          coveredFrom: null,
          coveredThrough: null,
          lastSuccessAt: '2026-09-19T11:00:00.000Z',
        }}
      />,
    )
    expect(legacy).toContain('recorded before window tracking')
    expect(legacy).not.toContain('no successful refresh recorded')
  })

  it('keeps zero-data rows interactive so the harnesses needing investigation stay clickable', () => {
    // Review PR #230 (kilo nux52): moving zero-data rows out of MatrixRow
    // dropped their drill control — the no-data section must wire the same
    // selection. Static markup pins the button; the handler is MatrixRow's.
    const reason = 'No collectable runs or sessions recorded for harness "copilot-cli".'
    const html = renderToStaticMarkup(
      <ScorecardMatrix
        rows={[
          { harness: 'pi', name: 'Pi', sampleCount: 2 },
          { harness: 'copilot-cli', name: 'GitHub Copilot CLI', sampleCount: 0, noDataReason: reason },
        ]}
        coverage={windowed}
        onSelectHarness={() => {}}
      />,
    )
    expect(html).toContain('data-testid="matrix-no-data"')
    expect(html).toContain('drill-harness-copilot-cli')
    expect(html).toContain('<button')
  })

  it('shows the empty state (not a header-only grid) when every row is zero-data', () => {
    // Review PR #230 (kilo nux56): the heading counts data rows while the
    // empty state keyed off all rows — a fresh install rendered column
    // headings above nothing under a heading reading (0).
    const reason = 'No collectable runs or sessions recorded.'
    const html = renderToStaticMarkup(
      <ScorecardMatrix
        rows={[{ harness: 'copilot-cli', name: 'GitHub Copilot CLI', sampleCount: 0, noDataReason: reason }]}
        coverage={windowed}
      />,
    )
    expect(html).toContain('Harness diagnostic matrix (0)')
    expect(html).toContain('data-testid="matrix-empty"')
    expect(html).toContain('data-testid="matrix-no-data"')
  })

  it('groups zero-data rows under their verbatim reason and stops claiming live data for them', () => {
    const reason = 'No collectable runs or sessions recorded for harness "copilot-cli".'
    const html = renderToStaticMarkup(
      <ScorecardMatrix
        rows={[
          { harness: 'pi', name: 'Pi', sampleCount: 2 },
          { harness: 'copilot-cli', name: 'GitHub Copilot CLI', sampleCount: 0, noDataReason: reason },
        ]}
        coverage={windowed}
      />,
    )
    // The verbatim rollup reason stays visible — never a dash-filled row or a `0 sessions` claim.
    // (React HTML-escapes the double quotes; the text is otherwise word-for-word.)
    expect(html).toContain('No collectable runs or sessions recorded for harness')
    expect(html).toContain('&quot;copilot-cli&quot;')
    expect(html).toContain('data-testid="matrix-no-data"')
    expect(html).not.toContain('0 sessions')
    // The caption no longer asserts every row is a live harness.
    expect(html).not.toContain('per live harness')
  })

  it('groups Claude split identities under one family with per-origin counts and no summed total', () => {
    const html = renderToStaticMarkup(
      <ScorecardMatrix
        rows={[
          { harness: 'claude-cli', name: 'Claude CLI', sampleCount: 3, family: 'claude-code' },
          { harness: 'claude-desktop', name: 'Claude Desktop', sampleCount: 5, family: 'claude-code' },
          { harness: 'claude-code', name: 'Claude Code', sampleCount: 7, family: 'claude-code' },
          { harness: 'pi', name: 'Pi', sampleCount: 2, family: 'pi' },
        ]}
        coverage={windowed}
      />,
    )
    // One family section, every canonical origin id still visible and distinct.
    expect(html).toContain('claude-code')
    expect(html).toContain('drill-harness-claude-cli')
    expect(html).toContain('drill-harness-claude-desktop')
    expect(html).toContain('drill-harness-claude-code')
    // Per-origin counts stay visible; the family sums nothing (3+5+7=15 never renders).
    expect(html).toContain('3')
    expect(html).toContain('5')
    expect(html).toContain('7')
    expect(html).not.toContain('15 samples')
    expect(html).not.toContain('15 sessions')
  })

  it('never renders the raw codeburn/ storage namespace as a harness', () => {
    const html = renderToStaticMarkup(
      <ScorecardMatrix rows={[{ harness: 'codeburn/claude-desktop' }]} coverage={windowed} />,
    )
    // No user-visible text — element content or accessible label — carries the prefix.
    expect(html).not.toContain('>codeburn/')
    expect(html).not.toContain('aria-label="codeburn/')
    // The canonical id still rides along for drill-down filtering and auditability.
    expect(html).toContain('drill-harness-codeburn/claude-desktop')
  })
})
