// @vitest-environment happy-dom
// Quarantine detail renders the stored `rawPayload` contract
// (`GET /api/kyber/quarantine/:spanId` → `{ rawPayload, evidenceAvailable }`).
// Regression: the panel only read `payload`/`attributes`, so a row with a
// stored `rawPayload` wrongly reported no payload. Synthetic data only.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, fireEvent, cleanup, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { QuarantineView } from './QuarantineView.js'

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('quarantine stored evidence (rawPayload contract)', () => {
  it('renders the complete stored rawPayload, including nested values', async () => {
    const stored = {
      spanId: 'synthetic-span-1',
      name: 'synthetic.operation',
      attributes: {
        'http.method': 'GET',
        nested: { level1: { level2: 'deep-value-42' } },
      },
      events: [{ name: 'synthetic-event', at: '2026-01-01T00:00:00Z' }],
    }
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({
        ok: true,
        status: 200,
        json: async () => ({
          span_id: 'synthetic-span-1',
          reason: 'unmapped-namespace',
          namespaces: ['synthetic.ns'],
          rawPayload: stored,
          evidenceAvailable: true,
        }),
      }) as Response),
    )
    renderDom(
      <QueryClientProvider client={queryClient()}>
        <QuarantineView
          entries={[{ spanId: 'synthetic-span-1', namespaces: ['synthetic.ns'], reason: 'unmapped-namespace', source: 'opencode' }]}
        />
      </QueryClientProvider>,
    )
    fireEvent.click(await screen.findByTestId('quarantine-row-button'))
    const payload = await screen.findByTestId('quarantine-detail-payload')
    expect(payload.textContent).toContain('synthetic.operation')
    expect(payload.textContent).toContain('deep-value-42')
    expect(payload.textContent).toContain('synthetic-event')
    expect(screen.queryByTestId('quarantine-detail-no-payload')).toBeNull()
  })

  it('states unavailable when the row has no stored evidence', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({
        ok: true,
        status: 200,
        json: async () => ({
          span_id: 'synthetic-span-old',
          reason: 'unmapped-namespace',
          namespaces: ['synthetic.ns'],
          rawPayload: null,
          evidenceAvailable: false,
        }),
      }) as Response),
    )
    renderDom(
      <QueryClientProvider client={queryClient()}>
        <QuarantineView
          entries={[{ spanId: 'synthetic-span-old', namespaces: ['synthetic.ns'], reason: 'unmapped-namespace', source: 'opencode' }]}
        />
      </QueryClientProvider>,
    )
    fireEvent.click(await screen.findByTestId('quarantine-row-button'))
    const detail = await screen.findByTestId('quarantine-detail')
    expect(await within(detail).findByTestId('quarantine-detail-no-payload')).toBeDefined()
    expect(screen.queryByTestId('quarantine-detail-payload')).toBeNull()
  })
})
