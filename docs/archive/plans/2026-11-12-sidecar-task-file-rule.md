---
id: plans/2026-11-12-sidecar-task-file-rule
title: Flag sidecar copies of a plan's task file left in the plans folder
doc-type: plan
status: archived
component: DocGraph
owner: dpalfery
created: 2026-11-12
last-reviewed: 2026-11-12
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [DocGraph, CI Pipelines, KyberSquad]
---

# Flag sidecar copies of a plan's task file left in the plans folder

**Status: Archived, 2026-11-12.** Approved for execution under decision AP1 (autonomous approval of gate `approve-and-execute`). The task file was never committed and was removed at closeout; see the Completion summary.
**Development mode:** `test-first` (RED by `test-dev`, GREEN by the implementation specialist, never the same worker).
**Autonomy:** `autonomous` (every open item decided by recommendation; provenance in `## Decisions`).
**Scale:** `extends-component`, components `DocGraph`, `CI Pipelines`, `KyberSquad`.
**Delivery:** one draft pull request from branch `claude/feat/validate-sidecar-task-files`, based on `main` at `31afb054`. No merge.

## Problem and goal

**Background.** At finalization the architect writes a plan's task file, `<plan>.tasks.json`, beside the plan in `docs/plans/`. `docs validate` checks that file against its plan (`KW-DOC-TASKS-001` to `-006`), and `docs validate --merge-ready` fails on a task file left behind at merge (`KW-DOC-LIFECYCLE-005`).

**Problem.** GitHub issue 599: an architect wrote its task file through a temporary sibling (for example `<plan>.tasks.json.new`) and left the sibling behind. No rule sees such a file:

- `PlanTaskFileValidator` reads only the exact `<plan>.tasks.json` path it derives from each plan.
- `KW-DOC-LIFECYCLE-005` walks `*.json` and keeps names equal to `tasks.json` or ending in `.tasks.json`, so `.tasks.json.new` and `.tasks.json.tmp` pass both filters. A sidecar therefore reaches `main` unflagged.

Pull request 600 (open) changes the architect's guidance to write the task file in place. This plan adds the check that catches a sidecar no matter how it got there.

**Goal.** A new finding, `KW-DOC-TASKS-007` (Error), reported by `docs validate` and by the docs MCP validate tool, for every file in the plans folder whose name holds `.tasks.json` followed by more characters. The rule and its id are documented wherever the `KW-DOC-TASKS-*` family is listed.

## Decisions

