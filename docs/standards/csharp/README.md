---
id: standards/csharp
title: "C# coding standard"
doc-type: coding-standard
status: current
technology: csharp
owner: dpalfery
last-reviewed: 2026-08-16
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

Two consequences worth stating:

- **Fix the cause, not the symptom.** Adding an id to `NoWarn` in `Directory.Build.props` is
  a decision about the whole repository, and it needs a reason you can state in the file.
- **NuGet audit failures at high or critical severity fail the build.** Low and moderate stay
  warnings so advisory noise does not block unrelated work.

## Style the analyzers already enforce

`.editorconfig` is authoritative; this section only names the choices that surprise people
coming from other .NET repositories.

- **Explicit types, never `var`** — including where the type is apparent. `csharp_style_var_*`
  is `false:warning` in all three positions.
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

## Comments explain why, not what

The most consistent habit in this codebase, and the one most worth keeping. Types and
non-obvious methods carry `<summary>`, and a `<remarks>` block gives the reasoning behind a
decision a reader would otherwise find arbitrary — why the CodeGraph index is read through the
`sqlite3` CLI rather than `Microsoft.Data.Sqlite`, why the host config is edited as text rather
than round-tripped through a YAML emitter, why plans are demoted in ranking.

Write that block at the point the decision is made. A rationale in a commit message is a
rationale nobody reads.

## Errors and diagnostics

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
