import { describe, it, expect, beforeEach } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'

import {
  SessionCostPanel,
  formatCostFigure,
  normalizeCostBlock,
  STATUS_WORDS,
  type CostBlock,
  type CostStatus,
} from './SessionCostPanel'

// ---------------------------------------------------------------------------
// Set up React 19 test hook dispatcher matching SessionSpendCharts.test.tsx
// ---------------------------------------------------------------------------

let hookStates: unknown[] = []
let hookIndex = 0

function clearHooks() {
  hookStates = []
  hookIndex = 0
}

const reactInternals = (
  React as unknown as {
    __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE?: {
      H?: unknown
    }
  }
).__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE

if (reactInternals) {
  reactInternals.H = {
    useState: <T,>(v: T | (() => T)): [T, (val: T | ((prev: T) => T)) => void] => {
      const idx = hookIndex++
      if (idx >= hookStates.length) {
        hookStates.push(typeof v === 'function' ? (v as () => T)() : v)
      }
      const setter = (next: T | ((prev: T) => T)) => {
        hookStates[idx] = typeof next === 'function' ? (next as (prev: T) => T)(hookStates[idx] as T) : next
      }
      return [hookStates[idx] as T, setter]
    },
    useMemo: <T,>(fn: () => T) => fn(),
    useCallback: <T,>(fn: T) => fn,
    useRef: <T,>(v: T) => ({ current: v }),
    useEffect: () => {},
    useLayoutEffect: () => {},
    useId: () => 'test-id',
  }
}

beforeEach(() => {
  clearHooks()
})

function renderHtml(element: React.ReactElement): string {
  return renderToStaticMarkup(element)
}

function findAllElements(
  node: unknown,
  predicate: (el: React.ReactElement<Record<string, unknown>>) => boolean,
): Array<React.ReactElement<Record<string, unknown>>> {
  const results: Array<React.ReactElement<Record<string, unknown>>> = []
  const walk = (curr: unknown) => {
    if (!curr || typeof curr !== 'object') return
    if (Array.isArray(curr)) {
      curr.forEach(walk)
      return
    }
    if (React.isValidElement(curr)) {
      const el = curr as React.ReactElement<Record<string, unknown>>
      if (predicate(el)) results.push(el)

      const type = el.type as unknown
      if (typeof type === 'function') {
        try {
          hookIndex = 0
          const rendered = (type as (p: unknown) => unknown)(el.props)
          walk(rendered)
        } catch {
          // ignore hook/state issues in plain walk
        }
      }
      if (el.props && 'children' in el.props) {
        walk(el.props.children)
      }
    }
  }
  walk(node)
  return results
}

function findByTestId(element: React.ReactElement, id: string): React.ReactElement<Record<string, unknown>> | undefined {
  const matches = findAllElements(element, (el) => el.props['data-testid'] === id)
  return matches[0]
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('SessionCostPanel: formatCostFigure & CostStatus in words (Rule 2)', () => {
  it('renders "priced" as currency when value and currency exist', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'priced',
      value: 1.25,
      currency: 'USD',
    }
    expect(formatCostFigure(block)).toBe('$1.25')
  })

  it('renders a genuine $0.00 for a priced zero, not a missing rate', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'priced',
      value: 0,
      currency: 'USD',
    }
    expect(formatCostFigure(block)).toBe('$0.00')
  })

  it('renders "not_billed" as "not billed" in words, never as $0.00', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'not_billed',
    }
    const rendered = formatCostFigure(block)
    expect(rendered).toBe('not billed')
    expect(rendered).not.toContain('$')
    expect(rendered).not.toContain('0.00')
  })

  it('renders "out_of_scope" as "out of scope" in words, never as $0.00', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'out_of_scope',
    }
    const rendered = formatCostFigure(block)
    expect(rendered).toBe('out of scope')
    expect(rendered).not.toContain('$')
    expect(rendered).not.toContain('0.00')
  })

  it('renders "no_rate" as "no published rate" in words, never as $0.00', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'no_rate',
    }
    const rendered = formatCostFigure(block)
    expect(rendered).toBe('no published rate')
    expect(rendered).not.toContain('$')
    expect(rendered).not.toContain('0.00')
  })

  it('renders "partial" as "partially priced" in words, never as $0.00', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'partial',
    }
    const rendered = formatCostFigure(block)
    expect(rendered).toBe('partially priced')
    expect(rendered).not.toContain('$')
    expect(rendered).not.toContain('0.00')
  })

  it('no status other than "priced" ever renders a currency figure, even if a value is erroneously present', () => {
    // R5.4 / R5.5: unpriced status with a numeric value must not show money
    const unpricedStatuses: CostStatus[] = ['not_billed', 'out_of_scope', 'no_rate', 'partial']
    for (const status of unpricedStatuses) {
      const block: CostBlock = {
        basis: 'published',
        status,
        value: 42.5,
        currency: 'USD',
      }
      const rendered = formatCostFigure(block)
      expect(rendered).not.toContain('$')
      expect(rendered).not.toContain('42.5')
      expect(rendered).toBe(STATUS_WORDS[status])
    }
  })

  it('renders every status in words in the rendered component markup', () => {
    const statuses: CostStatus[] = ['priced', 'partial', 'no_rate', 'out_of_scope', 'not_billed']

    for (const status of statuses) {
      const block: CostBlock = {
        basis: 'published',
        status,
        ...(status === 'priced' ? { value: 1.25, currency: 'USD' } : {}),
      }

      const element = React.createElement(SessionCostPanel, { cost: block })
      const html = renderHtml(element)

      // The status in words must be present in the card
      expect(html).toContain(STATUS_WORDS[status])

      if (status !== 'priced') {
        // Must never show a dollar sign or 0.00 for unpriced statuses
        expect(html).not.toContain('$')
        expect(html).not.toContain('0.00')
      } else {
        // Priced must show money
        expect(html).toContain('$1.25')
      }
    }
  })
})