Every open item was decided by recommendation under the autonomous-decisions rule. `autonomous: recommendation accepted` means decided and awaiting the owner's approval at the approve-and-execute gate. `autonomous: low-confidence, no authoritative source` marks a call no source settled; the pull request body lists those rows for review.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Where does the rule live, and what is its id? | **`PlanTaskFileValidator`, new constant `SidecarTaskFile = "KW-DOC-TASKS-007"`**, the next free id in the task-file family. | The ids are `public const string` fields on the validator, with no central registry (CodeGraph shows no `KW-DOC-*` rule catalogue in `src/KyberWeave.Core/Diagnostics`). `DocsSchemaGate.Validate` runs `PlanTaskFileValidator` in plain `docs validate`, in `--merge-ready` and in the MCP validate tool, so the rule reaches all three without a flag. Rejected: widening `KW-DOC-LIFECYCLE-005` in `OpenWorkValidator`. That rule runs only under `--merge-ready`, and the conductor's MCP check runs with `mergeReady` false, so the sidecar would be caught at merge rather than at finalization. That rule is also about legitimate files that outlived their plan, and a sidecar is never legitimate. | autonomous: recommendation accepted |
| D2 | Severity? | **Error.** | The class remarks set the principle: the one warning, `-006`, covers a state every plan passes through, and the rest are errors because each means a file that exists and is wrong. A sidecar is never a valid state, and `rule-reference.md` lists `-006` as the only warning. A sidecar that exists fails `docs validate` with exit 1. | autonomous: recommendation accepted |
| D3 | Which file names count as a sidecar? | **A file name that contains `.tasks.json` (case-insensitive) and does not end with it.** That covers `<plan>.tasks.json.new`, `.tmp`, `.bak`, `.orig`, `~`, and editor swap names such as `.<plan>.tasks.json.swp`. | Issue 599 names `.new` and `.tmp`. This test is simple and covers every suffix-style temporary name. Names that still end in `.tasks.json` (for example `<plan>.tmp.tasks.json`) are left to `KW-DOC-LIFECYCLE-005` at merge, so the two rules' name sets are disjoint and one file never gets two findings. Not covered: a temporary name that drops the `.tasks.json` substring (for example `<plan>.tasks.tmp`); risk R2. | autonomous: low-confidence, no authoritative source. Cheap to reverse (one predicate). |
| D4 | Where does the walk look? | **The folder of the `plan-index` registry property, recursively, through `DocsRootPath.EnumerateContainedFiles` with pattern `*` over each configured docs root, keeping paths inside that folder that are not archived (`InventoryReachability.IsWithin`, `!InventoryReachability.IsArchived`).** Specification folders and `docs/archive/**` are out of scope. | This is the same link-safe walk `OpenWorkValidator.LeftoverTaskFiles` uses (no symlink is followed). The folder comes from the registry, not a hard-coded `docs/plans`, as in `PlanTaskFileValidator.PlanningDirectories`. Closeout uses `git mv` on the plan alone, so a sidecar stays in the plans folder and is still caught there. A specification's `tasks.json` is written by `product-owner`, not by the architect path in issue 599. | autonomous: recommendation accepted |
| D5 | Is a sidecar reported when no plan sits beside it? | **Yes. The check runs over the folder, not per plan.** | `Validate` iterates only non-archived plan documents, and closeout moves the plan out. Keying the rule on a plan would miss exactly the sidecar that issue 599 describes once its plan is archived. T1 pins this orphan case. | autonomous: recommendation accepted |
| D6 | Do untracked or git-ignored files count? | **Yes. The walk reads the file system, as `KW-DOC-LIFECYCLE-005` does.** | The point is to catch the sidecar before it is committed, when the architect runs the docs checks at finalization. An editor swap file open at the moment of a run is also reported; risk R1. | autonomous: recommendation accepted |
| D7 | How does it interact with `KW-DOC-LIFECYCLE-005`, `--merge-ready` and the archive? | **`KW-DOC-LIFECYCLE-005` is unchanged.** A sidecar fails `--merge-ready` through `KW-DOC-TASKS-007`, because `DocsSchemaGate` runs `PlanTaskFileValidator` in both modes. The two rules never report the same file (D3). A sidecar under `docs/archive/**` is not reported (D4). | Verified in code: `OpenWorkValidator.LeftoverTaskFiles` uses `JsonSearchPattern = "*.json"`, and `DocsRootPath.MatchesSearchPattern` matches `*.ext` by `EndsWith`, so `.tasks.json.new` never reaches its suffix test. A sidecar can only reach the archive if someone moves it there deliberately, because plans are single files. | autonomous: recommendation accepted |
| D8 | Which documents change? | **`docs/ci-pipelines/rule-reference.md`:** one table row, "All six" becomes "All seven", and the sentence saying every rule is a disagreement or a bad prompt gains "or a leftover copy of the file". **`docs/plans/README.md`:** the paragraph that lists `-002` to `-006` gains one sentence for `-007`. **`docs/kyber-squad/architecture.md`:** "`-001` to `-006`" becomes `-007`, with one clause. **`docs/kyber-squad/onboarding.md`:** one clause naming `-007`. **`docs/todo/mcp-docs-gate-unpinned-for-task-files.md` and its row in `docs/todo/README.md`:** "six" becomes "seven". **ADR 0063 is not amended.** | These are every listing of the family found in the repository. ADR 0063 is accepted and records what it introduced; the rule reference is the registry of ids. The `rule-reference.md` edit is kept to one row and two phrases because pull request 600 edits the same file, so a small merge conflict is expected and acceptable. | autonomous: recommendation accepted |
| D9 | How are the tests written? | **Through `docs validate` (`ReportedFindings`) in `PlanTaskFileValidatorTests`, like the six sibling rules. Each test carries its own control corpus and asserts the exit code.** | This is the class's stated pattern. The conductor's surface is `docs validate` and the MCP tool, which share `DocsSchemaGate`. MCP-side coverage is the separate todo `mcp-docs-gate-unpinned-for-task-files.md`. | autonomous: recommendation accepted |
| D10 | Development mode and test contract? | **`test-first`**, with the Test contract below. | Packet value; no opt-out was supplied. | autonomous: recommendation accepted |
| D11 | ADR disposition? | **No ADR. ADR waiver: adds one rule to the existing task-file rule family under ADR 0063, with no new boundary, dependency or contract.** | Follows the plans index waiver policy. | autonomous: recommendation accepted |
| AP1 | Is the plan approved for execution? | **Approved.** D1 to D11 and the Test contract are accepted as recorded, and the plan is executed as written. | Conductor input `AUTONOMOUS_APPROVAL: approve-and-execute` under the run's resolved autonomy `autonomous`, 2026-11-12; no human answered the gate. The low-confidence row D3 stays flagged for the pull request body. The task file is written in place at `<plan>.tasks.json`, never through a temporary sibling. | autonomous: recommendation accepted (AUTONOMOUS_APPROVAL approve-and-execute, 2026-11-12) |

