---
id: plans/2026-11-07-conductor-run-speed-shrink-reads
title: Shrink what workers read (conductor run-speed, slice 8)
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
created: 2026-11-07
last-reviewed: 2026-11-08
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - KyberSquad
  - KyberArbiter
  - ReviewCouncil
---

# Shrink what workers read (conductor run-speed, slice 8)

**Status: Ready.** This plan is **Ready and approved for execution under AP1**, with resolved autonomy `autonomous`. It is carved from the parent design for the conductor run-speed work, which lives on its own branch (`claude/docs/conductor-bug-crusher-run-speed`) and is deliberately not in this tree. Per the parent's D12, each slice gets one child plan on the slice's branch, carved from the parent's section and complete on its own: this plan restates only slice 8's decision rows, design, harvest rows, ADR disposition and tasks. This is the last slice, so this slice's pull request also archives the parent design with a mapping to every child; this plan itself is archived in this slice's own pull request. Every decision is answered below with its provenance; the parent's AP1 approval covers the Test contract this plan restates and carries to this child through `AUTONOMOUS_APPROVAL` per D12. The pair of documentation gates ran in this worktree — `docs validate .` and `docs drift .`, not `--merge-ready` while the plan is open — each at zero findings. The kw-docs server is bound to the canonical checkout and cannot see a file written only here, so a passing answer from it would describe the wrong tree.

## Problem and goal

The parent design's measured run put the cost of worker text in plain numbers: the docs worker sent 13.1M tokens at about 158K per round trip — nearly all re-sent input — while making 4 successful docs tool calls, 12 direct source reads, and reading the 37 KB plan; `docs/kyber-squad/architecture.md` is 130 KB and `docs/cli-command-surface.md` is 48 KB. Agent-file text is re-sent on every round trip of every worker, while a skill loaded at the last step is re-sent only for the last few. Beside that, today's task gates give no executed test evidence per task: `review.task-gates` in `.kyber-weave/kyber-weave.yml` runs `csharp-dev` the two format checks and a Release build — no tests — because the one test project's full suite with coverage takes about 25 minutes, which is the open [test-project-mapping todo](../todo/task-gate-test-project-mapping.md) this slice completes. And the capability matrix still carries rows this program depends on but never confirmed on all 13 harnesses.

The goal of this slice (the parent's fix 7 and the harness check): cut worker text **only where a check already enforces it, and add a check where one is missing** — three new checks (a suppression gate, a verified gate report, targeted tests in the task gate), one injected always-on source for the Discovery order, the full-suite ban and batched reads, and the removal of duplicated rule text gated behind the rule map, the moved contract pins and a seeded-defect trial, so nothing is deleted until the proof passes. The estimated effect (the parent's, not re-measured): `test-dev` on C# from 72 KB of start-of-task text today to about 32 KB, about 9 KB loaded at the end; `csharp-dev` 46 KB to about 17, about 9 at the end; the first RED task's start text from about 1.5M to about 0.7M tokens, 6 to 7% of that task's 12.5M. This slice also confirms the harness behaviours the program relies on (T48) and, as the last slice, archives the parent design with its mapping to the eight children.

## Decisions

