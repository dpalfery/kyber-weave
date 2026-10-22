// @vitest-environment happy-dom
// The raw-packet disclosure suite at the foot of this file mounts the real
// component with @testing-library/react, so this file runs under happy-dom. The
// static-markup suites above are unaffected — renderToStaticMarkup needs no DOM
// (same split as ContextDoctor.test.tsx). dash/vitest.config.ts is untouched.
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import * as React from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
// Aliased: the static-markup suites above render with react-dom/server, so the
// DOM `render` is named `renderDom` to keep the two apart at every call site.
import { render as renderDom, screen, fireEvent, cleanup, waitFor, within } from '@testing-library/react'

import { TurnDetail, asMeasuredFigure, findSessionTurnRow, resolveTurnSessionId, turnContentErrorKind, turnRowFigures } from './TurnDetail.js'
import {
  KyberApiError,
  type KyberRunDetail,
  type KyberSessionTurnRow,
  type KyberTurnContentResult,
} from '../lib/kyberApi.js'

// The turn surface asked the content route for a run id. Every derived run is
// keyed `derived:<harness>:<session>` — 1,455 of 1,455 on the measured corpus —
// so the route 404'd for all of them and the page rendered "Session or turn
// content not found" with no context bands.
describe('resolveTurnSessionId', () => {
  const run = {
    executions: [
      { executionId: 'exec-1', sessionId: 'sess-1' },
      { executionId: 'exec-2', sessionId: 'sess-2' },
    ],
  }

  it('resolves the named execution to its session', () => {
    expect(resolveTurnSessionId(run, 'exec-2')).toBe('sess-2')
  })

  it('falls back to the run\'s first execution when the link names none', () => {
    // A turn reached from a finding carries no execution.
    expect(resolveTurnSessionId(run, undefined)).toBe('sess-1')
  })

  it('never returns a run id', () => {
    const derived = { executions: [{ executionId: 'exec-1', sessionId: 'sess-1' }] }

    expect(resolveTurnSessionId(derived, undefined)).not.toMatch(/^derived:/)
  })

  it('uses the execution id when the execution recorded no session', () => {
    const noSession = { executions: [{ executionId: 'exec-only' }] }

    expect(resolveTurnSessionId(noSession, 'exec-only')).toBe('exec-only')
  })

  it('keeps the requested execution when it is not in the run yet', () => {
    // The run query may not have resolved; the execution id is still the better
    // guess than the run id, since executions and sessions share their key.
    expect(resolveTurnSessionId(undefined, 'exec-9')).toBe('exec-9')
  })

  it('is undefined when nothing names an execution', () => {
    expect(resolveTurnSessionId(undefined, undefined)).toBeUndefined()
    expect(resolveTurnSessionId({ executions: [] }, undefined)).toBeUndefined()
  })
})

// The inspector header's measured counters must describe the same 0-based turn
// the content route resolved — never a neighbor (issue #184).
describe('findSessionTurnRow', () => {
  const rows: KyberSessionTurnRow[] = [
    { index: 0, input: 850, output: 120, fresh: 40, cache_read: 810 },
    { index: 1, input: 900, output: 130, fresh: 50, cache_read: 860 },
  ]

  it('resolves a 0-based index to exactly that row', () => {
    expect(findSessionTurnRow(rows, 1)?.input).toBe(900)
  })

  it('resolves a legacy 1-based `turn` row via `turn - 1`', () => {
    const legacy: KyberSessionTurnRow[] = [
      { index: 0, input: 1 },
      { turn: 2, input: 2 } as KyberSessionTurnRow,
    ]
    expect(findSessionTurnRow(legacy, 1)?.input).toBe(2)
  })

  it('returns undefined past the end instead of the last row', () => {
    expect(findSessionTurnRow(rows, 2)).toBeUndefined()
    expect(findSessionTurnRow(undefined, 0)).toBeUndefined()
  })

  it('prefers explicit legacy identity over array position (issue #184 review)', () => {
    const shuffled: KyberSessionTurnRow[] = [
      { turn: 2, input: 10 } as KyberSessionTurnRow,
      { turn: 1, input: 20 } as KyberSessionTurnRow,
    ]
    expect(findSessionTurnRow(shuffled, 0)?.input).toBe(20)
    expect(findSessionTurnRow(shuffled, 1)?.input).toBe(10)
  })
})

