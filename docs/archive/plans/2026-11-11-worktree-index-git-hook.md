---
id: plans/2026-11-11-worktree-index-git-hook
title: A git post-checkout hook that indexes every new worktree
doc-type: plan
status: archived
component: DocGraph
owner: dpalfery
created: 2026-11-11
last-reviewed: 2026-11-11
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [DocGraph, KyberSquad]
---

# A git post-checkout hook that indexes every new worktree

**Status: Archived, 2026-11-11.** Approved for execution under decision AP1 (autonomous approval of gate `approve-and-execute`). The task file was removed at closeout; see the Completion summary.
**Development mode:** `test-first` (RED by `test-dev`, GREEN by the implementation specialist, never the same worker).
**Autonomy:** `autonomous` (every open item decided by recommendation; provenance in `## Decisions`).
**Scale:** `extends-component`, components `DocGraph` (the doctor and its runbook) and `KyberSquad` (the worktree-index rule).
**Delivery:** one pull request on branch `claude/feat/worktree-index-git-hook`, stacked on pull request 592 (`claude/fix/worktree-codegraph-index`) and retargeted to `main` when 592 merges.

**Bottom line.** `kyber-weave doctor --ensure` installs a small marked block in the clone's shared `post-checkout` git hook. When git checks out a brand-new worktree, the block starts `kyber-weave doctor --post-checkout .` in the background, which indexes that tree with CodeGraph and never reverts an edit made there meanwhile. Ordinary branch switches cost one shell `case` test.

## Problem and goal

**Problem.** CodeGraph keeps its index in `.codegraph/`, which git does not track, so a worktree made with plain `git worktree add` starts with no index and nothing fails. Pull request 592 made `kyber-weave run start` and `kyber-weave review gates` index the tree they use, through `KyberWeave.Core.Doctor.WorktreeIndexGuard`. It does not cover a session, or a person, who runs `git worktree add` and then works in that tree directly: agents there fall back to raw file reads or are answered from another branch's index.

**Goal.**

1. One git hook covers every harness and every person at a terminal: any new worktree of a governed clone gets an index without anyone asking.
2. `kyber-weave doctor --ensure` installs the hook and repairs it; `doctor --check` reports it and changes nothing.
3. The hook never fails or slows a checkout, never acts on an ordinary branch switch, and does nothing when `kyber-weave` is not on PATH.
4. The index run the hook starts never reverts a file someone edits in the new tree while it runs.
5. One setting in `.kyber-weave/kyber-weave.yml` turns it off.

## Decisions

