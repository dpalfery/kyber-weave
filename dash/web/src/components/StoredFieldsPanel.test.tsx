// @vitest-environment happy-dom
// StoredFieldsPanel reads the catalog-driven stored-fields inventory
// (`GET /api/kyber/session/:sessionKey/stored-fields`) and lays every
// reachable `table.column` out. A table's rows load from
// `.../stored-fields/:table` only when the viewer opens that table, 25 at a
// time. Absent figures are "not reported" — never `0` or `""`. Synthetic
// data only: no fixture carries a real session payload.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, cleanup, within, fireEvent } from '@testing-library/react'
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
const INVENTORY_PATH = `/api/kyber/session/${encodeURIComponent(SESSION_KEY)}/stored-fields`
const rowsPath = (table: string, page: number) =>
  `/api/kyber/session/${encodeURIComponent(SESSION_KEY)}/stored-fields/${encodeURIComponent(table)}?page=${page}&limit=25`

/** The catalog-driven inventory the route serves: counts and columns, no rows. */
const INVENTORY = {
  sessionKey: SESSION_KEY,
  tables: [
    {
      tableName: 'session',
      columnNames: ['session_key', 'harness', 'turn_count'],
      rowCount: 2,
      missingParentKeys: [],
    },
    {
      tableName: 'record',
      columnNames: ['span_id', 'input_tokens'],
      rowCount: 30,
      missingParentKeys: [],
    },
    {
      tableName: 'record_model',
      columnNames: ['span_id', 'model'],
      rowCount: 0,
      missingParentKeys: ['span-copilot-1'],
    },
  ],
}

function cell(columnName: string, value: { text?: string; integer?: number }): unknown {
  return {
    columnName,
    columnType: typeof value.integer === 'number' ? 0 : 1,
    textValue: value.text ?? null,
    integerValue: value.integer ?? null,
    realValue: null,
  }
}

/** One page of one table's stored rows, as the rows route answers it. */
function rowsPage(table: 'session' | 'record', page: number): unknown {
  if (table === 'session') {
    return {
      tableName: 'session',
      columnNames: ['session_key', 'harness', 'turn_count'],
      totalRows: 2,
      page,
      limit: 25,
      offset: 0,
      rows:
        page > 1
          ? []
          : [
              {
                cells: [
                  cell('session_key', { text: SESSION_KEY }),
                  cell('harness', { text: 'copilot' }),
                  // A measured zero is a measurement: it renders as `0`.
                  cell('turn_count', { integer: 0 }),
                ],
              },
              {
                cells: [
                  cell('session_key', { text: `${SESSION_KEY}-2` }),
                  cell('harness', { text: 'copilot' }),
                  cell('turn_count', { integer: 3 }),
                ],
              },
            ],
    }
  }
  const offset = (page - 1) * 25
  const count = Math.min(25, 30 - offset)
  return {
    tableName: 'record',
    columnNames: ['span_id', 'input_tokens'],
    totalRows: 30,
    page,
    limit: 25,
    offset,
    rows: Array.from({ length: Math.max(0, count) }, (_, i) => ({
      cells: [
        cell('span_id', { text: `span-${offset + i + 1}` }),
        // A null cell in a present row was never measured: not reported.
        cell('input_tokens', {}),
      ],
    })),
  }
}

/** Routes each fetch by URL; a URL no route answers fails the test loudly. */
function stubRoutes(routes: Record<string, unknown>): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn((input: unknown) => {
    const url = String(input)
    const body = routes[url] ?? routes[url.split('?')[0]!]
    if (body === undefined) {
      return Promise.reject(new Error(`unexpected fetch in test: ${url}`))
    }
    return Promise.resolve({ ok: true, status: 200, json: async () => body } as unknown as Response)
  })
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
    const fetchMock = stubRoutes({ [INVENTORY_PATH]: INVENTORY })
    mount()
    await screen.findByTestId('stored-fields-panel')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0]![0]).toBe(INVENTORY_PATH)
  })

  it('fetches no rows on mount — a table’s rows wait for the viewer to open it', async () => {
    const fetchMock = stubRoutes({ [INVENTORY_PATH]: INVENTORY })
    mount()
    await screen.findByTestId('stored-fields-table-session')
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('renders the catalog inventory: table names, columns and row counts', async () => {
    stubRoutes({ [INVENTORY_PATH]: INVENTORY })
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
    expect(container.textContent).toContain('2 rows')
    expect(container.textContent).toContain('30 rows')
    expect(container.textContent).toContain('no rows stored')
  })

  it('states the closed table honestly instead of showing an empty rows grid', async () => {
    stubRoutes({ [INVENTORY_PATH]: INVENTORY })
    const { container } = mount()
    await screen.findByTestId('stored-fields-table-session')
    expect(screen.getByTestId('stored-fields-closed-session').textContent).toMatch(/open/i)
    expect(container.querySelectorAll('[data-testid^="stored-field-row-"]').length).toBe(0)
  })
})

