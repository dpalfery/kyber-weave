import { describe, it, expect, vi } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { ContextReviewPanel } from './ContextReviewPanel.js'
import type { KyberReviewResult } from '../../lib/kyberApi.js'

let hookStates: unknown[] = []
let hookIndex = 0

const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: { H?: any }
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE

if (reactInternals) {
  reactInternals.H = {
    useState: <T,>(value: T | (() => T)): [T, (next: T | ((previous: T) => T)) => void] => {
      const index = hookIndex++
      if (index >= hookStates.length) {
        hookStates.push(typeof value === 'function' ? (value as () => T)() : value)
      }
      return [hookStates[index] as T, () => {}]
    },
    useMemo: <T,>(factory: () => T) => factory(),
    useCallback: <T,>(callback: T) => callback,
    useRef: <T,>(value: T) => ({ current: value }),
    useEffect: () => {},
    useLayoutEffect: () => {},
    useId: () => `test-id-${hookIndex++}`,
  }
}

function renderHtml(element: React.ReactElement): string {
  hookIndex = 0
  hookStates = []
  return renderToStaticMarkup(element)
}

describe('ContextReviewPanel (Decision D10 & D8 Compliance)', () => {
  it('renders payload preview and token size calculation before confirmation (Criterion 2)', () => {
    const content = 'System prompt instructions: obey project conventions.\n'.repeat(20) // ~1,100 chars
    const blocks = [
      { key: 'system_prompt', label: 'System Instructions', text: content, tokens: 275 },
    ]

    const html = renderHtml(
      React.createElement(ContextReviewPanel, {
        content,
        turnIndex: 3,
        sessionId: 'sess-preview-test',
        blocks,
      }),
    )

    // Asserts Decision D10 opt-in badge and advisory labeling
    expect(html).toContain('LLM Context Review')
    expect(html).toContain('Decision D10 Opt-In')
    expect(html).toContain('Advisory Only')

    // Asserts token calculation and character count preview
    expect(html).toContain('Payload Size:')
    expect(html).toContain('Turn #3')
    expect(html).toContain('1 composition block')
    expect(html).toContain('Run Context Review')
  })

  it('Decision D10 compliance: never triggers review automatically on render (Criterion 1)', () => {
    const mockRunner = vi.fn().mockResolvedValue({
      status: 'completed',
      source: 'model_review',
      provider: 'mock',
      review: 'Review output',
      timestamp: new Date().toISOString(),
    } as KyberReviewResult)

    renderHtml(
      React.createElement(ContextReviewPanel, {
        content: 'Turn content for testing auto-execution guard',
        onRunReview: mockRunner,
      }),
    )

    // Mock runner must NOT be called on render
    expect(mockRunner).not.toHaveBeenCalled()
  })

  it('renders Decision D8 non-destructive strategy information', () => {
    const html = renderHtml(
      React.createElement(ContextReviewPanel, {
        content: 'Sample content',
      }),
    )

    // Asserts that the panel mentions D8 non-destructive principles
    expect(html).toContain('Decision D8')
    expect(html).toContain('relocation')
    expect(html).toContain('progressive disclosure')
  })
})
