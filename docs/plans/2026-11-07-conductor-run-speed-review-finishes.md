---
id: plans/2026-11-07-conductor-run-speed-review-finishes
title: Review finishes - one gate pass, one verdict
doc-type: plan
status: current
component: ReviewCouncil
owner: dpalfery
created: 2026-11-07
last-reviewed: 2026-11-07
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [ReviewCouncil, DocGraph, Distribution]
---

# Review finishes: one gate pass, one verdict

**Status: Ready and approved.** Approved for execution on the conductor's approve-and-execute gate (decision AP1, carried by the conductor's `AUTONOMOUS_APPROVAL: AP1` under resolved autonomy `autonomous`). **Development mode:** `test-first` (packet input). **Autonomy:** `autonomous` (packet input; every open item decided by recommendation and recorded in Decisions). **Scale:** `extends-component`, components `ReviewCouncil`, `DocGraph`, `Distribution`.

This is a child plan of the approved parent design for the conductor run-speed review work, which lives on branch `claude/docs/conductor-bug-crusher-run-speed` and is deliberately not in this tree, so it is not linked here. Under that design's D12, each slice gets one child plan on the slice's branch, carved from the parent's section and complete on its own: this plan restates only this slice's decision rows, design, harvest rows, ADR disposition and tasks. This is slice 6, "Review finishes", tasks T32 to T35.

## Problem and goal

**Problem.** A review council that never reaches a verdict is the most expensive failure in this system. In the measured run the reviewer ran its gates four times: once with `--all` and no `--council`, once with the full suite allowed, once as a direct test run, and once scoped, and it was killed while the fourth ran. The plan named the wrong command, `--all` removed the path filtering so the TypeScript gates ran in a fresh worktree with no `node_modules`, and the scoped run took as long as the unscoped one. A separate council dispatched against this repository repeated the behaviour: it ran for over half an hour without a verdict, and its owner stopped it.

**Goal.** This slice makes the review finish in one pass. The reviewer runs its own command once, with `--base` and `--council`; a refused full-suite gate stops the run at once instead of after every other gate has spent its time; a gate whose dependency is missing reports that fact instead of reading as a code failure; and the reviewer can re-check exactly one gate instead of the whole suite. Its output is a verdict, not a pile of gate runs.

## Decisions

Rows D12, D13, D15, D16 and D17 are this slice's rows from the parent design's decision ledger, copied verbatim per the parent's own D12 carve rule. In D12, "this file" means the parent design file on its branch, not this child plan. AP1 records the conductor's approval of this plan's Test contract and execution; D18 records the one scope adjustment this slice carries.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D12 | Can one plan span several pull requests? | **No. Several plans, one per slice.** This file stays the single approved design and never merges while open. On approval it stays on its own branch. Each slice gets a child plan on the slice's branch, carved from its section here, and that plan is archived in the slice's pull request. The last slice's pull request brings this file in and archives it with a mapping to the children. | `OpenWorkValidator` raises `KW-DOC-LIFECYCLE-003`, and `ci.yml:733-757` runs `docs validate --merge-ready` on every pull request. A plan left open in `docs/plans/` fails CI, and the rule exists because a specification merged in stages never got its closeout. Its message says "keep it on its branch". Changing the rule was rejected for that reason. Child plans restate only their slice's decision rows, so each is complete on its own. Approval here carries to each child through `AUTONOMOUS_APPROVAL`. | autonomous: recommendation accepted |
| D13 | Does the requested build order hold against real dependencies? | **Five changes.** (a) Fix 5's one-line test repair moves to the very front, because a failed blocking gate forces `REQUEST_CHANGES`, so no slice's council can approve on the owner's machine until it lands. (b) The `stop-on-tool-failure` setting moves to the fix 8 slice, because fix 8's fetch failure reads it. (c) The `branch=` and `repo=` provenance fields move from fix 4 to fix 1, because the worktree rule needs them. (d) Fix 5's remaining work goes after fix 2, because it edits the shared execution skill fix 2 creates. (e) The `github-actions` standard is declared in the first slice, because `github-devops` refuses to write a workflow without it. Fixes 6 and 10 land with fix 3: fix 6 adds a field to the task file, and fix 10 shares fix 3's detector. Otherwise 8, then 1 to 5, then 11, then 7 stands. | Fix 7 depends on fix 11 (the `Completion` section) and on fix 3 (commands in the prompt). Fix 11 depends on fix 4's topic repair. | autonomous: recommendation accepted |
| D15 | Should a refused full-suite gate stop the whole run? | **Only under `--all`.** A refused gate under `--all` exits at once with no gate started. A scoped call without `--council` keeps today's behaviour. | `code-review/SKILL.md:251` and [ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) make the pre-push self-review rely on "the other gates still run" (`AGENTS.md:80`). An unconditional stop would leave it no evidence. The measured waste was the `--all` call. Amends ADR 0059 in place. | autonomous: recommendation accepted |
| D16 | How does the reviewer re-check one gate? | New repeatable `review gates --gate <id>`. The reviewer reads `artifacts/gates.json` and re-runs only the gate it needs, with `--council` when that gate is full-suite. | `ReviewSettings.cs` has no such flag, so "re-run only the gate it needs" could not be followed. | autonomous: recommendation accepted |
| D17 | How is a gate with a missing dependency reported? | New optional gate key `requires-paths`. When a listed path is absent the gate does not start and reports `KW-REVIEW-033` with the install hint, and stays a blocking failure. | It must not read as a code failure, and must not silently pass. Set on the dash gates (`dash/node_modules`). The reviewer installs the dependency and re-runs that gate with `--gate`. | autonomous: low-confidence, no authoritative source |
| AP1 | Is the Test contract approved, and may this slice execute? | Approved as written, as recommended. Execution follows the parent design's D12. | Not a planning decision. Resolved by the conductor carrying `AUTONOMOUS_APPROVAL: AP1` under resolved autonomy `autonomous`. | autonomous: recommendation accepted |
| D18 | Which part of T34's file scope is deferred, and why? | `products/kyber-squad/skills/delivery-execution/SKILL.md` is deferred to slice 4, which creates that file when it moves the shared execution contract out of the conductor's references. T34 covers every other file in its list, and the review instructions land in `products/kyber-squad/skills/code-review/SKILL.md`, `products/kyber-squad/agents/code-reviewer.md`, `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/bug-crusher.md` and `products/kyber-squad/skills/bug-crusher/SKILL.md`. When slice 4 creates the shared skill, it carries the review instructions into it. | The parent's decision D13(d) orders fix 5's remaining work after fix 2, because it edits the shared execution skill fix 2 creates. The owner prioritised this slice ahead of slice 4, so the file that does not yet exist is deferred rather than the slice. The council fixes this slice carries - the `--all` stop, the single `--gate` re-check and the missing-dependency report - do not depend on that file. | autonomous: recommendation accepted |