## Investigation findings

**Discovery:** codegraph 8, docs 1, direct reads 17.

- **Revision read.** Worktree `/Users/dave/git/claude-worktree/kyber-weave/dev-input-site-rules-437c3f.worktrees/20261112T165511Z-claude-feat-validate-sidecar-task-files`, branch `claude/feat/validate-sidecar-task-files`, HEAD `31afb054`. CodeGraph was queried through this worktree's own index with the `codegraph explore` CLI (8 calls).
- **Docs server, 1 call, not relied on.** The kw-docs server is bound to the canonical checkout. Its provenance reported `branch=claude/fix/architect-plan-date-prefix rev=0ae061d2`, not this branch (`projectPath: wrong-branch`), so documents were read directly at this branch's HEAD.
- **Direct reads, 17, with reasons.**
  - Explore returned gapped excerpts (`cli: partial`), so these were read whole: `PlanTaskFileValidator.cs` (twice), `OpenWorkValidator.cs`, and `PlanTaskFileValidatorTests.cs` (twice).
  - `DocsSchemaGate.cs`, read to confirm the order in which validators run.
  - Markdown, which CodeGraph does not index and which kw-docs serves only from the wrong branch: `docs/ci-pipelines/rule-reference.md`, `docs/plans/README.md`, `docs/catalog.md`, `AGENTS.md` (`## Commands`), `docs/standards/test/README.md` (`## Completion`), excerpts of `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md` and `docs/todo/mcp-docs-gate-unpinned-for-task-files.md`, and `docs/archive/plans/2026-11-11-cleanupcode-exit-hang.md` (format template).
  - The architect references `plan-authoring.md`, `autonomy-decisions.md` and `test-first-contract.md`.
  - Repository-wide `git grep` searches for `KW-DOC-TASKS`, `LIFECYCLE-005` and rule listings.
- **History.** `git log --all` over `docs/plans/*tasks.json*` shows no sidecar ever committed, so issue 599's file was caught before merge or left untracked. Nothing in history has to be cleaned.

**Verified facts:**

- **`PlanTaskFileValidator`** (`src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs`):
  - Ids `-001` to `-006` are `public const string` fields; `-006` (`MissingTaskFile`) is the only `Severity.Warning`.
  - `Validate(DocumentSet)` loops over non-archived `DocType.Plan` documents and calls `ValidatePlan`, which reads only `TaskFilePath(plan)` (`<plan>.tasks.json`).
  - `PlanningDirectories()` resolves the folders of `plan-index`, `specification-index` and `todo-index` from `_config.ConfigReg.Resolve(_config.Ontology)`.
- **`KW-DOC-LIFECYCLE-005`** (`OpenWorkValidator.LeftoverTaskFiles`, `--merge-ready` only) walks every docs root with `DocsRootPath.EnumerateContainedFiles(root, "*.json")`. It keeps `tasks.json` and `*.tasks.json`, and it includes the archive.
- **`DocsRootPath.EnumerateContainedFiles`** (`src/KyberWeave.Core/Configuration/DocsRootPath.cs`, internal, already used from `KyberWeave.Core.Docs`) skips symlinks. `MatchesSearchPattern` supports `*`, `*.ext` and an exact name, and only compares case-insensitively on Windows.
- **`DocsSchemaGate.Validate`** constructs `PlanTaskFileValidator` unconditionally, so the CLI, `--merge-ready` and the MCP tool all run it.
- **Tests** (`tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs`, class `KyberWeave.Tests.PlanTaskFileValidatorTests`):
  - Every rule is driven through `DocsValidateCommand` via `ReportedFindings`, with an accepted-corpus control and `AssertReports` / `AssertDoesNotReport`.
  - Fixture paths come from `PlanTaskFileProbe`: `6-Docs/plans/2026-11-07-ready-tasks.md` and `.tasks.json`, and `PlanTaskFileProbe.Corpus()` writes the plan index and a Ready plan.
