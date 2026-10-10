// @vitest-environment happy-dom
// Header tools (RED): the right-header button group that owns the two
// right-side panels the dashboard lost when Context Doctor kept everything on
// one page.
//
// What is under test, and why each one matters:
//   - Status holds the ingest-coverage readout and is READ-ONLY. A status
//     surface that can wipe the database is a destructive control behind a
//     word like "Status".
//   - Maintenance holds the maintenance panel, the model-window catalog
//     refresh and the clean control — and stays reachable when the coverage
//     read fails. A wipe the operator cannot reach because an unrelated read
//     failed is an unreachable action, not an honest one.
//   - Only one panel slides in at a time, so the header cannot stack two
//     full-height panels over the content behind them.
//   - The panel is a dialog: accessible name, close button, Escape and
//     backdrop close, focus moved in and restored on close, and a width that
//     works on a phone.
//
// Every fetch is stubbed. No test reaches a real server or a real store.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, fireEvent, cleanup, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { DashboardTools } from './DashboardTools.js'
import type { KyberCoverage, KyberHarnessSummary } from '../../lib/kyberApi.js'

const COVERAGE: KyberCoverage = {
  refresh: {
    lastSuccessAt: '2026-10-01T10:00:00.000Z',
    lastFailure: null,
    inProgress: null,
    historyWeeks: 2,
    coveredFrom: '2026-09-17T10:00:00.000Z',
    coveredThrough: '2026-10-01T10:00:00.000Z',
  },
  ingest: { status: 'known', lastReceivedAt: '2026-10-01T09:55:00.000Z', sources: [] },
  quarantineByReason: [{ reason: 'unclaimed', count: 12 }],
  checkpoints: [],
}

const HARNESSES: KyberHarnessSummary[] = [
  { harness: 'pi', name: 'Pi', sampleCount: 12 },
  { harness: 'cursor', name: 'Cursor', sampleCount: 3 },
] as KyberHarnessSummary[]

const JOBS = {
  refresh: {
    state: 'idle',
    lastSuccessAt: '2026-10-09T10:00:00.000Z',
    lastFailure: null,
    nextDueAt: '2026-10-09T10:05:00.000Z',
  },
  paused: false,
  storeGeneration: 7,
  hostedElsewhere: false,
}

const SETTINGS = {
  folderImportScheduled: false,
  jobsPaused: false,
  refreshCadenceMinutes: 5,
  receiverHosted: true,
}

type Reply = { status?: number; json?: unknown; hold?: boolean }

function stubToolsFetch(
  overrides: (path: string) => Reply | undefined = () => undefined,
): { calls: string[]; release: () => void } {
  const calls: string[] = []
  let open: (() => void) | null = null
  const release = () => {
    open?.()
    open = null
  }
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string) => {
      const path = new URL(String(input), 'http://localhost').pathname
      calls.push(path)
      const override = overrides(path)
      if (override?.hold === true) {
        await new Promise<void>((resolve) => {
          open = resolve
        })
      }
      const reply = override ?? (path === '/api/kyber/coverage'
        ? { json: COVERAGE }
        : path === '/api/kyber/harnesses'
          ? { json: { harnesses: HARNESSES } }
          : path === '/api/kyber/jobs'
            ? { json: JOBS }
            : path === '/api/kyber/settings'
              ? { json: SETTINGS }
              : { json: {} })
      const status = reply.status ?? 200
      return {
        ok: status >= 200 && status < 300,
        status,
        json: async () => reply.json ?? {},
      } as Response
    }),
  )
  return { calls, release }
}

function renderTools() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
  return renderDom(
    <QueryClientProvider client={queryClient}>
      <DashboardTools />
    </QueryClientProvider>,
  )
}

