---
id: plans/2026-11-08-self-update-finishes-in-the-new-binary
title: Finish a self-update in the new binary, not in the one that was just replaced
doc-type: plan
status: current
component: Distribution
owner: dpalfery
created: 2026-11-08
last-reviewed: 2026-11-08
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [Distribution, KyberArbiter]
---

# Finish a self-update in the new binary, not in the one that was just replaced

**Status: Ready.** Approved and ready to execute (decision `AP1`). Worker prompts are in the sibling
`2026-11-08-self-update-finishes-in-the-new-binary.tasks.json`; this Markdown plan stays the authority.
**Development mode:** `test-first` (packet input).
**Autonomy:** `autonomous` (packet input; every open item is decided and recorded below).
**Scale:** `extends-component`, components `Distribution` and `KyberArbiter`.

**Bottom line.** `kyber-weave update` replaces its own program file, then keeps running in the old
process. The old process can no longer load any piece of itself it had not already loaded, so the
first step after the swap that needs one (reading a YAML file) crashes, the arbiter hooks are not
re-rendered, and the command exits 1 although every binary was installed. The fix is structural, not
another pre-load: once the new binaries are in place, the old process starts the **new** `kyber-weave`
to run everything that comes after (the KyberDash steps and the arbiter hook refresh) and only relays
its result. Reading the code also showed the old process would have re-rendered the hooks from the
**old** release's templates even if it had not crashed, so the new process is the right place for
that step on its own merits.

## Problem and goal

