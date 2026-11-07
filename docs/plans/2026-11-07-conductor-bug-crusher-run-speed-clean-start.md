---
id: plans/2026-11-07-conductor-bug-crusher-run-speed-clean-start
title: Conductor and bug-crusher run speed, slice 1 - clean start
doc-type: plan
status: current
component: KyberSquad
owner: dpalfery
created: 2026-11-07
last-reviewed: 2026-11-07
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberSquad, DocGraph, CI Pipelines, Distribution]
---

# Conductor and bug-crusher run speed, slice 1 - clean start

**Status: Ready.** Approved for execution 2026-11-07; decision AP1 records the approve-and-execute
gate carried by the conductor's `AUTONOMOUS_APPROVAL` input under resolved autonomy `autonomous`.
Per the convention the parent design established, the frontmatter stays `status: current` (the
frontmatter vocabulary is closed: current, draft, needs-review, superseded) while this status line
and the plan index row carry the Ready lifecycle state. **Development mode:** `test-first` (packet
input). **Autonomy:** `autonomous` (packet input; every open item decided and recorded below).
**Scale:** `extends-component`, components `KyberSquad`, `DocGraph`, `CI Pipelines`, `Distribution`.

This is a child plan, carved from the approved parent design for conductor and bug-crusher run
speed (branch `claude/docs/conductor-bug-crusher-run-speed`, deliberately not present in this
working tree, so it is named in prose only and never linked). It is complete on its own: it
restates only this slice's decision rows, design, harvest rows, ADR and tasks. It is the first of
the parent design's eight slices and covers three things: the one-line repair of the test that
fails on the owner's machine, the `run start` command, and the `stop-on-tool-failure` setting that
the command reads.

## Problem and goal

**Problem.** Runs of Kyber-Squad's entry points take hours. A conductor run took 2h48m with 1.63
workers running at once against a limit of 4; a bug-crusher run spent 2h14m on a 4-source-file fix
and never reached a review verdict. The parent design attacks this across eight slices; its
measured evidence is not in this repository, so those figures are the parent design's, not this
plan's own.

**Goal.** This slice, the first of the eight, fixes the start of the run. It fetches and branches
from the right remote base before anyone investigates or plans, it carries the harness name in
the branch and pull request title, it makes a broken tool a stop rather than a warning, and it
repairs the one always-failing test that blocks every slice's review council on the owner's
machine. The measured estimate for this slice is a small share of the parent design's 60 to 75
minute target.

## Decisions

