import { useState, useMemo } from 'react'
import { cn, fmtTokens } from '../../lib/utils.js'
import { Card } from '../ui/card.js'
import type { KyberFinding, KyberEvidenceLink } from '../../lib/kyberApi.js'

export interface FindingListProps {
  findings?: KyberFinding[]
  title?: string
  description?: string
  maxItems?: number
  onSelectFinding?: (findingId: string) => void
  onSelectTurn?: (turnIndex: number, executionId?: string) => void
  onSelectExecution?: (executionId: string) => void
  onSelectSpan?: (spanId: string) => void
  className?: string
}

/**
 * Confidence multipliers: Deterministic (1.0), Calibrated Statistical (0.45), Heuristic (0.10).
 * Deterministic evidence outranks a larger inferred finding.
 */
const CONFIDENCE_WEIGHTS: Record<string, number> = {
  deterministic: 1.0,
  calibrated_statistical: 0.45,
  heuristic: 0.10,
}

const CONFIDENCE_TIER: Record<string, number> = {
  deterministic: 3,
  calibrated_statistical: 2,
  heuristic: 1,
}

const MEASUREMENT_CLASS_TIER: Record<string, number> = {
  deterministic: 3,
  inferred: 2,
  'coverage-gap': 1,
}

export function measurementClassOf(finding: KyberFinding): string {
  if (finding.measurementClass) return finding.measurementClass
  return finding.confidence === 'deterministic' ? 'deterministic' : 'inferred'
}

/**
 * Computes or retrieves rank score for ordering findings.
 * Ranking is estimated recoverable waste * confidence multiplier.
 * Deterministic findings beat larger inferred findings.
 */
export function getFindingRankScore(finding: KyberFinding): number {
  if (typeof finding.rankScore === 'number') {
    return finding.rankScore
  }
  const weight = CONFIDENCE_WEIGHTS[finding.confidence] ?? 0.5
  return (finding.estimatedWasteTokens || 0) * weight
}

export function compareFindingsByRank(a: KyberFinding, b: KyberFinding): number {
  // B1: a deterministic finding ranks above a larger inferred/heuristic finding.
  const classDiff =
    (MEASUREMENT_CLASS_TIER[measurementClassOf(b)] ?? 0) -
    (MEASUREMENT_CLASS_TIER[measurementClassOf(a)] ?? 0)
  if (classDiff !== 0) return classDiff

  const confDiff =
    (CONFIDENCE_TIER[b.confidence] ?? 0) - (CONFIDENCE_TIER[a.confidence] ?? 0)
  if (confDiff !== 0) return confDiff

  const scoreDiff = getFindingRankScore(b) - getFindingRankScore(a)
  if (Math.abs(scoreDiff) > 0.001) return scoreDiff

  return (b.estimatedWasteTokens || 0) - (a.estimatedWasteTokens || 0)
}

export function FindingConfidenceBadge({
  confidence,
  measurementClass,
}: {
  confidence: string
  measurementClass?: string
}) {
  const isDeterministic = confidence === 'deterministic'
  const isHeuristic = confidence === 'heuristic'

  return (
    <div className="flex items-center gap-density-inline flex-wrap">
      <span
        data-testid="finding-confidence-badge"
        className={cn(
          'rounded-chrome px-chrome-sm py-chrome-xs text-density-2xs font-medium tracking-density uppercase',
          isDeterministic
            ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 border border-emerald-500/20'
            : isHeuristic
              ? 'bg-purple-500/10 text-purple-600 dark:text-purple-400 border border-purple-500/20'
              : 'bg-amber-500/10 text-amber-600 dark:text-amber-400 border border-amber-500/20',
        )}
      >
        {confidence.replace('_', ' ')}
      </span>
      {measurementClass && (
        <span className="rounded-chrome bg-interactive-secondary px-chrome-sm py-chrome-xs text-density-2xs font-mono text-tertiary-foreground">
          {measurementClass.replace('-', ' ')}
        </span>
      )}
    </div>
  )
}

