// Opencode loadable entry (T3): the module opencode itself loads.
//
// One codebase serves both targets from the dash-CLI-managed home
// `dash/src/capture/harnesses/opencode-plugin/`. The T0 pin
// (`fixtures/context-payload.json` @opencode/plugin 2.1.4) fixes the two
// loadable shapes this module exports:
//
// - v2 CLI: `Plugin.define({ id, setup })` where `setup` registers
//   `ctx.session.hook("context", …)`.
// - v1 Desktop: a default-export hooks map (dotted kinds).
//
// Both shapes bridge to the same pipeline — `createInstalledOpencodePlugin`
// + `registerOpencodePlugin` (`plugin.ts`) — so lifecycle behavior is
// unchanged. Every hook is best-effort and never fails the host.
//
// D5 consent gate: system prompt, tool definitions, and the skills listing
// are content and ride only under the sender-owned env consent
// `KYBERDASH_OPENCODE_CONTEXT=1` (off by default). With consent absent the
// context event still emits identity/markers; the content fields are dropped
// pre-emit. Capture never writes or receipts this key — the harness writer
// (`../opencode.ts`) reports it status-only, the
// `OTEL_EXPORTER_OTLP_ENDPOINT` pattern.

import {
  OPENCODE_PLUGIN_ID,
  createInstalledOpencodePlugin,
  registerOpencodePlugin,
  type InstalledOpencodePlugin,
  type InstalledOpencodePluginOptions,
  type OpencodeHookEvent,
  type OpencodeHookKind,
  type OpencodeHookPayload,
} from './plugin.js'

/** D5 sender-owned consent key. Exact name finalized in T3, pinned here and in docs. */
export const KYBERDASH_OPENCODE_CONTEXT_ENV = 'KYBERDASH_OPENCODE_CONTEXT'

/** True only when the consent key is exactly `1`. Anything else (unset, empty, other) is off. */
export function readContextConsent(env: NodeJS.ProcessEnv = process.env): boolean {
  return env[KYBERDASH_OPENCODE_CONTEXT_ENV] === '1'
}

/** Strip the D5 content fields from a hook payload; identity/markers ride verbatim. */
export function stripContextContent(payload: OpencodeHookPayload): OpencodeHookPayload {
  const { systemPrompt: _droppedSystem, toolsText: _droppedTools, skills: _droppedSkills, ...rest } =
    payload
  return rest
}

/**
 * Apply the D5 gate to one hook event. Content fields (`systemPrompt`,
 * `toolsText`, `skills`) are dropped when consent is absent; identity and
 * markers (`sessionId`, `messageId`, `turnIndex`, `spanId`, `model`,
 * `agent`, `kind`, `timestamp`) always flow.
 */
export function applyContextConsent(event: OpencodeHookEvent, consent: boolean): OpencodeHookEvent {
  if (consent) return event
  return { ...stripContextContent(event), kind: event.kind, timestamp: event.timestamp }
}

export type EntryHandlersOptions = InstalledOpencodePluginOptions & {
  /** Env mapping the consent key is read from (default: `process.env`); injected in tests. */
  env?: NodeJS.ProcessEnv
}

export type EntryHandlers = Record<
  OpencodeHookKind,
  (payload: OpencodeHookPayload) => Promise<void>
> & {
  installed: InstalledOpencodePlugin
}

/**
 * Build the hook handlers behind the loadable shapes. Each handler applies
 * the D5 gate (read per event, so a consent change takes effect without
 * reinstalling the plugin) then delivers through the installed pipeline's
 * skeleton plugin — bounded outbox, never-fail-host — unchanged.
 */