The rows with ids D1 to D42 below are this slice's rows of the parent design's decision ledger,
restated verbatim so this child plan is complete on its own. AP1 records the approve-and-execute
gate for this child plan. Q1 is this plan's own row, settling where the T7 naming tests
physically live.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What happens when CodeGraph or `kw-docs` cannot be repaired? | A project setting `stop-on-tool-failure` in `.kyber-weave/kyber-weave.yml` defaults to stop. A code-editing run stops before dispatching workers. Autonomous runs stop too. `false` warns and continues. | The name is confirmed by D33. Read-only runs do not stop, and a repository with no governed docs skips the check. | user: explicit decision 2026-11-07 |
| D8 | Where does a run start from? | Every run fetches origin first and branches directly from the remote base branch, without checking out `main`. Resuming a plan or pull request keeps its branch. | Command in D21. | user: explicit decision 2026-11-07 |
| D9 | Is the harness name carried? | In every branch and pull request title, enforced in CI, with Dependabot exempt and `manual` for hand-made work. | Mechanism in D14 and D41. | user: explicit decision 2026-11-07 |
| D12 | Can one plan span several pull requests? | **No. Several plans, one per slice.** This file stays the single approved design and never merges while open. On approval it stays on its own branch. Each slice gets a child plan on the slice's branch, carved from its section here, and that plan is archived in the slice's pull request. The last slice's pull request brings this file in and archives it with a mapping to the children. | `OpenWorkValidator` raises `KW-DOC-LIFECYCLE-003`, and `ci.yml:733-757` runs `docs validate --merge-ready` on every pull request. A plan left open in `docs/plans/` fails CI, and the rule exists because a specification merged in stages never got its closeout. Its message says "keep it on its branch". Changing the rule was rejected for that reason. Child plans restate only their slice's decision rows, so each is complete on its own. Approval here carries to each child through `AUTONOMOUS_APPROVAL`. | autonomous: recommendation accepted |
| D13 | Does the requested build order hold against real dependencies? | **Five changes.** (a) Fix 5's one-line test repair moves to the very front, because a failed blocking gate forces `REQUEST_CHANGES`, so no slice's council can approve on the owner's machine until it lands. (b) The `stop-on-tool-failure` setting moves to the fix 8 slice, because fix 8's fetch failure reads it. (c) The `branch=` and `repo=` provenance fields move from fix 4 to fix 1, because the worktree rule needs it. (d) Fix 5's remaining work goes after fix 2, because it edits the shared execution skill fix 2 creates. (e) The `github-actions` standard is declared in the first slice, because `github-devops` refuses to write a workflow without it. Fixes 6 and 10 land with fix 3: fix 6 adds a field to the task file, and fix 10 shares fix 3's detector. Otherwise 8, then 1 to 5, then 11, then 7 stands. | Fix 7 depends on fix 11 (the `Completion` section) and on fix 3 (commands in the prompt). Fix 11 depends on fix 4's topic repair. | autonomous: recommendation accepted |
| D14 | How is a title edit checked without changing branch protection? | **Do not add `edited` to `ci.yml`.** Add a `pr-naming` job that `CI Summary` needs, reading the live title and branch through the GitHub API. Add a small `pr-naming-recheck.yml` on `pull_request: edited`, only when the title changed, that re-runs the `pr-naming` job of the latest CI run for that head. | `ci.yml:14` uses the default event types and `ci.yml:20-22` cancels in-progress runs. With `edited`, every description edit, which the council makes when it writes its verdict, would cancel and restart CI. Job-level skipping is no answer: a skipped job counts as passed, so a description edit could turn a red `CI Summary` green. GitHub documents write access to Actions for the job re-run endpoint. Whether `actions: write` on `GITHUB_TOKEN` is enough, and whether a re-run sees the old event payload, is not documented, so the design reads the live title and T7 checks it on a scratch pull request. Pull requests from forks get a read-only token, so a fork's title edit is checked only at its next push; that is a recorded limit. Self-gathered: GitHub REST and Actions docs, 2026-11-07. | autonomous: low-confidence, no authoritative source |
| D21 | What is the one run-start command? | `kyber-weave run start --harness <token> --type <type> --slug <slug> [--resume <branch-or-pr>] [--failing-version <version>] [--json]`. Exit 0 ready, 1 tool failure under stop, 2 refused input, 3 CodeGraph binary missing. Where the conductor has no shell it dispatches `github-devops` with the exact command and a `RUN-START: true` marker. | `KW-ARB-PLAN-001` escalates a `delegate` with no `PLAN_FILE`, so that dispatch would be blocked on every hook harness. `TriggerClassifier` classifies the marker as `investigate`, which keeps the roster check and `KW-ARB-READONLY-001`. | autonomous: recommendation accepted |
| D33 | What are the new settings called? | Top level: `stop-on-tool-failure` (boolean, absent means stop) and `onboarding` (`done` or `declined`, absent means not done). A small `run:` block: `base-branch` (absent means `origin/HEAD`) and `harness-names` (default true). Both top-level values publish in the Config Reg block the way `autonomy` does, only when set. | `AGENTS.md`'s Config Reg is the only place a portable agent can read a setting. An unknown value is refused the way `ParseAutonomy` refuses. | autonomous: recommendation accepted |
| D41 | How does a skill know its harness? | A `{{harness}}` placeholder in canonical conductor, bug-crusher and `create-pull-request` text, replaced once in the shared render pipeline with `SquadTargetCatalog.GetToken`. | The 13 `HarnessSpec` roots are distinct. A folder name fails (this Command Code run was in the `Devin` checkout), and so does an environment variable (`OPENCODE_CONFIG` was set in a Claude session). Limit: Antigravity's `.agents/skills` is also read by Pi, ZCode and Command Code, so T48 checks a co-deployment. | autonomous: low-confidence, no authoritative source |
| D42 | What is the name format? | Branch `[<owner>.<agent>.]<harness>/<type>/<slug>`, for example `commandcode/fix/arbiter-global-block-identity`. Title suffix `[<harness>]`, for example `fix(arbiter): qualify global block identity by target [commandcode]`. Tokens are the 13 catalogue tokens plus `manual`, with `agy` written as `antigravity`. Dependabot is exempt by the pull request's author. | A suffix keeps the conventional prefix first. Nothing in CI filters on branch names and nothing parses titles today. The tag lands in `main`'s history and the changelog, which lists subjects as they are. A branch on an open pull request can be renamed through the GitHub API, so no grandfathering is needed. | autonomous: recommendation accepted |
| AP1 | Is the Test contract approved, and may this slice execute? | Approved as written, as recommended. Execution follows the parent design's D12. | Not a planning decision. Resolved by the conductor carrying `AUTONOMOUS_APPROVAL: AP1` under resolved autonomy `autonomous`. | autonomous: recommendation accepted |
| Q1 | The T7 runner command names `KyberWeave.Tests.PrNamingScriptTests` and `KyberWeave.Tests.PrNamingWorkflowTests`, while T2 places the two source files under `tests/KyberWeave.RepoPolicy.Tests/`. Where do the classes physically live so the command matches? | The command stays exactly as stated in the Test contract. The two classes live in the policy project `tests/KyberWeave.RepoPolicy.Tests/` and keep the namespace `KyberWeave.Tests`, so the fully-qualified names in T7's POLICY-RUN command match as written. T2 owns the physical placement and confirms the command matches before its row closes. | `tests/KyberWeave.RepoPolicy.Tests/AGENTS.md` states the project's classes keep the `KyberWeave.Tests` namespace, "so a fully-qualified class name means the same test in either project". The product project `KyberWeave.Tests` and the policy project `KyberWeave.RepoPolicy.Tests` therefore both resolve the same fully-qualified name, and T7's POLICY-RUN runs the policy assembly that holds them. | autonomous: recommendation accepted |