- **Fixture registry.** `ConfigRegConfig.Resolve` derives `plan-index` from the ontology docs root, so the test fixture resolves it to `6-Docs/plans/README.md` with no extra setup.
- **Plan grammar.** `PlanDocumentParser.ParseTaskBody` takes file scope from the bullet run after the Files label, and the out-of-scope list from every path in the "Out of scope" section. So each task's Files block in this plan lists paths only, and the out-of-scope list names no path that a task touches.
- **No other pin.** No test asserts the contents of `rule-reference.md` or the count of `KW-DOC-TASKS-*` ids. `DocsValidateSettings` and `DocsTools` name only the `LIFECYCLE` ids.
- **Listings of the family:**
  - `rule-reference.md` (table, plus "All six run").
  - `docs/plans/README.md` (the paragraph naming `-002` to `-005`, `-001` and `-006`).
  - `docs/kyber-squad/architecture.md` ("`KW-DOC-TASKS-001` to `-006`").
  - `docs/kyber-squad/onboarding.md` (the `-002` to `-006` sentence).
  - `docs/todo/mcp-docs-gate-unpinned-for-task-files.md` and its todo index row ("six").
  - ADR 0063 (historical; D8).
- **Installed CLI.** It predates this rule, so the repository-corpus check runs the branch build: `dotnet run --project src/KyberWeave.Cli -- docs validate .`.

## Design

1. **Constant** (`PlanTaskFileValidator`): `public const string SidecarTaskFile = "KW-DOC-TASKS-007";` with the XML summary "A copy of a task file left beside it under another name, such as `<plan>.tasks.json.new`."
2. **Walk** (`PlanTaskFileValidator.Validate`): after the plan loop, call a new private method, `ReportSidecarTaskFiles(DiagnosticReport report)`. It:
   - resolves the `plan-index` folder through the registry (factor the per-property lookup out of `PlanningDirectories`, or add a `PlanFolder()` helper beside it; return nothing when the property is unset);
   - for each root in `_config.Ontology.DocsRoots` that exists under `_repoRoot`, enumerates `DocsRootPath.EnumerateContainedFiles(root, "*")`;
   - converts each file to a repository-relative path with `/` separators, and keeps it when `InventoryReachability.IsWithin(relative, planFolder)`, `!InventoryReachability.IsArchived(relative)`, and the name test passes;
   - de-duplicates with a `HashSet<string>(DocsRootPath.PathComparer)`, as `LeftoverTaskFiles` does.
3. **Name test** (D3): `name.Contains(".tasks.json", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".tasks.json", StringComparison.OrdinalIgnoreCase)`. Reuse the existing `TaskFileSuffix` constant.
4. **Finding:** `new Diagnostic(SidecarTaskFile, Severity.Error, $"'{relative}' is a copy of a task file left beside it: the conductor reads only '<plan>.tasks.json', so this file is dead weight that the merge would carry.", relative, relative, Hint: "Delete it. Write the task file in place at '<plan>.tasks.json', never through a temporary sibling.")`. The wording may be tightened, but the message names the path and the hint says to delete the file and write in place.
5. **Class remarks:** the third `<para>` still holds (one warning, the rest errors). Add one sentence saying the sidecar rule walks the plans folder rather than following a plan, so it catches a copy whose plan has already been archived.

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | `KW-DOC-TASKS-007`: Error; a file in the plans folder whose name holds `.tasks.json` followed by more characters; reported by `docs validate` and the MCP validate tool, with or without a plan beside it; names ending in `.tasks.json` are left to `KW-DOC-LIFECYCLE-005` | CI Pipelines | reference | `docs/ci-pipelines/rule-reference.md`, section "Plan task files", table row and intro sentence (written by T3) |
| H2 | The task-file lifecycle: the file is written in place, and a sidecar fails validation (`-007`) | DocGraph | index | `docs/plans/README.md`, the task-file paragraph above "Active Plans" (written by T3) |
| H3 | The conductor's task-file checks list `-007` | KyberSquad | architecture | `docs/kyber-squad/architecture.md`, the "duplication is checked, not trusted" bullet, and `docs/kyber-squad/onboarding.md`, the `docs validate` paragraph on task files (written by T3) |

