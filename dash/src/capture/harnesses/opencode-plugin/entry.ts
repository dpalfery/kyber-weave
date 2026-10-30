// OpenCode loadable entry: the module OpenCode 2.x loads.
//
// The 2.x loader reads only `.default` and requires `{ id, setup }`, so this module default-exports
// exactly that. `setup(ctx)` wires the capture engine (`plugin.ts`) to the host:
//
//   ctx.event.subscribe()                    the bus: executions, steps, tools, skills, session metadata
//   ctx.session.hook('prompt' | 'context' | 'model.request')
//   ctx.tool.hook('execute.before' | 'execute.after')
//
// and returns a Cleanup that stops the subscription, emits each still-open execution once as
// outcome `unknown`, and flushes the outbox within a bounded wait.
//
// `experimental.idle` is deliberately not registered. The host runs it "just before it goes idle",
// it can admit more input into the same execution, and it does not run on failure, so it cannot say
// that a request ended. A request span is emitted at a terminal execution event, after 30 minutes
// without events, or at cleanup.
//
// Consent (plan section 3.4): prompt text, the session label, the system prompt, tool definitions
// and the skills listing are content. They ride only when `~/.kyberdash/opencode-plugin.json` says
// `content: true`, read per event so a Settings change applies without a reinstall;
// `KYBERDASH_OPENCODE_CONTEXT` is an override only (`0` off, `1` on). Metadata always flows.
//
// Generations. A reload runs `setup` again in the same process. A process-global registry, keyed by
// the instance directory, makes the newer setup stop the older one for that directory; setups for
// other directories are untouched, so a second project's plugin never silences the first.
//
// Never fails the host: every hook, event and setup step is best-effort and swallowed (reported to
// `onError` when given). Nothing runs at import; the outbox and timers are created inside `setup`.

import { resolveContentConsent, resolveConsentPath } from './consent.js'
import { OPENCODE_PLUGIN_ID, createCaptureEngine, type CaptureEngine } from './plugin.js'
import { createOutbox, type Outbox } from './transport/outbox.js'
import { DEFAULT_OTLP_ENDPOINT, type OtlpFetch } from './transport/otlp-sender.js'

/** How long `Cleanup` waits for the outbox to drain before it gives up and closes it. */
const DEFAULT_FLUSH_TIMEOUT_MS = 1500
/** The sweep timer never fires more often than this, whatever the remaining time. */
const MIN_SWEEP_MS = 1000

export type EntryOptions = {
  /** Transport for the loopback POST; default the global `fetch`. */
  fetchFn?: OtlpFetch
  endpoint?: string
  /** Env the consent override is read from; default `process.env` at setup. */
  env?: NodeJS.ProcessEnv
  /** Consent file path; default `~/.kyberdash/opencode-plugin.json`. */
  consentPath?: string
  /** Backoff clock and entropy for the outbox; default real timers and `Math.random`. */
  sleepFn?: (ms: number) => Promise<void>
  randomFn?: () => number
  /** Abort deadline per POST (ms). */
  requestTimeoutMs?: number
  /** Bound on the outbox flush inside `Cleanup` (ms). */
  flushTimeoutMs?: number
  /** Tap for failures the plugin swallowed. Itself runs swallowed. */
  onError?: (where: string, error: unknown) => void
}

/** The cleanup `setup` returns: stops the subscription and flushes. Safe to call more than once. */
export type Cleanup = () => Promise<void>

type HookRegistrar = {
  hook?: (name: string, callback: (input: never) => unknown) => unknown
}

/** The slice of the OpenCode 2.x setup `Context` this plugin touches. Every part is optional. */
export type OpencodeSetupContext = {
  location?: { directory?: unknown }
  session?: HookRegistrar
  tool?: HookRegistrar
  event?: { subscribe?: () => AsyncIterable<unknown> }
}

type Generation = { stop: () => Promise<void> }

const GENERATIONS_KEY = Symbol.for('kyberdash.opencode-plugin.generations')

/**
 * The process-global map of live generations by instance directory. It lives on `globalThis`, not
 * in this module, because a reload can load a second copy of this bundle that shares no module state.
 */
function generations(): Map<string, Generation> {
  // The key is ours alone; the cast only gives the global object an index signature for it.
  const holder = globalThis as unknown as Record<symbol, unknown>
  const existing = holder[GENERATIONS_KEY]
  if (existing instanceof Map) return existing as Map<string, Generation>
  const created = new Map<string, Generation>()
  holder[GENERATIONS_KEY] = created
  return created
}

function unref(handle: unknown): void {
  if (typeof handle === 'object' && handle !== null && 'unref' in handle && typeof handle.unref === 'function') {
    handle.unref()
  }
}

/** Waits for the outbox to drain, but never longer than `timeoutMs`; the outbox is closed either way. */
async function flushBounded(outbox: Outbox, timeoutMs: number, report: (where: string, error: unknown) => void): Promise<void> {
  let timer: ReturnType<typeof setTimeout> | undefined
  const expiry = new Promise<void>((resolve) => {
    timer = setTimeout(resolve, timeoutMs)
  })
  try {
    await Promise.race([outbox.drain(), expiry])
  } catch (error) {
    report('flush', error)
  } finally {
    if (timer !== undefined) clearTimeout(timer)
    try {
      await outbox.close()
    } catch (error) {
      report('close', error)
    }
  }
}

