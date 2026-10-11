// @vitest-environment happy-dom
// Dashboard port setting (RED first): a focused display-layer control over
// `GET|PUT /api/kyber/settings`. It reads the shared `dashboardPort`, writes it back
// through the one API, and says out loud that the new port applies on the next engine
// restart — it never claims the running server moved. UI pixels and input validation
// live here; persistence and semantic validation belong to the engine.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { DashboardPortSetting } from './DashboardPortSetting.js'

type Json = Record<string, any> // eslint-disable-line @typescript-eslint/no-explicit-any
type Recorded = { url: string; method: string; body: Json | null }

const SETTINGS = {
  folderImportScheduled: false,
  jobsPaused: false,
  refreshCadenceMinutes: 5,
  receiverHosted: false,
  dashboardPort: 4747,
}

type Reply = { status?: number; json?: unknown }

function stubFetch(options: {
  settings?: Reply
  put?: (call: Recorded) => Reply | Promise<Reply>
} = {}): { calls: Recorded[]; puts: () => Recorded[] } {
  const calls: Recorded[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      const call: Recorded = {
        url: String(url),
        method,
        body: init?.body ? (JSON.parse(String(init.body)) as Json) : null,
      }
      calls.push(call)
      let reply: Reply
      if (method === 'GET') reply = options.settings ?? { json: SETTINGS }
      else reply = (await options.put?.(call)) ?? { json: { ...SETTINGS } }
      const status = reply.status ?? 200
      return { ok: status >= 200 && status < 300, status, json: async () => reply.json ?? {} } as Response
    }),
  )
  return { calls, puts: () => calls.filter((c) => c.method === 'PUT') }
}

function renderSetting() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <DashboardPortSetting />
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('DashboardPortSetting: current value', () => {
  it('shows the persisted port read from the shared settings', async () => {
    stubFetch({ settings: { json: { ...SETTINGS, dashboardPort: 4848 } } })
    renderSetting()

    expect((await screen.findByTestId('dashboard-port-input') as HTMLInputElement).value).toBe('4848')
  })

  it('says the setting is unknown, rather than showing a default, when the read fails', async () => {
    stubFetch({ settings: { status: 500, json: { error: 'nope' } } })
    renderSetting()

    const alert = await screen.findByTestId('dashboard-port-unknown')
    expect(alert.getAttribute('role')).toBe('alert')
    expect(screen.queryByTestId('dashboard-port-input')).toBeNull()
  })
})

describe('DashboardPortSetting: saving', () => {
  it('saves through PUT /api/kyber/settings and states the restart plainly', async () => {
    const mock = stubFetch({
      put: () => ({ json: { ...SETTINGS, dashboardPort: 4848 } }),
    })
    renderSetting()
    const input = (await screen.findByTestId('dashboard-port-input')) as HTMLInputElement
    fireEvent.change(input, { target: { value: '4848' } })

    fireEvent.click(screen.getByTestId('dashboard-port-save'))

    await waitFor(() => expect(mock.puts()).toHaveLength(1))
    expect(mock.puts()[0]!.url).toBe('/api/kyber/settings')
    expect(mock.puts()[0]!.body).toEqual({ dashboardPort: 4848 })
    const notice = await screen.findByTestId('dashboard-port-restart-notice')
    expect(notice.textContent).toMatch(/restart/i)
    expect(notice.textContent).toMatch(/4848/)
  })

  it.each(['0', '65536', '-1', 'abc', '', '47.5'])(
    'refuses %j locally: no request leaves the browser',
    async (value) => {
      const mock = stubFetch()
      renderSetting()
      const input = (await screen.findByTestId('dashboard-port-input')) as HTMLInputElement
      fireEvent.change(input, { target: { value } })

      fireEvent.click(screen.getByTestId('dashboard-port-save'))

      expect(await screen.findByTestId('dashboard-port-error')).toBeDefined()
      expect(mock.puts()).toHaveLength(0)
      expect(screen.queryByTestId('dashboard-port-restart-notice')).toBeNull()
    },
  )

  it('shows a plain error, never the response body, when the save fails', async () => {
    stubFetch({ put: () => ({ status: 500, json: { error: '/secret/path leaked' } }) })
    renderSetting()
    const input = (await screen.findByTestId('dashboard-port-input')) as HTMLInputElement
    fireEvent.change(input, { target: { value: '4848' } })

    fireEvent.click(screen.getByTestId('dashboard-port-save'))

    const alert = await screen.findByTestId('dashboard-port-error')
    expect(alert.getAttribute('role')).toBe('alert')
    expect(alert.textContent).not.toContain('/secret/path')
  })

  it('keeps Save disabled while the write is in flight, so it cannot be sent twice', async () => {
    let release!: () => void
    const hold = new Promise<void>((resolve) => {
      release = resolve
    })
    const mock = stubFetch({
      put: async () => {
        await hold
        return { json: { ...SETTINGS, dashboardPort: 4848 } }
      },
    })
    renderSetting()
    const input = (await screen.findByTestId('dashboard-port-input')) as HTMLInputElement
    fireEvent.change(input, { target: { value: '4848' } })

    fireEvent.click(screen.getByTestId('dashboard-port-save'))
    await waitFor(() => expect((screen.getByTestId('dashboard-port-save') as HTMLButtonElement).disabled).toBe(true))
    fireEvent.click(screen.getByTestId('dashboard-port-save'))

    expect(mock.puts()).toHaveLength(1)
    release()
  })
})
