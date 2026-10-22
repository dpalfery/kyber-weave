import { existsSync } from 'fs'
import { readdir } from 'fs/promises'
import { join } from 'path'

import { calculateCost } from '../pricing/models.js'
import {
  isSqliteAvailable,
  getSqliteLoadError,
  openDatabase,
  blobToText,
  isSqliteBusyError,
  isSqliteReadonlyError,
  warnSqliteReadonlyOnce,
  type SqliteDatabase,
} from '../ingest/sqlite.js'
import { buildAssistantCall, parseTimestamp, sanitize, type MessageData, type PartData } from './session-message.js'
import type {
  SessionSource,
  SessionParser,
  ParsedProviderCall,
} from './types.js'

type MessageRow = {
  session_id: string
  id: string
  time_created: number
  data: Uint8Array | string
}

type PartRow = {
  message_id: string
  data: Uint8Array | string
}

type SessionRow = {
  id: string
  directory: Uint8Array | string
  title: Uint8Array | string
  time_created: number
}

type SessionTokenRow = {
  cost?: number
  tokens_input?: number
  tokens_output?: number
  tokens_reasoning?: number
  tokens_cache_read?: number
  tokens_cache_write?: number
  model?: Uint8Array | string
}

function parseSessionModel(value: Uint8Array | string | undefined): string | undefined {
  try {
    const parsed: unknown = JSON.parse(blobToText(value))
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return undefined

    const model = parsed as Record<string, unknown>
    const id = typeof model['id'] === 'string' ? model['id'].trim() : ''
    const providerID = typeof model['providerID'] === 'string' ? model['providerID'].trim() : ''
    return id && providerID ? `${providerID}/${id}` : undefined
  } catch {
    return undefined
  }
}

function tryQuerySessionTokens(db: SqliteDatabase, sessionId: string): {
  cost: number; input: number; output: number; reasoning: number
  cacheRead: number; cacheWrite: number; model: string | undefined
} | null {
  try {
    const rows = db.query<SessionTokenRow>(
      `SELECT cost, tokens_input, tokens_output, tokens_reasoning, tokens_cache_read, tokens_cache_write,
              CAST(model AS BLOB) AS model
       FROM session WHERE id = ?`,
      [sessionId],
    )
    if (rows.length === 0) return null
    const r = rows[0]!
    return {
      cost: r.cost ?? 0,
      input: r.tokens_input ?? 0,
      output: r.tokens_output ?? 0,
      reasoning: r.tokens_reasoning ?? 0,
      cacheRead: r.tokens_cache_read ?? 0,
      cacheWrite: r.tokens_cache_write ?? 0,
      model: parseSessionModel(r.model),
    }
  } catch {
    return null
  }
}

// Suffix discoverSqliteSessions puts on a db it listed but could not read —
// a store that exists but fails to open, fails the required-table check, or
// errors on the session query. The compound `dbPath:marker` shape rides the
// same seams real session sources do (`.db:` routing in createSessionParser,
// the colon-strip in fingerprintFile, kilo-root classification), so the
// failure is attributed to the harness that owns the db instead of dropped.
// `reason` rides ON the source: a parse that re-derived the failure could
// find a transient cause (busy lock) already cleared and report a healthy
// empty store — the exact silent zero this marker exists to prevent.
const UNREADABLE_STORE_SUFFIX = '__unreadable-store__'

function unreadableStoreSource(
  dbPath: string,
  config: SqliteProviderConfig,
  reason: string,
): SessionSource {
  return {
    path: `${dbPath}:${UNREADABLE_STORE_SUFFIX}`,
    project: '',
    provider: config.providerName,
    unreadableStoreError: reason,
  }
}

