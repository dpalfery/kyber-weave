// @vitest-environment happy-dom
// Status slideout collector emphasis: the large green/red indicator sits at
// the very top, above ingest coverage; loading/error/polling/green paths are
// all stated. Every fetch is stubbed; nothing reaches a real server.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, fireEvent, cleanup, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { DashboardTools } from './DashboardTools.js'
import type { KyberCoverage } from '../../lib/kyberApi.js'

function coverageWith(receiver: unknown): KyberCoverage {
  return {
    refresh: {
      lastSuccessAt: '2026-10-01T10:00:00.000Z',
      lastFailure: null,
      inProgress: null,
      historyWeeks: 2,
      coveredFrom: '2026-09-17T10:00:00.000Z',
      coveredThrough: '2026-10-01T10:00:00.000Z',
    },
    ingest: { status: 'known', lastReceivedAt: '2026-10-01T09:55:00.000Z', sources: [] },
    quarantineByReason: [],
    checkpoints: [],
    receiver: receiver as KyberCoverage['receiver'],
  }
}

const LIVE_RECEIVER = {
  status: 'hosted',
  listening: true,
  hostedByUs: true,
  paused: false,
  healthy: true,
  endpoint: 'http://127.0.0.1:4318',
  checkedAt: new Date(Date.now() - 5_000).toISOString(),
  lastAcceptedAt: new Date(Date.now() - 20_000).toISOString(),
  errors5m: { total: 0, last: null },
}

function stubCoverage(coverage: KyberCoverage, onCoverage?: () => void) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string) => {
      const path = new URL(String(input), 'http://localhost').pathname
      if (path === '/api/kyber/coverage') onCoverage?.()
      const json = path === '/api/kyber/coverage' ? coverage : path === '/api/kyber/harnesses' ? { harnesses: [] } : {}
      return { ok: true, status: 200, json: async () => json } as Response
    }),
  )
}

function renderTools() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return renderDom(
    <QueryClientProvider client={queryClient}>
      <DashboardTools />
    </QueryClientProvider>,
  )
}

async function openStatus() {
  renderTools()
  fireEvent.click(await screen.findByTestId('dashboard-tools-status-button'))
  return screen.findByTestId('dashboard-tools-panel')
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('Status slideout collector indicator', () => {
  it('renders a red indicator at the very top when receiver health is unavailable', async () => {
    const { receiver: _dropped, ...rest } = coverageWith(LIVE_RECEIVER)
    void _dropped
    stubCoverage(rest as KyberCoverage)
    const panel = await openStatus()

    const indicator = await within(panel).findByTestId('collector-indicator')
    expect(indicator.getAttribute('data-state')).toBe('down')
    expect(indicator.getAttribute('role')).toBe('status')
    // Named in text, not colour alone.
    expect(within(panel).getByTestId('collector-label').textContent).toMatch(/down|unavailable/i)
    // Above the ingest coverage.
    const panelHtml = panel.innerHTML
    expect(panelHtml.indexOf('receiver-health-panel')).toBeLessThan(panelHtml.indexOf('coverage-ingest-panel'))
  })

  it('renders green only for a healthy, listening, unpaused, error-free receiver', async () => {
    stubCoverage(coverageWith(LIVE_RECEIVER))
    const panel = await openStatus()

    const indicator = await within(panel).findByTestId('collector-indicator')
    expect(indicator.getAttribute('data-state')).toBe('live')
    expect(within(panel).getByTestId('collector-label').textContent).toMatch(/live/i)
    expect(within(panel).getByTestId('receiver-endpoint').textContent).toContain('127.0.0.1:4318')
    expect(within(panel).getByTestId('receiver-errors-5m').textContent).toBe('0')
  })

  it('renders red when paused even if the engine once said healthy', async () => {
    stubCoverage(coverageWith({ ...LIVE_RECEIVER, paused: true }))
    const panel = await openStatus()
    const indicator = await within(panel).findByTestId('collector-indicator')
    expect(indicator.getAttribute('data-state')).toBe('down')
    expect(within(panel).getByTestId('collector-label').textContent).toMatch(/paused|down/i)
  })

  it('shows endpoint, listener state, last accepted batch and latest error', async () => {
    stubCoverage(
      coverageWith({
        ...LIVE_RECEIVER,
        healthy: false,
        listening: false,
        errors5m: { total: 3, last: { at: '2026-10-10T11:59:00.000Z', code: 'OTLP_500', message: 'connection refused' } },
      }),
    )
    const panel = await openStatus()
    expect((await within(panel).findByTestId('receiver-listener')).textContent).toMatch(/not listening/i)
    expect((await within(panel).findByTestId('receiver-last-accepted')).textContent).not.toBe('')
    const last = await within(panel).findByTestId('receiver-last-error')
    expect(last.textContent).toContain('OTLP_500')
    expect(last.textContent).toContain('connection refused')
  })

  it('keeps historical ingest coverage separate below live health', async () => {
    stubCoverage(coverageWith(LIVE_RECEIVER))
    const panel = await openStatus()
    await within(panel).findByTestId('receiver-health-panel')
    await within(panel).findByTestId('coverage-ingest-panel')
    expect(within(panel).getByTestId('receiver-coverage-note').textContent).toMatch(/historical ingest coverage.*below/i)
  })

  it('re-reads coverage on a short cadence while the slideout is open', async () => {
    let calls = 0
    stubCoverage(coverageWith(LIVE_RECEIVER), () => {
      calls += 1
    })
    // Fake timers mock React Query's refetch interval, but they also freeze
    // testing-library's async `findBy*` waitFor loop — so this test uses only
    // synchronous `getBy*` queries and flushes promises via the fake clock.
    vi.useFakeTimers()
    try {
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
      renderDom(
        <QueryClientProvider client={queryClient}>
          <DashboardTools />
        </QueryClientProvider>,
      )
      fireEvent.click(screen.getByTestId('dashboard-tools-status-button'))
      await vi.advanceTimersByTimeAsync(0)
      screen.getByTestId('collector-indicator')
      const first = calls
      expect(first).toBeGreaterThanOrEqual(1)
      await vi.advanceTimersByTimeAsync(10_000)
      await vi.advanceTimersByTimeAsync(0)
      expect(calls).toBeGreaterThan(first)
    } finally {
      vi.useRealTimers()
    }
  })
})
