import type { IncomingMessage, ServerResponse } from 'http'
import type { KyberBridge } from './bridge.js'

/**
 * `/api/kyber/session/:id/content` — everything between the prefix and the
 * `/content` suffix is the session id, so encoded ids stay intact. `null`
 * means this is not the content route (so `/session/content` is not treated
 * as an empty id).
 */
function parseSessionContentPath(pathname: string): string | null {
  const prefix = '/api/kyber/session/'
  const suffix = '/content'
  if (!pathname.startsWith(prefix) || !pathname.endsWith(suffix)) return null
  const middle = pathname.slice(prefix.length, pathname.length - suffix.length)
  if (!middle || middle.endsWith('/')) return null
  return decodeURIComponent(middle).trim()
}

function sendKyberJson(res: ServerResponse, status: number, body: unknown): void {
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
  })
  res.end(JSON.stringify(body))
}

export function handleKyberRequest(
  req: IncomingMessage,
  res: ServerResponse,
  url: URL,
  bridge: KyberBridge,
): boolean {
  if (!url.pathname.startsWith('/api/kyber/') && url.pathname !== '/api/kyber') {
    return false
  }

  // Kyber agent session analysis endpoints
  if (url.pathname === '/api/kyber/sessions') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const limitParam = url.searchParams.get('limit')
    const limit = limitParam ? parseInt(limitParam, 10) : undefined
    let sessions = bridge.listSessions(limit && !isNaN(limit) ? limit : undefined)
    const harnessParam = url.searchParams.get('harness')
    if (harnessParam) {
      sessions = sessions.filter((s) => s.harness?.toLowerCase() === harnessParam.toLowerCase())
    }
    sendKyberJson(res, 200, { sessions })
    return true
  }

  // Full-fidelity content. Must run before /session/:id — that handler would
  // otherwise treat ".../content" as part of the session id and 404.
  const contentSessionId = parseSessionContentPath(url.pathname)
  if (contentSessionId !== null) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    if (!contentSessionId) {
      sendKyberJson(res, 400, { error: 'Missing session id' })
      return true
    }
    const span = (url.searchParams.get('span') ?? '').trim() || undefined
    const part = (url.searchParams.get('part') ?? '').trim() || undefined
    const body = bridge.getSessionContent(contentSessionId, { spanId: span, part })
    if (!body) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, body)
    return true
  }

  if (url.pathname === '/api/kyber/session' || url.pathname.startsWith('/api/kyber/session/')) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = ''
    if (url.pathname.startsWith('/api/kyber/session/')) {
      id = decodeURIComponent(url.pathname.slice('/api/kyber/session/'.length)).trim()
    }
    if (!id) {
      id = (url.searchParams.get('id') ?? '').trim()
    }
    if (!id) {
      sendKyberJson(res, 400, { error: 'Missing session id' })
      return true
    }
    const payload = bridge.getSessionPayload<Record<string, any>>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, payload)
    return true
  }

  if (url.pathname === '/api/kyber/compare') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const table = bridge.getComparisonTable()
    sendKyberJson(res, 200, table)
    return true
  }

  if (url.pathname === '/api/kyber/quarantine') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const limitParam = url.searchParams.get('limit')
    const limit = limitParam ? parseInt(limitParam, 10) : undefined
    const entries = bridge.getQuarantine(limit && !isNaN(limit) ? limit : 200)
    sendKyberJson(res, 200, { entries })
    return true
  }

  if (url.pathname === '/api/kyber/problems') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const limitParam = url.searchParams.get('limit')
    const limit = limitParam ? parseInt(limitParam, 10) : undefined
    const problems = bridge.getProblems(limit && !isNaN(limit) ? limit : 200)
    sendKyberJson(res, 200, { problems })
    return true
  }

  if (url.pathname === '/api/kyber/meta') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const meta = bridge.getMeta()
    sendKyberJson(res, 200, meta)
    return true
  }

  // Backward-compatible endpoints for older/legacy callers
  if (url.pathname === '/api/kyber/context') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = (url.searchParams.get('id') ?? '').trim()
    if (!id) {
      const list = bridge.listSessions(1)
      if (list.length === 0) {
        sendKyberJson(res, 404, { error: 'No sessions available' })
        return true
      }
      id = list[0].session_id
    }
    const payload = bridge.getSessionPayload<Record<string, any>>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    const rawContext = payload.context ?? {}
    const isMeasurable = Boolean(payload.turns && payload.turns.length > 0 && payload.harness === 'copilot')
    const contextAnalysis = {
      ...rawContext,
      measurable: rawContext.measurable ?? isMeasurable,
      contextLimit: rawContext.contextLimit ?? 200_000,
      turns: rawContext.turns ?? (payload.turns ? payload.turns.map((t: any) => ({
        index: t.index,
        buckets: t.buckets ?? {
          system_prompt: t.system_prompt ?? 0,
          tool_definitions: t.tool_definitions ?? 0,
          instruction_context: t.instruction_context ?? 0,
          conversation_history: t.conversation_history ?? 0,
          tool_result_content: t.tool_result_content ?? 0,
        },
        toolDefinitionsByServer: {},
        builtinToolDefinitionTokens: 0,
        strippedInstructionBlocks: { count: 0, tokens: 0 },
        bucketedTokens: (t.input ?? 0) - (t.fresh ?? 0),
        residual: { tokens: 0, attribution: 'tokenizer_drift' as const },
        headroom: 200_000 - (t.input ?? 0),
        pressure: (t.input ?? 0) / 200_000,
        accumulationRate: t.fresh ?? 0,
        freshInput: t.fresh ?? 0,
      })) : []),
      residualTotal: rawContext.residualTotal ?? 0,
      derivedCounts: rawContext.derivedCounts ?? true,
      freshJumpFactor: rawContext.freshJumpFactor ?? 1.5,
      flaggedTurns: rawContext.flaggedTurns ?? [],
      sessionAccumulationRate: rawContext.sessionAccumulationRate ?? 0,
      derivedModel: rawContext.derivedModel ?? 'o200k_base',
      reason: rawContext.reason ?? (!isMeasurable ? 'declared_not_measurable' : undefined),
    }
    sendKyberJson(res, 200, contextAnalysis)
    return true
  }

  if (url.pathname === '/api/kyber/schema') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = (url.searchParams.get('id') ?? '').trim()
    if (!id) {
      const list = bridge.listSessions(1)
      if (list.length === 0) {
        sendKyberJson(res, 404, { error: 'No sessions available' })
        return true
      }
      id = list[0].session_id
    }
    const payload = bridge.getSessionPayload<Record<string, any>>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    const tools = Array.isArray(payload.tools) ? payload.tools : []
    const turnsCount = payload.summary?.turn_count ?? payload.turns?.length ?? 0
    const measurable = tools.length > 0
    const ranked = tools.map((t: any) => ({
      name: t.name,
      server: t.server ?? 'built-in',
      cost: t.total_schema_cost ?? t.schema_tokens ?? 0,
      invoked: (t.invocations ?? 0) > 0,
    }))
    const neverInvoked = ranked.filter((t: any) => !t.invoked)
    const byServer: Record<string, number> = {}
    for (const t of tools) {
      const s = t.server ?? 'built-in'
      byServer[s] = (byServer[s] ?? 0) + (t.total_schema_cost ?? t.schema_tokens ?? 0)
    }
    const unusedResidencies = tools
      .filter((t: any) => (t.invocations ?? 0) === 0)
      .reduce((sum: number, t: any) => sum + (t.total_schema_cost ?? 0), 0)

    const schemaAnalysis = measurable
      ? {
          measurable: true,
          ranked,
          neverInvoked,
          byServer,
          unusedRange: {
            tokenResidencies: unusedResidencies,
            floor: 0,
            ceiling: unusedResidencies,
            currency: 'USD',
          },
          turns: turnsCount,
          derived: true,
          derivedModel: 'o200k_base',
          tools,
        }
      : {
          measurable: false,
          invocationCount: payload.summary?.tool_count ?? 0,
          reason: 'declared_not_measurable' as const,
          tools: [],
        }
    sendKyberJson(res, 200, schemaAnalysis)
    return true
  }

  if (url.pathname === '/api/kyber/timeline') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = (url.searchParams.get('id') ?? '').trim()
    if (!id) {
      const list = bridge.listSessions(1)
      if (list.length === 0) {
        sendKyberJson(res, 404, { error: 'No sessions available' })
        return true
      }
      id = list[0].session_id
    }
    const payload = bridge.getSessionPayload<Record<string, any>>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    const ensureNodeCost = (node: any): any => {
      if (!node || typeof node !== 'object') return node
      const cost = node.cost ?? {
        basis: 'published_rates',
        status: 'ok',
        currency: 'USD',
      }
      const children = Array.isArray(node.children) ? node.children.map(ensureNodeCost) : []
      return {
        ...node,
        cost,
        children,
        attributes: node.attributes ?? {},
        durationMs: Number(node.durationMs) || 0,
      }
    }

    const rawTimeline = payload.timeline
    let rootNode: any
    if (rawTimeline && !Array.isArray(rawTimeline) && typeof rawTimeline === 'object' && rawTimeline.spanId) {
      rootNode = ensureNodeCost(rawTimeline)
    } else {
      const children = (Array.isArray(rawTimeline) ? rawTimeline : []).map(ensureNodeCost)
      rootNode = {
        spanId: payload.id ?? 'root',
        parentId: null,
        children,
        startMs: 0,
        durationMs: payload.summary?.duration_ms ?? 0,
        kind: 'session',
        name: payload.label ?? payload.harness ?? 'Session Timeline',
        attributes: payload.summary ?? {},
        isSubagent: Boolean(payload.is_subagent),
        isAuxiliary: false,
        cost: {
          basis: payload.summary?.cost?.basis ?? 'published_rates',
          status: payload.summary?.cost?.status ?? 'ok',
          value: payload.summary?.cost?.usd,
          currency: 'USD',
        },
      }
    }
    sendKyberJson(res, 200, rootNode)
    return true
  }

  // Precedence guard: any unhandled /api/kyber/* route MUST return JSON 404, never SPA HTML
  if (url.pathname.startsWith('/api/kyber/') || url.pathname === '/api/kyber') {
    sendKyberJson(res, 404, { error: 'Not found' })
    return true
  }

  return false
}
