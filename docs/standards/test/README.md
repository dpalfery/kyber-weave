---
id: standards/test
title: "Test coding standard"
doc-type: coding-standard
status: current
technology: test
owner: dpalfery
last-reviewed: 2026-10-31
decided-by:
  - adr/0047-kyberdash-self-spawns-through-one-guarded-chokepoint
---

# Test coding standard

How tests are written in this repository. Agents and skills resolve this document as
`<test-coding-standard>` in the repository root `AGENTS.md`, so what it says here outranks
the defaults a portable agent shipped with. Language-level C# in test files follows
`<csharp-coding-standard>`.

This is not a summary of xUnit. It records the decisions this repository has made and
would otherwise have to re-argue in review.

## Stack

**xUnit**, one project covering all three artifact classes. The suite is fast — the whole
run stays well under a second — and it should stay that way.

Assertions are xUnit's. This repository does not take NSubstitute or FluentAssertions.

## Real files, and fakes that earn their keep

The loader and validator read the file system, so fixtures build a real directory tree in
a temp folder and dispose it. Never write into the repository tree from a test.

`FakeCodeGraphResolver` implements `ICodeGraphResolver` deterministically, so drift, join,
and retrieval are testable without `sqlite3` installed. That is the default.

`CodeGraphFixtureDb` builds a real sqlite database by shelling out to `sqlite3`. It exists
for one job — proving the adapter reads a real index the same way the fake claims to. Do
not reach for it to test behaviour a fake can express; it makes the suite depend on a tool
the machine may not have.

## A test name is an assertion

`AFreshlyScaffoldedCorpusPassesDocsValidate`, not `TestScaffold2`, and not
`MethodName_StateUnderTest_ExpectedBehavior`. The name states the guarantee.

When the reason is not obvious, put the failure it prevents in a `<summary>`. Several
tests here exist because a specific regression happened; that context is what stops
someone deleting them later.

## Test the decision, not the implementation

The tests that have earned their keep pin behaviour someone would otherwise "simplify"
back into a defect — that `--force` does not reach operator state, that an unclosed marker
is reported rather than overwritten.

Prefer the property over the mechanism. `DocsScaffolderTests` asserts that a freshly
scaffolded corpus **passes `DocSpecValidator`** rather than that three specific files were
written — the second breaks on every template edit, the first breaks only when the
guarantee breaks.

New behaviour ships with the test that would fail without it. A bug fix ships with a
regression test whose name encodes the scenario that was broken.

## Isolation and assertions

Tests run in any order and do not depend on each other. Each test arranges its own data.

- **No test that only asserts it does not throw.** Assert the observable outcome.
- **No `Thread.Sleep` or arbitrary delays.**
- **Do not add getter/setter-only tests to pad coverage.**

Failure messages should identify the offender. Where a report is asserted empty, join the
findings into the assertion message so a failure names the rule and document rather than
just `False != True`.

## Concurrency and zero-flakiness isolation

Tests run concurrently across classes. Never introduce hidden cross-test coupling, process-wide mutation, or unisolated state:

- **No process-wide environment variable mutation**: Never call `Environment.SetEnvironmentVariable` in test code. An xUnit collection setting `DisableParallelization = true` only serializes tests inside that collection; tests in other collections continue running concurrently and observe mutated process state. Inject delegate resolvers (`Func<string, string?>? getEnvironmentVariable = null`, defaulting to `Environment.GetEnvironmentVariable`) or pass `SquadRenderRequest.EnvironmentReader`.
- **Lock-free `AsyncLocal` execution context scopes**: CLI console redirection and durability overrides isolate per async context rather than using process-wide locks or mutable statics. `ProcessConsoleCapture` uses `AsyncLocalTextWriter` and `AsyncLocalAnsiConsole` so parallel CLI test classes never contend on a static lock or bleed console output. `SquadTransaction.UseSyncToDiskScope(bool)` uses an `AsyncLocal<bool?>` scope routing store flushes through `SquadTransaction.SyncToDisk` so durability tests never leak macOS APFS `F_FULLFSYNC` barriers onto concurrent test threads.
- **Serial collections policy**: xUnit `DisableParallelization = true` (e.g. `ScaleAcceptanceCollection`) is reserved strictly for benchmarks monitoring thread allocations (`GC.GetAllocatedBytesForCurrentThread()`) or hard execution-time ceilings across high-volume iterations (such as 10,000 DocGraph claims). Never use collection serialization to mask shared static mutable state.
- **Parallel repository-wide scans**: Whole-repository assertion sweeps (such as `UpstreamSeveranceTests`) partition tracked file sets across all available cores using `Parallel.ForEach` with thread-safe collectors (`ConcurrentBag<string>`) to avoid multi-second single-threaded disk I/O bottlenecks.

## Release sources and immutable corpus fixtures

