// T8 pipeline assembly: hook→mapper→turn→outbox→sender in the installed entry.
//
// The installed entry (`plugin.ts`) is what the dash CLI drops into opencode's
// own plugin directories, so it is the one file that must legitimately load
// the T3 mapper, the T4 turn, and the T5 transport (outbox + sender) —
// otherwise `check:reachable` keeps reporting those four modules as dead and
// the only sanctioned remedies are deletion or this wiring. Every hook boundary
// therefore runs the full pipeline in order: the hook stamps a mapper-shaped
// payload (mapper stage → envelope span), the same identity stamps a turn
// outcome (turn stage → `tool_result` log), and the event spools through the
// bounded outbox to the live loopback OTLP sender (transport stages). The
// host contract is unchanged: delivery is best-effort, failures are swallowed
// and recorded, the session always continues.

import { describe, expect, it, vi } from 'vitest'

import {
  OPENCODE_CONTRACT_VERSION,
  OPENCODE_CONTRACT_VERSION_KEY,
  OPENCODE_SESSION_ID_KEY,
} from '../../../canon/adapters/opencode-envelope.js'
import { ingestBatch } from '../../../canon/ingest.js'
import { CanonStore } from '../../../canon/store.js'
import type { OtlpSpan } from '../../../otel/receiver.js'
import {
  OPENCODE_PLUGIN_HOOK_KINDS,
  createInstalledOpencodePlugin,
} from './plugin.js'
import { OPENCODE_TOOL_RESULT_BODY, OPENCODE_TOOL_RESULT_SCOPE } from './turn.js'

const SESSION_ID = 'opencode-session-7f3a'
const TURN_INDEX = 3
const SPAN_ID = 'span-00c1'
const NOW = '2026-10-22T00:00:00.000Z'

const LIFECYCLE = [...OPENCODE_PLUGIN_HOOK_KINDS]

function okResponse(): Response {
  return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } })
}

async function fireLifecycle(plugin: ReturnType<typeof createInstalledOpencodePlugin>['plugin']) {
  await plugin.hooks['session.start']({ sessionId: SESSION_ID })
  await plugin.hooks['turn.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX })
  await plugin.hooks['span.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID })
  await plugin.hooks['span.end']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID })
  await plugin.hooks['turn.end']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX })
  await plugin.hooks['session.end']({ sessionId: SESSION_ID })
}

type WireAttribute = { key: string; value: Record<string, unknown> }

type WireSpan = {
  traceId: string
  spanId: string
  name: string
  kind: string
  startTimeUnixNano: string
  endTimeUnixNano: string
  attributes: WireAttribute[]
}

type WireBody = {
  resourceSpans: Array<{
    resource: { attributes: WireAttribute[] }
    scopeSpans: Array<{ scope: { name: string; version: string }; spans: WireSpan[] }>
  }>
}

/** Decode one OTLP attribute the way the loopback receiver does: string/int/bool by wrapper. */
function decodeWireValue(value: Record<string, unknown>): unknown {
  if (typeof value.stringValue === 'string') return value.stringValue
  if (typeof value.intValue === 'string' && value.intValue !== '') return Number(value.intValue)
  if (typeof value.boolValue === 'boolean') return value.boolValue
  return value
}

function decodeWireAttributes(attributes: WireAttribute[]): Record<string, unknown> {
  return Object.fromEntries(attributes.map(({ key, value }) => [key, decodeWireValue(value)]))
}

/**
 * Rebuild the decoded span the receiver would ingest from one POSTed wire
 * span. Field conventions mirror `mapOpencodePayloadToOtlpSpan` (the proven
 * ingestible shape): the OTLP kind enum normalizes to the lowercase kind,
 * timing rides nanoseconds verbatim, and a hook boundary with no error is ok.
 */
