// Codex session-file content reader.
//
// Codex is the only harness surveyed that stores a real system prompt
// (`session_meta.payload.base_instructions.text`) and a real context window
// (`event_msg.payload.model_context_window`, also mirrored under
// `payload.info` on `token_count` events). Nothing here is guessed from
// names, roles, or file size.
//
// Two mappings are load-bearing and easy to get backwards:
//
//   * Tool results are bucketed on `response_item.payload.type`, never on
//     `role`. Harnesses disagree about which role owns a tool result; using
//     role would silently move file contents into conversation (R7.2).
//   * `tool_definitions` is never emitted. Codex records tool *names*
//     (`response_item.payload.name`) and never the schemas a definition
//     bucket would claim to measure. Emitting a bucket from names would
//     present a measurement that does not exist (R8.5, R10.1).
//
// `session_meta.payload.context_window` is a UI window id, not the model
// window — reading it as a token limit would invent a number.

import { createReadStream } from 'node:fs'
import { createInterface } from 'node:readline'

import type { ContentPart } from '../../canon/types.js'
import type { ContentReader, ReaderTurn } from './types.js'

const MESSAGE_ITEM = 'message'
const TOOL_RESULT_TYPES = new Set(['function_call_output', 'custom_tool_call_output'])

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

/** Skip a line that is empty, truncated, or not an object. Never throw. */
function parseLine(line: string): Record<string, unknown> | undefined {
  const trimmed = line.trim()
  if (trimmed === '') return undefined
  try {
    const parsed: unknown = JSON.parse(trimmed)
    return isRecord(parsed) ? parsed : undefined
  } catch {
    return undefined
  }
}

function payloadOf(entry: Record<string, unknown>): Record<string, unknown> | undefined {
  return isRecord(entry['payload']) ? entry['payload'] : undefined
}

/**
 * The system prompt Codex actually stored. `base_instructions` is an object
 * with a `text` field in every file surveyed; a bare string is accepted if
 * that is what the line carries, but an empty value is absence, not a prompt.
 */
function systemPromptText(payload: Record<string, unknown>): string | undefined {
  const base = payload['base_instructions']
  if (typeof base === 'string' && base !== '') return base
  if (!isRecord(base)) return undefined
  const text = base['text']
  return typeof text === 'string' && text !== '' ? text : undefined
}

/**
 * Workspace instructions Codex persisted as AGENTS.md. Developer-role
 * messages are not this: they are still `type: message` and belong in
 * conversation. Instruction flags (`apps_instructions: true`) are booleans,
 * not content, and must not be stringified into a bucket.
 */
function agentsMdText(payload: Record<string, unknown>): string | undefined {
  const state = payload['state']
  if (!isRecord(state)) return undefined
  const agents = state['agents_md']
  if (!isRecord(agents)) return undefined
  const text = agents['text']
  return typeof text === 'string' && text !== '' ? text : undefined
}

/**
 * Model context window. `task_started` stores it at payload level;
 * `token_count` stores the same integer under `info`. Either location is
 * evidence; a missing field is not a zero.
 */
function contextWindowOf(payload: Record<string, unknown>): number | undefined {
  const direct = payload['model_context_window']
  if (typeof direct === 'number' && Number.isFinite(direct)) return direct
  const info = payload['info']
  if (!isRecord(info)) return undefined
  const nested = info['model_context_window']
  return typeof nested === 'number' && Number.isFinite(nested) ? nested : undefined
}

function messageText(payload: Record<string, unknown>): string | undefined {
  const content = payload['content']
  if (typeof content === 'string' && content !== '') return content
  if (!Array.isArray(content)) return undefined
  const texts: string[] = []
  for (const block of content) {
    if (!isRecord(block)) continue
    const text = block['text']
    if (typeof text === 'string' && text !== '') texts.push(text)
  }
  return texts.length > 0 ? texts.join('\n') : undefined
}

