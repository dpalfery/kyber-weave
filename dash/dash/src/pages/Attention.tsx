import { useState, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { usd } from '../lib/utils'
import { Card } from '../components/ui/card'
import { Skeleton } from '../components/ui/skeleton'
import {
  fetchHarnesses,
  fetchFindings,
  type KyberHarnessSummary,
  type KyberFinding,
  type ScorecardData,
} from '../lib/kyberApi'
import {
  HierarchyBreadcrumb,
  Scorecard,
  FindingList,
  BaselineSelect,
} from '../components/kyber'

export interface AttentionProps {
  initialHarnesses?: KyberHarnessSummary[]
  initialFindings?: KyberFinding[]
  onSelectHarness?: (harnessId: string) => void
  onSelectRun?: (runId: string, harnessId?: string) => void
  onSelectFinding?: (findingId: string) => void
  onSelectTurn?: (turnIndex: number, executionId?: string, runId?: string) => void
}

/**
 * Fallback surveyed harnesses catalogued in ADR 0009 and Task E4.
 * If live store has no rollups yet, these display with honest '—' unmeasured indicators.
 */
const DEFAULT_SURVEYED_HARNESSES: KyberHarnessSummary[] = [
  {
    harness: 'claude',
    name: 'Claude Code',
    sampleCount: 0,
    fieldCoverage: 0.83,
    measurability: {
      token_usage: 'measured',
      cache_hit_rate: 'measured',
      prefix_stability: 'derived',
      tool_yield: 'measured',
      delegation_overhead: 'not_measurable',
      context_pressure: 'measured',
    },
  },
  {
    harness: 'codex',
    name: 'Codex',
    sampleCount: 0,
    fieldCoverage: 0.83,
    measurability: {
      token_usage: 'measured',
      cache_hit_rate: 'measured',
      prefix_stability: 'derived',
      tool_yield: 'measured',
      delegation_overhead: 'not_measurable',
      context_pressure: 'measured',
    },
  },
  {
    harness: 'cursor',
    name: 'Cursor',
    sampleCount: 0,
    fieldCoverage: 0.33,
    measurability: {
      token_usage: 'measured',
      cache_hit_rate: 'not_measurable',
      prefix_stability: 'not_measurable',
      tool_yield: 'not_measurable',
      delegation_overhead: 'not_measurable',
      context_pressure: 'measured',
    },
  },
  {
    harness: 'copilot',
    name: 'GitHub Copilot',
    sampleCount: 0,
    fieldCoverage: 0.67,
    measurability: {
      token_usage: 'measured',
      cache_hit_rate: 'not_measurable',
      prefix_stability: 'not_measurable',
      tool_yield: 'measured',
      delegation_overhead: 'measured',
      context_pressure: 'measured',
    },
  },
  {
    harness: 'gemini',
    name: 'Gemini / Antigravity',
    sampleCount: 0,
    fieldCoverage: 0.67,
    measurability: {
      token_usage: 'measured',
      cache_hit_rate: 'not_measurable',
      prefix_stability: 'not_measurable',
      tool_yield: 'measured',
      delegation_overhead: 'measured',
      context_pressure: 'measured',
    },
  },
  {
    harness: 'pi',
    name: 'Pi',
    sampleCount: 0,
    fieldCoverage: 0.5,
    measurability: {
      token_usage: 'measured',
      cache_hit_rate: 'not_measurable',
      prefix_stability: 'not_measurable',
      tool_yield: 'measured',
      delegation_overhead: 'not_measurable',
      context_pressure: 'measured',
    },
  },
]

export function Attention({
  initialHarnesses,
  initialFindings,
  onSelectHarness,
  onSelectRun,
  onSelectFinding,
  onSelectTurn,
}: AttentionProps) {
  const [baseline, setBaseline] = useState('none')

  // Query live harnesses
  const { data: harnessesData, isLoading: loadingHarnesses } = useQuery({
    queryKey: ['kyber-harnesses'],
    queryFn: fetchHarnesses,
    initialData: initialHarnesses,
  })

  // Query live findings across the entire workspace
  const { data: findingsData, isLoading: loadingFindings } = useQuery({
    queryKey: ['kyber-findings-workspace'],
    queryFn: () => fetchFindings({ limit: 10 }),
    initialData: initialFindings,
  })

  const harnesses = useMemo(() => {
    if (harnessesData && harnessesData.length > 0) return harnessesData
    return DEFAULT_SURVEYED_HARNESSES
  }, [harnessesData])

  const findings = findingsData ?? []

  // Aggregate workspace diagnostic dimensions (never calculating a single composite score per D3!)
  const workspaceScorecardData: ScorecardData = useMemo(() => {
    // Collect non-zero values across harnesses
    const measuredPressures = harnesses
      .map((h) => h.contextPressureMedian)
      .filter((v): v is number => typeof v === 'number')

    const measuredCacheHits = harnesses
      .map((h) => h.cacheHitRate)
      .filter((v): v is number => typeof v === 'number')

    const measuredToolYields = harnesses
      .map((h) => h.toolYield)
      .filter((v): v is number => typeof v === 'number')

    const measuredDelegation = harnesses
      .map((h) => h.delegationOverhead)
      .filter((v): v is number => typeof v === 'number')

    const totalCost = harnesses.reduce((acc, h) => acc + (h.costUsd ?? 0), 0)

    return {
      dimensions: {
        contextHygiene: {
          key: 'contextHygiene',
          name: 'Context Hygiene',
          value: measuredPressures.length > 0 ? measuredPressures[0] : null,
          formatted: measuredPressures.length > 0 ? `${Math.round(measuredPressures[0] * 100)}%` : undefined,
          status: measuredPressures.length > 0 ? 'measured' : 'not_measurable',
          reason: measuredPressures.length === 0 ? 'No context pressure telemetry aggregated' : undefined,
          measurementClass: 'deterministic',
        },
        cacheEfficiency: {
          key: 'cacheEfficiency',
          name: 'Cache Efficiency',
          value: measuredCacheHits.length > 0 ? measuredCacheHits[0] : null,
          formatted: measuredCacheHits.length > 0 ? `${Math.round(measuredCacheHits[0] * 100)}%` : undefined,
          status: measuredCacheHits.length > 0 ? 'measured' : 'not_measurable',
          reason: measuredCacheHits.length === 0 ? 'No cache hit counters observed across harnesses' : undefined,
          measurementClass: 'inferred',
        },
        toolYield: {
          key: 'toolYield',
          name: 'Tool Yield',
          value: measuredToolYields.length > 0 ? measuredToolYields[0] : null,
          formatted: measuredToolYields.length > 0 ? `${measuredToolYields[0].toFixed(1)}x` : undefined,
          status: measuredToolYields.length > 0 ? 'measured' : 'not_measurable',
          reason: measuredToolYields.length === 0 ? 'No tool call telemetry recorded' : undefined,
        },
        skillUtilisation: {
          key: 'skillUtilisation',
          name: 'Skill Utilisation',
          status: 'not_measurable',
          reason: 'Skill activation unobserved on current active harnesses (Decision D16)',
          measurementClass: 'coverage-gap',
        },
        delegationOverhead: {
          key: 'delegationOverhead',
          name: 'Delegation Overhead',
          value: measuredDelegation.length > 0 ? measuredDelegation[0] : null,
          formatted: measuredDelegation.length > 0 ? `${Math.round(measuredDelegation[0] * 100)}%` : undefined,
          status: measuredDelegation.length > 0 ? 'measured' : 'not_measurable',
          reason: measuredDelegation.length === 0 ? 'Single-agent or uninstrumented subagent runs' : undefined,
        },
        continuity: {
          key: 'continuity',
          name: 'Continuity',
          status: 'not_measurable',
          reason: 'Correction/abandonment tracking unobserved across harnesses',
          measurementClass: 'coverage-gap',
        },
      },
      secondaryCost: {
        costUsd: totalCost > 0 ? totalCost : null,
        basis: 'workspace_rollup',
        status: totalCost > 0 ? 'derived' : 'not_measurable',
      },
    }
  }, [harnesses])

  return (
    <div className="flex flex-col gap-4" data-testid="page-attention">
      {/* Top Header & Spine Navigation */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <HierarchyBreadcrumb />
        <BaselineSelect value={baseline} onChange={setBaseline} />
      </div>

      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <h2 className="font-display text-2xl font-bold tracking-tight text-foreground">
            Workspace Attention
          </h2>
          <p className="text-xs text-muted-foreground mt-0.5">
            Six-level spine landing page: All Harnesses. Overview of telemetry health, highest-leverage findings, and harness postures.
          </p>
        </div>
      </div>

      {/* Level 1 Scorecard (NO composite score per D3, secondary cost per D9) */}
      <Scorecard
        data={workspaceScorecardData}
        title="Workspace Diagnostic Scorecard (All Harnesses)"
      />

      {/* Highest Leverage Findings per Decision D6 */}
      {loadingFindings ? (
        <Skeleton className="h-44 w-full" />
      ) : (
        <FindingList
          findings={findings}
          maxItems={5}
          title="Highest-Leverage Workspace Findings"
          description="Ranked by Decision D6 formula (estimated waste × outcome risk × confidence). Deterministic evidence beats inferred claims."
          onSelectFinding={onSelectFinding}
          onSelectTurn={(turnIdx, execId) => onSelectTurn?.(turnIdx, execId)}
          onSelectExecution={(execId) => onSelectRun?.(execId)}
        />
      )}

      {/* Harness Health Grid */}
      <Card className="p-4" data-testid="harness-health-table">
        <div className="flex items-center justify-between border-b border-border/60 pb-3 mb-3">
          <div>
            <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
              Agent Harnesses ({harnesses.length})
            </h3>
            <p className="text-[11px] text-muted-foreground mt-0.5">
              Select a harness to drill down into Level 2 (Harness Detail).
            </p>
          </div>
        </div>

        {loadingHarnesses ? (
          <div className="flex flex-col gap-2">
            {Array.from({ length: 4 }).map((_, i) => (
              <Skeleton key={i} className="h-12 w-full" />
            ))}
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-xs">
              <thead>
                <tr className="border-b border-border text-[11px] font-semibold text-tertiary-foreground uppercase tracking-wider">
                  <th className="py-2.5 px-3">Harness</th>
                  <th className="py-2.5 px-3 text-right">Runs</th>
                  <th className="py-2.5 px-3 text-right">Field Coverage</th>
                  <th className="py-2.5 px-3 text-right">Context Pressure</th>
                  <th className="py-2.5 px-3 text-right">Cache Hit</th>
                  <th className="py-2.5 px-3 text-right">Findings</th>
                  {/* Decision D9: Cost is secondary column only */}
                  <th className="py-2.5 px-3 text-right text-tertiary-foreground font-normal italic">
                    Cost (derived)
                  </th>
                  <th className="py-2.5 px-3 text-right">Action</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border/40">
                {harnesses.map((h) => {
                  const hasCache =
                    typeof h.cacheHitRate === 'number'
                      ? `${Math.round(h.cacheHitRate * 100)}%`
                      : '—'
                  const hasPressure =
                    typeof h.contextPressureMedian === 'number'
                      ? `${Math.round(h.contextPressureMedian * 100)}%`
                      : '—'
                  const coveragePct =
                    typeof h.fieldCoverage === 'number'
                      ? `${Math.round(h.fieldCoverage * 100)}%`
                      : '—'

                  return (
                    <tr
                      key={h.harness}
                      data-testid={`harness-row-${h.harness}`}
                      className="hover:bg-interactive-secondary/30 transition-colors"
                    >
                      <td className="py-2.5 px-3">
                        <div className="font-semibold text-foreground">
                          {h.name || h.harness}
                        </div>
                        <div className="text-[10px] font-mono text-tertiary-foreground">
                          {h.harness}
                        </div>
                      </td>
                      <td className="py-2.5 px-3 text-right font-mono tabular-nums text-foreground">
                        {h.sampleCount ?? h.runCount ?? 0}
                      </td>
                      <td className="py-2.5 px-3 text-right font-mono tabular-nums text-foreground">
                        {coveragePct}
                      </td>
                      <td
                        className="py-2.5 px-3 text-right font-mono tabular-nums"
                        title={hasPressure === '—' ? 'Context pressure unmeasured' : undefined}
                      >
                        {hasPressure === '—' ? (
                          <span className="text-tertiary-foreground font-mono">—</span>
                        ) : (
                          hasPressure
                        )}
                      </td>
                      <td
                        className="py-2.5 px-3 text-right font-mono tabular-nums"
                        title={hasCache === '—' ? 'Cache hit counters unmeasured' : undefined}
                      >
                        {hasCache === '—' ? (
                          <span className="text-tertiary-foreground font-mono">—</span>
                        ) : (
                          hasCache
                        )}
                      </td>
                      <td className="py-2.5 px-3 text-right font-mono tabular-nums text-amber-500 dark:text-amber-400">
                        {h.findingCount ?? 0}
                      </td>
                      {/* Decision D9: Muted secondary derived cost */}
                      <td className="py-2.5 px-3 text-right font-mono tabular-nums text-muted-foreground/80">
                        {h.costUsd ? usd(h.costUsd) : '—'}
                      </td>
                      <td className="py-2.5 px-3 text-right">
                        <button
                          type="button"
                          onClick={() => onSelectHarness?.(h.harness)}
                          data-testid={`drill-harness-${h.harness}`}
                          className="rounded bg-interactive-secondary px-2 py-1 text-xs font-medium text-foreground hover:bg-primary hover:text-primary-foreground transition-colors"
                        >
                          Inspect →
                        </button>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </div>
  )
}
