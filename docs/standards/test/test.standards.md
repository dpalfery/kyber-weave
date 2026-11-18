---
id: standards/test
title: "Test coding standard"
doc-type: coding-standard
status: current
technology: test
owner: dpalfery
last-reviewed: 2026-11-17
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

## Naming and layout

### Stack

**xUnit**, split into these projects:

- One unit test project per product project that holds logic, named `<Project>.Tests`:
  `KyberWeave.Application.Tests`, `Hooks.Tests`, `Kernel.Tests`, `Mcp.Tests`, `Arbiter.Tests`,
  `Cli.Tests` and `Data.Tests`. A test sits in the project of the class it tests, runs in
  memory, and uses the helpers in `tests/KyberWeave.Testing`. `Domain` holds data only and
  `Contracts` holds interfaces, so neither has one. The full run of every unit project, plus the
  content, policy and architecture projects (`KyberWeave.Unit.slnf` lists them), must finish in 120
  seconds or less
  ([the budget](../../../AGENTS.md#the-net-unit-suite-completes-in-120-seconds-or-less)), and
  the speed rules below are how a test keeps it there.
- `tests/KyberWeave.Integration.Tests` — the safety net: a test that performs a real Squad
  deployment, starts a real program, or needs a file system beyond its own scratch folder.
  It runs in CI and the review council, not in the unit time budget.
- `tests/KyberWeave.Testing` — the helpers both test assemblies use (the temp root, the
  durability switch, prepared installs, release fakes, console capture, path helpers and
  shared fixtures). It holds no tests.

The classes in the content and policy projects keep the `KyberWeave.Tests` namespace root and the
integration classes keep `KyberWeave.Integration.Tests`, so a fully-qualified class name names one
test in one project.

Assertions are xUnit's. This repository does not take NSubstitute or FluentAssertions.

### Which project a test belongs in

One rule, and it is checkable by reading the test:

- **`tests/KyberWeave.Integration.Tests`** holds a test that performs a real Squad deployment, starts
  a real program (git, the CLI, `dotnet`, `sqlite3`, node, bun), or needs a real file system beyond
  its own small scratch folder.
- **The unit project of the code under test** holds everything else. The unit projects' full run is the
  120-second suite, so a real deployment or a real child program in one is a defect, not a slow test to
  tolerate.

**New behaviour gets an in-memory unit test written against an interface.** If the behaviour depends
on the disk, the clock or another program, the seam is an interface the test replaces: a file-system
or executor abstraction, a fake release source, a fake renderer. The unit test proves the decision
through that seam, and the integration suite proves the real implementation once, end to end.

**Code that can only be tested through the real disk or a real program is a design gap to raise, not
a reason to add a slow test to the unit project.** Introduce the interface first (or widen an existing
one), write the unit test against it, and keep the real-disk or real-program test in the integration
project. A new test in the unit project that reaches the real disk or spawns a process will be moved
at review.

The integration project is the safety net: CI runs it on every pull request in its own required job.
Developer agents, task gates and the review council's gates run unit and content projects only, so
there is no integration gate in `.kyber-weave/kyber-weave.yml` and `scripts/fast-loop.sh --touched`
only reports that an integration class was touched.

### A test name is an assertion

`AFreshlyScaffoldedCorpusPassesDocsValidate`, not `TestScaffold2`, and not
`MethodName_StateUnderTest_ExpectedBehavior`. The name states the guarantee.

When the reason is not obvious, put the failure it prevents in a `<summary>`. Several
tests here exist because a specific regression happened; that context is what stops
someone deleting them later.

## Coding standards

### Real files, and fakes that earn their keep

The loader and validator read the file system, so fixtures build a real directory tree in
a temp folder and dispose it. Never write into the repository tree from a test.

`FakeCodeGraphResolver` implements `ICodeGraphResolver` deterministically, so drift, join,
and retrieval are testable without `sqlite3` installed. That is the default.

`CodeGraphFixtureDb` builds a real sqlite database by shelling out to `sqlite3`. It exists
for one job — proving the adapter reads a real index the same way the fake claims to. Do
not reach for it to test behaviour a fake can express; it makes the suite depend on a tool
the machine may not have.

### Test the decision, not the implementation

Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.

A test of such code has one case for every column after Source in the input-site table that the correctness lens of the code-review skill defines, and asserts the refusal.

The tests that have earned their keep pin behaviour someone would otherwise "simplify"
back into a defect — that `--force` does not reach operator state, that an unclosed marker
is reported rather than overwritten.

Prefer the property over the mechanism. `DocsScaffolderTests` asserts that a freshly
scaffolded corpus **passes `DocSpecValidator`** rather than that three specific files were
written — the second breaks on every template edit, the first breaks only when the
guarantee breaks.

New behaviour ships with the test that would fail without it. A bug fix ships with a
regression test whose name encodes the scenario that was broken.

### Isolation and assertions

Tests run in any order and do not depend on each other. Each test arranges its own data.

- **No test that only asserts it does not throw.** Assert the observable outcome.
- **No `Thread.Sleep` or arbitrary delays.**
- **Do not add getter/setter-only tests to pad coverage.**

Failure messages should identify the offender. Where a report is asserted empty, join the
findings into the assertion message so a failure names the rule and document rather than
just `False != True`.

### Documented behaviour

Ranking weights, thresholds, and rule severities are described in the governed corpus. A
test that pins one of those numbers is pinning a documented claim — if you change it,
change the document in the same commit. `RetrievalRegressionTests` in particular guards
whether real questions find real answers; treat a failure there as a product regression,
not a test to adjust.

### Prose rules

Agent and docs prose (`products/kyber-squad/**`, governed docs) is tested for what it says, not for
its words ([ADR 0073](../../adr/0073-prose-rules-are-graded-by-meaning-not-pinned-by-phrase.md)):

- **Structure and protocol tokens are deterministic checks.** File existence, frontmatter, rosters,
  `delegates-to`, reference resolution, baseline injection, single-home markers, word budgets, and the
  short tokens code parses or emits (`STATUS:` values, `PLAN_FILE:`, `TASK_GATE:`, rule ids, flags).
  Assert a token against its C# constant where one exists.
- **Meaning is graded by Jev.** Each rule is one entry in the rule catalog,
  [`tests/KyberWeave.Content.Tests/Fixtures/prose-rules/catalog.yml`](../../../tests/KyberWeave.Content.Tests/Fixtures/prose-rules/catalog.yml):
  a one-sentence `statement`, the `stated-in` files that must state it, at least one negative `control`
  that must be judged not to, and, where an old rule must not come back, a `retired` statement with the
  files it must stay `absent-from` and an `example` the grader must recognise. The worker rule map's ids
  (A1 to G7) are catalog ids; [the map](../../kyber-squad/worker-rule-map.md) names its catalog rule in
  each row.
- **No phrase pins, forbidden-phrase pins or hash pins on prose.** A test that asserts a sentence, or a
  word from one, breaks on every rewording and says nothing about whether the rule is still stated.

**Where the remaining wording checks live.** Classes that check the shipped content (the text of skills,
agents and instructions, and the shape of standards and documents) are content tests in
[`tests/KyberWeave.Content.Tests`](../../../tests/KyberWeave.Content.Tests/AGENTS.md), not unit tests and not
integration tests: they read the content through the corpus seam (`ICorpusSource`), write nothing and start no
program, so they run with the unit projects. A test of how product code handles content uses fixture content
in memory and lives in the unit project of that code. They are scheduled to become
eval-style checks, so do not add new wording pins there. The repository-policy project
(`tests/KyberWeave.RepoPolicy.Tests`) keeps the checks on scripts, workflows, gate configuration and docs.

*Stabilisation, from 2026-11-13:* the last phrase pins are deleted, and a prose change does not need a
new catalog rule. The catalog runs as a net at merge points; a rule whose subject is deleted is retired,
and a moved rule's home is repointed. Rules for prose written in this period are added in the later
refinement phase.

`ProseRuleCatalogTests` runs offline on every build: every file, section heading, seed file and anchor
the catalog names exists, and every rule has a home and a control. Deleting a file, a heading or a catalog
entry fails there.

`ProseRuleGradingTests` sends each document to Jev through `SystemOneClient`, one two-option `choice`
question per rule and document. A home passes at the catalog's `confidence-floor` (0.8); a control or an
absent-from file must be judged not to state the rule at the same floor. A home is sent whole; a file over
the model's budget is sent as the section the catalog names, and the report says so. An absent-from file is
judged one part (one or a few `##` sections) at a time. Outcomes: an answer that points the right way but
lands below the floor is a warning, listed in the report and not a failure; a confident wrong answer, or a
wrong-way answer below the floor, fails; a provider error or timeout skips the test with a warning; a
refused key fails.

**Adding a rule.** Write the statement as one narrow judgment, in the documents' own terms (split an "and"
into two rules). Name the homes, and give the section heading for any file over about 60 KB. Pick a control
that plainly does not state it (cutting the home's section with `without-section` works only when no other
section repeats the rule). Run the grader on the rule. If a home keeps drawing warnings after a rewording,
give that home an `anchor` (a protocol token or heading it already carries, never a sentence of the rule)
instead of a grade; if an absent-from file keeps drawing warnings, drop it and say why in the commit.

**Running it locally.** The key comes from `TYPESAFE_API_KEY` or the OS credential store entry for the
endpoint's origin:

```bash
KW_PROSE_GRADE=1 dotnet test tests/KyberWeave.Integration.Tests/KyberWeave.Integration.Tests.csproj \
  -c Release --filter "FullyQualifiedName~ProseRuleGradingTests"
```

`KW_PROSE_GRADE_ENDPOINT` and `KW_PROSE_GRADE_MODEL` default to `https://openrouter.ai/api/v1` and
`typesafe/jev-1.14`. `KW_PROSE_GRADE_RULES=<id,id>` grades only those rules, `KW_PROSE_GRADE_OUT=<file>`
writes every answer as JSON lines and the report beside it, and `KW_PROSE_GRADE_CATALOG=<file>` grades
another catalog. A full run is about 1,100 questions, 3 million input tokens, $0.13 and 30 seconds; the
test stops at 12 million tokens or $2.

## Testing

### Concurrency and zero-flakiness isolation

Tests run concurrently across classes. Never introduce hidden cross-test coupling, process-wide mutation, or unisolated state:

- **No process-wide environment variable mutation**: Never call `Environment.SetEnvironmentVariable` in test code. An xUnit collection setting `DisableParallelization = true` only serializes tests inside that collection; tests in other collections continue running concurrently and observe mutated process state. Inject delegate resolvers (`Func<string, string?>? getEnvironmentVariable = null`, defaulting to `Environment.GetEnvironmentVariable`) or pass `SquadRenderRequest.EnvironmentReader`.
- **Lock-free `AsyncLocal` execution context scopes**: CLI console redirection and durability overrides isolate per async context rather than using process-wide locks or mutable statics. `ProcessConsoleCapture` uses `AsyncLocalTextWriter` and `AsyncLocalAnsiConsole` so parallel CLI test classes never contend on a static lock or bleed console output. `SquadTransaction.UseSyncToDiskScope(bool)` uses an `AsyncLocal<bool?>` scope routing store flushes through `SquadTransaction.SyncToDisk` so durability tests never leak macOS APFS `F_FULLFSYNC` barriers onto concurrent test threads.
- **Serial collections policy**: xUnit `DisableParallelization = true` (e.g. `ScaleAcceptanceCollection`) is reserved strictly for benchmarks monitoring thread allocations (`GC.GetAllocatedBytesForCurrentThread()`) or hard execution-time ceilings across high-volume iterations (such as 10,000 DocGraph claims). Never use collection serialization to mask shared static mutable state.
- **Parallel repository-wide scans**: Whole-repository assertion sweeps (such as `UpstreamSeveranceTests`) partition tracked file sets across all available cores using `Parallel.ForEach` with thread-safe collectors (`ConcurrentBag<string>`) to avoid multi-second single-threaded disk I/O bottlenecks.

### The suite stays inside its time budget

Each rule is one sentence a reviewer can check, and the measurement after it is why the rule
exists. The suite has become slow more than once; these are the causes that made it slow.

- **A class that does heavy work stays small, split by theme** — the runner parallelises across
  test classes and never inside one, so a class of 513 tests summed to 390 s and pinned the whole
  run.
- **Expensive setup — an installed deployment, a git repository, the agent corpus — is built once
  per class and copied per test, never rebuilt per test** — a real install costs 150–250 ms and a
  one-target deployment commit about 1.5 s.
- **A test works only inside the test temp root the test project provides, in its own folder, and
  removes it** — `TempDirectory` creates `kw-export-<guid>` under that root and deletes it on
  dispose, and no test writes directly in the system temp folder or lists a shared folder.
- **No sleeps and no fixed waits — wait on the condition — and no real process unless the test is
  about that process, with one end-to-end test per behaviour.**
- **Product code under test must not list a whole directory to find one entry** — a path check
  that did cost 6.7 ms per call on a temp root of 8,543 entries, and grew as the run added folders.
- **A single test over one second states why in a comment.**
- **Whoever adds or changes tests reports the time of the classes they touched.**

### Release sources and immutable corpus fixtures

Tests avoid redundant disk extraction, file copying, and YAML/Markdown parsing through focused test fixtures:

- **Synthetic release sources for plumbing and lifecycle**: Tests verifying deployment state machines, receipts, locks, and CLI exit codes use `SyntheticValidSquadReleaseSource` (~15 small files) rather than copying the full 203-file corpus on every run. Reserve `CorpusSquadReleaseSource` for end-to-end installation contracts and canonical prose checks.
- **Immutable test corpus caching**: Read-only contract tests asserting renderer templates and model presets (such as `SquadModelPresetRenderTests`, `CopilotRendererTests`, `AntigravityRendererContractTests`, and `SquadRustGoSpecialistTests`) consume lazy, thread-safe instances via `TestSquadCorpus.CanonicalSource` and cached unpreset baseline renders, avoiding hundreds of redundant disk reads and YAML/Markdown parsing across test theories.
- **One cross-harness renderer contract**: What every Squad renderer owes whatever its harness lives once in `SquadRendererSharedContractTests`. The target checks — claiming exactly one target, refusing a request for another, rendering the corpus byte-for-byte the same twice — run automatically for every renderer `SquadCommandComposition.CreateRenderers()` returns. Resource projection and the `capability-not-isolable` record where the shell implies write depend on the harness, so a new renderer adds its row to `ResourceProjectionContracts` and, when it cannot isolate the shell from writes, to `ShellWithoutWriteContracts`. A harness's own suite asserts only that harness's file format. Do not copy a shared invariant into it; add the harness's row to the shared suite instead.
- **Git repository templates**: Tests requiring git repositories (such as Review Council gate command tests) use `TestGitRepositoryTemplate.CreateRepositoryCopy` to copy a pre-seeded, committed repository directory (<2ms, 0 child processes) instead of spawning repeated `git init`/`add`/`commit` OS child processes per fixture.

### Internals are in scope

`InternalsVisibleTo` exposes the product projects' internals here, so helpers like link
normalization, vocabulary parsing, and docs-root detection are tested directly. Marking a
helper `internal` is not a reason to test it only through a command.

### Fixtures

The shared helpers live in `tests/KyberWeave.Testing` and are referenced by both test projects.
Two helpers that need a real disk tree live with their callers in
`tests/KyberWeave.Integration.Tests`: `CodeGraphFixtureDb` (a real sqlite index, for adapter parity only)
and `TestGitRepositoryTemplate` (a pre-seeded git repository copied without spawning OS child processes).

| Helper | For |
|---|---|
| `TempDirectory` | A disposable scratch directory. Implement `IDisposable` on the test class and dispose it. |
| `DocFixture` | A documentation corpus on disk — `.WithCatalog()`, `.WithSourceRoot()`, `.Write()`, `.Load()` |
| `FakeCodeGraphResolver` | `.WithSymbols(...)` for deterministic symbol and route resolution |
| `KyberWeaveTestPaths` | Locates the repository root by walking up to `KyberWeave.sln` |
| `ProcessConsoleCapture` | Lock-free, `AsyncLocal` console capture isolating stdout, stderr, and Spectre console width per async context |
| `TestSquadCorpus` | Thread-safe, lazy-cached immutable canonical `SquadSource` and target baseline renders |
| `SquadRenderedOutput` | Reading rendered Squad files back: frontmatter split, required scalars, shared and suppressed identities |
| `SyntheticValidSquadReleaseSource` | Lightweight ~15-file valid Squad source tree for state, lock, receipt, and lifecycle tests |

### Tests that start processes

A test that starts a process can start more of them. In 2026-10 a dashboard test did exactly
that and fork-bombed `node`. The rules below keep every process a test starts bounded, and the
two static audits (below) keep the list of such sites honest.

1. **Inject a recording spawner by default.** A test that needs to observe a spawn takes an
   injectable spawn function. A recording function starts nothing, so the test asserts the argv and
   environment it would have used. KyberDash's own engine spawns no Node children any more, so most
   suites need no spawn at all.
2. **A real child states its bound at the site.** If a test cannot avoid starting a real child, give
   it a fixed argv, a timeout and a `killSignal`, and add the site to the allow-list below with the
   same bound.
3. **Kill the whole group, not only the wrapper.** A Playwright boot is spawned `detached: true` and
   stopped with `process.kill(-pid, signal)`. Killing only `npx` or `sh` leaves the node process
   under it running.
4. **Bound every wait.** Give each readiness wait a timeout, and each `spawnSync`, `execSync` and
   `execFileSync` a `timeout` with `killSignal: 'SIGKILL'`. A child that exits by itself is better
   than one the test must kill.
5. **Bound the run itself.** Run KyberDash and tray commands directly from the repository root
   (`npm --prefix dash …`, `npx --prefix dash playwright test …`, `cargo … --manifest-path
   dash/tray/src-tauri/Cargo.toml …`); `.NET` commands run directly via `dotnet`, as the
   [runbook](../../dash/runbook.md#running-tests-safely) describes. There is no process wrapper: the
   retired watchdog and JS process ledger went away with the Node engine, because KyberDash now runs
   its jobs and OTLP receiver in-process and starts no recursive Node children. Keep each run's own
   bounds (per-test timeouts, a bounded `webServer`, `killSignal: 'SIGKILL'`) and stop it yourself
   when it hangs. From an agent tool keep a foreground run under about 600 seconds, because a longer
   command is moved to the background.
6. **Never start the dashboard from a test.** Use the e2e boot seams in `dash/e2e/harness-collection-boot.ts` and `dash/e2e/factory-replay-boot.ts`.

**The allow-list (.NET and repo scripts).** `ProcessSpawnSitesTests` in
`tests/KyberWeave.RepoPolicy.Tests/ProcessSpawnSitesTests.cs` scans every test project and
`tests/KyberWeave.Testing` (C# and shell), and `src/` (C#).
It matches `Process.Start`, `new Process`, `new ProcessStartInfo`, `ProcessRunner.Run`,
`ProcessRunner.RunInteractive`, and `BuildStartInfo`.
`tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json` keys each entry by
root-prefixed file (`tests/...` or `src/...`), normalized line text and count, with a `bound`.
The test fails on any unlisted start, with a root-prefixed file and line, and on any stale entry.
Use the `ProcessRunner` test helper, which kills the child tree at its timeout. A raw
`Process.Start` needs its own timeout and a `Kill(entireProcessTree: true)` in a `finally`.

**Production .NET starts carry bounds too.** The shared process executor defaults to 60 s, and the
KyberDash plugin verb to 120 s. `docs init`'s `apm install` is 5 min. The self-updater's capture verbs
are 120 s and its tray menubar update is 5 min; both kill the tree on expiry. The finish-update hand-off,
which runs the post-swap steps in the new kyber-weave, is 15 min and kills the whole process tree on
expiry. Its pre-swap rehearsal runs the installed `kyber-weave --version` with its output discarded and
is bounded to 30 s, and it kills the tree on expiry too. The best-effort `xattr`
call is 30 s. The CodeGraph sqlite read is 60 s. The analysis cache's `git ls-files` is 60 s, the
analysis sqlite batch 120 s, and its version probe 30 s. **One deliberate exception:**
`ProcessRunner.RunInteractive`, which runs `squad run` attended by a user at the console, has no
timeout by design. Do not give it one; a timeout would end a live session.

**Adding a spawn site.** Write the bound first. Add the entry to the allow-list for its language, with
the file, the API name or line text, and the bound. If the site cannot state a bound, it does not
belong in a test: remove it or inject a fake.

## Commands

_Nothing specific yet._

## Security and data handling

_Nothing specific yet._

## Done checklist

### Completion

This is how a change to a test is proved. Build once, run the classes the change touches, run the
format check, then report. Run these `dotnet` commands directly from the repository root.
**No dev agent runs the full test suite**: the review council's `test` gate and CI run it. Whoever
runs it, person or agent, times it and reports the number: it must finish in 120 seconds or less
([the .NET test budget](../../../AGENTS.md#the-net-unit-suite-completes-in-120-seconds-or-less)).
The integration project (`tests/KyberWeave.Integration.Tests`) is not part of that budget: CI runs it
as its own required job, and no developer agent, task gate or council gate runs it.

```bash
dotnet build tests/KyberWeave.<Project>.Tests/KyberWeave.<Project>.Tests.csproj -c Release
dotnet test tests/KyberWeave.<Project>.Tests/KyberWeave.<Project>.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"
dotnet format KyberWeave.sln whitespace --verify-no-changes -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn -v minimal
```

A person reproducing a failing CI integration class runs it the same way, against its own project:

```bash
dotnet build tests/KyberWeave.Integration.Tests/KyberWeave.Integration.Tests.csproj -c Release
dotnet test tests/KyberWeave.Integration.Tests/KyberWeave.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"
```

The build comes first and `--no-build` depends on it: the same build produced the binaries the
filtered run executes. The filter names a class by its fully qualified name — a short name runs
zero tests and exits 0, so read the total and treat zero as nothing verified. The format check is
two verbs, `whitespace` and `style`, because the council runs both as blocking gates
(`format-whitespace` and `format-style` in [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml));
a test file that fails either is a finding the council raises and no test catches.

A **full run** is any run of the whole unit suite. A bare `dotnet test` (with or without options such as
`-c Release`), a solution target such as `dotnet test KyberWeave.sln` or `dotnet test --solution KyberWeave.Unit.slnf`, a directory, or
a unit test project or its `.dll` runner is full, unless it carries a narrowing flag (`-class`, `-method`, `-trait`, `-treenode-filter` and `--treenode-filter`, in the `=` form too, or any
`--filter`). `dotnet run` is full when any `--project`, `--project=` or `-p` names a unit test project, even beside
another project, and no narrowing flag appears; otherwise it is partial. `--artifacts-path` takes a value.
`npm test`, `npm t`, `npm run test` and `npm run-script test` with no positional word after `--` are full under any
`--prefix` (none, relative, absolute, or the tray UI directory), and so is `cargo test` with no positional filter.
Value-taking npm flags before the command word (`--omit`, `--tag`, `--otp`, `--include`, `--access`, `--prefix`) are
skipped. `cargo` skips a `+toolchain`, `-C <dir>` and `--artifact-dir` before the subcommand. `cargo test --test <x>`,
`--bin`, `--example` or `--bench` selects one target and is partial.
Leading launchers (`env`, `timeout`, `nice`, `time`, `command`, `exec`, `nohup`, `caffeinate`, `sudo`, `doas`, `ionice`,
`setsid`, `stdbuf`, `arch`) are skipped before the command is classified; an unknown word after a launcher makes the
classifier scan forward to the first `dotnet`, `npm` or `cargo` word. Naming another project (the RepoPolicy project, for example) or a path narrows a run to a partial run, which dev agents
may use. `npm run test:related` and `dotnet build` are never full runs. When a shape is ambiguous the classifier calls
it full. A command wrapped in a shell string (`sh -c '...'`) is not parsed. The refusal lives in the commands
themselves, not in a wrapper: `kyber-weave review gates` refuses a gate marked `full-suite: true` unless the
caller passes `--council` or stdin is a terminal (blocking failure `KW-REVIEW-033`, exit 1, nothing started),
and `scripts/fast-loop.sh --full` and `--scoped` exit 89 from a non-interactive shell. Only the
`code-reviewer` council and CI pass `--council`. The full-run definition above is the rule; the docs previously
exposed it as a `classify` verb on the retired watchdog script, and that verb is gone with the script, so read
this section as the reference.

A filter that matches nothing exits 8 ("Zero tests ran"). Outside a Kyber-Squad run,
`scripts/fast-loop.sh --class 'KyberWeave.Tests.<Area>*'` does the same for one area and builds
first. Inside a run, the task packet names the command.


### Limits of the guard

The guard is best effort. It reads the command line and whether stdin is a terminal, so it does not stop everything an agent can do:

- A shell with a pseudo-terminal looks like a person at a terminal and passes the terminal test. The Claude Code Bash tool has no terminal (checked 2026-11-06); other harnesses have not been checked.
- An agent that types `--council` passes. The flag is the caller's claim, not proof of who is calling.
- A `dotnet test` run outside `review gates` is not seen. Only a harness-level hook can see it.
- A command inside a shell string (`sh -c '...'`, `bash -c '...'`) or an `env -S` string is not parsed, so it is classified as partial and runs.
- A launcher outside the list above is not unwrapped. Its command is classified only when a `dotnet`, `npm` or `cargo` word follows it.
- An unknown value-taking flag before the command word can hide the subcommand, so the run is classified as partial.
- Other test runners are not classified: `dotnet vstest`, `npx vitest` and `cargo nextest` are classified as partial and run.
- Deliberate over-refusals: a targeted run classified as full is refused by design. These are any directory target, `npm test --workspace <name>`, `cargo test -p <package>` (`--package` too), any npm `--prefix` directory on a test script, and a launcher followed by an unknown word and then a `dotnet`, `npm` or `cargo` word (such as `sudo echo please run dotnet test`). Recover with a named class (`-class` for dotnet, `--class` for `scripts/fast-loop.sh`), `--council`, or a terminal.
- `scripts/fast-loop.sh` with no arguments runs an unfiltered `cargo test` over the whole tray crate for a tray Rust change, outside `review gates`.

The real closure is a per-harness shell hook that sees each shell call before it runs. It is tracked in [`docs/todo/per-harness-shell-hook-for-full-run-guard.md`](../../todo/per-harness-shell-hook-for-full-run-guard.md). The decision and its alternatives are in [ADR 0059](../../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md).

### Fast Assembly Runner and Iteration Workflow

During task execution and active development, use the direct xUnit assembly runner with class filtering. MSBuild project evaluation and incremental dependency checks add multi-second overhead to every `dotnet test` invocation:

```bash
dotnet tests/KyberWeave.<Project>.Tests/bin/Release/net10.0/KyberWeave.<Project>.Tests.dll -class "<namespace>.<Class>"
```

`-class` matches the fully qualified name: a short class name runs zero tests and exits 0, so read
the total and treat zero as nothing verified.

When the build used `--artifacts-path <dir>` with `-c Release`, the direct runner reads `<dir>/bin/KyberWeave.<Project>.Tests/release/KyberWeave.<Project>.Tests.dll` (the configuration is lowercase there) instead of `tests/KyberWeave.<Project>.Tests/bin/Release/net10.0/KyberWeave.<Project>.Tests.dll`; in your own isolated worktree without that flag the `bin/Release` command above is correct.

- **Build once, test many (`--no-build`)**: Compile the test project once before iteration. Subsequent targeted test executions against non-code or single-class edits run directly via the compiled assembly runner or pass `--no-build` to bypass redundant MSBuild restore and rebuild evaluation.
- **Reserve the full suite for the review council and CI**: Running the complete test suite across all classes is reserved for the review council's `test` gate, which runs it on the integrated head in the first full review, and for CI. No dev agent runs it, and a cross-cutting change is no exception. At task end, run only the test classes the task touched: the classes the task packet's runner command names, plus any other test class that references a symbol the task changed. If no class covers the change, run none and report `TESTS: total=0` with the reason, because zero tests run is not zero failures. If the change reaches shared test infrastructure (fixtures, base classes and shared helpers, meaning anything referenced by more than one test class, which `codegraph_explore` on the type shows; and test-project build settings, which every test class depends on), run a targeted set: the classes that reference what changed or, for a change every class depends on, a few representative classes. Name what you did not run; the council and CI cover it.
- **The failing run is the task's own**: In test-first mode the worker that writes a task's code writes its tests first and runs its task gate with `--red` before implementing, then runs the gate again once the tests pass; the two reports are the proof ([ADR 0076](../../adr/0076-test-first-is-one-worker-per-task.md)). Never stash or revert work to fake a failing run.

The speed rules in [the suite stays inside its time budget](#the-suite-stays-inside-its-time-budget)
are checked here, in the same order:

- **The classes you touched report their time.** State each one's time from the run in your report.
- **A class you add or enlarge is small and split by theme**, so it does not hold a large share of
  the suite's serial time.
- **Expensive setup is a class fixture the tests copy**, not work each test repeats.
- **Every test works in its own folder under the test temp root and removes it.**
- **No sleep, no fixed wait, and no real process the test is not about.**
- **No test makes product code list a directory to find one entry.**
- **A test over one second gives its reason in a comment.**

### Where the tests put their files, and the optional RAM disk

Every test writes under one per-process root, `<base>/kw-<checkout-key>/<pid>`, because
`TestTempRoot` redirects `TMPDIR` for the test process and every child it spawns. On macOS the
base is `/private/var/folders/<xx>/<hash>/kw-test-temp`, a folder the tests create beside the
per-user temp directory (`getconf DARWIN_USER_TEMP_DIR`). That tree is the location this
machine's endpoint-security software (Microsoft Defender) skips, and the folder is beside the
shared temp directory rather than inside it: canonicalising a deployment path lists every
ancestor, and on this machine the temp directory held 1,706 entries against 5 in its parent, so
leaving the root out of it takes that listing off every install. Linux and the rest use `/tmp`,
which Defender scans.

Measured on the same 651 tests (`ArbiterInstallCommandTests`, the eight `SquadDeploymentState`
classes and `SquadGlobalRootTests`, now in the integration project), Defender real-time
protection off, load average 6.5–8.6, runs back to back:

| Base | Run 1 | Run 2 |
|---|---|---|
| per-user temp directory (`/var/folders/<xx>/<hash>/T`) | 23.35 s | 22.38 s |
| `kw-test-temp` beside it | 19.23 s | 19.43 s |

The slowest single test in those runs,
`ArbiterInstallCommandTests.Install_ThreeGlobalTargets…`, took 9.61 s and 9.38 s under the temp
directory and 7.56 s and 7.58 s under its sibling. The cache folder that
`getconf DARWIN_USER_CACHE_DIR` returns is a third candidate, rejected on entry count: it held
693 entries against 5 in the per-user folder.

`KW_TEST_TEMP_ROOT=<absolute path>` moves the base while keeping the `kw-<key>/<pid>` shape, so a
run still cannot see another checkout's or another process's files. A relative path, or one the
control pipe cannot fit, fails the run instead of silently measuring somewhere else.

**A RAM disk, for a heavy local run.** Guidance, not a script: create the volume, point the root at
it, run, then eject it. This example is 8 GiB (16,777,216 sectors of 512 bytes):

```bash
diskutil erasevolume HFS+ KWTestRam "$(hdiutil attach -nomount ram://16777216)"
export KW_TEST_TEMP_ROOT=/Volumes/KWTestRam
# ... run the tests ...
diskutil eject /Volumes/KWTestRam
```

Measured on 651 tests with `/tmp` as the base, a RAM disk is about 26 % faster. That comparison
predates the default base change above and has not been repeated against `kw-test-temp`. Ejecting
discards everything on it, so it needs no cleanup.

**Why the path must stay short.** The test runner puts its control pipe at
`$TMPDIR/MONITORTOHOST_<32 hex>`, and a Unix domain socket path is limited to 103 bytes, so a root
that still carries that pipe may be at most 56 characters. The default macOS root is about 85
characters, so `TestTempRoot` moves the pipe to `/tmp` through
`TESTINGPLATFORM_PIPE_DIRECTORY`. An override is held to the same limit and fails with the budget
in its message unless you set `TESTINGPLATFORM_PIPE_DIRECTORY` yourself;
`/Volumes/KWTestRam/kw-<16 hex>/<pid>` is about 44 characters and fits.
