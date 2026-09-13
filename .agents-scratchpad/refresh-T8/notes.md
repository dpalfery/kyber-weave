# T8 notes

## STATUS: READY_FOR_REVIEW

## ARTIFACTS
- `dash/tests/refresh-filters.test.ts`
- `dash/e2e/refresh-filters.spec.ts`
- `dash/e2e/refresh-filters-world.ts`
- `dash/e2e/refresh-filters-boot.ts`
- `dash/playwright.refresh-filters.config.ts`
- `.agents-scratchpad/refresh-T8/diagnostics-baseline.md`
- `.agents-scratchpad/refresh-T8/diagnostics-sweep.md`
- `.agents-scratchpad/refresh-T8/harnesses.json`
- `.agents-scratchpad/refresh-T8/attention.png`
- `.agents-scratchpad/refresh-T8/empty-cline.png`

## Command / browser evidence
- `npx vitest run tests/refresh-filters.test.ts` (cwd `dash/`) — 1 passed, 260ms test body. Temp DB under `os.tmpdir()/kyber-refresh-t8-*`, never `~/.kyberdash/canon.db`.
- `npx playwright test --config=playwright.refresh-filters.config.ts` — 1 passed in 6.4s (11.7s including boot). Isolated `T8_READY http://127.0.0.1:<ephemeral>` (not 4747). `spine.spec.ts` untouched.
- API: each of Pi, OpenCode, Cursor, Cursor Agent, Kilo shared/legacy, Antigravity + CLI + IDE, Copilot CLI has its own `/api/kyber/runs?harness=` rows; sums reconcile with unfiltered `/api/kyber/runs`; Gemini absent; `/api/kyber/refresh` is JSON 404 (catch-all, not a refresh endpoint).
- Browser: Attention matrix drills each populated id; scorecard dashes carry a title reason; Cline empty state is “No runs recorded” plus unmeasurable dimensions; zero Refresh buttons.

## SUMMARY
T8 is tests-only. Store-backed filters already use canonical ids, so `routes.ts` / `bridge.ts` / `App.tsx` were left alone. Empty registered surfaces stay null KPIs with an explicit `not_measurable` reason rather than a fabricated zero. No refresh UI or endpoint was added.

## OPEN_QUESTIONS
- Copilot VS Code/JetBrains/Agent, Codex splits, Claude splits, Cline, and Kiro are registered-empty in this fixture set (no native files). The suite treats them as unavailable-with-reason; live-source smoke is T7/local, not this temp DB.
- Spine G1–G7 were not re-run here (still owned by the live 4747 host).

## DIAGNOSTICS
clean on T8 paths | inspectcode: skipped (not C# / .NET) | baseline: `.agents-scratchpad/refresh-T8/diagnostics-baseline.md` | remaining: none

## COVERAGE_GAPS
- Findings list still filters harness on the client (`kyberApi.ts`); API `GET /api/kyber/findings` has no harness query. Out of exclusive files if no alias bug.
- `fetchFindings` treats missing `harness` as visible on every tab; buildFindings stamps harness in payload, so refreshed rows are OK — unstamped legacy findings are not covered.
