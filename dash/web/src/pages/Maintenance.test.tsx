// @vitest-environment happy-dom
// Issue #319 T13 (RED, architecture rule R1): the web dashboard is a DISPLAY
// layer. It renders server state from `GET /api/kyber/jobs` and
// `GET /api/kyber/settings` and issues the bounded maintenance calls — nothing
// is scheduled, imported or decided here; the JobHost on the server owns all of
// that. Hence every assertion below is about rendered state and request bodies,
// never about behaviour the browser would have to implement.
//
// The suite fails today: `MaintenancePanel` does not exist, and
// `dash/web/src/lib/kyberApi.ts` has no client for jobs/settings/refresh/
// import-history. Fetch is mocked per test — no test may reach a real server.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { MaintenancePanel } from '../components/maintenance/MaintenancePanel.js'
import { CleanDatabaseControl } from '../components/maintenance/CleanDatabaseControl.js'
import type { KyberHarnessSummary } from '../lib/kyberApi.js'

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

type RecordedCall = { url: string; method: string; body: Record<string, unknown> | null }

type Reply = { status?: number; json?: unknown }

/**
 * One fetch mock for the whole panel surface: it answers GET jobs/settings
 * from the payload arguments, records every call, and lets a test override the
 * reply for any other verb.
 */
function stubFetch(options: {
  jobs?: Reply
  settings?: Reply
  otherwise?: (call: RecordedCall) => Reply
} = {}): { calls: RecordedCall[] } {
  const calls: RecordedCall[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      const call: RecordedCall = {
        url: String(url),
        method,
        body: init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : null,
      }
      calls.push(call)
      let reply: Reply
      if (method === 'GET' && call.url.includes('/jobs')) {
        reply = options.jobs ?? { json: JOBS }
      } else if (method === 'GET' && call.url.includes('/settings')) {
        reply = options.settings ?? { json: SETTINGS }
      } else {
        reply = options.otherwise?.(call) ?? { status: 202, json: {} }
      }
      const status = reply.status ?? 200
      return {
        ok: status >= 200 && status < 300,
        status,
        json: async () => reply.json ?? {},
      } as Response
    }),
  )
  return { calls }
}

function renderPanel() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
  const utils = renderDom(
    <QueryClientProvider client={queryClient}>
      <MaintenancePanel harnesses={HARNESSES} />
    </QueryClientProvider>,
  )
  return { ...utils, queryClient }
}

