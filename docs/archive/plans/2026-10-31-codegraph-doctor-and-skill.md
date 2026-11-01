---
id: plans/2026-10-31-codegraph-doctor-and-skill
title: "Repository doctor, docs init provisioning, a codegraph skill, and a run-start check in conductor and bug-crusher"
doc-type: plan
status: archived
component: DocGraph
owner: dpalfery
last-reviewed: 2026-10-31
development-mode: test-first
documentation-impact: extends-component
impact-components: [DocGraph, KyberSquad, ContextHygiene]
---

# Repository doctor, `docs init --provision`, the `codegraph` skill, and a run-start check

**Status: Archived — complete 2026-10-31.** The `doctor` verb, `docs init --provision`, the `.apm/skills/codegraph` skill and the read-only run-start step in `conductor` and `bug-crusher` all shipped. Every decision is answered; the closeout mapping below records where each durable item landed and what was verified. Development mode: `test-first`. Autonomy: `autonomous`. Scale: `extends-component`.

`development-mode: test-first` (the human did not opt out). Autonomy: `autonomous` (Config
Reg). This is a feature, so it is `conductor`-shaped work. It lands on a new branch cut from
`main`, not on `fix/dependabot-103-minimatch-advisory`. The untracked
`docs/plans/2026-10-31-mcp-external-shutdown-diagnostics.md` belongs to another session.
This plan does not touch it.

## Goal

A repository needs two working pieces of tooling before its agents can work well. The first is
a CodeGraph index, which `docs drift` needs (`KW-DOC-DRIFT-001` fails without one). The second
is a Kyber-Weave docs MCP server that answers a query. This plan adds four things:

1. **`kyber-weave doctor`**, a new top-level verb scoped to the repository. It checks both
   pieces and provisions what it is allowed to.
2. **`kyber-weave docs init --provision`**, which runs the same core after scaffolding.
3. **`.apm/skills/codegraph`**, a skill that tells the harness what to do when CodeGraph is
   missing or present, and calls the CLI for the work.
4. **A run-start step in `conductor` and `bug-crusher`.** It runs the read-only
   `doctor --check` when the repository has governed docs.

## Investigation findings

- `SquadDoctorCommand` (`src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`) diagnoses a
  squad **deployment**: the CLI version, renderer coverage, the MCP version probe
  (`McpProcessProbe` in `Commands/Squad/Infrastructure/ProcessProbes.cs`), ZCode MCP config
  tiers, and home state. It also migrates legacy state, so it is not read-only. Its scope is a
  deployment root or `--global`. It is not a repository's documentation tooling.
- `McpProcessProbe` runs a single-shot version probe through `IProcessExecutor`. Every run is
  bounded (`ProcessExecutor.DefaultTimeout` = 60 s). A timeout kills the whole process tree
  through `ProcessRunner.TryKillTree` (`src/KyberWeave.Core/Processes/ProcessRunner.cs:105`).
  `ProcessRunner.Run` writes all of standard input, closes it, and then waits. That does not
  fit an MCP stdio session, which needs request, then response, then the next request.
- ADR 0047 covers KyberDash (TypeScript) self-spawns through `guardedSpawn`. It does not cover
  the .NET CLI. On the C# side, `ProcessRunner` is the one bounded spawn path.
- `review.policy.always-human` (`.kyber-weave/kyber-weave.yml:374`) lists
  `products/kyber-squad/agents/**` and `.kyber-weave/kyber-weave.yml`.
- Canonical instruction surfaces, located on disk rather than guessed:
  - `conductor` is an **agent**: `products/kyber-squad/agents/conductor.md`. Its sections are
    Role, Capabilities and authority, Input router (with Intake assessment), Shared lifecycle
    invariants, Structured specialist handoffs, and Completion.
  - `bug-crusher` is a **skill**: `products/kyber-squad/skills/bug-crusher/SKILL.md`. Its
    sections are §1 Investigate, §2 Tripwires, §3 TRIVIAL path (step 4 run record),
    §4 Attempt budget, §5 Escalated path (step 5 run record), and §6 Report.
