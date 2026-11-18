# KyberWeave.Tests

Test policy lives in the path declared as **<test-coding-standard>**. Language-level C#
follows **<csharp-coding-standard>**. Read [`/AGENTS.md`](../../AGENTS.md) first for
repository-wide rules.

This file is how to work in this project: which fixtures exist, and that internals are in
scope. It does not restate the standard.

Tests whose subject is the repository itself (scripts, workflows, gate configuration, docs
prose, source scans) live in [`../KyberWeave.RepoPolicy.Tests`](../KyberWeave.RepoPolicy.Tests/AGENTS.md).
Tests that assert only the wording of skills, agents and instructions live in
[`../KyberWeave.PromptContent.Tests`](../KyberWeave.PromptContent.Tests/AGENTS.md), so this project
measures behaviour.

## Fixtures

The helpers both this project and `tests/KyberWeave.Integration.Tests` use live in
`tests/KyberWeave.Testing`; this table names the ones a test in this project reaches for.

| Helper | For |
|---|---|
| `TempDirectory` | A disposable scratch directory. Implement `IDisposable` on the test class and dispose it. |
| `DocFixture` | A documentation corpus — `.WithCatalog()`, `.WithSourceRoot()`, `.Write()`, `.Load()`. `DocFixture.CreateInMemory()` builds it on the in-memory file system for a unit test; `DocFixture.Create()` uses a real directory, for adapter and integration tests only |
| `FakeCodeGraphResolver` | `.WithSymbols(...)` for deterministic symbol and route resolution |
| `KyberWeaveTestPaths` | Locates the repository root by walking up to `KyberWeave.sln` |

## Internals are in scope

`InternalsVisibleTo` exposes the internals of the product projects here, so helpers like link normalization, vocabulary parsing, and
docs-root detection are tested directly. Marking a helper `internal` is not a reason to test
it only through a command.
