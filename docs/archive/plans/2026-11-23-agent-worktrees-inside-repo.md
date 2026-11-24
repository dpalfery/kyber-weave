---
id: plans/2026-11-23-agent-worktrees-inside-repo
title: Run worktrees inside the repository, and a council gate step that fails fast
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-23
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components:
  - KyberSquad
  - ReviewCouncil
  - DocGraph
---

# Run worktrees inside the repository, and a council gate step that fails fast

**Status: Archived, 2026-11-24.**

## Request

Verbatim, as the dispatch carried it:

> Feature request (owner David approved this design on 2026-11-23; plan it, then deliver per the conductor workflow).
>
> Problem: on 2026-11-23 an opencode conductor run's review-council gate step took 21 min, ~17 of it setup reruns. Root cause: run worktrees are created as a sibling of the repo (`RunStartRunner.WorktreePathFor` → `<repo>.worktrees/<stamp>-<branch>`), so sub-agents treat them as outside the session folder. Our opencode renderer emits `external_directory: deny`; Kilo, Claude Code, Codex, Cursor, Devin, Command Code also block outside folders in unattended runs (docs research, not tested). The gate worker was refused, made its own empty worktree (no npm packages), ran the full gate suite 3 extra times (missing dash, then dash/web node_modules, then without --out).
>
> Agreed changes (product-level, every host benefits):
> 1. Run worktrees live inside the repo at `.agent_worktrees/<stamp>-<branch>` (one neutral folder for all harnesses; not per-harness folders — Codex keeps `.codex`/`.agents` read-only). Change `WorktreePathFor` and anything documenting the sibling layout.
> 2. The folder is git-ignored both ways: run start adds `.agent_worktrees/` to `.git/info/exclude` (idempotent, local, before creating the worktree); the kw-onboarding skill adds it to the shared `.gitignore`.
> 3. `doctor` check: fail if any CodeGraph-indexed path starts with `.agent_worktrees/`. (Tested with codegraph 1.6.2: nested git worktrees are never indexed, ignored folders are not indexed; plain copied folders are indexed unless ignored. Live repo: 0 of 2895 indexed files are under the 7 existing nested worktrees.)
> 4. Worker/reviewer prompts: if a folder is refused, stop and report; never create your own worktree.
> 5. `review gates`: before any gate starts, check every admitted gate's `requires-paths`; if any is missing on a `--council` or `--all` run, refuse the whole run in seconds listing each missing path and its fix. Scoped/pre-push runs keep today's behaviour. Add `dash/web/node_modules` to this repo's gates that need it.
> 6. `review gates --council` without `--out` is refused (`review seats` already prints the full command with `--out`, see `ReviewCycleView.GatesCommand`).
> 7. Optional, after a trial run: mark independent gates `concurrent` and raise `review.parallelism` (the runner already supports both). Risk: coverlet instruments `bin/Release` in place, so concurrent `dotnet test` may race. Treat as a separate, measured step; do not block 1–6 on it.
>
> Expected result: a council's gate step goes from ~21 min to ~4–5 min (~2.5–3 min if item 7 works out).
> Out of scope: auto-installing dependencies into worktrees (rejected: downloads node_modules per worktree); per-harness allow-folder settings.

## Problem and goal

