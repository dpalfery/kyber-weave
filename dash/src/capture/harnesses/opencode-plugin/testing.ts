// Test kit for the OpenCode V2 capture plugin (test-only: never bundled, never imported by the plugin).
//
// The seam under test is `createSetup(options)` in entry.ts. It returns `setup(ctx)`, and `ctx` is the
// fake V2 host built here. The only reach outside the plugin is `fetchFn` (a recording receiver) and
// `consentPath` (a file under a temp directory). Replays read fixtures/v2 verbatim, with the probe's
// `{"$str", "$len"}` envelopes unwrapped to the text they keep.

import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, expect } from 'vitest'

export const FIXTURE_DIR = fileURLToPath(new URL('./fixtures/v2/', import.meta.url))
export const OWNED_DIRECTORY = '/HOME/scratch/oc-probe'
export const OWNED_SESSION_ID = 'ses_e74c398c7ffeVajzhRWZNgfR2n'
export const FOREIGN_SESSION_ID = 'ses_e74c858a4ffevuH3zgPbKTQ0xc'
export const LOOPBACK_TRACES_URL = 'http://127.0.0.1:4318/v1/traces'
export const OWNED_MODEL = { id: 'muse-spark-1.3-contributor', providerID: 'opencode-go', variant: 'default' }

// Captured before any test can install fake timers, so the test kit's own deadlines stay real.
const realSetTimeout = globalThis.setTimeout
const realClearTimeout = globalThis.clearTimeout
const realSetImmediate = globalThis.setImmediate

export type OtlpAttribute = { key: string; value: unknown }
export type OtlpSpan = { name: string; attributes: OtlpAttribute[]; [field: string]: unknown }
export type OtlpBody = {
  resourceSpans: { resource: unknown; scopeSpans: { scope: unknown; spans: OtlpSpan[] }[] }[]
}
export type GoldenVariant = 'consent-on' | 'consent-off'
type Callback = (input: unknown) => unknown

// ---------------------------------------------------------------------------------------------
// Fixtures

/** Probe envelopes keep the first 2000 characters of a long string; the golden is built on that text. */
export function unwrapProbeEnvelopes(value: unknown): unknown {
  if (Array.isArray(value)) return value.map((item) => unwrapProbeEnvelopes(item))
  if (typeof value !== 'object' || value === null) return value
  const record = value as Record<string, unknown>
  if (typeof record['$str'] === 'string' && typeof record['$len'] === 'number') return record['$str']
  return Object.fromEntries(Object.entries(record).map(([key, item]) => [key, unwrapProbeEnvelopes(item)]))
}

function fixtureText(name: string): string {
  return readFileSync(join(FIXTURE_DIR, name), 'utf8')
}

export type ProbeLine = { kind: string; payload: unknown }

/**
 * runA.events.jsonl in file order. `event:<type>` lines are bus events; `session.hook:*` and
 * `tool.hook:*` lines are hook inputs. The 22 foreign-session lines are included on purpose.
 */
export function readRunALines(): ProbeLine[] {
  return fixtureText('runA.events.jsonl')
    .split('\n')
    .filter((line) => line.trim() !== '')
    .map((line) => {
      const parsed = JSON.parse(line) as { kind: string; payload?: unknown }
      return { kind: parsed.kind, payload: unwrapProbeEnvelopes(parsed.payload) }
    })
}

/**
 * The three `session.hook('context')` inputs, in step order, as the host hands them over. The
 * fixture's `assistantMessageID` is deliberately left out: a hook input does not carry it, and the
 * plugin must pair each context with its step from the event stream.
 */
export function readRunAContextInputs(): Record<string, unknown>[] {
  const captured = unwrapProbeEnvelopes(JSON.parse(fixtureText('runA.context.json'))) as {
    steps: Record<string, { agent: string; model: unknown; system: unknown; tools: unknown }>
  }
  return Object.values(captured.steps).map((step) => ({
    sessionID: OWNED_SESSION_ID,
    agent: step.agent,
    model: step.model,
    system: step.system,
    tools: step.tools,
  }))
}

export function readGolden(variant: GoldenVariant): OtlpBody {
  return JSON.parse(fixtureText(`otlp-v3-runA.${variant}.json`)) as OtlpBody
}

// ---------------------------------------------------------------------------------------------
// Wire comparison

