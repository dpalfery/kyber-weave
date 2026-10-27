/**
 * A harness-reported count, or `undefined` when the value cannot be measured.
 *
 * Records and parts arrive from untrusted payloads — JSON files, SQLite rows —
 * so a field typed `number` can hold `NaN`, `Infinity`, a numeric string, null,
 * or a negative number at runtime. Comparing such a value with `>` coerces
 * (`'28' > 0` is true, so a corrupt row reads as token evidence), `??` passes
 * the bad value through to a bucket, and a numeric string concatenates instead
 * of adding. A negative count is not a measurement either: it would subtract
 * from a bucket and make a prompt look like it returned tokens. Every read of a
 * reported count goes through here instead.
 *
 * WHY it returns `undefined` rather than `0`: an absent measurement and a
 * measured zero are different facts. `0` means the harness counted and found
 * nothing, which is evidence; `undefined` means nothing was reported, which
 * the composition must derive or leave out.
 */
export function measuredCount(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined
}
