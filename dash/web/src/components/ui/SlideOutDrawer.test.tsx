// @vitest-environment happy-dom
// The suites below mount the real component and drive real events and focus,
// so this file runs under happy-dom (renderToStaticMarkup expresses none of
// those). dash/vitest.config.ts is left untouched.
import { describe, it, expect, vi, afterEach } from 'vitest'
import { useState } from 'react'
import { render, screen, fireEvent, cleanup } from '@testing-library/react'

import { SlideOutDrawer } from './SlideOutDrawer.js'

// Distinct from PREFIX_HONOURED below on purpose: a component that hardcoded
// one prefix's test ids cannot pass both suites at once.
const PREFIX = 'unit-drawer'
const PREFIX_HONOURED = 'settings-drawer'

afterEach(cleanup)

/**
 * A stateful host: the shell is controlled (open/onClose), so the focus suites
 * need an opener button and a real open → close transition, not a static tree.
 */
function DrawerHost({ manageFocus }: { manageFocus?: boolean }) {
  const [open, setOpen] = useState(false)
  return (
    <div>
      <button type="button" data-testid="drawer-opener" onClick={() => setOpen(true)}>
        Open drawer
      </button>
      <SlideOutDrawer
        open={open}
        onClose={() => setOpen(false)}
        label="Focus host drawer"
        testIdPrefix={PREFIX}
        manageFocus={manageFocus}
      >
        <p data-testid={`${PREFIX}-child`}>Drawer body content</p>
      </SlideOutDrawer>
    </div>
  )
}

describe('SlideOutDrawer: rendering', () => {
  it('renders nothing when open is false', () => {
    const { container } = render(
      <SlideOutDrawer open={false} onClose={() => {}} label="Closed drawer" testIdPrefix={PREFIX} />,
    )

    expect(container.innerHTML).toBe('')
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('renders a modal dialog labelled by the label prop with the children in the body', () => {
    render(
      <SlideOutDrawer open onClose={() => {}} label="Session inspector" testIdPrefix={PREFIX}>
        <p data-testid={`${PREFIX}-child`}>Drawer body content</p>
      </SlideOutDrawer>,
    )

    const dialog = screen.getByRole('dialog')
    expect(dialog.getAttribute('aria-modal')).toBe('true')
    expect(dialog.getAttribute('aria-label')).toBe('Session inspector')

    expect(screen.getByTestId(`${PREFIX}-body`).textContent).toContain('Drawer body content')
  })
})

describe('SlideOutDrawer: closing interactions', () => {
  it('calls onClose when Escape is pressed and ignores other keys', () => {
    const onClose = vi.fn()
    render(
      <SlideOutDrawer open onClose={onClose} label="Escape drawer" testIdPrefix={PREFIX} />,
    )

    // Dispatched on the body — no element inside has focus here — so a listener
    // on either document or window receives it, as a real keypress would.
    fireEvent.keyDown(document.body, { key: 'Tab' })
    expect(onClose).not.toHaveBeenCalled()

    fireEvent.keyDown(document.body, { key: 'Escape' })
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('calls onClose when the backdrop is clicked', () => {
    const onClose = vi.fn()
    render(
      <SlideOutDrawer open onClose={onClose} label="Backdrop drawer" testIdPrefix={PREFIX} />,
    )

    fireEvent.click(screen.getByTestId(`${PREFIX}-backdrop`))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('calls onClose when the close button is clicked', () => {
    const onClose = vi.fn()
    render(
      <SlideOutDrawer open onClose={onClose} label="Close button drawer" testIdPrefix={PREFIX} />,
    )

    fireEvent.click(screen.getByTestId(`${PREFIX}-close-button`))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('does not call onClose for clicks inside the panel', () => {
    const onClose = vi.fn()
    render(
      <SlideOutDrawer open onClose={onClose} label="Panel click drawer" testIdPrefix={PREFIX}>
        <p data-testid={`${PREFIX}-child`}>Drawer body content</p>
      </SlideOutDrawer>,
    )

    fireEvent.click(screen.getByRole('dialog'))
    expect(onClose).not.toHaveBeenCalled()

    fireEvent.click(screen.getByTestId(`${PREFIX}-child`))
    expect(onClose).not.toHaveBeenCalled()
  })
})

describe('SlideOutDrawer: test-id prefix', () => {
  it('derives the backdrop, close-button and body test ids from the prefix', () => {
    render(
      <SlideOutDrawer
        open
        onClose={() => {}}
        label="Prefixed drawer"
        testIdPrefix={PREFIX_HONOURED}
      >
        <p>Settings body content</p>
      </SlideOutDrawer>,
    )

    expect(screen.getByTestId(`${PREFIX_HONOURED}-backdrop`)).toBeTruthy()
    expect(screen.getByTestId(`${PREFIX_HONOURED}-close-button`)).toBeTruthy()
    expect(screen.getByTestId(`${PREFIX_HONOURED}-body`).textContent).toContain('Settings body content')
  })
})

describe('SlideOutDrawer: focus management', () => {
  it('moves focus into the dialog on open and back to the opener on close when manageFocus is on', () => {
    render(<DrawerHost manageFocus />)

    const opener = screen.getByTestId('drawer-opener')
    opener.focus()
    fireEvent.click(opener)

    // Which element inside receives focus (panel, close button, first field)
    // is the implementer's choice; the contract is only that it is inside.
    const dialog = screen.getByRole('dialog')
    expect(dialog.contains(document.activeElement)).toBe(true)

    fireEvent.click(screen.getByTestId(`${PREFIX}-close-button`))
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(document.activeElement).toBe(opener)
  })

  it('does not move focus when manageFocus is omitted', () => {
    render(<DrawerHost />)

    const opener = screen.getByTestId('drawer-opener')
    opener.focus()
    fireEvent.click(opener)

    const dialog = screen.getByRole('dialog')
    expect(document.activeElement).toBe(opener)
    expect(dialog.contains(document.activeElement)).toBe(false)
  })
})

describe('SlideOutDrawer: focus trap', () => {
  function TrapHost({ manageFocus }: { manageFocus: boolean }) {
    return (
      <div>
        <button type="button" data-testid="outside">
          outside
        </button>
        <SlideOutDrawer open onClose={() => {}} label="Trap drawer" testIdPrefix={PREFIX} manageFocus={manageFocus}>
          <button type="button" data-testid="inner-last">
            last
          </button>
        </SlideOutDrawer>
      </div>
    )
  }

  it('wraps Tab from the last control to the first and Shift+Tab back when manageFocus is on', () => {
    render(<TrapHost manageFocus />)
    const close = screen.getByTestId(`${PREFIX}-close-button`)
    const last = screen.getByTestId('inner-last')
    last.focus()
    expect(fireEvent.keyDown(last, { key: 'Tab' })).toBe(false)
    expect(document.activeElement).toBe(close)
    expect(fireEvent.keyDown(close, { key: 'Tab', shiftKey: true })).toBe(false)
    expect(document.activeElement).toBe(last)
  })

  it('pulls focus that escaped the panel back inside', () => {
    render(<TrapHost manageFocus />)
    const outside = screen.getByTestId('outside')
    outside.focus()
    fireEvent.keyDown(outside, { key: 'Tab' })
    expect(screen.getByTestId(`${PREFIX}-panel`).contains(document.activeElement)).toBe(true)
  })

  it('leaves Tab alone when manageFocus is off', () => {
    render(<TrapHost manageFocus={false} />)
    const last = screen.getByTestId('inner-last')
    last.focus()
    expect(fireEvent.keyDown(last, { key: 'Tab' })).toBe(true)
  })
})
