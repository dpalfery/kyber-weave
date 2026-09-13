# refresh T5 — bounded scheduler, writer queue, CLI

## STATUS

GREEN

## ARTIFACTS

- `dash/kyber/refresh/orchestrator.ts`
- `dash/kyber/refresh/scheduler.ts`
- `dash/kyber/refresh/writer.ts`
- `dash/kyber/refresh/report.ts`
- `dash/kyber/refresh/scheduler.test.ts`
- `dash/kyber/refresh/writer.test.ts`
- `dash/kyber/refresh/report.test.ts`
- `dash/kyber/refresh/__snapshots__/report.test.ts.snap`
- `dash/kyber/cli/refresh.ts` (replaced; re-exports orchestrator)
- `dash/kyber/cli/register.ts`
- `dash/kyber/cli/register.test.ts`
- `dash/kyber/cli/refresh.test.ts`

Did not edit `store.ts`, `source-state.ts`, `source-reader.ts`, `registry.ts`, synth, UI, or `dash/src/**`.

## RED

Missing `scheduler.js` / `writer.js` / `report.js` / orchestrator imports (0 tests collected on those files). `register.test.ts` failed: no `--history-weeks`, public `--provider` still present, usage errors exited `1`. `refresh.test.ts` still expected stored harness `gemini`.

## GREEN

`npx --prefix dash vitest run kyber/refresh/scheduler.test.ts kyber/refresh/writer.test.ts kyber/refresh/report.test.ts kyber/cli/refresh.test.ts kyber/cli/register.test.ts` — 5 files, 21 passed.

`npm --prefix dash run typecheck` — pass.

ESLint on exclusive merge-zone files — 0 errors.

## SUMMARY

`kyber-weave dash refresh` now runs one settled job per registry descriptor through an injected concurrency cap and a bounded single-writer queue. `--history-weeks` is a positive integer (default 2); public `--provider` is gone; usage errors exit 2 before the store is created; harness failures exit 1 without cancelling siblings; the store always closes. Gemini is excluded from harness rows. Production Claude `parseAllSessions` is wrapped with `CODEBURN_CACHE_DIR` + `acquireCacheRefreshLock`. `purgeExpiredContent` runs after writes drain.

## OPEN_QUESTIONS

- T3 still calls live `parseAllSessions` when tests omit the stub; T5 production wraps cache-dir/lock but does not change `source-reader.ts`.
- Content-reader reread of T3 envelopes falls back to `ingestProviders`' array path when the native file is absent (test fixtures use fake paths).
