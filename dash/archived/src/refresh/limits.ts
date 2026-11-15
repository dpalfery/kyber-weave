// Neutral home for the shared re-ingest window bound.
//
// Why this file exists: the bound is enforced by both the clean pipeline
// (`clean/clean.ts`) and the folder-history import (`refresh/folder-import.ts`),
// and folder-import is now called *by* the clean pipeline. Declaring the constant
// in either of those two modules and importing it from the other would close an
// import cycle, so the value lives here instead and both sides import this leaf.
// Re-exported from `clean/clean.ts`, which stays the module callers already
// reach the constant through.

/**
 * Upper bound on the re-ingest window: one year. Re-ingestion scans source
 * logs week by week, so an unbounded window turns a typo into a
 * multi-millennia self-inflicted DoS (F6). The route and CLI parsers enforce
 * the same bound so bad input is rejected before anything is wiped.
 */
export const MAX_CLEAN_REINGEST_WEEKS = 52