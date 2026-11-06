---
id: plans/2026-11-06-block-dev-agent-full-test-runs
title: Block dev agents from starting the full test suite, mechanically
doc-type: plan
status: archived
component: ReviewCouncil
owner: dpalfery
created: 2026-11-06
last-reviewed: 2026-11-06
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [ReviewCouncil, CI Pipelines, KyberDash]
---

# Block dev agents from starting the full test suite, mechanically

**Status: Archived.** Closed 2026-11-06; see the Closeout section at the end. Approved for execution on the conductor's "approve and execute" gate (decision D9). **Development mode:** `test-first` (packet input).
**Autonomy:** `autonomous` (packet input; every open item decided by recommendation and recorded below).
**Scale:** `extends-component`, components `ReviewCouncil`, `CI Pipelines`, `Distribution`, `KyberDash`.

## Problem and goal

**Problem.** The rule is settled in prose: only the review council and CI run the full .NET suite
(and the full dash and tray suites). No dev agent runs them, cross-cutting changes included; dev
agents run a targeted set. The uncommitted edits already say so in `AGENTS.md` ("The inner loop"),
the Squad agents and skills, and `docs/standards/test/README.md`, and tests pin that wording. Nothing
mechanical stops an agent from starting a full run. Today a dev agent that runs
`scripts/fast-loop.sh --full` or `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj`
simply runs it.

**Goal.** A dev agent's attempt to start a full run fails at once, with a message that names the targeted
alternative. The council's gates and CI still run the full suite unchanged. A human at an interactive
terminal can still run `--full`. The machine-wide `--slot dotnet-test` queue keeps its behaviour.

## Decisions

