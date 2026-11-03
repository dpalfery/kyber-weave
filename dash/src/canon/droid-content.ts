// The Factory (Droid) content gate.
//
// Factory reaches KyberDash through two routes, and BOTH of them can carry the owner's
// text:
//
//   * the Kyber-owned plugin publishes the owner's prompt text under
//     `factory.turn.prompt.text`. It gates that text at EMISSION — the plugin reads
//     the consent file when it maps the span — but the emission gate is not the last
//     gate: a span the plugin PARKED because the delivery failed is re-delivered
//     later WITHOUT the consent being read again. The gate therefore exists here,
//     at the canon seam, precisely because the plugin's envelope carries content;
//   * Factory's NATIVE OpenTelemetry export carries the prompt, the reply, the tool
//     arguments and the tool result as span attributes whenever the harness's own
//     content switch is on.
//
// The plugin's TOOL attributes stay off the content list on purpose: the plugin
// publishes tool arguments and results as BYTE COUNTS, never as content, so there is
// nothing there to strip.
//
// This module holds three things: the owner's consent decision, what a Factory span
// looks like, and the bounded memory of the Factory traces that have been seen.
//
// LEAF: node: built-ins only, no project import. The gate is read at the canon seam,
// and a leaf keeps that seam free of the engine's own dependencies — the same rule the
// capture-side consent reader follows.

import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

/** File name inside `~/.kyberdash/`. */
export const FACTORY_CONSENT_FILE_NAME = 'factory-plugin.json'

/** Optional override: `0` forces content off, `1` forces it on; anything else defers to the file. */
export const KYBERDASH_FACTORY_CONTEXT_ENV = 'KYBERDASH_FACTORY_CONTEXT'

/** The consent file is a few dozen bytes; anything larger is not ours and reads as off. */
const MAX_CONSENT_BYTES = 4096

/** `<home>/.kyberdash/factory-plugin.json`; `home` is injectable for tests. */
export function resolveConsentPath(home: string = homedir()): string {
  return join(home, '.kyberdash', FACTORY_CONSENT_FILE_NAME)
}

/**
 * True only for a readable file holding `{ version: 1, content: true }`.
 * Missing, unreadable, oversized, malformed, or unknown-version all mean off.
 */
export function readContentConsent(
  path: string,
  read: (path: string) => string = (p) => readFileSync(p, 'utf8'),
): boolean {
  try {
    const text = read(path)
    if (Buffer.byteLength(text, 'utf8') > MAX_CONSENT_BYTES) return false
    const parsed: unknown = JSON.parse(text)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return false
    const record = parsed as Record<string, unknown>
    return record['version'] === 1 && record['content'] === true
  } catch {
    // Any failure to read or parse is "off": the reader never throws at its callers.
    return false
  }
}

/** Consent for one batch: the env override first, then the file. */
export function resolveContentConsent(env: NodeJS.ProcessEnv, consentPath: string): boolean {
  const override = env[KYBERDASH_FACTORY_CONTEXT_ENV]
  if (override === '1') return true
  if (override === '0') return false
  return readContentConsent(consentPath)
}

// ---------------------------------------------------------------------------
// What a Factory span looks like
// ---------------------------------------------------------------------------

/** The attribute Factory's native export declares the agent under. */
export const FACTORY_AGENT_NAME_ATTRIBUTE = 'gen_ai.agent.name'

/** The agent name Factory's own native export declares. */
export const FACTORY_AGENT_NAME = 'droid'

/**
 * The prefix of Factory's own `legacy`-format span names.
 *
 * The `legacy` format's content spans (`droid.message.user`, `droid.message.assistant`,
 * `droid.tool.call`, `droid.tool.result`) carry no attribute fingerprint at all, so the
 * span name is the only thing that identifies them. It is not used to CLAIM a span for a
 * harness — the plugin envelope is the only Factory span this branch claims — it is used
 * to recognise Factory's own content and keep it out of the store.
 */
export const FACTORY_LEGACY_SPAN_PREFIX = 'droid.'

