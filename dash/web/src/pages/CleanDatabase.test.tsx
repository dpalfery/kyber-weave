// @vitest-environment happy-dom
// Issue #312 — Clean database control (RED): dialog opens from the coverage
// panel, shows scope and the fixed disclosures, confirms with the right body,
// disables while in flight, and reports busy/failure states without rendering
// remote response bodies.
//
// Issue #319 T13 (RED): re-ingest becomes an explicit operator choice. The
// confirm step carries an unchecked "Import folder history" checkbox and a
// weeks input (1..52, default 1). Unchecked ⇒ no `reingestWeeks` in the body at
// all (the server keeps its own default); checked ⇒ `reingestWeeks: <weeks>`.
// The disclosure may no longer promise an automatic re-ingest of "the last 7
// days" — an unconditional claim the operator never made.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { act } from 'react'
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

// ---------------------------------------------------------------------------
// Issue #319: recorded request bodies, so a scope or a window can be asserted.
// ---------------------------------------------------------------------------

type RecordedCall = { url: string; method: string; body: Record<string, unknown> | null }

/** The shape this stub can answer with. Hand-written so a reply's `json` body is
 *  free-form; `Partial<Response>` would type `json` as the real method. */
type Reply = { ok?: boolean; status?: number; json?: unknown }

function stubCleanFetch(respond: (call: RecordedCall) => Reply): RecordedCall[] {
  const calls: RecordedCall[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      const call: RecordedCall = {
        url: String(url),
        method: init?.method ?? 'GET',
        body: init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : null,
      }
      calls.push(call)
      const reply = respond(call)
      return {
        ok: reply.ok ?? true,
        status: reply.status ?? 200,
        json: async () => reply.json ?? {},
      } as Response
    }),
  )
  return calls
}

describe('Issue #319: clean re-ingest is an explicit choice', () => {
  it('posts wipe-all with no reingestWeeks when the checkbox is left unchecked', async () => {
    const calls = stubCleanFetch(() => ({
      json: {
        harnesses: ['*'],
        wipe: { harnesses: ['*'], records: 0, provenance: 0, checkpoints: 0 },
        reingested: false,
        historyWeeks: null,
      },
    }))

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-wipe-all'))

    // The box ships unchecked and the weeks field still reads 1 — the default
    // only becomes a request when the operator opts in.
    const box = (await screen.findByTestId('clean-database-import-history')) as HTMLInputElement
    expect(box.checked).toBe(false)
    const weeks = (await screen.findByTestId('clean-database-import-weeks')) as HTMLInputElement
    expect(weeks.value).toBe('1')

    fireEvent.click(await screen.findByTestId('clean-database-confirm'))
    await waitFor(() => expect(calls).toHaveLength(1))
    expect(calls[0]!.body).toEqual({ all: true, confirm: true })
    expect(calls[0]!.body).not.toHaveProperty('reingestWeeks')
  })

  it('posts a per-harness multi-select scope with no reingestWeeks', async () => {
    const calls = stubCleanFetch(() => ({
      json: {
        harnesses: ['pi', 'cursor'],
        wipe: { harnesses: ['pi', 'cursor'], records: 5, provenance: 1, checkpoints: 1 },
        reingested: false,
        historyWeeks: null,
      },
    }))

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-harness-pi'))
    fireEvent.click(await screen.findByTestId('clean-database-harness-cursor'))
    fireEvent.click(await screen.findByTestId('clean-database-confirm'))

    await waitFor(() => expect(calls).toHaveLength(1))
    expect(calls[0]!.url).toContain('/api/kyber/clean')
    expect(calls[0]!.method).toBe('POST')
    expect(calls[0]!.body).toEqual({ harnesses: ['pi', 'cursor'], confirm: true })
  })

  it('sends reingestWeeks when the checkbox is checked', async () => {
    const calls = stubCleanFetch(() => ({
      json: {
        harnesses: ['*'],
        wipe: { harnesses: ['*'], records: 1, provenance: 0, checkpoints: 0 },
        reingested: true,
        historyWeeks: 1,
      },
    }))

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-wipe-all'))
    fireEvent.click(await screen.findByTestId('clean-database-import-history'))
    fireEvent.click(await screen.findByTestId('clean-database-confirm'))

    await waitFor(() => expect(calls).toHaveLength(1))
    expect(calls[0]!.body).toEqual({ all: true, reingestWeeks: 1, confirm: true })
  })

  it('sends the operator-entered weeks with the clean', async () => {
    const calls = stubCleanFetch(() => ({
      json: {
        harnesses: ['*'],
        wipe: { harnesses: ['*'], records: 0, provenance: 0, checkpoints: 0 },
        reingested: true,
        historyWeeks: 4,
      },
    }))

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-wipe-all'))
    fireEvent.click(await screen.findByTestId('clean-database-import-history'))
    fireEvent.change(await screen.findByTestId('clean-database-import-weeks'), {
      target: { value: '4' },
    })
    fireEvent.click(await screen.findByTestId('clean-database-confirm'))

    await waitFor(() => expect(calls).toHaveLength(1))
    expect(calls[0]!.body).toMatchObject({ reingestWeeks: 4 })
  })

  // A window the server rejects (0, 53, fractional) must not leave the browser
  // to find out: the confirm is blocked, so no destructive request is sent.
  it.each([
    ['zero', '0'],
    ['above a year', '53'],
    ['fractional', '1.5'],
  ])('blocks confirm on a %s weeks value and sends nothing', async (_label, value) => {
    const calls = stubCleanFetch(() => ({ json: {} }))

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-wipe-all'))
    fireEvent.click(await screen.findByTestId('clean-database-import-history'))
    fireEvent.change(await screen.findByTestId('clean-database-import-weeks'), {
      target: { value },
    })

    const confirm = await screen.findByTestId('clean-database-confirm')
    expect(confirm).toBeDisabled()
    fireEvent.click(confirm)
    expect(calls).toHaveLength(0)
  })

  it('stops promising an automatic seven-day re-ingest in the disclosure', async () => {
    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))

    const dialog = await screen.findByTestId('clean-database-dialog')
    const text = dialog.textContent ?? ''
    // The irreversible facts stay.
    expect(text).toMatch(/cannot be undone/i)
    expect(text).toMatch(/no backup/i)
    expect(text).toMatch(/OTLP/i)
    // The unconditional re-ingest promise does not: it is now opt-in and
    // operator-chosen, so "automatic", a fixed 7-day window, or an implied
    // re-ingest of any fixed window would all misdescribe the behaviour.
    expect(text).not.toMatch(/automatic/i)
    expect(text).not.toMatch(/last 7 days/i)
    expect(text).not.toMatch(/re-?ingests? the last/i)
  })
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

  it('issues only one clean request on rapid double-confirm', async () => {
    let resolveClean: (value: Response) => void = () => {}
    const cleanPromise = new Promise<Response>((resolve) => {
      resolveClean = resolve
    })
    const fetchMock = vi.fn(async () => cleanPromise)
    vi.stubGlobal('fetch', fetchMock)

    renderWithQuery(<CleanDatabaseControl harnesses={HARNESSES} />)
    fireEvent.click(await screen.findByTestId('clean-database-button'))
    fireEvent.click(await screen.findByTestId('clean-database-wipe-all'))
    const confirm = await screen.findByTestId('clean-database-confirm')
    // Both clicks land before React re-renders, so the render-gated `disabled`
    // cannot stop the second one — only an in-handler guard can.
    await act(async () => {
      fireEvent.click(confirm)
      fireEvent.click(confirm)
    })

    expect(fetchMock).toHaveBeenCalledTimes(1)

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
