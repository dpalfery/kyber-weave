import { useState, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Skeleton } from '../components/ui/skeleton.js'
import {
  fetchCoverage,
  fetchHarnesses,
  fetchRuns,
  fetchFindings,
  type KyberCoverage,
  type KyberCoverageRefresh,
  type KyberHarnessSummary,
  type KyberFinding,
} from '../lib/kyberApi.js'
import {
  HierarchyBreadcrumb,
  FindingList,
  BaselineSelect,
  ScorecardMatrix,
} from '../components/analysis/index.js'
import type { ScorecardMatrixRow, ScorecardCoverageWindow } from '../components/analysis/ScorecardMatrix.js'

export interface ContextDoctorProps {
  initialHarnesses?: KyberHarnessSummary[]
  initialFindings?: KyberFinding[]
  /**
   * Coverage facts for the banner (T10) and ingest panel (T9). Accepts the
   * full `GET /api/kyber/coverage` payload or the refresh slice alone —
   * the T10 suite feeds the refresh slice; the panel renders only when the
   * full payload (with ingest activity) is present, so a refresh-only value
   * never invents receiver facts.
   */
  initialCoverage?: KyberCoverage | KyberCoverageRefresh
  onSelectHarness?: (harnessId: string) => void
  onSelectRun?: (runId: string, harnessId?: string) => void
  onSelectFinding?: (findingId: string) => void
  onSelectTurn?: (turnIndex: number, executionId?: string, runId?: string) => void
}

export type HarnessCatalogEntry = {
  /** Canonical KyberDash storage identifier, used verbatim in API filters. */
  harness: 'all' | 'claude-code' | 'copilot' | 'gemini'
  name: string
}

/**
 * Display names for harnesses the survey knows about. This is a labelling
 * table, NOT the list of tabs: the tab strip is built from the harnesses the
 * store actually holds (see `useHarnessTabs`), so a harness collected on this
 * machine is never hidden because it was missing from a hardcoded list.
 */
export const HARNESS_DISPLAY_NAMES: Readonly<Record<string, string>> = {
  all: 'All Harnesses',
  claude: 'Claude Code',
  'claude-code': 'Claude Code',
  copilot: 'GitHub Copilot',
  'copilot-cli': 'GitHub Copilot CLI',
  gemini: 'Gemini',
  antigravity: 'Antigravity',
  codex: 'Codex',
  cursor: 'Cursor',
  'cursor-agent': 'Cursor Agent',
  opencode: 'OpenCode',
  'kilo-code': 'Kilo Code',
  'roo-code': 'Roo Code',
  cline: 'Cline',
  windsurf: 'Windsurf',
  aider: 'Aider',
  droid: 'Droid',
  pi: 'Pi',
}

/** The harness's display name, falling back to its own id. */
export function harnessDisplayName(harness: string): string {
  return HARNESS_DISPLAY_NAMES[harness] ?? harness
}

export const HARNESS_CATALOG: readonly HarnessCatalogEntry[] = [
  { harness: 'all', name: 'All Harnesses' },
]

/** Returns whether a harness id represents collected, harness-specific data. */
function isObservedHarness(harness: string): boolean {
  return harness.length > 0 && harness !== 'all'
}

/**
 * Relative age of an ISO timestamp for the ingest panel's last-received line.
 * A pure formatter over a recorded timestamp — never a claim about whether
 * the receiver process is still running.
 */