Every open item was decided by recommendation under the autonomous-decisions rule. Provenance is
`autonomous: recommendation accepted` (packet instruction). The approval of the Test contract and the
execute gate is recorded in D9, from the conductor's `AUTONOMOUS_APPROVAL` input. Items marked **trade-off** are the ones David
should read first.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Where does the guard live? | **RECOMMENDED:** three layers that share one definition of a full run. (a) `scripts/proc-guard.sh run` refuses a full-run argv; (b) the `review gates` CLI refuses gates marked `full-suite: true`; (c) `scripts/fast-loop.sh --full`, `--scoped` and `--regenerate` refuse before any build. Not the Arbiter. | The Arbiter evaluates dispatch events only (its harness table lists `Agent`, `task`, `spawn_agent`, `runSubagent` and similar as the matched tools). A shell `dotnet test` never reaches it, so it cannot be the enforcement point. Per-harness hooks are the follow-up for the residual gap (D6). `proc-guard.sh` is the one place every wrapped test start already passes (AGENTS.md "process watchdog"). | autonomous: recommendation accepted |
| D2 | How is the council told apart from a dev agent? | **RECOMMENDED:** a full run is allowed when (i) the caller passes the flag `--council` (the review council and CI do; the flag is a command-line flag, never an environment variable), or (ii) the caller's stdin is an interactive terminal (a human). Anything else is a dev-agent full run and is refused. **Trade-off:** a PTY-backed agent shell looks like a terminal, and an agent that types `--council` passes. | Environment variables are rejected: AGENTS.md says watchdog limits change by flag, never by environment variable, and an env switch that lifts a guard is the same kind of knob. Flags are visible in config and in the test-policy suite. Agents' Bash tools are not verified to lack a TTY, so the terminal test is a live-check item (gap G1). | autonomous: recommendation accepted |
| D3 | What counts as a full run? | **RECOMMENDED:** (a) `dotnet test` whose target is `KyberWeave.Tests.csproj` or `KyberWeave.Tests.dll`, with no `-class`, `-method`, `-trait`, `-treenode-filter` or `--filter*` argument; (b) `npm --prefix dash run test` with no path argument after `--`; (c) `cargo test` with no positional filter (flag values skipped); (d) the review gates `test`, `ts-test` and `cargo-test` gates; (e) `fast-loop.sh --full`, `--scoped`, `--regenerate`. Not full: the RepoPolicy project, `--class` and `--method` runs, `npm run test:related`, and `dotnet build`. | Keeps the policy suite (about 30 s, per the `test-policy` gate comment) and every targeted run available. The classifier is one function (`proc-guard.sh classify`), so the rule is tested in one place and the CLI and fast-loop reuse it through the same argv shapes. | autonomous: recommendation accepted |
| D4 | Which exit code does a refusal return? | **RECOMMENDED:** `proc-guard.sh` exits **89** for a refused full run, a new code that joins 86, 87, 88, 124 and 143. The refusal prints the targeted alternative on stderr. The review gates CLI reports a refused gate as a blocking failure with the same alternative in its message. | 89 is unused (`proc-guard.sh` header). A distinct code lets a caller tell a refusal from a cap trip (86) or a queue timeout (88). | autonomous: recommendation accepted |
| D5 | How does the CLI know which gates are full-suite? | **RECOMMENDED:** a gate key `full-suite: true` in `.kyber-weave/kyber-weave.yml`, parsed by the review gate model, set on `test`, `ts-test` and `cargo-test`. Not a hard-coded id list. | A host may rename or add a full-suite gate; a key follows the gate, a list does not. The key is pinned by a RepoPolicy test so a new full-suite gate cannot ship unmarked. | autonomous: recommendation accepted |
| D6 | What is left open? | **RECOMMENDED:** accept and record three residuals, and file a todo for the per-harness hook (Claude `PreToolUse` on Bash) at closeout. (1) A raw, unwrapped `dotnet test` started by a dev agent is not seen; AGENTS.md already requires the wrapper, and only a harness hook can see the raw call. (2) A PTY-backed agent shell passes the terminal test (D2). (3) An agent that types `--council` passes (D2). | Closing (1) to (3) mechanically needs a per-harness shell hook, which the Arbiter's dispatch-only hooks do not give. That is a separate, larger decision and is not in this plan's scope. | autonomous: recommendation accepted |
| D7 | Does CI change? | **RECOMMENDED:** no change to `.github/workflows/ci.yml`. Its `dotnet test` steps are raw, not wrapped, so the guard does not see them. Only the CI workflows and the review-gate template that call `review gates` or a wrapped full-run command get `--council` (gap G2 lists the one to confirm). | CI runs the full suite today and must keep doing so. The template `templates/github-actions/kyber-weave-review-gate.yml` runs `review gates` on a host's CI, which is non-interactive, so it needs `--council`. | autonomous: recommendation accepted |
| D8 | Is a new ADR needed? | **RECOMMENDED:** yes. Create **ADR 0056** at closeout (next free number, `docs/adr/` index has up to 0055). It records the boundary: full runs reserved for the council and CI; caller identity by flag and terminal; the residuals in D6. No ADR waiver. | The decision creates a lasting process boundary with named alternatives (the Arbiter, env variables, per-harness hooks), which is what an ADR is for. ADR 0047 covers the watchdog chokepoint and is amended by a pointer only. | autonomous: recommendation accepted |
| D9 | Is the Test contract below approved? | **RECOMMENDED:** approve as written. It names one test surface per task, the RED evidence each must show, and targeted commands only. | Under autonomy the conductor records approval by `AUTONOMOUS_APPROVAL: <gate id>` when it dispatches the plan. Until then the plan stays Draft. | autonomous: recommendation accepted (approve and execute gate, `AUTONOMOUS_APPROVAL` from the conductor, 2026-11-06; also approves the Test contract) |
| D10 | G1: is the PTY residual (a PTY-backed agent shell passes the terminal test) acceptable? | **RECOMMENDED and accepted:** yes, acceptable until the per-harness hook follow-up lands. Keep a live check as a closeout step: confirm whether each harness's Bash tool runs with a TTY. | Carried into the closeout verification gates and the D6 follow-up todo. Does not change any task text. | autonomous: recommendation accepted |
| D11 | G2: is `.github/workflows/gate-catalogue-toolchains.yml` a council caller that needs `--council`? | **Accepted as written:** confirm first, then add `--council` if it calls `review gates` as the council. Carried into T4 (github-devops). | Confirm-first step, owned by T4. | autonomous: recommendation accepted |
| D12 | G3: does `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` pin the code-review skill wording? | **Accepted as written:** check before editing the skill; if pinned, update the pin in the same task. Carried into T4 (docs-dev). | Confirm-first step, owned by T4. | autonomous: recommendation accepted |
| D13 | G4: what exit code does a refused blocking gate return from the review gates CLI? | **Accepted as written:** confirm against `docs/cli-command-surface.md#exit-codes` and reuse the existing blocking-failure code. Carried into T3 (csharp-dev). | Confirm-first step, owned by T3. | autonomous: recommendation accepted |
| D14 | G5: how does `code-reviewer` route between `review gates` and `--agent` task gates? | **Accepted as written:** read it first; T4 adds only the `--council` flag and does not change routing. Carried into T4 (docs-dev). | Confirm-first step, owned by T4. | autonomous: recommendation accepted |
| D15 | The CI review-gate template needs `--council`, but the repository declares no github-actions coding standard, so the github-devops worker could not edit a workflow under its own rule. What now? | **RECOMMENDED and approved:** a single-flag exception. Add a comment and a `--council \` continuation to the `review gates` call in `templates/github-actions/kyber-weave-review-gate.yml`. No new steps, secrets, permissions or action versions. | The repository declares standards for csharp, go, rust, tauri, test and typescript only (checked with the docs standards lookup, 2026-11-06). The missing github-actions standard is not closed by this plan; it is an open gap for the owner. | autonomous: recommendation accepted (the worker's own recommendation was yes) |
| D16 | Does the pre-push self-review in `products/kyber-squad/skills/code-review/SKILL.md` pass `--council`? | **RECOMMENDED:** no. Only the end-of-run council (`code-reviewer`) appends `--council`. The self-review runs the plain gates command. A refused full-suite gate is reported as "not run here, CI runs them", not as a code finding. The refusal reuses the existing diagnostic `KW-REVIEW-022`; no new rule id is allocated. | If the self-review passed `--council`, an author could copy the command and bypass the guard. The verdict logic is unchanged. No authoritative source exists for the "not run here" wording, so this is a low-confidence call. | autonomous: low-confidence, recommendation accepted |
| D17 | After the first council pass found bypasses, what counts as a full run? | **RECOMMENDED, amended:** strip leading `env`, `timeout`, `nice`, `time`, `command` and `exec` wrappers. `dotnet test` is full unless it is narrowed (a filter flag) or aimed at another project. Full targets are: no target, a solution, a directory, the `KyberWeave.Tests` project or its dll. `npm` is full for the dash `test` script with no path after `--`, in every `--prefix` spelling. `cargo` skips a leading `+toolchain`. Ambiguous means full. `sh -c` strings are not parsed. | The first council found wrapper, bare `dotnet test`, `--prefix` spelling and `.sln` bypasses. The conductor reproduced each and the classifier was fixed. Residuals are listed under Known limits. A denylist classifier is best effort, so this is a low-confidence call. | autonomous: low-confidence, recommendation accepted |

## Investigation findings

**Discovery:** codegraph 1 (`codegraph_explore` over the proc-guard, fast-loop and review-gate names), docs 3 (`docs_status`, two `docs_explore`), direct reads 13. Direct reads of indexed code: `ReviewGatesCommand.cs` and `ReviewSettings.cs` were read with Grep only, because `codegraph_explore` returned the RepoPolicy test files and not these CLI files; `ProcGuardTtyTests.cs` was read in part, for the stdin pattern, because codegraph did not return it. All other direct reads are of scripts, YAML, markdown and architect references that the indexes do not cover. Read tree: `/Users/dave/git/personal/claude/kyber-weave`, branch `fix/full-suite-council-and-ci-only`, 57 uncommitted edits. Those edits are preserved; no task reverts them.

**Verified against the tree.**

- `scripts/proc-guard.sh`. `run` takes `--max-node`, `--max-tree`, `--timeout`, `--tty`, and the slot flags `--slot`, `--slot-count`, `--slot-wait`, `--task`, `--slot-dir`. The `--slot` path is taken only when the caller names a slot, so today only the test gate and `fast-loop.sh --regenerate` queue. Exit codes: 86 cap, 87 leftovers, 88 slot wait, 124 timeout, 143 signal, 2 usage. Stdin is redirected from `/dev/null` when it is a terminal and `--tty` is absent. `run_guarded` starts the command only after argument parsing, root validation and the process-table check, so a refusal placed after those and before the slot wait starts nothing.
- `scripts/fast-loop.sh`. `--full` is `exec dotnet run ... review gates . --all`. `--scoped` is `exec dotnet run ... review gates .`. `--regenerate` runs the product and policy DLLs through `proc-guard.sh run --slot dotnet-test --slot-wait 900` with no filter. The no-argument mode runs the fast tier with `-class` filters, so it is partial. `--class` and `--method` are partial.
- `.kyber-weave/kyber-weave.yml`. `review.gates` `test` argv is `scripts/proc-guard.sh run --slot dotnet-test --slot-wait 900 -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --coverlet ...` with `timeout-seconds: 1800`. `test-policy` is `proc-guard run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/...` with no slot. `ts-test` is `proc-guard run -- npm run --prefix dash test -- --coverage`. `cargo-test` is `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml` with no wrapper. The `task-gates` section runs no test command, so dev agents' task gates never run a full suite.
- `review gates` flags (`ReviewSettings.cs`): `-o|--out`, `--stop-on-failure`, `--agent`, `--paths`, `--base`, `--task`, `--fix`, `--all`. There is no `--council` flag today. `--all` runs every declared gate unscoped. The council's documented call is `kyber-weave review gates . --base <ref> --out artifacts/gates.json` (`products/kyber-squad/skills/code-review/SKILL.md`).
- Arbiter. `docs/kyber-arbiter/architecture.md` harness table: every matched tool is a dispatch (`Agent`, `task`, `spawn_agent`, `runSubagent`, `invoke_subagent`, `Task`, `run_subagent`). None sees a shell command. Confirms D1's rejection.
- `scripts/fast-loop.sh` and `docs/dash/runbook.md#running-tests-safely` already document "not for dev agents" for `--full` and `--scoped`. The rule has never been enforced.
- `.github/workflows/ci.yml` runs raw `dotnet test` (lines 162, 173, 316) and `scripts/proc-guard.sh check-leftovers`. It does not call `review gates`. `templates/github-actions/kyber-weave-review-gate.yml` calls `review gates` (grep match), and so does `.github/workflows/gate-catalogue-toolchains.yml` (grep match, not yet read; gap G2).
- Existing tests. `tests/KyberWeave.RepoPolicy.Tests` holds `ProcGuardScriptTests`, `ProcGuardSlotTests`, `ProcGuardTtyTests`, `ProcGuardKillScopeTests`, `ProcGuardDotnetServersTests`, `ReviewGateProcGuardWiringTests`, `FastLoopScriptTests`. Classes keep the `KyberWeave.Tests` namespace. `tests/KyberWeave.Tests` holds `ReviewGatesCouncilCommandTests` and `ReviewGatesAgentCommandTests`, which run `review gates` and must keep passing (see T3).
- ADR 0047 covers the process chokepoint and the test slot. Its consequence section names the slot and points at `docs/dash/runbook.md#taking-turns-the-test-slot`.

