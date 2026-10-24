---
id: plans/2026-10-24-issue-386-reduce-autonomous-gates
title: "Reduce autonomous gates (issue #386): audit, auto-remediate, scope size ceiling, collapse spec gates, bound concurrency"
doc-type: plan
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-24
development-mode: test-first
---

# Reduce autonomous gates (issue #386)

**Status: Draft 2026-10-24.** One plan covering items A–E (Q1). Branch: `dp-kyber-bot.grokbot.opencode/issue-386-reduce-autonomous-gates`.

**Development mode:** `test-first`. **Autonomy:** `supervised` for plan approval; execution autonomy resolved per-run by the conductor (`autonomous` runs follow the autonomy contract).

> Discovery: CodeGraph unavailable in this authoring harness (no `.codegraph/` index read path exposed); docs MCP unavailable in this harness. Fallback to direct reads. `Discovery: codegraph 0, docs 0, direct reads 12` — direct reads were `products/kyber-squad/agents/conductor/references/autonomy.md`, `execution-and-review.md`, `plan-path.md`, `spec-path.md`, `products/kyber-squad/agents/architect/references/plan-authoring.md`, `autonomy-decisions.md`, `products/kyber-squad/skills/product-owner/SKILL.md`, `products/kyber-squad/skills/code-review/SKILL.md`, `.kyber-weave/kyber-weave.yml`, `src/KyberWeave.Core/Review/VerdictEngine.cs`, `ChangedFiles.cs`, `ReviewJson.cs`, `src/KyberWeave.Cli/Commands/Review/ReviewVerdictCommand.cs`.

---

## 1. Problem and goal

### 1.1 Problem

Autonomous runs stop at too many human gates. Each stop parks work, waits for a human, and costs a full context reload. Issue #386 asks to reduce gates without removing any safety gate on destructive or irreversible action.

PR #424 already removed many gates. What remains on current `main` (`2089297`) is the baseline for this plan — the plan must determine from `main` what remains and deliver the rest, not re-litigate what #424 already removed.

### 1.2 Goal

Deliver items A–E as one plan (Q1 decision):

- **A — Gate audit inventory:** enumerate every remaining gate on `main` (`2089297`), classify keep / reduce / collapse, and mark every destructive-action gate as keep.
- **B — Auto-remediate before NEEDS_HUMAN:** for actionable Critical/Major review findings, attempt bounded deterministic remediation before parking as `NEEDS_HUMAN`; gate only on Critical and Major (Q3).
- **C — KW-REVIEW-009 scoping:** exclude lockfiles and scratch output from `ChangedLines` counting so generated churn cannot suppress remediation or force a size escalation; size never suppresses remediation.
- **D — Spec-gate collapse:** merge `tasks` approval + `approve-and-execute` when there are no gaps/questions; allow a consolidated review (max 2 gates total in the happy path). Supervised keeps all gates.
- **E — Concurrency cap + isolation:** configurable `max_concurrent_workers` (default 4), .NET flags `-nr:false -p:UseSharedCompilation=false`, per-task artifact isolation; clarify the "always push PR" rule to match the current `autonomy.md` contract.

### 1.3 Out of scope

- Any change to PR #424's already-removed gates except to inventory them as removed.
- Removing or weakening any gate on a destructive/irreversible action (see §2). If implementation finds such a gate in the way, stop and ask (hard rule, §2).
- Removing the `docs init` step (kept per Q2).
- Changing verdict severity for `Minor`/`Info` beyond "does not gate" (Q3 scope is Critical + Major only).
- Commit, push, merge, or PR creation by this plan's own authoring session.

---

## 2. Hard rules (binding on every task)

