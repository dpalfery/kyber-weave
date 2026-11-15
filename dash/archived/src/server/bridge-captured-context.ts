// The captured-context traversal: its budgets, units, chunks and cursors. Split
// from `./bridge.ts`, which re-exports every name here that it exported before.


/**
 * Complete captured-context traversal budget (plan defaults): at most 100
 * live traversals, each evicted after two minutes idle. A new traversal past
 * the cap answers `budget_exceeded` rather than evicting a live reader out
 * from under its cursor.
 */
export const CAPTURED_CONTEXT_MAX_TRAVERSALS = 100
export const CAPTURED_CONTEXT_TRAVERSAL_TTL_MS = 120_000
/**
 * Page fill target for captured-context content pages. Each chunk text is
 * capped at 16,384 UTF-8 bytes, so filling to 60,000 leaves headroom for the
 * chunk metadata plus the service envelope wrapped around the page — the
 * whole encoded response stays within 65,536 bytes.
 */
export const CAPTURED_CONTEXT_PAGE_BUDGET = 60_000
/** Per-chunk text ceiling, in UTF-8 bytes (validated by the inspect seam; re-clamped here). */
export const CAPTURED_CONTEXT_BYTE_LIMIT = 16_384
/** Manifest page bounds (validated by the inspect seam; re-clamped here). */
export const CAPTURED_CONTEXT_MANIFEST_DEFAULT_LIMIT = 25
export const CAPTURED_CONTEXT_MANIFEST_MAX_LIMIT = 100
/** Placeholder cursor length assumed while filling a page, so the sealed cursor cannot push it over budget. */
export const CAPTURED_CONTEXT_CURSOR_PLACEHOLDER = '0'.repeat(256)

export type CapturedContextOrigin = 'turn' | 'session_shared'

export type CapturedContextUnit = {
  sessionId: string
  turnIndex?: number
  spanId: string
  origin: CapturedContextOrigin
}

export type CapturedContextPart = {
  kind: string
  role: string | null
  order: number | null
  resident: boolean
  text: string
}

export type CapturedContextChunk = {
  partId: string
  blockId: string
  sessionId: string
  turnIndex?: number
  spanId: string
  origin: CapturedContextOrigin
  text: string
  byteOffset: number
  complete: boolean
  role: string | null
  order: number
  resident: boolean
}

export type CapturedContextTraversal = {
  binding: string
  touchedAt: number
}

export type CapturedContextTarget =
  | { kind: 'run'; runId: string }
  | { kind: 'session'; sessionId: string }
  | { kind: 'turn'; sessionId: string; turnIndex: number }

/** Canonical block identity for a captured part kind; unknown kinds keep their own name. */
export function capturedBlockIdOf(kind: string): string {
  if (kind === 'user_messages' || kind === 'assistant_turns') return 'conversation_history'
  if (
    kind === 'system_prompt' ||
    kind === 'tool_definitions' ||
    kind === 'instruction_context' ||
    kind === 'conversation_history' ||
    kind === 'tool_result_content'
  ) {
    return kind
  }
  return kind
}

/**
 * A part's role, from what the harness stored or — only for the two
 * unambiguous legacy buckets — the bucket itself. Anything else stays
 * absent rather than guessed, so a reconstructed view never invents
 * provenance.
 */
export function capturedRoleOf(kind: string, stored: unknown): string | null {
  if (stored === 'system' || stored === 'user' || stored === 'assistant') return stored
  if (kind === 'system_prompt') return 'system'
  if (kind === 'user_messages') return 'user'
  if (kind === 'assistant_turns') return 'assistant'
  return null
}

/**
 * Split text into pieces of at most `byteLimit` UTF-8 bytes without splitting
 * a multi-byte code point across pages: the cut backs up over continuation
 * bytes, exactly like the turn-block reader. A single code point wider than
 * the page is kept whole rather than split or dropped, so pagination can
 * neither lose bytes nor duplicate them.
 *
 * WHY byte cuts, not character iteration: cutting the encoded bytes and
 * backing over continuation bytes lands on exactly the same greedy
 * character boundaries, without a per-character byte-length call — the
 * scan cost that dominates multi-megabyte page splits.
 */
export function splitUtf8Text(text: string, byteLimit: number): string[] {
  if (text === '') return []
  const bytes = Buffer.from(text, 'utf8')
  if (bytes.byteLength <= byteLimit) return [text]
  const pieces: string[] = []
  let start = 0
  while (start < bytes.byteLength) {
    let end = Math.min(start + byteLimit, bytes.byteLength)
    while (end > start && (bytes[end] & 0xc0) === 0x80) end -= 1
    if (end === start) {
      end = start + 1
      while (end < bytes.byteLength && (bytes[end] & 0xc0) === 0x80) end += 1
    }
    pieces.push(bytes.subarray(start, end).toString('utf8'))
    start = end
  }
  return pieces
}

/**
 * A normalized captured-context target, or null when the request names no
 * usable one. Normalization keeps key order fixed, so the cursor binding
 * seals the same target the same way on every replay.
 */
export function capturedContextTargetOf(value: unknown): CapturedContextTarget | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null
  const record = value as Record<string, unknown>
  if (record.kind === 'run' && typeof record.runId === 'string' && record.runId.length > 0) {
    return { kind: 'run', runId: record.runId }
  }
  if (record.kind === 'session' && typeof record.sessionId === 'string' && record.sessionId.length > 0) {
    return { kind: 'session', sessionId: record.sessionId }
  }
  if (
    record.kind === 'turn' &&
    typeof record.sessionId === 'string' &&
    record.sessionId.length > 0 &&
    typeof record.turnIndex === 'number' &&
    Number.isSafeInteger(record.turnIndex) &&
    record.turnIndex >= 0
  ) {
    return { kind: 'turn', sessionId: record.sessionId, turnIndex: record.turnIndex }
  }
  return null
}
