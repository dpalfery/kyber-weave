// @vitest-environment happy-dom
// The skills section: one page of the session's skill rows. Measured rows
// render through the existing skills panel; a whole-block not-measurable
// verdict renders as such. A skill row click opens the inspector with the
// lossless turn content of the first turn that listed the skill.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactElement } from 'react'

import { SessionSkillsSection } from './SessionSkillsSection.js'
import type { OpenInspectorRequest } from './types.js'

const SESSION_ID = 'sess-abc-123'

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

function jsonResponse(body: unknown, status = 200): Response {
  return { ok: status >= 200 && status < 300, status, json: async () => body } as unknown as Response
}

function stubFetch(handler: () => Response): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(() => Promise.resolve(handler()))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function mount(openInspector: (request: OpenInspectorRequest) => void): ReturnType<typeof render> {
  const element = (
    <QueryClientProvider client={queryClient()}>
      <SessionSkillsSection sessionId={SESSION_ID} summaryRecord={null} openInspector={openInspector} />
    </QueryClientProvider>
  ) as ReactElement
  return render(element)
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const SKILL_ROW = {
  name: 'docs',
  listed: true,
  invocations: 2,
  turns_listed: [0, 1],
  derived_tokens: 38,
  description: 'Write the docs',
}

const SKILLS_PAGE = {
  measurable: true,
  rows: [SKILL_ROW],
  total: 1,
  limit: 25,
  offset: 0,
  page: 1,
}

describe('SessionSkillsSection states', () => {
  it('shows its loading state while the page is in flight', () => {
    stubFetch(() => jsonResponse(SKILLS_PAGE))
    mount(() => {})
    const loading = screen.getByTestId('session-skills-loading')
    expect(loading.getAttribute('role')).toBe('status')
    expect(loading.textContent).toMatch(/loading/i)
  })

  it('states the failure plainly and retries on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({}, 500))
    mount(() => {})
    const error = await screen.findByTestId('section-error-skills')
    expect(error.textContent).toContain('Request failed (500)')
    fireEvent.click(within(error).getByRole('button', { name: 'Retry' }))
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(2)
  })

  it('renders the measured rows through the skills panel and numbers the section 4', async () => {
    stubFetch(() => jsonResponse(SKILLS_PAGE))
    mount(() => {})
    const section = await screen.findByTestId('skills-section')
    expect(within(section).getByTestId('skills-panel')).toBeTruthy()
    expect(within(section).getByTestId('skill-row-docs')).toBeTruthy()
    expect(section.textContent).toContain('4 · Skills')
  })

  it('states the whole-block not-measurable verdict with its reason', async () => {
    const reason = 'Skill listings were not exported; only invoked skills are shown.'
    stubFetch(() =>
      jsonResponse({ availability: 'not_measurable', reason, rows: [], total: 0, limit: 25, offset: 0, page: 1 }),
    )
    mount(() => {})
    const section = await screen.findByTestId('skills-section')
    const banner = within(section).getByTestId('skills-not-measurable')
    expect(banner.textContent).toContain(reason)
    expect(banner.textContent).toMatch(/not measurable/i)
  })

  it('states the served headline pair honestly when the section carries none', async () => {
    stubFetch(() => jsonResponse(SKILLS_PAGE))
    mount(() => {})
    const section = await screen.findByTestId('skills-section')
    const headline = within(section).getByTestId('skills-headline')
    expect(headline.textContent).toContain('not measurable')
    expect(headline.textContent).not.toMatch(/\d+ of \d+ listed skills/)
  })
})

describe('SessionSkillsSection inspector wiring', () => {
  it('opens the lossless turn content for the first turn that listed the skill', async () => {
    stubFetch(() => jsonResponse(SKILLS_PAGE))
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('skills-section')
    fireEvent.click(within(section).getByTestId('skill-row-docs'))
    expect(openInspector).toHaveBeenCalledTimes(1)
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.title).toBe('Skill: docs')
    expect(request.contentRequest).toEqual({ sessionId: SESSION_ID, turnIndex: 0, full: true })
  })

  it('issues no content request for a skill no turn listed', async () => {
    stubFetch(() =>
      jsonResponse({
        measurable: true,
        rows: [{ ...SKILL_ROW, name: 'unlisted', turns_listed: [] }],
        total: 1,
        limit: 25,
        offset: 0,
        page: 1,
      }),
    )
    const openInspector = vi.fn()
    mount(openInspector)
    const section = await screen.findByTestId('skills-section')
    fireEvent.click(within(section).getByTestId('skill-row-unlisted'))
    const request = openInspector.mock.calls[0][0] as OpenInspectorRequest
    expect(request.contentRequest).toBeUndefined()
    expect(request.title).toBe('Skill: unlisted')
  })

  it('pages the skill list at 25 and fetches page two only on press', async () => {
    const fetchMock = stubFetch(() => jsonResponse({ ...SKILLS_PAGE, total: 30 }))
    mount(() => {})
    const section = await screen.findByTestId('skills-section')
    const pager = within(section).getByTestId('list-pager')
    fireEvent.click(within(pager).getByTestId('pager-next'))
    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some((call) => String(call[0]).includes('skills?page=2&limit=25')),
      ).toBe(true),
    )
  })
})
