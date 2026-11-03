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

/** One inet_aton part: decimal, 0-octal or 0x-hex. NaN when it is none of those. */
function inetPart(part: string): number {
  if (/^0x[0-9a-f]*$/i.test(part)) return part.length === 2 ? Number.NaN : Number.parseInt(part.slice(2), 16)
  if (/^0[0-7]+$/.test(part)) return Number.parseInt(part, 8)
  if (/^(0|[1-9][0-9]*)$/.test(part)) return Number.parseInt(part, 10)
  return Number.NaN
}

/**
 * The address a 1-4 part inet_aton host spells (`2130706433`, `0x7f.1`, `0177.0.0.1`), or
 * undefined when it is not such a host. The last part fills all remaining bytes.
 */
function inetAton(host: string): number | undefined {
  const parts = host.split('.')
  if (parts.length > 4) return undefined
  const nums = parts.map(inetPart)
  if (nums.some((n) => Number.isNaN(n))) return undefined
  const last = nums[nums.length - 1] as number
  const leading = nums.slice(0, -1)
  if (leading.some((n) => n > 255) || last >= 256 ** (4 - leading.length)) return undefined
  return leading.reduce((acc, n, i) => acc + n * 256 ** (3 - i), 0) + last
}

/**
 * True for any loopback host: localhost, 127.0.0.0/8 in any numeric spelling, [::1], or a
 * v4-mapped 127.x address. URL normalizes numeric hosts already; raw strings are handled too.
 */
export function isLoopbackHost(rawHost: string): boolean {
  // URL already lower-cases and normalizes IPv6 (::ffff:127.0.0.1 -> ::ffff:7f00:1); strip the
  // brackets and any trailing root dot.
  const host = rawHost.toLowerCase().replace(/^\[|\]$/g, '').replace(/\.$/, '')
  if (host === 'localhost') return true
  const v4 = inetAton(host)
  if (v4 !== undefined) return Math.floor(v4 / 2 ** 24) === 127
  if (host === '::1') return true
  const mapped = /^::ffff:([0-9a-f]{1,4}):[0-9a-f]{1,4}$/.exec(host)
  if (mapped) return (Number.parseInt(mapped[1] as string, 16) >> 8) === 127
  return /^::ffff:127\./.test(host)
}

/** True when the URL targets the live receiver: any loopback host on port 4318. */
export function isLiveReceiverUrl(input: string | URL | Request): boolean {
  let parsed: URL
  try {
    parsed = new URL(urlOf(input))
  } catch {
    return false
  }
  return isLoopbackHost(parsed.hostname) && parsed.port === LIVE_RECEIVER_PORT
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
