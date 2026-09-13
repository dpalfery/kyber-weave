# T2 — Checkpoint/provenance migration

Worker: TypeScript implementation. Exclusive files only. Live `SCHEMA_VERSION` bumped **10 → 11**.

## STATUS

**DONE**

## ARTIFACTS

- `dash/kyber/canon/store.ts` — `SCHEMA_VERSION = 11`, `MIGRATIONS[10]`, `SOURCE_STATE_SQL` in `SCHEMA_SQL`, `commitSourceUnit` + typed accessors
- `dash/kyber/canon/source-state.ts` — types, coverage arithmetic, DDL
- `dash/kyber/canon/source-state.test.ts`
- `dash/kyber/canon/store.test.ts` (extended)
- `dash/kyber/canon/migration.test.ts` (v10 fixture → current)

## RED evidence

`npx vitest run kyber/canon/source-state.test.ts kyber/canon/store.test.ts kyber/canon/migration.test.ts` (tests written, no implementation):

- `source-state.test.ts`: `Cannot find module './source-state.js'`
- `SCHEMA_VERSION` still 10: `expected 10 to be greater than 10`
- v10 fixture: no `source_checkpoint` / `record_provenance` tables
- `commitSourceUnit` / `getSourceCheckpoint` missing
- harness/session/time plan used `records_by_session` only

10 failed / 38 passed (pre-existing tests), 3 files failed.

## GREEN evidence

From `dash/` with package-local Vitest 3.2.7:

```
npm run typecheck   # pass
npx eslint kyber/canon/store.ts kyber/canon/source-state.ts kyber/canon/source-state.test.ts kyber/canon/store.test.ts kyber/canon/migration.test.ts  # pass
npx vitest run kyber/canon/source-state.test.ts kyber/canon/store.test.ts kyber/canon/migration.test.ts
  Test Files  3 passed (3)
  Tests  55 passed (55)
```

## SUMMARY

Additive schema 11 creates `source_checkpoint` (PK harness_id, source_key) and `record_provenance` (PK span_id). Checkpoint advances only inside `commitSourceUnit` with accepted rows. A constraint failure rolls back; prior checkpoint remains. Coverage expansion is pure (`uncoveredIntervals`). Parser-contract invalidation does not delete canonical rows. Tests use `mkdtemp` only — never `~/.kyberdash/canon.db`.

## OPEN_QUESTIONS

None blocking T3/T5. Writer queue should call `commitSourceUnit`; do not call `upsertMany` then a separate checkpoint write.