1. **NEVER push to `main`.** All changes via a pull request from `dp-kyber-bot.grokbot.opencode/issue-386-reduce-autonomous-gates`.
2. **Never merge, rebase, or force-push.** Keep the branch current by **merging `main` in** (`git merge origin/main`), never by rebasing.
3. **Do NOT remove any gate on a destructive/irreversible action.** If the plan as written would touch one, **stop and ask** with a question id — do not implement around it.
4. **Treat warnings as errors** (`TreatWarningsAsErrors` + `AnalysisMode=all`). Fix the cause; do not add to `NoWarn`.
5. **Editing anything under `docs/` means re-running `docs validate` and `docs drift`** in the same change.
6. Test-first: every implementation task carries a Test contract with RED evidence before GREEN (see §7).

### 2.1 Gates that MUST NOT be touched (keep as-is)

These stay human in autonomous mode. No task in this plan weakens them:

- **merge, publish, delete, spend** — hard stops in `products/kyber-squad/agents/conductor/references/autonomy.md` (`## Hard stops`). Committing to a feature branch, pushing it, and opening a **draft** PR are allowed; merging is not.
- **`STATUS: BLOCKED` unresolvable / write errors** (`PLAN_WRITE_ERROR`, `SPEC_WRITE_ERROR`, missing credentials/access) — relay and stop.
- **`RECOMMENDED: none` hard blocks** — only item relayed mid-run; batched with `SOURCES_SEARCHED` + `COST_OF_WRONG`.
- **`review.policy.always-human` paths** — `products/kyber-squad/profiles/capabilities.yml`, `products/kyber-squad/agents/**`, `.kyber-weave/kyber-weave.yml`, `**/*secret*`, `**/*credential*`. Touching one forces `KW-REVIEW-008` → `NEEDS_HUMAN` by design (VerdictEngine rule 1, before any finding is weighed).
- **`KW-REVIEW-008` (ReservedPath) itself** — always-human remains. No task removes or narrows it.
- **Blocking-gate failure → `REQUEST_CHANGES`** (VerdictEngine rule 3) — a failing blocking gate cannot be approved past.
- **Always-human paths in code-review procedure** (`code-reviewer` per-task ban, whole-change council, pre-push self-review sync/council/checklist/evidence) — procedure stays; only finding-severity gating changes per Q3.

If any task below is found at implementation time to require touching one of these, the worker stops that task and the conductor returns `STATUS: NEEDS_DECISION` with a new question id. That is the designed outcome, not a failure.

---