describe('SessionCostPanel: absent value is not zero (Rule 3)', () => {
  it('a cost with no value renders reason in words and shows token totals', () => {
    const block: CostBlock = {
      basis: 'unknown',
      status: 'no_rate',
    }
    const session = {
      summary: {
        total_input: 50000,
        total_output: 2500,
        total_cache_read: 30000,
        total_cache_creation: 10000,
        cost: block,
      },
    }

    const element = React.createElement(SessionCostPanel, { session })
    const html = renderHtml(element)

    // Reason stated in words, not zero
    expect(html).toContain('no published rate')
    expect(html).not.toContain('$0.00')

    // Token totals are still rendered so reader learns something
    expect(html).toContain('50.0K')
    expect(html).toContain('2.5K')
    expect(html).toContain('30.0K')
    expect(html).toContain('10.0K')
  })
})

describe('SessionCostPanel: bases are never blended (Rule 1)', () => {
  it('renders two bases side by side and never sums them', () => {
    const publishedBlock: CostBlock = {
      basis: 'published',
      status: 'priced',
      value: 1.25,
      currency: 'USD',
    }
    const harnessBlock: CostBlock = {
      basis: 'harness',
      status: 'priced',
      value: 2.5,
      currency: 'USD',
    }

    const element = React.createElement(SessionCostPanel, {
      costs: [publishedBlock, harnessBlock],
    })
    const html = renderHtml(element)

    // Both cards exist
    const publishedCard = findByTestId(element, 'cost-basis-card-published')
    const harnessCard = findByTestId(element, 'cost-basis-card-harness')
    expect(publishedCard).toBeDefined()
    expect(harnessCard).toBeDefined()

    // Each figure appears under its own basis
    expect(html).toContain('$1.25')
    expect(html).toContain('$2.50')

    // CRITICAL: They are NEVER blended into $3.75!
    expect(html).not.toContain('$3.75')
    expect(html).not.toContain('3.75')

    // Surfaces sumCosts refusal as a plain warning
    const warning = findByTestId(element, 'cost-basis-mismatch-warning')
    expect(warning).toBeDefined()
    expect(html).toContain('cost bases differ across blocks')
    expect(html).toContain('refusing to blend them into one total')
  })

  it('surfaces recorded COST_BASIS_MISMATCH from session problems plainly as a warning', () => {
    const session = {
      costs: [
        { basis: 'published', status: 'priced', value: 0.8, currency: 'USD' },
        { basis: 'harness', status: 'priced', value: 0.9, currency: 'USD' },
      ],
      problems: [
        {
          code: 'COST_BASIS_MISMATCH',
          message: 'cost bases differ across blocks (harness, published); refusing to blend them into one total',
          severity: 'error',
        },
      ],
    }

    const element = React.createElement(SessionCostPanel, { session })
    const html = renderHtml(element)

    expect(html).toContain('data-testid="cost-basis-mismatch-warning"')
    expect(html).toContain('refusing to blend them into one total')
  })

  it('does not render mismatch warning when only a single basis is present', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'priced',
      value: 1.0,
      currency: 'USD',
    }

    const element = React.createElement(SessionCostPanel, { cost: block })
    const html = renderHtml(element)

    expect(html).not.toContain('data-testid="cost-basis-mismatch-warning"')
  })
})

