// Post-run transcript synchronization engine job (spec: docs/specs/kyberdash).
//
// Reads incremental delta bytes from transcript files referenced in `session`,
// parses JSONL messages, enriches canonical OTel records by joining on `request_id`,
// manages session sync statuses ('pending' -> 'synced' -> 'settled' / 'unavailable'),
// and respects lock arbitration with store refreshes.

import { closeSync, existsSync, openSync, readSync, statSync } from 'node:fs'

import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord, ContentPart } from '../canon/types.js'
import { STORE_LOCK_FILE, readLockHolder, stateDir } from '../refresh/lock.js'

export type TranscriptSyncClock = {
  now(): number
}

export type TranscriptSyncOptions = {
  readonly store: CanonStore
  readonly stateDir?: string
  readonly clock?: TranscriptSyncClock
}

export type TranscriptSyncOutcome =
  | { readonly outcome: 'completed'; readonly syncedSessions?: number }
  | { readonly outcome: 'busy' }
  | { readonly outcome: 'failed'; readonly reason: string }

const realClock: TranscriptSyncClock = {
  now: () => Date.now(),
}

interface TranscriptLine {
  requestId?: string
  role?: string
  text?: string
}

function extractMessageContent(item: unknown): string {
  if (typeof item === 'string') return item
  if (item === null || typeof item !== 'object') return ''
  const obj = item as Record<string, unknown>
  if (typeof obj['text'] === 'string') return obj['text']
  if (typeof obj['thinking'] === 'string') return obj['thinking']
  if (typeof obj['content'] === 'string') return obj['content']
  if (Array.isArray(obj['content'])) {
    return obj['content']
      .map(extractMessageContent)
      .filter((s) => s.length > 0)
      .join('\n')
  }
  return ''
}

function parseTranscriptLine(raw: string): TranscriptLine | undefined {
  const trimmed = raw.trim()
  if (!trimmed) return undefined
  try {
    const obj = JSON.parse(trimmed) as Record<string, unknown>
    const msg = (obj['message'] && typeof obj['message'] === 'object' && !Array.isArray(obj['message'])
      ? obj['message']
      : obj) as Record<string, unknown>

    const requestId =
      (typeof obj['requestId'] === 'string' ? obj['requestId'] : undefined) ??
      (typeof obj['request_id'] === 'string' ? obj['request_id'] : undefined) ??
      (typeof msg['requestId'] === 'string' ? msg['requestId'] : undefined) ??
      (typeof msg['request_id'] === 'string' ? msg['request_id'] : undefined)

    const role =
      (typeof msg['role'] === 'string' ? msg['role'] : undefined) ??
      (typeof obj['role'] === 'string' ? obj['role'] : undefined)

    let text = ''
    if (typeof msg['content'] === 'string') {
      text = msg['content']
    } else if (Array.isArray(msg['content'])) {
      text = msg['content'].map(extractMessageContent).filter(Boolean).join('\n')
    } else if (typeof obj['content'] === 'string') {
      text = obj['content']
    } else if (Array.isArray(obj['content'])) {
      text = obj['content'].map(extractMessageContent).filter(Boolean).join('\n')
    } else if (typeof msg['text'] === 'string') {
      text = msg['text']
    } else if (typeof obj['text'] === 'string') {
      text = obj['text']
    }

    return { requestId, role, text }
  } catch {
    return undefined
  }
}

function getRecordRequestId(record: CanonicalRecord): string | undefined {
  if (record.raw && typeof record.raw === 'object') {
    const raw = record.raw as Record<string, unknown>
    if (typeof raw['request_id'] === 'string' && raw['request_id'] !== '') return raw['request_id']
    if (typeof raw['requestId'] === 'string' && raw['requestId'] !== '') return raw['requestId']
    if (typeof raw['gen_ai.response.id'] === 'string' && raw['gen_ai.response.id'].startsWith('req_')) {
      return raw['gen_ai.response.id']
    }
  }
  return undefined
}

function enrichRecordWithTranscript(
  store: CanonStore,
  record: CanonicalRecord,
  lines: TranscriptLine[],
): void {
  const existingParts: ContentPart[] = record.parts ? [...record.parts] : []
  let order = existingParts.length

  const newParts: ContentPart[] = []
  for (const line of lines) {
    if (line.text && line.text.trim() !== '') {
      newParts.push({
        part: 'conversation_history',
        text: line.text,
        order: order++,
      })
    }
  }

  if (newParts.length === 0) return

  const combinedParts = [...existingParts, ...newParts]
  const conversationHistory = combinedParts
    .filter((p) => p.part === 'conversation_history')
    .map((p) => p.text)
    .join('\n')

  const updatedContent = {
    ...record.content,
    conversation_history: conversationHistory,
  }

  store.setContent(record.spanId, updatedContent, combinedParts)
}

