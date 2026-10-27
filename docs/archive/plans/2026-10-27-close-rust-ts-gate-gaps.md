---
id: plans/2026-10-27-close-rust-ts-gate-gaps
title: "Close this repository's Rust and TypeScript gate gaps in kyber-weave.yml"
doc-type: plan
status: archived
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-10-27
development-mode: test-first
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# Close this repository's Rust and TypeScript gate gaps

**Status: Archived — complete 2026-10-27.** Closeout mapping is in the Closeout section. Approved for execution 2026-10-27 (AP0, AP1, `autonomous: recommendation accepted`). Frontmatter `status: archived` follows the closed status vocabulary (`KW-DOC-SPEC-002`).

**Development mode:** `test-first` (packet value; no user opt-out). **Autonomy:** `autonomous` (packet value and repository Config Reg `<autonomy>`).

**Scale:** `extends-component`, component `ReviewCouncil`.

**Branch:** `feat/close-rust-ts-gate-gaps`, stacked on draft PR #449, which is itself stacked on PR #440 (D10).

**Scope:** "Part A" of the intake assessment only: this repository's own gate configuration in `.kyber-weave/kyber-weave.yml`. Part B is out of scope (D9).

> Discovery (authoring pass 1): codegraph 3, docs 1, direct reads 16, greps 11.
> - CodeGraph returned `ReviewTaskGatesConfigTests` (the pinned developer set, `RepositoryTaskGates`), `KyberWeaveConfigLoader.TryLoad`, `GateRunner.Run`/`RunTaskGates`, `ReviewGatesCommand`, `TaskGateSelection`, and `ReviewConfigLoader.ParseGates`/`ParseAgentTaskGates`.
> - Docs: `docs_explore` returned `docs/code-review/architecture.md` (system diagram, gates and council).
> - Direct reads, each for something the indexes do not return verbatim or in full: the three architect references; `.kyber-weave/kyber-weave.yml` (config, not indexed code); `docs/plans/README.md` (index shape); `.github/workflows/ci.yml` lines 630-650, 755-908 (CI tray job and the ts-gates mirror comment; not indexed code); `docs/standards/rust/README.md` lines 100-144 and `docs/standards/tauri/README.md` lines 100-133 (gate prose to verify); `docs/code-review/architecture.md` lines 370-449 (Task gates section, beyond the docs budget); `src/KyberWeave.Core/Configuration/ReviewYamlSection.cs` lines 1-50 (YAML shape of `review.gates`, to confirm there is no `include`); `ReviewTaskGatesConfigTests.cs` lines 474-533 (the repository-gate test pattern beyond the explore budget); the archived plan `2026-10-27-declare-rust-tauri-go-standards.md` (template precedent); `AGENTS.md` lines 60-99 (tray gate commands).
> - Greps (narrow, self-gathered): `.github` for `review gates`; repository for cargo/clippy mentions; `REVIEW.md` for gate prose; `docs/` for agent and gate names; agents for `task-gate`; the YAML deserializer settings (`IgnoreUnmatchedProperties`); tests for repository `review.gates` pins.
>
> Validation note: the planner harness has no process tool. `docs-dev` runs `docs validate .` and `docs drift .` on this plan. The plan file must be `git add`ed before it can be archived with `git mv`.

---

## 1. Problem and goal

### 1.1 Problem

- **Council gates miss Rust.** `review.gates` runs .NET and dash TypeScript gates only. CI runs `cargo fmt --check`, `cargo clippy -- -D warnings` and `cargo test` on the tray crate (`.github/workflows/ci.yml` lines 893-903). The whole-change council can therefore approve a tray change that CI rejects, and `REVIEW.md` rule 4 ("don't restate what a gate already enforces", naming `clippy`) points at a gate the council never runs.
- **Two developer agents have no task gates.** `typescript-dev` (Node TypeScript under `dash/`) and `rust-dev` both carry the `task-gate` skill, but `review.task-gates` declares nothing for them. Their `task-gate` reports `NONE_DECLARED` (`KW-REVIEW-027`), so their changes reach review with no executed evidence.

### 1.2 Goal

