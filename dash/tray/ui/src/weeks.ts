/**
 * The 1..52 weeks bound shared by the manual import and clean-and-import.
 *
 * The server validates too; checking here keeps an obviously wrong request
 * from becoming a round trip that fails with a less readable message.
 */

export const MIN_WEEKS = 1
export const MAX_WEEKS = 52
export const WEEKS_ERROR = `Enter a whole number of weeks from ${MIN_WEEKS} to ${MAX_WEEKS}.`

/** The weeks value, or `null` when the text is not a whole number in range. */
export function parseWeeks(text: string): number | null {
  if (!/^\d+$/.test(text.trim())) return null
  const weeks = Number(text)
  return weeks >= MIN_WEEKS && weeks <= MAX_WEEKS ? weeks : null
}