Tests avoid redundant disk extraction, file copying, and YAML/Markdown parsing through focused test fixtures:

- **Synthetic release sources for plumbing and lifecycle**: Tests verifying deployment state machines, receipts, locks, and CLI exit codes use `SyntheticValidSquadReleaseSource` (~15 small files) rather than copying the full 203-file corpus on every run. Reserve `CorpusSquadReleaseSource` for end-to-end installation contracts and canonical prose checks.
- **Immutable test corpus caching**: Read-only contract tests asserting renderer templates and model presets (such as `SquadModelPresetRenderTests`, `CopilotRendererTests`, `AntigravityRendererContractTests`, and `SquadRustGoSpecialistTests`) consume lazy, thread-safe instances via `TestSquadCorpus.CanonicalSource` and cached unpreset baseline renders, avoiding hundreds of redundant disk reads and YAML/Markdown parsing across test theories.
- **Git repository templates**: Tests requiring git repositories (such as Review Council gate command tests) use `TestGitRepositoryTemplate.CreateRepositoryCopy` to copy a pre-seeded, committed repository directory (<2ms, 0 child processes) instead of spawning repeated `git init`/`add`/`commit` OS child processes per fixture.

## Internals are in scope

`InternalsVisibleTo` exposes `KyberWeave.Core` internals here, so helpers like link
normalization, vocabulary parsing, and docs-root detection are tested directly. Marking a
helper `internal` is not a reason to test it only through a command.

## Documented behaviour

Ranking weights, thresholds, and rule severities are described in the governed corpus. A
test that pins one of those numbers is pinning a documented claim — if you change it,
change the document in the same commit. `RetrievalRegressionTests` in particular guards
whether real questions find real answers; treat a failure there as a product regression,
not a test to adjust.

## Fixtures

| Helper | For |
|---|---|
| `TempDirectory` | A disposable scratch directory. Implement `IDisposable` on the test class and dispose it. |
| `DocFixture` | A documentation corpus on disk — `.WithCatalog()`, `.WithSourceRoot()`, `.Write()`, `.Load()` |
| `FakeCodeGraphResolver` | `.WithSymbols(...)` for deterministic symbol and route resolution |
| `CodeGraphFixtureDb` | A real sqlite index, for adapter parity only |
| `KyberWeaveTestPaths` | Locates the repository root by walking up to `KyberWeave.sln` |
| `ProcessConsoleCapture` | Lock-free, `AsyncLocal` console capture isolating stdout, stderr, and Spectre console width per async context |
| `TestSquadCorpus` | Thread-safe, lazy-cached immutable canonical `SquadSource` and target baseline renders |
| `SyntheticValidSquadReleaseSource` | Lightweight ~15-file valid Squad source tree for state, lock, receipt, and lifecycle tests |
| `TestGitRepositoryTemplate` | Pre-initialized git repository template directory copied without spawning OS child processes |

## Tests that start processes

A test that starts a process can start more of them. In 2026-10 a dashboard test did exactly
that and fork-bombed `node`. The rules below keep every process a test starts bounded, and the
two static audits (below) keep the list of such sites honest.

1. **Inject a recording spawner by default.** Every KyberDash self-spawn takes a `spawnFn`
   (`createNodeSpawner(spawnFn)`, the `DetachedLauncher` constructor). A recording function starts
   nothing, so the test asserts the argv and environment it would have used.
2. **Under test the guard is deny.** `dash/tests/setup/spawn-guard-setup.ts` sets
   `KYBERDASH_SPAWN_GUARD=deny` for every vitest file. A spawn that gets through anyway is recorded,
   and the test that caused it fails with the hit list. Playwright gets the same setting from
   `dash/playwright.isolated.config.ts`.
