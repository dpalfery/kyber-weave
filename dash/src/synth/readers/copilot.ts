// Copilot CLI context rows.
//
// `~/.copilot/data.db`'s sessions table already separates the ASAD context
// taxonomy. Preserve its optional fields exactly: a missing bucket is unknown,
// not a zero-valued measurement.

import { readFile } from 'node:fs/promises'
import { homedir } from 'node:os'
import { basename, dirname, join } from 'node:path'

import type { ParsedProviderCall } from '../../providers/types.js'
import { openDatabase } from '../../ingest/sqlite.js'
import type { ContentReader, ReaderTurn } from './types.js'

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

/**
 * Read a Copilot CLI `events.jsonl` journal into one turn per `assistant.message`.
 * Each turn carries the `system.message` last recorded for the same agent
 * (root events have no `agentId`, subagent events name theirs), so a subagent's
 * call never inherits the root prompt. Turns are keyed by `messageId`, which the
 * provider parser stamps on its calls as `pairingId`. Root turns are yielded
 * before subagent turns so position-paired calls land on the root prompt. Tool definitions are not
 * measured: the journal holds no tool schemas, and the prompt's `<tools>`
 * section is usage guidance for a subset of tools, not the offered tool set.
 */
async function* readEventsJournal(filePath: string): AsyncGenerator<ReaderTurn> {
  const root: ReaderTurn[] = []
  const subagent: ReaderTurn[] = []
  const sessionId = basename(dirname(filePath))
  const promptByAgent = new Map<string, string>()
  const text = await readFile(filePath, 'utf8')
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
    if (event['type'] === 'system.message') {
      const content = data['content']
      if (typeof content === 'string' && content !== '') promptByAgent.set(eventAgentId(event), content)
      continue
    }
    if (event['type'] !== 'assistant.message') continue
    const messageId = data['messageId']
    if (typeof messageId !== 'string' || messageId === '') continue
    const prompt = promptByAgent.get(eventAgentId(event))
    const turn: ReaderTurn = {
      sessionId,
      nativeRecordId: messageId,
      parts: prompt === undefined ? [] : [{ part: 'system_prompt', text: prompt, order: 0 }],
    }
    ;(eventAgentId(event) === '' ? root : subagent).push(turn)
  }
  // Calls without a messageId (the shutdown rollup legs) pair by position, and a
  // rollup describes the session, so the root agent's turns lead.
  yield* root
  yield* subagent
}

/**
 * Copilot CLI's counters live in SQLite (parsed above); its transcript content
 * lives in `<session>/events.jsonl`. Only the journal yields turns — a database
 * path yields nothing rather than inventing content from counter rows.
 */
export const copilotCliReader: ContentReader = {
  async *read(filePath: string): AsyncGenerator<ReaderTurn> {
    if (!filePath.endsWith('.jsonl')) return
    yield* readEventsJournal(filePath)
  },
}

export default copilotCliReader
