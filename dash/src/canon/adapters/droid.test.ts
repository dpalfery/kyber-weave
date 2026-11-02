// The Factory (Droid) canon adapter.
//
// Factory is ONE harness under ONE canonical id: `droid`, the id the Factory
// FILE source already stores (dash/src/providers/droid.ts names the provider
// `droid`, and dash/src/refresh/registry.ts maps the harness id `droid` onto
// the source root `~/.factory/sessions`). One harness, one bucket.
//
// Two claim paths reach this one adapter, and they are told apart by
// attribute fingerprint ALONE — never by the span's source name, its file name,
// or any other string the sender controls:
//
//   1. The Kyber-owned Factory plugin envelope: spans whose attributes are
//      namespaced `factory.*` and which name the contract they were emitted
//      under in `factory.contract_version`. A span naming a missing or unknown
//      contract version scores zero and is quarantined — see
//      droid-envelope.test.ts.
//   2. Factory's own native OpenTelemetry export, in its `genai` format: spans
//      carrying `gen_ai.agent.name` = `droid`. The content attributes
//      (`gen_ai.input.messages`, `gen_ai.output.messages`,
//      `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result`) are gated by a
//      content-consent flag elsewhere, so the adapter must not require them.
//
// A session can be observed by BOTH paths: the native export carries the Droid
// session id in `gen_ai.conversation.id`, the same id the plugin's session
// spans carry, so the two are joinable to one session and one turn. The
// precedence rule is therefore load-bearing: the plugin envelope is checked
// FIRST, and a native span for a session the plugin already covers is kept as
// evidence WITHOUT adding to the session's turn, tool and duration totals. A
// native span for a session no plugin span covers is counted normally.
//
// RED: written before `droid.ts` and `droid-envelope.ts` exist. Against the
// current tree every case below fails with the module under test missing.

import { describe, expect, it } from 'vitest'

import type { OtlpSpan } from '../../otel/receiver.js'
import { ingestBatchWithAdapters } from '../ingest.js'
import { buildSessions } from '../sessions.js'
import { CanonStore } from '../store.js'
import type { CanonicalRecord } from '../types.js'
import type { HarnessAdapter } from './base.js'
import { AdapterRegistry } from './registry.js'
import { rawSpan } from './testing.js'

/** The Droid session id both claim paths report for the session they share. */
const DROID_SESSION = 'droid-session-1'
/** A session only the native export observed. */
const NATIVE_ONLY_SESSION = 'droid-session-native-only'

/** The telemetry source the Kyber-owned plugin's export arrives under. */
const PLUGIN_SOURCE = 'kyberdash-droid-plugin'
/** The telemetry source Factory's own native export arrives under. */
const NATIVE_SOURCE = 'droid-native-export'
/** A telemetry source that names nothing the adapter may read as evidence. */
const FOREIGN_SOURCE = 'collector-7f3a'

/** The model the native export reports on its chat span. */
const NATIVE_MODEL = 'claude-sonnet-4-5'
/** The model the plugin envelope reports on its turn span. */
const PLUGIN_MODEL = 'gpt-5-codex'
/** The tool both paths report. */
const TOOL_NAME = 'Read'

/** The plugin turn's own duration, in milliseconds. */
const PLUGIN_DURATION_MS = 1000
/** The native chat span's own duration, in milliseconds. */
const NATIVE_DURATION_MS = 5000

/** The contract version the Kyber-owned plugin emits. */
const CONTRACT_VERSION = '1'

/**
 * The adapter module under test, loaded on demand.
 *
 * A static import would fail the whole file before a single case ran, so the
 * report would say nothing about the behaviour each case requires. Loading on
 * demand fails every case with the missing module named, and the case names
 * state what the adapter must do.
 */
let droidModule: Promise<typeof import('./droid.js')> | undefined

function loadDroid(): Promise<typeof import('./droid.js')> {
  droidModule ??= import('./droid.js')
  return droidModule
}

/**
 * Factory's native export, in its `genai` format.
 *
 * The attributes are the ones the native export is documented to carry: the
 * operation, the agent identity, the conversation and session ids, the model
 * and the provider on the chat span, and the tool identity on the tool span.
 * No token counter is among them — the native export reports none — and no
 * content attribute either, because content capture is consented elsewhere.
 */
function nativeAttributes(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    'gen_ai.operation.name': 'chat',
    'gen_ai.agent.name': 'droid',
    'gen_ai.conversation.id': DROID_SESSION,
    'session.id': DROID_SESSION,
    'gen_ai.request.model': NATIVE_MODEL,
    'gen_ai.provider.name': 'anthropic',
    'gen_ai.response.finish_reasons': ['stop'],
    ...over,
  }
}