describe('turnRowFigures', () => {
  it('passes measured counters through and nulls anything unmeasured', () => {
    expect(
      turnRowFigures({ index: 0, input: 850, output: 120, fresh: 40, cache_read: 810 }),
    ).toEqual({ input: 850, output: 120, fresh: 40, cacheRead: 810 })
    expect(turnRowFigures(undefined)).toBeUndefined()
  })

  it('asMeasuredFigure accepts only finite numbers', () => {
    expect(asMeasuredFigure(42)).toBe(42)
    expect(asMeasuredFigure(undefined)).toBeNull()
    expect(asMeasuredFigure(null)).toBeNull()
    expect(asMeasuredFigure('850')).toBeNull()
    expect(asMeasuredFigure(NaN)).toBeNull()
  })
})

// Issue #184: transport is 0-based `turnIndex`; the human-facing header is 1-based.
describe('TurnDetail header numbering', () => {
  it('renders a 1-based Turn heading for a 0-based turnIndex', () => {
    const qc = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0 } },
    })
    const html = renderToStaticMarkup(
      React.createElement(
        QueryClientProvider,
        { client: qc },
        React.createElement(TurnDetail, { runId: 'run-184', turnIndex: 5 }),
      ),
    )

    expect(html).toContain('data-testid="page-turn"')
    expect(html).toContain('Turn 6')
  })
})

// Issue #184 review: the "no such turn" state is for 404s only; every other
// failure renders the generic load error.
describe('turnContentErrorKind', () => {
  it('reports not-found for a 404 KyberApiError', () => {
    expect(turnContentErrorKind(new KyberApiError(404, '/api/kyber/session/s/turn/9/content'))).toBe('not-found')
  })

  it('reports load-failed for non-404 failures', () => {
    expect(turnContentErrorKind(new KyberApiError(500, '/api/kyber/session/s/turn/9/content'))).toBe('load-failed')
    expect(turnContentErrorKind(new Error('boom'))).toBe('load-failed')
    expect(turnContentErrorKind(undefined)).toBe('load-failed')
  })
})

// ---------------------------------------------------------------------------
// B7 raw packet: the turn view links the packets the server named (T11/T12).
//
// The disclosure renders exactly what the API returned and fetches only through
// `kyberApi` — no domain computation in the browser (ADR 0033, the display-layer
// rule). `rawPacket.spanAttributesPath` is `/api/kyber/span/<spanId>/attributes`
// and `rawPacket.interactionPath` is `/api/kyber/quarantine/<parentSpanId>`;
// the two fetchers resolve a span id to exactly those paths.
// ---------------------------------------------------------------------------

/** The packet pointers B7 adds to `TurnContentResult`; T8 folds them into the type. */
type TurnPacketPointers = {
  parentSpanId?: string
  rawPacket?: { spanAttributesPath: string; interactionPath?: string }
}

const rawPacketApi = vi.hoisted(() => {
  const SPAN_ID = 'llm-span-1'
  const PARENT_SPAN_ID = 'interaction-span-1'
  // Long enough that a clipped render would visibly drop the tail.
  const UNCLIPPED_TAIL = `TAIL_${'x'.repeat(400)}`
  return {
    SPAN_ID,
    PARENT_SPAN_ID,
    UNCLIPPED_TAIL,
    spanAttributesPath: `/api/kyber/span/${SPAN_ID}/attributes`,
    interactionPath: `/api/kyber/quarantine/${PARENT_SPAN_ID}`,
    spanAttributes: {
      spanId: SPAN_ID,
      attributes: {
        'claude_code.api_request_body': { user_prompt: `SPAN_PACKET_MARKER ${UNCLIPPED_TAIL}` },
      },
    },
    quarantineEntry: {
      spanId: PARENT_SPAN_ID,
      source: 'claude-code',
      name: 'claude_code.interaction',
      reason: 'non-model span',
      timestamp: '2026-10-10T00:00:00.000Z',
      namespaces: ['claude'],
      harness: 'claude-code',
      rawPayload: JSON.stringify({
        name: 'claude_code.interaction',
        attributes: { user_prompt: `INTERACTION_PACKET_MARKER ${UNCLIPPED_TAIL}` },
      }),
      evidenceAvailable: true,
    },
    fetchRun: vi.fn(),
    fetchTurnContent: vi.fn(),
    fetchKyberSessionTurns: vi.fn(),
    fetchSpanAttributes: vi.fn(),
    fetchQuarantineEntry: vi.fn(),
  }
})