function wireToDecodedSpan(wire: WireSpan, scopeName: string, serviceName: string): OtlpSpan {
  const startMs = Number(BigInt(wire.startTimeUnixNano) / 1_000_000n)
  const endMs = Number(BigInt(wire.endTimeUnixNano) / 1_000_000n)
  return {
    traceId: wire.traceId,
    spanId: wire.spanId,
    parentSpanId: null,
    name: wire.name,
    kind: wire.kind.replace(/^SPAN_KIND_/, '').toLowerCase(),
    startTimeUnixNano: wire.startTimeUnixNano,
    endTimeUnixNano: wire.endTimeUnixNano,
    timestamp: new Date(startMs).toISOString(),
    durationMs: Math.max(0, endMs - startMs),
    status: { code: 'ok' },
    attributes: decodeWireAttributes(wire.attributes),
    resource: { 'service.name': serviceName },
    scope: { name: scopeName },
  }
}

describe('Installed pipeline assembly (T8)', () => {
  it('flowsHookMapperTurnOutboxSenderInOrder', async () => {
    const fetchFn = vi.fn(async (_url: string, _init?: RequestInit): Promise<Response> => okResponse())
    const pipeline = createInstalledOpencodePlugin({
      fetchFn,
      now: () => NOW,
      sleepFn: async () => {},
    })

    await fireLifecycle(pipeline.plugin)
    await pipeline.outbox.drain()

    // Hook stage: every boundary attempted in firing order.
    expect(pipeline.plugin.events.map((event) => event.kind)).toEqual(LIFECYCLE)

    // Mapper stage: one envelope span per hook, version-stamped, session-anchored.
    expect(pipeline.mappedSpans).toHaveLength(LIFECYCLE.length)
    for (const [index, span] of pipeline.mappedSpans.entries()) {
      expect(span.attributes[OPENCODE_CONTRACT_VERSION_KEY]).toBe(OPENCODE_CONTRACT_VERSION)
      expect(span.attributes).toMatchObject({
        'opencode.session.id': SESSION_ID,
        'opencode.hook': LIFECYCLE[index],
      })
    }
    expect(pipeline.mappedSpans.map((span) => span.attributes['opencode.hook'])).toEqual(LIFECYCLE)

    // Turn stage: one `tool_result` log per hook under the `com.opencode` scope.
    expect(pipeline.outcomeLogs).toHaveLength(LIFECYCLE.length)
    for (const [index, log] of pipeline.outcomeLogs.entries()) {
      expect(log.body).toBe(OPENCODE_TOOL_RESULT_BODY)
      expect(log.scope?.name).toBe(OPENCODE_TOOL_RESULT_SCOPE)
      expect(log.attributes['opencode.hook']).toBe(LIFECYCLE[index])
    }

    // Outbox stage: the spool drained everything the hooks enqueued.
    expect(pipeline.outbox.status().sent_spans).toBe(LIFECYCLE.length)
    expect(pipeline.outbox.status().queued).toBe(0)

    // Sender stage: span-anchored OTLP POSTs to the live loopback receiver, in order.
    expect(fetchFn).toHaveBeenCalled()
    for (const [url] of fetchFn.mock.calls) {
      expect(url).toBe('http://127.0.0.1:4318/v1/traces')
    }
    const postedKinds: string[] = []
    for (const call of fetchFn.mock.calls) {
      const init = call[1]
      expect(init).toBeDefined()
      const body = JSON.parse(String(init?.body)) as {
        resourceSpans: Array<{ scopeSpans: Array<{ spans: Array<{ name: string; attributes: unknown }> }> }>
      }
      for (const span of body.resourceSpans[0]?.scopeSpans[0]?.spans ?? []) {
        postedKinds.push(span.name)
      }
      expect(JSON.stringify(body)).toContain(SESSION_ID)
    }
    expect(postedKinds).toEqual(LIFECYCLE.map((kind) => `opencode.${kind}`))
  })

  it('sendsDirectlyThroughTheSenderWhenSpoolingIsOff', async () => {
    const fetchFn = vi.fn(async (_url: string, _init?: RequestInit): Promise<Response> => okResponse())
    const pipeline = createInstalledOpencodePlugin({
      fetchFn,
      now: () => NOW,
      spool: false,
    })

    await pipeline.plugin.hooks['span.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID })
    await pipeline.plugin.hooks['span.end']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID })

    // Mapping still runs per hook even on the direct path ...
    expect(pipeline.mappedSpans.map((span) => span.attributes['opencode.hook'])).toEqual([
      'span.start',
      'span.end',
    ])
    expect(pipeline.outcomeLogs).toHaveLength(2)

    // ... and the sender POSTs each hook straight to the live loopback receiver.
    expect(fetchFn).toHaveBeenCalledTimes(2)
    expect(pipeline.sender.endpoint).toBe('http://127.0.0.1:4318/v1/traces')
    const firstInit = fetchFn.mock.calls[0]?.[1]
    expect(firstInit).toBeDefined()
    expect(String(firstInit?.body)).toContain(SESSION_ID)
  })

  it('neverFailsTheHostWhenThePipelineTransportIsDown', async () => {
    const fetchFn = vi.fn(async () => {
      throw new Error('receiver down: connection refused')
    })
    const pipeline = createInstalledOpencodePlugin({
      fetchFn,
      now: () => NOW,
      sleepFn: async () => {},
    })

    // The spooled path never throws: the outage spools, the session continues.
    await expect(
      pipeline.plugin.hooks['span.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID }),
    ).resolves.toBeUndefined()
    await expect(
      pipeline.plugin.hooks['span.end']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID }),
    ).resolves.toBeUndefined()

    expect(pipeline.mappedSpans).toHaveLength(2)
    expect(pipeline.outcomeLogs).toHaveLength(2)
    expect(pipeline.outbox.status().queued).toBe(2)
    await pipeline.outbox.close()

    // The direct path surfaces the outage as a recorded failure, still quiet.
    const direct = createInstalledOpencodePlugin({
      fetchFn,
      now: () => NOW,
      spool: false,
      timeoutMs: 250,
    })
    await expect(
      direct.plugin.hooks['span.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID }),
    ).resolves.toBeUndefined()
    expect(direct.plugin.failures).toHaveLength(1)
    expect(direct.mappedSpans).toHaveLength(1)
  })

  it('preservesDropOldestBackpressureThroughTheInstalledEntry', async () => {
    const fetchFn = vi.fn(async () => {
      throw new Error('receiver down')
    })
    const pipeline = createInstalledOpencodePlugin({
      fetchFn,
      now: () => NOW,
      capacity: 2,
      sleepFn: async () => {},
    })

    await pipeline.plugin.hooks['session.start']({ sessionId: SESSION_ID })
    await pipeline.plugin.hooks['turn.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX })
    await pipeline.plugin.hooks['span.start']({ sessionId: SESSION_ID, turnIndex: TURN_INDEX, spanId: SPAN_ID })
    await pipeline.plugin.hooks['session.end']({ sessionId: SESSION_ID })

    // enqueue is synchronous: no hook waited, the assertions below are exact.
    const status = pipeline.outbox.status()
    expect(status.queued).toBe(2)
    expect(status.dropped_spans).toBe(2)
    expect(status.capacity).toBe(2)
    void pipeline.outbox.close()
  })

  it('postsVersionStampedSpansTheReceiverAttributesAsNonModelBoundaries', async () => {
    // R2: a hook boundary is not a model invocation, so the receiver must
    // neither accept it as a canonical record nor version-quarantine it. This
    // pins the T8-claimable contract: the version guard passes, the opencode
    // adapter claims the span (opencode namespaces), and the boundary files
    // as a non-model quarantine with the session it came from. It also pins
    // the no-fabrication rule: the POSTed wire carries no model, tool, or
    // token-counter markers, and no record lands in the store.
    const fetchFn = vi.fn(async (_url: string, _init?: RequestInit): Promise<Response> => okResponse())
    const pipeline = createInstalledOpencodePlugin({
      fetchFn,
      now: () => NOW,
      sleepFn: async () => {},
    })

    await fireLifecycle(pipeline.plugin)
    await pipeline.outbox.drain()
    expect(pipeline.outbox.status().sent_spans).toBe(LIFECYCLE.length)

    const wireSpans: WireSpan[] = []
    const decoded: OtlpSpan[] = []
    for (const call of fetchFn.mock.calls) {
      const init = call[1]
      expect(init).toBeDefined()
      const body = JSON.parse(String(init?.body)) as WireBody
      const resource = body.resourceSpans[0]
      expect(resource).toBeDefined()
      const serviceEntry = resource?.resource.attributes.find(({ key }) => key === 'service.name')
      for (const scopeSpans of resource?.scopeSpans ?? []) {
        for (const wire of scopeSpans.spans) {
          wireSpans.push(wire)
          decoded.push(wireToDecodedSpan(wire, scopeSpans.scope.name, String(serviceEntry?.value.stringValue ?? '')))
        }
      }
    }
    expect(decoded).toHaveLength(LIFECYCLE.length)

    // Span-anchored (Q3a): one trace per session across every boundary.
    for (const span of decoded) {
      expect(span.traceId).toBe(decoded[0]?.traceId)
    }

    // Version-guard pass: every POSTed boundary names the supported contract
    // and carries the envelope session identity — and nothing it did not
    // observe (no model, tool, or usage markers to fabricate an op).
    for (const [index, span] of decoded.entries()) {
      expect(span.attributes[OPENCODE_CONTRACT_VERSION_KEY]).toBe(OPENCODE_CONTRACT_VERSION)
      expect(span.attributes[OPENCODE_SESSION_ID_KEY]).toBe(SESSION_ID)
      expect(span.attributes['opencode.hook']).toBe(LIFECYCLE[index])
    }
    for (const wire of wireSpans) {
      for (const { key } of wire.attributes) {
        expect(
          key === 'llm.token_count' ||
            key.startsWith('llm.token_count.') ||
            key.startsWith('opencode.model.') ||
            key.startsWith('opencode.tool.'),
        ).toBe(false)
      }
    }

    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(decoded, store)

    // Non-model quarantine is the correct taxonomy for marker-less lifecycle
    // boundaries: claimed by the opencode adapter, filed with their session,
    // accepted as canonical records never.
    expect(outcome).toEqual({ accepted: 0, quarantined: LIFECYCLE.length, rejected: 0 })
    for (const span of decoded) {
      expect(store.get(span.spanId)).toBeUndefined()
      const entry = store.getQuarantine(span.spanId)
      expect(entry?.reason).toMatch(/non-model span/)
      expect(entry?.namespaces).toContain('opencode')
    }
    store.close()
  })

  it('boundsRecordedEvidenceDropOldest', async () => {
    // The evidence arrays are a debugging tail, not the delivery record: a
    // long session must not grow host memory without bound.
    const fetchFn = vi.fn(async () => {
      throw new Error('receiver down')
    })
    const pipeline = createInstalledOpencodePlugin({
      fetchFn,
      now: () => NOW,
      sleepFn: async () => {},
      evidenceLimit: 3,
    })

    await fireLifecycle(pipeline.plugin)

    expect(pipeline.mappedSpans).toHaveLength(3)
    expect(pipeline.outcomeLogs).toHaveLength(3)
    expect(pipeline.mappedSpans.map((span) => span.attributes['opencode.hook'])).toEqual([
      'span.end',
      'turn.end',
      'session.end',
    ])
    expect(pipeline.outcomeLogs.map((log) => log.attributes['opencode.hook'])).toEqual([
      'span.end',
      'turn.end',
      'session.end',
    ])
    await pipeline.outbox.close()
  })
})