export function FindingCard({
  finding,
  onSelectFinding,
  onSelectTurn,
  onSelectExecution,
  onSelectSpan,
}: {
  finding: KyberFinding
  onSelectFinding?: (findingId: string) => void
  onSelectTurn?: (turnIndex: number, executionId?: string) => void
  onSelectExecution?: (executionId: string) => void
  onSelectSpan?: (spanId: string) => void
}) {
  const [expanded, setExpanded] = useState(false)
  const rankScore = getFindingRankScore(finding)
  const measurementClass = measurementClassOf(finding)
  const outcomeRisk =
    finding.outcomeRiskCaveat?.trim() ||
    'Outcome risk is not stated in telemetry.'

  return (
    <div
      data-testid={`finding-card-${finding.id}`}
      className={cn(
        'rounded-chrome border border-border/80 bg-card p-chrome transition-all shadow-xs',
        'hover:border-primary/40 hover:shadow-sm',
      )}
    >
      <div className="flex flex-wrap items-start justify-between gap-density-cluster">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-density-cluster flex-wrap mb-density-hair">
            <FindingConfidenceBadge
              confidence={finding.confidence}
              measurementClass={measurementClass}
            />
            <span className="text-density-2xs font-mono text-tertiary-foreground">
              {finding.detectorId}
            </span>
            <span
              className="rounded-chrome bg-interactive-secondary px-chrome-sm py-chrome-xs text-density-2xs font-mono text-muted-foreground"
              title={`Rank score: ${rankScore.toFixed(0)}`}
            >
              rank #{rankScore.toFixed(0)}
            </span>
          </div>

          <h4
            data-testid={`drill-finding-${finding.id}`}
            role={onSelectFinding ? 'button' : undefined}
            tabIndex={onSelectFinding ? 0 : undefined}
            onClick={() => onSelectFinding?.(finding.id)}
            onKeyDown={(e) => {
              if (!onSelectFinding) return
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault()
                onSelectFinding(finding.id)
              }
            }}
            className={cn(
              'font-semibold text-density-sm text-foreground tracking-density',
              onSelectFinding && 'cursor-pointer hover:text-primary transition-colors',
            )}
          >
            {finding.title}
          </h4>

          <p className="mt-density-hair text-density-xs text-muted-foreground leading-density">
            {finding.mechanism}
          </p>
        </div>

        {/* Estimated Waste with Error Bar */}
        <div className="shrink-0 text-right">
          <div className="text-density-2xs uppercase tracking-density text-tertiary-foreground">
            Estimated Waste
          </div>
          <div
            className="font-display text-density-lg font-semibold text-amber-500 dark:text-amber-400 tabular-nums"
            data-testid="finding-waste-tokens"
          >
            {fmtTokens(finding.estimatedWasteTokens)}
          </div>
          {finding.errorBar && (
            <div
              className="text-density-2xs font-mono text-tertiary-foreground tabular-nums"
              title="Calibrated confidence error bar"
            >
              [{fmtTokens(finding.errorBar.lower)} – {fmtTokens(finding.errorBar.upper)}]
            </div>
          )}
        </div>
      </div>

      {/* Decision D8: Recommendation strictly prefers relocation/progressive disclosure over deletion */}
      <div className="mt-density-cluster rounded-chrome border border-primary/20 bg-primary/5 p-chrome-sm text-density-xs text-foreground">
        <div className="flex items-center gap-density-inline font-semibold text-density-xs text-primary uppercase tracking-density mb-density-hair">
          <svg
            viewBox="0 0 16 16"
            width="12"
            height="12"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.8"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <path d="M8 2v8M4 6l4-4 4 4M2 14h12" />
          </svg>
          Recommendation
        </div>
        <p className="text-muted-foreground leading-density">
          {finding.recommendation}
        </p>
      </div>

      <div className="mt-density-cluster rounded-chrome border border-border bg-interactive-secondary/30 px-chrome-sm py-chrome-xs text-density-xs text-tertiary-foreground">
        <span className="font-semibold text-foreground/80">Outcome Risk Caveat: </span>
        <span>{outcomeRisk}</span>
      </div>

      {/* Evidence Links (Deep-links to Turn, Span, Execution per Decision D5) */}
      {finding.evidenceLinks && finding.evidenceLinks.length > 0 && (
        <div className="mt-density-cluster border-t border-border/50 pt-chrome-sm">
          <div className="flex items-center justify-between">
            <span className="text-density-2xs font-semibold uppercase tracking-density text-tertiary-foreground">
              Evidence Links ({finding.evidenceLinks.length})
            </span>
            {finding.evidenceLinks.length > 2 && (
              <button
                type="button"
                onClick={() => setExpanded(!expanded)}
                className="text-density-2xs text-primary hover:underline"
              >
                {expanded ? 'Show fewer' : `Show all ${finding.evidenceLinks.length}`}
              </button>
            )}
          </div>

          <ul className="mt-density-inline flex flex-col gap-density-hair text-density-xs">
            {(expanded ? finding.evidenceLinks : finding.evidenceLinks.slice(0, 2)).map(
              (link: KyberEvidenceLink, lIdx: number) => {
                const hasTurn = link.turnIndex !== undefined && link.turnIndex !== null
                const hasSpan = Boolean(link.spanId)
                const hasExec = Boolean(link.executionId || finding.executionId)

                return (
                  <li
                    key={lIdx}
                    className="flex items-center gap-density-cluster rounded-chrome bg-card/60 px-chrome-sm py-chrome-xs text-density-xs text-muted-foreground border border-border/40"
                  >
                    <span className="shrink-0 text-tertiary-foreground">↳</span>

                    {/* Turn Deep Link */}
                    {hasTurn && (
                      <button
                        type="button"
                        onClick={() =>
                          onSelectTurn?.(link.turnIndex, link.executionId || finding.executionId)
                        }
                        data-testid={`evidence-link-turn-${link.turnIndex}`}
                        className="rounded-chrome bg-interactive-secondary px-chrome-sm py-chrome-xs font-mono text-density-2xs text-primary hover:underline"
                        title={`Jump to turn #${link.turnIndex}`}
                      >
                        Turn #{link.turnIndex}
                      </button>
                    )}

                    {/* Execution Link */}
                    {hasExec && onSelectExecution && (
                      <button
                        type="button"
                        onClick={() =>
                          onSelectExecution?.(link.executionId || finding.executionId!)
                        }
                        className="rounded-chrome bg-interactive-secondary px-chrome-sm py-chrome-xs font-mono text-density-2xs text-tertiary-foreground hover:text-foreground"
                      >
                        Exec {((link.executionId || finding.executionId)!).slice(0, 6)}
                      </button>
                    )}

                    {/* Span Link */}
                    {hasSpan && (
                      <span
                        onClick={() => onSelectSpan?.(link.spanId)}
                        className={cn(
                          'font-mono text-density-2xs text-tertiary-foreground truncate max-w-[120px]',
                          onSelectSpan && 'cursor-pointer hover:underline',
                        )}
                        title={`Telemetry span: ${link.spanId}`}
                      >
                        span:{link.spanId.slice(0, 8)}
                      </span>
                    )}

                    <span className="truncate flex-1 text-foreground/90">
                      {link.description}
                    </span>
                  </li>
                )
              },
            )}
          </ul>
        </div>
      )}
    </div>
  )
}