## Test contract (test-first)

All commands run from the worktree root. Build once, then run the class with `--no-build`. Every run must report a non-zero test count.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T2 | `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs` (class `KyberWeave.Tests.PlanTaskFileValidatorTests`, new cases from T1) | `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release` then `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.PlanTaskFileValidatorTests"` | (a) Theory over the suffixes `.new`, `.tmp`, `.bak` and `~`. The control corpus holds the plan and its exact `.tasks.json`, and does not report `KW-DOC-TASKS-007`. The same corpus plus `6-Docs/plans/2026-11-07-ready-tasks.tasks.json<suffix>` reports `KW-DOC-TASKS-007`, exits 1, and the output contains the sidecar's path. (b) Orphan: a corpus holding `6-Docs/plans/2026-11-05-archived-plan.tasks.json.new`, for which no plan exists, reports `KW-DOC-TASKS-007` and exits 1. (c) Control: the plan, its exact task file and an unrelated `6-Docs/plans/notes.json` report no `KW-DOC-TASKS-007`. Every existing case still passes unchanged. | (a) and (b) fail: today nothing reads a sidecar, so `KW-DOC-TASKS-007` is absent and the exit is 0. (c) passes at RED, which is expected for a control. Failure output in the digest. | The same command passes with assertions unchanged and a non-zero total. |
| T3 | No test: documentation task | `kyber-weave docs validate .` and `kyber-weave docs drift .` (installed CLI) | The family is listed with `-007` in every place D8 names | n/a (no-test task, replaced by the docs gates) | Zero errors on both runs |
| T4 | No test: closeout | `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate .` and `docs drift .` before the archive move, then the same with `docs validate . --merge-ready` after it (branch build) | H1 to H3 present; plan archived with its closeout mapping; task file removed; no `KW-DOC-TASKS-007` on the repository corpus | n/a (no-test task, replaced by the docs gates) | Zero errors on every run |

## Implementation tasks

Every developer task ends with the worker's own `task-gate` run. Standards to fetch: `docs_get_coding_standard_for_technology('test')` for `test-dev`, and `('csharp')` for `csharp-dev`. Every test, build and tool command runs through `scripts/proc-guard.sh run --` from the worktree root. No worker runs the full test suite.

### T1 (test-dev): failing tests for `KW-DOC-TASKS-007`

**Session:** `red`. **Depends on:** none. **Required skills:** `test-dev`.

**Files:**
- `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs`

**Work.**
- Symbols in that file:
  - class `KyberWeave.Tests.PlanTaskFileValidatorTests`: a new constant `SidecarTaskFile = "KW-DOC-TASKS-007"` beside `MissingTaskFile`; new cases (a) `[Theory]` with `[InlineData(".new")]`, `[InlineData(".tmp")]`, `[InlineData(".bak")]` and `[InlineData("~")]`, (b) `[Fact]` for the orphan sidecar, and (c) `[Fact]` for the control (exact task file plus `notes.json`); one sentence in the class `<remarks>` naming `-007`.
  - class `KyberWeave.Tests.PlanTaskFileProbe`: an optional helper returning the sidecar path for a suffix.
- The fixture's registry already resolves `plan-index` to `6-Docs/plans/README.md`, because `ConfigRegConfig.Resolve` derives the folder indexes from the docs root, so no fixture setup is needed.
- Build each corpus with `PlanTaskFileProbe.Corpus()` and `fixture.Write(path, PlanTaskFileProbe.TaskFile())`, using the sidecar path for the sidecar. Run it through `Validate(fixture)` and assert with `AssertReports` / `AssertDoesNotReport`, plus `Assert.Equal(1, run.Exit)` for (a) and (b) and `Assert.Contains(<sidecar path>, run.Output)` for (a).
- Give each case a name that states its guarantee, for example `ASidecarCopyOfATaskFileIsKWDOCTASKS007`, `ASidecarWhosePlanIsGoneIsStillKWDOCTASKS007` and `TheTaskFileItselfAndOtherJsonAreNotKWDOCTASKS007`. Give each a `<summary>` naming issue 599's failure, a temporary sibling left behind.

