---
id: standards/test
title: "Test coding standard"
doc-type: coding-standard
status: current
technology: test
owner: dpalfery
last-reviewed: 2026-08-16
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

## Commands

```bash
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
```