export function FindingList({
  findings = [],
  title = 'Highest-Leverage Diagnostic Findings',
  description = 'Ranked by estimated recoverable waste, outcome risk, and confidence. Deterministic evidence outranks inferred claims.',
  maxItems,
  onSelectFinding,
  onSelectTurn,
  onSelectExecution,
  onSelectSpan,
  className,
}: FindingListProps) {
  // Filter and Sort strictly by Decision D6 rankScore descending. NEVER sort by cost per D9!
  const sortedFindings = useMemo(() => {
    const list = [...findings]
    list.sort(compareFindingsByRank)
    return maxItems ? list.slice(0, maxItems) : list
  }, [findings, maxItems])

  return (
    <Card className={cn('p-chrome', className)} data-testid="finding-list">
      <div className="flex flex-wrap items-baseline justify-between gap-density-cluster border-b border-border/60 pb-chrome-sm">
        <div>
          <div className="flex items-center gap-density-cluster">
            <h3 className="text-density-xs font-semibold uppercase tracking-density text-heading">
              {title}
            </h3>
            <span className="rounded-chrome bg-interactive-secondary px-chrome-sm py-chrome-xs text-density-2xs font-mono text-tertiary-foreground">
              {sortedFindings.length} finding{sortedFindings.length === 1 ? '' : 's'}
            </span>
          </div>
          {description && (
            <p className="mt-density-hair text-density-xs text-muted-foreground leading-density">
              {description}
            </p>
          )}
        </div>
      </div>

      {sortedFindings.length === 0 ? (
        <p
          data-testid="finding-list-empty"
          className="mt-density-cluster text-density-xs text-muted-foreground"
        >
          No findings detected
        </p>
      ) : (
        <div className="mt-density-stack flex flex-col gap-density-cluster">
          {sortedFindings.map((f) => (
            <FindingCard
              key={f.id}
              finding={f}
              onSelectFinding={onSelectFinding}
              onSelectTurn={onSelectTurn}
              onSelectExecution={onSelectExecution}
              onSelectSpan={onSelectSpan}
            />
          ))}
        </div>
      )}
    </Card>
  )
}
