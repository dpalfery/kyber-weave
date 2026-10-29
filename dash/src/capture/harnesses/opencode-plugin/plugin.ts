// Opencode plugin skeleton (T2): session/turn/span lifecycle hooks.
//
// One codebase serves both opencode targets — Desktop and CLI v2 — from the
// dash-CLI-managed home `dash/src/capture/harnesses/opencode-plugin/`. There
// is no standalone package: the dash CLI installs this file into opencode's
// own plugin directories (see `resolveOpencodePluginPaths`), and opencode
// loads it through its config `plugins` keys (see
// `OPENCODE_PLUGIN_CONFIG_KEYS`). v1 hook names and v2 namespaced hooks
// share the T0 catalog (`fixtures/hook-catalog.json`); both targets receive
// the same function identities from `registerOpencodePlugin`.
//
// Every hook is best-effort and never fails the host — the CommandCode
// `statusbar.ts` swallow-and-continue precedent. An emitter throw, an async
// rejection, or a delivery timeout is recorded on the bounded `failures`
// tail (and offered to `onError`) while the hook itself resolves quietly, so
// the session always continues. The default emitter records in memory only;
// production delivery arrives through the injected `emit` port, backed by
// the T5 transport outbox (`transport/outbox.ts`, OTLP/HTTP to 127.0.0.1:4318).
// The in-memory `events`/`failures` tails are count- and byte-bounded
// (oldest-first, with drop counters) and an ended session's payload bodies
// are released at its `session.end` — a long session must not grow host
// memory without bound (F2); the transport, not this tail, is the delivery
// record.
//
// T8 assembles the full pipeline in this installed entry: every hook boundary
// runs hook→mapper→turn→outbox→sender in order. The hook stamps a
// mapper-shaped payload (`mapper.ts`) onto an envelope span, the same identity
// stamps a turn outcome (`turn.ts`) onto a `tool_result` log, and the event
// spools through the bounded outbox to the live loopback sender
// (`transport/outbox.ts` + `transport/otlp-sender.ts`). The mapping evidence
// is recorded beside `events`/`failures`; the transport still never fails the
// host. The entry ships as the plugin directory, so these relative imports
// resolve in place wherever the dash CLI installs it.

import { mapOpencodePayloadToOtlpSpan, type OpencodePluginSpanPayload } from './mapper.js'
import { mapOpencodeTurnToOtlpLog, type OpencodeTurnOutcomePayload } from './turn.js'
import { createOutbox, type Outbox } from './transport/outbox.js'
import {
  DEFAULT_OTLP_ENDPOINT,
  createOtlpSender,
  type OtlpFetch,
  type OtlpSender,
} from './transport/otlp-sender.js'

/** The plugin id dash CLI writes into opencode's `plugins` config keys. */
export const OPENCODE_PLUGIN_ID = 'kyberdash-opencode'

/** The full lifecycle, in firing order. */
export const OPENCODE_PLUGIN_HOOK_KINDS = [
  'session.start',
  'turn.start',
  'span.start',
  'span.end',
  'turn.end',
  'session.end',
] as const

/** v2 `ctx.session.hook("context")` kind per the T0 pin (fixtures/context-payload.json @opencode/plugin 2.1.4). Not a lifecycle boundary. */
export const OPENCODE_CONTEXT_HOOK_KIND = 'session.context' as const

/**
 * Tool lifecycle hooks (B1). Names pinned by fixtures/hook-catalog.json: the
 * v1 `tool.execute.before`/`tool.execute.after` hooks and the v2 namespaced
 * `tool` surface (`execute.before`/`execute.after`). Provider-usage surfaces
 * (`session.http.response`, `session.model.request`, `session.generate`) are
 * deliberately NOT registered: the catalog pins their names only, no payload
 * shape, so there is nothing verifiable to map (no guessing, no live calls).
 */
export const OPENCODE_PLUGIN_TOOL_HOOK_KINDS = [
  'tool.execute.before',
  'tool.execute.after',
] as const

export type OpencodeLifecycleKind = (typeof OPENCODE_PLUGIN_HOOK_KINDS)[number]

export type OpencodeToolHookKind = (typeof OPENCODE_PLUGIN_TOOL_HOOK_KINDS)[number]

export type OpencodeHookKind = OpencodeLifecycleKind | OpencodeToolHookKind | typeof OPENCODE_CONTEXT_HOOK_KIND

