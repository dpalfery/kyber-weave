// @vitest-environment happy-dom
// T8 (RED first): the Settings drawer. It is a DISPLAY layer over
// `/api/kyber/capture`: every checkbox state comes from the server's status,
// a click asks the server for a dry-run preview, and only Confirm sends the
// write. The shared T0 fixtures are the wire payloads, consumed unmodified (a
// test derives a variant by cloning in memory, never by editing the files).
// Fetch is mocked per test; nothing reaches a real server.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { useState } from 'react'
import { render, screen, fireEvent, cleanup, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import statusMixed from '../../../../src/server/fixtures/capture-api/status-mixed.json'
import statusUnknown from '../../../../src/server/fixtures/capture-api/status-unknown.json'
import postPreviewEnable from '../../../../src/server/fixtures/capture-api/post-preview-enable.json'
import postWroteEnable from '../../../../src/server/fixtures/capture-api/post-wrote-enable.json'
import postRefusedDrift from '../../../../src/server/fixtures/capture-api/post-refused-drift.json'
import postDisableLast from '../../../../src/server/fixtures/capture-api/post-disable-last.json'

import { SettingsDrawer } from './SettingsDrawer.js'

type Json = Record<string, any> // eslint-disable-line @typescript-eslint/no-explicit-any

function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

/** The mixed status with one harness's fields overridden, so a test controls exactly one row. */
function mixedWith(id: string, patch: Json): Json {
  const status = clone(statusMixed) as Json
  const harness = (status.harnesses as Json[]).find((h) => h.id === id)!
  Object.assign(harness, patch)
  return status
}

type Recorded = { url: string; method: string; body: Json | null }
type Reply = { status?: number; json?: unknown; hold?: Promise<void> }

interface Mock {
  calls: Recorded[]
  gets: () => number
  posts: () => Recorded[]
  puts: () => Recorded[]
}

/** The dashboard-port control reads the shared settings on open, beside the capture status. */
const SETTINGS_JSON = {
  folderImportScheduled: false,
  jobsPaused: false,
  refreshCadenceMinutes: 5,
  receiverHosted: false,
  dashboardPort: 4747,
}

/** GET calls to one URL, so a new read-only fetch elsewhere does not move a count. */
function getsTo(mock: Mock, url: string): number {
  return mock.calls.filter((c) => c.method === 'GET' && c.url === url).length
}

/**
 * One fetch mock for the drawer's whole surface. `status` may be a function so
 * a test can serve a different answer after a write; `post` answers each POST.
 */
function stubFetch(options: {
  status?: () => Reply
  post?: (call: Recorded) => Reply
  put?: (call: Recorded) => Reply
}): Mock {
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
      if (method === 'GET' && String(url).includes('/api/kyber/settings')) reply = { json: SETTINGS_JSON }
      else if (method === 'GET') reply = options.status?.() ?? { json: statusMixed }
      else if (method === 'POST') reply = options.post?.(call) ?? { status: 500, json: {} }
      else reply = options.put?.(call) ?? { json: { receiverHosted: true } }
      if (reply.hold) await reply.hold
      const status = reply.status ?? 200
      return { ok: status >= 200 && status < 300, status, json: async () => reply.json ?? {} } as Response
    }),
  )
  return {
    calls,
    gets: () => calls.filter((c) => c.method === 'GET').length,
    posts: () => calls.filter((c) => c.method === 'POST'),
    puts: () => calls.filter((c) => c.method === 'PUT'),
  }
}

function renderDrawer(props: { open?: boolean; onClose?: () => void } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  const onClose = props.onClose ?? (() => {})
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <SettingsDrawer open={props.open ?? true} onClose={onClose} />
    </QueryClientProvider>,
  )
  return { ...utils, queryClient, onClose }
}

async function openedDrawer(props: { onClose?: () => void } = {}) {
  const utils = renderDrawer(props)
  await screen.findByTestId('settings-harness-row-copilot')
  return utils
}

