// @vitest-environment happy-dom
// Quarantine/Problems: the shell's own harness scope filters both lists (no
// duplicate selectors); every row is keyboard-openable into a read-only
// detail inspector; old rows without stored evidence say so plainly; API
// failures render a bounded unavailable notice. Fetches are stubbed.

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render as renderDom, screen, fireEvent, cleanup, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { quarantineInScope } from './QuarantineView.js'
import { problemsInScope } from './ProblemsView.js'
import { QuarantineView } from './QuarantineView.js'
import { ProblemsView } from './ProblemsView.js'

function queryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('harness scope predicates (shared selector, All + Unknown, no duplicates)', () => {
  it('quarantine: all passes everything, unknown keeps only unattributed rows', () => {
    const rows = [
      { spanId: 'a', namespaces: ['x'], reason: 'r', source: 'opencode' },
      { spanId: 'b', namespaces: ['x'], reason: 'r', source: null },
      { spanId: 'c', namespaces: ['x'], reason: 'r', source: 'copilot' },
    ]
    expect(quarantineInScope(rows, 'all')).toHaveLength(3)
    expect(quarantineInScope(rows, undefined)).toHaveLength(3)
    expect(quarantineInScope(rows, 'unknown').map((r) => r.spanId)).toEqual(['b'])
    expect(quarantineInScope(rows, 'OpenCode').map((r) => r.spanId)).toEqual(['a'])
  })

  it('problems: unknown keeps only rows with no harness', () => {
    const rows = [
      { severity: 'error' as const, code: 'E1', message: 'm', harness: 'opencode' },
      { severity: 'warning' as const, code: 'W1', message: 'm', harness: null },
    ]
    expect(problemsInScope(rows, 'all')).toHaveLength(2)
    expect(problemsInScope(rows, 'unknown').map((r) => r.code)).toEqual(['W1'])
    expect(problemsInScope(rows, 'opencode').map((r) => r.code)).toEqual(['E1'])
  })

  it('never labels opencode rows as copilot', () => {
    const rows = [{ spanId: 'a', namespaces: ['h'], reason: 'health check', source: 'opencode' }]
    expect(quarantineInScope(rows, 'copilot')).toHaveLength(0)
    expect(quarantineInScope(rows, 'opencode')).toHaveLength(1)
  })
})

describe('row navigation opens a read-only detail inspector', () => {
  it('every quarantine row has an accessible open action and shows unavailable evidence plainly', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({ ok: false, status: 404, json: async () => ({ error: 'Not found' }) }) as Response),
    )
    renderDom(
      <QueryClientProvider client={queryClient()}>
        <QuarantineView entries={[{ spanId: 'span-1', namespaces: ['http.request'], reason: 'unmapped', source: 'opencode' }]} />
      </QueryClientProvider>,
    )
    const button = await screen.findByTestId('quarantine-row-button')
    expect(button.getAttribute('aria-label')).toMatch(/span-1/)
    fireEvent.click(button)
    const detail = await screen.findByTestId('quarantine-detail')
    expect(detail.getAttribute('aria-label')).toMatch(/span-1/)
    // List-row reason is shown; missing stored payload is stated, not invented.
    expect(within(detail).getByTestId('quarantine-detail-reason').textContent).toContain('unmapped')
    expect(await within(detail).findByTestId('quarantine-detail-unavailable')).toBeDefined()
    expect(screen.queryByTestId('quarantine-detail-payload')).toBeNull()
  })

  it('quarantine detail renders the stored payload when the server returns one', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({
        ok: true,
        status: 200,
        json: async () => ({ span_id: 'span-9', reason: 'unmapped', namespaces: ['a'], payload: { hello: 'world' } }),
      }) as Response),
    )
    renderDom(
      <QueryClientProvider client={queryClient()}>
        <QuarantineView entries={[{ spanId: 'span-9', namespaces: ['a'], reason: 'unmapped', source: 'opencode' }]} />
      </QueryClientProvider>,
    )
    fireEvent.click(await screen.findByTestId('quarantine-row-button'))
    const payload = await screen.findByTestId('quarantine-detail-payload')
    expect(payload.textContent).toContain('hello')
  })

  it('every problem row opens detail showing code/message and stating missing stack plainly', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({ ok: false, status: 404, json: async () => ({ error: 'Not found' }) }) as Response),
    )
    renderDom(
      <QueryClientProvider client={queryClient()}>
        <ProblemsView problems={[{ id: 'p-1', severity: 'error', code: 'E_OTLP', message: 'connection refused' }]} />
      </QueryClientProvider>,
    )
    fireEvent.click(await screen.findByTestId('problem-row-button'))
    const detail = await screen.findByTestId('problem-detail')
    expect(within(detail).getByTestId('problem-detail-message').textContent).toContain('connection refused')
    expect(await within(detail).findByTestId('problem-detail-unavailable')).toBeDefined()
    expect(screen.queryByTestId('problem-detail-stack')).toBeNull()
  })
})