**Acceptance:** the T2 row of the Test contract, RED column. Existing cases still pass, and the failure output is pasted into the digest.

**Commands:**
  - `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.PlanTaskFileValidatorTests"`

### T2 (csharp-dev): the sidecar walk in `PlanTaskFileValidator`

**Session:** `green`. **Depends on:** T1. **Required skills:** `csharp-dev`.

**Files:**
- `src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs`

**Work.** In class `PlanTaskFileValidator`: add the constant `SidecarTaskFile`; add one call in method `Validate` after the plan loop; add the private method `ReportSidecarTaskFiles`; add the plan-folder lookup beside `PlanningDirectories`; and add one sentence to the class `<remarks>`, per Design item 5. Implement Design items 1 to 5 and decisions D3 to D6. Follow the walk in `OpenWorkValidator.LeftoverTaskFiles` (docs roots, `DocsRootPath.EnumerateContainedFiles`, a relative path with `/` separators, a dedupe set). Do not change `OpenWorkValidator`, `DocsSchemaGate` or `KW-DOC-LIFECYCLE-005` (D7).

**Acceptance:**
- T1's tests pass with their assertions unchanged, and every existing `PlanTaskFileValidatorTests` and `OpenWorkValidatorTests` case passes.
- The branch build's `docs validate .` on the repository reports no `KW-DOC-TASKS-007`.
- Both format checks pass.

**Commands:**
  - `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.PlanTaskFileValidatorTests"`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.OpenWorkValidatorTests"`
  - `scripts/proc-guard.sh run -- dotnet format KyberWeave.sln whitespace --verify-no-changes -v minimal`
  - `scripts/proc-guard.sh run -- dotnet format KyberWeave.sln style --verify-no-changes --severity warn -v minimal`
  - `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate .`

### T3 (docs-dev): list `KW-DOC-TASKS-007` wherever the family is listed

**Session:** `docs`. **Depends on:** none. **Required skills:** `docs-dev`, `kyber-docs-authoring`.

**Files:**
- `docs/ci-pipelines/rule-reference.md`
- `docs/plans/README.md`
- `docs/kyber-squad/architecture.md`
- `docs/kyber-squad/onboarding.md`
- `docs/todo/mcp-docs-gate-unpinned-for-task-files.md`
- `docs/todo/README.md`

**Work.** Make these edits exactly, and nothing else:

- In the rule reference, section "Plan task files — `docs validate` and the docs MCP validate tool":
  - add a row after `KW-DOC-TASKS-006`: `` | `KW-DOC-TASKS-007` | Error | A copy of a task file left in the plans folder under another name, such as `<plan>.tasks.json.new` or `.tmp`, with or without its plan beside it. Names ending in `.tasks.json` are `KW-DOC-LIFECYCLE-005`'s | ``;
  - in the intro paragraph, change "All six run" to "All seven run", and extend "or a prompt that cannot be acted on" with "or a leftover copy of the file".
  - Nothing else changes, because pull request 600 edits this file (D8). Update `last-reviewed`.
- In the plans index, the paragraph that begins "A plan delivered by a conductor may carry a tracked task file": after the sentence ending "a Ready plan with no file warns `-006`.", add "The architect writes the file in place; a copy left beside it under another name, such as `<plan>.tasks.json.new`, fails `-007`." Do not touch **Active Plans**.
- In the Kyber-Squad architecture document, the bullet "**The duplication is checked, not trusted.**": "`KW-DOC-TASKS-001` to `-006`" becomes "`KW-DOC-TASKS-001` to `-007`", and the list before it gains "a copy of the file left beside it under another name". Update `last-reviewed`.
- In the Kyber-Squad onboarding document, the paragraph beginning "`docs validate` and the docs MCP validate tool check the file": after "A Ready plan with no task file warns (`-006`)", add ", a copy of the file left beside it under another name fails (`-007`)". Update `last-reviewed`.
- In the todo on MCP coverage of task files, the opening paragraph: change "so the six task-file rules reach the conductor by construction" to "so the seven task-file rules reach the conductor by construction".
- In the todo index, the row for that todo: "The six `KW-DOC-TASKS-*` rules" becomes "The seven `KW-DOC-TASKS-*` rules".