`run start` puts a run's worktree beside the repository (`<repo>.worktrees/…`). Harnesses running unattended refuse folders outside the session folder, so a gate worker was refused, improvised an empty worktree with no npm packages, and re-ran the whole gate suite three times. Goal: run worktrees live inside the repository under one git-ignored folder, the index never picks them up, workers stop instead of improvising, and a council gate run that cannot succeed (a missing required path, no `--out`) is refused in seconds instead of after minutes of gates.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| 1. Run worktrees at `.agent_worktrees/<stamp>-<branch>`; change `WorktreePathFor` | T1 | `RunStartRunnerWorktreeTests` asserts `<root>/.agent_worktrees/<stamp>-<branch>` |
| 1. Change anything documenting the sibling layout | Design harvest (docs-dev closeout); T1 updates code comments and tests | `RunStartRunner` holds no `".worktrees"` container string, and `RunStartCommandTests` and `RunStartIndexTests` expect `<root>/.agent_worktrees`. `docs/cli-command-surface.md` § run start is the only current document describing the sibling layout, and docs-dev rewrites it (searched `src`, `products`, `.apm`, `docs` for `.worktrees`, `beside the repo`, `repository parent`). `repo.worktrees` strings in Arbiter, guard and gate test fixtures are arbitrary fake paths, not the run start layout, and stay unchanged. |
| 2. Run start adds `.agent_worktrees/` to `.git/info/exclude`, idempotent, local, before creating the worktree | T1 | `RunStartRunnerWorktreeExcludeTests` (append once, never twice, written before `WorktreePathFor`) |
| 2. kw-onboarding adds `.agent_worktrees/` to shared `.gitignore` | T5 | onboarding reference carries the step; skill gates pass |
| 3. `doctor` fails if an indexed path starts with `.agent_worktrees/` | T3 | `RepositoryDoctorNestedWorktreeTests` |
| 4. Worker/reviewer prompts: refused folder → stop and report; never create your own worktree | T5 | worker baseline carries the rule; content test pins it |
| 5. Preflight every admitted gate's `requires-paths`; refuse `--council`/`--all` runs in seconds listing path and fix | T2 | `ReviewGatesRequiredPathsPreflightTests` |
| 5. Scoped/pre-push runs keep today's behaviour | T2 | same class: scoped run still starts the other gates and reports the missing one as not started; `ReviewGatePrerequisiteTests` (scoped) unchanged |
| 5. Add `dash/web/node_modules` to this repo's gates that need it | T4 | `ReviewGateRequiredPathsConfigTests` |
| 6. `review gates --council` without `--out` is refused | T6 | `GateRunnerCouncilOutTests` |
| 7. Optional concurrency step, after a trial run | Out-of-scope boundaries (`PR note:`) | D9 |
| Expected result: ~21 min → ~4–5 min | Out-of-scope boundaries (`PR note:`) | Measured on the next real council run, not provable by a gate |
| Out of scope: auto-installing dependencies; per-harness allow-folder settings | Out-of-scope boundaries | No task touches them |

## Scale

