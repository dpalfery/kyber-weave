// OpenCode loadable entry (plan T2, R1/D1/D2): the module OpenCode 2.1.4 loads.
//
// The 2.1.4 loader reads only `.default` and requires `{ id, effect }` or
// `{ id, setup }` (`fixtures/plugin-module-1x.json`, `loader_default_vs_named_exports`).
// This module therefore default-exports exactly `{ id, setup }`. `setup`
// registers one hook, `ctx.session.hook("context", cb)`, and `cb` only READS
// the request the host hands it (`sessionID`, `model`, `agent`, `system`,
// `tools`; system/tools/messages are mutable in 2.1.4, so reading is a
// promise) and maps it to one `session.context` event.
//
// Consent (Q1/Q2, plan §3.4): the system prompt, tool definitions and skills
// listing are content. They ride only when `~/.kyberdash/opencode-plugin.json`
// says `content: true` (read per event, so a Settings change applies without a
// reinstall); `KYBERDASH_OPENCODE_CONTEXT` is an override only (`0` off, `1`
// on). Identity and the context window always flow.
//
// Never fails the host: every step is best-effort and swallowed. Nothing runs
// at import; the pipeline (outbox, sender) is built inside `setup`.

import { resolveContentConsent, resolveConsentPath } from './consent.js'
import {
  OPENCODE_PLUGIN_ID,
  createInstalledOpencodePlugin,
  detachedCopy,
  type InstalledOpencodePlugin,
  type InstalledOpencodePluginOptions,
  type OpencodeHookPayload,
  type OpencodeToolHookKind,
} from './plugin.js'
import { parseAvailableSkills } from './skills-block.js'

/** Bound on the one-time model catalog lookup; expiry means "no window", not a stalled request. */
const DEFAULT_LOOKUP_TIMEOUT_MS = 500

/**
 * Cap on each content field (system prompt, tools JSON) sent per event, in characters (UTF-16
 * units, at most 3 bytes each in UTF-8, so under 800 KB on the wire). A host prompt or tool
 * catalogue can be megabytes; the hook runs on the request path and has a 250ms budget, so
 * content past the cap is cut and ends with `CONTENT_TRUNCATION_MARKER`, never sent whole.
 */
export const MAX_CONTENT_CHARS = 262_144

/** Appended to a field that hit the cap, so a reader can tell it was cut. */
export const CONTENT_TRUNCATION_MARKER = '\n[kyberdash: truncated]'

function capContent(value: string): string {
  // Always detach, even when already under the cap: the input can be a short
  // slice holding a huge parent alive, and the tail and the outbox both
  // retain what this returns. (Untagged template results already flatten
  // today, but that is engine behavior, not a contract.)
  if (value.length <= MAX_CONTENT_CHARS) return detachedCopy(value)
  return `${detachedCopy(value.slice(0, MAX_CONTENT_CHARS))}${CONTENT_TRUNCATION_MARKER}`
}

export type EntryOptions = Omit<InstalledOpencodePluginOptions, 'emit'> & {
  /** A prebuilt pipeline (tests observe its events); default: built inside `setup`. */
  installed?: InstalledOpencodePlugin
  /** Env the consent override is read from; default `process.env` at call time. */
  env?: NodeJS.ProcessEnv
  /** Consent file path; default `~/.kyberdash/opencode-plugin.json`. */
  consentPath?: string
  /** Bound on the catalog lookup (ms). */
  lookupTimeoutMs?: number
}