## Investigation findings

**Discovery:** codegraph 1, docs 2, direct reads 7.

- `codegraph_explore` (1 call) over `PlanInventoryValidator` and `KW-DOC-LIFECYCLE-001`: the plan
  index rule is link-reachability only — a plan in `docs/plans/` must be reachable from the index
  by links inside that folder. No row shape is enforced, so the index row below follows the
  archived-row precedent.
- `docs_answer_question_from_repo_documentation` (2 calls, frontmatter and plan requirements):
  both returned the generic frontmatter section, not the plan doc-type specifics, so three narrow
  direct reads followed.
- Direct reads (7), each with a reason:
  1. `products/kyber-squad/agents/architect/references/plan-authoring.md` — the route reference
     for plan authoring and finalization, outside the governed corpus.
  2. `docs/plans/README.md` — the plan index, which plan authoring step 1 requires and which is
     this plan's second write target.
  3. `docs/documentation-ontology.md` — the doc answer stopped at the generic frontmatter table,
     so the doc-type closed sets and the per-doc-type required keys were read directly: `plan`
     requires `component`, and the closed status vocabulary is current, draft, needs-review,
     superseded.
  4. `docs/catalog.md` — the component list, to confirm `KyberSquad`, `DocGraph`,
     `CI Pipelines` and `Distribution` all exist as catalog components.
  5. `docs/ci-pipelines/rule-reference.md` — the lifecycle rule ids, to confirm what
     `KW-DOC-LIFECYCLE-001` and `KW-DOC-LIFECYCLE-003` check.
  6. `docs/archive/plans/2026-11-06-block-dev-agent-full-test-runs.md` — the format precedent for
     a recently archived plan (frontmatter keys, Decisions table shape, Test contract shape); the
     archive is excluded from the docs index.
  7. `tests/KyberWeave.RepoPolicy.Tests/AGENTS.md` — the policy test project's own instruction
     file, in neither index; its namespace rule settles Q1.

**Verified against the tree.**

- Preflight: `kw-docs___docs_get_source_repository_and_revision` returned
  `root=/Users/dave/git/personal/factory/kyber-weave`, `rev=decc903f`, clean, which matches this
  checkout.
- `docs/plans/README.md` has an empty Active Plans section ("No plans are active."); this plan's
  row replaces that empty state. Its frontmatter `last-reviewed` is bumped to 2026-11-07 in the
  same change.
- The parent design lives on branch `claude/docs/conductor-bug-crusher-run-speed` and is
  deliberately absent from this tree; no link is made to it, per the carving instruction and
  because the link checker would fail on it.
