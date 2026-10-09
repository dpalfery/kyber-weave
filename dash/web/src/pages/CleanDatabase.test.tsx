// @vitest-environment happy-dom
// Issue #312 — Clean database control (RED): dialog opens from the coverage
// panel, shows scope and the fixed disclosures, confirms with the right body,
// disables while in flight, and reports busy/failure states without rendering
// remote response bodies.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderToStaticMarkup } from 'react-dom/server'
import type * as React from 'react'

import { CleanDatabaseControl } from '../components/maintenance/CleanDatabaseControl.js'
import type { KyberHarnessSummary } from '../lib/kyberApi.js'

const HARNESSES: KyberHarnessSummary[] = [
  { harness: 'pi', name: 'Pi', sampleCount: 12 },
  { harness: 'cursor', name: 'Cursor', sampleCount: 3 },
] as KyberHarnessSummary[]

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

describe('Issue #312: Clean database control', () => {
  it('renders a Clean database button', () => {
    const html = renderToStaticMarkup(
      <QueryClientProvider client={new QueryClient()}>
        <CleanDatabaseControl harnesses={HARNESSES} />
      </QueryClientProvider>,
    )
    expect(html).toContain('Clean database')
    expect(html).toContain('data-testid="clean-database-button"')
  })

  it('opens a dialog with scope and the fixed disclosures', async () => {
    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)

    fireEvent.click(await screen.findByTestId('clean-database-button'))

    const dialog = await screen.findByTestId('clean-database-dialog')
    expect(dialog.getAttribute('role')).toBe('dialog')
    expect(dialog.textContent).toMatch(/cannot be undone/i)
    expect(dialog.textContent).toMatch(/no backup/i)
    expect(dialog.textContent).toMatch(/OTLP/i)
    expect(await screen.findByTestId('clean-database-harness-pi')).toBeDefined()
    expect(await screen.findByTestId('clean-database-wipe-all')).toBeDefined()
    // Nothing selectable yet: confirm stays disabled until a scope is chosen.
    expect(await screen.findByTestId('clean-database-confirm')).toBeDisabled()
  })

  it('confirms a harness wipe with the right body and reports counts', async () => {
    const posted: unknown[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        posted.push(true)
        return {
          ok: true,
          json: async () => ({
            harnesses: ['pi'],
            wipe: { harnesses: ['pi'], records: 3, provenance: 1, checkpoints: 1 },
            reingested: true,
            historyWeeks: 1,
          }),
        } as Response
      }),
    )

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-harness-pi'))

    const confirm = await screen.findByTestId('clean-database-confirm')
    expect(confirm).not.toBeDisabled()
    fireEvent.click(confirm)

    await waitFor(() => expect(posted).toHaveLength(1))
    const status = await screen.findByTestId('clean-database-status')
    expect(status.textContent).toMatch(/3 records wiped/)
    expect(status.textContent).toMatch(/pi/)
  })

  it('disables confirm while the clean is in flight', async () => {
    let resolveClean: (value: Response) => void = () => {}
    const cleanPromise = new Promise<Response>((resolve) => {
      resolveClean = resolve
    })
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => cleanPromise),
    )

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-wipe-all'))
    fireEvent.click(await screen.findByTestId('clean-database-confirm'))

    await waitFor(() => expect(screen.getByTestId('clean-database-confirm')).toBeDisabled())

    resolveClean({
      ok: true,
      json: async () => ({
        harnesses: ['*'],
        wipe: { harnesses: ['*'], records: 0, provenance: 0, checkpoints: 0 },
        reingested: true,
        historyWeeks: 1,
      }),
    } as Response)
    await waitFor(() => expect(screen.queryByTestId('clean-database-dialog')).toBeNull())
  })

  it('reports a busy store without remote bodies', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({ ok: false, status: 409, json: async () => ({ error: 'held by pid 4242' }) }) as Response),
    )

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-wipe-all'))
    fireEvent.click(await screen.findByTestId('clean-database-confirm'))

    const status = await screen.findByTestId('clean-database-status')
    expect(status.textContent).toMatch(/already running/i)
    expect(status.textContent).not.toContain('4242')
  })

  it('reports failure without remote bodies', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({ ok: false, status: 500, json: async () => ({ error: 'Clean failed: boom' }) }) as Response),
    )

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-wipe-all'))
    fireEvent.click(await screen.findByTestId('clean-database-confirm'))

    const status = await screen.findByTestId('clean-database-status')
    expect(status.textContent).toMatch(/failed/i)
    expect(status.textContent).not.toContain('boom')
  })
})
