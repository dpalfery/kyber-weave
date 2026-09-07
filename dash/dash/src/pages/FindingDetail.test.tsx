import { describe, it, expect, vi, beforeEach } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { FindingDetail } from './FindingDetail'
import { EvidenceTable } from '../components/kyber/EvidenceTable'
import { ConfidencePanel } from '../components/kyber/ConfidencePanel'
import { RecommendationPanel } from '../components/kyber/RecommendationPanel'
import type { KyberFinding, KyberEvidenceLink } from '../lib/kyberApi'

// Setup React 19 test hook dispatcher
let hookStates: unknown[] = []
let hookIndex = 0

function clearHooks() {
  hookStates = []
  hookIndex = 0
}

const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: { H?: any }
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE

if (reactInternals) {
  reactInternals.H = {
    useState: <T,>(v: T | (() => T)): [T, (val: T | ((prev: T) => T)) => void] => {
      const idx = hookIndex++
      if (idx >= hookStates.length) {
        hookStates.push(typeof v === 'function' ? (v as () => T)() : v)
      }
      const setter = (next: T | ((prev: T) => T)) => {
        hookStates[idx] = typeof next === 'function' ? (next as (prev: T) => T)(hookStates[idx] as T) : next
      }
      return [hookStates[idx] as T, setter]
    },
    useMemo: <T,>(fn: () => T) => fn(),
    useCallback: <T,>(fn: T) => fn,
    useRef: <T,>(v: T) => ({ current: v }),
    useEffect: () => {},
    useLayoutEffect: () => {},
    useId: () => 'test-finding-detail-id',
  }
}

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

function renderHtml(element: React.ReactElement | null | undefined, qc = createTestQueryClient()): string {
  if (element == null) return ''
  hookIndex = 0
  return renderToStaticMarkup(
    React.createElement(QueryClientProvider, { client: qc }, element),
  )
}

function findNodeByTestId(node: unknown, testId: string): React.ReactElement<any> | null {
  if (node == null) return null
  if (Array.isArray(node)) {
    for (const child of node) {
      const found = findNodeByTestId(child, testId)
      if (found) return found
    }
    return null
  }
  if (React.isValidElement(node)) {
    const props = node.props as Record<string, any>
    if (props && props['data-testid'] === testId) {
      return node
    }
    if (props && props.children) {
      const found = findNodeByTestId(props.children, testId)
      if (found) return found
    }
  }
  return null
}

// ---------------------------------------------------------------------------
// Sample Test Fixtures
// ---------------------------------------------------------------------------

const sampleDeterministicFinding: KyberFinding = {
  id: 'finding-duplicate-reads',
  detectorId: 'duplicate-tool-call',
  title: 'Duplicate File Read Invocations',
  mechanism:
    'Adjacent conversation turns executed identical byte-for-byte read operations for /src/auth/jwt.ts without intermediate file modification.',
  evidenceLinks: [
    {
      spanId: 'span-read-001',
      turnIndex: 2,
      description: 'First read of /src/auth/jwt.ts (1,850 tokens returned)',
      executionId: 'exec-root-1',
    },
    {
      spanId: 'span-read-002',
      turnIndex: 3,
      description: 'Redundant second read of /src/auth/jwt.ts (1,850 tokens returned)',
      executionId: 'exec-root-1',
    },
    {
      spanId: 'span-read-003',
      turnIndex: 5,
      description: 'Third identical read after test runner execution',
      executionId: 'exec-root-1',
    },
  ],
  confidence: 'deterministic',
  measurementClass: 'deterministic',
  confidenceBasis:
    'Direct telemetry trace spans show byte-identical file content and tool call arguments across turns 2 and 3.',
  whatWouldRaiseIt:
    'Maximum confidence tier reached. The finding is backed by confirmed byte hashes and trace spans.',
  estimatedWasteTokens: 3700,
  errorBar: {
    lower: 3700,
    upper: 3700,
  },
  recommendation:
    'Relocate file content into working memory or defer reload until file change event occurs per Decision D8.',
  outcomeRiskCaveat:
    'If external processes modify files outside agent visibility, cached content may become stale. Verify file watch triggers.',
  harness: 'claude',
  runId: 'run-auth-001',
  sessionId: 'sess-auth-001',
  executionId: 'exec-root-1',
  rankScore: 3700 * 1.0,
}