- The policy test project's classes keep the `KyberWeave.Tests` namespace
  (`tests/KyberWeave.RepoPolicy.Tests/AGENTS.md`), so T7's POLICY-RUN command with
  `-class KyberWeave.Tests.PrNamingScriptTests -class KyberWeave.Tests.PrNamingWorkflowTests`
  matches classes placed under `tests/KyberWeave.RepoPolicy.Tests/` (Q1).

## Design

### The run-start command

`run start` does this, before investigating or planning:

1. **Fetch** with `git fetch origin --prune`. Failure follows `stop-on-tool-failure`, because
   stale code is what cost the measured run.
2. **Find the base:** `run.base-branch` if set, otherwise `origin/HEAD`. Do not assume `main`.
3. **Compare builds.** For a defect, compare the failing binary's version with `HEAD` and
   `origin/<base>`, and print all three for the investigator's prompt.
4. **Choose where the work belongs.** New work, or a bug in the base or a shipped build: a new
   branch from `origin/<base>`. Resuming a plan or pull request: its existing branch, reporting
   if it is behind. A defect only on the current branch: that branch.
5. **Branch.** `git switch -c <branch> --no-track origin/<base>` when the checkout is clean and
   not in use. `git worktree add -b <branch> --no-track <path> origin/<base>` otherwise.
   `--no-track` matters: a fix branch that tracked `origin/main` let a bare `git push` aim at the
   wrong branch.
6. **Index and test the tools** are slice 2's work and are explicitly not in this slice.

`--json` prints base, branch, worktree, revision, versions and the server-alignment facts the
architect's packet needs. Exit codes: 0 ready, 1 tool failure under stop, 2 refused input, 3
CodeGraph binary missing.

Names follow D42. `kyber-weave run start` and `create-pull-request` fill them in, and CI rejects
a pull request without them (D14). Other repositories control this with `run.harness-names`, on
by default. The CI job is this repository's; a host adds its own.

### The harness token

A `{{harness}}` placeholder in canonical conductor, bug-crusher and `create-pull-request` text,
replaced once in the shared render pipeline with `SquadTargetCatalog.GetToken` (D41). One
replacement point covers agent bodies, skills and resources. The token is one of the 13 catalogue
tokens plus `manual`.

### The stop-on-tool-failure setting

A top-level boolean in `.kyber-weave/kyber-weave.yml`. Absent means stop (D1). A code-editing run
stops before dispatching workers; an autonomous run stops too. `false` warns and continues.
Read-only runs do not stop, and a repository with no governed docs skips the check. A missing
`stop-on-tool-failure` must mean stop in code, not only in the instruction text.

### The one-line test repair

`AnotherTargetIsDeployed_UnreadableState_ReportsNoOtherTargetWithoutThrowing` in
`tests/KyberWeave.Tests/ArbiterUninstallCommandTests.cs` never sets `_isolated.Value = true`, so
`ArbiterCommandComposition.HomeDirectory()` returns the real home. The fix is one line, and the
fixture already exists. This repair lands first in the whole parent design, because a failed
blocking gate forces a REQUEST_CHANGES verdict, so no slice's council can approve on the owner's
machine until it lands.

### The naming check in CI

A `pr-naming` job that `CI Summary` needs, reading the live title and branch through the GitHub
API, plus a small `pr-naming-recheck.yml` on `pull_request: edited` that only runs when the title
changed and re-runs the `pr-naming` job of the latest CI run for that head. `ci.yml` gains no
`edited`. Dependabot is exempt.

### The github-actions standard

`github-devops` refuses to write a workflow because no `github-actions` standard is declared in
this repository. The slice declares it, adapted from
`products/kyber-squad/standards/github-actions/README.md`, which exists in this tree.

## Design harvest

Declared scale: `documentation-impact: extends-component`, `impact-components: [KyberSquad,
DocGraph, CI Pipelines, Distribution]`.

| Design item | Target component | Target doc type | Target location |
|---|---|---|---|
| `run start`, its exit codes, branch and title format | Distribution | reference | `docs/cli-command-surface.md` |
| `stop-on-tool-failure` and the `run:` settings | DocGraph | reference | `docs/configuration.md` |
| PR naming job and recheck | CI Pipelines | runbook | `docs/ci-pipelines/workflows-runbook.md` |

