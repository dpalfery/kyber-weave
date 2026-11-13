---
id: standards/csharp
title: "C# coding standard"
doc-type: coding-standard
status: current
technology: csharp
owner: dpalfery
last-reviewed: 2026-11-11
---

# C# coding standard

How C# is written in this repository. Agents and skills resolve this document as
`<csharp-coding-standard>` in the repository root `AGENTS.md`, so what it says here outranks
the defaults a portable agent shipped with.

This is not a summary of the C# language documentation. It records the decisions this
repository has made and would otherwise have to re-argue in review.

## The build is the first reviewer

`TreatWarningsAsErrors` is on with `AnalysisMode=all` and `AnalysisLevel=latest-All`. A
warning fails the build, which means the analyzers — not a reviewer — are what catch the
mechanical problems.

Consequences worth stating:

- **Fix the cause, not the symptom.** Adding an id to `NoWarn` in `Directory.Build.props` is
  a decision about the whole repository, and it needs a reason you can state in the file.
- **NuGet audit failures at high or critical severity fail the build.** Low and moderate stay
  warnings so advisory noise does not block unrelated work.
- **Analyzers run during build, not in a separate task gate.** Because `dotnet build`
  runs all Roslyn analyzers with `TreatWarningsAsErrors=true` under `AnalysisMode=all`,
  developer task-gates (`review.task-gates`) omit `dotnet format analyzers`, saving ~24s
  per task with zero loss of analyzer coverage.
