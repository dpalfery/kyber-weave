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

**Status: Archived — complete 2026-10-31.** The owner directed the full bump (D2-R2): `dash/package.json` carries a top-level `overrides` entry pinning `minimatch` to `^11.0.1`, and the lockfile resolves every consumer to 11.0.3. `npm audit --prefix dash` reports zero vulnerabilities, no minimatch below 11.0.1 remains in the lockfile, and every dash gate passes. The 10.2.6 copy D2-R1 deferred is gone. [typescript-estree-minimatch-11](../../todo/typescript-estree-minimatch-11.md) now tracks only removing the override once upstream resolves 11 on its own. The verification record and closeout mapping are in the Closeout mapping section.

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

Goal: close alert #103 with a recorded, evidence-backed outcome. **Shipped outcome (D2-R2):** a
top-level `minimatch` override in `dash/package.json` moves all four consumers to 11.0.1 or
later, which the lockfile resolves to 11.0.3, so no in-range copy remains. The earlier D2-R1
conclusion — no dependency change, because the 10.2.6 copy could not be reached — is kept as
history below and was superseded by the owner's directive. The constraint it named still
holds: eslint 9's `^3.1.5` consumers must keep working, which `ts-lint` demonstrates.

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
| D1 | What happens to the 3.1.5 instance that alert #103 names? | **Dismiss alert #103 as `inaccurate`, with a justification comment.** Do not upgrade it, and add no Dependabot ignore rule. | 3.x has no `optimizationLevel` or `firstPhasePreProcess` (§2). Forcing 11.x under `^3.1.5` consumers is a major API break. An ignore rule in `.github/dependabot.yml` would also hide future real minimatch advisories, and that file is outside this plan's surface. | autonomous: recommendation accepted; **superseded by D2-R2** — the 3.1.5 copy is gone from the lockfile, so no dismissal is issued. |
| D2 | What happens to the 10.2.6 copy under typescript-estree? | **Probe, then take the first bounded path that works:** (A) a `typescript-eslint` update inside `^8.20.0` whose typescript-estree requires minimatch ≥ 11.0.1, lockfile only. (B) Otherwise a **parent-scoped** override in `dash/package.json`: `"overrides": { "@typescript-eslint/typescript-estree": { "minimatch": "11.0.1" } }`, kept only if every dash gate passes. (C) Otherwise defer to a todo. | The copy cannot be exploited here (default optimizationLevel 1, repository-owned patterns), so a deferral is safe. Path A changes no manifest. B is the smallest change that clears `npm audit`. | autonomous: recommendation accepted |
| D2-R1 | D2 revision, settled on the T1 evidence (2026-10-31): option B or option C? | **C: no dependency change.** Leave typescript-estree's minimatch 10.2.6 copy as it is. Record the D6 todo. Alert #103 is closed by the D1 dismissal alone, which T4 performs after merge. This **supersedes D2**, whose row stays above as history. | T1 found: (1) Option A is impossible. typescript-estree up to 8.72.1 still declares `minimatch: ^10.2.2`. (2) No patched 10.x release exists. 10.2.6 is the last 10.x, and the fix exists only in 11.0.1 and later (latest 11.0.3). (3) 11.0.1 would load (CJS export, node 18 \|\| 20 \|\| >=22). (4) Option B would bump `brace-expansion` to ^5.0.12 and add a new package, `@isaacs/nonrecursive-glob-match`. (5) `dash/eslint.config.js` sets no `projectService` or `allowDefaultProject`, so `filePathMatchedBy` never runs. No gate exercises the minimatch call, so B's gates would prove only that the module loads, not that matching still behaves the same. (6) `npm audit` stays non-clean under every path because of 3.1.5, so B buys no clean audit. Weighing it: the 10.2.6 copy cannot be reached here (no project service, default optimizationLevel 1, repository-owned patterns). No alert covers it. B would force a major version outside the parent's declared `^10.2.2` range and add a new transitive package, with no way to prove behaviour unchanged. Upstream will move off ^10 on its own schedule. Option B, had it been chosen, would have used `^11.0.1`, not an exact pin, so security patches still flow. | autonomous: recommendation accepted |
| D3 | Is a global `minimatch` override safe? | **No.** Never write a top-level `"minimatch": …` override or `"minimatch@^10"` keyed override. Only the per-parent form in D2(B) is allowed. | A global override would force 11.x onto eslint 9's `^3.1.5` consumers. The parent-keyed form affects only typescript-estree's subtree. | autonomous: recommendation accepted; **superseded by D2-R2** — the premise was tested and did not hold: eslint 9's consumers reach minimatch only through the `Minimatch` class, which 11 still exports. |
| D2-R2 | D2 revision, settled on the owner's directive (2026-10-31): take the bump, or keep the D2-R1 deferral? | **Ship the bump.** Add `"overrides": { "minimatch": "^11.0.1" }` to `dash/package.json` and regenerate `dash/package-lock.json`, so all four consumers resolve one deduped `minimatch` 11.0.3. Supersedes D2-R1 and D3. | Feasibility was checked before applying. `@eslint/config-array` does `const Minimatch = minimatch.Minimatch`; `@eslint/eslintrc` does `const { Minimatch } = minimatch__default["default"]` after an interop wrap. Both use the class, not the 3.x callable function, and minimatch 10 and 11 export it — so the breakage D3 predicted does not occur. Measured after the change: the lockfile diff is exactly `minimatch` 3.1.5→11.0.3, `brace-expansion` 1.1.21→5.0.12, `balanced-match` 1.0.2→4.0.4, removal of `concat-map` and the three nested typescript-estree copies, and addition of `@isaacs/nonrecursive-glob-match`; `npm audit --prefix dash --json` reports 0 vulnerabilities at every severity; `ts-lint`, `ts-typecheck`, `ts-test` and `ts-reachable` all pass, and `ts-lint` runs eslint's glob engine over the whole tree. Residual risk: the override sits above eslint 9's declared `^3.1.5` range, so an eslint upgrade may in future need a range that admits 11; the todo carries that. | owner directive 2026-10-31 ("lets bump it all the way to 11"), executed on this branch |
| D4 | How is lockfile churn controlled? | Regenerate with the toolchain the precedent used (node v26.x, npm 12.x) and record `npm --version` in evidence. Allowed diff: the typescript-estree nested minimatch node, its `brace-expansion` and `balanced-match` subtree, and, for path A, the `typescript-eslint` / `@typescript-eslint/*` package entries. Any other changed key fails the gate and gets reverted. | The precedent plan used node v26.12.0 and npm 12.0.1. Use `npm install --prefix dash` (A: `npm update --prefix dash typescript-eslint`). Never use `audit fix`. **Superseded by D2-R1:** under C, `dash/package.json` and `dash/package-lock.json` must show **zero** diff. T1 found that this whitelist was wrong for B: it omitted the `brace-expansion` ^5.0.12 bump and the new `@isaacs/nonrecursive-glob-match` package. | autonomous: recommendation accepted |
| D5 | Does this need an ADR or a docs rule? | **No ADR, no new rule.** ADR waiver: a dev-only dependency pin with no new direct dependency, boundary or schema, matching the vite precedent. | Writing a dependency-vulnerability policy rule would widen scope to make one finding tidy. The measurement that made the pin safe lives in D2-R2, and the removal trigger lives in the todo. | autonomous: recommendation accepted |
| D6 | Is any work deferred? | Only under D2(C): a todo under `docs/todo/` covering the typescript-estree minimatch 10.2.6 copy. It is reopened when `typescript-eslint` ships a minimatch ≥ 11.0.1 dependency. | No todo is written under A or B. **Active under D2-R1.** Todo `docs/todo/typescript-estree-minimatch-11.md`. Reopen trigger: a `typescript-eslint` 8.x or later release whose `@typescript-eslint/typescript-estree` declares minimatch ≥ 11.0.1; then run `npm update --prefix dash typescript-eslint` and the dash gates. Escalation trigger: dash enables `parserOptions.projectService` with `allowDefaultProject`, or a new advisory makes the default optimizationLevel reachable. If either happens, reconsider the option B override. | autonomous: recommendation accepted; **superseded by D2-R2** — the override shipped now, so the todo tracks its removal instead of a deferral. |