Rows D12, D13, D27, D28, D29, D30, D35, D36, D37, D41, D43 and AP1 are this slice's rows from the parent design's decision ledger, copied verbatim per the parent's own D12 carve rule. In D12 and AP1, "this file" and "this plan" name the parent design file on its branch, not this child plan. D59 to D62 record the scope and delivery decisions this carve adds, verified against this tree. Decision ids are plan-local (the slice 6 carve reused its own D18 the same way): slice 7's sibling carve on its own branch may number its local rows identically, and no conflict arises because each ledger belongs to its own plan file.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D12 | Can one plan span several pull requests? | **No. Several plans, one per slice.** This file stays the single approved design and never merges while open. On approval it stays on its own branch. Each slice gets a child plan on the slice's branch, carved from its section here, and that plan is archived in the slice's pull request. The last slice's pull request brings this file in and archives it with a mapping to the children. | `OpenWorkValidator` raises `KW-DOC-LIFECYCLE-003`, and `ci.yml` runs `docs validate --merge-ready` on every pull request. A plan left open in `docs/plans/` fails CI, and the rule exists because a specification merged in stages never got its closeout. Its message says "keep it on its branch". Changing the rule was rejected for that reason. Child plans restate only their slice's decision rows, so each is complete on its own. Approval here carries to each child through `AUTONOMOUS_APPROVAL`. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |
| D13 | Does the requested build order hold against real dependencies? | **Five changes.** (a) Fix 5's one-line test repair moves to the very front, because a failed blocking gate forces `REQUEST_CHANGES`, so no slice's council can approve on the owner's machine until it lands. (b) The `stop-on-tool-failure` setting moves to the fix 8 slice, because fix 8's fetch failure reads it. (c) The `branch=` and `repo=` provenance fields move from fix 4 to fix 1, because the worktree rule needs them. (d) Fix 5's remaining work goes after fix 2, because it edits the shared execution skill fix 2 creates. (e) The `github-actions` standard is declared in the first slice, because `github-devops` refuses to write a workflow without it. Fixes 6 and 10 land with fix 3: fix 6 adds a field to the task file, and fix 10 shares fix 3's detector. Otherwise 8, then 1 to 5, then 11, then 7 stands. | Fix 7 depends on fix 11 (the `Completion` section) and on fix 3 (commands in the prompt). Fix 11 depends on fix 4's topic repair. | autonomous: recommendation accepted |
| D27 | Where does the full-suite ban live? | One always-on line in each worker's instructions, from the single injected source. Not only in `task-gate`. | The source proposed `task-gate`, but that skill loads at the last step, and a raw `dotnet test` bypasses `proc-guard.sh` until the [shell hook todo](../todo/per-harness-shell-hook-for-full-run-guard.md) lands. A ban read after the mistake is no ban. The count is 27 files, not 14, so the rule map is rebuilt by search. | autonomous: recommendation accepted |
| D28 | What does the suppression gate allow? | `kyber-weave review suppressions` scans added lines only, for `#pragma warning disable`, `SuppressMessage` and `NoWarn`, and fails unless the same or previous added line carries `reason:`. | 21 files already hold `#pragma warning disable`, hence added lines only. `AGENTS.md` asks for "a reason you can state". | autonomous: low-confidence, no authoritative source |
| D29 | How is `TASK_GATE: PASS` verified? | New Arbiter rule `KW-ARB-GATE-REPORT-001` on `delegate.returned` reads the `report=` file and escalates when it is absent or disagrees with the line. Worktree workers report an absolute path. | `GateReportFacts` serves the council and nothing reads `report=` at task end. Where the harness cannot observe returns the rule cannot fire, and the end-of-run gates still catch it. | autonomous: low-confidence, no authoritative source |
| D30 | How does a task gate pick tests? | A `{tests}` token beside `{changed}`, resolved next to `TaskGateSelection`. Mapping: a declared `review.test-map` first, then the name convention `<Stem>Tests`, keeping only classes that exist. Nothing mapped means skipped with `KW-REVIEW-026`, never a pass. | `codegraph affected` is too unreliable on C# to select tests (recorded on issue 535). `-class` needs a fully qualified name, because a short name runs zero tests and exits 0. | autonomous: low-confidence, no authoritative source |
| D35 | Which decisions need an ADR? | See this plan's ADR disposition below, and the parent's Design harvest. New ADRs cover the stop rule (the reversal of "never block a run"), the server-side refresh, the tracked task file, project knowledge and onboarding, and run start with the harness name. ADR 0050, ADR 0059 and ADR 0053 are amended in place. Fixes 2, 6 and 7 carry a waiver or a section of another ADR. | Numbers are assigned at each closeout, from 0060. | autonomous: recommendation accepted |
| D36 | Where does the rule map live? | `docs/kyber-squad/worker-rule-map.md`, a permanent reference. | It says what enforces each rule, which stays useful and stops regrowth. A file in a plan folder would be archived away. | autonomous: recommendation accepted |
| D37 | How is the seeded-defect trial run? | In a scratch clone, one Squad deployment per arm, same harness and model on both arms, one fixed packet per worker role. The harness and model are recorded, and the owner's model rule still applies if Pi or OpenCode is used. Only the text differs. | A running session cannot swap agent definitions, so each arm is a fresh headless session in its own deployment. | autonomous: low-confidence, no authoritative source |
| D41 | How does a skill know its harness? | A `{{harness}}` placeholder in canonical conductor, bug-crusher and `create-pull-request` text, replaced once in the shared render pipeline with `SquadTargetCatalog.GetToken`. | The 13 `HarnessSpec` roots are distinct. A folder name fails (this Command Code run was in the `Devin` checkout), and so does an environment variable (`OPENCODE_CONFIG` was set in a Claude session). Limit: Antigravity's `.agents/skills` is also read by Pi, ZCode and Command Code, so T48 checks a co-deployment. | autonomous: low-confidence, no authoritative source |
| D43 | Is `cleanupcode` removed from the task-end pass? | Off by default. The skill stays for InspectCode review work. | `.editorconfig` already makes `var`, predefined types and unused usings warnings, the style gate checks them, and the whitespace gate checks formatting. Redundant qualifiers surface only in end-of-run InspectCode, as a rework item, and the rule map says so. It rewrote existing code in the first RED task, and the worker reverted it. The agent text also tells workers to run `dotnet format analyzers`, which the C# standard says task gates omit. | autonomous: recommendation accepted |
| AP1 | Is the Test contract approved, and may this plan execute? | Approved as written, as recommended. Execution follows the parent design's D12. The owner confirmed on 2026-11-07 that all eight slices run in one continuous run. | Not a planning decision. Resolved by the conductor carrying `AUTONOMOUS_APPROVAL: AP1` under resolved autonomy `autonomous`, and then confirmed by the owner. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |
| D59 | Why is this slice branched from slice 7's unmerged branch, and how is it delivered? | Because slice 8 edits files slices 4 and 5 rewrote: the `delivery-execution` skill (slice 4's T22 created it), and the worker text and authoring surfaces slice 5's T29 edits (`plan-authoring.md`, `docs-dev.md`, the conductor references). Branching from `main` would conflict on the first push, and GitHub runs no `pull_request` workflow on a conflicted pull request, so CI would never report. This branch is cut from slice 7's unmerged branch at `1da91498`, its tip. The slice's pull request targets slice 5's branch and is retargeted to `main` as the bases merge. Do not merge the base into this branch and do not rebase onto `main`. | Measured twice on slice 2 and recorded by slice 4's D51: a conflicted pull request produced no run at all; the only way to a verdict was merging the base first. The parent's order lands slice 7 before slice 8, and slice 7 shares file scope with this slice (`task-gate`, `delivery-execution`, `bug-crusher` and agent text), so the two siblings' pull requests merge one at a time, slice 7 first; every file named here is checked by path plus symbol at execution (the parent's own interleave rule). | autonomous: recommendation accepted |
| D60 | Which migration reports does T52's edit invalidate? | One per agent body T52 edits. Each of the 19 `products/kyber-squad/migration/` reports carries its agent's `final-body-sha256`, and the canonical suite asserts the report-name set and compares each recorded digest with the one the Squad loader computes, so an edited agent body without an updated report fails the suite. Every report whose agent body the rule map names joins T52's change in the same commit. Skills carry no migration digest — slice 4's D52 and slice 5's D58 recorded the same trap on this chain, and it is verified against this tree: the migration directory holds a report for every agent body T52 can touch. | The parent's T52 file list predates the trap's discovery (slice 4's D52) and names no migration reports; the widening follows the recorded pattern rather than the parent's list. The exact agent set is whatever the rule map (T49) names — the map is rebuilt by search (D27), so the report set is derived from the map, not guessed here. | autonomous: recommendation accepted |
| D61 | How does the parent design file reach this branch, never having merged? | The closeout task (T53) copies it out of the design branch without merging anything: `git show claude/docs/conductor-bug-crusher-run-speed:docs/plans/2026-11-07-conductor-bug-crusher-run-speed.md > docs/archive/plans/2026-11-07-conductor-bug-crusher-run-speed.md` (a `git fetch` of that ref first only if it is not already local), then `git add` the copy. The copy is then edited in place like every archived plan in this tree: frontmatter `status: archived`, body Status line to "Archived — complete <closeout date>", a `## Closeout` and `## Closeout mapping` appended with the mapping from each decision to its child and canonical destination, and every relative link rewritten for the archive's extra depth (including links to files this same closeout moves, such as the archived todo). The row lands in the `## Archived Plans` table of `docs/plans/README.md`. The design branch is never merged into this or any delivery branch. | Verified against this tree: `docs/archive/plans/` holds no separate index — the archive index is the `## Archived Plans` section of `docs/plans/README.md` (columns Plan / Status / Date / Archive Date / Canonical Docs / Harvested ADRs / Goal), and slice 3's archived plan shows the archived conventions (`status: archived`, "Status: Archived." body line, Closeout and Closeout mapping sections). A link from the archived parent to a file this branch's base does not carry (slice 7's plan, the archives of slices 4 and 5) would fail validation, so the mapping names children by file name and delivery branch as text, and links only what exists in this tree at closeout. | autonomous: recommendation accepted |
| D62 | T50's classes fail between T50 and T52. Is that a broken task? | No — it is the contract. T50 moves the pins to the text's new home before the text moves, so its four classes fail against today's text by design and pass only when T52 applies the removals. This is not a RED proof and not rework: the order T49, T50, T51, T52 is strict, and any worker running the four classes out of order sees the expected red. T52's acceptance is their GREEN. | The parent's own rows state it ("Fails against current text", "Passes after T52", "Fails until the text moves"); this row exists so the conductor's rework tally does not treat the intermediate state as a failure to fix, and so no worker "repairs" the pins by weakening them — no pinned string is weakened, which is T50's own acceptance. | autonomous: recommendation accepted |