export function createEntryHandlers(options: EntryHandlersOptions = {}): EntryHandlers {
  const { env = process.env, ...installedOptions } = options
  const installed = createInstalledOpencodePlugin(installedOptions)

  const gate = (payload: OpencodeHookPayload): OpencodeHookPayload =>
    readContextConsent(env) ? payload : stripContextContent(payload)

  const handlers = {
    'session.start': (payload: OpencodeHookPayload) =>
      installed.plugin.hooks['session.start'](gate(payload)),
    'turn.start': (payload: OpencodeHookPayload) =>
      installed.plugin.hooks['turn.start'](gate(payload)),
    'span.start': (payload: OpencodeHookPayload) =>
      installed.plugin.hooks['span.start'](gate(payload)),
    'span.end': (payload: OpencodeHookPayload) =>
      installed.plugin.hooks['span.end'](gate(payload)),
    'turn.end': (payload: OpencodeHookPayload) =>
      installed.plugin.hooks['turn.end'](gate(payload)),
    'session.end': (payload: OpencodeHookPayload) =>
      installed.plugin.hooks['session.end'](gate(payload)),
    'session.context': (payload: OpencodeHookPayload) => {
      // Round-2 review (PR #425): a session.context without a session is
      // unattributable — it would merge under trace:undefined with
      // {stringValue: undefined} on the wire. Drop it at the entry
      // boundary instead of emitting (no drop counter exists on this
      // path, so the drop is a silent return; never fails the host).
      const gated = gate(payload)
      if (typeof gated.sessionId !== 'string' || gated.sessionId === '') return Promise.resolve()
      return installed.plugin.context(gated)
    },
    installed,
  }
  return handlers
}

/** Minimal v2 context passed to `setup` (the `Plugin.define` `Context` surface, session domain). */
export type OpencodePluginSetupContext = {
  session?: {
    hook?: (name: string, callback: (input: never) => void | Promise<void>) => Promise<unknown>
  }
  hooks?: (registered: Record<string, (payload: OpencodeHookPayload) => Promise<void>>) => void
}

/**
 * Map a raw v2 `SessionContext` input onto the hook payload. The v2 input
 * carries `sessionID`/`model`/`agent` identity (T0 pin); payload-shaped
 * inputs pass through verbatim.
 */
export function toHookPayload(input: Record<string, unknown>): OpencodeHookPayload {
  const { sessionID, ...rest } = input as Record<string, unknown> & { sessionID?: unknown }
  const rawId = (rest as { sessionId?: unknown }).sessionId
  const sessionId =
    typeof rawId === 'string' && rawId !== ''
      ? rawId
      : typeof sessionID === 'string' && sessionID !== ''
        ? sessionID
        : undefined
  const { sessionId: _dropped, ...withoutId } = rest as Record<string, unknown>
  return (sessionId === undefined ? { ...withoutId } : { ...withoutId, sessionId }) as OpencodeHookPayload
}

/**
 * v2 `Plugin.define({ id, setup })` shape (T0 pin:
 * `package/dist/promise/plugin.d.ts`). `setup` registers
 * `ctx.session.hook("context", …)` on a v2 host and bridges v1 dotted hosts
 * through `registerOpencodePlugin`. Registration itself is best-effort: a
 * host that rejects it keeps running.
 */
export const KyberdashOpencodePlugin = {
  id: OPENCODE_PLUGIN_ID,
  setup: async (ctx: OpencodePluginSetupContext): Promise<void> => {
    try {
      const handlers = createEntryHandlers()
      const { installed: _installed, ...hooks } = handlers
      try {
        await ctx.session?.hook?.('context', (input) =>
          handlers['session.context'](toHookPayload(input as unknown as Record<string, unknown>)),
        )
      } catch {
        // Swallow — a host that rejects context registration must not take the session down.
      }
      try {
        if (typeof ctx.hooks === 'function') {
          registerOpencodePlugin(
            ctx as unknown as Parameters<typeof registerOpencodePlugin>[0],
            handlers.installed.plugin,
          )
        }
        void hooks
      } catch {
        // Swallow — same never-fail-host contract as registerOpencodePlugin.
      }
    } catch {
      // Swallow — setup must never fail the host.
    }
  },
}

/** Lazily-created shared handlers behind the v1 default-export map (no pipeline at import time). */
let sharedHandlers: EntryHandlers | undefined

function shared(): EntryHandlers {
  sharedHandlers ??= createEntryHandlers()
  return sharedHandlers
}

/**
 * v1 default-export hooks map (Desktop dotted-hook surface). Handlers are
 * the same gated identities as the v2 shape; the pipeline starts on first
 * hook fire, not at import.
 */
const entryHooksMap: Record<OpencodeHookKind, (payload: OpencodeHookPayload) => Promise<void>> = {
  'session.start': (payload) => shared()['session.start'](payload),
  'turn.start': (payload) => shared()['turn.start'](payload),
  'span.start': (payload) => shared()['span.start'](payload),
  'span.end': (payload) => shared()['span.end'](payload),
  'turn.end': (payload) => shared()['turn.end'](payload),
  'session.end': (payload) => shared()['session.end'](payload),
  'session.context': (payload) => shared()['session.context'](payload),
}

export default entryHooksMap
