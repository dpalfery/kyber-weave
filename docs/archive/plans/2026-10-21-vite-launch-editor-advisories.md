---
id: plans/2026-10-21-vite-launch-editor-advisories
title: "Patch vite and @vitest/mocker past advisories in dash, dash/web, dash/tray/ui"
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-22
development-mode: test-first
---

# Vite and @vitest/mocker advisories: minimal secure patches across dash, dash/web, dash/tray/ui

**Status: Complete — T0–T5 finished 2026-10-22 on the local checkout (three lockfiles only;
no `package.json` edits; no push — local `main` still diverged from `origin/main`).**

Frontmatter is `status: current`, the governed ontology value for an active plan; the body
and plan index carry the execution lifecycle. `development-mode: test-first` (human did
not opt out). Supervised autonomy: answers below were relayed by the conductor from the
human; nothing was inferred.

## 1. Problem and goal

Dependabot alerts and matching `npm audit` findings report vulnerable dev-only packages
in three prefixes (all scope development):

| Prefix | Package | Resolved | Advisory | Fixed in |
|---|---|---|---|---|
| `dash/` (transitive via `vitest` / `@vitest/mocker`) | `vite` | 8.3.0 | GHSA-698v-4r3x-3vcv, GHSA-r825-w8mm-5pxw, GHSA-587h-7wmj-h5pm (`launch-editor`) | 8.3.3 |
| `dash/web` (devDep `vite ^6.0.5`) | `vite` | 6.4.3 | Same three GHSAs | 6.4.4 |
| `dash/tray/ui` (devDep `vite ^6.0.5`) | `vite` | 6.4.3 | Same three GHSAs | 6.4.4 |
| `dash/` (transitive via `vitest@5.0.0` → `@vitest/mocker@5.0.0`) | `@vitest/mocker` | 5.0.0 | Alert #90: GHSA-xp26-3454-mjgp (Windows backslash traversal bypasses redirect-allowlist fix); Alert #91: GHSA-qxg6-89mg-rqv8 (code injection via unauthenticated manual mock registration) | 5.0.3 |

Goal: reach zero `npm audit` findings in all three prefixes with the smallest safe diff,
clear alerts #90/#91 through the shared `vitest` upgrade path in the same change, keep
every dash gate green, and start from the current checkout with **no push** (local HEAD
has diverged from `origin/main`).

Why this is low-risk: every affected package is a devDependency (or transitive-via-
`vitest`) — never shipped in the `kyberdash` dist or the tray binary — and the vite CVEs
are Windows-only dev-server/`openEditor` issues.

## 2. Approved decisions

Answered by the human via the conductor, 2026-10-21 (provenance: `NEEDS_DECISION`
handoff on this plan; Q1–Q3 all answered, no inference):

| Id | Decision | Provenance |
|---|---|---|
| Q1 | (a) Minimal secure patches per line: web/tray → vite 6.4.4, `dash/` → vite ≥8.3.3 via `audit fix`. The 8.x-line jump was rejected: `@vitejs/plugin-react@4.7.0` peers exclude vite 8, forcing a plugin 4→5 migration for a dev-only advisory. | human, 2026-10-21 |
| Q2 | (a) Leave the `vite ^6.0.5` spec floors untouched — lockfiles only. The range already permits 6.4.4; the lockfile is the enforcement. | human, 2026-10-21 |
| Q3 | (b) EXPAND scope: also fix interacting `@vitest/mocker` alerts #90/#91 via the shared `vitest` upgrade path, as explicit tasks (overriding the plan's verify-only recommendation). | human, 2026-10-21 |

## 3. Investigation findings

Source: bug-crusher investigator verdict (reproduced) plus self-gathered confirmation of
pins, peers, alert bodies, and toolchain:

- `dash/package-lock.json`: `node_modules/vite` = 8.3.0; in-lockfile spec ranges
  `"vite": "^6.4.0 || ^7.0.0 || ^8.0.0"` (vitest) and
  `"vite": "^6.0.0 || ^7.0.0 || ^8.0.0"` (@vitest/mocker). `dash/package.json` carries no
  direct `vite` dep (transitive dev-only via `vitest ^5.0.0`). Fix vehicle: `npm
  --prefix dash audit fix` moves `vite` to ≥8.3.3 inside the already-allowed range.
