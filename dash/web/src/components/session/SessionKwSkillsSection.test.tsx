// @vitest-environment happy-dom
// The KW skills section: one page of the session's KW skill loads, under its
// own heading, apart from the Skills section. Each data state renders its own
// message; a row without a skill name says so rather than showing a blank.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { SessionKwSkillsSection } from './SessionKwSkillsSection.js'

const SESSION_ID = 'sess-abc-123'

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function jsonResponse(body: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => body } as unknown as Response
}

function stubFetch(handler: (url: string) => Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn((url: string) => Promise.resolve(handler(url)))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function mount(): ReturnType<typeof render> {
  return render(
    <QueryClientProvider client={queryClient()}>
      <SessionKwSkillsSection sessionId={SESSION_ID} />
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  window.sessionStorage.clear()
  vi.unstubAllGlobals()
})

const KW_ROW = {
  skill: 'kw-docs',
  agent: 'claude',
  session_id: SESSION_ID,
  span_id: 'span-1',
  loaded_at: '2026-11-19T10:05:00.000Z',
}

const KW_PAGE = {
  rows: [KW_ROW],
  total: 1,
  limit: 25,
  offset: 0,
  page: 1,
}

describe('SessionKwSkillsSection states', () => {
  it('shows its loading state while the page is in flight', () => {
    stubFetch(() => jsonResponse(KW_PAGE))
    mount()
    const loading = screen.getByTestId('session-kw-skills-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount()
    const error = await screen.findByTestId('section-error-kw-skills')
    expect(error.textContent).toContain('KW skills could not be loaded.')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    await waitFor(() => expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2))
  })

  it('states the empty page in words, with no rows', async () => {
    stubFetch(() => jsonResponse({ rows: [], total: 0, limit: 25, offset: 0, page: 1 }))
    mount()
    const section = await screen.findByTestId('kw-skills-section')
    expect(within(section).getByTestId('kw-skills-empty').textContent).toBe(
      'No KW skills were loaded in this session.',
    )
    expect(within(section).queryByTestId('kw-skill-row-1')).toBeNull()
  })

  it('renders each row with skill, agent, session and time columns', async () => {
    stubFetch(() => jsonResponse(KW_PAGE))
    mount()
    const section = await screen.findByTestId('kw-skills-section')
    const row = within(section).getByTestId('kw-skill-row-1')
    expect(row.textContent).toContain('kw-docs')
    expect(row.textContent).toContain('claude')
    expect(row.textContent).toContain(SESSION_ID)
    expect(row.textContent).toContain('2026-11-19T10:05:00.000Z')
  })

  it('says a skill was not recorded when the row carries no name', async () => {
    stubFetch(() =>
      jsonResponse({
        ...KW_PAGE,
        rows: [{ ...KW_ROW, skill: null, agent: null, loaded_at: null }],
      }),
    )
    mount()
    const section = await screen.findByTestId('kw-skills-section')
    const row = within(section).getByTestId('kw-skill-row-1')
    expect(row.textContent).toContain('not recorded')
    expect(row.textContent).not.toContain('null')
  })

  it('says a skill was not recorded when the row carries an empty name', async () => {
    stubFetch(() =>
      jsonResponse({
        ...KW_PAGE,
        rows: [{ ...KW_ROW, skill: '', agent: '', loaded_at: '' }],
      }),
    )
    mount()
    const section = await screen.findByTestId('kw-skills-section')
    const row = within(section).getByTestId('kw-skill-row-1')
    expect(row.textContent).toContain('not recorded')
    expect(row.textContent).not.toContain('null')
  })

  it('heads the section KW skills, with no section number', async () => {
    stubFetch(() => jsonResponse(KW_PAGE))
    mount()
    const section = await screen.findByTestId('kw-skills-section')
    const heading = within(section).getByRole('heading', { name: 'KW skills' })
    expect(heading.textContent).toBe('KW skills')
  })
})

describe('SessionKwSkillsSection paging', () => {
  it('shows the shared pager past 25 rows and requests page two only on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({ ...KW_PAGE, total: 30 }))
    mount()
    const section = await screen.findByTestId('kw-skills-section')
    const pager = within(section).getByTestId('list-pager')
    expect(pager.getAttribute('aria-label')).toBe('KW skill pages')
    expect(fetchMock.mock.calls.some((call) => String(call[0]).includes('page=2'))).toBe(false)

    fireEvent.click(within(pager).getByTestId('pager-next'))
    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some((call) =>
          String(call[0]).includes(`/api/kyber/session/${SESSION_ID}/kw-skills?page=2&limit=25&order=desc`),
        ),
      ).toBe(true),
    )
  })

  it('shows limit and order controls on a single non-empty page and hides the pager when total is 0', async () => {
    stubFetch(() => jsonResponse(KW_PAGE))
    const { unmount } = mount()
    const section = await screen.findByTestId('kw-skills-section')
    const pager = within(section).getByTestId('list-pager')
    expect(within(pager).getByTestId('pager-limit')).toBeTruthy()
    expect(within(pager).getByTestId('pager-order').textContent).toBe('Newest first')
    expect(within(pager).queryByTestId('pager-prev')).toBeNull()
    expect(within(pager).queryByTestId('pager-next')).toBeNull()
    unmount()

    stubFetch(() => jsonResponse({ rows: [], total: 0, limit: 25, offset: 0, page: 1 }))
    mount()
    const emptySection = await screen.findByTestId('kw-skills-section')
    expect(within(emptySection).queryByTestId('list-pager')).toBeNull()
  })

  it('changes page size (25 -> 50 -> 100), persists per section, toggles sort order, and resets to page 1', async () => {
    const fetchMock = stubFetch((url) => {
      const parsed = new URL(url, 'http://localhost')
      const page = Number(parsed.searchParams.get('page') ?? '1')
      const limit = Number(parsed.searchParams.get('limit') ?? '25')
      const order = parsed.searchParams.get('order') ?? 'desc'
      return jsonResponse({ ...KW_PAGE, total: 120, page, limit, order })
    })
    mount()
    const section = await screen.findByTestId('kw-skills-section')

    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('kw-skills?page=2&limit=25&order=desc'))).toBe(true),
    )

    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '50' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('kw-skills?page=1&limit=50&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:kw-skills')).toBe('50')

    fireEvent.change(within(section).getByTestId('pager-limit'), { target: { value: '100' } })
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('kw-skills?page=1&limit=100&order=desc'))).toBe(true),
    )
    expect(window.sessionStorage.getItem('kyberdash:session-section:limit:kw-skills')).toBe('100')

    fireEvent.click(within(section).getByTestId('pager-next'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('kw-skills?page=2&limit=100&order=desc'))).toBe(true),
    )

    fireEvent.click(within(section).getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url]) => String(url).includes('kw-skills?page=1&limit=100&order=asc'))).toBe(true),
    )
    expect(within(section).getByTestId('pager-order').textContent).toBe('Oldest first')

    fireEvent.click(within(section).getByTestId('pager-order'))
    await waitFor(() =>
      expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('kw-skills?page=1&limit=100&order=desc')).length).toBeGreaterThanOrEqual(2),
    )
  })
})
