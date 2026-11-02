import { sumSessionFigures, type KyberBridge } from '../../server/bridge.js'
import type { AnalyticsSource, ExecutionOptions } from './service.js'

/**
 * The legacy turn-block cursor is sealed against its target: generation and
 * byte offset plus the exact session, turn, block and byte limit the first
 * page was read with. Replaying it against a different block, turn, session
 * or limit reads as stale instead of serving another target's bytes at the
 * old offset.
 */
type TurnPageBinding = { sessionId: string; turnIndex: number; blockId: string; byteLimit: number }

function pageOffset(cursor: string | undefined, generation: string, binding: TurnPageBinding): number | 'stale' {
  if (cursor === undefined) return 0
  try {
    const value: unknown = JSON.parse(Buffer.from(cursor, 'base64url').toString('utf8'))
    if (typeof value !== 'object' || value === null || Array.isArray(value)) return 'stale'
    const page = value as { v?: unknown; g?: unknown; o?: unknown; sessionId?: unknown; turnIndex?: unknown; blockId?: unknown; byteLimit?: unknown }
    if (page.v !== 1 || page.g !== generation) return 'stale'
    if (page.o === undefined || !Number.isSafeInteger(page.o) || typeof page.o !== 'number' || page.o < 0) return 'stale'
    if (page.sessionId !== binding.sessionId || page.turnIndex !== binding.turnIndex || page.blockId !== binding.blockId || page.byteLimit !== binding.byteLimit) return 'stale'
    return page.o
  } catch {
    return 'stale'
  }
}

function pageCursor(offset: number, generation: string, binding: TurnPageBinding): string {
  return Buffer.from(JSON.stringify({ v: 1, g: generation, o: offset, sessionId: binding.sessionId, turnIndex: binding.turnIndex, blockId: binding.blockId, byteLimit: binding.byteLimit }), 'utf8').toString('base64url')
}

/**
 * Adapts the canonical projection to the analytics source seam. The bridge is
 * intentionally the only database owner here: analysis never opens a second
 * store, reprojects, or reaches raw records for aggregate figures.
 */
export function createCanonAnalyticsSource(bridge: KyberBridge, options: { projectionGeneration?: () => string } = {}): AnalyticsSource {
  return {
    async status() {
      return { capabilities: ['status', 'catalog', 'query', 'search-sessions', 'inspect', 'findings', 'compare', 'outcomes'] }
    },
    async readCatalog() { return { version: 1 } },
    async candidateSessionIds(scope, cap) {
      return bridge.analyticsSessionIds(scope, cap)
    },
    async readSessionSummaries(ids) {
      return bridge.analyticsSessionProjections(ids)
    },
    async readSession(id) {
      return bridge.getSessionSummary(id)
    },
    async readTurnPage({ sessionId, turnIndex, blockId, byteLimit, cursor }) {
      const generation = options.projectionGeneration?.() ?? 'unversioned'
      const binding: TurnPageBinding = { sessionId, turnIndex, blockId, byteLimit }
      const offset = pageOffset(cursor, generation, binding)
      if (offset === 'stale') return { kind: 'stale_cursor' }
      const page = await bridge.analyticsTurnBlock({ sessionId, turnIndex, blockKey: blockId, byteLimit, offset })
      if (page === undefined) return undefined
      return { text: page.text, byteLength: page.byteLength, nextCursor: page.nextOffset === null ? null : pageCursor(page.nextOffset, generation, binding) }
    },
    async readCapturedContext(request, execution?: ExecutionOptions) {
      return bridge.analyticsCapturedContext({
        ...request,
        instanceId: 'analytics-source',
        projectionGeneration: options.projectionGeneration?.() ?? 'unversioned',
      }, execution?.signal === undefined ? undefined : { signal: execution.signal })
    },
    async readFindings(scope) {
      return bridge.listFindings({
        ...(scope.runId !== undefined ? { runId: scope.runId } : {}),
        ...(scope.sessionId !== undefined ? { sessionId: scope.sessionId } : {}),
        ...(scope.harness !== undefined ? { harness: scope.harness } : {}),
        limit: 100,
      }) as unknown as Record<string, unknown>[]
    },
    async readRuns(scope) {
      const runs = bridge.listRuns(scope.harness)
      if (runs.length === 0) return []
      // One executions read and one figures read for the whole set: per-run
      // figure queries here would be an N+1 over the run list.
      const sessionsByRun = new Map<string, string[]>()
      for (const execution of bridge.listExecutions()) {
        if (typeof execution.sessionId !== 'string' || execution.sessionId.length === 0) continue
        const list = sessionsByRun.get(execution.runId) ?? []
        list.push(execution.sessionId)
        sessionsByRun.set(execution.runId, list)
      }
      const figures = bridge.sessionSummaryFigures([...new Set([...sessionsByRun.values()].flat())])
      return runs.map((run) => {
        const measured = sumSessionFigures(figures, sessionsByRun.get(run.runId) ?? [])
        return { ...run, inputTokens: measured.totalInput ?? null } as unknown as Record<string, unknown>
      })
    },
    async readOutcomes(scope) {
      // Observed outcomes only: a run whose projection carries no outcome
      // block stays absent — never inferred from findings or completion.
      const requested = typeof scope.runId === 'string' && scope.runId.length > 0
        ? [...new Set(scope.runId.split(',').map((id) => id.trim()).filter((id) => id.length > 0))].slice(0, 100)
        : undefined
      const runs = requested !== undefined
        ? requested.flatMap((id) => {
          const run = bridge.getRun(id)
          return run === undefined ? [] : [run]
        })
        : bridge.listRuns(scope.harness).slice(0, 100)
      return runs.flatMap((run) => {
        if (typeof run.outcome !== 'object' || run.outcome === null) return []
        const status = (run.outcome as { status?: unknown }).status
        if (typeof status !== 'string') return []
        return [{ runId: run.runId, ...(run.outcome as unknown as Record<string, unknown>) }]
      })
    },
    async freshness() {
      return { recordedAt: null, ingestAt: null, projectionAt: null, storeGeneration: null, reason: 'Projection freshness is not recorded by this bridge.' }
    },
  }
}