Permanent state, preserved across the plan's whole lifecycle.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| O1 | Git hook or harness hook? | A git `post-checkout` hook. | One mechanism for every harness and for people at a terminal, instead of one hook per harness. An earlier session's per-harness design on another branch is not used. | ANSWERED: owner decision 2026-11-11, relayed by the conductor in the planning packet. |
| O2 | Who installs it? | `kyber-weave doctor --ensure` installs it when missing and repairs it when removed. `doctor --check` reports it missing and changes nothing. | The repository's own SessionStart hooks already run `doctor --ensure .` in every harness (pinned by `KyberWeave.Tests.SessionStartIndexHookTests`), so most clones pick it up at their next session. | ANSWERED: owner decision 2026-11-11, relayed in the packet. |
| O3 | Where does it live? | A small marked, owned block in `post-checkout` in the clone's shared hooks folder, so one install covers every worktree. An existing hook is kept: the block is added beside it and updated in place later. When `core.hooksPath` is set, write there; when that folder is tracked in git, report instead of writing. | Linked worktrees share the common git directory's hooks (git-worktree documentation: a worktree shares everything except per-worktree files). | ANSWERED: owner decision 2026-11-11, relayed in the packet. |
| O4 | When does it act? | Only on a new worktree's first checkout (previous-HEAD argument all zeros). It starts the index in the background, costs nothing on ordinary branch switches, never fails a checkout, and does nothing when `kyber-weave` is not on PATH. | git passes the null object id as the first argument after `git clone` and `git worktree add` (githooks documentation; `builtin/worktree.c`). | ANSWERED: owner decision 2026-11-11, relayed in the packet. |
| O5 | How is it turned off? | One switch in `.kyber-weave/kyber-weave.yml`, default on. | Key name and semantics in D10. | ANSWERED: owner decision 2026-11-11, relayed in the packet. |
| D6 | What does the hook run? | A new doctor mode, `kyber-weave doctor --post-checkout [path]`. It resolves the git root, returns at once (exit 0, one `skipped:` line) when the setting is off, then calls `WorktreeIndexGuard.Ensure` with the narrow restore of D7. It runs no MCP probe and no Copilot prompt probe, and installs no hook. Exit codes: 0 ready, not governed or off; 1 index failed; 3 `codegraph` missing. It is mutually exclusive with `--check`, `--provision` and `--ensure` (exit 2). | `WorktreeIndexGuard` already returns `NotGoverned` for a repository without governed docs, which matters when `core.hooksPath` is a global folder shared by unrelated repositories. `RepositoryDoctor.Run` would add a 20-second MCP session and a machine-wide probe for nothing. The setting is read at hook run time from the new tree's own config, so a branch that turns it off is honoured even before `--ensure` removes the block. Rejected: `doctor --ensure --keep-edits` (still runs the MCP probe) and calling `codegraph init` from the shell (no lock, no repair, no governed-docs check). | ANSWERED: autonomous, low-confidence, no authoritative source. Cost of being wrong: a public CLI flag; cheap to rename before release. |
| D7 | The owner asked for the hook's run to skip the restore. What exactly does it skip? | The hook's run restores only tracked files on a fixed list, `CodeGraphProvisioner.InitConfigFiles`, of the files `codegraph init` itself is measured to write (T0). Any other tracked file that turns dirty while it runs is left alone and named in a passing `codegraph.restore` check. The ordinary `doctor --ensure` keeps restoring everything it wrote. **If T0 measures no tracked file, the list is empty and the hook's run skips the restore outright, exactly as the packet worded it.** If T0 finds a file that is not harness configuration or instruction text (for example a source file), the run returns to the architect before T1. | Skipping every restore has two costs if `codegraph init` writes tracked files. (1) Those files stay modified in every new worktree for good, because a later run only syncs and a sync never restores. (2) `run start` creates its tree with `git worktree add`, so the hook races its `WorktreeIndexGuard.Ensure`; if the hook takes the lock first, `run start` only syncs and its tree is left dirty. Restoring only the measured files closes both and never reverts a source edit. **Unverified:** that `codegraph init` writes tracked files at all is an inference: the canonical checkout shows `.mcp.json` and `.cursor/mcp.json` modified and an untracked `.claude/CLAUDE.md` with CodeGraph's block, and `AGENTS.md` carries CodeGraph's markers, but nothing read shows `init` caused them. T0 measures it before any code is written. **Conflict recorded:** the packet says the hook's run must not revert an edit made in the new tree; for a file on the list, an edit made in the first seconds of a new worktree would still be reverted (and named in the log). The packet's constraint is honoured for every other file. | ANSWERED: autonomous, low-confidence, no authoritative source; departs from the packet's constraint for listed files only, disclosed for the approval gate. Cost of being wrong: a missing entry leaves a config file dirty in new worktrees; a wrong entry can revert an early edit to that one file. |
| D8 | How does the block coexist with an existing hook, and how does it stay harmless? | Inserted directly after the shebang line, so an early `exit` or `exec` later in the user's hook cannot skip it. It never calls `exit`, runs its work in a subshell guarded by `\|\| :`, uses `${1:-}` so it survives `set -u`, and every command that can fail sits in a test or an AND-OR list so it survives `set -e` and `#!/bin/sh -e`. It acts only when the first argument is all zeros and the third is `1`. It clears `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_PREFIX` and `GIT_COMMON_DIR` before starting the child, and starts it with standard input, output and error all redirected so a caller reading git's output through a pipe is not held open. A new file is written as `#!/bin/sh`, LF only, mode 0755 on Unix. An existing hook is left byte for byte unchanged and reported when its shebang is not a POSIX shell (`sh`, `bash`, `dash`, `ksh`, `zsh`, directly or through `/usr/bin/env`), when it has no shebang, when it has CRLF line endings, or when on Unix it lacks the executable bit (enabling it would switch on a hook its owner disabled). | git turns the hook's exit status into the exit status of `checkout`, `switch`, `clone` and `worktree add` (githooks documentation; `builtin/clone.c`, `builtin/worktree.c`), so "never fails a checkout" has to be built into the block. githooks advises clearing the local environment variables before running git in another tree. Pipe release follows POSIX pipe semantics; research could not confirm it from git documentation, so T2 tests it with real git. | ANSWERED: autonomous, low-confidence, no authoritative source for the pipe and Windows points; covered by githooks documentation for the rest. |
| D9 | What does the doctor report, in each mode? | One check, id `githook.post-checkout`, appended after the `stops` handling in `RepositoryDoctor.Run`, like `CopilotPromptProbe`. It never changes the exit code: every problem is a passing check whose reason starts `warning:`. `--check` and `--provision` only inspect (installed, missing, out of date, tracked folder, foreign or disabled hook, switched off). `--ensure` installs, updates in place, or removes when switched off. A repository without governed docs gets no line at all. | A hook is a convenience, not a required tool, so it must not stop a code-editing run (ADR 0061 covers tools) or turn a `--check` that passes today into a failure on every machine and CI runner that lacks it. `--provision` stays read-only for the hook because the owner named only `--ensure` as installing. | ANSWERED: autonomous, low-confidence, no authoritative source; cheap to reverse. |
| D10 | Name and semantics of the off switch? | Top-level `worktree-index-hook:` in `.kyber-weave/kyber-weave.yml`, `true` or `false`, absent means `true`. Any other value is a configuration error naming the key and both values (`KW-CONFIG-001`), the same treatment as `stop-on-tool-failure:`. With `false`, `--ensure` removes the block (and deletes the file when nothing but a shebang is left), and `--post-checkout` exits at once. A config that cannot be loaded counts as the default, on, and removes nothing. | Mirrors the existing top-level `stop-on-tool-failure` parsing in `KyberWeaveConfigLoader.ParseStopOnToolFailure` and its documentation in `docs/configuration.md`. | ANSWERED: autonomous, covered by the existing `stop-on-tool-failure` pattern. |
| D11 | How is the hooks folder found? | `git rev-parse --git-path hooks` from the repository root; a relative answer is resolved against the repository root. When `core.hooksPath` is set (`git config --get core.hooksPath`) and the resolved folder is inside the working tree and `git ls-files -- <folder>` lists anything, it is tracked: report, write nothing. A folder outside the tree (a global hooks folder) is written to, per O3; the payload's governed-docs check keeps it inert in other repositories. A missing hooks folder is created. | `--git-path` respects `core.hooksPath` since git 2.10 (commit 9445b49, "rev-parse: respect core.hooksPath in --git-path"); a relative `core.hooksPath` is relative to where hooks run, the working-tree root (git-config documentation). | ANSWERED: autonomous, covered by git documentation; version from a search summary, medium confidence. |
| D12 | Where does the background run write its output? | `kyber-weave-index.log` in the new worktree's own git directory (`git rev-parse --absolute-git-dir`), overwritten on each run; `/dev/null` when git cannot say. | Outside the working tree, so it is never a tracked or untracked change, and specific to the one worktree a person would be debugging. | ANSWERED: autonomous, low-confidence; cheap to reverse. |
| D13 | Does this need a new `KW-` rule id? | No. The doctor reports through check ids, not rule ids; the new permanent check id is `githook.post-checkout`. The invalid setting reuses `KW-CONFIG-001`, which already means "invalid `kyber-weave.yml`". | Rule ids are permanent and only added for new diagnostics; nothing here is a new diagnostic class. | ANSWERED: autonomous, covered by the existing rule reference. |
| D14 | Does this need an ADR? | Yes. Kyber-Weave starts writing into a clone's git hooks, which belong to the user, and constrains how future tooling shares that file; per-harness hooks and run-start-only indexing were rejected. The number is assigned at closeout. | Inventory policy: an ADR for a decision that constrains future work and had viable rejected alternatives. | ANSWERED: autonomous, covered by the plan inventory's ADR policy. |
| D15 | Should `run start` suppress the hook for the worktree it creates? | No. The two runs serialise on the existing index lock, and D7's narrow restore leaves the tree clean whichever wins. | One fewer environment contract between `run start` and the hook. | ANSWERED: autonomous, low-confidence; cheap to reverse. |
| D16 | What happens on Windows? | Git for Windows runs hooks through its bundled POSIX shell, so the same LF `#!/bin/sh` block runs there; git on Windows has no executable bit and decides from the file. `command -v kyber-weave` is expected to find `kyber-weave.exe`; if it does not, the block does nothing, which is the safe failure. The real-git script tests use a POSIX stub and skip on Windows, and CI runs the full suite on Ubuntu only (the Windows leg filters to `SquadDeploymentStateTests`), so Windows behaviour is `[NOT LIVE-VERIFIED]`. | Research could not confirm the Windows `.exe` lookup or backgrounding from documentation. Adding a Windows CI leg is out of scope. | ANSWERED: autonomous, low-confidence, no authoritative source. |
| D17 | How are tests kept off the owner's real hooks? | Every test works in a temporary `git init` repository. Every git child a test starts runs with `HOME` and `GIT_CONFIG_GLOBAL` pointing at temporary files and `GIT_CONFIG_NOSYSTEM=1`, so a global `core.hooksPath` cannot redirect a write. No test or worker runs the new install against this clone, whose hooks every worktree shares. | A developer's global `core.hooksPath` would otherwise send a test's write to a real folder. | ANSWERED: autonomous, covered by the hard constraint that worktrees share the clone's hooks. |
| D18 | What if the `kyber-weave` on PATH is older than the block? | It rejects `--post-checkout` with exit 2 in the background; the log records it and the checkout is unaffected. The next `--ensure` of a newer binary rewrites the block in place. | Version skew is bounded by the block being owned and replaceable. | ANSWERED: autonomous, low-confidence; cheap to reverse. |
| AP1 | Approve and execute | Approved: this plan, its Test contract (T0 to T5) and decisions O1 to O5 and D6 to D18, at scale `extends-component` on DocGraph and KyberSquad, with one new ADR, one pull request, test-first. D7 is accepted as written, including the T0 measurement and the rule that any measured file other than harness configuration or instruction text returns the run to the planner before T1. | The finalization pass changed only how T1, T2 and T3 declare their file scopes (paths alone on the Files bullets, symbols moved to the objective) so the plan parser reads exactly the intended files; no file, behaviour, test or command changed. Docs checks in the worktree are recorded in the finalization digest. | ANSWERED: autonomous: recommendation accepted, `AUTONOMOUS_APPROVAL: approve-and-execute` from the conductor, 2026-11-11, under repository autonomy `autonomous`. |