function toolResultText(payload: Record<string, unknown>): string | undefined {
  const output = payload['output']
  if (typeof output === 'string' && output !== '') return output
  if (isRecord(output) || Array.isArray(output)) return JSON.stringify(output)
  return undefined
}

function snapshot(state: {
  systemPrompt?: string
  instructionContext?: string
  items: ContentPart[]
  sessionId?: string
  contextWindow?: number
}): ReaderTurn {
  const parts: ContentPart[] = []
  let order = 0
  if (state.systemPrompt !== undefined) {
    parts.push({ part: 'system_prompt', text: state.systemPrompt, order: order++ })
  }
  if (state.instructionContext !== undefined) {
    parts.push({ part: 'instruction_context', text: state.instructionContext, order: order++ })
  }
  for (const item of state.items) {
    parts.push({ ...item, order: order++ })
  }
  return {
    parts,
    ...(state.sessionId !== undefined ? { sessionId: state.sessionId } : {}),
    ...(state.contextWindow !== undefined ? { contextWindow: state.contextWindow } : {}),
  }
}

function hasAnything(turn: ReaderTurn): boolean {
  return turn.parts.length > 0 || turn.sessionId !== undefined || turn.contextWindow !== undefined
}

/**
 * Codex logs items incrementally; a turn's context is the resident prompt
 * plus every item recorded up to that `token_count`. Yielding only the
 * delta would make later turns look empty and dump the missing history
 * into the residual, which is the failure composition exists to prevent.
 */
export const codexReader: ContentReader = {
  async *read(filePath: string): AsyncGenerator<ReaderTurn> {
    const stream = createReadStream(filePath, { encoding: 'utf-8' })
    const rl = createInterface({ input: stream, crlfDelay: Infinity })

    const state: {
      systemPrompt?: string
      instructionContext?: string
      items: ContentPart[]
      sessionId?: string
      contextWindow?: number
    } = { items: [] }
    let dirty = false
    let yielded = false

    const flush = (): ReaderTurn | undefined => {
      const turn = snapshot(state)
      if (!hasAnything(turn)) return undefined
      dirty = false
      yielded = true
      return turn
    }

    try {
      for await (const line of rl) {
        const entry = parseLine(line)
        if (entry === undefined) continue

        const type = entry['type']
        const payload = payloadOf(entry)

        if (type === 'session_meta' && payload !== undefined) {
          const sessionId = payload['session_id']
          if (typeof sessionId === 'string' && sessionId !== '') {
            state.sessionId = sessionId
            dirty = true
          }
          const prompt = systemPromptText(payload)
          if (prompt !== undefined) {
            state.systemPrompt = prompt
            dirty = true
          }
          continue
        }

        if (type === 'world_state' && payload !== undefined) {
          const text = agentsMdText(payload)
          if (text !== undefined) {
            state.instructionContext = text
            dirty = true
          }
          continue
        }

        if (type === 'event_msg' && payload !== undefined) {
          const window = contextWindowOf(payload)
          if (window !== undefined) state.contextWindow = window

          // A token_count is one model invocation — the turn boundary.
          if (payload['type'] === 'token_count') {
            const turn = flush()
            if (turn !== undefined) yield turn
          }
          continue
        }

        if (type === 'response_item' && payload !== undefined) {
          const itemType = payload['type']
          if (itemType === MESSAGE_ITEM) {
            const text = messageText(payload)
            if (text !== undefined) {
              state.items.push({ part: 'conversation_history', text })
              dirty = true
            }
            continue
          }
          if (typeof itemType === 'string' && TOOL_RESULT_TYPES.has(itemType)) {
            const text = toolResultText(payload)
            if (text !== undefined) {
              state.items.push({ part: 'tool_result_content', text })
              dirty = true
            }
          }
          // function_call / custom_tool_call / reasoning: names and traces,
          // not content buckets. Deliberately dropped.
        }
      }

      // Content after the last token_count, or a file that never reported one.
      if (dirty || !yielded) {
        const turn = flush()
        if (turn !== undefined) yield turn
      }
    } finally {
      rl.close()
      stream.destroy()
    }
  },
}