export function formatCoverageAgo(iso: string, nowMs: number = Date.now()): string {
  const then = Date.parse(iso)
  if (!Number.isFinite(then)) return 'unknown age'
  const minutes = Math.floor(Math.max(0, nowMs - then) / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

/**
 * Ingest coverage panel (T9: issues #189/#198/#199). Reads the single
 * `GET /api/kyber/coverage` payload: per-source counts under T7 display
 * names (the stored id rides along only as a `data-raw-source` audit
 * attribute, never as the shown label), the last-received line, verbatim
 * quarantine reasons, and partial-checkpoint state. Empty log ⇒ unknown;
 * null window ⇒ unknown; zero-data keeps its verbatim reason. This panel
 * owns ingest activity only — the matrix banner and row grouping below
 * belong to T10.
 */
export function CoverageIngestPanel({ coverage }: { coverage: KyberCoverage }) {
  const { refresh, ingest, quarantineByReason, checkpoints } = coverage
  const partialUnits = checkpoints.filter((unit) => unit.lastStatus === 'partial')
  const windowKnown =
    refresh.historyWeeks !== null &&
    refresh.historyWeeks !== undefined &&
    Number.isFinite(refresh.historyWeeks)
  const receiverLine =
    ingest.status === 'unknown'
      ? 'no receiver activity recorded — receiver status is not observable from this page'
      : ingest.lastReceivedAt === null
        ? 'last received unknown (no receiver timestamp recorded)'
        : `last received ${ingest.lastReceivedAt} (${formatCoverageAgo(ingest.lastReceivedAt)})`

  return (
    <section
      className="rounded-lg border border-border bg-card p-chrome"
      data-testid="coverage-ingest-panel"
      aria-label="Ingest coverage"
    >
      <h3 className="text-density-xs font-semibold uppercase tracking-density text-heading">
        Ingest coverage
      </h3>
      <p className="text-density-xs text-muted-foreground mt-density-hair leading-density" data-testid="coverage-window">
        {windowKnown
          ? `Coverage window: last ${refresh.historyWeeks} weeks (${refresh.coveredFrom ?? 'range unknown'} → ${refresh.coveredThrough ?? 'range unknown'})`
          : 'coverage window unknown (recorded before window tracking)'}
      </p>
      <p className="text-density-xs text-muted-foreground mt-density-hair leading-density" data-testid="coverage-last-received">
        {receiverLine}
      </p>
      {ingest.status === 'known' && (
        <ul className="mt-density-cluster flex flex-col gap-density-hair">
          {ingest.sources.map((entry) => (
            <li
              key={entry.source}
              data-testid="coverage-source-row"
              data-raw-source={entry.source}
              title={`stored source: ${entry.source}`}
              className="flex flex-wrap items-baseline justify-between gap-density-cluster text-density-xs"
            >
              <span data-testid="coverage-source-label">{entry.display || entry.source}</span>
              <span className="text-muted-foreground">
                {entry.recordCount} records · {entry.kind}
              </span>
            </li>
          ))}
        </ul>
      )}
      {quarantineByReason.length > 0 && (
        <ul className="mt-density-cluster flex flex-col gap-density-hair">
          {quarantineByReason.map((entry) => (
            <li
              key={entry.reason}
              data-testid="coverage-quarantine-row"
              className="flex flex-wrap items-baseline justify-between gap-density-cluster text-density-xs"
            >
              <span>{entry.reason}</span>
              <span className="text-muted-foreground">{entry.count}</span>
            </li>
          ))}
        </ul>
      )}
      {partialUnits.length > 0 && (
        <div className="mt-density-cluster text-density-xs" data-testid="coverage-partial">
          <p>
            {partialUnits.length} source{partialUnits.length === 1 ? '' : 's'} partial (problems
            recorded, coverage incomplete)
          </p>
          <ul className="mt-density-hair flex flex-col gap-density-hair">
            {partialUnits.map((unit) => (
              <li
                key={`${unit.harnessId}:${unit.sourceKey}`}
                data-testid="coverage-partial-unit"
                className="text-muted-foreground"
              >
                {unit.harnessId}: {unit.recordCount} records
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  )
}

/**
 * Type guard for the coverage prop union: the full payload carries `ingest`,
 * the refresh slice does not. The ingest panel renders only for the full
 * payload; the matrix banner reads the refresh facts from either shape.
 */
export function isFullCoverage(
  coverage: KyberCoverage | KyberCoverageRefresh | undefined,
): coverage is KyberCoverage {
  return coverage !== undefined && 'ingest' in coverage
}

/**
 * Renders the Context Doctor landing page with workspace findings and a
 * per-harness diagnostic scorecard.
 */
export function ContextDoctor({
  initialHarnesses,
  initialFindings,
  initialCoverage,
  onSelectHarness,
  onSelectRun,
  onSelectFinding,
  onSelectTurn,
}: ContextDoctorProps) {
  const [baseline, setBaseline] = useState('none')

  // Query live harnesses
  const { data: harnessesData, isLoading: loadingHarnesses } = useQuery({
    queryKey: ['kyber-harnesses'],
    queryFn: fetchHarnesses,
    initialData: initialHarnesses,
  })

  // Harness rollups are deliberately optional. When their endpoint has not
  // been populated yet, the existing run projection still supplies real
  // harness identities for navigation. It supplies no metrics: those remain
  // not measurable until the rollup contract is available.
  const { data: runsData, isLoading: loadingRuns } = useQuery({
    queryKey: ['kyber-runs-navigation'],
    queryFn: () => fetchRuns(),
  })

  // Query live findings across the entire workspace
  const { data: findingsData, isLoading: loadingFindings } = useQuery({
    queryKey: ['kyber-findings-workspace'],
    queryFn: () => fetchFindings({ limit: 10 }),
    initialData: initialFindings,
  })

  // Coverage payload for the T9 ingest panel: window, receiver activity,
  // quarantine reasons, checkpoint statuses. Optional initial data keeps the
  // panel deterministic under test; otherwise the live endpoint supplies it.
  // A refresh-only initial value feeds the T10 banner below without
  // inventing ingest facts — the panel renders only for the full payload.
  const { data: coverageData } = useQuery({
    queryKey: ['kyber-coverage'],
    queryFn: fetchCoverage,
    initialData: isFullCoverage(initialCoverage) ? initialCoverage : undefined,
  })
  const refreshFacts: KyberCoverageRefresh | undefined =
    coverageData?.refresh ?? (isFullCoverage(initialCoverage) ? initialCoverage.refresh : initialCoverage)
  // T10's matrix banner owns the window wording (ScorecardMatrix `coverage`
  // prop); T9 only feeds it the refresh facts, which read identically from
  // the full payload's `refresh` slice or from a refresh-only value.
  // `undefined` (not fetched) renders no banner — absence is never invented.
  const matrixCoverage: ScorecardCoverageWindow | undefined =
    refreshFacts === undefined
      ? undefined
      : {
          historyWeeks: refreshFacts.historyWeeks,
          coveredFrom: refreshFacts.coveredFrom,
          coveredThrough: refreshFacts.coveredThrough,
        }

  const harnesses = useMemo((): ScorecardMatrixRow[] => {
    const fromRollups = (harnessesData ?? []).filter((h) => isObservedHarness(h.harness))
    if (fromRollups.length > 0) return fromRollups

    const observed = [...new Set((runsData ?? []).map((run) => run.harness).filter(isObservedHarness))]
    return observed.map((harness) => ({
      harness,
      name: harnessDisplayName(harness),
    }))
  }, [harnessesData, runsData])

  const findings = findingsData ?? []
  const loadingMatrix = loadingHarnesses || (!harnessesData?.length && loadingRuns)

  return (
    <div className="flex flex-col gap-density-stack" data-testid="page-context-doctor">
      {/* Top Header & Spine Navigation */}
      <div className="flex flex-wrap items-center justify-between gap-density-cluster">
        <HierarchyBreadcrumb />
        <BaselineSelect value={baseline} onChange={setBaseline} />
      </div>

      <div className="flex flex-wrap items-baseline justify-between gap-density-cluster">
        <div>
          <h2 className="font-display text-density-display font-bold tracking-density text-foreground">
            Context Doctor
          </h2>
          <p className="text-density-xs text-muted-foreground mt-density-hair leading-density">
            What's bloating or breaking your agents' context, per harness, and what to change.
          </p>
        </div>
      </div>

      {coverageData && <CoverageIngestPanel coverage={coverageData} />}

      {loadingFindings ? (
        <Skeleton className="h-44 w-full" />
      ) : (
        <FindingList
          findings={findings}
          maxItems={5}
          title="Highest-Leverage Workspace Findings"
          description="Ranked by estimated waste, outcome risk, and confidence. Deterministic evidence beats inferred claims."
          onSelectFinding={onSelectFinding}
          onSelectTurn={(turnIdx, execId) => onSelectTurn?.(turnIdx, execId)}
          onSelectExecution={(execId) => onSelectRun?.(execId)}
        />
      )}

      <ScorecardMatrix
        rows={harnesses}
        loading={loadingMatrix}
        coverage={matrixCoverage}
        onSelectHarness={onSelectHarness}
      />
    </div>
  )
}