## 3. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| Q1 | One plan or several for items A–E? | **One plan** covering A–E. | Owner direction: single plan keeps the gate inventory, remediation, size scoping, spec collapse, and concurrency cap mutually consistent and reviewable as one unit. | ANSWERED — user decision 2026-10-24 |
| Q2 | Keep the `docs init` step? | **Yes — keep it.** No task removes `docs init`, the Config Reg generation, or the `docs validate` / `docs drift` checks it scaffolds. | `docs init` is the mechanical half (host config, catalog, ontology); removal would reintroduce drift the corpus gates exist to catch. | ANSWERED — user decision 2026-10-24 |
| Q3 | Which finding severities gate? | **Gate only on Critical and Major review findings.** Minor/Info never block, never force remediation loops, never force `NEEDS_HUMAN`. Blocking gates (`KW-REVIEW-005`), `KW-REVIEW-008`, and `KW-REVIEW-009` still decide per the verdict table. | Reduces noise stops while keeping the two escalation rules (reserved path, unreviewable diff) ahead of all findings. | ANSWERED — user decision 2026-10-24 |
| Q4 | The 2026-10-14 directive said "no max concurrency". This plan (item E) explicitly reverses it with `max_concurrent_workers` default 4. Is the reversal authorized? | **Proposed: yes, reverse it** — record `max_concurrent_workers` (default 4, host-configurable) as the new constraint; update `execution-and-review.md`, `plan-authoring.md`, and `conductor.md` phrasing that currently says "no concurrency cap / harness runs every ready task". If the owner declines, item E ships isolation + flags only, with unlimited parallelism retained. | Reversal rationale: MSBuild `obj/`/`bin/` contention, `dotnet format`/`cleanupcode` shared-state corruption, and gate-evidence integrity already documented in the developer agent bodies; the 10-14 directive predates per-task `--artifacts-path` isolation and the task-gate re-run-once rule. Reversal is cheap to reverse (one config value) but changes scheduling posture, so it needs explicit re-authorization. Conductor must not infer approval from silence. | OPEN — RECOMMENDED: authorize the reversal (default 4, configurable) — awaiting owner approval at the **approve and execute** gate |
| Q5 | 2026-10-22 "always push PR regardless of risk" vs current `autonomy.md`: push draft PR on `APPROVE` or parked (`NEEDS_HUMAN` / exhausted `REQUEST_CHANGES`); never push with open `REQUEST_CHANGES`; never mark ready; never merge. Which governs? | **Proposed: follow current `autonomy.md` as-is** unless the owner clarifies otherwise. This plan documents the rule verbatim in every touched delivery paragraph and does not implement "push regardless of risk". | Tension: "always push" would push known-bad diffs (open `REQUEST_CHANGES`, failing blocking gates) into draft PRs, defeating the verdict engine's `REQUEST_CHANGES` terminal and the pre-push self-review "do not push; fix again or report" rule. Current contract already pushes parked work (with the parked list in the PR description, assigned to the invoking user) — that is the safe subset of "always push". If the owner wants unconditional push, that is a new decision with new risk and needs its own question round. | OPEN — RECOMMENDED: keep `autonomy.md` delivery rule unchanged — awaiting owner approval at the **approve and execute** gate |
| D1 | Baseline for "what remains"? | Inventory from current `main` (`2089297`), with PR #424's removals recorded as already-removed. | Prevents re-removing or re-litigating #424. Task T0 diffs #424's merge against its parent to list removed gates. | OPEN — decided by T0 evidence, confirmed at approve-and-execute |
| D2 | Where does Critical/Major-only gating apply? | Council remediation loop + pre-push self-review fix loop + autonomous park rule. Verdict rules 1–3 (always-human, size, blocking gates) unchanged; rule 4 (any Critical) unchanged; rule 5 (`major-count-blocks`) unchanged; Minor/Info findings reported but never blocking. | Q3 is about finding severity, not about gates or escalation paths. Coverage stays reported-never-decisive. | OPEN — decided by T2 evidence |
| D3 | Lockfile/scratch exclusion mechanism? | Exclude from `ChangedLines` (the `max-reviewable-lines` numerator), not from `ChangedPaths` or review readability. `ChangedPaths` still lists them so reviewers see lockfile bumps; only the line count ignores them. | Lockfiles are machine-generated (`package-lock.json`, `pnpm-lock.yaml`, `yarn.lock`, `bun.lock*`, `uv.lock`, `Cargo.lock`, `*.lock`) and scratch output (`.agent_scratchpad/`) can be thousands of lines; counting them against an attention ceiling punishesVendored churn, not human review load. Path-based exclusion keeps `KW-REVIEW-008` semantics intact. | OPEN — decided by T3 evidence |
| D4 | Spec-gate collapse shape? | When `tasks` phase returns `READY_FOR_REVIEW` with `GAPS: none` and `OPEN_QUESTIONS: none`, conductor may present **one consolidated review** covering tasks approval + approve-and-execute (max 2 gates total in the happy path: phase review + consolidated final). Any gap/question reopens separate gates. Supervised mode keeps every gate separately. | Preserves the "never execute a Draft artifact" invariant and the `SPEC_FINALIZED` validation-evidence requirement; only the presentation collapses, and only when there is nothing to present separately. | OPEN — decided by T4 evidence |
| D5 | Concurrency default and configurability? | `max_concurrent_workers` under `review:` (or conductor config — T5 decides exact key), default **4**, host-overridable, respected by the ready-queue dispatcher; plus mandatory `-nr:false -p:UseSharedCompilation=false` on .NET task-gate/build invocations and per-task `--artifacts-path .agent_scratchpad/<task-id>/artifacts`. | Default 4 bounds MSBuild/node contention while keeping parallelism; flags disable node-reuse/shared-compilation cross-task interference; per-task paths give each worker its own `obj/`/`bin/`. | OPEN — decided by T5 evidence, subject to Q4 authorization |