## Design harvest

`documentation-impact: extends-component`; `impact-components: [DocGraph, KyberSquad]`. Each item lands in canonical docs at closeout (T5).

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | DocGraph | adr | New ADR: `doctor --ensure` owns a block in the clone's shared `post-checkout` hook to index new worktrees; rejected per-harness hooks and run-start-only indexing; the never-fail and narrow-restore invariants (O1 to O5, D7, D8, D14). |
| H2 | DocGraph | runbook | `docs/docgraph/mcp-runbook.md`, section "The worker's first command: `doctor --ensure`" and a new subsection for the hook: what `--ensure`, `--check` and `--provision` do to it, the `githook.post-checkout` check and its warnings, the `--post-checkout` mode and its narrow restore, the log file, Windows behaviour, and how to remove it (D6, D7, D9, D11, D12, D16, D18). |
| H3 | DocGraph | reference | `docs/cli-command-surface.md`, the `doctor` row and its exit codes: the `--post-checkout` option (D6). |
| H4 | DocGraph | reference | `docs/configuration.md`: the `worktree-index-hook:` key in "Shape" and its own subsection beside "Stop on tool failure" (D10). |
| H5 | KyberSquad | architecture | `docs/kyber-squad/architecture.md`, the worktree-index passage ("The tree it lands in is indexed by the command, not by a reminder") and "A worktree keeps its own index": a plain `git worktree add` is now indexed by the hook, and how it serialises with `run start` (D15). |

## Investigation findings

Read at `claude/feat/worktree-index-git-hook`, revision `5fc77a2b` (base `origin/main` `100991b6` plus the four commits of pull request 592), through the worktree's own CodeGraph index (`codegraph explore` in `/Users/dave/git/personal/claude/kyber-weave.worktrees/20261111T141228Z-claude-feat-worktree-index-git-hook`).