describe('SessionCostPanel: per-model breakdown (byModel)', () => {
  it('renders per-model breakdown when byModel is present', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'priced',
      value: 2.0,
      currency: 'USD',
      byModel: {
        'claude-3-5-sonnet': 1.5,
        'gpt-4o': 0.5,
      },
    }

    const element = React.createElement(SessionCostPanel, { cost: block })
    const html = renderHtml(element)

    const section = findByTestId(element, 'cost-by-model')
    expect(section).toBeDefined()
    expect(html).toContain('Per-Model Breakdown')
    expect(html).toContain('claude-3-5-sonnet')
    expect(html).toContain('$1.50')
    expect(html).toContain('gpt-4o')
    expect(html).toContain('$0.50')
  })

  it('omits per-model breakdown entirely when byModel is absent (undefined)', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'priced',
      value: 1.25,
      currency: 'USD',
    }

    const element = React.createElement(SessionCostPanel, { cost: block })
    const html = renderHtml(element)

    const section = findByTestId(element, 'cost-by-model')
    expect(section).toBeUndefined()
    expect(html).not.toContain('data-testid="cost-by-model"')
    expect(html).not.toContain('Per-Model Breakdown')
  })

  it('omits per-model breakdown entirely when byModel is an empty object', () => {
    const block: CostBlock = {
      basis: 'published',
      status: 'priced',
      value: 1.25,
      currency: 'USD',
      byModel: {},
    }

    const element = React.createElement(SessionCostPanel, { cost: block })
    const html = renderHtml(element)

    expect(html).not.toContain('data-testid="cost-by-model"')
  })
})

describe('SessionCostPanel: token totals & honest cache hit ratio', () => {
  it('renders honest cache hit ratio from total_cache_read / total_input', () => {
    const session = {
      summary: {
        total_input: 100000,
        total_output: 5000,
        total_cache_read: 75000,
        total_cache_creation: 15000,
        cost: { basis: 'published', status: 'priced', value: 1.1, currency: 'USD' },
      },
    }

    const element = React.createElement(SessionCostPanel, { session })
    const html = renderHtml(element)

    // 75000 / 100000 = 75.0%
    expect(html).toContain('75.0%')
    expect(html).toContain('100.0K')
    expect(html).toContain('5.0K')
    expect(html).toContain('75.0K')
    expect(html).toContain('15.0K')
  })

  it('reports cache hit ratio as "not measurable" when input is 0 or missing, never 0.0%', () => {
    const session = {
      summary: {
        total_input: 0,
        total_output: 0,
        total_cache_read: 0,
        total_cache_creation: 0,
      },
    }

    const element = React.createElement(SessionCostPanel, { session })
    const hitRatioEl = findByTestId(element, 'token-cache-hit-ratio')
    expect(hitRatioEl).toBeDefined()

    const html = renderHtml(element)
    // Never invent 0.0% when input is 0
    expect(html).toContain('not measurable')
  })

  it('reports absent token metrics as "not measurable", never inventing 0', () => {
    const session = {
      summary: {},
    }

    const element = React.createElement(SessionCostPanel, { session })
    const html = renderHtml(element)

    expect(html).toContain('not measurable')
  })
})

describe('SessionCostPanel: normalizeCostBlock & extractCostBlocks', () => {
  it('normalizes legacy summary cost shape with usd and published_rates', () => {
    const legacy = {
      usd: 1.45,
      basis: 'published_rates',
      status: 'ok',
      by_model: [{ model: 'claude-3-5-sonnet', usd: 1.45 }],
    }
    const normalized = normalizeCostBlock(legacy)

    expect(normalized.basis).toBe('published')
    expect(normalized.status).toBe('priced')
    expect(normalized.value).toBe(1.45)
    expect(normalized.currency).toBe('USD')
    expect(normalized.byModel).toEqual({ 'claude-3-5-sonnet': 1.45 })
  })

  it('normalizes empty or null input into unknown no_rate block', () => {
    const normalized = normalizeCostBlock(null)
    expect(normalized.basis).toBe('unknown')
    expect(normalized.status).toBe('no_rate')
    expect(normalized.value).toBeUndefined()
  })
})
