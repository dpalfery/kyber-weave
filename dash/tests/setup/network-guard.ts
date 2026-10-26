// Live-receiver fetch guard for the test suite (T1, D10).
//
// The KyberDash OTLP receiver listens at 127.0.0.1:4318. A test that reaches it
// posts real telemetry from the developer's machine. The plugin host swallows
// delivery errors by design, so a leaking test can stay green while it posts.
// This guard fails closed instead: a fetch to the receiver is refused with a
// named error and recorded, and tests/setup/network-guard-setup.ts fails the
// test that caused it. Every other URL goes to the real fetch unchanged.
//
// No vitest import here. The setup file registers the hooks; the rules stay
// testable on their own.

export const NETWORK_GUARD_ERROR_NAME = 'KyberDashNetworkGuardError'

const LIVE_RECEIVER_HOSTS: readonly string[] = ['127.0.0.1', 'localhost']
const LIVE_RECEIVER_PORT = '4318'

type FetchLike = (input: string | URL | Request, init?: RequestInit) => Promise<Response>

/** Raised in place of a request to the live receiver. Named so a failure reads as the guard. */
export class NetworkGuardError extends Error {
  constructor(url: string) {
    super(
      `KyberDash test network guard refused ${url}: tests must not reach the live receiver ` +
        `(127.0.0.1:4318 / localhost:4318). Inject fetchFn instead.`,
    )
    this.name = NETWORK_GUARD_ERROR_NAME
  }
}

export type NetworkGuard = {
  /** The guarded fetch to install in place of the real one. */
  readonly fetch: typeof fetch
  /** URLs refused so far, in order. */
  readonly hits: readonly string[]
  /** Forget recorded hits. Used only by a test that asserts a hit on purpose. */
  clear(): void
}

function urlOf(input: string | URL | Request): string {
  if (typeof input === 'string') return input
  if (input instanceof URL) return input.href
  return input.url
}

/** True when the URL targets the live receiver: 127.0.0.1 or localhost on port 4318. */
export function isLiveReceiverUrl(input: string | URL | Request): boolean {
  let parsed: URL
  try {
    parsed = new URL(urlOf(input))
  } catch {
    return false
  }
  return LIVE_RECEIVER_HOSTS.includes(parsed.hostname) && parsed.port === LIVE_RECEIVER_PORT
}

const GUARD_HANDLE = Symbol.for('kyberdash.test.network-guard')

type GuardedFetch = typeof fetch & { [GUARD_HANDLE]?: NetworkGuard }

/** Wrap a base fetch so live-receiver URLs are refused, recorded, and never delegated. */
export function createNetworkGuard(base: FetchLike): NetworkGuard {
  const hits: string[] = []
  const guardedFetch = ((input: string | URL | Request, init?: RequestInit): Promise<Response> => {
    if (isLiveReceiverUrl(input)) {
      const url = urlOf(input)
      hits.push(url)
      return Promise.reject(new NetworkGuardError(url))
    }
    return base(input, init)
  }) as GuardedFetch

  const handle: NetworkGuard = {
    fetch: guardedFetch,
    get hits(): readonly string[] {
      return hits
    },
    clear: () => {
      hits.length = 0
    },
  }
  guardedFetch[GUARD_HANDLE] = handle
  return handle
}

/** The guard handle already installed on a fetch, if any. */
export function networkGuardOf(fetchFn: unknown): NetworkGuard | undefined {
  if (typeof fetchFn !== 'function') return undefined
  return (fetchFn as GuardedFetch)[GUARD_HANDLE]
}

export function isNetworkGuarded(fetchFn: unknown): boolean {
  return networkGuardOf(fetchFn) !== undefined
}

/**
 * Install the guard on `target.fetch` (default: globalThis). Idempotent: a
 * second install returns the handle already in place rather than wrapping twice.
 */
export function installNetworkGuard(target: { fetch: typeof fetch } = globalThis): NetworkGuard {
  const existing = networkGuardOf(target.fetch)
  if (existing) return existing
  const guard = createNetworkGuard(target.fetch)
  target.fetch = guard.fetch
  return guard
}

/**
 * Test double for the live receiver. While installed, a fetch to the live
 * receiver is answered by `answer` instead of being refused, so the test
 * states what the receiver says (for example, "nothing is listening"). Any
 * other URL still reaches the guarded fetch. Returns the restore function.
 *
 * This is the explicit, reviewed way to let a test touch the receiver's
 * URL without a socket. An unstubbed fetch to the receiver is still refused.
 */
export function answerLiveReceiver(answer: (url: string) => Promise<Response>): () => void {
  const previous = globalThis.fetch
  const stubbed = ((input: string | URL | Request, init?: RequestInit): Promise<Response> =>
    isLiveReceiverUrl(input) ? answer(urlOf(input)) : previous(input, init)) as typeof fetch
  globalThis.fetch = stubbed
  return () => {
    globalThis.fetch = previous
  }
}
