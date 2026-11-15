// The ingest coverage window body shared by the text and Markdown renderers
// (plan T3, issues #189/#198/#199).
//
// One formatter, one guard: the two renderers previously copy-pasted the same
// `!== null` check, so a stored `history_weeks` of 0 sailed through both and
// printed `last 0 weeks` — the exact string the contract above the old
// copies said must never be printed. A tracked run prints
// `last N weeks (<from> → <through>)`; a pre-window-tracking run (null) with
// a recorded success states `unknown (recorded before window tracking)`; a
// store with no successful refresh at all (lastSuccessAt null) states
// `unknown (no successful refresh recorded)` — never the current default,
// never 0. The label is the caller's: the text renderer pads it into the
// section's 12-character label column while Markdown bolds it.

export type CoverageWindowFacts = {
  historyWeeks?: number | null
  coveredFrom?: string | null
  coveredThrough?: string | null
  lastSuccessAt?: string | null
}

export function formatCoverageWindowBody(refresh: CoverageWindowFacts): string {
  const historyWeeks = refresh.historyWeeks ?? null
  const coveredFrom = refresh.coveredFrom ?? null
  const coveredThrough = refresh.coveredThrough ?? null
  if (
    typeof historyWeeks === 'number' &&
    Number.isSafeInteger(historyWeeks) &&
    historyWeeks > 0 &&
    coveredFrom !== null &&
    coveredThrough !== null
  ) {
    const weeks = `last ${historyWeeks} week${historyWeeks === 1 ? '' : 's'}`
    return `${weeks} (${coveredFrom} → ${coveredThrough})`
  }
  if ((refresh.lastSuccessAt ?? null) === null) {
    return 'unknown (no successful refresh recorded)'
  }
  return 'unknown (recorded before window tracking)'
}
