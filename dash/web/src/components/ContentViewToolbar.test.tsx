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

  it('reports 0/0 when nothing matches, and keeps the count visible when the query is cleared', async () => {
    render(<Host />)
    // Always rendered: the count sits next to the box before any query is typed.
    expect(screen.getByTestId('probe-search-count').textContent).toBe('0/0')

    typeQuery('zzz-no-such-text')
    await waitFor(() => expect(screen.getByTestId('probe-search-count').textContent).toBe('0/0'))

    typeQuery('')
    await waitFor(() => expect(screen.getByTestId('probe-search-count').textContent).toBe('0/0'))
    expect(screen.getByRole('button', { name: 'Next match' }).hasAttribute('disabled')).toBe(true)
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

/** Records each element a scrollIntoView call targets, in call order. */
function installScrollRecorder(): { targets: Element[]; restore: () => void } {
  const previous = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollIntoView')
  const targets: Element[] = []
  Object.defineProperty(Element.prototype, 'scrollIntoView', {
    value: function recordScroll(this: Element) {
      targets.push(this)
    },
    configurable: true,
    writable: true,
  })
  return {
    targets,
    restore: () => {
      if (previous) Object.defineProperty(Element.prototype, 'scrollIntoView', previous)
      else delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView
    },
  }
}

/** Installs a CSS Custom Highlight registry; `null` removes the API entirely. */
function installHighlightApi(enabled: boolean): { registry: Map<string, unknown>; restore: () => void } {
  // happy-dom defines CSS as a getter-only global, so each stub is defined as a
  // data property and the original descriptor is put back on restore.
  const registry = new Map<string, unknown>()
  const cssPrevious = Object.getOwnPropertyDescriptor(globalThis, 'CSS')
  const highlightPrevious = Object.getOwnPropertyDescriptor(globalThis, 'Highlight')
  Object.defineProperty(globalThis, 'CSS', {
    value: enabled ? { highlights: registry } : undefined,
    configurable: true,
    writable: true,
  })
  Object.defineProperty(globalThis, 'Highlight', {
    value: enabled
      ? class {
          ranges: Range[]
          constructor(...ranges: Range[]) {
            this.ranges = ranges
          }
        }
      : undefined,
    configurable: true,
    writable: true,
  })
  return {
    registry,
    restore: () => {
      if (cssPrevious) Object.defineProperty(globalThis, 'CSS', cssPrevious)
      else delete (globalThis as { CSS?: unknown }).CSS
      if (highlightPrevious) Object.defineProperty(globalThis, 'Highlight', highlightPrevious)
      else delete (globalThis as { Highlight?: unknown }).Highlight
    },
  }
}

/** Formatted body with a match in plain text and one inside a closed fold. */
function NavHost({ text }: { text: string }) {
  const [view, setView] = useState<ContentViewMode>('formatted')
  return (
    <SearchableContentView
      view={view}
      onViewChange={setView}
      text={text}
      label="Nav"
      viewPrefix="nav"
      formatted={
        <div>
          <p>top alpha</p>
          <details>
            <summary>fold</summary>
            <p>deep alpha</p>
          </details>
        </div>
      }
      raw={<pre>{text}</pre>}
    />
  )
}

/** Plain text in both views, for matching characters whose lowercase changes length. */
function TextHost({ text }: { text: string }) {
  const [view, setView] = useState<ContentViewMode>('formatted')
  return (
    <SearchableContentView
      view={view}
      onViewChange={setView}
      text={text}
      label="Text"
      viewPrefix="text"
      formatted={<div>{text}</div>}
      raw={<pre>{text}</pre>}
    />
  )
}

describe('ContentViewToolbar: search navigation and highlighting', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('scrolls the first match into view as soon as the query is typed', async () => {
    const scroll = installScrollRecorder()
    try {
      render(<NavHost text="top alpha deep alpha" />)
      fireEvent.change(screen.getByTestId('nav-search'), { target: { value: 'alpha' } })
      await waitFor(() => expect(screen.getByTestId('nav-search-count').textContent).toBe('1/2'))
      expect(scroll.targets.at(-1)?.textContent).toBe('top alpha')
    } finally {
      scroll.restore()
    }
  })

  it('steps with Enter and Shift+Enter, re-scrolling each time and opening a fold that holds the match', async () => {
    const scroll = installScrollRecorder()
    try {
      const { container } = render(<NavHost text="top alpha deep alpha" />)
      fireEvent.change(screen.getByTestId('nav-search'), { target: { value: 'alpha' } })
      await waitFor(() => expect(screen.getByTestId('nav-search-count').textContent).toBe('1/2'))

      const input = screen.getByTestId('nav-search')
      fireEvent.keyDown(input, { key: 'Enter' })
      expect(screen.getByTestId('nav-search-count').textContent).toBe('2/2')
      expect(scroll.targets.at(-1)?.textContent).toBe('deep alpha')
      expect((container.querySelector('details') as HTMLDetailsElement).open).toBe(true)

      fireEvent.keyDown(input, { key: 'Enter', shiftKey: true })
      expect(screen.getByTestId('nav-search-count').textContent).toBe('1/2')
      expect(scroll.targets.at(-1)?.textContent).toBe('top alpha')

      fireEvent.click(screen.getByRole('button', { name: 'Next match' }))
      expect(screen.getByTestId('nav-search-count').textContent).toBe('2/2')
      expect(scroll.targets.at(-1)?.textContent).toBe('deep alpha')
    } finally {
      scroll.restore()
    }
  })

  it('marks the active match element when the CSS Highlight API is unavailable', async () => {
    const api = installHighlightApi(false)
    const scroll = installScrollRecorder()
    try {
      const { container } = render(<NavHost text="top alpha deep alpha" />)
      fireEvent.change(screen.getByTestId('nav-search'), { target: { value: 'alpha' } })
      await waitFor(() => expect(screen.getByTestId('nav-search-count').textContent).toBe('1/2'))

      expect(container.querySelectorAll('[data-search-hit]')).toHaveLength(2)
      expect(container.querySelector('[data-search-active]')?.textContent).toBe('top alpha')

      fireEvent.keyDown(screen.getByTestId('nav-search'), { key: 'Enter' })
      expect(container.querySelector('[data-search-active]')?.textContent).toBe('deep alpha')
    } finally {
      scroll.restore()
      api.restore()
    }
  })

  it('counts and ranges by the original text for characters whose lowercase changes length', async () => {
    const api = installHighlightApi(true)
    try {
      render(<TextHost text="İİ alpha İ" />)
      fireEvent.change(screen.getByTestId('text-search'), { target: { value: 'İ' } })
      await waitFor(() => expect(screen.getByTestId('text-search-count').textContent).toBe('1/3'))

      const key = [...api.registry.keys()].find((k) => k.startsWith('search-match-'))
      expect(key).toBeDefined()
      const ranges = (api.registry.get(key!) as { ranges: Range[] }).ranges
      // Offsets land on the three capital dotted I characters in the original string.
      expect(ranges.map((r) => r.startOffset)).toEqual([0, 1, 9])
      for (const r of ranges) expect(r.endOffset - r.startOffset).toBe(1)
    } finally {
      api.restore()
    }
  })
})

describe('CopyExactButton: clipboard edge cases', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('does not surface a rejected clipboard write, and does not claim success', async () => {
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      value: { writeText: () => Promise.reject(new Error('permission denied')) },
      configurable: true,
      writable: true,
    })
    render(<Host />)
    expect(() => fireEvent.click(screen.getByTestId('probe-copy'))).not.toThrow()
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(screen.getByTestId('probe-copy').textContent).toBe('Copy')
  })

  it('does nothing when the clipboard API is missing', () => {
    Object.defineProperty(globalThis.navigator, 'clipboard', {
      value: undefined,
      configurable: true,
      writable: true,
    })
    render(<Host />)
    expect(() => fireEvent.click(screen.getByTestId('probe-copy'))).not.toThrow()
    expect(screen.getByTestId('probe-copy').textContent).toBe('Copy')
  })
})
