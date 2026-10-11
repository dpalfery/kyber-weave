// Quarantine/problems harness filtering and detail routes, end to end against a real
// store-backed bridge: scoping (all/unknown/concrete), invalid-harness 400s, detail
// evidence (exact raw payload / message / stack), legacy rows reading as unavailable,
// and bounded metadata-only lists.

import type { IncomingMessage, ServerResponse } from 'node:http'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore, problemsHarnessWhere, quarantineHarnessWhere, type QuarantineEntryDetail } from '../canon/store.js'
import { KyberBridge, type ProblemDetail, type ProblemRow, type QuarantineRow } from './bridge.js'
import { handleKyberRequest, type KyberRouteDeps } from './routes.js'

const stores: CanonStore[] = []
const bridges: KyberBridge[] = []

afterEach(() => {
  for (const bridge of bridges.splice(0)) bridge.close()
  for (const store of stores.splice(0)) store.close()
})

const OPENTELEMETRY_SPAN = {
  traceId: '0af7651916cd43dd8448eb211c80319c',
  spanId: 'opencode-span-1',
  parentSpanId: null,
  name: 'Session.load',
  kind: 'internal',
  timestamp: '2026-10-10T20:05:54.000Z',
  attributes: { 'session.id': 'abc' },
  resource: { 'service.name': 'opencode' },
}

function seed(): { store: CanonStore; bridge: KyberBridge; deps: KyberRouteDeps } {
  const store = new CanonStore(':memory:')
  stores.push(store)

  store.quarantine('opencode-span-1', ['session'], 'non-model span', {
    source: 'opencode',
    name: 'Session.load',
    timestamp: '2026-10-10T20:05:54.000Z',
    rawPayload: JSON.stringify(OPENTELEMETRY_SPAN),
  })
  store.quarantine('opencode-span-2', ['gen_ai'], 'unclaimed', {
    source: 'opencode',
    name: 'ai.invoke',
    timestamp: '2026-10-10T20:05:55.000Z',
    rawPayload: JSON.stringify({ ...OPENTELEMETRY_SPAN, spanId: 'opencode-span-2' }),
  })
  store.quarantine('copilot-span-1', ['copilot_chat'], 'unclaimed', {
    source: 'copilot',
    name: 'copilot_chat/gen_ai',
    timestamp: '2026-10-10T20:05:56.000Z',
    rawPayload: JSON.stringify({ ...OPENTELEMETRY_SPAN, spanId: 'copilot-span-1' }),
  })
  // A twin front-end spelling: scopes under its canonical owner.
  store.quarantine('claude-span-1', ['claude'], 'unclaimed', {
    source: 'claude-desktop',
    name: 'chat',
    timestamp: '2026-10-10T20:05:57.000Z',
  })
  // Unattributed rows: the null source and the neutral grouping key both scope to unknown.
  store.quarantine('unattributed-1', [], 'unclaimed', { name: 'orphan' })
  store.quarantine('otlp-1', ['http'], 'non-model span', { source: 'otlp', name: 'GET /healthz' })

  store.recordProblem({
    spanId: 'copilot-span-9',
    severity: 'error',
    code: 'TOKEN_NEGATIVE_FRESH',
    message: 'fresh input is negative: counters are exclusive-shaped',
    location: 'copilot-span-9',
    harness: 'copilot',
    timestamp: '2026-10-10T20:05:58.000Z',
  })
  store.recordProblem({
    spanId: 'opencode-span-9',
    severity: 'error',
    code: 'ADAPTER_NORMALIZE_FAILED',
    message: 'cannot read counters of undefined',
    stack: 'Error: cannot read counters of undefined\n    at normalize (opencode.ts:1:1)',
    location: 'opencode-span-9',
    harness: 'opencode',
    timestamp: '2026-10-10T20:05:59.000Z',
  })
  store.recordProblem({
    spanId: 'orphan-span-9',
    severity: 'warning',
    code: 'ORPHAN_RECORD',
    message: 'no root resolvable in group',
    location: 'orphan-span-9',
    timestamp: '2026-10-10T20:06:00.000Z',
  })

  const bridge = new KyberBridge({ store })
  bridges.push(bridge)
  return { store, bridge, deps: { store } }
}

function call(
  href: string,
  bridge: KyberBridge,
  deps?: KyberRouteDeps,
  method = 'GET',
): { status: number; body: Record<string, unknown>; handled: boolean } {
  let status = 0
  let body = ''
  const req = { method } as IncomingMessage
  const res = {
    writeHead: (code: number) => {
      status = code
    },
    end: (data: string) => {
      body = data
    },
  } as unknown as ServerResponse
  const handled = handleKyberRequest(req, res, new URL(href, 'http://127.0.0.1:4747'), bridge, deps)
  return { status, body: body === '' ? {} : (JSON.parse(body) as Record<string, unknown>), handled }
}

