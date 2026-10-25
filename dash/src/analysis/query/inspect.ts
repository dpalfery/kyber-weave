/**
 * Explicit evidence inspection (`inspect`): metadata-only session detail,
 * and one bounded turn content block at a time.
 *
 * <remarks>
 * Session inspection strips turns and content so a session read cannot
 * accidentally disclose a transcript; content has its own caller-selected
 * turn/block read with a decoded budget. A missing turn block is
 * `content_unavailable`, never a neighboring turn.
 * </remarks>
 */

import { MAX_CONTENT_BYTES } from './catalog.js'
import { isFiniteNumber, isRecord, type AnalyticsSource, type ExecutionOptions, type OperationError } from './types.js'

const MAX_MANIFEST_ROWS = 100
const DEFAULT_MANIFEST_ROWS = 25

function capturedRequest(raw: Record<string, unknown>): Record<string, unknown> | null {
  if (raw.mode !== 'manifest' && raw.mode !== 'content') return null
  const target = isRecord(raw.target)
  if (target === null || (target.kind !== 'run' && target.kind !== 'session' && target.kind !== 'turn')) return null
  if (target.kind === 'run' && typeof target.runId !== 'string') return null
  if (target.kind === 'session' && typeof target.sessionId !== 'string') return null
  if (target.kind === 'turn' && (typeof target.sessionId !== 'string' || !Number.isSafeInteger(target.turnIndex) || (typeof target.turnIndex === 'number' && target.turnIndex < 0))) return null
  const representation = raw.representation ?? 'captured_parts'
  if (representation !== 'captured_parts' && representation !== 'assembled_turn') return null
  if (representation === 'assembled_turn' && target.kind !== 'turn') return null
  if (raw.byteLimit !== undefined && (!isFiniteNumber(raw.byteLimit) || !Number.isSafeInteger(raw.byteLimit) || raw.byteLimit < 1 || raw.byteLimit > MAX_CONTENT_BYTES)) return null
  if (raw.limit !== undefined && (!isFiniteNumber(raw.limit) || !Number.isSafeInteger(raw.limit) || raw.limit < 1 || raw.limit > MAX_MANIFEST_ROWS)) return null
  if (raw.cursor !== undefined && typeof raw.cursor !== 'string') return null
  return {
    mode: raw.mode,
    target,
    representation,
    ...(raw.byteLimit === undefined ? { byteLimit: MAX_CONTENT_BYTES } : { byteLimit: raw.byteLimit }),
    ...(raw.limit === undefined ? { limit: DEFAULT_MANIFEST_ROWS } : { limit: raw.limit }),
    ...(typeof raw.cursor === 'string' ? { cursor: raw.cursor } : {}),
  }
}

export type InspectSuccess = {
  data: Record<string, unknown>
  evidence: Array<Record<string, unknown>>
  error: { code: string; message: string; hint: string } | null
  coverage?: Record<string, unknown>
  pagination?: Record<string, unknown>
}

export type InspectResult =
  | { ok: true; result: InspectSuccess }
  | { ok: false; error: OperationError }

export async function executeInspect(
  source: AnalyticsSource,
  raw: Record<string, unknown>,
  options?: ExecutionOptions,
): Promise<InspectResult> {
  const captured = capturedRequest(raw)
  if (captured !== null) {
    if (source.readCapturedContext === undefined) {
      return { ok: false, error: { code: 'content_unavailable', message: 'Complete captured-context inspection is unavailable.', hint: 'Use the legacy bounded turn inspector.' } }
    }
    if (options?.signal?.aborted) {
      return { ok: false, error: { code: 'cancelled', message: 'The analytics request was cancelled.', hint: 'Retry the complete request.' } }
    }
    const result = await source.readCapturedContext(captured, options)
    const body = isRecord(result)
    if (body === null) return { ok: false, error: { code: 'content_unavailable', message: 'Captured context is unavailable.', hint: 'Retry the requested target.' } }
    if (body.error === 'cancelled') return { ok: false, error: { code: 'cancelled', message: 'The analytics request was cancelled.', hint: 'Retry the complete request.' } }
    if (body.error === 'stale_cursor') return { ok: false, error: { code: 'stale_cursor', message: 'The context cursor is stale or belongs to another request.', hint: 'Restart inspection from the first page.' } }
    if (body.error === 'budget_exceeded') return { ok: false, error: { code: 'budget_exceeded', message: 'The context reader is at capacity.', hint: 'Finish or retry an existing inspection shortly.' } }
    const data = isRecord(body.data)
    const coverage = isRecord(body.coverage)
    const pagination = isRecord(body.pagination)
    return {
      ok: true,
      result: {
        data: data ?? {},
        evidence: [],
        error: null,
        ...(coverage === null ? {} : { coverage }),
        ...(pagination === null ? {} : { pagination }),
      },
    }
  }
  if (raw.kind === 'turn' && raw.includeContent === true && typeof raw.sessionId === 'string' && Number.isInteger(raw.turnIndex) && typeof raw.blockId === 'string') {
    const page = await source.readTurnPage({
      sessionId: raw.sessionId,
      turnIndex: raw.turnIndex as number,
      blockId: raw.blockId,
      byteLimit: Math.min(isFiniteNumber(raw.byteLimit) ? raw.byteLimit : MAX_CONTENT_BYTES, MAX_CONTENT_BYTES),
      ...(typeof raw.cursor === 'string' ? { cursor: raw.cursor } : {}),
    })
    if (isRecord(page)?.kind === 'stale_cursor') {
      return { ok: false, error: { code: 'stale_cursor', message: 'The content cursor belongs to a different projection generation.', hint: 'Restart inspection from the first page.' } }
    }
    if (page === undefined) {
      return { ok: false, error: { code: 'content_unavailable', message: 'The selected content block is unavailable.', hint: 'Inspect an existing turn and block identifier.' } }
    }
    return {
      ok: true,
      result: {
        data: { content: isRecord(page) ?? {} },
        evidence: [{ sessionId: raw.sessionId, turnIndex: raw.turnIndex as number, blockId: raw.blockId }],
        error: null,
      },
    }
  }
  if (raw.kind === 'session' && typeof raw.id === 'string') {
    const detail = await source.readSession(raw.id)
    // Detail inspection is intentionally metadata-only. Content has a separate,
    // caller-selected turn/block read so a session inspection cannot accidentally
    // disclose an entire transcript.
    const safeDetail = isRecord(detail)
    if (safeDetail !== null) {
      delete safeDetail.turns
      delete safeDetail.content
    }
    return {
      ok: true,
      result: {
        data: safeDetail ?? {},
        evidence: [{ sessionId: raw.id }],
        error: detail === undefined ? { code: 'not_found', message: 'The requested session was not found.', hint: 'Use search-sessions to discover session identifiers.' } : null,
      },
    }
  }
  return { ok: false, error: { code: 'invalid_request', message: 'Inspection requires an explicit target.', hint: 'Specify a session or a bounded turn content block.' } }
}
