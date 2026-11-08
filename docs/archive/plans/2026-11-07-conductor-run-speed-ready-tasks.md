---
id: plans/2026-11-07-conductor-run-speed-ready-tasks
title: Ready-to-paste tasks (conductor run-speed, slice 5)
doc-type: plan
status: archived
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
---

# Ready-to-paste tasks (conductor run-speed, slice 5)

**Status: Archived.** Closed 2026-11-08; see the Closeout and Closeout mapping sections at the end.

This plan is **Ready and approved for execution under AP1**, with resolved autonomy `autonomous`. It is the child plan of the parent design for the conductor run-speed work, which lives on its own branch (`claude/docs/conductor-bug-crusher-run-speed`) and is deliberately not in this tree. Per the parent's D12, each slice gets one child plan on the slice's branch, carved from the parent's section and complete on its own: this plan restates only slice 5's decision rows, design, harvest rows, ADR disposition and tasks. The parent design is archived by the last slice's pull request with a mapping to the children; this plan is archived in this slice's own pull request. Every decision is answered below with its provenance; the parent's AP1 approval covers the Test contract this plan restates and carries to this child through `AUTONOMOUS_APPROVAL` per D12, and the conductor presents the approve-and-execute gate. The pair of documentation gates ran in this worktree — `docs validate .` and `docs drift .`, not `--merge-ready` while the plan is open — each at zero findings; the kw-docs server is bound to the canonical checkout and cannot see a file written only here, so a passing answer from it would describe the wrong tree.

## Problem and goal

The parent design's measured conductor run showed the orchestrator rewriting a 42 KB plan into **128 KB of prompts across 26 dispatches** (`Tasks` plus `Test contract` were a third of the plan), while the prompts themselves still told workers too little: the architect's authoring contract asks for "exact files or symbols" and no commands (`plan-authoring.md:25`), so no prompt carries a full test class name or an exact command, and bug-crusher told 5 of its 6 workers to read the plan — a read the guard cannot stop where hooks are advisory. Three gaps sit beside it: nothing enforces the plan-read ban at dispatch; the Arbiter's lens rule skips `correctness` and `test-adequacy` whenever the changed-path set is Markdown-only, and an empty set passes that test, so a committed tree — which per-task commits make the normal case — silently loses both lenses (D44); and resuming carried nothing, because worker prompts said "Do not commit", so a mid-run push had no `Task:` lines to read (D38).

