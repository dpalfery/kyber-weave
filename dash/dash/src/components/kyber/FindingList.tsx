import { useState, useMemo } from 'react'
import { cn, fmtTokens } from '../../lib/utils'
import { Card } from '../ui/card'
import type { KyberFinding, KyberEvidenceLink } from '../../lib/kyberApi'

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
 * Confidence multipliers per Decision D6:
 * Deterministic (1.0), Calibrated Statistical (0.45), Heuristic (0.10)
 */
const CONFIDENCE_WEIGHTS: Record<string, number> = {
  deterministic: 1.0,
  calibrated_statistical: 0.45,
  heuristic: 0.10,
}

/**
 * Computes or retrieves D6 rank score for ordering findings.
 * Ranking is estimated recoverable waste * confidence multiplier * (1 - outcomeRiskDiscount).
 * Deterministic findings beat larger inferred findings.
 */
export function getFindingRankScore(finding: KyberFinding): number {
  if (typeof finding.rankScore === 'number') {
    return finding.rankScore
  }
  const weight = CONFIDENCE_WEIGHTS[finding.confidence] ?? 0.5
  return (finding.estimatedWasteTokens || 0) * weight
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
    <div className="flex items-center gap-1.5 flex-wrap">
      <span
        data-testid="finding-confidence-badge"
        className={cn(
          'rounded px-2 py-0.5 text-[10.5px] font-medium tracking-wide uppercase',
          isDeterministic
            ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 border border-emerald-500/20'
            : isHeuristic
              ? 'bg-purple-500/10 text-purple-600 dark:text-purple-400 border border-purple-500/20'
              : 'bg-amber-500/10 text-amber-600 dark:text-amber-400 border border-amber-500/20',
        )}
      >
        {confidence.replace('_', ' ')}
      </span>
      {measurementClass && measurementClass !== confidence && (
        <span className="rounded bg-interactive-secondary px-1.5 py-0.5 text-[10px] font-mono text-tertiary-foreground">
          {measurementClass}
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

  return (
    <div
      data-testid={`finding-card-${finding.id}`}
      className={cn(
        'rounded-lg border border-border/80 bg-card p-4 transition-all shadow-xs',
        'hover:border-primary/40 hover:shadow-sm',
      )}
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 flex-wrap mb-1">
            <FindingConfidenceBadge
              confidence={finding.confidence}
              measurementClass={finding.measurementClass}
            />
            <span className="text-[10.5px] font-mono text-tertiary-foreground">
              {finding.detectorId}
            </span>
            <span
              className="rounded bg-interactive-secondary px-1.5 py-0.5 text-[9.5px] font-mono text-muted-foreground"
              title={`Rank Score: ${rankScore.toFixed(0)} per Decision D6 formula`}
            >
              rank #{rankScore.toFixed(0)}
            </span>
          </div>

          <h4
            onClick={() => onSelectFinding?.(finding.id)}
            className={cn(
              'font-semibold text-sm text-foreground tracking-tight',
              onSelectFinding && 'cursor-pointer hover:text-primary transition-colors',
            )}
          >
            {finding.title}
          </h4>

          <p className="mt-1 text-xs text-muted-foreground leading-relaxed">
            {finding.mechanism}
          </p>
        </div>

        {/* Estimated Waste with Error Bar */}
        <div className="shrink-0 text-right">
          <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
            Estimated Waste
          </div>
          <div
            className="font-display text-lg font-semibold text-amber-500 dark:text-amber-400 tabular-nums"
            data-testid="finding-waste-tokens"
          >
            {fmtTokens(finding.estimatedWasteTokens)}
          </div>
          {finding.errorBar && (
            <div
              className="text-[10px] font-mono text-tertiary-foreground tabular-nums"
              title="Calibrated confidence error bar (Decision D5)"
            >
              [{fmtTokens(finding.errorBar.lower)} – {fmtTokens(finding.errorBar.upper)}]
            </div>
          )}
        </div>
      </div>

      {/* Decision D8: Recommendation strictly prefers relocation/progressive disclosure over deletion */}
      <div className="mt-3 rounded-md border border-primary/20 bg-primary/5 p-2.5 text-xs text-foreground">
        <div className="flex items-center gap-1.5 font-semibold text-[11px] text-primary uppercase tracking-wider mb-1">
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
          Recommendation (D8: Relocate, Do Not Delete)
        </div>
        <p className="text-muted-foreground leading-relaxed">
          {finding.recommendation}
        </p>
      </div>

      {/* Outcome Risk Caveat (Decision D5) */}
      {finding.outcomeRiskCaveat && (
        <div className="mt-2 rounded-md border border-border bg-interactive-secondary/30 px-2.5 py-1.5 text-[11px] text-tertiary-foreground">
          <span className="font-semibold text-foreground/80">Outcome Risk Caveat: </span>
          <span>{finding.outcomeRiskCaveat}</span>
        </div>
      )}

      {/* Evidence Links (Deep-links to Turn, Span, Execution per Decision D5) */}
      {finding.evidenceLinks && finding.evidenceLinks.length > 0 && (
        <div className="mt-3 border-t border-border/50 pt-2.5">
          <div className="flex items-center justify-between">
            <span className="text-[10px] font-semibold uppercase tracking-wider text-tertiary-foreground">
              Evidence Links ({finding.evidenceLinks.length})
            </span>
            {finding.evidenceLinks.length > 2 && (
              <button
                type="button"
                onClick={() => setExpanded(!expanded)}
                className="text-[10.5px] text-primary hover:underline"
              >
                {expanded ? 'Show fewer' : `Show all ${finding.evidenceLinks.length}`}
              </button>
            )}
          </div>

          <ul className="mt-1.5 flex flex-col gap-1 text-xs">
            {(expanded ? finding.evidenceLinks : finding.evidenceLinks.slice(0, 2)).map(
              (link: KyberEvidenceLink, lIdx: number) => {
                const hasTurn = link.turnIndex !== undefined && link.turnIndex !== null
                const hasSpan = Boolean(link.spanId)
                const hasExec = Boolean(link.executionId || finding.executionId)

                return (
                  <li
                    key={lIdx}
                    className="flex items-center gap-2 rounded bg-card/60 px-2 py-1 text-[11.5px] text-muted-foreground border border-border/40"
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
                        className="rounded bg-interactive-secondary px-1.5 py-0.5 font-mono text-[10.5px] text-primary hover:underline"
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
                        className="rounded bg-interactive-secondary px-1.5 py-0.5 font-mono text-[10.5px] text-tertiary-foreground hover:text-foreground"
                      >
                        Exec {((link.executionId || finding.executionId)!).slice(0, 6)}
                      </button>
                    )}

                    {/* Span Link */}
                    {hasSpan && (
                      <span
                        onClick={() => onSelectSpan?.(link.spanId)}
                        className={cn(
                          'font-mono text-[10px] text-tertiary-foreground truncate max-w-[120px]',
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
  description = 'Ranked by Decision D6 formula: estimated recoverable waste × outcome risk × confidence. Deterministic evidence outranks inferred claims.',
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
    list.sort((a, b) => getFindingRankScore(b) - getFindingRankScore(a))
    return maxItems ? list.slice(0, maxItems) : list
  }, [findings, maxItems])

  return (
    <Card className={cn('p-4', className)} data-testid="finding-list">
      <div className="flex flex-wrap items-baseline justify-between gap-2 border-b border-border/60 pb-3">
        <div>
          <div className="flex items-center gap-2">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
              {title}
            </h3>
            <span className="rounded bg-interactive-secondary px-2 py-0.5 text-[10.5px] font-mono text-tertiary-foreground">
              {sortedFindings.length} finding{sortedFindings.length === 1 ? '' : 's'}
            </span>
          </div>
          {description && (
            <p className="mt-0.5 text-[11px] text-muted-foreground">
              {description}
            </p>
          )}
        </div>
      </div>

      {sortedFindings.length === 0 ? (
        <div
          data-testid="finding-list-empty"
          className="my-6 rounded-lg border border-dashed border-border p-8 text-center text-xs text-muted-foreground"
        >
          <p className="font-medium text-foreground">No findings detected</p>
          <p className="mt-1 text-[11px] text-tertiary-foreground">
            No actionable waste signals or telemetry defects identified for the active scope.
          </p>
        </div>
      ) : (
        <div className="mt-4 flex flex-col gap-3">
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