## Investigation findings

Verified against this tree — the worktree at `/tmp/kw-slice8`, branch `factory/feat/conductor-run-speed-shrink-reads`, cut from slice 7's unmerged branch at its tip `1da91498` — not assumed from the packet. **Discovery: codegraph 1, docs 1, direct reads 16**, plus targeted greps and path-existence globs. The worktree carries no `.codegraph/` index of its own and this harness gives the architect no shell, so `doctor --ensure` cannot run here; the codegraph call reported the worktree unindexed and the kw-docs server is bound to the canonical checkout (provenance `root=/Users/dave/git/personal/factory/kyber-weave`, `rev=d9c489c1`, `documents=192`) — the revision this branch's chain descends from, and it can never validate a file written only in this worktree. Per the parent design's worktree rule, a shell-less role reads the worktree's absolute path directly; the direct reads are enumerations and line-citation checks an index query does not return — file existence, rule counts and header lines, index and archive row formats, anchors, and configuration state.

**The task-gate surface, today.** `review.task-gates` in `.kyber-weave/kyber-weave.yml` runs `csharp-dev` three gates — `format-whitespace`, `format-style`, `dotnet-build` — written once as YAML anchors and aliased across the six .NET agents and `test-dev`: no test gate, and no `review.test-map` key exists anywhere in the file, which is D30's premise and the open todo's. `TaskGateSelection.cs` sits at `src/KyberWeave.Core/Review/`, `GateReportFacts.cs` at `src/KyberWeave.Core/Arbiter/Facts/`, `GateCatalogue.cs` at `src/KyberWeave.Core/Review/Catalogue/`, and `ReviewConfig.cs` / `ReviewConfigLoader.cs` in `src/KyberWeave.Core/Configuration/`. `ReviewSuppressionsCommand` and `SuppressionScan` exist nowhere under `src/` (searched) — both are new in T44.

**The injection pattern T47 follows.** `SquadResponsePreferencesInjector` exists at `src/KyberWeave.Core.Squad/Deployment/SquadResponsePreferencesInjector.cs` with an `Inject(SquadRenderResult, string)` seam and carrier validation; `SquadHarnessRenderer.cs` sits beside the render pipeline. No `products/kyber-squad/shared/` directory exists — T47 creates it with `worker-baseline.md`.

**The Arbiter rule inventory.** `default-rules.yml:1` reads "The 18 shipped Arbiter rules. Ids are permanent"; `KW-ARB-GATE-REPORT-001` exists nowhere under `src/` (searched), so it is this slice's id. Slice 5's open plan adds `KW-ARB-PLANREAD-001` as the 19th rule ahead of this one (its T27), so at T45's execution the header says 19 and T45 writes 20; in this tree it says 18.

