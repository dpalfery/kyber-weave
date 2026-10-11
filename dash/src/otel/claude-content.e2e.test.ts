// T9/T10 — end-to-end regression for Claude Code turn content
// (docs/plans/2026-10-10-kyberdash-claude-code-turn-content.md, test-contract
// row T9/T10).
//
// The one test that runs the whole path in a single process: synthetic OTLP
// over HTTP into the real receiver, through the real ingest writer into a real
// temp canon.db, and out through the real /api/kyber routes. It exists because
// every layer in between could look healthy while the turn view showed nothing:
// the interaction span is quarantined and its `user_prompt` went with it, and
// the request-body log was matched on a record name Claude Code never emits and
// read from an attribute key it never sends (plan R1-R3, F7).
//
// Every fixture below is built from constants in this file. No real prompt text
// and nothing read from ~/.claude — the regression is that the shapes Claude
// Code *emits* (B1-B5) survive the pipeline, not that one machine's telemetry
// does.
//
// RED: `assembledText === ""`, and no `rawPacket` pointers at all.

import { mkdtempSync, rmSync } from 'node:fs'
import type { Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { runWebDashboard } from '../cli/web.js'
import { KyberBridge, type TurnContentResult } from '../server/bridge.js'
import { OTLP_LOGS_PATH, OTLP_TRACES_PATH } from './receiver.js'
import { startOtlpCollectorService } from './service.js'

// ---------------------------------------------------------------------------
// The synthetic packet
// ---------------------------------------------------------------------------

const SESSION_ID = 's-e2e'
const TRACE_ID = '4bf92f3577b34da6a3ce929d0e0e4736'
const INTERACTION_SPAN_ID = '00f067aa0ba902b7'
const REQUEST_SPAN_ID = 'a3ce929d0e0e4736'

/** Fabricated in this file. Nothing here is a real prompt. */
const SYNTHETIC_PROMPT = 'synthetic e2e prompt: summarise the fixture only'
const SYNTHETIC_SYSTEM_PROMPT = 'synthetic e2e system prompt: answer from the fixture'

/**
 * The telemetry source name, used for grouping and same-source inheritance
 * only — never attribution evidence (R6.2). Attribution comes from the
 * `claude.*` / `claude_code.*` namespaces and the shared usage keys.
 */
const SERVICE_NAME = 'claude-code'

/** OTLP timestamps are nanoseconds since the epoch, as a decimal string. */
function nanos(iso: string): string {
  return (BigInt(Date.parse(iso)) * 1_000_000n).toString()
}

/**
 * One OTLP JSON attribute in the hand-rolled collector form: ints as decimal
 * strings (`intValue`), everything else as `stringValue`.
 */
function attribute(key: string, value: string | number): Record<string, unknown> {
  return {
    key,
    value: typeof value === 'number' ? { intValue: String(value) } : { stringValue: value },
  }
}

/**
 * The traces payload: one `claude_code.interaction` span recognised by
 * `span.type: 'interaction'` (B2, both signals on one packet) carrying the
 * prompt, and its `claude_code.llm_request` child whose counters make
 * `canonicalOp` resolve to `llm.invoke` (B1).
 */
function tracesPayload(): string {
  return JSON.stringify({
    resourceSpans: [
      {
        resource: { attributes: [attribute('service.name', SERVICE_NAME)] },
        scopeSpans: [
          {
            scope: { name: 'com.anthropic.claude_code' },
            spans: [
              {
                traceId: TRACE_ID,
                spanId: INTERACTION_SPAN_ID,
                name: 'claude_code.interaction',
                kind: 1, // INTERNAL
                startTimeUnixNano: nanos('2026-10-10T12:00:00.000Z'),
                endTimeUnixNano: nanos('2026-10-10T12:00:00.500Z'),
                attributes: [
                  attribute('span.type', 'interaction'),
                  attribute('user_prompt', SYNTHETIC_PROMPT),
                  attribute('session.id', SESSION_ID),
                  // B2 recognition applies only to a span attributed to
                  // claude-code, and attribution is vendor-namespace driven
                  // (R6.2) — the interaction span is emitted under the same
                  // claude namespace as its children, so the packet carries it.
                  attribute('claude.deployment_mode', '1p'),
                ],
                status: { code: 1 }, // OK
              },
              {
                traceId: TRACE_ID,
                spanId: REQUEST_SPAN_ID,
                parentSpanId: INTERACTION_SPAN_ID,
                name: 'claude_code.llm_request',
                kind: 3, // CLIENT
                startTimeUnixNano: nanos('2026-10-10T12:00:00.600Z'),
                endTimeUnixNano: nanos('2026-10-10T12:00:01.000Z'),
                attributes: [
                  attribute('session.id', SESSION_ID),
                  attribute('claude.deployment_mode', '1p'),
                  attribute('input_tokens', 1200),
                  attribute('output_tokens', 80),
                ],
                status: { code: 1 }, // OK
              },
            ],
          },
        ],
      },
    ],
  })
}

/**
 * The log payload: one inline `api_request_body` event (B3) correlated to the
 * request span by exact trace and span id, carrying the request JSON under
 * `attributes.body`. The event is recognised by both signals B3 names —
 * `event.name` and the `LogRecord.body` string — and its scope is the one the
 * Claude Code instrumentation emits.
 */
function logsPayload(): string {
  const requestBody = JSON.stringify({
    system: SYNTHETIC_SYSTEM_PROMPT,
    messages: [
      {
        role: 'user',
        content: [{ type: 'text', text: SYNTHETIC_PROMPT }],
      },
    ],
  })

  return JSON.stringify({
    resourceLogs: [
      {
        resource: { attributes: [attribute('service.name', SERVICE_NAME)] },
        scopeLogs: [
          {
            scope: { name: 'com.anthropic.claude_code.events' },
            logRecords: [
              {
                timeUnixNano: nanos('2026-10-10T12:00:00.900Z'),
                traceId: TRACE_ID,
                spanId: REQUEST_SPAN_ID,
                body: { stringValue: 'claude_code.api_request_body' },
                attributes: [
                  attribute('event.name', 'api_request_body'),
                  attribute('body', requestBody),
                ],
              },
            ],
          },
        ],
      },
    ],
  })
}

// ---------------------------------------------------------------------------
// Harness
// ---------------------------------------------------------------------------

type CollectorService = Awaited<ReturnType<typeof startOtlpCollectorService>>

/**
 * B7 adds `parentSpanId` and `rawPacket` to `TurnContentResult`. They are
 * intersected here so this RED test compiles against the pre-T8 `bridge.ts`;
 * T8 folds both into the production type and this alias becomes redundant
 * (same seam the T7 route test uses).
 */
type TurnBody = TurnContentResult & {
  parentSpanId?: string
  rawPacket?: { spanAttributesPath: string; interactionPath?: string }
}

type SpanAttributesBody = { spanId: string; attributes: Record<string, unknown> }
type QuarantineEntryBody = { spanId: string; rawPayload: string | null }

let root: string
let service: CollectorService
let server: Server
let base: string
let tracesStatus = 0
let logsStatus = 0

function post(url: string, body: string): Promise<Response> {
  return fetch(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body,
  })
}

