// @vitest-environment happy-dom
// The shared toolbar's search counts text inside the rendered body through the
// DOM and switches views on real events, so this suite mounts it under happy-dom.
import { describe, it, expect, vi, afterEach } from 'vitest'
import { useState } from 'react'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'

import { SearchableContentView, type ContentViewMode } from './ContentViewToolbar.js'

afterEach(cleanup)

const RAW = 'alpha one alpha two alpha three'

/**
 * A stateful host. The formatted body prints only two of the three raw
 * occurrences, so the match count proves which view the search scanned.
 */
function Host({ controls, withLeading = false }: { controls?: 'full' | 'minimal'; withLeading?: boolean }) {
  const [view, setView] = useState<ContentViewMode>('formatted')
  return (
    <SearchableContentView
      view={view}
      onViewChange={setView}
      text={RAW}
      label="Probe"
      viewPrefix="probe"
      controls={controls}
      leading={withLeading ? <span data-testid="probe-leading">Probe · 31 chars</span> : undefined}
      formatted={
        <div>
          <p>alpha one</p>
          <p>alpha two</p>
        </div>
      }
      raw={<pre>{RAW}</pre>}
    />
  )
}

function typeQuery(value: string) {
  fireEvent.change(screen.getByTestId('probe-search'), { target: { value } })
}

describe('ContentViewToolbar: shared Formatted/Raw toolbar', () => {
  it('counts matches in the formatted view, then recounts when Raw is shown', async () => {
    render(<Host />)
    typeQuery('alpha')
    await waitFor(() => expect(screen.getByTestId('probe-search-count').textContent).toBe('1/2'))

    fireEvent.click(screen.getByTestId('probe-view-raw'))
    await waitFor(() => expect(screen.getByTestId('probe-search-count').textContent).toBe('1/3'))
    expect(screen.getByRole('button', { name: 'Raw' }).getAttribute('aria-pressed')).toBe('true')
  })

  it('steps through matches with Enter and Shift+Enter', async () => {
    render(<Host />)
    typeQuery('alpha')
    await waitFor(() => expect(screen.getByTestId('probe-search-count').textContent).toBe('1/2'))

    const input = screen.getByTestId('probe-search')
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(screen.getByTestId('probe-search-count').textContent).toBe('2/2')
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(screen.getByTestId('probe-search-count').textContent).toBe('1/2')
    fireEvent.keyDown(input, { key: 'Enter', shiftKey: true })
    expect(screen.getByTestId('probe-search-count').textContent).toBe('2/2')
  })

  it('reports 0/0 when nothing matches and hides the count once the query is cleared', async () => {
    render(<Host />)
    typeQuery('zzz-no-such-text')
    await waitFor(() => expect(screen.getByTestId('probe-search-count').textContent).toBe('0/0'))

    typeQuery('')
    await waitFor(() => expect(screen.queryByTestId('probe-search-count')).toBeNull())
  })

  it('copies the exact source text, not the formatted rendering', () => {
    const written: string[] = []
    const writeText = vi.fn((s: string) => {
      written.push(s)
      return Promise.resolve()
    })
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
      writable: true,
    })

    render(<Host />)
    fireEvent.click(screen.getByTestId('probe-copy'))
    expect(written).toEqual([RAW])
  })

  it('renders a minimal body with no toggle, search or Copy, keeping the leading label', () => {
    render(<Host controls="minimal" withLeading />)
    expect(screen.getByTestId('probe-leading').textContent).toBe('Probe · 31 chars')
    expect(screen.queryByTestId('probe-search')).toBeNull()
    expect(screen.queryByTestId('probe-copy')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Raw' })).toBeNull()
    expect(screen.getByText('alpha one')).toBeTruthy()
  })
})