## ADR for this slice

One new ADR at the next free number (from 0060), in `docs/adr/`, with its row in
`docs/adr/README.md`: **Runs start from the fetched remote and carry the harness name.** It
records D8, D9, D14, D21 and D42. The rejected alternatives to state: checking out `main` first,
detecting the harness from the folder name, detecting it from an environment variable, and
changing branch protection. The ADR is written by the slice's closeout, not now.

## Test contract

**Commands.** Everything runs through the watchdog from the repository root. No row runs an
unfiltered `dotnet test`, `review gates --all`, `fast-loop.sh --full` or `--scoped`. The council
and CI run the full suite. Check every run reports a non-zero test count, because a short class
name runs zero tests and exits 0.

- **PRODUCT-RUN** means: `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`, then `scripts/proc-guard.sh run -- dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll` followed by each `-class` named in the row.
- **POLICY-RUN** means: `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`, then `scripts/proc-guard.sh run -- dotnet tests/KyberWeave.RepoPolicy.Tests/bin/Release/net10.0/KyberWeave.RepoPolicy.Tests.dll` followed by each `-class`.
- When a worker passes `--artifacts-path <dir>`, the assembly is at `<dir>/bin/KyberWeave.Tests/release/KyberWeave.Tests.dll`.
- RED proofs run on a clean base checkout or isolated worktree, never with `git stash`.
- Test classes in `tests/KyberWeave.Tests/Arbiter/` are in namespace `KyberWeave.Tests.Arbiter`. Everything else named here is `KyberWeave.Tests`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/ArbiterUninstallCommandTests.cs` | `HOME=<scratch with a global Arbiter target>` before PRODUCT-RUN `-class KyberWeave.Tests.ArbiterUninstallCommandTests` | The test passes whatever the real home holds | Fails with a home that holds another global target | Passes with that home and with a blank one, assertion unchanged |
| T2 | Not applicable | Not applicable | RED authoring task; its proof is T3 to T7's rows | Each new class fails for the named reason | Not applicable |
| T3 | `ConfigRegTests`, `ReviewTaskGatesConfigTests`, new `RunSettingsConfigTests` | PRODUCT-RUN `-class KyberWeave.Tests.ConfigRegTests -class KyberWeave.Tests.RunSettingsConfigTests` | The settings parse, an unknown value is refused, and values publish in the Config Reg block only when set | Fails: keys unknown, no property line | Passes; `KW-CONFIG-REG-002` reports a stale block |
| T4 | New `RunStartPlannerTests`, `RunStartCommandTests`, and `Arbiter/ArbiterClassificationTests` | PRODUCT-RUN `-class KyberWeave.Tests.RunStartPlannerTests -class KyberWeave.Tests.RunStartCommandTests -class KyberWeave.Tests.Arbiter.ArbiterClassificationTests` | Branch format, `--no-track`, in-place versus worktree, resume keeps its branch, fetch failure follows the setting, exit codes, version compare output, `RUN-START: true` is not `delegate` | Fails: planner absent; marker classified `delegate` | Passes, using a real temporary git repository for the command tests |
| T5 | New `SquadHarnessTokenRenderTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadHarnessTokenRenderTests` | Each of the 13 renders carries its own token, and no rendered file holds `{{harness}}` | Fails: placeholder not replaced | Passes for all 13 |
| T6 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings; `github-actions` listed in the Config Reg block | Not applicable | Read-only check |
| T7 | New `PrNamingScriptTests`, `PrNamingWorkflowTests` | POLICY-RUN `-class KyberWeave.Tests.PrNamingScriptTests -class KyberWeave.Tests.PrNamingWorkflowTests` | Accepts and rejects the D42 shapes, Dependabot exempt, tokens match `SquadTargetCatalog`, `CI Summary` needs `pr-naming`, `ci.yml` has no `edited` | Fails: script and job absent | Passes, plus the scratch pull request live check |
| T8 | `SquadCanonicalContentTests`, `SquadWorktreeMcpContractTests` | PRODUCT-RUN `-class KyberWeave.Tests.SquadCanonicalContentTests -class KyberWeave.Tests.SquadWorktreeMcpContractTests` | Both entry points name `run start` and the shell-or-dispatch route | Fails: text absent | Passes |
| T9 | Not applicable | Not applicable | `docs_validate` and `docs_drift` zero findings | Not applicable | Read-only check |

**Open question for the T2 implementation worker (Q1).** The T7 runner command names
`KyberWeave.Tests.PrNamingScriptTests` and `KyberWeave.Tests.PrNamingWorkflowTests`, while T2
places the two source files under `tests/KyberWeave.RepoPolicy.Tests/`. The product test project
is `KyberWeave.Tests` and the policy project is `KyberWeave.RepoPolicy.Tests`. T7's runner command
stays exactly as stated above; T2 resolves where the classes physically live so that T7's command
actually matches them. Q1 records the evidence that the policy project's classes keep the
`KyberWeave.Tests` namespace, so the command matches classes placed under
`tests/KyberWeave.RepoPolicy.Tests/`; T2 still confirms this before its row closes.

## Tasks

### T1: Repair the test that reads the real home folder

- **Files:** `tests/KyberWeave.Tests/ArbiterUninstallCommandTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** `AnotherTargetIsDeployed_UnreadableState_ReportsNoOtherTargetWithoutThrowing` sets `_isolated.Value = true`, as the class's other isolated tests do. T1's row holds. Report any other test in the class that reaches the real home.

