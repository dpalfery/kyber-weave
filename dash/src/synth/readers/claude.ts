// Claude Code transcript content reader (spec: docs/specs/kyberdash; R7.1, R8.3, R10.1).
//
// Claude Code stores user and assistant turns on disk in per-session JSONL files
// (~/.claude/projects/**/*.jsonl). Unlike its OTLP spans, which carry token
// counters but no content attributes, these transcripts contain conversation text,
// model thinking blocks, and tool results.
//
// What this reader is accountable for:
//
//   * Canonical bucketing —
//       conversation_history  <- 'text' and 'thinking' parts
//       tool_result_content   <- 'tool_result' parts, bucketed by part type rather
//                                than message role: tool results arrive on user-role
//                                messages here, and bucketing by role would misfile
//                                them as conversation.
//       system_prompt         <- genuinely absent from disk; Claude Code injects it
//                                at runtime and never writes it to the transcript.
//                                Emitting an empty or zero bucket would fabricate a
//                                measurement that does not exist (R10.1).
//       tool_definitions      <- genuinely absent from disk; only invocation names
//                                exist in transcripts, never tool schemas.
//   * Ground-truth MCP server attribution —
//       Assistant records carrying `attributionMcpServer` provide an authoritative
//       server identifier. This field is preserved on the emitted `ContentPart.server`.
//       A prefixed tool name alone (e.g. `mcp__github__list_issues`) is NEVER split
//       to guess a server (R8.3), because real server names contain delimiters.
//   * Robustness —
//       Non-message records (metadata, hooks, titles) and malformed lines are
//       skipped cleanly rather than failing the read pass.

import { existsSync, readFileSync } from 'fs'
import { basename, extname } from 'path'

import { claudeCount, claudeText, claudeUsageOf } from '../../providers/claude.js'
import { detectUserCorrection } from '../../canon/outcome.js'
import type { ContentPart } from '../../canon/types.js'
import type { DateRange } from '../../types.js'
import type { ContentReader, ReaderToolCall, ReaderToolResult, ReaderTurn } from './types.js'

export {
  claudeCount,
  claudeText,
  claudeUsageOf,
  loadClaudeCalls,
} from '../../providers/claude.js'

function parseLineUsageInfo(rawLine: string, fileStem: string): {
  messageId?: string
  sessionId: string
  model: string
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheCreationTokens: number
  timestamp?: string
} | undefined {
  const usage = claudeUsageOf(rawLine)
  if (usage === undefined) return undefined
  let record: Record<string, unknown>
  try {
    record = JSON.parse(rawLine) as Record<string, unknown>
  } catch {
    return undefined
  }
  const message = record['message'] as Record<string, unknown> | undefined
  return {
    messageId: message ? claudeText(message['id']) : undefined,
    // Same stem substitution as loadClaudeCalls — a missing sessionId must
    // not let the reader fuse a pair the parser would split on file stem.
    sessionId: claudeText(record['sessionId']) ?? fileStem,
    // Same default as loadClaudeCalls — a missing model must not let the
    // reader fuse a pair the parser would split as 'unknown' vs a real name.
    model: message ? (claudeText(message['model']) ?? 'unknown') : 'unknown',
    inputTokens: claudeCount(usage['input_tokens']),
    outputTokens: claudeCount(usage['output_tokens']),
    cacheReadTokens: claudeCount(usage['cache_read_input_tokens']),
    cacheCreationTokens: claudeCount(usage['cache_creation_input_tokens']),
    timestamp: claudeText(record['timestamp']),
  }
}

function isMatchingTurnUsage(
  prev: ReturnType<typeof parseLineUsageInfo>,
  next: ReturnType<typeof parseLineUsageInfo>,
): boolean {
  if (!prev || !next) return false
  if (prev.messageId !== undefined && next.messageId !== undefined && prev.messageId !== next.messageId) {
    return false
  }
  // Strict compare after stem substitution — mirrors isContiguousPair.
  if (prev.sessionId !== next.sessionId) {
    return false
  }
  if (prev.model !== next.model) {
    return false
  }
  if (
    prev.inputTokens !== next.inputTokens ||
    prev.outputTokens !== next.outputTokens ||
    prev.cacheReadTokens !== next.cacheReadTokens ||
    prev.cacheCreationTokens !== next.cacheCreationTokens
  ) {
    return false
  }
  const isZero =
    prev.inputTokens === 0 &&
    prev.outputTokens === 0 &&
    prev.cacheReadTokens === 0 &&
    prev.cacheCreationTokens === 0
  if (isZero) return false

  if (prev.timestamp && next.timestamp) {
    const prevTime = new Date(prev.timestamp).getTime()
    const nextTime = new Date(next.timestamp).getTime()
    if (Number.isFinite(prevTime) && Number.isFinite(nextTime)) {
      if (Math.abs(nextTime - prevTime) > 60_000) return false
    }
  }
  return true
}