const sampleHeuristicFinding: KyberFinding = {
  id: 'finding-cache-prefix-invalidation',
  detectorId: 'cache-prefix-shift',
  title: 'Dynamic System Prompt Invalidation',
  mechanism:
    'Timestamp and request-specific variables inserted at token position 420 invalidate prompt prefix cache for all downstream turns.',
  evidenceLinks: [
    {
      spanId: 'span-prefix-001',
      turnIndex: 1,
      description: 'Initial turn prompt prefix (cache write: 14,200 tokens)',
    },
    {
      spanId: 'span-prefix-002',
      turnIndex: 2,
      description: 'Cache miss on turn 2: fresh input jumped +85% due to timestamp drift',
    },
  ],
  confidence: 'heuristic',
  measurementClass: 'inferred',
  confidenceBasis:
    'Inferred from fresh token jump (+85%) between turns 1 and 2 without explicit cache hit counters from harness.',
  whatWouldRaiseIt:
    'To elevate to Calibrated Statistical: Gather ≥5 completed task pairs. To elevate to Deterministic: Enable harness cache read/creation counters.',
  estimatedWasteTokens: 28400,
  errorBar: {
    lower: 21000,
    upper: 35000,
  },
  recommendation:
    'Relocate volatile timestamps and dynamic session parameters after static prompt instructions per Decision D8.',
  outcomeRiskCaveat:
    'Reordering prompt components may subtly alter attention distribution on smaller models. Verify completion accuracy with unit tests.',
  harness: 'copilot',
  runId: 'run-copilot-002',
  rankScore: 28400 * 0.1, // 2,840
}

