// Content-reader contract for KyberDash session files.
//
// Harness readers (Codex first, others next) implement this and nothing
// larger: a path in, per-turn canonical parts out. Session identity and the
// model context window ride along only when the file actually named them —
// optional fields stay absent rather than becoming 0 or a generated id,
// because a fabricated number is what makes an uninstrumented harness look
// efficient (R10.1, R10.2).
//
// `ContentPart` is the record model's type; this file does not redefine it.
// Downstream already addresses content through those keys, and a parallel
// shape here would drift the first time a bucket is added.

import type { ContentPart } from '../../canon/types.js'
import type { ParsedProviderCall } from '../../providers/types.js'
import type { DateRange } from '../../types.js'

/**
 * Provenance a refresh source unit carries onto a synthesized record.
 * These fields identify the native row for checkpointed replacement; they
 * are not a second validation path.
 */
export type SourceRecordProvenance = {
  harnessId: string
  sourceKey: string
  nativeSessionId: string
  nativeRecordId: string
  recordDigest?: string
  sourceRevision?: string
  parserContractVersion?: string
  importedAt?: string
  locationToken?: string
}

/**
 * Adapter projection of one native record into synthesis. Classification
 * happens before this envelope is built; Gemini is never a harness id here.
 */
export type SourceRecordEnvelope = Omit<SourceRecordProvenance, 'nativeRecordId'> & {
  nativeRecordId?: string
  call: ParsedProviderCall
  readerTurn?: ReaderTurn
}

/**
 * Structured tool call invoked by the model during a turn.
 */
export interface ReaderToolCall {
  id: string
  name: string
  arguments: Record<string, unknown> | string
  timestamp?: string
  durationMs?: number
}

/**
 * Structured execution result of a tool call returned to the model.
 */
export interface ReaderToolResult {
  toolCallId: string
  name?: string
  content: string
  isError?: boolean
  timestamp?: string
}

/**
 * One model turn's content as the file recorded it. Optional fields are
 * omitted when the file did not carry them; callers must not treat absence
 * as zero.
 */
export type ReaderTurn = {
  parts: readonly ContentPart[]
  /** The harness's own session id, when `session_meta` named one. */
  sessionId?: string
  /** Model context window in tokens, when an `event_msg` reported one. */
  contextWindow?: number
  /**
   * Model context window in tokens, as the harness declared (configured) it
   * rather than measured it. Kept apart from `contextWindow` because a
   * declared window is weaker provenance: synthesis files it under the
   * dedicated declared raw key, never under a reported key.
   */
  declaredContextWindow?: number
  /** Termination or exit indicator, when the transcript reported one. */
  terminationReason?: string
  /** Process or command exit code observed in transcript. */
  exitCode?: number
  /** Whether this turn represents an explicit user correction turn. */
  isCorrection?: boolean
  /** The rule that identified this turn as a user correction. */
  correctionRule?: string
  /** Native turn/message id when the transcript named one. */
  nativeRecordId?: string
  /** Tool calls invoked in this turn. */
  toolCalls?: ReaderToolCall[]
  /** Tool execution results received in this turn. */
  toolResults?: ReaderToolResult[]
  /** Tool schemas explicitly offered to the model in this turn, if observed in telemetry. */
  toolsOffered?: string[]
}

/**
 * Reads one harness session file into canonical content parts, yielding
 * once per turn. Implementors emit only buckets the file genuinely carries.
 */
export interface ContentReader {
  read(filePath: string, dateRange?: DateRange): AsyncIterable<ReaderTurn>
  /**
   * When true, `matchingTurns` must not fall back to `turns[index]`. Cursor
   * yields a filtered turn list (a subset of requests), so positional pairing
   * would steal another turn's parts. Readers that omit this keep main's
   * index-pairing behaviour for id-less calls and id-map misses.
   */
  readonly positionalPairingUnsafe?: boolean
}