/**
 * The assistant records in a Claude Code transcript each carry the `usage`
 * block for the request that produced them, so an assistant record with usage
 * is exactly one model turn. This splits a transcript's lines into those turns:
 * every line since the previous turn belongs to the turn it precedes, which is
 * how a user prompt and its tool results stay attached to the request they fed.
 *
 * Contiguous assistant records sharing identical usage counters within the turn
 * window are grouped into a single turn to fuse paired request/response halves (#232).
 * A defined `message.id` learned earlier in the group still conflicts with a
 * later different defined id — matching `loadClaudeCalls`' contiguous-pair rule
 * — so an id-less middle record cannot bridge A and B into one turn. A missing
 * `sessionId` resolves to the transcript file stem on both sides, exactly as
 * `loadClaudeCalls` does, so a mixed-presence pair splits instead of fusing.
 * A missing `message.model` resolves to `'unknown'`, exactly as `loadClaudeCalls`
 * does, so an unknown/real-model pair splits instead of fusing.
 *
 * Lines after the last assistant record are emitted as a trailing group so a
 * transcript that never reported usage still reads as a single turn.
 */
export function splitClaudeTurns(lines: readonly string[], fileStem: string): string[][] {
  const rawGroups: string[][] = []
  let current: string[] = []

  for (const rawLine of lines) {
    if (rawLine.trim() === '') continue
    current.push(rawLine)
    if (claudeUsageOf(rawLine) === undefined) continue
    rawGroups.push(current)
    current = []
  }

  if (current.length > 0) rawGroups.push(current)
  if (rawGroups.length <= 1) return rawGroups

  const mergedGroups: string[][] = []
  // Parallel to loadClaudeCalls: the defined message.id and the first usage
  // line of each merged group. prevCall.timestamp / counters / model are
  // never rewritten on merge, so the gap must be measured from that first
  // line — walking the last usage line fuses a t=0,+40s,+80s chain that
  // the parser splits (0→80 > 60s).
  const mergedMessageIds: Array<string | undefined> = []
  const mergedFirstUsage: Array<ReturnType<typeof parseLineUsageInfo>> = []
  for (let i = 0; i < rawGroups.length; i++) {
    const group = rawGroups[i]!
    const lastLine = group[group.length - 1]!
    const usageInfo = parseLineUsageInfo(lastLine, fileStem)

    if (mergedGroups.length > 0) {
      const prevMerged = mergedGroups[mergedGroups.length - 1]!
      const prevMessageId = mergedMessageIds[mergedMessageIds.length - 1]
      const prevFirstUsage = mergedFirstUsage[mergedFirstUsage.length - 1]
      const prevForMatch =
        prevFirstUsage === undefined
          ? undefined
          : { ...prevFirstUsage, messageId: prevMessageId ?? prevFirstUsage.messageId }
      if (prevForMatch !== undefined && isMatchingTurnUsage(prevForMatch, usageInfo)) {
        prevMerged.push(...group)
        if (prevMessageId === undefined && usageInfo?.messageId !== undefined) {
          mergedMessageIds[mergedMessageIds.length - 1] = usageInfo.messageId
        }
        continue
      }
    }
    mergedGroups.push([...group])
    mergedMessageIds.push(usageInfo?.messageId)
    mergedFirstUsage.push(usageInfo)
  }

  return mergedGroups
}

/**
 * Pairing identity for one turn group. Uses `message.id` only — that is what
 * `loadClaudeCalls` stamps as `turnId`. Emitting the transcript `uuid` here
 * would invent ids the parser never stamps as `turnId`, so those calls could
 * not join through the id map.
 */
function nativeRecordIdOfGroup(group: readonly string[]): string | undefined {
  for (let i = 0; i < group.length; i++) {
    if (claudeUsageOf(group[i]!) === undefined) continue
    let record: Record<string, unknown>
    try {
      record = JSON.parse(group[i]!) as Record<string, unknown>
    } catch {
      continue
    }
    const message = record['message'] as Record<string, unknown> | undefined
    const messageId = message ? claudeText(message['id']) : undefined
    if (messageId !== undefined) return messageId
  }
  return undefined
}