`extends-component`: run start and the worker baseline (KyberSquad), `review gates` (ReviewCouncil), `doctor` (DocGraph). No new component.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Where is the folder anchored? | `<repository root run start resolved>/.agent_worktrees/<stamp>-<branch>[-n]`: the same root `WorktreePathFor` uses today, so the folder is inside the session folder the run was started from. The stamp, suffix and `.claim` logic is unchanged; only the container moves from beside the root to inside it. | Request item 1 states the layout, and the root cause is a folder outside the session folder. Anchoring at the main checkout instead would put a run started from inside a linked worktree outside that session again. The cost (deleting that linked worktree deletes the nested one) is a risk row. The folder name is one Domain constant (`AgentWorktreeFolder.Name`) read by run start and doctor. | ANSWERED — autonomous: request states it |
| D2 | How is the local exclude written? | New `IRunStartRunner.EnsureWorktreeFolderExcluded()`: resolve the file with `git rev-parse --git-path info/exclude` (correct from a linked worktree too, it names the common dir), create it if absent, append the line `.agent_worktrees/` only when no line already equals it (trimmed); called in `RunStartService.Start` before `WorktreePathFor`. | Before `WorktreePathFor` because that call already creates the folder and its `.claim` file. Idempotent per request. | ANSWERED — autonomous: request states order; git-path is git's documented way |
| D3 | What if the exclude cannot be written? | The run fails before any folder is created, with the error and the remedy "add `.agent_worktrees/` to `.git/info/exclude` and start again". | Without it the nested worktree shows as untracked in the canonical checkout, which later reads as a dirty checkout. | ANSWERED — autonomous: low-confidence, no authoritative source; cheap to reverse |
| D4 | Doctor check shape | New check id `codegraph.nested-worktrees`, added after the CodeGraph checks and before the `stop-on-tool-failure` downgrade; fails when `ICodeGraphPortsFactory.ForRepository(root).Resolver.HasFilesUnder(".agent_worktrees")` is true; passes (no line noise) when the index is unavailable, because `codegraph.index` already reports that. Fix: `add '.agent_worktrees/' to .gitignore, then rebuild the index with 'codegraph index .'`. | Reuses the existing resolver port and `HasFilesUnder` (already used by drift). Placing it before the downgrade keeps ADR 0061: under `stop-on-tool-failure: false` an ensure run warns, every other mode fails with exit 1. | ANSWERED — autonomous: follows ADR 0061 |
| D5 | Where does the requires-paths preflight live? | `IGateRunService` gains `MissingRequirements(ReviewConfig config, string workingDirectory, IReadOnlyCollection<string>? changedPaths)` returning each gate id, path and fix for admitted gates whose `applies-when` admits them; `GateRunService` implements it with the same `AppliesWhenReason` and `Resolve` it already uses, so "admitted" means "would start". `ReviewGatesService.ExecuteRepositoryGates` calls it for `--council` or `--all` right after the full-suite selection, and moves the CodeGraph `worktreeIndex.Ensure` call to after the preflight (still before any gate starts) so a refusal costs seconds, not an index sync. It refuses exactly like the existing `--all` refusal: empty report to `--out`, one `KW-REVIEW-033` error per missing path, exit 1. A refused council run writes no evidence and no finished-cycle record, so a rerun with the same `--out` is the same cycle, in full mode. | One place decides applicability and path resolution, so the preflight cannot disagree with the runner. Scoped runs skip the preflight and keep the per-gate `MissingRequirement` result. Prior cycles are read with `cycles.ReadFinished` in `DetectCycle`, so a refused run must not leave a record that reads as finished. | ANSWERED — autonomous: follows the existing `--all` refusal pattern |
| D6 | Fix text for a missing path | `MissingRequirementSummary` (and the preflight) say `npm install --prefix <parent>` for any path whose last segment is `node_modules` (so `dash/node_modules` → `--prefix dash`, `dash/web/node_modules` → `--prefix dash/web`); any other path keeps "Install whatever provides it". | `dash/web` has its own `package.json` and lock file; `dash` builds it with `cd web && npm install`. Today's text sends a missing `dash/web/node_modules` to the wrong install. | ANSWERED — autonomous: checked `dash/package.json` and `dash/web/package.json` |
| D7 | Which gates get `dash/web/node_modules`? | `ts-typecheck` (`tsc -p web/tsconfig.json`), `ts-test` (vitest over `web/src`) and `ts-lint` (eslint over `dash/**`). Not `ts-reachable`. | Each of the three resolves `web/src` imports from `dash/web/node_modules`; `check:reachable` follows file references only. A wrong extra entry would refuse councils for nothing, so the list stays minimal. | ANSWERED — autonomous: low-confidence, no authoritative source; cheap to reverse |
| D8 | Where is `--council` without `--out` refused? | In `ReviewGatesCommand.Execute`, with the other option-combination refusals: `KW-REVIEW-028` client-input error, exit 2, message naming `review seats` as the source of the full command. Applies to `--agent`-less runs only (task gates never take `--council`). | Matches the neighbouring refusals' code and exit. | ANSWERED — autonomous: follows existing pattern |
| D9 | Item 7 (concurrent gates, higher parallelism) | Not planned here; listed as a `PR note:`. | The request marks it optional, after a trial run, and not blocking 1–6; an autonomous run cannot do the trial. | ANSWERED — autonomous: request states it |
| D10 | Does this repository's own `.gitignore` get `.agent_worktrees/`? | Yes, in T4, beside the existing `.claude/worktrees/` and `.kilo/worktrees/` lines. | This repository is onboarded; item 2 puts the line in every onboarded repository's shared `.gitignore`. | ANSWERED — autonomous: request states it |
| D11 | Where do the prompt rules go? | One rule in `products/kyber-squad/shared/worker-baseline.md`, which `SquadWorkerBaselineInjector` puts in every subagent's body (developers, code-reviewer, review-lens, review-triage, docs-dev). | One home, no per-agent restatement (the baseline file's own rule). Kyber-Squad prompt text: the owner's standing rule keeps prompt edits on a Claude worker. | ANSWERED — autonomous: follows the baseline's single-home rule |
| D12 | Existing sibling `<repo>.worktrees/…` folders | No migration: a resumed branch continues in the worktree git lists as holding it, wherever it is; only new folders go inside. | `RunStartService.Start` resumes from `git worktree list` (`GitWorktreePorcelain.Holding`). | ANSWERED — autonomous: read in code |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute (owner approved the design in chat 2026-11-23; plan review: 1 finding, 0 rejected), 2026-11-23 |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| KyberSquad | reference | `docs/cli-command-surface.md` § run start, "Where the branch lands" | Worktrees at `<repo>/.agent_worktrees/<stamp>-<branch>`; run start writes `.agent_worktrees/` to `info/exclude` first and fails if it cannot |
| ReviewCouncil | reference | `docs/cli-command-surface.md` § review gates options and exit-code row | `--council` needs `--out`; `--council`/`--all` refuse up front when a required path is missing, naming path and fix |
| ReviewCouncil | architecture | `docs/code-review/architecture.md` gate runner | The requires-paths preflight and where applicability is decided |
| DocGraph | runbook | `docs/docgraph/mcp-runbook.md` doctor checks | `codegraph.nested-worktrees` check, meaning and fix |
| KyberSquad | ADR | ADR 0060 consequence note or a new ADR | Run worktrees live inside the repository in one git-ignored folder, because unattended harnesses refuse outside folders |

## Investigation findings