**Watchdog precedent (lead answered).** AGENTS.md: "The caps change only by flag, never by environment variable." The rule is about caps. It also constrains how the guard tells a council from a dev agent: an environment switch that lifts the guard would be the same kind of knob, so D2 uses a flag and a terminal test.

## Design

Three layers, one definition (D3).

- **Layer A, `scripts/proc-guard.sh`.** Add `classify -- <argv>`, which prints `full` or `partial` and exits 0 and starts nothing. Add `run --council`. In `run`, when the argv classifies as full and neither `--council` nor an interactive stdin (`[[ -t 0 ]]`) is present, print the refusal with the alternative and exit 89 before the slot wait.
- **Layer B, `review gates` CLI.** Add `--council` to `ReviewSettings`. Gates with `full-suite: true` are a pure selection step: when `--council` is absent and the CLI's stdin is not a terminal, those gates are reported as refused (blocking failure, message naming `review gates . --agent <name>` and `scripts/fast-loop.sh --class 'KyberWeave.Tests.<Area>*'`). Other gates run.
- **Layer C, `scripts/fast-loop.sh`.** Before any build, `--full`, `--scoped` and `--regenerate` exit 89 with the alternative when stdin is not a terminal. A human at a terminal passes.
- **Council identity.** The `test` and `ts-test` gate argv gain a literal `--council` after `run`, so the watchdog accepts the council's own call. `cargo-test` is not wrapped and relies on Layer B. The council's invocation (`code-review` skill, `code-reviewer` agent, the review-gate template) adds `--council`.
- **Slot.** Unchanged. The `--slot dotnet-test` queue applies after the guard admits a run, exactly as today.