/** Object keys sorted at every level; the `attributes` lists sorted by key (OTLP attribute order carries no meaning). */
export function canonicalize(value: unknown, key?: string): unknown {
  if (Array.isArray(value)) {
    const items = value.map((item) => canonicalize(item))
    if (key === 'attributes') {
      items.sort((left, right) => {
        const a = String((left as { key: string }).key)
        const b = String((right as { key: string }).key)
        return a < b ? -1 : a > b ? 1 : 0
      })
    }
    return items
  }
  if (typeof value === 'object' && value !== null) {
    const record = value as Record<string, unknown>
    return Object.fromEntries(Object.keys(record).sort().map((name) => [name, canonicalize(record[name], name)]))
  }
  return value
}

export function canonicalText(value: unknown): string {
  return JSON.stringify(canonicalize(value))
}

/** Every accepted POST folded into one body: one resource, one scope, spans in arrival order. */
export function mergeBodies(bodies: readonly OtlpBody[]): OtlpBody {
  const first = bodies[0]?.resourceSpans[0]
  if (first === undefined) return { resourceSpans: [] }
  return {
    resourceSpans: [
      {
        resource: first.resource,
        scopeSpans: [{ scope: first.scopeSpans[0]?.scope, spans: bodies.flatMap((body) => spansOf(body)) }],
      },
    ],
  }
}

export function spansOf(body: OtlpBody): OtlpSpan[] {
  return body.resourceSpans.flatMap((resource) => resource.scopeSpans.flatMap((scope) => scope.spans))
}

export function attributesOf(span: OtlpSpan): Map<string, unknown> {
  return new Map(span.attributes.map((attribute) => [attribute.key, attribute.value]))
}

/** The whole body must equal the golden: first as structure (a readable diff), then byte for byte. */
export function expectGoldenWire(actual: OtlpBody, variant: GoldenVariant): void {
  const golden = readGolden(variant)
  expect(canonicalize(actual)).toEqual(canonicalize(golden))
  expect(canonicalText(actual)).toBe(canonicalText(golden))
}

// ---------------------------------------------------------------------------------------------
// Receiver: a recording fetch with a scripted answer per attempt

export type Answer = number | 'throw' | 'throwSync' | 'hang'
export type Attempt = { url: string; contentType: string | null; body: OtlpBody }
export type Receiver = {
  fetch: (url: string, init?: RequestInit) => Promise<Response>
  /** Every POST, answered or not, in order. */
  attempts: Attempt[]
  /** Bodies the receiver accepted (2xx), in order. */
  accepted: OtlpBody[]
  /** Highest number of POSTs open at once. */
  readonly maxInFlight: number
}

export function receiver(policy: (attempt: number) => Answer = () => 200): Receiver {
  const attempts: Attempt[] = []
  const accepted: OtlpBody[] = []
  let inFlight = 0
  let maxInFlight = 0
  const fetchFn = (url: string, init?: RequestInit): Promise<Response> => {
    const body = JSON.parse(String(init?.body)) as OtlpBody
    attempts.push({ url, contentType: new Headers(init?.headers).get('content-type'), body })
    const answer = policy(attempts.length)
    if (answer === 'throwSync') throw new Error('transport failed synchronously (synthetic)')
    if (answer === 'throw') return Promise.reject(new Error('connect ECONNREFUSED 127.0.0.1:4318 (synthetic)'))
    if (answer === 'hang') {
      return new Promise<Response>((_, reject) => {
        init?.signal?.addEventListener('abort', () => reject(new Error('aborted (synthetic)')), { once: true })
      })
    }
    inFlight += 1
    maxInFlight = Math.max(maxInFlight, inFlight)
    return new Promise<Response>((resolve) => {
      realSetImmediate(() => {
        inFlight -= 1
        if (answer >= 200 && answer < 300) accepted.push(body)
        resolve(new Response('{}', { status: answer, headers: { 'content-type': 'application/json' } }))
      })
    })
  }
  return {
    fetch: fetchFn,
    attempts,
    accepted,
    get maxInFlight() {
      return maxInFlight
    },
  }
}

// ---------------------------------------------------------------------------------------------
// Fake V2 host

export type HostOptions = {
  /** `ctx.location.directory`; defaults to the directory the golden session ran in. */
  directory?: string
  /** `ctx.session.hook` rejects at registration (a host that refuses the surface). */
  rejectSessionHook?: boolean
  /** `ctx.event.subscribe` throws. */
  throwOnSubscribe?: boolean
  /** No session, tool or event surface at all. */
  bare?: boolean
}