## 4. Tasks (test-first)

All npm, test and gate commands run as `scripts/proc-guard.sh run -- <command>` from the
repository root.

- **T0 — Baseline (done, read-only).** Run `npm audit --prefix dash --json` and
  `npm ls --prefix dash minimatch`. Both minimatch nodes were present, range `<11.0.1`.
- **T1 — Probe (done 2026-10-31).** Findings are recorded in D2-R1. Outcome before the
  owner directive: option C.
- **T2 — Dependency change (D2-R2).** Add `"overrides": { "minimatch": "^11.0.1" }` to
  `dash/package.json`, then `npm install --prefix dash`. The test-first check is the
  failing state T0 recorded: two nodes below 11.0.1 and a non-clean `npm audit`. It turns
  green when the lockfile carries one deduped `minimatch` 11.0.3 and audit reports none.
- **T3 — Todo (done).** `docs/todo/typescript-estree-minimatch-11.md` records the reopen
  and escalation triggers for the override, and is registered under `<todo-index>`.
- **T4 — Alert disposition (conductor, after merge).** The lockfile no longer contains any
  minimatch below 11.0.1, so Dependabot should mark alert #103 fixed on its next scan with
  no dismissal. Confirm that; if it lingers, dismiss as `inaccurate` with the D1 rationale,
  citing this plan.