async function start(ctx: OpencodeSetupContext | undefined, options: EntryOptions): Promise<Cleanup> {
  const report = (where: string, error: unknown): void => {
    try {
      options.onError?.(where, error)
    } catch {
      // The tap is observability only; a failing tap has nowhere further to report.
    }
  }

  const rawDirectory = ctx?.location?.directory
  const directory = typeof rawDirectory === 'string' && rawDirectory !== '' ? rawDirectory : undefined
  const env = options.env ?? process.env
  const consentPath = options.consentPath ?? resolveConsentPath()
  const flushTimeoutMs = options.flushTimeoutMs ?? DEFAULT_FLUSH_TIMEOUT_MS

  const outbox = createOutbox({
    endpoint: options.endpoint ?? DEFAULT_OTLP_ENDPOINT,
    ...(options.fetchFn !== undefined ? { fetchFn: options.fetchFn } : {}),
    ...(options.sleepFn !== undefined ? { sleepFn: options.sleepFn } : {}),
    ...(options.randomFn !== undefined ? { randomFn: options.randomFn } : {}),
    ...(options.requestTimeoutMs !== undefined ? { requestTimeoutMs: options.requestTimeoutMs } : {}),
  })
  const engine: CaptureEngine = createCaptureEngine({
    ...(directory !== undefined ? { directory } : {}),
    emit: outbox.enqueue,
    consent: () => resolveContentConsent(env, consentPath),
    onError: report,
  })

  let stopped = false
  let iterator: AsyncIterator<unknown> | undefined
  let sweep: { handle: ReturnType<typeof setTimeout>; clear: typeof clearTimeout } | undefined
  const disposables: Array<() => unknown> = []

  /** Arms one timer for the next execution to fall idle; none while no execution is open. */
  const armSweep = (): void => {
    if (stopped || sweep !== undefined) return
    const delay = engine.nextSweepDelayMs()
    if (delay === undefined) return
    // Timer functions are read when the timer is armed and kept with it, so the same pair that
    // armed it cancels it.
    const set = globalThis.setTimeout
    const clear = globalThis.clearTimeout
    const handle = set(() => {
      sweep = undefined
      if (stopped) return
      engine.sweep()
      armSweep()
    }, Math.max(MIN_SWEEP_MS, delay))
    unref(handle)
    sweep = { handle, clear }
  }

  const hookCallback =
    (handle: (input: unknown) => void) =>
    (input: unknown): Promise<void> => {
      if (!stopped) {
        try {
          handle(input)
          armSweep()
        } catch (error) {
          report('hook', error)
        }
      }
      return Promise.resolve()
    }

  let stopping: Promise<void> | undefined
  const run = async (): Promise<void> => {
    // Everything before the first await runs synchronously, so a superseding setup sees the
    // subscription closed as soon as it calls this.
    stopped = true
    if (directory !== undefined && generations().get(directory) === generation) generations().delete(directory)
    if (sweep !== undefined) {
      sweep.clear(sweep.handle)
      sweep = undefined
    }
    try {
      void Promise.resolve(iterator?.return?.()).catch((error: unknown) => report('unsubscribe', error))
    } catch (error) {
      report('unsubscribe', error)
    }
    for (const dispose of disposables.splice(0)) {
      try {
        void Promise.resolve(dispose()).catch((error: unknown) => report('dispose', error))
      } catch (error) {
        report('dispose', error)
      }
    }
    engine.flushOpenExecutions()
    await flushBounded(outbox, flushTimeoutMs, report)
  }
  const cleanup: Cleanup = () => {
    stopping ??= run()
    return stopping
  }
  const generation: Generation = { stop: cleanup }

  // A newer setup for this directory supersedes the older one; other directories are untouched.
  if (directory !== undefined) {
    const live = generations()
    const previous = live.get(directory)
    live.set(directory, generation)
    if (previous !== undefined) previous.stop().catch((error: unknown) => report('supersede', error))
  }

  // The bus first, so no event is missed while the hooks register.
  try {
    const events = ctx?.event?.subscribe?.()
    if (events !== undefined) {
      const source = events[Symbol.asyncIterator]()
      iterator = source
      void (async () => {
        try {
          for (;;) {
            const next = await source.next()
            if (next.done === true || stopped) return
            try {
              engine.onEvent(next.value)
              armSweep()
            } catch (error) {
              report('event', error)
            }
          }
        } catch (error) {
          if (!stopped) report('event-stream', error)
        }
      })()
    }
  } catch (error) {
    report('subscribe', error)
  }

  const register = async (registrar: HookRegistrar | undefined, name: string, handle: (input: unknown) => void): Promise<void> => {
    if (typeof registrar?.hook !== 'function') return
    try {
      const registration: unknown = await registrar.hook(name, hookCallback(handle))
      if (isDisposable(registration)) disposables.push(() => registration.dispose())
    } catch (error) {
      report(`hook ${name}`, error)
    }
  }

  await register(ctx?.session, 'prompt', engine.onPrompt)
  await register(ctx?.session, 'context', engine.onContext)
  await register(ctx?.session, 'model.request', engine.onModelRequest)
  await register(ctx?.tool, 'execute.before', engine.onToolBefore)
  await register(ctx?.tool, 'execute.after', engine.onToolAfter)

  return cleanup
}

function isDisposable(value: unknown): value is { dispose: () => unknown } {
  return typeof value === 'object' && value !== null && 'dispose' in value && typeof value.dispose === 'function'
}

/**
 * Build the OpenCode 2.x `setup` function. The pipeline is created when `setup` runs, never at
 * import, and `setup` always resolves to a Cleanup.
 */
export function createSetup(options: EntryOptions = {}): (ctx: OpencodeSetupContext) => Promise<Cleanup> {
  return async (ctx) => {
    try {
      return await start(ctx, options)
    } catch (error) {
      try {
        options.onError?.('setup', error)
      } catch {
        // The tap is observability only; a failing tap has nowhere further to report.
      }
      return () => Promise.resolve()
    }
  }
}

/** The only export OpenCode 2.x reads: `{ id, setup }`. */
export default {
  id: OPENCODE_PLUGIN_ID,
  setup: createSetup(),
}
