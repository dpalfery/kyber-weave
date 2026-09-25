// Cursor session content reader.
//
// Reads SQLite databases (and JSON fixtures) produced by Cursor and extracts
// input-side turn snapshots. Retains available user prompt, instructions
// (agentKv system blobs), and tool context (agentKv tool blobs) without
// claiming a complete multi-turn conversation prefix.

import { existsSync, readFileSync, statSync } from 'node:fs'

import type { ContentPart } from '../../canon/types.js'
import { blobToText, isSqliteAvailable, openDatabase } from '../../ingest/sqlite.js'
import {
  decodeSourcePath,
  getCursorTimeFloor,
  getCursorComposerFilter,
  loadAgentStreams,
  loadCursorBubbles,
  parseComposerIdFromKey,
} from '../../providers/cursor.js'
import type { DateRange } from '../../types.js'
import type { ContentReader, ReaderTurn } from './types.js'

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function readFromJsonFile(filePath: string): ReaderTurn[] {
  let parsed: unknown
  try {
    parsed = JSON.parse(readFileSync(filePath, 'utf-8'))
  } catch {
    return []
  }

  // Virtual turns array [{ sessionId, turnId, model, ... }]
  if (Array.isArray(parsed)) {
    return parsed.filter(isRecord).map((item) => {
      const sessionId = typeof item['sessionId'] === 'string' ? item['sessionId'] : undefined
      const turnId = typeof item['turnId'] === 'string' ? item['turnId'] : undefined
      const contextWindow = typeof item['contextWindow'] === 'number' ? item['contextWindow'] : undefined
      return {
        parts: [],
        ...(sessionId ? { sessionId } : {}),
        ...(turnId ? { nativeRecordId: turnId } : {}),
        ...(contextWindow !== undefined ? { contextWindow } : {}),
      }
    })
  }

  // Object with { sessionId, requestId, contextWindow, rows: [...] }
  if (isRecord(parsed) && Array.isArray(parsed['rows'])) {
    const rootSessionId = typeof parsed['sessionId'] === 'string' ? parsed['sessionId'] : undefined
    const rootRequestId = typeof parsed['requestId'] === 'string' ? parsed['requestId'] : undefined
    const rootContextWindow = typeof parsed['contextWindow'] === 'number' ? parsed['contextWindow'] : undefined

    type Row = { key: string; value: Record<string, unknown> }
    const rows = parsed['rows'].filter((r): r is Row => isRecord(r) && typeof r['key'] === 'string' && isRecord(r['value']))

    const requestToComposer = new Map<string, string>()
    const bubbleUserText = new Map<string, string>()
    const requestContextWindow = new Map<string, number>()

    for (const r of rows) {
      if (r.key.startsWith('bubbleId:')) {
        const cid = parseComposerIdFromKey(r.key)
        const reqId = typeof r.value['requestId'] === 'string' ? r.value['requestId'] : undefined
        if (cid && reqId) requestToComposer.set(reqId, cid)
        if (typeof r.value['contextWindow'] === 'number') {
          if (reqId) requestContextWindow.set(reqId, r.value['contextWindow'])
        }
        if (reqId && r.value['type'] === 1 && typeof r.value['text'] === 'string') {
          bubbleUserText.set(reqId, r.value['text'])
        }
      }
    }

    const instructionsByRequest = new Map<string, string[]>()
    const userByRequest = new Map<string, string[]>()
    const toolsByRequest = new Map<string, string[]>()

    let currentRequestId: string | null = null
    const pendingInstructions: string[] = []
    const pendingUser: string[] = []

    for (const r of rows) {
      if (r.key.startsWith('agentKv:blob:')) {
        const providerOptions = isRecord(r.value['providerOptions']) ? r.value['providerOptions'] : undefined
        const cursorOpt = providerOptions && isRecord(providerOptions['cursor']) ? providerOptions['cursor'] : undefined
        const reqId = cursorOpt && typeof cursorOpt['requestId'] === 'string' ? cursorOpt['requestId'] : undefined
        if (reqId) {
          currentRequestId = reqId
          if (pendingInstructions.length > 0) {
            const list = instructionsByRequest.get(currentRequestId) ?? []
            list.push(...pendingInstructions)
            instructionsByRequest.set(currentRequestId, list)
            pendingInstructions.length = 0
          }
          if (pendingUser.length > 0) {
            const list = userByRequest.get(currentRequestId) ?? []
            list.push(...pendingUser)
            userByRequest.set(currentRequestId, list)
            pendingUser.length = 0
          }
        }

        const role = r.value['role']
        const content = r.value['content']
        const texts: string[] = []
        if (typeof content === 'string') texts.push(content)
        else if (Array.isArray(content)) {
          for (const block of content) {
            if (isRecord(block) && typeof block['text'] === 'string') texts.push(block['text'])
          }
        }

        if (role === 'system') {
          if (currentRequestId) {
            const list = instructionsByRequest.get(currentRequestId) ?? []
            list.push(...texts)
            instructionsByRequest.set(currentRequestId, list)
          } else {
            pendingInstructions.push(...texts)
          }
        } else if (role === 'user') {
          if (currentRequestId) {
            const list = userByRequest.get(currentRequestId) ?? []
            list.push(...texts)
            userByRequest.set(currentRequestId, list)
          } else {
            pendingUser.push(...texts)
          }
        } else if (role === 'tool' && currentRequestId) {
          const list = toolsByRequest.get(currentRequestId) ?? []
          list.push(...texts)
          toolsByRequest.set(currentRequestId, list)
        }
      }
    }

    const allRequestIds = new Set<string>([
      ...(rootRequestId ? [rootRequestId] : []),
      ...requestToComposer.keys(),
      ...instructionsByRequest.keys(),
      ...userByRequest.keys(),
      ...toolsByRequest.keys(),
    ])

    const turns: ReaderTurn[] = []
    for (const reqId of allRequestIds) {
      const parts: ContentPart[] = []
      let order = 0

      for (const text of instructionsByRequest.get(reqId) ?? []) {
        if (text.trim() !== '') parts.push({ part: 'instruction_context', text, order: order++ })
      }
      for (const text of userByRequest.get(reqId) ?? []) {
        if (text.trim() !== '') parts.push({ part: 'conversation_history', text, order: order++ })
      }
      if (!userByRequest.has(reqId) && bubbleUserText.has(reqId)) {
        const text = bubbleUserText.get(reqId)!
        if (text.trim() !== '') parts.push({ part: 'conversation_history', text, order: order++ })
      }
      for (const text of toolsByRequest.get(reqId) ?? []) {
        if (text.trim() !== '') parts.push({ part: 'tool_result_content', text, order: order++ })
      }

      const sessionId = requestToComposer.get(reqId) ?? rootSessionId ?? reqId
      const contextWindow = requestContextWindow.get(reqId) ?? rootContextWindow

      turns.push({
        parts,
        sessionId,
        nativeRecordId: reqId,
        ...(contextWindow !== undefined ? { contextWindow } : {}),
      })
    }
    return turns
  }

  return []
}

