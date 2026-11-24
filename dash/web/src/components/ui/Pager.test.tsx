// @vitest-environment happy-dom
// The one pager every paged list shares (Task T3): previous, next and page
// numbers, keyboard-operable, disabled while its page loads, and absent when
// everything fits on one page.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'

import { Pager } from './Pager.js'

afterEach(() => {
  cleanup()
})

function renderPager(props: Partial<Parameters<typeof Pager>[0]> = {}) {
  const onPageChange = vi.fn()
  const utils = render(
    <Pager
      page={props.page ?? 1}
      limit={props.limit ?? 25}
      total={props.total ?? 100}
      loading={props.loading}
      ariaLabel={props.ariaLabel ?? 'Sessions pages'}
      onPageChange={onPageChange}
    />,
  )
  return { onPageChange, ...utils }
}

describe('Pager', () => {
  it('renders nothing when every row fits one page', () => {
    const { container } = renderPager({ total: 25, limit: 25 })
    expect(container.children).toHaveLength(0)
  })

  it('renders nothing when the list is empty', () => {
    const { container } = renderPager({ total: 0, limit: 25 })
    expect(container.children).toHaveLength(0)
  })

  it('renders previous, next and page numbers when there is more than one page', () => {
    renderPager({ page: 1, limit: 25, total: 100 })
    expect(screen.getByRole('navigation', { name: 'Sessions pages' })).not.toBeNull()
    expect(screen.getByRole('button', { name: 'Previous page' })).not.toBeNull()
    expect(screen.getByRole('button', { name: 'Next page' })).not.toBeNull()
    for (const pageNumber of [1, 2, 3, 4]) {
      expect(screen.getByRole('button', { name: `Page ${pageNumber}` })).not.toBeNull()
    }
    expect(screen.getByRole('button', { name: 'Page 1' }).getAttribute('aria-current')).toBe('page')
    expect(screen.queryByRole('button', { name: 'Page 5' })).toBeNull()
  })

  it('names the current page and leaves the others unmarked', () => {
    renderPager({ page: 3, limit: 25, total: 100 })
    expect(screen.getByRole('button', { name: 'Page 3' }).getAttribute('aria-current')).toBe('page')
    expect(screen.getByRole('button', { name: 'Page 2' }).getAttribute('aria-current')).toBeNull()
  })

  it('reports the next page only when Next is pressed', () => {
    const { onPageChange } = renderPager({ page: 2, limit: 25, total: 100 })
    expect(onPageChange).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }))
    expect(onPageChange).toHaveBeenCalledExactlyOnceWith(3)
  })

  it('reports the previous page and an arbitrary page number on press', () => {
    const { onPageChange } = renderPager({ page: 2, limit: 25, total: 100 })
    fireEvent.click(screen.getByRole('button', { name: 'Previous page' }))
    fireEvent.click(screen.getByRole('button', { name: 'Page 4' }))
    expect(onPageChange.mock.calls).toEqual([[1], [4]])
  })

  it('disables Previous on the first page and Next on the last', () => {
    renderPager({ page: 1, limit: 25, total: 100 })
    expect(screen.getByRole('button', { name: 'Previous page' })).toHaveProperty('disabled', true)
    expect(screen.getByRole('button', { name: 'Next page' })).toHaveProperty('disabled', false)

    cleanup()
    renderPager({ page: 4, limit: 25, total: 100 })
    expect(screen.getByRole('button', { name: 'Previous page' })).toHaveProperty('disabled', false)
    expect(screen.getByRole('button', { name: 'Next page' })).toHaveProperty('disabled', true)
  })

  it('disables every control while the page it points at is loading', () => {
    renderPager({ page: 2, limit: 25, total: 100, loading: true })
    for (const name of ['Previous page', 'Next page', 'Page 1', 'Page 3']) {
      expect(screen.getByRole('button', { name })).toHaveProperty('disabled', true)
    }
    // The current page is where the user already is; it is not a control to press.
    expect(screen.getByRole('button', { name: 'Page 2' })).toHaveProperty('disabled', false)
  })

  it('windowes a long run: first page, a window around the current one, and the last', () => {
    renderPager({ page: 50, limit: 25, total: 10_000 })
    expect(screen.queryByRole('button', { name: 'Page 1' })).not.toBeNull()
    expect(screen.queryByRole('button', { name: 'Page 400' })).not.toBeNull()
    for (const near of [49, 50, 51]) {
      expect(screen.queryByRole('button', { name: `Page ${near}` })).not.toBeNull()
    }
    // Far pages are gaps, not buttons that would pretend to be reachable.
    expect(screen.queryByRole('button', { name: 'Page 2' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Page 399' })).toBeNull()
  })

  it('is keyboard-operable: every destination is a real button', () => {
    renderPager({ page: 2, limit: 25, total: 100 })
    for (const button of screen.getAllByRole('button')) {
      expect(button.getAttribute('type')).toBe('button')
      expect(button).not.toHaveProperty('tabIndex', -1)
    }
  })
})