vi.mock('../lib/kyberApi.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../lib/kyberApi.js')>()
  return {
    ...actual,
    fetchRun: rawPacketApi.fetchRun,
    fetchTurnContent: rawPacketApi.fetchTurnContent,
    fetchKyberSessionTurns: rawPacketApi.fetchKyberSessionTurns,
    fetchSpanAttributes: rawPacketApi.fetchSpanAttributes,
    // Added by T12; stubbed here so the module boundary is the only thing the
    // disclosure can reach before the fetcher exists in kyberApi.ts.
    fetchQuarantineEntry: rawPacketApi.fetchQuarantineEntry,
  }
})

const RUN_WITH_PACKET: KyberRunDetail = {
  runId: 'run-raw-packet',
  harness: 'claude-code',
  groupingBasis: 'derived',
  executionTree: [],
  executions: [
    {
      executionId: 'exec-raw-packet',
      runId: 'run-raw-packet',
      harness: 'claude-code',
      isRoot: true,
      sessionId: 'sess-raw-packet',
    },
  ],
  findings: [],
}

function turnContent(over: TurnPacketPointers = {}): KyberTurnContentResult & TurnPacketPointers {
  return {
    sessionId: 'sess-raw-packet',
    turnIndex: 0,
    spanId: rawPacketApi.SPAN_ID,
    model: 'claude-3-7-sonnet-20250219',
    measurability: 'full',
    blocks: [
      {
        key: 'conversation_history',
        label: 'Conversation History',
        tokens: 12,
        text: 'user: why is the Claude turn inspector empty?',
        parts: [
          {
            part: 'conversation_history',
            label: 'Conversation History',
            tokens: 12,
            text: 'user: why is the Claude turn inspector empty?',
          },
        ],
      },
    ],
    parts: [
      {
        part: 'conversation_history',
        label: 'Conversation History',
        tokens: 12,
        text: 'user: why is the Claude turn inspector empty?',
      },
    ],
    assembledText: '[CONVERSATION HISTORY]\nuser: why is the Claude turn inspector empty?',
    truncated: false,
    ...over,
  }
}

/** The turn page, mounted with the query client the page's own tests use. */
function renderTurn() {
  renderDom(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })}>
      <TurnDetail runId="run-raw-packet" turnIndex={0} />
    </QueryClientProvider>,
  )
}

/** The request target `fetchSpanAttributes` resolves for its argument (id or path). */
function requestedSpanPath(arg: unknown): string {
  const value = String(arg)
  return value.startsWith('/') ? value : `/api/kyber/span/${value}/attributes`
}

/** The request target `fetchQuarantineEntry` resolves for its argument (id or path). */
function requestedQuarantinePath(arg: unknown): string {
  const value = String(arg)
  return value.startsWith('/') ? value : `/api/kyber/quarantine/${value}`
}