describe('StoredFieldsPanel: rows load on open, 25 at a time', () => {
  it('loads a table’s rows only when the viewer opens it', async () => {
    const fetchMock = stubRoutes({
      [INVENTORY_PATH]: INVENTORY,
      [rowsPath('session', 1)]: rowsPage('session', 1),
    })
    mount()
    await screen.findByTestId('stored-fields-table-session')
    expect(fetchMock).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByTestId('stored-fields-table-toggle-session'))
    await screen.findByTestId('stored-field-row-session-0')
    expect(fetchMock).toHaveBeenCalledTimes(2)
    expect(fetchMock.mock.calls[1]![0]).toBe(rowsPath('session', 1))
  })

  it('pages a long table 25 rows at a time behind a load-more control', async () => {
    const fetchMock = stubRoutes({
      [INVENTORY_PATH]: INVENTORY,
      [rowsPath('record', 1)]: rowsPage('record', 1),
      [rowsPath('record', 2)]: rowsPage('record', 2),
    })
    mount()
    fireEvent.click(await screen.findByTestId('stored-fields-table-toggle-record'))
    await screen.findByTestId('stored-field-row-record-24')
    expect(within(screen.getByTestId('stored-fields-table-record')).getAllByTestId(/^stored-field-row-record-/).length).toBe(25)
    const more = screen.getByTestId('stored-fields-more-record')
    expect(more.textContent).toContain('5')
    fireEvent.click(more)
    await screen.findByTestId('stored-field-row-record-29')
    expect(within(screen.getByTestId('stored-fields-table-record')).getAllByTestId(/^stored-field-row-record-/).length).toBe(30)
    expect(fetchMock.mock.calls[1]![0]).toBe(rowsPath('record', 1))
    expect(fetchMock.mock.calls[2]![0]).toBe(rowsPath('record', 2))
    expect(screen.queryByTestId('stored-fields-more-record')).toBeNull()
  })

  it('shows a rows loading state while the opened page is in flight', async () => {
    const fetchMock = vi.fn((input: unknown) => {
      if (String(input) === INVENTORY_PATH) {
        return Promise.resolve({ ok: true, status: 200, json: async () => INVENTORY } as unknown as Response)
      }
      return new Promise<Response>(() => {})
    })
    vi.stubGlobal('fetch', fetchMock)
    mount()
    fireEvent.click(await screen.findByTestId('stored-fields-table-toggle-session'))
    const loading = await screen.findByTestId('stored-fields-loading-rows-session')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states a rows failure and retries it on press', async () => {
    let attempts = 0
    const fetchMock = vi.fn((input: unknown) => {
      const url = String(input)
      if (url === INVENTORY_PATH) {
        return Promise.resolve({ ok: true, status: 200, json: async () => INVENTORY } as unknown as Response)
      }
      attempts += 1
      return Promise.resolve(
        { ok: false, status: 500, json: async () => ({ error: 'boom' }) } as unknown as Response,
      )
    })
    vi.stubGlobal('fetch', fetchMock)
    mount()
    fireEvent.click(await screen.findByTestId('stored-fields-table-toggle-session'))
    const failure = await screen.findByTestId('stored-fields-rows-error-session')
    expect(failure.textContent).toMatch(/could not be loaded/i)
    expect(failure.getAttribute('role')).toBe('status')
    fireEvent.click(within(failure).getByTestId('stored-fields-rows-retry-session'))
    await screen.findByTestId('stored-fields-rows-error-session')
    expect(attempts).toBeGreaterThanOrEqual(2)
  })

  it('renders a measured zero as 0, never as not reported', async () => {
    stubRoutes({
      [INVENTORY_PATH]: INVENTORY,
      [rowsPath('session', 1)]: rowsPage('session', 1),
    })
    mount()
    fireEvent.click(await screen.findByTestId('stored-fields-table-toggle-session'))
    const zeroCells = await screen.findAllByTestId('stored-field-value-session.turn_count')
    expect(zeroCells[0]!.textContent).toBe('0')
  })

  it('renders a null cell in a present row as not reported', async () => {
    stubRoutes({
      [INVENTORY_PATH]: INVENTORY,
      [rowsPath('record', 1)]: rowsPage('record', 1),
    })
    mount()
    fireEvent.click(await screen.findByTestId('stored-fields-table-toggle-record'))
    const cells = await screen.findAllByTestId('stored-field-value-record.input_tokens')
    expect(cells[0]!.textContent).toMatch(/not reported/i)
    expect(cells[0]!.textContent).not.toBe('0')
    expect(cells[0]!.textContent).not.toBe('')
  })

  it('renders an empty optional child table as not reported with its missing parent keys, without a rows fetch', async () => {
    const fetchMock = stubRoutes({ [INVENTORY_PATH]: INVENTORY })
    mount()
    const empty = await screen.findByTestId('stored-fields-empty-record_model')
    expect(empty.textContent).toMatch(/not reported/i)
    expect(empty.textContent).toContain('span-copilot-1')
    expect(empty.querySelectorAll('[data-testid^="stored-field-value-"]').length).toBe(0)
    expect(empty.textContent).not.toMatch(/(^|\s)0(\s|$)/)
    expect(screen.queryByTestId('stored-fields-table-toggle-record_model')).toBeNull()
    expect(fetchMock).toHaveBeenCalledTimes(1)
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
    const fetchMock = stubRoutes({ [INVENTORY_PATH]: INVENTORY })
    mount(null)
    expect(screen.getByTestId('stored-fields-no-session').textContent).toMatch(/no session/i)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('states not found when the session was never projected', async () => {
    stubNotFound()
    mount()
    const notFound = await screen.findByTestId('stored-fields-not-found')
    expect(notFound.textContent).toMatch(/no stored fields/i)
    expect(notFound.getAttribute('role')).toBe('status')
    expect(notFound.getAttribute('aria-live')).toBe('polite')
  })

  it('states a load failure without inventing fields', async () => {
    stubError()
    mount()
    const failure = await screen.findByTestId('stored-fields-error')
    expect(failure.textContent).toMatch(/could not be loaded/i)
    expect(failure.getAttribute('role')).toBe('status')
    expect(failure.getAttribute('aria-live')).toBe('polite')
  })
})

function stubNotFound(): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async () =>
    ({ ok: false, status: 404, json: async () => ({ error: 'Session not found' }) }) as unknown as Response,
  )
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function stubError(): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async () =>
    ({ ok: false, status: 500, json: async () => ({ error: 'boom' }) }) as unknown as Response,
  )
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}