### T2: RED for clean start

- **Files:** `tests/KyberWeave.Tests/RunSettingsConfigTests.cs`, `tests/KyberWeave.Tests/ConfigRegTests.cs`, `tests/KyberWeave.Tests/RunStartPlannerTests.cs`, `tests/KyberWeave.Tests/RunStartCommandTests.cs`, `tests/KyberWeave.Tests/Arbiter/ArbiterClassificationTests.cs`, `tests/KyberWeave.Tests/SquadHarnessTokenRenderTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/PrNamingScriptTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/PrNamingWorkflowTests.cs`, `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`
- **Required skills:** `test-dev`
- **Depends on:** none
- **Acceptance:** every row T3 to T8 is written and fails for the reason it names. No product file changes. Command tests use real temporary git repositories.

### T3: Settings: `stop-on-tool-failure` and `run:`

- **Files:** `src/KyberWeave.Core/Configuration/KyberWeaveYamlDocument.cs`, `src/KyberWeave.Core/Configuration/ConfigRegConfig.cs`, `src/KyberWeave.Core/Configuration/ConfigRegConfigLoader.cs`, `src/KyberWeave.Core.Docs/Scaffolding/ConfigRegMarkdown.cs`, `src/KyberWeave.Core.Docs/Validation/ConfigRegValidator.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T2
- **Acceptance:** D33. A missing `stop-on-tool-failure` means stop in code, not only in text. T3's row holds.

### T4: The `run start` command

- **Files:** `src/KyberWeave.Core/RunStart/RunStartPlanner.cs` (new, pure decisions), `src/KyberWeave.Core/RunStart/RunStartRunner.cs` (new, git through `IProcessExecutor`), `src/KyberWeave.Cli/Commands/Run/RunStartCommand.cs` (new), `src/KyberWeave.Cli/Commands/Run/RunStartSettings.cs` (new), `src/KyberWeave.Cli/Program.cs`, `src/KyberWeave.Core/Arbiter/Facts/TriggerClassifier.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T2, T3
- **Acceptance:** the six steps under the run-start design except tool testing, which slice 2 adds. D21 and D42. `--json` prints base, branch, worktree, revision, versions and the server-alignment facts the architect's packet needs. T4's row holds, and `ProcessSpawnSitesTests` stays green.

### T5: Harness token in the render pipeline

- **Files:** `src/KyberWeave.Core.Squad/Rendering/SquadHarnessRenderer.cs`
- **Required skills:** `csharp-dev`
- **Depends on:** T2
- **Acceptance:** D41. One replacement point covers agent bodies, skills and resources. T5's row holds.

### T6: Declare the `github-actions` standard

