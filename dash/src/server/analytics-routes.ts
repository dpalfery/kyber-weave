import type { IncomingMessage, ServerResponse } from 'node:http'

import type { AnalyticsEnvelope, AnalyticsOperation } from '../analysis/query/service.js'

export type AnalyticsService = {
  execute(
    operation: AnalyticsOperation,
    request: unknown,
    options?: { signal?: AbortSignal },
  ): Promise<AnalyticsEnvelope>
}

type Route = { path: string; method: 'GET' | 'POST'; operation: AnalyticsOperation }

const ROUTES: readonly Route[] = [
  { path: '/api/kyber/analytics/status', method: 'GET', operation: 'status' },
  { path: '/api/kyber/analytics/catalog', method: 'GET', operation: 'catalog' },
  { path: '/api/kyber/analytics/query', method: 'POST', operation: 'query' },
  { path: '/api/kyber/analytics/search-sessions', method: 'POST', operation: 'search-sessions' },
  { path: '/api/kyber/analytics/inspect', method: 'POST', operation: 'inspect' },
  { path: '/api/kyber/analytics/findings', method: 'POST', operation: 'findings' },
  { path: '/api/kyber/analytics/compare', method: 'POST', operation: 'compare' },
  { path: '/api/kyber/analytics/outcomes', method: 'POST', operation: 'outcomes' },
]

const MAX_BODY_BYTES = 64 * 1024

function statusFor(envelope: AnalyticsEnvelope): number {
  switch (envelope.error?.code) {
    case undefined:
      return 200
    case 'invalid_request':
    case 'unsupported_query':
      return 400
    case 'not_found':
      return 404
    case 'stale_cursor':
      return 409
    case 'budget_exceeded':
      return 413
    case 'engine_unavailable':
    case 'incompatible_engine':
    case 'cancelled':
      return 503
    case 'internal_error':
      return 500
    default:
      return 200
  }
}

function sendJson(res: ServerResponse, status: number, body: AnalyticsEnvelope, headers: Record<string, string> = {}): void {
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    ...headers,
  })
  res.end(JSON.stringify(body))
}

function transportError(
  code: 'invalid_request' | 'budget_exceeded' | 'engine_unavailable' | 'internal_error',
  message: string,
  hint: string,
): AnalyticsEnvelope {
  return {
    contractVersion: 1,
    scope: {},
    data: null,
    measurement: {},
    coverage: {},
    freshness: {},
    evidence: [],
    pagination: {},
    error: { code, message, hint },
  }
}

function readJsonObject(req: IncomingMessage): Promise<Record<string, unknown> | AnalyticsEnvelope> {
  return new Promise((resolve) => {
    let received = 0
    const chunks: Buffer[] = []
    let settled = false
    const complete = (value: Record<string, unknown> | AnalyticsEnvelope): void => {
      if (settled) return
      settled = true
      resolve(value)
    }
    req.on('data', (chunk: Buffer | string) => {
      if (settled) return
      // WHY buffers, not string appends: a multi-byte UTF-8 sequence split
      // across TCP chunks decodes to replacement characters when each chunk
      // is stringified alone. Accumulating raw bytes and decoding once keeps
      // the boundary intact. Well-formed string chunks encode back to the
      // same bytes, so mixed chunk kinds stay exact.
      const buf = typeof chunk === 'string' ? Buffer.from(chunk, 'utf8') : chunk
      received += buf.length
      if (received > MAX_BODY_BYTES) {
        complete(transportError('budget_exceeded', 'The request body exceeds the 65536-byte limit.', 'Reduce the request size and retry.'))
        req.resume()
        return
      }
      chunks.push(buf)
    })
    req.on('error', () => complete(transportError('invalid_request', 'The request body could not be read.', 'Send a complete JSON object.')))
    req.on('end', () => {
      if (settled) return
      try {
        const parsed: unknown = JSON.parse(chunks.length === 0 ? '{}' : Buffer.concat(chunks).toString('utf8'))
        if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
          complete(transportError('invalid_request', 'The request body must be a JSON object.', 'Send an object matching the operation catalog.'))
          return
        }
        complete(parsed as Record<string, unknown>)
      } catch {
        complete(transportError('invalid_request', 'The request body is not valid JSON.', 'Send a valid JSON object.'))
      }
    })
  })
}

function isEnvelope(value: Record<string, unknown> | AnalyticsEnvelope): value is AnalyticsEnvelope {
  return 'contractVersion' in value
}

/**
 * Serves the eight read-only analytics operations. The shared service owns all domain
 * validation and result construction; this adapter only enforces HTTP framing and maps
 * the service's stable error code to a transport status.
 */
export function handleAnalyticsRequest(
  req: IncomingMessage,
  res: ServerResponse,
  url: URL,
  service: AnalyticsService | undefined,
): boolean {
  const route = ROUTES.find((candidate) => candidate.path === url.pathname)
  if (route === undefined) return false
  if (req.method !== route.method) {
    res.writeHead(405, { allow: route.method, 'cache-control': 'no-store' })
    res.end()
    return true
  }
  if (service === undefined) {
    sendJson(res, 503, transportError('engine_unavailable', 'The analytics engine is unavailable.', 'Restart KyberDash and retry.'))
    return true
  }
  if (route.method === 'POST' && !String(req.headers['content-type'] ?? '').toLowerCase().startsWith('application/json')) {
    sendJson(res, 400, transportError('invalid_request', 'The request content type must be application/json.', 'Send a JSON request body.'))
    return true
  }

  void (async () => {
    const request = route.method === 'GET' ? {} : await readJsonObject(req)
    if (isEnvelope(request)) {
      sendJson(res, statusFor(request), request, request.error?.code === 'budget_exceeded' ? { connection: 'close' } : {})
      return
    }
    const controller = new AbortController()
    req.once('aborted', () => controller.abort())
    const envelope = await service.execute(route.operation, request, { signal: controller.signal })
    sendJson(res, statusFor(envelope), envelope)
  })().catch(() => {
    sendJson(res, 500, transportError('internal_error', 'The analytics engine could not complete the request.', 'Retry the request.'))
  })
  return true
}