- `SquadCanonicalContentTests` (`tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`) already
  asserts content in the canonical squad sources. It is the natural place to pin the new step.
- Each skill under `.apm/skills/` has its own `skill-validate`/`skill-lint`/`skill-scan` gate
  triple in `.kyber-weave/kyber-weave.yml` (lines 74–145).

Discovery: codegraph 2, docs 4, direct reads 9. Every direct read is of non-indexed or
documentation content: `docs/plans/README.md` (index row format), an archived plan's
frontmatter (format), two slices of `.kyber-weave/kyber-weave.yml` (gates and always-human;
config is not indexed), and globs and greps to locate the `conductor` and `bug-crusher`
sources, their headings, the content test class, the catalog rows, and the Exit codes
heading. The architect reference files were unreachable from this harness
(`external_directory` denied). The plan follows the plan index policy and the archived plan
format instead.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D0 | Deliverables | Doctor-shaped check, `docs init` provisioning path, `.apm/skills/codegraph` | Owner-settled | owner decision |
| D1 | MCP check depth | Start the server and call `docs_explore` over a real stdio MCP session. Do not parse harness config files. | Owner-settled | owner decision |
| D2 | Missing `codegraph` binary | Report it, print the install command, exit non-zero, and never install | Owner-settled | owner decision |
| D3 | Index policy | Every **provisioning** `doctor` run indexes: `init` when `.codegraph/` is absent, `index` otherwise | Owner-settled ("always index when doctor runs"). D11 keeps that rule for every provisioning run. It adds only an explicit read-only `--check` mode for run-start and CI. | owner decision |
| D4 | Skill location | `.apm/skills/codegraph`, shipped with the squad kit | Owner-settled | owner decision |
| D5 | Command surface | New top-level `kyber-weave doctor [path] [--check] [--provision] [--index-timeout <s>] [--json]`, scoped to the repository. `squad doctor` is unchanged. | `squad doctor` diagnoses a deployment target or `--global` and migrates home state, so it is not read-only. Folding repository checks into it would mix two scopes and would make the run-start check write home state. A top-level verb follows `update`, which is also cross-product. Shared logic lives in `KyberWeave.Core/Doctor/RepositoryDoctor`. `doctor`, `docs init --provision`, and the skill (through the CLI) all call it, so the three cannot diverge. | autonomous: recommendation accepted |
| D6 | Exit codes | `0` all checks pass. `1` a check failed: index failed or timed out, MCP did not start, handshake timed out, `docs_explore` returned an error, `.codegraph/` is corrupt or not writable, or `--check` found no index. `2` client input error, the existing CLI-wide rule. `3` prerequisite missing and installing it needs user approval (`codegraph` not on PATH). | `3` separates "ask the human to install" from "something broke", so the skill and the run-start step can branch without parsing text. T0 confirms against `docs/cli-command-surface.md` §Exit codes that `3` is not already assigned to a different meaning on a top-level verb. If it is, take the next free code and update this row. | autonomous: recommendation accepted |
| D7 | Which MCP server binary, and how it is bounded | Use the same executable resolution `McpProcessProbe` uses: the bare PATH name, with no repo-build fallback, so the check proves what agents will actually launch. A new `KyberWeave.Core/Processes/StdioSession` reuses `ProcessRunner`'s safe start info and `TryKillTree`. The session has a 20 s total budget (`--mcp-timeout`). It kills the process tree in `finally` on every exit path, and it fails a run that leaves a child behind. | `ProcessRunner.Run` cannot do request/response turns. Adding the session next to it keeps one bounded chokepoint in .NET. | autonomous: recommendation accepted |
| D8 | Which MCP call proves the most | Run `initialize`, then `notifications/initialized`, then `tools/call docs_explore {query:"README"}`. A pass needs a non-error result whose `provenance: root=` equals the git root. | `docs_status` proves only that the process is alive. `docs_explore` also proves the index loads and that the server answers for this repository. The root check catches a server answering from the wrong checkout. | autonomous: recommendation accepted (owner named `docs_explore`) |
| D9 | Failure message contract | Each failed check prints one line: `FAIL <check-id> <reason>`, then `  fix: <exact command>`. The check ids are `codegraph.binary`, `codegraph.index`, `mcp.start`, `mcp.handshake`, `mcp.docs_explore`, and `mcp.root`. A failure never echoes the child's environment or stderr beyond the first error line. | An agent can act on the check id and the fix line. Withholding the environment and full stderr follows the existing `ProcessProbes` no-disclosure tests. | autonomous: recommendation accepted |
| D10 | CodeGraph mechanics | Root is `git rev-parse --show-toplevel`. Refuse with exit 2 when the root is not a repository or equals `$HOME`. Binary on PATH: `codegraph init .` when `.codegraph/` is absent, `codegraph index .` otherwise. Both run with cwd = root, under a 600 s `--index-timeout`, and kill the tree on timeout. A running daemon is left alone: `index` coexists with it, and doctor never kills or restarts it. A cross-process lock file in the OS temp directory, keyed by a hash of the root, serialises concurrent doctors. The second doctor waits up to the index timeout, then exits 1 with `codegraph.index busy`. If `.codegraph/` is not writable or owned by another user, or if `index` reports a corrupt DB, exit 1 with fix: `remove .codegraph and run kyber-weave doctor`. Doctor never deletes it. | Root `AGENTS.md` already authorises a repository-local `codegraph init .`. "Always index" costs one incremental index, cheap when nothing changed. The index cost is why D11 keeps run-start read-only. | autonomous: recommendation accepted |
| D11 | Non-interactive behaviour, and the run-start cost tension | Modes: `--check` is always read-only: it detects the binary and index and runs the MCP proof, but never runs init or index. `--provision` always provisions (D3). Bare `doctor` provisions when stdin is a TTY and `CI` is unset; otherwise it acts as `--check` and prints `provisioning skipped: non-interactive; rerun with --provision`. A gate or CI uses `doctor --check`. | This settles the tension in the second addendum. Run-start calls `--check`, so no run pays a full index. The owner's "always index" rule holds for every provisioning run, with no "only when absent" dilution. Provisioning stays an explicit action. | autonomous: recommendation accepted |
| D12 | `docs init --provision` | Opt-in flag. After scaffolding succeeds, it runs `RepositoryDoctor` in provision mode against the scaffolded root. With `--target`, it provisions the target's git root. Without the flag, behaviour is unchanged. Exit: the scaffold's code when the scaffold fails; otherwise the doctor's code (D6). | CI runs `docs init` as pure scaffolding today, so the default must not start indexing or spawning servers. | autonomous: recommendation accepted |
| D13 | Skill trigger and body | Description: "Use when the CodeGraph index (.codegraph/) is missing or stale, codegraph tools report a project is not indexed, or docs drift fails KW-DOC-DRIFT-001. Do not use for ordinary code search or exploration — call codegraph_explore directly." Body: (1) run `kyber-weave doctor --check`; (2) exit 0: stop; (3) exit 3: stop and ask the user to install, quoting the printed install command, and never install; (4) exit 1 with `codegraph.index` and the binary present: run `kyber-weave doctor --provision`; (5) any other failure: report the check id and fix line. The skill never runs `codegraph` itself. | Short, when-to-use, with an exclusion against ordinary searches. Has its own `skill-validate-codegraph`/`skill-lint-codegraph` (`--min-desc-score 70`)/`skill-scan-codegraph` (`--fail-on critical`) gate triple. | autonomous: recommendation accepted |
| D14 | What "docs is enabled" means | A repository has docs enabled when `.kyber-weave/kyber-weave.yml` exists at the git root **and** the `docs-root` it declares resolves to an existing directory. Both are checked by `test -f` and `test -d` equivalents the harness can do without the CLI. | Config alone may be a review-only adoption, and a `docs/` folder alone is not governed docs. Requiring both means the step never fires in a repository with no governed docs. | autonomous: recommendation accepted |
| D15 | Run-start failure behaviour | Warn and continue, recording each failed check id. Two exceptions: (a) exit 1 with `codegraph.index` (no or stale index) and the binary present: run `kyber-weave doctor --provision` once, because root-level init is repository-authorised, then continue whatever it returns. (b) Exit 3 (binary missing): never install. Supervised mode asks the user with the install command. Autonomous mode records it as pending human and continues. | Doctor never blocks a run. A missing index is normal on a fresh clone. Item (a) keeps "always index" on the provisioning path only. | autonomous: recommendation accepted |
| D16 | Where run-start is recorded | One run-record line, `run-start doctor: <exit> <check ids>`, in the asked/decided+why/files-changed record both entry points already keep. The report and the PR description carry it. A failure is a line in the record, not a stop. | Reuses the existing run record, so no new artefact is needed. | autonomous: recommendation accepted |
| D17 | Where the instruction lives | `products/kyber-squad/agents/conductor.md`: a new bullet under **Shared lifecycle invariants** (the earliest section that applies to every route). `products/kyber-squad/skills/bug-crusher/SKILL.md`: a new step 0 at the top of **# Workflow**, before §1 Investigate, plus a reference in §6 Report. The wording names the shell command and the two path checks only, with no editor- or harness-specific tool names. | Both files ship to many harnesses. Plain commands render on all of them. | autonomous: recommendation accepted |
| D18 | How the run-start step is tested | Add `SquadCanonicalContentTests` assertions that both canonical files contain `kyber-weave doctor --check`, the D14 condition, and "warn and continue". | A content pin stops a later edit from silently dropping the step. A docs rule would be the wrong layer, because these are squad sources rather than governed docs. | autonomous: recommendation accepted |
| D19 | ADR | New ADR, number assigned at closeout: "The .NET CLI spawns long-lived stdio children only through one bounded session". It records D7 and D10's kill-tree-on-every-exit and no-orphan rules, and cites ADR 0047 as the KyberDash analogue. | This is the first .NET spawn site that holds a two-way session. That is a lasting constraint with rejected alternatives (raw `Process`, reusing `ProcessRunner.Run`). | autonomous: recommendation accepted |
| D20 | Review verdict | This change edits `products/kyber-squad/agents/conductor.md` and `.kyber-weave/kyber-weave.yml` (the new skill gates). Both are `review.policy.always-human` paths, so the council verdict **will be `NEEDS_HUMAN` (KW-REVIEW-008)** whatever the findings. `bug-crusher/SKILL.md` is an instruction surface too, though not a listed path. Per ADR 0045, autonomous delivery reports the verdict as pending human review and does not hold the draft PR. | Expected and accepted. It is not an approval. | autonomous: recommendation accepted |
| D21 | What fills the `codegraph.binary` fix line? | It names the action — ask the user to install CodeGraph, then rerun — not an install command. | The repository documents no authoritative installer for CodeGraph, and the owner's rule is that nothing installs without approval. Inventing a URL or a package name would be worse than naming the step. The skill states the same thing, so an agent never reaches for a package manager on its own. | autonomous: recommendation accepted |
| D22 | Where is the docs-enabled root declared? | `DocsEnabled` reads `ontology.docs-root` with a top-level `docs-root` fallback for a config written before the nesting, then defaults to `docs`. It has no production caller: `conductor` and `bug-crusher` test the predicate themselves with `test -f`/`test -d`, as D14 specifies, and the content pins keep the step in both files. | The design sketch assumed a top-level key; the host config nests it under `ontology`, which would have made a repository with a non-default root silently fall back to `docs`. Corrected in implementation and pinned by two added `DocsEnabledTests` cases. | autonomous: recommendation accepted |
| AP1 | Approve and execute | Approved. Execute at the declared scale: the repository-scoped `doctor`, the `docs init --provision` path, the `.apm/skills/codegraph` skill, and the read-only run-start step in `conductor` and `bug-crusher`. | Conductor gate `AUTONOMOUS_APPROVAL: AP1` accepted on 2026-10-31 under Config Reg `autonomy: autonomous`. T0's two probes are recorded below and confirm D6 and D7 as written. | autonomous: recommendation accepted |

