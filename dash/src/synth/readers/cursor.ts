// Cursor session content reader.
//
// Reads SQLite databases (and JSON fixtures) produced by Cursor and extracts
// input-side turn snapshots. Retains available user prompt, instructions
// (agentKv system blobs), and tool context (agentKv tool blobs) without
// claiming a complete multi-turn conversation prefix.

import { existsSync, readFileSync } from 'node:fs'

import type { ContentPart } from '../../canon/types.js'
import { blobToText, isSqliteAvailable, openDatabase } from '../../ingest/sqlite.js'
import {
  decodeSourcePath,
  getCursorComposerFilter,
  loadAgentStreams,
  parseComposerIdFromKey,
} from '../../providers/cursor.js'
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

function* readFromSqlite(filePath: string): Generator<ReaderTurn> {
  const { dbPath, workspaceTag } = decodeSourcePath(filePath)
  if (!existsSync(dbPath) || !isSqliteAvailable()) return

  const { composerFilter, filterMode } = getCursorComposerFilter(dbPath, workspaceTag)
  const isComposerAllowed = (cid: string | null): boolean => {
    if (!cid) return false
    if (composerFilter === null) return true
    const inSet = composerFilter.has(cid)
    return filterMode === 'include' ? inSet : !inSet
  }

  let db: ReturnType<typeof openDatabase>
  try {
    db = openDatabase(dbPath)
  } catch {
    return
  }

  try {
    type BubbleRow = {
      bubble_key: string
      request_id: string | null
      text: Uint8Array | string | null
      bubble_type: number | null
      context_window: number | null
    }

    let bubbleRows: BubbleRow[] = []
    try {
      bubbleRows = db.query<BubbleRow>(`
        SELECT
          key as bubble_key,
          json_extract(value, '$.requestId') as request_id,
          CAST(json_extract(value, '$.text') AS BLOB) as text,
          json_extract(value, '$.type') as bubble_type,
          json_extract(value, '$.contextWindow') as context_window
        FROM cursorDiskKV
        WHERE key LIKE 'bubbleId:%'
        ORDER BY ROWID ASC
      `)
    } catch {
      return
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
      if (row.request_id && row.bubble_type === 1 && row.text) {
        const text = blobToText(row.text)
        if (text) {
          bubbleUserText.set(row.request_id, text)
        }
      }
    }

    const { byComposer, unjoined, byRequest } = loadAgentStreams(db, requestToComposer, {
      retainContent: true,
    })

    const allRequestIds: string[] = []
    for (const [requestId, cid] of requestToComposer) {
      if (isComposerAllowed(cid)) {
        allRequestIds.push(requestId)
      }
    }
    if (filterMode === 'exclude' || composerFilter === null) {
      for (const requestId of unjoined.keys()) {
        allRequestIds.push(requestId)
      }
    }

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

      yield {
        parts,
        sessionId,
        nativeRecordId: requestId,
        ...(contextWindow !== undefined ? { contextWindow } : {}),
      }
    }

    if (allRequestIds.length === 0) {
      for (const [cid, stream] of byComposer) {
        if (!isComposerAllowed(cid)) continue
        const parts: ContentPart[] = []
        let order = 0
        for (const text of stream.instructionContent) {
          if (text.trim() !== '') parts.push({ part: 'instruction_context', text, order: order++ })
        }
        for (const text of stream.userContent) {
          if (text.trim() !== '') parts.push({ part: 'conversation_history', text, order: order++ })
        }
        for (const text of stream.toolResultContent) {
          if (text.trim() !== '') parts.push({ part: 'tool_result_content', text, order: order++ })
        }
        yield {
          parts,
          sessionId: cid,
        }
      }
    }
  } finally {
    db.close()
  }
}

export const cursorReader: ContentReader = {
  async *read(filePath: string): AsyncGenerator<ReaderTurn> {
    if (filePath.endsWith('.json')) {
      for (const turn of readFromJsonFile(filePath)) {
        yield turn
      }
      return
    }

    for (const turn of readFromSqlite(filePath)) {
      yield turn
    }
  },
}

export default cursorReader
