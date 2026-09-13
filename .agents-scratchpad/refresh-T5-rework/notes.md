# refresh T5 pass 2 — writer isolation + history-window skip

## STATUS

READY_FOR_REVIEW

## ARTIFACTS

- `dash/kyber/refresh/writer.ts`
- `dash/kyber/refresh/writer.test.ts`
- `dash/kyber/refresh/orchestrator.ts`
- `dash/kyber/cli/refresh.test.ts` (focused 6-week Updated=0 case)

Did not edit store.ts, source-reader, registry, synth, UI, dash/src, or T7 fixtures.

## RED

Writer: shared `processing` promise — a thrown `commit` rejected every waiter and `drain()`, so `formatRefreshReport` never ran.

Coverage: `--history-weeks 6` after a 2-week run re-sliced the full `utcHistoryWindow`, dropped fingerprints on coverage gaps, and counted existing Pi span ids as `Updated=1`.

Writer isolation test was added first; drain-after-failure was the case that failed on the old shared promise. The 6-week contract is asserted in `refresh.integration.test.ts:317` and a focused `refreshHarnessSources` test.

## GREEN

`npx --prefix dash vitest run kyber/refresh/writer.test.ts kyber/refresh/refresh.integration.test.ts kyber/cli/refresh.test.ts` — 3 files, 19 passed (includes the 6-week case).

`npm --prefix dash run typecheck` — pass.

## SUMMARY

`createCanonicalWriter` now catches per item, rejects only that enqueue, keeps pumping, and `drain()` waits without rethrowing. `runHarnessJob` still iterates the full history window (so new units appear) but commits only spans not already in the store and, for a reusable checkpoint, only timestamps outside prior coverage. Already-covered Pi rows stay New=0 Updated=0; checkpoints expand `coveredFromUtc` and keep the old revision token.

## OPEN_QUESTIONS

- Existing rows with the same span id are never re-upserted, even on a revision change. A same-span content edit would need a later equality check.
- Local `eslint` binary was not present under `dash/node_modules/.bin`; typecheck was the lint-adjacent gate run here.