/** Opencode config keys the dash CLI manages for this plugin (never hand-edited paths). */
export const OPENCODE_PLUGIN_CONFIG_KEYS = {
  /** v2 `plugins` list in `opencode.jsonc`; v1 `plugin` entries resolve to the same file. */
  plugins: 'plugins',
} as const

export type OpencodeHookPayload = {
  sessionId: string
  turnIndex?: number
  spanId?: string
  /** v2 SessionContext.request message identity (chat.params message.id on the v1 ladder). */
  messageId?: string
  /** Tool name observed on a tool lifecycle hook (T0 `tools` bucket `tool`). Never synthesized. */
  tool?: string
  /** Tool call identity observed on a tool lifecycle hook (T0 `callID`). Never synthesized. */
  callId?: string
  /**
   * Tool outcome observed on `tool.execute.after`: false when the T0
   * `errors` bucket names an error, true when it names none. Absent on
   * `tool.execute.before` — the outcome is not yet known, never defaulted.
   */
  success?: boolean
  /**
   * Milliseconds between the paired before/after observations (entry-side
   * clock). Absent when no before was seen — never zero.
   */
  durationMs?: number
  /**
   * UTF-8 byte length of the observed tool output text. The text itself is
   * never carried (consent content stays exactly as it is); the size is
   * metadata, like the duration. Absent when no output was observed.
   */
  resultSizeBytes?: number
  /** Assembled system prompt (v2 SessionContext.system; v1 experimental.chat.system.transform output). */
  systemPrompt?: string
  /** JSON-serialized tool definitions (v2 SessionContext.tools; v1 tool.definition output). */
  toolsText?: string
  /** Skill names listed for the session (v2 SessionContext extension per the T0 pin). */
  skills?: readonly string[]
  /** Model identity (v2 SessionContext.model.id; v1 chat.params model.id). */
  model?: string
  /** Provider identity (2.1.4 SessionContext.model.providerID). */
  provider?: string
  /** Model context window from the 2.1.4 catalog (`limit.context`); absent when the catalog cannot say (D6). */
  contextLimit?: number
  /** Agent identity (v2 SessionContext.agent; v1 chat.params agent). */
  agent?: string
}

export type OpencodeHookEvent = OpencodeHookPayload & {
  kind: OpencodeHookKind
  timestamp: string
}

/**
 * Bounded normalized failure diagnostics (F2): the error name and a capped
 * message only. The original thrown value is never retained — an error
 * object can carry arbitrarily large attached buffers, causes, and custom
 * fields that a string rendering would conceal. `String()` of this record
 * renders the message, preserving the long-standing assertion shape that a
 * retained failure stringifies to its message.
 */
export type OpencodeFailureDiagnostics = {
  name: string
  message: string
  toString(): string
}

export type OpencodeHookFailure = {
  kind: OpencodeHookKind
  /** The session the failed delivery belonged to; absent when the payload named none. */
  sessionId?: string
  /** Bounded normalized diagnostics; the original thrown value is not retained. */
  error: OpencodeFailureDiagnostics
}

/**
 * The `onError` tap payload. Unlike the retained `OpencodeHookFailure`, the
 * tap receives the original thrown value transiently (caller-owned, never
 * stored by the plugin) under the pre-existing `{ kind, error }` contract.
 */
export type OpencodeHookErrorTap = {
  kind: OpencodeHookKind
  error: unknown
}

/** Delivery port; production posts to the local OTLP receiver through the T5 transport outbox, tests inject a fake. */
export type OpencodePluginEmitter = (event: OpencodeHookEvent) => void | Promise<void>

/**
 * Adapt a T5 outbox spool to the delivery port. The plugin itself stays
 * transport-agnostic: production passes `outbox.emit` (or this adapter over
 * any `{ enqueue }` spool) as `emit`, and bounded offline spooling,
 * backoff, and drop accounting happen behind the port.
 */
export function createOutboxEmitter(spool: {
  enqueue: (event: OpencodeHookEvent) => void
}): OpencodePluginEmitter {
  return (event) => {
    spool.enqueue(event)
  }
}