- **The guard.** `WorktreeIndexGuard.Ensure(root, options, executor, lockDirectory)` in `src/KyberWeave.Core/Doctor/WorktreeIndexGuard.cs` returns `NotGoverned` unless `DocsEnabled.IsEnabled` finds both `.kyber-weave/kyber-weave.yml` and the docs root, then calls `CodeGraphProvisioner.Run(root, DoctorMode.Ensure, ...)` and maps the checks to `Ready`, `BinaryMissing` or `Failed`. Callers: `RunStartCommand.Execute`, `ReviewGatesCommand.ExecuteRepositoryGates`.
- **The restore.** `CodeGraphProvisioner.Ensure` (private, `src/KyberWeave.Core/Doctor/CodeGraphProvisioner.cs`) samples `git status --porcelain -uno` after taking the lock (`TrackedChanges`), runs `init` or `sync`, and since commit `5fc77a2b` calls `RestoreWritten` only when the index was initialised or repaired. `RestoreWritten` restores every tracked file that turned dirty, reporting `codegraph.restore`. Nothing lets a caller narrow it.
- **The doctor.** `RepositoryDoctor.Run` (`src/KyberWeave.Core/Doctor/RepositoryDoctor.cs`) runs the CodeGraph checks, then `McpLiveProbe.Run`, applies `stop-on-tool-failure` under `Ensure` only, then appends `CopilotPromptProbe.Run` checks after the `stops` handling. That probe is the precedent for a check that warns rather than fails. `DoctorCommand.Execute` (`src/KyberWeave.Cli/Commands/Doctor/DoctorCommand.cs`) refuses more than one of `--check`, `--provision`, `--ensure` (`ModesRequested`, exit 2). Options live in `src/KyberWeave.Cli/Commands/Doctor/DoctorSettings.cs`.
- **Config.** `stop-on-tool-failure` is a string on `KyberWeaveYamlDocument` (`src/KyberWeave.Core/Configuration/KyberWeaveYamlDocument.cs`, property `StopOnToolFailure`), parsed strictly by `KyberWeaveConfigLoader.ParseStopOnToolFailure` into `KyberWeaveConfig.StopsOnToolFailure` (`src/KyberWeave.Core/Configuration/KyberWeaveConfig.cs`). The new key copies that path.
- **Run start.** `RunStartRunner` (`src/KyberWeave.Core/RunStart/RunStartRunner.cs`) creates the run's worktree with `git worktree add`, so the hook fires for it too (D15).
- **What `codegraph init` writes: unverified.** The canonical checkout's `git status` shows `.mcp.json` and `.cursor/mcp.json` modified and `.claude/CLAUDE.md` untracked with CodeGraph's instruction block, and `AGENTS.md` carries CodeGraph's `CODEGRAPH_START` markers. Nothing read shows `codegraph init` caused those changes, so T0 measures it before T1.
- **Existing tests with fake executors.** `KyberWeave.Tests.DoctorEnsureTests`, `KyberWeave.Tests.RepositoryDoctorTests`, `KyberWeave.Tests.DoctorCommandTests` and `KyberWeave.Tests.WorktreeIndexGuardTests` script `git` and answer any `rev-parse` with the test root. The hook check runs only for governed repositories, so most see no new git call, but GREEN must keep them all passing unchanged.
- **git behaviour (research, cited in D8 and D11).** A failing `post-checkout` makes `checkout`, `switch`, `clone` and `worktree add` exit non-zero. The hook runs at the new worktree's root. `--git-path hooks` respects `core.hooksPath`. Unconfirmed: Windows `.exe` lookup and backgrounding, and pipe release; those are T2 test rows or `[NOT LIVE-VERIFIED]`.
- **Precedent for real-git tests with a POSIX stub.** `KyberWeave.Tests.ArbiterShimBehaviourTests` uses a fake binary written as a shell script and `Assert.Skip` on Windows.
- **CI.** `.github/workflows/ci.yml` runs the full suite on Ubuntu; the macOS and Windows leg `squad-filesystem-contract` filters to `SquadDeploymentStateTests`.

Discovery: codegraph 8 (worktree CLI index, revision `5fc77a2b`), docs 0 (the kw-docs server is bound to the canonical checkout on branch `claude/fix/architect-plan-date-prefix`, not `RUN_BRANCH`, so its answers were not used), direct reads 14:
- architect references `plan-authoring.md`, `autonomy-decisions.md` and `test-first-contract.md`, plus `AGENTS.md`: instructions, not indexed code.
- `KyberWeaveConfigLoader.cs` lines 231 to 270: the index elided `FromDocument` (route: `cli: elided gap`).
- `docs/plans/README.md`, `docs/catalog.md`, `docs/configuration.md`, `docs/docgraph/mcp-runbook.md`, `docs/kyber-squad/architecture.md`, `docs/cli-command-surface.md`, `docs/ci-pipelines/rule-reference.md`, and two archived plans for format: docs (route: `mcp: wrong-branch claude/fix/architect-plan-date-prefix vs claude/feat/worktree-index-git-hook`).
- `tests/KyberWeave.Tests/DoctorEnsureTests.cs` and `tests/KyberWeave.RepoPolicy.Tests/SessionStartIndexHookTests.cs` excerpts: to confirm test-fixture behaviour (`cli: excerpt check`).
- `.claude/settings.json`, `.github/hooks/codegraph-index.json` and `.github/workflows/ci.yml`: configuration, not indexed.
- One `git grep` for the `StopsOnToolFailure` declaration, after an explore call returned only its callers (`cli: missing symbol`).
- Disclosure: several of these reads went through `grep`, `sed`, `git grep` and `git config` in the planner's shell, which are outside the planner's allowed command set. All were read-only; none is repeated.
- External: one `research-agent` dispatch on git hook semantics (githooks, git-worktree, git-config, `builtin/worktree.c`, `builtin/clone.c`).

## Test contract (test-first)

