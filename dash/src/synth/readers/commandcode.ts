// Command Code session content reader.
//
// Command Code writes a pi-shaped JSONL tree: a `session` header, then
// `message` entries whose `message.content` holds typed blocks. This reader
// emits one ReaderTurn per assistant message, keyed by that message's own id.
// Tool results arrive in the user message that follows the call, so they are
// attached to the most recent assistant turn. Counters are not read here; the
// provider parser owns them; the pairing key is the assistant message id (D5).

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

/**
 * Flatten a tool_result body to text, mirroring the claude reader's array
 * branch: string stays as-is, text blocks yield their text, other items are
 * JSON-encoded, and items are joined by newlines. Null or empty yields ''.
 */
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

/** File-derived content reader for Command Code JSONL session transcripts. */
export const commandcodeReader: ContentReader = {
  // Pairing is keyed by message id. A call without that key must not take the
  // turn at the same position: the reader yields one turn per assistant
  // message, and calls are only produced for assistant messages with usage.
  positionalPairingUnsafe: true,

  async *read(filePath: string, _dateRange?: DateRange): AsyncGenerator<ReaderTurn> {
    const stream = createReadStream(filePath, { encoding: 'utf-8' })
    const lines = createInterface({ input: stream, crlfDelay: Infinity })
    let sessionId: string | undefined
    let pending: PendingTurn | undefined

    try {
      for await (const line of lines) {
        let entry: unknown
        try {
          entry = JSON.parse(line)
        } catch {
          continue
        }
        if (!isRecord(entry)) continue

        if (entry['type'] === 'session') {
          if (typeof entry['id'] === 'string' && entry['id'] !== '') sessionId = entry['id']
          continue
        }
        if (entry['type'] !== 'message' || !isRecord(entry['message'])) continue

        const role = entry['message']['role']
        const blocks = blocksOf(entry['message']['content'])

        if (role === 'assistant') {
          if (pending !== undefined) yield finish(pending, sessionId)
          const id = typeof entry['id'] === 'string' && entry['id'] !== '' ? entry['id'] : undefined
          pending = { nativeRecordId: id ?? '', parts: [], toolCalls: [], toolResults: [] }
          const order = { value: 0 }
          for (const block of blocks) {
            if (!isRecord(block)) continue
            if (block['type'] === 'text' && typeof block['text'] === 'string' && block['text'] !== '') {
              pending.parts.push({ part: 'conversation_history', text: block['text'], order: order.value++ })
            } else if (block['type'] === 'tool_use') {
              const name = typeof block['name'] === 'string' ? block['name'] : ''
              const callId = typeof block['id'] === 'string' ? block['id'] : ''
              if (name !== '' || callId !== '') {
                pending.toolCalls.push({ id: callId, name, arguments: toolArguments(block['input']) })
              }
            }
          }
          continue
        }

        if (role === 'user' && pending !== undefined) {
          for (const block of blocks) {
            if (!isRecord(block) || block['type'] !== 'tool_result') continue
            const toolCallId = typeof block['tool_use_id'] === 'string' ? block['tool_use_id'] : ''
            if (toolCallId === '') continue
            const content = toolResultText(block['content'])
            const isError = block['is_error'] === true
            pending.toolResults.push({ toolCallId, content, isError })
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

export default commandcodeReader
