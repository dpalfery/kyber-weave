---
id: plans/2026-10-31-minimatch-globparts-advisory
title: "Resolve Dependabot alert #103 (minimatch GHSA-g3hv-75wg-m249) in dash"
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-31
development-mode: test-first
documentation-impact: no-design-change
impact-components: [KyberDash]
---

# Dependabot alert #103: minimatch globParts amplification in dash lint tooling

**Status: Archived — complete 2026-10-31.** Alert #103 is closed as inaccurate (D1) with no dependency change (D2-R1, option C). The 10.2.6 copy under typescript-estree is tracked in [typescript-estree-minimatch-11](../../todo/typescript-estree-minimatch-11.md) (D6). The verification record and closeout mapping are in the Closeout mapping section.

`development-mode: test-first` (the human did not opt out). Autonomy: `autonomous` (Config
Reg). Defect/escalation run: the conductor keeps ownership after approval.

## 1. Problem and goal

Dependabot alert [#103](https://github.com/dpalfery/kyber-weave/security/dependabot/103)
(GHSA-g3hv-75wg-m249, CVE-2026-118764, medium, CWE-405) reports npm `minimatch` 3.1.5 in
`dash/package-lock.json`, development scope, transitive. Advisory range `< 11.0.1`; first
patched 11.0.1. The flaw: an attacker-controlled pattern causes globParts amplification in
`firstPhasePreProcess()` when `optimizationLevel >= 2`, then quadratic work in
`secondPhasePreProcess()`.

`npm audit` matches the range against two lockfile nodes:

| Node | Version | Required by | Range |
|---|---|---|---|
| `node_modules/minimatch` | 3.1.5 | `eslint@9.39.5`, `@eslint/config-array@0.21.2`, `@eslint/eslintrc@3.3.6` | `^3.1.5` |
| `node_modules/@typescript-eslint/typescript-estree/node_modules/minimatch` | 10.2.6 | `@typescript-eslint/typescript-estree@8.68.0` (via `typescript-eslint@8.68.0`) | `^10.2.2` |

Goal: close alert #103 with a recorded, evidence-backed outcome. Under D2-R1 the result is no
dependency change: the 10.2.6 copy cannot be reached here and is tracked in a todo. Do not
break eslint 9's `^3.1.5` consumers.

## 2. Investigation findings

Discovery: codegraph 0, docs 0, direct reads 7. CodeGraph indexes repository source, not
`node_modules`. These are third-party package files and the gate configuration, so direct
reads were the only option. The docs MCP was used for the gates only. The governing
precedent, [2026-10-21-vite-launch-editor-advisories](2026-10-21-vite-launch-editor-advisories.md),
was read directly from the plan inventory. Self-gathered: the subagent harness for this
plan had no shell, so `npm view` was not run. T1 carries that probe.

- **3.1.5 lacks the vulnerable code (this closes the investigator's gap).** A search of
  `dash/node_modules/minimatch` (package.json `version: 3.1.5`) finds no
  `optimizationLevel` and no `firstPhasePreProcess`. The vulnerable preprocessing does not
  exist in the 3.x line, so alert #103's named instance is a range false positive.
- **eslint passes no `optimizationLevel`.** eslint 9 and `@eslint/*` call 3.x, which has no
  such option. Their patterns come from `dash/eslint.config.js` and ignore files in the
  repository.
- **The 10.2.6 copy has the vulnerable code but cannot reach it here.** Its only call site
  is `typescript-estree/dist/useProgramFromProjectService.js:204`:
  `minimatch(filePath, pattern, { dot: true })`. The call does not set `optimizationLevel`,
  so minimatch's default (1) applies, and the advisory needs ≥ 2. Correction from the T1
  probe (D2-R1): `dash/eslint.config.js` sets no `parserOptions.projectService` and no
  `allowDefaultProject`, so this function never runs during `npm run lint` at all. Under
  dash's threat model, lint runs over repository-owned files with a repository-owned
  config, locally and in CI, and never on network input.
- **Dependabot names 3.1.5 only.** No open alert covers 10.2.6.
- **Gates:** `ts-lint` (`npm run --prefix dash lint`), `ts-typecheck`, the dash test gate
  and `check:reachable` all include `dash/**` in `.kyber-weave/kyber-weave.yml`.
  `dash-lint` is a task gate for every dash-touching role. No gate runs `npm audit`.
- npm audit's `fixAvailable: eslint@4.0.0` is a semver-major downgrade. It is rejected.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What happens to the 3.1.5 instance that alert #103 names? | **Dismiss alert #103 as `inaccurate`, with a justification comment.** Do not upgrade it, and add no Dependabot ignore rule. | 3.x has no `optimizationLevel` or `firstPhasePreProcess` (§2). Forcing 11.x under `^3.1.5` consumers is a major API break. An ignore rule in `.github/dependabot.yml` would also hide future real minimatch advisories, and that file is outside this plan's surface. | autonomous: recommendation accepted |
| D2 | What happens to the 10.2.6 copy under typescript-estree? | **Probe, then take the first bounded path that works:** (A) a `typescript-eslint` update inside `^8.20.0` whose typescript-estree requires minimatch ≥ 11.0.1, lockfile only. (B) Otherwise a **parent-scoped** override in `dash/package.json`: `"overrides": { "@typescript-eslint/typescript-estree": { "minimatch": "11.0.1" } }`, kept only if every dash gate passes. (C) Otherwise defer to a todo. | The copy cannot be exploited here (default optimizationLevel 1, repository-owned patterns), so a deferral is safe. Path A changes no manifest. B is the smallest change that clears `npm audit`. | autonomous: recommendation accepted |
| D2-R1 | D2 revision, settled on the T1 evidence (2026-10-31): option B or option C? | **C: no dependency change.** Leave typescript-estree's minimatch 10.2.6 copy as it is. Record the D6 todo. Alert #103 is closed by the D1 dismissal alone. This **supersedes D2**, whose row stays above as history. | T1 found: (1) Option A is impossible. typescript-estree up to 8.72.1 still declares `minimatch: ^10.2.2`. (2) No patched 10.x release exists. 10.2.6 is the last 10.x, and the fix exists only in 11.0.1 and later (latest 11.0.3). (3) 11.0.1 would load (CJS export, node 18 \|\| 20 \|\| >=22). (4) Option B would bump `brace-expansion` to ^5.0.12 and add a new package, `@isaacs/nonrecursive-glob-match`. (5) `dash/eslint.config.js` sets no `projectService` or `allowDefaultProject`, so `filePathMatchedBy` never runs. No gate exercises the minimatch call, so B's gates would prove only that the module loads, not that matching still behaves the same. (6) `npm audit` stays non-clean under every path because of 3.1.5, so B buys no clean audit. Weighing it: the 10.2.6 copy cannot be reached here (no project service, default optimizationLevel 1, repository-owned patterns). No alert covers it. B would force a major version outside the parent's declared `^10.2.2` range and add a new transitive package, with no way to prove behaviour unchanged. Upstream will move off ^10 on its own schedule. Option B, had it been chosen, would have used `^11.0.1`, not an exact pin, so security patches still flow. | autonomous: recommendation accepted |
| D3 | Is a global `minimatch` override safe? | **No.** Never write a top-level `"minimatch": …` override or `"minimatch@^10"` keyed override. Only the per-parent form in D2(B) is allowed. | A global override would force 11.x onto eslint 9's `^3.1.5` consumers. The parent-keyed form affects only typescript-estree's subtree. | autonomous: recommendation accepted |
| D4 | How is lockfile churn controlled? | Regenerate with the toolchain the precedent used (node v26.x, npm 12.x) and record `npm --version` in evidence. Allowed diff: the typescript-estree nested minimatch node, its `brace-expansion` and `balanced-match` subtree, and, for path A, the `typescript-eslint` / `@typescript-eslint/*` package entries. Any other changed key fails the gate and gets reverted. | The precedent plan used node v26.12.0 and npm 12.0.1. Use `npm install --prefix dash` (A: `npm update --prefix dash typescript-eslint`). Never use `audit fix`. **Superseded by D2-R1:** under C, `dash/package.json` and `dash/package-lock.json` must show **zero** diff. T1 found that this whitelist was wrong for B: it omitted the `brace-expansion` ^5.0.12 bump and the new `@isaacs/nonrecursive-glob-match` package. | autonomous: recommendation accepted |
| D5 | Does this need an ADR or a docs rule? | **No ADR, no new rule.** ADR waiver: a dev-only dependency triage with no new boundary, dependency or schema, matching the vite precedent. | Writing a dependency-vulnerability policy rule would widen scope to make one finding tidy. The dismissal justification lives on the alert and in this plan. | autonomous: recommendation accepted |
| D6 | Is any work deferred? | Only under D2(C): a todo under `docs/todo/` covering the typescript-estree minimatch 10.2.6 copy. It is reopened when `typescript-eslint` ships a minimatch ≥ 11.0.1 dependency. | No todo is written under A or B. **Active under D2-R1.** Todo `docs/todo/typescript-estree-minimatch-11.md`. Reopen trigger: a `typescript-eslint` 8.x or later release whose `@typescript-eslint/typescript-estree` declares minimatch ≥ 11.0.1; then run `npm update --prefix dash typescript-eslint` and the dash gates. Escalation trigger: dash enables `parserOptions.projectService` with `allowDefaultProject`, or a new advisory makes the default optimizationLevel reachable. If either happens, reconsider the option B override. | autonomous: recommendation accepted |

## 4. Tasks (test-first)

All npm, test and gate commands run as `scripts/proc-guard.sh run -- <command>` from the
repository root.

- **T0 — Baseline (done, read-only).** Run `npm audit --prefix dash --json` and
  `npm ls --prefix dash minimatch`. Both minimatch nodes are present, range `<11.0.1`.
- **T1 — Probe (done 2026-10-31).** Findings are recorded in D2-R1. Outcome: option C.
- **T2 — No dependency change (D2-R1).** `dash/package.json` and `dash/package-lock.json`
  must stay untouched. The test-first check is evidence that the vulnerable code cannot
  run: T0's audit and `ls` output, the 3.1.5 source search (§2), and the
  `useProgramFromProjectService.js:204` call site with no `projectService` configured.
  This plan adds no new executable test, because nothing executable changes.
- **T3 — Todo (docs-dev or architect).** Write `docs/todo/typescript-estree-minimatch-11.md`
  with the D6 reopen and escalation triggers, register it under `<todo-index>`, then run
  `docs validate` and `docs drift`.
- **T4 — Alert disposition (conductor).** Dismiss with
  `gh api -X PATCH repos/dpalfery/kyber-weave/dependabot/alerts/103 -f state=dismissed -f dismissed_reason=inaccurate -f dismissed_comment="minimatch 3.1.5 predates optimizationLevel/firstPhasePreProcess (GHSA-g3hv-75wg-m249 code path absent); eslint 9 passes repository-owned patterns. See docs/archive/plans/2026-10-31-minimatch-globparts-advisory.md"`.
  Do this only after the PR merges, so the comment's link resolves.
- **T5 — Closeout (docs-dev).** Archive this plan with `git mv` to `docs/archive/plans/`,
  move its index row, run `docs validate --merge-ready` and `docs drift`, then run
  `review gates . --all` before push. Do not run Playwright: no e2e surface changes. If
  Playwright is ever needed, run it only with
  `--config dash/playwright.isolated.config.ts`.

## 5. Verification contract

| Check | Pass condition |
|---|---|
| `git diff -- dash/package.json dash/package-lock.json` | Empty (D2-R1). |
| `npm audit --prefix dash --json` | Unchanged from T0. Both minimatch nodes remain. The 3.1.5 node is dismissed under D1, and the 10.2.6 node is tracked by the D6 todo. A non-clean audit is expected and accepted. |
| Gates via `review gates` (scoped, then `--all` before push) | All pass. The change is docs-only, so the dash gates are not triggered. No gate exercises the minimatch call, and this check claims no behavioural evidence. |
| `docs validate --merge-ready`, `docs drift` | 0 findings after archive. |

## Design harvest

| Item | Target component | Doc type | Disposition |
|---|---|---|---|
| D1–D6 alert triage | KyberDash | — | Documentation waiver: a transient dependency triage that leaves no architectural or operational invariant. The rationale lives in the alert's dismissal comment and this archived plan. No ADR — ADR waiver: a dev-only dependency patch with no new boundary, dependency or schema. |

## Closeout mapping

**Completed 2026-10-31.** Archived under `docs/archive/plans/`. The Decisions table is preserved unchanged, including the superseded D2 row kept as history.

### Verification evidence

Produced in the closeout session. No dependency manifest changed.

| Check | Result |
|---|---|
| `npm audit --prefix dash --json` | `vulnerabilities.minimatch` with `range: "<11.0.1"` and two nodes: `node_modules/minimatch` (3.1.5) and `node_modules/@typescript-eslint/typescript-estree/node_modules/minimatch` (10.2.6). Unchanged before and after. A non-clean audit is accepted under the Verification contract. |
| `dash/node_modules/minimatch` | Resolves to 3.1.5. A search for `firstPhasePreProcess` and `optimizationLevel` returns nothing. The advisory code path is absent from the 3.x line. |
| `@typescript-eslint/typescript-estree/dist/useProgramFromProjectService.js` | Line 41 is `const minimatch_1 = require("minimatch");`. Line 204 is `(0, minimatch_1.minimatch)(filePath, pattern, { dot: true })`, with no `optimizationLevel`. |
| `grep -n "allowDefaultProject\|projectService\|parserOptions" dash/eslint.config.js` | No matches. The only minimatch call site in typescript-estree never runs under `npm run lint`. |
| T1 probe | Latest typescript-eslint 8.x (8.72.1) resolves typescript-estree 8.72.1, which declares `minimatch: ^10.2.2`. No 10.x release newer than 10.2.6 exists. The patched line is 11.0.1 and later (latest 11.0.3). Option A was impossible. |
| `git diff -- dash/package.json dash/package-lock.json` | Empty. |
| `docs validate .` and `docs drift .` | 0 findings for this plan and for the todo `docs/todo/typescript-estree-minimatch-11.md`. |

### Mapping

Documentation-impact is `no-design-change`, impact component is KyberDash, last-reviewed is 2026-10-31 for every row, and status is archived.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | Documentation waiver: the alert #103 dismissal is a transient dependency triage with no surviving invariant. The dismissal comment is the T4 action (conductor, after merge); the rationale stays in Investigation findings and Decisions of this archived plan. | no-design-change | KyberDash | 2026-10-31 | archived |
| D2 |  Documentation waiver: D2 is superseded by D2-R1; the rejected options A and B leave no surviving invariant. | no-design-change | KyberDash | 2026-10-31 | archived |
| D2-R1 | Documentation waiver: option C is a dependency deferral with no surviving architectural invariant; the only durable follow-up is the D6 todo, linked on row D6. | no-design-change | KyberDash | 2026-10-31 | archived |
| D3 | Documentation waiver: the prohibition on global `minimatch` overrides is a dependency-tooling constraint of this triage; its reopen guidance survives in the [typescript-estree todo](../../todo/typescript-estree-minimatch-11.md) ("Use the parent-scoped override form only"). | no-design-change | KyberDash | 2026-10-31 | archived |
| D4 | Documentation waiver: superseded by D2-R1. `dash/package.json` and `dash/package-lock.json` show zero diff, so no lockfile whitelist survives. | no-design-change | KyberDash | 2026-10-31 | archived |
| D5 | Documentation waiver: no lasting docs rule follows from this triage. No ADR — ADR waiver: a dev-only dependency triage with no new boundary, dependency or schema, matching the vite precedent (archived). | no-design-change | KyberDash | 2026-10-31 | archived |
| D6 | [docs/todo/typescript-estree-minimatch-11.md](../../todo/typescript-estree-minimatch-11.md): the 10.2.6 copy, reopen trigger (`typescript-eslint` release with `minimatch >= 11.0.1`) and escalation trigger (`projectService` with `allowDefaultProject`, or a reachable `optimizationLevel`). Registered in [docs/todo/README.md](../../todo/README.md). | no-design-change | KyberDash | 2026-10-31 | archived |
