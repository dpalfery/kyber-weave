# Q3 — Content + 14-day purge (D18)

## RED
`npm --prefix dash exec vitest run kyber/canon/retention.test.ts`
Failed: `Cannot find module './retention.js'` (test imported `purgeExpiredContent` before the implementation file existed).

## GREEN
Same command: 1 passed (11ms). Temp DB under `os.tmpdir()` (`kyber-canon-retention-*`), never `~/.kyberdash/canon.db`.

## Implementation
- `purgeExpiredContent(store, now?)` walks `spanIds()` / `get()`, and for timestamps older than 14 days calls `setContent(spanId, {})`.
- That UPDATE writes `content_json = '{}'` and `parts_json = NULL`. `records.raw` is not touched.
- `content_json` is not SQL-NULL: `toRecord` always `JSON.parse`s it. Empty object is the no-migration purge that keeps reads working.
- Metadata: `content_retention_days`, `content_purged_through`.

## Exclusive files
- dash/kyber/canon/retention.ts
- dash/kyber/canon/retention.test.ts
