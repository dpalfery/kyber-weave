// Copilot CLI context rows.
//
// `~/.copilot/data.db`'s sessions table already separates the ASAD context
// taxonomy. Preserve its optional fields exactly: a missing bucket is unknown,
// not a zero-valued measurement.

import { existsSync, statSync } from 'node:fs'
import { homedir } from 'node:os'
import { basename, dirname, join } from 'node:path'

import type { ContentPart } from '../../canon/types.js'
import type { ParsedProviderCall } from '../../providers/types.js'
import { readSessionFile } from '../../ingest/fs-utils.js'
import { openDatabase } from '../../ingest/sqlite.js'
import type { ContentReader, ReaderToolCall, ReaderToolResult, ReaderTurn } from './types.js'

const CONTEXT_FIELDS = [
  'context_system_tokens',
  'context_conversation_tokens',
  'context_tool_definitions_tokens',
  'context_mcp_tools_tokens',
  'context_buffer_tokens',
  'context_tier',
] as const

export type CopilotCliContextRow = Partial<Record<(typeof CONTEXT_FIELDS)[number], number | string>>

type SqliteRow = Record<string, unknown>

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function stringValue(value: unknown, fallback = ''): string {
  return typeof value === 'string' && value !== '' ? value : fallback
}

function numberValue(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

function timestampValue(value: unknown): string {
  if (typeof value === 'string' && value !== '') {
    const parsed = Date.parse(value)
    if (!Number.isNaN(parsed)) return new Date(parsed).toISOString()
  }
  if (typeof value === 'number' && Number.isFinite(value) && value > 0) {
    return new Date(value < 1_000_000_000_000 ? value * 1_000 : value).toISOString()
  }
  return new Date(0).toISOString()
}

/**
 * Select the reported ASAD context fields from a Copilot CLI session row.
 * Omitted columns remain omitted, allowing downstream measurability to state
 * the gap instead of silently substituting zero.
 */
export function parseCopilotCliContextRow(row: unknown): CopilotCliContextRow {
  if (!isRecord(row)) return {}

  const context: CopilotCliContextRow = {}
  for (const field of CONTEXT_FIELDS) {
    const value = row[field]
    if (typeof value === 'number' || typeof value === 'string') context[field] = value
  }
  return context
}

/**
 * Load the CLI's context rows as ordinary provider calls. The taxonomy stays on
 * the structural call object, so synthesis retains it in `CanonicalRecord.raw`
 * without claiming a value for columns the CLI did not report.
 */
export function loadCopilotCliCalls(
  filePath = join(homedir(), '.copilot', 'data.db'),
): ParsedProviderCall[] {
  const db = openDatabase(filePath)
  try {
    return db.query<SqliteRow>('SELECT * FROM sessions').map((row, index) => {
      const id = stringValue(row.id, `copilot-cli-row-${index}`)
      const sessionId = stringValue(row.session_id, id)
      return {
        provider: 'copilot',
        model: stringValue(row.model, 'unknown'),
        inputTokens: numberValue(row.input_tokens),
        outputTokens: numberValue(row.output_tokens),
        cacheCreationInputTokens: numberValue(row.cache_write_tokens),
        cacheReadInputTokens: numberValue(row.cache_read_tokens),
        cachedInputTokens: numberValue(row.cache_read_tokens),
        reasoningTokens: numberValue(row.reasoning_tokens),
        webSearchRequests: 0,
        costUSD: numberValue(row.cost_usd),
        // Marker only when the row carries a real cost_usd; absent/NULL/non-finite is not a
        // harness figure (issue #186) and falls through to the published-rate default.
        costHarnessReported: typeof row.cost_usd === 'number' && Number.isFinite(row.cost_usd),
        tools: [],
        bashCommands: [],
        timestamp: timestampValue(row.created_at),
        speed: 'standard',
        deduplicationKey: `copilot:${sessionId}:${id}`,
        userMessage: '',
        sessionId,
        ...parseCopilotCliContextRow(row),
      }
    })
  } finally {
    db.close()
  }
}

function eventAgentId(event: Record<string, unknown>): string {
  return typeof event['agentId'] === 'string' ? event['agentId'] : ''
}

/** What one session's `events.jsonl` journal says about its root prompt and tool use. */
type JournalSession = {
  /** The root agent's latest `system.message`; a subagent's prompt never lands here. */
  prompt?: string
  /** Every tool request the session made, root and subagent alike. */
  toolCalls: ReaderToolCall[]
  /** Every tool execution the journal completed, joined to a call by `toolCallId`. */
  toolResults: ReaderToolResult[]
}

function toolRequestOf(request: unknown, agentId: string): ReaderToolCall | undefined {
  if (!isRecord(request)) return undefined
  const id = stringValue(request['toolCallId'])
  const name = stringValue(request['name'], stringValue(request['toolName']))
  if (id === '' || name === '') return undefined
  const args = request['arguments']
  return {
    id,
    name,
    arguments: typeof args === 'string' || isRecord(args) ? args : {},
    ...(agentId !== '' ? { agentId } : {}),
  }
}

/**
 * Parse one `events.jsonl` journal. The latest root `system.message` is the
 * prompt in force; subagent events contribute tool calls and results only.
 * The journal holds no tool schemas, so tool definitions are not read from it.
 */
function readJournal(text: string): JournalSession {
  const session: JournalSession = { toolCalls: [], toolResults: [] }
  for (const line of text.split('\n')) {
    if (line.trim() === '') continue
    let event: unknown
    try {
      event = JSON.parse(line)
    } catch {
      continue
    }
    if (!isRecord(event) || !isRecord(event['data'])) continue
    const data = event['data']
    const root = eventAgentId(event) === ''
    if (event['type'] === 'system.message') {
      const content = data['content']
      if (root && typeof content === 'string' && content !== '') session.prompt = content
    } else if (event['type'] === 'assistant.message') {
      const requests = Array.isArray(data['toolRequests']) ? data['toolRequests'] : []
      for (const request of requests) {
        const call = toolRequestOf(request, eventAgentId(event))
        if (call !== undefined) session.toolCalls.push(call)
      }
    } else if (event['type'] === 'tool.execution_complete') {
      const toolCallId = stringValue(data['toolCallId'])
      if (toolCallId === '') continue
      const result: Record<string, unknown> = isRecord(data['result']) ? data['result'] : {}
      session.toolResults.push({
        toolCallId,
        content: stringValue(result['content']),
        ...(data['success'] === false ? { isError: true } : {}),
      })
    }
  }
  return session
}

/**
 * The session's single turn: its root prompt as one `system_prompt` part, and its
 * tool calls and results. Absent when the journal holds neither.
 */
function sessionTurnOf(sessionId: string, session: JournalSession, withTools: boolean): ReaderTurn | undefined {
  const parts: ContentPart[] = session.prompt === undefined
    ? []
    : [{ part: 'system_prompt', text: session.prompt, order: 0 }]
  const toolCalls = withTools ? session.toolCalls : []
  const toolResults = withTools ? session.toolResults : []
  if (parts.length === 0 && toolCalls.length === 0) return undefined
  return {
    sessionId,
    parts,
    ...(toolCalls.length > 0 ? { toolCalls } : {}),
    ...(toolResults.length > 0 ? { toolResults } : {}),
  }
}

/** The session store the CLI keeps beside its journals: `<home>/.copilot/session-store.db`. */
function sessionStorePath(): string {
  return join(homedir(), '.copilot', 'session-store.db')
}

/** The journal a session directory name resolves to: `<home>/.copilot/session-state/<sessionId>/events.jsonl`. */
function journalPathOf(sessionId: string): string {
  return join(homedir(), '.copilot', 'session-state', sessionId, 'events.jsonl')
}

/**
 * The last session store read, so consecutive journal reads do not reopen the same
 * store (F3). One entry, keyed by the store's fingerprint: a rewritten store misses
 * and is read again, and no other store is kept.
 */
let storeIdsMemo: { storePath: string; fingerprint: string; ids: string[] } | undefined

/**
 * What a store's contents look like from the outside: the database and its hot
 * `-wal` file, each by mtime and size. A write may land in the WAL alone, so the
 * main file's stats are not enough. Undefined when the store is absent.
 */
function storeFingerprint(storePath: string): string | undefined {
  if (!existsSync(storePath)) return undefined
  const part = (path: string): string => {
    try {
      const stats = statSync(path)
      return `${stats.mtimeMs}:${stats.size}`
    } catch {
      return '-'
    }
  }
  return `${part(storePath)}|${part(`${storePath}-wal`)}`
}

/**
 * Canonical session ids a session store names, in store order. Each id names its
 * journal directory exactly, so an id that could address another directory
 * (a separator, `.` or `..`) is skipped rather than resolved.
 */
function storeSessionIds(storePath: string): string[] {
  const fingerprint = storeFingerprint(storePath)
  if (fingerprint === undefined) return []
  if (storeIdsMemo?.storePath === storePath && storeIdsMemo.fingerprint === fingerprint) return [...storeIdsMemo.ids]
  const db = openDatabase(storePath)
  let rows: SqliteRow[]
  try {
    rows = db.query<SqliteRow>('SELECT * FROM sessions')
  } finally {
    db.close()
  }
  const ids = new Set<string>()
  for (const row of rows) {
    const sessionId = stringValue(row.session_id, stringValue(row.id))
    if (sessionId === '' || sessionId === '.' || sessionId === '..' || /[\\/]/.test(sessionId)) continue
    ids.add(sessionId)
  }
  storeIdsMemo = { storePath, fingerprint, ids: [...ids] }
  return [...ids]
}

/**
 * Yield the session's one turn, if its journal exists and carries anything.
 * `withTools` is false when a session store row owns the session's tool records.
 */
async function* readSessionTurn(filePath: string, sessionId: string, withTools: boolean): AsyncGenerator<ReaderTurn> {
  if (!existsSync(filePath)) return
  const text = await readSessionFile(filePath)
  if (text === null) return
  const turn = sessionTurnOf(sessionId, readJournal(text), withTools)
  if (turn !== undefined) yield turn
}

/**
 * Copilot CLI's counters live in SQLite (parsed above); its transcript content
 * lives in each session's `events.jsonl` journal. A `.jsonl` path names one
 * journal, whose session is its directory name. A `.db` path names a session
 * store, whose sessions each resolve to their own journal by exact directory
 * name. Each session yields one turn; a database path never yields content
 * from its counter rows.
 *
 * Tool records have one owner per session (D25). A session with a row in the
 * session store is owned by the store unit, which attaches them to its
 * placeholder row; a journal read for that session carries the prompt only.
 * A session with no store row has its tool records emitted from its journal.
 */
export const copilotCliReader: ContentReader = {
  async *read(filePath: string): AsyncGenerator<ReaderTurn> {
    if (filePath.endsWith('.jsonl')) {
      const sessionId = basename(dirname(filePath))
      const storeOwns = storeSessionIds(sessionStorePath()).includes(sessionId)
      yield* readSessionTurn(filePath, sessionId, !storeOwns)
      return
    }
    if (!filePath.endsWith('.db')) return
    for (const sessionId of storeSessionIds(filePath)) {
      yield* readSessionTurn(journalPathOf(sessionId), sessionId, true)
    }
  },
}

export default copilotCliReader