- **Files:** `.kyber-weave/kyber-weave.yml`, `AGENTS.md`, `docs/standards/github-actions/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** none
- **Acceptance:** `github-actions` joins `ontology.technologies`. The standard comes from `products/kyber-squad/standards/github-actions/README.md`, adapted to this repository's rules. The Config Reg block lists it, and only the two lines change. T6's row holds.

### T7: Naming check in CI

- **Files:** `scripts/check-pr-name.sh` (new), `.github/workflows/ci.yml`, `.github/workflows/pr-naming-recheck.yml` (new)
- **Required skills:** `github-devops`
- **Depends on:** T2, T6
- **Acceptance:** D14 and D42. `pr-naming` has `pull-requests: read`, skips on non-pull-request events, and `CI Summary` needs it and reports it. Actions are pinned by digest like the rest. `ci.yml` gains no `edited`. Live check on a scratch pull request: a bad title fails, a corrected title turns `CI Summary` green through the recheck, and a description edit starts nothing; if `actions: write` proves insufficient, report that and leave the recheck's own failure visible. T7's row holds.

### T8: Entry points use `run start`

- **Files:** `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/conductor/references/intake-path.md`, `products/kyber-squad/agents/conductor/references/plan-path.md`, `products/kyber-squad/skills/bug-crusher/SKILL.md`, `products/kyber-squad/agents/bug-crusher-investigator.md`, `products/kyber-squad/agents/github-devops.md`, `products/kyber-squad/skills/create-pull-request/SKILL.md`, `CONTRIBUTING.md`
- **Required skills:** `docs-dev`
- **Depends on:** T4, T5
- **Acceptance:** a run fetches and branches before investigating or planning. The shell-or-dispatch route resolves the `conductor.md:62` conflict. The three versions reach the investigator's prompt. The title and branch rules are in `create-pull-request`. `CONTRIBUTING.md` no longer names `develop`. T8's row holds.

### T9: Docs and closeout for slice 1

- **Files:** `docs/cli-command-surface.md`, `docs/configuration.md`, `docs/ci-pipelines/workflows-runbook.md`, `docs/adr/README.md`, `docs/plans/README.md`
- **Required skills:** `docs-dev`
- **Depends on:** T3, T4, T5, T6, T7, T8
- **Acceptance:** harvest for this slice, the first new ADR, child plan archived. `docs validate --merge-ready` and `docs drift` report zero findings.

## Dependency graph

T1 stands alone. T2 and T6 stand alone. T3 depends on T2. T4 depends on T2 and T3. T5 depends on
T2. T7 depends on T2 and T6. T8 depends on T4 and T5. T9 depends on T3, T4, T5, T6, T7 and T8.

## Risks

- A stop blocks work on a machine with a broken tool; that is the owner's decision, `false` is
  the escape, and the failure names the fix.
- Fetch failure now stops the run, so a network outage stops a run that could otherwise proceed.
- The naming job reads the live title through the GitHub API, and a pull request from a fork gets
  a read-only token, so a fork's title edit is checked only at its next push; that is a recorded
  limit.
- `actions: write` on `GITHUB_TOKEN` may prove insufficient for the re-run endpoint, which is not
  documented; T7 checks it on a scratch pull request and reports rather than guessing.

## Out of scope

The parent design's other seven slices. Indexing and testing the tools at run start. Any change to
model assignment or model profiles. Collapsing RED and GREEN into one dispatch. A size limit on
plans. A shell for the architect. The per-harness shell hook for the full-run guard. Repairing the
`KW-ARB-MODE-001` boolean comparison, which is reported rather than fixed. Widening `context7`
grants.

## Verification gates

- Each task's row in the Test contract, with a non-zero test count. No task runs the full suite.
- `docs validate` and `docs drift` after every plan save and every docs task, with provenance
  `root=` equal to the checkout.
- This slice: a `task-gate` per developer task, one review council with `--council`, CI on the
  pull request, and `docs validate --merge-ready` and `docs drift` after this child plan is
  archived.
- After this slice, a scratch pull request proves the naming job.

## Review

This slice's pull request goes through one `code-reviewer` council over its whole change, after
the draft is pushed, with nothing else editing that tree. The verdict goes in the pull request
description.

## Closeout

`docs-dev` closes this child plan in this slice's pull request: it harvests this slice's Design
harvest rows, writes the new ADR described above with its row in `docs/adr/README.md`, archives
this plan with `git mv` into `docs/archive/plans/`, moves its row to Archived Plans in
`docs/plans/README.md`, and runs `docs validate . --merge-ready` and `docs drift .`.

## Rules that stay

- Do not check out `main` first. A checkout would carry another session's edits or refuse to run.
- Do not use `git stash` for RED proofs.
- Rule ids are permanent: add new ids, never reuse or renumber.
