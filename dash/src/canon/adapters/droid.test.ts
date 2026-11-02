// The Factory (Droid) canon adapter.
//
// Factory is ONE harness under ONE canonical id: `droid`, the id the Factory
// FILE source already stores (dash/src/providers/droid.ts names the provider
// `droid`, and dash/src/refresh/registry.ts maps the harness id `droid` onto
// the source root `~/.factory/sessions`). One harness, one bucket.
//
// ONE claim path reaches this adapter: the Kyber-owned Factory plugin
// envelope — spans whose attributes are namespaced `factory.*` and which name
// the contract they were emitted under in `factory.contract_version`. A span
// naming a missing or unknown contract version scores zero and is
// quarantined — see droid-envelope.test.ts.
//
// Factory's OWN native OpenTelemetry export is NOT claimed (plan D50). Its
// `genai` format is recognised only by `gen_ai.agent.name` = `droid`, and the
// export puts that attribute on the ROOT span alone: a trace of one root and
// `k` children scores 1/(1+k) at the registry's group vote, against a 0.6
// threshold, so every real turn is quarantined; the `legacy` format carries
// no attribute fingerprint at all. A native span is therefore quarantined as
// `unclaimed`, and the whole decoded packet is kept as the audit evidence.
//
// RED: written before `droid.ts` and `droid-envelope.ts` exist. Against the
// current tree every case below fails with the module under test missing.

import { describe, expect, it } from 'vitest'

import type { OtlpSpan } from '../../otel/receiver.js'
import { ingestBatch, ingestBatchWithAdapters } from '../ingest.js'
import { buildSessions } from '../sessions.js'
import { CanonStore } from '../store.js'
import type { CanonicalRecord } from '../types.js'
import type { HarnessAdapter } from './base.js'
import { AdapterRegistry } from './registry.js'
import { rawSpan } from './testing.js'

/** The Droid session id the plugin envelope reports for the session it covers. */
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

/** The trace the native export's root span and its children share. */
const NATIVE_TRACE = 'trace-native-genai'
/** The span id of the native trace's root. */
const NATIVE_ROOT = 'native-root'

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
 * The ROOT span names the agent (`gen_ai.agent.name` = `droid`), the
 * operation, the conversation and session ids, the model and the provider;
 * the `chat` and `execute_tool` children carry the operation, the ids and the
 * model, and NO agent identity — the export names the agent on the root
 * alone. That is what makes the trace's group vote 1/(1+k), and it is why a
 * real trace is never claimed.
 *
 * No token counter is among the attributes — the native export reports none —
 * and no content attribute either, because content capture is consented
 * elsewhere.
 */
function nativeRootAttributes(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    'gen_ai.operation.name': 'invoke_agent',
    'gen_ai.agent.name': 'droid',
    'gen_ai.conversation.id': DROID_SESSION,
    'session.id': DROID_SESSION,
    'gen_ai.request.model': NATIVE_MODEL,
    'gen_ai.provider.name': 'anthropic',
    'server.address': 'api.anthropic.com',
    'server.port': 443,
    ...over,
  }
}

