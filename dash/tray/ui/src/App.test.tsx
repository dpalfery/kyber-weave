// @vitest-environment jsdom

import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { invoke } from '@tauri-apps/api/core'
import { listen } from '@tauri-apps/api/event'

import full from '../../../src/analysis/report/fixtures/full.json' with { type: 'json' }
import type { ContextReport } from '../../../src/analysis/report/types.ts'

import { App } from './App'
import type { ViewState } from './viewState'

vi.mock('@tauri-apps/api/core', () => ({ invoke: vi.fn() }))
vi.mock('@tauri-apps/api/event', () => ({ listen: vi.fn() }))

;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const REPORT = full as unknown as ContextReport
const invokeMock = vi.mocked(invoke)
const listenMock = vi.mocked(listen)

type ViewStateEvent = { payload: ViewState }
type ViewStateListener = (event: ViewStateEvent) => void

let container: HTMLDivElement
let root: Root
let stateListener: ViewStateListener | undefined
let unlisten: ReturnType<typeof vi.fn> & (() => void)

/**
 * The static shell remains useful as a contract for the first paint. The
 * effectful tests below cover the runtime behavior that static markup cannot
 * reach.
 */
describe('App', () => {
  beforeEach(() => {
    container = document.createElement('div')
    document.body.appendChild(container)
    stateListener = undefined
    unlisten = vi.fn() as ReturnType<typeof vi.fn> & (() => void)
    invokeMock.mockReset()
    listenMock.mockReset()
    listenMock.mockImplementation(async (_name, handler) => {
      stateListener = handler as ViewStateListener
      return unlisten
    })
  })

  afterEach(async () => {
    if (root !== undefined) {
      await act(async () => {
        root.unmount()
        await Promise.resolve()
      })
    }
    container.remove()
  })

  it('shows a loading view until the first ViewState arrives', () => {
    const html = renderToStaticMarkup(<App />)

    expect(html).toContain('data-testid="popover-loading"')
    // Not an empty frame: a popover that opened before the first state should
    // say why it is blank rather than look broken.
    expect(html).toContain('Reading the store…')
  })

  it('turns an initial IPC rejection into a visible retry error', async () => {
    invokeMock.mockImplementationOnce(() => rejectedInvoke(new Error('IPC unavailable')))

    await mountApp()

    const error = container.querySelector('[data-testid="ipc-error"]')
    expect(error).not.toBeNull()
    expect(error?.textContent ?? '').toContain('IPC unavailable')
    expect(container.querySelector('[data-testid="retry-load"]')).not.toBeNull()
  })

  it('shows the popover after retrying a rejected initial load successfully', async () => {
    invokeMock
      .mockImplementationOnce(() => rejectedInvoke(new Error('IPC unavailable')))
      .mockResolvedValueOnce(viewState())

    await mountApp()
    const retry = container.querySelector<HTMLButtonElement>('[data-testid="retry-load"]')
    expect(retry).not.toBeNull()

    await act(async () => {
      retry?.click()
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(container.querySelector('[data-testid="popover"]')).not.toBeNull()
  })

  it('makes a rejected refresh command visible in the open popover', async () => {
    invokeMock.mockImplementation((command) => {
      if (command === 'get_view_state') return Promise.resolve(viewState())
      return rejectedInvoke(new Error('Refresh unavailable'))
    })

    await mountApp()
    const refresh = container.querySelector<HTMLButtonElement>('[data-testid="refresh-now"]')
    expect(refresh).not.toBeNull()

    await act(async () => {
      refresh?.click()
      await Promise.resolve()
      await Promise.resolve()
    })

    const error = container.querySelector('[data-testid="ipc-action-error"]')
    expect(error).not.toBeNull()
    expect(error?.textContent ?? '').toContain('Refresh unavailable')
  })

  it('updates an open popover when the tray emits a new ViewState', async () => {
    invokeMock.mockResolvedValue(viewState())

    await mountApp()
    expect(container.querySelector('[data-testid="popover"]')?.getAttribute('data-phase')).toBe(
      'ready',
    )
    expect(stateListener).toBeDefined()

    await act(async () => {
      stateListener!({
        payload: viewState({ phase: 'stale', error: 'refresh failed' }),
      })
      await Promise.resolve()
    })

    expect(container.querySelector('[data-testid="popover"]')?.getAttribute('data-phase')).toBe(
      'stale',
    )
    expect(container.querySelector('[data-testid="stale-banner"]')?.textContent).toContain(
      'refresh failed',
    )
  })

  it('unlistens from ViewState events when the app unmounts', async () => {
    invokeMock.mockResolvedValue(viewState())

    await mountApp()
    await act(async () => {
      root.unmount()
      await Promise.resolve()
    })

    expect(unlisten).toHaveBeenCalledTimes(1)
  })
})

function viewState(overrides: Partial<ViewState> = {}): ViewState {
  return {
    phase: 'ready',
    report: REPORT,
    reportFetchedAt: '2026-09-20T12:00:00.000Z',
    error: null,
    refresh: { state: 'idle', lastSuccessAt: '2026-09-20T11:00:00.000Z', lastFailure: null },
    receiver: 'reachable',
    settings: {
      harness: 'all',
      windowDays: 7,
      refreshMinutes: 5,
      attentionThreshold: 0.7,
      criticalThreshold: 0.9,
      launchAtLogin: false,
      hostReceiver: false,
    },
    ...overrides,
  }
}

async function mountApp(): Promise<void> {
  await act(async () => {
    root = createRoot(container)
    root.render(<App />)
    await Promise.resolve()
    await Promise.resolve()
  })
}

/**
 * Preserve a real rejected promise for the component while consuming the
 * unhandled chain created by the pre-fix `void promise.then(...)` code.
 */
function rejectedInvoke<T>(error: Error): Promise<T> {
  const promise = Promise.reject<T>(error)
  const originalThen = promise.then.bind(promise)
  void originalThen(undefined, () => undefined)
  promise.then = ((onFulfilled, onRejected) => {
    const chained = originalThen(onFulfilled, onRejected)
    void chained.catch(() => undefined)
    return chained
  }) as typeof promise.then
  return promise
}