async function openPanel(buttonTestId: 'dashboard-tools-status-button' | 'dashboard-tools-maintenance-button') {
  renderTools()
  const button = await screen.findByTestId(buttonTestId)
  fireEvent.click(button)
  const panel = await screen.findByTestId('dashboard-tools-panel')
  return { button, panel }
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('DashboardTools: the header button group', () => {
  it('offers Status and Maintenance with no panel open to begin with', async () => {
    stubToolsFetch()
    renderTools()

    expect(await screen.findByTestId('dashboard-tools-status-button')).toBeDefined()
    expect(screen.getByTestId('dashboard-tools-maintenance-button')).toBeDefined()
    expect(screen.queryByTestId('dashboard-tools-panel')).toBeNull()
  })

  it('opens one slide-in panel at a time', async () => {
    stubToolsFetch()
    renderTools()

    fireEvent.click(await screen.findByTestId('dashboard-tools-status-button'))
    const statusPanel = await screen.findByTestId('dashboard-tools-panel')
    expect(await within(statusPanel).findByTestId('coverage-ingest-panel')).toBeDefined()

    fireEvent.click(screen.getByTestId('dashboard-tools-maintenance-button'))
    // Exactly one panel exists: opening Maintenance closed Status rather than
    // stacking a second full-height panel over the content behind them.
    await waitFor(() =>
      expect(screen.getByTestId('dashboard-tools-panel').getAttribute('aria-label')).toBe('Maintenance'),
    )
    expect(screen.getAllByTestId('dashboard-tools-panel')).toHaveLength(1)
    expect(screen.queryByTestId('coverage-ingest-panel')).toBeNull()
  })
})

describe('DashboardTools: Status is a read-only ingest readout', () => {
  it('shows the coverage content it took over from Context Doctor', async () => {
    stubToolsFetch()
    const { panel } = await openPanel('dashboard-tools-status-button')

    const ingest = await within(panel).findByTestId('coverage-ingest-panel')
    expect(ingest.textContent).toMatch(/last received 2026-10-01T09:55:00.000Z/)
    expect(within(panel).getByTestId('coverage-window').textContent).toMatch(/last 2 weeks/)
    expect(within(panel).getByTestId('coverage-quarantine-row').textContent).toMatch(/unclaimed/)
  })

  it('offers no destructive or mutating control', async () => {
    stubToolsFetch()
    const { panel } = await openPanel('dashboard-tools-status-button')
    await within(panel).findByTestId('coverage-ingest-panel')

    expect(within(panel).queryByTestId('clean-database-button')).toBeNull()
    expect(within(panel).queryByTestId('model-catalog-refresh-button')).toBeNull()
    expect(within(panel).queryByTestId('maintenance-panel')).toBeNull()
  })

  it('says the coverage read is in flight rather than rendering an empty panel', async () => {
    stubToolsFetch((path) => (path === '/api/kyber/coverage' ? { hold: true, json: COVERAGE } : undefined))
    const { panel } = await openPanel('dashboard-tools-status-button')

    expect(within(panel).getByTestId('dashboard-tools-coverage-loading')).toBeDefined()
    expect(within(panel).queryByTestId('coverage-ingest-panel')).toBeNull()
  })

  it('reports a failed coverage read with a retry and never shows a zero', async () => {
    let attempts = 0
    stubToolsFetch((path) => {
      if (path !== '/api/kyber/coverage') return undefined
      attempts += 1
      return attempts === 1 ? { status: 503, json: { error: 'receiver offline' } } : { json: COVERAGE }
    })
    const { panel } = await openPanel('dashboard-tools-status-button')

    const error = await within(panel).findByTestId('dashboard-tools-coverage-error')
    expect(error.getAttribute('role')).toBe('alert')
    // Bounded: the remote body never renders, and the failure is not dressed
    // up as "no sources, nothing wrong".
    expect(error.textContent).not.toContain('receiver offline')
    expect(within(panel).queryByTestId('coverage-ingest-panel')).toBeNull()

    fireEvent.click(within(panel).getByTestId('dashboard-tools-coverage-retry'))
    expect(await within(panel).findByTestId('coverage-ingest-panel')).toBeDefined()
  })
})

describe('DashboardTools: Maintenance holds the operator controls', () => {
  it('carries the maintenance panel, the catalog refresh and the clean control', async () => {
    stubToolsFetch()
    const { panel } = await openPanel('dashboard-tools-maintenance-button')

    expect(await within(panel).findByTestId('maintenance-panel')).toBeDefined()
    expect(within(panel).getByTestId('model-catalog-refresh-button')).toBeDefined()
    const wipe = within(panel).getByTestId('clean-database-button')
    expect(wipe.textContent).toBe('Wipe database')
    // The maintenance panel reads the host's own state; nothing here invents it.
    expect(within(panel).getByTestId('maintenance-jobs-paused').textContent).toMatch(/not paused/i)
  })

  it('keeps the clean control reachable when the coverage read fails', async () => {
    stubToolsFetch((path) => (path === '/api/kyber/coverage' ? { status: 500, json: { error: 'boom' } } : undefined))
    const { panel } = await openPanel('dashboard-tools-maintenance-button')

    expect(await within(panel).findByTestId('clean-database-button')).toBeDefined()
    // The coverage failure is not the panel's to report, and it must not
    // displace the destructive control behind an error state.
    expect(within(panel).queryByTestId('dashboard-tools-coverage-error')).toBeNull()
  })

  it('keeps the clean control reachable when the harness read fails', async () => {
    // Without harnesses the scope checkboxes are empty — the operator still
    // gets the wipe-all path rather than a control that vanished.
    stubToolsFetch((path) => (path === '/api/kyber/harnesses' ? { status: 500, json: {} } : undefined))
    const { panel } = await openPanel('dashboard-tools-maintenance-button')

    expect(await within(panel).findByTestId('clean-database-button')).toBeDefined()
    fireEvent.click(within(panel).getByTestId('clean-database-button'))
    expect(await within(panel).findByTestId('clean-database-dialog')).toBeDefined()
    expect(within(panel).getByTestId('clean-database-wipe-all')).toBeDefined()
  })

  it('says the harness inventory could not be read, and offers a retry', async () => {
    let attempts = 0
    stubToolsFetch((path) => {
      if (path !== '/api/kyber/harnesses') return undefined
      attempts += 1
      return attempts === 1 ? { status: 503, json: { error: 'inventory boom' } } : undefined
    })
    const { panel } = await openPanel('dashboard-tools-maintenance-button')

    const error = await within(panel).findByTestId('dashboard-tools-harness-error')
    expect(error.getAttribute('role')).toBe('alert')
    // An unreadable inventory is NOT an empty inventory: the scope list would
    // otherwise read as "this store holds no harnesses", and the operator
    // could reach for wipe-all having been told nothing else is there.
    expect(error.textContent).not.toContain('inventory boom')
    expect(error.textContent).toMatch(/not/i)

    fireEvent.click(within(panel).getByTestId('dashboard-tools-harness-retry'))
    await waitFor(() => expect(screen.queryByTestId('dashboard-tools-harness-error')).toBeNull())
    expect(within(panel).getByTestId('clean-database-button')).toBeDefined()
  })

  it('keeps every control reachable while the harness inventory is unreadable', async () => {
    stubToolsFetch((path) => (path === '/api/kyber/harnesses' ? { status: 500, json: {} } : undefined))
    const { panel } = await openPanel('dashboard-tools-maintenance-button')

    await within(panel).findByTestId('dashboard-tools-harness-error')
    // The failure is scoped to the inventory, not to the panel: the operator
    // can still read the host's job state, refresh the catalog, and wipe.
    expect(within(panel).getByTestId('maintenance-panel')).toBeDefined()
    expect(within(panel).getByTestId('model-catalog-refresh-button')).toBeDefined()
    expect(within(panel).getByTestId('clean-database-button')).toBeDefined()
  })

  it('states that the harness inventory is still being read', async () => {
    stubToolsFetch((path) => (path === '/api/kyber/harnesses' ? { hold: true, json: { harnesses: [] } } : undefined))
    const { panel } = await openPanel('dashboard-tools-maintenance-button')

    expect(within(panel).getByTestId('dashboard-tools-harness-loading')).toBeDefined()
    // Loading is not an empty inventory either: the wipe is there, and so is
    // the fact that its scope list has not arrived.
    expect(within(panel).getByTestId('clean-database-button')).toBeDefined()
  })
})

describe('DashboardTools: the panel behaves like a dialog', () => {
  it('has an accessible name and a close button', async () => {
    stubToolsFetch()
    const { panel } = await openPanel('dashboard-tools-status-button')

    expect(panel.getAttribute('role')).toBe('dialog')
    expect(panel.getAttribute('aria-modal')).toBe('true')
    expect(panel.getAttribute('aria-label')).toBe('Status')
    expect(within(panel).getByTestId('dashboard-tools-panel-title').textContent).toBe('Status')
    expect(within(panel).getByTestId('dashboard-tools-panel-close').getAttribute('aria-label')).toMatch(
      /close/i,
    )
  })

  it('closes on the close button, on Escape and on the backdrop', async () => {
    stubToolsFetch()
    renderTools()

    fireEvent.click(await screen.findByTestId('dashboard-tools-status-button'))
    fireEvent.click(await screen.findByTestId('dashboard-tools-panel-close'))
    await waitFor(() => expect(screen.queryByTestId('dashboard-tools-panel')).toBeNull())

    fireEvent.click(screen.getByTestId('dashboard-tools-status-button'))
    fireEvent.keyDown(document, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByTestId('dashboard-tools-panel')).toBeNull())

    fireEvent.click(screen.getByTestId('dashboard-tools-status-button'))
    fireEvent.click(await screen.findByTestId('dashboard-tools-panel-backdrop'))
    await waitFor(() => expect(screen.queryByTestId('dashboard-tools-panel')).toBeNull())
  })

  it('moves focus into the panel and restores it to the button that opened it', async () => {
    stubToolsFetch()
    const { button, panel } = await openPanel('dashboard-tools-maintenance-button')

    await waitFor(() => expect(panel.contains(document.activeElement)).toBe(true))

    fireEvent.click(within(panel).getByTestId('dashboard-tools-panel-close'))
    await waitFor(() => expect(screen.queryByTestId('dashboard-tools-panel')).toBeNull())
    expect(document.activeElement).toBe(button)
  })

  it('stays usable at phone width and scrolls its own content', async () => {
    stubToolsFetch()
    const { panel } = await openPanel('dashboard-tools-maintenance-button')

    // A fixed-width panel is unreachable on a phone, and a panel taller than
    // the viewport without its own scroll traps the close button off-screen.
    expect(panel.className).toMatch(/w-full/)
    expect(panel.className).toMatch(/max-w-/)
    expect(panel.className).toMatch(/overflow-y-auto/)
    expect(panel.className).toMatch(/max-h-/)
  })
})