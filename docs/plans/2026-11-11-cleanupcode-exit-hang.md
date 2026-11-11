---
id: plans/2026-11-11-cleanupcode-exit-hang
title: Let the cleanupcode fix pass finish on a machine where the tool never exits
doc-type: plan
status: current
component: ReviewCouncil
owner: dpalfery
created: 2026-11-11
last-reviewed: 2026-11-11
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# Let the cleanupcode fix pass finish on a machine where the tool never exits

**Status: Ready, 2026-11-11.** Approved for execution under decision AP1 (autonomous approval of gate `approve-and-execute`). Task file: `docs/plans/2026-11-11-cleanupcode-exit-hang.tasks.json`.
**Development mode:** `test-first` (RED by `test-dev`, GREEN by the implementation specialist, never the same worker).
**Autonomy:** `autonomous` (every open item decided by recommendation; provenance in `## Decisions`).
**Scale:** `extends-component`, component `ReviewCouncil`.
**Delivery:** one draft pull request from branch `claude/fix/cleanupcode-exit-hang`, stacked on `claude/fix/inspectcode-gate-timeout` (draft pull request 594) and targeted at that branch; retargeted to `main` when 594 merges. No merge.

## Problem and goal

**Background.** A developer agent that changed C# may run ReSharper's `cleanupcode` as a mechanical fix pass before its task gate. The pass is optional and off by default; the `resharper-clt` skill is its single home. Pull request 594 added `scripts/report-gate.sh`, a wrapper that stops ReSharper `inspectcode` once its report is complete, because on macOS 27.3 Beta the ReSharper command line tools never exit.

**Problem.** `cleanupcode` hangs the same way, and worse:

1. Run as agents run it today (with its own pre-build), it hangs right after the pre-build's restore step and changes no file.
2. With `--no-build` it fixes the file in about 11 seconds, then hangs at exit.
3. It writes no report and prints no "done" line. Its only finish signal is the log line `Close solution instance` (logger `SolutionHostManager`, level VERBOSE), written in the same second the file is saved.
4. Nothing bounds a hand-run command. One such process has been stuck for more than 8 hours.

Cause, measured 2026-11-11 and not re-diagnosed here: .NET never returns from closing a listening socket while another thread is blocked in `accept()` on it, and the ReSharper tools do that after their pre-build and again at exit.

**Goal.** A workaround, small and easy to remove once the OS, .NET or JetBrains side is fixed:

- `scripts/report-gate.sh` can finish on "the watched file contains this text", and has a hard time limit of its own that stops the command's process tree and exits 124.
- In this repository the fix pass runs `cleanupcode --no-build` through the wrapper, keyed on its log line, so it returns, with the file fixed, or fails.
- A run whose finish signal never appears fails. It never passes.

## Decisions

