import { readdir, readFile } from 'fs/promises'
import { join } from 'path'

import { buildAssistantCall, sanitize, type MessageData, type PartData } from './session-message.js'
import type { SessionSource, SessionParser, ParsedProviderCall } from './types.js'

// OpenCode 1.1+ stores sessions as file-based JSON instead of a SQLite DB:
//   storage/session/<projectID>/<sessionID>.json   session metadata
//   storage/message/<sessionID>/<messageID>.json    one file per message
//   storage/part/<messageID>/<partID>.json          one file per part
// The message/part shape matches the SQLite layout, so the per-message build
// logic is shared via buildAssistantCall.

type SessionMeta = {
  id?: string
  directory?: string
  title?: string
  time?: { created?: number }
}

type FileMessageData = MessageData & {
  id?: string
  time?: { created?: number }
}

// Current-shape assistant messages carry modelID alongside a separate
// providerID (e.g. providerID 'opencode-go' + modelID 'muse-spark'). The
// shared builder keys pricing off a single model string, so qualify the pair
// here: without it the call still yields but prices against a bare model that
// never matches the rate table and falls back to the stored cost.
function withQualifiedModel(data: FileMessageData): FileMessageData {
  const carrier = data as unknown as { providerID?: unknown; modelID?: unknown }
  if (typeof carrier.providerID !== 'string' || typeof carrier.modelID !== 'string') return data
  const providerID = carrier.providerID.trim()
  const modelID = carrier.modelID.trim()
  if (!providerID || !modelID || modelID.includes('/')) return data
  return { ...data, modelID: `${providerID}/${modelID}` }
}

async function readJson<T>(path: string): Promise<T | null> {
  try {
    return JSON.parse(await readFile(path, 'utf8')) as T
  } catch {
    return null
  }
}

async function readParts(dataDir: string, messageId: string): Promise<PartData[]> {
  const dir = join(dataDir, 'storage', 'part', messageId)
  let files: string[]
  try {
    files = (await readdir(dir)).sort()
  } catch {
    return []
  }
  const parts: PartData[] = []
  for (const f of files) {
    if (!f.endsWith('.json')) continue
    const part = await readJson<PartData>(join(dir, f))
    if (part) parts.push(part)
  }
  return parts
}

export async function discoverOpenCodeFileSessions(
  dataDir: string,
  providerName: string,
): Promise<SessionSource[]> {
  const sessionRoot = join(dataDir, 'storage', 'session')
  let entries: string[]
  try {
    entries = await readdir(sessionRoot)
  } catch {
    return []
  }

  const sources: SessionSource[] = []
  for (const entry of entries) {
    // Current-shape stores may place session JSON directly under
    // storage/session/*.json (flat) as well as the nested
    // storage/session/<project>/*.json layout. A flat file that parses as a
    // session is a session; only a directory read failure that is also not a
    // session file is skipped, so neither shape reads as a silent zero.
    if (entry.endsWith('.json')) {
      const flatPath = join(sessionRoot, entry)
      const flatMeta = await readJson<SessionMeta>(flatPath)
      if (flatMeta?.id) {
        sources.push({
          path: flatPath,
          project: sanitize(flatMeta.directory || flatMeta.title || ''),
          provider: providerName,
        })
        continue
      }
    }
    let files: string[]
    try {
      files = await readdir(join(sessionRoot, entry))
    } catch {
      continue
    }
    for (const f of files) {
      if (!f.endsWith('.json')) continue
      const path = join(sessionRoot, entry, f)
      const meta = await readJson<SessionMeta>(path)
      if (!meta?.id) continue
      sources.push({
        path,
        project: sanitize(meta.directory || meta.title || ''),
        provider: providerName,
      })
    }
  }
  return sources
}

export function createOpenCodeFileSessionParser(
  source: SessionSource,
  seenKeys: Set<string>,
  dataDir: string,
  providerName: string,
): SessionParser {
  return {
    async *parse(): AsyncGenerator<ParsedProviderCall> {
      // A discovered session file that is gone by parse time is absence
      // (R1.2): stay silent. A file that exists but no longer parses as a
      // session is a present-but-unreadable store (R1.3): throw with its
      // reason rather than masquerading the shift as an empty provider.
      let raw: string
      try {
        raw = await readFile(source.path, 'utf8')
      } catch (err) {
        if ((err as { code?: unknown }).code === 'ENOENT') return
        throw new Error(
          `cannot read OpenCode session file ${source.path}: ${err instanceof Error ? err.message : String(err)}`,
        )
      }
      let meta: SessionMeta | null = null
      try {
        meta = JSON.parse(raw) as SessionMeta
      } catch (err) {
        throw new Error(
          `cannot parse OpenCode session file ${source.path}: ${err instanceof Error ? err.message : String(err)}`,
        )
      }
      if (!meta?.id) {
        throw new Error(
          `OpenCode session file ${source.path} has an unrecognized schema (missing session id)`,
        )
      }
      const sessionId = meta.id

      const messageDir = join(dataDir, 'storage', 'message', sessionId)
      let messageFiles: string[]
      try {
        messageFiles = await readdir(messageDir)
      } catch {
        return
      }

      const messages: Array<{ id: string; data: FileMessageData }> = []
      for (const f of messageFiles) {
        if (!f.endsWith('.json')) continue
        const data = await readJson<FileMessageData>(join(messageDir, f))
        if (!data) continue
        messages.push({ id: data.id ?? f.replace(/\.json$/, ''), data })
      }
      messages.sort((a, b) => {
        const byTime = (a.data.time?.created ?? 0) - (b.data.time?.created ?? 0)
        if (byTime !== 0) return byTime
        return a.id < b.id ? -1 : a.id > b.id ? 1 : 0
      })

      let currentUserMessage = ''
      for (const { id, data } of messages) {
        if (data.role === 'user') {
          const parts = await readParts(dataDir, id)
          const text = parts
            .filter((p) => p.type === 'text')
            .map((p) => p.text ?? '')
            .filter(Boolean)
            .join(' ')
          if (text) currentUserMessage = text
          continue
        }

        if (data.role !== 'assistant' && data.role !== 'model') continue

        const dedupKey = `${providerName}:${sessionId}:${id}`
        if (seenKeys.has(dedupKey)) continue

        const parts = await readParts(dataDir, id)
        const call = buildAssistantCall({
          providerName,
          dedupKey,
          sessionId,
          data: withQualifiedModel(data),
          parts,
          timeCreatedMs: data.time?.created ?? meta.time?.created ?? 0,
          userMessage: currentUserMessage,
        })
        if (!call) continue

        seenKeys.add(dedupKey)
        yield call
      }
    },
  }
}