export type FakeV2Host = {
  /** Handed to `setup`; pass it through `start`, which casts it to the setup parameter. */
  readonly ctx: unknown
  /** Hook names registered through `session.hook`, in registration order. */
  readonly sessionHooks: string[]
  /** Hook names registered through `tool.hook`, in registration order. */
  readonly toolHooks: string[]
  /** Number of `event.subscribe()` calls. */
  readonly subscriptions: number
  /** Subscriptions the plugin ended (the consumer called `return()`). */
  readonly closedSubscriptions: number
  /** Calls every callback registered for `name`, in order, and awaits each. */
  deliverSession(name: string, input: unknown): Promise<void>
  deliverTool(name: string, input: unknown): Promise<void>
  /** Hands the event to every open subscription and waits until each consumer asks for the next one. */
  deliverEvent(event: unknown): Promise<void>
}

class EventSubscriber {
  private readonly queue: { event: unknown; taken: () => void }[] = []
  private parked: ((result: IteratorResult<unknown>) => void) | undefined
  private inFlight: (() => void) | undefined
  private closed = false

  constructor(private readonly onClose: () => void) {}

  /** Resolves once the consumer requests the next event, which means it finished this one. */
  push(event: unknown): Promise<void> {
    if (this.closed) return Promise.resolve()
    return new Promise<void>((taken) => {
      if (this.parked !== undefined) {
        const park = this.parked
        this.parked = undefined
        this.inFlight = taken
        park({ done: false, value: event })
      } else {
        this.queue.push({ event, taken })
      }
    })
  }

  next(): Promise<IteratorResult<unknown>> {
    this.inFlight?.()
    this.inFlight = undefined
    if (this.closed) return Promise.resolve({ done: true, value: undefined })
    const queued = this.queue.shift()
    if (queued !== undefined) {
      this.inFlight = queued.taken
      return Promise.resolve({ done: false, value: queued.event })
    }
    return new Promise<IteratorResult<unknown>>((resolve) => {
      this.parked = resolve
    })
  }

  close(): void {
    if (this.closed) return
    this.closed = true
    this.onClose()
    this.inFlight?.()
    this.inFlight = undefined
    for (const item of this.queue.splice(0)) item.taken()
    this.parked?.({ done: true, value: undefined })
    this.parked = undefined
  }

  iterable(): AsyncIterable<unknown> {
    return {
      [Symbol.asyncIterator]: () => ({
        next: () => this.next(),
        return: () => {
          this.close()
          return Promise.resolve({ done: true as const, value: undefined })
        },
      }),
    }
  }
}

function bounded(work: Promise<void>, message: string): Promise<void> {
  let timer: ReturnType<typeof setTimeout> | undefined
  const expiry = new Promise<never>((_, reject) => {
    timer = realSetTimeout(() => reject(new Error(message)), 2000)
  })
  return Promise.race([work, expiry]).finally(() => {
    if (timer !== undefined) realClearTimeout(timer)
  })
}

export function createHost(options: HostOptions = {}): FakeV2Host {
  const directory = options.directory ?? OWNED_DIRECTORY
  const sessionCallbacks = new Map<string, Callback[]>()
  const toolCallbacks = new Map<string, Callback[]>()
  const subscribers = new Set<EventSubscriber>()
  const sessionHooks: string[] = []
  const toolHooks: string[] = []
  let subscriptions = 0
  let closedSubscriptions = 0

  const register = (into: Map<string, Callback[]>, names: string[], name: string, callback: unknown): Promise<unknown> => {
    names.push(name)
    into.set(name, [...(into.get(name) ?? []), callback as Callback])
    return Promise.resolve({ dispose: () => undefined })
  }
  const subscribe = (): AsyncIterable<unknown> => {
    if (options.throwOnSubscribe === true) throw new Error('subscribe refused (synthetic)')
    subscriptions += 1
    const subscriber: EventSubscriber = new EventSubscriber(() => {
      closedSubscriptions += 1
      subscribers.delete(subscriber)
    })
    subscribers.add(subscriber)
    return subscriber.iterable()
  }
  const deliverTo = async (callbacks: Map<string, Callback[]>, name: string, input: unknown): Promise<void> => {
    for (const callback of callbacks.get(name) ?? []) await callback(input)
  }

  const session =
    options.rejectSessionHook === true
      ? { hook: () => Promise.reject(new Error('session hook refused (synthetic)')) }
      : { hook: (name: string, callback: unknown) => register(sessionCallbacks, sessionHooks, name, callback) }
  const tool = { hook: (name: string, callback: unknown) => register(toolCallbacks, toolHooks, name, callback) }
  const ctx =
    options.bare === true
      ? { location: { directory } }
      : { location: { directory }, session, tool, event: { subscribe } }

  return {
    ctx,
    sessionHooks,
    toolHooks,
    get subscriptions() {
      return subscriptions
    },
    get closedSubscriptions() {
      return closedSubscriptions
    },
    deliverSession: (name, input) => deliverTo(sessionCallbacks, name, input),
    deliverTool: (name, input) => deliverTo(toolCallbacks, name, input),
    deliverEvent: async (event) => {
      await Promise.all(
        [...subscribers].map((subscriber) => bounded(subscriber.push(event), 'event consumer did not take the next event')),
      )
    },
  }
}