function entriesOf(body: Record<string, unknown>): QuarantineRow[] {
  return (body['entries'] ?? body['data']) as QuarantineRow[]
}

function problemsOf(body: Record<string, unknown>): ProblemRow[] {
  return (body['problems'] ?? body['data']) as ProblemRow[]
}

describe('quarantine harness filtering', () => {
  it('keeps the unfiltered list backwards-compatible and metadata-only', () => {
    const { bridge, deps } = seed()
    const { status, body } = call('/api/kyber/quarantine', bridge, deps)

    expect(status).toBe(200)
    const entries = entriesOf(body)
    expect(entries).toHaveLength(6)
    expect(body['total']).toBe(6)
    for (const entry of entries) {
      expect(entry).not.toHaveProperty('rawPayload')
      expect(entry).not.toHaveProperty('raw_payload')
    }
  })

  it('scopes to a concrete harness without counting its neighbors', () => {
    const { bridge, deps } = seed()

    const copilot = call('/api/kyber/quarantine?harness=copilot', bridge, deps)
    expect(copilot.status).toBe(200)
    expect(entriesOf(copilot.body).map((e) => e.span_id)).toEqual(['copilot-span-1'])
    expect(copilot.body['total']).toBe(1)

    const opencode = call('/api/kyber/quarantine?harness=opencode', bridge, deps)
    expect(entriesOf(opencode.body).map((e) => e.span_id).sort()).toEqual([
      'opencode-span-1',
      'opencode-span-2',
    ])
    expect(opencode.body['total']).toBe(2)
  })

  it('scopes twin front-ends under their canonical owner', () => {
    const { bridge, deps } = seed()

    const { status, body } = call('/api/kyber/quarantine?harness=claude-code', bridge, deps)
    expect(status).toBe(200)
    expect(entriesOf(body).map((e) => e.span_id)).toEqual(['claude-span-1'])
  })

  it('scopes unknown to unattributed rows only', () => {
    const { bridge, deps } = seed()

    const { status, body } = call('/api/kyber/quarantine?harness=unknown', bridge, deps)
    expect(status).toBe(200)
    expect(entriesOf(body).map((e) => e.span_id).sort()).toEqual(['otlp-1', 'unattributed-1'])
    expect(body['total']).toBe(2)
  })

  it('treats all, blank, and mixed case as unfiltered or canonical', () => {
    const { bridge, deps } = seed()

    expect(call('/api/kyber/quarantine?harness=all', bridge, deps).body['total']).toBe(6)
    expect(call('/api/kyber/quarantine?harness=', bridge, deps).body['total']).toBe(6)
    expect(entriesOf(call('/api/kyber/quarantine?harness=COPILOT', bridge, deps).body)).toHaveLength(1)
  })

  it('rejects invalid harness identifiers with a 400 and nothing listed', () => {
    const { bridge, deps } = seed()

    for (const bad of ['copilotx', 'service.name', 'opencode;drop', '*']) {
      const { status, body } = call(`/api/kyber/quarantine?harness=${encodeURIComponent(bad)}`, bridge, deps)
      expect(status).toBe(400)
      expect(String(body['error'])).toMatch(/invalid harness filter/)
    }
  })
})

describe('quarantine detail route', () => {
  it('exposes the exact stored payload for a newly quarantined span', () => {
    const { bridge, deps } = seed()

    const { status, body } = call('/api/kyber/quarantine/opencode-span-1', bridge, deps)
    expect(status).toBe(200)
    // The detail contract is B7's: camelCase fields, every one present, and the
    // payload served exactly as stored — never parsed or re-shaped server-side.
    const detail = body as unknown as QuarantineEntryDetail
    expect(detail.spanId).toBe('opencode-span-1')
    expect(detail.source).toBe('opencode')
    expect(detail.reason).toBe('non-model span')
    expect(detail.harness).toBeNull()
    expect(detail.evidenceAvailable).toBe(true)
    expect(detail.rawPayload).toBe(JSON.stringify(OPENTELEMETRY_SPAN))
  })

  it('reads rows without stored evidence as explicitly unavailable, never invented', () => {
    const { bridge, deps } = seed()

    const { status, body } = call('/api/kyber/quarantine/claude-span-1', bridge, deps)
    expect(status).toBe(200)
    const detail = body as unknown as QuarantineEntryDetail
    expect(detail.rawPayload).toBeNull()
    expect(detail.evidenceAvailable).toBe(false)
  })

  it('answers 404 for unknown span ids and 405 for writes', () => {
    const { bridge, deps } = seed()

    expect(call('/api/kyber/quarantine/no-such-span', bridge, deps).status).toBe(404)
    expect(call('/api/kyber/quarantine/opencode-span-1', bridge, deps, 'POST').status).toBe(405)
  })
})