Do not edit ADR 0063 (D8). Run the docs gates with the installed `kyber-weave` CLI, not the branch build: T1 builds the same projects at the same time, and these edits need no new rule. The installed CLI's one `KW-CONFIG-002` warning (key `concurrent`) is expected.

**Acceptance:** the T3 row of the Test contract; `git diff --stat` touches only the six files above.

**Commands:**
  - `kyber-weave docs validate .`
  - `kyber-weave docs drift .`

### T4 (docs-dev): closeout

**Session:** `docs`. **Depends on:** T2, T3. **Required skills:** `docs-dev`, `kyber-docs-authoring`.

**Files:**
- `docs/plans/2026-11-12-sidecar-task-file-rule.md`
- `docs/archive/plans/2026-11-12-sidecar-task-file-rule.md`
- `docs/archive/plans/README.md`
- `docs/plans/README.md`
- `docs/plans/2026-11-12-sidecar-task-file-rule.tasks.json`

**Work.**
- Write the plan's completion summary and closeout mapping. Add the archive row at the top of the archived plan inventory, and return the plans index's Active Plans to its empty state.
- Confirm H1 to H3 are in place as written by T3, and map each to its destination in the closeout mapping.
- Record the ADR disposition `no ADR — ADR waiver: adds one rule to the existing task-file rule family under ADR 0063, with no new boundary, dependency or contract`.
- Archive the plan with `git mv` and remove the task file with `git rm` in the same commit.

**Acceptance:** the T4 row of the Test contract.

**Commands:**
  - `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate .` and `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .` before the archive move
  - `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .` after it

## Dependency graph

```text
T1 ──> T2 ──┐
            ├──> T4
T3 ─────────┘
```

T1 and T3 start at once. Their file scopes are disjoint, and T3 uses the installed CLI, so it builds nothing that T1 builds. T2 consumes T1's failing tests. T3 and T4 share the `docs` session, and T4 stays last.

## Risks

- **R1. Local noise.** An editor swap file of an open task file (for example `.<plan>.tasks.json.swp`) fails a local `docs validate` while the editor is open (D6). This is accepted, because the file is a real leftover if it is committed.
- **R2. Names the rule does not see.** A temporary name that drops `.tasks.json` (for example `<plan>.tasks.tmp`) is not caught, and one that ends in `.tasks.json` is caught only at merge by `KW-DOC-LIFECYCLE-005` (D3). Widening the rule later is a one-predicate change.
- **R3. Merge conflict with pull request 600.** Both edit `docs/ci-pipelines/rule-reference.md`. T3 keeps the edit to one row and two phrases, so whichever lands second resolves a small conflict.
## Out of scope

- The architect's guidance to write the task file in place (pull request 600).
- Sidecars in specification folders and in the archive folders (D4).
- Any change to the merge-time leftover rule, the open-work validator or the shared schema gate (D7).
- Amending ADR 0063 (D8).
- MCP-side test coverage of the task-file rules (already tracked as a todo; T3 only updates its count).

## Verification and review

- Each developer task runs its own commands above and its `task-gate`.
- The end-of-run review council runs under the `code-review` skill over the whole branch, and CI runs the full suites on the draft pull request.
- The pull request body links issue 599, notes the expected conflict with pull request 600 in `rule-reference.md`, and lists the low-confidence row D3.

## Completion summary

**Outcome.** Delivered. `PlanTaskFileValidator` reports `KW-DOC-TASKS-007` (Error) for a file in the plans folder whose name contains `.tasks.json` and does not end with it. The rule runs in `docs validate`, in `--merge-ready` and in the docs MCP validate tool, with or without a plan beside the file.

