// The Factory (Droid) capture hook runner: the one-shot process Factory runs for every hook
// event.
//
// Factory runs a Kyber-owned plugin as a HOOK: one short-lived process per hook event, the
// payload as JSON on stdin, and the process exits when the hook is done. That is the opposite of
// the OpenCode plugin, which is long-lived with an in-memory outbox, so this runner parks an
// undelivered event in a file-backed spool (`./spool.ts`) instead of a queue.
//
// The rules this runner keeps:
//
//   - it never fails its host. `runHook` resolves 0 on every path - a valid payload, malformed
//     JSON, empty stdin, an unknown hook event, a payload with a missing field, an internal
//     error - and it never rejects;
//   - stdout carries the hook protocol and nothing else. Diagnostics go to stderr and to a
//     bounded tail file under the KyberDash state root;
//   - one payload becomes at most one span, through the mapper (`./mapper.ts`);
//   - the prompt text travels only when the consent reader says so: the consent file, or the
//     `KYBERDASH_FACTORY_CONTEXT` override, which wins over the file (`./consent.ts`);
//   - the delivery is bounded: it carries an abort signal and a deadline of at most two seconds;
//   - every run drains the spool before it sends its own event.
//
// This module is a LEAF: it imports only `node:` built-ins and the capture family it belongs
// to, so the plugin never drags engine code into the small bounded process it runs in.

import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

import { writeFileAtomic } from '../../consent-file.js'
import { FACTORY_CONSENT_FILE_NAME, resolveContentConsent } from './consent.js'
import { mapHookEvent, type FactoryWireSpan } from './mapper.js'
import { appendSpoolEvent, clearSpool, readSpoolEvents } from './spool.js'
import {
  DEFAULT_OTLP_ENDPOINT,
  OtlpDeliveryError,
  buildOtlpTracesBody,
  type OtlpWireSpan,
} from '../transport/otlp-sender.js'

/** The `service.name` the Factory plugin's spans are reported under. */
const FACTORY_SERVICE_NAME = 'kyberdash-factory-plugin'
/** The scope the Factory plugin's spans ride under. */
const FACTORY_SCOPE_NAME = 'kyberdash.factory-plugin'
/** The cap on the diagnostic tail file. */
const TAIL_CAP_BYTES = 64 * 1024
/**
 * The abort deadline of one delivery. The hook runs inside somebody else's tool, so it must not
 * hold that tool for longer than the plan allows.
 */
const DEFAULT_REQUEST_TIMEOUT_MS = 2000
/** Bound on one response-body teardown: disposal must never wedge the hook. */
const BODY_DISPOSE_TIMEOUT_MS = 250
/**
 * The cap on the bytes one hook payload may carry.
 *
 * The payload is the harness's own JSON: the event name, the ids, and the values the mapper
 * measures. The one part of it that is text is the prompt, and the mapper caps that at 64 KiB
 * itself. A megabyte is sixteen times that cap, so a real payload sits comfortably inside the
 * bound, and a payload past it is REFUSED rather than accumulated: this process runs inside
 * somebody else's tool, and their memory is not the hook's to spend.
 */
const MAX_STDIN_BYTES = 1024 * 1024
/**
 * The deadline of one payload read.
 *
 * The harness writes the payload and closes its stdin, so a read that is still waiting after
 * this belongs to a harness that never closed it. The hook gives up on it rather than hold the
 * tool that ran it: five seconds is more than twice the delivery deadline the runner holds
 * itself to, and no payload that is going to arrive needs that long.
 */
const STDIN_READ_TIMEOUT_MS = 5_000

/** Everything the runner needs. Every seam is injected, so a test never touches the network. */
export type HookRunOptions = {
  /** The fetch the delivery uses. */
  fetchFn?: (url: string, init?: RequestInit) => Promise<Response>
  /** The OTLP traces endpoint; the loopback receiver by default. */
  endpoint?: string
  /** The consent file. The prompt text travels only when it says content is on. */
  consentPath?: string
  /**
   * The environment the consent override is read from; `process.env` by default. The override
   * (`KYBERDASH_FACTORY_CONTEXT`) wins over the consent file, so it is the seam a test injects.
   */
  env?: NodeJS.ProcessEnv
  /** The KyberDash state directory the spool and the diagnostic tail live under. */
  stateDir?: string
  /** Reads the hook payload; the process stdin by default. */
  readStdin?: () => Promise<string>
  /** Writes to the host's stdout. The hook protocol only, never diagnostics. */
  stdout?: (text: string) => void
  /** Writes to the host's stderr. */
  stderr?: (text: string) => void
  /**
   * Injectable clock. The one-shot runner never waits - the spool is the retry - but the seam
   * is part of the shared transport's shape, so one call site can serve both plugins.
   */
  sleepFn?: (ms: number) => Promise<void>
  /**
   * Injectable entropy, for the same reason as `sleepFn`: the runner never jitters a delay.
   */
  randomFn?: () => number
  /** The abort deadline of one delivery, in milliseconds. */
  requestTimeoutMs?: number
}