The goal of this slice (the parent's fixes 3, 6 and 10, plus D44): **the architect saves a tracked per-plan task file at finalization** (`docs/plans/<plan>.tasks.json`), whose prompts carry path plus symbol, full test class names and exact commands, and the conductor validates the file, pastes each prompt unchanged and fills only two slots; **a `session` group keeps follow-on work in one worker session** while RED and GREEN stay separate workers; **a new `KW-ARB-PLANREAD-001` rule enforces the plan-read ban at dispatch**, with the docs tools as the shell-less conductor's validator; **the lens rule stops skipping on a committed tree**; and **`run start --resume` reads `Task:` commit lines**. The slice completes no existing todo.

## Decisions

Rows D2, D12, D13, D18, D19, D35, D38, D39, D44 and AP1 are this slice's rows from the parent design's decision ledger, copied verbatim per the parent's own D12 carve rule. In D12 and AP1, "this file" and "this plan" name the parent design file on its branch, not this child plan. D55 to D58 record the scope and delivery adjustments this slice carries, verified against this tree.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D2 | Where do worker prompts live? | The architect writes each worker's prompt into `docs/plans/<plan>.tasks.json`, tracked in git so a push carries it to another machine. `docs-dev` deletes it with `git rm` in the same commit that archives the plan. The Markdown plan stays the authority. | Details in D18, D19, D38 and D39. | user: explicit decision 2026-11-07 |
| D12 | Can one plan span several pull requests? | **No. Several plans, one per slice.** This file stays the single approved design and never merges while open. On approval it stays on its own branch. Each slice gets a child plan on the slice's branch, carved from its section here, and that plan is archived in the slice's pull request. The last slice's pull request brings this file in and archives it with a mapping to the children. | `OpenWorkValidator` raises `KW-DOC-LIFECYCLE-003`, and the pull request CI runs `docs validate --merge-ready`. A plan left open in `docs/plans/` fails CI, and the rule exists because a specification merged in stages never got its closeout. Its message says "keep it on its branch". Changing the rule was rejected for that reason. Child plans restate only their slice's decision rows, so each is complete on its own. Approval here carries to each child through `AUTONOMOUS_APPROVAL`. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |
| D13 | Does the requested build order hold against real dependencies? | **Five changes.** (a) Fix 5's one-line test repair moves to the very front, because a failed blocking gate forces `REQUEST_CHANGES`, so no slice's council can approve on the owner's machine until it lands. (b) The `stop-on-tool-failure` setting moves to the fix 8 slice, because fix 8's fetch failure reads it. (c) The `branch=` and `repo=` provenance fields move from fix 4 to fix 1, because the worktree rule needs them. (d) Fix 5's remaining work goes after fix 2, because it edits the shared execution skill fix 2 creates. (e) The `github-actions` standard is declared in the first slice, because `github-devops` refuses to write a workflow without it. Fixes 6 and 10 land with fix 3: fix 6 adds a field to the task file, and fix 10 shares fix 3's detector. Otherwise 8, then 1 to 5, then 11, then 7 stands. | Fix 7 depends on fix 11 (the `Completion` section) and on fix 3 (commands in the prompt). Fix 11 depends on fix 4's topic repair. | autonomous: recommendation accepted |
| D18 | What does the task file's digest cover? | A new `PlanDocumentParser.ExecutionDigest(PlanDocument)` over the parsed tasks, files, dependencies, skills, test rows, out-of-scope paths and mode. Not the raw-text `Digest`. | The existing `Digest` hashes the whole file (`PlanDocumentParser.cs:128`), so a status flip or a decisions-row edit would mark the file stale and force an architect dispatch. Stale means the conductor refuses it until the architect regenerates. | autonomous: low-confidence, no authoritative source |
| D19 | How does a conductor with no shell check the task file? | Through `docs_validate_governed_document_schema`, which runs a new `PlanTaskFileValidator`. New ids: `KW-DOC-TASKS-001` malformed or wrong schema, `-002` tasks, files or dependencies differ from the plan, `-003` stale digest, `-004` prompt has no command or acceptance criteria, `-005` prompt tells a worker to open a plan, `-006` (warning) Ready plan with no task file. `KW-DOC-LIFECYCLE-005` fails `--merge-ready` when a task file is left behind. | `capabilities.yml:121` denies the orchestrator process execution, but it has the docs tools. `OpenWorkValidator` reads only `*.md` documents, so it would miss the file. | autonomous: recommendation accepted |
| D35 | Which decisions need an ADR? | See this plan's ADR disposition below, and the parent's Design harvest. New ADRs cover the stop rule (the reversal of "never block a run"), the server-side refresh, the tracked task file, project knowledge and onboarding, and run start with the harness name. ADR 0050, ADR 0059 and ADR 0053 are amended in place. Fixes 2, 6 and 7 carry a waiver or a section of another ADR. | Numbers are assigned at each closeout, from 0060. | autonomous: recommendation accepted |
| D38 | How does another machine resume? | Each finished task commits on the feature branch with a `Task: <id>` line and the worker's report in the commit body. The branch is pushed after each integrated task. `run start --resume` reads those lines and prints completed tasks and prior results. | Worker prompts said "Do not commit" in the measured run, so a mid-run push carried nothing. The conductor run opened with an architect pass just to assess progress (225 seconds, 64 tool calls). Pushes before a pull request exists start no CI. Squash merging keeps `main` clean. | autonomous: recommendation accepted |
| D39 | Where does a specification's task file go? | `<spec-folder>/tasks.json`, removed with `git rm` before the folder's `git mv`. | Specifications share the execution contract. `KW-DOC-LIFECYCLE-005` also looks under the archive, so a file moved with the folder is caught. | autonomous: recommendation accepted |
| D44 | The Arbiter's lens-skip rule reads a set that is empty on a committed tree. Is that in scope? | Yes. `review.changed-paths` becomes committed plus dirty paths against the review base, and `KW-ARB-LENS-001` no longer skips on an empty set. T27's RED test confirms the behaviour first. | `HookGitFacts.cs:31` uses only `GetDirtyStatuses`. An empty list passes `subset-of ["**/*.md"]`, which would mark `correctness` and `test-adequacy` not applicable. Seen by reading, not by running. Delivery already pushes before the council, and fix 3 makes a clean tree the normal case, so leaving it would lose a quality check. | autonomous: recommendation accepted |
| AP1 | Is the Test contract approved, and may this plan execute? | Approved as written, as recommended. Execution follows D12. The owner confirmed on 2026-11-07 that all eight slices run in one continuous run. | Not a planning decision. Resolved by the conductor carrying `AUTONOMOUS_APPROVAL: AP1` under resolved autonomy `autonomous`, and then confirmed by the owner. | user: confirmed 2026-11-07 — the owner confirmed the autonomous recommendation |
| D55 | Why does this slice branch from slice 4's unmerged branch? | Because slice 5 edits files slice 4 rewrote: `products/kyber-squad/skills/delivery-execution/SKILL.md` exists only on slice 4's branch (its T22 created it there), and T29 edits it beside `plan-path.md` and `spec-path.md`, which slice 4 also touched. Branching from `main` would conflict on the first push, and GitHub runs no `pull_request` workflow on a conflicted pull request, so CI would never report. This branch is cut from slice 4's branch at `d9c489c1`, its tip today, and holds no commit of its own yet. | Measured twice on slice 2 and recorded by slice 4's D51: a conflicted pull request produced no run at all; the only way to a verdict was merging the base first. The slice's pull request targets slice 4's branch and is retargeted to `main` as slice 4 merges. Do not merge slice 4 into this branch and do not rebase onto `main`. | autonomous: recommendation accepted |
| D56 | Does T28 touch the open `--resume` todo slice 6 filed? | It touches the same command but not the todo's work. `docs/todo/run-start-resume-refuses-a-pull-request-number.md` (open in this tree, filed by slice 6 after the parent was written) wants the pull-request form of `--resume` implemented, or the decision narrowed with a pinning test. This tree already narrows the option to `--resume <BRANCH>` (`RunStartSettings.cs:33-38`) and every refusal names the accepted branch form (`RunStartCommand.cs:163-196`); what is missing is the recorded decision and the pinning test. T28 implements only the `Task:` commit-line reader (D38) and must not widen `--resume` to accept a pull-request number. | The parent's slice table says this slice completes no todos, and the todo postdates the parent. The todo stays open; T31's closeout records its state. T25's `RunStartResumeTests` pins the reader, and if those tests incidentally pin the refusal too, that is reported to the owner rather than closing the todo here. | autonomous: recommendation accepted |
| D57 | Where does `SquadAutonomousGatesInventoryTests` run, and why does adding a rule keep it green? | In the policy project (`tests/KyberWeave.RepoPolicy.Tests/`), in namespace `KyberWeave.Tests`, so T27 proves it stays green through a POLICY-RUN, not a PRODUCT-RUN. It pins `docs/reference/autonomous-gates.md`, which lists the human-approval gates reachable in autonomous mode and holds no `KW-ARB-*` rule id (verified by search); adding `KW-ARB-PLANREAD-001` touches neither the page nor the phrases the test asserts, so it stays green without an edit to it. | The parent's T27 acceptance says the test stays green but names no runner; the class's project and namespace were verified in this tree. The new rule's own documentation lands in the rule reference and `docs/kyber-arbiter/architecture.md` (H2), not in the autonomous-gates inventory. | autonomous: recommendation accepted |
| D58 | Which migration reports does T29's edit invalidate? | Three: `products/kyber-squad/migration/architect.md`, `migration/docs-dev.md` and `migration/product-owner.md`. T29 edits those three agent bodies, and each report's `final-body-sha256` covers the evolved LF-normalized instruction body, so the canonical suite fails until each report carries its body's new digest. The three reports join T29's file list. | The slice 4 D52 trap, verified against this tree: the migration directory holds reports for all three agents (listed), and slice 4 had to update `migration/conductor.md` for the same reason. The conductor references (`plan-path.md`, `spec-path.md`), the architect reference `plan-authoring.md` and the `delivery-execution` skill are not agent bodies and hold no migration digest. | autonomous: recommendation accepted |

## Investigation findings

Verified against this tree — the worktree at `/tmp/kw-slice5`, branch `factory/feat/conductor-run-speed-ready-tasks`, cut from slice 4's unmerged branch at its tip `d9c489c1` (both branch refs read `d9c489c1752…`; this branch holds no commit of its own yet) — not assumed from the packet. Discovery: codegraph 1, docs 1, direct reads 17, plus targeted greps and path-existence globs. The worktree carries no `.codegraph/` index of its own and this harness gives the architect no shell, so `doctor --ensure` cannot run here; the kw-docs and codegraph servers are bound to the canonical checkout (this session's provenance: `root=/Users/dave/git/personal/factory/kyber-weave`, `rev=d9c489c1`, `dirty=no`) — the same revision this branch is cut from, so their corpus answers describe this tree's content, though the server can never validate a file written only in this worktree. Per the parent design's worktree rule, a shell-less role reads the worktree's absolute path directly; the direct reads are enumerations and line-citation checks an index query does not return — rule-id inventories, file existence, the migration-report set, git ref tips, and line numbers such as `HookGitFacts.cs:31`.