/** The native export's child attributes: the operation, the ids and the model. */
function nativeChildAttributes(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    'gen_ai.operation.name': 'chat',
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

/** The native export's chat span: the session's own model call, seen again. */
function nativeChatSpan(spanId: string, sessionId: string = DROID_SESSION): OtlpSpan {
  return otlpSpan(
    spanId,
    nativeChildAttributes({ 'gen_ai.conversation.id': sessionId, 'session.id': sessionId }),
    {
      name: 'gen_ai.chat',
      resource: { 'service.name': NATIVE_SOURCE },
      durationMs: NATIVE_DURATION_MS,
    },
  )
}

/**
 * The native trace's root span: the `invoke_agent` span that names the agent
 * identity, and the only span of the trace that carries it.
 */
function nativeRootSpan(
  spanId: string = NATIVE_ROOT,
  sessionId: string = DROID_SESSION,
): OtlpSpan {
  return otlpSpan(
    spanId,
    nativeRootAttributes({ 'gen_ai.conversation.id': sessionId, 'session.id': sessionId }),
    { name: 'invoke_agent', traceId: NATIVE_TRACE },
  )
}

/** The native trace's per-turn chat span: the root's child. */
function nativeChatChildSpan(spanId: string): OtlpSpan {
  return otlpSpan(spanId, nativeChildAttributes(), {
    name: 'gen_ai.chat',
    traceId: NATIVE_TRACE,
    parentSpanId: NATIVE_ROOT,
  })
}

/** The native trace's per-tool span: the root's other child. */
function nativeToolChildSpan(
  spanId: string,
  over: Record<string, unknown> = {},
): OtlpSpan {
  return otlpSpan(
    spanId,
    nativeChildAttributes({
      'gen_ai.operation.name': 'execute_tool',
      'gen_ai.tool.name': TOOL_NAME,
      'gen_ai.tool.call.id': 'call_native_1',
      'gen_ai.tool.type': 'function',
      ...over,
    }),
    { name: 'gen_ai.execute_tool', traceId: NATIVE_TRACE, parentSpanId: NATIVE_ROOT },
  )
}

/** Normalize one span through the adapter seam, as ingest would hand it over. */
function normalize(
  adapter: HarnessAdapter,
  spanId: string,
  attributes: Record<string, unknown>,
  source: string = NATIVE_SOURCE,
  name: string = 'span',
): CanonicalRecord {
  return adapter.normalize(rawSpan({ spanId, source, attributes, name }))
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

/**
 * The counter classes the adapter declares on every record, and the word each
 * class's metric is named by.
 *
 * The classes are the canonical token counters: the total input-side usage,
 * the output usage, the cache read count and the cache creation count.
 */
const COUNTER_CLASSES = [
  { metric: 'token_usage', metricPattern: /token/i },
  { metric: 'output', metricPattern: /output/i },
  { metric: 'cache_read', metricPattern: /cache[\s-]?read/i },
  { metric: 'cache_creation', metricPattern: /cache[\s-]?creation/i },
] as const

/**
 * The claim a reason must not make: that the counter WAS measured.
 *
 * A reason states where the number WOULD come from, so a past-tense claim
 * that the counter was measured, recorded or is present contradicts the
 * declaration the reason belongs to.
 */
const PRESENCE_CLAIMS = [
  /\b(?:was|were|has been|have been)\s+(?:measured|recorded|captured|counted|exported|present|available|known)\b/i,
  /\bthe\s+(?:tokens?|counters?|numbers?)\s+(?:exist|are present|are known|were present|were measured)\b/i,
  /\b(?:telemetry|export|plugin|span)\s+(?:measured|recorded|captured|counted|exported)\s+(?:the\s+)?(?:token|counter|usage|number)/i,
] as const

/**
 * The word a reason must carry when it states that the counter is absent.
 *
 * The reason has to say the number is not there, not merely say where it
 * would come from: a reader who only reads the source clause must not come
 * away believing the counter was measured.
 */
const ABSENCE_MARKER =
  /\b(?:not|never|no|none|nothing|without|cannot|absent|lacks?|missing|unavailable|outside|elsewhere)\b/i

/**
 * The declaration a counter class carries, and the reason it gives.
 */
function declarationOf(
  record: CanonicalRecord,
  metric: string,
): { availability: string; reason?: string } | undefined {
  const declared = record.measurability?.[metric]
  if (declared === undefined) return undefined
  if (typeof declared === 'string') return { availability: declared }
  return { availability: declared.availability, reason: declared.reason }
}

/**
 * Assert what a counter class's reason must carry.
 *
 * The wording is the implementer's to choose, so the reason is checked for
 * the FACTS it states — the harness, the metric, the file source the number
 * would come from, and the session-id pairing that supplies it — and never
 * for one exact sentence.
 */
function expectCounterReason(
  record: CanonicalRecord,
  metric: string,
  metricPattern: RegExp,
): void {
  const declared = declarationOf(record, metric)
  expect(declared, `the ${metric} class must declare what it cannot measure`).toBeDefined()
  expect(declared?.availability, `the ${metric} class must not be claimed measured`).toBe(
    'not_measurable',
  )
  const reason = declared?.reason ?? ''
  expect(reason, `the ${metric} reason must name the harness`).toMatch(/droid/i)
  expect(reason, `the ${metric} reason must name the metric`).toMatch(metricPattern)
  // The file source: the Factory session file's companion settings are where
  // the counter comes from.
  expect(reason, `the ${metric} reason must name the file source the counter comes from`).toMatch(
    /file|settings/i,
  )
  // The pairing: the number is supplied when this session's id matches a
  // file-sourced session.
  expect(reason, `the ${metric} reason must name the session id the pairing is made on`).toMatch(
    /\bsession(?:['’]s)?[\s_-]*(?:ids?|identifier)\b/i,
  )
  expect(reason, `the ${metric} reason must name the pairing that supplies the counter`).toMatch(
    /match|pair|join|equal|same/i,
  )
  // The reason states where the number would come from. It never says the
  // counter is there.
  expect(reason, `the ${metric} reason must state that the counter is absent`).toMatch(
    ABSENCE_MARKER,
  )
  for (const claim of PRESENCE_CLAIMS) {
    expect(reason, `the ${metric} reason must not claim the counter was measured`).not.toMatch(
      claim,
    )
  }
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

/**
 * The reason a span was quarantined under, or undefined when it was not
 * quarantined at all.
 */
function quarantineReason(store: CanonStore, spanId: string): string | undefined {
  return store.getQuarantine(spanId)?.reason
}

/**
 * The attributes a quarantined span's stored packet carries, or an empty map.
 *
 * The packet is the decoded span, not the normalized record: it is the only
 * surviving copy of a span nothing claimed, so it is where the export's own
 * vocabulary is still readable.
 */
function quarantinedAttributes(store: CanonStore, spanId: string): Record<string, unknown> {
  const payload = store.getQuarantine(spanId)?.rawPayload
  if (typeof payload !== 'string') return {}
  const parsed = JSON.parse(payload) as { attributes?: Record<string, unknown> }
  return parsed.attributes ?? {}
}

describe('the droid adapter — one harness, one bucket', () => {
  it('stamps the canonical harness id on every record it claims', async () => {
    const { droidAdapter } = await loadDroid()
    expect(droidAdapter.name).toBe('droid')
    expect(
      normalize(droidAdapter, 'plugin-turn', pluginAttributes(), PLUGIN_SOURCE).harness,
    ).toBe('droid')
  })

  it('claims the plugin envelope with full confidence and its canonical operation', async () => {
    const { droidAdapter } = await loadDroid()
    const registry = new AdapterRegistry([droidAdapter])
    const envelope = rawSpan({
      spanId: 'plugin-turn',
      source: PLUGIN_SOURCE,
      traceId: 'trace-plugin',
      attributes: pluginAttributes(),
    })
    expect(registry.scoreGroup(PLUGIN_SOURCE, 'trace-plugin', [envelope])).toEqual({
      harness: 'droid',
      confidence: 1,
    })
    expect(registry.attribute([envelope]).get('plugin-turn')).toBe('droid')
    expect(
      normalize(droidAdapter, 'plugin-turn', pluginAttributes(), PLUGIN_SOURCE, 'factory.turn').op,
    ).toBe('llm.invoke')
  })

  it('claims no native Factory span — the genai vocabulary is not this adapter\'s fingerprint', async () => {
    const { droidAdapter } = await loadDroid()
    const registry = new AdapterRegistry([droidAdapter])
    const native = rawSpan({
      spanId: 'native-root',
      source: NATIVE_SOURCE,
      traceId: NATIVE_TRACE,
      attributes: nativeRootAttributes(),
    })
    expect(droidAdapter.detect(native), 'a native Factory span must score no evidence').toBe(0)
    expect(
      registry.attribute([native]).get('native-root'),
      'a native Factory span must not be attributed to any harness',
    ).toBeUndefined()
  })

  it('does not claim a native span whose agent names another harness', async () => {
    const { droidAdapter } = await loadDroid()
    const span = rawSpan({
      spanId: 'foreign-agent',
      source: NATIVE_SOURCE,
      traceId: 'trace-foreign',
      attributes: nativeRootAttributes({ 'gen_ai.agent.name': 'claude-code' }),
    })
    expect(droidAdapter.detect(span)).toBe(0)
    const attributed = new AdapterRegistry([droidAdapter]).attribute([span])
    expect(attributed.get('foreign-agent')).toBeUndefined()
  })

  it('never reads the telemetry source name as evidence', async () => {
    const { droidAdapter } = await loadDroid()
    // The plugin envelope under an arbitrary collector name is still claimed.
    const named = rawSpan({
      spanId: 'odd-source',
      source: FOREIGN_SOURCE,
      traceId: 'trace-odd',
      attributes: pluginAttributes(),
    })
    expect(new AdapterRegistry([droidAdapter]).attribute([named]).get('odd-source')).toBe('droid')

    // A foreign span under a Droid-sounding source name is not.
    const foreign = rawSpan({
      spanId: 'droid-sounding',
      source: 'droid',
      traceId: 'trace-sounding',
      attributes: { 'codeburn.provider': 'github-copilot' },
    })
    expect(
      new AdapterRegistry([droidAdapter]).attribute([foreign]).get('droid-sounding'),
    ).toBeUndefined()
  })
})

describe('the native Factory export — withdrawn (D50)', () => {
  it('quarantines the root of a native trace as unclaimed and stores no record', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([nativeRootSpan()], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get(NATIVE_ROOT), 'no native span may be stored as a droid record').toBeUndefined()
    expect(quarantineReason(store, NATIVE_ROOT)).toBe('unclaimed')
    store.close()
  })

  it('quarantines the chat child of a native trace as unclaimed and stores no record', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([nativeChatChildSpan('native-chat-child')], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get('native-chat-child')).toBeUndefined()
    expect(quarantineReason(store, 'native-chat-child')).toBe('unclaimed')
    store.close()
  })

  it('quarantines the tool child of a native trace as unclaimed and stores no record', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch([nativeToolChildSpan('native-tool-child')], store)

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 1, rejected: 0 })
    expect(store.get('native-tool-child')).toBeUndefined()
    expect(quarantineReason(store, 'native-tool-child')).toBe('unclaimed')
    store.close()
  })

  it('quarantines every span of a native trace alike — the root and both children', () => {
    const store = new CanonStore(':memory:')
    const outcome = ingestBatch(
      [
        nativeRootSpan(),
        nativeChatChildSpan('native-chat-child'),
        nativeToolChildSpan('native-tool-child'),
      ],
      store,
    )

    expect(outcome).toMatchObject({ accepted: 0, quarantined: 3, rejected: 0 })
    expect(store.count(), 'a native trace must leave no record behind').toBe(0)
    for (const spanId of [NATIVE_ROOT, 'native-chat-child', 'native-tool-child']) {
      expect(quarantineReason(store, spanId), `${spanId} must be quarantined as unclaimed`).toBe(
        'unclaimed',
      )
    }
    store.close()
  })

  it('keeps the whole decoded packet of a quarantined native span as the evidence', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([nativeRootSpan()], store)

    const packet = store.getQuarantine(NATIVE_ROOT)?.rawPayload
    expect(typeof packet, 'the quarantined span must keep its decoded packet').toBe('string')
    const attributes = quarantinedAttributes(store, NATIVE_ROOT)
    expect(attributes['gen_ai.agent.name']).toBe('droid')
    expect(attributes['gen_ai.conversation.id']).toBe(DROID_SESSION)
    expect(attributes['gen_ai.request.model']).toBe(NATIVE_MODEL)
    store.close()
  })

  it('declares a native span\'s counter classes with the plugin path\'s reasons', async () => {
    const { droidAdapter } = await loadDroid()
    // The adapter has ONE reason set now: the plugin path's, which names the
    // file source the counter would come from. The native path's own reasons
    // are gone, so no record can declare them.
    const record = normalize(droidAdapter, 'native-root', nativeRootAttributes())
    for (const { metric, metricPattern } of COUNTER_CLASSES) {
      expectCounterReason(record, metric, metricPattern)
    }
  })

  it('exports no native-claim symbol', async () => {
    const module = await loadDroid()
    expect(Object.keys(module), 'the native claim is withdrawn from the adapter').not.toContain(
      'isDroidNativeSpan',
    )
    expect(Object.keys(module)).not.toContain('isDroidNativeRecord')
    expect(Object.keys(module)).not.toContain('DROID_NATIVE_SUPERSEDED_REASON')
  })

  it('claims no native span that rides in on the plugin\'s own source', async () => {
    // The registry's second pass hands an undecided group the attribution of a
    // confidently mapped group of the SAME source. A native span that shares
    // the plugin's source inherits the plugin's harness — and it must still
    // not become a droid record.
    const { droidAdapter } = await loadDroid()
    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [
          pluginTurnSpan('plugin-turn'),
          otlpSpan(
            'native-tool',
            nativeChildAttributes({
              'gen_ai.operation.name': 'execute_tool',
              'gen_ai.tool.name': TOOL_NAME,
              'gen_ai.tool.call.id': 'call_native_1',
            }),
            {
              name: 'gen_ai.execute_tool',
              traceId: NATIVE_TRACE,
              resource: { 'service.name': PLUGIN_SOURCE },
            },
          ),
        ],
        store,
        [droidAdapter],
      )
      expect(store.get('native-tool'), 'a native span must never become a droid record').toBeUndefined()
      expect(quarantineReason(store, 'native-tool')).toBe('unclaimed')
    } finally {
      store.close()
    }
  })
})

