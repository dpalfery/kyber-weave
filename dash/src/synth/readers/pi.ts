// pi session content reader.
//
// pi's JSONL transcript records message blocks, while its collector is the
// authority for token counters. This reader emits only stored content parts;
// D7 joins them onto an OTel turn only when that turn has no parts.

import { createReadStream } from 'node:fs'
import { readFile } from 'node:fs/promises'
import { homedir } from 'node:os'
import { join } from 'node:path'
import { createInterface } from 'node:readline'

import { detectUserCorrection } from '../../canon/outcome.js'
import type { ContentPart } from '../../canon/types.js'
import type { DateRange } from '../../types.js'
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

/**
 * Options for the pi reader's declared-window lookup. The models-store path
 * defaults to `<home>/.pi/agent/models-store.json` resolved from HOME and
 * is injectable through either field.
 */
export type PiReaderOptions = {
  /** Explicit store path, bypassing HOME resolution. */
  modelsStorePath?: string
  /** Home directory the store path resolves from; defaults to HOME. */
  homeDir?: string
}

/** Resolve the pi models-store path from a home directory (HOME by default). */
export function piModelsStorePath(homeDir: string = process.env['HOME'] ?? homedir()): string {
  return join(homeDir, '.pi', 'agent', 'models-store.json')
}

/**
 * Resolve the turn's model the way the provider parser does: the first
 * assistant message's own model wins, falling back to the session model from
 * `model_change`. Returns undefined when the transcript named no model, so
 * no store lookup is attempted — an unknown model must never gain a default.
 */
export function turnModel(firstAssistantModel: string | undefined, resolvedModel: string): string | undefined {
  if (firstAssistantModel !== undefined) return firstAssistantModel
  return resolvedModel !== '' ? resolvedModel : undefined
}

/** The segment after the last `/`: store ids are bare while transcripts may qualify. */
function bareModelId(model: string): string {
  return model.includes('/') ? model.slice(model.lastIndexOf('/') + 1) : model
}

/**
 * Find the declared context window for a turn model in a parsed
 * `models-store.json` document (`{ [provider]: { models: [{ id,
 * contextWindow }] } }`). Matches a bare store id against either the full
 * transcript model or its bare segment. Returns undefined when nothing
 * matches or the matched entry names no usable window — never a default.
 */
export function declaredWindowForModel(model: string | undefined, store: unknown): number | undefined {
  if (model === undefined || model === '') return undefined
  if (!isRecord(store)) return undefined
  const want = bareModelId(model)
  if (want === '') return undefined
  for (const group of Object.values(store)) {
    if (!isRecord(group)) continue
    const models = group['models']
    if (!Array.isArray(models)) continue
    for (const entry of models) {
      if (!isRecord(entry)) continue
      const id = entry['id']
      if (typeof id !== 'string' || id === '') continue
      if (id !== model && bareModelId(id) !== want) continue
      const window = entry['contextWindow']
      return typeof window === 'number' && Number.isFinite(window) && window > 0 ? window : undefined
    }
  }
  return undefined
}

/**
 * Read the declared context window for a turn model from the models store.
 * A missing or unreadable file, unparseable JSON, or a wrongly shaped
 * document all mean no window — never a default, never an error.
 */
export async function loadDeclaredWindow(
  model: string | undefined,
  storePath: string,
): Promise<number | undefined> {
  if (model === undefined) return undefined
  let raw: string
  try {
    raw = await readFile(storePath, 'utf-8')
  } catch {
    return undefined
  }
  let store: unknown
  try {
    store = JSON.parse(raw) as unknown
  } catch {
    return undefined
  }
  return declaredWindowForModel(model, store)
}

/** File-derived content reader for pi JSONL session transcripts. */
export const piReader: ContentReader = {
  async *read(filePath: string, _dateRange?: DateRange, options?: PiReaderOptions): AsyncGenerator<ReaderTurn> {
    const stream = createReadStream(filePath, { encoding: 'utf-8' })
    const lines = createInterface({ input: stream, crlfDelay: Infinity })
    const parts: ContentPart[] = []
    const order = { value: 0 }
    let sessionId: string | undefined
    let observedExitCode: number | undefined
    let observedTerminationReason: string | undefined
    let hasCorrection = false
    let detectedCorrectionRule: string | undefined
    // The turn's model, resolved the way the provider parser resolves it: a
    // session-level `model_change` entry sets the session model, and the
    // first assistant message's own model wins for the turn. Stays undefined
    // when the transcript named no model, so no store lookup is attempted.
    let resolvedModel = ''
    let firstAssistantModel: string | undefined

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
        if (entry['type'] === 'model_change') {
          if (typeof entry['model'] === 'string' && entry['model'] !== '') resolvedModel = entry['model']
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
        if (msgRole === 'assistant' && firstAssistantModel === undefined) {
          const model = entry['message']['model']
          if (typeof model === 'string' && model !== '') firstAssistantModel = model
        }
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
      // The declared window is configuration, not telemetry: a missing or
      // unparseable store yields no window, never a default, and the lookup
      // never throws.
      const declaredContextWindow = await loadDeclaredWindow(
        turnModel(firstAssistantModel, resolvedModel),
        options?.modelsStorePath ?? piModelsStorePath(options?.homeDir),
      )
      yield {
        parts,
        ...(sessionId !== undefined ? { sessionId } : {}),
        ...(declaredContextWindow !== undefined ? { declaredContextWindow } : {}),
        ...(observedTerminationReason !== undefined ? { terminationReason: observedTerminationReason } : {}),
        ...(observedExitCode !== undefined ? { exitCode: observedExitCode } : {}),
        ...(hasCorrection ? { isCorrection: true, correctionRule: detectedCorrectionRule } : {}),
      }
    }
  },
}

export default piReader
