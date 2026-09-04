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
}

/**
 * Reads one harness session file into canonical content parts, yielding
 * once per turn. Implementors emit only buckets the file genuinely carries.
 */
export interface ContentReader {
  read(filePath: string): AsyncIterable<ReaderTurn>
}
