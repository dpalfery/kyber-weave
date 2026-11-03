// @vitest-environment happy-dom
// The static App suite cannot click (it renders to markup under a stub hook
// dispatcher), so the open/close wiring of the header Settings button is
// exercised here against the real DOM. Fetch is mocked: the shell's own reads
// get empty answers and the capture route gets the shared T0 status fixture.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import statusMixed from '../../src/server/fixtures/capture-api/status-mixed.json'
import { App } from './App.js'

function stubFetch(): { urls: string[] } {
  const urls: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      urls.push(String(url))
      const body = String(url).startsWith('/api/kyber/capture') ? statusMixed : []
      return { ok: true, status: 200, json: async () => body } as Response
    }),
  )
  return { urls }
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function renderApp() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <App initialPage="quarantine" />
    </QueryClientProvider>,
  )
}

describe('App: Settings drawer wiring', () => {
  it('opens the drawer from the header button, fetches the capture status once, and reflects aria-expanded', async () => {
    const { urls } = stubFetch()
    renderApp()
    const button = screen.getByTestId('header-settings-button')
    expect(button.getAttribute('aria-expanded')).toBe('false')
    expect(urls.filter((u) => u.startsWith('/api/kyber/capture'))).toHaveLength(0)

    button.focus()
    fireEvent.click(button)

    expect(await screen.findByRole('dialog', { name: 'Settings' })).toBeDefined()
    expect(button.getAttribute('aria-expanded')).toBe('true')
    await screen.findByTestId('settings-harness-row-opencode')
    expect(urls.filter((u) => u === '/api/kyber/capture')).toHaveLength(1)
  })

  it('closes on Escape, collapses aria-expanded and returns focus to the button', async () => {
    stubFetch()
    renderApp()
    const button = screen.getByTestId('header-settings-button')
    button.focus()
    fireEvent.click(button)
    await screen.findByRole('dialog')

    fireEvent.keyDown(window, { key: 'Escape' })

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(button.getAttribute('aria-expanded')).toBe('false')
    expect(document.activeElement).toBe(button)
  })
})