describe('the plugin envelope path', () => {
  it('carries the session id, the model and the tool name onto the droid record', async () => {
    const { droidAdapter } = await loadDroid()
    const turn = normalize(droidAdapter, 'plugin-turn', pluginAttributes(), PLUGIN_SOURCE)
    expect(turn.harness).toBe('droid')
    expect(turn.sessionId).toBe(DROID_SESSION)
    expect(modelOf(turn)).toBe(PLUGIN_MODEL)

    const tool = normalize(droidAdapter, 'plugin-tool', pluginToolAttributes(), PLUGIN_SOURCE)
    expect(tool.harness).toBe('droid')
    expect(toolNameOf(tool)).toBe(TOOL_NAME)
  })

  it('never defaults the model on a plugin span that carries none', async () => {
    const { droidAdapter } = await loadDroid()
    const attributes = pluginAttributes()
    delete attributes['factory.model.id']
    delete attributes['factory.model']
    expect(
      modelOf(normalize(droidAdapter, 'plugin-turn-no-model', attributes, PLUGIN_SOURCE)),
    ).toBeUndefined()
  })

  it('declares the token usage not measurable and names where the counter would come from', async () => {
    const { droidAdapter } = await loadDroid()
    const record = normalize(droidAdapter, 'plugin-turn', pluginAttributes(), PLUGIN_SOURCE)
    const declared = tokenUsageOf(record)
    expect(declared, 'a record with no token counter must declare what it cannot measure').toBeDefined()
    expect(declared?.availability).toBe('not_measurable')
    expect(declared?.reason).toMatch(/droid/i)
    expect(declared?.reason).toMatch(/token/i)
    // The reason also says WHERE the number would come from — the Factory
    // session file's companion settings, supplied when this session's id
    // matches a file-sourced session — so a reader can tell "never
    // measured" from "not joined yet".
    expectCounterReason(record, 'token_usage', /token/i)
  })

  it('names the file source and the session-id pairing for the output counter', async () => {
    const { droidAdapter } = await loadDroid()
    const record = normalize(droidAdapter, 'plugin-turn', pluginAttributes(), PLUGIN_SOURCE)
    expectCounterReason(record, 'output', /output/i)
  })

  it('names the file source and the session-id pairing for the cache read counter', async () => {
    const { droidAdapter } = await loadDroid()
    const record = normalize(droidAdapter, 'plugin-turn', pluginAttributes(), PLUGIN_SOURCE)
    expectCounterReason(record, 'cache_read', /cache[\s-]?read/i)
  })

  it('names the file source and the session-id pairing for the cache creation counter', async () => {
    const { droidAdapter } = await loadDroid()
    const record = normalize(droidAdapter, 'plugin-turn', pluginAttributes(), PLUGIN_SOURCE)
    expectCounterReason(record, 'cache_creation', /cache[\s-]?creation/i)
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

  it('quarantines a native span of a session the plugin covers, leaving the plugin\'s totals alone', async () => {
    const { droidAdapter } = await loadDroid()
    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [pluginTurnSpan('plugin-turn'), nativeRootSpan('native-root')],
        store,
        [droidAdapter],
      )
      const summary = await summaryOf(store, DROID_SESSION)
      // The plugin's counts, not the sum of both sources.
      expect(summary.turn_count).toBe(1)
      expect(summary.duration_ms).toBe(PLUGIN_DURATION_MS)
      // The native span is quarantined as unclaimed — there is no native
      // reason left, and no native claim to supersede.
      expect(quarantineReason(store, 'native-root')).toBe('unclaimed')
      expect(store.get('native-root')).toBeUndefined()
    } finally {
      store.close()
    }
  })

  it('stores no native turn for a session the plugin does not cover', async () => {
    const { droidAdapter } = await loadDroid()
    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [nativeRootSpan('native-root-only', NATIVE_ONLY_SESSION)],
        store,
        [droidAdapter],
      )
      await buildSessions(store)
      expect(store.get('native-root-only')).toBeUndefined()
      expect(
        store.getSessionPayload(NATIVE_ONLY_SESSION),
        'a native span must not build a session',
      ).toBeUndefined()
    } finally {
      store.close()
    }
  })
})