// An open failure on a PRESENT store becomes the problem the reader reports:
// the message names the db path, and the sqlite/fs codes ride along so
// isSqliteBusyError/isSqliteReadonlyError still classify it downstream
// (a busy store keeps its deferral semantics in the legacy ingest path).
function unreadableStoreError(
  dbPath: string,
  config: SqliteProviderConfig,
  err: unknown,
): Error {
  const cause = err instanceof Error ? err.message : String(err)
  const failure = new Error(
    `cannot open ${config.displayName} database ${dbPath}: ${cause}`,
  ) as Error & { path?: string; code?: string; errcode?: number }
  failure.path = dbPath
  const carrier = err as { code?: unknown; errcode?: unknown }
  if (typeof carrier.code === 'string') failure.code = carrier.code
  if (typeof carrier.errcode === 'number') failure.errcode = carrier.errcode
  return failure
}

type SchemaCheckResult = { ok: true } | { ok: false; missing: string[] }

function validateSchemaDetailed(db: SqliteDatabase): SchemaCheckResult {
  const required = ['session', 'message', 'part']
  const missing: string[] = []
  for (const table of required) {
    try {
      db.query<{ cnt: number }>(`SELECT COUNT(*) as cnt FROM ${table} LIMIT 1`)
    } catch (err) {
      if (isSqliteBusyError(err)) throw err
      missing.push(table)
    }
  }
  return missing.length === 0 ? { ok: true } : { ok: false, missing }
}

const warnedSchemas = new Map<string, Set<string>>()

function warnUnrecognizedSchemaOnce(providerLabel: string, missing: string[]): void {
  const providerSet = warnedSchemas.get(providerLabel) ?? new Set()
  const key = missing.slice().sort().join(',')
  if (providerSet.has(key)) return
  providerSet.add(key)
  warnedSchemas.set(providerLabel, providerSet)
  process.stderr.write(
    `kyberdash: ${providerLabel} database is missing expected tables (${missing.join(', ')}). ` +
    `Run ${providerLabel} once to apply migrations, or report at https://github.com/dpalfery/kyber-weave/issues if this persists.\n`
  )
}

export type SqliteProviderConfig = {
  providerName: string
  displayName: string
  dbDir: string
  dbFilePrefix: string
  /**
   * Opt-in: stamp `pairingId` with the store's `message.id` so a content
   * reader pairs turns to calls by id rather than position. The call list
   * and the reader's turn list are different lengths wherever a zero-token
   * assistant message still yields a call, so index pairing would attach
   * each turn's content to its neighbour. Off by default: providers whose
   * parser output must stay byte-identical (OpenCode) do not set it, and the
   * field stays pairing-only — `turnId` remains span identity (D6).
   */
  pairingIdFromMessageId?: boolean
}

