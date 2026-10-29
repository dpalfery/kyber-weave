// B1: tool lifecycle hooks (tool.execute.before/after) → envelope span + tool_result log.
//
// The hook catalog (fixtures/hook-catalog.json) names the v1 hooks
// (`tool.execute.before`/`tool.execute.after`) and the v2 namespaced tool
// surface (`execute.before`/`execute.after`); the T0 skeleton
// (fixtures/span-payload-skeleton.json: tools + errors buckets) pins the
// identity and outcome keys the entry adapts. Provider-usage surfaces
// (`session.http.response`, `session.model.request`, `session.generate`)
// are NOT probed: the catalog pins their names only, no payload shape, so
// there is nothing verifiable to read (no guessing, no live calls).
//
// Acceptance: `tool.execute.after` yields a `tool_result` log carrying
// name/success/duration/size; the mapped spans never carry usage counters
// (double-count guard: native OTel owns usage, the plugin never emits it).

import { describe, expect, it, vi } from 'vitest'

import { ingestBatch } from '../../../canon/ingest.js'
import { ingestLogBatch } from '../../../canon/log-ingest.js'
import { CanonStore } from '../../../canon/store.js'
import {
  mapOpencodePayloadToAttributes,
  type OpencodePluginSpanPayload,
} from './mapper.js'
import {
  OPENCODE_PLUGIN_HOOK_KINDS,
  OPENCODE_PLUGIN_TOOL_HOOK_KINDS,
  createInstalledOpencodePlugin,
  createOpencodePlugin,
  registerOpencodePlugin,
  type OpencodePluginHost,
} from './plugin.js'
import { OPENCODE_TOOL_RESULT_BODY, OPENCODE_TOOL_RESULT_SCOPE } from './turn.js'

const SESSION_ID = 'opencode-session-b1-tool'
const NOW = '2026-10-28T00:00:00.000Z'

const acceptingReceiver = () => Promise.resolve(new Response(null, { status: 200 }))

describe('tool lifecycle hook skeleton (B1)', () => {
  it('exposes tool.execute.before/after beside the six lifecycle kinds', () => {
    expect([...OPENCODE_PLUGIN_TOOL_HOOK_KINDS]).toEqual([
      'tool.execute.before',
      'tool.execute.after',
    ])
    // The lifecycle set is unchanged: tool hooks ride beside it, not among it.
    expect([...OPENCODE_PLUGIN_HOOK_KINDS]).toHaveLength(6)
    expect(OPENCODE_PLUGIN_HOOK_KINDS).not.toContain('tool.execute.before')
  })

  it('fires tool hooks in order, recording the payload verbatim', async () => {
    const plugin = createOpencodePlugin({ now: () => NOW })
    await plugin.toolHooks['tool.execute.before']({
      sessionId: SESSION_ID,
      tool: 'read',
      callId: 'call_b1_001',
    })
    await plugin.toolHooks['tool.execute.after']({
      sessionId: SESSION_ID,
      tool: 'read',
      callId: 'call_b1_001',
      success: true,
      durationMs: 15,
      resultSizeBytes: 19,
    })
    expect(plugin.events.map((event) => event.kind)).toEqual([
      'tool.execute.before',
      'tool.execute.after',
    ])
    expect(plugin.events.at(-1)).toMatchObject({
      kind: 'tool.execute.after',
      sessionId: SESSION_ID,
      tool: 'read',
      callId: 'call_b1_001',
      success: true,
      durationMs: 15,
      resultSizeBytes: 19,
    })
  })

  it('never fails the host when a tool hook delivery throws', async () => {
    const plugin = createOpencodePlugin({
      emit: () => {
        throw new Error('receiver down')
      },
      now: () => NOW,
    })
    await expect(
      plugin.toolHooks['tool.execute.after']({ sessionId: SESSION_ID, tool: 'read' }),
    ).resolves.toBeUndefined()
    expect(plugin.failures).toHaveLength(1)
    expect(plugin.failures[0]).toMatchObject({ kind: 'tool.execute.after' })
  })
})