- **A format gate's exit 0 does not prove a project file was checked.** `dotnet format
  --include` accepts a `.csproj`, `.props` or `.targets` path and exits 0 without formatting
  anything, because it rewrites C# documents; a path that does not exist behaves the same way.
  Measured on Linux, Windows and macOS for all three verbs. Those globs in a gate's `include`
  buy **selection** — a change to one of those files makes the gate run instead of selecting
  nothing — and never coverage of the project file itself.
- **Persistent InspectCode AST caching.** Whole-solution ReSharper InspectCode
  (`inspectcode`) runs with `--caches-home=artifacts/inspectcode-cache` to persist AST
  caches across runs, cutting warm inspection duration by ~35% wall-clock and ~68% CPU.

## Style the analyzers already enforce

`.editorconfig` is authoritative; this section only names the choices that surprise people
coming from other .NET repositories.

- **Explicit types, never `var`** — including where the type is apparent. `csharp_style_var_*`
  is `false:warning` in all three positions. `KyberWeave.sln.DotSettings` sets ReSharper's
  var preferences to `UseExplicitType` to match, so the `KyberWeave` cleanup profile rewrites
  `var` to explicit types rather than the reverse.
- **File-scoped namespaces.**
- **Predefined type keywords** — `string`, `int`, `bool`, never `String` or `Int32`.
- **Allman braces**, and a new line before `else`, `catch` and `finally`.
- **`System` usings first**, in one group with the rest.
- LF endings, four-space indent, final newline, no trailing whitespace. Two spaces for YAML,
  JSON, and MSBuild files.

## Types

**Seal by default.** Public types are `sealed` unless something derives from them. Inheritance
in this codebase is a deliberate extension point, not the default shape of a class.

**Records for values, classes for behaviour.** A record models a value the code passes around
and compares — `ScaffoldedFile`, `ConfigRegEntry`, `Diagnostic`. Something with dependencies
or lifetime is a class.

**Enums are closed vocabularies.** Adding a member to `DocType` or `Severity` is a change to
the product's ontology, not an authoring convenience, and it moves together with the
configuration defaults, the parser, and the documentation that describes it.

**Nullable is enabled.** A nullable annotation is a claim; do not answer a warning with `!`
when the honest fix is a check or a different signature.

## Safe filesystem enumeration

Any recursive filesystem walk over a user-controlled root must not use `SearchOption.AllDirectories`
or `RecurseSubdirectories = true` directly, because .NET invariably follows directory symbolic
links during such recursion ([dotnet/runtime#52666](https://github.com/dotnet/runtime/issues/52666)).
This can cause the walk to escape the intended directory boundary.

Instead, walk one level at a time using `new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 })`,
and for each entry check `entry.LinkTarget is not null` to detect a symlink. `AttributesToSkip = 0`
keeps the recursive overload's behavior: the default skips `Hidden`, which Unix sets on every
dot-prefixed name. Skip any entry that is itself a symlink — do not descend into symlinked
directories, do not yield symlinked files, and never call `ResolveLinkTarget` (which opens
symlinks, potentially reaching outside the root). See [`DocsRootPath.EnumerateContainedFiles`](../../src/KyberWeave.Core/Configuration/DocsRootPath.cs)
in the codebase for the canonical implementation of this pattern.

For a single configured path, such as the catalog, check each directory below the root outermost
first, then the file's `LinkTarget` before `File.Exists`, which follows a link on Unix.
`DocsRootPath.IsContainedFile` implements this.

This per-entry guarantee applies only below the root the walk starts from. Whether that root may
itself be a symlink is the caller's decision, outside this pattern.

## Comments explain why, not what

The most consistent habit in this codebase, and the one most worth keeping. Types and
non-obvious methods carry `<summary>`, and a `<remarks>` block gives the reasoning behind a
decision a reader would otherwise find arbitrary — why the CodeGraph index is read through the
`sqlite3` CLI rather than `Microsoft.Data.Sqlite`, why the host config is edited as text rather
than round-tripped through a YAML emitter, why plans are demoted in ranking.

Write that block at the point the decision is made. A rationale in a commit message is a
rationale nobody reads.

## Errors and diagnostics

Parsers fail closed (KW-STD-INPUT-001). Code that parses or converts a value from outside the process refuses any input it does not recognise, with an error or diagnostic that names the input; it never silently drops, defaults or coerces it. The one exception is an unknown field in a format whose specification says to ignore unknown fields, and the code names that specification where it skips the field.

**Catch what you can name.** Operational failures are caught by type — `IOException`,
`UnauthorizedAccessException`, `YamlException`, `InvalidDataException` — and translated into a
diagnostic or an exit code. A bare `catch (Exception)` around code that can fail for a reason
you have not thought about hides the reason.

**Never swallow.** A failure that produces no diagnostic, no exit code and no log line is
indistinguishable from success.

**A diagnostic carries a hint.** A finding that cannot be acted on is noise. Where a
nearest-match suggestion is computable — the closest catalog component, the closest known id —
offer it.

**Rule ids are permanent.** A `KW-*` id is what suppressions, SARIF baselines and code-scanning
alerts key on. Add new ids; never reuse or renumber one.

## Dependencies

`KyberWeave.Core` takes Markdig and YamlDotNet, and adding a third needs a justification you
can state. The reasoning is in [`AGENTS.md`](../../../AGENTS.md): every dependency this
library takes is one every host repository takes with it.

## Output

CLI output goes through `AnsiConsole`, never `Console.WriteLine`. Any value that came from a
file, a configuration, or an operator is passed through `Markup.Escape` before it reaches
markup — a path containing brackets otherwise corrupts the render or throws.

## Tests

Test authorship follows `<test-coding-standard>`. Language-level C# in test files follows
this document.

## Completion

A finishing worker builds once, runs the test classes its change touches, and passes the format
check. Nothing here runs the whole suite: the review council's `test` gate and CI run it, and a
cross-cutting change is no exception. Run everything through the process watchdog, from the
repository root.

```bash
scripts/proc-guard.sh run -- dotnet build KyberWeave.sln -c Release
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"
scripts/proc-guard.sh run -- dotnet format KyberWeave.sln whitespace --verify-no-changes -v minimal
scripts/proc-guard.sh run -- dotnet format KyberWeave.sln style --verify-no-changes --severity warn -v minimal
```

`dotnet format` is the only formatter. ReSharper `cleanupcode` is not used: it can finish its work and then never exit (ADR 0073).

- **Build first, and let the later runs reuse it.** The build is where the analyzers run, so a
  warning stops here rather than at review; `--no-build` on the test line is safe only after it.
- **A filter names a class by its fully qualified name.** A short class name runs zero tests and
  exits 0, so read the total and treat zero as nothing verified. A filter that matches nothing is
  a wrong class name, not a pass.
- **The format check is two verbs**, `whitespace` and `style`, because the council runs both as
  blocking gates (`format-whitespace` and `format-style`). Either one failing is a finding no test
  catches.
- **A faster loop after one build** runs the compiled assembly directly:
  `dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class "KyberWeave.Tests.<Class>"`.
  With `--artifacts-path <dir>` the same assembly is at `<dir>/bin/KyberWeave.Tests/release/`
  (the configuration is lowercase in that path). The
  [test standard](../test/README.md#completion) gives the full runner contract.
- **The watchdog refuses a full run** with exit 89 unless the caller passes `--council` or stdin is
  a terminal. Only the review council and CI pass `--council`; a person at a terminal is not
  refused.