export type OpencodePluginOptions = {
  emit?: OpencodePluginEmitter
  now?: () => string
  /** Delivery budget per hook; expiry is swallowed, never thrown. Defaults to 250ms. */
  timeoutMs?: number
  /**
   * Observability tap for swallowed failures; itself runs swallowed. Receives
   * the original thrown value transiently — the plugin never stores it.
   */
  onError?: (failure: OpencodeHookErrorTap) => void
  /**
   * Bounds on the in-memory delivery-attempt history (F2). The skeleton is a
   * diagnostics tail, not an event store: the transport (outbox) is the
   * delivery record. Every bound evicts oldest-first and counts the eviction,
   * and `session.end` releases the ended session's retained payload bodies.
   */
  diagnostics?: OpencodeDiagnosticsBounds
}

/**
 * Count and byte bounds for the skeleton's retained diagnostics. Byte budgets
 * are enforced against a defensible retained-size estimate (UTF-16 storage is
 * two bytes per unit across every retained string, plus per-value overhead),
 * not a character count — a multibyte payload and a large identity cost what
 * they actually retain.
 */
export type OpencodeDiagnosticsBounds = {
  /** Maximum retained delivery-attempt records. Defaults to 200. */
  eventLimit?: number
  /** Maximum retained failure records. Defaults to 100. */
  failureLimit?: number
  /** Maximum estimated retained bytes across events. Defaults to 1 MiB. */
  maxEventBytes?: number
  /** Maximum estimated retained bytes across failures. Defaults to 256 KiB. */
  maxFailureBytes?: number
}

/**
 * The host surface the plugin attaches to. Desktop calls `hooks({...})`
 * (v1 shape); CLI v2 exposes namespaced `session`/`tool` surfaces. Every
 * member is optional — a host without a hook surface is tolerated, and the
 * plugin still exposes its hook set.
 */
export type OpencodePluginHost = {
  hooks?: (registered: Record<OpencodeHookKind, OpencodeHookHandler>) => void
  session?: Record<string, OpencodeHookHandler>
  tool?: Record<string, OpencodeHookHandler>
}

export type OpencodeHookHandler = (payload: OpencodeHookPayload) => Promise<void>

export type OpencodePlugin = {
  hooks: Record<OpencodeLifecycleKind, OpencodeHookHandler>
  /** v2 session.context handler (content payload); same never-fail-host contract as hooks. */
  context: OpencodeHookHandler
  /** B1 tool lifecycle handlers; same never-fail-host contract as hooks. */
  toolHooks: Record<OpencodeToolHookKind, OpencodeHookHandler>
  /**
   * Freshest delivery attempts, in firing order, at most the diagnostics
   * event bound — a debugging tail, never a session archive. Payload bodies
   * of an ended session are released at its `session.end`.
   */
  events: OpencodeHookEvent[]
  /** Freshest swallowed delivery failures, in order, at most the diagnostics failure bound. */
  failures: OpencodeHookFailure[]
  /** Lifetime delivery-attempt records evicted oldest-first by the diagnostics bounds. */
  droppedEvents: number
  /** Lifetime failure records evicted oldest-first by the diagnostics bounds. */
  droppedFailures: number
}

const DEFAULT_TIMEOUT_MS = 250

/** Default diagnostics bounds (F2): a tail for debugging, not a session archive. */
const DEFAULT_DIAGNOSTIC_EVENT_LIMIT = 200
const DEFAULT_DIAGNOSTIC_FAILURE_LIMIT = 100
const DEFAULT_DIAGNOSTIC_MAX_EVENT_BYTES = 1_048_576
const DEFAULT_DIAGNOSTIC_MAX_FAILURE_BYTES = 262_144
/** Cap on one normalized failure message: failures stay small by construction. */
const MAX_FAILURE_MESSAGE_CHARS = 2_048

/**
 * Estimated retained bytes of one diagnostic event record. Every retained
 * string costs two bytes per UTF-16 unit plus a per-value overhead, and the
 * record itself carries object/array overhead — identity and metadata
 * (session id, span id, timestamps, model names) count alongside content, so
 * a large identity cannot hide beside a small prompt.
 */