- Read at `f122a2c65` on `claude/feat/agent-worktrees-inside-repo` through the worktree's own index (`codegraph_explore` with `projectPath`).
- `RunStartRunner.WorktreePathFor` builds `<parent>/<name>.worktrees`; `AddWorktree` creates the parent folder; `RunStartService.Start` calls `WorktreePathFor` then `CreateBranchInWorktree`/`AddWorktreeForBranch` and releases the claim.
- `GateRunService.Run` checks `applies-when`, then the deadline, then `MissingRequirement` per gate; `requires-paths` is parsed by `ReviewConfigParser.ParseRequiresPaths`. Today a missing path is reported per gate, after earlier gates ran.
- `ReviewGatesService.ExecuteRepositoryGates` already refuses a whole `--all` run when a full-suite gate is refused (empty report, exit 1) — the pattern D5 copies.
- `ICodeGraphResolver.HasFilesUnder` exists (`CodeGraphResolverAdapter`), reached through `ICodeGraphPortsFactory.ForRepository(root).Resolver`.
- `RunCheckoutGuard.EnclosingMainCheckout` walks up to the nearest `.git` entry and treats a `.git` file as a linked worktree, so a write inside a nested worktree is not refused as a canonical-checkout write. This repository already runs nested worktrees under `.claude/worktrees/` and `.kilo/worktrees/`.
- Nesting is safe for the Arbiter's path logic: `HookRunContextResolver.TryCandidate` picks the longest (most specific) containing worktree, `PlanRootResolver.ResolvePlanRoot` picks by which worktree holds the plan file, and `RunCheckoutGuard` stops at the nearest `.git` entry.
- `GateRunnerDeadlineTests` (Cli.Tests) runs `--council` without `--out` five times; T6 gives those runs an `--out`. The integration tests that pass `--council` (`ReviewGatesCycleTests`, `GateCatalogueToolchainTests`, `ReviewGatesCouncilCommandTests.GatesCommand`) already pass `--out`. `ReviewGatePrerequisiteTests` exercises `requires-paths` on scoped runs only.
- Integration tests asserting the sibling layout: `RunStartCommandTests` and `RunStartIndexTests` (both in T1).
- Only `RunStartRunner.AddWorktree` runs `git worktree add` in `src`.
- CI never runs `review gates` (grep of `.github/workflows`), so the new refusals cannot fail CI.
- Existing test `RunStartRunnerWorktreeTests` uses the POSIX literal `/kw/work`, which the test standard forbids; T1 touches it, so T1 fixes it.
- Discovery: codegraph 10, docs 5, direct reads 8 (`ReviewGatesService.cs` 77-201 and 300-399, and `ReviewGatesCommand.cs` 35-94: explore trimmed those bodies; `RunStartService.cs` 225-284: trimmed; `RepositoryDoctorService.cs` 1-130: trimmed; `ReviewGatesCouncilCommandTests.cs` 550-569: to confirm its `--out`; `kyber-weave.yml`, `cli-command-surface.md`, `kw-onboarding/SKILL.md`, `worker-baseline.md`, plan index: not indexed code).

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Application.Tests/RunStart/RunStartRunnerWorktreeTests.cs` (extend), `tests/KyberWeave.Application.Tests/RunStart/RunStartRunnerWorktreeExcludeTests.cs` (new) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Application.Tests.RunStart.RunStartRunnerWorktree"` | `WorktreePathFor` returns `<root>/.agent_worktrees/<stamp>-<branch>`, `/` in the branch replaced by `-`; a taken name gets `-2`. `EnsureWorktreeFolderExcluded`: the file path comes from `git rev-parse --git-path info/exclude` (a relative answer resolved against the root); absent file → created with the one line; file without the line → line appended after existing content (a file not ending in a newline gets one first); file already holding `.agent_worktrees/` (with surrounding spaces) → unchanged; a write failure → returns the failure. `RunStartService.Start` calls the exclude before `WorktreePathFor` and, when it fails, stops with an error and the remedy line and creates no worktree. In-place runs (clean checkout, no other worktree) never write the exclude. | New tests fail: paths still end in `<repo>.worktrees`; `EnsureWorktreeFolderExcluded` missing | All named tests pass; no test root is a POSIX literal |
| T2 | `tests/KyberWeave.Application.Tests/Review/ReviewGatesRequiredPathsPreflightTests.cs` (new; arrange the service as `ReviewPathSeamTests` does, and the gate runner as `GateRunServiceTests` does) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Application.Tests.Review.ReviewGatesRequiredPathsPreflightTests"` | `GateRunService.MissingRequirements`: none missing → empty; two gates each missing a path → both listed with gate id, path, fix; a gate whose `applies-when` excludes the changed set is not listed even with its path missing; a gate with no `requires-paths` is never listed. Fix text: `dash/node_modules` → `npm install --prefix dash`, `dash/web/node_modules` → `npm install --prefix dash/web`, `tools/x` → "Install whatever provides it". Service: `--council` with a missing path → exit 1, one `KW-REVIEW-033` error per path naming path and fix, no gate process started, empty report written to `--out`, CodeGraph ensure not called; `--all` the same; scoped run (no `--council`/`--all`) with a missing path → other gates still start and the gate is reported not started as today; `--council` with every path present → gates run; a refused `--council` run followed by a rerun with the same `--out` → the rerun is the same cycle number in full mode, not a verifier cycle. | New tests fail: `MissingRequirements` missing; council run starts gates | All named tests pass; existing `GateRunServiceTests` and `ReviewPathSeamTests` keep their assertions |
| T6 | `tests/KyberWeave.Cli.Tests/GateRunnerCouncilOutTests.cs` (new; arrange as `GateRunnerDeadlineTests` does), `tests/KyberWeave.Cli.Tests/GateRunnerDeadlineTests.cs` (its `Gates("--council", …)` calls gain `--out`) | `dotnet test tests/KyberWeave.Cli.Tests/KyberWeave.Cli.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.GateRunner"` | `--council` without `--out` → exit 2, a `KW-REVIEW-028` message naming `--out` and `review seats`, and the service is never called; `--council --out <path>` reaches the service; `--out` without `--council` reaches the service; an `--agent` run is unaffected. | New test fails: the CLI accepts `--council` alone | All named tests pass; `GateRunnerDeadlineTests` keeps its assertions |
| T3 | `tests/KyberWeave.Application.Tests/Doctor/RepositoryDoctorNestedWorktreeTests.cs` (new, copy `RepositoryDoctorTests` and its `InMemoryDoctorWorld`) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Application.Tests.Doctor.RepositoryDoctor"` | Index with a file under `.agent_worktrees/` → check `codegraph.nested-worktrees` fails with the fix line, exit 1 in check, provision and ensure modes; index without such a file → passes; index unavailable → passes; ensure with `stop-on-tool-failure: false` → reported as a warning, exit 0; a file under `.agent_worktrees-other/` does not count. | New tests fail: no `codegraph.nested-worktrees` check | All named tests pass; existing `RepositoryDoctorTests` keep their assertions (a check-list count may grow by one; name each such edit) |
| T4 | `tests/KyberWeave.RepoPolicy.Tests/ReviewGateRequiredPathsConfigTests.cs` (new, copy `ReviewGateFullSuiteConfigTests`) | `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.ReviewGateRequiredPathsConfigTests"` | `ts-typecheck`, `ts-test`, `ts-lint` each require both `dash/node_modules` and `dash/web/node_modules`; `ts-reachable` requires `dash/node_modules` only; the root `.gitignore` holds the line `.agent_worktrees/`. | New test fails: `dash/web/node_modules` absent; `.gitignore` line absent | Test passes |
| T5 | `tests/KyberWeave.Content.Tests/WorkerRuleRefusedFolderTests.cs` (new, copy `WorkerRuleSingleHomeTests`) | `dotnet test tests/KyberWeave.Content.Tests/KyberWeave.Content.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.WorkerRule"` | The baseline block holds the refused-folder rule once (stop and report the refused path; never create a worktree, clone or copy of your own); the kw-onboarding references carry the `.gitignore` step and a `codegraph.nested-worktrees` row in `doctor-checks.md`. | New test fails: rule and rows absent | Tests pass; skill gates for kw-onboarding pass |