/**
 * Usage-line timestamp for a turn group, matching the instant `loadClaudeCalls`
 * keeps on a collapsed contiguous pair (the first usage line) and that
 * `sliceCallsToWindow` therefore sees. Walking from the last usage line would
 * disagree when a window boundary falls inside the ≤60s merge gap. Missing or
 * malformed timestamps are unusable instants — the same rows
 * `sliceCallsToWindow` drops when a refresh window is set — so a date-ranged
 * read must exclude them too.
 */
function timestampOfGroup(group: readonly string[]): Date | undefined {
  for (let i = 0; i < group.length; i++) {
    if (claudeUsageOf(group[i]!) === undefined) continue
    let record: Record<string, unknown>
    try {
      record = JSON.parse(group[i]!) as Record<string, unknown>
    } catch {
      continue
    }
    const raw = claudeText(record['timestamp'])
    if (raw === undefined) return undefined
    const instant = new Date(raw)
    return Number.isNaN(instant.getTime()) ? undefined : instant
  }
  return undefined
}

function groupInDateRange(group: readonly string[], dateRange?: DateRange): boolean {
  if (dateRange === undefined) return true
  const timestamp = timestampOfGroup(group)
  // Align with sliceCallsToWindow: no usable instant → not in the window.
  // Keeping these rows would leave the reader ahead of window-sliced calls and
  // let positional pairing attach their text to a later in-window call.
  if (timestamp === undefined) return false
  if (timestamp < dateRange.start) return false
  if (timestamp > dateRange.end) return false
  return true
}

/** Result of reading a full session transcript, including its identifier. */
export type ClaudeSessionReadResult = {
  /** The session id from the transcript records or filename stem. */
  sessionId?: string
  /** Canonical content parts in transcript sequence order. */
  parts: ContentPart[]
  /** Termination or exit indicator, when the transcript reported one. */
  terminationReason?: string
  /** Process or command exit code observed in transcript. */
  exitCode?: number
  /** Whether this session contains an explicit user correction turn. */
  isCorrection?: boolean
  /** The rule that identified the user correction. */
  correctionRule?: string
  /** Tool calls invoked in this turn/session. */
  toolCalls?: ReaderToolCall[]
  /** Tool execution results received in this turn/session. */
  toolResults?: ReaderToolResult[]
  /** Tool schemas explicitly offered to the model, if observed in telemetry. */
  toolsOffered?: string[]
}

/**
 * Extracts canonical content parts and session identity from a Claude Code JSONL transcript.
 *
 * @param source Raw JSONL string, array of JSONL lines, or a path to a transcript file.
 * @returns The session id (if discovered) and array of canonical content parts.
 */
