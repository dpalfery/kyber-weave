// Antigravity MCP tool catalogue.
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

import { readdir, readFile, stat } from 'node:fs/promises'
import { basename, dirname, join } from 'node:path'

import type { ContentPart } from '../../canon/types.js'
import type { ContentReader, ReaderTurn } from './types.js'

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
  try {
    const info = await stat(path)
    if (!info.isFile() || info.size > MAX_SCHEMA_FILE_BYTES) return undefined
    const parsed: unknown = JSON.parse(await readFile(path, 'utf-8'))
    return isRecord(parsed) && typeof parsed['name'] === 'string' && parsed['name'].trim() !== ''
      ? parsed
      : undefined
  } catch {
    return undefined
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
      order: parts.length,
    })
  }
  return parts
}

export const antigravityReader: ContentReader = {
  async *read(filePath: string): AsyncGenerator<ReaderTurn> {
    const root = antigravityRootFromConversationPath(filePath)
    if (root === undefined) return
    try {
      if (!(await stat(filePath)).isFile()) return
    } catch {
      return
    }
    const parts = await readAntigravityMcpCatalogue(root)
    if (parts.length === 0) return
    yield { parts }
  },
}

export default antigravityReader
