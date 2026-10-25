// Kilo Code session content reader (decision D7).
//
// Kilo's shared runtime writes ~/.local/share/kilo/kilo.db: one `message` row
// per turn and one `part` row per content fragment. This reader is the content
// half of that store — the SQLite parser already owns its counters — so the
// contract here is which fragments become canonical buckets and which do not.
//
// Two mappings are load-bearing and easy to get backwards:
//
//   * One turn per TOKEN-BEARING assistant message, keyed by the Kilo message
//     id. `buildAssistantCall` also emits a call for a zero-token assistant
//     message that still carries text (a router response), so the reader's
//     turn list and the parser's call list are not the same length. Pairing
//     therefore happens by id (`nativeRecordId`/`pairingId`), never by index,
//     which is why this reader also declares positional pairing unsafe.
//   * kilo.db stores neither the runtime system prompt nor tool-definition
//     schemas. Emitting those buckets from a part name would present a
//     measurement the store does not carry (R8.5, R10.1). They come only from
//     the capture plugin's store (kilo-capture.ts), joined by the user message
//     an assistant message answers; with no capture for a turn, the buckets
//     stay absent.
//
// `tool_result_content` names the tool output currently resident in the
// model's context at that turn: the output a message's own tool parts
// recorded when they completed or errored, or — when the message invoked no
// tool — the last output the model consumed, which is still the tool-result
// evidence it generated against. A text-only follow-up that answered the
// result is not a turn with no tool evidence.

import { existsSync } from 'node:fs'

import { blobToText, openDatabase } from '../../ingest/sqlite.js'
import {
  parseTimestamp,
  type MessageData,
  type PartData,
} from '../../providers/session-message.js'
import type { ContentPart } from '../../canon/types.js'
import type { DateRange } from '../../types.js'

import { readKiloCapture } from './kilo-capture.js'
import type { ContentReader, ReaderToolCall, ReaderToolResult, ReaderTurn } from './types.js'

type MessageRow = {
  session_id: string
  id: string
  time_created: number | null
  data: Uint8Array | string
}

type PartRow = {
  message_id: string
  time_created: number | null
  data: Uint8Array | string
}

type SessionPart = {
  data: PartData
  timeCreatedMs: number | null
}

/** Anything that is not a finite number behaves as absent (PR #264 parity). */
const finite = (value: unknown): number =>
  typeof value === 'number' && Number.isFinite(value) ? value : 0

/**
 * The token counters `buildAssistantCall` totals into a call: a message is
 * turn-bearing when any of them is non-zero. A zero-counter assistant message
 * (a router reply) is still a parser call, so the reader must not emit a turn
 * for it — otherwise the id pairing that follows would steal content.
 */
function tokenTotal(data: MessageData): number {
  return (
    finite(data.tokens?.input) +
    finite(data.usage?.input_tokens) +
    finite(data.tokens_input) +
    finite(data.tokens?.output) +
    finite(data.usage?.output_tokens) +
    finite(data.tokens_output) +
    finite(data.tokens?.reasoning) +
    finite(data.tokens_reasoning) +
    finite(data.tokens?.cache?.read) +
    finite(data.usage?.cache_read_input_tokens) +
    finite(data.tokens_cache_read) +
    finite(data.tokens?.cache?.write) +
    finite(data.usage?.cache_creation_input_tokens) +
    finite(data.tokens_cache_write)
  )
}

/** A text fragment a `text`/`markdown` part genuinely carries, or none. */
function partText(part: PartData): string | undefined {
  if (part.type !== 'text' && part.type !== 'markdown') return undefined
  if (typeof part.text !== 'string') return undefined
  const text = part.text
  return text.trim().length > 0 ? text : undefined
}

/**
 * The output a tool part's state recorded, as the store wrote it. `output`
 * wins because that is where a completed call lands its payload; `error`
 * covers errored states that report only a message. Structured output is
 * serialized rather than dropped — it is the recorded result either way.
 */
function toolOutputText(part: PartData): string {
  const state = part.state
  if (state === undefined) return ''
  if (typeof state.output === 'string') return state.output
  if (state.output !== undefined && state.output !== null) return JSON.stringify(state.output)
  if (typeof state.error === 'string') return state.error
  return ''
}