## Investigation findings

**Why this slice runs early, out of the parent's slice order.** The measured run's reviewer ran its gates repeatedly and never reached a verdict, and a council dispatched against this repository repeated that behaviour. The owner prioritised slice 6 ahead of the parent's slice order for that reason.

**Parent context.** The parent design lives on branch `claude/docs/conductor-bug-crusher-run-speed` and is not in this working tree, so this plan links to nothing on that branch. The parent numbers its changes as fixes; "fix 5" is the reviewer-instructions change whose remaining work is this slice, so T34's acceptance line "fix 5's list" means T34's file list here (minus the file D18 defers).

**Verified against this tree.**

- Every file T33 and T34 names exists in this tree except two, both expected: `products/kyber-squad/skills/delivery-execution/SKILL.md` does not exist because slice 4 creates it (D18 defers it), and `tests/KyberWeave.Tests/ReviewGatePrerequisiteTests.cs` does not exist because T32 creates it.
- `tests/KyberWeave.RepoPolicy.Tests/ReviewGateFullSuiteConfigTests.cs` already exists; T32 extends it rather than creating it (see the Test contract notes).
- [ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) exists at `docs/adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md`; this slice amends it in place rather than creating a new ADR.
- The plan index (`docs/plans/README.md`) held no active plans before this one; the row added alongside this plan is its only active row.

**Discovery:** codegraph 0, docs 2, direct reads 3. No code question was open: the parent supplies every code fact and this planning step changes no code, so CodeGraph was not consulted. The two docs calls were the kw-docs provenance check and a query on the plan doc-type frontmatter contract. The three direct reads are the architect's plan-authoring reference (`products/kyber-squad/agents/architect/references/plan-authoring.md`, not in the docs corpus), the plan index (`docs/plans/README.md`, read to add this plan's row), and the archived sibling plan `2026-11-06-block-dev-agent-full-test-runs.md` (frontmatter and section precedent; `docs/archive/` is excluded from the docs server). File-existence checks of the task-named paths used Glob, not indexed reads.

## Design

### One gate run, then one gate