/**
 * The content-bearing attributes the gate strips.
 *
 * These are the attributes that carry the owner's text: the prompt and the reply
 * (`gen_ai.input.messages`, `gen_ai.output.messages`), the tool arguments and the tool
 * result (`gen_ai.tool.call.arguments`, `gen_ai.tool.call.result`), the `legacy`
 * format's own message text (`message.content`), and the plugin envelope's own prompt
 * text (`factory.turn.prompt.text`). Everything else is a measurement — the session,
 * the conversation, the model, the counters — and survives the gate.
 *
 * The plugin's TOOL attributes are not on the list: the plugin publishes tool
 * arguments and results as BYTE COUNTS, never as content, so there is nothing there
 * to strip.
 */
export const FACTORY_CONTENT_ATTRIBUTES = [
  'gen_ai.input.messages',
  'gen_ai.output.messages',
  'gen_ai.tool.call.arguments',
  'gen_ai.tool.call.result',
  'message.content',
  'factory.turn.prompt.text',
] as const

/** True for one of Factory's own `legacy`-format span names. */
export function isFactoryLegacySpanName(name: string): boolean {
  return name.startsWith(FACTORY_LEGACY_SPAN_PREFIX)
}

/**
 * True for the span that names the agent: the `genai` format's trace ROOT, and the
 * only span of a native trace that carries Factory's identity.
 */
export function isFactoryNativeRoot(attributes: Record<string, unknown>): boolean {
  return attributes[FACTORY_AGENT_NAME_ATTRIBUTE] === FACTORY_AGENT_NAME
}

/** True when the span carries any of the content-bearing attributes. */
export function carriesFactoryContent(attributes: Record<string, unknown>): boolean {
  return FACTORY_CONTENT_ATTRIBUTES.some((key) => key in attributes)
}

/**
 * The attributes without their content. The original map is returned when it carries
 * no content at all, so the common path allocates nothing.
 */
export function stripFactoryContent(attributes: Record<string, unknown>): Record<string, unknown> {
  let stripped: Record<string, unknown> | undefined
  for (const key of FACTORY_CONTENT_ATTRIBUTES) {
    if (!(key in attributes)) continue
    if (stripped === undefined) stripped = { ...attributes }
    delete stripped[key]
  }
  return stripped ?? attributes
}

// ---------------------------------------------------------------------------
// The bounded memory of Factory traces
// ---------------------------------------------------------------------------

/**
 * How many Factory traces are remembered at once.
 *
 * The memory is what carries the identification across a batch boundary, so it is
 * generous: a forgotten trace is a trace whose children are no longer recognised as
 * Factory's.
 */
export const FACTORY_TRACE_MEMORY_LIMIT = 1024

/**
 * The bounded, least-recently-used memory of the Factory traces that have been seen.
 *
 * The `genai` format names the agent on the trace root alone, so a child span carries
 * no identity of its own: the trace IS the child's identity. A child can arrive in a
 * later batch than its root — the root is exported when the invocation ends, and a
 * child that ends earlier is exported earlier — so the identification has to survive
 * the batch boundary. This memory is where it survives, and it is bounded by COUNT:
 * the least recently used trace is forgotten when the bound is reached.
 */
export class FactoryTraceMemory {
  /** How many traces this memory keeps. */
  readonly limit: number

  /** The remembered traces, oldest first: a Map keeps insertion order. */
  private readonly traces = new Map<string, true>()

  constructor(limit: number = FACTORY_TRACE_MEMORY_LIMIT) {
    this.limit = limit
  }

  /** How many traces are remembered right now. */
  get size(): number {
    return this.traces.size
  }

  /** Remember a trace as Factory's own, forgetting the oldest one at the bound. */
  remember(traceId: string): void {
    if (traceId === '') return
    // Re-inserting moves the trace to the most-recent end of the map.
    this.traces.delete(traceId)
    this.traces.set(traceId, true)
    while (this.traces.size > this.limit) {
      const oldest = this.traces.keys().next()
      if (oldest.done === true) break
      this.traces.delete(oldest.value)
    }
  }

  /** Whether the trace is remembered, and it counts as the most recently used one. */
  has(traceId: string): boolean {
    if (!this.traces.has(traceId)) return false
    this.traces.delete(traceId)
    this.traces.set(traceId, true)
    return true
  }

  /** Forget every trace. */
  clear(): void {
    this.traces.clear()
  }
}