export function readClaudeSession(source: string | readonly string[]): ClaudeSessionReadResult {
  let lines: readonly string[]
  let fileStem: string | undefined

  if (Array.isArray(source)) {
    lines = source
  } else if (typeof source === 'string') {
    // Distinguish between file path and inline JSONL content.
    if (!source.includes('\n') && !source.trim().startsWith('{') && existsSync(source)) {
      fileStem = basename(source, extname(source))
      lines = readFileSync(source, 'utf-8').split(/\r?\n/)
    } else {
      lines = source.split(/\r?\n/)
    }
  } else {
    return { parts: [] }
  }

  let discoveredSessionId: string | undefined
  let observedExitCode: number | undefined
  let observedTerminationReason: string | undefined
  let hasCorrection = false
  let detectedCorrectionRule: string | undefined
  const parts: ContentPart[] = []
  const toolCalls: ReaderToolCall[] = []
  const toolResults: ReaderToolResult[] = []
  let order = 0
  const nextOrder = () => order++

  for (const rawLine of lines) {
    const line = rawLine.trim()
    if (line === '') continue

    let record: Record<string, unknown>
    try {
      const parsed = JSON.parse(line)
      if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
        continue
      }
      record = parsed as Record<string, unknown>
    } catch {
      // Malformed lines are skipped cleanly.
      continue
    }

    if (discoveredSessionId === undefined && typeof record['sessionId'] === 'string' && record['sessionId'] !== '') {
      discoveredSessionId = record['sessionId']
    }

    if (typeof record['exitCode'] === 'number') observedExitCode = record['exitCode']
    if (typeof record['exit_code'] === 'number') observedExitCode = record['exit_code']
    if (typeof record['terminationReason'] === 'string') observedTerminationReason = record['terminationReason']
    if (typeof record['stop_reason'] === 'string') observedTerminationReason = record['stop_reason']
    if (record['type'] === 'exit' || record['type'] === 'result') {
      observedTerminationReason = String(record['type'])
      if (typeof record['code'] === 'number') observedExitCode = record['code']
      if (typeof record['exitCode'] === 'number') observedExitCode = record['exitCode']
    }

    // Ground-truth MCP server field carried on assistant records.
    const rawServer = record['attributionMcpServer']
    const server = typeof rawServer === 'string' && rawServer.trim() !== ''
      ? rawServer.trim()
      : undefined

    const msg = record['message']
    if (msg === null || typeof msg !== 'object' || Array.isArray(msg)) {
      // Non-message metadata line (e.g. ai-title, bridge-session, queue-operation, system hook summary).
      continue
    }

    const messageObj = msg as Record<string, unknown>
    const role = messageObj['role']
    const content = messageObj['content']

    if (typeof content === 'string') {
      if (content !== '') {
        if (role === 'user') {
          const check = detectUserCorrection(content)
          if (check.matched) {
            hasCorrection = true
            detectedCorrectionRule = check.rule
          }
        }
        parts.push({
          part: 'conversation_history',
          text: content,
          ...(server !== undefined ? { server } : {}),
          order: nextOrder(),
        })
      }
      continue
    }

    if (!Array.isArray(content)) {
      continue
    }

    for (const block of content) {
      if (block === null || typeof block !== 'object' || Array.isArray(block)) {
        continue
      }

      const blockObj = block as Record<string, unknown>
      const type = blockObj['type']

      if (type === 'text') {
        const text = typeof blockObj['text'] === 'string' ? blockObj['text'] : ''
        if (text === '') continue
        if (role === 'user') {
          const check = detectUserCorrection(text)
          if (check.matched) {
            hasCorrection = true
            detectedCorrectionRule = check.rule
          }
        }
        parts.push({
          part: 'conversation_history',
          text,
          ...(server !== undefined ? { server } : {}),
          order: nextOrder(),
        })
      } else if (type === 'thinking') {
        const text = typeof blockObj['thinking'] === 'string'
          ? blockObj['thinking']
          : (typeof blockObj['text'] === 'string' ? blockObj['text'] : '')
        if (text === '') continue
        parts.push({
          part: 'conversation_history',
          text,
          ...(server !== undefined ? { server } : {}),
          order: nextOrder(),
        })
      } else if (type === 'tool_result') {
        // Bucket on part TYPE, never on message role: tool results arrive on user-role
        // messages in Claude Code, and bucketing by role would misfile them as conversation.
        let text = ''
        const rawResultContent = blockObj['content']
        if (typeof rawResultContent === 'string') {
          text = rawResultContent
        } else if (Array.isArray(rawResultContent)) {
          text = rawResultContent
            .map((item) => {
              if (typeof item === 'string') return item
              if (item !== null && typeof item === 'object') {
                const itemObj = item as Record<string, unknown>
                if (typeof itemObj['text'] === 'string') return itemObj['text']
                return JSON.stringify(item)
              }
              return String(item)
            })
            .join('\n')
        } else if (rawResultContent !== null && typeof rawResultContent === 'object') {
          const resObj = rawResultContent as Record<string, unknown>
          text = typeof resObj['text'] === 'string' ? resObj['text'] : JSON.stringify(rawResultContent)
        } else if (typeof blockObj['text'] === 'string') {
          text = blockObj['text']
        } else {
          text = JSON.stringify(blockObj)
        }

        const toolCallId = typeof blockObj['tool_use_id'] === 'string'
          ? blockObj['tool_use_id']
          : (typeof blockObj['id'] === 'string' ? blockObj['id'] : '')

        if (toolCallId !== '') {
          const isError = blockObj['is_error'] === true ||
            (typeof blockObj['exit_code'] === 'number' && blockObj['exit_code'] !== 0) ||
            (typeof blockObj['exitCode'] === 'number' && blockObj['exitCode'] !== 0)
          toolResults.push({
            toolCallId,
            content: text,
            isError,
          })
        }

        if (text === '') continue
        parts.push({
          part: 'tool_result_content',
          text,
          ...(server !== undefined ? { server } : {}),
          order: nextOrder(),
        })
      } else if (type === 'tool_use') {
        const id = typeof blockObj['id'] === 'string' ? blockObj['id'] : ''
        const name = typeof blockObj['name'] === 'string' ? blockObj['name'] : ''
        const rawInput = blockObj['input']
        const input: Record<string, unknown> | string =
          typeof rawInput === 'string'
            ? rawInput
            : (rawInput !== null && typeof rawInput === 'object' && !Array.isArray(rawInput))
              ? (rawInput as Record<string, unknown>)
              : {}
        if (id !== '' || name !== '') {
          toolCalls.push({
            id,
            name,
            arguments: input,
          })
        }
      }
    }
  }

  return {
    sessionId: discoveredSessionId ?? fileStem,
    parts,
    ...(observedTerminationReason !== undefined ? { terminationReason: observedTerminationReason } : {}),
    ...(observedExitCode !== undefined ? { exitCode: observedExitCode } : {}),
    ...(hasCorrection ? { isCorrection: true, correctionRule: detectedCorrectionRule } : {}),
    ...(toolCalls.length > 0 ? { toolCalls } : {}),
    ...(toolResults.length > 0 ? { toolResults } : {}),
  }
}