describe('TurnDetail raw packet disclosure (B7)', () => {
  beforeEach(() => {
    rawPacketApi.fetchRun.mockReset()
    rawPacketApi.fetchTurnContent.mockReset()
    rawPacketApi.fetchKyberSessionTurns.mockReset()
    rawPacketApi.fetchSpanAttributes.mockReset()
    rawPacketApi.fetchQuarantineEntry.mockReset()

    rawPacketApi.fetchRun.mockResolvedValue(RUN_WITH_PACKET)
    // The bounded per-turn route answers one row, not the session's list of
    // them: the page reads the turn it displays and nothing else. This row
    // carries no counters, so the inspector header renders its absent state.
    rawPacketApi.fetchKyberSessionTurns.mockResolvedValue({
      session_id: 'sess-raw-packet',
      turn_index: 0,
      index: 0,
    })
    rawPacketApi.fetchSpanAttributes.mockResolvedValue(rawPacketApi.spanAttributes)
    rawPacketApi.fetchQuarantineEntry.mockResolvedValue(rawPacketApi.quarantineEntry)
  })

  afterEach(() => {
    cleanup()
  })

  /** The disclosure's toggle — a button by contract, a `<summary>` if T12 used `<details>`. */
  function openDisclosure(): HTMLElement {
    const disclosure = screen.getByTestId('raw-packet-disclosure')
    const toggle =
      within(disclosure).queryByTestId('raw-packet-toggle') ??
      within(disclosure).queryByRole('button') ??
      disclosure.querySelector('summary')
    expect(toggle).not.toBeNull()
    fireEvent.click(toggle as Element)
    return disclosure
  }

  it('renders a Raw packet disclosure when the turn-content result carries one', async () => {
    rawPacketApi.fetchTurnContent.mockResolvedValue(
      turnContent({
        parentSpanId: rawPacketApi.PARENT_SPAN_ID,
        rawPacket: {
          spanAttributesPath: rawPacketApi.spanAttributesPath,
          interactionPath: rawPacketApi.interactionPath,
        },
      }),
    )
    renderTurn()

    await waitFor(() => {
      expect(screen.getByTestId('raw-packet-disclosure')).toBeTruthy()
    })
    expect(screen.getByTestId('raw-packet-disclosure').textContent).toContain('Raw packet')
  })

  it('opens by fetching exactly spanAttributesPath plus interactionPath through kyberApi, and shows the JSON unclipped', async () => {
    rawPacketApi.fetchTurnContent.mockResolvedValue(
      turnContent({
        parentSpanId: rawPacketApi.PARENT_SPAN_ID,
        rawPacket: {
          spanAttributesPath: rawPacketApi.spanAttributesPath,
          interactionPath: rawPacketApi.interactionPath,
        },
      }),
    )
    renderTurn()

    await waitFor(() => {
      expect(screen.getByTestId('raw-packet-disclosure')).toBeTruthy()
    })
    // "Opening it fetches": nothing is requested while the disclosure is closed.
    expect(rawPacketApi.fetchSpanAttributes).not.toHaveBeenCalled()
    expect(rawPacketApi.fetchQuarantineEntry).not.toHaveBeenCalled()

    openDisclosure()

    await waitFor(() => {
      expect(rawPacketApi.fetchSpanAttributes).toHaveBeenCalledTimes(1)
    })
    expect(requestedSpanPath(rawPacketApi.fetchSpanAttributes.mock.calls[0][0])).toBe(
      rawPacketApi.spanAttributesPath,
    )

    await waitFor(() => {
      expect(rawPacketApi.fetchQuarantineEntry).toHaveBeenCalledTimes(1)
    })
    expect(requestedQuarantinePath(rawPacketApi.fetchQuarantineEntry.mock.calls[0][0])).toBe(
      rawPacketApi.interactionPath,
    )

    const shown = () => screen.getByTestId('raw-packet-disclosure').textContent ?? ''
    await waitFor(() => {
      expect(shown()).toContain('SPAN_PACKET_MARKER')
      expect(shown()).toContain('INTERACTION_PACKET_MARKER')
    })
    // Unclipped: the whole tail is rendered, not a prefix of it.
    expect(shown()).toContain(rawPacketApi.UNCLIPPED_TAIL)
  })

  it('fetches only the span attributes when the result names no interaction packet', async () => {
    rawPacketApi.fetchTurnContent.mockResolvedValue(
      turnContent({
        parentSpanId: rawPacketApi.PARENT_SPAN_ID,
        rawPacket: { spanAttributesPath: rawPacketApi.spanAttributesPath },
      }),
    )
    renderTurn()

    await waitFor(() => {
      expect(screen.getByTestId('raw-packet-disclosure')).toBeTruthy()
    })
    openDisclosure()

    await waitFor(() => {
      expect(rawPacketApi.fetchSpanAttributes).toHaveBeenCalledTimes(1)
    })
    expect(requestedSpanPath(rawPacketApi.fetchSpanAttributes.mock.calls[0][0])).toBe(
      rawPacketApi.spanAttributesPath,
    )
    expect(rawPacketApi.fetchQuarantineEntry).not.toHaveBeenCalled()
  })

  it('caps the rendered packet past the display budget and says so, while the API still serves the whole payload', async () => {
    // The scroll container handles the display, but not the cost of
    // stringifying a multi-megabyte payload or mounting the resulting DOM
    // nodes. Past the cap the render cuts and names how to reach the rest.
    const huge = 'H'.repeat(200_000)
    rawPacketApi.fetchQuarantineEntry.mockResolvedValue({
      ...rawPacketApi.quarantineEntry,
      rawPayload: JSON.stringify({ name: 'claude_code.interaction', blob: huge }),
    })
    rawPacketApi.fetchTurnContent.mockResolvedValue(
      turnContent({
        parentSpanId: rawPacketApi.PARENT_SPAN_ID,
        rawPacket: {
          spanAttributesPath: rawPacketApi.spanAttributesPath,
          interactionPath: rawPacketApi.interactionPath,
        },
      }),
    )
    renderTurn()

    await waitFor(() => {
      expect(screen.getByTestId('raw-packet-disclosure')).toBeTruthy()
    })
    openDisclosure()

    const shown = () => screen.getByTestId('raw-packet-disclosure').textContent ?? ''
    await waitFor(() => {
      expect(shown()).toContain('truncated')
    })
    // The cut happened near the cap, not at a whole-payload mount. The
    // disclosure renders the quarantined entry twice (as filed, and the kept
    // packet as sent), each bounded by the cap — an uncapped mount of this
    // payload would be roughly twice what a capped pair can reach.
    expect(shown().length).toBeLessThan(220_000)
    expect(shown()).not.toContain(huge)
  })

  it('renders no disclosure when the turn-content result carries no raw packet', async () => {
    rawPacketApi.fetchTurnContent.mockResolvedValue(turnContent())
    renderTurn()

    await waitFor(() => {
      expect(screen.getByTestId('page-turn')).toBeTruthy()
    })
    expect(screen.queryByTestId('raw-packet-disclosure')).toBeNull()
  })
})

