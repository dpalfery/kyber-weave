// Log identity derivation, split out of the receiver so the OpenCode plugin bundle (plan T3)
// can use it without pulling in the receiver's HTTP server, port discovery, and package.json
// read, none of which can load from a standalone plugin file.

import { createHash } from 'node:crypto'

/**
 * Globally unique log identity for enrichment idempotency.
 *
 * Event class names (`claude_code.api_request_body`) repeat every turn; using
 * one as `logId` made later records of the same class look like duplicates.
 * Correlation identity + timestamp + a payload digest distinguishes them.
 */
export function deriveLogId(input: {
  traceId: string | null
  spanId: string | null
  sessionId: string | null
  timeUnixNano: bigint | string
  body: unknown
  attributes: Record<string, unknown>
  eventName?: string
}): string {
  const keys = Object.keys(input.attributes).sort()
  const attributes: Record<string, unknown> = {}
  for (const key of keys) attributes[key] = input.attributes[key]
  const digest = createHash('sha256')
    .update(
      JSON.stringify({
        event: input.eventName ?? '',
        body: input.body ?? null,
        attributes,
      }),
    )
    .digest('hex')
    .slice(0, 16)
  return `${input.traceId ?? input.sessionId ?? 'log'}:${input.spanId ?? 'none'}:${String(input.timeUnixNano)}:${digest}`
}
