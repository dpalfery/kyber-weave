// Stable classification of host fs errors for the capture domain (T2).
//
// The settings surface reports why a harness toggle failed using a fixed
// reason vocabulary; a raw Node fs exception must map onto it without
// leaking its message, which embeds the owner's absolute paths. errno codes
// are stable identifiers and safe to return; message text is not, so the
// result carries exactly { code, reason } and nothing else.

/** The fixed reason vocabulary the settings API reports for fs failures. */
export type FsErrorReason = 'drift' | 'conflict' | 'unparseable' | 'unreadable' | 'busy'

/** A path-free classification: an errno code (or `UNKNOWN`) plus a reason. */
export type FsErrorClassification = {
  code: string
  reason: FsErrorReason
}

/**
 * Reasons per errno code. ENOENT is drift in this domain, not a read
 * failure: `readTextIfPresent` already treats a missing file as "absent
 * since enable", and disable reports that path as drift. Permission-class
 * codes mean the bytes exist but this process may not have them.
 */
const REASONS_BY_ERRNO: ReadonlyMap<string, FsErrorReason> = new Map([
  ['EACCES', 'unreadable'],
  ['EPERM', 'unreadable'],
  ['EBUSY', 'busy'],
  ['ENOENT', 'drift'],
])

/** The code reported when a thrown value carries no errno at all. */
const UNKNOWN_ERRNO = 'UNKNOWN'

/**
 * Classify an unknown thrown value. Unrecognized errno codes (EISDIR, ...)
 * keep their code but fall back to `unreadable`, the safest reason: the
 * caller treats the file as unusable rather than guessing a stronger claim.
 */
export function classifyFsError(error: unknown): FsErrorClassification {
  const code = errnoCodeOf(error)
  if (code === undefined) return { code: UNKNOWN_ERRNO, reason: 'unreadable' }
  return { code, reason: REASONS_BY_ERRNO.get(code) ?? 'unreadable' }
}

/**
 * The errno code of a Node fs error, or undefined. Only an `Error` carrying
 * a string `code` counts: a thrown string or `null` has no stable identity
 * to report, so it maps to `UNKNOWN` instead of stringifying arbitrary
 * values into the API surface.
 */
function errnoCodeOf(error: unknown): string | undefined {
  if (!(error instanceof Error)) return undefined
  const code = (error as { code?: unknown }).code
  return typeof code === 'string' ? code : undefined
}
