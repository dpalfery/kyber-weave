// Copilot CLI context rows.
//
// `~/.copilot/data.db`'s sessions table already separates the ASAD context
// taxonomy. Preserve its optional fields exactly: a missing bucket is unknown,
// not a zero-valued measurement.

import { homedir } from 'node:os'
import { join } from 'node:path'

import type { ParsedProviderCall } from '../../../src/providers/types.js'
import { openDatabase } from '../../../src/sqlite.js'
import type { ContentReader } from './types.js'

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

/**
 * Copilot CLI's collectable source is its SQLite context table, not a
 * transcript content format. The reader stays empty rather than inventing
 * content parts from counter rows.
 */
export const copilotCliReader: ContentReader = {
  async *read(): AsyncGenerator<never> {
    // Context counter rows are parsed through parseCopilotCliContextRow.
  },
}

export default copilotCliReader