---

## 4. Baseline: what PR #424 already did vs what remains (to be confirmed by T0 on `main` `2089297`)

T0 must verify each line against `main`. Expected remainder (hypotheses to confirm, not claims):

1. **Kept (verify kept):** `docs init` step; `autonomous: recommendation accepted` provenance rows; `AUTONOMOUS_APPROVAL: <gate id>` re-invocation protocol; hard-block escalation shape; hard stops (merge/publish/delete/spend, BLOCKED/write errors, `RECOMMENDED: none`); delivery rule (draft PR on APPROVE or parked, never with open REQUEST_CHANGES, never merge, never mark ready); `NEEDS_HUMAN`/circuit-breaker park-and-continue; `task-gate` exit gate with `TASK_GATE:` digest; whole-change council once per run; pre-push self-review gate; `KW-REVIEW-008` always-human; `KW-REVIEW-009` size ceiling (20000); blocking-gate → `REQUEST_CHANGES`; Critical → `REQUEST_CHANGES`; `major-count-blocks: 3`; coverage reported-never-decisive; spec phase gates (`READY_FOR_REVIEW` per phase + `approve and execute`).
2. **Removed by #424 (verify removed, do not re-implement):** per-task LLM audits (replaced by `task-gate` per ADR 0005 / 2026-10-20 plan); per-task `inspectcode`; any duplicate per-phase re-approvals #424 collapsed.
3. **This plan's reductions (new):** B (auto-remediate Critical/Major before park), C (lockfile/scratch `ChangedLines` exclusion), D (tasks + execute consolidation), E (concurrency cap + flags + isolation enforcement).

If T0 finds the baseline differs, T0 amends §4 and the Decisions rows before approval — the inventory is the approval basis.

---

## 5. Item specifications

### A. Gate audit inventory

Produce `docs/reference/autonomous-gates.md` (new canonical page; exact path confirmed by T0 against the reference index) listing every gate reachable in autonomous mode: intake choice, per-plan/spec phase `READY_FOR_REVIEW`, tasks approval, `approve and execute`, mode-change re-approval, developer `task-gate`, whole-change council, pre-push self-review, `docs validate` / `docs drift`, and the verdict rules. Each row: gate id/trigger, owning specialist, autonomous behavior (auto-approve with provenance vs park vs hard-stop), keep/reduce/collapse disposition, and for keeps the irreversible-action justification. PR #424 removals listed as "removed by #424 — not reinstated".

### B. Auto-remediate before NEEDS_HUMAN (Critical/Major only, per Q3)

When the council returns actionable Critical or Major findings (fixable by deterministic means: format, analyzer/CleanupCode fix pass, lint auto-fix, unambiguous test-contract repair within the approved scope), the conductor routes them to the owning worker session for **one bounded remediation pass** (same session continuation, same file scope, same task-gate re-run) **before** parking as `NEEDS_HUMAN`. Unactionable, out-of-scope, or second-failure findings park immediately with the finding ids in the parked list. Minor/Info findings never trigger remediation loops and never park — they are reported. `KW-REVIEW-008`, size, and blocking-gate verdicts are not "findings" for this loop and bypass it (escalation/REQUEST_CHANGES as today).

### C. KW-REVIEW-009 scoping

`ChangedLines` (the `max-reviewable-lines` numerator in `findings.json` → `ReviewScope`) excludes:

- lockfiles: `*.lock`, `package-lock.json`, `pnpm-lock.yaml`, `yarn.lock`, `bun.lock*`, `uv.lock`, `Cargo.lock`;
- scratch output: `.agent_scratchpad/**` (and the configured scratchpad path where the repository declares one).

`ChangedPaths` still includes them. Size never suppresses remediation: a size escalation (`KW-REVIEW-009` → `NEEDS_HUMAN`) parks the delivery but does not cancel an in-flight B remediation pass, and the remediation result is reported alongside the park. Never subdivide a review to slip under the ceiling (existing `code-reviewer` rule stays).

### D. Spec-gate collapse

Happy path (no gaps, no open questions at `tasks` review): conductor presents **one consolidated review** — tasks contract + final approve-and-execute — recorded as two approvals in `## Decisions` (tasks `AUTONOMOUS_APPROVAL` + execute `AUTONOMOUS_APPROVAL`) from one presentation. Total gates in the happy path: at most 2 (the consolidated review + the underlying phase `READY_FOR_REVIEW` that produced it; requirements/design phases already approved upstream are not re-gated). Any `GAPS` or `OPEN_QUESTIONS` disables consolidation for that spec. Supervised mode: unchanged, every gate presented separately. Plan path unchanged (single approve-and-execute already).

### E. Concurrency cap, .NET flags, isolation, and the push rule

- `max_concurrent_workers` (default **4**, host-configurable in `.kyber-weave/kyber-weave.yml`; exact key decided by T5) caps ready-queue dispatch. Replaces "no concurrency cap / harness runs every ready task" phrasing in `execution-and-review.md`, `plan-authoring.md`, `conductor.md` (all harnesses: canonical `products/kyber-squad/` sources; generated `.opencode/` + `.claude/` mirrors updated by the same task or flagged as generated — T5 establishes which).
- Every .NET `dotnet build` / task-gate invocation in the gate path carries `-nr:false -p:UseSharedCompilation=false` (no node reuse, no shared compilation across workers).
- Every task-gate/build uses a per-task artifacts path (`.agent_scratchpad/<task-id>/artifacts`, `--artifacts-path` / `-p:BaseOutputPath=` / `-p:BaseIntermediateOutputPath=`), cited in the digest; a gate run against shared output is not evidence.
- **Push rule (explicit, per Q5):** autonomous pushes a **draft** PR on `APPROVE` or parked (`NEEDS_HUMAN` or exhausted `REQUEST_CHANGES` loop), assigned to the invoking user with low-confidence decisions, auto-approved gate count, and parked list in the description; **never** with open `REQUEST_CHANGES` (leave unpushed, report); **never** mark ready; **never** merge. This matches current `autonomy.md` and is documented verbatim, not extended to "always push regardless of risk".

---

## 6. Non-negotiable callouts (explicit per packet)

- **Must not touch:** merge / publish / delete / spend; unresolvable `BLOCKED` / write errors (`PLAN_WRITE_ERROR`, `SPEC_WRITE_ERROR`); `RECOMMENDED: none` hard blocks; `always-human` paths (`capabilities.yml`, `agents/**`, `.kyber-weave/kyber-weave.yml`, `*secret*`, `*credential*` sensitive paths).
- **`KW-REVIEW-008` always-human remains.** No task narrows it.
- **2026-10-14 "no max concurrency" is explicitly reversed** by item E — re-authorization required (Q4). Without Q4 approval, E ships isolation + flags only.
- **2026-10-22 "always push PR regardless of risk" tension:** current rule is push draft on `APPROVE` or parked only, never with open `REQUEST_CHANGES`, never merge. Proposal: follow current `autonomy.md` as-is unless the owner clarifies (Q5).
- **Keep `docs init` step** (Q2). **Gate only on Critical and Major** (Q3).

---

## 7. Tasks (test-first; dispatchable with file scope)

