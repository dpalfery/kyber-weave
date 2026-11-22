// @vitest-environment happy-dom
// StoredFieldsPanel reads the catalog-driven stored-fields inspection route
// (`GET /api/kyber/session/:sessionKey/stored-fields`) and lays every
// reachable `table.column` out. Absent rows are "not reported" — never `0`
// or `""`. Synthetic data only: no fixture carries a real session payload.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, cleanup } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { StoredFieldsPanel } from './StoredFieldsPanel.js'

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const SESSION_KEY = 'copilot-session-1'

/** Synthetic catalog-driven view: populated tables plus an empty optional child. */
const cannedView = {
  sessionKey: SESSION_KEY,
  tables: [
    {
      tableName: 'session',
      columnNames: ['session_key', 'harness', 'turn_count'],
      rows: [
        {
          cells: [
            { columnName: 'session_key', columnType: 2, textValue: SESSION_KEY },
            { columnName: 'harness', columnType: 2, textValue: 'copilot' },
            // A measured zero is a measurement: it renders as `0`.
            { columnName: 'turn_count', columnType: 0, integerValue: 0 },
          ],
        },
      ],
      missingParentKeys: [],
    },
    {
      tableName: 'record',
      columnNames: ['span_id', 'input_tokens'],
      rows: [
        {
          cells: [
            { columnName: 'span_id', columnType: 2, textValue: 'span-copilot-1' },
            // A null cell in a present row was never measured: not reported.
            { columnName: 'input_tokens', columnType: 0, integerValue: null },
          ],
        },
      ],
      missingParentKeys: [],
    },
    {
      tableName: 'record_model',
      columnNames: ['span_id', 'model'],
      rows: [],
      missingParentKeys: ['span-copilot-1'],
    },
  ],
}

function stubFetch(body: unknown, status = 200, ok = status >= 200 && status < 300) {
  const fetchMock = vi.fn(async (..._args: unknown[]) => ({ ok, status, json: async () => body }) as unknown as Response)
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function mount(sessionKey: string | null | undefined = SESSION_KEY): ReturnType<typeof renderDom> {
  return renderDom(
    <QueryClientProvider client={queryClient()}>
      <StoredFieldsPanel sessionKey={sessionKey ?? undefined} />
    </QueryClientProvider>,
  )
}

describe('StoredFieldsPanel: wire contract', () => {
  it('fetches the stored-fields route for the session key, never /api/sessions', async () => {
    const fetchMock = stubFetch(cannedView)
    mount()
    await screen.findByTestId('stored-fields-panel')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0][0]).toBe(
      `/api/kyber/session/${encodeURIComponent(SESSION_KEY)}/stored-fields`,
    )
  })

  it('renders every reachable catalog column labeled table.column', async () => {
    stubFetch(cannedView)
    const { container } = mount()
    await screen.findByTestId('stored-fields-table-session')
    await screen.findByTestId('stored-fields-table-record_model')
    for (const label of [
      'session.session_key',
      'session.harness',
      'session.turn_count',
      'record.span_id',
      'record.input_tokens',
      'record_model.span_id',
      'record_model.model',
    ]) {
      expect(container.textContent).toContain(label)
    }
  })
})

describe('StoredFieldsPanel: absent rows are not reported, never 0 or ""', () => {
  it('renders a measured zero as 0, never as not reported', async () => {
    stubFetch(cannedView)
    mount()
    const zeroCell = await screen.findByTestId('stored-field-value-session.turn_count')
    expect(zeroCell.textContent).toBe('0')
  })

  it('renders a null cell in a present row as not reported', async () => {
    stubFetch(cannedView)
    mount()
    const cell = await screen.findByTestId('stored-field-value-record.input_tokens')
    expect(cell.textContent).toMatch(/not reported/i)
    expect(cell.textContent).not.toBe('0')
    expect(cell.textContent).not.toBe('')
  })

  it('renders an empty optional child table as not reported with its missing parent keys', async () => {
    stubFetch(cannedView)
    mount()
    const empty = await screen.findByTestId('stored-fields-empty-record_model')
    expect(empty.textContent).toMatch(/not reported/i)
    expect(empty.textContent).toContain('span-copilot-1')
    // An absent row must never read as a measured figure or an empty string.
    expect(empty.querySelectorAll('[data-testid^="stored-field-value-"]').length).toBe(0)
    expect(empty.textContent).not.toMatch(/(^|\s)0(\s|$)/)
  })
})

describe('StoredFieldsPanel: states', () => {
  it('shows a loading state while the view is in flight', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})))
    mount()
    const loading = screen.getByTestId('stored-fields-loading')
    expect(loading.textContent).toMatch(/loading/i)
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.getAttribute('aria-live')).toBe('polite')
  })

  it('states plainly when no session key is given', () => {
    const fetchMock = stubFetch(cannedView)
    mount(null)
    expect(screen.getByTestId('stored-fields-no-session').textContent).toMatch(/no session/i)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('states not found when the session was never projected', async () => {
    stubFetch({ error: 'Session not found' }, 404, false)
    mount()
    const notFound = await screen.findByTestId('stored-fields-not-found')
    expect(notFound.textContent).toMatch(/no stored fields/i)
    expect(notFound.getAttribute('role')).toBe('status')
    expect(notFound.getAttribute('aria-live')).toBe('polite')
  })

  it('states a load failure without inventing fields', async () => {
    stubFetch({ error: 'boom' }, 500, false)
    mount()
    const failure = await screen.findByTestId('stored-fields-error')
    expect(failure.textContent).toMatch(/could not be loaded/i)
    expect(failure.getAttribute('role')).toBe('status')
    expect(failure.getAttribute('aria-live')).toBe('polite')
  })
})