/**
 * The plugin envelope's attributes.
 *
 * The wire contract pins the version attribute (`factory.contract_version`) and
 * the span names; it does not pin the spelling of the remaining keys, so the
 * fixture offers the mirror spellings of each value. The assertions below pin
 * the OUTCOME — the record carries the session id, the model and the tool name
 * — rather than one spelling of them.
 */
function pluginAttributes(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    'factory.contract_version': CONTRACT_VERSION,
    'factory.session.id': DROID_SESSION,
    'factory.session_id': DROID_SESSION,
    'factory.sessionId': DROID_SESSION,
    'factory.model.id': PLUGIN_MODEL,
    'factory.model': PLUGIN_MODEL,
    ...over,
  }
}

/** The plugin envelope's tool span, carrying the tool it invoked. */
function pluginToolAttributes(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    ...pluginAttributes(),
    'factory.tool.name': TOOL_NAME,
    'factory.tool_name': TOOL_NAME,
    ...over,
  }
}

/** A decoded OTLP span, built whole so no field is left to a cast. */
function otlpSpan(
  spanId: string,
  attributes: Record<string, unknown>,
  over: Partial<OtlpSpan> = {},
): OtlpSpan {
  return {
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    name: 'span',
    kind: 'internal',
    startTimeUnixNano: '1793337355759000000',
    endTimeUnixNano: '1793337363002000000',
    timestamp: '2026-10-30T12:00:00.000Z',
    durationMs: 1000,
    status: { code: 'unset' },
    resource: { 'service.name': NATIVE_SOURCE },
    scope: {},
    ...over,
    spanId,
    attributes,
  }
}

/** The plugin envelope's turn span: the session's own model call. */
function pluginTurnSpan(spanId: string): OtlpSpan {
  return otlpSpan(spanId, pluginAttributes(), {
    name: 'factory.turn',
    resource: { 'service.name': PLUGIN_SOURCE },
    durationMs: PLUGIN_DURATION_MS,
  })
}

/** The native export's chat span: the same session's model call, seen again. */
function nativeChatSpan(spanId: string, sessionId: string = DROID_SESSION): OtlpSpan {
  return otlpSpan(
    spanId,
    nativeAttributes({ 'gen_ai.conversation.id': sessionId, 'session.id': sessionId }),
    {
      name: 'gen_ai.chat',
      resource: { 'service.name': NATIVE_SOURCE },
      durationMs: NATIVE_DURATION_MS,
    },
  )
}

/** Normalize one span through the adapter seam, as ingest would hand it over. */
function normalize(
  adapter: HarnessAdapter,
  spanId: string,
  attributes: Record<string, unknown>,
): CanonicalRecord {
  return adapter.normalize(rawSpan({ spanId, source: NATIVE_SOURCE, attributes }))
}

/** The canonical model keys the session projection reads (canon/sessions.ts). */
const MODEL_KEYS = ['gen_ai.response.model', 'gen_ai.request.model', 'model'] as const

/** The model the record exposes to the projection, or undefined when it names none. */
function modelOf(record: CanonicalRecord): string | undefined {
  const raw = record.raw
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return undefined
  const attributes = raw as Record<string, unknown>
  for (const key of MODEL_KEYS) {
    const value = attributes[key]
    if (typeof value === 'string' && value !== '') return value
  }
  return undefined
}

/** The canonical tool-name keys the session projection reads, then the record's own name. */
const TOOL_NAME_KEYS = ['gen_ai.tool.name', 'tool.name', 'tool_name'] as const

/** The tool the record names, read the way the session projection reads it. */
function toolNameOf(record: CanonicalRecord): string | undefined {
  const raw = record.raw
  if (typeof raw === 'object' && raw !== null && !Array.isArray(raw)) {
    const attributes = raw as Record<string, unknown>
    for (const key of TOOL_NAME_KEYS) {
      const value = attributes[key]
      if (typeof value === 'string' && value !== '') return value
    }
  }
  return record.name
}

/** The token usage declaration the record carries, and the reason it gives. */
function tokenUsageOf(record: CanonicalRecord): { availability: string; reason?: string } | undefined {
  const declared = record.measurability?.token_usage
  if (declared === undefined) return undefined
  if (typeof declared === 'string') return { availability: declared }
  return { availability: declared.availability, reason: declared.reason }
}

