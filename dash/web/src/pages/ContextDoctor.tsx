import { useState, useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Skeleton } from '../components/ui/skeleton.js'
import {
  fetchHarnesses,
  fetchRuns,
  fetchFindings,
  type FindingsPage,
  type KyberHarnessSummary,
  type KyberFinding,
} from '../lib/kyberApi.js'
import {
  HierarchyBreadcrumb,
  FindingList,
  BaselineSelect,
  ScorecardMatrix,
} from '../components/analysis/index.js'
import type { ScorecardMatrixRow } from '../components/analysis/ScorecardMatrix.js'

export interface ContextDoctorProps {
  initialHarnesses?: KyberHarnessSummary[]
  initialFindings?: FindingsPage
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
 *
 * Folded front-ends have no entry (issue #182, Q5): `cursor-agent` is
 * `cursor` at the derived layer, so a label here would imply a tab that no
 * longer exists.
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

/** Page size for the workspace findings browser (issue #191). */
export const FINDINGS_PAGE_SIZE = 25

/**
 * Renders the Context Doctor landing page with workspace findings and a
 * per-harness diagnostic scorecard.
 */
export function ContextDoctor({
  initialHarnesses,
  initialFindings,
  onSelectHarness,
  onSelectRun,
  onSelectFinding,
  onSelectTurn,
}: ContextDoctorProps) {
  const [baseline, setBaseline] = useState('none')
  const [detectorFilter, setDetectorFilter] = useState<string | undefined>(undefined)
  const [harnessFilter, setHarnessFilter] = useState<string | undefined>(undefined)
  const [visibleLimit, setVisibleLimit] = useState(FINDINGS_PAGE_SIZE)

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

  // Headline findings across the entire workspace (top-5 card below).
  const { data: headlineData, isLoading: loadingFindings } = useQuery({
    queryKey: ['kyber-findings-workspace'],
    queryFn: () => fetchFindings({ limit: 10 }),
    initialData: initialFindings,
  })

  // Full workspace browser (issue #191): server-side detector/harness
  // filters with per-detector counts over the narrowed set. When no filter
  // is active the headline envelope already carries the whole answer, so no
  // second request fires.
  const browserActive =
    detectorFilter !== undefined || harnessFilter !== undefined || visibleLimit !== FINDINGS_PAGE_SIZE
  const { data: browserData, isLoading: loadingBrowser } = useQuery({
    queryKey: ['kyber-findings-browser', detectorFilter ?? '', harnessFilter ?? '', visibleLimit],
    queryFn: () =>
      fetchFindings({
        ...(detectorFilter !== undefined ? { detector: detectorFilter } : {}),
        ...(harnessFilter !== undefined ? { harness: harnessFilter } : {}),
        limit: visibleLimit,
      }),
    enabled: browserActive && initialFindings === undefined,
    initialData: browserActive ? undefined : initialFindings,
  })

  const harnesses = useMemo((): ScorecardMatrixRow[] => {
    const fromRollups = (harnessesData ?? []).filter((h) => isObservedHarness(h.harness))
    if (fromRollups.length > 0) return fromRollups

    const observed = [...new Set((runsData ?? []).map((run) => run.harness).filter(isObservedHarness))]
    return observed.map((harness) => ({
      harness,
      name: harnessDisplayName(harness),
    }))
  }, [harnessesData, runsData])

  const headline: KyberFinding[] = headlineData?.findings ?? []
  const browser: FindingsPage | undefined = browserActive ? browserData : headlineData
  const browserFindings: KyberFinding[] = browser?.findings ?? []
  const browserTotal = browser?.total ?? 0
  const detectorCounts = browser?.detectorCounts ?? {}
  const unknownWindowSessions = browser?.unknownWindowSessions ?? 0
  const loadingMatrix = loadingHarnesses || (!harnessesData?.length && loadingRuns)

  const detectorChips = useMemo(
    () =>
      Object.entries(detectorCounts)
        .filter(([, count]) => count > 0)
        .sort(([, a], [, b]) => b - a),
    [detectorCounts],
  )

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

      {loadingFindings ? (
        <Skeleton className="h-44 w-full" />
      ) : (
        <FindingList
          findings={headline}
          maxItems={5}
          title="Highest-Leverage Workspace Findings"
          description="Ranked by estimated waste, outcome risk, and confidence. Deterministic evidence beats inferred claims."
          onSelectFinding={onSelectFinding}
          onSelectTurn={(turnIdx, execId) => onSelectTurn?.(turnIdx, execId)}
          onSelectExecution={(execId) => onSelectRun?.(execId)}
        />
      )}

      {/* Full workspace findings browser (issue #191): every finding is one */}
      {/* filter or page away, with per-detector counts — the 49 */}
      {/* duplicate-tool-call findings are no longer invisible. */}
      <section aria-label="All workspace findings" data-testid="all-workspace-findings">
        <div className="flex flex-wrap items-baseline justify-between gap-density-cluster">
          <h3 className="font-display text-density-base font-semibold text-foreground">
            All Workspace Findings ({browserTotal})
          </h3>
          <label className="text-density-xs text-muted-foreground">
            Harness:{' '}
            <select
              data-testid="findings-harness-filter"
              value={harnessFilter ?? ''}
              onChange={(event) => {
                setHarnessFilter(event.target.value === '' ? undefined : event.target.value)
                setVisibleLimit(FINDINGS_PAGE_SIZE)
              }}
            >
              <option value="">All harnesses</option>
              {harnesses.map((row) => (
                <option key={row.harness} value={row.harness}>
                  {row.name ?? harnessDisplayName(row.harness)}
                </option>
              ))}
            </select>
          </label>
        </div>

        {unknownWindowSessions > 0 && (
          <p data-testid="unknown-window-banner" className="text-density-xs text-muted-foreground mt-density-hair">
            {unknownWindowSessions} session{unknownWindowSessions === 1 ? '' : 's'} with unknown context
            window — pressure unmeasurable, not zero. Findings are suppressed for these sessions until a
            source reports a window.
          </p>
        )}

        <div className="flex flex-wrap gap-density-cluster mt-density-hair" data-testid="findings-detector-chips">
          {detectorChips.map(([detector, count]) => {
            const active = detectorFilter === detector
            return (
              <button
                key={detector}
                type="button"
                data-testid={`findings-detector-chip-${detector}`}
                aria-pressed={active}
                title={active ? `Clear ${detector} filter` : `Show only ${detector} findings`}
                onClick={() => {
                  setDetectorFilter(active ? undefined : detector)
                  setVisibleLimit(FINDINGS_PAGE_SIZE)
                }}
              >
                {detector} ×{count}
              </button>
            )
          })}
        </div>

        {loadingBrowser ? (
          <Skeleton className="h-44 w-full" />
        ) : (
          <>
            <FindingList
              findings={browserFindings}
              title=""
              description="Server-ranked across the workspace; filters narrow the set before paging."
              onSelectFinding={onSelectFinding}
              onSelectTurn={(turnIdx, execId) => onSelectTurn?.(turnIdx, execId)}
              onSelectExecution={(execId) => onSelectRun?.(execId)}
            />
            {browserFindings.length < browserTotal && (
              <button
                type="button"
                data-testid="findings-load-more"
                onClick={() => setVisibleLimit((n) => n + FINDINGS_PAGE_SIZE)}
              >
                Load more ({browserFindings.length} of {browserTotal})
              </button>
            )}
          </>
        )}
      </section>

      <ScorecardMatrix
        rows={harnesses}
        loading={loadingMatrix}
        onSelectHarness={onSelectHarness}
      />
    </div>
  )
}