## T0 results

- **Exit code `3`.** `docs/cli-command-surface.md` §Exit codes assigns `3` only under the
  `kyberdash` binary (`dash refresh`, `dash clean`, `dash import-history`: another operation
  holds the lock). No `kyber-weave` command in that table uses `3`, and the contract states
  the branches are deliberately not uniform and are documented per binary. `kyber-weave
  doctor` therefore takes `3` for a missing prerequisite that needs user approval (D6); the
  collision is across binaries, not within one, and the new exit row is added under the
  `kyber-weave` table in T6.
- **MCP executable.** `src/KyberWeave.Cli/Commands/Squad/Infrastructure/ProcessProbes.cs`
  resolves the bare PATH name `kyber-weave-mcp` and parses its exact `--version` envelope;
  `SelfUpdater` uses the same base name and `SquadDoctorCommand` reports it. D7's "same
  executable resolution, no repo-build fallback" is confirmed against the code, not assumed.

## Design

```
KyberWeave.Core/Doctor/
  RepositoryDoctor            Run(root, DoctorMode {Check, Provision}, options) -> DoctorReport (checks[], exitCode)
  CodeGraphProvisioner        detect binary, init|index, lock, timeout       (IProcessExecutor-injected)
  McpLiveProbe                StdioSession -> initialize, docs_explore, root match
  DocsEnabled                 D14 predicate (also used by tests)
KyberWeave.Core/Processes/StdioSession   bounded two-way JSON-RPC child, kill-tree in finally
KyberWeave.Cli/Commands/DoctorCommand    flags, TTY/CI resolution (D11), rendering (D9)
KyberWeave.Cli/Commands/Docs/DocsInitCommand  +--provision -> RepositoryDoctor(Provision)
```