- Plans stop naming review commands. The reviewer uses its own: `--base <ref> --council`.
- Fail fast under `--all`: a refused gate under `--all` exits at once with no gate started. A scoped call without `--council` keeps today's behaviour, because the pre-push self-review relies on the other gates still running and an unconditional stop would leave it no evidence.
- A missing dependency is reported as such. A new optional gate key `requires-paths` names the paths a gate needs. When a listed path is absent the gate does not start, reports `KW-REVIEW-033` with the install hint, and stays a blocking failure. It must not read as a code failure and it must not silently pass. The dash gates set it on `dash/node_modules`.
- One suite run, then `--gate`: a new repeatable `review gates --gate <id>` re-runs exactly one gate, with `--council` when that gate is a full-suite gate. The reviewer reads `artifacts/gates.json` and re-runs only the gate it needs.

`KW-REVIEW-033` already means "a gate never started - its program could not start, its working directory does not exist, or a full-suite gate was refused without `--council`". A missing required path is the same class of fact, so this slice extends that rule id rather than duplicating it. No new rule id is introduced, and no existing one is renumbered: rule ids are permanent (`KW-*`).

### Review runs alone, last, in the foreground

No docs work and no edits happen in that tree during the review. In the measured run the main session edited two source files in the reviewed tree while the review was running.

### Where the reviewer has no agent tool

On a harness where the reviewer cannot start agents, it stops after its gates with `STATUS: COUNCIL_SEATS_NEEDED`, listing the lenses and the scope. The main session runs the seats in parallel and re-invokes the reviewer with their output. In-process lenses stay the fallback when the main session cannot either.

### The reviewer and plan instructions

- `delegates-to` on the conductor and on bug-crusher gains `review-lens` and `review-triage`, used only on `COUNCIL_SEATS_NEEDED`.
- The documentation-impact review, and any edit it causes, finish BEFORE the council starts.

## Design harvest

| Design item | Target component | Target doc type | Target location |
|---|---|---|---|
| `--gate`, `requires-paths`, fail-fast scope, `COUNCIL_SEATS_NEEDED` | ReviewCouncil | architecture | `docs/code-review/architecture.md`, `docs/configuration.md#review` |
| `review gates --gate <id>` as a CLI option | Distribution | reference | `docs/cli-command-surface.md` |

## ADR disposition

No new ADR. This slice AMENDS [ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) in place: it records D15, D16 and D17, keeping the ADR's existing structure and adding what these three decisions decide. The ADR's own file is in T35's file list; the in-place amendment, with the ADR's index row already carrying it, happens in T35.

## Test contract

**Commands.** Everything runs through the watchdog from the repository root. No row runs an unfiltered `dotnet test`, `review gates --all`, `fast-loop.sh --full` or `--scoped`. The council and CI run the full suite. Check every run reports a non-zero test count, because a short class name runs zero tests and exits 0.

- **PRODUCT-RUN** means: `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`, then `scripts/proc-guard.sh run -- dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll` followed by each `-class` named in the row.
- **POLICY-RUN** means: `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`, then `scripts/proc-guard.sh run -- dotnet tests/KyberWeave.RepoPolicy.Tests/bin/Release/net10.0/KyberWeave.RepoPolicy.Tests.dll` followed by each `-class`.
- When a worker passes `--artifacts-path <dir>`, the assembly is at `<dir>/bin/KyberWeave.Tests/release/KyberWeave.Tests.dll`.
- RED proofs run on a clean base checkout or isolated worktree, never with `git stash`.
- Test classes in `tests/KyberWeave.Tests/Arbiter/` are in namespace `KyberWeave.Tests.Arbiter`. Everything else named here is `KyberWeave.Tests`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T32 | Not applicable | Not applicable | RED authoring task; proof in T33's row | New rows fail for the named reason | Not applicable |
| T33 | `ReviewFullSuiteGuardTests`, `ReviewGatesCouncilCommandTests`, new `ReviewGatePrerequisiteTests`, `ReviewGateFullSuiteConfigTests` | PRODUCT-RUN `-class KyberWeave.Tests.ReviewFullSuiteGuardTests -class KyberWeave.Tests.ReviewGatesCouncilCommandTests -class KyberWeave.Tests.ReviewGatePrerequisiteTests`, and POLICY-RUN `-class KyberWeave.Tests.ReviewGateFullSuiteConfigTests` | `--all` with a refusal starts nothing and exits 1; scoped still runs the rest; `--gate` selects; a missing path reports `KW-REVIEW-033` and stays blocking | Fails: gates run; no `--gate` | Passes with no full run started |
| T34 | `SquadCanonicalContentTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadCanonicalContentTests` | Plans name no review command; reviewer runs once and uses `--gate`; review runs alone; `COUNCIL_SEATS_NEEDED` is routed | Fails: old text | Passes |
| T35 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings | Not applicable | Read-only check |

Two facts about the runner commands, so the implementation worker is not surprised:

- The policy project's classes keep the `KyberWeave.Tests` namespace, so `KyberWeave.Tests.ReviewGateFullSuiteConfigTests` names the same test whether it lives in the policy project or the product project. The T32 authoring task places the source files where the plan lists them, and the T33 row's runner command must actually match where the classes live.
- `ReviewGateFullSuiteConfigTests` already exists in `tests/KyberWeave.RepoPolicy.Tests/`. T32 extends it rather than creating it.

## Tasks

### T32: RED for the review changes

- **Files:** `tests/KyberWeave.Tests/ReviewFullSuiteGuardTests.cs`, `tests/KyberWeave.Tests/ReviewGatesCouncilCommandTests.cs`, `tests/KyberWeave.Tests/ReviewGatePrerequisiteTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/ReviewGateFullSuiteConfigTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** rows T33 and T34 are written and fail for the reason they name.

### T33: `--all` stop, `--gate`, `requires-paths`

- **Files:** `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`, `src/KyberWeave.Cli/Commands/Review/ReviewSettings.cs`, `src/KyberWeave.Core/Review/GateRunner.cs`, `src/KyberWeave.Core/Review/ReviewModel.cs`, `src/KyberWeave.Core/Configuration/ReviewConfig.cs`, `src/KyberWeave.Core/Configuration/ReviewConfigLoader.cs`, `src/KyberWeave.Core/Configuration/ReviewYamlSection.cs`, `.kyber-weave/kyber-weave.yml`
- **Required skills:** `csharp-dev`
- **Depends on:** T32
- **Acceptance:** D15, D16, D17. A not-started gate has a result the verdict treats as failed, and the report separates it from a code failure. T33's row holds.

### T34: Reviewer and plan instructions

- **Files:** `products/kyber-squad/agents/code-reviewer.md`, `products/kyber-squad/skills/code-review/SKILL.md`, `products/kyber-squad/agents/architect/references/plan-authoring.md`, `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/bug-crusher.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`
- **Required skills:** `docs-dev`
- **Depends on:** T33
- **Acceptance:** fix 5's list. `delegates-to` on conductor and bug-crusher gains `review-lens` and `review-triage`, used only on `COUNCIL_SEATS_NEEDED`. The docs-impact review and any edit finish before the council starts. T34's row holds. The shared execution skill is slice 4's file and does not exist yet; the review instructions land in the files that do, and slice 4 carries them into the skill when it creates it.

### T35: Docs and closeout for slice 6

- **Files:** `docs/code-review/architecture.md`, `docs/configuration.md`, `docs/cli-command-surface.md`, `docs/adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md`, `AGENTS.md`, `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T33, T34
- **Acceptance:** harvest, ADR 0059 amended, `AGENTS.md:78-80` still true and now names the `--all` stop, child plan archived, zero findings.

## Dependency graph

T32 stands alone. T33 depends on T32. T34 depends on T33. T35 depends on T33 and T34.

## Risks

- A fail-fast under `--all` could leave a caller with no evidence from the other gates, which is why the stop is scoped to `--all` alone and a scoped call keeps today's behaviour.
- A `--gate` re-run that selects a full-suite gate without `--council` must be refused the same way a normal full-suite run is.
- A missing dependency reported as a blocking failure blocks on the environment rather than on the change, which is intended but must be legible in the report: the verdict has to separate "the gate never started" from "the gate ran and failed". The dash gates' `requires-paths` means a contributor without `dash/node_modules` sees a clear install hint rather than a confusing failure.

## Out of scope

The parent's other seven slices. The shared execution skill, which slice 4 creates. Any change to model assignment or model profiles. Collapsing RED and GREEN into one dispatch. A size limit on plans. A shell for the architect. The per-harness shell hook for the full-run guard. The `KW-ARB-MODE-001` boolean comparison, which is reported rather than fixed. Widening `context7` grants.

## Verification gates

Each task's row in the Test contract, with a non-zero test count. No task runs the full suite. `docs validate` and `docs drift` after every plan save and every docs task, with provenance `root=` equal to the checkout. This slice: a `task-gate` per developer task, and `docs validate . --merge-ready` and `docs drift .` after this child plan is archived. After this slice, a council reaches a verdict in one pass on a real change.

## Review

This slice's pull request goes through one `code-reviewer` council over its whole change, after the draft is pushed, with nothing else editing that tree. The verdict goes in the pull request description.

## Closeout

`docs-dev` closes this child plan in this slice's pull request: it harvests this slice's Design harvest rows, amends ADR 0059 in place with its rows already in `docs/adr/README.md`, archives this plan with `git mv` into `docs/archive/plans/`, moves its row to Archived Plans in `docs/plans/README.md`, and runs `docs validate . --merge-ready` and `docs drift .`.