async function turnContent(): Promise<{ status: number; body: TurnBody }> {
  const res = await fetch(`${base}/api/kyber/session/${SESSION_ID}/turn/0/content`)
  return { status: res.status, body: (await res.json()) as TurnBody }
}

describe('Claude Code turn content end to end (T9/T10)', () => {
  beforeAll(async () => {
    root = mkdtempSync(join(tmpdir(), 'kyberdash-claude-e2e-'))
    service = await startOtlpCollectorService({ port: 0, dbPath: join(root, 'canon.db') })

    const receiverUrl = `http://127.0.0.1:${service.receiver.port}`
    tracesStatus = (await post(`${receiverUrl}${OTLP_TRACES_PATH}`, tracesPayload())).status
    // The trace is committed before the log arrives on purpose: the log
    // correlates by exact trace and span id, so a request record the store does
    // not hold yet would park it as pending instead of enriching it.
    await service.writer.flush()
    await service.scheduler.drain()

    logsStatus = (await post(`${receiverUrl}${OTLP_LOGS_PATH}`, logsPayload())).status
    await service.writer.flush()
    await service.scheduler.drain()

    server = await runWebDashboard({
      port: 0,
      open: false,
      writeStdout: () => {},
      kyberBridge: new KyberBridge({
        store: service.canon,
        ratesPath: join(root, 'absent-rates.json'),
      }),
      // The hosted services read the same store the receiver wrote, and are
      // pointed at this test's scratch directory: the default state dir is the
      // developer's real ~/.kyberdash, and the scheduled maintenance pass is not
      // part of what this regression asserts.
      store: service.canon,
      stateDir: join(root, 'state'),
      maintenance: async () => {},
    })
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`
  })

  afterAll(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()))
    await service.close()
    rmSync(root, { recursive: true, force: true })
  })

  it('assembles the prompt and the system prompt into the turn view', async () => {
    // The synthetic packet really travelled: the receiver accepted it and the
    // ingest pipeline stored the request span as a claude-code turn. A failure
    // here is a broken fixture, not the defect this file pins.
    expect(tracesStatus, 'the synthetic trace packet must be accepted').toBe(200)
    expect(logsStatus, 'the synthetic log packet must be accepted').toBe(200)

    const stored = service.canon.get(REQUEST_SPAN_ID)
    expect(stored?.harness).toBe('claude-code')
    expect(stored?.sessionId).toBe(SESSION_ID)

    const turn = await turnContent()
    expect(turn.status).toBe(200)
    expect(turn.body.sessionId).toBe(SESSION_ID)
    expect(turn.body.turnIndex).toBe(0)
    expect(turn.body.spanId).toBe(REQUEST_SPAN_ID)

    // The defect: the turn view assembled no text at all. The prompt was
    // discarded with the quarantined interaction span and the request-body log
    // was read from a key Claude Code never sends.
    expect(
      turn.body.assembledText,
      `assembledText === ${JSON.stringify(turn.body.assembledText)}`,
    ).not.toBe('')
    expect(turn.body.assembledText).toContain(SYNTHETIC_PROMPT)
    expect(turn.body.assembledText).toContain(SYNTHETIC_SYSTEM_PROMPT)
  })

  it('links the turn packet and its quarantined parent packet, and both resolve', async () => {
    const turn = await turnContent()
    expect(turn.status).toBe(200)

    const rawPacket = turn.body.rawPacket
    expect(rawPacket, 'B7: the turn result must carry its raw-packet pointers').toBeDefined()
    expect(rawPacket?.spanAttributesPath).toBe(
      `/api/kyber/span/${REQUEST_SPAN_ID}/attributes`,
    )

    const attributes = await fetch(`${base}${rawPacket?.spanAttributesPath}`)
    expect(attributes.status).toBe(200)
    const packet = (await attributes.json()) as SpanAttributesBody
    // The packet is the turn's own span, not a neighbour's: the counters the
    // sender exported are in it.
    expect(packet.spanId).toBe(REQUEST_SPAN_ID)
    expect(packet.attributes.input_tokens).toBe(1200)

    // The interaction span is quarantined, so its kept packet is the only
    // surviving copy of the prompt the sender exported.
    const interactionPath = rawPacket?.interactionPath
    expect(interactionPath).toBe(`/api/kyber/quarantine/${INTERACTION_SPAN_ID}`)

    const interaction = await fetch(`${base}${interactionPath}`)
    expect(interaction.status).toBe(200)
    const entry = (await interaction.json()) as QuarantineEntryBody
    expect(String(entry.rawPayload)).toContain('user_prompt')
  })
})