- `review.gates` gains `cargo-fmt`, `cargo-clippy` and `cargo-test`, each with `--manifest-path dash/tray/src-tauri/Cargo.toml`, matching CI.
- `review.task-gates` gains `typescript-dev` (dash typecheck and lint) and `rust-dev` (cargo fmt and clippy), reusing the existing anchors. `test-dev` gains the cargo fmt and clippy gates (D5).
- `ReviewTaskGatesConfigTests` pins all of it.
- Canonical docs that describe the gates stay accurate.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Which Rust gates join the council's `review.gates`, and in what form? | Three entries, appended after `ts-reachable`, under a comment that names the CI job they mirror:<br>- `cargo-fmt`: `[cargo, fmt, --manifest-path, dash/tray/src-tauri/Cargo.toml, --check]`<br>- `cargo-clippy`: `[cargo, clippy, --manifest-path, dash/tray/src-tauri/Cargo.toml, --, -D, warnings]`<br>- `cargo-test`: `[cargo, test, --manifest-path, dash/tray/src-tauri/Cargo.toml]`<br>All `blocking: true`. Ids are the same as the task-gate ids (separate namespaces, as `format-whitespace` already is). | CI runs the same three commands from `working-directory: dash/tray/src-tauri` (ci.yml 893-903). `--manifest-path` is the argv-only equivalent, because `run` cannot `cd` (the yml header comment). The tauri standard already states that equivalence (docs/standards/tauri/README.md lines 122-130). The fmt and clippy argv are exactly the existing `tauri-dev` task-gate argv. CI's `cargo build --release --locked` is Linux-only release work and stays out. | autonomous: recommendation accepted (conductor packet, 2026-10-27; source = ci.yml and the tauri standard) |
| D2 | Council gates have no `include`, so the cargo gates run on every review. Least-risk handling now? | Accept the always-run cost. Do not add an `include` key to any `review.gates` entry. | Verified in code: `ReviewGateYaml` has only `id`, `run`, `blocking`, `timeout-seconds` (`ReviewYamlSection.cs` 28-37), and `GateRunner.Run` runs every gate unconditionally. The YAML deserializer uses `IgnoreUnmatchedProperties()` (`MarkdownFrontmatterReader.Deserializer`), so an `include:` written on a council gate would be **silently ignored**: it would look conditional and still run every time. That is worse than an honest always-run gate. Cost: the council already runs a no-incremental Release build, the full .NET test suite and InspectCode; an incremental cargo clippy and test on one crate is small beside that, and only the first cold build is long (D3). Rejected: `blocking: false` (a council that cites clippy as enforced must fail on it); deferring to Part B (leaves the CI gap open). Conditional council gates are Part B (D9). | autonomous: recommendation accepted (conductor packet, 2026-10-27; source = `ReviewYamlSection`, `GateRunner`, `MarkdownFrontmatterReader`) |
| D3 | Timeout for the council cargo gates | `cargo-clippy` and `cargo-test` set `timeout-seconds: 1800`. `cargo-fmt` keeps the 900-second default. | A timeout is reported as a failed blocking gate. A cold Tauri 2 build compiles several hundred crates (the release SBOM counted 486 Cargo components, archived plan 2026-10-21-issue-383), and clippy and test each build. 1800 s leaves headroom on a cold machine. Cheap to reverse: one number per gate. | autonomous: low-confidence, no authoritative source |
| D4 | What are the `typescript-dev` and `rust-dev` task gates? | `typescript-dev: [*dash-typecheck, *dash-lint]`.<br>`rust-dev: [*cargo-fmt, *cargo-clippy]`.<br>Aliases only; no new anchor, include or argv. | `typescript-dev` owns non-React `.ts` (agent trigger); in this repository that is the dash engine and scripts, which `*dash-paths` covers. It does not get `*tray-ui-typecheck`: the tray UI is React, owned by `react-dev`. `rust-dev` excludes `src-tauri` by its trigger, but the tray crate is the only first-party Rust here (rust standard scope; `dash/tray/vendor/glib-0.18.5` is vendored), so `*tray-rust-paths` is the only Rust path a gate can cover. A `rust-dev` change outside it reports `NONE_APPLICABLE`, which is honest. | autonomous: recommendation accepted (conductor packet, 2026-10-27; source = agent triggers and the yml anchors) |
| D5 | Does `test-dev` also get the cargo gates? | Yes: append `*cargo-fmt` and `*cargo-clippy` to `test-dev`, and reword its comment ("Tests are .NET, TypeScript and the tray's Rust"). | `test-dev` authors Rust tests (`#[cfg(test)]` modules and `dash/tray/src-tauri/tests/`; agent trigger and rust standard "Tests"). Today its Rust test edits pass no gate before review. Include-scoped, so a .NET or TS test task skips them. Cheap to reverse. | autonomous: low-confidence, no authoritative source |
| D6 | Is a `cargo-test` task gate added? | No. `cargo test` runs in the council only (D1). | The task-gate cost model in this file runs format, lint, typecheck and build per task, never a test suite: `dotnet test` and `ts-test` are council-only. A per-task `cargo test` is a whole-crate run that a concurrent worker's edit can fail (the whole-project caveat, yml lines 195-199), and the test-first RED/GREEN evidence already runs the task's tests. | autonomous: recommendation accepted (conductor packet, 2026-10-27; source = the yml's task-gate set and comment) |
| D7 | `go-dev` and `python-dev` task gates? | Neither. Record why in the yml comment above the task gates and pin `go-dev` alongside `python-dev` in the "no task gates" test. | No `go.mod`, `.go` or Python project exists in this repository (archived standards plan, §4 "Go reality"; the existing `python-dev` exclusion test). A gate with no code to match is dead config. The Go standard already says the change that introduces Go adds its gates. | autonomous: recommendation accepted (conductor packet, 2026-10-27) |
| D8 | Which agent edits the yml (T2)? | `github-devops`. | The edit is build and CI gate commands that must mirror `.github/workflows/ci.yml`; that is `github-devops`'s trigger ("CI/CD pipelines, build diagnostics", "a build ... pipeline"), and it can run the cargo commands to prove them. `docs-dev`'s trigger is a `.md` deliverable; it edited this yml before only for `ontology.technologies`, a docs-declaration change. `github-devops`'s own task gates are .NET-only and report `NONE_APPLICABLE` for a yml edit, so its exit evidence is the T2 runs listed in its acceptance. | autonomous: low-confidence, no authoritative source |
| D9 | Part B | Out of scope and tracked separately as a specification: a shipped per-technology gate catalogue, conditional council gates (an `include` on `review.gates`, which D2 shows is silently ignored today), and a CI template. Nothing here implements or pre-empts it. | Part B changes engine code, a config schema and shipped product content; Part A is this repository's config only. | autonomous: recommendation accepted (conductor packet, 2026-10-27) |
| D10 | Branch and PR | Own PR from `feat/close-rust-ts-gate-gaps`, targeting #449's branch. Retarget as the stack merges. | #449 declares the Rust standard this plan cross-checks. | autonomous: recommendation accepted (conductor packet, 2026-10-27) |
| D11 | Expected review outcome | The council returns `NEEDS_HUMAN` under `KW-REVIEW-008`, because `.kyber-weave/kyber-weave.yml` is in `review.policy.always-human`. Expected, not a defect; the owner reviews before merge. | Same outcome as the archived 2026-10-25 and 2026-10-27 plans. | autonomous: decided, source = `.kyber-weave/kyber-weave.yml` policy |
| D12 | Do the standards and REVIEW.md need changes? | Verified consistent; only small accuracy edits (H1-H3). `REVIEW.md` is not edited. | Rust standard "Commands" gives `cd dash/tray/src-tauri && cargo fmt --check && cargo clippy -- -D warnings && cargo test`, equivalent to D1. Tauri standard "Gates" lists the exact `--manifest-path` argv of D1. Rust standard "Existing exceptions" ("the gate is `-D warnings` only") matches D1. `REVIEW.md` line 296 ("Anything ... `clippy` ... already enforces") becomes true for the council once D1 lands; line 352 (task-gate covers single tasks) stays true. Stale: `docs/code-review/architecture.md` line 443-444 says anchors are shared "for the .NET format gates and the build", already untrue (dash and cargo anchors exist) and more so after D4-D5. | autonomous: decided, source = the documents read in discovery |
| AP0 | Phase gates before execution: intake recommendation (PLAN), mode (test-first), scale (`extends-component`, ReviewCouncil), Test contract (§5) | All accepted as recommended. | Conductor `AUTONOMOUS_APPROVAL` under resolved autonomy `autonomous`, 2026-10-27. `docs validate .` and `docs drift .` are run by `docs-dev` (no process tool in the planner harness). | autonomous: recommendation accepted (conductor AUTONOMOUS_APPROVAL, 2026-10-27) |
| AP1 | Approve and execute | Approved. The plan is Ready, to execute as T1 → T2 → T3 → council review → T4. | Conductor `AUTONOMOUS_APPROVAL` for the final gate. The `KW-REVIEW-008` NEEDS_HUMAN result at review is expected (D11); the owner reviews before merge. | autonomous: recommendation accepted (conductor AUTONOMOUS_APPROVAL, 2026-10-27) |

## Design harvest

Scale: `extends-component`; impact component `ReviewCouncil`.

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| H1 | ReviewCouncil | architecture | [`docs/code-review/architecture.md`](../../code-review/architecture.md) (Task gates, the anchor paragraph near line 443; Configuration) | (a) Replace "This repository does so for the .NET format gates and the build" with the real set: .NET format and build, dash typecheck and lint, tray cargo fmt and clippy. (b) State that `review.gates` entries take no `include` and run on every review; an `include` key there is ignored by the loader, not honoured (D2). |
| H2 | ReviewCouncil | coding-standard | [`docs/standards/rust/README.md`](../../standards/rust/README.md) (Commands) | One sentence: the council runs these three as `review.gates` `cargo-fmt`, `cargo-clippy`, `cargo-test`; `tauri-dev`, `rust-dev` and `test-dev` run fmt and clippy as task gates; `cargo test` is council-only (D6). Machines running the council need `cargo` on PATH and, on Linux, the Tauri system packages CI installs. |
| H3 | ReviewCouncil | coding-standard | [`docs/standards/tauri/README.md`](../../standards/tauri/README.md) (Gates) | One sentence: the three cargo commands above are the declared council gates of the same ids. |

No ADR expected. ADR waiver: adds gate entries through the existing `review.gates` and `review.task-gates` mechanisms, with no new boundary, dependency or schema.

## 4. Investigation findings

- **Council gates are unconditional.** `GateRunner.Run` loops over every `ReviewConfig.Gates` entry. `ReviewGateYaml` has no `include`. The deserializer ignores unknown keys, so an `include` would be silently dropped (D2).
- **Council gate timeout key** is `timeout-seconds`, default 900 (`ReviewConfigLoader.ParseGates`). Task gates use `timeout`.
- **CI tray job** (`ci.yml` 755-908): matrix macOS, Windows, Ubuntu 24.04. Linux installs `TAURI_APT_PACKAGES` and builds the tray UI first. Steps: `cargo fmt --check`, `cargo clippy -- -D warnings`, `cargo test` from `dash/tray/src-tauri`; Linux adds `cargo build --release --locked`.
- **Nothing in CI runs `kyber-weave review gates`.** The only reference is the `ts-gates` job comment (line 640), which mirrors `ts-typecheck`, `ts-test`, `ts-lint`. The new council gates run where a council runs (developer machines and agent runs), not in CI.
- **Existing anchors.** `&dash-typecheck`, `&dash-lint` (`&dash-paths`) under `react-dev`; `&cargo-fmt`, `&cargo-clippy` (`&tray-rust-paths: ["dash/tray/src-tauri/**"]`) under `tauri-dev`.
- **Agents.** `typescript-dev`, `rust-dev`, `go-dev`, `python-dev` all load the `task-gate` skill. Only `go-dev` and `python-dev` stay without gates (D7).
- **Test pins.** `RepositoryConfigDeclaresDeveloperTaskGates` pins the exact developer key set (nine agents today); adding two agents fails it until the yml changes, which is the RED. `RepositoryConfigDeclaresNoTaskGatesForPythonOrNonDeveloperAgents` lists `python-dev` and non-developers. `ReviewBuildGateCarriesNoNodeReuseAndNoSharedCompilation` is the pattern for asserting a repository `review.gates` entry. No test pins the `review.gates` id list or count.

## 5. Test contract (test-first)

Runner for every row: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class "*ReviewTaskGatesConfigTests"`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 (a) | `tests/KyberWeave.Tests/ReviewTaskGatesConfigTests.cs`, existing `RepositoryConfigDeclaresDeveloperTaskGates` | above | The developer set is the nine current agents plus `typescript-dev` and `rust-dev`, each with at least one gate. | Fails: key sets differ (the two new agents are missing). | Passes with the array unchanged from T1. |
| T1 (b) | Same file, existing `RepositoryConfigDeclaresNoTaskGatesForPythonOrNonDeveloperAgents` | above | `go-dev` joins `python-dev` and the non-developers as agents with no task gates; summary names Go (D7). | Guard: passes before and after (no RED expected; it pins D7). Record the pass. | Still passes. |
| T1 (c) | Same file, new fact `TypeScriptAndRustDevelopersReuseTheSharedDashAndTrayGates` | above | `typescript-dev` gate ids are exactly `[dash-typecheck, dash-lint]`; `rust-dev` ids are exactly `[cargo-fmt, cargo-clippy]`. Each gate's `Include`, `Run` and `Fix` equal the same-id gate under `react-dev` (dash) or `tauri-dev` (cargo). `rust-dev` `cargo-fmt` include is `["dash/tray/src-tauri/**"]`. Neither declares `cargo-test` (D6). Messages name the agent and gate. | Fails: `typescript-dev` / `rust-dev` absent. | Passes unchanged. |
| T1 (d) | Same file, new fact `TestDevRunsTheTrayRustFormatAndLintGates` | above | `test-dev` contains `cargo-fmt` and `cargo-clippy`, each equal in `Include`, `Run`, `Fix` to `tauri-dev`'s; its existing gates are still present. | Fails: `test-dev` has no cargo gates. | Passes unchanged. |
| T1 (e) | Same file, new fact `ReviewGatesRunTheTrayRustSuiteThatCiRuns` (repository `review.gates` via `KyberWeaveConfigLoader.TryLoad(KyberWeaveTestPaths.ToolRoot)`, as `ReviewBuildGateCarriesNoNodeReuseAndNoSharedCompilation` does) | above | `review.gates` has `cargo-fmt`, `cargo-clippy`, `cargo-test` with exactly the D1 argv, `Blocking == true`; `cargo-clippy` and `cargo-test` `TimeoutSeconds == 1800`, `cargo-fmt` 900 (D3). The council `cargo-fmt` and `cargo-clippy` `Run` equal `tauri-dev`'s task-gate `Run`. | Fails: no `cargo-*` council gate. | Passes unchanged. |
| T2 | Same class | above | T1 (a), (c), (d), (e) turn GREEN from the yml edit alone. | T1's RED | Whole class passes; no assertion changed. |
| T3 | No test: documentation-only | `docs validate .` and `docs drift .` | H1-H3 prose matches the yml. | n/a (documentation task) | Both report zero findings. |
| T4 | No test: closeout | `docs validate . --merge-ready`, `docs drift .` | Plan archived with full mapping. | n/a | Both clean after archive. |

## 6. Tasks

### T1: RED config-pin tests (`test-dev`)

- **Objective.** Add or extend the five tests of §5 T1 (a)-(e).
- **Files.** `tests/KyberWeave.Tests/ReviewTaskGatesConfigTests.cs` only.
- **Acceptance.**
  - Follow the file's style: `RepositoryTaskGates()` for task gates; `KyberWeaveConfigLoader.TryLoad(KyberWeaveTestPaths.ToolRoot)` for `review.gates`; `Assert.Single` by id; messages that name the agent and gate.
  - Do not weaken or reorder existing assertions beyond adding the two developer slugs and `go-dev`.
  - Run the §5 command. Record RED for (a), (c), (d), (e) with their failure messages, and the pass for (b).
  - `task-gate` passes.
- **Dependencies.** None.
- **Skills.** `test-dev`, `task-gate`.

### T2: GREEN yml edit (`github-devops`, D8)

- **Objective.** Make T1 GREEN by editing `.kyber-weave/kyber-weave.yml` only.
- **Files.** `.kyber-weave/kyber-weave.yml`.
- **Steps.**
  1. Append the D1 gates after `ts-reachable`, with D3 timeouts and a comment: mirrors the "Tray Rust gates" CI job; `--manifest-path` because argv cannot `cd`; runs on every review because `review.gates` has no `include` (and an `include` key is ignored, so do not add one); needs `cargo` and, on Linux, the Tauri system packages.
  2. Add `typescript-dev: [*dash-typecheck, *dash-lint]` after `react-dev`, and `rust-dev: [*cargo-fmt, *cargo-clippy]` after `tauri-dev` (aliases must follow their anchors). One-line comment each, stating D4.
  3. Append `*cargo-fmt` and `*cargo-clippy` to `test-dev`; reword its comment (D5).
  4. Add the D6 and D7 rationale to the task-gates comment block in one or two sentences each.
- **Acceptance.**
  - §5 command: whole class passes; T1 assertions untouched.
  - `cargo fmt --manifest-path dash/tray/src-tauri/Cargo.toml --check`, `cargo clippy --manifest-path dash/tray/src-tauri/Cargo.toml -- -D warnings`, `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml` each exit 0 on the current tree (proves the new blocking gates do not fail on arrival). Record durations.
  - `dotnet run --project src/KyberWeave.Cli -c Release -- review gates . --agent rust-dev --paths dash/tray/src-tauri/src/lib.rs --task t2-rust` and `... --agent typescript-dev --paths dash/src/cli/register.ts --task t2-ts` both pass with their gates run, not skipped.
  - `git diff --stat` shows only the yml changed.
  - Do not run the full `review gates` suite here; the end-of-run council runs it.
- **Dependencies.** T1 (RED recorded first).
- **Skills.** `github-devops`, `task-gate`.

### T3: Documentation accuracy (`docs-dev`)

- **Objective.** Land H1-H3.
- **Files.** `docs/code-review/architecture.md` (H1), `docs/standards/rust/README.md` (H2), `docs/standards/tauri/README.md` (H3). Bump `last-reviewed` on each.
- **Acceptance.** Every gate id, agent and argv named matches the T2 yml. No `REVIEW.md` edit (D12). `docs validate .` and `docs drift .` zero findings.
- **Dependencies.** T2 (consumes the final ids and agents).
- **Skills.** `docs-dev`, `app-docs-standard`.

### T4: Closeout (`docs-dev`)

- **Objective.** Map D1-D12, AP0-AP1 and H1-H3 in a Closeout mapping, then archive.
- **Steps.**
  1. Confirm the whole-change council ran; record its verdict (`NEEDS_HUMAN`, `KW-REVIEW-008`, D11) and the gate results including the three new cargo gates.
  2. Write the Closeout mapping; ADR waiver as in Design harvest.
  3. `git add` this plan if it is not yet tracked, then `git mv` it to `docs/archive/plans/`, set `status: archived`, and move its index row to Archived Plans.
  4. `docs validate . --merge-ready` and `docs drift .` clean.
- **Dependencies.** T3 and the end-of-run council review.
- **Skills.** `docs-dev`.

## 7. Dependency graph

```text
T1 (RED) ─> T2 (GREEN) ─> T3 (docs) ─> [council review] ─> T4 (closeout)
```

Strictly serial: T2 consumes T1's RED, T3 consumes T2's final ids. Each task is small; no split adds parallelism worth its overhead.

## 8. Risks

- **Machines without Rust.** A council run on a machine without `cargo` fails three blocking gates ("could not start 'cargo'"). Root `AGENTS.md` already lists the tray Rust gates as required for tray work; H2 states the prerequisite.
- **Linux without Tauri system packages.** `cargo clippy` and `cargo test` fail to build without `libwebkit2gtk-4.1-dev` and the rest of `TAURI_APT_PACKAGES` (ci.yml 766), and CI also builds the tray UI first on Linux. A Linux council run must have those installed; H2 states it. The owner tests on Linux, so T2 records whether its runs were macOS or Linux.
- **Review time.** Always-run cargo gates add a cold build once per machine and incremental time afterwards (D2, D3).
- **Concurrent cargo.** A task-gate cargo run and a council cargo run share `target/`; cargo's build lock serialises them rather than corrupting output.
- **Stacked PR drift.** If #449 or #440 change the standards or this yml before merge, T2 and T3 rebase.

## 9. Verification gates

- `dotnet run --project src/KyberWeave.Cli -c Release -- review gates . --out artifacts/gates.json` (the declared suite, now including the cargo gates), run by the conductor or council.
- The §5 runner command.
- The three tray cargo commands (T2).
- `docs validate . --merge-ready` after archive, and `docs drift .`.

## 10. Review and closeout

- Whole-change `code-reviewer` council at the end. Expected verdict `NEEDS_HUMAN` (`KW-REVIEW-008`, D11); the owner reviews before merge.
- `docs-dev` closeout in T4 maps every decision and harvest id to a governed document or a documentation waiver.

## Closeout

**Closed 2026-10-27 by `docs-dev` (T4).** Links below are written for the archived location, `docs/archive/plans/`.

### Verification

- **Whole-change council** (after T2 and T3): `NEEDS_HUMAN` under `KW-REVIEW-008`, as D11 expected. 27 of 27 declared gates passed, including `cargo-fmt`, `cargo-clippy`, `cargo-test`, and the `dotnet test` gate that runs `ReviewTaskGatesConfigTests`. That gate run is the GREEN evidence for the T1 test class.
- **T1 RED** was recorded before the yml edit (run record). **T2 GREEN** is the yml edit alone; no assertion was changed.
- **T2 commands** (the three cargo commands and the two `review gates --agent` runs) were run by `github-devops` and recorded in its run record. They were not re-run at closeout.
- **T3 documentation** was checked against the yml at closeout: gate ids, argv, agents and timeouts match. One correction was made at closeout: `docs/code-review/architecture.md` now states the per-task timeout key (`timeout`) and the council cargo timeout (1800 s).
- **Platform:** every run was on macOS. Linux was not exercised.

### Open items at closeout (owner)

These do not block closeout. They are recorded for the owner's review before merge.

1. `TestDevRunsTheTrayRustFormatAndLintGates` checks the gate ids only. T1(d) asks for `Include`, `Run` and `Fix` equal to `tauri-dev`'s. The shared YAML anchors make them equal today, so the gap is in the test, not the config.
2. An unrelated re-wrap of an existing assertion near line 606 of `tests/KyberWeave.Tests/ReviewTaskGatesConfigTests.cs`.
3. `cargo-clippy` and `cargo-test` now block every council review, including documentation-only changes, and need `cargo` (and the Tauri system packages on Linux). This is the cost D2 accepted.

## Closeout mapping

ADR waiver for the whole plan: `no ADR — ADR waiver: adds gate entries through the existing review.gates and review.task-gates mechanisms, with no new boundary, dependency or schema.`

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | Council cargo gates and argv: [Review council architecture, Task gates](../../code-review/architecture.md#task-gates); [Rust standard, Commands](../../standards/rust/README.md); [Tauri standard, Gates](../../standards/tauri/README.md) | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D2 | Council gates take no `include` and always run: [Review council architecture, Task gates](../../code-review/architecture.md#task-gates) (council paragraph) | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D3 | Cargo clippy and test timeout 1800 s: [Review council architecture, Task gates](../../code-review/architecture.md#task-gates) (council paragraph) | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D4 | `typescript-dev` and `rust-dev` task gates: [Review council architecture, Task gates](../../code-review/architecture.md#task-gates) (anchor paragraph) | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D5 | `test-dev` cargo format and lint gates: [Review council architecture, Task gates](../../code-review/architecture.md#task-gates) (anchor paragraph) | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D6 | No `cargo-test` task gate: [Review council architecture, Task gates](../../code-review/architecture.md#task-gates); [Rust standard, Commands](../../standards/rust/README.md) | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D7 | `go-dev` and `python-dev` declare no task gates: [Review council architecture, Task gates](../../code-review/architecture.md#task-gates); the [Go standard, Gates](../../standards/go/README.md#gates) already states the rule for a future Go change | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D8 | Documentation waiver: which agent made one edit. Transient, with no surviving invariant. | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D9 | Documentation waiver for the Part B pointer: transient, tracked outside this plan. The durable limit it depends on (no `include` on council gates) is mapped at D2. | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D10 | Documentation waiver: delivery logistics, transient. | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D11 | Documentation waiver: a one-time review result, transient. Recorded above in Verification. | extends-component | ReviewCouncil | 2026-10-27 | archived |
| D12 | Standards and REVIEW.md consistency: [Rust standard](../../standards/rust/README.md) and [Tauri standard](../../standards/tauri/README.md) (H2, H3). `REVIEW.md` unchanged, as D12 decided. The stale anchor sentence in the architecture document was replaced (H1). | extends-component | ReviewCouncil | 2026-10-27 | archived |
| AP0 | Documentation waiver: approval provenance, transient. | extends-component | ReviewCouncil | 2026-10-27 | archived |
| AP1 | Documentation waiver: approval provenance, transient. | extends-component | ReviewCouncil | 2026-10-27 | archived |
| H1 | Architecture harvest: [Review council architecture, Task gates](../../code-review/architecture.md#task-gates) | extends-component | ReviewCouncil | 2026-10-27 | archived |
| H2 | Rust standard harvest: [Rust standard, Commands](../../standards/rust/README.md) | extends-component | ReviewCouncil | 2026-10-27 | archived |
| H3 | Tauri standard harvest: [Tauri standard, Gates](../../standards/tauri/README.md) | extends-component | ReviewCouncil | 2026-10-27 | archived |