describe('the native Factory vocabulary — kept as the quarantine evidence', () => {
  it('keeps the whole native vocabulary in the quarantined packet', () => {
    const store = new CanonStore(':memory:')
    ingestBatch(
      [
        nativeRootSpan(),
        nativeChatChildSpan('native-chat-child'),
        nativeToolChildSpan('native-tool-child'),
      ],
      store,
    )

    const root = quarantinedAttributes(store, NATIVE_ROOT)
    expect(root['gen_ai.agent.name']).toBe('droid')
    expect(root['gen_ai.operation.name']).toBe('invoke_agent')
    expect(root['gen_ai.conversation.id']).toBe(DROID_SESSION)
    expect(root['session.id']).toBe(DROID_SESSION)
    expect(root['gen_ai.request.model']).toBe(NATIVE_MODEL)
    expect(root['gen_ai.provider.name']).toBe('anthropic')
    expect(root['server.address']).toBe('api.anthropic.com')
    expect(root['server.port']).toBe(443)

    const tool = quarantinedAttributes(store, 'native-tool-child')
    expect(tool['gen_ai.operation.name']).toBe('execute_tool')
    expect(tool['gen_ai.tool.name']).toBe(TOOL_NAME)
    expect(tool['gen_ai.tool.call.id']).toBe('call_native_1')
    expect(tool['gen_ai.tool.type']).toBe('function')
    store.close()
  })

  it('keeps the finish reasons as the array the export sends', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([nativeChatSpan('native-chat-finish')], store)

    // An array, not a joined string and not the first element alone.
    expect(
      quarantinedAttributes(store, 'native-chat-finish')['gen_ai.response.finish_reasons'],
    ).toEqual(['stop'])
    store.close()
  })

  it('keeps a policy-denied tool call\'s error type as evidence, and claims no status for it', () => {
    const store = new CanonStore(':memory:')
    ingestBatch(
      [nativeToolChildSpan('native-tool-rejected', { 'error.type': 'rejected' })],
      store,
    )

    // The error type is the evidence of WHY the call was denied, and it
    // survives in the packet. No record is claimed, so no status is stamped.
    expect(quarantinedAttributes(store, 'native-tool-rejected')['error.type']).toBe('rejected')
    expect(store.get('native-tool-rejected')).toBeUndefined()
    store.close()
  })

  it('claims no record for an ordinary native tool call either', () => {
    const store = new CanonStore(':memory:')
    ingestBatch([nativeToolChildSpan('native-tool-allowed')], store)

    expect(store.get('native-tool-allowed')).toBeUndefined()
    expect(quarantineReason(store, 'native-tool-allowed')).toBe('unclaimed')
    store.close()
  })
})