function eventRetainedBytes(event: OpencodeHookEvent): number {
  const stringBytes = (value: string | undefined): number =>
    value === undefined ? 0 : value.length * 2 + 16
  let bytes = 96
  bytes += stringBytes(event.kind)
  bytes += stringBytes(event.sessionId)
  bytes += stringBytes(event.spanId)
  bytes += stringBytes(event.messageId)
  bytes += stringBytes(event.timestamp)
  bytes += stringBytes(event.systemPrompt)
  bytes += stringBytes(event.toolsText)
  bytes += stringBytes(event.model)
  bytes += stringBytes(event.provider)
  bytes += stringBytes(event.agent)
  bytes += 32
  if (event.skills !== undefined) {
    bytes += 32
    for (const skill of event.skills) bytes += stringBytes(skill)
  }
  return bytes
}

/**
 * Normalize a thrown value to bounded diagnostics. Only the error name and a
 * capped message are kept; attached buffers, causes, and custom fields are
 * never retained, however small their string rendering looks. `String()` of
 * the result renders the message.
 */
function normalizeFailureError(error: unknown): OpencodeFailureDiagnostics {
  const cap = (text: string): string =>
    text.length > MAX_FAILURE_MESSAGE_CHARS ? text.slice(0, MAX_FAILURE_MESSAGE_CHARS) : text
  const diagnosed = (name: string, message: string): OpencodeFailureDiagnostics => {
    const capped = cap(message)
    return { name, message: capped, toString: () => capped }
  }
  if (typeof error === 'string') return diagnosed('Error', error)
  if (error instanceof Error) {
    const name = typeof error.name === 'string' && error.name !== '' ? error.name : 'Error'
    return diagnosed(name, String(error.message ?? ''))
  }
  try {
    return diagnosed('Error', String(error))
  } catch {
    return diagnosed('Error', 'unrenderable failure value')
  }
}

/**
 * Estimated retained bytes of one normalized failure record. Every retained
 * string counts — kind and session id alongside the normalized name and
 * message — so a large or highly distinct failure identity cannot bypass
 * the byte budget.
 */
function failureRetainedBytes(failure: OpencodeHookFailure): number {
  return (
    64 +
    failure.kind.length * 2 +
    (failure.sessionId === undefined ? 0 : failure.sessionId.length * 2 + 16) +
    failure.error.name.length * 2 +
    failure.error.message.length * 2 +
    32
  )
}

/**
 * The diagnostic copy of an event. The transport owns the original object it
 * was handed; the tail retains only this copy, so session-end cleanup can
 * never mutate an event still queued for delivery.
 */
function diagnosticCopyOf(event: OpencodeHookEvent): OpencodeHookEvent {
  // Spread preserves absent keys as absent (a consent-dropped field must
  // stay absent, never `key: undefined`); only the shared array is re-owned.
  const copy: OpencodeHookEvent = { ...event }
  if (event.skills !== undefined) copy.skills = [...event.skills]
  return copy
}

/**
 * Create the skeleton plugin. Pure construction: no host is touched until
 * `registerOpencodePlugin` runs, so tests can drive `hooks` directly.
 */
