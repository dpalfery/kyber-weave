// VS Code Copilot chat-session content reader.
//
// The journal contains a reconstructed request array, not incremental chat
// events. Each snapshot therefore contains earlier requests and their replies,
// plus the current request. The current reply is input to a later request only.
// VS Code does not persist the runtime system prompt or tool schemas, so this
// reader leaves those buckets absent.

import { readSessionFile } from '../../ingest/fs-utils.js'
import { replayChatSessionJournal } from '../../providers/copilot.js'
import type { ContentPart } from '../../canon/types.js'
import type { ContentReader, ReaderTurn } from './types.js'

type JsonRecord = Record<string, unknown>

function isRecord(value: unknown): value is JsonRecord {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function recordText(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() !== '' ? value : undefined
}

function textFromParts(value: unknown): string | undefined {
  if (!Array.isArray(value)) return undefined
  const texts = value.flatMap((part) => {
    if (typeof part === 'string') return recordText(part) ?? []
    if (!isRecord(part)) return []
    return recordText(part['text']) ?? []
  })
  return texts.length > 0 ? texts.join('\n') : undefined
}

function messageText(request: JsonRecord): string | undefined {
  const message = request['message']
  if (typeof message === 'string') return recordText(message)
  if (!isRecord(message)) return undefined
  return recordText(message['text']) ?? textFromParts(message['parts'])
}

function responseText(request: JsonRecord): string | undefined {
  const result = isRecord(request['result']) ? request['result'] : undefined
  const response = isRecord(request['response'])
    ? request['response']
    : result !== undefined && isRecord(result['response'])
      ? result['response']
      : undefined
  if (response === undefined) return recordText(request['response'])
  return recordText(response['text']) ?? textFromParts(response['parts'])
}

function modeInstructions(request: JsonRecord): string | undefined {
  const message = isRecord(request['message']) ? request['message'] : undefined
  const entries = [recordText(message?.['modeInstructions']), recordText(request['modeInstructions'])]
    .filter((text): text is string => text !== undefined)
  const unique = [...new Set(entries)]
  return unique.length > 0 ? unique.join('\n') : undefined
}

function part(part: ContentPart['part'], text: string, order: number): ContentPart {
  return { part, text, order }
}

function requestId(request: JsonRecord): string | undefined {
  return recordText(request['requestId'])
}

export const copilotVscodeReader: ContentReader = {
  async *read(filePath: string): AsyncGenerator<ReaderTurn> {
    const content = await readSessionFile(filePath)
    if (content === null) return

    const root = replayChatSessionJournal(content)
    if (!isRecord(root) || !Array.isArray(root['requests'])) return

    const sessionId = recordText(root['sessionId'])
    const requests = Array.isArray(root['requests']) ? root['requests'] : []

    for (let index = 0; index < requests.length; index++) {
      const current = requests[index]
      if (!isRecord(current)) continue
      const parts: ContentPart[] = []
      let order = 0

      for (let previousIndex = 0; previousIndex < index; previousIndex++) {
        const previous = requests[previousIndex]
        if (!isRecord(previous)) continue
        const previousMessage = messageText(previous)
        if (previousMessage !== undefined) {
          parts.push(part('conversation_history', previousMessage, order++))
        }
        const previousResponse = responseText(previous)
        if (previousResponse !== undefined) {
          parts.push(part('conversation_history', previousResponse, order++))
        }
      }

      const currentMessage = messageText(current)
      if (currentMessage !== undefined) {
        parts.push(part('conversation_history', currentMessage, order++))
      }

      const instructions = modeInstructions(current)
      if (instructions !== undefined) {
        parts.push(part('instruction_context', instructions, order++))
      }

      const nativeRecordId = requestId(current) ?? `request-${index}`
      yield {
        parts,
        ...(sessionId !== undefined ? { sessionId } : {}),
        nativeRecordId,
      }
    }
  },
}

export default copilotVscodeReader
