import { useState, useMemo, useEffect } from 'react'
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
  // Offset paging (issue #191): Load more advances `offset` and appends the
  // next page instead of refetching a growing prefix.
  const [offset, setOffset] = useState(0)
  const [appended, setAppended] = useState<{ offset: number; rows: KyberFinding[] }[]>([])

  const resetBrowse = (update: () => void) => {
    update()
    setOffset(0)
    setAppended([])
  }

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

  // Headline findings across the entire workspace (top-5 card below). The
  // query fetches a full page; the card renders five via `maxItems`.
  const { data: headlineData, isLoading: loadingFindings } = useQuery({
    queryKey: ['kyber-findings-workspace'],
    queryFn: () => fetchFindings({ limit: FINDINGS_PAGE_SIZE }),
    initialData: initialFindings,
  })

  // Full workspace browser (issue #191): server-side detector/harness
  // filters with per-detector counts over the narrowed set, paged by
  // offset. Counts ignore paging, so chips stay comparable across pages.
  const filtersActive = detectorFilter !== undefined || harnessFilter !== undefined
  const { data: pageData, isLoading: loadingBrowser } = useQuery({
    queryKey: ['kyber-findings-browser', detectorFilter ?? '', harnessFilter ?? '', offset],
    queryFn: () =>
      fetchFindings({
        ...(detectorFilter !== undefined ? { detector: detectorFilter } : {}),
        ...(harnessFilter !== undefined ? { harness: harnessFilter } : {}),
        limit: FINDINGS_PAGE_SIZE,
        offset,
      }),
    initialData: offset === 0 && !filtersActive ? initialFindings : undefined,
  })

  // Accumulate pages past the first as they arrive; the guard keeps a
  // refetch from appending the same page twice.
  useEffect(() => {
    if (offset === 0 || !pageData) return
    setAppended((prev) =>
      prev.some((page) => page.offset === offset) ? prev : [...prev, { offset, rows: pageData.findings }],
    )
  }, [pageData, offset])

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
  // First page renders straight from the query; later pages accumulate above
  // (effects do not run in static render, where only the first page exists).
  const browserFindings: KyberFinding[] = useMemo(() => {
    const first = offset === 0 ? (pageData?.findings ?? []) : []
    const extra = [...appended].sort((a, b) => a.offset - b.offset).flatMap((page) => page.rows)
    const current =
      offset > 0 && pageData && !appended.some((page) => page.offset === offset) ? pageData.findings : []
    return [...first, ...extra, ...current]
  }, [pageData, offset, appended])
  const browserTotal = pageData?.total ?? 0
  const detectorCounts = pageData?.detectorCounts ?? {}
  const unknownWindowSessions = pageData?.unknownWindowSessions ?? 0
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
              onChange={(event) =>
                resetBrowse(() =>
                  setHarnessFilter(event.target.value === '' ? undefined : event.target.value),
                )
              }
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
                onClick={() =>
                  resetBrowse(() => setDetectorFilter(active ? undefined : detector))
                }
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
                onClick={() => setOffset((n) => n + FINDINGS_PAGE_SIZE)}
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