/** The session's summary figures, as the dashboard projects them. */
async function summaryOf(store: CanonStore, sessionId: string): Promise<Record<string, unknown>> {
  await buildSessions(store)
  const payload = store.getSessionPayload(sessionId) as
    | { summary?: Record<string, unknown> }
    | undefined
  if (payload === undefined) throw new Error(`no session payload was built for ${sessionId}`)
  return payload.summary ?? {}
}

describe('the droid adapter — one harness, one bucket', () => {
  it('stamps the canonical harness id on every record it claims', async () => {
    const { droidAdapter } = await loadDroid()
    expect(droidAdapter.name).toBe('droid')
    expect(normalize(droidAdapter, 'native-chat', nativeAttributes()).harness).toBe('droid')
  })

  it('claims a native Factory span by its agent identity', async () => {
    const { droidAdapter } = await loadDroid()
    const attributed = new AdapterRegistry([droidAdapter]).attribute([
      rawSpan({
        spanId: 'native-chat',
        source: NATIVE_SOURCE,
        traceId: 'trace-native',
        attributes: nativeAttributes(),
      }),
    ])
    expect(attributed.get('native-chat')).toBe('droid')
  })

  it('does not claim a native span whose agent names another harness', async () => {
    const { droidAdapter } = await loadDroid()
    const span = rawSpan({
      spanId: 'foreign-agent',
      source: NATIVE_SOURCE,
      traceId: 'trace-foreign',
      attributes: nativeAttributes({ 'gen_ai.agent.name': 'claude-code' }),
    })
    expect(droidAdapter.detect(span)).toBe(0)
    const attributed = new AdapterRegistry([droidAdapter]).attribute([span])
    expect(attributed.get('foreign-agent')).toBeUndefined()
  })

  it('never reads the telemetry source name as evidence', async () => {
    const { droidAdapter } = await loadDroid()
    // A Droid span under an arbitrary collector name is still claimed.
    const named = rawSpan({
      spanId: 'odd-source',
      source: FOREIGN_SOURCE,
      traceId: 'trace-odd',
      attributes: nativeAttributes(),
    })
    expect(new AdapterRegistry([droidAdapter]).attribute([named]).get('odd-source')).toBe('droid')

    // A foreign agent under a Droid-sounding source name is not.
    const foreign = rawSpan({
      spanId: 'droid-sounding',
      source: 'droid',
      traceId: 'trace-sounding',
      attributes: nativeAttributes({ 'gen_ai.agent.name': 'codex' }),
    })
    expect(new AdapterRegistry([droidAdapter]).attribute([foreign]).get('droid-sounding')).toBeUndefined()
  })
})

describe('the native Factory path — the genai format', () => {
  it('maps the chat operation onto a canonical turn of the session', async () => {
    const { droidAdapter } = await loadDroid()
    const record = normalize(droidAdapter, 'native-chat', nativeAttributes())
    expect(record.op).toBe('llm.invoke')
    expect(record.sessionId).toBe(DROID_SESSION)
  })

  it('maps the tool operation onto a canonical tool record named after the tool', async () => {
    const { droidAdapter } = await loadDroid()
    const record = normalize(
      droidAdapter,
      'native-tool',
      nativeAttributes({
        'gen_ai.operation.name': 'execute_tool',
        'gen_ai.tool.name': TOOL_NAME,
        'gen_ai.tool.call.id': 'call_native_1',
        'gen_ai.tool.type': 'function',
      }),
    )
    expect(record.op).toBe('tool.invoke')
    expect(toolNameOf(record)).toBe(TOOL_NAME)
  })

  it('maps the agent operation onto the session root', async () => {
    const { droidAdapter } = await loadDroid()
    const record = normalize(
      droidAdapter,
      'native-agent',
      nativeAttributes({
        'gen_ai.operation.name': 'invoke_agent',
        'server.address': 'api.anthropic.com',
        'server.port': 443,
      }),
    )
    expect(record.op).toBe('agent.invoke')
  })

  it('takes the model from the span and never invents one', async () => {
    const { droidAdapter } = await loadDroid()
    expect(modelOf(normalize(droidAdapter, 'native-chat', nativeAttributes()))).toBe(NATIVE_MODEL)

    const withoutModel = nativeAttributes()
    delete withoutModel['gen_ai.request.model']
    expect(modelOf(normalize(droidAdapter, 'native-chat-no-model', withoutModel))).toBeUndefined()
  })

  it('does not require the consent-gated content attributes', async () => {
    const { droidAdapter } = await loadDroid()
    const record = normalize(droidAdapter, 'native-chat', nativeAttributes())
    expect(record.harness).toBe('droid')
    expect(record.op).toBe('llm.invoke')

    const withContent = normalize(
      droidAdapter,
      'native-chat-with-content',
      nativeAttributes({
        'gen_ai.input.messages': [{ role: 'user', content: 'hello' }],
        'gen_ai.output.messages': [{ role: 'assistant', content: 'hi' }],
      }),
    )
    expect(withContent.harness).toBe('droid')
    expect(withContent.op).toBe('llm.invoke')
  })

  it('declares the token usage not measurable instead of reporting a measured zero', async () => {
    const { droidAdapter } = await loadDroid()
    const record = normalize(droidAdapter, 'native-chat', nativeAttributes())
    const declared = tokenUsageOf(record)
    expect(declared, 'a record with no token counter must declare what it cannot measure').toBeDefined()
    expect(declared?.availability).toBe('not_measurable')
    // The reason names the harness and the metric, so the absence is legible.
    expect(declared?.reason).toMatch(/droid/i)
    expect(declared?.reason).toMatch(/token/i)
    // The identity stores zeros; the declaration is what says they are not a
    // measurement. A zero must never read as "nothing was spent".
    expect(record.tokens.freshInput).toBe(0)
    expect(record.tokens.output).toBe(0)
    for (const metric of ['token_usage', 'output', 'cache_read', 'cache_creation']) {
      const value = record.measurability?.[metric]
      if (value !== undefined) {
        expect(value, `${metric} must not be claimed measured when no counter was reported`).not.toBe('measured')
      }
    }
  })
})