describe('tool lifecycle host registration (B1)', () => {
  it('registers tool hooks on the Desktop and v2 namespaced surfaces with shared identities', () => {
    const plugin = createOpencodePlugin({ now: () => NOW })
    const desktopRegistered: Record<string, unknown> = {}
    const desktopHost: OpencodePluginHost = {
      hooks: (registered: Record<string, unknown>) => {
        Object.assign(desktopRegistered, registered)
      },
    }
    const cliHost: OpencodePluginHost = {}

    registerOpencodePlugin(desktopHost, plugin)
    registerOpencodePlugin(cliHost, plugin)

    for (const kind of OPENCODE_PLUGIN_TOOL_HOOK_KINDS) {
      expect(desktopRegistered).toHaveProperty(kind)
      expect(desktopRegistered[kind]).toBe(plugin.toolHooks[kind])
    }
    expect(cliHost.tool?.['execute.before']).toBe(plugin.toolHooks['tool.execute.before'])
    expect(cliHost.tool?.['execute.after']).toBe(plugin.toolHooks['tool.execute.after'])
  })
})

describe('tool lifecycle through the installed pipeline (B1)', () => {
  it('maps tool.execute.before onto a usage-free tool span with the hook marker', async () => {
    const installed = createInstalledOpencodePlugin({ spool: true, fetchFn: acceptingReceiver })
    await installed.plugin.toolHooks['tool.execute.before']({
      sessionId: SESSION_ID,
      tool: 'read',
      callId: 'call_b1_001',
    })
    const span = installed.mappedSpans.at(-1)
    expect(span?.attributes['opencode.tool.name']).toBe('read')
    expect(span?.attributes['opencode.tool.call.id']).toBe('call_b1_001')
    expect(span?.attributes['opencode.hook']).toBe('tool.execute.before')
    expect(span?.attributes['opencode.session.id']).toBe(SESSION_ID)
    // Double-count guard: the plugin never emits usage counters.
    for (const key of Object.keys(span?.attributes ?? {})) {
      expect(key.startsWith('llm.token_count'), key).toBe(false)
    }
  })

  it('tool.execute.after yields a tool_result log with name, success, duration, and size', async () => {
    const installed = createInstalledOpencodePlugin({ spool: true, fetchFn: acceptingReceiver })
    await installed.plugin.toolHooks['tool.execute.before']({
      sessionId: SESSION_ID,
      tool: 'read',
      callId: 'call_b1_001',
    })
    await installed.plugin.toolHooks['tool.execute.after']({
      sessionId: SESSION_ID,
      tool: 'read',
      callId: 'call_b1_001',
      success: true,
      durationMs: 15,
      resultSizeBytes: 19,
    })
    const log = installed.outcomeLogs.at(-1)
    expect(log?.body).toBe(OPENCODE_TOOL_RESULT_BODY)
    expect(log?.scope?.name).toBe(OPENCODE_TOOL_RESULT_SCOPE)
    expect(log?.attributes).toMatchObject({
      tool_name: 'read',
      success: true,
      duration_ms: 15,
      tool_result_size_bytes: 19,
      'opencode.tool.name': 'read',
      'opencode.tool.call.id': 'call_b1_001',
    })
  })

  it('leaves outcome fields absent on the before log instead of fabricating them', async () => {
    const installed = createInstalledOpencodePlugin({ spool: true, fetchFn: acceptingReceiver })
    await installed.plugin.toolHooks['tool.execute.before']({
      sessionId: SESSION_ID,
      tool: 'read',
      callId: 'call_b1_001',
    })
    const log = installed.outcomeLogs.at(-1)
    expect(log?.body).toBe(OPENCODE_TOOL_RESULT_BODY)
    for (const key of ['success', 'duration_ms', 'tool_result_size_bytes']) {
      expect(key in (log?.attributes ?? {}), key).toBe(false)
    }
  })

  it('round-trips a tool.execute.after span plus its log to one tool.invoke record', async () => {
    const store = new CanonStore(':memory:')
    try {
      const installed = createInstalledOpencodePlugin({ spool: true, fetchFn: acceptingReceiver })
      // The host named the span: the after log joins it exactly on the
      // session-anchored trace.
      await installed.plugin.toolHooks['tool.execute.after']({
        sessionId: SESSION_ID,
        spanId: 'span-b1-tool',
        tool: 'read',
        callId: 'call_b1_001',
        success: true,
        durationMs: 15,
        resultSizeBytes: 19,
      })
      const span = installed.mappedSpans.at(-1)
      const log = installed.outcomeLogs.at(-1)
      expect(span).toBeDefined()
      expect(log).toBeDefined()
      expect(span?.spanId).toBe('span-b1-tool')
      expect(log?.traceId).toBe(SESSION_ID)
      expect(log?.spanId).toBe('span-b1-tool')
      const ingested = ingestBatch([span!], store)
      expect(ingested).toEqual({ accepted: 1, quarantined: 0, rejected: 0 })
      const outcome = ingestLogBatch([log!], store)
      expect(outcome.enriched).toBe(1)
      const record = store.get(span!.spanId)
      expect(record?.harness).toBe('opencode')
      expect(record?.op).toBe('tool.invoke')
      expect(record?.content).toMatchObject({ tool_name: 'read', success: true, duration_ms: 15 })
    } finally {
      store.close()
    }
  })

  it('still yields the outcome log on the session trace when no span was observed', async () => {
    const installed = createInstalledOpencodePlugin({ spool: true, fetchFn: acceptingReceiver })
    await installed.plugin.toolHooks['tool.execute.after']({
      sessionId: SESSION_ID,
      tool: 'read',
      callId: 'call_b1_001',
      success: true,
      durationMs: 15,
      resultSizeBytes: 19,
    })
    const log = installed.outcomeLogs.at(-1)
    // Session-anchored trace, no span: the log waits for its span (pending)
    // rather than joining the wrong record — but the outcome is yielded.
    expect(log?.traceId).toBe(SESSION_ID)
    expect(log?.spanId).toBeNull()
    expect(log?.attributes).toMatchObject({
      tool_name: 'read',
      success: true,
      duration_ms: 15,
      tool_result_size_bytes: 19,
    })
  })
})