## Tasks

### T1: Run worktrees inside the repository, excluded locally first

- **Objective:** `run start` creates worktrees under `<repository root>/.agent_worktrees/` and writes `.agent_worktrees/` to git's local exclude file before it creates one.
- **Capabilities:** C# application code with in-memory unit tests over git and file-system seams
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `T1`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Domain/RunStart/AgentWorktreeFolder.cs` (new): static class `AgentWorktreeFolder` in namespace `KyberWeave.Core.RunStart` (the namespace `GitOutcome` uses), constants `Name = ".agent_worktrees"` and `ExcludeLine = ".agent_worktrees/"`, with XML docs.
  - `src/KyberWeave.Contracts/Services/IRunStartRunner.cs`: `IRunStartRunner`, add `GitOutcome EnsureWorktreeFolderExcluded()` with XML docs; update the `WorktreePathFor` docs to the new location.
  - `src/KyberWeave.Application/RunStart/RunStartRunner.cs`: `WorktreePathFor` uses the D1 container; new `EnsureWorktreeFolderExcluded` per D2 (`git rev-parse --git-path info/exclude` run in the repository root, resolve a relative answer against the root, read/append through `IWritableFileSystem`, catch the file-system failures the seam documents and return them as a failed `GitOutcome`); update the constructor `paths` param doc ("sits beside").
  - `src/KyberWeave.Application/RunStart/RunStartService.cs`: `Start`, call `runner.EnsureWorktreeFolderExcluded()` in the branch that creates a new folder, before `WorktreePathFor`; on failure `Fail(messages, …)` with the D3 remedy.
  - Every other `IRunStartRunner` implementation in `tests/KyberWeave.Testing` (fakes and stubs) gains the member.
  - `tests/KyberWeave.Application.Tests/RunStart/RunStartRunnerWorktreeTests.cs`: new expectations; replace the `/kw/work` literal with a root from the fake's in-memory root.
  - `tests/KyberWeave.Application.Tests/RunStart/RunStartRunnerWorktreeExcludeTests.cs` (new), covering both the runner and the `RunStartService.Start` ordering (arrange the service as `RunStartResumeLogTests` does).
  - `tests/KyberWeave.Integration.Tests/RunStartCommandTests.cs` and `tests/KyberWeave.Integration.Tests/RunStartIndexTests.cs`: the expected worktree container (the `Path.GetFileName(_probe.Root) + ".worktrees"` expressions) becomes `<root>/.agent_worktrees`; CI runs them.
- **Commands:**
  - `kyber-weave review gates . --agent <your agent name> --task T1 --red`
  - `kyber-weave review gates . --agent <your agent name> --task T1`
- **Acceptance:**
  - No code in `src` builds a `<name>.worktrees` container: `RunStartRunner` holds no `".worktrees"` string.
  - `RunStartCommandTests` and `RunStartIndexTests` no longer expect the sibling folder.
  - Leave alone the `repo.worktrees` paths in the Arbiter, guard and gate test fixtures (for example `ArbiterCanonicalCheckoutTests`, `PlanRootResolverTests`, `ReviewGatesCycleTests`). They are arbitrary fake paths and have nothing to do with run start.
  - The Test contract row for T1 is green.

### T2: `review gates` refuses a council or full run that cannot succeed

- **Objective:** A `--council` or `--all` run with a missing required path is refused before any gate or index work, listing each path and its fix.
- **Capabilities:** C# application code with in-memory unit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `T2`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Contracts/Services/IGateRunService.cs`: `IGateRunService`, add `MissingRequirements(...)` per D5, returning a list of a new record.
  - `src/KyberWeave.Domain/Review/MissingGateRequirement.cs` (new): `sealed record MissingGateRequirement(string GateId, string Path, string Fix)` in the namespace `GateResult` uses.
  - `src/KyberWeave.Application/Review/GateRunService.cs`: implement `MissingRequirements` with the existing `AppliesWhenReason` and `Resolve`; `MissingRequirementSummary` uses the D6 fix text, shared with the new method.
  - `src/KyberWeave.Application/Review/ReviewGatesService.cs`: `ExecuteRepositoryGates`, the preflight and refusal per D5; move the `worktreeIndex.Ensure` block after it.
  - `src/KyberWeave.Application/Review/Commands/ReviewGateReporting.cs`: reuse `GateNotStarted` for the per-path diagnostic (add a helper there only if the service needs one).
  - `tests/KyberWeave.Testing/ReviewTestServices.cs`, `tests/KyberWeave.Testing/InMemoryReviewServices.cs`: every `IGateRunService` fake gains the member.
  - `tests/KyberWeave.Application.Tests/Review/ReviewGatesRequiredPathsPreflightTests.cs` (new).