const checkbox = (id: string) => screen.getByTestId(`settings-harness-checkbox-${id}`) as HTMLInputElement

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('SettingsDrawer: opening', () => {
  it('renders nothing and requests nothing while closed', () => {
    const mock = stubFetch({})
    const { container } = renderDrawer({ open: false })

    expect(container.innerHTML).toBe('')
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(mock.calls).toHaveLength(0)
  })

  it('is a labelled modal dialog that fetches the status exactly once on open', async () => {
    const mock = stubFetch({})
    await openedDrawer()

    const dialog = screen.getByRole('dialog')
    expect(dialog.getAttribute('aria-modal')).toBe('true')
    expect(dialog.getAttribute('aria-label')).toBe('Settings')
    expect(getsTo(mock, '/api/kyber/capture')).toBe(1)
    // The dashboard-port control reads the shared settings beside the capture status.
    expect(getsTo(mock, '/api/kyber/settings')).toBe(1)
  })

  it('moves focus into the dialog (the close button) when it opens', async () => {
    stubFetch({})
    await openedDrawer()

    expect(document.activeElement).toBe(screen.getByTestId('settings-drawer-close-button'))
  })

  it('closes on Escape and on the close button', async () => {
    stubFetch({})
    const onClose = vi.fn()
    await openedDrawer({ onClose })

    fireEvent.keyDown(window, { key: 'Escape' })
    expect(onClose).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByTestId('settings-drawer-close-button'))
    expect(onClose).toHaveBeenCalledTimes(2)
  })

  it('returns focus to the opener when it closes', async () => {
    stubFetch({})
    function Host() {
      const [open, setOpen] = useState(false)
      return (
        <div>
          <button type="button" data-testid="opener" onClick={() => setOpen(true)}>
            open
          </button>
          <SettingsDrawer open={open} onClose={() => setOpen(false)} />
        </div>
      )
    }
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
    render(
      <QueryClientProvider client={queryClient}>
        <Host />
      </QueryClientProvider>,
    )
    const opener = screen.getByTestId('opener')
    opener.focus()
    fireEvent.click(opener)
    await screen.findByRole('dialog')

    fireEvent.keyDown(window, { key: 'Escape' })

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(document.activeElement).toBe(opener)
  })

  it('says the status could not be read, rather than showing defaults, when the request fails', async () => {
    stubFetch({ status: () => ({ status: 500, json: { error: 'Capture failed' } }) })
    renderDrawer()

    const alert = await screen.findByTestId('settings-load-error')
    expect(alert.getAttribute('role')).toBe('alert')
    expect(screen.queryByTestId('settings-harness-row-copilot')).toBeNull()
  })

  it('refreshes the status on demand, with no other request', async () => {
    const mock = stubFetch({})
    await openedDrawer()

    fireEvent.click(screen.getByTestId('settings-refresh-status-button'))

    await waitFor(() => expect(getsTo(mock, '/api/kyber/capture')).toBe(2))
    expect(mock.posts()).toHaveLength(0)
  })
})