## Design harvest

| Design item | Target component | Target doc type | Target location |
|---|---|---|---|
| Full-run definition, refusal exit 89, `classify` subcommand, `--council` flag | Distribution | reference | `docs/cli-command-surface.md` (exit codes, `review gates --council`) |
| Refusal behaviour, terminal test, targeted alternatives | ReviewCouncil | standard | `docs/standards/test/README.md#commands` |
| `full-suite: true` gate key and `--council` | ReviewCouncil | reference | `docs/configuration.md#review` |
| Inner-loop rule and the refusal message | ReviewCouncil | operations (agent guidance) | `AGENTS.md` ("The inner loop", "Before opening a PR") |
| Watchdog refusal and the KyberDash runbook section | KyberDash | runbook | `docs/dash/runbook.md#running-tests-safely` |
| CI and template callers pass `--council` | CI Pipelines | runbook | `docs/ci-pipelines/workflows-runbook.md` |
| Boundary decision: full runs reserved for council and CI; residuals D6 | ReviewCouncil | ADR | `docs/adr/0056-*.md` (new, at closeout) |

## Test contract

Every implementation task has a row. The runner is a targeted filter on a built assembly, through the watchdog from the repository root. No row runs an unfiltered `dotnet test`, `review gates --all`, `fast-loop.sh --full`, or `npm run test` without a path.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.RepoPolicy.Tests/ProcGuardFullRunGuardTests.cs` (class `KyberWeave.Tests.ProcGuardFullRunGuardTests`) | `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release` then `scripts/proc-guard.sh run -- dotnet tests/KyberWeave.RepoPolicy.Tests/bin/Release/net10.0/KyberWeave.RepoPolicy.Tests.dll -class KyberWeave.Tests.ProcGuardFullRunGuardTests` | `classify` prints `full` for the full-run argv of D3 and `partial` for the partial argv. `run` with a full argv and no `--council`, stdin from `/dev/null`, exits 89 and names `--class` on stderr, and starts nothing. `run --council -- <non-full argv>` exits with the command's own code. | Fails: `classify` is unknown and `run` starts the command (no exit 89). | All theory rows pass; the refusal starts no process (checked by a marker file the command would create). |
| T1 | `tests/KyberWeave.RepoPolicy.Tests/ReviewGateFullSuiteConfigTests.cs` (class `KyberWeave.Tests.ReviewGateFullSuiteConfigTests`) | same policy-DLL command, `-class KyberWeave.Tests.ReviewGateFullSuiteConfigTests` | Gates `test`, `ts-test`, `cargo-test` carry `full-suite: true` and no other gate does. The `test` and `ts-test` argv contain `--council` after `run`. The `review gates` call in `templates/github-actions/kyber-weave-review-gate.yml` contains `--council`. | Fails: the key and the flag are absent. | Passes after T4 with no assertion weakened. |
| T1 | `tests/KyberWeave.RepoPolicy.Tests/FastLoopFullRunRefusalTests.cs` (class `KyberWeave.Tests.FastLoopFullRunRefusalTests`) | same policy-DLL command, `-class KyberWeave.Tests.FastLoopFullRunRefusalTests` | `scripts/fast-loop.sh --full`, `--scoped` and `--regenerate`, started with stdin from `/dev/null`, exit 89 before any build and print the `--class` alternative. | Fails: the script builds and starts the suite (test runs with a 60 s harness timeout, no exit 89). | Passes after T2 and T4 with the refusal before any build. |
| T1 | `tests/KyberWeave.Tests/ReviewFullSuiteGuardTests.cs` (class `KyberWeave.Tests.ReviewFullSuiteGuardTests`) | `dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release` then `dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class KyberWeave.Tests.ReviewFullSuiteGuardTests` | `FullSuiteGate` selection: without `--council` and non-interactive, `full-suite` gates become refused with the alternative text; with `--council` or interactive, nothing is refused. The config loader reads `full-suite: true` onto the gate model. | RED is a compile failure on the named, not-yet-existing `FullSuiteGate` and the new gate property. That is the intended missing behaviour. | Passes after T3. |

Rules for the Test contract: assertions name the decision (refused or admitted, exit code, message content), not the wiring; the refusal tests use a stdin redirect so the run is non-interactive and cannot depend on the machine.

## Tasks

Each task names its files, delegate, acceptance, and targeted command. Delegate matching is against the agent descriptions in the architect's inventory. Where no description matches a file, the general-purpose fallback is recorded.

### T1. RED: write the failing tests (test-first)

- **Files (new):** `tests/KyberWeave.RepoPolicy.Tests/ProcGuardFullRunGuardTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/ReviewGateFullSuiteConfigTests.cs`, `tests/KyberWeave.RepoPolicy.Tests/FastLoopFullRunRefusalTests.cs`, `tests/KyberWeave.Tests/ReviewFullSuiteGuardTests.cs`.
- **Delegate:** `test-dev`. Reason: each file is a test project file under `tests/`, which matches the `test-dev` description.
- **Acceptance:** each test row of the Test contract is written and runs RED for the reason it names. No product or script file is changed. The RepoPolicy filter run and the product filter run each report a non-zero test count (the filter must match).
- **Command:** the T1 rows of the Test contract. No unfiltered run.
- **Dependencies:** none.

### T2. GREEN: the watchdog classifier and refusal

- **Files:** `scripts/proc-guard.sh` only, plus its header comment (exit code 89, `classify`, `--council`).
- **Delegate:** `general-purpose`. Reason: a bash script under `scripts/`. No agent description covers shell scripts; `csharp-dev`, `github-devops` and `test-dev` do not match, and the nearest specialist (`github-devops`) is for workflows and Docker. Recorded as a fallback.
- **Acceptance:** `classify` implements D3 exactly (the argv shapes in the T1 rows). `run --council` is accepted as a flag and is not forwarded to the command. Full run, no council, non-interactive: exit 89 before the slot wait and before any process starts. The `--slot` queue, the caps, the leftover check and exit codes 86, 87, 88, 124 and 143 are unchanged. The existing `ProcGuard*Tests` classes keep passing.
- **Command:** `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`, then `scripts/proc-guard.sh run -- dotnet tests/KyberWeave.RepoPolicy.Tests/bin/Release/net10.0/KyberWeave.RepoPolicy.Tests.dll -class KyberWeave.Tests.ProcGuardFullRunGuardTests -class KyberWeave.Tests.ProcGuardSlotTests -class KyberWeave.Tests.ProcGuardScriptTests -class KyberWeave.Tests.ProcGuardTtyTests -class KyberWeave.Tests.ProcGuardKillScopeTests -class KyberWeave.Tests.ProcGuardDotnetServersTests`.
- **Dependencies:** T1.

### T3. GREEN: the review gates CLI and the gate key (C#)

- **Files:** `src/KyberWeave.Cli/Commands/Review/ReviewSettings.cs` (`--council`), `src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs` (apply the refusal to the selected plan), a new `src/KyberWeave.Core/Review/FullSuiteGate.cs` (the pure selection and message), and the review gate model and its YAML loader under `src/KyberWeave.Core/Review/` (locate with codegraph; add the `full-suite` property). Existing tests `tests/KyberWeave.Tests/ReviewGatesCouncilCommandTests.cs` and `ReviewGatesAgentCommandTests.cs` may need `--council` added to their non-interactive `review gates .` calls if they run a `full-suite` gate. Any such change is recorded in the task report.
- **Delegate:** `csharp-dev`. Reason: non-test `.cs` in the CLI and Core. The two existing test files are `.cs` under `tests/KyberWeave.Tests`, which `test-dev` owns; if a change is needed there, it is made by `test-dev` in the same task's second step and cited.
- **Acceptance:** `ReviewFullSuiteGuardTests` passes. Refused gates are reported as blocking failures carrying the alternative text. `--council` and an interactive stdin admit every gate. `--agent` task-gate runs are unaffected. `TreatWarningsAsErrors` stays on and no `NoWarn` is added.
- **Command:** `dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`, then `dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class KyberWeave.Tests.ReviewFullSuiteGuardTests -class KyberWeave.Tests.ReviewGatesCouncilCommandTests -class KyberWeave.Tests.ReviewGatesAgentCommandTests`.
- **Dependencies:** T1.

### T4. GREEN: callers, gate config and fast-loop

- **Files:**
  - `.kyber-weave/kyber-weave.yml`: `full-suite: true` on `test`, `ts-test`, `cargo-test`; `--council` after `run` on `test` and `ts-test` argv. Delegate `general-purpose`. Reason: gate configuration is YAML read by the review engine; no agent description owns it, and `github-devops` covers CI workflows only.
  - `scripts/fast-loop.sh`: refuse `--full`, `--scoped`, `--regenerate` when stdin is not a terminal, exit 89, before any build. Delegate `general-purpose` (shell script, same reason as T2). Dependency: T2 for the exit code's meaning.
  - `templates/github-actions/kyber-weave-review-gate.yml` and `.github/workflows/gate-catalogue-toolchains.yml` (confirm first; gap G2): add `--council` to each `review gates` call. Delegate `github-devops`. Reason: GitHub Actions workflow and the template that hosts copy into `.github/workflows/`.
  - `products/kyber-squad/skills/code-review/SKILL.md` and `products/kyber-squad/agents/code-reviewer.md`: the council's `review gates` call adds `--council`. Delegate `docs-dev`. Reason: both deliverables are `.md` files, which `docs-dev` owns. Check `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` for a pinned string before editing (gap G3).
- **Acceptance:** T1 rows for `ReviewGateFullSuiteConfigTests` and `FastLoopFullRunRefusalTests` pass. The `test`, `test-policy`, `ts-test`, `cargo-test` gates still run in CI and in the council. `build`, format and docs gates unchanged.
- **Command:** `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`, then `scripts/proc-guard.sh run -- dotnet tests/KyberWeave.RepoPolicy.Tests/bin/Release/net10.0/KyberWeave.RepoPolicy.Tests.dll -class KyberWeave.Tests.ReviewGateFullSuiteConfigTests -class KyberWeave.Tests.FastLoopFullRunRefusalTests -class KyberWeave.Tests.ReviewGateProcGuardWiringTests -class KyberWeave.Tests.FastLoopScriptTests`, and `dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release` then `dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class KyberWeave.Tests.SquadCanonicalContentTests`.
- **Dependencies:** T2, T3.

### T5. Documentation (docs-dev)

- **Files:** `AGENTS.md` ("The inner loop" and "Before opening a PR"; the Config Reg block is generated and is not edited), `docs/standards/test/README.md#commands`, `docs/dash/runbook.md#running-tests-safely`, `docs/cli-command-surface.md` (exit 89, `review gates --council`), `docs/configuration.md#review` (`full-suite`), `docs/ci-pipelines/workflows-runbook.md` (CI and template `--council`), and `docs/plans/README.md` only for the index row's status at closeout.
- **Delegate:** `docs-dev`. Reason: every file is governed `.md`.
- **Acceptance:** the docs describe the rule, the refusal text, `--council`, the terminal test, and the residuals D6. `docs_validate` (mergeReady false) and `docs_drift` report zero findings, with provenance `root=` equal to this checkout.
- **Dependencies:** T2, T3, T4.