- **Commands:**
  - `kyber-weave review gates . --agent <your agent name> --task T2 --red`
  - `kyber-weave review gates . --agent <your agent name> --task T2`
- **Acceptance:**
  - The Test contract row for T2 is green.
  - A scoped run's behaviour is unchanged (its existing tests keep their assertions).

### T3: Doctor fails when the index holds a run worktree

- **Objective:** `kyber-weave doctor` reports `codegraph.nested-worktrees` and fails when any indexed path is under `.agent_worktrees/`.
- **Capabilities:** C# application code with in-memory unit tests over the CodeGraph port
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `T3`
- **Depends on:** T1 (`AgentWorktreeFolder.Name`)
- **Files:**
  - `src/KyberWeave.Application/Doctor/RepositoryDoctorService.cs`: constructor gains `ICodeGraphPortsFactory codeGraph`; `Run` adds the D4 check after `codegraph.Checks` and before the `stops` downgrade. Not added to `RunPostCheckout`.
  - `src/KyberWeave.Application/ServiceCollectionExtensions.cs`: `AddWeaveApplication` registers `IRepositoryDoctor` with `TryAddSingleton<IRepositoryDoctor, RepositoryDoctorService>()`; the container resolves the new parameter, change the line only if it does not.
  - `tests/KyberWeave.Testing/Doctor/InMemoryDoctorWorld.cs`: build the service with `FakeCodeGraphPortsFactory` (`tests/KyberWeave.Testing/FakeCodeGraphPortsFactory.cs`), whose resolver's indexed files a test sets; widen that fake only if it cannot answer `HasFilesUnder`.
  - `tests/KyberWeave.Application.Tests/Doctor/RepositoryDoctorNestedWorktreeTests.cs` (new).