/**
 * Extracts canonical content parts from a Claude Code JSONL transcript.
 *
 * @param source Raw JSONL string, array of JSONL lines, or a path to a transcript file.
 * @returns Array of canonical content parts in transcript sequence order.
 */
export function readClaudeTranscript(source: string | readonly string[]): ContentPart[] {
  return readClaudeSession(source).parts
}

/** Alias for {@link readClaudeTranscript}. */
export const readClaudeContent = readClaudeTranscript

/** Alias for {@link readClaudeTranscript}. */
export const readClaudeParts = readClaudeTranscript

/**
 * Content reader for Claude Code transcripts implementing {@link ContentReader}.
 */
export class ClaudeContentReader implements ContentReader {
  readonly harness = 'claude-code'

  /**
   * Claude's transcript has no invocation-counter boundary like Codex's
   * `token_count` event. Its complete file is therefore one content snapshot:
   * it is joined to the canonical session only after the file reader has
   * established the session identity, rather than inventing turns from roles.
   */
  async *read(filePath: string, dateRange?: DateRange): AsyncGenerator<ReaderTurn> {
    // One turn per assistant request, so a turn pairs with the call
    // `loadClaudeCalls` emitted for the same request. Reading the whole
    // transcript as a single turn would put every part on the first record and
    // report a context-pressure curve that rises once and then flatlines.
    let lines: string[]
    try {
      lines = readFileSync(filePath, 'utf-8').split(/\r?\n/)
    } catch {
      return
    }

    for (const group of splitClaudeTurns(lines, basename(filePath, extname(filePath)))) {
      // Refresh window-slices counter calls; emit the same window here so
      // positional pairing cannot attach an out-of-window turn's text. Id maps
      // (`nativeRecordId` ↔ `turnId`) remain the durable pairing key.
      if (!groupInDateRange(group, dateRange)) continue

      const session = readClaudeSession(group)
      if (
        session.parts.length === 0 &&
        session.sessionId === undefined &&
        (!session.toolCalls || session.toolCalls.length === 0) &&
        (!session.toolResults || session.toolResults.length === 0)
      ) {
        continue
      }
      const nativeRecordId = nativeRecordIdOfGroup(group)
      yield {
        parts: session.parts,
        ...(session.sessionId !== undefined ? { sessionId: session.sessionId } : {}),
        ...(nativeRecordId !== undefined ? { nativeRecordId } : {}),
        ...(session.terminationReason !== undefined ? { terminationReason: session.terminationReason } : {}),
        ...(session.exitCode !== undefined ? { exitCode: session.exitCode } : {}),
        ...(session.isCorrection !== undefined ? { isCorrection: session.isCorrection, correctionRule: session.correctionRule } : {}),
        ...(session.toolCalls !== undefined ? { toolCalls: session.toolCalls } : {}),
        ...(session.toolResults !== undefined ? { toolResults: session.toolResults } : {}),
        ...(session.toolsOffered !== undefined ? { toolsOffered: session.toolsOffered } : {}),
      }
    }
  }

  readSession(source: string | readonly string[]): ClaudeSessionReadResult {
    return readClaudeSession(source)
  }
}

/** Shared default instance of {@link ClaudeContentReader}. */
export const claudeReader: ContentReader = new ClaudeContentReader()

/** Alias for {@link claudeReader}. */
export const claudeContentReader: ContentReader = claudeReader

/** Alias for {@link ClaudeContentReader}. */
export const ClaudeReader = ClaudeContentReader

export default claudeReader
