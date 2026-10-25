import { analyticsTool } from './tools.js'
import { resolveAnalyticsServerUrl, validateAnalyticsServerUrl } from './discovery.js'

export { resolveAnalyticsServerUrl } from './discovery.js'

const MAX_REPLY_BYTES = 65_536
const REQUEST_TIMEOUT_MS = 10_000

type AnalyticsEnvelope = {
  contractVersion: 1
  scope: Record<string, unknown>
  data: unknown | null
  measurement: Record<string, unknown>
  coverage: Record<string, unknown>
  freshness: Record<string, unknown>
  evidence: readonly Record<string, unknown>[]
  pagination: Record<string, unknown>
  error: { code: string; message: string; hint?: string } | null
}

export type AnalyticsToolResult = { content: Array<{ type: 'text'; text: string }>; structuredContent: AnalyticsEnvelope; isError?: boolean }

function isRecord(value: unknown): Record<string, unknown> | null { return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : null }

const STABLE_PREFIX = /^(engine_unavailable|incompatible_engine|budget_exceeded|invalid_request|cancelled|internal_error):/

function toStableError(error: unknown, code: 'engine_unavailable' | 'incompatible_engine'): Error {
  if (error instanceof Error && STABLE_PREFIX.test(error.message)) return error
  const detail = error instanceof Error ? error.message : String(error)
  return new Error(`${code}: the analytics engine request failed (${detail}).`, { cause: error })
}

/**
 * Maps MCP-friendly arguments onto the engine analytics contract. The MCP
 * surface names search text and inspection targets for discovery; the engine
 * speaks `query` and explicit `kind` discriminators. Engine-shaped bodies
 * pass through untouched so the HTTP fixture and engine callers share one path.
 *
 * `windowDays` is an MCP-surface convenience, not engine contract: every tool
 * that advertises it gets it translated into the request scope window here,
 * so the engine never sees the key. An explicit scope range wins over the
 * convenience window.
 */
function applyWindowDays(out: Record<string, unknown>, now: () => Date): void {
  if (!('windowDays' in out)) return
  const windowDays = out['windowDays']
  delete out['windowDays']
  // WHY reject here instead of dropping: the MCP schema already constrains
  // this range, but HTTP and test callers bypass schemas. Silently ignoring
  // a malformed window would send an unscopable request the caller never
  // meant; the error names the valid range so it stays actionable.
  if (windowDays === undefined) return
  if (typeof windowDays !== 'number' || !Number.isSafeInteger(windowDays) || windowDays < 1 || windowDays > 365) {
    throw new Error('invalid_request: windowDays must be an integer between 1 and 365.')
  }
  const end = now()
  const start = new Date(end.getTime() - windowDays * 86_400_000)
  const scope = isRecord(out['scope']) ?? {}
  const translated: Record<string, unknown> = { ...scope }
  if (translated['from'] === undefined) translated['from'] = start.toISOString()
  if (translated['to'] === undefined) translated['to'] = end.toISOString()
  out['scope'] = translated
}

function toEngineBody(name: string, args: unknown, now: () => Date): Record<string, unknown> {
  const raw = isRecord(args) ?? {}
  if (name === 'dash_search_sessions') {
    if (typeof raw['query'] === 'string') {
      const out: Record<string, unknown> = { ...raw }
      applyWindowDays(out, now)
      return out
    }
    const { text, ...rest } = raw
    const out: Record<string, unknown> = typeof text === 'string' ? { ...rest, query: text } : { ...raw }
    applyWindowDays(out, now)
    return out
  }
  if (name === 'dash_inspect') {
    // Complete-context requests already are the engine contract. In particular,
    // do not reinterpret their target or cursor: both are opaque evidence handles.
    if (typeof raw['mode'] === 'string' || 'target' in raw) return { ...raw }
    if (typeof raw['kind'] === 'string') return { ...raw }
    const { sessionId, ...rest } = raw
    if (raw['includeContent'] === true && typeof sessionId === 'string' && Number.isInteger(raw['turnIndex']) && typeof raw['blockId'] === 'string') {
      return { ...rest, kind: 'turn', sessionId, turnIndex: raw['turnIndex'], blockId: raw['blockId'], includeContent: true }
    }
    if (typeof sessionId === 'string') return { ...rest, kind: 'session', id: sessionId }
    return { ...raw }
  }
  if (name === 'dash_query') {
    const out: Record<string, unknown> = { ...raw }
    if (Array.isArray(out['metrics']) && (out['metrics'] as unknown[]).every((metric) => typeof metric === 'string')) {
      out['metrics'] = (out['metrics'] as string[]).map((id) => ({ id }))
    }
    if (Array.isArray(out['groups'])) {
      out['groupBy'] = out['groups']
      delete out['groups']
    }
    applyWindowDays(out, now)
    if (out['catalogVersion'] === undefined) out['catalogVersion'] = 1
    return out
  }
  if (name === 'dash_findings' || name === 'dash_outcomes') {
    const out: Record<string, unknown> = { ...raw }
    applyWindowDays(out, now)
    return out
  }
  return { ...raw }
}

