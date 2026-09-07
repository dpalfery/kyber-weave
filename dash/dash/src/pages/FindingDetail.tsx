import { useState, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { cn, fmtTokens } from '../lib/utils'
import { Card } from '../components/ui/card'
import { Skeleton } from '../components/ui/skeleton'
import {
  fetchFinding,
  type KyberFinding,
  type KyberEvidenceLink,
} from '../lib/kyberApi'
import {
  HierarchyBreadcrumb,
  FindingConfidenceBadge,
  getFindingRankScore,
  EvidenceTable,
  ConfidencePanel,
  RecommendationPanel,
} from '../components/kyber'
import { SessionInspectorDrawer } from '../components/SessionInspectorDrawer'

export interface FindingDetailProps {
  findingId?: string
  initialFinding?: KyberFinding
  initialSelectedLink?: KyberEvidenceLink | null
  initialDrawerOpen?: boolean
  onSelectAll?: () => void
  onSelectHarness?: (harness: string) => void
  onSelectRun?: (runId: string) => void
  onSelectExecution?: (executionId: string) => void
  onSelectTurn?: (turnIndex: number) => void
  onBack?: () => void
  className?: string
}

/**
 * Finding Detail Screen (Task G3).
 *
 * Renders four distinct regions so a developer can disagree with a finding:
 * 1. Diagnosis: Title, detector mechanism explanation, and waste summary.
 * 2. Confidence: Tier, measurement basis, and what would raise it (always uncollapsed).
 * 3. Recommendation: Decision D8 relocation/progressive disclosure actions, recoverable waste, and error bars.
 * 4. Evidence Table: Deep-linkable rows resolving to span/turn in SessionInspectorDrawer.
 *
 * Non-collapsible outcome-risk caveat protects against regression when outcomes are unmeasured or high-risk.
 */
export function FindingDetail({
  findingId,
  initialFinding,
  initialSelectedLink = null,
  initialDrawerOpen = false,
  onSelectAll,
  onSelectHarness,
  onSelectRun,
  onSelectExecution,
  onSelectTurn,
  onBack,
  className,
}: FindingDetailProps) {
  const [selectedLink, setSelectedLink] = useState<KyberEvidenceLink | null>(() => initialSelectedLink)
  const [drawerOpen, setDrawerOpen] = useState<boolean>(() => initialDrawerOpen)

  // Fetch finding data if findingId is provided and no initialFinding
  const { data: fetchedFinding, isLoading, isError } = useQuery({
    queryKey: ['kyber-finding', findingId],
    queryFn: () => (findingId ? fetchFinding(findingId) : Promise.reject(new Error('No findingId'))),
    enabled: Boolean(findingId && !initialFinding),
    initialData: initialFinding,
    retry: false,
  })

  const finding: KyberFinding | undefined = fetchedFinding ?? initialFinding

  // Deep-link trigger handler opening SessionInspectorDrawer pre-selected to target span/turn
  const handleOpenEvidence = (link: KyberEvidenceLink) => {
    setSelectedLink(link)
    setDrawerOpen(true)
    if (onSelectTurn && link.turnIndex !== undefined && link.turnIndex !== null) {
      onSelectTurn(link.turnIndex)
    }
  }

  // D6 rank score computation
  const rankScore = useMemo(() => {
    if (!finding) return 0
    return getFindingRankScore(finding)
  }, [finding])

  if (isLoading && !finding && findingId && !isError) {
    return (
      <div className="flex flex-col gap-4 p-4" data-testid="finding-detail-loading">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40 w-full" />
        <Skeleton className="h-56 w-full" />
        <Skeleton className="h-48 w-full" />
      </div>
    )
  }

  if (!finding) {
    return (
      <div className="p-8 text-center" data-testid="finding-not-found">
        <Card className="p-6 max-w-md mx-auto space-y-3">
          <h2 className="text-sm font-semibold text-foreground">Finding Not Found</h2>
          <p className="text-xs text-muted-foreground">
            The requested diagnostic finding could not be loaded or does not exist.
          </p>
          {onBack && (
            <button
              type="button"
              onClick={onBack}
              className="rounded bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 transition-opacity"
            >
              Go Back
            </button>
          )}
        </Card>
      </div>
    )
  }

  const targetSessionId = finding.sessionId || finding.runId || 'session'

  return (
    <div className={cn('flex flex-col gap-5 p-2 sm:p-4 max-w-6xl mx-auto', className)} data-testid="page-finding-detail">
      {/* Top Header & Breadcrumb */}
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border/60 pb-3">
        <HierarchyBreadcrumb
          harness={finding.harness}
          runId={finding.runId || finding.sessionId}
          executionId={finding.executionId}
          onSelectAll={onSelectAll}
          onSelectHarness={onSelectHarness}
          onSelectRun={onSelectRun}
          onSelectExecution={onSelectExecution}
          onSelectTurn={onSelectTurn}
        />

        {onBack && (
          <button
            type="button"
            onClick={onBack}
            data-testid="finding-detail-back-button"
            className="rounded border border-border bg-card px-2.5 py-1 text-xs font-medium text-muted-foreground hover:text-foreground hover:bg-interactive-secondary transition-colors inline-flex items-center gap-1.5"
          >
            <span aria-hidden="true">←</span>
            <span>Back</span>
          </button>
        )}
      </div>

      {/* Region 1: Diagnosis & Waste Summary */}
      <Card className="p-5 space-y-4" data-testid="diagnosis-panel">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="min-w-0 flex-1 space-y-2">
            <div className="flex items-center gap-2 flex-wrap">
              <FindingConfidenceBadge
                confidence={finding.confidence}
                measurementClass={finding.measurementClass}
              />
              <span className="rounded bg-interactive-secondary border border-border px-2 py-0.5 font-mono text-[10.5px] text-tertiary-foreground">
                {finding.detectorId}
              </span>
              <span
                className="rounded bg-interactive-secondary px-2 py-0.5 text-[10px] font-mono text-muted-foreground"
                title="Rank score per Decision D6 formula: waste × confidence × (1 - outcomeRiskDiscount)"
              >
                rank #{rankScore.toFixed(0)}
              </span>
              {finding.harness && (
                <span className="text-[11px] text-muted-foreground">
                  Harness: <strong className="text-foreground">{finding.harness}</strong>
                </span>
              )}
            </div>

            <h1
              className="font-display text-2xl font-bold tracking-tight text-foreground"
              data-testid="diagnosis-title"
            >
              {finding.title}
            </h1>
          </div>

          {/* Waste Summary Metric */}
          <div className="text-right shrink-0 rounded-lg border border-border bg-card/60 p-3">
            <div className="text-[10.5px] uppercase tracking-wider text-tertiary-foreground">
              Estimated Waste
            </div>
            <div
              className="font-display text-2xl font-bold text-amber-500 dark:text-amber-400 tabular-nums"
              data-testid="waste-summary"
            >
              {fmtTokens(finding.estimatedWasteTokens)}
            </div>
            {finding.errorBar && (
              <div
                className="text-[10px] font-mono text-tertiary-foreground tabular-nums mt-0.5"
                title="Confidence error bar bounds"
                data-testid="waste-summary-error-bar"
              >
                [{fmtTokens(finding.errorBar.lower)} – {fmtTokens(finding.errorBar.upper)}]
              </div>
            )}
          </div>
        </div>

        {/* Detector Mechanism Explanation */}
        <div className="border-t border-border/50 pt-3 space-y-1.5">
          <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">
            Detector Mechanism Explanation
          </h2>
          <p
            className="text-xs sm:text-sm text-muted-foreground leading-relaxed"
            data-testid="detector-mechanism"
          >
            {finding.mechanism}
          </p>
        </div>
      </Card>

      {/* Region: Outcome-Risk Caveat (Acceptance Criterion 5: Non-collapsible warning) */}
      <div
        className="rounded-lg border-2 border-amber-500/40 bg-amber-500/10 p-4 space-y-1.5"
        data-testid="outcome-risk-caveat"
      >
        <div className="flex items-center justify-between flex-wrap gap-2">
          <div className="flex items-center gap-2">
            <span className="text-base" role="img" aria-label="warning">⚠️</span>
            <h3 className="text-xs font-bold uppercase tracking-wider text-amber-900 dark:text-amber-200">
              Outcome-Risk Caveat (Decision D5)
            </h3>
          </div>
          <span className="rounded bg-amber-500/20 px-2 py-0.5 text-[10px] font-mono font-medium text-amber-800 dark:text-amber-300">
            Non-Collapsible Guard
          </span>
        </div>

        <p className="text-xs text-amber-900/90 dark:text-amber-200/90 leading-relaxed font-medium">
          {finding.outcomeRiskCaveat ||
            'The task outcome carries regression risk. Deferring or relocating context may alter model reasoning stability. Verify with task verification tests before permanent changes.'}
        </p>
      </div>

      {/* Region 3: Confidence Panel */}
      <ConfidencePanel
        confidence={finding.confidence}
        measurementClass={finding.measurementClass}
        confidenceBasis={finding.confidenceBasis}
        whatWouldRaiseIt={finding.whatWouldRaiseIt}
      />

      {/* Region 4: Recommendation Panel */}
      <RecommendationPanel
        recommendation={finding.recommendation}
        estimatedWasteTokens={finding.estimatedWasteTokens}
        errorBar={finding.errorBar}
      />

      {/* Region 2: Evidence Table */}
      <EvidenceTable
        evidenceLinks={finding.evidenceLinks}
        onSelectEvidence={handleOpenEvidence}
        onSelectTurn={(turnIdx) => {
          if (onSelectTurn) onSelectTurn(turnIdx)
        }}
      />

      {/* Slide-out SessionInspectorDrawer pre-selected to target span/turn */}
      {drawerOpen && (
        <SessionInspectorDrawer
          open={drawerOpen}
          onClose={() => setDrawerOpen(false)}
          title={`Turn #${selectedLink?.turnIndex ?? 0} Evidence Inspector`}
          subtitle={`Span: ${selectedLink?.spanId ?? '—'} · ${finding.title}`}
          rawContent={{
            sessionId: targetSessionId,
            turnIndex: selectedLink?.turnIndex ?? 0,
            spanId: selectedLink?.spanId,
            description: selectedLink?.description,
          }}
          contentRequest={{
            sessionId: targetSessionId,
            span: selectedLink?.spanId,
          }}
          inspectContext={true}
        >
          {selectedLink && (
            <div
              className="rounded border border-primary/30 bg-primary/10 p-3 text-xs text-foreground space-y-1"
              data-testid="evidence-drawer-target-banner"
            >
              <div className="flex items-center gap-1.5 font-semibold text-primary uppercase text-[10.5px]">
                <span aria-hidden="true">📍</span>
                <span>Target Telemetry Evidence Link</span>
              </div>
              <p className="text-muted-foreground">{selectedLink.description}</p>
              <div className="font-mono text-[11px] text-tertiary-foreground pt-1 flex items-center gap-3">
                <span>Turn: #{selectedLink.turnIndex}</span>
                {selectedLink.spanId && <span>Span: {selectedLink.spanId}</span>}
                {selectedLink.executionId && <span>Exec: {selectedLink.executionId}</span>}
              </div>
            </div>
          )}
        </SessionInspectorDrawer>
      )}
    </div>
  )
}