3. **A real child needs `allowRealSpawns(reason)`.** Call it in the test, from
   `dash/tests/setup/spawn-guard.ts`. The reason names the bound (for example, "self-exits after
   30 s" or "fixed argv, one line, exits"). The opt-in ends after the test. Add the site to the
   allow-list with the same bound.
4. **Kill the whole group, not only the wrapper.** A Playwright boot is spawned `detached: true` and
   stopped with `process.kill(-pid, signal)`. Killing only `npx` or `sh` leaves the node process
   under it running.
5. **Bound every wait.** Give each readiness wait a timeout, and each `spawnSync`, `execSync` and
   `execFileSync` a `timeout` with `killSignal: 'SIGKILL'`. A child that exits by itself is better
   than one the test must kill.
6. **Run it through the watchdog.** `scripts/proc-guard.sh run -- <command>` from the repository
   root, as the [runbook](../../dash/runbook.md#running-tests-safely) describes. The watchdog is
   POSIX-only; on Windows the process ledger still checks the children the run recorded, by pid,
   but there is no absolute process cap. The guard gives the command stdin from `/dev/null` when
   its own stdin is a terminal, so a command that reads the terminal does not stop; `--tty` keeps
   the terminal and is risky. From an agent tool keep `--timeout` at or below 540 seconds, because
   a longer foreground command is moved to the background.
7. **Never start the dashboard from a test.** Use the e2e boot seams in `dash/e2e/boot-dashboard.ts`.

**The allow-list (dash).** `dash/tests/spawn-sites.allowlist.ts` lists every `child_process`
spawn, exec and fork call, every `new Worker`, and every Playwright `webServer` under `dash/`. Each
entry gives the file, the API name, the `bound` and a `note`. The `bound` says how the site is
limited: a timeout, a tree kill, a single owned child closed on exit, an `afterEach` kill, an
injected fake, or a bounded external tool. A site with no limit is written with "NO TIMEOUT" in its
bound and listed as a follow-up. That wording records a residual; it is not a bound.
`dash/tests/spawn-sites.test.ts` fails on any site with no entry, on any entry that matches no site,
and on any empty bound. An entry covers one file and one API name, so
review each new site against the bound it inherits.

**The allow-list (.NET).** `ProcessSpawnSitesTests` scans two roots: `tests/KyberWeave.Tests`
(C# and shell) and `src/` (C#). It matches `Process.Start`, `new Process`, `new ProcessStartInfo`,
`ProcessRunner.Run` and `BuildStartInfo`. `tests/KyberWeave.Tests/Fixtures/spawn-sites-allowlist.json`
keys each entry by root-prefixed file (`tests/...` or `src/...`), normalized line text and count,
with a `bound`. The test fails on any unlisted start, with a root-prefixed file and line, and on any
stale entry. Use the `ProcessRunner` test helper, which kills the child tree at its timeout. A raw
`Process.Start` needs its own timeout and a `Kill(entireProcessTree: true)` in a `finally`.

**Production .NET starts carry bounds too.** The shared process executor defaults to 60 s, and the
KyberDash plugin verb to 120 s. `docs init`'s `apm install` is 5 min. The self-updater's capture verbs
are 120 s and its tray menubar update is 5 min; both kill the tree on expiry. The best-effort `xattr`
call is 30 s. The CodeGraph sqlite read is 60 s. The analysis cache's `git ls-files` is 60 s, the
analysis sqlite batch 120 s, and its version probe 30 s. **One deliberate exception:**
`ProcessRunner.RunInteractive`, which runs `squad run` attended by a user at the console, has no
timeout by design. Do not give it one; a timeout would end a live session.

**Adding a spawn site.** Write the bound first. Add the entry to the allow-list for its language, with
the file, the API name or line text, and the bound. If the site cannot state a bound, it does not
belong in a test: remove it or inject a fake.

## Commands

Run every command through the watchdog, from the repository root:

```bash
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
```

### Fast Assembly Runner and Iteration Workflow

During task execution and active development, do not invoke the full `dotnet test` suite repeatedly. MSBuild project evaluation and incremental dependency checks introduce multi-second overhead per invocation. Instead, use the direct xUnit assembly runner with class filtering and the quiet reporter:

```bash
dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class "<namespace>.<Class>" -reporter quiet
```

When the build used `--artifacts-path <dir>` with `-c Release`, the direct runner reads `<dir>/bin/KyberWeave.Tests/release/KyberWeave.Tests.dll` (the configuration is lowercase there) instead of `tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll`; in your own isolated worktree without that flag the `bin/Release` command above is correct.

- **Build once, test many (`--no-build`)**: Compile the test project once before iteration. Subsequent targeted test executions against non-code or single-class edits run directly via the compiled assembly runner or pass `--no-build` to bypass redundant MSBuild restore and rebuild evaluation.
- **Reserve the full suite for the final council**: Running the complete test suite across all classes is reserved for the review council's `test` gate, which runs it once per run, on the integrated head. At task end, run only the test classes the task touched: the classes the task packet's runner command names, plus any other test class that references a symbol the task changed. If no class covers the change, run none and report `TESTS: total=0` with the reason, because zero tests run is not zero failures. If the change reaches shared test infrastructure (fixtures, base classes and shared helpers, meaning anything referenced by more than one test class, which `codegraph_explore` on the type shows; and test-project build settings, which every test class depends on) and its classes cannot be named, ask the conductor for one full run instead of starting your own; the conductor decides and dispatches at most one full run for the whole run, shared by every worker. Never run the full test suite on inner iteration loops.
- **Zero-stash RED proofs**: When proving a test fails before implementation (RED proof in TDD), never use `git stash` to toggle between dirty working tree states. `git stash` creates uncommitted stash commits that conflict with parallel task execution and risk race conditions across agents. Instead, run RED proofs against a clean base checkout, throwaway working tree copy, or isolated `git worktree`.