| Task | Objective + files | Test contract (RED → GREEN) | Acceptance criteria |
|---|---|---|---|
| T0 | **Gate audit inventory.** Read `main` `2089297`: `products/kyber-squad/agents/conductor/references/autonomy.md`, `execution-and-review.md`, `plan-path.md`, `spec-path.md`, `products/kyber-squad/skills/product-owner/SKILL.md` + `references/*.md`, `products/kyber-squad/skills/code-review/SKILL.md`, `products/kyber-squad/agents/architect/references/plan-authoring.md`, `autonomy-decisions.md`, `.kyber-weave/kyber-weave.yml` (`review.*`, `task-gates`), `src/KyberWeave.Core/Review/*`, `src/KyberWeave.Cli/Commands/Review/*`. Diff #424's merge vs parent for the removed set. Author `docs/reference/autonomous-gates.md` draft table. | RED: new test class `SquadAutonomousGatesInventoryTests` asserting the inventory page exists and lists each expected gate disposition — fails (page absent). GREEN: page present, every row resolves to a live file/section (no dangling refs). | Inventory lists every gate on `2089297`, marks keeps with irreversible-action justification, marks #424 removals, and matches the Decisions D1 baseline. `docs validate` + `docs drift` pass for the new page. |
| T1 | **B — auto-remediate loop (conductor + worker contracts).** Edit `products/kyber-squad/agents/conductor/references/execution-and-review.md` (findings/council section: bounded Critical/Major remediation before park), `products/kyber-squad/agents/code-reviewer.md` (actionable Critical/Major → one remediation pass), `products/kyber-squad/skills/code-review/SKILL.md` (adjudication + verdict procedure note; verdict table itself unchanged), and the mirrored `.opencode/` + `.claude/` copies of each touched file. Add remediation-pass cap (1), session-continuation rule, park-on-second-failure rule, Minor/Info never-loop rule. | RED: `SquadAutonomyRemediationTests` — council returns one actionable Major → conductor packet shows remediation rework before any park flag; Minor-only council → no rework, no park. Fails today (immediate park). GREEN: both assertions pass. | Actionable Critical/Major gets exactly one bounded same-session remediation + task-gate re-run before park; unactionable/second-failure parks immediately; Minor/Info never loops or parks; `KW-REVIEW-008`/size/blocking-gate paths bypass the loop. |
| T2 | **B + Q3 — Critical/Major-only gating (verdict + pre-push).** Confirm `VerdictEngine.EvaluateFindings` already gates only Critical + Major-threshold (no code change if so — record as no-op with evidence). Edit pre-push self-review gate in `products/kyber-squad/skills/code-review/SKILL.md` (§Pre-push: "Fix every major-or-worse finding") to state explicitly Minor/Info never block push; mirror to `.opencode/` + `.claude/`. | RED: `ReviewVerdictSeverityGateTests` (or extend `ReviewVerdictTests`): Minor/Info-only findings + green gates → `Approve`; asserted against docs prose that says so. Fails if prose missing. GREEN: verdict + prose agree. | Verdict rules 4–5 unchanged in code; docs state Minor/Info reported-never-blocking in council, pre-push, and autonomous park rules. No severity threshold value changed (`major-count-blocks: 3`, `min-confidence: 7` untouched). |
| T3 | **C — KW-REVIEW-009 scoping.** Implement `ChangedLines` exclusion: decide layer (T3a: findings-producer guidance in `code-review/SKILL.md` scope step + `code-reviewer.md` + `docs/code-review/architecture.md` + `docs/cli-command-surface.md`) and, if the CLI computes any line count for findings, the corresponding `src/KyberWeave.Core/Review/*` + `src/KyberWeave.Cli/Commands/Review/*` change with `ReviewJson`/`ReviewScope` preserved. Exclusion set: `*.lock`, `package-lock.json`, `pnpm-lock.yaml`, `yarn.lock`, `bun.lock*`, `uv.lock`, `Cargo.lock`, `.agent_scratchpad/**`. | RED: `ReviewScopeExclusionTests` — synthetic diff with 18k lockfile lines + 200 source lines → scoped `ChangedLines` ≈ 200, `ChangedPaths` still lists lockfiles; 21k source lines → still `NEEDS_HUMAN` (`KW-REVIEW-009`). Fails today (full count). GREEN: passes. | Lockfile/scratch lines excluded from the ceiling numerator; source-only overflow still escalates; remediation (T1) still runs under size escalation; "never subdivide to slip under" rule retained and cited. |
| T4 | **D — spec-gate collapse.** Edit `products/kyber-squad/agents/conductor/references/spec-path.md` (consolidated tasks + execute presentation, happy-path max 2, gap/question disables, supervised unchanged) + `products/kyber-squad/skills/product-owner/SKILL.md` + `references/tasks-phase.md` (or `spec-authoring.md`, whichever owns the tasks-review contract) to permit the combined approval recording (two `AUTONOMOUS_APPROVAL` rows, one presentation). Mirror to `.opencode/` + `.claude/`. | RED: `SquadSpecGateCollapseTests` — tasks `READY_FOR_REVIEW` with `GAPS: none`, `OPEN_QUESTIONS: none` → conductor may present consolidated review; with any gap/question → separate gates required; supervised → separate gates always. Fails today (always separate). GREEN: passes. | Happy path ≤ 2 gates; gaps/questions force separate gates; supervised untouched; `SPEC_FINALIZED` still requires validation evidence; plan path untouched. |
| T5 | **E — concurrency cap + flags + isolation.** Add `max_concurrent_workers` (default 4) to config (`review:` or conductor section — key decided in-task, documented in `docs/configuration.md`), enforce in ready-queue dispatcher docs (`execution-and-review.md`, `plan-authoring.md` "no concurrency cap" lines, `conductor.md`), add `-nr:false -p:UseSharedCompilation=false` to .NET task-gate/build argv in `.kyber-weave/kyber-weave.yml` + `products/kyber-squad/skills/task-gate/SKILL.md` + developer agent bodies, enforce per-task `--artifacts-path`. Record Q4 reversal provenance in the Decisions rows. | RED: config test (`ReviewConfigTests`-adjacent): absent key → 4; set key → honored; invalid (0/negative) → config error. Plus gate-selection test: build argv contains both flags. Fails today (no key, no flags). GREEN: passes. | Default 4, host-overridable, invalid rejected; every .NET gate invocation carries both flags; per-task paths cited in digests; "no concurrency cap" phrasing gone or explicitly superseded with Q4 provenance. **If Q4 declined:** cap lines dropped, flags + isolation ship alone. |
| T6 | **E (push rule) — document, don't change.** Verify `autonomy.md` `## Delivery` + `## NEEDS_HUMAN` + bug-crusher relay paragraphs already state the Q5 rule; add the verbatim rule to the T0 inventory page and to `docs/kyber-squad/architecture.md` conductor-delivery section only if that section misstates it. No behavior change. | RED: `SquadAutonomyContractTests`-adjacent assertion: open `REQUEST_CHANGES` → unpushed; parked → draft PR, never ready, never merge. Confirm existing tests already pin this (they do: `SquadAutonomyContractTests` lines ~162–203); if any doc line says "always push regardless", that line is the RED. GREEN: docs + tests agree on the Q5 rule. | Docs state: draft PR on APPROVE or parked only; never with open REQUEST_CHANGES; never mark ready; never merge; parked list + low-confidence + gate count in PR description, assigned to invoking user. No "push regardless of risk" implementation. |
| T7 | **Docs init kept (Q2 guard).** Pin test that `docs init` + Config Reg + `docs validate`/`docs drift` remain in every touched onboarding/governance page (`docs/docgraph/onboarding.md`, `docs/configuration.md`, plan-authoring/spec-authoring references). No behavior change; guard against accidental removal by A–E edits. | RED: `DocsInitKeptTests` (or extend `SquadCanonicalContentTests`): each touched doc still references `docs init` where it did. Fails if a T1–T6 edit drops it. GREEN: passes. | `docs init` step present everywhere it was; no task removes it. |
| T8 | **Whole-change review + closeout (`docs-dev`).** Council over the accumulated diff (procedure steps 1–5, fix Critical/Major, re-run, second verdict governs); `docs validate .`, `docs drift .`, full `dotnet` gate suite per `AGENTS.md` (no `--full` shortcuts as evidence); migrate durable facts (inventory page, remediation rule, `ChangedLines` scope, spec consolidation, concurrency key, push-rule restatement) into canonical docs; sync plan index; archive per lifecycle (`KW-DOC-LIFECYCLE-003` fails on open plans under `--merge-ready`, so archival rides the delivery PR). | N/A (verification + closeout task). | Council `APPROVE` (or parked `NEEDS_HUMAN` with parked list — never open `REQUEST_CHANGES`); all gates green; `docs validate` + `docs drift` zero findings; closeout mapping complete; delivery as draft PR per §5E, never merge. |