- **Commands:**
  - `kyber-weave review gates . --agent <your agent name> --task T3 --red`
  - `kyber-weave review gates . --agent <your agent name> --task T3`
- **Acceptance:**
  - The Test contract row for T3 is green.

### T4: This repository's gates and ignore file

- **Objective:** The TypeScript gates that read `dash/web` require `dash/web/node_modules`, and the root `.gitignore` ignores `.agent_worktrees/`.
- **Capabilities:** gate configuration with a repository-policy test
- **Difficulty:** easy
- **Technology:** test
- **Session:** `T4`
- **Depends on:** none
- **Files:**
  - `tests/KyberWeave.RepoPolicy.Tests/ReviewGateRequiredPathsConfigTests.cs` (new).
  - `.kyber-weave/kyber-weave.yml`: gate entries `ts-typecheck`, `ts-test` and `ts-lint` list both `dash/node_modules` and `dash/web/node_modules` under requires-paths (D7); update the comment above `ts-typecheck` that says each declares `dash/node_modules`.
  - `.gitignore`: add the `.agent_worktrees/` line under the "Agent harness worktrees" comment.
- **Commands:**
  - `kyber-weave review gates . --agent <your agent name> --task T4 --red`
  - `kyber-weave review gates . --agent <your agent name> --task T4`
- **Acceptance:**
  - The Test contract row for T4 is green.

### T5: Worker stop rule and onboarding steps

- **Objective:** Every subagent is told to stop and report a refused folder instead of making its own worktree, and kw-onboarding adds `.agent_worktrees/` to the shared `.gitignore` and explains the new doctor check.
- **Capabilities:** agent prompt and skill markdown with content tests
- **Difficulty:** easy
- **Technology:** test
- **Session:** `T5`
- **Depends on:** none
- **Files:**
  - `products/kyber-squad/shared/worker-baseline.md`: inside `<worker-baseline>`, one rule: "**A refused folder is a stop.** When the harness refuses a folder your task needs, stop and report the refused path and the refusal text. Never create a worktree, clone or copy of your own to work around it." (D11).
  - `docs/kyber-squad/worker-rule-map.md`: the map row for the new rule, if the map lists each baseline rule (`WorkerRuleSingleHomeTests` / `ProseRuleCatalogTests` say which).
  - `.apm/skills/kw-onboarding/references/mcp-and-index.md`: a question, after the worktree index hook, to add `.agent_worktrees/` to the repository's `.gitignore` (context: run worktrees live there; without the line the folder shows as untracked and a copied folder gets indexed); recommended yes; idempotent.
  - `.apm/skills/kw-onboarding/references/doctor-checks.md`: a row for `codegraph.nested-worktrees` (what it means, the fix, recommend yes).
  - `.apm/skills/kw-onboarding/SKILL.md`: Step 2's ask list names the `.gitignore` question.
  - `tests/KyberWeave.Content.Tests/WorkerRuleRefusedFolderTests.cs` (new).
- **Commands:**
  - `kyber-weave review gates . --agent <your agent name> --task T5 --red`
  - `kyber-weave review gates . --agent <your agent name> --task T5`
- **Acceptance:**
  - The Test contract row for T5 is green; the `skill-validate-kw-onboarding`, `skill-lint-kw-onboarding` and `skill-scan-kw-onboarding` gates pass.

### T6: `review gates --council` needs `--out`

- **Objective:** The CLI refuses `review gates --council` without `--out` before the service runs.
- **Capabilities:** C# CLI option validation with in-memory unit tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `T6`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`: `Execute`, the D8 refusal beside the `--deadline` and `--gate` combination checks, written with `WriteClientInputError` and `ClientInputExitCode`.
  - `tests/KyberWeave.Cli.Tests/GateRunnerCouncilOutTests.cs` (new).
  - `tests/KyberWeave.Cli.Tests/GateRunnerDeadlineTests.cs`: each `Gates("--council", …)` call gains `--out` and a scratch path; assertions unchanged.