## Dependency graph

- T1 (RED) gates everything.
- T2 and T3 run in parallel after T1: disjoint files (`scripts/proc-guard.sh` against `src/` and `tests/KyberWeave.Tests/`).
- T4 after T2 and T3 (its tests check the flag and the exit code those tasks create).
- T5 after T2, T3 and T4.
- Closeout (docs-dev, ADR 0056, todo for the per-harness hook) after T5 and after the council and CI have run once with the change.

## Risks

- **PTY-backed agent shells** pass the terminal test (D2, D6-2). Mitigation: live check G1; follow-up per-harness hook.
- **Typed `--council`** passes the guard (D6-3). Mitigation: stated residual; visible in config and transcripts.
- **Raw `dotnet test` unwrapped by an agent** is not seen (D6-1). Mitigation: AGENTS.md wrapper rule; follow-up hook.
- **Council or CI broken by the refusal** if a caller misses `--council` (G2). Mitigation: T4 policy tests pin the flag on every known caller; closeout verifies one council run and one CI run.
- **Classifier false positive** blocks a legitimate partial run. Mitigation: T1 covers each partial shape named in D3, including `test:related` and `--class`.
- **Uncommitted edits in the tree.** The 57 edits (including `AGENTS.md`, `CONTRIBUTING.md`, the Squad agents and skills) are the baseline. Every task edits its own lines and must not revert others.