type CachedTurn = { composerId: string | null; turn: ReaderTurn }
type CachedRead = { fingerprint: string; timeFloor: string; timeCeiling?: string; turns: CachedTurn[] }
const sqliteReads = new Map<string, CachedRead>()

function sqliteFingerprint(dbPath: string): string {
  const db = statSync(dbPath)
  let wal = ''
  try {
    const stat = statSync(`${dbPath}-wal`)
    wal = `${stat.mtimeMs}:${stat.size}`
  } catch {
    // A database without a WAL is ordinary; the main file still fingerprints it.
  }
  return `${db.mtimeMs}:${db.size}:${wal}`
}

function readFromSqlite(dbPath: string, timeFloor: string, timeCeiling?: string): CachedTurn[] | null {
  if (!isSqliteAvailable()) return null

  let db: ReturnType<typeof openDatabase>
  try {
    db = openDatabase(dbPath)
  } catch {
    return null
  }

  try {
    let bubbleRows: ReturnType<typeof loadCursorBubbles>['rows']
    try {
      bubbleRows = loadCursorBubbles(db, timeFloor, timeCeiling).rows
    } catch {
      return null
    }

    const requestToComposer = new Map<string, string>()
    const bubbleUserText = new Map<string, string>()
    const requestContextWindow = new Map<string, number>()

    for (const row of bubbleRows) {
      const cid = parseComposerIdFromKey(row.bubble_key)
      if (cid && row.request_id) {
        requestToComposer.set(row.request_id, cid)
      }
      if (typeof row.context_window === 'number') {
        if (row.request_id) requestContextWindow.set(row.request_id, row.context_window)
      }
      if (row.request_id && row.bubble_type === 1 && row.user_text) {
        const text = blobToText(row.user_text)
        if (text) {
          bubbleUserText.set(row.request_id, text)
        }
      }
    }

    const { unjoined, byRequest } = loadAgentStreams(db, requestToComposer, {
      retainContent: true,
    })

    const allRequestIds = [...requestToComposer.keys()]
    // agentKv has no timestamp; the provider uses the database mtime as its
    // bounded timestamp for stream-only requests, so the reader does too.
    if (new Date(statSync(dbPath).mtimeMs).toISOString() > timeFloor) {
      allRequestIds.push(...unjoined.keys())
    }

    const turns: CachedTurn[] = []
    for (const requestId of allRequestIds) {
      const stream = byRequest.get(requestId)
      const parts: ContentPart[] = []
      let order = 0

      if (stream && stream.instructionContent.length > 0) {
        for (const text of stream.instructionContent) {
          if (text.trim() !== '') {
            parts.push({ part: 'instruction_context', text, order: order++ })
          }
        }
      }

      if (stream && stream.userContent.length > 0) {
        for (const text of stream.userContent) {
          if (text.trim() !== '') {
            parts.push({ part: 'conversation_history', text, order: order++ })
          }
        }
      } else {
        const userText = bubbleUserText.get(requestId)
        if (userText && userText.trim() !== '') {
          parts.push({ part: 'conversation_history', text: userText, order: order++ })
        }
      }

      if (stream && stream.toolResultContent.length > 0) {
        for (const text of stream.toolResultContent) {
          if (text.trim() !== '') {
            parts.push({ part: 'tool_result_content', text, order: order++ })
          }
        }
      }

      const composerId = requestToComposer.get(requestId)
      const sessionId = composerId ?? requestId
      const contextWindow = requestContextWindow.get(requestId)

      turns.push({
        composerId: composerId ?? null,
        turn: {
          parts,
          sessionId,
          nativeRecordId: requestId,
          ...(contextWindow !== undefined ? { contextWindow } : {}),
        },
      })
    }
    return turns
  } finally {
    db.close()
  }
}