## Test contract (test-first)

Tests live in `tests/KyberWeave.Tests`. They follow that project's `AGENTS.md` and the test
standard: fakes through `IProcessExecutor` (`RecordingProcessExecutor`/`FakeProcessExecutor`),
environment through injected delegates (no process-environment mutation), and no real
`codegraph` or MCP spawn in unit tests. Each test is RED before its implementation task.

| Test | Pins |
|---|---|
| `Doctor_CodegraphMissing_Exits3WithInstallCommand_AndRunsNothing` | D2, D6, D9 |
| `Doctor_Provision_NoIndex_RunsInitThenSucceeds` / `_IndexPresent_RunsIndex` | D3, D10 |
| `Doctor_Provision_IndexTimeout_KillsTreeExits1` | D6, D10 |
| `Doctor_Check_NeverInvokesInitOrIndex` | D11 |
| `Doctor_NonInteractive_Bare_BehavesAsCheck_PrintsHint` (CI set; no TTY) | D11 |
| `Doctor_RootIsHome_OrNotGit_Exits2` | D10 |
| `Doctor_ConcurrentLockBusy_Exits1Busy` | D10 |
| `Doctor_CodegraphDirUnwritable_Exits1WithRemedy_DoesNotDelete` | D10 |
| `McpLiveProbe_HandshakeTimeout_KillsChild_Exits1_mcp.handshake` | D6, D7 |
| `McpLiveProbe_DocsExploreError_Exits1` / `_RootMismatch_Exits1_mcp.root` | D8 |
| `McpLiveProbe_StartFailure_DoesNotDiscloseEnvironment` | D9 |
| `StdioSession_DisposeOnEveryPath_LeavesNoChild` (real `/bin/cat`-style echo child, through proc-guard) | D7 |
| `DocsInit_WithoutProvision_SpawnsNothing` / `_WithProvision_UsesRepositoryDoctor_TargetRoot` | D12 |
| `DocsEnabled_RequiresConfigAndDocsRoot` (3 cases) | D14 |
| `SquadCanonicalContentTests`: conductor and bug-crusher carry the run-start step | D17, D18 |
| Skill gates `skill-validate/lint/scan-codegraph` pass | D13 |