async function openPanel() {
  const utils = renderPanel()
  await screen.findByTestId('maintenance-panel')
  await waitFor(() => expect(screen.getByTestId('maintenance-jobs-store-generation')).toBeDefined())
  return utils
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('Issue #319: maintenance panel renders server state', () => {
  it('shows the refresh, pause, generation and hosting state from GET /api/kyber/jobs', async () => {
    stubFetch({
      jobs: {
        json: {
          ...JOBS,
          refresh: { ...JOBS.refresh, state: 'failed', lastFailure: 'exit 1 at 10:07' },
        },
      },
    })
    await openPanel()

    expect(screen.getByTestId('maintenance-jobs-refresh-state').textContent).toMatch(/failed/i)
    expect(screen.getByTestId('maintenance-jobs-last-success').textContent).toContain(
      '2026-10-09T10:00:00.000Z',
    )
    // The failure reason is shown, but only because the server sent it in the
    // status payload — no remote error body is ever rendered.
    expect(screen.getByTestId('maintenance-jobs-last-failure').textContent).toContain('exit 1')
    expect(screen.getByTestId('maintenance-jobs-next-due').textContent).toContain(
      '2026-10-09T10:05:00.000Z',
    )
    expect(screen.getByTestId('maintenance-jobs-store-generation').textContent).toContain('7')
    // Polarity matters here: with paused:false the copy must actually say "not
    // paused"/"running" — asserting only the absence of "paused" would pass for
    // any rendering, including "unknown".
    expect(screen.getByTestId('maintenance-jobs-paused').textContent).toMatch(
      /not paused|running/i,
    )
    // WHY two assertions: the natural copy is "Not hosted elsewhere", so a
    // bare not.toMatch(/hosted elsewhere/i) rejects the correct rendering. Pin
    // the positive phrasing, then forbid any un-negated "hosted elsewhere".
    expect(screen.getByTestId('maintenance-jobs-hosted-elsewhere').textContent).toMatch(
      /not hosted elsewhere|this host|hosted here|local/i,
    )
    expect(screen.getByTestId('maintenance-jobs-hosted-elsewhere').textContent).not.toMatch(
      /^(?!.*\bnot\b).*hosted elsewhere/i,
    )
  })

  // The companion of the assertion above: with paused:true the text has to read
  // as paused, and with hostedElsewhere:true it has to read as hosted elsewhere
  // while never carrying the negated "not hosted" phrasing.
  it('states the paused and hosted-elsewhere state with the opposite polarity', async () => {
    stubFetch({ jobs: { json: { ...JOBS, paused: true, hostedElsewhere: true } } })
    await openPanel()

    // WHY both: "paused" alone matches the substring inside "not paused", so
    // the positive check is paired with an explicit ban on the negated phrasing.
    expect(screen.getByTestId('maintenance-jobs-paused').textContent).toMatch(/paused/i)
    expect(screen.getByTestId('maintenance-jobs-paused').textContent).not.toMatch(
      /not paused|running/i,
    )
    const hosted = screen.getByTestId('maintenance-jobs-hosted-elsewhere').textContent ?? ''
    expect(hosted).toMatch(/hosted elsewhere/i)
    expect(hosted).not.toMatch(/not hosted/i)
  })

  it('shows the shared settings from GET /api/kyber/settings', async () => {
    stubFetch()
    await openPanel()

    expect(screen.getByTestId('maintenance-settings-folder-import-scheduled').textContent).toMatch(
      /off|not scheduled/i,
    )
    expect(screen.getByTestId('maintenance-settings-refresh-cadence').textContent).toContain('5')
    // `receiverHosted: true` must not be satisfied by "Not hosted" — the
    // negation carries its own match, so the positive pattern is anchored to
    // exclude it and the text is checked against the negated phrase too.
    const hosted = screen.getByTestId('maintenance-settings-receiver-hosted').textContent ?? ''
    expect(hosted).toMatch(/hosted/i)
    expect(hosted).not.toMatch(/not hosted/i)
  })

  it('states folder-import-scheduled and receiver-hosted with the opposite polarity', async () => {
    stubFetch({
      settings: { json: { ...SETTINGS, folderImportScheduled: true, receiverHosted: false } },
    })
    await openPanel()

    const scheduled =
      screen.getByTestId('maintenance-settings-folder-import-scheduled').textContent ?? ''
    expect(scheduled).not.toMatch(/off|not scheduled/i)
    expect(scheduled).toMatch(/on|scheduled/i)
    expect(screen.getByTestId('maintenance-settings-receiver-hosted').textContent).toMatch(
      /not hosted/i,
    )
  })

  // "unknown" is the honest rendering of an unobservable read. Substituting a
  // default (0 records, "healthy", "off") would be the dashboard inventing
  // server state it never received.
  it('renders unknown for job state when GET /api/kyber/jobs fails', async () => {
    stubFetch({ jobs: { status: 500, json: { error: 'store locked by pid 4242' } } })
    await openPanel()

    expect(screen.getByTestId('maintenance-jobs-refresh-state').textContent).toMatch(/unknown/i)
    expect(screen.getByTestId('maintenance-jobs-store-generation').textContent).toMatch(/unknown/i)
    // Never the remote body.
    expect(screen.getByTestId('maintenance-panel').textContent).not.toContain('4242')
  })

  it('renders unknown for settings when GET /api/kyber/settings fails', async () => {
    stubFetch({ settings: { status: 503, json: { error: 'settings unavailable' } } })
    await openPanel()

    expect(screen.getByTestId('maintenance-settings-refresh-cadence').textContent).toMatch(
      /unknown/i,
    )
    expect(screen.getByTestId('maintenance-settings-folder-import-scheduled').textContent).toMatch(
      /unknown/i,
    )
  })
})

describe('Issue #319: pause only stops scheduled jobs', () => {
  it('pauses through PUT /api/kyber/settings and discloses what keeps running', async () => {
    const { calls } = stubFetch()
    await openPanel()

    fireEvent.click(screen.getByTestId('maintenance-pause-button'))

    await waitFor(() => {
      const put = calls.find((c) => c.method === 'PUT' && c.url.includes('/settings'))
      expect(put?.url).toContain('/api/kyber/settings')
      expect(put?.body).toEqual({ jobsPaused: true })
    })

    // The disclosure is the whole point of the control: pausing jobs must not
    // read as "the dashboard stops updating".
    const disclosure = screen.getByTestId('maintenance-pause-disclosure').textContent ?? ''
    expect(disclosure).toMatch(/only scheduled jobs stop|only scheduled/i)
    expect(disclosure).toMatch(/manual/i)
    expect(disclosure).toMatch(/otlp/i)
    expect(disclosure).toMatch(/retention/i)
  })

  it('resumes with jobsPaused:false', async () => {
    const { calls } = stubFetch({ jobs: { json: { ...JOBS, paused: true } } })
    await openPanel()

    const button = screen.getByTestId('maintenance-pause-button')
    expect(button.textContent).toMatch(/resume/i)
    fireEvent.click(button)

    await waitFor(() => {
      const put = calls.find((c) => c.method === 'PUT' && c.url.includes('/settings'))
      expect(put?.body).toEqual({ jobsPaused: false })
    })
  })

  it('sends one PUT per pause click: a second click while the first is pending is refused', async () => {
    // The double-write the disabled state exists to prevent: two PUTs a moment apart
    // carrying opposite `jobsPaused` values, with the panel rendering whichever landed
    // last - and nothing about the request tells the operator their click was dropped.
    const puts: RecordedCall[] = []
    let release!: () => void
    const held = new Promise<void>((resolve) => {
      release = resolve
    })
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        const method = init?.method ?? 'GET'
        const call: RecordedCall = {
          url: String(url),
          method,
          body: init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : null,
        }
        const json = method === 'GET' && call.url.includes('/jobs')
          ? JOBS
          : method === 'GET' && call.url.includes('/settings')
            ? SETTINGS
            : (puts.push(call), await held, {})
        return { ok: true, status: 200, json: async () => json } as Response
      }),
    )

    await openPanel()
    const button = screen.getByTestId('maintenance-pause-button') as HTMLButtonElement
    expect(button.disabled).toBe(false)

    fireEvent.click(button)
    await waitFor(() => expect(puts).toHaveLength(1))

    // In flight: the same `disabled` attribute every other disabled control in this panel
    // uses, so it is disabled to the accessibility tree and not merely styled.
    await waitFor(() => expect(button.disabled).toBe(true))
    fireEvent.click(button)
    fireEvent.click(button)
    expect(puts).toHaveLength(1)
    expect(puts[0].body).toEqual({ jobsPaused: true })

    release()
    await waitFor(() => expect(button.disabled).toBe(false))

    // Still usable afterwards - it gates the write, not the control.
    fireEvent.click(button)
    await waitFor(() => expect(puts).toHaveLength(2))
  })

  // Pausing is a pause of the scheduler, not a lock on the product: the
  // operator must still be able to clean, refresh, import, and un-pause.
  it('keeps Pause, Refresh, Import and Clean enabled while paused', async () => {
    stubFetch({
      jobs: { json: { ...JOBS, paused: true } },
      settings: { json: { ...SETTINGS, jobsPaused: true } },
    })
    renderDom(
      <QueryClientProvider
        client={new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })}
      >
        <>
          <MaintenancePanel harnesses={HARNESSES} />
          <CleanDatabaseControl harnesses={HARNESSES} />
        </>
      </QueryClientProvider>,
    )
    await screen.findByTestId('maintenance-panel')
    // WHY: wait for the jobs/settings fetches to resolve, otherwise the
    // controls are asserted while still in their loading state.
    await screen.findByTestId('maintenance-jobs-store-generation')

    for (const id of [
      'maintenance-pause-button',
      'maintenance-refresh-button',
      'maintenance-import-button',
      'clean-database-button',
    ]) {
      const control = screen.getByTestId(id)
      expect((control as HTMLButtonElement).disabled, `${id} must stay usable while paused`).toBe(
        false,
      )
    }
  })
})