/** ISO timestamp for a millisecond/`time_created` instant, when usable. */
function timestampOf(ms: number | null | undefined): string | undefined {
  if (typeof ms !== 'number' || !Number.isFinite(ms) || ms <= 0) return undefined
  return parseTimestamp(ms)
}

/** Sibling-reader date rule: untimed rows are eligible only when unbounded. */
function inDateRange(ms: number | null, dateRange: DateRange | undefined): boolean {
  if (dateRange === undefined) return true
  const iso = timestampOf(ms)
  if (iso === undefined) return false
  const instant = new Date(iso)
  return instant >= dateRange.start && instant <= dateRange.end
}

/**
 * Reads one kilo.db session tree into canonical turns. The source path is the
 * parser's own `<dbPath>:<sessionId>` shape (the final colon segment is the
 * session id). An absent store or an absent session yields nothing — absence
 * is a negative existence check, never an error — while a present store that
 * will not open keeps `openDatabase`'s error so the ingest seam reports it.
 */
export const kiloReader: ContentReader = {
  // The parser emits calls the reader has no turn for (zero-token assistant
  // messages): index pairing would attach each turn to its neighbour's call.
  positionalPairingUnsafe: true,

  async *read(filePath: string, dateRange?: DateRange): AsyncGenerator<ReaderTurn> {
    const separator = filePath.lastIndexOf(':')
    if (separator < 0) return
    const dbPath = filePath.slice(0, separator)
    const sessionId = filePath.slice(separator + 1)
    if (dbPath === '' || sessionId === '') return
    if (!existsSync(dbPath)) return

    // An existing store that fails to open is a read failure to report, not
    // a silent zero — `openDatabase` carries the same error the parser path
    // surfaces as a present-but-unreadable problem.
    const db = openDatabase(dbPath)
    try {
      // The same recursive session-tree CTE the parser reads: a session's
      // child sessions hold their own messages and parts under the same root.
      const messageRows = db.query<MessageRow>(
        `WITH RECURSIVE session_tree(id) AS (
          SELECT id FROM session WHERE id = ?
          UNION
          SELECT child.id
          FROM session child
          JOIN session_tree parent ON child.parent_id = parent.id
          WHERE child.time_archived IS NULL
        )
        SELECT session_id, id, time_created, CAST(data AS BLOB) AS data
        FROM message
        WHERE session_id IN (SELECT id FROM session_tree)
        ORDER BY time_created ASC, id ASC`,
        [sessionId],
      )

      const partRows = db.query<PartRow>(
        `WITH RECURSIVE session_tree(id) AS (
          SELECT id FROM session WHERE id = ?
          UNION
          SELECT child.id
          FROM session child
          JOIN session_tree parent ON child.parent_id = parent.id
          WHERE child.time_archived IS NULL
        )
        SELECT message_id, time_created, CAST(data AS BLOB) AS data
        FROM part
        WHERE session_id IN (SELECT id FROM session_tree)
        ORDER BY message_id ASC, time_created ASC, id ASC`,
        [sessionId],
      )

      const partsByMessage = new Map<string, SessionPart[]>()
      for (const row of partRows) {
        let data: PartData
        try {
          data = JSON.parse(blobToText(row.data)) as PartData
        } catch {
          continue // skip corrupt part data, as the parser does
        }
        const list = partsByMessage.get(row.message_id) ?? []
        list.push({ data, timeCreatedMs: row.time_created })
        partsByMessage.set(row.message_id, list)
      }

      // Per conversation: user text waits for the assistant turn that answers
      // it, and the latest tool output stays the session's tool-result
      // evidence until a newer one lands (both keyed by session so sibling
      // sessions in one tree never share context).
      const capture = readKiloCapture()
      const pendingUserText = new Map<string, string[]>()
      const latestToolResult = new Map<string, ContentPart[]>()

      for (const row of messageRows) {
        if (!inDateRange(row.time_created, dateRange)) continue

        let data: MessageData
        try {
          data = JSON.parse(blobToText(row.data)) as MessageData
        } catch {
          continue // skip corrupt message data, as the parser does
        }

        const ownParts = partsByMessage.get(row.id) ?? []

        if (data.role === 'user') {
          const texts = ownParts
            .map((part) => partText(part.data))
            .filter((text): text is string => text !== undefined)
          if (texts.length > 0) {
            const list = pendingUserText.get(row.session_id) ?? []
            list.push(...texts)
            pendingUserText.set(row.session_id, list)
          }
          continue
        }

        if (data.role !== 'assistant' && data.role !== 'model') continue

        // A zero-token assistant message is a parser call, never a turn —
        // id pairing keys on this message list, so the lists must differ
        // exactly where the store's counters do.
        if (tokenTotal(data) <= 0) continue

        const parts: ContentPart[] = []
        let order = 0
        const toolCalls: ReaderToolCall[] = []
        const toolResults: ReaderToolResult[] = []
        const ownToolResultParts: ContentPart[] = []

        // What Kilo sent on this model call, from the capture plugin: the
        // assembled system prompt (persona, environment, skills catalog and
        // instruction files) and each offered tool's definition.
        const parentId = (data as { parentID?: unknown }).parentID
        const captured =
          typeof parentId === 'string'
            ? capture.forMessage(row.session_id, parentId, row.time_created ?? 0)
            : undefined
        if (captured?.systemPrompt !== undefined) {
          parts.push({ part: 'system_prompt', text: captured.systemPrompt, order: order++ })
        }
        for (const tool of captured?.tools ?? []) {
          parts.push({ part: 'tool_definitions', text: tool.text, order: order++ })
        }

        for (const text of pendingUserText.get(row.session_id) ?? []) {
          parts.push({ part: 'conversation_history', text, order: order++ })
        }
        pendingUserText.delete(row.session_id)

        for (const part of ownParts) {
          const text = partText(part.data)
          if (text !== undefined) {
            parts.push({ part: 'conversation_history', text, order: order++ })
            continue
          }

          if (part.data.type !== 'tool') continue
          const output = toolOutputText(part.data)
          const isError = part.data.state?.status === 'error'
          if (output !== '') {
            ownToolResultParts.push({ part: 'tool_result_content', text: output })
          }

          // A `callID` is the durable identity a tool record needs: a part
          // without one still contributes its output as content, but a
          // tool.invoke record it cannot name is not a record to emit.
          const callId = typeof part.data.callID === 'string' ? part.data.callID : ''
          if (callId === '') continue
          const timestamp = timestampOf(part.timeCreatedMs)
          const callStart = part.data.state?.time?.start
          const callEnd = part.data.state?.time?.end
          const durationMs =
            typeof callStart === 'number' && Number.isFinite(callStart) &&
            typeof callEnd === 'number' && Number.isFinite(callEnd) && callEnd >= callStart
              ? callEnd - callStart
              : undefined
          toolCalls.push({
            id: callId,
            // The store's own tool spelling ('bash', 'read'), not the
            // normalized display name `normalizeToolName` maps for counters:
            // the record keeps the name the harness recorded.
            name: part.data.tool ?? '',
            arguments:
              part.data.state?.input !== null && typeof part.data.state?.input === 'object'
                ? (part.data.state.input as Record<string, unknown>)
                : {},
            ...(timestamp !== undefined ? { timestamp } : {}),
            ...(durationMs !== undefined ? { durationMs } : {}),
          })
          if (output !== '' || isError) {
            toolResults.push({
              toolCallId: callId,
              content: output,
              isError,
              ...(timestamp !== undefined ? { timestamp } : {}),
            })
          }
        }

        // The tool output the model most recently received is the turn's
        // tool-result evidence: a message that invoked no tool still
        // generated against the last result the model consumed, while a
        // newer output supersedes it the moment one lands.
        const resultParts =
          ownToolResultParts.length > 0
            ? ownToolResultParts
            : (latestToolResult.get(row.session_id) ?? [])
        if (ownToolResultParts.length > 0) {
          latestToolResult.set(row.session_id, ownToolResultParts)
        }
        for (const resultPart of resultParts) {
          parts.push({ ...resultPart, order: order++ })
        }

        yield {
          parts,
          nativeRecordId: row.id,
          ...(toolCalls.length > 0 ? { toolCalls } : {}),
          ...(toolResults.length > 0 ? { toolResults } : {}),
        }
      }
    } finally {
      db.close()
    }
  },
}

export default kiloReader