**What the owner sees.** `kyber-weave update` finishes with the lines below and exit code 1, although
all binaries were installed (rc.21 on the owner's machine):

```text
factory plugin: unchanged /Users/dave/.kyberdash/marketplaces/kyberdash
factory consent: kept /Users/dave/.kyberdash/factory-plugin.json
warning: arbiter hooks could not be checked because the project registry could not be read: Could not load file or assembly 'YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e'. The system cannot find the file specified.

kyber-weave: error: Could not load file or assembly 'YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e'. The system cannot find the file specified.
```

**Goal.**

1. After a real self-update, the arbiter hooks are re-rendered by the new release, the KyberDash steps
   run, and the command exits 0. No step after the swap runs in the process whose program file was
   replaced.
2. Any failure after the binaries are installed says so: "installed, but this step did not finish",
   with the real cause and the command that retries it. The label "project registry could not be read"
   appears only when the registry could not be read.
3. A test that fails on a real single-file swap with global targets and a registered project in the
   home directory, and passes after the fix. The CI job `update-loop` runs it on Linux and macOS.
4. The documentation states the new rule: the old process runs nothing but the hand-off after the
   swap. The "never shells out" sentence is replaced by what is still true.

## Investigation findings

Discovery: codegraph 2, docs 8 (plus Context7 2 for Spectre.Console.Cli), direct reads 28. Reasons for
the direct reads: the two `codegraph_explore` calls elide bodies and the budget of two calls is spent,
so `SelfUpdater.cs` (3 reads), `ArbiterHookRefresher.cs` (2), `UpdateCommand.cs`, `SelfUpdateHost.cs`,
`UpdateSettings.cs` and `UserHostConfig.cs` were read for the parts it did not return; the rest are
not code-graph material (tests, shell scripts, the CI workflow, governed docs, the catalog, the
config registry, three reads of an archived plan for the house format, one of the open parent plan).

**Gaps.**

- **G1 — `origin/main` not read directly.** This run has no shell, so `git show origin/main:<path>`
  could not be run. The checked-out tree is a docs branch that merged `main` at `9876ef7b`. Every
  `origin/main` citation in the investigator's report matches this tree exactly
  (`src/KyberWeave.Cli/Update/SelfUpdater.cs` lines 128, 156, 230-234, 311-312, 421, 476), so line
  numbers below are reliable for orientation. T1 starts with a premise check on the fix branch.
- **G2 — Windows and the owner's real home not exercised.** The investigator reproduced on macOS with
  a fake home. Windows is not in the `update-loop` matrix. Both stay `[NOT LIVE-VERIFIED]`.
- **G3 — No subagent dispatch.** None was needed; nothing external drove a decision.

**Evidence the plan rests on** (paths relative to the repository root, all in the checked-out tree).

| # | Claim | Evidence |
|---|---|---|
| E1 | `Run` turns an `IOException` (which includes a failed assembly load) into exit 1 carrying the raw exception text; `UpdateCommand` prints it as `kyber-weave: error: ...` | `src/KyberWeave.Cli/Update/SelfUpdater.cs:128-148`; `src/KyberWeave.Cli/Commands/Update/UpdateCommand.cs:56-65` |
| E2 | The running image is committed last, but then `ReconcileKyberDash` and `RefreshArbiterHooks` run in the same process; the "already on" path has no swap | `SelfUpdater.cs:230-239` (order and the comment that overwriting the image costs the runtime the ability to load anything it has not touched), `:257-261` (commits), `:311-312` (post-commit calls), `:188-189` (already-on calls) |
| E3 | `RefreshArbiterHooks` reads the registry (`:432`), then calls `UserHostConfig.ReadGlobalTargets(home)` outside any `try` (`:439-440`), then calls the refresher inside a `try` (`:445-460`) | `SelfUpdater.cs:421-469` |
| E4 | The registry collector catches `IOException` and always labels it "the project registry could not be read" | `SelfUpdater.cs:476-504` (label at `:500-501`) |
| E5 | `ReadGlobalTargets` parses with YamlDotNet and its catch filter names `YamlException` first. Evaluating that filter needs YamlDotNet itself, so a failed load inside the filter is swallowed and the original exception escapes. This is inferred from the code, consistent with the reported "uncaught" behaviour; nothing here relies on it | `src/KyberWeave.Core/Configuration/UserHostConfig.cs:77-109` (filter at `:105`) |
| E6 | The refresh renders hook text from templates compiled into the running CLI: `ArbiterPluginShim.Render`, `PiRenderer.RenderArbiterExtension`, `CopilotRenderer.RenderCliHooksFile`, `ArbiterHookWiring.HookCommandLine`. The interface's own remarks say the hook source is embedded in the CLI. So an in-process refresh by the old process renders the **old** release's hook source. Found by reading; not reproduced | `src/KyberWeave.Cli/Update/ArbiterHookRefresher.cs:275-339`; `src/KyberWeave.Cli/Update/IArbiterHookRefresher.cs:11-15` |
| E7 | The KyberDash steps also touch assemblies for the first time after the swap (`ProcessRunner`, `System.Text.Json` in `ReadRecordedTrayVersion`, `KyberDashGuidance`). They work today only because those happen to be loaded or pre-warmed | `SelfUpdater.cs:391-413`, `:557-619`, `:627-643` |
| E8 | The earlier incident was patched by pre-warming one assembly on macOS only (`System.Diagnostics.Process` in `ClearMacQuarantine`); the repro for this defect named a different one (`System.Text.Encoding.Extensions`), which is the whack-a-mole this plan ends | `src/KyberWeave.Cli/Update/BinaryInstaller.cs:98-130`; investigator report |
| E9 | `RunProcess` starts a child with inherited console streams and a 5-minute wait that kills the tree on expiry; it is the model for the hand-off | `SelfUpdater.cs:747-775` |
| E10 | Every process start in `src/` is listed with its bound in an allow-list that a repo-policy test enforces | `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json` (`SelfUpdater.cs` entries); `tests/KyberWeave.RepoPolicy.Tests/ProcessSpawnSitesTests.cs` |
| E11 | The CI job runs `./scripts/update-loop.sh` on `ubuntu-latest` and `macos-latest`. The loop's self-update runs under an empty sandbox home, so no YAML is read after the swap and it passes today. Its per-case helpers are `begin_case`, `update_case`, `expect_exit_zero`, `expect_logged`, `expect_reports` | `scripts/update-loop.sh:336-401`, `:437-563`; `.github/workflows/ci.yml:451-495` |
| E12 | Existing unit tests drive release-changed updates in-process with fakes: `SelfUpdaterArbiterRefreshTests` (6 updater constructions; asserts `refresher.Calls == 1`) and `UpdateCommandTests` (57 `SelfUpdateOptions` constructions; helpers `RunWithTray`, `RunReconciling`, `RunRecording`, `Run`). Their assertions are the regression net for the reconcile behaviour | `tests/KyberWeave.Tests/SelfUpdaterArbiterRefreshTests.cs`; `tests/KyberWeave.Tests/UpdateCommandTests.cs:868,988,1584,1713` |
| E13 | `CommandOptionAttribute.IsHidden` hides an option from help text; the repository has no hidden option yet | Spectre.Console.Cli 0.57.2 (`src/KyberWeave.Cli/KyberWeave.Cli.csproj:26`), Context7 `/spectreconsole/spectre.console.cli` |
| E14 | Documents that state the current update behaviour or the "in-process / never shells out" idea | `docs/cli-command-surface.md:83-105` and `:471`; `docs/install.md:458-523`; `docs/distribution.md:496-552` (the swap sentence at `:543-545`); `docs/adr/0053-kyber-arbiter-three-step-decision-gates.md:112`; `docs/standards/test/README.md` ("Production .NET starts carry bounds too"); `src/KyberWeave.Cli/Update/IArbiterHookRefresher.cs` and `SelfUpdater.cs` doc comments; `tests/KyberWeave.Tests/SelfUpdaterArbiterRefreshTests.cs` (class and test comments) |
| E15 | The investigator's reproduction: a scratch copy of the installed rc.21 binary, a fake home with `arbiter.global.targets: [factory]`, a loopback fake release whose `kyber-weave` was a different single-file bundle: both binaries installed, the warning, the error, exit 1. Control: the same binary on the "already on" path loads YamlDotNet and exits 0; `doctor --check` passes | packet |

**Root cause in one line.** A single-file program finds its bundled pieces by re-opening its own file
by path; after the swap that path is the new file, so a piece the old process had not yet loaded cannot
be found. It is not a packaging problem and not a problem with the owner's machine.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Which approach ends the whole class? | **Re-exec.** After the last binary is committed, the old process starts the new installed `kyber-weave` with a hidden option and lets it run everything that used to follow the swap in the old process (the KyberDash reconcile and the arbiter hook refresh). The old process only relays the exit code and prints the final line. | Rejected, each for a stated reason. (a) **Move the post-swap work before the swap:** the refresh would then render the old release's hook source (E6), a failing KyberDash step would leave the CLI un-swapped while its message says "the binaries are already updated", and it breaks the swap-last rollback design in `SelfUpdater.cs:230-239`. (b) **Pre-load every referenced assembly before the swap:** generic, but it leaves the stale-template defect (E6), still depends on a runtime mechanism (resources, reflection-loaded types) nobody can enumerate, and is the same bet as the earlier pre-warm. (c) **Non-fatal with an honest message only:** the hooks stay stale on exactly the update that needs them; kept as the safety net (D6, D7), not as the fix. (d) **Let the new binary perform the swap itself:** moves commit and rollback into a new mode and still needs a spawn from the old process; larger, and the same hand-off problem. Cost of being wrong: an internal redesign of one class, reversible in one pull request; the E2E case (T4) and the unit tests (T2) are the net. | autonomous: low-confidence, no authoritative source (choice between architectures; named cost above) |
| D2 | What moves into the new process? | Both `ReconcileKyberDash` and `RefreshArbiterHooks`, not only the hook refresh. | The KyberDash steps first-touch assemblies after the swap too (E7) and work only by luck. Moving one and not the other keeps a second copy of the same hazard. The "already on" path is unchanged: it already runs in a process that was never replaced. | ANSWERED: autonomous, source E2, E7 |
| D3 | What is the hand-off contract? | The old process runs `<install dir>/kyber-weave[.exe] update <version> --finish-update`, adding `--no-kyberdash`, `--no-menubar`, `--no-arbiter`, `--no-restart` when the owner passed them (never `--no-mcp` or `--release-candidate`). Console streams are inherited; the wait is bounded at 15 minutes (more than the child's own bounds add up to: 120 s engine, 5 min tray, two 120 s plugin steps) and the tree is killed on expiry. `--finish-update` is a **hidden** option (`IsHidden = true`). In that mode the process downloads and commits nothing, refuses unless its own version equals the requested one (so a child that is still the old image can never run the steps), treats the release as changed (so the tray step behaves exactly as it does on a real update), prints no outcome line on success, and never starts another hand-off. The parent prints the one final line `updated kyber-weave <version> → <path>`; on a non-zero child exit it returns that exit code with the message from D6. | A flag, not an environment variable: a test can assert it from the argv, and it shows in a process listing when someone debugs a stuck update. Hidden because only the updater passes it; it is still written down in `cli-command-surface.md` so an agent reading the surface is not surprised. A child older than this option (a pinned older release) rejects the unknown option: the parent then says the binaries are installed and gives `kyber-weave update <version>` as the retry, which works on both old and new binaries (R4). Cost of being wrong: a hidden, additive option, removable in a later release because only the updater passes it. | autonomous: low-confidence, no authoritative source (a public interface; named cost above) |
| D4 | How is the hand-off itself protected from the same failure? | **Rehearse it once, before the first commit.** The old process runs the exact hand-off code path against its own still-valid image with `--version`, output discarded, result ignored, exceptions swallowed. The path is one private method shared by the rehearsal and the real hand-off; the rehearsal differs only in discarding the child's output. | The hand-off needs `System.Diagnostics.Process` and whatever it pulls in (the repro failed on a facade, E8). Executing the real path once before the swap loads exactly what the real call will need on every platform, which the macOS-only pre-warm did not, and which no CI leg proves for Windows. This is a rehearsal of one function, not a list of assembly names. Rejected: running the **staged new** binary instead (a noexec temp directory or a first-run scan would turn a harmless problem into a refused update), and no rehearsal (leaves Windows protected by hope). | autonomous: low-confidence, no authoritative source (cheap to reverse; local to T5) |
| D5 | Does the old process hand off only when there is something to do? | No: always, on a real update. | The skip lines ("kyberdash is not installed ...", "--no-kyberdash given; leaving ...") are printed by the steps themselves and the update loop asserts some of them; a conditional hand-off would silently drop them. The cost is one extra process start (well under a second) for an owner with no KyberDash. | ANSWERED: autonomous, source `scripts/update-loop.sh:513`, `:522`, `:530`, `:562-563` (asserted skip lines), E12 |
| D6 | What do the failure messages say? | One shape for every step after the binaries are installed: `warning: <step> did not finish: <cause>. The binaries are already updated; run kyber-weave update <version> to retry.` The cause is exactly one of: "the project registry could not be read: <message>" (only when reading the registry failed); "a program component could not be loaded (<assembly name>): <message>" (for `FileLoadException`, `BadImageFormatException`, and a `FileNotFoundException` whose `FileName` is an assembly identity rather than a path); otherwise the exception message. The whole refresh step, including `ReadGlobalTargets`, sits under one handler, so a refresh problem is a warning with exit 0 and never an uncaught exception. A failed KyberDash engine or tray step stays what it is today: a named failure, exit 1, "binaries already updated". | `kyber-weave update <version>` (pinned) is the right retry: on the installed binary it takes the "already on" path, which the investigator's control showed works. The unpinned form could install a newer release. The csharp standard says to catch what you can name and never swallow; the classification is by type, not by message text, so it does not depend on the machine's language. | ANSWERED: autonomous, source `docs/standards/csharp/README.md` (Errors and diagnostics), E3, E4, E5 |
| D7 | What if something else throws after the commit? | Any `IOException`, `UnauthorizedAccessException`, `Win32Exception`, `InvalidOperationException` or `TimeoutException` raised between the self-image commit and the end of the run is reported as `kyber-weave <version> is installed, but the last steps did not finish: <cause>. Run kyber-weave update <version> to finish them.` with exit 1. | Today the same exceptions surface as a bare `kyber-weave: error: <raw text>`, which tells the owner nothing was installed. This is the backstop for whatever the hand-off cannot foresee. | ANSWERED: autonomous, source E1 |
| D8 | How does test-first work when the tests need members that do not exist? | T1 adds only inert declarations (the hidden option, a field on the options record, and an optional constructor seam) with no behaviour, so the RED tests compile and fail at run time. T2 writes the RED unit tests and migrates every existing in-process update test to a loopback that plays the child, with those tests' assertions unchanged. T3 and T4 write the E2E RED. | A compile error is a weak RED. The inert surface is a no-test task with a read-only check: it builds and every existing targeted suite stays green. | ANSWERED: autonomous, source `products/kyber-squad/agents/architect/references/test-first-contract.md` |
| D9 | What must the E2E case prove? | On a real single-file swap, with global arbiter targets and a registered project in the home directory: exit 0; none of "Could not load file or assembly", "kyber-weave: error:" or "could not be checked because the project registry" in the output; a deliberately stale global hook file is rewritten to the current command; the CLI reports the new version. A precondition guard fails the case if the "from" and "to" `kyber-weave` files are identical, so the swap cannot be vacuous. State is created through the product's own verbs under the case home, never hand-written. | This is the path the investigator's control and the current loop both miss (E11). RED is required on the unfixed tree before any fix is written. | ANSWERED: autonomous, source E11, E15 |
| D10 | What happens to the "never shells out" contract? | Replaced by: the hook refresh never runs a hook script or the arbiter binary, and it runs in-process **inside the new `kyber-weave`**; the old process's only act after the swap is to start that new process. The unit test `Update_ArbiterRefreshSeamIsInjectedNeverShelledOut` keeps its assertion; its comment is rewritten. | The sentence in `IArbiterHookRefresher`, `SelfUpdater` and the test comment was true only because the refresh ran in the old process. | ANSWERED: autonomous, source E6, E14 |
| D11 | New ADR, amendment, or waiver? | A new, short ADR (next free number after the ADR index at write time; 0061 today): "The self-updater hands everything after the swap to the new binary." | It constrains future work (any new post-update step belongs in the finishing process), it had real alternatives (D1), and it replaces an unrecorded rule. No existing ADR records the self-updater's ordering, so there is nothing to amend. | autonomous: low-confidence, no authoritative source (an ADR number can move; T6 reads the index first) |
| D12 | Which documents change? | See the Design harvest table: `docs/distribution.md`, `docs/install.md`, `docs/cli-command-surface.md`, `docs/kyber-arbiter/runbook.md`, `docs/standards/test/README.md`, the new ADR and its index row, plus the doc comments in code. | Found by the E14 sweep. `docs/adr/0053-...:112` states only that update re-renders hooks, which stays true. | ANSWERED: autonomous, source E14 |
| D13 | Branch, delivery and naming. | The conductor starts the run with `kyber-weave run start` from the fetched `origin/main` (never from the checked-out docs branch). This plan file and its index row travel to the fix branch as its first commit (T0). One draft pull request; nothing is merged, tagged or published. The branch and the title carry the harness name per ADR 0060. | The checked-out branch is unrelated to the code this fixes. | ANSWERED: autonomous, source `docs/adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name.md`, packet |
| D14 | Development mode and autonomy. | `test-first`, `autonomous`. | Packet values; autonomy also matches the Config Reg **<autonomy>**. | ANSWERED: packet 2026-11-08 |
| D15 | What does the owner do now? | Run `kyber-weave update` once more. It takes the "already on" path in a fresh process, which the investigator's control showed loads YAML and exits 0, and re-renders the hooks. After the fix ships, the **first** update away from rc.21 is still run by rc.21's own updater and will show this error once more; every update after that is run by the fixed updater. | A self-updater is always executed by the old binary (`docs/distribution.md:496-499`). | ANSWERED: autonomous, source E15, `docs/distribution.md` |
| AP1 | Approve the Test contract and execute. | Recommended, at the declared scale: `extends-component`, components `Distribution` and `KyberArbiter`. | Decision-complete, mode-complete and scale-declared, with `docs_validate` and `docs_drift` reporting nothing about this plan. The conductor returned `AUTONOMOUS_APPROVAL: approve-and-execute` for this gate on 2026-11-08 under resolved autonomy `autonomous`, so execution may start. Approval is never inferred from the packet's autonomy alone. | autonomous: recommendation accepted |

## Fix design

**Sequence today and after.**

```text
today : stage -> commit non-self binaries -> commit self image -> [same process] KyberDash steps -> hook refresh -> print
after : stage -> rehearse hand-off (old image, --version, quiet) -> commit non-self -> commit self image
        -> start <installed kyber-weave> update <version> --finish-update [opt-outs]   (inherit console, 15 min bound)
        -> relay its exit code -> print one final line
child : (--finish-update) version guard -> KyberDash steps (release changed) -> hook refresh -> exit
```

**The "already on" path** is unchanged: it runs both steps in a process that was never replaced.

**Hook refresh in the finishing process.** The body of the refresh (registry collection, the global
targets read, the refresher call) sits under one handler that classifies its failure per D6 and never
lets an exception escape as exit 1. The registry collector's catch no longer captures a component-load
failure under the registry label.

**Output.** Exactly one bold outcome line from the whole command: the parent's. The child prints its
log lines but no outcome line on success; on failure it prints its named failure once and the parent
adds the single retry sentence.

**Do not change** — each is deliberate:

- the swap-last order and the rollback in `SelfUpdater.cs:230-295`, and `BinaryInstaller.Replace`;
- the "already on" path and its log lines;
- any existing assertion in `UpdateCommandTests` and `SelfUpdaterArbiterRefreshTests`: they move to a
  loopback, they are not edited;
- the arbiter binary, the hook templates, the registry format;
- `--no-*` flag meanings, the exit-code table, and the visible `--help` text of `update`.

## Test contract

Mode `test-first`. Every RED is observed and quoted in the task digest before its GREEN starts, in
the fix worktree, with no `git stash`. Weakening an assertion to reach green is a scope change.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | none — no test task | `git status --short` | The plan and its index row are the fix branch's first commit | n/a — mechanical verification replaces it | Branch holds exactly the two plan files; checked-out docs branch untouched |
| T1 | none — no test task (inert declarations) | CMD-BUILD, CMD-UPDATE | The solution still builds, no behaviour changes, every existing targeted suite is green; the premise check holds | n/a — read-only verification replaces it: quote the three premise answers and the green summary with a non-zero test count | Build green, CMD-UPDATE green with a non-zero total, `git diff` shows only declarations |
| T2 | `tests/KyberWeave.Tests`: new `SelfUpdaterPostUpdateHandoffTests.cs`; additions to `UpdateCommandTests.cs`; loopback helper in `tests/KyberWeave.Tests/Fakes/`; migrated constructions in `UpdateCommandTests.cs` and `SelfUpdaterArbiterRefreshTests.cs` | CMD-BUILD, CMD-UPDATE | (a) a release-changed update starts the installed CLI once, after every commit, with `update <version> --finish-update` plus only the owner's opt-outs, and runs no KyberDash verb and no refresher call itself; (b) the hand-off is preceded by one quiet rehearsal against the old image before the first commit; (c) a non-zero child exit gives that exit code, a message saying the binaries are installed, and the pinned retry command; (d) a hand-off that cannot start, including a failed assembly load, gives the same honest message and never the raw text or the registry label; (e) the "already on" path still runs both steps itself and starts nothing; (f) `--finish-update` runs the KyberDash steps as a changed release and the refresh in-process, downloads nothing, starts nothing, prints no outcome line; (g) it refuses when its own version is not the requested one, and without a version; (h) a refresher that throws an assembly-load failure gives a warning with exit 0 naming the component and the retry, never the registry label; (i) a corrupt registry gives the registry label (the one true case); (j) `UpdateCommand` prints exactly one bold outcome line on a successful hand-off and one error line on a failed one | Quote every failure verbatim. (a)-(d), (j) fail on the unfixed code because it never starts the CLI and calls the refresher itself; (e) and (i) pass already and are recorded as passing; (f), (g) and (h) fail because the inert option runs the full update path, (g) has no guard, and the refresh failure is worded differently. The migrated existing tests must be green before and after the migration | CMD-UPDATE green with a non-zero total, new assertions unchanged, every migrated test's assertions byte-identical to before |
| T3 | `tests/KyberWeave.RepoPolicy.Tests/ReleaseTests.cs`: new `UpdateLoopRunsTheArbiterRefreshAfterARealSwap` | CMD-POLICY | `scripts/update-loop.sh` contains the case `arbiter-hooks-after-swap`, seeds a global arbiter target and a registered project through product verbs, plants a stale hook (`/legacy/kyber-weave-arbiter`), asserts the output has no `Could not load file or assembly`, and guards that the from and to CLI files differ | The test fails because the script has no such case; quote the failure | Test passes after T4 with the assertions unchanged |
| T4 | `scripts/update-loop.sh` (new case `arbiter-hooks-after-swap`); `.github/workflows/ci.yml` only if the path filter needs it | CMD-LOOP | The real-swap case from decision D9 | Run CMD-LOOP on the unfixed tree: the new case must FAIL with exit 1 and `Could not load file or assembly` (and the retry warning) in the quoted output, on this machine's OS. If the stand-in swap does not reproduce, apply the fallback in [Premise and fixture](#premise-and-fixture) before continuing; do not proceed on a vacuous pass | CMD-POLICY (T3) green; CMD-LOOP is run again at T5 and the new case passes together with every existing case |
| T5 | Implementation, not tests | CMD-BUILD, CMD-UPDATE, CMD-POLICY, CMD-LOOP | The behaviour in [Fix design](#fix-design); the process-start allow-list matches the code | RED already carried by T2 and T4 | CMD-UPDATE and CMD-POLICY green with non-zero totals, assertions unchanged; CMD-LOOP: the new case and every existing case pass |
| T6 | Governed docs | CMD-DOCS | The documents state the new rule and match the code | Not a separate RED — T2 and T4's failures are the evidence | `docs validate` and `docs drift` zero findings |
| T7 | Whole-change review | the `code-review` skill | No finding unaddressed | Not a test task — read-only verification | Verdict recorded with the gate counts |
| T8 | Closeout | CMD-DOCS with `--merge-ready` | Plan archived, index row moved | Not a test task — read-only verification | Zero findings after the archive |

### Premise and fixture

**Premise check (T1, before any other task edits code).** On the fix branch, confirm and quote:

1. `RunCore` still calls `ReconcileKyberDash` and `RefreshArbiterHooks` after the commit loop, in the
   updating process;
2. `RefreshArbiterHooks` still calls `UserHostConfig.ReadGlobalTargets` outside a `try`;
3. `ArbiterHookRefresher` still renders hook text from templates compiled into the CLI.

If (1) or (2) fails, the defect may already be fixed upstream: stop and report. If only (3) fails,
continue; D1 still stands on the crash alone.

**Fixture notes (T3, T4).** The swap only fails when the new file's layout differs from the old, because
the old process looks its pieces up by their old position. The loop builds "from" and "to" from the
same tree at different versions, whose version strings differ, so the layouts should differ. This is
the one assumption this plan could not check. If T4's RED run passes on the unfixed tree: first confirm
the precondition guard compares the two `kyber-weave` files byte for byte; then make the "to" bundle
differ in layout deterministically (for example a longer version string or one extra embedded file in
the "to" publish only) and say so in the case's comment; then repeat the RED. Do not move on with a
passing RED.

The case should build its state with the product, under `CASE_HOME`, using the **new** binaries
(`TO_BIN`), then copy the old CLI into `CASE_BIN` and run the update: `arbiter install --target cursor`
(global, `--provider ""`), then rewrite the hook command in the installed global hook file to
`/legacy/kyber-weave-arbiter hook --harness cursor --scope global`; a project in a throwaway git
directory registered through the product's project-scope install, with `arbiter.enabled: true` in its
`.kyber-weave/kyber-weave.yml` written after the install. Run the update with `--no-kyberdash
--no-menubar` so the case needs no KyberDash build. If the sandbox cannot do one of these steps, the
implementer records which and uses the closest product verb; hand-written registry files are not
allowed.

### Commands

Run from the fix worktree root. Build once per task, then re-run with `--no-build`; check each summary
reports a non-zero total, because a mistyped filter matches nothing and exits 8.

```bash
# CMD-BUILD
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release

# CMD-UPDATE
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~SelfUpdaterPostUpdateHandoffTests|FullyQualifiedName~SelfUpdaterArbiterRefreshTests|FullyQualifiedName~UpdateCommandTests|FullyQualifiedName~ArbiterHookRefresherRenameNoticeTests|FullyQualifiedName~ArbiterKiloReviewRemediationTests"

# CMD-POLICY
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release \
  --filter "FullyQualifiedName~ProcessSpawnSitesTests|FullyQualifiedName~UpdateLoop"

# CMD-LOOP  (publishes six single-file builds: run it in the background and poll the log;
#            never --reuse between a RED and a GREEN run, it would reuse the stale release)
scripts/proc-guard.sh run --timeout 1800 -- ./scripts/update-loop.sh --no-kyberdash

# CMD-DOCS
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate .
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .
```

No dev agent runs the full suite. The end-of-run council runs its own gates; this plan does not carry
that command.

## Tasks

Every developer task ends with the worker's own `task-gate` run, and a failing gate goes straight back
to that worker. No file appears in two tasks unless a dependency orders them.

### T0: Put the plan on the fix branch

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`. **Depends on:** none.
- **Entry files:** `docs/plans/2026-11-08-self-update-finishes-in-the-new-binary.md`, its sibling
  `docs/plans/2026-11-08-self-update-finishes-in-the-new-binary.tasks.json`, and this plan's row in
  `docs/plans/README.md`.
- **Acceptance:** the conductor has started the run from the fetched origin/main (D13). Copy the
  three files to the fix branch, commit them as its first commit, and leave the checked-out docs branch
  clean. Report both `git status --short` outputs. Re-run `docs validate` and `docs drift` on the fix
  branch and report both results: on the docs branch this plan was authored on they report two
  findings about a different plan (the 2026-11-07 conductor-bug-crusher-run-speed plan, which
  exists only on that branch and is not linked from the index there), none about this one, so a clean
  pass is expected on a branch cut from origin/main. Do not copy that other plan.

  The task file leaves the architect with an empty `execution-digest`, because the architect has no way
  to run code. T0 fills it before the commit: the digest is what `PlanDocumentParser.ExecutionDigest`
  returns for the plan as copied, computed with a throwaway file-based C# app kept outside the
  repository (the recipe is in the task file's T0 prompt). If the app cannot be built or run, T0 stops
  and reports the error; a digest is never guessed or reused. After that `docs validate` reports
  nothing about this plan's task file.

### T1: Premise check and inert surface

- **Delegate:** `csharp-dev`. **Skills:** `csharp-dev`, **<csharp-coding-standard>**, `task-gate`.
  **Depends on:** T0.
- **Entry files:** `src/KyberWeave.Cli/Commands/Update/UpdateSettings.cs`,
  `src/KyberWeave.Cli/Update/SelfUpdateHost.cs`, `src/KyberWeave.Cli/Commands/Update/UpdateCommand.cs`,
  `src/KyberWeave.Cli/Update/SelfUpdater.cs` (constructor only).
- **Acceptance:** run the [premise check](#premise-and-fixture) and quote it. Then add declarations
  only: a hidden `--finish-update` option (`IsHidden = true`) on the settings, a `FinishUpdate` field
  with default `false` on `SelfUpdateOptions`, passed through by `UpdateCommand`, and an optional
  constructor seam on `SelfUpdater` for the finishing process (default left unused). No behaviour
  change, no new process start, no comment about behaviour that does not exist yet. CMD-BUILD and
  CMD-UPDATE green with a non-zero total.

### T2: RED unit tests and the loopback migration

- **Delegate:** `test-dev`. **Skills:** `test-dev`, **<test-coding-standard>**. **Depends on:** T1.
- **Entry files:** new `tests/KyberWeave.Tests/SelfUpdaterPostUpdateHandoffTests.cs`; a loopback helper
  under `tests/KyberWeave.Tests/Fakes/`; `tests/KyberWeave.Tests/UpdateCommandTests.cs`;
  `tests/KyberWeave.Tests/SelfUpdaterArbiterRefreshTests.cs`.
- **Acceptance:** first migrate: every existing construction that completes a release-changed update
  (the 15 or so sites in the two files, found by reading, not by line) passes a loopback that plays the
  child by re-entering the same updater with `FinishUpdate` set and the host's version equal to the new
  release. With the seam unused those tests stay green; show it, then keep their assertions
  byte-identical after the fix. Then write the contract in the Test-contract row (a)-(j) against the
  intended behaviour. Do not edit any file under src/.
- **Commands:** CMD-BUILD, CMD-UPDATE.

### T3: RED for the update-loop case's existence

- **Delegate:** `test-dev`. **Skills:** `test-dev`, **<test-coding-standard>**. **Depends on:** T0.
- **Entry files:** `tests/KyberWeave.RepoPolicy.Tests/ReleaseTests.cs` beside
  `UpdateLoopAssertsArbiterVersion`.
- **Acceptance:** `UpdateLoopRunsTheArbiterRefreshAfterARealSwap` pins the case name
  `arbiter-hooks-after-swap`, the stale-hook marker "/legacy/kyber-weave-arbiter", the negative
  assertion on `Could not load file or assembly`, and the from/to file guard. Quote the failure.
  Do not edit the update-loop script itself; T4 owns it.
- **Commands:** CMD-POLICY.

### T4: The real-swap case and its RED

- **Delegate:** `github-devops`. **Skills:** `github-devops`, **<github-actions-coding-standard>**.
  **Depends on:** T3.
- **Entry files:** `scripts/update-loop.sh`; `.github/workflows/ci.yml` only if the `changes` filter
  does not already send a change to `scripts/update-loop.sh` to the `update-loop` job.
- **Acceptance:** add the case as specified in D9 and [Premise and fixture](#premise-and-fixture),
  using the loop's own helpers, and update the script's header comment and `--help` range. Run CMD-LOOP
  on the unfixed tree and quote the new case failing with the original error; apply the fixture
  fallback if it passes. Report whether the CI workflow needed a change and why. T3's test is green.
- **Commands:** CMD-LOOP (background), CMD-POLICY.

### T5: GREEN — hand off, finish, and say the truth

- **Delegate:** `csharp-dev`. **Skills:** `csharp-dev`, **<csharp-coding-standard>**, `task-gate`.
  **Depends on:** T2, T4.
- **Entry files:** `src/KyberWeave.Cli/Update/SelfUpdater.cs`,
  `src/KyberWeave.Cli/Commands/Update/UpdateCommand.cs`,
  `src/KyberWeave.Cli/Update/IArbiterHookRefresher.cs` (doc comment),
  `src/KyberWeave.Cli/Update/ArbiterHookRefresher.cs` (only if a message or comment needs it),
  `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json`.
- **Acceptance:** [Fix design](#fix-design) and decisions D2-D7 and D10: rehearsal, hand-off through
  one shared private start method with its bounds, finish mode with its guards, the single handler and
  the classification in D6, the post-commit backstop in D7, one outcome line from `UpdateCommand`. The
  process-start allow-list matches the code and states each bound. CMD-UPDATE and CMD-POLICY green with
  non-zero totals and no assertion changed; CMD-LOOP passes every case including the new one. No
  `NoWarn` entry, no analyzer suppression, no catch of bare `Exception`. Doc comments describe what is
  now true.
- **Commands:** CMD-BUILD, CMD-UPDATE, CMD-POLICY, CMD-LOOP.

### T6: Documentation harvest

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`, `architecture-decision-record`,
  `app-docs-standard`. **Depends on:** T5.
- **Entry files:** `docs/distribution.md`, `docs/install.md`, `docs/cli-command-surface.md`,
  `docs/kyber-arbiter/runbook.md`, `docs/standards/test/README.md`, the ADR index
  `docs/adr/README.md`, and the new ADR under `docs/adr/`; the [Design harvest](#design-harvest) table
  says what each destination receives. Also a corpus search for any other statement that the
  post-swap steps run in the updating process or "never shell out".
- **Acceptance:** each harvest row lands and says what the row says, quoting the final messages from
  T5. The new ADR takes the next free number after reading the index on the fix branch. The
  `Production .NET starts carry bounds too` paragraph names the 15-minute hand-off bound and the quiet
  rehearsal. `docs validate` and `docs drift` zero findings.
- **Commands:** CMD-DOCS.

### T7: Whole-change review

- **Delegate:** `code-review`. **Skills:** `code-review`. **Depends on:** T5, T6.
- **Acceptance:** one review over the whole diff by the council with the `code-review` skill. A
  finding that changes behaviour returns to T5 with a new test first. Record the verdict and gate
  counts.

### T8: Closeout

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`. **Depends on:** T7.
- **Entry files:** `docs/plans/2026-11-08-self-update-finishes-in-the-new-binary.md`, its sibling
  `docs/plans/2026-11-08-self-update-finishes-in-the-new-binary.tasks.json`, `docs/plans/README.md`,
  `docs/archive/plans/`, `docs/todo/` and `docs/todo/README.md`.
- **Acceptance:** `git mv` the plan into the archive plans directory, move its index row to
  **Archived Plans** with the harvest destinations and evidence, record the closeout mapping, set
  frontmatter `status: archived` and the body status to match, leave **Active Plans** empty, file the
  candidate todos under Out of scope as items in the todo directory, remove the sibling task file with
  `git rm` in the same commit (a task file left behind fails `--merge-ready`), then CMD-DOCS with
  `--merge-ready`.

## Dependency graph

```text
T0 ─► T1 ─► T2 ─────────────┐
T0 ─► T3 ─► T4 ─────────────┼─► T5 ─► T6 ─► T7 ─► T8
```

First ready set: T0, then T1 and T3 together. T2 and T4 then run in parallel. File scopes do not
overlap: T1 and T5 share `SelfUpdater.cs`, `UpdateCommand.cs`, `UpdateSettings.cs` and
`SelfUpdateHost.cs` and are ordered through T2; T2 and T3 are different test projects; T4 owns the
script alone.

## Risks

| Risk | Mitigation |
|---|---|
| R1. The same-tree stand-in swap does not reproduce, so the E2E is vacuous. | T4's RED on the unfixed tree is mandatory, with a byte-compare guard and a stated fallback; a passing RED stops the task. |
| R2. Moving the in-process update tests to a loopback weakens them. | T2 migrates before any fix and keeps assertions byte-identical; the migrated tests are green before and after. |
| R3. The hand-off or rehearsal cannot start in the field (a first-run scan, a locked file). | The rehearsal runs the old image, never the staged one, and swallows its failure; the real hand-off failing gives the honest message and the retry (D6, D7). |
| R4. A pinned older release rejects `--finish-update`. | The parent reports the binaries as installed and gives `kyber-weave update <version>`, which both old and new binaries run as "already on". |
| R5. Output is doubled or contradicts itself. | T2 (j) pins one outcome line and one error line through `UpdateCommand`. |
| R6. A hung child. | 15-minute bound, tree kill, an honest timeout message. |
| R7. The process-start allow-list test fails. | T5 owns the JSON; CMD-POLICY is in its acceptance. |
| R8. Windows is not covered by CI. | The rehearsal runs the real path before the swap on every platform; the live Windows check stays `[NOT LIVE-VERIFIED]`. |
| R9. A premise is wrong (gap G1). | T1's premise check with an explicit stop rule. |
| R10. The ADR number moved. | T6 reads the index on the fix branch before numbering. |
| R11. The owner reads "fixed" and still sees the error on the next update. | D15 and the pull request body say the first update away from rc.21 is still run by the old updater. |

## Out of scope

- The swap-last order, rollback, and `BinaryInstaller.Replace`.
- A Windows leg for the `update-loop` job.
- Candidate todos for closeout to file, not fixed here: `UserHostConfig.ReadGlobalTargets` turns a
  YAML problem into "no targets" without a word, and its catch filter names a YamlDotNet type first;
  `ArbiterHookRefresher.ResolveGlobalTimeout` has a bare `catch`; a failed KyberDash engine step ends
  the run before the hook refresh runs.
- KyberDash, tray and capture-plugin behaviour itself.
- Repairing an already installed rc.21 updater (impossible; see D15).
- Publishing a release, cutting a tag, or merging.

## Verification gates

CMD-UPDATE and CMD-POLICY at T5, CMD-LOOP at T4 (RED) and T5 (GREEN), CMD-DOCS at T6, the council
review at T7, and CMD-DOCS with `--merge-ready` after the archive at T8.

`[NOT LIVE-VERIFIED]` until someone runs them: (1) Windows, the whole update; (2) the owner's real
home on macOS, where the first update away from rc.21 still shows the error and the second `kyber-weave
update` is clean; (3) the first update *by* a fixed binary, which is the only true end-to-end proof of
the fix on the owner's machine.

## Review and closeout

- One draft pull request from the fix branch, titled per ADR 0060 with the harness name, body
  carrying the reproduction, the before and after, and the upgrade note from D15.
- One whole-change council review (T7).
- `docs-dev` closeout (T8), including the harvest mapping.

## Design harvest

| ID | Target component | Target doc type | Durable item | Destination |
|---|---|---|---|---|
| H1 | Distribution | adr | After the swap the updating process runs nothing but the hand-off; every later step runs in the new binary; the alternatives rejected (D1) | new ADR under `docs/adr/` and its row in `docs/adr/README.md` |
| H2 | Distribution | architecture | The update sequence (stage, rehearse, commit, hand off, finish), the finishing mode's guards, the one-outcome-line rule, and the real-swap case `arbiter-hooks-after-swap` in the local loop with what it proves | `docs/distribution.md` (the self-updater and local-loop sections, including the sentence at `:543-545`) |
| H3 | Distribution | reference | The hidden `--finish-update` option, who passes it, its refusal rules; the failure wording for steps after the install and the pinned retry command; `kyber-weave update` after an error | `docs/cli-command-surface.md` (Update section and the `update` row) and `docs/install.md` (Updating) |
| H4 | KyberArbiter | runbook | The update-time refresh runs in the new release and so renders the new release's hook source; how its failure reads | `docs/kyber-arbiter/runbook.md` (Hooks) |
| H5 | Distribution | standard | The hand-off's 15-minute bound and the quiet rehearsal as production process starts | `docs/standards/test/README.md` ("Production .NET starts carry bounds too") |