/** The slice of the 2.1.4 setup `Context` this plugin touches. */
export type OpencodePluginSetupContext = {
  session?: {
    hook?: (name: string, callback: (input: never) => void | Promise<void>) => Promise<unknown>
  }
  /**
   * B1 tool domain. The v2 namespaced catalog (fixtures/hook-catalog.json)
   * lists `execute.before`/`execute.after` under `tool`; registration mirrors
   * the pinned `session.hook("context", …)` shape with the bare in-domain
   * name. Optional throughout — a host without it keeps context-only capture.
   */
  tool?: {
    hook?: (name: string, callback: (input: never) => void | Promise<void>) => Promise<unknown>
  }
  model?: {
    list?: () => unknown
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}

/** Reject if `work` does not settle in `ms`; the timer is always cleared. */
function bounded<T>(work: Promise<T>, ms: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined
  const expiry = new Promise<never>((_, reject) => {
    timer = setTimeout(() => {
      reject(new Error(`catalog lookup timed out after ${ms}ms`))
    }, ms)
  })
  return Promise.race([work, expiry]).finally(() => {
    if (timer !== undefined) clearTimeout(timer)
  })
}

/** Catalog rows from a `model.list()` result: `{ data: [...] }` (2.1.4) or a bare array. */
function catalogRows(result: unknown): readonly unknown[] {
  if (Array.isArray(result)) return result as unknown[]
  if (isRecord(result) && Array.isArray(result['data'])) return result['data'] as unknown[]
  return []
}

type ContextLimitLookup = (providerID: string | undefined, modelID: string) => Promise<number | undefined>

/**
 * G6 (pinned in the fixture): the setup context exposes `ctx.model.list()`
 * whose rows carry `limit.context`. The catalog is read once per setup and
 * kept; a failure or timeout is sticky, so a broken catalog costs one bounded
 * wait, not one per request. No catalog, no match, or an unusable value all
 * mean "no window" (undefined), never 0.
 */
function createContextLimitLookup(
  model: OpencodePluginSetupContext['model'],
  timeoutMs: number,
): ContextLimitLookup {
  const list = model?.list
  if (typeof list !== 'function') return () => Promise.resolve(undefined)
  let rows: Promise<readonly unknown[]> | undefined
  const load = (): Promise<readonly unknown[]> => {
    rows ??= bounded(Promise.resolve().then(() => list.call(model)), timeoutMs).then(
      catalogRows,
      () => [],
    )
    return rows
  }
  return async (providerID, modelID) => {
    const match = (await load()).find(
      (row) => isRecord(row) && row['id'] === modelID && (providerID === undefined || row['providerID'] === providerID),
    )
    const limit = isRecord(match) && isRecord(match['limit']) ? match['limit']['context'] : undefined
    return typeof limit === 'number' && Number.isFinite(limit) && limit > 0 ? limit : undefined
  }
}

/** Joined `system[n].text`; undefined when the host sent none. Reads only. */
function systemText(system: unknown): string | undefined {
  if (!Array.isArray(system)) return undefined
  const parts = (system as unknown[]).flatMap((part) => (isRecord(part) ? [text(part['text'])] : [])).filter(
    (part): part is string => part !== undefined,
  )
  return parts.length > 0 ? parts.join('\n\n') : undefined
}

/**
 * JSON of the tools record, capped at `MAX_CONTENT_CHARS`; undefined when empty or not
 * serializable. Tools are serialized one at a time and the walk stops once the cap is passed,
 * so a huge catalogue is not stringified whole. Equal to `JSON.stringify(tools)` under the cap.
 * Reads only.
 */
function toolsJson(tools: unknown): string | undefined {
  if (!isRecord(tools)) return undefined
  const keys = Object.keys(tools)
  if (keys.length === 0) return undefined
  try {
    const members: string[] = []
    let size = 2
    let cut = false
    for (const key of keys) {
      const value = JSON.stringify(tools[key])
      if (value === undefined) continue // JSON.stringify omits such members too
      const member = `${JSON.stringify(key)}:${value}`
      members.push(member)
      size += member.length + 1
      if (size > MAX_CONTENT_CHARS) {
        cut = true
        break
      }
    }
    // Past the cap the object is left open on purpose: it is cut and marked, not valid JSON.
    return capContent(`{${members.join(',')}${cut ? '' : '}'}`)
  } catch {
    return undefined
  }
}

/**
 * Map one 2.1.4 `context` input to a hook payload. Returns undefined when the
 * input names no session (an unattributable event is dropped, not emitted).
 */
async function toContextPayload(
  raw: unknown,
  consent: () => boolean,
  contextLimit: ContextLimitLookup,
): Promise<OpencodeHookPayload | undefined> {
  if (!isRecord(raw)) return undefined
  const sessionId = text(raw['sessionID'])
  if (sessionId === undefined) return undefined
  const ref = isRecord(raw['model']) ? raw['model'] : undefined
  const model = text(ref?.['id'])
  const provider = text(ref?.['providerID'])
  const agent = text(raw['agent'])
  const window = model === undefined ? undefined : await contextLimit(provider, model)

  const payload: OpencodeHookPayload = {
    sessionId: detachedCopy(sessionId),
    ...(model !== undefined ? { model: detachedCopy(model) } : {}),
    ...(provider !== undefined ? { provider: detachedCopy(provider) } : {}),
    ...(agent !== undefined ? { agent: detachedCopy(agent) } : {}),
    ...(window !== undefined ? { contextLimit: window } : {}),
  }
  if (!consent()) return payload

  const systemPrompt = systemText(raw['system'])
  const toolsText = toolsJson(raw['tools'])
  const skills = systemPrompt === undefined ? undefined : parseAvailableSkills(systemPrompt)
  return {
    ...payload,
    ...(systemPrompt !== undefined ? { systemPrompt: capContent(systemPrompt) } : {}),
    ...(toolsText !== undefined ? { toolsText } : {}),
    // A missing or malformed block leaves skills absent; the adapter names the reason (T7).
    // Skill ids are regex slices of the prompt: detach them so a retained id
    // cannot hold the whole prompt's backing alive.
    ...(skills?.ok === true && skills.ids.length > 0
      ? { skills: skills.ids.map((id) => detachedCopy(id)) }
      : {}),
  }
}

/** Cap on remembered tool starts; past it the oldest unpaired before is dropped. */
const MAX_PENDING_TOOL_STARTS = 1000

function isToolErrorStatus(status: unknown): boolean {
  if (typeof status === 'string') return status.toLowerCase() === 'error'
  return typeof status === 'number' && Number.isFinite(status) && status !== 0
}

/**
 * A turn index is a non-negative integer. `Number.isFinite` alone admitted
 * `-1` and `1.5`, which then rode `opencode.turn.index` as if they were turn
 * numbers.
 */
function isTurnIndex(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0
}

/**
 * Map one tool lifecycle input to a hook payload. Keys follow the T0
 * skeleton (`fixtures/span-payload-skeleton.json`: `sessionID`, `messageID`,
 * the `tools` bucket's `tool`/`callID`, the `errors` bucket), tolerating the
 * same field's camelCase spelling where the codebase already uses both.
 * Returns undefined when the input names no session (an unattributable event
 * is dropped, not emitted).
 *
 * The before phase only records the start time under the stable call
 * identity. The after phase measures the duration from it (absent when no
 * before was seen — never zero), derives success from the errors bucket
 * (absent error means completed), and sizes the observed output text in
 * bytes without ever carrying the text itself — size is metadata, like the
 * duration, so consent gating (context content only) is untouched.
 */
function toToolPayload(
  raw: unknown,
  phase: OpencodeToolHookKind,
  starts: Map<string, number>,
  nowMs: () => number,
): OpencodeHookPayload | undefined {
  if (!isRecord(raw)) return undefined
  const sessionId = text(raw['sessionID']) ?? text(raw['sessionId'])
  if (sessionId === undefined) return undefined
  const tool = text(raw['tool'])
  const callId = text(raw['callID']) ?? text(raw['callId'])
  const messageId = text(raw['messageID']) ?? text(raw['messageId'])
  const agent = text(raw['agent'])
  const spanId = text(raw['spanId']) ?? text(raw['span_id'])
  const turnIndex = raw['turnIndex']
  const payload: OpencodeHookPayload = {
    sessionId,
    ...(tool !== undefined ? { tool } : {}),
    ...(callId !== undefined ? { callId } : {}),
    ...(messageId !== undefined ? { messageId } : {}),
    ...(agent !== undefined ? { agent } : {}),
    ...(spanId !== undefined ? { spanId } : {}),
    ...(isTurnIndex(turnIndex) ? { turnIndex } : {}),
  }
  const key = callId ?? spanId
  if (phase === 'tool.execute.before') {
    if (key !== undefined) {
      if (starts.size >= MAX_PENDING_TOOL_STARTS) {
        const oldest = starts.keys().next()
        if (!oldest.done) starts.delete(oldest.value)
      }
      starts.set(`${sessionId}::${key}`, nowMs())
    }
    return payload
  }
  if (key !== undefined) {
    const started = starts.get(`${sessionId}::${key}`)
    starts.delete(`${sessionId}::${key}`)
    if (started !== undefined) {
      const elapsed = nowMs() - started
      if (Number.isFinite(elapsed) && elapsed >= 0) payload.durationMs = elapsed
    }
  }
  const explicit = raw['success']
  const errors = isRecord(raw['errors']) ? raw['errors'] : undefined
  const failed =
    errors !== undefined &&
    (isToolErrorStatus(errors['status']) ||
      text(errors['type']) !== undefined ||
      text(errors['message']) !== undefined)
  // Failure wins over the hook's own checkbox: a run that reports both
  // `success: true` and an error bucket failed. Otherwise an explicit
  // boolean is the harness's answer, and its absence falls back to the bucket.
  payload.success = failed ? false : typeof explicit === 'boolean' ? explicit : true
  const output =
    text(raw['output_text']) ?? text(raw['outputText']) ?? text(raw['output']) ?? text(raw['result'])
  if (output !== undefined) payload.resultSizeBytes = new TextEncoder().encode(output).length
  return payload
}

/**
 * Build the 2.1.4 `setup` function. The pipeline, catalog lookup and consent
 * reader are created when `setup` runs, never at import. `setup` always
 * resolves.
 */
export function createSetup(
  options: EntryOptions = {},
): (ctx: OpencodePluginSetupContext) => Promise<void> {
  const { installed: injected, env, consentPath, lookupTimeoutMs, ...pipelineOptions } = options
  const lookupBoundMs =
    typeof lookupTimeoutMs === 'number' && Number.isFinite(lookupTimeoutMs) && lookupTimeoutMs > 0
      ? lookupTimeoutMs
      : DEFAULT_LOOKUP_TIMEOUT_MS
  return async (ctx) => {
    try {
      const session = ctx?.session
      const tool = ctx?.tool
      const hasSession = typeof session?.hook === 'function'
      const hasTool = typeof tool?.hook === 'function'
      if (!hasSession && !hasTool) return
      const installed = injected ?? createInstalledOpencodePlugin(pipelineOptions)
      const path = consentPath ?? resolveConsentPath()
      const lookup = createContextLimitLookup(ctx?.model, lookupBoundMs)
      if (hasSession) {
        const callback = async (input: unknown): Promise<void> => {
          try {
            const payload = await toContextPayload(
              input,
              () => resolveContentConsent(env ?? process.env, path),
              lookup,
            )
            if (payload !== undefined) await installed.plugin.context(payload)
          } catch {
            // Swallow — a context event is never worth failing the request.
          }
        }
        await session?.hook?.('context', callback as (input: never) => Promise<void>)
      }
      if (hasTool) {
        // Paired before/after start times, keyed by the stable call identity.
        // Drop-oldest past the cap so a hook that never pairs cannot grow
        // host memory without bound.
        const starts = new Map<string, number>()
        const before = async (input: unknown): Promise<void> => {
          try {
            const payload = toToolPayload(input, 'tool.execute.before', starts, Date.now)
            if (payload !== undefined) await installed.plugin.toolHooks['tool.execute.before'](payload)
          } catch {
            // Swallow — a tool event is never worth failing the request.
          }
        }
        const after = async (input: unknown): Promise<void> => {
          try {
            const payload = toToolPayload(input, 'tool.execute.after', starts, Date.now)
            if (payload !== undefined) await installed.plugin.toolHooks['tool.execute.after'](payload)
          } catch {
            // Swallow — a tool event is never worth failing the request.
          }
        }
        await tool?.hook?.('execute.before', before as (input: never) => Promise<void>)
        await tool?.hook?.('execute.after', after as (input: never) => Promise<void>)
      }
    } catch {
      // Swallow — setup must never fail the host.
    }
  }
}

/** The only export OpenCode 2.1.4 reads: `{ id, setup }`. */
export default {
  id: OPENCODE_PLUGIN_ID,
  setup: createSetup(),
}