## Out of scope

- Per-harness hooks (Claude `PreToolUse` on Bash, and the others): a follow-up todo, not this plan.
- Any change to what the council's gates check, to the slot's behaviour, or to the watchdog caps.
- Any change to `ci.yml`'s raw `dotnet test` steps (D7).
- Any environment-variable switch.
- The Arbiter.

## Verification gates

- Each task's targeted command, as listed above, with a non-zero test count.
- `docs_validate` and `docs_drift` after every plan save and after T5 (MCP tools, mergeReady false while in the active folder). Both must show the provenance `root=` of this checkout.
- Closeout only: one live `review gates .` by the council (or `--council`) with the test gate running, and one live dev-style `scripts/fast-loop.sh --full` from a non-interactive shell refused with exit 89.

## Review

Standard `code-reviewer` council at the end of the run, over the whole change. The council's own `review gates` call is the one that must pass `--council`. No per-task review beyond the worker's `task-gate`.

## Docs-dev closeout

- Create ADR 0056 (D8) and add it to `docs/adr/README.md`.
- Add a pointer from ADR 0047 to ADR 0056 (no decision change).
- File a todo for the per-harness shell hook (D6), under `docs/todo/`, and link it from the closeout mapping.
- Map each durable decision (D1 to D9) to its destination in the closeout summary. A Documentation waiver is not used; each decision has a surviving invariant.

## Gaps and open questions

- **G1 (live check).** Do Claude Code and the other harnesses' Bash tools run without a TTY? If one runs with a PTY, D2's terminal test admits dev agents on that harness. Owner question for David, once: is the PTY residual acceptable until the hook follow-up lands? Recommendation: yes.
- **G2.** `.github/workflows/gate-catalogue-toolchains.yml` calls `review gates` and was not read. T4 must confirm whether it is a council or a full-run caller before adding `--council`.
- **G3.** `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` may pin the code-review skill's wording. T4 checks before editing.
- **G4.** The `review gates` CLI's exit code for a refused blocking gate reuses the CLI's existing blocking-failure code. T3 confirms the value against `docs/cli-command-surface.md#exit-codes`.
- **G5.** The council's routing between `review gates` and `--agent` task gates in `code-reviewer` is not fully read. T4 adds only the `--council` flag and does not change routing.

