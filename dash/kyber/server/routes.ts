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

type SessionViewPayload = {
  context?: unknown
  schema?: unknown
  timeline?: unknown
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
    const payload = bridge.getSessionPayload<SessionViewPayload>(id)
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
    const payload = bridge.getSessionPayload<SessionViewPayload>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, payload.context ?? {})
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
    const payload = bridge.getSessionPayload<SessionViewPayload>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, payload.schema ?? null)
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
    const payload = bridge.getSessionPayload<SessionViewPayload>(id)
    if (!payload) {
      sendKyberJson(res, 404, { error: 'Session not found' })
      return true
    }
    sendKyberJson(res, 200, payload.timeline ?? [])
    return true
  }

  // Precedence guard: any unhandled /api/kyber/* route MUST return JSON 404, never SPA HTML
  if (url.pathname.startsWith('/api/kyber/') || url.pathname === '/api/kyber') {
    sendKyberJson(res, 404, { error: 'Not found' })
    return true
  }

  return false
}
