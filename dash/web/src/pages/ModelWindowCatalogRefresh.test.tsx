// @vitest-environment happy-dom
// Model-window catalog refresh control (RED).
//
// The control moved with the panel split: it now lives in the Maintenance panel
// of `DashboardTools` (the catalog drives the context windows findings are
// measured against, so refreshing it is maintenance), and it is deliberately
// absent from the read-only Status panel. These cases mount the header tools
// and open the panel the control belongs in, so the wiring is pinned rather
// than the component in isolation.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, fireEvent, cleanup, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { DashboardTools } from '../components/maintenance/DashboardTools.js'
import type { KyberCoverage } from '../lib/kyberApi.js'

const COVERAGE: KyberCoverage = {
  refresh: {
    lastSuccessAt: '2026-10-01T10:00:00.000Z',
    lastFailure: null,
    inProgress: null,
    historyWeeks: 2,
    coveredFrom: '2026-09-17T10:00:00.000Z',
    coveredThrough: '2026-10-01T10:00:00.000Z',
  },
  ingest: { status: 'known', lastReceivedAt: null, sources: [] },
  quarantineByReason: [],
  checkpoints: [],
}

/** Stubs the panel host's reads plus the catalog refresh; every call is local. */
function stubToolsFetch(refresh: (path: string) => Response | Promise<Response> | undefined) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo) => {
      const path = String(input)
      const answer = await refresh(path)
      if (answer !== undefined) return answer
      if (path.includes('/api/kyber/model-catalog/refresh')) {
        return {
          ok: true,
          json: async () => ({
            rowCount: 2,
            lastRefreshAt: '2026-10-01T09:00:00.000Z',
            vendorsUpdated: [],
            derivedRebuildCount: 0,
          }),
        } as Response
      }
      if (path.includes('/api/kyber/coverage')) {
        return { ok: true, json: async () => COVERAGE } as Response
      }
      if (path.includes('/api/kyber/harnesses')) {
        return { ok: true, json: async () => ({ harnesses: [] }) } as Response
      }
      if (path.includes('/api/kyber/jobs')) {
        return {
          ok: true,
          json: async () => ({
            refresh: { state: 'idle', lastSuccessAt: null, lastFailure: null, nextDueAt: null },
            paused: false,
            storeGeneration: 1,
            hostedElsewhere: false,
          }),
        } as Response
      }
      if (path.includes('/api/kyber/settings')) {
        return {
          ok: true,
          json: async () => ({
            folderImportScheduled: false,
            jobsPaused: false,
            refreshCadenceMinutes: 5,
            receiverHosted: true,
          }),
        } as Response
      }
      return { ok: false, status: 404, json: async () => ({}) } as Response
    }),
  )
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

/** Opens the panel the control belongs in, and scopes queries to it. */
async function openMaintenance(): Promise<HTMLElement> {
  renderTools()
  fireEvent.click(await screen.findByTestId('dashboard-tools-maintenance-button'))
  const panel = await screen.findByTestId('dashboard-tools-panel')
  await within(panel).findByTestId('model-catalog-refresh-button')
  return panel
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('Refresh model windows control', () => {
  it('renders a Refresh model windows button in the Maintenance panel', async () => {
    stubToolsFetch(() => undefined)
    const panel = await openMaintenance()
    expect(within(panel).getByTestId('model-catalog-refresh-button').textContent).toBe(
      'Refresh model windows',
    )
  })

  it('is absent from the read-only Status panel', async () => {
    stubToolsFetch(() => undefined)
    renderTools()
    fireEvent.click(await screen.findByTestId('dashboard-tools-status-button'))
    const panel = await screen.findByTestId('dashboard-tools-panel')
    await within(panel).findByTestId('coverage-ingest-panel')
    expect(within(panel).queryByTestId('model-catalog-refresh-button')).toBeNull()
  })

  it('disables the button while refresh is in flight', async () => {
    let resolveRefresh: (value: Response) => void = () => {}
    const refreshPromise = new Promise<Response>((resolve) => {
      resolveRefresh = resolve
    })
    stubToolsFetch((path) =>
      path.includes('/api/kyber/model-catalog/refresh') ? refreshPromise : undefined,
    )

    const panel = await openMaintenance()
    const button = within(panel).getByTestId('model-catalog-refresh-button')
    expect(button).not.toBeDisabled()
    fireEvent.click(button)
    expect(button).toBeDisabled()

    resolveRefresh({
      ok: true,
      json: async () => ({
        rowCount: 3,
        lastRefreshAt: '2026-10-01T11:00:00.000Z',
        vendorsUpdated: ['synth-vendor-a'],
        derivedRebuildCount: 1,
      }),
    } as Response)

    await waitFor(() => expect(button).not.toBeDisabled())
  })

  it('shows bounded success state without remote response bodies', async () => {
    stubToolsFetch((path) =>
      path.includes('/api/kyber/model-catalog/refresh')
        ? ({
            ok: true,
            json: async () => ({
              rowCount: 4,
              lastRefreshAt: '2026-10-01T12:00:00.000Z',
              vendorsUpdated: ['synth-vendor-a', 'synth-vendor-b'],
              derivedRebuildCount: 1,
            }),
          } as Response)
        : undefined,
    )

    const panel = await openMaintenance()
    fireEvent.click(within(panel).getByTestId('model-catalog-refresh-button'))

    const status = await within(panel).findByTestId('model-catalog-refresh-status')
    expect(status.textContent).toMatch(/4 rows/i)
    expect(status.textContent).toMatch(/2026-10-01T12:00:00.000Z/)
    expect(status.textContent?.includes('<html')).toBe(false)
    expect(status.textContent?.includes('vendor-b-invalid')).toBe(false)
  })

  it('names failed vendors on partial failure without rendering remote bodies', async () => {
    stubToolsFetch((path) =>
      path.includes('/api/kyber/model-catalog/refresh')
        ? ({
            ok: true,
            json: async () => ({
              rowCount: 3,
              lastRefreshAt: '2026-10-01T12:30:00.000Z',
              vendorsUpdated: ['synth-vendor-a'],
              vendorsFailed: [{ vendor: 'synth-vendor-b', error: 'invalid context window' }],
              derivedRebuildCount: 1,
            }),
          } as Response)
        : undefined,
    )

    const panel = await openMaintenance()
    fireEvent.click(within(panel).getByTestId('model-catalog-refresh-button'))

    const status = await within(panel).findByTestId('model-catalog-refresh-status')
    expect(status.textContent).toMatch(/synth-vendor-b/i)
    expect(status.textContent).toMatch(/partial/i)
    // The vendor's error message may carry a remote body; only the vendor
    // name is shown.
    expect(status.textContent?.includes('invalid context window')).toBe(false)
  })
})