/**
 * Run one hook event. Resolves with the process exit code, which is 0 on every path: a capture
 * plugin must never fail the host that ran it.
 */
export async function runHook(options: HookRunOptions = {}): Promise<number> {
  const stateDir = options.stateDir ?? defaultStateDir()
  try {
    await captureOnce(options, stateDir)
  } catch (error) {
    // The one place an unexpected failure ends: the diagnostic tail records it and the hook
    // still exits 0.
    reportDiagnostic(stateDir, options.stderr, 'hook', error)
  }
  return 0
}

/** The diagnostic tail file under a KyberDash state root. */
export function diagnosticTailPath(stateDir: string): string {
  return join(stateDir, 'capture', 'factory', 'diagnostics.log')
}

/**
 * Append one diagnostic to the tail file. The file stays within 64 KB by dropping its oldest
 * content, so a hook that fails on every run cannot fill the disk.
 */
export function appendDiagnosticTail(tailPath: string, text: string): void {
  const combined = Buffer.from(`${readTextIfPresent(tailPath)}${text}`, 'utf8')
  const kept =
    combined.length <= TAIL_CAP_BYTES ? combined : combined.subarray(combined.length - TAIL_CAP_BYTES)
  writeFileAtomic(tailPath, kept.toString('utf8'))
}

// ---------------------------------------------------------------------------------------------
// The run

/** Read the payload, map it, drain the spool, and deliver. */
async function captureOnce(options: HookRunOptions, stateDir: string): Promise<void> {
  const readStdin = options.readStdin ?? readProcessStdin
  const raw = await readStdin()
  const payload = parsePayload(raw)
  if (payload === undefined) {
    reportDiagnostic(stateDir, options.stderr, 'payload', new Error('the hook payload is not a JSON object'))
    return
  }
  const consentPath = options.consentPath ?? join(stateDir, FACTORY_CONSENT_FILE_NAME)
  // The consent reader decides, not the runner: content travels only when the file says so, a
  // missing or malformed file means content off, and the environment override wins over the file.
  const contentAllowed = resolveContentConsent(options.env ?? process.env, consentPath)
  const spans = mapHookEvent(payload, { contentAllowed })
  // The spool is drained first: an event parked by an earlier run is older than this one.
  await drainSpool(options, stateDir)
  if (spans.length === 0) return
  const delivered = await deliver(options, stateDir, spans)
  if (!delivered) {
    // The event is parked, not lost: the next run delivers it.
    for (const span of spans) appendSpoolEvent(stateDir, span)
  }
}

/** Deliver the parked events and empty the spool when the receiver took them. */
async function drainSpool(options: HookRunOptions, stateDir: string): Promise<void> {
  const parked = readSpoolEvents(stateDir)
  if (parked.length === 0) return
  const delivered = await deliver(options, stateDir, parked)
  if (delivered) clearSpool(stateDir)
}

/**
 * Post the spans to the loopback receiver. Answers whether the receiver took them: a failure is
 * recorded and reported as a failure, never as a delivery.
 */
async function deliver(options: HookRunOptions, stateDir: string, spans: readonly unknown[]): Promise<boolean> {
  const endpoint = options.endpoint ?? DEFAULT_OTLP_ENDPOINT
  const fetchFn = options.fetchFn ?? ((url, init) => fetch(url, init))
  const requestTimeoutMs = Math.max(1, Math.floor(options.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS))
  const body = JSON.stringify(
    buildOtlpTracesBody(spans.map((span) => toWireSpan(span)), {
      serviceName: FACTORY_SERVICE_NAME,
      scopeName: FACTORY_SCOPE_NAME,
    }),
  )
  const controller = new AbortController()
  const timer = setTimeout(() => {
    controller.abort(new Error(`the OTLP delivery timed out after ${requestTimeoutMs}ms`))
  }, requestTimeoutMs)
  try {
    const pending = fetchFn(endpoint, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body,
      signal: controller.signal,
    })
    // The deadline must settle the attempt even when the transport ignores the signal: race the
    // POST against the abort instead of merely handing the signal over.
    const aborted = new Promise<never>((_, reject) => {
      if (controller.signal.aborted) {
        reject(controller.signal.reason ?? new Error('the OTLP delivery was aborted'))
      } else {
        controller.signal.addEventListener(
          'abort',
          () => reject(controller.signal.reason ?? new Error('the OTLP delivery was aborted')),
          { once: true },
        )
      }
    })
    void aborted.catch(() => undefined)
    const response = await Promise.race([pending, aborted])
    if (!response.ok) throw new OtlpDeliveryError(response.status, response.statusText)
    disposeBody(response)
    return true
  } catch (error) {
    reportDiagnostic(stateDir, options.stderr, 'deliver', error)
    return false
  } finally {
    clearTimeout(timer)
  }
}