export const cursorReader: ContentReader = {
  async *read(filePath: string, dateRange?: DateRange): AsyncGenerator<ReaderTurn> {
    if (filePath.endsWith('.json')) {
      for (const turn of readFromJsonFile(filePath)) {
        yield turn
      }
      return
    }

    const { dbPath, workspaceTag } = decodeSourcePath(filePath)
    if (!existsSync(dbPath)) return
    const timeFloor = getCursorTimeFloor(dateRange)
    const timeCeiling = dateRange?.end.toISOString()
    const fingerprint = sqliteFingerprint(dbPath)
    let cached = sqliteReads.get(dbPath)
    if (cached?.fingerprint !== fingerprint || cached.timeFloor !== timeFloor || cached.timeCeiling !== timeCeiling) {
      const turns = readFromSqlite(dbPath, timeFloor, timeCeiling)
      if (turns === null) return
      cached = { fingerprint, timeFloor, timeCeiling, turns }
      // Cursor can write during our scan. A mixed snapshot is never reused.
      if (sqliteFingerprint(dbPath) === fingerprint) {
        sqliteReads.delete(dbPath)
        sqliteReads.set(dbPath, cached)
        if (sqliteReads.size > 2) sqliteReads.delete(sqliteReads.keys().next().value!)
      }
    }
    const { composerFilter, filterMode } = getCursorComposerFilter(dbPath, workspaceTag)
    for (const { composerId, turn } of cached.turns) {
      if (composerFilter !== null) {
        const inSet = composerId !== null && composerFilter.has(composerId)
        if (filterMode === 'include' && !inSet) continue
        if (filterMode === 'exclude' && inSet) continue
      }
      yield turn
    }
  },
}

export default cursorReader