export function createOpencodePlugin(options: OpencodePluginOptions = {}): OpencodePlugin {
  const emit = options.emit ?? ((): void => undefined)
  const now = options.now ?? ((): string => new Date().toISOString())
  const timeoutMs = Math.max(1, Math.floor(options.timeoutMs ?? DEFAULT_TIMEOUT_MS))
  const onError = options.onError
  const eventLimit = Math.max(1, Math.floor(options.diagnostics?.eventLimit ?? DEFAULT_DIAGNOSTIC_EVENT_LIMIT))
  const failureLimit = Math.max(1, Math.floor(options.diagnostics?.failureLimit ?? DEFAULT_DIAGNOSTIC_FAILURE_LIMIT))
  const maxEventBytes = Math.max(1, Math.floor(options.diagnostics?.maxEventBytes ?? DEFAULT_DIAGNOSTIC_MAX_EVENT_BYTES))
  const maxFailureBytes = Math.max(1, Math.floor(options.diagnostics?.maxFailureBytes ?? DEFAULT_DIAGNOSTIC_MAX_FAILURE_BYTES))

  const plugin: OpencodePlugin = {
    hooks: undefined as never,
    context: undefined as never,
    toolHooks: undefined as never,
    events: [],
    failures: [],
    droppedEvents: 0,
    droppedFailures: 0,
  }
  let retainedEventBytes = 0
  let retainedFailureBytes = 0

  /**
   * WHY oldest-first: the tail answers "what just happened" for the live
   * session; the transport already delivered the head. A single record
   * larger than the whole byte budget is omitted outright (counted as
   * dropped) instead of evicting the entire tail — delivery still receives
   * the full original, which the transport owns untouched.
   */
  const recordEvent = (event: OpencodeHookEvent): void => {
    const diagnostic = diagnosticCopyOf(event)
    if (eventRetainedBytes(diagnostic) > maxEventBytes) {
      plugin.droppedEvents += 1
      return
    }
    plugin.events.push(diagnostic)
    retainedEventBytes += eventRetainedBytes(diagnostic)
    while (plugin.events.length > eventLimit || retainedEventBytes > maxEventBytes) {
      const evicted = plugin.events.shift()
      if (evicted === undefined) break
      retainedEventBytes -= eventRetainedBytes(evicted)
      plugin.droppedEvents += 1
    }
  }

  const recordFailure = (kind: OpencodeHookKind, sessionId: string | undefined, error: unknown): void => {
    const failure: OpencodeHookFailure =
      sessionId === undefined
        ? { kind, error: normalizeFailureError(error) }
        : { kind, sessionId, error: normalizeFailureError(error) }
    // Normalized records are capped by construction, so one failure can
    // never exceed the budget the way one event can — no omit path needed.
    plugin.failures.push(failure)
    retainedFailureBytes += failureRetainedBytes(failure)
    while (plugin.failures.length > failureLimit || retainedFailureBytes > maxFailureBytes) {
      const evicted = plugin.failures.shift()
      if (evicted === undefined) break
      retainedFailureBytes -= failureRetainedBytes(evicted)
      plugin.droppedFailures += 1
    }
  }

  /**
   * Release an ended session's retained payload bodies. Identity (kind,
   * session, timestamp) stays so the boundary record remains queryable, but
   * the content strings — the megabytes — are freed. The ledger deducts only
   * the released before-minus-after difference: the surviving identity still
   * retains bytes and stays counted, so repeated session ends can neither
   * drive the ledger negative nor admit content past the configured bound.
   * This touches only the diagnostic copies: the transport's queued
   * originals are separate objects and still carry full content for a
   * pending offline delivery.
   */
  const releaseSessionPayloads = (sessionId: string): void => {
    for (const retained of plugin.events) {
      if (retained.sessionId !== sessionId) continue
      const before = eventRetainedBytes(retained)
      delete retained.systemPrompt
      delete retained.toolsText
      delete retained.skills
      retainedEventBytes -= before - eventRetainedBytes(retained)
    }
    if (retainedEventBytes < 0) retainedEventBytes = 0
    const kept = plugin.failures.filter((failure) => failure.sessionId !== sessionId)
    if (kept.length !== plugin.failures.length) {
      plugin.failures = kept
      retainedFailureBytes = kept.reduce((sum, failure) => sum + failureRetainedBytes(failure), 0)
    }
  }

  const deliver = async (kind: OpencodeHookKind, payload: OpencodeHookPayload): Promise<void> => {
    const event: OpencodeHookEvent = { kind, ...payload, timestamp: now() }
    recordEvent(event)
    try {
      await withTimeout(() => emit(event), timeoutMs)
    } catch (error) {
      recordFailure(kind, payload.sessionId, error)
      try {
        onError?.({ kind, error })
      } catch {
        // Swallow — a diagnostics tap must never fail the host either.
      }
    }
    if (kind === 'session.end') releaseSessionPayloads(payload.sessionId)
  }

  const hook =
    (kind: OpencodeHookKind): OpencodeHookHandler =>
    (payload) =>
      deliver(kind, payload)

  plugin.hooks = {
    'session.start': hook('session.start'),
    'turn.start': hook('turn.start'),
    'span.start': hook('span.start'),
    'span.end': hook('span.end'),
    'turn.end': hook('turn.end'),
    'session.end': hook('session.end'),
  }
  plugin.context = hook('session.context')
  plugin.toolHooks = {
    'tool.execute.before': hook('tool.execute.before'),
    'tool.execute.after': hook('tool.execute.after'),
  }
  return plugin
}

/**
 * Attach one plugin instance to a host. Desktop receives the full set
 * through `hooks()`; CLI v2 receives the same identities on its namespaced
 * `session` (session/turn) and `tool` (span) surfaces. Registration itself
 * is best-effort: a host that rejects registration keeps running.
 */