describe('SettingsDrawer: rows come only from the server', () => {
  it('lists every harness; managed rows get a checkbox, status-only rows do not', async () => {
    stubFetch({})
    await openedDrawer()

    for (const id of ['claude-code', 'copilot', 'opencode']) {
      expect(screen.getByTestId(`settings-harness-checkbox-${id}`)).toBeDefined()
    }
    for (const id of ['codex', 'pi', 'antigravity']) {
      expect(screen.getByTestId(`settings-harness-row-${id}`)).toBeDefined()
      expect(screen.queryByTestId(`settings-harness-checkbox-${id}`)).toBeNull()
    }
  })

  it('shows status-only rows with their server-provided lines', async () => {
    stubFetch({})
    await openedDrawer()

    const pi = screen.getByTestId('settings-harness-row-pi')
    expect(pi.textContent).toContain('extension: collector (@dpalfery/pi-statusline)')
    expect(pi.textContent).toContain('warn: both the collector and ObservMe are loaded')
    expect(screen.getByTestId('settings-harness-row-codex').textContent).toContain(
      'no [otel] table is present; coverage comes from session files',
    )
  })

  it('renders on as checked, off as unchecked and partial as indeterminate', async () => {
    // claude-code is on-without-receipt in the fixture; give it a receipt so it is switchable.
    stubFetch({ status: () => ({ json: mixedWith('claude-code', { receipt: 'recorded' }) }) })
    await openedDrawer()

    expect(checkbox('claude-code').checked).toBe(true)
    expect(checkbox('claude-code').indeterminate).toBe(false)
    expect(checkbox('opencode').checked).toBe(false)
    expect(checkbox('opencode').indeterminate).toBe(false)
    expect(checkbox('copilot').indeterminate).toBe(true)
    expect(checkbox('copilot').getAttribute('aria-checked')).toBe('mixed')
  })

  it('disables a row whose state is unknown and says why', async () => {
    stubFetch({ status: () => ({ json: statusUnknown }) })
    await openedDrawer()

    expect(checkbox('claude-code').disabled).toBe(true)
    expect(screen.getByTestId('settings-harness-reason-claude-code').textContent).toMatch(/unknown|could not/i)
    // The other managed row stays switchable.
    expect(checkbox('copilot').disabled).toBe(false)
  })

  it('disables a not-detected harness that has no receipt, naming the reason', async () => {
    stubFetch({ status: () => ({ json: mixedWith('opencode', { installed: 'not-detected' }) }) })
    await openedDrawer()

    expect(checkbox('opencode').disabled).toBe(true)
    expect(screen.getByTestId('settings-harness-reason-opencode').textContent).toMatch(/not detected/i)
  })

  it('keeps a not-detected harness switchable off when KyberDash recorded a receipt', async () => {
    stubFetch({
      status: () => ({ json: mixedWith('opencode', { installed: 'not-detected', state: 'on', receipt: 'recorded' }) }),
    })
    await openedDrawer()

    expect(checkbox('opencode').disabled).toBe(false)
    expect(checkbox('opencode').checked).toBe(true)
  })

  it('disables an on harness with no receipt: the keys were set outside KyberDash', async () => {
    stubFetch({})
    await openedDrawer()

    // Fixture: claude-code is `on` with `receipt: absent`.
    expect(checkbox('claude-code').checked).toBe(true)
    expect(checkbox('claude-code').disabled).toBe(true)
    expect(screen.getByTestId('settings-harness-reason-claude-code').textContent).toMatch(/outside KyberDash|nothing to restore/i)
  })

  it('shows the endpoint-differs warning as visible text', async () => {
    stubFetch({})
    await openedDrawer()

    expect(screen.getByTestId('settings-harness-row-claude-code').textContent).toContain(
      'env.OTEL_EXPORTER_OTLP_ENDPOINT is http://127.0.0.1:4319',
    )
  })

  it('names the OpenCode environment variable as a manual step without claiming it is set', async () => {
    stubFetch({})
    await openedDrawer()

    const manual = screen.getByTestId('settings-harness-manual-opencode')
    expect(manual.textContent).toContain('OTEL_EXPORTER_OTLP_ENDPOINT')
    expect(manual.textContent).toMatch(/cannot (see|read|observe)/i)
    expect(manual.textContent).not.toMatch(/is currently|already set|is set to/i)
  })
})