describe('Issue #319: refresh, import and the scheduled toggle', () => {
  it('requests a data refresh scoped to the web surface', async () => {
    const { calls } = stubFetch()
    await openPanel()

    fireEvent.click(screen.getByTestId('maintenance-refresh-button'))

    await waitFor(() => {
      const post = calls.find((c) => c.method === 'POST' && c.url.includes('/refresh'))
      expect(post?.url).toContain('/api/kyber/refresh')
      expect(post?.body).toMatchObject({ surface: 'web' })
    })
  })

  it('reports a running refresh on 409 without the remote body', async () => {
    stubFetch({
      otherwise: (call) =>
        call.url.includes('/refresh')
          ? { status: 409, json: { error: 'already running as pid 4242' } }
          : { status: 202, json: {} },
    })
    await openPanel()

    fireEvent.click(screen.getByTestId('maintenance-refresh-button'))

    const status = await screen.findByTestId('maintenance-refresh-status')
    expect(status.textContent).toMatch(/already running|running/i)
    expect(status.textContent).not.toContain('4242')
  })

  it('imports folder history for the default one week', async () => {
    const { calls } = stubFetch()
    await openPanel()

    const weeks = (await screen.findByTestId('maintenance-import-weeks')) as HTMLInputElement
    expect(weeks.value).toBe('1')
    fireEvent.click(screen.getByTestId('maintenance-import-button'))

    await waitFor(() => {
      const post = calls.find((c) => c.method === 'POST' && c.url.includes('/import-history'))
      expect(post?.url).toContain('/api/kyber/import-history')
      expect(post?.body).toMatchObject({ weeks: 1 })
    })
  })

  it('imports the requested window and narrows it to selected harnesses', async () => {
    const { calls } = stubFetch()
    await openPanel()

    fireEvent.change(await screen.findByTestId('maintenance-import-weeks'), {
      target: { value: '52' },
    })
    fireEvent.click(screen.getByTestId('maintenance-import-harness-pi'))
    fireEvent.click(screen.getByTestId('maintenance-import-button'))

    await waitFor(() => {
      const post = calls.find((c) => c.method === 'POST' && c.url.includes('/import-history'))
      expect(post?.body).toMatchObject({ weeks: 52, harnesses: ['pi'] })
    })
  })

  it.each([
    ['zero', '0'],
    ['above a year', '53'],
    ['fractional', '1.5'],
  ])('sends no import for a %s weeks value', async (_label, value) => {
    const { calls } = stubFetch()
    await openPanel()

    fireEvent.change(await screen.findByTestId('maintenance-import-weeks'), {
      target: { value },
    })
    const button = screen.getByTestId('maintenance-import-button')
    expect(button).toBeDisabled()
    fireEvent.click(button)
    expect(calls.filter((c) => c.url.includes('/import-history'))).toHaveLength(0)
  })

  it('toggles the scheduled folder import and discloses its first-tick window', async () => {
    const { calls } = stubFetch()
    await openPanel()

    fireEvent.click(screen.getByTestId('maintenance-folder-import-toggle'))
    await waitFor(() => {
      const put = calls.find((c) => c.method === 'PUT' && c.url.includes('/settings'))
      expect(put?.body).toEqual({ folderImportScheduled: true })
    })

    // Turning scheduling on must not read as "the whole window imports now":
    // the host catches up at its next tick, bounded.
    const disclosure = screen.getByTestId('maintenance-folder-import-disclosure').textContent ?? ''
    expect(disclosure).toMatch(/two weeks|2 weeks/i)
    expect(disclosure).toMatch(/next (tick|run|scheduled)/i)
  })

  it('turns the scheduled folder import back off', async () => {
    const { calls } = stubFetch({
      settings: { json: { ...SETTINGS, folderImportScheduled: true } },
    })
    await openPanel()

    fireEvent.click(screen.getByTestId('maintenance-folder-import-toggle'))
    await waitFor(() => {
      const put = calls.find((c) => c.method === 'PUT' && c.url.includes('/settings'))
      expect(put?.body).toEqual({ folderImportScheduled: false })
    })
  })

  it('writes the refresh cadence in minutes', async () => {
    const { calls } = stubFetch()
    await openPanel()

    fireEvent.change(await screen.findByTestId('maintenance-cadence-input'), {
      target: { value: '30' },
    })
    fireEvent.click(screen.getByTestId('maintenance-cadence-apply'))

    await waitFor(() => {
      const put = calls.find((c) => c.method === 'PUT' && c.url.includes('/settings'))
      expect(put?.body).toEqual({ refreshCadenceMinutes: 30 })
    })
  })
})