export function registerOpencodePlugin(
  host: OpencodePluginHost,
  plugin: OpencodePlugin = createOpencodePlugin(),
): OpencodePlugin {
  try {
    host.hooks?.({ ...plugin.hooks, ...plugin.toolHooks, 'session.context': plugin.context })
  } catch {
    // Swallow — a host that rejects hook registration must not take the session down.
  }
  try {
    host.session = {
      ...host.session,
      'session.start': plugin.hooks['session.start'],
      'session.end': plugin.hooks['session.end'],
      'turn.start': plugin.hooks['turn.start'],
      'turn.end': plugin.hooks['turn.end'],
      // v1 dotted kind + v2 namespaced `ctx.session.hook("context")` per the T0 pin
      // (fixtures/context-payload.json @opencode/plugin 2.1.4); same identity.
      'session.context': plugin.context,
      context: plugin.context,
    }
    host.tool = {
      ...host.tool,
      'span.start': plugin.hooks['span.start'],
      'span.end': plugin.hooks['span.end'],
      // B1: v1 dotted kinds + v2 namespaced `tool.hook("execute.*")` per
      // fixtures/hook-catalog.json; same identities on both targets.
      'tool.execute.before': plugin.toolHooks['tool.execute.before'],
      'tool.execute.after': plugin.toolHooks['tool.execute.after'],
      'execute.before': plugin.toolHooks['tool.execute.before'],
      'execute.after': plugin.toolHooks['tool.execute.after'],
    }
  } catch {
    // Swallow — a frozen host surface is not the session's problem.
  }
  return plugin
}

/**
 * Transport options for the installed pipeline. The endpoint defaults to the
 * live loopback OTLP receiver (Q2a); `spool` selects the bounded outbox
 * (default, drop-oldest backpressure) or direct sender delivery.
 */
export type InstalledOpencodePluginTransport = {
  /** OTLP/HTTP trace export endpoint. Defaults to the live loopback receiver. */
  endpoint?: string
  /** Injectable fetch for tests; defaults to the global fetch. */
  fetchFn?: OtlpFetch
  /**
   * True (default) spools hook events through the bounded T5 outbox; false
   * sends each hook straight through the T5 sender. The direct path still
   * never fails the host, but it loses data while the receiver is down.
   */
  spool?: boolean
  /** Maximum spooled events; beyond this the oldest is dropped. */
  capacity?: number
  /** Maximum events per POST; halved on 429 down to 1. */
  batchSize?: number
  /** First backoff delay after a failed attempt. */
  baseBackoffMs?: number
  /** Backoff ceiling. */
  maxBackoffMs?: number
  /**
   * Abort deadline per transport POST, shared by the outbox and the direct
   * sender (F5). Defaults to 10s.
   */
  requestTimeoutMs?: number
  /** Injectable clock for tests; defaults to a real timer. */
  sleepFn?: (ms: number) => Promise<void>
  /** Injectable entropy for tests; defaults to `Math.random`. */
  randomFn?: () => number
  /**
   * Maximum recorded mapper/turn evidence entries each; beyond it the oldest
   * is dropped. The transport is the delivery record — evidence is a
   * debugging tail — so a long session keeps the freshest boundaries instead
   * of growing host memory without bound. Defaults to 500.
   */
  evidenceLimit?: number
}

export type InstalledOpencodePluginOptions = Omit<OpencodePluginOptions, 'emit'> &
  InstalledOpencodePluginTransport

/**
 * The installed production pipeline: the lifecycle plugin plus the transport
 * it delivers through and the per-hook mapping evidence it recorded. Evidence
 * is bounded (drop-oldest at `evidenceLimit`): a long session must not grow
 * host memory without bound, and the transport — not this tail — is the
 * delivery record.
 */
export type InstalledOpencodePlugin = {
  plugin: OpencodePlugin
  outbox: Outbox
  sender: OtlpSender
  /** Freshest envelope spans, in firing order (mapper stage), at most `evidenceLimit`. */
  mappedSpans: Array<ReturnType<typeof mapOpencodePayloadToOtlpSpan>>
  /** Freshest `tool_result` logs, in firing order (turn stage), at most `evidenceLimit`. */
  outcomeLogs: Array<ReturnType<typeof mapOpencodeTurnToOtlpLog>>
}

/** Default evidence cap: mirrors the outbox scale so one bound covers a long session. */
const DEFAULT_EVIDENCE_LIMIT = 500

