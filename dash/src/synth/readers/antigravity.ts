// Antigravity MCP tool catalogue, and the per-call tool activity of a conversation.
//
// Antigravity's conversation store (`<root>/conversations/<cascade>.db`)
// records usage, steps and tool *invocations*; it carries no system prompt and
// no tool definitions. Its built-in tools (`list_dir`, `run_command`, ...) are
// defined inside the language-server binary and are written nowhere on disk.
//
// The one place real definitions exist is the MCP schema directory the agent
// itself is told to read: `<root>/mcp/<serverName>/<toolName>.json`, each file
// a `{ name, description, parameters }` object. This reader surfaces exactly
// those files as `tool_definitions` parts, one per server, and nothing else:
//
//   * Built-in tools, the system prompt and skill text are never emitted.
//   * The catalogue is a snapshot of the directory at ingest time, not a
//     per-turn record. It is attached to the session's first turn only, so the
//     session lists the tools once and no turn is made to claim residency the
//     store cannot prove.
//   * A missing or empty directory, an unreadable file, or a file without a
//     string `name` contributes nothing — absent stays absent.
//
// Tool activity is one turn per model call, keyed `agy-step:<last_step_index>`.
// The call's own steps come from its transcript segment, the same segmentation
// the provider uses, so a turn names only the tools its own generation made.

import { open, readdir, stat } from 'node:fs/promises'
import { basename, dirname, join } from 'node:path'

import type { ContentPart } from '../../canon/types.js'
import { antigravityCallStepIndexes, antigravityCascadeIdFromPath } from '../../providers/antigravity.js'
import { agyStepId, readTranscript, segmentTranscript } from '../../providers/antigravity-transcript.js'
import type { ContentReader, ReaderToolCall, ReaderToolResult, ReaderTurn } from './types.js'

/** A single schema file larger than this is skipped rather than read whole. */
const MAX_SCHEMA_FILE_BYTES = 1_048_576

/** Upper bound on the schema files taken from one server directory. */
const MAX_TOOLS_PER_SERVER = 2_000

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

/**
 * The app-data root (`~/.gemini/antigravity[-cli|-ide]`) that owns a
 * conversation database, or undefined for any other path (the statusline
 * event journal, a stray file).
 */
export function antigravityRootFromConversationPath(filePath: string): string | undefined {
  if (!filePath.toLowerCase().endsWith('.db')) return undefined
  const conversations = dirname(filePath)
  if (basename(conversations) !== 'conversations') return undefined
  return dirname(conversations)
}

async function readToolSchema(path: string): Promise<Record<string, unknown> | undefined> {
  // Open once and check the open handle, never the path, so the file cannot change
  // between the check and the read.
  let handle: Awaited<ReturnType<typeof open>> | undefined
  try {
    handle = await open(path, 'r')
    const info = await handle.stat()
    if (!info.isFile() || info.size > MAX_SCHEMA_FILE_BYTES) return undefined
    const parsed: unknown = JSON.parse(await handle.readFile('utf-8'))
    return isRecord(parsed) && typeof parsed['name'] === 'string' && parsed['name'].trim() !== ''
      ? parsed
      : undefined
  } catch {
    return undefined
  } finally {
    await handle?.close()
  }
}

/**
 * Read every MCP server's tool schemas under `<root>/mcp`, one
 * `tool_definitions` part per server that yielded at least one valid tool.
 * Servers and tools are ordered by name so the output is stable.
 */
export async function readAntigravityMcpCatalogue(root: string): Promise<ContentPart[]> {
  const mcpDir = join(root, 'mcp')
  let servers: string[]
  try {
    const entries = await readdir(mcpDir, { withFileTypes: true })
    servers = entries.filter((entry) => entry.isDirectory()).map((entry) => entry.name).sort()
  } catch {
    return []
  }

  const parts: ContentPart[] = []
  for (const server of servers) {
    let files: string[]
    try {
      files = (await readdir(join(mcpDir, server)))
        .filter((name) => name.toLowerCase().endsWith('.json'))
        .sort()
        .slice(0, MAX_TOOLS_PER_SERVER)
    } catch {
      continue
    }
    const definitions: Record<string, unknown>[] = []
    for (const file of files) {
      const schema = await readToolSchema(join(mcpDir, server, file))
      if (schema !== undefined) definitions.push(schema)
    }
    if (definitions.length === 0) continue
    parts.push({
      part: 'tool_definitions',
      text: JSON.stringify(definitions),
      server,
      // A snapshot of the directory, not a record of what the model had resident.
      resident: false,
      order: parts.length,
    })
  }
  return parts
}

export const antigravityReader: ContentReader = {
  // A call's id is its agy-step key, so turns pair by id. A call whose segment is
  // missing keeps its own turn rather than inheriting a neighbour's position.
  positionalPairingUnsafe: true,

  async *read(filePath: string): AsyncGenerator<ReaderTurn> {
    const root = antigravityRootFromConversationPath(filePath)
    if (root === undefined) return
    try {
      if (!(await stat(filePath)).isFile()) return
    } catch {
      return
    }
    const catalogue = await readAntigravityMcpCatalogue(root)
    // The provider's own decoder yields the call boundaries, so the reader and the
    // attached calls agree on each segment.
    const lastStepIndexes = await antigravityCallStepIndexes(filePath)
    if (lastStepIndexes.length === 0) {
      if (catalogue.length > 0) yield { parts: catalogue }
      return
    }

    const transcript = await readTranscript(antigravityCascadeIdFromPath(filePath), [root])
    const segments = transcript === undefined ? undefined : segmentTranscript(transcript, lastStepIndexes)

    for (const [i, lastStepIndex] of lastStepIndexes.entries()) {
      const nativeRecordId = agyStepId(lastStepIndex)
      const segment = segments?.[i]
      const toolCalls: ReaderToolCall[] = (segment?.calls ?? []).map((call, k) => ({
        id: `${nativeRecordId}:${k}`,
        name: call.name,
        arguments: call.args,
      }))
      const [only] = toolCalls
      // segmentTranscript sets a result only for a segment with exactly one tool call.
      const toolResults: ReaderToolResult[] = segment?.result !== undefined && only !== undefined
        ? [{ toolCallId: only.id, content: segment.result }]
        : []
      yield {
        // The catalogue is a session snapshot, so it rides on the first call's turn.
        parts: i === 0 ? catalogue : [],
        nativeRecordId,
        ...(toolCalls.length > 0 ? { toolCalls } : {}),
        ...(toolResults.length > 0 ? { toolResults } : {}),
      }
    }
  },
}

export default antigravityReader
