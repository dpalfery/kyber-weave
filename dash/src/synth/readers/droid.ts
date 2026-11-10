// Droid (Factory) session content reader.
//
// Droid writes a JSONL transcript: a `session_start` entry, then `message`
// entries whose `message.content` holds typed blocks. This reader emits one
// ReaderTurn per assistant message, keyed by that message's own id. Tool results
// arrive in the user message that follows the call, so they are attached to the
// most recent assistant turn. Counters are not read here; the provider parser
// owns them and stamps the same message id as the pairing key.

import { createReadStream } from 'node:fs'
import { createInterface } from 'node:readline'

import type { ContentPart } from '../../canon/types.js'
import type { DateRange } from '../../types.js'
import type { ContentReader, ReaderToolCall, ReaderToolResult, ReaderTurn } from './types.js'

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function blocksOf(content: unknown): unknown[] {
  if (Array.isArray(content)) return content
  return typeof content === 'string' ? [{ type: 'text', text: content }] : []
}

function toolArguments(input: unknown): Record<string, unknown> | string {
  if (typeof input === 'string') return input
  return isRecord(input) ? input : {}
}

/** Flatten a tool_result body to text: strings as-is, text blocks by text, others JSON-encoded. */
function toolResultText(content: unknown): string {
  if (typeof content === 'string') return content
  if (content === null || content === undefined) return ''
  const items = Array.isArray(content) ? content : [content]
  return items
    .map((item) =>
      typeof item === 'string'
        ? item
        : isRecord(item) && typeof item['text'] === 'string'
          ? item['text']
          : JSON.stringify(item),
    )
    .filter((text) => text !== '')
    .join('\n')
}

type PendingTurn = {
  nativeRecordId: string
  parts: ContentPart[]
  toolCalls: ReaderToolCall[]
  toolResults: ReaderToolResult[]
}

function finish(pending: PendingTurn, sessionId: string | undefined): ReaderTurn {
  return {
    parts: pending.parts,
    ...(pending.nativeRecordId !== '' ? { nativeRecordId: pending.nativeRecordId } : {}),
    ...(sessionId !== undefined ? { sessionId } : {}),
    ...(pending.toolCalls.length > 0 ? { toolCalls: pending.toolCalls } : {}),
    ...(pending.toolResults.length > 0 ? { toolResults: pending.toolResults } : {}),
  }
}

/** File-derived content reader for Droid (Factory) JSONL session transcripts. */
export const droidReader: ContentReader = {
  // Pairing is keyed by message id. A call without that key must not take the
  // turn at the same position: the reader yields one turn per assistant
  // message, and calls are only produced for some of those messages.
  positionalPairingUnsafe: true,

  async *read(filePath: string, _dateRange?: DateRange): AsyncGenerator<ReaderTurn> {
    const stream = createReadStream(filePath, { encoding: 'utf-8' })
    const lines = createInterface({ input: stream, crlfDelay: Infinity })
    let sessionId: string | undefined
    let pending: PendingTurn | undefined
    // Mirrors the provider's `msg-N` fallback, where N counts the calls the provider has
    // emitted so far: an assistant message with neither a tool_use nor text emits none.
    let emittedCalls = 0

    try {
      for await (const line of lines) {
        let entry: unknown
        try {
          entry = JSON.parse(line)
        } catch {
          continue
        }
        if (!isRecord(entry)) continue

        if (entry['type'] === 'session_start') {
          if (typeof entry['id'] === 'string' && entry['id'] !== '') sessionId = entry['id']
          continue
        }
        if (entry['type'] !== 'message' || !isRecord(entry['message'])) continue

        const role = entry['message']['role']
        const blocks = blocksOf(entry['message']['content'])
        const timestamp = typeof entry['timestamp'] === 'string' ? entry['timestamp'] : undefined

        if (role === 'assistant') {
          if (pending !== undefined) yield finish(pending, sessionId)
          const id = typeof entry['id'] === 'string' && entry['id'] !== '' ? entry['id'] : `msg-${emittedCalls}`
          let emits = false
          pending = { nativeRecordId: id, parts: [], toolCalls: [], toolResults: [] }
          let order = 0
          for (const block of blocks) {
            if (!isRecord(block)) continue
            if (block['type'] === 'text' && typeof block['text'] === 'string' && block['text'] !== '') {
              emits = true
              pending.parts.push({ part: 'conversation_history', text: block['text'], order: order++ })
            } else if (block['type'] === 'tool_use') {
              emits = true
              const name = typeof block['name'] === 'string' ? block['name'] : ''
              const callId = typeof block['id'] === 'string' ? block['id'] : ''
              if (name !== '' && callId !== '') {
                pending.toolCalls.push({
                  id: callId,
                  name,
                  arguments: toolArguments(block['input']),
                  ...(timestamp !== undefined ? { timestamp } : {}),
                })
              }
            }
          }
          if (emits) emittedCalls++
          continue
        }

        if (role === 'user' && pending !== undefined) {
          for (const block of blocks) {
            if (!isRecord(block) || block['type'] !== 'tool_result') continue
            const toolCallId = typeof block['tool_use_id'] === 'string' ? block['tool_use_id'] : ''
            if (toolCallId === '') continue
            pending.toolResults.push({
              toolCallId,
              content: toolResultText(block['content']),
              isError: block['is_error'] === true,
              ...(timestamp !== undefined ? { timestamp } : {}),
            })
          }
        }
      }
      if (pending !== undefined) yield finish(pending, sessionId)
    } finally {
      lines.close()
      stream.destroy()
    }
  },
}

export default droidReader