// ---------------------------------------------------------------------------------------------
// The wire shape

/**
 * Release the response body without waiting for it. A cancellation that never settles must not
 * wedge the hook, so the wait itself is bounded and unobserved.
 */
function disposeBody(response: Response): void {
  try {
    const cancelled = response.body?.cancel()
    if (cancelled !== undefined) {
      void Promise.race([
        cancelled.catch(() => undefined),
        new Promise<void>((resolve) => setTimeout(resolve, BODY_DISPOSE_TIMEOUT_MS)),
      ])
    }
  } catch {
    // Body teardown must never fail a delivery the receiver accepted.
  }
}

/** The Unix nanoseconds of a moment in time. */
function nanos(ms: number): string {
  return (BigInt(Math.trunc(ms)) * 1000000n).toString()
}

/**
 * The span as the OTLP body carries it. The mapper stamps no time of its own, so the transport
 * stamps the moment the span was handed over.
 */
function toWireSpan(event: unknown): OtlpWireSpan {
  const span = (typeof event === 'object' && event !== null ? event : {}) as Partial<FactoryWireSpan>
  const at = nanos(Date.now())
  return {
    traceId: typeof span.traceId === 'string' ? span.traceId : '',
    spanId: typeof span.spanId === 'string' ? span.spanId : '',
    name: typeof span.name === 'string' ? span.name : '',
    kind: 'SPAN_KIND_INTERNAL',
    startTimeUnixNano: at,
    endTimeUnixNano: at,
    attributes: Array.isArray(span.attributes) ? span.attributes : [],
  }
}

// ---------------------------------------------------------------------------------------------
// The payload

/** The payload as an object, or undefined when it is not one. */
function parsePayload(raw: string): Record<string, unknown> | undefined {
  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  } catch {
    return undefined
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return undefined
  return parsed as Record<string, unknown>
}

/**
 * The whole of the process stdin, as text.
 *
 * The read is bounded twice: by the bytes the payload may carry and by the time the read may
 * take. A payload past the byte cap is refused rather than accumulated, and a stdin that never
 * ends is given up on — a hook that waits forever is a hook that holds the tool that ran it.
 */
async function readProcessStdin(): Promise<string> {
  const chunks: Buffer[] = []
  let bytes = 0
  const read = (async (): Promise<string> => {
    for await (const chunk of process.stdin) {
      const buffer = Buffer.from(chunk)
      bytes += buffer.length
      if (bytes > MAX_STDIN_BYTES) {
        throw new Error(`the hook payload is over the ${MAX_STDIN_BYTES} byte bound`)
      }
      chunks.push(buffer)
    }
    return Buffer.concat(chunks).toString('utf8')
  })()
  let timer: NodeJS.Timeout | undefined
  const expiry = new Promise<never>((_, reject) => {
    timer = setTimeout(() => {
      // The stream is closed as well: a reader still waiting on it would keep this
      // process alive after the deadline, which is the very thing the deadline is for.
      process.stdin.destroy()
      reject(new Error(`the hook payload was not read within ${STDIN_READ_TIMEOUT_MS} ms`))
    }, STDIN_READ_TIMEOUT_MS)
  })
  try {
    return await Promise.race([read, expiry])
  } finally {
    if (timer !== undefined) clearTimeout(timer)
  }
}

// ---------------------------------------------------------------------------------------------
// Diagnostics

/** The KyberDash state directory of this machine. */
function defaultStateDir(): string {
  return join(homedir(), '.kyberdash')
}

/** The text of a file, or an empty string when there is none to read. */
function readTextIfPresent(path: string): string {
  try {
    return readFileSync(path, 'utf8')
  } catch {
    return ''
  }
}

/**
 * Record one diagnostic: in the bounded tail file and on stderr. Neither is allowed to fail the
 * hook, so a tail that cannot be written is not an error the host has to hear about.
 */
function reportDiagnostic(
  stateDir: string,
  stderr: ((text: string) => void) | undefined,
  where: string,
  error: unknown,
): void {
  const text = `${new Date().toISOString()} ${where}: ${messageOf(error)}\n`
  try {
    appendDiagnosticTail(diagnosticTailPath(stateDir), text)
  } catch {
    // The tail is a diagnostic, not a duty.
  }
  try {
    stderr?.(text)
  } catch {
    // A host that closed its stderr must not fail the hook either.
  }
}

/** The message of an error, whatever it is. */
function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