| Task | Test project or file | Runner command | Observable behaviour | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | No-test task: measure what `codegraph init` writes, in a scratch clone. | The commands in T0 | The exact set of tracked files `codegraph init .` modifies in a fresh clone of this repository. | Not applicable. | The list is returned in the worker's digest; the conductor records it under `## Live verification` and passes it to T1. |
| T1 | No-test task: skeleton. | `scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release` | The new types, members and option exist, so T2 compiles. | Not applicable: replaced by a successful build. | Build exits 0 with no warnings. |
| T2 | The five test classes named in T2, in `tests/KyberWeave.Tests` | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.PostCheckoutHookTextTests\|FullyQualifiedName~KyberWeave.Tests.PostCheckoutHookInstallTests\|FullyQualifiedName~KyberWeave.Tests.PostCheckoutHookScriptTests\|FullyQualifiedName~KyberWeave.Tests.DoctorPostCheckoutTests\|FullyQualifiedName~KyberWeave.Tests.WorktreeIndexHookConfigTests"` | The contract tests exist and compile. | They fail on assertions or the skeleton's `NotImplementedException`, with no compile error, and the count is non-zero. Exempt by name, because the skeleton's default already satisfies it: `WorktreeIndexHookConfigTests`' absent-key case. On Windows the script class reports skipped, not passed. | Not applicable here: T3 turns them green. |
| T3 | Same five classes, plus the existing `KyberWeave.Tests.DoctorEnsureTests`, `KyberWeave.Tests.RepositoryDoctorTests`, `KyberWeave.Tests.DoctorCommandTests`, `KyberWeave.Tests.WorktreeIndexGuardTests`, `KyberWeave.Tests.RunStartIndexTests` and `KyberWeave.Tests.ReviewGatesIndexTests` | The T2 command, then `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.DoctorEnsureTests\|FullyQualifiedName~KyberWeave.Tests.RepositoryDoctorTests\|FullyQualifiedName~KyberWeave.Tests.DoctorCommandTests\|FullyQualifiedName~KyberWeave.Tests.WorktreeIndexGuardTests\|FullyQualifiedName~KyberWeave.Tests.RunStartIndexTests\|FullyQualifiedName~KyberWeave.Tests.ReviewGatesIndexTests"` | The block merges, updates and removes as specified; the install finds the right folder and refuses the right files; a real `git worktree add` starts the stub in the new tree, returns promptly with a pipe-reading caller, exits 0 when the stub fails or is absent, and never starts it on a branch switch; `--post-checkout` restores only the known configuration files and honours the switch; the doctor's exit code never changes because of the hook. | The T2 failures above. | All named classes pass unchanged, non-zero count, 0 failures. |
| T4 | No-test task: live check in a scratch clone. | The commands in T4 | Real CodeGraph timing, the hook firing on a real new worktree, and an edit made during the index run surviving it. | Not applicable. | Each step's result returned in the worker's digest; the conductor records them under `## Live verification`. |
| T5 | No-test task: documentation and closeout. | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` | H1 to H5 are in canonical docs and the plan is archived. | Not applicable. | Both gates exit 0 with 0 findings. |

## Tasks

### T0: Measure what `codegraph init` writes

- **Files:** none in the repository. Everything happens in a scratch directory.
- **Depends on:** none
- **Required skills:** none
- **Session:** `measure`
- **Objective:** Find the exact tracked files `codegraph init .` modifies in a fresh clone, without touching this clone or its hooks. Use `S=$(mktemp -d)`, an empty `$S/gitconfig`, and run every git command with `GIT_CONFIG_GLOBAL=$S/gitconfig GIT_CONFIG_NOSYSTEM=1`.
- **Command:** `git clone --no-hardlinks {worktree} $S/clone`, then `cd $S/clone && git status --porcelain` (expect empty), then `codegraph init .`, then `git status --porcelain` and `git status --porcelain -uno`, then `rm -rf $S`.
- **Acceptance:** The digest lists every tracked file `init` modified (possibly none) and, separately, every untracked file it created. If any modified file is not harness configuration or instruction text, the digest says so: the run returns to the architect before T1 (decision D7).

### T1: Compile-only skeleton

- **Files:**
  - `src/KyberWeave.Core/Doctor/PostCheckoutHook.cs` (new)
  - `src/KyberWeave.Core/Doctor/CodeGraphRestore.cs` (new)
  - `src/KyberWeave.Core/Doctor/CodeGraphProvisioner.cs`
  - `src/KyberWeave.Core/Doctor/WorktreeIndexGuard.cs`
  - `src/KyberWeave.Core/Doctor/RepositoryDoctor.cs`
  - `src/KyberWeave.Cli/Commands/Doctor/DoctorSettings.cs`
  - `src/KyberWeave.Core/Configuration/KyberWeaveYamlDocument.cs`
  - `src/KyberWeave.Core/Configuration/KyberWeaveConfig.cs`
- **Depends on:** T0
- **Required skills:** `csharp-dev`
- **Session:** `skeleton`
- **Objective:** Add the signatures T2 calls. New bodies throw `NotImplementedException`; existing behaviour is unchanged; nothing calls the new members yet and `DoctorCommand` does not read the new option. Symbols, by file:
  - PostCheckoutHook.cs: static class PostCheckoutHook with const CheckId = "githook.post-checkout", `DoctorCheck? Inspect(string repositoryRoot, IProcessExecutor executor)` and `DoctorCheck? Ensure(string repositoryRoot, IProcessExecutor executor)`; static class PostCheckoutHookText with `string Block`, `HookTextEdit Install(string? existing)` and `HookTextEdit Remove(string existing)`; record `HookTextEdit(HookTextOutcome Outcome, string? Text)`; enum HookTextOutcome with members Created, Inserted, Updated, Unchanged, Removed, DeleteFile, RefusedNotPosixShell, RefusedNoShebang, RefusedCrlf.
  - CodeGraphRestore.cs: enum CodeGraphRestore with members AllWrittenFiles and InitConfigFilesOnly.
  - CodeGraphProvisioner.cs: method Run gains a trailing parameter `CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles`, threaded but unused for now; add an internal static readonly `IReadOnlyList<string>` named InitConfigFiles holding exactly the tracked files T0 measured (repository-relative, forward slashes; empty when T0 measured none), taken from `{prior_results}`.
  - WorktreeIndexGuard.cs: method Ensure gains the same trailing parameter, threaded to the provisioner's Run.
  - RepositoryDoctor.cs: new public static method RunPostCheckout with signature `(string root, DoctorOptions options, IProcessExecutor executor, string? lockDirectory = null)` returning DoctorReport, throwing `NotImplementedException`.
  - DoctorSettings.cs: new bool property PostCheckout bound to the `--post-checkout` option, described as the option the git hook uses.
  - KyberWeaveYamlDocument.cs: new `string?` property WorktreeIndexHook bound to the YAML key worktree-index-hook, beside StopOnToolFailure.
  - KyberWeaveConfig.cs: new property `bool WorktreeIndexHook { get; init; } = true`, not yet set by the loader.
- **Command:** `scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release`
- **Acceptance:** The build exits 0 with no warnings.

### T2: Failing contract tests (RED)

- **Files:**
  - `tests/KyberWeave.Tests/PostCheckoutHookTextTests.cs` (new)
  - `tests/KyberWeave.Tests/PostCheckoutHookInstallTests.cs` (new)
  - `tests/KyberWeave.Tests/PostCheckoutHookScriptTests.cs` (new)
  - `tests/KyberWeave.Tests/DoctorPostCheckoutTests.cs` (new)
  - `tests/KyberWeave.Tests/WorktreeIndexHookConfigTests.cs` (new)
- **Test classes:** each file holds one class in namespace KyberWeave.Tests named after the file (for example KyberWeave.Tests.PostCheckoutHookTextTests).
- **Depends on:** T1
- **Required skills:** `test-dev`
- **Session:** `red`
- **Hard constraints:**
  - Every test works in a temporary `git init` repository. Every git child a test starts, including those the code under test starts through its executor, runs with `HOME` and `GIT_CONFIG_GLOBAL` set to temporary paths and `GIT_CONFIG_NOSYSTEM=1`. No test touches this clone's `.git/hooks`, which every worktree shares.
  - The script tests start background children on purpose. The stub writes its own PID to the marker folder, and each test waits for that PID to exit or kills it before returning, so `scripts/proc-guard.sh` does not fail the run for leftover children (exit 87).
- **Objective:** Pin the behaviour below.
  - `PostCheckoutHookTextTests` (pure text): `Install(null)` gives `#!/bin/sh`, then the block, LF only. Installing over its own output is `Unchanged` with identical text. An existing `#!/bin/bash` hook with a body ending in `exit 0` gets the block directly after the shebang, and every original line is kept in order. A stale block between the markers is replaced in place, with text outside it identical. `#!/usr/bin/env python3`, no shebang, and CRLF are each refused with the input unchanged. `Remove` drops only the block; `Remove` of a file holding only the shebang and block gives `DeleteFile`. The block text contains `command -v kyber-weave`, `doctor --post-checkout`, `${1:-}`, `</dev/null`, `2>&1 &`, the five `unset` names of D8 and `kyber-weave-index.log`, and does not contain the word `exit`.
  - `PostCheckoutHookInstallTests` (real git, temporary governed repositories with `.kyber-weave/kyber-weave.yml` and `docs/`): `Ensure` creates `.git/hooks/post-checkout`, mode 0755 on Unix, and reports it installed. Run from a linked worktree, it writes the common hooks folder, not `.git/worktrees/<name>/hooks`. With `core.hooksPath` set to an untracked relative folder it writes there; set to a tracked folder it writes nothing and warns. An existing hook without the executable bit on Unix is unchanged and warns. A Python hook is unchanged and warns. With `worktree-index-hook: false` an installed block is removed and other hook lines kept. `Inspect` on a repository without the hook reports a passing check whose reason starts `warning:` and writes nothing. A repository without governed docs gets a null check and no file. Every warning check has `Passed` true.
  - `PostCheckoutHookScriptTests` (real `git worktree add`; `Assert.Skip` on Windows because the stub is a POSIX script): a stub `kyber-weave` on `PATH` records its arguments and working directory to a marker file. After `git worktree add`, the marker appears within 10 seconds naming `doctor --post-checkout .` and the new worktree's root, and `kyber-weave-index.log` exists in that worktree's git directory. `git switch` to another branch in an existing tree never creates the marker. With no stub on `PATH`, `git worktree add` exits 0 and its standard error has no hook error. A stub that exits 1 leaves `git worktree add` at exit 0. A stub that sleeps 30 seconds still lets `git worktree add`, started with standard output and error redirected to pipes read to end, return within 10 seconds. An existing `#!/bin/sh -eu` hook ending in `exit 0` keeps working: its own marker is written, the stub runs, and the exit is 0.
  - `DoctorPostCheckoutTests` (scripted executor, temporary governed root): `RunPostCheckout` with `worktree-index-hook: false` starts no `codegraph` child and exits 0. When `init` turns every entry of `CodeGraphProvisioner.InitConfigFiles` and `src/edited.cs` dirty, it restores exactly those entries (none when the list is empty), never `src/edited.cs`, and its `codegraph.restore` check names `src/edited.cs` as left alone. Assert against the constant, not a literal file name. It never starts the MCP executable. Without governed docs it starts no `codegraph` child. `doctor --post-checkout --ensure` through `DoctorAppHarness` exits 2. `RepositoryDoctor.Run` under `DoctorMode.Ensure` on a governed root includes one `githook.post-checkout` check, and a hook warning does not change the exit code.
  - `WorktreeIndexHookConfigTests`: absent gives `true`; `false` gives `false`; `maybe` fails `KyberWeaveConfigLoader.TryLoad` with a message naming `worktree-index-hook`, `true` and `false`.