**Evidence.**
- T1 RED, commit `8c57652f`: five of twenty `PlanTaskFileValidatorTests` failed by design.
- T2 GREEN, commit `e1843389`: `PlanTaskFileValidatorTests` 20/20 and `OpenWorkValidatorTests` 10/10 pass; build has 0 warnings; both format checks pass; task gate PASS.
- T3 docs, commit `6565f59b`: the six documents that list the task-file rules now list `-007`.
- T4 closeout: before archival, `docs validate .` and `docs drift .` report zero findings on the branch build. The rule reference gained one sentence stating the walk's scope (recursive, `docs/archive/**` skipped, file system read, so untracked and git-ignored copies count), approved by the coordinator.

**Task file.** `docs/plans/2026-11-12-sidecar-task-file-rule.tasks.json` was never tracked by git. It was removed from disk before archival and is not in the closeout commit, so the `--merge-ready` leftover rule finds nothing beside the archived plan.

**Acceptance.** H1 to H3 are in place and mapped below. The plan is archived with `git mv`, and its Active Plans row is removed, with the empty-state sentence restored.

## Closeout mapping

Every harvested item and decision maps to its canonical destination. Documentation impact and impact components are as declared in the frontmatter (`extends-component`; DocGraph, CI Pipelines, KyberSquad). Last reviewed: 2026-11-12. Status: archived.

| Item | Canonical destination | Documentation impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| H1 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool): the `KW-DOC-TASKS-007` row, the recursive walk sentence, and "All seven run" | extends-component | CI Pipelines | 2026-11-12 | archived |
| H2 | [plans index, task-file paragraph](../../plans/README.md#plan-decisions-and-closeout-policy): the file is written in place and a sidecar fails `-007` | extends-component | DocGraph | 2026-11-12 | archived |
| H3 | [Kyber-Squad architecture, duplication is checked](../../kyber-squad/architecture.md) and [onboarding, `docs validate` on task files](../../kyber-squad/onboarding.md) list `-007` | extends-component | KyberSquad | 2026-11-12 | archived |
| D1 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool): the `KW-DOC-TASKS-007` row; the constant is `PlanTaskFileValidator.SidecarTaskFile` | extends-component | CI Pipelines | 2026-11-12 | archived |
| D2 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool): the `KW-DOC-TASKS-007` row, severity Error | extends-component | CI Pipelines | 2026-11-12 | archived |
| D3 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool): the row's name test and the `.tasks.json`-suffix hand-off to `KW-DOC-LIFECYCLE-005` | extends-component | CI Pipelines | 2026-11-12 | archived |
| D4 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool): the recursive walk sentence (plans folder, `docs/archive/**` skipped) | extends-component | CI Pipelines | 2026-11-12 | archived |
| D5 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool): the row's "with or without its plan beside it" | extends-component | CI Pipelines | 2026-11-12 | archived |
| D6 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool): the file system sentence, so untracked and git-ignored copies count | extends-component | CI Pipelines | 2026-11-12 | archived |
| D7 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool): the `KW-DOC-LIFECYCLE-005` paragraph, the merge-time half, unchanged | extends-component | CI Pipelines | 2026-11-12 | archived |
| D8 | [MCP task-file todo](../../todo/mcp-docs-gate-unpinned-for-task-files.md) (the count of task-file rules), with the [rule reference](../../ci-pipelines/rule-reference.md), [plans index](../../plans/README.md), [Kyber-Squad architecture](../../kyber-squad/architecture.md) and [Kyber-Squad onboarding](../../kyber-squad/onboarding.md) listing the family; ADR 0063 is not amended | extends-component | CI Pipelines, DocGraph, KyberSquad | 2026-11-12 | archived |
| D9 | Documentation waiver: the test pattern follows the existing `PlanTaskFileValidatorTests` class; test-fixture scoped, with no surviving invariant beyond the rule itself | extends-component | CI Pipelines | 2026-11-12 | archived |
| D10 | Documentation waiver: the development mode of this plan's delivery; a transient process with no surviving invariant | extends-component | CI Pipelines | 2026-11-12 | archived |
| D11 | [rule reference, Plan task files](../../ci-pipelines/rule-reference.md#plan-task-files--docs-validate-and-the-docs-mcp-validate-tool) — no ADR — ADR waiver: adds one rule to the existing task-file rule family under ADR 0063, with no new boundary, dependency or contract | extends-component | DocGraph | 2026-11-12 | archived |
| AP1 | Documentation waiver: the execution approval record, transient; its provenance stays in the Decisions section | extends-component | DocGraph | 2026-11-12 | archived |