export function createSqliteSessionParser(
  source: SessionSource,
  seenKeys: Set<string>,
  config: SqliteProviderConfig,
): SessionParser {
  return {
    async *parse(): AsyncGenerator<ParsedProviderCall> {
      if (!isSqliteAvailable()) {
        process.stderr.write(getSqliteLoadError() + '\n')
        return
      }

      const segments = source.path.split(':')
      const sessionId = segments[segments.length - 1]!
      const dbPath = segments.slice(0, -1).join(':')

      // A marker source carries the failure discovery already observed.
      // Throw it unconditionally rather than re-open the store: a transient
      // cause (busy lock) may have cleared by parse time, and a succeeding
      // second open would masquerade the failure as an empty store.
      if (source.unreadableStoreError !== undefined) {
        const failure = new Error(
          `${config.displayName} database ${dbPath}: ${source.unreadableStoreError}`,
        ) as Error & { path?: string }
        failure.path = dbPath
        throw failure
      }

      let db: SqliteDatabase
      try {
        db = openDatabase(dbPath)
      } catch (err) {
        // Absence is a negative existence check (R1.2): a store discovered
        // earlier but gone by parse time, or a path that was never real,
        // stays a silent empty parse. A PRESENT store that cannot be opened
        // is a parse failure to surface, not a silent zero.
        if (!existsSync(dbPath)) {
          process.stderr.write(`kyberdash: cannot open ${config.displayName} database: ${err instanceof Error ? err.message : err}\n`)
          return
        }
        throw unreadableStoreError(dbPath, config, err)
      }

      try {
        const schema = validateSchemaDetailed(db)
        if (!schema.ok) {
          warnUnrecognizedSchemaOnce(config.displayName, schema.missing)
          const failure = new Error(
            `${config.displayName} database ${dbPath} is missing expected tables (${schema.missing.join(', ')})`,
          ) as Error & { path?: string }
          failure.path = dbPath
          throw failure
        }

        const messages = db.query<MessageRow>(
          `WITH RECURSIVE session_tree(id) AS (
            SELECT id FROM session WHERE id = ?
            UNION
            SELECT child.id
            FROM session child
            JOIN session_tree parent ON child.parent_id = parent.id
            WHERE child.time_archived IS NULL
          )
          SELECT session_id, id, time_created, CAST(data AS BLOB) AS data
          FROM message
          WHERE session_id IN (SELECT id FROM session_tree)
          ORDER BY time_created ASC, id ASC`,
          [sessionId],
        )

        const parts = db.query<PartRow>(
          `WITH RECURSIVE session_tree(id) AS (
            SELECT id FROM session WHERE id = ?
            UNION
            SELECT child.id
            FROM session child
            JOIN session_tree parent ON child.parent_id = parent.id
            WHERE child.time_archived IS NULL
          )
          SELECT message_id, CAST(data AS BLOB) AS data
          FROM part
          WHERE session_id IN (SELECT id FROM session_tree)
          ORDER BY message_id, id`,
          [sessionId],
        )

        const partsByMsg = new Map<string, PartData[]>()
        for (const part of parts) {
          try {
            const parsed = JSON.parse(blobToText(part.data)) as PartData
            const list = partsByMsg.get(part.message_id) ?? []
            list.push(parsed)
            partsByMsg.set(part.message_id, list)
          } catch {
            // skip corrupt part data
          }
        }

        const currentUserMessageBySession = new Map<string, string>()
        let yieldCount = 0
        let parseFailCount = 0
        let roleSkipCount = 0

        for (const msg of messages) {
          let data: MessageData
          try {
            data = JSON.parse(blobToText(msg.data)) as MessageData
          } catch {
            parseFailCount++
            continue
          }

          if (data.role === 'user') {
            const textParts = (partsByMsg.get(msg.id) ?? [])
              .filter((p) => p.type === 'text' || p.type === 'markdown')
              .map((p) => p.text ?? '')
              .filter(Boolean)
            if (textParts.length > 0) {
              currentUserMessageBySession.set(msg.session_id, textParts.join(' '))
            }
            continue
          }

          if (data.role !== 'assistant' && data.role !== 'model') {
            if (data.role !== 'user') roleSkipCount++
            continue
          }

          const dedupKey = `${config.providerName}:${msg.session_id}:${msg.id}`
          if (seenKeys.has(dedupKey)) continue

          const call = buildAssistantCall({
            providerName: config.providerName,
            dedupKey,
            sessionId,
            data,
            parts: partsByMsg.get(msg.id) ?? [],
            timeCreatedMs: msg.time_created,
            userMessage: currentUserMessageBySession.get(msg.session_id) ?? '',
          })
          if (!call) continue

          seenKeys.add(dedupKey)
          yieldCount++
          yield config.pairingIdFromMessageId === true
            ? { ...call, pairingId: msg.id }
            : call
        }

        if (yieldCount === 0 && messages.length > 0) {
          const sessionTokens = tryQuerySessionTokens(db, sessionId)
          if (sessionTokens && (sessionTokens.cost > 0 || sessionTokens.input > 0 || sessionTokens.output > 0)) {
            const dedupKey = `${config.providerName}:${sessionId}:session-level`
            if (!seenKeys.has(dedupKey)) {
              seenKeys.add(dedupKey)
              const model = sessionTokens.model ?? 'unknown'
              let costUSD = calculateCost(model, sessionTokens.input, sessionTokens.output, sessionTokens.cacheWrite, sessionTokens.cacheRead, 0)
              if (costUSD === 0 && sessionTokens.cost > 0) costUSD = sessionTokens.cost
              yield {
                provider: config.providerName,
                model,
                inputTokens: sessionTokens.input,
                outputTokens: sessionTokens.output,
                cacheCreationInputTokens: sessionTokens.cacheWrite,
                cacheReadInputTokens: sessionTokens.cacheRead,
                cachedInputTokens: sessionTokens.cacheRead,
                reasoningTokens: sessionTokens.reasoning,
                webSearchRequests: 0,
                costUSD,
                tools: [],
                bashCommands: [],
                timestamp: parseTimestamp(messages[0]!.time_created),
                speed: 'standard',
                deduplicationKey: dedupKey,
                userMessage: '',
                sessionId,
              }
              yieldCount++
            }
          }

          if (yieldCount === 0 && process.env['KYBERDASH_VERBOSE'] === '1') {
            process.stderr.write(
              `kyberdash: ${config.displayName} session ${sessionId} has ${messages.length} messages ` +
              `(${parseFailCount} unparseable, ${roleSkipCount} non-user/assistant roles) ` +
              `but yielded 0 calls. Parts: ${parts.length}.\n`
            )
          }
        }
      } finally {
        db.close()
      }
    },
  }
}