Every open item was decided by recommendation under the autonomous-decisions rule. `autonomous: recommendation accepted` means decided and awaiting the owner's approval at the approve-and-execute gate. `autonomous: low-confidence, no authoritative source` marks a call no source settled; the pull request body lists those rows for review.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | How does the wrapper recognise a finish line that is not at the end of the file? | **A new option `--contains <text>`, mutually exclusive with `--ends-with`.** Exactly one of the two is required; both or neither is a usage error (exit 2). In `--contains` mode the file is complete when a fixed-string search of the whole file finds the text. | `complete_size` today reads only the last 4096 bytes, which a VERBOSE log outgrows after the finish line (`closing server socket` follows it). A second mode keeps `--ends-with` byte-for-byte unchanged for the `inspectcode` gate. | autonomous: recommendation accepted |
| D2 | When does `--contains` mode count the run as done? | **`--grace` seconds after the text is first seen, whether or not the file keeps growing.** `--ends-with` keeps its rule (size unchanged for `--grace` seconds). | The size rule needs the file to stop growing. Whether other ReSharper threads keep logging after the main thread blocks is unmeasured, and a log that keeps growing would never settle, so the run would hang until the time limit and fail although the work was done. The finish line itself is the evidence; the grace period only gives a healthy process the chance to exit on its own. | autonomous: low-confidence, no authoritative source. Cheap to reverse (one script branch). |
| D3 | Does the wrapper get its own time limit? | **Yes: `--timeout <seconds>`, optional, a positive integer.** When it elapses before the run counts as done, the wrapper stops the command's whole process tree, says so on standard error, and exits 124. A finish seen before the limit still passes. Without `--timeout`, behaviour is unchanged. | No gate runner bounds a command an agent runs by hand. 124 is the code `timeout(1)` and `scripts/proc-guard.sh` already use for an elapsed limit. The `inspectcode` gate does not pass it; its `timeout-seconds` still bounds it. | autonomous: recommendation accepted |
| D4 | Where does this repository's exact wrapped command live? | **In the C# coding standard, `docs/standards/csharp/README.md`, section `## Completion`**: one optional step after the format check, giving the command and why it is wrapped. | [ADR 0064](../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) (accepted) decides that "how to work and finish lives in one `## Completion` section in each coding standard", read through `docs_get_coding_standard_for_technology('csharp', 'completion')`, and that runnable checks otherwise live in `review.gates` / `review.task-gates`. The same standard already names the `KyberWeave` cleanup profile. Rejected: a `review.task-gates` entry, which would run the pass on every task and turn it on by default (constraint), and whose `fix`/`run` pair has no verify form for a fixer; the root `AGENTS.md` `## Commands` section, which ADR 0064 does not name as a home and which would become a second answer to "how do I finish" (first drafted here, moved after reading the ADR); `docs/code-review/architecture.md`, which workers do not read before a task gate (it gets the wrapper contract at closeout, H1). | autonomous: recommendation accepted (source: ADR 0064, decision items 2, 3 and 5) |
| D5 | What does the portable `resharper-clt` skill say? | **Two things, nothing repository-specific:** the `cleanupcode` line gains `--no-build` (in the fix-pass command and in the worked example), and one sentence says that when the host's C# coding standard gives its own `cleanupcode` command in its `## Completion` section, the agent runs that one instead. `scripts/report-gate.sh` is not named in the skill. | ADR 0064 item 5: a portable file names the host's section, not the fact. `products/kyber-squad/skills/**` ship to repositories where the wrapper does not exist. `--no-build` is portable: the `dotnet format` steps before it restore the solution, and the pre-build adds time and is where the first hang occurs. The `task-gate` skill already defers to `resharper-clt` and is not edited. | autonomous: recommendation accepted |
| D6 | Is `--no-build` a documented `cleanupcode` option? | **Not on the 2026.2 help page; it is used on the strength of the 2026-11-11 measurement** (the tool pinned at 2026.2.1 accepted it and fixed the file). | The JetBrains Rider 2026.2 CleanupCode page documents `--LogFile`, `--LogFolder` and `--LogLevel` but not `--no-build`, and says the solution should be built before cleanup. T3 re-confirms the option on this machine. If a later tool version rejects it, the command fails loudly (non-zero), never silently. | autonomous: low-confidence, no authoritative source. Cheap to reverse. |
| D7 | Which file does the wrapper watch? | **`--LogFile=<absolute path>` with `--LogLevel=VERBOSE`, a per-run path under `.agent_scratchpad/`, passed to the wrapper as `--report`.** | The finish line was measured with `--LogFolder`, which writes several files whose names were not recorded; the wrapper deletes and watches one fixed path. JetBrains documents `--LogFile` as "an absolute path to the log file for messages from CleanupCode", so the `SolutionHostManager` line is expected there, but this was not tried. T3 verifies it before any wording lands. Fallback if `--LogFile` lacks the line: T3 reports it, and the plan returns to Draft for a revised D7 (a scope change, not a worker decision). | autonomous: low-confidence, no authoritative source. Cost of being wrong: one rework of T4's command only; T1 and T2 are unaffected. |
| D8 | Which cleanup profile does the host command use? | **`KyberWeave`**, the profile `KyberWeave.sln.DotSettings` declares. | `docs/standards/csharp/README.md` names it as the profile that applies this repository's settled style (explicit types). The measurement used `Built-in: Reformat Code`, which only reformats; the skill asks for the host's profile. T3 and T4 run with `KyberWeave`, so the real run proves the command as written. | autonomous: recommendation accepted |
| D9 | What time limit and grace does the host command use? | **`--timeout 300`, `--grace 5`.** | The measured run fixed the file 11 seconds after start; a cold solution load is slower, and 300 seconds leaves room without letting a hang last hours. Five seconds after the finish line gives a healthy tool time to exit by itself. | autonomous: low-confidence, no authoritative source. Cheap to reverse (two numbers in one sentence). |
| D10 | Does a finish line prove the cleanup succeeded? | **No, and the plan accepts that.** A stopped run exits 0 and the tool's own exit code is lost. T3 runs one negative probe (a profile that does not exist) and records whether the finish line also appears there. | `cleanupcode` is a fixer, not a gate: the task gate's `format-whitespace` and `format-style` verify forms and the council's `inspectcode` still judge the result, so a cleanup that silently did less is caught downstream. Risk R2. | autonomous: recommendation accepted |
| D11 | Is there a restore precondition for `--no-build`? | **Yes, stated with the command:** run it after the skill's two `dotnet format` steps, which restore the solution. | The `inspectcode` gate guards the same hazard with `requires-paths`; this command is not a gate, so the precondition is stated in the sentence that gives the command. Cleanup on an unrestored tree resolves nothing and could make wrong edits. | autonomous: recommendation accepted |
| D12 | How is the script checked with no `shellcheck` on this machine? | **`bash -n scripts/report-gate.sh` plus the contract tests, which run the script under `/bin/bash` (bash 3.2 on macOS).** No bash 4 features (`declare -A`, `mapfile`, `wait -n`, `${v,,}`, `&>>`, `|&`). CI's own shell checks still apply on the pull request. | Running the tests under the oldest bash the script must support is a stronger check of portability than a lint of syntax. | autonomous: recommendation accepted |
| D14 | Which wins when the finish text is seen but `--timeout` elapses inside the grace period? | **The time limit.** `--timeout` is absolute: if the run has not counted as done (D2) when it elapses, the wrapper stops the tree and exits 124. Each one-second tick checks "done" before "time limit". | One predictable rule; the host command's 300 s limit is far longer than the 5 s grace, so the overlap only matters for misconfigured values. | autonomous: recommendation accepted |
| D15 | What if `Close solution instance` appears before the file is saved, or more than once? | **T3 records every occurrence with its timestamp against the file's save time. More than one occurrence, or one before the save, is the D7 fallback**: the plan returns to Draft. | D2 counts the run done on the first sighting, so an early line would stop the tool before it saved and exit 0 with nothing fixed. The 2026-11-11 measurement saw it in the same second as the save, once. | autonomous: recommendation accepted |
| D13 | Development mode and test contract. | **`test-first`**, with the Test contract below. | Default mode; no opt-out was supplied. | autonomous: recommendation accepted |
| AP1 | Is the plan approved for execution? | **Approved.** D1 to D15 and the Test contract are accepted as recorded, and the plan is executed as written. | Conductor input `AUTONOMOUS_APPROVAL: approve-and-execute` under the repository's resolved autonomy `autonomous`, 2026-11-11; no human answered the gate. The low-confidence rows D2, D6, D7 and D9 stay flagged for the pull request body. Since the Draft, the base branch's review fix changed `scripts/report-gate.sh` (`rm -f -- "${report}"`, and exit 2 when `pgrep` is not on the PATH); the tasks name the script by function, not line, so none moved, and T2 keeps both. | autonomous: recommendation accepted (AUTONOMOUS_APPROVAL approve-and-execute, 2026-11-11) |

