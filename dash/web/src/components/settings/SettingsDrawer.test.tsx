// @vitest-environment happy-dom
// T8 (RED first): the Settings drawer. It is a DISPLAY layer over
// `/api/kyber/capture`: every button state comes from the server's status,
// a click asks the server for a dry-run preview, and only Confirm sends the
// write. The shared T0 fixtures are the wire payloads, consumed unmodified (a
// test derives a variant by cloning in memory, never by editing the files).
// Fetch is mocked per test; nothing reaches a real server.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { useState } from 'react'
import { act, render, screen, fireEvent, cleanup, waitFor, within } from '@testing-library/react'
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
type Reply = { status?: number; json?: unknown; hold?: Promise<void>; networkError?: boolean }

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

/** The running engine's version, as `/api/kyber/version` answers it. */
const VERSION_JSON = { version: '9.8.7-rc.1', apiVersion: 1 }

/** GET calls to one URL, so a new read-only fetch elsewhere does not move a count. */
function getsTo(mock: Mock, url: string): number {
  return mock.calls.filter((c) => c.method === 'GET' && c.url === url).length
}

/**
 * Lets real time pass, so a request the drawer would make later has the chance to
 * happen before a test counts. Proving that nothing more is requested needs a window.
 */
async function elapse(ms: number): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, ms))
  })
}

/** A retry fires after a short delay, so waits for it need more than waitFor's 1s default. */
const RETRY_WAIT = { timeout: 5000 }
/** Longer than the retry delay, so a further request (polling) would land inside the window. */
const SETTLE_MS = 2500

/**
 * One fetch mock for the drawer's whole surface. `status` may be a function so
 * a test can serve a different answer after a write; `post` answers each POST.
 */
