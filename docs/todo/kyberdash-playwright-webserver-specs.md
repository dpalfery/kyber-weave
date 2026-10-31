---
id: todo/kyberdash-playwright-webserver-specs
title: Give the webServer Playwright specs a sanctioned, isolated invocation and run refresh-filters in CI
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-30
---

# Give the webServer Playwright specs a sanctioned, isolated invocation and run refresh-filters in CI

After the [test process storm guard plan](../archive/plans/2026-10-30-test-process-storm-guard.md), the
repository rule is that Playwright runs only through
`scripts/proc-guard.sh run -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts`
(root `AGENTS.md`, `dash/AGENTS.md`, `docs/dash/runbook.md`). That config runs only the two
self-booting specs, `harness-collection.spec.ts` and `refresh-filters.spec.ts`.

Two gaps follow.

1. **The webServer specs have no sanctioned invocation.** `spine`, `deep-links` and `numerals`
   (under `dash/e2e/`) are served by `dash/playwright.config.ts`, whose `webServer` runs
   `npm run build:dash && npm run dev -- web --no-open --port 4747` with `reuseExistingServer`
   outside CI. That server uses the owner's real `~/.kyberdash` state and the port the tray uses,
   and the docs now say never to run that config by hand. So these specs are not run anywhere.
   The plan recorded the isolation problem as out of scope. Decide whether they move to a
   self-booting world like the isolated specs (temp state, port 0, inert spawners through
   `dash/e2e/boot-dashboard.ts`), or are retired.
2. **`refresh-filters.spec.ts` is not in CI.** The `dash-e2e` job in `.github/workflows/ci.yml`
   runs only `dash/e2e/harness-collection.spec.ts`. `refresh-filters.spec.ts` is in the isolated
   config's `testMatch` and was repaired during the plan (a missing `build:dash` step and wrong
   `dist` paths), but nothing runs it on a pull request. Add it to `dash-e2e`, with the dashboard
   build it needs, or record why it stays local.
3. **`refresh-filters.spec.ts` test T8 fails on a stale selector (found by the plan's G3 run,
   2026-10-30).** Run through the isolated config, `harness-collection.spec.ts` passed 4 of 4 and
   T8 of `refresh-filters.spec.ts` failed on the selector `drill-harness-antigravity`
   (`dash/e2e/refresh-filters.spec.ts:160`). The control is no longer rendered: ContextDoctor
   stopped mounting the ScorecardMatrix drill buttons in commit `22895810` (PR #328, 2026-10-10).
   `dash/e2e/spine.spec.ts:24` is broken the same way. The T8 test body is byte-identical to
   `origin/main`, and its data and fixture are present. The test starts no child process, since
   `refreshTempCanon` runs `refreshHarnessSources` in-process, so the failure is not a spawn
   defect. Options:
   - **A (test fix):** rewrite the spec's T8 steps (about lines 154 to 190) to click
     `harness-tab-<harness>` (`web/src/App.tsx` around line 588), which is the navigation the
     product exposes now.
   - **B (product decision):** restore a drill entry for each harness in ContextDoctor. Only
     take this if the owner wants the drill path back; the spec then needs no change.
   Pick one before `refresh-filters` runs in CI (item 2), because it cannot pass as written.

This todo is complete when every spec under `dash/e2e/` either runs in CI under the isolated
config, or is removed, or is documented in `docs/dash/runbook.md` with the one command allowed to
run it.
