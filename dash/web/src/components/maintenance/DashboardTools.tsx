// Header tools: the right-header button group and the two panels it opens.
//
// Context Doctor used to render the ingest coverage readout, the maintenance
// controls and the scorecard matrix on one page, which left the operator
// scrolling past three panels to reach the findings. The two operator-facing
// panels are split out here; the matrix was dropped from the page.
//
//   Status      the ingest coverage readout. Read-only. It issues no mutation,
//               so a surface labelled "Status" cannot wipe anything.
//   Maintenance the maintenance panel, the model-window catalog refresh and
//               the clean control — the operator actions, with their scope and
//               confirmation, together.
//
// Only one panel is open at a time: two full-height panels stacked over the
// content behind them is how a header control becomes unusable.
//
// This is a DISPLAY layer. Every fact on screen comes from
// `GET /api/kyber/coverage` and `GET /api/kyber/harnesses`, and the reads
// happen in the panel host — not in Context Doctor — so a coverage failure
// cannot take the clean control with it. A failed read renders a bounded
// error and a retry, never a default.

import { useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'

import { Skeleton } from '../ui/skeleton.js'
import { fetchCoverage, fetchHarnesses } from '../../lib/kyberApi.js'
import { CleanDatabaseControl } from './CleanDatabaseControl.js'
import { CoverageIngestPanel } from './CoverageIngestPanel.js'
import { MaintenancePanel } from './MaintenancePanel.js'
import { ModelCatalogRefreshControl } from './ModelCatalogRefreshControl.js'
import { SlideInPanel } from './SlideInPanel.js'

/** The panels this host opens. `null` is "neither". */
type PanelId = 'status' | 'maintenance'

const PANEL_TITLES: Record<PanelId, string> = {
  status: 'Status',
  maintenance: 'Maintenance',
}

export function DashboardTools() {
  const [open, setOpen] = useState<PanelId | null>(null)
  // The control focus returns to on close; see `SlideInPanel`.
  const trigger = useRef<HTMLElement | null>(null)

  function show(id: PanelId, from: HTMLElement) {
    trigger.current = from
    setOpen(id)
  }

  return (
    <div className="flex flex-wrap items-center gap-density-cluster" data-testid="dashboard-tools">
      <button
        type="button"
        data-testid="dashboard-tools-status-button"
        aria-expanded={open === 'status'}
        aria-haspopup="dialog"
        className="rounded-md border border-border px-chrome py-density-hair text-density-xs text-foreground hover:bg-interactive-secondary"
        onClick={(event) => show('status', event.currentTarget)}
      >
        Status
      </button>
      <button
        type="button"
        data-testid="dashboard-tools-maintenance-button"
        aria-expanded={open === 'maintenance'}
        aria-haspopup="dialog"
        className="rounded-md border border-border px-chrome py-density-hair text-density-xs text-foreground hover:bg-interactive-secondary"
        onClick={(event) => show('maintenance', event.currentTarget)}
      >
        Maintenance
      </button>
      {open !== null && (
        <SlideInPanel
          title={PANEL_TITLES[open]}
          onClose={() => setOpen(null)}
          restoreFocusTo={trigger.current}
        >
          {open === 'status' ? <StatusPanel /> : <MaintenanceContent />}
        </SlideInPanel>
      )}
    </div>
  )
}

/**
 * The Status panel's content: the coverage readout, with the loading and
 * failure states stated. A panel that renders an empty card while the read is
 * in flight claims the store has no sources; a panel that swallowed the
 * failure claims the store is healthy.
 */
function StatusPanel() {
  const { data, isLoading, error, refetch } = useQuery({
    queryKey: ['kyber-coverage'],
    queryFn: fetchCoverage,
  })

  if (isLoading) {
    return (
      <div data-testid="dashboard-tools-coverage-loading" className="flex flex-col gap-density-cluster">
        <Skeleton className="h-32 w-full" />
        <p className="text-density-xs text-muted-foreground">Reading ingest coverage…</p>
      </div>
    )
  }

  if (error !== null || data === undefined) {
    return (
      <div data-testid="dashboard-tools-coverage-error" role="alert" className="flex flex-col gap-density-cluster">
        <p className="text-density-xs text-muted-foreground">
          Ingest coverage could not be read. Nothing about the receiver is known from this page —
          this is not a report of an empty store.
        </p>
        <button type="button" data-testid="dashboard-tools-coverage-retry" onClick={() => void refetch()}>
          Retry
        </button>
      </div>
    )
  }

  return <CoverageIngestPanel coverage={data} />
}

/**
 * The Maintenance panel's content: what the host reports about its own jobs
 * and settings, the catalog refresh, and the clean control.
 *
 * <remarks>
 * The harness read is stated, not defaulted. Without it the per-harness scope
 * checkboxes are empty, and the operator still has the wipe-all path — but an
 * unreadable inventory must not present as an empty one, or the wipe reads as
 * the only thing there is to wipe. So the failure is a bounded message with a
 * retry, and every control stays mounted through it.
 * </remarks>
 */
function MaintenanceContent() {
  const {
    data: harnesses,
    isLoading: loadingHarnesses,
    isError: harnessesError,
    refetch: refetchHarnesses,
  } = useQuery({ queryKey: ['kyber-harnesses'], queryFn: fetchHarnesses })
  const known = harnesses ?? []

  return (
    <>
      <MaintenancePanel harnesses={known} />
      <section
        className="rounded-lg border border-border bg-card p-chrome"
        data-testid="dashboard-tools-catalog-and-clean"
        aria-label="Catalog and store maintenance"
      >
        <h3 className="text-density-xs font-semibold uppercase tracking-density text-heading">
          Catalog and store
        </h3>
        <p className="text-density-xs text-muted-foreground mt-density-hair leading-density">
          The model-window catalog supplies the context windows findings are measured against.
          Wiping the store deletes telemetry and cannot be undone; the dialog states the scope and
          asks for confirmation first.
        </p>
        {loadingHarnesses && (
          <p data-testid="dashboard-tools-harness-loading" className="text-density-xs text-muted-foreground mt-density-hair">
            Reading the harness inventory… the per-harness clean scope is not known yet.
          </p>
        )}
        {harnessesError && (
          <div data-testid="dashboard-tools-harness-error" role="alert" className="mt-density-hair flex flex-wrap items-center gap-density-cluster">
            <p className="text-density-xs text-muted-foreground">
              The harness inventory could not be read, so the per-harness clean scope is not
              available — that is not the same as a store holding no harnesses. Wipe all remains
              available.
            </p>
            <button type="button" data-testid="dashboard-tools-harness-retry" onClick={() => void refetchHarnesses()}>
              Retry
            </button>
          </div>
        )}
        <ModelCatalogRefreshControl />
        <CleanDatabaseControl harnesses={known} />
      </section>
    </>
  )
}