describe('the plugin envelope path', () => {
  it('carries the session id, the model and the tool name onto the droid record', async () => {
    const { droidAdapter } = await loadDroid()
    const turn = normalize(droidAdapter, 'plugin-turn', pluginAttributes())
    expect(turn.harness).toBe('droid')
    expect(turn.sessionId).toBe(DROID_SESSION)
    expect(modelOf(turn)).toBe(PLUGIN_MODEL)

    const tool = normalize(droidAdapter, 'plugin-tool', pluginToolAttributes())
    expect(tool.harness).toBe('droid')
    expect(toolNameOf(tool)).toBe(TOOL_NAME)
  })

  it('never defaults the model on a plugin span that carries none', async () => {
    const { droidAdapter } = await loadDroid()
    const attributes = pluginAttributes()
    delete attributes['factory.model.id']
    delete attributes['factory.model']
    expect(modelOf(normalize(droidAdapter, 'plugin-turn-no-model', attributes))).toBeUndefined()
  })

  it('declares the token usage not measurable on the plugin path too', async () => {
    const { droidAdapter } = await loadDroid()
    const declared = tokenUsageOf(normalize(droidAdapter, 'plugin-turn', pluginAttributes()))
    expect(declared, 'a record with no token counter must declare what it cannot measure').toBeDefined()
    expect(declared?.availability).toBe('not_measurable')
    expect(declared?.reason).toMatch(/droid/i)
    expect(declared?.reason).toMatch(/token/i)
  })
})

describe('precedence — the plugin envelope is checked first', () => {
  it('counts the plugin turn of a session the plugin covers', async () => {
    const { droidAdapter } = await loadDroid()
    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan('plugin-turn')], store, [droidAdapter])
      const summary = await summaryOf(store, DROID_SESSION)
      expect(summary.turn_count).toBe(1)
      expect(summary.duration_ms).toBe(PLUGIN_DURATION_MS)
    } finally {
      store.close()
    }
  })

  it('excludes a native span from the totals of a session the plugin already covers', async () => {
    const { droidAdapter } = await loadDroid()
    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [pluginTurnSpan('plugin-turn'), nativeChatSpan('native-chat')],
        store,
        [droidAdapter],
      )
      const summary = await summaryOf(store, DROID_SESSION)
      // The plugin's counts, not the sum of both sources.
      expect(summary.turn_count).toBe(1)
      expect(summary.duration_ms).toBe(PLUGIN_DURATION_MS)
      // The native span is retained as evidence — it is excluded from the
      // totals, never dropped.
      const retained =
        store.get('native-chat') !== undefined || store.getQuarantine('native-chat') !== undefined
      expect(retained, 'the excluded native span must survive as evidence').toBe(true)
    } finally {
      store.close()
    }
  })

  it('counts a native span for a session the plugin does not cover', async () => {
    const { droidAdapter } = await loadDroid()
    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [nativeChatSpan('native-chat-only', NATIVE_ONLY_SESSION)],
        store,
        [droidAdapter],
      )
      const summary = await summaryOf(store, NATIVE_ONLY_SESSION)
      expect(summary.turn_count).toBe(1)
      expect(summary.duration_ms).toBe(NATIVE_DURATION_MS)
    } finally {
      store.close()
    }
  })
})