/** Calls `setup` with the host as its context. The cast is the seam: the host is a fake, not the real `Context`. */
export async function start(setup: (ctx: never) => unknown, host: FakeV2Host): Promise<unknown> {
  return await setup(host.ctx as never)
}

export function isCleanup(value: unknown): value is () => unknown {
  return typeof value === 'function'
}

/**
 * Ends the plugin the way the host does: calls the cleanup `setup` returned, which stops the
 * subscription and flushes the outbox. Without one, it yields to pending work so an assertion still
 * sees what was sent; a missing cleanup is reported by the tests that pin it.
 */
export async function stopPlugin(cleanup: unknown): Promise<void> {
  if (isCleanup(cleanup)) {
    await cleanup()
    return
  }
  for (let turn = 0; turn < 20; turn += 1) {
    await new Promise<void>((resolve) => realSetImmediate(resolve))
  }
}

// ---------------------------------------------------------------------------------------------
// Replay of runA through the host, in file order

export type ReplayHooks = {
  /** Reorders the fixture lines before replay (for example, to move a hook after the event it used to precede). */
  order?: (lines: ProbeLine[]) => ProbeLine[]
  /** Rewrites one fixture line before it is delivered (for example, a tool that fails). */
  transform?: (line: ProbeLine, lineNumber: number) => ProbeLine
  /** Runs after each line has been delivered and awaited (for example, a consent change mid-session). */
  afterLine?: (lineNumber: number) => void | Promise<void>
}

export async function replayRunA(host: FakeV2Host, hooks: ReplayHooks = {}): Promise<void> {
  const contexts = readRunAContextInputs()
  const lines = hooks.order ? hooks.order(readRunALines()) : readRunALines()
  let contextIndex = 0
  for (const [index, original] of lines.entries()) {
    const lineNumber = index + 1
    const line = hooks.transform?.(original, lineNumber) ?? original
    if (line.kind.startsWith('event:')) {
      await host.deliverEvent(line.payload)
    } else if (line.kind === 'session.hook:context') {
      const input = contexts[contextIndex]
      contextIndex += 1
      if (input === undefined) throw new Error(`runA context fixture has no step for line ${lineNumber}`)
      await host.deliverSession('context', input)
    } else if (line.kind.startsWith('session.hook:')) {
      await host.deliverSession(line.kind.slice('session.hook:'.length), line.payload)
    } else if (line.kind.startsWith('tool.hook:')) {
      await host.deliverTool(line.kind.slice('tool.hook:'.length), line.payload)
    }
    await hooks.afterLine?.(lineNumber)
  }
}

// ---------------------------------------------------------------------------------------------
// Scratch directories and consent files

/** Registers an afterEach that removes every directory this returns; call it at the top of a test file. */
export function useTempDirectories(): () => string {
  const made: string[] = []
  afterEach(() => {
    for (const dir of made.splice(0)) rmSync(dir, { recursive: true, force: true })
  })
  return () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-oc-v2-'))
    made.push(dir)
    return dir
  }
}

/** `<dir>/.kyberdash/opencode-plugin.json`. The file may not exist yet: that reads as consent off. */
export function consentPathIn(dir: string): string {
  return join(dir, '.kyberdash', 'opencode-plugin.json')
}

export function writeConsent(path: string, content: boolean): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, JSON.stringify({ version: 1, content }))
}

export const noSleep = (): Promise<void> => Promise.resolve()

// ---------------------------------------------------------------------------------------------
// Synthetic sequences: small hand-written streams for rules the golden does not exercise