export async function discoverSqliteSessions(
  config: SqliteProviderConfig,
): Promise<SessionSource[]> {
  if (!isSqliteAvailable()) return []

  let dbPaths: string[]
  try {
    const entries = await readdir(config.dbDir)
    dbPaths = entries
      .filter((f) => f.startsWith(config.dbFilePrefix) && f.endsWith('.db'))
      .map((f) => join(config.dbDir, f))
  } catch {
    return []
  }

  if (dbPaths.length === 0) return []

  const sessions: SessionSource[] = []
  for (const dbPath of dbPaths) {
    let db: SqliteDatabase
    try {
      db = openDatabase(dbPath)
    } catch (err) {
      if (isSqliteReadonlyError(err)) warnSqliteReadonlyOnce(dbPath)
      // Listed this pass but gone by open time: absence, not unreadability.
      if (!existsSync(dbPath)) continue
      // The file exists: present-but-unreadable is reported, never skipped.
      sessions.push(
        unreadableStoreSource(dbPath, config, err instanceof Error ? err.message : String(err)),
      )
      continue
    }

    try {
      const schema = validateSchemaDetailed(db)
      // A present store missing the required tables is a problem to report —
      // emit the marker source rather than continue past it.
      if (!schema.ok) {
        sessions.push(
          unreadableStoreSource(dbPath, config, `missing required tables (${schema.missing.join(', ')})`),
        )
        continue
      }

      const rows = db.query<SessionRow>(
        'SELECT id, CAST(directory AS BLOB) AS directory, CAST(title AS BLOB) AS title, time_created FROM session WHERE time_archived IS NULL AND parent_id IS NULL ORDER BY time_created DESC',
      )

      for (const row of rows) {
        const dir = blobToText(row.directory)
        const title = blobToText(row.title)
        sessions.push({
          path: `${dbPath}:${row.id}`,
          project: dir ? sanitize(dir) : sanitize(title),
          provider: config.providerName,
        })
      }
    } catch (err) {
      // A query failure on a db this pass already listed is the same
      // present-but-unreadable shape (busy lock, mid-run migration, corrupt
      // pages): mark it so the read path reports it instead of skipping.
      sessions.push(
        unreadableStoreSource(dbPath, config, err instanceof Error ? err.message : String(err)),
      )
    } finally {
      db.close()
    }
  }

  return sessions
}