function nonEmptyEventText(value: unknown): value is string {
  return typeof value === 'string' && value !== ''
}

function finiteEventNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

/**
 * Stamp a hook event onto a mapper-shaped payload. Session, turn, and span
 * identity ride verbatim — never synthesized. The sender anchors every span
 * on the session trace downstream (Q3a); when the hook observed no span, the
 * mapped span takes the hook-anchored identity so the envelope still names
 * the boundary it came from.
 *
 * Double-count guard (B1, decided): the stamper never stamps usage — the
 * hook payload carries no token counters by type, and the mapper additionally
 * drops counters on tool lifecycle spans. Native OTel owns usage; the plugin
 * emits identity, markers, and outcomes only, so a tool observed by both
 * paths is counted once.
 */
function stampMapperPayload(event: OpencodeHookEvent): OpencodePluginSpanPayload {
  const atMs = Date.parse(event.timestamp)
  const safeMs = Number.isFinite(atMs) ? atMs : Date.now()
  const nanos = BigInt(safeMs) * 1_000_000n
  return {
    traceId: event.sessionId,
    spanId: event.spanId ?? `${event.sessionId}:${event.kind}:${event.timestamp}`,
    name: `opencode.${event.kind}`,
    kind: 'internal',
    sessionID: event.sessionId,
    ...(event.turnIndex !== undefined ? { turnIndex: event.turnIndex } : {}),
    // T2 context content (T0 SessionContext pin): forwarded only when
    // present, so absent stays absent — never empty-string padding (T4
    // semantics). The mapper drops empties again; the stamper keeps the
    // payload itself free of content keys it was never given.
    ...(event.systemPrompt !== undefined && event.systemPrompt !== ''
      ? { systemPrompt: event.systemPrompt }
      : {}),
    ...(event.toolsText !== undefined && event.toolsText !== ''
      ? { toolsText: event.toolsText }
      : {}),
    ...(event.skills !== undefined && event.skills.length > 0 ? { skills: [...event.skills] } : {}),
    ...(event.model !== undefined && event.model !== '' ? { modelId: event.model } : {}),
    ...(event.provider !== undefined && event.provider !== ''
      ? { model: { providerID: event.provider, ...(event.model !== undefined && event.model !== '' ? { modelID: event.model } : {}) } }
      : {}),
    ...(event.contextLimit !== undefined ? { contextLimit: event.contextLimit } : {}),
    ...(event.agent !== undefined && event.agent !== '' ? { agent: event.agent } : {}),
    // B1 tool identity (T0 `tools` bucket): the mapper forwards a bare
    // `opencode.call.id` and gates only `opencode.tool.call.id` on the tool,
    // so the stamper mirrors it — a call id always rides, and a bare one
    // never fabricates a tool marker.
    ...(nonEmptyEventText(event.tool) ? { tool: event.tool } : {}),
    ...(nonEmptyEventText(event.callId) ? { callID: event.callId } : {}),
    ...(nonEmptyEventText(event.messageId) ? { messageID: event.messageId } : {}),
    startTimeUnixNano: nanos,
    endTimeUnixNano: nanos,
    hook: event.kind,
  }
}

/**
 * Stamp a hook event onto a turn outcome payload. Lifecycle and context
 * boundaries observed no result, so their outcome fields stay unset and the
 * log carries only the correlation identity under the `com.opencode`
 * `tool_result` vocabulary. A `tool.execute.after` event forwards the
 * observed outcome — name, success, duration, size — each only when observed,
 * never defaulted.
 *
 * The trace is the session trace (Q3a anchoring, the same trace the mapped
 * spans ride), so a log whose span was observed joins it exactly; with no
 * observed span the log names the trace but no span and waits for it
 * (pending) rather than joining the wrong record.
 */
function stampTurnOutcome(event: OpencodeHookEvent): OpencodeTurnOutcomePayload {
  return {
    traceId: event.sessionId,
    spanId: event.spanId ?? null,
    sessionID: event.sessionId,
    ...(event.turnIndex !== undefined ? { turnIndex: event.turnIndex } : {}),
    ...(nonEmptyEventText(event.tool) ? { tool: event.tool } : {}),
    ...(nonEmptyEventText(event.callId) ? { callID: event.callId } : {}),
    ...(nonEmptyEventText(event.messageId) ? { messageID: event.messageId } : {}),
    ...(nonEmptyEventText(event.agent) ? { agent: event.agent } : {}),
    ...(typeof event.success === 'boolean' ? { success: event.success } : {}),
    ...(finiteEventNumber(event.durationMs) ? { durationMs: event.durationMs } : {}),
    ...(finiteEventNumber(event.resultSizeBytes) ? { resultSizeBytes: event.resultSizeBytes } : {}),
    hook: event.kind,
    timestamp: event.timestamp,
  }
}