function stubFetch(options: {
  status?: () => Reply
  post?: (call: Recorded) => Reply
  put?: (call: Recorded) => Reply
  version?: () => Reply
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
      else if (method === 'GET' && String(url) === '/api/kyber/version') reply = options.version?.() ?? { json: VERSION_JSON }
      else if (method === 'GET') reply = options.status?.() ?? { json: statusMixed }
      else if (method === 'POST') reply = options.post?.(call) ?? { status: 500, json: {} }
      else reply = options.put?.(call) ?? { json: { receiverHosted: true } }
      // A rejected fetch, as a dropped connection surfaces: no response at all.
      if (reply.networkError) throw new TypeError('Failed to fetch')
      if (reply.hold) await reply.hold
      const status = reply.status ?? 200
      // An explicit `json` (even `null`) is the body as sent; an absent one is an empty object.
      return { ok: status >= 200 && status < 300, status, json: async () => ('json' in reply ? reply.json : {}) } as Response
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

const enableButton = (id: string) => screen.getByTestId(`settings-harness-enable-${id}`) as HTMLButtonElement
const disableButton = (id: string) => screen.getByTestId(`settings-harness-disable-${id}`) as HTMLButtonElement
const enableQuery = (id: string) => screen.queryByTestId(`settings-harness-enable-${id}`) as HTMLButtonElement | null
const disableQuery = (id: string) => screen.queryByTestId(`settings-harness-disable-${id}`) as HTMLButtonElement | null
const resetButton = (id: string) => screen.getByTestId(`settings-harness-reset-${id}`) as HTMLButtonElement
const resetQuery = (id: string) => screen.queryByTestId(`settings-harness-reset-${id}`) as HTMLButtonElement | null

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

describe('SettingsDrawer: the configuration check', () => {
  const summary = () => screen.getByTestId('settings-check-summary')

  it('words the server verdict: harnesses needing attention and the issue count', async () => {
    stubFetch({})
    await openedDrawer()

    // Fixture check: attention, 2 harnesses, 3 issues.
    expect(summary().getAttribute('data-health')).toBe('attention')
    expect(summary().textContent).toContain('2 harnesses need attention (3 issues)')
  })

  it('uses the singular for one harness and one issue', async () => {
    const status = clone(statusMixed) as Json
    status.check = { ...status.check, attention: 1, issueCount: 1 }
    stubFetch({ status: () => ({ json: status }) })
    await openedDrawer()

    expect(summary().textContent).toContain('1 harness needs attention (1 issue)')
  })

  it('reports a clean check as no issues found', async () => {
    const status = clone(statusMixed) as Json
    status.check = { ...status.check, health: 'ok', enabled: 1, attention: 0, issueCount: 0 }
    stubFetch({ status: () => ({ json: status }) })
    await openedDrawer()

    expect(summary().getAttribute('data-health')).toBe('ok')
    expect(summary().textContent).toContain('1 enabled harness, no issues found')
  })

  it('reports that nothing is enabled when the check finds nothing to judge', async () => {
    const status = clone(statusMixed) as Json
    status.check = { ...status.check, health: 'off', enabled: 0, attention: 0, issueCount: 0 }
    stubFetch({ status: () => ({ json: status }) })
    await openedDrawer()

    expect(summary().textContent).toContain('No harness collection is enabled')
  })

  it('shows no summary line when an older server sends no check', async () => {
    const status = clone(statusMixed) as Json
    delete status.check
    stubFetch({ status: () => ({ json: status }) })
    await openedDrawer()

    expect(screen.queryByTestId('settings-check-summary')).toBeNull()
  })

  it('re-runs the check from the Check configuration button', async () => {
    const mock = stubFetch({})
    await openedDrawer()

    const button = screen.getByTestId('settings-refresh-status-button')
    expect(button.textContent).toBe('Check configuration')
    fireEvent.click(button)

    await waitFor(() => expect(getsTo(mock, '/api/kyber/capture')).toBe(2))
  })
})

describe('SettingsDrawer: rows come only from the server', () => {
  it('lists every harness; managed rows get action buttons, status-only rows do not', async () => {
    stubFetch({})
    await openedDrawer()

    // Fixture: claude-code is `on` (Disable + Reset), copilot is `partial`
    // (Enable + Disable), opencode is `off` (Enable).
    expect(disableButton('claude-code')).toBeDefined()
    expect(resetButton('claude-code')).toBeDefined()
    expect(enableButton('copilot')).toBeDefined()
    expect(disableButton('copilot')).toBeDefined()
    expect(enableButton('opencode')).toBeDefined()
    for (const id of ['codex', 'pi', 'antigravity']) {
      expect(screen.getByTestId(`settings-harness-row-${id}`)).toBeDefined()
      expect(enableQuery(id)).toBeNull()
      expect(disableQuery(id)).toBeNull()
      expect(resetQuery(id)).toBeNull()
    }
  })

  it('labels states Enabled, Disabled, Needs setup and Read-only — never as a button', async () => {
    stubFetch({})
    await openedDrawer()

    // Fixture: claude-code is `on` but its endpoint points elsewhere, copilot is `partial`,
    // opencode is `off`. An `on` row with issues is not a plain green "Enabled".
    expect(screen.getByTestId('settings-harness-state-claude-code').textContent).toBe('Enabled — needs attention')
    expect(screen.getByTestId('settings-harness-state-copilot').textContent).toBe('Needs setup')
    expect(screen.getByTestId('settings-harness-state-opencode').textContent).toBe('Disabled')
    expect(screen.getByTestId('settings-harness-state-codex').textContent).toBe('Read-only')
    expect(disableButton('claude-code').textContent).toBe('Disable')
    expect(enableButton('copilot').textContent).toBe('Enable')
    expect(disableButton('copilot').textContent).toBe('Disable')
    expect(enableButton('opencode').textContent).toBe('Enable')
    expect(screen.getByTestId('settings-harness-state-claude-code').tagName).not.toBe('BUTTON')
  })

  it('shows a partial row with Enable and Disable but no Reset', async () => {
    stubFetch({})
    await openedDrawer()

    expect(enableButton('copilot')).toBeDefined()
    expect(disableButton('copilot')).toBeDefined()
    expect(resetQuery('copilot')).toBeNull()
  })

  it('shows no action for a row whose state is unknown and says why', async () => {
    stubFetch({ status: () => ({ json: statusUnknown }) })
    await openedDrawer()

    expect(enableQuery('claude-code')).toBeNull()
    expect(disableQuery('claude-code')).toBeNull()
    expect(screen.getByTestId('settings-harness-reason-claude-code').textContent).toMatch(/unknown|could not/i)
    // The other managed row stays switchable.
    expect(enableButton('copilot').disabled).toBe(false)
  })

  it('shows no action for a not-detected harness that has no receipt, naming the reason', async () => {
    stubFetch({ status: () => ({ json: mixedWith('opencode', { installed: 'not-detected' }) }) })
    await openedDrawer()

    expect(enableQuery('opencode')).toBeNull()
    expect(disableQuery('opencode')).toBeNull()
    expect(screen.getByTestId('settings-harness-reason-opencode').textContent).toMatch(/not detected/i)
  })

  it('keeps a not-detected harness switchable off when KyberDash recorded a receipt', async () => {
    stubFetch({
      status: () => ({ json: mixedWith('opencode', { installed: 'not-detected', state: 'on', receipt: 'recorded' }) }),
    })
    await openedDrawer()

    expect(disableButton('opencode').disabled).toBe(false)
    expect(disableButton('opencode').textContent).toBe('Disable')
  })

  it('offers Disable and Reset for an on harness even with no receipt, through the engine-safe path', async () => {
    stubFetch({})
    await openedDrawer()

    // Fixture: claude-code is `on` with `receipt: absent`; the engine reports any refusal.
    expect(disableButton('claude-code').textContent).toBe('Disable')
    expect(disableButton('claude-code').disabled).toBe(false)
    expect(resetButton('claude-code')).toBeDefined()
  })

  it('shows Reset only for a fully-enabled (`on`) harness, never for off or partial', async () => {
    stubFetch({})
    await openedDrawer()

    // Fixture: claude-code is `on`, copilot is `partial`, opencode is `off`.
    expect(resetButton('claude-code')).toBeDefined()
    expect(resetQuery('copilot')).toBeNull()
    expect(resetQuery('opencode')).toBeNull()
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

    fireEvent.click(enableButton('claude-code'))

    const preview = await screen.findByTestId('settings-preview')
    expect(mock.posts()).toHaveLength(1)
    expect(mock.posts()[0]!.body).toEqual({ action: 'enable', harness: 'claude-code', dryRun: true })
    expect(preview.textContent).toContain('/Users/example/.claude/settings.json')
    expect(preview.textContent).toContain('env.OTEL_LOGS_EXPORTER')
    expect(preview.textContent).toContain('console')
    expect(preview.textContent).toContain('otlp')
    // Still unchanged until Confirm: the row shows server state only.
    expect(enableButton('claude-code').textContent).toBe('Enable')
  })

  it('names the receiver change in the preview when the route says it will enable it', async () => {
    const withReceiver = clone(postPreviewEnable) as Json
    withReceiver.receiver.willEnable = true
    withReceiver.receiver.hosted = false
    stubFetch({ status: offClaude, post: () => ({ json: withReceiver }) })
    await openedDrawer()

    fireEvent.click(enableButton('claude-code'))

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

    fireEvent.click(enableButton('claude-code'))

    const note = await screen.findByTestId('settings-preview-receiver-note')
    expect(note.textContent).toMatch(/already on/i)
    expect(note.textContent).not.toMatch(/currently off|will also turn on/i)
  })

  it('Cancel sends nothing more and dismisses the preview', async () => {
    const mock = stubFetch({ status: offClaude, post: () => ({ json: postPreviewEnable }) })
    await openedDrawer()
    fireEvent.click(enableButton('claude-code'))
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
    fireEvent.click(enableButton('copilot'))
    await screen.findByTestId('settings-preview')

    fireEvent.click(screen.getByTestId('settings-preview-confirm'))

    await waitFor(() => expect(mock.posts()).toHaveLength(2))
    expect(mock.posts()[1]!.body).toEqual({ action: 'enable', harness: 'copilot', dryRun: false })
    await waitFor(() => expect(getsTo(mock, '/api/kyber/capture')).toBe(2))
    await waitFor(() => expect(screen.queryByTestId('settings-preview')).toBeNull())
    expect(screen.getByTestId('settings-result').textContent).toMatch(/copilot/i)
  })

  it('an on row asks to disable with the Settings manual-key opt-in', async () => {
    const mock = stubFetch({
      status: () => ({ json: mixedWith('claude-code', { receipt: 'recorded' }) }),
      post: () => ({ json: postPreviewEnable }),
    })
    await openedDrawer()

    fireEvent.click(disableButton('claude-code'))

    await screen.findByTestId('settings-preview')
    expect(mock.posts()[0]!.body).toEqual({
      action: 'disable',
      harness: 'claude-code',
      dryRun: true,
      removeUnreceiptedKeys: true,
    })
  })

  it('a partial row shows Needs setup with Enable and no Reset', async () => {
    const mock = stubFetch({ post: () => ({ json: postPreviewEnable }) })
    await openedDrawer()

    expect(screen.getByTestId('settings-harness-state-copilot').textContent).toBe('Needs setup')
    expect(enableButton('copilot').textContent).toBe('Enable')
    expect(resetQuery('copilot')).toBeNull()

    fireEvent.click(enableButton('copilot'))

    await screen.findByTestId('settings-preview')
    expect(mock.posts()[0]!.body).toEqual({ action: 'enable', harness: 'copilot', dryRun: true })
  })

  it('shows a 409 drift refusal as an alert with the drifted keys, leaves the row unchanged and writes nothing', async () => {
    const mock = stubFetch({
      status: () => ({ json: mixedWith('claude-code', { receipt: 'recorded' }) }),
      post: () => ({ status: 409, json: postRefusedDrift }),
    })
    await openedDrawer()

    fireEvent.click(disableButton('claude-code'))

    const alert = await screen.findByTestId('settings-harness-alert-claude-code')
    expect(alert.getAttribute('role')).toBe('alert')
    expect(alert.textContent).toContain('disable refused: claude-code drifted since enable; no files were changed')
    expect(alert.textContent).toContain('env.OTEL_LOG_USER_PROMPTS')
    expect(alert.textContent).toContain('env.OTEL_LOG_RAW_API_BODIES')
    expect(screen.queryByTestId('settings-preview')).toBeNull()
    expect(disableButton('claude-code').textContent).toBe('Disable')
    expect(mock.posts()).toHaveLength(1)
  })

  it('shows a plain error, never the response body, when the request fails outright', async () => {
    stubFetch({ status: offClaude, post: () => ({ status: 500, json: { error: '/etc/secret/path leaked' } }) })
    await openedDrawer()

    fireEvent.click(enableButton('claude-code'))

    const alert = await screen.findByTestId('settings-harness-alert-claude-code')
    expect(alert.textContent).not.toContain('/etc/secret/path')
    expect(alert.textContent).toMatch(/could not/i)
  })

  it('blocks every button and a second click while a request is in flight', async () => {
    let release!: () => void
    const hold = new Promise<void>((resolve) => {
      release = resolve
    })
    const mock = stubFetch({
      status: offClaude,
      post: () => ({ json: postPreviewEnable, hold }),
    })
    await openedDrawer()

    fireEvent.click(enableButton('claude-code'))
    await waitFor(() => expect(enableButton('opencode').disabled).toBe(true))
    expect(enableButton('copilot').disabled).toBe(true)
    fireEvent.click(enableButton('opencode'))
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
    fireEvent.click(enableButton('claude-code'))
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
    fireEvent.click(disableButton('opencode'))
    await screen.findByTestId('settings-preview')

    fireEvent.click(screen.getByTestId('settings-preview-confirm'))

    const note = await screen.findByTestId('settings-receiver-note')
    expect(note.textContent).toMatch(/still hosted/i)
    expect(note.textContent).toMatch(/receiver section|switch it off/i)
  })

  it('a partial row Disable also carries the Settings manual-key opt-in, with Reset hidden', async () => {
    const mock = stubFetch({ post: () => ({ json: postPreviewEnable }) })
    await openedDrawer()

    expect(resetQuery('copilot')).toBeNull()
    fireEvent.click(disableButton('copilot'))

    await screen.findByTestId('settings-preview')
    expect(mock.posts()[0]!.body).toEqual({
      action: 'disable',
      harness: 'copilot',
      dryRun: true,
      removeUnreceiptedKeys: true,
    })
  })

  it('the confirmed Disable repeats the opt-in on the write', async () => {
    let expectedPreviewEvents: Json[] = []
    const mock = stubFetch({
      status: () => ({ json: mixedWith('opencode', { state: 'on', receipt: 'recorded' }) }),
      post: (call) => {
        if (call.body?.dryRun === true) {
          const preview = clone(postDisableLast) as Json
          preview.dryRun = true
          preview.outcome = 'previewed'
          preview.events[0].kind = 'would-restore'
          expectedPreviewEvents = preview.events
          return { json: preview }
        }
        return { json: postDisableLast }
      },
    })
    await openedDrawer()
    fireEvent.click(disableButton('opencode'))
    await screen.findByTestId('settings-preview')

    fireEvent.click(screen.getByTestId('settings-preview-confirm'))

    await waitFor(() => expect(mock.posts()).toHaveLength(2))
    expect(mock.posts()[1]!.body).toEqual({
      action: 'disable',
      harness: 'opencode',
      dryRun: false,
      removeUnreceiptedKeys: true,
      expectedPreviewEvents,
    })
  })
})

describe('SettingsDrawer: reset', () => {
  it('shows Reset only for a managed on row; partial and off rows have none', async () => {
    stubFetch({})
    await openedDrawer()

    // Fixture: claude-code is `on`, copilot is `partial`, opencode is `off`.
    expect(resetQuery('claude-code')).not.toBeNull()
    expect(resetQuery('copilot')).toBeNull()
    expect(resetQuery('opencode')).toBeNull()
    expect(resetQuery('codex')).toBeNull()
  })

  it('shows Reset for an on row without a receipt and re-runs enable, previewed first', async () => {
    // Fixture default: claude-code is `on` with `receipt: absent`.
    const mock = stubFetch({ post: () => ({ json: postPreviewEnable }) })
    await openedDrawer()

    fireEvent.click(screen.getByTestId('settings-harness-reset-claude-code'))

    const preview = await screen.findByTestId('settings-preview')
    expect(mock.posts()).toHaveLength(1)
    expect(mock.posts()[0]!.body).toEqual({ action: 'enable', harness: 'claude-code', dryRun: true })
    expect(preview.textContent).toContain('Reset collection for Claude Code')
  })

  it('Confirm on a reset reapplies through the enable path and says reapplied', async () => {
    let written = false
    const mock = stubFetch({
      status: () => ({ json: statusMixed }),
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

    fireEvent.click(screen.getByTestId('settings-harness-reset-claude-code'))
    await screen.findByTestId('settings-preview')
    fireEvent.click(screen.getByTestId('settings-preview-confirm'))

    await waitFor(() => expect(mock.posts()).toHaveLength(2))
    expect(mock.posts()[1]!.body).toEqual({ action: 'enable', harness: 'claude-code', dryRun: false })
    expect(written).toBe(true)
    await waitFor(() => expect(screen.getByTestId('settings-result').textContent).toMatch(/reapplied/i))
  })

  it('a reset refusal surfaces as an alert and never reports success', async () => {
    stubFetch({
      status: () => ({ json: mixedWith('claude-code', { receipt: 'recorded' }) }),
      post: () => ({ status: 409, json: postRefusedDrift }),
    })
    await openedDrawer()

    fireEvent.click(screen.getByTestId('settings-harness-reset-claude-code'))

    const alert = await screen.findByTestId('settings-harness-alert-claude-code')
    expect(alert.textContent).toContain('drifted since enable')
    expect(screen.queryByTestId('settings-preview')).toBeNull()
    expect(screen.queryByTestId('settings-result')).toBeNull()
  })
})

describe('SettingsDrawer: collapse', () => {
  it('renders the harness collection as an open disclosure with an enabled count', async () => {
    stubFetch({})
    await openedDrawer()

    const group = screen.getByTestId('settings-harness-group')
    expect(group.tagName).toBe('DETAILS')
    expect(group.hasAttribute('open')).toBe(true)
    // Fixture: exactly one managed harness is `on`.
    expect(screen.getByTestId('settings-harness-enabled-count').textContent).toBe('1 enabled')
  })

  it('tucks paths, warnings and manual steps inside per-row configuration details', async () => {
    stubFetch({})
    await openedDrawer()

    for (const id of ['claude-code', 'copilot', 'opencode']) {
      const details = screen.getByTestId(`settings-harness-details-${id}`)
      expect(details.tagName).toBe('DETAILS')
      expect(details.hasAttribute('open')).toBe(false)
      expect(details.textContent).toContain('Configuration details')
    }
    // Compact rows still show the primary actions: Reset and Disable for the
    // `on` row, Enable for the `off` row.
    expect(disableButton('claude-code').textContent).toBe('Disable')
    expect(resetQuery('claude-code')).not.toBeNull()
    expect(enableButton('opencode').textContent).toBe('Enable')
  })

  it('keeps the dashboard port control visible beside the collection group', async () => {
    stubFetch({})
    await openedDrawer()

    expect(screen.getByTestId('dashboard-port-setting')).toBeDefined()
    expect(screen.getByTestId('dashboard-port-input')).toBeDefined()
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

describe('SettingsDrawer: version line', () => {
  const versionLine = () => screen.getByTestId('settings-kyberdash-version')

  it('shows the KyberDash version the server reports, inside the dialog', async () => {
    stubFetch({ version: () => ({ json: VERSION_JSON }) })
    await openedDrawer()

    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version 9.8.7-rc.1'))
    expect(screen.getByRole('dialog').contains(versionLine())).toBe(true)
  })

  it('shows unknown, never a placeholder version, when the version request fails with a 500', async () => {
    stubFetch({ version: () => ({ status: 500, json: { error: 'Version failed' } }) })
    await openedDrawer()

    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version unknown'))
    expect(versionLine().textContent).not.toContain('0.0.0')
  })

  it('shows unknown, never a placeholder version, when the version request is rejected', async () => {
    stubFetch({ version: () => ({ networkError: true }) })
    await openedDrawer()

    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version unknown'))
    expect(versionLine().textContent).not.toContain('0.0.0')
  })

  it.each([
    ['an empty object', {}],
    ['null', null],
  ])('shows unknown, never undefined, null, or a placeholder version, when the version body is %s', async (_label, body) => {
    stubFetch({ version: () => ({ json: body }) })
    await openedDrawer()

    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version unknown'))
    expect(versionLine().textContent).not.toMatch(/undefined|null|0\.0\.0/)
  })

  it.each([
    ['a whitespace-only version', { version: '   ', apiVersion: 1 }],
    ['the 0.0.0 placeholder version', { version: '0.0.0', apiVersion: 1 }],
    ['the 0.0.0 placeholder padded with spaces', { version: ' 0.0.0 ', apiVersion: 1 }],
    ['the 0.0.0 placeholder with a trailing space', { version: '0.0.0 ', apiVersion: 1 }],
  ])('shows unknown, never a blank or placeholder version, when the engine reports %s', async (_label, body) => {
    stubFetch({ version: () => ({ json: body }) })
    await openedDrawer()

    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version unknown'))
    expect(versionLine().textContent).not.toMatch(/0\.0\.0/)
  })

  it('shows a real version trimmed, with no padding inside the line, when the engine pads it with spaces', async () => {
    stubFetch({ version: () => ({ json: { version: ' 1.2.3 ', apiVersion: 1 } }) })
    await openedDrawer()

    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version 1.2.3'))
  })

  it('requests the version once when the first request succeeds, and a status refresh does not repeat it', async () => {
    const mock = stubFetch({ version: () => ({ json: VERSION_JSON }) })
    await openedDrawer()

    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version 9.8.7-rc.1'))
    expect(getsTo(mock, '/api/kyber/version')).toBe(1)

    fireEvent.click(screen.getByTestId('settings-refresh-status-button'))

    await waitFor(() => expect(getsTo(mock, '/api/kyber/capture')).toBe(2))
    expect(getsTo(mock, '/api/kyber/version')).toBe(1)
  })

  // The engine can be restarting while the drawer opens. One retry lets the version
  // recover; a second consecutive failure is final, with no polling behind it.
  it('retries once after a transient failure and shows the version the retry returns', async () => {
    let attempt = 0
    const mock = stubFetch({
      version: () => (++attempt === 1 ? { status: 500, json: { error: 'Version failed' } } : { json: VERSION_JSON }),
    })
    await openedDrawer()

    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version 9.8.7-rc.1'), RETRY_WAIT)
    expect(getsTo(mock, '/api/kyber/version')).toBe(2)
  })

  it('shows unknown after two consecutive failures, and makes no further request', async () => {
    const mock = stubFetch({ version: () => ({ status: 500, json: { error: 'Version failed' } }) })
    await openedDrawer()

    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version unknown'), RETRY_WAIT)
    expect(getsTo(mock, '/api/kyber/version')).toBe(2)

    await elapse(SETTLE_MS)

    expect(versionLine().textContent).toBe('KyberDash version unknown')
    expect(getsTo(mock, '/api/kyber/version')).toBe(2)
  })

  it('fetches the version again when the connection comes back after the failures', async () => {
    let engineUp = false
    const mock = stubFetch({
      version: () => (engineUp ? { json: VERSION_JSON } : { status: 500, json: { error: 'Version failed' } }),
    })
    await openedDrawer()
    await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version unknown'), RETRY_WAIT)
    const before = getsTo(mock, '/api/kyber/version')

    engineUp = true
    try {
      act(() => {
        window.dispatchEvent(new Event('offline'))
      })
      act(() => {
        window.dispatchEvent(new Event('online'))
      })
      await waitFor(() => expect(versionLine().textContent).toBe('KyberDash version 9.8.7-rc.1'), RETRY_WAIT)
    } finally {
      act(() => {
        window.dispatchEvent(new Event('online'))
      })
    }
    expect(getsTo(mock, '/api/kyber/version')).toBe(before + 1)
  })
})