const sampleStatisticalFinding: KyberFinding = {
  id: 'finding-schema-bloat',
  detectorId: 'schema-resident-overhead',
  title: 'Unactivated MCP Tool Definitions',
  mechanism:
    '18 resident MCP tool schemas occupy 6,400 input tokens per turn, but were invoked in only 1 of 14 turns.',
  evidenceLinks: [
    {
      spanId: 'span-schema-001',
      turnIndex: 0,
      description: 'Initial context load containing 18 tool schemas (6,400 tokens)',
    },
    {
      spanId: 'span-schema-002',
      turnIndex: 7,
      description: 'Zero tool invocations recorded through turn 7 despite 44,800 cumulative tokens paid',
    },
  ],
  confidence: 'calibrated_statistical',
  measurementClass: 'inferred',
  confidenceBasis:
    'Empirically calibrated across 12 runs of task family "refactor-service" with observed tool activation rate < 8%.',
  whatWouldRaiseIt:
    'To elevate to Deterministic: Log discrete tool schema registration and schema assembly spans.',
  estimatedWasteTokens: 42000,
  errorBar: {
    lower: 32000,
    upper: 51000,
  },
  recommendation:
    'Apply progressive disclosure: load tool definitions on-demand via MCP tool search rather than pre-loading all schemas.',
  outcomeRiskCaveat:
    'If the model fails to search for tools when required, multi-step workflows may fail. Maintain core file editing tools resident.',
  harness: 'cursor',
  runId: 'run-cursor-003',
  rankScore: 42000 * 0.45,
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('FindingDetail Screen (Task G3 Compliance)', () => {
  beforeEach(() => {
    clearHooks()
  })

  it('Criterion 1: displays diagnosis title, detector mechanism explanation, and waste summary', () => {
    const html = renderHtml(<FindingDetail initialFinding={sampleDeterministicFinding} />)

    // Diagnosis region asserts
    expect(html).toContain('data-testid="diagnosis-panel"')
    expect(html).toContain('data-testid="diagnosis-title"')
    expect(html).toContain('Duplicate File Read Invocations')

    // Detector ID badge
    expect(html).toContain('duplicate-tool-call')

    // Detector mechanism explanation asserts
    expect(html).toContain('data-testid="detector-mechanism"')
    expect(html).toContain('Adjacent conversation turns executed identical byte-for-byte read operations')

    // Waste summary asserts
    expect(html).toContain('data-testid="waste-summary"')
    expect(html).toContain('3.7K')
    expect(html).toContain('data-testid="waste-summary-error-bar"')
    expect(html).toContain('[3.7K – 3.7K]')
  })

  it('Criterion 2: Evidence table displays all evidence links (>=2 links) with deep-link triggers', () => {
    const html = renderHtml(<FindingDetail initialFinding={sampleDeterministicFinding} />)

    // Evidence table asserts
    expect(html).toContain('data-testid="evidence-table"')
    expect(html).toContain('data-testid="evidence-count-badge"')
    expect(html).toContain('3 records')

    // All >= 2 evidence rows are displayed
    expect(html).toContain('data-testid="evidence-row-0"')
    expect(html).toContain('data-testid="evidence-row-1"')
    expect(html).toContain('data-testid="evidence-row-2"')

    // Deep link triggers
    expect(html).toContain('data-testid="deep-link-turn-2"')
    expect(html).toContain('data-testid="deep-link-turn-3"')
    expect(html).toContain('data-testid="deep-link-turn-5"')

    expect(html).toContain('data-testid="inspect-evidence-0"')
    expect(html).toContain('data-testid="inspect-evidence-1"')
    expect(html).toContain('data-testid="inspect-evidence-2"')

    // Evidence descriptions
    expect(html).toContain('First read of /src/auth/jwt.ts')
    expect(html).toContain('Redundant second read of /src/auth/jwt.ts')
    expect(html).toContain('Third identical read after test runner execution')
  })

  it('Criterion 2: deep-link trigger opens SessionInspectorDrawer pre-selected to target span/turn', () => {
    const targetLink: KyberEvidenceLink = sampleDeterministicFinding.evidenceLinks[1] // Turn 3, span-read-002
    const html = renderHtml(
      <FindingDetail
        initialFinding={sampleDeterministicFinding}
        initialSelectedLink={targetLink}
        initialDrawerOpen={true}
      />,
    )

    // Drawer is opened
    expect(html).toContain('data-testid="session-inspector-drawer"')
    expect(html).toContain('Turn #3 Evidence Inspector')
    expect(html).toContain('span-read-002')

    // Target evidence banner in drawer
    expect(html).toContain('data-testid="evidence-drawer-target-banner"')
    expect(html).toContain('Target Telemetry Evidence Link')
    expect(html).toContain(targetLink.description)
  })

  it('Criterion 3: Confidence panel displays tier, measurement basis, and what-would-raise-it', () => {
    // Test Deterministic
    const htmlDeterministic = renderHtml(<FindingDetail initialFinding={sampleDeterministicFinding} />)
    expect(htmlDeterministic).toContain('data-testid="confidence-panel"')
    expect(htmlDeterministic).toContain('data-testid="confidence-tier-badge"')
    expect(htmlDeterministic).toContain('Deterministic')
    expect(htmlDeterministic).toContain('[Ground Truth — Exact]')
    expect(htmlDeterministic).toContain('1.00x')

    expect(htmlDeterministic).toContain('data-testid="confidence-basis"')
    expect(htmlDeterministic).toContain(sampleDeterministicFinding.confidenceBasis!)

    expect(htmlDeterministic).toContain('data-testid="what-would-raise-it"')
    expect(htmlDeterministic).toContain(sampleDeterministicFinding.whatWouldRaiseIt!)

    // Test Heuristic
    const htmlHeuristic = renderHtml(<FindingDetail initialFinding={sampleHeuristicFinding} />)
    expect(htmlHeuristic).toContain('Heuristic')
    expect(htmlHeuristic).toContain('[Inferred Heuristic — Approximate]')
    expect(htmlHeuristic).toContain('0.10x')
    expect(htmlHeuristic).toContain(sampleHeuristicFinding.confidenceBasis!)
    expect(htmlHeuristic).toContain(sampleHeuristicFinding.whatWouldRaiseIt!)

    // Test Calibrated Statistical
    const htmlStatistical = renderHtml(<FindingDetail initialFinding={sampleStatisticalFinding} />)
    expect(htmlStatistical).toContain('Calibrated Statistical')
    expect(htmlStatistical).toContain('[Statistical Model — Bounded]')
    expect(htmlStatistical).toContain('Empirically calibrated across 12 runs')
    expect(htmlStatistical).toContain('Log discrete tool schema registration')
  })

  it('Criterion 3: confidence basis and what-would-raise-it are never collapsed behind interaction', () => {
    const html = renderHtml(<FindingDetail initialFinding={sampleHeuristicFinding} />)

    // Verify neither section is inside a <details> tag
    const confidencePanelHtml = html.slice(
      html.indexOf('data-testid="confidence-panel"'),
      html.indexOf('data-testid="recommendation-panel"'),
    )

    expect(confidencePanelHtml).not.toContain('<details')
    expect(confidencePanelHtml).toContain('data-testid="confidence-basis"')
    expect(confidencePanelHtml).toContain('data-testid="what-would-raise-it"')
  })

  it('Criterion 3: inferred findings are visually distinguishable from deterministic ones without relying on colour alone', () => {
    const htmlDeterministic = renderHtml(<FindingDetail initialFinding={sampleDeterministicFinding} />)
    const htmlHeuristic = renderHtml(<FindingDetail initialFinding={sampleHeuristicFinding} />)

    // Deterministic has [Ground Truth — Exact], checkmark symbol ✓, and solid border
    expect(htmlDeterministic).toContain('[Ground Truth — Exact]')
    expect(htmlDeterministic).toContain('✓')
    expect(htmlDeterministic).toContain('border-solid')
    expect(htmlDeterministic).not.toContain('data-testid="inferred-warning-note"')

    // Inferred/heuristic has explicit text badge, wave symbol ~, dotted border, and warning note
    expect(htmlHeuristic).toContain('[Inferred Heuristic — Approximate]')
    expect(htmlHeuristic).toContain('~')
    expect(htmlHeuristic).toContain('border-dotted')
    expect(htmlHeuristic).toContain('data-testid="inferred-warning-note"')
    expect(htmlHeuristic).toContain('This finding is inferred or heuristic')
  })

  it('Criterion 4: Recommendation panel renders D8 relocation/progressive disclosure actions with recoverable waste and error bars', () => {
    const html = renderHtml(<FindingDetail initialFinding={sampleStatisticalFinding} />)

    expect(html).toContain('data-testid="recommendation-panel"')
    expect(html).toContain('data-testid="d8-relocation-badge"')
    expect(html).toContain('D8: Relocate, Do Not Delete')

    // Recommendation prose
    expect(html).toContain('data-testid="recommendation-text"')
    expect(html).toContain('Apply progressive disclosure')

    // Recoverable waste token estimate
    expect(html).toContain('data-testid="recoverable-waste-tokens"')
    expect(html).toContain('42.0K tokens')

    // Calibrated error bar
    expect(html).toContain('data-testid="recommendation-error-bar"')
    expect(html).toContain('[32.0K – 51.0K]')
    expect(html).toContain('data-testid="error-bar-visual"')

    // D8 relocation strategy action card
    expect(html).toContain('data-testid="relocation-action-card"')
    expect(html).toContain('Progressive Disclosure &amp; On-Demand Loading')
    expect(html).toContain('Reversible')
  })

  it('Criterion 5: Outcome-risk caveat displays non-collapsible warning caveat', () => {
    const html = renderHtml(<FindingDetail initialFinding={sampleDeterministicFinding} />)

    expect(html).toContain('data-testid="outcome-risk-caveat"')
    expect(html).toContain('Outcome-Risk Caveat (Decision D5)')
    expect(html).toContain('Non-Collapsible Guard')
    expect(html).toContain(sampleDeterministicFinding.outcomeRiskCaveat)

    // Assert that the caveat is NOT wrapped in a collapsible <details> tag
    const caveatIndex = html.indexOf('data-testid="outcome-risk-caveat"')
    const nextSectionIndex = html.indexOf('data-testid="confidence-panel"')
    const caveatHtml = html.slice(caveatIndex, nextSectionIndex)
    expect(caveatHtml).not.toContain('<details')
    expect(caveatHtml).not.toContain('summary')
  })

  it('renders breadcrumb and supports onBack navigation', () => {
    const onBackMock = vi.fn()
    const html = renderHtml(
      <FindingDetail
        initialFinding={sampleDeterministicFinding}
        onBack={onBackMock}
      />,
    )

    expect(html).toContain('data-testid="hierarchy-breadcrumb"')
    expect(html).toContain('breadcrumb-harness')
    expect(html).toContain('claude')
    expect(html).toContain('breadcrumb-run')
    expect(html).toContain('data-testid="finding-detail-back-button"')
  })

  it('renders fallback when finding is not found', () => {
    const html = renderHtml(<FindingDetail />)
    expect(html).toContain('data-testid="finding-not-found"')
    expect(html).toContain('Finding Not Found')
  })

  it('renders loading state when finding is fetching', () => {
    const html = renderHtml(<FindingDetail findingId="loading-finding-id" />)
    expect(html).toContain('data-testid="finding-detail-loading"')
  })
})

describe('EvidenceTable Component Isolation', () => {
  it('renders all evidence links and triggers callbacks on inspect click', () => {
    const onSelectEvidence = vi.fn()
    const onSelectTurn = vi.fn()
    const onSelectSpan = vi.fn()

    const tree = EvidenceTable({
      evidenceLinks: sampleDeterministicFinding.evidenceLinks,
      onSelectEvidence,
      onSelectTurn,
      onSelectSpan,
    })

    const inspectBtn = findNodeByTestId(tree, 'inspect-evidence-0')
    expect(inspectBtn).not.toBeNull()

    inspectBtn!.props.onClick()
    expect(onSelectEvidence).toHaveBeenCalledWith(sampleDeterministicFinding.evidenceLinks[0])
    expect(onSelectTurn).toHaveBeenCalledWith(2, 'exec-root-1', 'span-read-001')
    expect(onSelectSpan).toHaveBeenCalledWith('span-read-001')
  })

  it('renders empty fallback when evidenceLinks is empty', () => {
    const html = renderToStaticMarkup(<EvidenceTable evidenceLinks={[]} />)
    expect(html).toContain('data-testid="evidence-table-empty"')
    expect(html).toContain('No evidence links recorded')
  })
})

describe('ConfidencePanel Component Isolation', () => {
  it('displays correct configuration for each confidence level', () => {
    const deterministicHtml = renderToStaticMarkup(
      <ConfidencePanel confidence="deterministic" measurementClass="deterministic" />,
    )
    expect(deterministicHtml).toContain('Deterministic')
    expect(deterministicHtml).toContain('1.00x')

    const statisticalHtml = renderToStaticMarkup(
      <ConfidencePanel confidence="calibrated_statistical" measurementClass="inferred" />,
    )
    expect(statisticalHtml).toContain('Calibrated Statistical')
    expect(statisticalHtml).toContain('0.45x')

    const heuristicHtml = renderToStaticMarkup(
      <ConfidencePanel confidence="heuristic" measurementClass="inferred" />,
    )
    expect(heuristicHtml).toContain('Heuristic')
    expect(heuristicHtml).toContain('0.10x')
  })
})

describe('RecommendationPanel Component Isolation', () => {
  it('renders cache reordering action when recommendation addresses cache positioning', () => {
    const html = renderToStaticMarkup(
      <RecommendationPanel
        recommendation="Move dynamic session ID after static system prompt prefix to restore cache hit rate."
        estimatedWasteTokens={15000}
        errorBar={{ lower: 12000, upper: 18000 }}
      />,
    )

    expect(html).toContain('Cache Breakpoint Repositioning')
    expect(html).toContain('D8: Relocate, Do Not Delete')
    expect(html).toContain('15.0K tokens')
    expect(html).toContain('[12.0K – 18.0K]')
  })
})