**The prompt-authoring surface, today.** `plan-authoring.md:25` asks for "dispatchable tasks with objective, exact files or symbols, acceptance criteria, dependencies, and required skills" — no commands, no test class names. `docs-dev.md:41-47` is a closed list of four command classes (`git mv`, `docs validate`, `docs drift`, targeted tests) closed by "Run nothing else" at `:47` — no `git rm`, so the file D2 says `docs-dev` deletes could never be removed. The orchestrator profile denies process execution at `products/kyber-squad/profiles/capabilities.yml:121` (the parent's `capabilities.yml:121` citation), which is why the conductor checks the task file through the docs tools (D19); the MCP validate tool already carries its merge-ready flag with "Default false for active plan work" (`src/KyberWeave.Mcp/DocsTools.cs:458`).

**The parser and the digest.** `PlanDocumentParser.Parse` returns status, development mode, tasks (ids, text, files, dependencies, skills, checked), out-of-scope paths and contract rows (`PlanDocumentParser.cs:136-220`), so D18's execution digest has a parsed model to cover; the raw-text `Digest(string)` it must not use hashes the whole file (`:128`).

**The Arbiter rule inventory.** `default-rules.yml` ships 18 rules and says so in its header (`:1`: "The 18 shipped Arbiter rules. Ids are permanent"); `KW-ARB-PLANREAD-001` exists nowhere in `src/` (searched), so it is the 19th rule and the header count updates with it. `KW-ARB-LENS-001` (`:198`) skips `correctness` and `test-adequacy` when `review.changed-paths` is `subset-of ["**/*.md"]` (`:203-205`, and the `lens-paths` table at `:294-301`) — an empty set passes `subset-of`, which is D44's trap. `KW-ARB-DIFF-001` reads `git.changed-paths.since-dispatch` (`:179`). `config.planning-dirs` is a real fact key (`ArbiterTriggerCatalog.cs:51`, `TriggerFactBuilder.cs:150`, `ReadGuard.cs:44`), and the new rule is distinct from `KW-ARB-PLAN-001` (`:168`): that rule checks the `delegation.plan-file` fact, while the new one reads the prompt text after removing the routing header block.

**The git facts.** `HookGitFacts.cs:31` feeds its changed-path list from `GitFacts.GetDirtyStatuses` alone. `GitFacts.GetChangedPathsSinceBase` (`GitFacts.cs:125`) is the committed-plus-dirty form against a base, and `GetChangedPathsSinceSnapshot` (`:184`) already documents that a path counts "when `HEAD` has moved and the committed diff names it" (`:177-183`) — T30's proof target, and why its row may record a passing proof.

**The resume surface.** `--resume <BRANCH>` exists (`RunStartSettings.cs:33-38`) with refusals that name the accepted branch form (`RunStartCommand.cs:163-196`); what is missing is D38's reader — no `Task:` commit-line reading and no pinning test. The open todo slice 6 filed on the pull-request-number form is D56's boundary.

**The test surface.** Of T25's ten files, five exist today — `OpenWorkValidatorTests.cs`, `Arbiter/ArbiterDefaultRulesTests.cs`, `Arbiter/ArbiterEvaluatorTests.cs`, `SquadCanonicalContentTests.cs`, `SquadPerformanceContractTests.cs` — and five are new in T25: `PlanTaskFileValidatorTests.cs`, `PlanExecutionDigestTests.cs`, `Arbiter/ArbiterPlanReadRuleTests.cs`, `Arbiter/ArbiterLensCleanTreeTests.cs`, `RunStartResumeTests.cs` (all searched for). `SquadAutonomousGatesInventoryTests` lives in the policy project in namespace `KyberWeave.Tests` and pins `docs/reference/autonomous-gates.md`, which holds no `KW-ARB-*` id (D57). No `KW-DOC-TASKS-*` id and no `KW-DOC-LIFECYCLE-005` exist yet; `KW-DOC-LIFECYCLE` stops at `-004`. Every product and docs file T26 to T31 names exists in this tree except the three T26 creates; the migration reports D58 names exist for all three agent bodies T29 edits.

**The naming job gates this branch.** `pr-naming` is declared at `ci.yml:115`, needed by the pull request summary (`:1188`), with `pr-naming-recheck.yml` beside it. The delivery branch `factory/feat/conductor-run-speed-ready-tasks` already follows `<harness>/<type>/<slug>`; the pull request title follows `<type>(<scope>): <summary> [factory]` or the naming job fails it, and the pull request targets slice 4's branch per D55.

**ADR numbering.** `docs/adr/` holds 0001 to 0062: slices 1 and 2 took 0060 to 0062 and no slice since added one, so this slice's new ADR takes **0063**, assigned at closeout per D35.

## Design

This slice's design, in the parent's voice. Fixes 3, 6 and 10 verbatim:

> ### Fix 3: the architect writes each worker's prompt
>
> At finalization the architect saves `docs/plans/<plan>.tasks.json` and returns its path. It is committed with the plan at once.
>
> - **Contents.** Schema version, plan path, execution digest, and for each task: id, role, dependencies, file scope, session group (fix 6) and prompt. Each prompt carries files as path plus symbol, full test class names, exact commands, acceptance criteria and the standards to fetch (`csharp`, `test`). Line numbers are hints at most, because they go stale when an earlier task edits the same file.
> - **Slots.** The conductor fills `{worktree}` and `{prior_results}` at dispatch, pasting earlier worker reports unchanged. No plan can know these: the second GREEN prompt needed a cause an earlier worker found.
> - **The plan stays.** The owner approves it, it holds the Decisions table, and the Arbiter reads task ids, files, dependencies, skills, task text, test rows and out-of-scope items from it through `PlanDocumentParser`. Only the conductor stops reading it (68 KB in the measured run).
> - **Duplication is by design, and checked** (D18, D19).
> - **Purpose.** Quality first. Conductor-only time was 6.6% of the run, and the conductor still sends each prompt. The saving is fewer reads by workers. Pointing workers at a file would save more, but it conflicts with the read guard, so this starts with pasting.
> - **After archive.** `docs-dev` removes the file with `git rm`, so it never reaches `main`, because this repository only squash merges. Nothing durable is lost, since the archived plan keeps the decisions, tasks and test contract, and the pull request's commits stay reachable.
> - **Resume** (D38).
> - **Docs worker.** Its prompt names the sections to edit and summarizes what changed, so it does not re-derive that from source.

> ### Fix 6: keep related work in one worker session
>
> RED and GREEN stay in separate workers. Follow-up fixes and later same-role tasks on one change continue the earlier session where the harness allows it: changing one assertion took a fresh worker 11 minutes. The architect sets a `session` group in the task file, and may merge RED tasks when a later one does not need an earlier GREEN.

> ### Fix 10: enforce the plan-read ban
>
> The read guard is advisory where hooks are, so nothing stopped the reads on Command Code. New rule `KW-ARB-PLANREAD-001` on `delegate`: after removing the routing header block, does the prompt name a path under `config.planning-dirs`? Documentation-profile targets are exempt, for closeout. It shares `PlanReadInstruction` with `KW-DOC-TASKS-005`. It may not fire where dispatch hooks are unconfirmed.

**The task file, its digest and its validator.** The execution digest (D18) covers the parsed tasks, files, dependencies, skills, test rows, out-of-scope paths and mode, so a status flip or a decisions-row edit does not mark the file stale, while a task edit does. `PlanTaskFileValidator` (D19) raises `KW-DOC-TASKS-001` for a malformed or wrong-schema file, `-002` when tasks, files or dependencies differ from the plan, `-003` for a stale digest, `-004` when a prompt has no command or acceptance criteria, `-005` when a prompt tells a worker to open a plan, and `-006` (warning) when a Ready plan has no task file; `KW-DOC-LIFECYCLE-005` fails `--merge-ready` when a task file is left behind, and looks under the archive too, so a specification folder moved with its `tasks.json` is caught (D39). All of it is reachable through `docs validate` and the MCP validate tool, because the orchestrator holds the docs tools and no shell.

**The lens-skip repair (D44).** `review.changed-paths` becomes committed plus dirty paths against the review base — the form `GitFacts.GetChangedPathsSinceBase` already computes — replacing the dirty-only list at `HookGitFacts.cs:31`, and `KW-ARB-LENS-001` no longer treats an empty set as Markdown-only, so `correctness` and `test-adequacy` still apply on a committed tree. T25's `ArbiterLensCleanTreeTests` is written first and confirms the skip exists; if the lens is not skipped today, that is reported and D44's task is amended rather than the test forced.

**Resume (D38).** Each finished task commits on the feature branch with a `Task: <id>` line and the worker's report in the commit body; the branch is pushed after each integrated task; `run start --resume` reads those lines and prints completed tasks and prior results. T28 adds the reader to the existing `--resume <BRANCH>` surface and does not widen the form (D56).

**Where the text lands (T29).** The architect's authoring contract (`plan-authoring.md:25`) changes from "exact files or symbols" to path plus symbol, full test class names and exact commands, and gains the `session` group rule; the architect saves the file at finalization and returns its path. The conductor's execution text — whose current home is the `delivery-execution` skill slice 4 created, with `plan-path.md` and `spec-path.md` beside it; the old `agents/conductor/references/execution-and-review.md` no longer exists in this tree — gains the validation step and the paste-and-fill rule: prompts are pasted unchanged, and only `{worktree}` and `{prior_results}` are filled. Prompts tell workers to commit with a `Task:` line. `git rm` joins `docs-dev`'s closed command list (`docs-dev.md:41-47`), so the file is removed in the same commit that archives the plan. The product-owner text follows the specification form of the same contract (`<spec-folder>/tasks.json`, D39). Three migration reports update with the agent bodies in the same change (D58).

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | Task file format (`docs/plans/<plan>.tasks.json`), its rule ids, the `Task:` commit trailer, and the `session` group | KyberSquad | reference | `docs/kyber-squad/architecture.md`, `docs/ci-pipelines/rule-reference.md`, `docs/plans/README.md` policy text |
| H2 | `KW-ARB-PLANREAD-001` and the lens-skip change (committed plus dirty paths against the review base) | KyberArbiter | architecture | `docs/kyber-arbiter/architecture.md`, rule reference (`docs/ci-pipelines/rule-reference.md`) |

The parent's harvest table carries `KW-ARB-GATE-REPORT-001` in the same row as H2's two items; that rule is slice 8's (its T45), so this child harvests only the plan-read rule and the lens-skip change, and the row's third item stays slice 8's.

## ADR disposition

One new ADR, written by T31 at the next free number: **0063** in this tree (`docs/adr/` holds 0001 to 0062; slices 1 and 2 took 0060 to 0062 and none since), confirmed at closeout per the parent's D35. It records the tracked task file with the Markdown plan as authority — D2, D18, D19, D38, D39 — and the rejected alternatives: pointing workers at a file, moving tasks out of the plan. [ADR 0053](../../adr/0053-kyber-arbiter-three-step-decision-gates.md) is amended in place for the two new enforcement behaviours — `KW-ARB-PLANREAD-001` and the lens-skip change, D44 — with its index row updated. ADR waiver, per the parent's own harvest note: fix 6 (the session group) refines existing session rules and opens no boundary of its own, so no ADR covers it; its durable behaviour lands in H1's targets and the `delivery-execution` skill.

## Test contract

Development mode is `test-first`: every task's RED proof is written and shown failing first, then the implementation turns it GREEN. RED and GREEN are separate workers — never one task for both. No row runs the full suite — every command below is narrowed by class, and the process guard refuses an unfiltered run from a dev agent (exit 89). A filter that matches nothing exits 8 ("Zero tests ran"), which is how a wrong class name surfaces. RED proofs run on a clean base checkout or an isolated worktree, never with `git stash`.

The two runner definitions, restated from `docs/standards/test/README.md` ("Commands") and `tests/KyberWeave.RepoPolicy.Tests/AGENTS.md`:

- **PRODUCT-RUN** — build once, then run the named classes:
  `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<ClassList>"`
- **POLICY-RUN** — build once, then run the named classes:
  `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"`

The direct assembly runner (`dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class "<namespace>.<Class>"`, the form the parent's contract names) is the test standard's iteration alternative; either way the class must be fully qualified, because a short class name runs zero tests and exits 0. Classes in `tests/KyberWeave.Tests/Arbiter/` are in namespace `KyberWeave.Tests.Arbiter`; everything else named here is `KyberWeave.Tests` — including `SquadAutonomousGatesInventoryTests`, which lives in the policy project (D57). New classes are marked new; every unmarked class named below exists today.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T25 | `tests/KyberWeave.Tests/` — `PlanTaskFileValidatorTests` (new), `PlanExecutionDigestTests` (new), `RunStartResumeTests` (new), `Arbiter/ArbiterPlanReadRuleTests` (new), `Arbiter/ArbiterLensCleanTreeTests` (new), `OpenWorkValidatorTests`, `Arbiter/ArbiterDefaultRulesTests`, `Arbiter/ArbiterEvaluatorTests`, `SquadCanonicalContentTests`, `SquadPerformanceContractTests` | Not applicable | RED authoring task; proof in T26 to T30's rows | New classes fail for the named reason | Not applicable |
| T26 | `tests/KyberWeave.Tests/` — `PlanTaskFileValidatorTests` (new), `PlanExecutionDigestTests` (new), `OpenWorkValidatorTests` | PRODUCT-RUN over the three classes | Each of `KW-DOC-TASKS-001` to `-006` and `KW-DOC-LIFECYCLE-005` fires on its case. A status edit does not change the digest. A task edit does | Fails: validator absent | Passes, including through the MCP validate tool |
| T27 | `tests/KyberWeave.Tests/` — `Arbiter/ArbiterDefaultRulesTests`, `Arbiter/ArbiterEvaluatorTests`, `Arbiter/ArbiterPlanReadRuleTests` (new), `Arbiter/ArbiterLensCleanTreeTests` (new) | PRODUCT-RUN over the four classes, plus POLICY-RUN over `SquadAutonomousGatesInventoryTests` (D57) | A prompt naming a planning path escalates; the routing header alone does not; `docs-dev` is exempt. On a committed tree `correctness` and `test-adequacy` still apply | Fails: rule absent; lens skipped on a clean tree (or the test shows it is not, which is reported) | Passes |
| T28 | `tests/KyberWeave.Tests/` — `RunStartResumeTests` (new) | PRODUCT-RUN over the class | `--resume` lists completed tasks and prior results from `Task:` commit lines | Fails: no resume reader | Passes on a real temporary repository |
| T29 | `tests/KyberWeave.Tests/` — `SquadCanonicalContentTests`, `SquadPerformanceContractTests` | PRODUCT-RUN over the two classes | Architect saves the file at finalization; conductor pastes and fills slots; `docs-dev` list holds `git rm`; session group rule present | Fails: old text | Passes |
| T30 | `tests/KyberWeave.Tests/` — `Arbiter/ArbiterEvaluatorTests` | PRODUCT-RUN over the class | `git.changed-paths.since-dispatch` still reports committed paths, so per-task commits do not hide scope drift | Fails if commits hide paths, otherwise records a passing proof | Passes |
| T31 | No test class — documentation and closeout | `docs validate .` and `docs drift .` after the docs edits (CLI gates, through `scripts/proc-guard.sh run --`) | `docs_validate` and `docs_drift` zero findings | None — documentation task | T31's acceptance: harvest, new ADR, ADR 0053 amended, the plan index's policy text covers the task file, child plan archived, zero findings |

The parent's T25 contract row says "proof in T26 to T29's rows" while its task acceptance says "rows T26 to T30"; T30's own RED-evidence cell resolves the overlap — its row is authored and run in T25's RED batch and either fails (if commits hide paths) or records a passing proof, exactly as its cell allows.

## Tasks

- **T25: RED for the task file**
  - **Files:** `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs` (new), `tests/KyberWeave.Tests/PlanExecutionDigestTests.cs` (new), `tests/KyberWeave.Tests/OpenWorkValidatorTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterPlanReadRuleTests.cs` (new), `tests/KyberWeave.Tests/Arbiter/ArbiterLensCleanTreeTests.cs` (new), `tests/KyberWeave.Tests/Arbiter/ArbiterDefaultRulesTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterEvaluatorTests.cs`, `tests/KyberWeave.Tests/RunStartResumeTests.cs` (new), `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `tests/KyberWeave.Tests/SquadPerformanceContractTests.cs`
  - **Required skills:** `test-dev`
  - **Depends on:** none
  - **Acceptance:** rows T26 to T30 are written and fail for the reason they name. If `ArbiterLensCleanTreeTests` shows the lens is not skipped, report that and amend D44's task.
- **T26: Task file model and validator**
  - **Files:** `src/KyberWeave.Core/Arbiter/Plans/PlanDocumentParser.cs`, `src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs` (new), `src/KyberWeave.Core.Docs/Validation/PlanTaskFile.cs` (new), `src/KyberWeave.Core.Docs/Validation/PlanReadInstruction.cs` (new), `src/KyberWeave.Core.Docs/Validation/OpenWorkValidator.cs`, `src/KyberWeave.Core.Docs/Validation/DocsSchemaGate.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T25
  - **Acceptance:** D18, D19, D39. Reachable through `docs validate` and the MCP validate tool. T26's row holds.
- **T27: New Arbiter rule and the lens-skip repair**
  - **Files:** `src/KyberWeave.Core/Arbiter/Rules/default-rules.yml`, `src/KyberWeave.Core/Arbiter/ArbiterTriggerCatalog.cs`, `src/KyberWeave.Arbiter/Hooks/HookGitFacts.cs`, `src/KyberWeave.Arbiter/Hooks/HookPlanReader.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T25, T26
  - **Acceptance:** fix 10 and D44. The new id is added and none renumbered, and the rule count in the file header is updated (18 to 19, `default-rules.yml:1`). T27's row holds. `SquadAutonomousGatesInventoryTests` stays green — proven through a POLICY-RUN, per D57.
- **T28: Resume from the branch**
  - **Files:** `src/KyberWeave.Core/RunStart/RunStartRunner.cs`, `src/KyberWeave.Core/RunStart/RunStartPlanner.cs`, `src/KyberWeave.Cli/Commands/Run/RunStartCommand.cs`
  - **Required skills:** `csharp-dev`
  - **Depends on:** T25
  - **Acceptance:** D38. T28's row holds. T28 does not widen `--resume` to accept a pull-request number; that todo stays open (D56).
- **T29: Architect, conductor, docs-dev and session rules**
  - **Files:** `products/kyber-squad/agents/architect.md`, `products/kyber-squad/agents/architect/references/plan-authoring.md`, `products/kyber-squad/agents/product-owner.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `products/kyber-squad/agents/conductor/references/spec-path.md`, `products/kyber-squad/skills/delivery-execution/SKILL.md`, `products/kyber-squad/agents/docs-dev.md`, and the migration reports D58 adds in the same change: `products/kyber-squad/migration/architect.md`, `products/kyber-squad/migration/docs-dev.md`, `products/kyber-squad/migration/product-owner.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T26, T28
  - **Acceptance:** fixes 3 and 6. `plan-authoring.md:25` asks for path plus symbol, full test class names and exact commands, and for a `session` group. Prompts tell workers to commit with a `Task:` line. The conductor validates the file, pastes prompts unchanged and fills only the two slots. `git rm` joins the closed list. T29's row holds. The execution contract's current home is the `delivery-execution` skill; `agents/conductor/references/execution-and-review.md` no longer exists in this tree and is not recreated.
- **T30: Prove per-task commits do not hide scope drift**
  - **Files:** `tests/KyberWeave.Tests/Arbiter/ArbiterEvaluatorTests.cs`
  - **Required skills:** `test-dev`
  - **Depends on:** T25
  - **Acceptance:** `GitFacts.GetChangedPathsSinceSnapshot` is proven to include committed paths, so `KW-ARB-DIFF-001` still sees them. If it does not, return the failure rather than changing the rule. T30's row holds. (`GitFacts.cs:177-183` already documents that a moved `HEAD`'s committed diff counts, so a passing proof is the likely outcome; the row allows either.)
- **T31: Docs and closeout for slice 5**
  - **Files:** `docs/kyber-squad/architecture.md`, `docs/kyber-arbiter/architecture.md`, `docs/ci-pipelines/rule-reference.md`, `docs/adr/0053-kyber-arbiter-three-step-decision-gates.md`, `docs/adr/README.md`, `docs/plans/README.md`
  - **Required skills:** `docs-dev`
  - **Depends on:** T26, T27, T28, T29, T30
  - **Acceptance:** harvest, new ADR (0063, per the ADR disposition and D35), ADR 0053 amended, the plan index's policy text covers the task file, child plan archived, zero findings.

## Dependency graph

From the parent: T25 stands alone and unlocks T26, T28 and T30. T26 unlocks T27. T26 and T28 together unlock T29. T31 is last and depends on T26, T27, T28, T29 and T30. Workers run in parallel up to `review.max-concurrent-workers` (4) where file scopes allow; the slice ends with one council, then its closeout.

## Risks

- **Task file and plan disagree.** `KW-DOC-TASKS-002` and `-003` catch it. A digest that ignores status edits could miss an edit to prose outside the parsed fields, which is acceptable because the Arbiter reads the same fields (the parent's own risk row).
- **A prompt richer than the plan's task text** could trip `KW-ARB-SCOPE-002` (`adds-work`), because the decision model compares prompt and task. The architect writes both from the same content, and the plan's task text keeps the acceptance criteria.
- **Commit per task changes what the Arbiter sees.** T30 and D44 cover scope drift and lens applicability; `.agent_scratchpad` stays out of commits.
- **The plan-read rule cannot fire where dispatch hooks are unconfirmed** — Command Code is `dispatch-unconfirmed`, and OpenCode, Kilo and VS Code Copilot are unconfirmed (the parent's harness matrix). The fallback is `KW-DOC-TASKS-005` at validation, and the dispatch-coverage todo stays open.
- **The stacked-branch risk (D55).** This branch sits on slice 4's unmerged branch; the pull request targets slice 4's branch and is retargeted to `main` when slice 4 merges. Until then a conflict can produce a pull request GitHub runs no `pull_request` workflows on at all — measured twice on slice 2. Do not merge slice 4 into this branch and do not rebase onto `main`; push to the stacked branch only.
- **An edited agent body without an updated migration report fails the suite (D58).** The three reports update in the same change as their bodies; the references and the skill hold no migration digest.

## Out of scope

- The parent's other slices — each has its own child plan on its own branch and its own pull request. `KW-ARB-GATE-REPORT-001`, the third item in the parent's shared harvest row, is slice 8's (its T45).
- The `--resume` pull-request-number form: the todo slice 6 filed stays open (D56); T28 reads `Task:` commit lines and does not widen the form.
- Collapsing RED and GREEN into one dispatch. The adversarial split stays; fix 6 merges only same-role follow-on work into one session, never a RED with its GREEN.
- A size limit on plans (D3): regrowth is stopped by the digest and the validator, not by a limit.
- A shell for the architect, and the fact-finder agent ([issue 221](https://github.com/dpalfery/kyber-weave/issues/221) stays open).
- The per-harness shell hook for the full-run guard: that todo stays open.
- The `KW-ARB-MODE-001` boolean comparison noted in the parent's findings: reported, not fixed here. Every no-test task in this plan is a `docs-dev` task (T31), which is the parent's own mitigation.
- Fix 9, routing each request with the decision model: [issue 535](https://github.com/dpalfery/kyber-weave/issues/535).

## Verification gates

- Every task's row in the Test contract runs a named class set with a **non-zero test count**; a filter that matches nothing exits 8 and is treated as a wrong class name, not a pass. **No task or gate runs the full suite** — `scripts/proc-guard.sh run` refuses an unfiltered run from a dev agent with exit 89, and the full suite belongs to the review council's `test` gate and CI.
- `docs validate .` and `docs drift .` run after every plan save and after the docs task (T31), each at zero findings, in the tree the work runs in (the worktree protocol; the kw-docs server is bound to the canonical checkout and cannot see a file written in this worktree). `docs validate . --merge-ready` is **not** the check while this plan is open: `KW-DOC-LIFECYCLE-003` fails an open plan by design, and the pull request CI runs it, which is what archives this plan in this slice's pull request at closeout. `docs drift` needs a CodeGraph index in the tree it runs in: `doctor --ensure .` runs first in a fresh worktree.
- **Branch and title.** The `pr-naming` CI job gates this slice's pull request (verified on this branch: `ci.yml:115`, needed at `:1188`, with `pr-naming-recheck.yml` beside it). The delivery branch `factory/feat/conductor-run-speed-ready-tasks` already follows `<harness>/<type>/<slug>`; the pull request title follows `<type>(<scope>): <summary> [factory]` or the naming job fails it, and the pull request targets slice 4's branch, retargeted to `main` when slice 4 merges (D55).

## Review

This slice's pull request goes through **one `code-reviewer` council over its whole change**, run after the draft is pushed, with nothing else editing that tree while the council runs. No agent reviews a single task; each developer task ends with the worker's own `task-gate` run, and the council is the single end-of-run review, its command derived from the `code-review` skill with `--base` — never copied into this plan.

## Closeout

**Closed 2026-11-08 by `docs-dev`. Status: Archived.**

### Verification

Verified against this tree — branch `factory/feat/conductor-run-speed-ready-tasks`, head
`6e17c918` — by reading the delivered code and the canonical documents, not by trusting a task's
summary:

- **The task file and its validator.** `PlanTaskFile` and `PlanTaskFileValidator` live in
  `src/KyberWeave.Core.Docs/Validation/`, and the validator raises `KW-DOC-TASKS-001` malformed or
  unread older schema, `-002` plan/tasks/files/dependencies disagree, `-003` stale digest, `-004`
  prompt without a command or acceptance criteria, `-005` prompt that names a planning path, and
  `-006` (warning) Ready plan with no file. `DocsSchemaGate` constructs it for every `docs validate`
  run, not behind a flag, because the orchestrator has the docs tools and no shell.
- **The execution digest covers the execution.** `PlanDocumentParser.ExecutionDigest` hashes the mode,
  the task count, each task's id, title, text, files, dependencies and skills, the test contract
  rows, and the out-of-scope paths — so a status flip or a Decisions edit does not mark a file stale
  while a task edit does. The raw-text `Digest` is untouched and still hashes the whole file.
- **The merge-time half.** `OpenWorkValidator` raises `KW-DOC-LIFECYCLE-005` for a leftover task file
  and runs only under `--merge-ready`, walking the configured docs root and looking under the archive
  too, so a specification folder moved with its `tasks.json` is caught.
- **The rule.** `default-rules.yml:1` says nineteen rules, and `KW-ARB-PLANREAD-001` is the
  nineteenth (`:280`): a documentation-profile target answers `closeout`, a zero count answers
  `no-plan`, and a named planning path answers `names-plan`, which escalates. It reads the prompt
  after the routing header block is removed, through `PlanReadInstruction`, the same detector
  `KW-DOC-TASKS-005` uses, so the dispatch-time and authoring-time checks cannot drift apart.
- **The lens basis.** `HookGitFacts` fills `review.changed-paths` from
  `GitFacts.GetChangedPathsSinceBase`, and `KW-ARB-LENS-001`'s two skip branches now require the set to
  be non-empty as well as Markdown-only, so a committed tree no longer loses `correctness` and
  `test-adequacy`.
- **Resume.** `RunStartResumeReader` reads `Task: <id>` trailers from the resumed branch's commits and
  `RunStartCommand` prints the completed tasks and their results. `--resume` still accepts the branch
  form only (D56).
- **The agent text.** `plan-authoring.md:25` now asks for the `session` group, path plus symbol, full
  test class names and exact commands; `delivery-execution/SKILL.md:25-29` states the task file and
  the paste-and-fill rule; `docs-dev.md:43` and `:70` add `git rm` to its closed command list and to
  the plan-closeout steps.
- **The RED classes exist:** `PlanTaskFileValidatorTests`, `PlanExecutionDigestTests`,
  `RunStartResumeTests`, `Arbiter/ArbiterPlanReadRuleTests`, `Arbiter/ArbiterLensCleanTreeTests`.
- **Not claimed here.** This closeout did not run the whole suite; the run's council and CI are that
  evidence, and the two sweeps below re-ran only the classes these edits could move.

### No task file existed for this plan, and that is the correct state

There is no `docs/plans/2026-11-07-conductor-run-speed-ready-tasks.tasks.json`, and the archive
removed none. This slice introduced the mechanism, and a plan that introduces a mechanism is not
finalized by an architect that can use it: the mechanism is not live at its own finalization, so the
first plan to carry a file is a later slice's. `KW-DOC-TASKS-006` warned on this plan while it was
Ready, which was the intended advisory — it never fails a merge — and that warning retired with the
archive.

One Ready plan in the tree still has no task file after this closeout: slice 4's
`2026-11-07-conductor-run-speed-shared-execution.md`. That is expected and is not silenced here. Its
warning clears when it archives in its own pull request, which is blocked on the live Claude check the
owner must run. The removal duty (`git rm` in the archiving commit) starts with the plans delivered
after this slice.

### Deferred work recorded

- **T26's gap — the MCP route is unpinned for the task-file rules.** The six ids reach the conductor
  through `docs_validate_governed_document_schema`, and no test drives that tool over a Ready plan with
  no task file: the positive case rests on the shared call chain, which is a reading rather than a run.
  Filed as [mcp-docs-gate-unpinned-for-task-files.md](../../todo/mcp-docs-gate-unpinned-for-task-files.md).
- **T27's discovery — `KW-ARB-GATE-001` has the vacuous shape D44 fixed for the lenses.** The rule
  answers `not-applicable` when `review.changed-paths` exists and nothing intersects, so an empty set
  marks every path-scoped gate not applicable; D44 scoped the repair to the two lenses and left this
  one unfixed. Filed as
  [arbiter-gate-select-empty-changed-set.md](../../todo/arbiter-gate-select-empty-changed-set.md),
  because a tree the Arbiter cannot read would otherwise report every gate as not applicable.
- **D56 — the `--resume` pull-request form.** `run start --resume` still refuses a number with exit
  `2`; widening or narrowing it stays owned by
  [run-start-resume-refuses-a-pull-request-number.md](../../todo/run-start-resume-refuses-a-pull-request-number.md),
  filed by slice 6 and left open. T28 read `Task:` commit lines and did not widen a form it was not
  asked to change.

### Gates

After the archive move, from the repository root:

- `kyber-weave docs validate .` — 0 critical, 0 error, **1 warning**: `KW-DOC-TASKS-006` on slice 4's
  Ready plan, the known remaining item above. Zero findings for this plan.
- `kyber-weave docs drift .` — 0 findings.
- `kyber-weave docs validate . --merge-ready` — no closeout finding for this plan: `KW-DOC-LIFECYCLE-004`
  and `KW-DOC-LIFECYCLE-005` are both clear, and the mapping below is what cleared the archive gate.
  One error remains, `KW-DOC-LIFECYCLE-003` on slice 4's plan, which is open in this branch because
  this slice is stacked on slice 4 (D55) — not this plan's finding, and not fixable here.

The two test sweeps this closeout ran are recorded in the slice's pull request; neither ran the full
suite (ADR 0059).

## Closeout mapping

Every decision id in this plan's ledger — D2, D12, D13, D18, D19, D35, D38, D39, D44, AP1, D55, D56,
D57, D58 — and the Design harvest's two requirement-groups map to a destination below, or to a
recorded Documentation waiver. The harvest declares **H1** and **H2**; they land in four documents, so
the table records **H3** and **H4** for the two further destinations each group carries. The parent's
third harvest item, `KW-ARB-GATE-REPORT-001`, is slice 8's and is not harvested here.

Documentation-impact for every mapped row: `extends-component`. Impact components: KyberSquad,
KyberArbiter. Last reviewed: 2026-11-08. Status: archived.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D2 | [ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — the tracked task file, the plan as authority, and the rejected alternatives; [plans index policy](../../plans/README.md#plan-decisions-and-closeout-policy) — the file at authoring time and `git rm` at closeout | extends-component | KyberSquad | 2026-11-08 | archived |
| D12 | [plans index policy](../../plans/README.md#plan-decisions-and-closeout-policy) — one plan never spans several pull requests, and the task-file paragraphs that sit beside it | extends-component | KyberSquad | 2026-11-08 | archived |
| D13 | Documentation waiver: cross-slice build order and sequencing for one continuous run — a scheduling decision with no surviving architectural, operational or behavioural invariant. All five reorderings it names landed in their slices | extends-component | KyberSquad | 2026-11-08 | archived |
| D18 | [ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — the execution digest and what it covers; [kyber-squad architecture, the tracked task file](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) — why a status or Decisions edit is not stale and a task edit is | extends-component | KyberSquad | 2026-11-08 | archived |
| D19 | [ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — the six ids and why the check runs in the docs tools rather than behind a flag; [rule reference](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool) — each id's meaning, and `KW-DOC-LIFECYCLE-005` at merge | extends-component | KyberSquad | 2026-11-08 | archived |
| D35 | Documentation waiver: ADR numbering bookkeeping — which decisions take a record, resolved by writing ADR 0063 and amending ADR 0053. The records themselves carry the durable behaviour; the allocation note survives nowhere | extends-component | KyberSquad | 2026-11-08 | archived |
| D38 | [ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — resume from the branch and the `Task:` trailer; [kyber-squad architecture](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) — each task commits with `Task: <id>` and why "do not commit" was the defect | extends-component | KyberSquad | 2026-11-08 | archived |
| D39 | [ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — the specification form, `<spec-folder>/tasks.json`, removed before the folder moves; [plans index policy](../../plans/README.md#plan-decisions-and-closeout-policy) — the archive walk that catches it | extends-component | KyberSquad | 2026-11-08 | archived |
| D44 | [ADR 0053](../../adr/0053-kyber-arbiter-three-step-decision-gates.md) decision 9, amended in place — committed plus dirty against the review base, and an empty set is not Markdown-only; [kyber-arbiter architecture](../../kyber-arbiter/architecture.md#the-rules) — the same rule stated with the failure it repairs | extends-component | KyberArbiter | 2026-11-08 | archived |
| AP1 | Documentation waiver: execution-approval provenance, transient, with no surviving invariant | extends-component | KyberSquad | 2026-11-08 | archived |
| D55 | Documentation waiver: stacked-branch delivery sequencing for this one branch — branch from the unmerged slice, target its branch, retarget when it merges. The rule itself is already canonical in [plans index policy](../../plans/README.md#plan-decisions-and-closeout-policy), which this closeout left unchanged | extends-component | KyberSquad | 2026-11-08 | archived |
| D56 | [run start --resume refuses a pull request number](../../todo/run-start-resume-refuses-a-pull-request-number.md) — left open, unchanged: this slice did not widen the form, and the todo owns the decision and its pinning test | extends-component | KyberSquad | 2026-11-08 | archived |
| D57 | Documentation waiver: test-runner bookkeeping for a policy-project class. `SquadAutonomousGatesInventoryTests` lives in `tests/KyberWeave.RepoPolicy.Tests/` in namespace `KyberWeave.Tests`, so it is proven by a POLICY-RUN, and the page it pins holds no `KW-ARB-*` id; the rule it protects is recorded in [ADR 0053](../../adr/0053-kyber-arbiter-three-step-decision-gates.md) decision 23 and the [rule reference](../../ci-pipelines/rule-reference.md#shipped-rules--default-rulesyml) | extends-component | KyberArbiter | 2026-11-08 | archived |
| D58 | Documentation waiver: implementation bookkeeping for three migration reports whose `final-body-sha256` covers the agent bodies T29 edited. The reports are regenerated source, not documentation, and the behaviour those bodies carry is recorded in [kyber-squad architecture](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) and pinned by `SquadCanonicalContentTests` | extends-component | KyberSquad | 2026-11-08 | archived |
| H1 | [kyber-squad architecture, the tracked task file and the paste-and-fill dispatch](../../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) — the file's format, what a prompt carries, the two slots, the execution digest, the `Task:` trailer and the `session` group boundary; [plans index policy](../../plans/README.md#plan-decisions-and-closeout-policy) — the authoring and removal duties | extends-component | KyberSquad | 2026-11-08 | archived |
| H2 | [kyber-arbiter architecture, the rules](../../kyber-arbiter/architecture.md#the-rules) — `KW-ARB-PLANREAD-001` on `delegate` with its documentation-profile exemption and the routing-header removal, and the committed-plus-dirty lens basis; [ADR 0053](../../adr/0053-kyber-arbiter-three-step-decision-gates.md) decisions 9 and 23 | extends-component | KyberArbiter | 2026-11-08 | archived |
| H3 | [kyber-squad adoption guide, inlined task packets](../../kyber-squad/onboarding.md#inlined-task-packets-zero-plan-context-hunting) — what the conductor does with the file at dispatch (validates, pastes unchanged, fills two slots), the `session` group rule for operators, and the `Task:` commit trailer | extends-component | KyberSquad | 2026-11-08 | archived |
| H4 | [rule reference, plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool) and the [shipped Arbiter rules](../../ci-pipelines/rule-reference.md#shipped-rules--default-rulesyml) — the six `KW-DOC-TASKS-*` ids, `KW-DOC-LIFECYCLE-005`, and `KW-ARB-PLANREAD-001` with a one-line meaning each | extends-component | KyberArbiter | 2026-11-08 | archived |
| Deferred work: the MCP docs gate is unpinned for the task-file rules (T26) | [mcp-docs-gate-unpinned-for-task-files.md](../../todo/mcp-docs-gate-unpinned-for-task-files.md) | extends-component | DocGraph | 2026-11-08 | archived |
| Deferred work: `KW-ARB-GATE-001` reads an empty changed set as not applicable (T27) | [arbiter-gate-select-empty-changed-set.md](../../todo/arbiter-gate-select-empty-changed-set.md) | extends-component | KyberArbiter | 2026-11-08 | archived |
| Deferred work: the `--resume` pull-request form stays unimplemented (D56) | [run-start-resume-refuses-a-pull-request-number.md](../../todo/run-start-resume-refuses-a-pull-request-number.md), reached from D56 | extends-component | KyberSquad | 2026-11-08 | archived |