## Tasks

| ID | Task | Owner | Depends |
|---|---|---|---|
| T0 | Confirmed 2026-10-31 (below): exit `3` is unassigned for `kyber-weave`, and the MCP executable is the bare PATH name `kyber-weave-mcp`. | conductor | done |
| T1 | RED: doctor, provisioner, probe, StdioSession, DocsEnabled tests | test-dev | T0 |
| T2 | GREEN: `StdioSession` + `RepositoryDoctor` core | csharp-dev | T1 |
| T3 | GREEN: `DoctorCommand` + `docs init --provision` | csharp-dev | T2 |
| T4 | `.apm/skills/codegraph/SKILL.md` and its gate triple in `.kyber-weave/kyber-weave.yml` (always-human) | docs-dev (skill) + conductor | T3 |
| T5 | RED: content pins. GREEN: `conductor.md` and `bug-crusher/SKILL.md` edits per D17 | test-dev, then conductor | T3 |
| T6 | Docs: `cli-command-surface.md` (verb, flags, exit rows), docgraph runbook section, kyber-squad architecture run-start note, ADR (D19) | docs-dev | T3–T5 |
| T7 | Gates through `scripts/proc-guard.sh run --`: `review gates . --all`, `docs validate --merge-ready`, `docs drift`. Council run, with `NEEDS_HUMAN` expected (D20). Then closeout and archive. | conductor | T6 |