describe('mapper double-count guard (B1)', () => {
  const TOOL_AFTER: OpencodePluginSpanPayload = {
    traceId: 'trace-b1-tool',
    spanId: 'span-b1-tool',
    parentSpanId: null,
    name: 'opencode.tool.execute.after',
    kind: 'internal',
    sessionID: SESSION_ID,
    tool: 'read',
    callID: 'call_b1_001',
    tokens: { input_tokens: 120, output_tokens: 45 },
    startTimeUnixNano: '1720000000100000000',
    endTimeUnixNano: '1720000000115000000',
    hook: 'tool.execute.after',
  }

  it.each(['tool.execute.before', 'tool.execute.after'] as const)(
    'never stamps usage counters on a %s span, even when handed tokens',
    (hook) => {
      const attributes = mapOpencodePayloadToAttributes({ ...TOOL_AFTER, hook })
      for (const key of Object.keys(attributes)) {
        expect(key === 'llm.token_count' || key.startsWith('llm.token_count.'), key).toBe(false)
      }
      // The tool marker still rides: the span votes tool.invoke downstream.
      expect(attributes['opencode.tool.name']).toBe('read')
      expect(attributes['opencode.tool.call.id']).toBe('call_b1_001')
    },
  )

  it('keeps stamping usage on non-tool spans', () => {
    const attributes = mapOpencodePayloadToAttributes({ ...TOOL_AFTER, hook: 'span.end' })
    expect(attributes['llm.token_count.prompt']).toBe(120)
    expect(attributes['llm.token_count.completion']).toBe(45)
  })
})