## Closeout

**Closed 2026-11-06 by docs-dev.** The plan file was never tracked by git, so it was moved to `docs/archive/plans/` with `mv`, not `git mv`.

### Verification

- **Council run with `--council`:** 31 gates ran, 31 passed, 0 refused, exit 0. The `test` gate ran the full suite once, in the council.
- **Live refusals:** a `review gates` run with no `--council`, from a shell with no terminal, refuses the full-suite gate with `KW-REVIEW-022` and exit 1, and starts nothing. `scripts/proc-guard.sh run` with a stubbed `dotnet test` exits 89 and starts nothing. `scripts/fast-loop.sh --full`, `--scoped` and `--regenerate` exit 89 from a non-terminal shell.
- **Re-check council:** verdict `REQUEST_CHANGES`. The four first-pass Major findings are closed. The re-check raised five items, N1 to N4 classifier gaps and N5 version skew (listed below). The conductor parked them under the bounded remediation rule. They are disclosed here and in the test standard, not fixed.
- **Not verified:** whether the Bash tools of harnesses other than Claude Code have a terminal (G1, still open). A live CI run of the template on a host.

### Known limits at archive

- **N4** `scripts/fast-loop.sh` with no arguments runs an unfiltered `cargo test` over the tray crate, outside the watchdog. Disclosed in the same section.
- **N5** The installed kyber-weave 0.1.8-rc.19 does not know `--council` or the `full-suite` key. It warns and fails safe. The CLI, the classifier, the gate keys and the CI template must ship together. Disclosed in the [CI pipelines workflows runbook](../../ci-pipelines/workflows-runbook.md).
- **Minor items** (recorded here only): `env -S` strings; `vstest`, `exec` and `npx vitest` shapes; over-refusal of directory targets and of a bare word after an unknown flag; the `cargo-test` gate argv is not wrapped by the watchdog; `fast-loop.sh` refusal checks only `$1`; a refused gate is missing from the `failed` metric; the `proc-guard.sh` header comment overstates what is not forwarded.

### Pass 3

After the remediation circuit-breaker cap, David authorised one more bounded pass. It fixed N1
(`dotnet run --project` on the test project is full), N2 (the launchers `nohup`, `caffeinate`, `sudo`
and the other listed wrappers are unwrapped, and an unknown word after a launcher scans forward to the
first `dotnet`, `npm` or `cargo` word), N3 (`npm --prefix <absolute path> run test`, and any other
`--prefix` spelling, is full), and the tray UI line in the runbook (`npm --prefix dash/tray/ui run test`
with no path is now refused, so the runbook gives the targeted form). N1 to N3 are removed from the list
above. N4 and N5 remain open. The rules now live in the test standard, ADR 0059 and the per-harness hook
todo, and D17 is extended in the closeout mapping. The pass-3 re-check (a single in-process reader, with
no lens seats) returned REQUEST_CHANGES with six further gaps, F1 to F6; they are parked, not fixed, and
are recorded in the per-harness hook todo.

## Closeout mapping

Every decision D1 to D17 and every Design harvest item H1 to H7 maps to a destination below. Documentation-impact for every row: `extends-component`. Last reviewed: 2026-11-06 for every mapped document. Status: archived. `Distribution` was dropped from `impact-components` at closeout: the CLI reference that carries the changed flags is owned by ReviewCouncil, and `docs/distribution.md` (last reviewed 2026-11-05) does not describe the changed behaviour, so it was not re-reviewed to satisfy the field.