## Closeout mapping

**Completed 2026-10-31.** Archived under `docs/archive/plans/`. Every decision is answered; the
revision recorded in D22 corrects the design's assumption about the config key, and D21 records
why the install line names an action rather than a command.

### Verification evidence

| Check | Result |
|---|---|
| `dotnet build KyberWeave.sln -c Release` | 0 warnings, 0 errors, with `TreatWarningsAsErrors` and `AnalysisMode=all`. |
| Full suite (`dotnet test … --coverlet`) | 4579 total, 4552 succeeded, 0 failed, 27 skipped, 2m 38s. |
| `review gates . --all` | 33 gates. The two failures in the run were `docs-validate` (the untracked plan from another session, absent from this branch) and `test` under parallel MSBuild load; the same test command passed serially with 0 failed. |
| `docs validate . --merge-ready` | 0 findings on the committed tree. |
| `docs drift .` | 0 findings. |
| Skill gates | `skill validate`, `skill lint --min-desc-score 70` and `skill scan --fail-on critical` all report 0 critical and 0 error for `.apm/skills/codegraph`. |

### Mapping

Documentation-impact is `extends-component`; impact components are `DocGraph` and `KyberSquad`;
`last-reviewed` is 2026-10-31.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D0 | [`doctor` verb](../../cli-command-surface.md#doctor--repository-documentation-tooling) and [the skill](../../context-hygiene/skills.md#codegraph) record the three deliverables. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D1 | [the MCP runbook](../../docgraph/mcp-runbook.md#the-live-check-doctor---check) records the live `docs_explore` proof. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D2 | [command surface, exit codes](../../cli-command-surface.md#exit-codes) records exit `3` and the no-install rule. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D3 | [docgraph architecture](../../docgraph/architecture.md#provisioning-the-index) records init-once, index-thereafter. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D4 | [skills](../../context-hygiene/skills.md#codegraph) registers the shipped location. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D5 | [command surface](../../cli-command-surface.md#doctor--repository-documentation-tooling) records why `doctor` is a top-level verb beside `squad doctor`. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D6 | [command surface, exit codes](../../cli-command-surface.md#exit-codes) carries the `0/1/2/3` contract. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D7 | [ADR 0049](../../adr/0049-dotnet-cli-spawns-stdio-children-through-one-bounded-session.md) records the bounded-session rule. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D8 | [the MCP runbook](../../docgraph/mcp-runbook.md#the-live-check-doctor---check) names the call and the root match. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D9 | [the MCP runbook](../../docgraph/mcp-runbook.md#the-live-check-doctor---check) records the `FAIL`/`fix:` output contract and the check ids. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D10 | [docgraph architecture](../../docgraph/architecture.md#provisioning-the-index) records root resolution, the lock, and never-delete. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D11 | [command surface](../../cli-command-surface.md#doctor--repository-documentation-tooling) records the interactive/non-interactive mode rule. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D12 | [command surface, `docs init` row](../../cli-command-surface.md#command-reference) records `--provision`. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D13 | [the skill](../../../.apm/skills/codegraph/SKILL.md) carries the trigger and step list; [skills](../../context-hygiene/skills.md#codegraph) registers it. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D14 | [kyber-squad architecture](../../kyber-squad/architecture.md#run-start-documentation-check) records the docs-enabled predicate. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D15 | [kyber-squad architecture](../../kyber-squad/architecture.md#run-start-documentation-check) records warn-and-continue and the two exceptions. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D16 | [kyber-squad architecture](../../kyber-squad/architecture.md#run-start-documentation-check) records the `run-start doctor:` line. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D17 | Documentation waiver: the step lives in two instruction surfaces outside the governed docs tree (`products/kyber-squad/agents/conductor.md` and `products/kyber-squad/skills/bug-crusher/SKILL.md`), and `SquadCanonicalContentTests` pins it there. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D18 | Documentation waiver: the pins are executable assertions in `SquadCanonicalContentTests`, which is the durable record for a content invariant. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D19 | [ADR 0049](../../adr/0049-dotnet-cli-spawns-stdio-children-through-one-bounded-session.md) is the record. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D20 | Documentation waiver: the review verdict is provenance, not an invariant; it is reported on the pull request. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D21 | [the MCP runbook](../../docgraph/mcp-runbook.md#the-live-check-doctor---check) states that no install command is named, and why. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| D22 | Documentation waiver: the config key is pinned by `DocsEnabledTests` and recorded in this row; no prose artefact repeats it. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| AP1 | Documentation waiver: an approval gate leaves provenance, not an invariant. | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| H1 | [command surface](../../cli-command-surface.md#doctor--repository-documentation-tooling) and [exit codes](../../cli-command-surface.md#exit-codes). | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| H2 | [command surface, `docs init` row](../../cli-command-surface.md#command-reference) and [the MCP runbook](../../docgraph/mcp-runbook.md#the-live-check-doctor---check). | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| H3 | [the MCP runbook](../../docgraph/mcp-runbook.md#the-live-check-doctor---check). | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| H4 | [docgraph architecture](../../docgraph/architecture.md#provisioning-the-index). | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| H5 | [ADR 0049](../../adr/0049-dotnet-cli-spawns-stdio-children-through-one-bounded-session.md). | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| H6 | [kyber-squad architecture](../../kyber-squad/architecture.md#run-start-documentation-check). | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |
| H7 | [skills](../../context-hygiene/skills.md#codegraph). | extends-component | DocGraph, KyberSquad | 2026-10-31 | archived |

## Design harvest

| ID | Durable item | Target component | Target doc type / location |
|---|---|---|---|
| H1 | `doctor` verb, modes, flags, exit codes `0/1/2/3` | DocGraph | REFERENCE `docs/cli-command-surface.md` (+ Exit codes table) |
| H2 | `docs init --provision` | DocGraph | REFERENCE `docs/cli-command-surface.md`; RUNBOOK `docs/docgraph/mcp-runbook.md` |
| H3 | Live MCP proof, check ids, and fix-line contract | DocGraph | RUNBOOK `docs/docgraph/mcp-runbook.md` |
| H4 | CodeGraph provisioning rules (root, init/index, lock, never delete, never install) | DocGraph | ARCHITECTURE `docs/docgraph/architecture.md` |
| H5 | Bounded stdio session spawn rule | DocGraph | ADR (new, D19) |
| H6 | Run-start check: docs-enabled predicate, warn-and-continue, run-record line | KyberSquad | ARCHITECTURE `docs/kyber-squad/architecture.md` |
| H7 | `codegraph` skill registration | ContextHygiene | `docs/context-hygiene/skills.md` |

## Risks

- Indexing a large repository on first provision can take minutes. The 600 s default is a
  flag, and the timeout kills the tree.
- An MCP server version older than one with `docs_explore` fails `mcp.docs_explore`. That is
  the correct result, and the fix line is `kyber-weave update`.
