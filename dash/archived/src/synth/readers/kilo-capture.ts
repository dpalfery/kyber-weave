// Reads what the Kilo capture plugin recorded (capture/kilo-plugin/source.ts): the system
// prompt and tool definitions Kilo sent on each model call. kilo.db never stores them, so
// this is the only source for those two buckets.
//
// A call is found by the user message that started it plus the assistant message's time:
// an agent loop makes several assistant messages (and several model calls) for one user
// message, so the call nearest in time wins. Anything missing, corrupt or unreadable reads
// as "not captured", never as an empty bucket.

import { existsSync, readFileSync, statSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

export type KiloCapturedCall = {
  /** Absent when the blob is missing; never an empty string standing in for absence. */
  systemPrompt?: string
  tools: { id: string; text: string }[]
}

export type KiloCapture = {
  forMessage: (sessionId: string, userMessageId: string, atMs: number) => KiloCapturedCall | undefined
}

type IndexRow = {
  ts: number
  sessionID: string
  messageID?: string
  systemHash?: string
  toolHashes: Record<string, string>
}

const SHA256 = /^[0-9a-f]{64}$/

/** The capture store: the plugin's directory, or its override. */
export function kiloCaptureDir(): string {
  return process.env.KYBERDASH_KILO_CAPTURE_DIR || join(homedir(), '.kyberdash', 'capture', 'kilo')
}

function readBlob(dir: string, hash: unknown): string | undefined {
  // Only a bare sha256 names a blob, so an index line cannot point outside the store.
  if (typeof hash !== 'string' || !SHA256.test(hash)) return undefined
  try {
    return readFileSync(join(dir, 'blobs', hash), 'utf8')
  } catch {
    return undefined
  }
}

function parseIndex(dir: string): Map<string, IndexRow[]> {
  const bySession = new Map<string, IndexRow[]>()
  const path = join(dir, 'index.jsonl')
  if (!existsSync(path)) return bySession
  let text: string
  try {
    text = readFileSync(path, 'utf8')
  } catch {
    return bySession
  }
  for (const line of text.split('\n')) {
    let row: Partial<IndexRow>
    try {
      row = JSON.parse(line) as Partial<IndexRow>
    } catch {
      continue
    }
    if (typeof row.ts !== 'number' || typeof row.sessionID !== 'string') continue
    const list = bySession.get(row.sessionID) ?? []
    list.push({
      ts: row.ts,
      sessionID: row.sessionID,
      messageID: typeof row.messageID === 'string' ? row.messageID : undefined,
      systemHash: row.systemHash,
      toolHashes: row.toolHashes !== null && typeof row.toolHashes === 'object' ? row.toolHashes : {},
    })
    bySession.set(row.sessionID, list)
  }
  return bySession
}

const cache = new Map<string, { stamp: string; index: Map<string, IndexRow[]> }>()

/** Parsed once per store version; the index only ever grows, so size and mtime key it. */
function indexFor(dir: string): Map<string, IndexRow[]> {
  let stamp = 'none'
  try {
    const stat = statSync(join(dir, 'index.jsonl'))
    stamp = `${stat.size}:${stat.mtimeMs}`
  } catch {
    // no index yet
  }
  const hit = cache.get(dir)
  if (hit !== undefined && hit.stamp === stamp) return hit.index
  const index = parseIndex(dir)
  cache.set(dir, { stamp, index })
  return index
}

export function readKiloCapture(dir: string = kiloCaptureDir()): KiloCapture {
  const index = indexFor(dir)
  return {
    forMessage(sessionId, userMessageId, atMs) {
      const candidates = (index.get(sessionId) ?? []).filter((row) => row.messageID === userMessageId)
      if (candidates.length === 0) return undefined
      const row = candidates.reduce((best, next) =>
        Math.abs(next.ts - atMs) < Math.abs(best.ts - atMs) ? next : best,
      )
      const tools = Object.entries(row.toolHashes).flatMap(([id, hash]) => {
        const text = readBlob(dir, hash)
        return text === undefined ? [] : [{ id, text }]
      })
      const systemPrompt = readBlob(dir, row.systemHash)
      return { ...(systemPrompt !== undefined ? { systemPrompt } : {}), tools }
    },
  }
}