- **T5 — Closeout (docs-dev).** Archive this plan with `git mv` to `docs/archive/plans/`,
  move its index row, run `docs validate --merge-ready` and `docs drift`, then run
  `review gates . --all` before push. Do not run Playwright: no e2e surface changes. If
  Playwright is ever needed, run it only with
  `--config dash/playwright.isolated.config.ts`.

## 5. Verification contract

| Check | Pass condition |
|---|---|
| `git diff -- dash/package.json` | Exactly one addition: the top-level `overrides` entry (D2-R2). |
| Lockfile key diff | Only `minimatch` (→11.0.3), `brace-expansion` (→5.0.12), `balanced-match` (→4.0.4), `@isaacs/nonrecursive-glob-match` added, `concat-map` and the nested typescript-estree copies removed. |
| `npm ls --prefix dash minimatch` | One deduped `minimatch@11.0.3` under all four consumers. |
| `npm audit --prefix dash --json` | 0 vulnerabilities at every severity. |
| Dash gates via `review gates` | `ts-typecheck`, `ts-lint`, `ts-test` and `ts-reachable` all pass. `ts-lint` exercises eslint's glob engine over the whole tree, which is the behavioural evidence a docs-only triage could not give. |
| `docs validate --merge-ready`, `docs drift` | 0 findings after archive. |

## Design harvest

| Item | Target component | Doc type | Disposition |
|---|---|---|---|
| D1–D6 alert triage | KyberDash | — | Documentation waiver: a transient dependency triage that leaves no architectural or operational invariant. The rationale lives in this archived plan. No ADR — ADR waiver: a dev-only dependency pin with no new direct dependency, boundary or schema. |

## Closeout mapping

**Completed 2026-10-31.** Archived under `docs/archive/plans/`. Every revision is kept as history: D2 superseded by D2-R1, both superseded by D2-R2, and D1, D3 and D6 superseded by D2-R2.

### Verification evidence

Produced in the closeout session, then re-run after the D2-R2 bump moved the dependency. The T0/T1 rows are the baseline the bump had to change; the D2-R2 rows are the shipped state.

