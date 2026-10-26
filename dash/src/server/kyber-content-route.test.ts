import type { AddressInfo } from 'net'
import type { Server } from 'http'
import { tmpdir } from 'os'
import { join } from 'path'
import { DatabaseSync } from 'node:sqlite'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { vi } from 'vitest'

import { runWebDashboard } from '../cli/web.js'
import {
  CONTENT_RESPONSE_BUDGET,
  KyberBridge,
  MAX_STRING_LENGTH,
  type SessionContentResult,
  type TurnContentResult,
} from './bridge.js'
import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord, ContentPart } from '../canon/types.js'
import { useAbsentLiveReceiver } from '../../tests/setup/live-receiver-stub.js'

useAbsentLiveReceiver()

describe('Backend Contract Tests: GET /api/kyber/session/:id/content', () => {
  let server: Server
  let base: string
  let canonDb: DatabaseSync
  let store: CanonStore
  let testBridge: KyberBridge

  // Longer than `_clip`'s 2,000-character leaf cap — the reason this route exists.
  const SYSTEM_PROMPT =
    'You are a diagnostic coding agent.\nFollow the repository rules exactly.\n' +
    'x'.repeat(11_000)
  const TOOL_DEFINITION = '{"name":"read_file","description":"Read a file from the workspace"}'
  const CONVERSATION = 'user: why is the inspector showing a stub mid-sentence?'
  const HUGE_TOOL_RESULT = 'R'.repeat(CONTENT_RESPONSE_BUDGET + 50_000)
  // Longer than the session-content default response budget: the shared
  // session-level system prompt the whole-turn view reads losslessly.
  const HUGE_SYSTEM_PROMPT =
    'HUGE_SHARED_PROMPT_HEAD_7d2a\n' + 'S'.repeat(CONTENT_RESPONSE_BUDGET + 1_000) + '\nHUGE_SHARED_PROMPT_TAIL_c814'
  // Issue #216: Claude Desktop synth spans — conversation text the inspector must surface.
  const DESKTOP_CONVERSATION =
    'user: why are Claude Desktop inspector blocks empty?\nassistant: capture should attach parts.'

  // Claude Code OTLP turn content (plan 2026-10-10, B6/B7). The sender's
  // interaction span is quarantined and its packet is the only copy of the
  // prompt, so the turn view must render what the child request span carries
  // and must not repeat a session-level "not measurable" over text it has.
  const CLAUDE_PROMPT = 'why is the Claude turn inspector empty?'
  const CLAUDE_CONVERSATION = `user: ${CLAUDE_PROMPT}`
  const CLAUDE_TURN_SPAN = 'span-claude-turn-1'
  const CLAUDE_TURN_NO_PARTS_SPAN = 'span-claude-turn-2'
  const CLAUDE_INTERACTION_SPAN = 'span-claude-interaction-1'
  const CLAUDE_BARE_INTERACTION_SPAN = 'span-claude-interaction-2'
  const CLAUDE_INTERACTION_PACKET = JSON.stringify({
    name: 'claude_code.interaction',
    attributes: { 'span.type': 'interaction', user_prompt: CLAUDE_PROMPT },
  })
  const SYSTEM_PROMPT_REASON =
    'Claude Code did not capture the system prompt for this session.'
  const PROMPT_REDACTED_REASON =
    'The prompt text was redacted; set OTEL_LOG_USER_PROMPTS=1 to send it.'

  /**
   * B7 adds `parentSpanId` and `rawPacket` to `TurnContentResult`. They are
   * intersected here so this RED test compiles against the pre-T8 `bridge.ts`;
   * T8 folds both into the production type and this alias becomes redundant.
   */
  type TurnPacketPointers = {
    parentSpanId?: string
    rawPacket?: { spanAttributesPath: string; interactionPath?: string }
  }

  const tokens = () => ({
    freshInput: 1000,
    cacheRead: 0,
    cacheCreation: 0,
    output: 100,
    reportedInput: 1000,
    reportedOutput: 100,
  })

  function turn(
    spanId: string,
    parts: ContentPart[],
    over: Partial<CanonicalRecord> = {},
  ): CanonicalRecord {
    return {
      spanId,
      traceId: 'trace-content-001',
      parentSpanId: null,
      source: 'copilot',
      harness: 'copilot',
      sessionId: 'sess-content-001',
      name: 'llm_request',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: '2026-09-03T10:00:00.000Z',
      durationMs: 100,
      status: 'ok',
      tokens: tokens(),
      content: {},
      parts,
      cost: { basis: 'unknown', status: 'no_rate' },
      ...over,
    }
  }

  beforeAll(async () => {
    store = new CanonStore(':memory:')
    store.upsertMany([
      turn('span-prompt', [
        { part: 'system_prompt', text: SYSTEM_PROMPT, tokens: 5800 },
      ]),
      turn(
        'span-tools',
        [
          { part: 'tool_definitions', text: TOOL_DEFINITION, tokens: 24, server: 'built-in' },
          { part: 'conversation_history', text: CONVERSATION, tokens: 14 },
        ],
        { timestamp: '2026-09-03T10:01:00.000Z' },
      ),
      turn(
        'span-huge',
        [{ part: 'tool_result_content', text: HUGE_TOOL_RESULT }],
        { timestamp: '2026-09-03T10:02:00.000Z' },
      ),
    ])
    store.upsertSession({
      sessionId: 'sess-content-001',
      harness: 'copilot',
      label: 'Content drill-down',
      payload: { id: 'sess-content-001', harness: 'copilot' },
    })
    // Session-level system prompt longer than the session-content default
    // budget: the shared-context reader's lossless contract fixture.
    store.upsertMany([
      turn('span-huge-prompt', [{ part: 'system_prompt', text: HUGE_SYSTEM_PROMPT, tokens: 999 }], {
        sessionId: 'sess-huge-001',
        traceId: 'trace-huge-001',
        timestamp: '2026-09-03T10:00:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-huge-001',
      harness: 'copilot',
      label: 'Huge shared prompt',
      payload: { id: 'sess-huge-001', harness: 'copilot' },
    })
    // Captured-context recurrence: three turns sharing some exact part
    // texts while changing others, with explicit 0-based descriptors.
    const REC_SHARED = 'SHARED_PROMPT_REC_aaa'
    const recHistory = (user: string, assistant: string) =>
      JSON.stringify([
        { role: 'user', content: user },
        { role: 'assistant', content: assistant },
      ])
    store.upsertMany([
      turn(
        'span-r0',
        [
          { part: 'system_prompt', text: REC_SHARED, tokens: 10 },
          { part: 'conversation_history', text: recHistory('USER_Q_REC', 'ASSIST_A0_REC') },
          { part: 'tool_result_content', text: 'RESULT_R0_REC' },
          { part: 'custom_skill', text: 'SKILL_ALPHA_REC' } as unknown as ContentPart,
        ],
        { sessionId: 'sess-rec-001', traceId: 'trace-rec-001', timestamp: '2026-09-03T15:00:00.000Z' },
      ),
      turn(
        'span-r1',
        [
          { part: 'system_prompt', text: REC_SHARED, tokens: 10 },
          { part: 'tool_definitions', text: '{"name":"zeta_tool"}' },
          { part: 'conversation_history', text: recHistory('USER_Q_REC', 'ASSIST_A1_NEW') },
          { part: 'tool_result_content', text: 'RESULT_R1_NEW' },
          { part: 'custom_skill', text: 'SKILL_ALPHA_REC' } as unknown as ContentPart,
        ],
        { sessionId: 'sess-rec-001', traceId: 'trace-rec-001', timestamp: '2026-09-03T15:01:00.000Z' },
      ),
      turn(
        'span-r2',
        [
          { part: 'system_prompt', text: 'PROMPT_V2_NEW', tokens: 11 },
          { part: 'conversation_history', text: recHistory('USER_Q2_NEW', 'ASSIST_A2_NEW') },
        ],
        { sessionId: 'sess-rec-001', traceId: 'trace-rec-001', timestamp: '2026-09-03T15:02:00.000Z' },
      ),
    ])
    store.upsertSession({
      sessionId: 'sess-rec-001',
      harness: 'copilot',
      label: 'Recurrence labels',
      payload: {
        id: 'sess-rec-001',
        harness: 'copilot',
        turns: [
          { index: 0, spanId: 'span-r0', model: 'm' },
          { index: 1, spanId: 'span-r1', model: 'm' },
          { index: 2, spanId: 'span-r2', model: 'm' },
        ],
      },
    })
    // Session prompt living outside any turn span: the turn spans carry no
    // system prompt, so shared context must come from the session-wide
    // fallback scan — every matching part, in session order.
    const LONG_SESSION_PROMPT = 'SESSION_SCOPE_PROMPT_HEAD_1a2b\n' + 'P'.repeat(200) + '\nSESSION_SCOPE_PROMPT_TAIL_9e8f'
    store.upsertMany([
      turn(
        'span-f0',
        [{ part: 'tool_result_content', text: 'fallback turn output' }],
        { sessionId: 'sess-rec-fallback-001', traceId: 'trace-rec-fallback-001', timestamp: '2026-09-03T16:00:00.000Z' },
      ),
      turn(
        'span-f-session-a',
        [{ part: 'system_prompt', text: 'SESSION_SCOPE_PROMPT_ALPHA', tokens: 5 }] as unknown as ContentPart[],
        {
          sessionId: 'sess-rec-fallback-001',
          traceId: 'trace-rec-fallback-001',
          op: 'session.start',
          name: 'session_scope',
          timestamp: '2026-09-03T15:59:00.000Z',
        },
      ),
      turn(
        'span-f-session-b',
        [{ part: 'system_prompt', text: LONG_SESSION_PROMPT, tokens: 6 }] as unknown as ContentPart[],
        {
          sessionId: 'sess-rec-fallback-001',
          traceId: 'trace-rec-fallback-001',
          op: 'session.start',
          name: 'session_scope',
          timestamp: '2026-09-03T15:59:30.000Z',
        },
      ),
    ])
    store.upsertSession({
      sessionId: 'sess-rec-fallback-001',
      harness: 'copilot',
      label: 'Session-scope prompt fallback',
      payload: {
        id: 'sess-rec-fallback-001',
        harness: 'copilot',
        turns: [{ index: 0, spanId: 'span-f0', model: 'm' }],
      },
    })
    // Mixed session and turn prompts: the turn span carries its own system
    // prompt while non-turn session spans carry theirs — the reader must
    // keep both, in capture order, with no text deduplication.
    store.upsertMany([
      turn(
        'span-g0',
        [{ part: 'system_prompt', text: 'TURN_PROMPT_MIXED_ROUTE', tokens: 7 }] as unknown as ContentPart[],
        {
          sessionId: 'sess-rec-mixed-001',
          traceId: 'trace-rec-mixed-001',
          timestamp: '2026-09-03T16:00:00.000Z',
        },
      ),
      turn(
        'span-g-session-a',
        [{ part: 'system_prompt', text: 'SESSION_PROMPT_MIXED_ROUTE', tokens: 5 }] as unknown as ContentPart[],
        {
          sessionId: 'sess-rec-mixed-001',
          traceId: 'trace-rec-mixed-001',
          op: 'session.start',
          name: 'session_scope',
          timestamp: '2026-09-03T15:59:00.000Z',
        },
      ),
    ])
    store.upsertSession({
      sessionId: 'sess-rec-mixed-001',
      harness: 'copilot',
      label: 'Mixed session and turn prompts',
      payload: {
        id: 'sess-rec-mixed-001',
        harness: 'copilot',
        turns: [{ index: 0, spanId: 'span-g0', model: 'm' }],
      },
    })
    // Session whose payload carries explicit turn descriptors: one current
    // 0-based `index` row and one legacy 1-based `turn` row (issue #184).
    store.upsertMany([
      turn('span-m0', [{ part: 'system_prompt', text: 'mixed zero', tokens: 2 }], {
        sessionId: 'sess-mixed-001',
        timestamp: '2026-09-03T11:00:00.000Z',
      }),
      turn('span-m1', [{ part: 'system_prompt', text: 'mixed one', tokens: 2 }], {
        sessionId: 'sess-mixed-001',
        timestamp: '2026-09-03T11:01:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-mixed-001',
      harness: 'copilot',
      label: 'Mixed turn descriptors',
      payload: {
        id: 'sess-mixed-001',
        harness: 'copilot',
        turns: [
          { index: 0, spanId: 'span-m0', model: 'm' },
          { turn: 2, spanId: 'span-m1', model: 'm' },
        ],
      },
    })
    // Payload rows whose legacy identity disagrees with array position
    // (issue #184 review): explicit identity must win over position.
    store.upsertMany([
      turn('span-pa', [{ part: 'system_prompt', text: 'precedence A', tokens: 1 }], {
        sessionId: 'sess-precedence-001',
        timestamp: '2026-09-03T12:00:00.000Z',
      }),
      turn('span-pb', [{ part: 'system_prompt', text: 'precedence B', tokens: 1 }], {
        sessionId: 'sess-precedence-001',
        timestamp: '2026-09-03T12:01:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-precedence-001',
      harness: 'copilot',
      label: 'Precedence descriptors',
      payload: {
        id: 'sess-precedence-001',
        harness: 'copilot',
        turns: [
          { turn: 2, spanId: 'span-pa', model: 'm' },
          { turn: 1, spanId: 'span-pb', model: 'm' },
        ],
      },
    })
    // Identity-bearing payload shorter than the record pool (issue #184
    // review): an unmatched identity must 404, not fall through to a
    // positional record match.
    store.upsertMany([
      turn('span-x0', [{ part: 'system_prompt', text: 'cross X0', tokens: 1 }], {
        sessionId: 'sess-cross-001',
        timestamp: '2026-09-03T13:00:00.000Z',
      }),
      turn('span-x1', [{ part: 'system_prompt', text: 'cross X1', tokens: 1 }], {
        sessionId: 'sess-cross-001',
        timestamp: '2026-09-03T13:01:00.000Z',
      }),
      turn('span-x2', [{ part: 'system_prompt', text: 'cross X2', tokens: 1 }], {
        sessionId: 'sess-cross-001',
        timestamp: '2026-09-03T13:02:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-cross-001',
      harness: 'copilot',
      label: 'Cross-source descriptors',
      payload: {
        id: 'sess-cross-001',
        harness: 'copilot',
        turns: [
          { index: 0, spanId: 'span-x0', model: 'm' },
          { index: 1, spanId: 'span-x1', model: 'm' },
        ],
      },
    })
    // Payload rows without any identity (issue #184 review): the canonical
    // positional lookup stays available as the only option.
    store.upsertMany([
      turn('span-q0', [{ part: 'system_prompt', text: 'noident Q0', tokens: 1 }], {
        sessionId: 'sess-noidentity-001',
        timestamp: '2026-09-03T14:00:00.000Z',
      }),
      turn('span-q1', [{ part: 'system_prompt', text: 'noident Q1', tokens: 1 }], {
        sessionId: 'sess-noidentity-001',
        timestamp: '2026-09-03T14:01:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-noidentity-001',
      harness: 'copilot',
      label: 'Identity-free descriptors',
      payload: {
        id: 'sess-noidentity-001',
        harness: 'copilot',
        turns: [{ spanId: 'span-q0', model: 'm' }],
      },
    })

    // Issue #216: codeburn/claude-desktop synth: span with conversation parts
    // (the HTTP shape the inspector should see once capture lands).
    const desktopPartsSpan =
      'synth:claude-desktop:sess-desktop-parts-001:msg-with-parts'
    store.upsertMany([
      turn(
        desktopPartsSpan,
        [{ part: 'conversation_history', text: DESKTOP_CONVERSATION, tokens: 22 }],
        {
          source: 'codeburn/claude-desktop',
          harness: 'claude-desktop',
          sessionId: 'sess-desktop-parts-001',
          traceId: 'trace-desktop-parts-001',
          name: 'claude-sonnet-5-5',
          timestamp: '2026-09-30T10:00:00.000Z',
        },
      ),
    ])
    store.upsertSession({
      sessionId: 'sess-desktop-parts-001',
      harness: 'claude-desktop',
      label: 'Claude Desktop with parts',
      payload: {
        id: 'sess-desktop-parts-001',
        harness: 'claude-desktop',
        turns: [{ index: 0, spanId: desktopPartsSpan, model: 'claude-sonnet-5-5' }],
      },
    })

    // Issue #216 control: counters-only desktop synth (no reader parts) must
    // stay honestly empty — the route must not fabricate inspector text.
    const desktopEmptySpan =
      'synth:claude-desktop:sess-desktop-empty-001:msg-counters-only'
    store.upsertMany([
      turn(desktopEmptySpan, [], {
        source: 'codeburn/claude-desktop',
        harness: 'claude-desktop',
        sessionId: 'sess-desktop-empty-001',
        traceId: 'trace-desktop-empty-001',
        name: 'claude-sonnet-5-5',
        timestamp: '2026-09-30T11:00:00.000Z',
        parts: undefined,
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-desktop-empty-001',
      harness: 'claude-desktop',
      label: 'Claude Desktop counters-only',
      payload: {
        id: 'sess-desktop-empty-001',
        harness: 'claude-desktop',
        turns: [{ index: 0, spanId: desktopEmptySpan, model: 'claude-sonnet-5-5' }],
      },
    })

    // Claude Code OTLP (plan 2026-10-10, B6/B7): two request turns whose parent
    // interaction spans were quarantined. Turn 0 carries the prompt text the
    // receiver recovered; turn 1 carries none. Both parents have an audit row,
    // but only the first kept a raw packet.
    store.upsertMany([
      turn(
        CLAUDE_TURN_SPAN,
        [{ part: 'conversation_history', text: CLAUDE_CONVERSATION, tokens: 12 }],
        {
          source: 'claude-code',
          harness: 'claude-code',
          sessionId: 'sess-claude-content-001',
          traceId: 'trace-claude-content-001',
          name: 'claude_code.llm_request',
          parentSpanId: CLAUDE_INTERACTION_SPAN,
          timestamp: '2026-10-10T10:00:00.000Z',
        },
      ),
      turn(CLAUDE_TURN_NO_PARTS_SPAN, [], {
        source: 'claude-code',
        harness: 'claude-code',
        sessionId: 'sess-claude-content-001',
        traceId: 'trace-claude-content-001',
        name: 'claude_code.llm_request',
        parentSpanId: CLAUDE_BARE_INTERACTION_SPAN,
        timestamp: '2026-10-10T10:01:00.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-claude-content-001',
      harness: 'claude-code',
      label: 'Claude Code turn content',
      payload: {
        id: 'sess-claude-content-001',
        harness: 'claude-code',
        turns: [
          { index: 0, spanId: CLAUDE_TURN_SPAN, model: 'claude-sonnet-5-5' },
          { index: 1, spanId: CLAUDE_TURN_NO_PARTS_SPAN, model: 'claude-sonnet-5-5' },
        ],
        // Content buckets the session never saw. The turn view must drop this
        // overlay for a turn that has parts of its own (B6/R6).
        context: {
          first: {
            buckets: {
              system_prompt: { availability: 'not_measurable', reason: SYSTEM_PROMPT_REASON },
              conversation_history: {
                availability: 'not_measurable',
                reason: PROMPT_REDACTED_REASON,
              },
            },
          },
        },
      },
    })
    // The interaction spans are already out of the corpus; only their quarantine
    // rows remain. T14 adds `rawPayload`/`harness` to `quarantineAndDelete`, so
    // the metadata travels through a variable rather than a fresh literal.
    const quarantinedPacket = {
      source: 'claude-code',
      name: 'claude_code.interaction',
      timestamp: '2026-10-10T10:00:00.000Z',
      rawPayload: CLAUDE_INTERACTION_PACKET,
      harness: 'claude-code',
    }
    store.quarantineAndDelete(
      CLAUDE_INTERACTION_SPAN,
      ['claude_code'],
      'non-model span',
      quarantinedPacket,
    )
    store.quarantine(CLAUDE_BARE_INTERACTION_SPAN, ['claude_code'], 'non-model span', {
      source: 'claude-code',
      name: 'claude_code.interaction',
      timestamp: '2026-10-10T10:01:00.000Z',
    })

    canonDb = new DatabaseSync(':memory:')

    testBridge = new KyberBridge({
      store,
      canonDb,
      ratesPath: join(tmpdir(), 'nonexistent-rates.json'),
    })

    server = await runWebDashboard({ port: 0, open: false, kyberBridge: testBridge, writeStdout: () => {} })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    store.close()
  })

  function assertStandardKyberHeaders(res: Response) {
    const contentType = res.headers.get('content-type')
    expect(contentType).toBeDefined()
    expect(contentType).toContain('application/json')
    expect(contentType).toContain('charset=utf-8')
    expect(res.headers.get('cache-control')).toBe('no-store')
  }

  describe('GET /api/kyber/session/:id/content', () => {
    it('returns a system prompt longer than 2000 characters untruncated', async () => {
      expect(SYSTEM_PROMPT.length).toBeGreaterThan(MAX_STRING_LENGTH)

      const res = await fetch(`${base}/api/kyber/session/sess-content-001/content?span=span-prompt`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as SessionContentResult
      expect(body.sessionId).toBe('sess-content-001')
      expect(body.spanId).toBe('span-prompt')
      expect(body.parts).toHaveLength(1)
      expect(body.parts[0].part).toBe('system_prompt')
      expect(body.parts[0].text).toBe(SYSTEM_PROMPT)
      expect(body.parts[0].text.length).toBe(SYSTEM_PROMPT.length)
      expect(body.parts[0].text).not.toContain('[truncated')
      expect(body.parts[0].truncated).toBeUndefined()
      expect(body.parts[0].tokens).toBe(5800)
    })

    it('filters content to one span', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/content?span=span-tools`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as SessionContentResult
      expect(body.spanId).toBe('span-tools')
      expect(body.parts.map((p) => p.part)).toEqual(['tool_definitions', 'conversation_history'])
      expect(body.parts.every((p) => p.spanId === 'span-tools')).toBe(true)
      expect(body.parts[0].text).toBe(TOOL_DEFINITION)
      expect(body.parts[0].server).toBe('built-in')
      expect(body.parts[1].text).toBe(CONVERSATION)
    })

    it('filters content to one canonical bucket', async () => {
      const res = await fetch(
        `${base}/api/kyber/session/sess-content-001/content?part=tool_definitions`,
      )
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as SessionContentResult
      expect(body.spanId).toBeUndefined()
      expect(body.parts).toHaveLength(1)
      expect(body.parts[0].part).toBe('tool_definitions')
      expect(body.parts[0].text).toBe(TOOL_DEFINITION)
      expect(body.parts[0].server).toBe('built-in')
      expect(body.parts[0].tokens).toBe(24)
    })

    it('flags truncation and reports totalLength when the response budget is exceeded', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/content?span=span-huge`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as SessionContentResult
      expect(body.parts).toHaveLength(1)
      const part = body.parts[0]
      expect(part.part).toBe('tool_result_content')
      expect(part.truncated).toBe(true)
      expect(part.totalLength).toBe(HUGE_TOOL_RESULT.length)
      expect(part.text.length).toBe(CONTENT_RESPONSE_BUDGET)
      expect(part.text).toBe(HUGE_TOOL_RESULT.slice(0, CONTENT_RESPONSE_BUDGET))
      expect(part.tokens).toBeUndefined()
    })

    describe('explicit lossless session-content path (?full=true)', () => {
      it('keeps the default capped behavior for existing callers', async () => {
        expect(HUGE_SYSTEM_PROMPT.length).toBeGreaterThan(CONTENT_RESPONSE_BUDGET)

        const res = await fetch(`${base}/api/kyber/session/sess-huge-001/content?part=system_prompt`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as SessionContentResult
        expect(body.parts).toHaveLength(1)
        const part = body.parts[0]
        expect(part.part).toBe('system_prompt')
        expect(part.truncated).toBe(true)
        expect(part.totalLength).toBe(HUGE_SYSTEM_PROMPT.length)
        expect(part.text.length).toBe(CONTENT_RESPONSE_BUDGET)
        expect(part.text).toBe(HUGE_SYSTEM_PROMPT.slice(0, CONTENT_RESPONSE_BUDGET))
      })

      it('returns an over-budget system prompt exact with no truncation flags', async () => {
        expect(HUGE_SYSTEM_PROMPT.length).toBeGreaterThan(CONTENT_RESPONSE_BUDGET)

        const res = await fetch(`${base}/api/kyber/session/sess-huge-001/content?part=system_prompt&full=true`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as SessionContentResult
        expect(body.parts).toHaveLength(1)
        const part = body.parts[0]
        expect(part.part).toBe('system_prompt')
        expect(part.text).toBe(HUGE_SYSTEM_PROMPT)
        expect(part.text).toContain('HUGE_SHARED_PROMPT_HEAD_7d2a')
        expect(part.text).toContain('HUGE_SHARED_PROMPT_TAIL_c814')
        expect(part.truncated).toBeUndefined()
        expect(part.totalLength).toBeUndefined()
        expect(part.tokens).toBe(999)
      })
    })
  })

  describe('Edge cases and error handling', () => {
    it('returns HTTP 404 JSON for an unknown session', async () => {
      const res = await fetch(`${base}/api/kyber/session/nonexistent-xyz-999/content`)
      expect(res.status).toBe(404)
      assertStandardKyberHeaders(res)
      const body = (await res.json()) as { error: string }
      expect(body).toEqual({ error: 'Session not found' })
    })

    it('returns HTTP 405 Method Not Allowed JSON with no-store for non-GET methods', async () => {
      const url = `${base}/api/kyber/session/sess-content-001/content`
      for (const method of ['POST', 'PUT', 'DELETE', 'PATCH']) {
        const res = await fetch(url, { method })
        expect(res.status).toBe(405)
        assertStandardKyberHeaders(res)
        const body = (await res.json()) as { error: string }
        expect(body).toEqual({ error: 'Method Not Allowed' })
      }
    })
  })

  describe('GET /api/kyber/session/:id/turn/:index/content (Task G1 / Decision D14)', () => {
    it('returns assembled turn content with blocks, parts, and full assembledText', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/0/content`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as TurnContentResult
      expect(body.sessionId).toBe('sess-content-001')
      expect(body.turnIndex).toBe(0)
      expect(body.spanId).toBe('span-prompt')
      expect(body.blocks).toBeDefined()
      expect(body.parts).toBeDefined()
      expect(body.assembledText).toBeDefined()

      const sysBlock = body.blocks.find((b) => b.key === 'system_prompt')
      expect(sysBlock).toBeDefined()
      expect(sysBlock?.text).toBe(SYSTEM_PROMPT)
      expect(body.assembledText).toContain(SYSTEM_PROMPT)
    })

    it('returns parts with tools, user messages, and conversation history for turn 1', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/1/content`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as TurnContentResult
      expect(body.spanId).toBe('span-tools')
      expect(body.parts.some((p) => p.part === 'tool_definitions')).toBe(true)
      expect(body.parts.some((p) => p.part === 'user_messages' || p.part === 'conversation_history')).toBe(true)

      const toolPart = body.parts.find((p) => p.part === 'tool_definitions')
      expect(toolPart?.text).toBe(TOOL_DEFINITION)
      expect(toolPart?.server).toBe('built-in')

      const convPart = body.parts.find((p) => p.part === 'user_messages' || p.part === 'conversation_history')
      expect(convPart?.text).toContain('why is the inspector showing a stub mid-sentence?')
    })

    it('supports ?turn=1 query param on /session/:id/content as alias', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/content?turn=1`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as TurnContentResult
      expect(body.spanId).toBe('span-tools')
      expect(body.turnIndex).toBe(1)
    })

    it('returns 404 for nonexistent turn index', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/999/content`)
      expect(res.status).toBe(404)
      assertStandardKyberHeaders(res)
    })

    describe('strict 0-based turn resolution (issue #184)', () => {
      it('returns 404 for a turn index equal to the turn count instead of the last turn', async () => {
        // sess-content-001 holds 3 turns (indices 0-2). The 1-based
        // `turns[turnIndex - 1]` fallback serves the last turn with 200 here.
        const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/3/content`)
        expect(res.status).toBe(404)
        assertStandardKyberHeaders(res)
      })

      it('resolves a 0-based index to exactly that span', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/2/content`)
        expect(res.status).toBe(200)
        const body = (await res.json()) as TurnContentResult
        expect(body.turnIndex).toBe(2)
        expect(body.spanId).toBe('span-huge')
      })

      it('resolves a legacy 1-based `turn` descriptor via `turn - 1`', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-mixed-001/turn/1/content`)
        expect(res.status).toBe(200)
        const body = (await res.json()) as TurnContentResult
        expect(body.spanId).toBe('span-m1')
        expect(body.assembledText).toContain('mixed one')
      })

      it('returns 404 past the end of a session with legacy descriptors', async () => {
        // Two payload turns: valid 0-based indices are 0 and 1.
        const res = await fetch(`${base}/api/kyber/session/sess-mixed-001/turn/2/content`)
        expect(res.status).toBe(404)
        assertStandardKyberHeaders(res)
      })

      it('prefers explicit legacy identity over array position (issue #184 review)', async () => {
        // Payload order is [{turn: 2}, {turn: 1}]: transport 0 is the second
        // row (span-pb), transport 1 the first (span-pa). A positional match
        // would serve span-pa for both.
        const first = await fetch(`${base}/api/kyber/session/sess-precedence-001/turn/0/content`)
        expect(first.status).toBe(200)
        const firstBody = (await first.json()) as TurnContentResult
        expect(firstBody.spanId).toBe('span-pb')
        expect(firstBody.assembledText).toContain('precedence B')

        const second = await fetch(`${base}/api/kyber/session/sess-precedence-001/turn/1/content`)
        expect(second.status).toBe(200)
        const secondBody = (await second.json()) as TurnContentResult
        expect(secondBody.spanId).toBe('span-pa')
        expect(secondBody.assembledText).toContain('precedence A')
      })

      it('returns 404 when an identity-bearing payload misses, without falling through to records (issue #184 review)', async () => {
        // Two identified payload turns but three records: transport 2 matches
        // no identity, and must not resolve positionally to span-x2.
        const res = await fetch(`${base}/api/kyber/session/sess-cross-001/turn/2/content`)
        expect(res.status).toBe(404)
        assertStandardKyberHeaders(res)
      })

      it('resolves identified payload turns while the cross-source guard stands', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-cross-001/turn/1/content`)
        expect(res.status).toBe(200)
        const body = (await res.json()) as TurnContentResult
        expect(body.spanId).toBe('span-x1')
        expect(body.assembledText).toContain('cross X1')
      })

      it('preserves the canonical positional lookup for identity-free payload rows (issue #184 review)', async () => {
        // One identity-free payload row, two records: transport 1 has no
        // payload answer, so the positional record lookup still serves span-q1.
        const res = await fetch(`${base}/api/kyber/session/sess-noidentity-001/turn/1/content`)
        expect(res.status).toBe(200)
        const body = (await res.json()) as TurnContentResult
        expect(body.spanId).toBe('span-q1')
        expect(body.assembledText).toContain('noident Q1')
      })
    })

    it('flags budget truncation when turn content exceeds budget param', async () => {
      const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/2/content?budget=100`)
      expect(res.status).toBe(200)
      assertStandardKyberHeaders(res)

      const body = (await res.json()) as TurnContentResult
      expect(body.truncated).toBe(true)
      expect(body.totalLength).toBeGreaterThan(100)
      expect(body.assembledText.length).toBeLessThanOrEqual(100)
    })

    it('ignores a negative or non-numeric budget instead of slicing from the end', async () => {
      const baseline = await fetch(`${base}/api/kyber/session/sess-content-001/turn/2/content`)
      const baselineBody = (await baseline.json()) as TurnContentResult

      for (const query of ['budget=-1', 'budget=-100', 'budget=abc']) {
        for (const path of [
          `/api/kyber/session/sess-content-001/turn/2/content?${query}`,
          `/api/kyber/session/sess-content-001/content?turn=2&${query}`,
        ]) {
          const res = await fetch(`${base}${path}`)
          expect(res.status).toBe(200)
          const body = (await res.json()) as TurnContentResult
          expect(body.truncated).toBe(true)
          expect(body.assembledText.length).toBe(baselineBody.assembledText.length)
          expect(body.assembledText).toBe(baselineBody.assembledText)
        }
      }
    })

    describe('explicit lossless full-content path (?full=true)', () => {
      it('truncates over-budget content by default', async () => {
        expect(HUGE_TOOL_RESULT.length).toBeGreaterThan(CONTENT_RESPONSE_BUDGET)

        const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/2/content`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as TurnContentResult
        // The assembled turn text wraps the block with a section header, so
        // the total exceeds the raw part by exactly that header length.
        const header = '=== TOOL RESULTS ===\n'
        expect(body.truncated).toBe(true)
        expect(body.totalLength).toBe(HUGE_TOOL_RESULT.length + header.length)
        expect(body.assembledText.length).toBe(CONTENT_RESPONSE_BUDGET)
        expect(body.assembledText).toBe((header + HUGE_TOOL_RESULT).slice(0, CONTENT_RESPONSE_BUDGET))
      })

      it('returns over-budget source text intact with ?full=true', async () => {
        expect(HUGE_TOOL_RESULT.length).toBeGreaterThan(CONTENT_RESPONSE_BUDGET)

        const res = await fetch(`${base}/api/kyber/session/sess-content-001/turn/2/content?full=true`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as TurnContentResult
        expect(body.truncated).toBeUndefined()
        expect(body.totalLength).toBeUndefined()
        expect(body.assembledText).toBe(`=== TOOL RESULTS ===\n${HUGE_TOOL_RESULT}`)
        expect(body.assembledText).toContain(HUGE_TOOL_RESULT)
        const toolBlock = body.blocks.find((b) => b.key === 'tool_result_content')
        expect(toolBlock?.truncated).toBeUndefined()
        expect(toolBlock?.text).toBe(HUGE_TOOL_RESULT)
      })

      it('supports ?full=1 and the ?turn= alias lossless path', async () => {
        const direct = await fetch(`${base}/api/kyber/session/sess-content-001/turn/2/content?full=1`)
        expect(direct.status).toBe(200)
        const directBody = (await direct.json()) as TurnContentResult
        expect(directBody.assembledText).toContain(HUGE_TOOL_RESULT)
        expect(directBody.truncated).toBeUndefined()

        const alias = await fetch(`${base}/api/kyber/session/sess-content-001/content?turn=2&full=true`)
        expect(alias.status).toBe(200)
        assertStandardKyberHeaders(alias)
        const aliasBody = (await alias.json()) as TurnContentResult
        expect(aliasBody.turnIndex).toBe(2)
        expect(aliasBody.assembledText).toContain(HUGE_TOOL_RESULT)
        expect(aliasBody.truncated).toBeUndefined()
      })
    })

    describe('captured-context recurrence (exact whole-part text)', () => {
      async function turnBody(index: number): Promise<TurnContentResult> {
        const res = await fetch(`${base}/api/kyber/session/sess-rec-001/turn/${index}/content?full=true`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)
        return (await res.json()) as TurnContentResult
      }

      function blockOf(body: TurnContentResult, key: string) {
        const block = body.blocks.find((b) => b.key === key)
        expect(block).toBeDefined()
        return block!
      }

      it('marks every part new on the first turn with earliest shared context', async () => {
        const body = await turnBody(0)
        expect(body.spanId).toBe('span-r0')
        for (const part of body.parts) {
          expect(part.recurrence).toEqual({ seenBefore: false })
        }
        for (const block of body.blocks) {
          if (block.parts.length > 0) {
            expect(block.recurrence).toEqual({ newThisTurn: true, seenBefore: false })
          }
        }
        expect(body.sharedContext).toEqual({
          parts: [{ text: 'SHARED_PROMPT_REC_aaa', spanId: 'span-r0', turnIndex: 0 }],
          seenBefore: false,
        })
      })

      it('marks a repeated-only block seen with no new claim', async () => {
        const body = await turnBody(1)
        const sys = blockOf(body, 'system_prompt')
        expect(sys.text).toBe('SHARED_PROMPT_REC_aaa')
        expect(sys.recurrence).toEqual({ newThisTurn: false, seenBefore: true })
        expect(sys.parts).toHaveLength(1)
        expect(sys.parts[0].recurrence).toEqual({ seenBefore: true })
      })

      it('marks a changed same-kind part new without calling it cached', async () => {
        const body = await turnBody(1)
        const results = blockOf(body, 'tool_result_content')
        expect(results.text).toBe('RESULT_R1_NEW')
        expect(results.recurrence).toEqual({ newThisTurn: true, seenBefore: false })
        // Recurrence never converts into a cache claim anywhere in the body.
        const serialized = JSON.stringify(body)
        expect(serialized).not.toContain('cached')
        expect(serialized).not.toContain('cache_read')
        expect(serialized).not.toContain('"cache"')
      })

      it('treats a part kind absent from earlier turns as new, not repeated', async () => {
        const body = await turnBody(1)
        const tools = blockOf(body, 'tool_definitions')
        expect(tools.text).toBe('{"name":"zeta_tool"}')
        expect(tools.recurrence).toEqual({ newThisTurn: true, seenBefore: false })
      })

      it('shows both labels on one block mixing new and repeated parts', async () => {
        const body = await turnBody(1)
        const history = blockOf(body, 'conversation_history')
        const kinds = history.parts.map((p) => p.part).sort()
        expect(kinds).toEqual(['assistant_turns', 'user_messages'])
        const user = history.parts.find((p) => p.part === 'user_messages')!
        const assistant = history.parts.find((p) => p.part === 'assistant_turns')!
        expect(user.text).toBe('USER_Q_REC')
        expect(user.recurrence).toEqual({ seenBefore: true })
        expect(assistant.text).toBe('ASSIST_A1_NEW')
        expect(assistant.recurrence).toEqual({ seenBefore: false })
        expect(history.recurrence).toEqual({ newThisTurn: true, seenBefore: true })
      })

      it('passes unknown part kinds through with recurrence', async () => {
        const body = await turnBody(1)
        const skill = body.parts.find((p) => p.part === 'custom_skill')
        expect(skill).toBeDefined()
        expect(skill!.text).toBe('SKILL_ALPHA_REC')
        expect(skill!.recurrence).toEqual({ seenBefore: true })
      })

      it('reports shared context with its own earlier-turn recurrence', async () => {
        const first = await turnBody(1)
        // Every matching part is preserved: turn 0's prompt plus the turn's
        // own identical prompt, each with its contributing turnIndex. The
        // client suppresses only the selected-turn occurrence from display.
        expect(first.sharedContext).toEqual({
          parts: [
            { text: 'SHARED_PROMPT_REC_aaa', spanId: 'span-r0', turnIndex: 0 },
            { text: 'SHARED_PROMPT_REC_aaa', spanId: 'span-r1', turnIndex: 1 },
          ],
          seenBefore: true,
        })
        // The earliest prompt stays shared even after a turn carries its own.
        const second = await turnBody(2)
        expect(second.sharedContext?.parts).toEqual([
          { text: 'SHARED_PROMPT_REC_aaa', spanId: 'span-r0', turnIndex: 0 },
          { text: 'SHARED_PROMPT_REC_aaa', spanId: 'span-r1', turnIndex: 1 },
          { text: 'PROMPT_V2_NEW', spanId: 'span-r2', turnIndex: 2 },
        ])
        expect(second.sharedContext?.seenBefore).toBe(true)
        const own = blockOf(second, 'system_prompt')
        expect(own.text).toBe('PROMPT_V2_NEW')
        expect(own.recurrence).toEqual({ newThisTurn: true, seenBefore: false })
      })

      it('preserves every session-scope prompt part via the non-turn fallback', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-rec-fallback-001/turn/0/content?full=true`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)
        const body = (await res.json()) as TurnContentResult
        expect(body.spanId).toBe('span-f0')
        // Both session-scope prompts survive in session order — none dropped.
        expect(body.sharedContext?.parts.map((p) => p.spanId)).toEqual(['span-f-session-a', 'span-f-session-b'])
        expect(body.sharedContext?.parts[0].text).toBe('SESSION_SCOPE_PROMPT_ALPHA')
        expect(body.sharedContext?.parts[1].text).toContain('SESSION_SCOPE_PROMPT_HEAD_1a2b')
        expect(body.sharedContext?.parts[1].text).toContain('SESSION_SCOPE_PROMPT_TAIL_9e8f')
        expect(body.sharedContext?.seenBefore).toBe(false)
        expect(body.truncated).toBeUndefined()
        // The turn's own non-prompt content is unaffected.
        expect(body.assembledText).toContain('fallback turn output')
      })

      it('keeps mixed session and turn prompts together in capture order', async () => {
        const res = await fetch(`${base}/api/kyber/session/sess-rec-mixed-001/turn/0/content?full=true`)
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)
        const body = (await res.json()) as TurnContentResult
        expect(body.spanId).toBe('span-g0')
        // The non-turn session prompt is not dropped merely because the
        // turn span carries its own prompt: both render, in capture order,
        // with exact texts and distinct span ids.
        expect(body.sharedContext?.parts).toEqual([
          { text: 'SESSION_PROMPT_MIXED_ROUTE', spanId: 'span-g-session-a' },
          { text: 'TURN_PROMPT_MIXED_ROUTE', spanId: 'span-g0', turnIndex: 0 },
        ])
        expect(body.sharedContext?.seenBefore).toBe(false)
        expect(body.blocks.find((b) => b.key === 'system_prompt')?.text).toBe('TURN_PROMPT_MIXED_ROUTE')
        expect(body.truncated).toBeUndefined()
      })

      it('budgets shared parts truthfully by default and exactly when full', async () => {
        const capped = await fetch(`${base}/api/kyber/session/sess-rec-fallback-001/turn/0/content?budget=10`)
        expect(capped.status).toBe(200)
        const cappedBody = (await capped.json()) as TurnContentResult
        expect(cappedBody.truncated).toBe(true)
        expect(cappedBody.sharedContext).toBeDefined()
        expect(cappedBody.sharedContext!.parts).toHaveLength(2)
        expect(cappedBody.sharedContext!.parts[0].text).toBe('SESSION_SC')
        expect(cappedBody.sharedContext!.parts[0].truncated).toBe(true)
        expect(cappedBody.sharedContext!.parts[0].totalLength).toBe('SESSION_SCOPE_PROMPT_ALPHA'.length)
        expect(cappedBody.sharedContext!.parts[1].truncated).toBe(true)
        expect(cappedBody.sharedContext!.parts[1].totalLength).toBeGreaterThan(200)

        const full = await fetch(`${base}/api/kyber/session/sess-rec-fallback-001/turn/0/content?full=true`)
        expect(full.status).toBe(200)
        const fullBody = (await full.json()) as TurnContentResult
        expect(fullBody.sharedContext?.parts[0].text).toBe('SESSION_SCOPE_PROMPT_ALPHA')
        expect(fullBody.sharedContext?.parts[0].truncated).toBeUndefined()
        expect(fullBody.sharedContext?.parts[1].text).toContain('SESSION_SCOPE_PROMPT_TAIL_9e8f')
        expect(fullBody.sharedContext?.parts[1].truncated).toBeUndefined()
        expect(fullBody.sharedContext?.parts[1].totalLength).toBeUndefined()
      })
    })

    describe('claude-desktop synth parts (issue #216)', () => {
      it('returns non-empty parts and assembledText for a desktop synth span that has conversation parts', async () => {
        const res = await fetch(
          `${base}/api/kyber/session/sess-desktop-parts-001/turn/0/content`,
        )
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as TurnContentResult
        expect(body.sessionId).toBe('sess-desktop-parts-001')
        expect(body.turnIndex).toBe(0)
        expect(body.spanId).toBe(
          'synth:claude-desktop:sess-desktop-parts-001:msg-with-parts',
        )
        expect(body.parts.length).toBeGreaterThan(0)
        // assembleTurnContent splits parseable conversation_history into
        // user_messages / assistant_turns (same contract as the copilot cases).
        expect(
          body.parts.some(
            (p) =>
              p.part === 'conversation_history' ||
              p.part === 'user_messages' ||
              p.part === 'assistant_turns',
          ),
        ).toBe(true)
        expect(body.assembledText).toContain('why are Claude Desktop inspector blocks empty?')
        expect(body.assembledText).toContain('capture should attach parts.')
      })

      it('returns honest empty parts for a counters-only desktop synth span (no fabrication)', async () => {
        const res = await fetch(
          `${base}/api/kyber/session/sess-desktop-empty-001/turn/0/content`,
        )
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as TurnContentResult
        expect(body.sessionId).toBe('sess-desktop-empty-001')
        expect(body.spanId).toBe(
          'synth:claude-desktop:sess-desktop-empty-001:msg-counters-only',
        )
        expect(body.parts).toEqual([])
        expect(body.assembledText).toBe('')
        // Blocks may exist as structure, but none carry fabricated text.
        expect(body.blocks.every((b) => b.text === '')).toBe(true)
      })
    })

    describe('Claude Code OTLP turn content (plan 2026-10-10 B6/B7)', () => {
      it('renders a text-bearing Claude turn block without notMeasurable while a partless bucket keeps its reason', async () => {
        const res = await fetch(
          `${base}/api/kyber/session/sess-claude-content-001/turn/0/content`,
        )
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as TurnContentResult & TurnPacketPointers

        expect(body.sessionId).toBe('sess-claude-content-001')
        expect(body.spanId).toBe(CLAUDE_TURN_SPAN)

        const convBlock = body.blocks.find((b) => b.key === 'conversation_history')
        expect(convBlock).toBeDefined()
        expect(convBlock?.parts.length).toBeGreaterThan(0)
        // The assembled block text is the message body; the `user:` label the
        // history parser strips is asserted on the part, not the block.
        expect(convBlock?.text).toContain(CLAUDE_PROMPT)
        // R6: a session-level declaration must not survive onto a block this
        // turn actually carries text for.
        expect(convBlock?.notMeasurable).toBeUndefined()

        // The system prompt is genuinely absent from this turn, so it stays
        // declared not measurable, with the reason that explains the gap.
        const sysBlock = body.blocks.find((b) => b.key === 'system_prompt')
        expect(sysBlock).toBeDefined()
        expect(sysBlock?.parts).toEqual([])
        expect(sysBlock?.text).toBe('')
        expect(sysBlock?.notMeasurable).toBeDefined()
        expect(sysBlock?.notMeasurable?.reason).toContain('system prompt')
      })

      it('points a Claude turn at its own attribute path and at the quarantined parent packet that stored a payload', async () => {
        const res = await fetch(
          `${base}/api/kyber/session/sess-claude-content-001/turn/0/content`,
        )
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as TurnContentResult & TurnPacketPointers

        expect(body.rawPacket).toBeDefined()
        // The turn's own request span is stored, so its attributes are reachable.
        expect(body.rawPacket?.spanAttributesPath).toBe(
          `/api/kyber/span/${CLAUDE_TURN_SPAN}/attributes`,
        )
        // The interaction span is quarantined and kept its packet: the only copy
        // of the prompt text the sender exported.
        expect(body.rawPacket?.interactionPath).toBe(
          `/api/kyber/quarantine/${CLAUDE_INTERACTION_SPAN}`,
        )
        expect(body.parentSpanId).toBe(CLAUDE_INTERACTION_SPAN)
      })

      it('keeps notMeasurable with an env-var reason on a partless turn and omits interactionPath when the quarantine row stored no payload', async () => {
        const res = await fetch(
          `${base}/api/kyber/session/sess-claude-content-001/turn/1/content`,
        )
        expect(res.status).toBe(200)
        assertStandardKyberHeaders(res)

        const body = (await res.json()) as TurnContentResult & TurnPacketPointers

        expect(body.spanId).toBe(CLAUDE_TURN_NO_PARTS_SPAN)
        expect(body.parts).toEqual([])

        const convBlock = body.blocks.find((b) => b.key === 'conversation_history')
        expect(convBlock?.parts).toEqual([])
        // Still missing, so it still says which setting would supply it (B6).
        expect(convBlock?.notMeasurable?.reason).toContain('OTEL_LOG_USER_PROMPTS=1')

        expect(body.rawPacket).toBeDefined()
        expect(body.rawPacket?.spanAttributesPath).toBe(
          `/api/kyber/span/${CLAUDE_TURN_NO_PARTS_SPAN}/attributes`,
        )
        // The audit row exists but stored no packet: never link to nothing.
        expect(body.rawPacket?.interactionPath).toBeUndefined()
        expect(body.parentSpanId).toBe(CLAUDE_BARE_INTERACTION_SPAN)
      })
    })
  })
})

// T2 (RED): the turn content route gains an additive `skills` entry. It is the
// skills the turn had access to (`listed`, with each description as `text`
// where the listing carried one) and the skills it invoked (`invoked`). Either
// side may instead be declared not measurable; the whole entry is declared when
// the harness has no skill source. Existing blocks are unchanged.
describe('GET /api/kyber/session/:id/turn/:index/content: skills entry (T2)', () => {
  let server: Server
  let base: string
  let store: CanonStore

  const LISTED_KEY = 'kyber.skills.listed'
  const INVOKED_KEY = 'kyber.skills.invoked'
  const AZURE = 'use the Azure CLI to interact with Azure resources.'
  const TURN_TIME = '2026-10-14T10:00:00.000Z'
  const LISTING = {
    isInitial: true,
    skills: [{ name: 'azure-cli', description: AZURE }, { name: 'app-docs-standard' }],
  }

  type NotMeasurableShape = { availability: 'not_measurable'; reason: string }
  type SkillsEntry =
    | {
        listed: Array<{ name: string; text?: string }> | NotMeasurableShape
        invoked: string[] | NotMeasurableShape
      }
    | NotMeasurableShape
  type TurnContentWithSkills = TurnContentResult & { skills?: SkillsEntry }

  function turnRecord(
    spanId: string,
    sessionId: string,
    over: Partial<CanonicalRecord> = {},
  ): CanonicalRecord {
    return {
      spanId,
      traceId: `trace-${sessionId}`,
      parentSpanId: null,
      source: 'claude-code',
      harness: 'claude-code',
      sessionId,
      name: 'llm_request',
      op: 'llm.invoke',
      kind: 'client',
      timestamp: TURN_TIME,
      durationMs: 100,
      status: 'ok',
      tokens: { freshInput: 1000, cacheRead: 0, cacheCreation: 0, output: 100, reportedInput: 1000, reportedOutput: 100 },
      content: {},
      parts: [],
      cost: { basis: 'unknown', status: 'no_rate' },
      ...over,
    }
  }

  beforeAll(async () => {
    store = new CanonStore(':memory:')

    // Claude Code: turn 0 lists two skills and invokes one; turn 1 lists them
    // again and invokes none, so `invoked` is a measured empty list there.
    store.upsertMany([
      turnRecord('span-skills-claude-1', 'sess-skills-claude', {
        timestamp: '2026-10-14T10:00:00.000Z',
        raw: { [LISTED_KEY]: LISTING, [INVOKED_KEY]: ['azure-cli'] },
      }),
      turnRecord('span-skills-claude-2', 'sess-skills-claude', {
        timestamp: '2026-10-14T10:01:00.000Z',
        raw: { [LISTED_KEY]: LISTING },
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-skills-claude',
      harness: 'claude-code',
      label: 'Claude skills turn content',
      payload: {
        id: 'sess-skills-claude',
        harness: 'claude-code',
        // As the engine stores it: `rows[].description` is the first listing text
        // seen for the skill, and `turns[i].skills_invoked` is measured per turn.
        skills: {
          measurable: true,
          listed_count: 2,
          listed_uninvoked_count: 1,
          rows: [
            { name: 'app-docs-standard', listed: true, invocations: 0, turns_listed: [0, 1] },
            { name: 'azure-cli', listed: true, invocations: 1, turns_listed: [0, 1], description: AZURE },
          ],
        },
        turns: [
          {
            index: 0,
            spanId: 'span-skills-claude-1',
            skills_listed: ['app-docs-standard', 'azure-cli'],
            skills_invoked: ['azure-cli'],
          },
          {
            index: 1,
            spanId: 'span-skills-claude-2',
            skills_listed: ['app-docs-standard', 'azure-cli'],
            skills_invoked: [],
          },
        ],
      },
    })

    // Antigravity: a listing with names only, and no invocation source.
    store.upsertMany([
      turnRecord('span-skills-agy-1', 'sess-skills-agy', {
        harness: 'antigravity',
        source: 'antigravity',
        raw: { 'gen_ai.skills': 'Active Skills: agy-customizations, antigravity-guide' },
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-skills-agy',
      harness: 'antigravity',
      label: 'Antigravity skills turn content',
      payload: {
        id: 'sess-skills-agy',
        harness: 'antigravity',
        skills: {
          measurable: true,
          listed_count: 2,
          rows: [
            {
              name: 'agy-customizations',
              listed: true,
              invocations: { availability: 'not_measurable', reason: 'No invocation source.' },
              turns_listed: [0],
            },
            {
              name: 'antigravity-guide',
              listed: true,
              invocations: { availability: 'not_measurable', reason: 'No invocation source.' },
              turns_listed: [0],
            },
          ],
        },
        // No `skills_invoked` key: invocations are not measured for this harness.
        turns: [
          {
            index: 0,
            spanId: 'span-skills-agy-1',
            skills_listed: ['agy-customizations', 'antigravity-guide'],
          },
        ],
      },
    })

    // Copilot: the listing's description comes from the turn's own system prompt.
    store.upsertMany([
      turnRecord('span-skills-copilot-1', 'sess-skills-copilot', {
        harness: 'github-copilot',
        source: 'github-copilot',
        parts: [
          {
            part: 'system_prompt',
            text: '<skill><name>architect</name><description>Design systems.</description></skill>',
          },
        ],
        raw: { 'github.copilot.context.skills': '["architect"]' },
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-skills-copilot',
      harness: 'github-copilot',
      label: 'Copilot skills turn content',
      payload: {
        id: 'sess-skills-copilot',
        harness: 'github-copilot',
        skills: {
          measurable: true,
          listed_count: 1,
          rows: [
            {
              name: 'architect',
              listed: true,
              invocations: { availability: 'not_measurable', reason: 'No invocation source.' },
              turns_listed: [0],
              description: 'Design systems.',
            },
          ],
        },
        turns: [{ index: 0, spanId: 'span-skills-copilot-1', skills_listed: ['architect'] }],
      },
    })

    // A harness with no skill source at all.
    store.upsertMany([turnRecord('span-skills-pi-1', 'sess-skills-pi', { harness: 'pi', source: 'pi' })])
    store.upsertSession({
      sessionId: 'sess-skills-pi',
      harness: 'pi',
      label: 'Pi turn content',
      payload: {
        id: 'sess-skills-pi',
        harness: 'pi',
        skills: {
          availability: 'not_measurable',
          reason: 'No skill listing source is recorded for harness pi.',
        },
        turns: [{ index: 0, spanId: 'span-skills-pi-1' }],
      },
    })

    // Projected before skills existed: no `skills` key on the payload at all.
    store.upsertMany([
      turnRecord('span-skills-unprojected-1', 'sess-skills-unprojected', {
        raw: { [LISTED_KEY]: LISTING },
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-skills-unprojected',
      harness: 'claude-code',
      label: 'Unprojected session',
      payload: {
        id: 'sess-skills-unprojected',
        harness: 'claude-code',
        turns: [{ index: 0, spanId: 'span-skills-unprojected-1' }],
      },
    })

    // Listing projected, but the turn carries no `skills_invoked` key.
    store.upsertMany([
      turnRecord('span-skills-partial-1', 'sess-skills-partial', {
        raw: { [LISTED_KEY]: LISTING },
      }),
    ])
    store.upsertSession({
      sessionId: 'sess-skills-partial',
      harness: 'claude-code',
      label: 'Partly projected session',
      payload: {
        id: 'sess-skills-partial',
        harness: 'claude-code',
        skills: {
          measurable: true,
          listed_count: 1,
          rows: [{ name: 'azure-cli', listed: true, invocations: 0, turns_listed: [0], description: AZURE }],
        },
        turns: [{ index: 0, spanId: 'span-skills-partial-1', skills_listed: ['azure-cli'] }],
      },
    })

    const canonDb = new DatabaseSync(':memory:')
    const bridge = new KyberBridge({
      store,
      canonDb,
      ratesPath: join(tmpdir(), 'nonexistent-rates.json'),
    })
    server = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    store.close()
  })

  async function turnContent(sessionId: string, index: number): Promise<TurnContentWithSkills> {
    const res = await fetch(`${base}/api/kyber/session/${sessionId}/turn/${index}/content`)
    expect(res.status).toBe(200)
    const body = (await res.json()) as TurnContentWithSkills
    expect(body, 'turn content must carry a skills entry').toHaveProperty('skills')
    return body
  }

  it('lists the skills a Claude turn had access to, with the description as text where the listing gave one', async () => {
    const body = await turnContent('sess-skills-claude', 0)
    const skills = body.skills as Exclude<SkillsEntry, NotMeasurableShape>

    expect(skills).not.toHaveProperty('availability')
    const listed = skills.listed as Array<{ name: string; text?: string }>
    expect(listed.map((skill) => skill.name).sort()).toEqual(['app-docs-standard', 'azure-cli'])
    expect(listed.find((skill) => skill.name === 'azure-cli')?.text).toBe(AZURE)
    // The listing carried no description for this skill: text is absent, not empty.
    expect(listed.find((skill) => skill.name === 'app-docs-standard')).not.toHaveProperty('text')
  })

  it('names the skills a turn invoked, and reports an empty list when it invoked none', async () => {
    const invoking = (await turnContent('sess-skills-claude', 0)).skills as Exclude<SkillsEntry, NotMeasurableShape>
    const idle = (await turnContent('sess-skills-claude', 1)).skills as Exclude<SkillsEntry, NotMeasurableShape>

    expect(invoking.invoked).toEqual(['azure-cli'])
    expect(idle.invoked).toEqual([])
  })

  it('keeps the skills entry out of the existing content blocks', async () => {
    const body = await turnContent('sess-skills-claude', 0)

    expect(body.blocks.map((block) => block.key)).not.toContain('skills')
  })

  it('declares invocations not measurable when the harness has no invocation source', async () => {
    const body = await turnContent('sess-skills-agy', 0)
    const skills = body.skills as Exclude<SkillsEntry, NotMeasurableShape>

    expect((skills.listed as Array<{ name: string }>).map((skill) => skill.name).sort()).toEqual([
      'agy-customizations',
      'antigravity-guide',
    ])
    expect(skills.invoked).toStrictEqual({ availability: 'not_measurable', reason: expect.any(String) })
  })

  it('takes a Copilot listing description from the turn system prompt', async () => {
    const body = await turnContent('sess-skills-copilot', 0)
    const skills = body.skills as Exclude<SkillsEntry, NotMeasurableShape>

    expect(skills.listed).toEqual([{ name: 'architect', text: 'Design systems.' }])
  })

  it('declares the whole skills entry not measurable, with a reason, for a harness with no skill source', async () => {
    const body = await turnContent('sess-skills-pi', 0)

    expect(body.skills).toStrictEqual({ availability: 'not_measurable', reason: expect.any(String) })
  })

  it('declares the whole skills entry not measurable, with the projection reason, when the payload has no skills key', async () => {
    const body = await turnContent('sess-skills-unprojected', 0)

    expect(body.skills).toStrictEqual({
      availability: 'not_measurable',
      reason: 'session not yet projected with skills',
    })
  })

  it('serves the listed skills and declares invocations not measurable when the turn has no skills_invoked key', async () => {
    const body = await turnContent('sess-skills-partial', 0)
    const skills = body.skills as Exclude<SkillsEntry, NotMeasurableShape>

    expect(skills.listed).toEqual([{ name: 'azure-cli', text: AZURE }])
    expect(skills.invoked).toStrictEqual({ availability: 'not_measurable', reason: expect.any(String) })
  })

  it('reads the skills entry from the loaded payload without walking the session records', async () => {
    const recordsForSession = vi.spyOn(store, 'recordsForSession')
    const get = vi.spyOn(store, 'get')
    try {
      const body = await turnContent('sess-skills-claude', 0)
      const skills = body.skills as Exclude<SkillsEntry, NotMeasurableShape>

      expect(skills.invoked).toEqual(['azure-cli'])
      // The full-session walk is what the payload seam replaces.
      expect(recordsForSession).not.toHaveBeenCalled()
      // At most the target span is fetched by id, never one get per row.
      const fetched = get.mock.calls.map((call) => call[0])
      expect(fetched.length).toBeLessThanOrEqual(1)
      expect(fetched.every((spanId) => spanId === 'span-skills-claude-1')).toBe(true)
    } finally {
      recordsForSession.mockRestore()
      get.mockRestore()
    }
  })
})
