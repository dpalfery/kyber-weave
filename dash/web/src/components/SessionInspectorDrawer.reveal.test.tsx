// @vitest-environment happy-dom
// Opening a folded section is verified on a real DOM: the revealed body must be
// scrolled into view only when it overflows its nearest scroll container. The
// rectangles are stubbed because happy-dom performs no layout.
import { describe, it, expect, vi, afterEach } from 'vitest'
import { render, fireEvent, cleanup } from '@testing-library/react'

import { XmlFoldedText } from './SessionInspectorDrawer.js'

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

interface ScrollCall {
  target: Element
  options: unknown
}

function installScrollRecorder(): { calls: ScrollCall[]; restore: () => void } {
  const previous = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollIntoView')
  const calls: ScrollCall[] = []
  Object.defineProperty(Element.prototype, 'scrollIntoView', {
    value: function recordScroll(this: Element, options: unknown) {
      calls.push({ target: this, options })
    },
    configurable: true,
    writable: true,
  })
  return {
    calls,
    restore: () => {
      if (previous) Object.defineProperty(Element.prototype, 'scrollIntoView', previous)
      else delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView
    },
  }
}

function rect(top: number, bottom: number): DOMRect {
  return { top, bottom, left: 0, right: 0, width: 0, height: bottom - top, x: 0, y: top, toJSON: () => ({}) } as DOMRect
}

function renderSections(scrollable: boolean) {
  const { container } = render(
    <div data-testid="scroller" style={scrollable ? { overflowY: 'auto', maxHeight: '100px' } : undefined}>
      <XmlFoldedText text="<system>Section body alpha.</system><context>Section body beta.</context>" />
    </div>,
  )
  const details = container.querySelector('[data-testid="folded-tag-context"]') as HTMLDetailsElement
  // A details element holds its summary, then the revealed body.
  const body = details.children[1] as HTMLElement
  const scroller = container.querySelector('[data-testid="scroller"]') as HTMLElement
  return { details, body, scroller }
}

/**
 * Opens or closes a section and delivers exactly one toggle. happy-dom fires
 * `toggle` itself when `open` changes, so the event is dispatched manually
 * only when that did not happen.
 */
function toggle(details: HTMLDetailsElement, open: boolean) {
  let delivered = 0
  const count = () => {
    delivered += 1
  }
  details.addEventListener('toggle', count)
  details.open = open
  if (delivered === 0) fireEvent(details, new Event('toggle'))
  details.removeEventListener('toggle', count)
}

describe('SessionInspectorDrawer: opened folds keep the revealed body on screen', () => {
  it('scrolls the revealed body into view, nearest, when it overflows the scroll container', () => {
    const scroll = installScrollRecorder()
    try {
      const { details, body, scroller } = renderSections(true)
      vi.spyOn(scroller, 'getBoundingClientRect').mockReturnValue(rect(0, 100))
      vi.spyOn(body, 'getBoundingClientRect').mockReturnValue(rect(150, 400))

      toggle(details, true)

      expect(scroll.calls).toHaveLength(1)
      expect(scroll.calls[0]!.target).toBe(body)
      expect(scroll.calls[0]!.options).toEqual({ block: 'nearest' })
    } finally {
      scroll.restore()
    }
  })

  it('does nothing when the revealed body already sits inside the scroll container', () => {
    const scroll = installScrollRecorder()
    try {
      const { details, body, scroller } = renderSections(true)
      vi.spyOn(scroller, 'getBoundingClientRect').mockReturnValue(rect(0, 300))
      vi.spyOn(body, 'getBoundingClientRect').mockReturnValue(rect(40, 120))

      toggle(details, true)

      expect(scroll.calls).toHaveLength(0)
    } finally {
      scroll.restore()
    }
  })

  it('does nothing when the section closes', () => {
    const scroll = installScrollRecorder()
    try {
      const { details, body, scroller } = renderSections(true)
      vi.spyOn(scroller, 'getBoundingClientRect').mockReturnValue(rect(0, 100))
      vi.spyOn(body, 'getBoundingClientRect').mockReturnValue(rect(150, 400))

      toggle(details, false)

      expect(scroll.calls).toHaveLength(0)
    } finally {
      scroll.restore()
    }
  })

  it('does nothing when no ancestor scrolls, because nothing is clipped', () => {
    const scroll = installScrollRecorder()
    try {
      const { details, body } = renderSections(false)
      vi.spyOn(body, 'getBoundingClientRect').mockReturnValue(rect(150, 400))

      toggle(details, true)

      expect(scroll.calls).toHaveLength(0)
    } finally {
      scroll.restore()
    }
  })
})