- **Commands:**
  - `kyber-weave review gates . --agent <your agent name> --task T6 --red`
  - `kyber-weave review gates . --agent <your agent name> --task T6`
- **Acceptance:**
  - The Test contract row for T6 is green.

## Dependency graph

- T1, T2, T4, T5 and T6 start together.
- T3 after T1 (consumes `AgentWorktreeFolder.Name`).
- docs-dev closeout after all six.

## Risks

- A run started from inside a linked worktree nests its own worktree there (D1); removing that outer worktree with `git worktree remove` refuses while the nested one exists, and deleting it by hand loses it: acceptable, because run start is normally invoked from the canonical checkout and the nested folder is still the session folder of the run that made it.
- A harness may still refuse a dot-folder inside the repo: mitigated by T5's stop rule, which turns a refusal into a report instead of an improvised worktree.
- Building `CodeGraphResolverAdapter` in the doctor loads the whole index through `sqlite3`: acceptable at ~3k files; if a doctor run slows noticeably, narrow to a single `files` query in a follow-up.
- A tree walk that does not use git (the non-git fallback of `FileRepositoryFileLister`) could descend into `.agent_worktrees/`: only for non-git roots, where run start never creates one.
- The council refusal applies to any host whose gates declare `requires-paths`: intended by the request; scoped and pre-push runs are unchanged.

## Out-of-scope boundaries

- PR note: `.kyber-weave/kyber-weave.yml` `review.gates` — request item 7 (mark independent gates `concurrent`, raise `review.parallelism`) — optional and needs a measured trial run; coverlet instruments `bin/Release` in place, so concurrent `dotnet test` may race.
- PR note: council gate step timing — the expected drop from ~21 min to ~4–5 min — can only be measured on the next real council run.
- PR note: `.claude/skills/conductor-hill-climb/scripts/*-test.sh` — these scripts build their own sibling `.worktrees` folders for demo repos — left as they are because they are not run start; a human decides whether to align them.
- Auto-installing dependencies into worktrees, and per-harness allow-folder settings: rejected by the request.

## Verification gates

- Each task: its own task gate (red, then green), as listed in its Commands.
- End of run: the review council's declared gates; CI runs the integration project (`RunStartCommandTests`) and the full suite.
- `kyber-weave docs validate .` and `kyber-weave docs drift .` after docs-dev's closeout edits.

## Review

The end-of-run review council (`code-reviewer`) reviews the whole change under the `code-review` skill.

## docs-dev closeout

docs-dev harvests the Design harvest rows into `docs/cli-command-surface.md` (run start "Where the branch lands", `review gates` options and the exit-code table row), `docs/code-review/architecture.md`, `docs/docgraph/mcp-runbook.md`, and an ADR or ADR 0060 consequence for the in-repository worktree folder; fills the Closeout mapping; then the plan is archived.

## Closeout mapping

| ID | Destination | Documentation impact |
|---|---|---|
| D1, D12 | [Where the branch lands](../../cli-command-surface.md) and [ADR 0060](../../adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md), amended; no ADR — ADR waiver: refines the run-start location in ADR 0060 | extends-component |
| D2, D3 | [Where the branch lands](../../cli-command-surface.md) (exclude line written first, run stops with the remedy) | extends-component |
| D4 | [Check ids](../../docgraph/mcp-runbook.md) (`codegraph.nested-worktrees`) | extends-component |
| D5, D6 | [Gate runner](../../code-review/architecture.md), [`requires-paths`](../../configuration.md) and the `review gates` rows in [CLI command surface](../../cli-command-surface.md) | extends-component |
| D7, D10 | Documentation waiver: which gates list `dash/web/node_modules` and this repository's `.gitignore` line are host configuration, pinned by `ReviewGateRequiredPathsConfigTests`; the gate runner section names the three gates | extends-component |
| D8 | [CLI command surface](../../cli-command-surface.md) (`review gates` `--council` needs `--out`, `KW-REVIEW-028`) | extends-component |
| D9 | Documentation waiver: not built; listed as a PR note in Out-of-scope boundaries | extends-component |
| D11 | [Worker rule map](../../kyber-squad/worker-rule-map.md) (refused-folder rule, one home in the worker baseline) | extends-component |
| AP1 | Documentation waiver: approval record only | extends-component |
| KyberSquad | [Worker rule map](../../kyber-squad/worker-rule-map.md), [Where the branch lands](../../cli-command-surface.md) | extends-component |
| ReviewCouncil | [Gate runner](../../code-review/architecture.md) | extends-component |
| DocGraph | [Check ids](../../docgraph/mcp-runbook.md) | extends-component |