describe('entry setup registers tool hooks (B1)', () => {
  it('registers context plus tool.execute.before/after on a 2.1.4 context with a tool domain', async () => {
    const { createSetup } = await import('./entry.js')
    const installed = createInstalledOpencodePlugin({ fetchFn: acceptingReceiver, now: () => NOW })
    const hooked: string[] = []
    const callbacks = new Map<string, (input: never) => Promise<void>>()
    const ctx = {
      session: {
        hook: vi.fn((name: string, callback: (input: never) => Promise<void>) => {
          hooked.push(`session.${name}`)
          callbacks.set(`session.${name}`, callback)
          return Promise.resolve({ dispose: () => Promise.resolve() })
        }),
      },
      tool: {
        hook: vi.fn((name: string, callback: (input: never) => Promise<void>) => {
          hooked.push(`tool.${name}`)
          callbacks.set(`tool.${name}`, callback)
          return Promise.resolve({ dispose: () => Promise.resolve() })
        }),
      },
    }
    await createSetup({ installed, env: {}, consentPath: '/nonexistent-b1-consent.json' })(ctx as never)
    expect(hooked.sort()).toEqual(['session.context', 'tool.execute.after', 'tool.execute.before'])
  })

  it('a tool before/after pair yields events with measured duration and byte size', async () => {
    const { createSetup } = await import('./entry.js')
    const installed = createInstalledOpencodePlugin({ fetchFn: acceptingReceiver, now: () => NOW })
    const callbacks = new Map<string, (input: never) => Promise<void>>()
    const ctx = {
      session: { hook: vi.fn(() => Promise.resolve({ dispose: () => Promise.resolve() })) },
      tool: {
        hook: vi.fn((name: string, callback: (input: never) => Promise<void>) => {
          callbacks.set(name, callback)
          return Promise.resolve({ dispose: () => Promise.resolve() })
        }),
      },
    }
    await createSetup({ installed, env: {}, consentPath: '/nonexistent-b1-consent.json' })(ctx as never)
    const before = callbacks.get('execute.before')
    const after = callbacks.get('execute.after')
    expect(before).toBeDefined()
    expect(after).toBeDefined()
    await before!({
      sessionID: SESSION_ID,
      tool: 'read',
      callID: 'call_b1_pair',
    } as never)
    await after!({
      sessionID: SESSION_ID,
      tool: 'read',
      callID: 'call_b1_pair',
      output_text: 'file contents: hello',
    } as never)
    const events = installed.plugin.events
    expect(events.map((event) => event.kind)).toEqual(['tool.execute.before', 'tool.execute.after'])
    const result = events.at(-1)
    expect(result).toMatchObject({ tool: 'read', callId: 'call_b1_pair', success: true })
    expect(typeof result?.durationMs).toBe('number')
    expect(result?.durationMs).toBeGreaterThanOrEqual(0)
    expect(result?.resultSizeBytes).toBe(Buffer.byteLength('file contents: hello', 'utf8'))
    // The outcome text itself never rides: size without content.
    expect(result).not.toHaveProperty('resultText')
    const log = installed.outcomeLogs.at(-1)
    expect(log?.attributes).toMatchObject({
      tool_name: 'read',
      success: true,
      tool_result_size_bytes: Buffer.byteLength('file contents: hello', 'utf8'),
    })
  })

  it('marks a failed tool run without content and leaves unmeasured fields absent', async () => {
    const { createSetup } = await import('./entry.js')
    const installed = createInstalledOpencodePlugin({ fetchFn: acceptingReceiver, now: () => NOW })
    const callbacks = new Map<string, (input: never) => Promise<void>>()
    const ctx = {
      session: { hook: vi.fn(() => Promise.resolve({ dispose: () => Promise.resolve() })) },
      tool: {
        hook: vi.fn((name: string, callback: (input: never) => Promise<void>) => {
          callbacks.set(name, callback)
          return Promise.resolve({ dispose: () => Promise.resolve() })
        }),
      },
    }
    await createSetup({ installed, env: {}, consentPath: '/nonexistent-b1-consent.json' })(ctx as never)
    // No before was seen: duration stays absent, never zero.
    await callbacks.get('execute.after')!({
      sessionID: SESSION_ID,
      tool: 'bash',
      callID: 'call_b1_fail',
      errors: { type: 'ExitError', message: 'exit 1', status: 'error' },
    } as never)
    const result = installed.plugin.events.at(-1)
    expect(result).toMatchObject({ tool: 'bash', success: false })
    expect(result).not.toHaveProperty('durationMs')
    expect(result).not.toHaveProperty('resultSizeBytes')
    expect(result).not.toHaveProperty('resultText')
  })

  // Review (Kilo 7): the hook's declared `success` overruled the error bucket,
  // so a run that reported both `success: true` and an error ingested as a
  // happy path. Failure wins: an error bucket is evidence the run failed.
  it('lets an error bucket win over an explicit success: true', async () => {
    const { createSetup } = await import('./entry.js')
    const installed = createInstalledOpencodePlugin({ fetchFn: acceptingReceiver, now: () => NOW })
    const callbacks = new Map<string, (input: never) => Promise<void>>()
    const ctx = {
      session: { hook: vi.fn(() => Promise.resolve({ dispose: () => Promise.resolve() })) },
      tool: {
        hook: vi.fn((name: string, callback: (input: never) => Promise<void>) => {
          callbacks.set(name, callback)
          return Promise.resolve({ dispose: () => Promise.resolve() })
        }),
      },
    }
    await createSetup({ installed, env: {}, consentPath: '/nonexistent-b1-consent.json' })(ctx as never)
    await callbacks.get('execute.after')!({
      sessionID: SESSION_ID,
      tool: 'bash',
      callID: 'call_b1_conflict',
      success: true,
      errors: { type: 'ExitError', message: 'exit 1' },
    } as never)

    const result = installed.plugin.events.at(-1)
    expect(result).toMatchObject({ tool: 'bash', success: false })
  })

  // Review (Kilo 8): Number.isFinite accepted a negative or fractional turn
  // index and forwarded it as `opencode.turn.index`. A turn index is a
  // non-negative integer or it is not a turn index.
  it('does not forward a negative or fractional turn index', async () => {
    const { createSetup } = await import('./entry.js')
    const installed = createInstalledOpencodePlugin({ fetchFn: acceptingReceiver, now: () => NOW })
    const callbacks = new Map<string, (input: never) => Promise<void>>()
    const ctx = {
      session: { hook: vi.fn(() => Promise.resolve({ dispose: () => Promise.resolve() })) },
      tool: {
        hook: vi.fn((name: string, callback: (input: never) => Promise<void>) => {
          callbacks.set(name, callback)
          return Promise.resolve({ dispose: () => Promise.resolve() })
        }),
      },
    }
    await createSetup({ installed, env: {}, consentPath: '/nonexistent-b1-consent.json' })(ctx as never)

    await callbacks.get('execute.before')!({
      sessionID: SESSION_ID, tool: 'read', callID: 'call_b1_neg', turnIndex: -1,
    } as never)
    await callbacks.get('execute.before')!({
      sessionID: SESSION_ID, tool: 'read', callID: 'call_b1_frac', turnIndex: 1.5,
    } as never)
    await callbacks.get('execute.before')!({
      sessionID: SESSION_ID, tool: 'read', callID: 'call_b1_ok', turnIndex: 3,
    } as never)

    const events = installed.plugin.events
    expect(events[0]).not.toHaveProperty('turnIndex')
    expect(events[1]).not.toHaveProperty('turnIndex')
    expect(events[2]).toMatchObject({ turnIndex: 3 })
  })

  it('drops a tool input that names no session and never fails the host', async () => {
    const { createSetup } = await import('./entry.js')
    const installed = createInstalledOpencodePlugin({ fetchFn: acceptingReceiver, now: () => NOW })
    const callbacks = new Map<string, (input: never) => Promise<void>>()
    const ctx = {
      session: { hook: vi.fn(() => Promise.resolve({ dispose: () => Promise.resolve() })) },
      tool: {
        hook: vi.fn((name: string, callback: (input: never) => Promise<void>) => {
          callbacks.set(name, callback)
          return Promise.resolve({ dispose: () => Promise.resolve() })
        }),
      },
    }
    await createSetup({ installed, env: {}, consentPath: '/nonexistent-b1-consent.json' })(ctx as never)
    await callbacks.get('execute.before')!({ tool: 'read' } as never)
    await callbacks.get('execute.after')!(null as never)
    expect(installed.plugin.events).toHaveLength(0)
  })
})
