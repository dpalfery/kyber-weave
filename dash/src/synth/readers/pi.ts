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

function nonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
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
export function piModelsStorePath(homeDir: string = process.env['HOME'] || homedir()): string {
  return join(homeDir, '.pi', 'agent', 'models-store.json')
}

/** The model a turn ran on, with the provider group the transcript named for it. */
export type PiTurnModel = {
  model: string
  provider?: string
}

/**
 * Resolve the turn's model the way the provider parser does: the first
 * assistant message's own model wins, falling back to the session model from
 * `model_change`. The provider follows the same order. Returns undefined when
 * the transcript named no model, so no store lookup is attempted — an unknown
 * model must never gain a default.
 */
export function turnModel(
  firstAssistant: { model?: string; provider?: string },
  session: { model?: string; provider?: string },
): PiTurnModel | undefined {
  const model = firstAssistant.model ?? session.model
  if (model === undefined || model === '') return undefined
  const provider = firstAssistant.provider ?? session.provider
  return provider !== undefined && provider !== '' ? { model, provider } : { model }
}

function usableWindow(entry: Record<string, unknown>): number | undefined {
  const window = entry['contextWindow']
  return typeof window === 'number' && Number.isFinite(window) && window > 0 ? window : undefined
}

function groupEntries(store: Record<string, unknown>, provider: string): Record<string, unknown>[] | undefined {
  if (!Object.hasOwn(store, provider)) return undefined
  const group = store[provider]
  if (!isRecord(group) || !Array.isArray(group['models'])) return undefined
  return group['models'].filter(isRecord)
}

function windowInGroup(store: Record<string, unknown>, provider: string, id: string): number | undefined {
  const entry = groupEntries(store, provider)?.find((candidate) => candidate['id'] === id)
  return entry !== undefined ? usableWindow(entry) : undefined
}

/**
 * Find the declared context window for a turn model in a parsed
 * `models-store.json` document (`{ [provider]: { models: [{ id,
 * contextWindow }] } }`).
 *
 * @remarks
 * The same model id can sit under several provider groups with different
 * windows, so a window is returned only for an unambiguous match. With a
 * known provider, only that group is searched, by exact id. Without one, a
 * `group/model` prefix counts as the provider only when it names a store
 * group; otherwise every entry with that exact id must declare the same
 * window. Ids are never truncated at `/`: `org/model` is a different model
 * from a bare `model` under some other provider. Anything else is a guess,
 * and a guess is undefined — never a default.
 */
export function declaredWindowForModel(turn: PiTurnModel | undefined, store: unknown): number | undefined {
  if (turn === undefined || turn.model === '') return undefined
  if (!isRecord(store)) return undefined
  const { model, provider } = turn

  if (provider !== undefined) {
    const exact = windowInGroup(store, provider, model)
    if (exact !== undefined) return exact
    const prefix = `${provider}/`
    return model.startsWith(prefix) ? windowInGroup(store, provider, model.slice(prefix.length)) : undefined
  }

  const slash = model.indexOf('/')
  if (slash > 0 && groupEntries(store, model.slice(0, slash)) !== undefined) {
    return windowInGroup(store, model.slice(0, slash), model.slice(slash + 1))
  }

  let agreed: number | undefined
  for (const group of Object.keys(store)) {
    for (const entry of groupEntries(store, group) ?? []) {
      if (entry['id'] !== model) continue
      const window = usableWindow(entry)
      if (window === undefined || (agreed !== undefined && agreed !== window)) return undefined
      agreed = window
    }
  }
  return agreed
}

/**
 * Read the declared context window for a turn model from the models store.
 * A missing or unreadable file, unparseable JSON, or a wrongly shaped
 * document all mean no window — never a default, never an error.
 */
export async function loadDeclaredWindow(
  model: PiTurnModel | undefined,
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
    // first assistant message's own model wins for the turn. Each source keeps
    // the provider it named, because a model id alone can sit under several
    // provider groups with different windows.
    let sessionModel: { model?: string; provider?: string } = {}
    let firstAssistant: { model?: string; provider?: string } | undefined

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
          const model = nonEmptyString(entry['model'])
          if (model !== undefined) {
            const provider = nonEmptyString(entry['provider'])
            sessionModel = provider !== undefined ? { model, provider } : { model }
          }
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
        if (msgRole === 'assistant' && firstAssistant === undefined) {
          const model = nonEmptyString(entry['message']['model'])
          if (model !== undefined) {
            const provider = nonEmptyString(entry['message']['provider'])
            firstAssistant = provider !== undefined ? { model, provider } : { model }
          }
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
        turnModel(firstAssistant ?? {}, sessionModel),
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