describe('SettingsDrawer: preview, confirm, cancel', () => {
  /** claude-code switchable (off + receipt absent is fine: enabling needs no receipt). */
  const offClaude = () => ({ json: mixedWith('claude-code', { state: 'off', warnings: [] }) })

  it('sends a dry-run POST on click and shows the preview, writing nothing', async () => {
    const mock = stubFetch({ status: offClaude, post: () => ({ json: postPreviewEnable }) })
    await openedDrawer()

    fireEvent.click(checkbox('claude-code'))

    const preview = await screen.findByTestId('settings-preview')
    expect(mock.posts()).toHaveLength(1)
    expect(mock.posts()[0]!.body).toEqual({ action: 'enable', harness: 'claude-code', dryRun: true })
    expect(preview.textContent).toContain('/Users/example/.claude/settings.json')
    expect(preview.textContent).toContain('env.OTEL_LOGS_EXPORTER')
    expect(preview.textContent).toContain('console')
    expect(preview.textContent).toContain('otlp')
    // Still unchanged until Confirm: the checkbox shows server state only.
    expect(checkbox('claude-code').checked).toBe(false)
  })

  it('names the receiver change in the preview when the route says it will enable it', async () => {
    const withReceiver = clone(postPreviewEnable) as Json
    withReceiver.receiver.willEnable = true
    withReceiver.receiver.hosted = false
    stubFetch({ status: offClaude, post: () => ({ json: withReceiver }) })
    await openedDrawer()

    fireEvent.click(checkbox('claude-code'))

    const note = await screen.findByTestId('settings-preview-receiver-note')
    expect(note.textContent).toMatch(/receiver/i)
    expect(note.textContent).toMatch(/turn(ed)? on|enable/i)
  })

  it('says the receiver is already on, not that it will be turned on, when it is hosted', async () => {
    const hostedPreview = clone(postPreviewEnable) as Json
    hostedPreview.receiver.willEnable = true
    hostedPreview.receiver.hosted = true
    stubFetch({ status: offClaude, post: () => ({ json: hostedPreview }) })
    await openedDrawer()

    fireEvent.click(checkbox('claude-code'))

    const note = await screen.findByTestId('settings-preview-receiver-note')
    expect(note.textContent).toMatch(/already on/i)
    expect(note.textContent).not.toMatch(/currently off|will also turn on/i)
  })

  it('Cancel sends nothing more and dismisses the preview', async () => {
    const mock = stubFetch({ status: offClaude, post: () => ({ json: postPreviewEnable }) })
    await openedDrawer()
    fireEvent.click(checkbox('claude-code'))
    await screen.findByTestId('settings-preview')
    const callsBefore = mock.calls.length

    fireEvent.click(screen.getByTestId('settings-preview-cancel'))

    expect(screen.queryByTestId('settings-preview')).toBeNull()
    expect(mock.calls).toHaveLength(callsBefore)
  })

  it('Confirm sends the write, then refetches the status', async () => {
    let written = false
    const mock = stubFetch({
      status: () => ({ json: written ? statusMixed : mixedWith('copilot', { state: 'off' }) }),
      post: (call) => {
        if (call.body?.dryRun === true) {
          const preview = clone(postWroteEnable) as Json
          preview.dryRun = true
          preview.outcome = 'previewed'
          preview.events[0].kind = 'would-write'
          return { json: preview }
        }
        written = true
        return { json: postWroteEnable }
      },
    })
    await openedDrawer()
    fireEvent.click(checkbox('copilot'))
    await screen.findByTestId('settings-preview')

    fireEvent.click(screen.getByTestId('settings-preview-confirm'))

    await waitFor(() => expect(mock.posts()).toHaveLength(2))
    expect(mock.posts()[1]!.body).toEqual({ action: 'enable', harness: 'copilot', dryRun: false })
    await waitFor(() => expect(getsTo(mock, '/api/kyber/capture')).toBe(2))
    await waitFor(() => expect(screen.queryByTestId('settings-preview')).toBeNull())
    expect(screen.getByTestId('settings-result').textContent).toMatch(/copilot/i)
  })

  it('a partial row asks to enable (complete) rather than disable', async () => {
    const mock = stubFetch({ post: () => ({ json: postPreviewEnable }) })
    await openedDrawer()

    fireEvent.click(checkbox('copilot'))

    await screen.findByTestId('settings-preview')
    expect(mock.posts()[0]!.body).toEqual({ action: 'enable', harness: 'copilot', dryRun: true })
  })

  it('an on row with a receipt asks to disable', async () => {
    const mock = stubFetch({
      status: () => ({ json: mixedWith('claude-code', { receipt: 'recorded' }) }),
      post: () => ({ json: postPreviewEnable }),
    })
    await openedDrawer()

    fireEvent.click(checkbox('claude-code'))

    await screen.findByTestId('settings-preview')
    expect(mock.posts()[0]!.body).toEqual({ action: 'disable', harness: 'claude-code', dryRun: true })
  })

  it('shows a 409 drift refusal as an alert with the drifted keys, leaves the checkbox unchanged and writes nothing', async () => {
    const mock = stubFetch({
      status: () => ({ json: mixedWith('claude-code', { receipt: 'recorded' }) }),
      post: () => ({ status: 409, json: postRefusedDrift }),
    })
    await openedDrawer()

    fireEvent.click(checkbox('claude-code'))

    const alert = await screen.findByTestId('settings-harness-alert-claude-code')
    expect(alert.getAttribute('role')).toBe('alert')
    expect(alert.textContent).toContain('disable refused: claude-code drifted since enable; no files were changed')
    expect(alert.textContent).toContain('env.OTEL_LOG_USER_PROMPTS')
    expect(alert.textContent).toContain('env.OTEL_LOG_RAW_API_BODIES')
    expect(screen.queryByTestId('settings-preview')).toBeNull()
    expect(checkbox('claude-code').checked).toBe(true)
    expect(mock.posts()).toHaveLength(1)
  })

  it('shows a plain error, never the response body, when the request fails outright', async () => {
    stubFetch({ status: offClaude, post: () => ({ status: 500, json: { error: '/etc/secret/path leaked' } }) })
    await openedDrawer()

    fireEvent.click(checkbox('claude-code'))

    const alert = await screen.findByTestId('settings-harness-alert-claude-code')
    expect(alert.textContent).not.toContain('/etc/secret/path')
    expect(alert.textContent).toMatch(/could not/i)
  })

  it('blocks every checkbox and a second click while a request is in flight', async () => {
    let release!: () => void
    const hold = new Promise<void>((resolve) => {
      release = resolve
    })
    const mock = stubFetch({
      status: offClaude,
      post: () => ({ json: postPreviewEnable, hold }),
    })
    await openedDrawer()

    fireEvent.click(checkbox('claude-code'))
    await waitFor(() => expect(checkbox('opencode').disabled).toBe(true))
    expect(checkbox('copilot').disabled).toBe(true)
    fireEvent.click(checkbox('opencode'))
    expect(mock.posts()).toHaveLength(1)

    release()
    await screen.findByTestId('settings-preview')
  })

  it('keeps Confirm disabled while the write is in flight, so it cannot be sent twice', async () => {
    let release!: () => void
    const hold = new Promise<void>((resolve) => {
      release = resolve
    })
    const mock = stubFetch({
      status: offClaude,
      post: (call) => (call.body?.dryRun === true ? { json: postPreviewEnable } : { json: postWroteEnable, hold }),
    })
    await openedDrawer()
    fireEvent.click(checkbox('claude-code'))
    await screen.findByTestId('settings-preview')

    fireEvent.click(screen.getByTestId('settings-preview-confirm'))
    await waitFor(() => expect((screen.getByTestId('settings-preview-confirm') as HTMLButtonElement).disabled).toBe(true))
    fireEvent.click(screen.getByTestId('settings-preview-confirm'))

    expect(mock.posts()).toHaveLength(2)
    release()
  })

  it('states that the receiver stays hosted after the last managed harness is turned off', async () => {
    stubFetch({
      status: () => ({ json: mixedWith('opencode', { state: 'on', receipt: 'recorded' }) }),
      post: (call) => {
        if (call.body?.dryRun === true) {
          const preview = clone(postDisableLast) as Json
          preview.dryRun = true
          preview.outcome = 'previewed'
          preview.events[0].kind = 'would-restore'
          return { json: preview }
        }
        return { json: postDisableLast }
      },
    })
    await openedDrawer()
    fireEvent.click(checkbox('opencode'))
    await screen.findByTestId('settings-preview')

    fireEvent.click(screen.getByTestId('settings-preview-confirm'))

    const note = await screen.findByTestId('settings-receiver-note')
    expect(note.textContent).toMatch(/still hosted/i)
    expect(note.textContent).toMatch(/receiver section|switch it off/i)
  })
})