describe('problems harness filtering and detail', () => {
  it('keeps the unfiltered problems list backwards-compatible and metadata-only', () => {
    const { bridge, deps } = seed()
    const { status, body } = call('/api/kyber/problems', bridge, deps)

    expect(status).toBe(200)
    expect(problemsOf(body)).toHaveLength(3)
    expect(body['total']).toBe(3)
    for (const problem of problemsOf(body)) {
      expect(problem).not.toHaveProperty('stack')
    }
  })

  it('scopes problems to concrete and unknown harnesses and rejects invalid ones', () => {
    const { bridge, deps } = seed()

    expect(problemsOf(call('/api/kyber/problems?harness=opencode', bridge, deps).body)).toHaveLength(1)
    expect(problemsOf(call('/api/kyber/problems?harness=copilot', bridge, deps).body)).toHaveLength(1)
    const unknown = call('/api/kyber/problems?harness=unknown', bridge, deps)
    expect(problemsOf(unknown.body).map((p) => p.code)).toEqual(['ORPHAN_RECORD'])
    expect(call('/api/kyber/problems?harness=nonsense', bridge, deps).status).toBe(400)
  })

  it('exposes the exact message and the exception stack on the detail route', () => {
    const { bridge, deps } = seed()
    const listed = problemsOf(call('/api/kyber/problems?harness=opencode', bridge, deps).body)
    const id = listed[0].id

    const { status, body } = call(`/api/kyber/problem/${id}`, bridge, deps)
    expect(status).toBe(200)
    const detail = body as unknown as ProblemDetail
    expect(detail.code).toBe('ADAPTER_NORMALIZE_FAILED')
    expect(detail.message).toBe('cannot read counters of undefined')
    expect(detail.stack).toBe('Error: cannot read counters of undefined\n    at normalize (opencode.ts:1:1)')
  })

  it('reads a computed validation problem with a null stack, never an invented one', () => {
    const { bridge, deps } = seed()
    const listed = problemsOf(call('/api/kyber/problems?harness=copilot', bridge, deps).body)

    const { status, body } = call(`/api/kyber/problem/${listed[0].id}`, bridge, deps)
    expect(status).toBe(200)
    const detail = body as unknown as ProblemDetail
    expect(detail.message).toBe('fresh input is negative: counters are exclusive-shaped')
    expect(detail.stack).toBeNull()
  })

  it('answers 404 for unknown problem ids', () => {
    const { bridge, deps } = seed()

    expect(call('/api/kyber/problem/999999', bridge, deps).status).toBe(404)
    expect(call('/api/kyber/problem/not-a-number', bridge, deps).status).toBe(404)
  })
})

describe('harness filter SQL fragments', () => {
  it('never builds an empty IN () list for a harness with no known spellings', () => {
    const { where } = quarantineHarnessWhere('not-a-known-harness')
    expect(where).not.toMatch(/IN \(\s*\)/)
    const store = new CanonStore(':memory:')
    stores.push(store)
    store.quarantine('s1', [], 'unclaimed', { source: 'opencode', name: 'x' })
    expect(store.countQuarantineFiltered('not-a-known-harness')).toBe(0)
  })

  it('expands twin spellings in the problems filter exactly as the quarantine filter does', () => {
    const store = new CanonStore(':memory:')
    stores.push(store)
    store.quarantine('q1', [], 'unclaimed', { source: 'copilot-chat', name: 'x' })
    store.recordProblem({
      spanId: 'p1',
      severity: 'error',
      code: 'X',
      message: 'm',
      location: 'p1',
      harness: 'copilot-chat',
      timestamp: '2026-10-10T20:05:58.000Z',
    })
    const bridge = new KyberBridge({ store })
    bridges.push(bridge)
    const deps = { store }
    const quarantine = call('/api/kyber/quarantine?harness=copilot-vscode', bridge, deps)
    const problems = call('/api/kyber/problems?harness=copilot-vscode', bridge, deps)
    expect(entriesOf(quarantine.body)).toHaveLength(1)
    expect(problemsOf(problems.body)).toHaveLength(1)
    expect(problemsHarnessWhere('copilot-vscode').params).toEqual(
      expect.arrayContaining(['copilot-chat', 'copilot-vscode']),
    )
  })
})

describe('malformed percent-encoding in detail ids', () => {
  it('answers 400, not a thrown URIError, for a stray % in the quarantine and problem ids', () => {
    const { bridge, deps } = seed()
    for (const href of ['/api/kyber/quarantine/%E0%A4%A', '/api/kyber/problem/100%']) {
      const result = call(href, bridge, deps)
      expect(result.status).toBe(400)
      expect(result.body['error']).toEqual(expect.stringContaining('Malformed'))
    }
  })
})
