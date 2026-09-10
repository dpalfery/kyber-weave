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

import type { ParsedProviderCall } from '../../../src/providers/types.js'

import { detectUserCorrection } from '../../canon/outcome.js'
import type { ContentPart } from '../../canon/types.js'
import type { ContentReader, ReaderTurn } from './types.js'

/**
 * The assistant records in a Claude Code transcript each carry the `usage`
 * block for the request that produced them, so an assistant record with usage
 * is exactly one model turn. This splits a transcript's lines into those turns:
 * every line since the previous turn belongs to the turn it precedes, which is
 * how a user prompt and its tool results stay attached to the request they fed.
 *
 * Lines after the last assistant record are emitted as a trailing group so a
 * transcript that never reported usage still reads as a single turn.
 */
export function splitClaudeTurns(lines: readonly string[]): string[][] {
  const groups: string[][] = []
  let current: string[] = []

  for (const rawLine of lines) {
    if (rawLine.trim() === '') continue
    current.push(rawLine)
    if (claudeUsageOf(rawLine) === undefined) continue
    groups.push(current)
    current = []
  }

  if (current.length > 0) groups.push(current)
  return groups
}

/** The `message.usage` block of one transcript line, when it is an assistant turn. */
function claudeUsageOf(rawLine: string): Record<string, unknown> | undefined {
  let record: Record<string, unknown>
  try {
    const parsed: unknown = JSON.parse(rawLine)
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return undefined
    record = parsed as Record<string, unknown>
  } catch {
    return undefined
  }
  if (record['type'] !== 'assistant') return undefined
  const message = record['message']
  if (message === null || typeof message !== 'object' || Array.isArray(message)) return undefined
  const usage = (message as Record<string, unknown>)['usage']
  if (usage === null || usage === undefined || typeof usage !== 'object' || Array.isArray(usage)) {
    return undefined
  }
  return usage as Record<string, unknown>
}

/** A finite non-negative counter, or 0 — never a fabricated estimate. */
function claudeCount(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : 0
}

function claudeText(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() !== '' ? value.trim() : undefined
}

/**
 * Read one Claude Code transcript as provider calls — one per assistant turn.
 *
 * Claude Code's provider entry exposes discovery but no streaming session
 * parser (`createSessionParser` yields nothing), so synthesis had no calls to
 * build records from and every Claude transcript ingested as zero records.
 * This is the same seam Copilot CLI uses for its SQLite store: the transcript
 * is the collectable source, so the counters are read straight off it.
 *
 * Anthropic's `input_tokens` excludes both cache classes, which is the
 * `exclusive` convention already registered for this provider. Counters are
 * copied verbatim; nothing is inferred when a field is absent.
 */
export function loadClaudeCalls(filePath: string): ParsedProviderCall[] {
  let lines: string[]
  try {
    lines = readFileSync(filePath, 'utf-8').split(/\r?\n/)
  } catch {
    return []
  }

  const calls: ParsedProviderCall[] = []
  const fileStem = basename(filePath, extname(filePath))
  let index = 0

  for (const rawLine of lines) {
    const usage = claudeUsageOf(rawLine)
    if (usage === undefined) continue

    const record = JSON.parse(rawLine) as Record<string, unknown>
    const message = record['message'] as Record<string, unknown>
    const sessionId = claudeText(record['sessionId']) ?? fileStem
    // `uuid` is the transcript's own per-record identity; the index keeps the
    // key unique for a transcript that omits it.
    const messageId = claudeText(record['uuid']) ?? claudeText(message['id']) ?? `turn-${index}`
    index += 1

    const serverToolUse = usage['server_tool_use']
    const webSearchRequests =
      serverToolUse !== null && typeof serverToolUse === 'object' && !Array.isArray(serverToolUse)
        ? claudeCount((serverToolUse as Record<string, unknown>)['web_search_requests'])
        : 0

    calls.push({
      provider: 'claude',
      model: claudeText(message['model']) ?? 'unknown',
      inputTokens: claudeCount(usage['input_tokens']),
      outputTokens: claudeCount(usage['output_tokens']),
      cacheCreationInputTokens: claudeCount(usage['cache_creation_input_tokens']),
      cacheReadInputTokens: claudeCount(usage['cache_read_input_tokens']),
      cachedInputTokens: claudeCount(usage['cache_read_input_tokens']),
      reasoningTokens: 0,
      webSearchRequests,
      // Cost is derived downstream from the counters and the rate table; the
      // transcript states no price, and stating 0 here would be a fabrication.
      costUSD: 0,
      costIsEstimated: true,
      tools: [],
      bashCommands: [],
      timestamp: claudeText(record['timestamp']) ?? new Date(0).toISOString(),
      speed: claudeText(usage['speed']) === 'fast' ? 'fast' : 'standard',
      deduplicationKey: `claude:${sessionId}:${messageId}`,
      sessionId,
      userMessage: '',
    })
  }

  return calls
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

        if (text === '') continue
        parts.push({
          part: 'tool_result_content',
          text,
          ...(server !== undefined ? { server } : {}),
          order: nextOrder(),
        })
      }
      // 'tool_use' blocks represent tool invocations rather than tool definitions (schemas),
      // and system prompts are never written to disk by Claude Code. Neither is emitted.
    }
  }

  return {
    sessionId: discoveredSessionId ?? fileStem,
    parts,
    ...(observedTerminationReason !== undefined ? { terminationReason: observedTerminationReason } : {}),
    ...(observedExitCode !== undefined ? { exitCode: observedExitCode } : {}),
    ...(hasCorrection ? { isCorrection: true, correctionRule: detectedCorrectionRule } : {}),
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
  async *read(filePath: string): AsyncGenerator<ReaderTurn> {
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

    for (const group of splitClaudeTurns(lines)) {
      const session = readClaudeSession(group)
      if (session.parts.length === 0 && session.sessionId === undefined) continue
      yield {
        parts: session.parts,
        ...(session.sessionId !== undefined ? { sessionId: session.sessionId } : {}),
        ...(session.terminationReason !== undefined ? { terminationReason: session.terminationReason } : {}),
        ...(session.exitCode !== undefined ? { exitCode: session.exitCode } : {}),
        ...(session.isCorrection !== undefined ? { isCorrection: session.isCorrection, correctionRule: session.correctionRule } : {}),
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