describe('SettingsDrawer: receiver section', () => {
  it('shows the hosted flag and liveness from the server', async () => {
    stubFetch({})
    await openedDrawer()

    const section = screen.getByTestId('settings-receiver')
    expect(section.textContent).toContain('KyberDash receiver is listening at http://127.0.0.1:4318/healthz')
    expect((screen.getByTestId('settings-receiver-toggle') as HTMLInputElement).checked).toBe(true)
  })

  it('writes receiverHosted through the settings API and then re-reads the status', async () => {
    const mock = stubFetch({ put: () => ({ json: { receiverHosted: false } }) })
    await openedDrawer()

    fireEvent.click(screen.getByTestId('settings-receiver-toggle'))

    await waitFor(() => expect(mock.puts()).toHaveLength(1))
    expect(mock.puts()[0]!.url).toBe('/api/kyber/settings')
    expect(mock.puts()[0]!.body).toEqual({ receiverHosted: false })
    // The receiver write re-reads both the capture status and the shared settings.
    await waitFor(() => expect(getsTo(mock, '/api/kyber/capture')).toBe(2))
    await waitFor(() => expect(getsTo(mock, '/api/kyber/settings')).toBe(2))
  })

  it('shows a not-hosted receiver as off and turns it on when toggled', async () => {
    const mock = stubFetch({ status: () => ({ json: statusUnknown }), put: () => ({ json: { receiverHosted: true } }) })
    await openedDrawer()

    const toggle = screen.getByTestId('settings-receiver-toggle') as HTMLInputElement
    expect(toggle.checked).toBe(false)
    expect(within(screen.getByTestId('settings-receiver')).getByText(/does not host the OTLP receiver/i)).toBeDefined()
    fireEvent.click(toggle)

    await waitFor(() => expect(mock.puts()[0]?.body).toEqual({ receiverHosted: true }))
  })
})