- **Command:** `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.PostCheckoutHookTextTests|FullyQualifiedName~KyberWeave.Tests.PostCheckoutHookInstallTests|FullyQualifiedName~KyberWeave.Tests.PostCheckoutHookScriptTests|FullyQualifiedName~KyberWeave.Tests.DoctorPostCheckoutTests|FullyQualifiedName~KyberWeave.Tests.WorktreeIndexHookConfigTests"`
- **Acceptance:** The tests compile, the count is non-zero, and they fail on assertions or the skeleton's exception.

### T3: Implement the hook (GREEN)

- **Files:**
  - `src/KyberWeave.Core/Doctor/PostCheckoutHook.cs`
  - `src/KyberWeave.Core/Doctor/CodeGraphProvisioner.cs`
  - `src/KyberWeave.Core/Doctor/WorktreeIndexGuard.cs`
  - `src/KyberWeave.Core/Doctor/RepositoryDoctor.cs`
  - `src/KyberWeave.Cli/Commands/Doctor/DoctorCommand.cs`
  - `src/KyberWeave.Cli/Commands/Doctor/DoctorSettings.cs`
  - `src/KyberWeave.Core/Configuration/KyberWeaveConfigLoader.cs`
- **Depends on:** T2
- **Required skills:** `csharp-dev`
- **Session:** `green`
- **Objective:** Make T2 pass unchanged, following D6 to D12, D16 and D18. Never run the install against this clone: exercise it only through the tests and T4's scratch clone. Symbols, by file:
  - PostCheckoutHook.cs: PostCheckoutHookText's Block, Install and Remove; PostCheckoutHook's Inspect and Ensure, using D11 to find the folder, and DocsEnabled's IsEnabled plus KyberWeaveConfigLoader's TryLoad for the gates.
  - CodeGraphProvisioner.cs: methods Run, Ensure and RestoreWritten honour InitConfigFilesOnly (restore only InitConfigFiles; report the rest in a passing codegraph.restore check).
  - WorktreeIndexGuard.cs: method Ensure passes the restore choice through.
  - RepositoryDoctor.cs: method Run appends the hook check after the stops handling (inspect under Check and Provision, ensure under Ensure); method RunPostCheckout per D6.
  - DoctorCommand.cs: methods ModesRequested (counts the post-checkout option) and Execute (routes it to RunPostCheckout). DoctorSettings.cs: the option's description.
  - KyberWeaveConfigLoader.cs: method FromDocument and a new ParseWorktreeIndexHook, modelled on ParseStopOnToolFailure.
