// @vitest-environment happy-dom
// P2.8 — Context Doctor model-window catalog refresh control (RED).

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderToStaticMarkup } from 'react-dom/server'
import type * as React from 'react'

import { ContextDoctor } from './ContextDoctor.js'
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

function renderWithQuery(ui: React.ReactElement) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
  return renderDom(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>)
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('P2.8: Context Doctor Refresh model windows control', () => {
  it('renders a Refresh model windows button in the coverage panel', () => {
    const html = renderToStaticMarkup(
      <QueryClientProvider client={new QueryClient()}>
        <ContextDoctor initialCoverage={COVERAGE} />
      </QueryClientProvider>,
    )
    expect(html).toContain('Refresh model windows')
    expect(html).toContain('data-testid="model-catalog-refresh-button"')
  })

  it('disables the button while refresh is in flight', async () => {
    let resolveRefresh: (value: Response) => void = () => {}
    const refreshPromise = new Promise<Response>((resolve) => {
      resolveRefresh = resolve
    })

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo) => {
        const path = String(input)
        if (path.includes('/api/kyber/model-catalog/refresh')) {
          return refreshPromise
        }
        if (path.includes('/api/kyber/model-catalog')) {
          return {
            ok: true,
            json: async () => ({
              rowCount: 2,
              lastRefreshAt: '2026-10-01T09:00:00.000Z',
              vendors: {},
            }),
          } as Response
        }
        if (path.includes('/api/kyber/coverage')) {
          return { ok: true, json: async () => COVERAGE } as Response
        }
        if (path.includes('/api/kyber/findings')) {
          return {
            ok: true,
            json: async () => ({ findings: [], total: 0, offset: 0, detectorCounts: {}, unknownWindowSessions: 0 }),
          } as Response
        }
        if (path.includes('/api/kyber/harnesses')) {
          return { ok: true, json: async () => ({ harnesses: [] }) } as Response
        }
        return { ok: false, status: 404, json: async () => ({}) } as Response
      }),
    )

    renderWithQuery(<ContextDoctor initialCoverage={COVERAGE} />)

    const button = await screen.findByTestId('model-catalog-refresh-button')
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
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo) => {
        const path = String(input)
        if (path.includes('/api/kyber/model-catalog/refresh')) {
          return {
            ok: true,
            json: async () => ({
              rowCount: 4,
              lastRefreshAt: '2026-10-01T12:00:00.000Z',
              vendorsUpdated: ['synth-vendor-a', 'synth-vendor-b'],
              derivedRebuildCount: 1,
            }),
          } as Response
        }
        if (path.includes('/api/kyber/model-catalog')) {
          return {
            ok: true,
            json: async () => ({
              rowCount: 2,
              lastRefreshAt: '2026-10-01T09:00:00.000Z',
              vendors: {},
            }),
          } as Response
        }
        if (path.includes('/api/kyber/coverage')) {
          return { ok: true, json: async () => COVERAGE } as Response
        }
        if (path.includes('/api/kyber/findings')) {
          return {
            ok: true,
            json: async () => ({ findings: [], total: 0, offset: 0, detectorCounts: {}, unknownWindowSessions: 0 }),
          } as Response
        }
        if (path.includes('/api/kyber/harnesses')) {
          return { ok: true, json: async () => ({ harnesses: [] }) } as Response
        }
        return { ok: false, status: 404, json: async () => ({}) } as Response
      }),
    )

    renderWithQuery(<ContextDoctor initialCoverage={COVERAGE} />)
    fireEvent.click(await screen.findByTestId('model-catalog-refresh-button'))

    const status = await screen.findByTestId('model-catalog-refresh-status')
    expect(status.textContent).toMatch(/4 rows/i)
    expect(status.textContent).toMatch(/2026-10-01T12:00:00.000Z/)
    expect(status.textContent?.includes('<html')).toBe(false)
    expect(status.textContent?.includes('vendor-b-invalid')).toBe(false)
  })

  it('names failed vendors on partial failure without rendering remote bodies', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo) => {
        const path = String(input)
        if (path.includes('/api/kyber/model-catalog/refresh')) {
          return {
            ok: true,
            json: async () => ({
              rowCount: 3,
              lastRefreshAt: '2026-10-01T12:30:00.000Z',
              vendorsUpdated: ['synth-vendor-a'],
              vendorsFailed: [{ vendor: 'synth-vendor-b', error: 'invalid context window' }],
              derivedRebuildCount: 1,
            }),
          } as Response
        }
        if (path.includes('/api/kyber/model-catalog')) {
          return {
            ok: true,
            json: async () => ({
              rowCount: 3,
              lastRefreshAt: '2026-10-01T09:00:00.000Z',
              vendors: {},
            }),
          } as Response
        }
        if (path.includes('/api/kyber/coverage')) {
          return { ok: true, json: async () => COVERAGE } as Response
        }
        if (path.includes('/api/kyber/findings')) {
          return {
            ok: true,
            json: async () => ({
              findings: [],
              total: 0,
              offset: 0,
              detectorCounts: {},
              unknownWindowSessions: 2,
            }),
          } as Response
        }
        if (path.includes('/api/kyber/harnesses')) {
          return { ok: true, json: async () => ({ harnesses: [] }) } as Response
        }
        return { ok: false, status: 404, json: async () => ({}) } as Response
      }),
    )

    renderWithQuery(<ContextDoctor initialCoverage={COVERAGE} />)
    fireEvent.click(await screen.findByTestId('model-catalog-refresh-button'))

    const status = await screen.findByTestId('model-catalog-refresh-status')
    expect(status.textContent).toMatch(/synth-vendor-b/i)
    expect(status.textContent).toMatch(/partial/i)
    expect(status.textContent?.includes('invalid context window')).toBe(false)

    const banner = await screen.findByTestId('unknown-window-banner')
    expect(banner.textContent?.toLowerCase()).toMatch(/reported/)
    expect(banner.textContent?.toLowerCase()).toMatch(/declared/)
    expect(banner.textContent?.toLowerCase()).toMatch(/catalog/)
  })
})