- `dash/web` and `dash/tray/ui` lockfiles: `node_modules/vite` = 6.4.3 under devDep spec
  `vite ^6.0.5` (`package.json:28` web, `:25` tray-ui), which already permits 6.4.4. Fix
  vehicle: `npm audit fix` per prefix. Per Q2, no spec-file edits.
- Alerts #90/#91 (read live via `gh api`, both `open`, both manifest
  `dash/package-lock.json`, scope development): lockfile `@vitest/mocker` = 5.0.0 falls
  in the vulnerable ranges (`>5.0.0-beta.1,<5.0.3` for #91;
  `>5.0.0-rc.2,<5.0.3` for #90); first patched version on the 5.x line is **5.0.3** for
  both. `dash/package.json` pins `vitest ^5.0.0` / `@vitest/coverage-v8 ^5.0.0`;
  `@vitest/mocker` arrives transitively (`vitest@5.0.0` deps pin `@vitest/mocker 5.0.0`
  exact). Fix vehicle: `npm --prefix dash update vitest` (rolls the 5.x line forward,
  carrying `@vitest/mocker` to ≥5.0.3) — stays inside the declared `^5.0.0` spec, so no
  `package.json` edit.
- 8.x-jump veto (self-gathered from installed `dash/web/node_modules`):
  `@vitejs/plugin-react@4.7.0` peers `vite: ^4.2.0 || ^5.0.0 || ^6.0.0 || ^7.0.0`;
  `@tailwindcss/vite@4.3.1` peers allow `^5.2.0 || ^6 || ^7 || ^8`. Toolchain: node
  v26.12.0, npm 12.0.1.
- Blast radius under Q1=(a)+Q2=(a)+Q3=(b): 3 lockfiles only
  (`dash/package-lock.json`, `dash/web/package-lock.json`, `dash/tray/ui/package-lock.json`).
  `dash/package-lock.json` now additionally moves `vitest`, `@vitest/mocker`, and any
  `vitest`-peer 5.x fellow-travellers — still dev-tooling only. New version implication
  vs the original scope: `vitest` 5.0.0 → latest 5.x is a test-runner upgrade, not just a
  transitive pin move; T4's full dash `test` gate is the backstop.
- Existing coverage: NONE for dependency versions (typecheck/lint/vitest pass with the
  vulnerable versions; findings surface only via Dependabot/`npm audit`). Hence the audit
  gate itself is the Test contract (§4), not a new unit test.
- Failed attempts: none (no fix attempted yet).
- Branch posture: work starts from the current checkout (HEAD `69749213`, clean tree at
  planning time); local HEAD has diverged from `origin/main` (ahead 1 checkpoint commit,
  behind 82) — no push, no PR creation, until a human dispositions the divergence.

Discovery: codegraph 0, docs 2, direct reads 13. No `.codegraph/` index exists in this
checkout, and this harness exposes no `codegraph_explore` tool — the binary is present at
`~/.local/bin/codegraph` but initialization was deferred: no code symbol changes, renames,
or contract changes are in scope, so the index would serve only the drift gate (see GAPS
in §11). Docs: `docs_explore` 1 (dash layout/gates), `docs_status` 1 (root confirmed
`/Users/dave/git/kyber-weave2`, rev `69749213`, clean). Direct reads (all version/pin/
alert evidence the docs index does not cover): 3 `package.json`, 1 plan index, 1
prior-plan frontmatter example, 1 skill head (`kyber-weave-docs`), 1 ontology grep (plan
row), 3 lockfile `rg` traces, 1 installed-`node_modules` peer-dependency read, 2 live
`gh api` alert reads (#90, #91).

## 4. Test contract (`development-mode: test-first`)

No new unit test is written: there is no behavior to pin beyond "no known advisory
resolves in the tree", which `npm audit` already asserts. The audit gate is the contract
test for each prefix; RED must be captured before any fix, GREEN uses the same commands.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 RED (all prefixes) | `dash/`, `dash/web`, `dash/tray/ui` audit gates | `npm --prefix dash audit`; `npm --prefix dash/web audit`; `npm --prefix dash/tray/ui audit` | Audits exit non-zero: `dash/` lists the three vite GHSAs (8.3.0) **and** the two `@vitest/mocker` GHSAs (GHSA-xp26-3454-mjgp, GHSA-qxg6-89mg-rqv8 at 5.0.0); web/tray-ui list the three vite GHSAs (6.4.3) | Save all three outputs showing these findings at the §1 versions | — (RED only) |
| T3 GREEN (all prefixes) | Same three audit gates | Same three commands | All three audits exit zero with zero findings | — | All three outputs saved showing 0 vulnerabilities; `npm --prefix dash ls vite @vitest/mocker vitest` shows vite ≥8.3.3, `@vitest/mocker` ≥5.0.3, vitest within `^5.0.0`; web/tray-ui show vite 6.4.4 |
| T4 regression | dash gates + tray UI gates | `npm --prefix dash run typecheck`, `npm --prefix dash run lint`, `npm --prefix dash run test`, `npm --prefix dash run check:reachable`; `npm --prefix dash/tray/ui run typecheck`, `npm --prefix dash/tray/ui run test` | Lockfile-only change (including the vitest 5.x roll-forward) keeps every gate green | T4 runs only after T3 GREEN; any failure is a fix-scope regression, not a weakened assertion | All six commands exit 0 (web `typecheck` is covered by dash `typecheck`, which includes `web/tsconfig.json`) |

Weakening any assertion in this table to reach green (e.g. `--audit-level=critical`,
skipping a prefix, dropping a gate, accepting `@vitest/mocker` <5.0.3) is a scope change:
return the plan to Draft and require relayed reapproval before implementation resumes.

## 5. Tasks

- **T0 — Capture RED evidence.** Objective: run the §4 RED commands and save outputs
  (must show both the vite and the `@vitest/mocker` findings in `dash/`).
  Files: none written (evidence attached to the task report). Acceptance: three audit
  outputs each list the §1 findings at the §1 versions. Dependencies: none.
  Skills: `test-dev`.
- **T1a — Patch `dash/` vite transitively.** Objective: `npm --prefix dash audit fix`
  (fallback: `npm --prefix dash update vite`) so lockfile `vite` resolves ≥8.3.3 within
  the existing `vitest`/`@vitest/mocker` ranges; no `dash/package.json` edit (no direct
  dep). Files: `dash/package-lock.json`. Acceptance: `npm --prefix dash ls vite` shows
  ≥8.3.3; vitest still resolves within `^5.0.0`. Dependencies: T0. Skills: `test-dev`.
- **T1b — Roll the `dash/` vitest line forward for #90/#91.** Objective:
  `npm --prefix dash update vitest` (fallback: `npm install --prefix dash
  vitest@^5.0.0 @vitest/coverage-v8@^5.0.0`) so lockfile `@vitest/mocker` resolves
  ≥5.0.3; no `package.json` edit (stays inside `^5.0.0`). Runs after T1a: same lockfile,
  so strictly sequenced, not concurrent. Files: `dash/package-lock.json`. Acceptance:
  `npm --prefix dash ls @vitest/mocker vitest` shows mocker ≥5.0.3 and vitest latest
  5.x within spec; any fellow-traveller move beyond `vitest`/`@vitest/*` is reported,
  not silently kept. If T1a's `audit fix` already moved mocker to ≥5.0.3, T1b verifies
  the pin and becomes a no-op with evidence. Dependencies: T1a. Skills: `test-dev`.
- **T2 — Patch `dash/web` + `dash/tray/ui` vite.** Objective: `npm --prefix dash/web
  audit fix` and `npm --prefix dash/tray/ui audit fix` so both lockfiles resolve vite
  6.4.4; per Q2, no `package.json` edits. Files: `dash/web/package-lock.json`,
  `dash/tray/ui/package-lock.json`. Acceptance: `npm --prefix <dir> ls vite` shows
  6.4.4 in both; no other dependency version moves except what `audit fix` requires
  (any additional move is reported, not silently kept). Dependencies: T0 (disjoint
  prefixes from T1a/T1b — may run concurrently with either). Skills: `test-dev`.
- **T3 — Capture GREEN evidence + alert-state check.** Objective: re-run the §4 GREEN
  commands; read-only check of alerts #90/#91 plus the three vite alerts
  (`gh api repos/dpalfery/kyber-weave/dependabot/alerts/{83,87,88,90,91}`) — expect them
  fixed in-tree and auto-dismissed by Dependabot; never manually dismiss an alert.
  Files: none. Acceptance: three zero-finding audits saved with the §4 pins; alert
  states reported verbatim. Dependencies: T1b, T2. Skills: `test-dev`.
- **T4 — Regression gates.** Objective: run the six §4 gate commands. Files: none.
  Acceptance: all six exit 0; any failure is reported as a regression with logs, and the
  fix stays untouched until the conductor routes it. Dependencies: T3. Skills: `test-dev`.
- **T5 — Review and closeout.** Objective: council review of the lockfile diff, then
  `docs-dev` closeout: `docs validate` and `docs drift` (see §11 GAPS for the drift
  precondition), archive-ready index update. Files: `docs/plans/README.md` (index row
  status only). Acceptance: review verdict recorded; both documentation checks pass or
  are explicitly reported skipped-with-cause. Dependencies: T4. Skills: `code-review`,
  `docs-dev`.

## 6. Dependency graph and concurrency

`T0 → {T1a, T2} → T1b → T3 → T4 → T5`. T1a and T2 touch disjoint prefixes and may run
concurrently. T1b consumes T1a's lockfile (same file — strictly sequenced) but is
independent of T2's prefixes, so it may overlap a still-running T2. Every other edge
consumes the predecessor's concrete output (patched lockfiles → audit evidence → gate
signal → review input).

**MAX_CONCURRENCY: 2** (the T1a‖T2 pair, then T1b‖T2 if T2 overruns; all other tasks are
strictly sequenced).

## 7. Risks

- `npm audit fix` / `npm update` moves more than the target (e.g. a `vitest` 5.x minor
  roll inside T1a, or fellow-travellers in T1b/T2): T1b/T2 acceptance already requires
  reporting any extra move; the same rule applies to T1a — extras are surfaced to the
  conductor, not reverted blindly nor silently kept.
- T1b is a test-runner upgrade (5.0.0 → latest 5.x), not a pure pin move: behavior drift
  in the runner is possible; T4's full dash `test` gate is the backstop, and any
  runner-attributable failure returns to the conductor rather than widening scope.
- `dash/tray/ui/node_modules` is absent in this checkout: T2's audit/fix run will install
  first; allow network + install time, and do not mistake install-only diffs for fix content.
- npm 12 against lockfiles written by newer npm: if `audit fix` rewrites lockfile
  structure wholesale, stop and report rather than committing a reformatted world.
- Local HEAD divergence (§3): no push and no PR from this work until a human dispositions
  the ahead/behind state; the change lives and validates on the current checkout.

## 8. Out of scope

- Any vite 8.x migration for `dash/web` / `dash/tray/ui` (rejected under Q1).
- Manually dismissing Dependabot alerts (T3 verifies auto-dismissal read-only).
- Rust/`cargo` tray work, `kyberdash` dist rebuild, pricing re-snapshot, release loop.
- Pushing, PR creation, or origin divergence resolution.

## 9. Verification gates

Per-task GREEN (§4) plus, before review: the three zero-finding audits, the six gate
commands in §4/T4, and `git diff --stat` showing only the §5 file scopes. After review:
`docs-dev` closeout checks in T5.

## 10. Review and closeout

T5 complete 2026-10-22: `code-review` council over the lockfile-only diff returned
**APPROVE** (zero product findings on scope). The declared review gate suite ran to
**15/15 PASS** (`artifacts/gates.json` on the executor host). T4 had already run the six
§4 dash/tray regression commands (all exit 0). `docs-dev` closeout updated this plan body
and the plan index row; `docs validate . --merge-ready` and `docs drift .` results are
recorded in §12. No ADR — ADR waiver: dependency pin moves under existing KyberDash
dev-tooling posture (§1, §8); no new architectural constraint. Canonical harvest: none
required beyond this plan record (lockfile enforcement of Q1–Q3). Archival to
`docs/archive/plans/` awaits a merge-ready PR per `KW-DOC-LIFECYCLE-003`.

## 11. GAPS

- CodeGraph unavailable: no `.codegraph/` index in this checkout and no
  `codegraph_explore` tool in this harness; initialization deferred (no code-symbol change
  in scope). Consequence: `docs drift` may report a vacuous pass — T5 recorded the actual
  run in §12; this plan asserts no `code-refs` joins to drift.
- Dependabot alerts #90/#91 remain **`open`** in GitHub after T3 GREEN: in-tree
  `@vitest/mocker` is **5.0.5** (≥5.0.3) and all three `npm audit` gates exit 0; expect
  auto-dismissal on the next Dependabot rescan against the patched lockfile tree. Do not
  manually dismiss.
- Residual advisory out of scope (§8): low-severity **esbuild** GHSA-g7r4-m6w7-qqqr may
  still appear in broader audits; not targeted by this plan.

## 12. Closeout evidence (T5)

Task evidence roots under `.agent_scratchpad/` (not committed):

| Task | Evidence directory | Notes |
|---|---|---|
| T0 RED | `t0-vite-advisories/` | `audit-dash.log`, `audit-dash-web.log`, `audit-dash-tray-ui.log`, `ls-*.log`, `README.md` |
| T1a | `t1a-vite-advisories/` | `evidence.txt`, `task-gate.json` |
| T1b | `t1b-vitest-roll/` | `evidence.txt`, `diff-stat.txt`, `diagnostics-baseline.txt`, `task-gate.json` |
| T2 | `t2-vite-web-tray/` | post-fix audit logs, `tray-ui-ci.log`, `task-gate.json` |
| T3 GREEN | `t3-vite-green/` | zero-finding `audit-*.log`, `ls-*.log`, `dependabot-states.txt`, `dependabot-alerts.json` |
| T4 regression | `t4-vite-regression/` | six gate logs, `summary.txt`, `evidence.txt`, `task-gate.json` |

**T3 pins (GREEN):** `dash/` — `vite@8.3.5`, `vitest@5.0.5`, `@vitest/mocker@5.0.5`;
`dash/web` and `dash/tray/ui` — `vite@6.4.4` (see `t3-vite-green/ls-*.log`).

**Dependabot states (read-only at T3, `t3-vite-green/dependabot-states.txt`):**

| Alert | Package | State |
|---|---|---|
| #83 | vite (GHSA-698v-4r3x-3vcv) | fixed |
| #87 | vite (GHSA-587h-7wmj-h5pm) | fixed |
| #88 | vite (GHSA-r825-w8mm-5pxw) | fixed |
| #90 | @vitest/mocker (GHSA-xp26-3454-mjgp) | open (pending rescan; tree at 5.0.5) |
| #91 | @vitest/mocker (GHSA-qxg6-89mg-rqv8) | open (pending rescan; tree at 5.0.5) |

**Review:** council **APPROVE**; **15/15** declared review gates PASS (`artifacts/gates.json`).

**T5 documentation checks (2026-10-22):** `docs validate .` — 0 findings (pass).
`docs validate . --merge-ready` — **2 errors** (`KW-DOC-LIFECYCLE-003`): this plan and
`2026-10-07-kyberdash-context-capture.md` remain under `docs/plans/` until a merge PR
archives them (expected for local closeout before PR). `docs drift .` — 0 findings
(vacuous pass: no `.codegraph/` index in this checkout).