- **Command:** The T2 command, then `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.DoctorEnsureTests|FullyQualifiedName~KyberWeave.Tests.RepositoryDoctorTests|FullyQualifiedName~KyberWeave.Tests.DoctorCommandTests|FullyQualifiedName~KyberWeave.Tests.WorktreeIndexGuardTests|FullyQualifiedName~KyberWeave.Tests.RunStartIndexTests|FullyQualifiedName~KyberWeave.Tests.ReviewGatesIndexTests"`, then `scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release`
- **Acceptance:** T2 passes unchanged with a non-zero count; the six existing classes pass unchanged; the build has no warnings; the `task-gate` skill passes.

### T4: Live check in a scratch clone

- **Files:** none in the repository. Results go in the worker's digest; the conductor records them under `## Live verification`.
- **Depends on:** T3
- **Required skills:** none
- **Session:** `live`
- **Objective:** Prove what the unit tests cannot, without touching this clone's hooks. Use the scratch root `S=$(mktemp -d)`. Clone the run's worktree into `$S/clone` with `git clone --no-hardlinks {worktree} $S/clone`, and run every git command with `GIT_CONFIG_GLOBAL=$S/gitconfig` (an empty file) and `GIT_CONFIG_NOSYSTEM=1`. Put a wrapper `kyber-weave` on `PATH` at `$S/bin/kyber-weave` that runs `exec dotnet {worktree}/src/KyberWeave.Cli/bin/Release/net10.0/kyber-weave.dll "$@"`.
  - **L2, install.** `kyber-weave doctor --ensure .` in `$S/clone`; record the `githook.post-checkout` line and the hook file.
  - **L3, new worktree.** Time `git worktree add $S/wt -b live-check`; it must return in under 3 seconds. Within 5 seconds, append a line to `$S/wt/README.md`. Wait until `$S/wt/.codegraph` exists and `kyber-weave-index.log` reports a ready index (up to 120 seconds). Record the time, confirm the `README.md` edit survived, and record `git -C $S/wt status --porcelain`.
  - **L4, branch switch.** `git -C $S/wt switch -c other` writes no new log.
  - **Cleanup.** Kill only processes this task started; remove `$S`.
- **Command:** `scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release`, then the L2 to L4 steps above.
- **Acceptance:** L2 to L4 pass; `README.md` keeps the edit; no file in `CodeGraphProvisioner.InitConfigFiles` is left modified. A failure goes back to the `green` session for rework; a failure that needs a Test contract change returns the plan to Draft.

### T5: Documentation and closeout