## Investigation findings

**Discovery:** codegraph 7, docs 0, direct reads 15, web 2.

- **Revision read.** Worktree `/Users/dave/git/claude-worktree/kyber-weave/xenodochial-poitras-d43f49`, branch `claude/fix/cleanupcode-exit-hang`, HEAD `574cb63faac8b6fae0569a0c391df62f1ebc1f61`. CodeGraph was queried through this worktree's own index with the `codegraph explore` CLI (5 calls). The kw-docs server is bound to the main checkout, so no docs answer was used; documents were read directly.
- **Direct reads, 15.** Not indexed by CodeGraph (shell, Markdown, YAML): `scripts/report-gate.sh`, `products/kyber-squad/skills/resharper-clt/SKILL.md`, `products/kyber-squad/skills/task-gate/SKILL.md`, `AGENTS.md` (`## Commands`), `.kyber-weave/kyber-weave.yml` (`inspectcode` gate, `review.task-gates` including `github-devops`'s `shellcheck`), `docs/code-review/architecture.md` and `docs/configuration.md` (the wrapper paragraphs), `docs/standards/csharp/README.md` (`## Completion`, profile), `KyberWeave.sln.DotSettings` (profile name), `docs/adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md`. Explore returned unrelated code for `RootAgentsOperationalPointerTests` and `StandardsCompletionSectionTests` (`cli: wrong-result`), so both were read directly, as was `src/KyberWeave.Core.Squad/KyberWeave.Core.Squad.csproj` (hash guard).
- **C# standard pins** (`tests/KyberWeave.RepoPolicy.Tests/StandardsCompletionSectionTests.cs`): each declared standard has exactly one `## Completion` section that names the build, test and format commands, and no `## Commands` section. Adding one optional step inside `## Completion` keeps all three; T4 runs the class.
- **`github-devops` task gates** include `shellcheck` over `scripts/*.sh`, which is not installed on this machine: T2 reports that gate as skipped for a missing tool, not as failed (D12).
- **Installed CLI.** `kyber-weave docs validate .` reports one warning, `KW-CONFIG-002` (unknown gate key `concurrent`), because the installed CLI predates pull request 594's new key. It is not caused by this plan.
- **Web, 2.** JetBrains Rider 2026.2 CleanupCode help: documents `--LogFile` (absolute path), `--LogFolder`, `--LogLevel`; no `--no-build`.

**Verified facts:**

- **Wrapper today** (`scripts/report-gate.sh`): options `--report`, `--ends-with`, `--grace` (default 20); `complete_size` strips whitespace from the last 4096 bytes and matches the suffix; the main loop polls once a second while the command runs and stops the tree (`kill_tree`, deepest descendants first, `SIGKILL`) when the report is complete and its size has been unchanged for `--grace` seconds; exit 1 when the command exits 0 with no complete report; 143 on `INT`/`TERM`. No time limit of its own.
- **Contract tests** (`tests/KyberWeave.RepoPolicy.Tests/ReportGateScriptTests.cs`, class `KyberWeave.Tests.ReportGateScriptTests`, namespace `KyberWeave.Tests` although the project is RepoPolicy): every case runs through `RunScriptArguments` (one `ProcessRunner.Run` spawn site, already on the allowlist), under `/bin/bash`, with a `sh -c` stand-in, bounded by a 30-second `ProcessRunner` timeout; hang cases assert `TimeoutException` with a 5-second bound. New cases that reuse `RunScriptArguments` add no spawn site, so `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json` is unchanged.
- **Single-home pins** (`tests/KyberWeave.Tests/WorkerRuleSingleHomeTests.cs`): no file under `products/kyber-squad/agents/` may contain `dotnet jb cleanupcode`, and `skills/resharper-clt/SKILL.md` must contain `cleanupcode`. `docs/kyber-squad/worker-rule-map.md` exists, so the class is green at the base.
- **Rendered copies.** `.apm/skills/` holds no `resharper-clt` or `task-gate` copy. The only SHA-256 guard over product files is `VerifyResponseGuidanceDefault` in `KyberWeave.Core.Squad.csproj`, which covers `response-guidance.default.md` only. `SquadCanonicalContentTests` lists `resharper-clt` and `task-gate` among the canonical skills; T4 runs it.
- **`AGENTS.md` is not edited** (D4). Its `## Commands` pins (`RootAgentsOperationalPointerTests`) are therefore untouched.
- **Task gates.** `review.task-gates` in `.kyber-weave/kyber-weave.yml` declares `format-whitespace`, `format-style`, `dotnet-build`, `suppressions` and `tests` for the .NET roles; none runs `cleanupcode`, which keeps the pass off by default.
- **Existing inconsistency, not touched.** `resharper-clt` says the fix pass "MUST" run before `READY_FOR_REVIEW`, while `task-gate` and `WorkerRuleSingleHomeTests` treat `cleanupcode` as optional. Out of scope; flagged in the digest.

## Design

1. **`--contains <text>`** (`scripts/report-gate.sh`, option parsing and `complete_size`). Exactly one of `--ends-with` and `--contains`. In `--contains` mode `complete_size` prints the file size when `grep -F -q -- "<text>" "<file>"` succeeds, and nothing otherwise.
2. **Settle rule per mode** (main loop). `--ends-with`: unchanged. `--contains`: the loop records the first second it saw the text, and counts the run as done once `--grace` seconds have passed since then and the command is still running; stop the tree, standard error says the command did not exit, exit 0.
3. **`--timeout <seconds>`** (option parsing, main loop). Positive integer, validated like `--grace` (else exit 2). Elapsed time is measured with bash's `SECONDS`. Each tick checks "done" first, then the limit (D14). When the limit is reached before the run is done: stop the tree with `kill_tree`, print `report-gate: the command did not finish within <n> seconds; stopping it` to standard error, exit 124.
4. **Usage line** names every option: `usage: report-gate.sh --report <path> (--ends-with <text> | --contains <text>) [--grace <seconds>] [--timeout <seconds>] -- <cmd...>`.
5. **Unchanged:** the report file is removed before the command starts; a command that exits 0 without the finish signal exits 1; a command's own non-zero exit code passes through; 143 on `INT`/`TERM`. The header comment documents both modes, `--timeout`, and exit 124.
6. **Host command** (`docs/standards/csharp/README.md`, `## Completion`, an optional step after the two `dotnet format` lines, which restore the solution — D11), stated as optional and as a workaround for the tool not exiting on this machine:

   ```bash
   mkdir -p .agent_scratchpad/<task>
   scripts/report-gate.sh --report "$PWD/.agent_scratchpad/<task>/cleanupcode.log" --contains "Close solution instance" --grace 5 --timeout 300 -- dotnet jb cleanupcode KyberWeave.sln --profile=KyberWeave --include="<changed files>" --no-build "--LogFile=$PWD/.agent_scratchpad/<task>/cleanupcode.log" --LogLevel=VERBOSE
   ```

   The finish text, log option and log level are as T3 confirms them (D7, D15).
7. **Portable skill** (`products/kyber-squad/skills/resharper-clt/SKILL.md`): `--no-build` on the `cleanupcode` line of the fix-pass block and of the worked example, and the "host's C# standard's `## Completion` command wins" sentence (D5).

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | `scripts/report-gate.sh` contract: the two finish modes, the grace rule per mode, `--timeout` and exit 124 | ReviewCouncil | architecture | `docs/code-review/architecture.md`, section "Repository gate scoping and caching in this repository", the paragraph that starts "Its argv starts with `scripts/report-gate.sh`" |
| H2 | The `cleanupcode` fix pass in this repository runs `--no-build` through the wrapper keyed on its log line, why, and that the C# standard's `## Completion` section holds the command (the command itself is written there by T4) | ReviewCouncil | architecture | `docs/code-review/architecture.md`, same section, one sentence after H1 |

## Test contract (test-first)

All commands run from the worktree root. Build once per task, then run the classes with `--no-build`. Every run must report a non-zero test count.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T2 | `tests/KyberWeave.RepoPolicy.Tests/ReportGateScriptTests.cs` (class `KyberWeave.Tests.ReportGateScriptTests`, new cases) | `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release` then `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.ReportGateScriptTests"` | (a) `--contains` with a stand-in that writes the finish text mid-file, keeps appending a line every 0.2 s, and never exits: exit 0, standard error contains `did not exit`, the recorded pid is not running. (b) `--contains` with a stand-in that exits 0 having written a file without the text: exit 1, standard error contains `no complete report`. (c) `--contains` with a hang and no text, `--timeout 2`: exit 124, standard error contains `did not finish within`, the recorded pid is not running, total run under 30 s. (d) `--ends-with` with an incomplete report and a hang, `--timeout 2`: exit 124. (e) `--contains`, finish text written then hang, `--grace 1 --timeout 20`: exit 0. (f) `--ends-with` and `--contains` together: exit 2, and standard error's usage line contains both `--contains` and `--timeout`. (g) `--timeout 0` and `--timeout x`: exit 2, same usage-line assertion. Every existing case still passes unchanged. | All of (a) to (g) fail: today the script rejects `--contains` and `--timeout` as usage errors, so (a) to (e) see exit 2 where 0, 1 or 124 is asserted, and (f) and (g) fail on the usage line, which does not yet name `--contains` or `--timeout`. Failure output in the digest. | Same commands pass with assertions unchanged; `bash -n scripts/report-gate.sh` exits 0. |
| T3 | No test: measurement task | The probe commands in T3 | `--no-build` is accepted; the `--LogFile` file contains the finish line exactly once, at or after the file's save; the negative probe's behaviour is recorded | n/a (no-test task, replaced by recorded measurements) | Measurements reported in the digest; no `cleanupcode` process started by the task remains; the scratch file is restored |
| T4 | No test: documentation and product-text task, plus one real run | The four test commands listed under T4 (`WorkerRuleSingleHomeTests`, `SquadCanonicalContentTests`, `StandardsCompletionSectionTests`, each run on its own), then the real run | The C# standard's command, run exactly as written on a deliberately misformatted C# file, returns with exit 0 within 300 s and the file is fixed | n/a (no-test task, replaced by the pinned classes and the real run) | Every test run passes with a non-zero count; the real run's exit code, duration, the file's diff before restore, and an empty leftover-process check are in the digest; `git status` shows the scratch file restored |
| T5 | No test: documentation task | `kyber-weave docs validate .` and `kyber-weave docs drift .` before the archive move, then `kyber-weave docs validate . --merge-ready` after it, run in the worktree | H1 and H2 written; plan archived with its closeout mapping; task file removed | n/a (no-test task, replaced by the docs gates) | Zero errors on every run; the only warning allowed is `KW-CONFIG-002` on the `concurrent` key, which the installed CLI raises because it predates pull request 594 |

## Implementation tasks

Every developer task ends with the worker's own `task-gate` run. Standards to fetch: `docs_get_coding_standard_for_technology('test')` for `test-dev`, `('csharp')` for `csharp-dev`. Every test, build and tool command runs through `scripts/proc-guard.sh run --` from the worktree root, except the wrapped `cleanupcode` command in T4, which the wrapper itself bounds. No worker runs the full test suite. No worker kills a process it did not start: a `cleanupcode` process that predates this run (one has been stuck for over 8 hours) is the owner's to stop.

### T1 (test-dev): failing contract tests for `--contains` and `--timeout`

**Session:** `red-script`. **Depends on:** none. **Required skills:** `test-dev`.

**Files:**
- `tests/KyberWeave.RepoPolicy.Tests/ReportGateScriptTests.cs` (class `KyberWeave.Tests.ReportGateScriptTests`: new `[Fact]` cases (a) to (g) of the T2 Test-contract row; a new private helper beside `RunScript` that passes `--contains`, `--grace` and optional `--timeout`, routed through the existing `RunScriptArguments`)

**Work.** Reuse `RunScriptArguments`, `ReadRecordedPid`, `IsRunning` and `SkipIfBashUnavailable`. Do not add a `ProcessRunner.Run`, `Process.Start` or `new ProcessStartInfo` line: the spawn allowlist must stay unchanged. Stand-ins are `sh -c` strings inside the temporary directory; a looping writer is `while true; do echo tick >> log; sleep 0.2; done`. Keep every case bounded by the existing 30-second `ScriptTimeout`.

**Acceptance:** the T2 row of the Test contract, RED column; the existing cases still pass; failure output pasted into the digest.

**Commands:**
  - `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.ReportGateScriptTests"`

### T2 (github-devops): `--contains` and `--timeout` in the wrapper

**Session:** `green-script`. **Depends on:** T1. **Required skills:** `github-devops`.

**Files:**
- `scripts/report-gate.sh` (header comment: usage, both modes, exit 124; function `usage`; the option-parsing `while`/`case`; validation after it; function `complete_size`; the main `while kill -0` loop)

**Work.** Design items 1 to 5. Bash 3.2 compatible (D12). Keep the base branch's `rm -f -- "${report}"` and its refusal to start (exit 2) when `pgrep` is not on the PATH; the header's exit-code list keeps that meaning for 2 and adds 124. Keep the `--ends-with` path's behaviour identical, so the `inspectcode` gate in `.kyber-weave/kyber-weave.yml` needs no change.

**Acceptance:** T1's tests pass with assertions unchanged; every pre-existing `ReportGateScriptTests` case passes; `bash -n scripts/report-gate.sh` exits 0; no test leaves a process behind. Your task gate's `shellcheck` gate cannot run here (`shellcheck` is not installed): report it as skipped for a missing tool, never as passed or failed.

**Commands:**
  - `bash -n scripts/report-gate.sh`
  - `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.ReportGateScriptTests"`

### T3 (csharp-dev): measure `--no-build` with `--LogFile`

**Session:** `probe`. **Depends on:** none. **Required skills:** `resharper-clt`.

**Files:** none committed. Scratch edit to `src/KyberWeave.Core/Configuration/ReviewConfig.cs` (record `ReviewGate`, property `FailOnOutput`), restored before the task ends. Logs under `.agent_scratchpad/T3/` (gitignored).

**Work.** Run in your own worktree (this task runs beside T1 and T2, which build the same projects). Run `mkdir -p .agent_scratchpad/T3`, `dotnet tool restore` and `dotnet restore KyberWeave.sln` first. Misformat the `FailOnOutput` line with whitespace only, so it still compiles (for example `public   bool FailOnOutput {get;init;}`). Then:

1. Positive probe:
   `scripts/proc-guard.sh run --timeout 120 -- dotnet jb cleanupcode KyberWeave.sln --profile=KyberWeave --include="src/KyberWeave.Core/Configuration/ReviewConfig.cs" --no-build "--LogFile=$PWD/.agent_scratchpad/T3/positive.log" --LogLevel=VERBOSE`
   Exit 124 is expected (the tool hangs and proc-guard stops it). Record: whether `--no-build` was accepted; whether the file was fixed (`git diff`) and its modification time (`stat -f %Sm -t %H:%M:%S <file>`); every line of `positive.log` that contains `Close solution instance`, with its timestamp, logger and level; the seconds from start to the first such line; and the last ten lines of the log. More than one occurrence, or one timestamped before the file's save, is the D15 fallback.
2. Restore the file with `git checkout -- src/KyberWeave.Core/Configuration/ReviewConfig.cs`.
3. Negative probe, file unmodified:
   `scripts/proc-guard.sh run --timeout 120 -- dotnet jb cleanupcode KyberWeave.sln --profile="No Such Profile" --include="src/KyberWeave.Core/Configuration/ReviewConfig.cs" --no-build "--LogFile=$PWD/.agent_scratchpad/T3/negative.log" --LogLevel=VERBOSE`
   Record its exit code and whether `negative.log` contains `Close solution instance`.
4. Leftover check by this task's own paths only: `pgrep -f "agent_scratchpad/T3[/]"` prints nothing (the bracket keeps `pgrep` from matching its own shell). Never use a broad `pgrep`/`pkill` on `cleanupcode` or `jb`.

**Acceptance:** the four measurements are in the digest with the log excerpts; `git status` shows no change to tracked files; the leftover check is empty. If `positive.log` lacks the finish line, or D15's fallback applies, report that as the result: do not switch to `--LogFolder` or another text yourself.

**Commands:** as listed in Work, steps 1 to 4, plus `git status --short`.

### T4 (docs-dev): host command, portable skill wording, and the real run

**Session:** `docs`. **Depends on:** T2, T3. **Required skills:** `docs-dev`, `resharper-clt`.

**Files:**
- `docs/standards/csharp/README.md` (section `## Completion`: after the code block and before the bullets, one optional step giving Design item 6's command, the restore precondition (D11), that the pass stays optional and off by default, and that the wrapper exists because the tool does not exit on this machine and can be removed when it does. Keep the section the last in the file and keep its build, test and format lines, which `StandardsCompletionSectionTests` pins. Update `last-reviewed` in the frontmatter.)
- `products/kyber-squad/skills/resharper-clt/SKILL.md` (section "Running the fix pass before you claim work is complete", step 2: `--no-build` on the `cleanupcode` line and one sentence that, when the host's C# coding standard gives its own `cleanupcode` command in its `## Completion` section, that command replaces this line; the worked-example block (the one that cleans the Hauling solution): `--no-build` on the `cleanupcode` line. Nothing else in the skill changes.)

**Work.** Use the finish text and log option T3 confirmed (from `{prior_results}`). If T3 reported that `--LogFile` lacks the finish line, or that D15's fallback applies, stop and report `BLOCKED: D7 fallback` without editing. Then the real run, on this machine:

1. Misformat `src/KyberWeave.Core/Configuration/ReviewConfig.cs` (record `ReviewGate`, property `FailOnOutput`) with whitespace only, as T3 did; run `dotnet restore KyberWeave.sln`.
2. Run the standard's commands exactly as written (the `mkdir -p` line and the wrapped command), with `<task>` = `T4` and `<changed files>` = `src/KyberWeave.Core/Configuration/ReviewConfig.cs`. Record exit code and wall time.
3. `git diff src/KyberWeave.Core/Configuration/ReviewConfig.cs` shows no diff against HEAD (the line is back to `public bool FailOnOutput { get; init; }`); if the `KyberWeave` profile changed anything else in the file, paste that diff.
4. `pgrep -f "agent_scratchpad/T4[/]"` prints nothing.
5. `git checkout -- src/KyberWeave.Core/Configuration/ReviewConfig.cs`; `git status --short` shows only the two files above.

**Acceptance:** the T4 row of the Test contract; `git diff` touches only the two files above; no file under `products/kyber-squad/agents/` or `products/kyber-squad/skills/code-review/` changes; `kyber-weave docs validate .` reports zero errors.

**Commands:**
  - `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.WorkerRuleSingleHomeTests"`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.SquadCanonicalContentTests"`
  - `scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  - `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.StandardsCompletionSectionTests"`
  - `kyber-weave docs validate .`
  - the real-run steps 1 to 5 above

### T5 (docs-dev): closeout

**Session:** `docs`. **Depends on:** T2, T4. **Required skills:** `docs-dev`, `kyber-docs-authoring`.

**Files:**
- `docs/code-review/architecture.md` (section "Repository gate scoping and caching in this repository": H1 and H2)
- `docs/plans/2026-11-11-cleanupcode-exit-hang.md` (completion summary and closeout mapping, then moved to the archive)
- `docs/archive/plans/2026-11-11-cleanupcode-exit-hang.md` (archived plan)
- `docs/archive/plans/README.md` (archive row at the top)
- `docs/plans/README.md` (Active Plans back to its empty state)
- `docs/plans/2026-11-11-cleanupcode-exit-hang.tasks.json` (removed with `git rm`)

**Acceptance:** the T5 row of the Test contract. ADR disposition: `no ADR — ADR waiver: a removable workaround inside an existing wrapper, no new boundary or dependency`.

**Commands:**
  - `kyber-weave docs validate .` and `kyber-weave docs drift .` before the archive move
  - `kyber-weave docs validate . --merge-ready` and `kyber-weave docs drift .` after it

## Dependency graph

```text
T1 ──> T2 ──┐
            ├──> T4 ──> T5
T3 ─────────┘
```

T1 and T3 start at once; T3 runs in its own worktree because T1 and T2 build the same projects. T4 consumes T2's script and T3's measurements. T4 and T5 share the `docs` session.

## Risks

- **R1. `--LogFile` does not carry the finish line.** Measured only with `--LogFolder`. T3 checks it before any wording; the fallback returns the plan to Draft (D7).
- **R2. A finish line after a failed cleanup reads as success.** The tool's exit code is lost when it is stopped. T3's negative probe measures it; the task gate's format verify forms and the council's `inspectcode` still judge the code (D10).
- **R3. `--no-build` is undocumented.** It works with the pinned 2026.2.1 tool; a later version that drops it fails loudly (D6).
- **R4. A VERBOSE log is large.** The wrapper searches the whole file once a second. Bounded by the 300-second limit; logs live in the gitignored scratchpad.
- **R5. The workaround outlives the bug.** When the OS, .NET or JetBrains side is fixed, the tool exits by itself within the grace period and the wrapper passes through its exit code, so the command keeps working; removing it is a one-step edit to the C# standard's `## Completion` section plus the script options.
- **R6. Early or repeated finish line.** If the line can appear before the save, the wrapper would stop the tool with nothing fixed and exit 0. T3 measures it, and the D15 fallback returns the plan to Draft.

## Out of scope

- `products/kyber-squad/agents/**` and `products/kyber-squad/skills/code-review/**` (another session owns them).
- `products/kyber-squad/skills/task-gate/SKILL.md` (it already defers to `resharper-clt`).
- The root `AGENTS.md` (D4: the command's home is the C# standard).
- The `inspectcode` gate in `.kyber-weave/kyber-weave.yml` (its argv is unchanged).
- Turning the `cleanupcode` pass on by default, or adding it to `review.task-gates`.
- The `resharper-clt` skill's "MUST run before `READY_FOR_REVIEW`" wording, which disagrees with the optional pass in `task-gate`; flagged for a separate change.
- Stopping the `cleanupcode` process already stuck on this machine (the owner's to stop).
- An upstream report to JetBrains or Microsoft.

## Verification and review

- Each developer task runs its own commands above and its `task-gate`.
- The real run in T4 is the end-to-end proof on this machine.
- The end-of-run review council runs under the `code-review` skill over the whole branch; CI runs the full suites on the draft pull request.
- The pull request body lists the low-confidence rows D2, D6, D7 and D9.