function processDelta(store: CanonStore, sessionId: string, deltaText: string): void {
  const lines = deltaText.split('\n')
  const linesByRequestId = new Map<string, TranscriptLine[]>()

  for (const line of lines) {
    const parsed = parseTranscriptLine(line)
    if (parsed && parsed.requestId && parsed.text) {
      const list = linesByRequestId.get(parsed.requestId)
      if (list !== undefined) {
        list.push(parsed)
      } else {
        linesByRequestId.set(parsed.requestId, [parsed])
      }
    }
  }

  if (linesByRequestId.size === 0) return

  const db = store.getDatabase()
  const candidateRows = db
    .prepare('SELECT span_id FROM records WHERE session_id = ? OR trace_id = ?')
    .all(sessionId, sessionId) as { span_id: string }[]

  for (const { span_id } of candidateRows) {
    const record = store.get(span_id)
    if (!record) continue

    const reqId = getRecordRequestId(record)
    if (!reqId) continue

    const matchingLines = linesByRequestId.get(reqId)
    if (!matchingLines || matchingLines.length === 0) continue

    enrichRecordWithTranscript(store, record, matchingLines)
  }
}

interface SessionRow {
  session_id: string
  harness: string
  transcript_path: string | null
  transcript_offset: number | null
  sync_status: string | null
  settled_at: string | null
  started: string | null
  ended: string | null
  payload: string
}

function syncSessions(store: CanonStore, clock: TranscriptSyncClock): number {
  const db = store.getDatabase()
  const now = clock.now()
  const nowIso = new Date(now).toISOString()

  const rows = db
    .prepare(
      `SELECT session_id, harness, transcript_path, transcript_offset, sync_status, settled_at, started, ended, payload
       FROM session
       WHERE sync_status NOT IN ('settled', 'unavailable') OR sync_status IS NULL`,
    )
    .all() as unknown as SessionRow[]

  let syncedCount = 0

  for (const row of rows) {
    // 1. Missing transcript check (> 24 hours past session ended / started)
    if (!row.transcript_path || !existsSync(row.transcript_path)) {
      const refTime = row.ended
        ? Date.parse(row.ended)
        : (row.started ? Date.parse(row.started) : null)
      if (refTime !== null && !isNaN(refTime) && (now - refTime) > 24 * 3600_000) {
        db.prepare(
          `UPDATE session SET sync_status = 'unavailable' WHERE session_id = ?`,
        ).run(row.session_id)
      }
      continue
    }

    // 2. Stat transcript file
    let stat
    try {
      stat = statSync(row.transcript_path)
    } catch {
      continue
    }

    const fileSize = stat.size
    const oldOffset = row.transcript_offset ?? 0

    // 3. Incremental delta parse
    if (fileSize > oldOffset) {
      const bytesToRead = fileSize - oldOffset
      const buffer = Buffer.alloc(bytesToRead)
      const fd = openSync(row.transcript_path, 'r')
      try {
        readSync(fd, buffer, 0, bytesToRead, oldOffset)
      } finally {
        closeSync(fd)
      }

      const deltaText = buffer.toString('utf-8')
      processDelta(store, row.session_id, deltaText)

      db.prepare(
        `UPDATE session
         SET transcript_offset = ?, synced_at = ?, sync_status = 'synced'
         WHERE session_id = ?`,
      ).run(fileSize, nowIso, row.session_id)
      syncedCount++
    } else if (fileSize < oldOffset) {
      // Truncated file: re-read full file from offset 0
      const buffer = Buffer.alloc(fileSize)
      const fd = openSync(row.transcript_path, 'r')
      try {
        readSync(fd, buffer, 0, fileSize, 0)
      } finally {
        closeSync(fd)
      }

      const deltaText = buffer.toString('utf-8')
      processDelta(store, row.session_id, deltaText)

      db.prepare(
        `UPDATE session
         SET transcript_offset = ?, synced_at = ?, sync_status = 'synced'
         WHERE session_id = ?`,
      ).run(fileSize, nowIso, row.session_id)
      syncedCount++
    } else {
      // fileSize === oldOffset (no new bytes)
      if (row.sync_status === 'pending') {
        db.prepare(
          `UPDATE session
           SET synced_at = ?, sync_status = 'synced'
           WHERE session_id = ?`,
        ).run(nowIso, row.session_id)
      } else if (row.sync_status === 'synced') {
        // Settlement check: > 30 min inactivity and 2 unchanged passes
        const lastActivity = row.ended
          ? Date.parse(row.ended)
          : (row.started ? Date.parse(row.started) : null)
        const isInactive =
          lastActivity !== null && !isNaN(lastActivity) && (now - lastActivity) >= 30 * 60_000

        if (isInactive) {
          db.prepare(
            `UPDATE session
             SET sync_status = 'settled', settled_at = ?
             WHERE session_id = ?`,
          ).run(nowIso, row.session_id)
        }
      }
    }
  }

  return syncedCount
}

export async function runTranscriptSync(
  storeOrOptions: CanonStore | TranscriptSyncOptions,
  maybeOptions?: Partial<TranscriptSyncOptions>,
): Promise<TranscriptSyncOutcome> {
  const options: TranscriptSyncOptions =
    storeOrOptions instanceof CanonStore
      ? { store: storeOrOptions, ...maybeOptions }
      : storeOrOptions

  const store = options.store
  const clock = options.clock ?? realClock
  const targetDir = options.stateDir ?? stateDir()

  // Yield if the store refresh lock is held
  if (readLockHolder(targetDir, STORE_LOCK_FILE) !== null) {
    return { outcome: 'busy' }
  }

  try {
    const syncedSessions = syncSessions(store, clock)
    return { outcome: 'completed', syncedSessions }
  } catch (err) {
    return { outcome: 'failed', reason: err instanceof Error ? err.message : String(err) }
  }
}