**The worker-text surface.** No `docs/kyber-squad/worker-rule-map.md`, no `products/kyber-squad/skills/task-gate/references/` — both created here. The `delivery-execution` skill slice 4 created exists at `products/kyber-squad/skills/delivery-execution/SKILL.md`; `products/kyber-squad/agents/conductor/references/execution-and-review.md` no longer exists in this tree (slice 4's T22 removed it) and is not named or recreated. The migration directory holds 19 reports, one for every agent body T52 can touch (D60).

**The test surface.** Of T43's five files, `TaskGateSelectionTests` and `ReviewGatesAgentCommandTests` exist and `ReviewSuppressionsCommandTests`, `Arbiter/ArbiterGateReportRuleTests`, `SquadWorkerBaselineInjectionTests` are new; of T50's four, `SquadPerformanceContractTests`, `SquadCanonicalContentTests` and `ArbiterTestDevContractTests` exist and `WorkerRuleSingleHomeTests` is new (all searched; `tests/KyberWeave.Tests/Arbiter/` holds no gate-report class today).

**The docs surface.** `docs/kyber-squad/requirements.md` carries "## Target Capability and Degradation Matrix" — T48's matrix. `docs/standards/test/README.md` still heads its runner section "## Commands"; slice 7's `## Completion` rename is not in this branch's base, so the anchor is verified by path at execution. The todo `task-gate-test-project-mapping.md` is open with its row in `docs/todo/README.md`; no `watch-worker-text-trim-in-real-runs.md` exists — T53 creates it, and completed todos are archived by moving the file to `docs/archive/todo/`. `docs/adr/` holds 0001 to 0062: slices 1 and 2 took 0060 to 0062 and none since; slice 5's open plan claims 0063 at its closeout and this slice's waiver consumes no number.

**The naming job gates this branch.** `pr-naming` is declared at `ci.yml:115`, needed by the pull request summary (`ci.yml:1188`), with `pr-naming-recheck.yml` beside it. The delivery branch `factory/feat/conductor-run-speed-shrink-reads` already follows `<harness>/<type>/<slug>`; the pull request title follows `<type>(<scope>): <summary> [factory]` — `feat(squad): shrink what workers read [factory]` — and targets slice 5's branch, retargeted to `main` as the bases merge (D59).

## Design

This slice's design, in the parent's voice. Fix 7 verbatim:

> ### Fix 7: shrink what workers read without losing a quality check
>
> **Principle.** Quality comes from checks that run, not from text a worker reads. Cut text only where a check already enforces it, and add a check where one is missing. Agent-file text is re-sent on every round trip. A skill loaded at the last step is re-sent only for the last few.
>
> **These checks stay exactly as they are.** Every task: whitespace format, style format, and a Release build with analyzers as errors; the conductor rejects a report with no `TASK_GATE:` line. End of run: InspectCode, the full test suite, duplicate detection and the review lenses. Pull request: CI.
>
> **The rule map (starting rows; T49 adds a row for every sentence removed).** A rule with no home stays.
>
> | Rule | Today | New home | What enforces it |
> |---|---|---|---|
> | Run the gate and report it | Long text in 6 agents | One line in each agent, details in `task-gate` | Conductor rejects a missing line, and `KW-ARB-GATE-REPORT-001` |
> | Format, style, analyzers | Long text in agents | Commands in `review.task-gates`, process in `task-gate` | The gates |
> | Report formats | Agent files | `task-gate`, loaded when finishing | Conductor, and the Arbiter for `RED_EVIDENCE` |
> | Full test suite ban | 27 files | One always-on line from the injected source (D27) | `KW-REVIEW-033` for gate runs. `proc-guard` exit 89 only when used. A raw `dotnet test` gets through until the shell hook todo lands, which is why the line stays |
> | Build isolation paths | Agent files and conductor | `Completion` section | Gate builds already set their own paths |
> | Test commands | Agent files | The task prompt | Exact commands in the prompt, and the `{tests}` gate |
> | Failure-loop rules | 1.4 KB in agents | `task-gate/references/rework.md`, loaded after a failed fix | Conductor's rework tally |
> | `cleanupcode` pass | Required, plus a 13.8 KB skill | Off by default (D43) | Style and whitespace gates. Redundant qualifiers: end-of-run InspectCode |
> | Baseline diagnostics | Required | Only where warnings do not fail the build | Warnings as errors means none to baseline |
> | Scope, no weakened tests, no masked output | Hard rules | Stay in the agent file, short | Task prompt and review |
> | Judgement rules such as test design | Standards | The standards tool | Review lenses |
>
> **New checks, so quality goes up.** Suppression gate (D28). Verify the gate report (D29). Targeted tests in the task gate (D30), which closes the test mapping todo.
>
> **Other changes.** Tell workers to send independent reads in one round trip. Keep one source for the Discovery order (938 bytes across agents), the ban and the batching line, injected at render the way `<response-preferences>` works; this keeps one copy and does not reduce what a worker reads. Remove the 2.7 KB of `conductor.md` that is word for word in the execution contract.
>
> **Estimated size.** Estimates, assuming skills and standards are read at the start, about 4 bytes per token, and `task-gate` in context for the last 10 round trips. `test-dev` on C#: 72 KB at start today, about 32 KB after, about 9 KB loaded at the end. `csharp-dev`: 46 KB, about 17 KB, about 9 KB. For the first RED task this text goes from about 1.5M tokens to about 0.7M, which is 6 to 7% of that task's 12.5M. Code reads stay the largest share, so fixes 1 and 3 matter more. `python-dev`, `go-dev` and `typescript-dev` (5.4 to 6.7 KB) already work this way.
>
> **Proof that nothing was lost. Nothing is deleted until this passes.**
>
> 1. **Rule map** (T49): every sentence removed gets a row.
> 2. **Seeded-defect trial** (T51), D37. Plant: a compiler warning, a nullable error, `var`, an unused using, bad formatting, an analyzer violation, a suppression, a test that only checks nothing throws, and a test reading the real home folder. Run workers on old and new text. The new text must catch each at the same stage, or the row must say which check now catches it later.
> 3. **Move the contract tests** (T50). Pins on exact strings move to the skill, and `WorkerRuleSingleHomeTests` fails when a moved rule appears in more than one file. That stops regrowth without a size limit.
> 4. **Watch real runs** (T53). Compare review findings over the next few runs. If InspectCode rework rises, add a scoped step back.

And the harness check, verbatim:

> ### Other harnesses
>
> Behaviour this plan cannot confirm on all 13. T48 checks each and records the result in [the capability matrix](../kyber-squad/requirements.md#target-capability-and-degradation-matrix). Anything not reached stays marked `[NOT LIVE-VERIFIED]`, with its fallback.
>
> | Behaviour | Depends on it | Known | Fallback |
> |---|---|---|---|
> | Subagent can call `codegraph_explore` | Fix 1 | Named by Claude, Factory, ZCode, Devin, Antigravity and Copilot. Added here for Command Code, OpenCode, Kilo. Pi, Codex, Cursor, Warp unconfirmed | `codegraph explore` from the shell |
> | Worker's shell can run `doctor --ensure` | Fixes 1, 8 | The `worker` profile allows it | `STATUS: TOOL_FAILURE` |
> | Conductor holds a shell at run start | Fix 8 | A skill in the main session does. An agent under the `orchestrator` profile does not. Copilot does not | Dispatch `github-devops` |
> | Per-harness token reaches the rendered text | Fix 8, D9 | 13 distinct roots. `.agents/skills` is also read by Pi, ZCode and Command Code | `--harness` is required and validated, so a wrong token is at worst a wrong label |
> | Session continuation | Fix 6 | Discovered from exposed tools. Command Code unverified | A fresh worker with the recovery brief |
> | Arbiter sees dispatch | Fix 10, D29 | Unconfirmed on OpenCode, Kilo, VS Code Copilot. `dispatch-unconfirmed` on Command Code | MCP fallback, and `KW-DOC-TASKS-005` at validation |
> | `edited` title recheck | D14 | Not run yet | The next push re-checks |
> | MCP tool-call timeout above the refresh bound | D23 | Not known per harness | Lower the bound |
>
> ### Rules that stay
>
> - Do not check out `main` first. A checkout would carry another session's edits or refuse to run.
> - Do not use `git stash` for RED proofs. That rule stands.
> - `review-lens` and `review-triage` stay exempt from discovery reporting, because their output schemas are fixed.

**The three new checks, and where they land.** The suppression gate (D28) is a new `review suppressions` command — `ReviewSuppressionsCommand` beside `ReviewGatesCommand`, with `SuppressionScan` in `KyberWeave.Core/Review`, wired into `Program.cs` — declared in `review.task-gates` following the file's anchor-alias pattern so every .NET agent's task gate runs it, and offered by the csharp catalogue family (`GateCatalogue.cs`). The gate-report rule (D29) is `KW-ARB-GATE-REPORT-001` on `delegate.returned` in `default-rules.yml`, with `ArbiterTriggerCatalog.cs` and `GateReportFacts.cs` — worktree workers report an absolute path, and where a harness cannot observe returns the rule cannot fire and the end-of-run gates still catch it. The `{tests}` token (D30) is resolved next to `TaskGateSelection`: a declared `review.test-map` first, then the `<Stem>Tests` name convention, keeping only classes that exist; nothing mapped means skipped with `KW-REVIEW-026`, never a pass. The declared map for this repository lands in `.kyber-weave/kyber-weave.yml` so the `csharp-dev` and `test-dev` task gates run mapped tests, and T46 measures the fast path — the open todo's completion condition, which T53's archive records.

**The single always-on source (T47).** `products/kyber-squad/shared/worker-baseline.md` (new directory, new file) holds the Discovery order, the full-suite ban line and the batched-reads line, injected once per worker render by a new `SquadWorkerBaselineInjector` that follows the `SquadResponsePreferencesInjector` pattern (`src/KyberWeave.Core.Squad/Deployment/`), applied in `SquadHarnessRenderer`. This keeps one copy and does not reduce what a worker reads; agents switch to it in T52, not here.

**The harness check is not a test.** T48 is a `docs-dev` task with no test class, by the parent's own contract, and inventing one is not allowed: its observable acceptance is the matrix itself — every row of "Other harnesses" above confirmed against the installed harness or vendor documentation, or marked `[NOT LIVE-VERIFIED]` with its fallback, recorded in the capability matrix of `docs/kyber-squad/requirements.md` — plus the two documentation gates at zero findings.

**The rule map, the moved pins and the trial (T49 to T52).** T49 builds `docs/kyber-squad/worker-rule-map.md` (D36) by search, not from the parent's estimates: one row per sentence to be removed across the agents and the conductor text, and across all 27 files holding the ban; the 5 standard templates keep their line, because a standard is host-owned project content, and the map says so; a rule with no enforcing check stays and is marked so; nothing is deleted. T50 moves the pins (each pin touching moved text — the `RED_EVIDENCE` template, the digest fields — points at its new home with the same string) and adds `WorkerRuleSingleHomeTests`, which fails when a moved rule appears in more than one file; per D62 those four classes are red by design until T52. T51 runs the seeded-defect trial (D37) — nine planted defects, two arms, the candidate text on a scratch branch for the "new" arm — and is T52's gate: a defect caught later on the new arm, and not named in the map as the check that now catches it later, stops T52 and the text for that rule stays. T52 applies only the rows the trial passed, updates every migration report whose agent body it edited (D60), and reports byte counts against the estimate.

**Archiving the parent design (T53).** The parent file lives only on `claude/docs/conductor-bug-crusher-run-speed` and never merges; D61 records the copy-and-edit mechanism, the link rewrite for the archive's extra depth, the `## Archived Plans` row, and the mapping's link policy. The mapping covers every decision of the parent ledger and all eight children: slices 1, 2, 3 and 6 are archived in this tree (`docs/archive/plans/`), slices 4 and 5 sit in this tree's `docs/plans/` and archive in their own pull requests before this one merges, and slice 7's child plan name and delivery branch are carried by the closeout from the carve record — never guessed (its archive is on its own branch, not in this tree). This plan archives itself in the same pull request: `git mv` to `docs/archive/plans/` with its row moved to `## Archived Plans`.

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | Rule map and trial result — one row per removed sentence, what enforces it, and the trial's per-defect stage table | KyberSquad | reference | `docs/kyber-squad/worker-rule-map.md` (new, D36) |
| H2 | `KW-ARB-GATE-REPORT-001` — the gate-report verification rule | KyberArbiter | architecture | `docs/kyber-arbiter/architecture.md`, rule reference (`docs/ci-pipelines/rule-reference.md`) |
| H3 | New task gates — `review suppressions` (D28) and the `{tests}` token with `review.test-map` (D30) | ReviewCouncil | architecture | `docs/code-review/architecture.md`, `docs/configuration.md#review`, `docs/cli-command-surface.md` (the `review suppressions` verb) |
| H4 | Harness matrix confirmed or marked `[NOT LIVE-VERIFIED]`, with fallbacks (T48) | KyberSquad | requirements | `docs/kyber-squad/requirements.md` target-capability-and-degradation-matrix |

Two shared rows of the parent's harvest are split the way slice 5 split one: H2 is the third item of the parent's `KW-ARB-PLANREAD-001`, `KW-ARB-GATE-REPORT-001`, lens-skip row — slice 5 harvested the other two and names this rule slice 8's. H3 is the "new task gates" half of the parent's "Catalogue `full-suite` key and new task gates" row — the catalogue-key half is slice 7's harvest. The parent-archive mapping is T53's own deliverable, recorded in the archived parent and its `## Archived Plans` row, not a harvest row here.

## ADR disposition

**No new ADR — ADR waiver.** The parent's own Design harvest assigns fix 7 a waiver: fix 7 applies the principle already in the C# standard — quality comes from checks that run, not from text a worker reads — and the durable record is the rule map (D36), a permanent reference, plus H1 to H4's targets. The three new checks refine enforcement surfaces earlier records already cover — the task-gate and review-gate architecture, the full-suite ban's context in [ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md), and the three-step gate pattern of [ADR 0053](../adr/0053-kyber-arbiter-three-step-decision-gates.md) that the new rule id extends — and open no boundary, dependency, schema or contract of their own; the new rule id is added, never renumbered. The harness check records observed behaviour into an existing requirements matrix. Number check: `docs/adr/` holds 0001 to 0062; slice 5's open plan claims 0063 at its closeout, and this slice consumes no number.

## Test contract

Development mode is `test-first`: every task's RED proof is written and shown failing first, then the implementation turns it GREEN. RED and GREEN are separate workers — never one task for both. No row runs the full suite — every command below is narrowed by class, and the process guard refuses an unfiltered run from a dev agent (exit 89). A filter that matches nothing exits 8 ("Zero tests ran"), which is how a wrong class name surfaces. RED proofs run on a clean base checkout or an isolated worktree, never with `git stash`.

The runner definition, restated from `docs/standards/test/README.md` (its Commands section — `## Completion` once slice 7's rename lands ahead of this slice) and `tests/KyberWeave.RepoPolicy.Tests/AGENTS.md`:

- **PRODUCT-RUN** — build once, then run the named classes:
  `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<ClassList>"`

The direct assembly runner (`dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class "<namespace>.<Class>"`) is the test standard's iteration alternative, and the task gates' `--artifacts-path` build puts the assembly at `<dir>/bin/KyberWeave.Tests/release/KyberWeave.Tests.dll`; either way the class must be fully qualified, because a short class name runs zero tests and exits 0. Classes in `tests/KyberWeave.Tests/Arbiter/` are in namespace `KyberWeave.Tests.Arbiter`; everything else named here is `KyberWeave.Tests`. New classes are marked new; every unmarked class named below exists today.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T43 | `tests/KyberWeave.Tests/` — `ReviewSuppressionsCommandTests` (new), `Arbiter/ArbiterGateReportRuleTests` (new), `TaskGateSelectionTests`, `ReviewGatesAgentCommandTests`, `SquadWorkerBaselineInjectionTests` (new) | Not applicable | RED authoring task; proof in T44 to T47's rows | New classes fail for the named reason | Not applicable |
| T44 | `tests/KyberWeave.Tests/` — `ReviewSuppressionsCommandTests` (new) | PRODUCT-RUN over the class | Added suppressions fail, existing ones do not, `reason:` passes | Fails: command absent | Passes on a real temporary repository |
| T45 | `tests/KyberWeave.Tests/` — `Arbiter/ArbiterGateReportRuleTests` (new) | PRODUCT-RUN over the class | Missing report and a line disagreeing with the report escalate; a matching report allows | Fails: rule absent | Passes, with the rule count in the `default-rules.yml` header updated and no id renumbered (18 shipped today; 19 once slice 5's T27 lands ahead of this slice) |
| T46 | `tests/KyberWeave.Tests/` — `TaskGateSelectionTests`, `ReviewGatesAgentCommandTests` | PRODUCT-RUN over the two classes | `{tests}` expands to fully qualified existing classes; nothing mapped gives `KW-REVIEW-026` | Fails: token unknown | Passes; the task-gate fast path is measured under a few minutes, recorded for T53's archive of the test-mapping todo |
| T47 | `tests/KyberWeave.Tests/` — `SquadCanonicalContentTests`, `SquadWorkerBaselineInjectionTests` (new) | PRODUCT-RUN over the two classes | Each worker's render carries the single source once: Discovery order, the ban, batched reads | Fails: no injection | Passes for all 13 |
| T48 | No test class — documentation and live verification | `docs validate .` and `docs drift .` after the matrix edit (CLI gates, through `scripts/proc-guard.sh run --`) | Every row of "Other harnesses" confirmed against the installed harness or vendor documentation, or marked `[NOT LIVE-VERIFIED]` with its fallback, in the requirements matrix | None — not a test task | `docs_validate` and `docs_drift` zero findings |
| T49 | No test class — documentation | `docs validate .` and `docs drift .` after the map is written | `docs/kyber-squad/worker-rule-map.md` has one row per removed sentence; nothing deleted yet | None — documentation task | `docs_validate` and `docs_drift` zero findings |
| T50 | `tests/KyberWeave.Tests/` — `SquadPerformanceContractTests`, `SquadCanonicalContentTests`, `ArbiterTestDevContractTests`, `WorkerRuleSingleHomeTests` (new) | PRODUCT-RUN over the four classes | Pinned strings hold at their new home; a moved rule in two files fails | Fails against current text — by design, per D62; not a RED proof | Passes after T52, no pinned string weakened |
| T51 | No test class — the seeded-defect trial | Not applicable (evidence task; runs real workers, not the suite) | Seeded-defect trial: nine defects, two arms, per D37 | Not applicable | Table recorded in the rule map. Each defect is caught at the same stage or the row names the later check. T52 does not start until this passes |
| T52 | The T50 classes | Same as T50 | Text removed per the rule map | Fails until the text moves | All T50 classes pass; byte counts recorded against the estimate |
| T53 | No test class — documentation and closeout | `docs validate .` and `docs drift .` after the docs edits, then `docs validate . --merge-ready` after the archives | Closeout, the follow-up todo for watching real runs, the parent design brought in and archived with its mapping to the eight children | None — documentation task | T53's acceptance: harvest, ADR waiver recorded, the test-mapping todo archived with T46's measurement, the child plan and the parent archived, zero findings on `--merge-ready` |

## Tasks

- **T43: RED for the new checks**
  - **Files:** `tests/KyberWeave.Tests/ReviewSuppressionsCommandTests.cs` (new), `tests/KyberWeave.Tests/Arbiter/ArbiterGateReportRuleTests.cs` (new), `tests/KyberWeave.Tests/TaskGateSelectionTests.cs`, `tests/KyberWeave.Tests/ReviewGatesAgentCommandTests.cs`, `tests/KyberWeave.Tests/SquadWorkerBaselineInjectionTests.cs` (new)
  - **Required skills:** `test-dev`
  - **Depends on:** none
  - **Acceptance:** rows T44 to T47 are written and fail for the reason they name.
- **T44: Suppression gate**
  - **Files:** `src/KyberWeave.Cli/Commands/Review/ReviewSuppressionsCommand.cs` (new), `src/KyberWeave.Core/Review/SuppressionScan.cs` (new), `src/KyberWeave.Cli/Program.cs`, `src/KyberWeave.Core/Review/Catalogue/GateCatalogue.cs`, `.kyber-weave/kyber-weave.yml`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T43
  - **Acceptance:** D28. Declared in `review.task-gates` — following the file's anchor-alias pattern so every .NET agent's task gate runs it — and offered by the csharp catalogue family. T44's row holds.
- **T45: Verify the gate report**
  - **Files:** `src/KyberWeave.Core/Arbiter/Rules/default-rules.yml`, `src/KyberWeave.Core/Arbiter/ArbiterTriggerCatalog.cs`, `src/KyberWeave.Core/Arbiter/Facts/GateReportFacts.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T43
  - **Acceptance:** D29. The new id `KW-ARB-GATE-REPORT-001` is added and none renumbered, and the rule count in the file header is updated (18 shipped in this tree; slice 5's T27 makes it 19 ahead of this slice, so T45 writes the then-current count plus one). T45's row holds.
- **T46: `{tests}` token**
  - **Files:** `src/KyberWeave.Core/Review/TaskGateSelection.cs`, `src/KyberWeave.Core/Configuration/ReviewConfig.cs`, `src/KyberWeave.Core/Configuration/ReviewConfigLoader.cs`, `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`, `.kyber-weave/kyber-weave.yml`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T43, T44
  - **Acceptance:** D30. This repository's `csharp-dev` and `test-dev` task gates run mapped tests through a declared `review.test-map`, and nothing mapped is skipped with `KW-REVIEW-026`, never a pass. T46's row holds, including the measured fast path — the open todo's completion condition, recorded so T53 can archive it.
- **T47: Single source for always-on worker text**
  - **Files:** `products/kyber-squad/shared/worker-baseline.md` (new), `src/KyberWeave.Core.Squad/Deployment/SquadWorkerBaselineInjector.cs` (new), `src/KyberWeave.Core.Squad/Rendering/SquadHarnessRenderer.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T43
  - **Acceptance:** follows the `SquadResponsePreferencesInjector` pattern (`Inject(SquadRenderResult, string)`, carrier validation). Holds the Discovery order, the full-suite ban line and the batched-reads line, injected once per worker render. Agents switch to it in T52, not here. T47's row holds.
- **T48: Check the harness behaviour this plan relies on**
  - **Files:** `docs/kyber-squad/requirements.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** none
  - **Acceptance:** every row of "Other harnesses" is confirmed against the installed harness or vendor documentation, or marked `[NOT LIVE-VERIFIED]` with its fallback, recorded in the target-capability-and-degradation-matrix. Includes Pi, Codex, Cursor and Warp subagent access to `codegraph_explore`, and a co-deployment of Antigravity with Pi, ZCode and Command Code (D41). This is not a test task and gets no test class: its observable acceptance is the matrix content itself, plus `docs validate .` and `docs drift .` at zero findings. T48's row holds.
- **T49: Build the rule map**
  - **Files:** `docs/kyber-squad/worker-rule-map.md` (new)
  - **Required skills:** `docs-dev`
  - **Depends on:** T44, T45, T46, T47
  - **Acceptance:** one row per sentence to be removed, across the agents and the conductor text, and across all 27 files holding the ban — rebuilt by search (D27), not from this plan's starting rows. The 5 standard templates keep their line, because a standard is host-owned project content, and the map says so. A rule with no enforcing check stays and is marked so. Nothing is deleted. The map also names the agent bodies it touches, from which D60's migration-report set is derived.
- **T50: Move the contract tests**
  - **Files:** `tests/KyberWeave.Tests/SquadPerformanceContractTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `tests/KyberWeave.Tests/ArbiterTestDevContractTests.cs`, `tests/KyberWeave.Tests/WorkerRuleSingleHomeTests.cs` (new)
  - **Required skills:** `test-dev`
  - **Depends on:** T49
  - **Acceptance:** each pin touching moved text, such as the `RED_EVIDENCE` template and the digest fields, points at its new home with the same string. The classes fail against today's text and pass after T52 — by design (D62), not a broken task, and no pinned string is weakened to make them pass early. `WorkerRuleSingleHomeTests` fails when a moved rule appears in more than one file.
- **T51: Seeded-defect trial**
  - **Files:** `.agent_scratchpad/T51/seeded-defect-trial.md`
  - **Required skills:** `test-dev`
  - **Depends on:** T50
  - **Acceptance:** D37. The candidate text lives on a scratch branch for the "new" arm. Each of the nine defects has a row giving the stage caught on each arm, returned for T52 to record in the rule map. This is the gate for T52: any row caught later and not named in the map stops T52, and the text for that rule stays.
- **T52: Remove the text**
  - **Files:** `docs/kyber-squad/worker-rule-map.md`, `products/kyber-squad/agents/*.md`, `products/kyber-squad/skills/task-gate/SKILL.md`, `products/kyber-squad/skills/task-gate/references/rework.md` (new), `products/kyber-squad/skills/delivery-execution/SKILL.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`, `products/kyber-squad/skills/test-dev/SKILL.md`, `products/kyber-squad/skills/kilo-review-loop/SKILL.md`, `products/kyber-squad/skills/typescript-dev/references/esm-and-gates.md`, `products/kyber-squad/skills/rust-dev/references/gates-and-platforms.md`, `products/kyber-squad/skills/go-dev/references/gates-and-platforms.md`, `products/kyber-squad/skills/csharp-dev/references/build-commands.md`, and every migration report whose agent body this change edits (D60): `products/kyber-squad/migration/<agent>.md`, one per agent named by the rule map
  - **Required skills:** `docs-dev`
  - **Depends on:** T51
  - **Acceptance:** only rows the trial passed are applied. D27, D43. The full-suite ban stays as one always-on line. `dotnet format analyzers` leaves the task-end text. The 2.7 KB duplicate leaves `conductor.md`. Baseline diagnostics apply only where warnings do not fail the build. Scope, weakened-test and masked-output rules stay in the agent file. T52's row holds, and byte counts are reported. All T50 classes pass.
- **T53: Docs and closeout for slice 8**
  - **Files:** `docs/kyber-squad/architecture.md`, `docs/code-review/architecture.md`, `docs/ci-pipelines/rule-reference.md`, `docs/todo/README.md`, `docs/todo/watch-worker-text-trim-in-real-runs.md` (new), `docs/plans/README.md`, and the archives this closeout performs: `docs/archive/plans/2026-11-07-conductor-bug-crusher-run-speed.md` (new — copied from the design branch per D61), `docs/archive/todo/task-gate-test-project-mapping.md` (moved), `docs/archive/plans/2026-11-07-conductor-run-speed-shrink-reads.md` (this plan, moved)
  - **Required skills:** `docs-dev`
  - **Depends on:** T48, T52
  - **Acceptance:** harvest, ADR waiver recorded, the test-mapping todo archived with T46's fast-path measurement, a new todo for comparing findings over the next few runs (this plan authorizes it), this child plan archived, and the parent design brought in from `claude/docs/conductor-bug-crusher-run-speed` by the D61 copy-and-edit mechanism — never by merging that branch — and archived with its mapping to the eight children, its row added to `## Archived Plans`. Zero findings on `--merge-ready`.

## Dependency graph

From the parent: T43 unlocks T44, T45 and T47. T44 unlocks T46 (both edit `.kyber-weave/kyber-weave.yml`). All four — T44, T45, T46, T47 — unlock T49. T48 stands alone. T49, then T50, then T51, then T52, then T53. T53 additionally depends on T48 (the parent's own task list). Workers run in parallel up to `review.max-concurrent-workers` (4) where file scopes allow; the slice ends with one council, then its closeout. The T49 to T52 chain is strict: T50's classes are red until T52 (D62), and T51's trial is T52's gate.

## Risks

- **Fix 7 loses a check.** The rule map, the trial and T52's gate exist for this. T53's todo watches real runs. A raw `dotnet test` is still possible, and the always-on line stays (the parent's own risk row).
- **The trial is only as good as its packets.** Same harness, model and packet on both arms (D37), and only stages are compared, not wording (the parent's own risk row).
- **T50's four classes are red between T50 and T52** (D62). The strict order covers it; a worker who runs them out of order sees the expected red, and the rework tally must not treat it as rework. No pinned string is weakened to shorten the gap.
- **Slices interleave with other work.** `run start` re-fetches for each slice, and a child plan is carved from the design at that slice's start, so files named here are checked by path plus symbol then (the parent's own risk row). Concretely: slice 7 delivers before this slice and shares file scope (`task-gate`, `delivery-execution`, `bug-crusher` and agent text), and this branch's base does not carry slice 7's changes — the rule map's destinations (for instance the `Completion` section) are verified against the tree T49 and T52 actually run in, and the sibling pull requests merge one at a time, slice 7 first (D59).
- **An edited agent body without an updated migration report fails the suite** (D60). The report set is derived from the rule map's named agents, and every report updates in the same change as its body.
- **The stacked-branch risk (D59).** This branch sits on slice 7's unmerged branch; the pull request targets slice 7's branch and is retargeted to `main` when slice 7 merges. Until then a conflict can produce a pull request GitHub runs no `pull_request` workflows on at all. Do not merge the base into this branch and do not rebase onto `main`; push to the stacked branch only.
- **The parent-archive copy could drift from the design branch.** T53 copies the file from the branch ref at closeout time; if the design branch moved since the last read, the copy is of the then-current version, which is the version D12 keeps authoritative. The mapping is written against the copy, and the two documentation gates and the link checker verify the archived result in this tree.

## Out of scope

- The parent's other slices — each has its own child plan on its own branch and its own pull request. `KW-ARB-PLANREAD-001` and the lens-skip repair are slice 5's; the catalogue `full-suite` key and the `## Completion` rename are slice 7's; the third item of the parent's shared harvest row is this slice's (H2).
- Fix 9, routing each request with the decision model: [issue 535](https://github.com/dpalfery/kyber-weave/issues/535).
- Any change to model assignment or model profiles. The owner handles model targeting separately.
- Collapsing RED and GREEN into one dispatch. The adversarial split stays.
- A size limit on plans. Regrowth is stopped by `WorkerRuleSingleHomeTests` and the single-home pins, not by a limit.
- A shell for the architect, and the fact-finder agent in issue 221.
- The per-harness shell hook for the full-run guard: that todo stays open.
- Widening `context7` grants.

## Verification gates

- Every task's row in the Test contract runs a named class set with a **non-zero test count**; a filter that matches nothing exits 8 and is treated as a wrong class name, not a pass. **No task or gate runs the full suite** — the full suite belongs to the review council's `test` gate and CI, and `scripts/proc-guard.sh run` refuses an unfiltered run from a dev agent with exit 89.
- `docs validate .` and `docs drift .` run after every plan save and after every docs task (T48, T49, T52, T53), each at zero findings, in the tree the work runs in (the worktree protocol; the kw-docs server is bound to the canonical checkout and cannot see a file written in this worktree). `docs drift` needs a CodeGraph index in the tree it runs in: `doctor --ensure .` runs first in a fresh worktree. `docs validate . --merge-ready` is **not** the check while this plan is open: `KW-DOC-LIFECYCLE-003` fails an open plan by design, and the pull request CI runs it, which is what archives this plan in this slice's pull request at closeout.
- **Documentation gates at carve.** This harness gives the architect no shell and the docs server is bound to the canonical checkout, so the conductor ran the two gates in this worktree: both reached zero findings, the body Status line was set to Ready (approved for execution under AP1, the parent's carry per D12), the index row's status cell was updated to match, and both gates were re-run after those edits, again at zero findings. The frontmatter `status: current` did not change. The commands, for the same tree after every later docs edit:
  `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .`
  `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -c Release -- docs drift .`
- **Branch and title.** The `pr-naming` CI job gates this slice's pull request (verified on this branch: `ci.yml:115`, needed at `:1188`, with `pr-naming-recheck.yml` beside it). The delivery branch `factory/feat/conductor-run-speed-shrink-reads` already follows `<harness>/<type>/<slug>`; the pull request title follows `<type>(<scope>): <summary> [factory]` — `feat(squad): shrink what workers read [factory]` — or the naming job fails it, and the pull request targets slice 5's branch, retargeted to `main` as the bases merge (D59).
- **After the slice:** compare a bug-crusher run of similar size against the parent's measured table and report the time and tokens, as an estimate is not a result (the parent's own verification gate). The comparison lands in T53's new todo, which watches real runs.

## Review

This slice's pull request goes through **one `code-reviewer` council over its whole change**, run after the draft is pushed, with nothing else editing that tree while the council runs. No agent reviews a single task; each developer task ends with the worker's own `task-gate` run, and the council is the single end-of-run review, its command derived from the `code-review` skill with `--base` — never copied into this plan.

## Closeout

**Not closed.** This section is written by this slice's closeout (`docs-dev`, T53): what was verified against the delivered tree, the gates run, the archive move of this plan to `docs/archive/plans/` in this slice's own pull request, and the parent design's archive with its mapping to the eight children. This plan carries no `<plan>.tasks.json` of its own: the task-file mechanism is slice 5's delivery and the architect finalizing this plan runs under the pre-change authoring text on this branch, so `KW-DOC-TASKS-006`'s warning is the expected state until slice 5 lands; if a task file exists at closeout, T53 removes it with `git rm` in the same commit that archives this plan.

## Closeout mapping

**To be written at closeout.** Every durable decision (D12, D13, D27, D28, D29, D30, D35, D36, D37, D41, D43, AP1, D59 to D62) and all four harvest rows (H1 to H4) map to a destination; the mapping table is recorded when the slice closes, and the parent design's own mapping to the eight children is recorded in the archived parent and its `## Archived Plans` row.
