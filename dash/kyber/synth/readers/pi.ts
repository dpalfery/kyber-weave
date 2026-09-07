// pi session content reader.
//
// pi's JSONL transcript records message blocks, while its collector is the
// authority for token counters. This reader emits only stored content parts;
// D7 joins them onto an OTel turn only when that turn has no parts.

import { createReadStream } from 'node:fs'
import { createInterface } from 'node:readline'

import { detectUserCorrection } from '../../canon/outcome.js'
import type { ContentPart } from '../../canon/types.js'
import type { ContentReader, ReaderTurn } from './types.js'

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function textParts(content: unknown, order: { value: number }): ContentPart[] {
  const blocks = Array.isArray(content) ? content : [content]
  const parts: ContentPart[] = []
  for (const block of blocks) {
    const text = typeof block === 'string'
      ? block
      : isRecord(block) && typeof block['text'] === 'string'
        ? block['text']
        : undefined
    if (text === undefined || text === '') continue
    parts.push({ part: 'conversation_history', text, order: order.value++ })
  }
  return parts
}

/** File-derived content reader for pi JSONL session transcripts. */
export const piReader: ContentReader = {
  async *read(filePath: string): AsyncGenerator<ReaderTurn> {
    const stream = createReadStream(filePath, { encoding: 'utf-8' })
    const lines = createInterface({ input: stream, crlfDelay: Infinity })
    const parts: ContentPart[] = []
    const order = { value: 0 }
    let sessionId: string | undefined
    let observedExitCode: number | undefined
    let observedTerminationReason: string | undefined
    let hasCorrection = false
    let detectedCorrectionRule: string | undefined

    try {
      for await (const line of lines) {
        let entry: unknown
        try {
          entry = JSON.parse(line)
        } catch {
          continue
        }
        if (!isRecord(entry)) continue

        if (entry['type'] === 'session' && typeof entry['id'] === 'string' && entry['id'] !== '') {
          sessionId = entry['id']
          continue
        }
        if (entry['type'] === 'exit' || entry['type'] === 'session_end') {
          observedTerminationReason = typeof entry['reason'] === 'string' ? entry['reason'] : 'completed'
          if (typeof entry['exitCode'] === 'number') observedExitCode = entry['exitCode']
          if (typeof entry['code'] === 'number') observedExitCode = entry['code']
          continue
        }
        if (entry['type'] !== 'message' || !isRecord(entry['message'])) continue

        const msgRole = entry['message']['role']
        const msgContent = entry['message']['content']
        if (msgRole === 'user' || msgRole === undefined) {
          const rawText = typeof msgContent === 'string'
            ? msgContent
            : Array.isArray(msgContent)
              ? msgContent.map((c) => (typeof c === 'string' ? c : isRecord(c) && typeof c['text'] === 'string' ? c['text'] : '')).join('\n')
              : undefined
          if (rawText) {
            const check = detectUserCorrection(rawText)
            if (check.matched) {
              hasCorrection = true
              detectedCorrectionRule = check.rule
            }
          }
        }

        parts.push(...textParts(msgContent, order))
      }
    } finally {
      lines.close()
      stream.destroy()
    }

    if (
      parts.length > 0 ||
      sessionId !== undefined ||
      observedTerminationReason !== undefined ||
      observedExitCode !== undefined ||
      hasCorrection
    ) {
      yield {
        parts,
        ...(sessionId !== undefined ? { sessionId } : {}),
        ...(observedTerminationReason !== undefined ? { terminationReason: observedTerminationReason } : {}),
        ...(observedExitCode !== undefined ? { exitCode: observedExitCode } : {}),
        ...(hasCorrection ? { isCorrection: true, correctionRule: detectedCorrectionRule } : {}),
      }
    }
  },
}

export default piReader
