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

import type { ContentPart } from '../../canon/types.js'
import type { ContentReader, ReaderTurn } from './types.js'

/** Result of reading a full session transcript, including its identifier. */
export type ClaudeSessionReadResult = {
  /** The session id from the transcript records or filename stem. */
  sessionId?: string
  /** Canonical content parts in transcript sequence order. */
  parts: ContentPart[]
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
    const content = messageObj['content']

    if (typeof content === 'string') {
      if (content !== '') {
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
    const session = readClaudeSession(filePath)
    if (session.parts.length === 0 && session.sessionId === undefined) return
    yield {
      parts: session.parts,
      ...(session.sessionId !== undefined ? { sessionId: session.sessionId } : {}),
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