| ID | Destination | Durable fact | Impact components |
|---|---|---|---|
| D1 | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) (boundary and alternatives); [CLI command surface, repository scripts](../../cli-command-surface.md#repository-scripts) | Three layers share one definition of a full run; the Arbiter is not the enforcement point | ReviewCouncil |
| D2 | [AGENTS.md, The inner loop](../../../AGENTS.md#the-inner-loop); [test standard, Commands](../../standards/test/README.md#commands); [CLI command surface, repository scripts](../../cli-command-surface.md#repository-scripts) ("no environment variable lifts the refusal") | Council identity by the `--council` flag and the terminal test; environment variables rejected | ReviewCouncil |
| D3 | [test standard, Commands](../../standards/test/README.md#commands); the classifier comments in `scripts/proc-guard.sh` | Full-run definition | ReviewCouncil |
| D4 | [CLI command surface, repository scripts](../../cli-command-surface.md#repository-scripts) (89); [CLI command surface, `review`](../../cli-command-surface.md#review--the-deterministic-review-half) (exit 1) | Refusal exits 89; a refused full-suite gate is a blocking failure and the command exits 1 | ReviewCouncil |
| D5 | [configuration, review](../../configuration.md#review); [code-review architecture](../../code-review/architecture.md) (gate keys table); pinned by `ReviewGateFullSuiteConfigTests` | `full-suite: true` gate key on `test`, `ts-test`, `cargo-test` | ReviewCouncil |
| D6 | [test standard, Limits of the guard](../../standards/test/README.md#limits-of-the-guard); [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) (known limits); [per-harness shell hook todo](../../todo/per-harness-shell-hook-for-full-run-guard.md) | Three residuals (raw unwrapped `dotnet test`, PTY shell, typed `--council`) and the per-harness hook as the real closure | ReviewCouncil |
| D7 | [CI pipelines workflows runbook](../../ci-pipelines/workflows-runbook.md) (template and `ci.yml` paragraph); `templates/github-actions/kyber-weave-review-gate.yml` | CI does not change; the review-gate template passes `--council` | CI Pipelines |
| D8 | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) | ADR for the boundary. The number is 0059, not the planned 0056, because 0056 and 0057 already exist. | ReviewCouncil |
| D9 | Documentation waiver: execution-approval provenance, transient, with no surviving invariant | Test contract and execute approval | ReviewCouncil |
| D10 | [test standard, Limits of the guard](../../standards/test/README.md#limits-of-the-guard) (PTY bullet); [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) | PTY residual accepted until the hook lands | ReviewCouncil |
| D11 | Documentation waiver: a confirm-first check with no surviving invariant. The only `review gates` caller changed is the template, documented in the CI runbook. | The gate-catalogue workflow is not a council caller | CI Pipelines |
| D12 | Documentation waiver: a confirm-first check with no surviving invariant. The durable wording is in [code-review SKILL.md](../../../products/kyber-squad/skills/code-review/SKILL.md), step 2. | Pinned skill wording checked before the skill was edited | ReviewCouncil |
| D13 | [CLI command surface, `review`](../../cli-command-surface.md#review--the-deterministic-review-half) (exit 1); [AGENTS.md, Before opening a PR](../../../AGENTS.md#before-opening-a-pr) (`KW-REVIEW-022`, exit `1`) | Exit code for a refused blocking gate | ReviewCouncil |
| D14 | Documentation waiver: a confirm-first check; no behaviour changed, so no document moves. The existing routing text in [code-review architecture](../../code-review/architecture.md) stays accurate. | Council routing between `review gates` and `--agent` task gates is unchanged | ReviewCouncil |
| D15 | [CI pipelines workflows runbook](../../ci-pipelines/workflows-runbook.md) (template paragraph). ADR waiver: no ADR — ADR waiver: a one-flag call change inside an existing template, with no boundary. The missing github-actions standard is an open gap for the owner. | Template exception with no github-actions standard | CI Pipelines |
| D16 | [code-review SKILL.md](../../../products/kyber-squad/skills/code-review/SKILL.md), step 2; [CLI command surface, `review`](../../cli-command-surface.md#review--the-deterministic-review-half) (`--council` bullet); [configuration, review](../../configuration.md#review) | The pre-push self-review never passes `--council`; a refusal means "not run here"; `KW-REVIEW-022` is reused | ReviewCouncil |
| D17 | [test standard, Commands](../../standards/test/README.md#commands); the classifier in `scripts/proc-guard.sh`; [dash runbook, running tests safely](../../dash/runbook.md#running-tests-safely) (full-run clause) | Amended full-run definition (wrappers, `dotnet test` targets, `--prefix` spellings, `+toolchain`, ambiguous means full, `sh -c` not parsed); extended in pass 3: launcher list, dotnet run, npm any prefix | ReviewCouncil, KyberDash |
| Full-run definition, refusal exit 89, `classify` subcommand, `--council` flag | [CLI command surface, repository scripts](../../cli-command-surface.md#repository-scripts). Open: the `classify` subcommand is not named there yet; it is documented in the test standard. | Design harvest H1 | ReviewCouncil |
| Refusal behaviour, terminal test, targeted alternatives | [test standard, Commands](../../standards/test/README.md#commands) | Design harvest H2 | ReviewCouncil |
| `full-suite: true` gate key and `--council` | [configuration, review](../../configuration.md#review) | Design harvest H3 | ReviewCouncil |
| Inner-loop rule and the refusal message | [test standard, Commands](../../standards/test/README.md#commands) (the rule also stands in the `AGENTS.md` "The inner loop" section) | Design harvest H4 | ReviewCouncil |
| Watchdog refusal and the KyberDash runbook section | [dash runbook, running tests safely](../../dash/runbook.md#running-tests-safely) | Design harvest H5 | KyberDash |
| CI and template callers pass `--council` | [CI pipelines workflows runbook](../../ci-pipelines/workflows-runbook.md) | Design harvest H6 | CI Pipelines |
| Boundary decision: full runs reserved for council and CI; residuals D6 | [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md); residual tracking: [per-harness shell hook todo](../../todo/per-harness-shell-hook-for-full-run-guard.md) | Design harvest H7 | ReviewCouncil |

### Open follow-ups (not closure conditions)

- Per-harness shell hook (D6): tracked in the todo above.
- The `classify` subcommand in the CLI command surface (H1).
- The missing github-actions coding standard (D15).
- Docs outside this closeout that still show a full run as a dev command: `docs/rules/kyberdash-display-layer.md`, `docs/standards/typescript/README.md`, `docs/dash/runbook.md` (the pre-PR list and the tray `bash -c` cargo test line), and the Rust standard's description of the `cargo-test` gate as watchdog-wrapped, which the gate config does not do.
- Residual `scripts/fast-loop.sh` tray `cargo test` (N4): tracked in the [fast-loop tray cargo test todo](../../todo/fast-loop-default-tray-cargo-test.md).
- Hosts that run `docs init` get generated configs with no `full-suite` key, so their test gate runs unrefused: tracked in the [docs init full-suite key todo](../../todo/docs-init-emit-full-suite-gate-key.md).