| Check | Result |
|---|---|
| `npm audit --prefix dash --json` (T0) | Before the bump: `vulnerabilities.minimatch`, `range: "<11.0.1"`, two nodes — `node_modules/minimatch` (3.1.5) and `node_modules/@typescript-eslint/typescript-estree/node_modules/minimatch` (10.2.6). |
| `npm audit --prefix dash --json` (after D2-R2) | `{"moderate": 0, "high": 0, "critical": 0, "low": 0, "info": 0, "total": 0}`; `vulnerabilities: {}`. |
| `npm ls --prefix dash minimatch` (after D2-R2) | `minimatch@11.0.3 deduped` under `eslint@9.39.5`, `@eslint/config-array@0.21.2`, `@eslint/eslintrc@3.3.6` and `@typescript-eslint/typescript-estree@8.68.0`. No other copy. |
| Lockfile key diff (after D2-R2) | `minimatch` 3.1.5→11.0.3; `brace-expansion` 1.1.21→5.0.12; `balanced-match` 1.0.2→4.0.4; added `node_modules/@isaacs/nonrecursive-glob-match`; removed `node_modules/concat-map` and the three nested `@typescript-eslint/typescript-estree/node_modules/*` copies. 31 insertions, 61 deletions, nothing else. |
| Dash gates (after D2-R2) | `ts-typecheck` passed (11.8 s), `ts-test` passed (46.9 s), `ts-lint` passed (10.0 s), `ts-reachable` passed (0.9 s), `docs-drift` passed (1.4 s): 5 gates, 0 failed. `ts-lint` runs eslint's glob engine over the whole tree and is the behavioural evidence the docs-only plan could not claim. |
| `dash/node_modules/minimatch` (T1) | Resolved 3.1.5 before the bump; a search for `firstPhasePreProcess` and `optimizationLevel` returned nothing, so the advisory code path was absent from the 3.x line. |
| `@typescript-eslint/typescript-estree/dist/useProgramFromProjectService.js` | Line 41 is `const minimatch_1 = require("minimatch");`. Line 204 is `(0, minimatch_1.minimatch)(filePath, pattern, { dot: true })`, with no `optimizationLevel`. |
| `grep -n "allowDefaultProject\|projectService\|parserOptions" dash/eslint.config.js` | No matches. The only minimatch call site in typescript-estree never ran under `npm run lint`, which is why the pre-bump triage could justify a deferral. |
| T1 probe | Latest typescript-eslint 8.x (8.72.1) resolves typescript-estree 8.72.1, which declares `minimatch: ^10.2.2`. No 10.x release newer than 10.2.6 exists; the patched line is 11.0.1 and later (latest 11.0.3). Option A was impossible, so the bump needed an override. |
| `docs validate .` and `docs drift .` | 0 findings for this plan and for the todo `docs/todo/typescript-estree-minimatch-11.md`. |

### Mapping

Documentation-impact is `no-design-change`, impact component is KyberDash, last-reviewed is 2026-10-31 for every row, and status is archived.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | Documentation waiver: the finding that 3.1.5 lacks the advisory's code path was triage context for a superseded path; it survives only in Investigation findings and Decisions. No dismissal is issued, because D2-R2 removes every copy below 11.0.1. | no-design-change | KyberDash | 2026-10-31 | archived |
| D2 |  Documentation waiver: D2 is superseded by D2-R1, which D2-R2 supersedes in turn; the rejected options A and B leave no surviving invariant. | no-design-change | KyberDash | 2026-10-31 | archived |
| D2-R1 | Documentation waiver: the option-C deferral was superseded by D2-R2 before release; nothing it deferred survives. | no-design-change | KyberDash | 2026-10-31 | archived |
| D2-R2 | Documentation waiver: the override is a dependency-pin change with no architectural invariant, but it does carry one durable operational fact — the override must be removed once upstream resolves 11 on its own — and that fact is the [typescript-estree todo](../../todo/typescript-estree-minimatch-11.md). The measurement that made it safe (eslint uses `Minimatch`, not the 3.x callable) stays in the Decisions row. | no-design-change | KyberDash | 2026-10-31 | archived |
| D3 | Documentation waiver: the blanket prohibition was measured and withdrawn; its surviving guidance is "use the smallest override that clears the finding", recorded in the [typescript-estree todo](../../todo/typescript-estree-minimatch-11.md). | no-design-change | KyberDash | 2026-10-31 | archived |
| D4 | Documentation waiver: the lockfile whitelist is a verification detail, and the measured diff is recorded in Verification evidence above. | no-design-change | KyberDash | 2026-10-31 | archived |
| D5 | Documentation waiver: no lasting docs rule follows from this triage. No ADR — ADR waiver: a dev-only dependency pin with no new direct dependency, boundary or schema, matching the vite precedent (archived). | no-design-change | KyberDash | 2026-10-31 | archived |
| D6 | [docs/todo/typescript-estree-minimatch-11.md](../../todo/typescript-estree-minimatch-11.md): removing the `minimatch` override once `eslint` and `typescript-eslint` resolve 11.0.1 or later on their own, plus the escalation trigger (`projectService` with `allowDefaultProject`). Registered in [docs/todo/README.md](../../todo/README.md). | no-design-change | KyberDash | 2026-10-31 | archived |