### Dependency graph

`T0 → (T1, T2, T3, T4, T5, T6, T7) → T8`. T1–T7 are file-scope disjoint except shared skill mirrors (coordinate via ready-queue scope rule); T2's prose depends on T1's loop definition — run T1 before T2 where they touch the same file. T8 after all GREEN + second-verdict council.

---

## 8. Verification gates

- Per-task: the worker's own `task-gate` (`TASK_GATE:` digest line; `FAIL`/missing → back to same worker; 2-rework cap → circuit-breaker).
- Per-run: full declared suite on the delivery branch — `dotnet restore`, `dotnet format … --verify-no-changes`, `dotnet build -c Release`, `dotnet test`, `skill validate/lint/scan`, `docs validate .`, `docs drift .`, `review gates .`, plus `scripts/update-loop.sh` if tray/install paths touched (not expected).
- Review: whole-change `code-reviewer` council once; Critical/Major fixed and re-gated; verdict from `review verdict` (not from memory).
- Plan gates: `docs validate` + `docs drift` after every docs edit; plan index in sync; archival only after T8 closeout.

---

## 9. Risks

- **Baseline drift:** `main` moved past `2089297` — T0 re-pins to the merge-base at execution and records the SHA.
- **Mirror skew:** canonical `products/kyber-squad/` vs generated `.opencode/` + `.claude/` copies diverge — every doc edit task updates all copies or records why a copy is generated.
- **Q4 declined:** E's cap is the only item needing re-authorization; isolation + flags still ship (pre-agreed fallback).
- **Q5 clarified toward unconditional push:** out of scope for this plan; needs a new question round and risk note, not a mid-execution reinterpretation.

---

## 10. Closeout (to be completed by `docs-dev` at execution end)

Harvest durable decisions (Q1–Q5, D1–D5) into `docs/reference/autonomous-gates.md`, `docs/code-review/architecture.md`, `docs/kyber-squad/architecture.md`, `docs/configuration.md`, and ADRs only if a costly-to-reverse choice emerged (else `no ADR — ADR waiver: refines existing autonomy/review/concurrency patterns with no new boundary, dependency, or schema`). Documentation waiver is not available for this plan (new invariants ship in canonical docs). Sync `docs/plans/README.md`; archive to `docs/archive/plans/`.

---

## 11. Questions for the owner (answer at approve-and-execute)

- **Q4:** Authorize reversing the 2026-10-14 "no max concurrency" directive with `max_concurrent_workers` default 4 (configurable)? If no, E ships isolation + flags only.
- **Q5:** Confirm the push rule stays as current `autonomy.md` (draft PR on APPROVE or parked only; never with open REQUEST_CHANGES; never ready; never merge), and that 2026-10-22 "always push regardless of risk" is **not** adopted?
