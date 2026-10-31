---
id: standards/test
title: "Test coding standard"
doc-type: coding-standard
status: current
technology: test
owner: dpalfery
last-reviewed: 2026-10-31
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

## Commands

```bash
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
```

### Fast Assembly Runner and Iteration Workflow

During task execution and active development, do not invoke the full `dotnet test` suite repeatedly. MSBuild project evaluation and incremental dependency checks introduce multi-second overhead per invocation. Instead, use the direct xUnit assembly runner with class filtering and the quiet reporter:

```bash
dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class "<namespace>.<Class>" -reporter quiet
```

When the build used `--artifacts-path <dir>`, the direct runner reads `<dir>/bin/KyberWeave.Tests/release/KyberWeave.Tests.dll`, not `bin/Release`; in your own isolated worktree without that flag the `bin/Release` command above is correct.

- **Build once, test many (`--no-build`)**: Compile the test project once before iteration. Subsequent targeted test executions against non-code or single-class edits run directly via the compiled assembly runner or pass `--no-build` to bypass redundant MSBuild restore and rebuild evaluation.
- **Reserve full suite for task end / final council**: Running the complete test suite across all classes is reserved for final task completion or pre-push review council gates. Never run the full test suite on inner iteration loops.
- **Zero-stash RED proofs**: When proving a test fails before implementation (RED proof in TDD), never use `git stash` to toggle between dirty working tree states. `git stash` creates uncommitted stash commits that conflict with parallel task execution and risk race conditions across agents. Instead, run RED proofs against a clean base checkout, throwaway working tree copy, or isolated `git worktree`.