// ---------------------------------------------------------------------------
// The inspector header's measured counters (issue #184, Q3).
//
// The page displays one turn, so it reads one turn: the bounded per-turn
// route, not the session's whole payload — which carried up to 10,933 turn
// rows to describe the four counters the header renders.
// ---------------------------------------------------------------------------
describe('TurnDetail measured counters (bounded per-turn read)', () => {
  beforeEach(() => {
    rawPacketApi.fetchRun.mockReset()
    rawPacketApi.fetchTurnContent.mockReset()
    rawPacketApi.fetchKyberSessionTurns.mockReset()

    rawPacketApi.fetchRun.mockResolvedValue(RUN_WITH_PACKET)
    rawPacketApi.fetchTurnContent.mockResolvedValue(turnContent())
  })

  afterEach(() => {
    cleanup()
  })

  it('asks for the turn it displays, and renders that turn\'s counters', async () => {
    rawPacketApi.fetchKyberSessionTurns.mockResolvedValue({
      session_id: 'sess-raw-packet',
      turn_index: 0,
      index: 0,
      input: 900,
      output: 130,
      fresh: 50,
      cache_read: 860,
    })
    renderTurn()

    // The bounded read, for this turn alone: the session id and the turn
    // index, never the session's whole payload.
    await waitFor(() => {
      expect(rawPacketApi.fetchKyberSessionTurns).toHaveBeenCalledWith('sess-raw-packet', 0)
    })

    // The same counters the header renders, from the row the route served.
    const figures = await screen.findByTestId('turn-token-figures')
    expect(figures.textContent).toContain('In 900')
    expect(figures.textContent).toContain('Out 130')
    expect(figures.textContent).toContain('Fresh 50')
    expect(figures.textContent).toContain('Cache 860')
  })

  it('renders no counters when the route answers no row for that turn', async () => {
    // An unknown session or a turn index past the end is the route's 404:
    // the counters stay absent, as they did when the payload answered with
    // no such row.
    rawPacketApi.fetchKyberSessionTurns.mockRejectedValue(
      new KyberApiError(404, '/api/kyber/session/sess-raw-packet/turn/0'),
    )
    renderTurn()

    await waitFor(() => {
      expect(rawPacketApi.fetchKyberSessionTurns).toHaveBeenCalledWith('sess-raw-packet', 0)
    })
    await waitFor(() => {
      expect(screen.getByTestId('context-inspector')).toBeTruthy()
    })
    expect(screen.queryByTestId('turn-token-figures')).toBeNull()
    expect(screen.getByTestId('turn-token-figures-absent')).toBeTruthy()
  })
})
