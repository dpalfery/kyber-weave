---
id: archive/specs/ship-gate-catalogue/tasks
title: Shipped review-gate catalogue tasks
doc-type: spec
status: archived
owner: dpalfery
last-reviewed: 2026-10-27
documentation-impact: extends-component
impact-components:
  - ReviewCouncil
  - DocGraph
  - KyberSquad
  - CI Pipelines
  - Distribution
component: ReviewCouncil
---

# Implementation Tasks

**Status:** Archived
**Archive Date:** 2026-10-27
**Phase status:** Delivered and verified (implementation ships on the same branch and PR #451; harvest H1–H10 in canonical docs, see [README](README.md#closeout-mapping))
**Development mode:** test-first
**Autonomy:** autonomous

These tasks build the approved [requirements](requirements.md) and [design](design.md). Where a
task says "per design §N", that section is the specification. The tasks add only file names,
test names and sequencing; no task makes a design decision.

## How every task runs

- **Test-first.** `test-dev` writes the test and records a failing run (RED) with the reason.
  Then the implementer named on the task makes it pass (GREEN) and tidies the code with the
  tests still green (REFACTOR). No production file is written before RED evidence exists.
- **Runner.**
  `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class KyberWeave.Tests.<Class>`.
  `global.json` selects Microsoft Testing Platform, so xUnit v3 filters with `--filter-class`.
  `scripts/fast-loop.sh` is for iteration only; its output is never evidence.
- **Exit gate.** Before handing off, each implementer runs the `task-gate` skill
  (`kyber-weave review gates --agent <slug> --task <task-id>`).
- **File ownership.** Each task owns the files it lists. Tasks in the same wave touch disjoint
  files, so they can run in parallel. A task that needs a file another task owns waits on
  that task.
- **Always-human paths (`KW-REVIEW-008`).** No task edits `products/kyber-squad/agents/**` or
  `.kyber-weave/kyber-weave.yml`. The design needs neither: the `task-gate` skill and the agent
  files are unchanged, and this repository's own `review:` section is out of scope. A worker
  who finds it needs one of those files stops and reports to the conductor.
- **Platform evidence.** Unit tests run on any OS. Only task 13 counts as the R9.6 Linux
  evidence. A local macOS run must be recorded as "macOS, not Linux" and does not satisfy
  R9.6.

## Order and parallelism

- **Wave A**, all independent: 1, 4, 5, 8, 9, 10.
- **Wave B:** 2 needs 1. 6 needs 1, 4 and 5.
- **Wave C:** 3 needs 2. 7 needs 2, 6 and 10. 11 needs 6. Tasks 3, 7 and 11 touch disjoint
  files.
- **Wave D:** 12 needs 1–9 and 11. 13 needs 3, 7 and 10. 14 needs 3, 7, 8, 9 and 11; a
  draft of 14 can start after wave A.
- **Then** 15 (final council review), which needs 1 to 14, and 16 (closeout), which needs 15.

## Tasks

- [ ] 1. **Gate config model and loader: `include`, `os` and `working-directory` on gates;
  unknown-key warnings** · R4.7, R5.1, R5.2, R5.7, R3.5 · design §3, §4 (loader part), §5
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`.
  - **Files:**
    - `src/KyberWeave.Core/Configuration/ReviewYamlSection.cs`
    - `src/KyberWeave.Core/Configuration/ReviewConfig.cs`
    - `src/KyberWeave.Core/Configuration/ReviewConfigLoader.cs`
    - `src/KyberWeave.Core/Configuration/KyberWeaveConfigLoader.cs` (passes the repository
      root to `ReviewConfigLoader.Merge` for the existence and containment checks)
    - `src/KyberWeave.Cli/Commands/CommandHelpers.cs` (surfaces `ReviewConfig.Warnings` as
      `KW-CONFIG-002`)
  - **Work:**
    - `ReviewGateYaml` and `ReviewGate` gain `Include`, `Os` and `WorkingDirectory`.
    - `ReviewTaskGateYaml` and `ReviewTaskGate` gain `WorkingDirectory`.
    - Add `ReviewConfig.Warnings`.
    - `working-directory` is refused when it is absolute, contains `..`, escapes the root
      (including through a symlink), does not exist, or appears in a gate that uses
      `{changed}`. Reuse `TaskGateSelection.IsWithin` and `LeadsOutsideThroughSymlink`.
      Task 1 may change only their visibility in
      `src/KyberWeave.Core/Review/TaskGateSelection.cs`. This does not clash with task 2,
      which owns that file, because task 2 runs after task 1.
    - Add a raw-YAML second pass that collects unknown keys on `review.gates[*]` and
      `review.task-gates.<agent>[*]`.
    - `ReviewConfig.ProductDefaults` stays empty.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/ReviewGateKeysConfigTests.cs`, class
      `ReviewGateKeysConfigTests`.
    - **Behaviours to cover:**
      - Council `include` and `os` parse.
      - An unknown `os` is refused.
      - Each `working-directory` refusal case yields a `KW-CONFIG-001` message naming the
        gate.
      - A valid `working-directory` is kept.
      - An unknown key yields one `KW-CONFIG-002` warning naming the key and the gate, and
        config load still succeeds.
      - `ReviewConfig.ProductDefaults.Gates` and `.TaskGates` are empty (R3.5).
    - **RED reason:** the properties do not exist (compile failure), or the keys are
      silently ignored.
    - **GREEN:** the class passes, and existing `ReviewConfigTests` and
      `ReviewTaskGatesConfigTests` still pass unchanged.

- [ ] 2. **Gate runner: per-gate working directory, `NotStarted` outcome, council selection**
  · R5.2, R5.3, R5.6, R8.1 · design §4 (selection), §6
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 1.
  - **Files:**
    - `src/KyberWeave.Core/Review/GateRunner.cs`
    - `src/KyberWeave.Core/Review/ReviewModel.cs` (`GateResult.Outcome`, enum `GateOutcome`)
    - `src/KyberWeave.Core/Review/ReviewJson.cs` (writes `outcome`)
    - `src/KyberWeave.Core/Review/TaskGateSelection.cs` (`PlannedTaskGate.WorkingDirectory`)
    - new `src/KyberWeave.Core/Review/CouncilGateSelection.cs`
  - **Work:**
    - `RunArgv` starts each gate in its resolved directory and sets `Outcome` on its three
      paths (completed, timed out, could not start).
    - `CouncilGateSelection.Plan(gates, changed?, os)` returns run and skipped lists.
      `changed == null` means the changed set is unknown, and every gate runs.
  - **Test contract:**
    - **New files:** `tests/KyberWeave.Tests/CouncilGateSelectionTests.cs` and
      `tests/KyberWeave.Tests/GateRunnerOutcomeTests.cs`; extend
      `tests/KyberWeave.Tests/ReviewJsonTests.cs`.
    - **Behaviours to cover:**
      - An include that matches runs.
      - An include that matches nothing is skipped with the reason.
      - A gate with no include always runs.
      - An `os` mismatch is skipped.
      - A null changed set runs every gate.
      - A gate whose `working-directory` is a subdirectory of a temp git repository runs
        there. Probe with `[git, rev-parse, --show-prefix]`, whose output names the
        subdirectory; this works on all three OSes and needs no shell.
      - A nonexistent program gives `Outcome = NotStarted`.
      - A timeout gives `TimedOut`.
      - The JSON carries `outcome`.
    - **RED reason:** types and members are missing.
    - **GREEN:** these classes plus `TaskGateSelectionTests` and `ReviewGatesAgentCommandTests`
      pass.

- [ ] 3. **`review gates` command: council changed set, `--base` and `--paths` without
  `--agent`, `KW-REVIEW-033` and `KW-REVIEW-034`** · R5.3, R5.4, R5.5, R8.1, R8.2 · design §4,
  §6
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 2.
  - **Files:**
    - `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs`
    - `src/KyberWeave.Cli/Commands/Review/ReviewSettings.cs` (`HasAgentOnlyOptions` covers
      only `--task` and `--fix`)
  - **Work:**
    - In `ExecuteRepositoryGates`, compute the changed set only when some council gate has
      `include`:
      - Use `--paths` if given; otherwise
        `ChangedFiles.ResolveSinceMergeBase(root, --base ?? "origin/HEAD")`.
      - Validate paths with `TaskGateSelection.ValidatePath`. A refused path is
        `KW-REVIEW-028`, as today.
      - If git fails, there is no changed set: emit Info `KW-REVIEW-034` and run every gate.
    - Report skips as `KW-REVIEW-026`.
    - Report `NotStarted` as `KW-REVIEW-033` instead of `KW-REVIEW-022`, on both the council
      and agent paths. Severity follows `blocking`.
    - Exit codes are unchanged.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/ReviewGatesCouncilCommandTests.cs`, using a temp
      git repository.
    - **Behaviours to cover:**
      - A gate with an unmatched include is skipped with `KW-REVIEW-026` and exit 0.
      - A matched include runs.
      - An unresolvable `--base` gives `KW-REVIEW-034` and the gate still runs.
      - `--paths` is accepted without `--agent`.
      - `--task` or `--fix` without `--agent` still exits 2.
      - A missing program gives `KW-REVIEW-033` and exit 1 when blocking.
    - Update `tests/KyberWeave.Tests/ReviewGatesAgentCommandTests.cs` where a case asserts
      that `--base` or `--paths` without `--agent` exits 2. That behaviour changes on
      purpose, and the change is noted in the test.
    - **RED reason:** the options are refused, and there is no selection or new ids.
    - **GREEN:** both classes pass.

- [ ] 4. **Catalogue data files and embedding** · R1.1–R1.7, R4.1, R4.8, R6.1, R6.3, R6.5 ·
  design §1, §11
  - **Implementer:** `csharp-dev`. These files are executable configuration consumed by
    `GateCatalogue` and the gate runner, not documentation, so they sit with the code owner.
    **Tests:** `test-dev`. **Depends on:** none (schema per design §1).
  - **Files:**
    - new `products/kyber-squad/standards/{csharp,typescript,rust,go,python}/gates.yml`
    - `src/KyberWeave.Core/KyberWeave.Core.csproj` (five `EmbeddedResource` entries,
      `LogicalName="Gates.<family>.yml"`)
  - **Work:**
    - Write the five family files exactly per design §11, with:
      - `schema: 1`
      - `technologies`, `roots` (markers, nesting), `gates` and `task-gates`, each with
        `role`, `agents` and `when`, as design §1 describes
      - a `sources:` URL for every gate id, copied from design §11
    - Node gates carry `os: unix` (D15). `golangci-lint` is `when: optional`.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/GateCatalogueResourceTests.cs`.
    - **Behaviours to cover:**
      - Each `Gates.<family>.yml` manifest resource exists and parses as YAML.
      - `schema` is 1.
      - Every gate id has a `sources` entry.
      - No file contains `KyberWeave.sln`, `dash/` or `src-tauri/Cargo.toml` (R4.8).
      - Every `run` and `fix` value is a YAML sequence, never a string (R1.4).
      - Every Node-program gate declares `os` (R1.7).
      - Each family's council gates include roles `format`, `lint`, a type check or build,
        and `test` (R1.3). The only exceptions are gates whose condition is `when:` (written
        commented out when unmet). The csharp lint gate is `format-analyzers` (design D21).
    - If `tests/KyberWeave.Tests/SquadPackAndReleaseTests.cs` enumerates embedded resources,
      `test-dev` extends it here.
    - **RED reason:** the resources do not exist.
    - **GREEN:** the class passes.

- [ ] 5. **Project root detector** · R4.2–R4.5 · design §2
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** none.
  - **Files:** new `src/KyberWeave.Core/Review/Catalogue/ProjectRootDetector.cs` and
    `ProjectRoot.cs`.
  - **Work:**
    - Enumerate with `git ls-files -z --cached --others --exclude-standard` through
      `ProcessRunner` (argv only), falling back to a filesystem walk.
    - Apply the skip list of design §2 on top of the listing.
    - Match markers (globs such as `*.sln`, `*.slnx`, and the paired
      `tsconfig.json`+`package.json`). Apply `outermost` or `each` nesting.
    - Use the single-`*.csproj` fallback for csharp.
    - Sort roots ordinally and enforce the cap of 8.
    - Derive slugs per design §2 (the repository root's slug is `root`; collisions get `-2`,
      `-3` and so on).
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/ProjectRootDetectorTests.cs`, using temp
      directories with and without `git init`.
    - **Behaviours to cover:**
      - No marker gives no roots.
      - Two `go.mod` files give two roots in ordinal order.
      - A Cargo workspace with members gives the outermost root only.
      - A `package.json` under `node_modules` is ignored.
      - A gitignored directory is ignored.
      - A committed `vendor/` directory is ignored.
      - Nine roots give `capped = true`.
      - Slug derivation and collisions.
      - Identical output on a second call (R4.5).
    - **RED reason:** the type is missing.
    - **GREEN:** the class passes.

- [ ] 6. **`GateCatalogue` loader and renderer** · R1.4, R1.6, R1.8, R2.4, R4.3, R4.4, R4.6,
  R6.1–R6.3, R6.5 · design §1, §2 (ids), §7 (rendered block)
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 1, 4, 5.
  - **Files:** new `src/KyberWeave.Core/Review/Catalogue/GateCatalogue.cs`,
    `GateCatalogueFamily.cs` and `RenderedReviewBlock.cs`.
  - **Work:**
    - Load the embedded families and refuse an unknown `schema`.
    - Map technologies to families.
    - Render a `review:` block for (technologies, roots, `global.json` MTP flag):
      - Substitute the placeholders `{{root}}`, `{{marker}}` and `{{root-glob}}`, leaving
        `{changed}` and `{task}` literal.
      - Evaluate `when` (`file`, `script` by reading `package.json`, `optional`).
      - Comment out gates whose condition is unmet, or whose family has no roots or is
        capped, with the reason.
      - Add the `# <technology>: <role> (<root>)` comments.
      - Assemble per-agent task gates in family order.
      - Copy `role: format` and `role: lint` gates to `test-dev`.
      - Split rust roots under a `src-tauri` directory to `tauri-dev`.
      - Choose the csharp test form, VSTest or MTP, from `global.json`.
    - The result lists the gate ids written and the gates commented out, with reasons.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/GateCatalogueRenderTests.cs`.
    - **Behaviours to cover:**
      - **R9.1:** each family rendered at root `.` loads through `KyberWeaveConfigLoader`
        with no error. All families rendered together have no duplicate id.
      - Typescript and react declared together write each council gate once, and supply both
        `typescript-dev` and `react-dev`.
      - Rust and tauri roots split by agent.
      - `test-dev` receives only format and lint gates. For csharp that is
        `format-whitespace`, `format-style` and `format-analyzers` (design D22).
      - Two roots give suffixed ids, and include globs are prefixed with the root.
      - A missing root comments out with the marker named.
      - An unmet `script:` comments out with the reason.
      - The csharp build task gate carries `--artifacts-path .agent_scratchpad/{task}/artifacts`
        (R6.5).
      - A technology without a family is reported in `NoFamily` (R1.8).
      - The MTP `global.json` switches to `--solution`.
    - **RED reason:** the type is missing.
    - **GREEN:** the class passes.

- [ ] 7. **`docs init` writes the review block** · R2.1–R2.8, R3.1–R3.3, R1.8, R6.4 · design §7
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 2, 6, 10.
  - **Files:**
    - new `src/KyberWeave.Core/Docs/Scaffolding/ReviewBlockWriter.cs`
    - `src/KyberWeave.Core/Docs/Scaffolding/DocsScaffolder.cs` (calls the writer after
      `WriteHostConfig`, with the `reviewGates` parameter)
    - `src/KyberWeave.Cli/Commands/Docs/DocsInitSettings.cs` (`--no-review-gates`)
    - `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs` (passes the flag; prints written,
      commented-out, no-family and missing-id lines plus the workflow pointer)
  - **Work:**
    - A top-level `review` key is detected with the column-0 regex.
    - **If present:** leave the file untouched (`--force` included) and print missing
      catalogue ids as Info.
    - **If absent:** render the block, validate it in memory with
      `KyberWeaveConfigLoader.TryLoad`, then append it with the file's newline style, or
      include it on creation.
    - If no family applies, or the opt-out was given, write nothing.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/DocsInitReviewGatesTests.cs`.
    - **Behaviours to cover:**
      - **R9.2:** for each family fixture from task 10, the written `review:` section equals
        `tests/fixtures/gate-catalogue/<family>/expected-review.yml`.
      - **R9.3 cases:**
        - An existing `review:` is byte-identical after `docs init` and after
          `docs init --force`.
        - A second run is byte-identical.
        - An existing file with no `review:` keeps every other byte, including CRLF.
        - `--no-review-gates` writes nothing and says so.
        - Only non-family technologies write nothing and print "no gates shipped for …".
        - `--kyber-standards` on a go-only fixture writes go gates and comments out the
          others with "no … found".
      - **R6.4:** after init on the rust fixture, `review gates --agent rust-dev --paths
        <changed .rs>` runs gates and does not emit `KW-REVIEW-027`.
    - **RED reason:** no review block is written.
    - **GREEN:** the class passes, and `DocsScaffolderTests` and `DocsInitCommandTests` still
      pass. Update an existing assertion only where it pinned the exact host-config text, and
      say why in the test.

- [ ] 8. **Squad task-gate hint** · R6.6 · design §9
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** none.
  - **Files:** `src/KyberWeave.Cli/Commands/Squad/SquadInstallCommand.cs` and
    `src/KyberWeave.Cli/Commands/Squad/SquadUpdateCommand.cs`. A shared helper, if any, goes
    in a new `src/KyberWeave.Cli/Commands/Squad/SquadTaskGateHint.cs`.
  - **Work:**
    - In project scope, load the project config without failing the install.
    - If `Review.TaskGates` is empty, print the single hint line from design §9.
    - In global scope, print nothing.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/SquadTaskGateHintTests.cs`, using the existing
      Squad fakes.
    - **Behaviours to cover:**
      - Project scope with no config shows the hint.
      - Project scope with empty task gates shows the hint.
      - Project scope with task gates shows no hint.
      - Global scope shows no hint.
      - A broken config still installs and shows the hint.
      - No configuration file is created or changed.
    - **RED reason:** no hint is printed.
    - **GREEN:** the class passes.

- [ ] 9. **Review-gate CI workflow template** · R7.1–R7.7 · design §10
  - **Implementer:** `github-devops`. **Tests:** `test-dev`. **Depends on:** none. The command
    line it calls (`--base` without `--agent`) lands in task 3. The template is a sample, so
    it can merge in any order within this change.
  - **Files:** new `templates/github-actions/kyber-weave-review-gate.yml`;
    `templates/github-actions/README.md`.
  - **Work:**
    - Follow design §10.
    - Look up the **real** current commit SHAs for:
      - `actions/checkout`
      - `actions/setup-dotnet`
      - `actions/setup-node`
      - `actions/setup-go`
      - `actions/setup-python`
      - the Rust toolchain action
    - Use the `gh api` releases and tags, and note the version tag in a trailing comment, as
      `kyber-weave-docs-gate.yml` does. Never invent a SHA.
    - Each toolchain step is `if: false`, with a comment naming its technology.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/ReviewGateWorkflowTemplateTests.cs`.
    - **Behaviours to cover:**
      - The file parses as YAML.
      - Every `uses:` is pinned to a 40-hex SHA.
      - Top-level and job `permissions` are only `contents: read`.
      - Checkout has `fetch-depth: 0`.
      - There is a `kyber-weave review gates` step with `--base`.
      - No `run:` block contains `${{ github.`; event values arrive through `env:` (R7.6).
      - There is one toolchain step per family, each disabled.
      - The README lists the file.
    - **RED reason:** the file does not exist.
    - **GREEN:** the class passes. As extra evidence, if `actionlint` is available, run it on
      the template and record the result.

- [ ] 10. **Fixture projects per family** · R9.2, R9.6
  - **Implementer:** `test-dev`. This is test data only. **Depends on:** none.
  - **Files:** new `tests/fixtures/gate-catalogue/<family>/clean/**` and
    `.../<family>/defects/**` for csharp, typescript, rust, go and python.
  - **Work:** build the smallest project that each family's gates accept:
    - **csharp:** a `.sln`, one class library and one xUnit test.
    - **typescript:** `package.json` with the `typecheck`, `lint` and `test` scripts and a
      `tsconfig.json`.
    - **rust:** a one-crate `Cargo.toml` with one test.
    - **go:** a `go.mod` with one test.
    - **python:** a `pyproject.toml` with `[tool.ruff]` and `[tool.mypy]` and one pytest
      test.

    For each family, `defects/` holds one copy per check, each with exactly one seeded defect
    (bad formatting, a lint hit, a type or build error, a failing test). The
    `expected-review.yml` files are written in task 7, not here. Add the fixture directories
    to `.gitattributes` or the `editorconfig` exclusions if this repository's format gates
    would otherwise reformat them. If that needs an edit to `.kyber-weave/kyber-weave.yml`,
    stop and report to the conductor (always-human).
  - **No-test task.** Validation is task 13 (the clean fixtures pass, every defect fails) and
    task 7 (rendered output matches).

- [ ] 11. **`kyber-weave review catalogue` command** · R3.4 · design §8
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 6.
  - **Files:**
    - new `src/KyberWeave.Cli/Commands/Review/ReviewCatalogueCommand.cs`
    - new `src/KyberWeave.Cli/Commands/Review/ReviewCatalogueSettings.cs`
    - `src/KyberWeave.Cli/Program.cs` (registers `review.AddCommand<ReviewCatalogueCommand>("catalogue")`
      beside `gates` at line ~256)
  - **Work:**
    - Render with `GateCatalogue` for the declared technologies, or for `--technology`, with
      roots detected in PATH.
    - Write to stdout only.
    - Exit codes: 0 when rendered, 2 for an unknown technology with a nearest-match hint, 1
      for a config error.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/ReviewCatalogueCommandTests.cs`.
    - **Behaviours to cover:**
      - The output equals the block `docs init` would write for the same fixture.
      - No file in the repository changes; compare a hash snapshot before and after.
      - `--technology gox` exits 2 and suggests `go`.
      - A broken config exits 1.
    - **RED reason:** the command is not registered.
    - **GREEN:** the class passes.

- [ ] 12. **Unit-test wiring for new test classes** · R9.7
  - **Implementer:** `test-dev`. **Depends on:** 1–9 and 11. Task 4 is included because it
    creates `GateCatalogueResourceTests`. Run this task after the new classes exist.
  - **Files:** `scripts/fast-loop-classes.txt`, regenerated with
    `scripts/fast-loop.sh --regenerate`.
  - **No-test task.** Validation: `scripts/fast-loop.sh` lists and runs the new classes.

- [ ] 13. **R9.6 real-toolchain run on Linux** · R9.6, R1.5, GAP-2 · design §11, testing
  strategy 9
  - **Tests:** `test-dev` writes the test class. **CI job:** `github-devops`. **Probe fixes:**
    `csharp-dev`, in task 4's files. **Depends on:** 3, 7, 10.
  - **Files:**
    - new `tests/KyberWeave.Tests/GateCatalogueToolchainTests.cs`
    - new `.github/workflows/gate-catalogue-toolchains.yml` (decision T7)
  - **Work:**
    - **Test class** (`test-dev`):
      - Skip with `Assert.Skip` unless `KW_GATE_TOOLCHAIN=1`.
      - For each family, copy `clean/` to a temp git repository, run `docs init`, then run
        `kyber-weave review gates` (council) and `review gates --agent <slug> --paths <file>`.
        All must pass.
      - For each defect copy, the matching gate must fail with `KW-REVIEW-022`, never
        `KW-REVIEW-033`.
    - **CI job** (`github-devops`), a workflow and job, both named `gate-catalogue-toolchains`:
      - Runs on `ubuntu-latest` and installs:
        - .NET from `global.json`
        - Node LTS, plus `npm ci` in the typescript fixture
        - stable Rust with `rustfmt` and `clippy`
        - stable Go
        - Python 3 with `pip install ruff mypy pytest`
      - Every action is pinned to a real SHA.
      - Sets `KW_GATE_TOOLCHAIN=1` and runs
        `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class KyberWeave.Tests.GateCatalogueToolchainTests`.
      - Triggers on pull requests that touch `products/kyber-squad/standards/*/gates.yml`,
        `src/KyberWeave.Core/Review/**` or the fixtures, and on `workflow_dispatch`.
  - **How it runs:** in GitHub Actions on Linux. The workflow run URL and per-family
    pass/fail are the evidence, recorded in the Evidence section below. A local run on macOS
    may be used to iterate, but must be recorded as "macOS, not Linux" and does not close
    this task.
  - **Probes.** Every row marked *probe* in design §11 is confirmed here:
    - `gofmt -d` exits 1 on a diff.
    - `dotnet format <sln> whitespace|style|analyzers --verify-no-changes` fails on a defect.
    - tsc exits 1.
    - clippy accepts `--manifest-path`, and `--target-dir` in the task form.

    A probe that fails is fixed or dropped in that family's `gates.yml`, by `csharp-dev`, with
    the source updated. Tasks 4, 6 and 7 tests are then re-run, and the decision is logged in
    design Decisions (R1.5).
  - **Release gate (R1.5, design D23).** No probe row ships until this task confirms it on
    Linux. Task 15 (final review) and task 16 (closeout) do not start until this task is
    GREEN, and the feature is not reported complete without it.
  - **Test contract:**
    - **Class:** `GateCatalogueToolchainTests`.
    - **RED:** the first run on the CI job, before any probe fixes, records which checks fail
      for the wrong reason. If all are already correct, RED is the class failing before
      task 7 existed, which is the expected missing-`review:` failure.
    - **GREEN:** every clean fixture passes and every defect fails its intended gate on
      `ubuntu-latest`.

- [ ] 14. **Documentation updates (harvest H1–H10)** · R10.1–R10.5 · design "Design harvest"
  - **Implementer:** `docs-dev`. **Depends on:** 3, 7, 8, 9, 11. Drafting may start after
    wave A.
  - **Files:**
    - `docs/code-review/architecture.md` (H1–H3)
    - `docs/ci-pipelines/rule-reference.md` (H4: `KW-CONFIG-002`, `KW-REVIEW-033`,
      `KW-REVIEW-034`)
    - `docs/docgraph/onboarding.md` (H5)
    - `docs/cli-command-surface.md` (H6: `review catalogue`, `--base` and `--paths` on
      council gates)
    - `docs/kyber-squad/architecture.md` (H7)
    - `docs/ci-pipelines/workflows-runbook.md` (H8)
    - `docs/install.md` (H9)
    - `docs/todo/task-gate-config-review.md` (H10)
    - new `docs/todo/windows-node-gate-probe.md` (GAP-1) and its row in `docs/todo/README.md`
  - **Work:**
    - Describe the shipped behaviour, not the plan.
    - Say plainly in `install.md` that the CI workflow is a copy-ready template and that no
      command writes `.github/workflows/`.
    - Add `code-refs` for `GateCatalogue`, `ProjectRootDetector`, `ReviewBlockWriter`,
      `CouncilGateSelection` and `ReviewCatalogueCommand`.
  - **No-test task.** Validation: `kyber-weave docs validate .` and `kyber-weave docs drift .`
    report no findings.

- [ ] 15. **Final council review** · all
  - **Agent:** `code-reviewer`. **Depends on:** 1–14.
  - **Work:** run the review council and `kyber-weave review gates` over the whole
    accumulated change.
  - **Done when:** the verdict is APPROVE, or each REQUEST_CHANGES finding is fixed by the
    owning task's implementer and re-reviewed.

- [ ] 16. **Closeout** · all
  - **Agent:** `docs-dev`, through the conductor. **Depends on:** 15.
  - **Work:**
    - Verify every acceptance criterion R1.1–R10.5 against the implementation and the
      evidence (tasks 1–14, the task-13 Linux run, the task-15 verdict).
    - Confirm the H1–H10 harvest landed at the declared scale (`extends-component`; ReviewCouncil,
      DocGraph, KyberSquad, CI Pipelines, Distribution).
    - Add the `Closeout mapping` table mapping every decision (D1–D23, T1–T7) and harvest id
      (H1–H10) to a live governed document or a waiver.
    - Set frontmatter and body statuses to archived, and move the spec to
      `docs/archive/specs/ship-gate-catalogue/`.
    - Update the inventory in `docs/specs/README.md`.
    - Confirm `kyber-weave docs validate . --merge-ready` passes `KW-DOC-LIFECYCLE-003` and
      `-004`.

## Coverage

| Requirement | Tasks |
|---|---|
| R1.1–R1.7 | 4, 6, 13 |
| R1.8 | 6, 7 |
| R2.1–R2.8 | 7 |
| R3.1–R3.3 | 7 |
| R3.4 | 11 |
| R3.5 | 1 |
| R4.1–R4.6, R4.8 | 4, 5, 6 |
| R4.7 | 1 (load-time checks), 2 (run-time working directory) |
| R5.1–R5.7 | 1, 2, 3 |
| R6.1–R6.5 | 4, 6, 7 |
| R6.6 | 8 |
| R7.1–R7.7 | 9 |
| R8.1–R8.2 | 2, 3 |
| R9.1–R9.5 | 1, 2, 3, 6, 7 |
| R9.6 | 13 |
| R9.7 | every implementation task, 12 |
| R10.1–R10.5 | 14 |

## Evidence

Filled in as tasks complete: RED and GREEN runs (command, OS, result), the task-13 Linux
workflow run URL with per-family results, probe outcomes, and the task-15 verdict.

## Decisions

Decisions D1–D23 live in [design.md](design.md#decisions) and
[requirements.md](requirements.md#decisions). Task-phase decisions:

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| T1 | Who owns `products/kyber-squad/standards/*/gates.yml`? | `csharp-dev` (task 4) | They are executable configuration that `GateCatalogue` renders into commands the runner starts, and probe fixes in task 13 change them. They are not documentation. | autonomous: low-confidence, no authoritative source |
| T2 | How does R9.6 run on Linux? | A gated xUnit class (`KW_GATE_TOOLCHAIN=1`) in a new `ci.yml` job on `ubuntu-latest` with all five toolchains | Keeps the default unit run fast; it gives Linux evidence a macOS developer cannot produce locally | autonomous: low-confidence, no authoritative source |
| T3 | The `--base`/`--paths` without `--agent` exit-2 assertions in `ReviewGatesAgentCommandTests` | Updated on purpose in task 3 | Required by design §4; `--task` and `--fix` keep exit 2 | autonomous: recommendation accepted (design approved) |
| T4 | Are the tasks approved, with T1–T3? | Yes, the tasks phase is approved and T1–T3 are accepted. The final approve-and-execute gate is **not** recorded: the owner asked only for the specification, so execution waits for the owner's go-ahead. | Conductor instruction | autonomous: recommendation accepted (conductor AUTONOMOUS_APPROVAL, 2026-10-27) |
| T5 | Final approve-and-execute gate | Approved: execute the tasks as written (waves and dependencies unchanged), on the same branch and pull request (#451), so the specification is archived with its build work | The owner said "execute the tasks" in chat on 2026-10-27, relayed by the conductor | owner approved in chat, 2026-10-27 (not autonomous) |
| T7 | Should the R9.6 job live in `ci.yml`? | No. It goes in a separate `.github/workflows/gate-catalogue-toolchains.yml` with path filters and `workflow_dispatch`. | `ci.yml` has one workflow-wide trigger (push to main, every pull request, a schedule), so a job inside it cannot filter by path without adding a third-party action. A separate file keeps `ci.yml` unchanged. | autonomous: recommendation accepted |
| T6 | Where do the fixture projects live? | `tests/fixtures/gate-catalogue/<family>/`, outside the test project | `tests/KyberWeave.Tests/KyberWeave.Tests.csproj` uses the SDK default compile globs (no `Compile Remove`), so fixture `.cs` files under it, including seeded defects, would break the test build. Found while writing the task packets. | autonomous: recommendation accepted |

## Design harvest

The durable design items (H1–H10, tabulated authoritatively in
[design](design.md#design-harvest)) and the tasks that built them:

| ID | Design item | Built by |
|---|---|---|
| H1 | Catalogue format, families, placeholders | Tasks 4, 6 |
| H2 | Root detection and id derivation | Tasks 5, 6 |
| H3 | `working-directory`, council `include` and `os`, changed set | Tasks 1, 2, 3 |
| H4 | New rule ids `KW-CONFIG-002`, `KW-REVIEW-033`, `KW-REVIEW-034` | Tasks 1, 2, 3 |
| H5 | `docs init` review block, `--no-review-gates`, re-run rule | Task 7 |
| H6 | `review catalogue` command | Task 11 |
| H7 | Squad task-gate hint | Task 8 |
| H8 | Review-gate CI workflow | Task 9 |
| H9 | What an installing repository gets | Task 14 |
| H10 | Answer to the .NET agent-mapping question | Task 14 |

## Closeout mapping

No ADR: the tasks execute the approved design, which refines the existing review
architecture and established patterns — no ADR, ADR waiver: implementation of a refined
existing architecture; task-phase decisions below are transient execution records.

| ID | Canonical destination |
|---|---|
| T1 | [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| T2 | Documentation waiver — Linux-run mechanics; transient execution record, no surviving invariant. |
| T3 | Documentation waiver — test-update note preserved in this file; no surviving invariant. |
| T4 | Documentation waiver — phase approval record; historical, no surviving invariant. |
| T5 | Documentation waiver — owner approval relay; historical, no surviving invariant. |
| T6 | Documentation waiver — fixture-location rationale; test-scaffolding detail, no surviving invariant. |
| T7 | [Workflows runbook](../../../ci-pipelines/workflows-runbook.md#the-review-gate-template) |
| H1, H2 | [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| H3 | [Review council architecture](../../../code-review/architecture.md#council-gates), [DocGraph onboarding](../../../docgraph/onboarding.md) |
| H4 | [Rule reference](../../../ci-pipelines/rule-reference.md), [Workflows runbook](../../../ci-pipelines/workflows-runbook.md) |
| H5 | [DocGraph onboarding](../../../docgraph/onboarding.md), [Install](../../../install.md) |
| H6 | [CLI command surface](../../../cli-command-surface.md), [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| H7 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md), [Review council architecture](../../../code-review/architecture.md#the-shipped-gate-catalogue) |
| H8 | [Workflows runbook](../../../ci-pipelines/workflows-runbook.md#the-review-gate-template) |
| H9 | [Install](../../../install.md), [DocGraph onboarding](../../../docgraph/onboarding.md) |
| H10 | [Task-gate config review](../../../todo/task-gate-config-review.md), [Windows Node gate probe](../../../todo/windows-node-gate-probe.md) |
