import type { IncomingMessage, ServerResponse } from 'http'
import type { KyberBridge } from './bridge.js'
import { runContextReview, type ReviewRequest } from '../analysis/review.js'
import { createReviewProvider } from '../analysis/review-providers/index.js'
import { recordPrediction } from '../analysis/calibration.js'

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

/**
 * `/api/kyber/span/:spanId/attributes` — the harness-emitted attribute map for
 * one span (R9.2). Session payloads no longer carry these inline; see
 * `CanonStore.spanAttributes`.
 */
function parseSpanAttributesPath(pathname: string): string | null {
  const prefix = '/api/kyber/span/'
  const suffix = '/attributes'
  if (!pathname.startsWith(prefix) || !pathname.endsWith(suffix)) return null
  const middle = pathname.slice(prefix.length, pathname.length - suffix.length)
  if (!middle || middle.includes('/')) return null
  return decodeURIComponent(middle).trim()
}

/**
 * `/api/kyber/session/:id/turn/:index/content` — returns unclipped assembled context
 * for a specific turn index (Task G1 / Decision D14).
 */
function parseTurnContentPath(pathname: string): { sessionId: string; turnIndex: number } | null {
  const prefix = '/api/kyber/session/'
  const suffix = '/content'
  if (!pathname.startsWith(prefix) || !pathname.endsWith(suffix)) return null
  const middle = pathname.slice(prefix.length, pathname.length - suffix.length)
  const turnMarker = '/turn/'
  const turnIdx = middle.lastIndexOf(turnMarker)
  if (turnIdx === -1) return null
  const sessionIdRaw = middle.slice(0, turnIdx)
  const turnIndexRaw = middle.slice(turnIdx + turnMarker.length)
  if (!sessionIdRaw || !turnIndexRaw) return null
  const turnIndex = parseInt(turnIndexRaw, 10)
  if (isNaN(turnIndex)) return null
  return {
    sessionId: decodeURIComponent(sessionIdRaw).trim(),
    turnIndex,
  }
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

  // One span's attributes, fetched on demand by the timeline inspector.
  const spanAttributesId = parseSpanAttributesPath(url.pathname)
  if (spanAttributesId !== null) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    if (!spanAttributesId) {
      sendKyberJson(res, 400, { error: 'Missing span id' })
      return true
    }
    const body = bridge.getSpanAttributes(spanAttributesId)
    if (!body) {
      sendKyberJson(res, 404, { error: 'Span attributes not found' })
      return true
    }
    sendKyberJson(res, 200, body)
    return true
  }

  // Full-fidelity turn context content (Task G1 / Decision D14)
  const turnContentRoute = parseTurnContentPath(url.pathname)
  if (turnContentRoute !== null) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const { sessionId, turnIndex } = turnContentRoute
    if (!sessionId) {
      sendKyberJson(res, 400, { error: 'Missing session id' })
      return true
    }
    const budgetParam = url.searchParams.get('budget')
    const budget = budgetParam ? parseInt(budgetParam, 10) : undefined
    const body = bridge.assembleTurnContent(sessionId, turnIndex, budget && !isNaN(budget) ? budget : undefined)
    if (!body) {
      sendKyberJson(res, 404, { error: 'Turn content not found' })
      return true
    }
    sendKyberJson(res, 200, body)
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
    const turnParam = url.searchParams.get('turn')
    if (turnParam !== null && turnParam.trim() !== '') {
      const turnIndex = parseInt(turnParam, 10)
      if (!isNaN(turnIndex)) {
        const budgetParam = url.searchParams.get('budget')
        const budget = budgetParam ? parseInt(budgetParam, 10) : undefined
        const turnBody = bridge.assembleTurnContent(contentSessionId, turnIndex, budget && !isNaN(budget) ? budget : undefined)
        if (!turnBody) {
          sendKyberJson(res, 404, { error: 'Turn content not found' })
          return true
        }
        sendKyberJson(res, 200, turnBody)
        return true
      }
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

  // Ranked findings endpoint (Task F3 / Decision D5 / D6)
  if (url.pathname === '/api/kyber/findings') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const runId = (url.searchParams.get('runId') ?? url.searchParams.get('run_id') ?? '').trim() || undefined
    const sessionId = (url.searchParams.get('sessionId') ?? url.searchParams.get('session_id') ?? '').trim() || undefined
    const limitParam = url.searchParams.get('limit')
    const limit = limitParam ? parseInt(limitParam, 10) : undefined

    const findings = bridge.listFindings({
      runId,
      sessionId,
      limit: limit && !isNaN(limit) ? limit : undefined,
    })
    sendKyberJson(res, 200, { findings })
    return true
  }

  // Individual finding detail endpoint
  if (url.pathname === '/api/kyber/finding' || url.pathname.startsWith('/api/kyber/finding/')) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = ''
    if (url.pathname.startsWith('/api/kyber/finding/')) {
      id = decodeURIComponent(url.pathname.slice('/api/kyber/finding/'.length)).trim()
    }
    if (!id) {
      id = (url.searchParams.get('id') ?? '').trim()
    }
    if (!id) {
      sendKyberJson(res, 400, { error: 'Missing finding id' })
      return true
    }
    const finding = bridge.getFinding(id)
    if (!finding) {
      sendKyberJson(res, 404, { error: 'Finding not found' })
      return true
    }
    sendKyberJson(res, 200, finding)
    return true
  }

  // Predictions endpoint (Task F4 / Decision D11)
  if (url.pathname === '/api/kyber/predictions') {
    if (req.method === 'GET') {
      const runId = (url.searchParams.get('runId') ?? url.searchParams.get('run_id') ?? '').trim() || undefined
      const findingId = (url.searchParams.get('findingId') ?? url.searchParams.get('finding_id') ?? '').trim() || undefined
      const scoredOnlyParam = url.searchParams.get('scoredOnly') ?? url.searchParams.get('scored_only')
      const scoredOnly = scoredOnlyParam === 'true' || scoredOnlyParam === '1'
      const limitParam = url.searchParams.get('limit')
      const limit = limitParam ? parseInt(limitParam, 10) : undefined

      const predictions = bridge.listPredictions({
        runId,
        findingId,
        scoredOnly: scoredOnly ? true : undefined,
        limit: limit && !isNaN(limit) ? limit : undefined,
      })
      sendKyberJson(res, 200, { predictions })
      return true
    }

    if (req.method === 'POST') {
      let bodyText = ''
      req.on('data', (chunk) => {
        bodyText += chunk
      })
      req.on('end', () => {
        try {
          const parsed = JSON.parse(bodyText || '{}')
          if (Array.isArray(parsed)) {
            const recorded = parsed.map((item) => {
              const rec = recordPrediction(item)
              return bridge.recordPrediction(rec)
            })
            sendKyberJson(res, 201, { predictions: recorded })
          } else {
            const rec = recordPrediction(parsed)
            const saved = bridge.recordPrediction(rec)
            sendKyberJson(res, 201, { prediction: saved })
          }
        } catch {
          sendKyberJson(res, 400, { error: 'Invalid prediction payload' })
        }
      })
      return true
    }

    sendKyberJson(res, 405, { error: 'Method Not Allowed' })
    return true
  }

  // Calibration curve and scoring summary endpoint (Task F4 / Decision D11)
  if (url.pathname === '/api/kyber/calibration') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const runId = (url.searchParams.get('runId') ?? url.searchParams.get('run_id') ?? '').trim() || undefined
    const calibration = bridge.getCalibrationSummary({ runId })
    sendKyberJson(res, 200, { calibration, ...calibration })
    return true
  }

  // Harness rollups endpoint (Task G2 / Decision D1)
  if (url.pathname === '/api/kyber/harnesses') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const harnesses = bridge.listHarnessRollups()
    sendKyberJson(res, 200, { harnesses })
    return true
  }

  // Individual harness detail endpoint
  if (url.pathname === '/api/kyber/harness' || url.pathname.startsWith('/api/kyber/harness/')) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = ''
    if (url.pathname.startsWith('/api/kyber/harness/')) {
      id = decodeURIComponent(url.pathname.slice('/api/kyber/harness/'.length)).trim()
    }
    if (!id) {
      id = (url.searchParams.get('id') ?? '').trim()
    }
    if (!id) {
      sendKyberJson(res, 400, { error: 'Missing harness id' })
      return true
    }
    const rollup = bridge.getHarnessRollup(id)
    if (!rollup) {
      sendKyberJson(res, 404, { error: 'Harness not found' })
      return true
    }
    sendKyberJson(res, 200, rollup)
    return true
  }

  // Runs endpoint (Task G2 / Decision D1 / D2)
  if (url.pathname === '/api/kyber/runs') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const harnessParam = (url.searchParams.get('harness') ?? '').trim() || undefined
    // `run` carries no finding count of its own. Without this join the table
    // renders `findingCount ?? 0` for every row — a fabricated zero sitting
    // directly beneath a findings panel listing the same runs' findings.
    const findingCounts = new Map<string, number>()
    for (const finding of bridge.listFindings()) {
      if (finding.runId === undefined) continue
      findingCounts.set(finding.runId, (findingCounts.get(finding.runId) ?? 0) + 1)
    }
    const runs = bridge.listRuns(harnessParam).map((run) => ({
      ...run,
      findingCount: findingCounts.get(run.runId) ?? 0,
    }))
    sendKyberJson(res, 200, { runs })
    return true
  }

  // Individual run detail endpoint (with execution tree, executions, and findings)
  if (url.pathname === '/api/kyber/run' || url.pathname.startsWith('/api/kyber/run/')) {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let id = ''
    if (url.pathname.startsWith('/api/kyber/run/')) {
      id = decodeURIComponent(url.pathname.slice('/api/kyber/run/'.length)).trim()
    }
    if (!id) {
      id = (url.searchParams.get('id') ?? '').trim()
    }
    if (!id) {
      sendKyberJson(res, 400, { error: 'Missing run id' })
      return true
    }
    const run = bridge.getRun(id)
    if (!run) {
      sendKyberJson(res, 404, { error: 'Run not found' })
      return true
    }
    const executionTree = bridge.getExecutionTree(id)
    const executions = bridge.listExecutions(id)
    const findings = bridge.listFindings({ runId: id })
    sendKyberJson(res, 200, { run, executionTree, executions, findings })
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

  // LLM context review endpoint (Task G5 / Decision D10)
  if (url.pathname === '/api/kyber/review') {
    if (req.method !== 'POST') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    let bodyText = ''
    req.on('data', (chunk) => {
      bodyText += chunk
    })
    req.on('end', async () => {
      try {
        const parsed = JSON.parse(bodyText || '{}')
        const request: ReviewRequest = {
          content: parsed.content || '',
          blocks: parsed.blocks,
          sessionId: parsed.sessionId,
          turnIndex: parsed.turnIndex,
          harness: parsed.harness,
          model: parsed.model,
          focus: parsed.focus,
        }
        const result = await runContextReview(request, parsed.options || {})
        sendKyberJson(res, 200, result)
      } catch {
        sendKyberJson(res, 400, { error: 'Invalid review request payload' })
      }
    })
    return true
  }

  // Review provider configuration status
  if (url.pathname === '/api/kyber/review/status') {
    if (req.method !== 'GET') {
      sendKyberJson(res, 405, { error: 'Method Not Allowed' })
      return true
    }
    const provider = createReviewProvider()
    sendKyberJson(res, 200, {
      provider: provider.name,
      isConfigured: provider.isConfigured,
    })
    return true
  }

  // Precedence guard: any unhandled /api/kyber/* route MUST return JSON 404, never SPA HTML
  if (url.pathname.startsWith('/api/kyber/') || url.pathname === '/api/kyber') {
    sendKyberJson(res, 404, { error: 'Not found' })
    return true
  }

  return false
}