/** A bus event as the host delivers it: `created` is epoch milliseconds, and the id derives from type and time. */
export function busEvent(
  type: string,
  sessionID: string,
  created: number,
  data: Record<string, unknown> = {},
  directory?: string,
): unknown {
  return {
    id: `evt_${type}_${created}`,
    created,
    type,
    durable: { aggregateID: sessionID },
    ...(directory !== undefined ? { location: { directory } } : {}),
    data: { sessionID, ...data },
  }
}

export const bus = {
  sessionCreated: (sessionID: string, created: number, directory: string = OWNED_DIRECTORY): unknown =>
    busEvent('session.created', sessionID, created, { model: OWNED_MODEL, version: '2.2.1', slug: 'synthetic' }, directory),
  renamed: (sessionID: string, created: number, title: string): unknown =>
    busEvent('session.renamed', sessionID, created, { title }),
  executionStarted: (sessionID: string, created: number): unknown => busEvent('session.execution.started', sessionID, created),
  executionSucceeded: (sessionID: string, created: number): unknown =>
    busEvent('session.execution.succeeded', sessionID, created),
  executionFailed: (sessionID: string, created: number, error: { type: string; message: string }): unknown =>
    busEvent('session.execution.failed', sessionID, created, { error: { ...error, status: undefined } }),
  executionInterrupted: (sessionID: string, created: number, reason: string): unknown =>
    busEvent('session.execution.interrupted', sessionID, created, { reason }),
  stepStarted: (sessionID: string, created: number, messageID: string): unknown =>
    busEvent('session.step.started', sessionID, created, { assistantMessageID: messageID, agent: 'build', model: OWNED_MODEL }),
  stepEnded: (
    sessionID: string,
    created: number,
    messageID: string,
    extra: { finish?: string; cost?: number } = {},
  ): unknown =>
    busEvent('session.step.ended', sessionID, created, {
      assistantMessageID: messageID,
      finish: extra.finish ?? 'stop',
      ...(extra.cost !== undefined ? { cost: extra.cost } : {}),
      tokens: { input: 1, output: 1, reasoning: 0, cache: { read: 0, write: 0 } },
    }),
  toolCalled: (sessionID: string, created: number, messageID: string, callID: string, input: unknown): unknown =>
    busEvent('session.tool.called', sessionID, created, { assistantMessageID: messageID, id: callID, input, executed: false }),
  toolSucceeded: (sessionID: string, created: number, messageID: string, callID: string, text: string): unknown =>
    busEvent('session.tool.success', sessionID, created, {
      assistantMessageID: messageID,
      id: callID,
      content: [{ type: 'text', text }],
    }),
  toolFailed: (
    sessionID: string,
    created: number,
    messageID: string,
    callID: string,
    error: { type: string; message: string },
  ): unknown => busEvent('session.tool.failed', sessionID, created, { assistantMessageID: messageID, id: callID, error }),
  skillActivated: (sessionID: string, created: number, name: string): unknown =>
    busEvent('session.skill.activated', sessionID, created, { id: name, name, text: `skill ${name}` }),
}

/** Hook inputs as the 2.2.1 host shapes them (see promise/session.d.ts and promise/tool.d.ts). */
export const hook = {
  prompt: (sessionID: string, messageID: string, text: string, skills?: { id: string }[]): unknown => ({
    sessionID,
    messageID,
    prompt: { text, files: [], ...(skills !== undefined ? { skills } : {}) },
    delivery: 'steer',
  }),
  context: (sessionID: string, system: string[], tools: Record<string, unknown> = {}): unknown => ({
    sessionID,
    agent: 'build',
    model: OWNED_MODEL,
    system: system.map((text) => ({ type: 'text', text })),
    tools,
  }),
  modelRequest: (sessionID: string, kind = 'primary'): unknown => ({
    sessionID,
    agent: 'build',
    model: OWNED_MODEL,
    kind,
    baseURL: 'https://opencode.ai/inference/go/openai/v1',
  }),
  toolBefore: (sessionID: string, messageID: string, id: string, tool: string, input: unknown): unknown => ({
    tool,
    sessionID,
    agent: 'build',
    messageID,
    id,
    input,
  }),
  toolAfterCompleted: (sessionID: string, messageID: string, id: string, tool: string, input: unknown): unknown => ({
    tool,
    sessionID,
    agent: 'build',
    messageID,
    id,
    input,
    status: 'completed',
    result: { output: 'ok' },
  }),
  toolAfterError: (
    sessionID: string,
    messageID: string,
    id: string,
    tool: string,
    input: unknown,
    error: { type: string; message: string },
  ): unknown => ({ tool, sessionID, agent: 'build', messageID, id, input, status: 'error', error }),
}