/**
 * Assemble the installed production pipeline: hook→mapper→turn→outbox→sender.
 * Every hook boundary maps through the T3 mapper and the T4 turn onto the
 * recorded evidence, then delivers through the T5 transport — the bounded
 * outbox by default, the sender directly when `spool` is false. Either way
 * the skeleton contract holds: delivery is best-effort, failures are
 * swallowed and recorded, the session always continues, and one codebase
 * serves both Desktop and CLI v2 targets.
 */
export function createInstalledOpencodePlugin(
  options: InstalledOpencodePluginOptions = {},
): InstalledOpencodePlugin {
  const {
    endpoint = DEFAULT_OTLP_ENDPOINT,
    fetchFn = (url, init) => fetch(url, init),
    spool = true,
    capacity,
    batchSize,
    baseBackoffMs,
    maxBackoffMs,
    requestTimeoutMs,
    sleepFn,
    randomFn,
    evidenceLimit: evidenceLimitOption,
    ...pluginOptions
  } = options

  const outbox = createOutbox({
    endpoint,
    fetchFn,
    capacity,
    batchSize,
    baseBackoffMs,
    maxBackoffMs,
    requestTimeoutMs,
    sleepFn,
    randomFn,
  })
  const sender = createOtlpSender({ endpoint, fetchFn, timeoutMs: requestTimeoutMs })

  const evidenceLimit = Math.max(1, Math.floor(evidenceLimitOption ?? DEFAULT_EVIDENCE_LIMIT))

  const mappedSpans: InstalledOpencodePlugin['mappedSpans'] = []
  const outcomeLogs: InstalledOpencodePlugin['outcomeLogs'] = []

  const recordEvidence = (event: OpencodeHookEvent): void => {
    if (mappedSpans.length >= evidenceLimit) mappedSpans.shift()
    mappedSpans.push(mapOpencodePayloadToOtlpSpan(stampMapperPayload(event)))
    if (outcomeLogs.length >= evidenceLimit) outcomeLogs.shift()
    outcomeLogs.push(mapOpencodeTurnToOtlpLog(stampTurnOutcome(event)))
  }

  const emit: OpencodePluginEmitter = spool
    ? (event) => {
        recordEvidence(event)
        outbox.enqueue(event)
      }
    : async (event) => {
        recordEvidence(event)
        await sender.send([event])
      }

  const plugin = createOpencodePlugin({ ...pluginOptions, emit })
  return { plugin, outbox, sender, mappedSpans, outcomeLogs }
}

export type OpencodePluginPaths = {
  /** `~/.config/opencode/plugins/` — v1 global dir and v2 local discovery. */
  globalPluginDir: string
  /** `.opencode/plugins/` — v1 project dir and v2 local discovery. */
  projectPluginDir: string
}

/**
 * Where the dash CLI installs this plugin file. Derived from the T0
 * `install-targets.json` survey; resolution takes HOME at call time so
 * tests run under a temporary HOME.
 */
export function resolveOpencodePluginPaths(home: string): OpencodePluginPaths {
  const root = home.replace(/\/+$/, '')
  return {
    globalPluginDir: `${root}/.config/opencode/plugins`,
    projectPluginDir: '.opencode/plugins',
  }
}

/** Run `work`, rejecting if it does not settle within `ms`. Sync throws become rejections. */
function withTimeout(work: () => void | Promise<void>, ms: number): Promise<void> {
  let timer: ReturnType<typeof setTimeout> | undefined
  const guarded = (async (): Promise<void> => {
    await work()
  })()
  const bounded = new Promise<void>((_, reject) => {
    timer = setTimeout(() => {
      reject(new Error(`opencode hook delivery timed out after ${ms}ms`))
    }, ms)
  })
  return Promise.race([guarded, bounded]).finally(() => {
    if (timer !== undefined) clearTimeout(timer)
  })
}