function validateEnvelope(value: unknown): AnalyticsEnvelope {
  const envelope = isRecord(value)
  if (envelope === null || envelope.contractVersion !== 1 || isRecord(envelope.scope) === null || !('data' in envelope) || isRecord(envelope.measurement) === null || isRecord(envelope.coverage) === null || isRecord(envelope.freshness) === null || !Array.isArray(envelope.evidence) || isRecord(envelope.pagination) === null || (envelope.error !== null && isRecord(envelope.error) === null)) {
    throw new Error('incompatible_engine: the discovered server does not provide analytics contract version 1.')
  }
  return envelope as AnalyticsEnvelope
}

function textResult(envelope: AnalyticsEnvelope): AnalyticsToolResult {
  const text = JSON.stringify(envelope)
  if (Buffer.byteLength(text, 'utf8') > MAX_REPLY_BYTES) {
    const bounded = { ...envelope, data: null, error: { code: 'budget_exceeded', message: 'The analytics result exceeded the 64 KiB MCP response limit.', hint: 'Narrow the request or use its cursor.' } }
    return { content: [{ type: 'text', text: JSON.stringify(bounded) }], structuredContent: bounded, isError: true }
  }
  return { content: [{ type: 'text', text }], structuredContent: envelope, ...(envelope.error === null ? {} : { isError: true }) }
}

export function createAnalyticsClient(options: { serverUrl?: string; stateDir?: string; configDir?: string; fetchImpl?: typeof fetch; now?: () => Date } = {}) {
  const explicitUrl = options.serverUrl === undefined ? undefined : validateAnalyticsServerUrl(options.serverUrl)
  const fetchImpl = options.fetchImpl ?? fetch
  const now = options.now ?? (() => new Date())
  let discoveredUrl: string | undefined

  async function endpoint(): Promise<string> {
    if (explicitUrl !== undefined) return explicitUrl
    if (discoveredUrl !== undefined) return discoveredUrl
    discoveredUrl = await resolveAnalyticsServerUrl(options)
    return discoveredUrl
  }

  async function request(name: string, args: unknown, signal?: AbortSignal): Promise<AnalyticsToolResult> {
    const tool = analyticsTool(name)
    if (tool === undefined) throw new Error(`invalid_request: unknown analytics tool ${name}.`)
    const url = await endpoint()
    const timeout = new AbortController()
    const timer = setTimeout(() => timeout.abort(), REQUEST_TIMEOUT_MS)
    const combined = signal === undefined ? timeout.signal : AbortSignal.any([signal, timeout.signal])
    const engineBody = tool.method === 'POST' ? toEngineBody(name, args, now) : undefined
    try {
      let response: Response
      try {
        response = await fetchImpl(`${url}/api/kyber/analytics/${tool.operation}`, {
          method: tool.method,
          redirect: 'error',
          headers: tool.method === 'POST' ? { 'content-type': 'application/json' } : undefined,
          ...(tool.method === 'POST' ? { body: JSON.stringify(engineBody) } : {}),
          signal: combined,
        })
      } catch (error: unknown) {
        if (signal?.aborted) return textResult({ contractVersion: 1, scope: {}, data: null, measurement: {}, coverage: {}, freshness: {}, evidence: [], pagination: {}, error: { code: 'cancelled', message: 'The analytics request was cancelled.' } })
        if (timeout.signal.aborted && !(signal?.aborted)) throw new Error('engine_unavailable: the analytics engine request timed out.', { cause: error })
        throw toStableError(error, 'engine_unavailable')
      }
      let rawText: string
      try {
        const body = await response.arrayBuffer()
        if (body.byteLength > MAX_REPLY_BYTES) throw new Error('budget_exceeded: engine response exceeded 64 KiB.')
        rawText = new TextDecoder().decode(body)
      } catch (error: unknown) {
        if (signal?.aborted) return textResult({ contractVersion: 1, scope: {}, data: null, measurement: {}, coverage: {}, freshness: {}, evidence: [], pagination: {}, error: { code: 'cancelled', message: 'The analytics request was cancelled.' } })
        throw toStableError(error, 'engine_unavailable')
      }
      let parsed: unknown
      try {
        parsed = JSON.parse(rawText) as unknown
      } catch (error: unknown) {
        throw toStableError(error, 'incompatible_engine')
      }
      const envelope = validateEnvelope(parsed)
      if (!response.ok && envelope.error === null) throw new Error(`engine_unavailable: analytics HTTP status ${response.status}.`)
      return textResult(envelope)
    } catch (error: unknown) {
      if (signal?.aborted) return textResult({ contractVersion: 1, scope: {}, data: null, measurement: {}, coverage: {}, freshness: {}, evidence: [], pagination: {}, error: { code: 'cancelled', message: 'The analytics request was cancelled.' } })
      throw error
    } finally { clearTimeout(timer) }
  }

  return { callTool: (name: string, args: unknown, options?: { signal?: AbortSignal }) => request(name, args, options?.signal) }
}