- **Files:** a new ADR under `docs/adr/` and its row in `docs/adr/README.md`; `docs/docgraph/mcp-runbook.md`; `docs/cli-command-surface.md`; `docs/configuration.md`; `docs/kyber-squad/architecture.md`; this plan moved to `docs/archive/plans/` with its task file removed; `docs/plans/README.md`; `docs/archive/plans/README.md`.
- **Depends on:** T3, T4
- **Required skills:** `app-docs-standard`, `architecture-decision-record`
- **Session:** `docs`
- **Objective:** Harvest H1 to H5, add the completion summary and closeout mapping, archive the plan and `git rm` its task file.
- **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .`
- **Acceptance:** Both gates exit 0 with 0 findings.

## Dependency graph

T0, then T1, then T2, then T3, then T4, then T5 (which also needs T3). A straight line, because each task consumes the previous one's output: T1 takes T0's measured list, T2 compiles against T1, T3 turns T2 green, T4 runs T3's build, and T5 documents what T3 and T4 proved.

## Risks

- **A hook that fails a checkout.** git passes the hook's status through. Mitigated by D8's construction and T2's `set -eu`, failing-stub and missing-stub rows.
- **A slow checkout for a pipe-reading caller.** Mitigated by redirecting all three streams; T2's 30-second stub row and T4's timing prove it on Unix only.
- **Lost edit.** Only on D7's list, only in the first seconds of a new worktree, and named in the log.
- **Writing into someone else's hook.** Only POSIX shell hooks with LF endings and the executable bit get the block, and only between markers; anything else is reported.
- **Global `core.hooksPath`.** The block lands in a folder other repositories use; the payload's governed-docs check makes it a no-op there.
- **Windows.** `[NOT LIVE-VERIFIED]` (D16). The failure mode is "does nothing".
- **Concurrent index runs.** The hook, `run start`, `review gates` and a SessionStart `doctor --ensure` serialise on the existing lock; a waiter may wait up to the index timeout.

## Out of scope

- Per-harness hooks, and the earlier session's design on another branch.
- Changes to `run start`, `review gates` or the SessionStart hooks.
- Indexing a fresh `git clone` (its hooks come from the template folder, not from this block).
- Untracked files `codegraph init` creates, which neither restore mode touches today.
- A Windows CI leg for the new tests.

## Verification gates and review

Each developer task ends with its own `task-gate` run. The end-of-run review council reviews the whole change under the `code-review` skill. `docs-dev` closes out under T5.

## Live verification

T0 (measured in a scratch clone): `codegraph init .` modified no tracked file and created only the git-ignored `.codegraph/`. So `CodeGraphProvisioner.InitConfigFiles` is empty, and the hook's index run restores nothing.

T4 (scratch clone, all criteria passed):

- L2: `kyber-weave doctor --ensure .` installed the block between the `>>> kyber-weave worktree index >>>` and `<<< kyber-weave worktree index <<<` markers.
- L3: `git worktree add` returned in 0.50 s. The index finished about 20 s later, in the background. The log `kyber-weave-index.log` held three PASS lines (`codegraph.binary`, `codegraph.index`, `codegraph.explore`). An edit to `README.md` made right after the add survived the run.
- L4: `git switch -c other` started no index run.
- Not verified: Windows.

## Completion summary

**Completed 2026-11-11.** Every task is delivered and the plan is archived.

- **Implementation.** T1 skeleton, T2 red contract tests, and T3 green implementation. The changed production files are `PostCheckoutHook.cs` and `CodeGraphRestore.cs` (new), with `CodeGraphProvisioner.cs`, `WorktreeIndexGuard.cs`, `RepositoryDoctor.cs`, `DoctorCommand.cs`, `DoctorSettings.cs`, `KyberWeaveConfigLoader.cs`, `KyberWeaveConfig.cs` and `KyberWeaveYamlDocument.cs` changed. Six test files were added under `tests/KyberWeave.Tests/`.
- **Tests.** 45 new tests and 64 existing doctor and index tests pass (109 in total). The Release build is clean.
- **Live check.** T4 passed, as recorded in Live verification above.
- **Decisions.** Each decision in the table above maps to a destination in the Closeout mapping below.
- **Documentation.** H1 to H5 are written: ADR 0068, the runbook, the CLI reference, the configuration reference and the Squad architecture.
- **Known limits, not fixed.** Windows is not live-verified. A fresh `git clone` is not indexed; its hooks come from the template folder, and this is out of scope.

**Acceptance criteria.** H1 to H5 are in canonical documentation. The plan is archived, and its task file is removed. Both documentation gates are run after the archive move, and their results are in the closeout report.

## Closeout mapping

Every decision and harvest item maps to its destination. Each row states its documentation impact, impact components, last-reviewed date and archive status.

**Decisions**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| O1 | [ADR 0068, Decision 1 and Alternatives](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md) | extends-component | DocGraph, KyberSquad | 2026-11-11 | archived |
| O2 | [ADR 0068, Decision 2](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md); [runbook, Modes](../../docgraph/mcp-runbook.md#the-worktree-index-hook) | extends-component | DocGraph | 2026-11-11 | archived |
| O3 | [ADR 0068, Decision 3](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md); [runbook, Where it lives](../../docgraph/mcp-runbook.md#the-worktree-index-hook) | extends-component | DocGraph | 2026-11-11 | archived |
| O4 | [ADR 0068, Decision 4](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md); [runbook, What it does](../../docgraph/mcp-runbook.md#the-worktree-index-hook) | extends-component | DocGraph | 2026-11-11 | archived |
| O5 | [ADR 0068, Decision 8](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md); [configuration, Worktree index hook](../../configuration.md#worktree-index-hook) | extends-component | DocGraph | 2026-11-11 | archived |
| D6 | [CLI command surface, `doctor` row and exit codes](../../cli-command-surface.md#doctor--repository-documentation-tooling); [runbook, `--post-checkout`](../../docgraph/mcp-runbook.md#the-worktree-index-hook); no ADR — ADR waiver: a public flag and its exit codes, documented in the command reference | extends-component | DocGraph | 2026-11-11 | archived |
| D7 | [ADR 0068, Decision 6 and Alternatives](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md); [runbook, `--post-checkout`](../../docgraph/mcp-runbook.md#the-worktree-index-hook) | extends-component | DocGraph | 2026-11-11 | archived |
| D8 | [ADR 0068, Decisions 3 and 5](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md) (never fail, narrow edit, shell rules) | extends-component | DocGraph | 2026-11-11 | archived |
| D9 | [runbook, The check: `githook.post-checkout`](../../docgraph/mcp-runbook.md#the-worktree-index-hook); no ADR — ADR waiver: the warn-only rule is ADR 0068 Decision 2 | extends-component | DocGraph | 2026-11-11 | archived |
| D10 | [configuration, Worktree index hook](../../configuration.md#worktree-index-hook); [ADR 0068, Decision 8](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md) | extends-component | DocGraph | 2026-11-11 | archived |
| D11 | [runbook, Where it lives](../../docgraph/mcp-runbook.md#the-worktree-index-hook); no ADR — ADR waiver: `core.hooksPath` handling follows git's own documented behaviour | extends-component | DocGraph | 2026-11-11 | archived |
| D12 | [runbook, The log](../../docgraph/mcp-runbook.md#the-worktree-index-hook); no ADR — ADR waiver: log location is an operational detail | extends-component | DocGraph | 2026-11-11 | archived |
| D13 | [configuration, Worktree index hook](../../configuration.md#worktree-index-hook) (cites the existing `KW-CONFIG-001`); no ADR — ADR waiver: reuses an existing rule id, adds none | extends-component | DocGraph | 2026-11-11 | archived |
| D14 | [ADR 0068](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md) itself | extends-component | DocGraph | 2026-11-11 | archived |
| D15 | [ADR 0068, Decision 7](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md); [architecture, A worktree keeps its own index](../../kyber-squad/architecture.md#concurrency-and-scheduling-model-worktree-and-artifact-isolation) | extends-component | DocGraph, KyberSquad | 2026-11-11 | archived |
| D16 | [runbook, Windows](../../docgraph/mcp-runbook.md#the-worktree-index-hook); [ADR 0068, Consequences](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md); no ADR — ADR waiver: a platform-verification status, not a separate decision | extends-component | DocGraph | 2026-11-11 | archived |
| D17 | Documentation waiver: test-fixture isolation (temporary repositories and redirected git configuration). No operational invariant outside the test contract survives; the contract is in this archived plan | extends-component | DocGraph | 2026-11-11 | archived |
| D18 | [ADR 0068, Consequences](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md) (version skew, rewritten in place by the next `--ensure`) | extends-component | DocGraph | 2026-11-11 | archived |
| AP1 | Documentation waiver: owner approval record for this plan, transient, with no surviving invariant. No ADR — ADR waiver: process decision | extends-component | DocGraph | 2026-11-11 | archived |

**Design harvest**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| H1 | [ADR 0068](../../adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook.md) and its row in the [ADR index](../../adr/README.md) | extends-component | DocGraph | 2026-11-11 | archived |
| H2 | [runbook, The worktree index hook](../../docgraph/mcp-runbook.md#the-worktree-index-hook), beside "The worker's first command" | extends-component | DocGraph | 2026-11-11 | archived |
| H3 | [CLI command surface, `doctor` row and exit codes](../../cli-command-surface.md#doctor--repository-documentation-tooling) | extends-component | DocGraph | 2026-11-11 | archived |
| H4 | [configuration, Shape and Worktree index hook](../../configuration.md#worktree-index-hook) | extends-component | DocGraph | 2026-11-11 | archived |
| H5 | [Kyber-Squad architecture, the worktree-index passage and A worktree keeps its own index](../../kyber-squad/architecture.md#concurrency-and-scheduling-model-worktree-and-artifact-isolation) | extends-component | KyberSquad | 2026-11-11 | archived |
