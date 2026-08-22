# KyberWeave.Tests

Test policy lives in the path declared as **<test-coding-standard>**. Language-level C#
follows **<csharp-coding-standard>**. Read [`/AGENTS.md`](../../AGENTS.md) first for
repository-wide rules.

This file is how to work in this project: which fixtures exist, and that internals are in
scope. It does not restate the standard.

## Fixtures

| Helper | For |
|---|---|
| `TempDirectory` | A disposable scratch directory. Implement `IDisposable` on the test class and dispose it. |
| `DocFixture` | A documentation corpus on disk — `.WithCatalog()`, `.WithSourceRoot()`, `.Write()`, `.Load()` |
| `FakeCodeGraphResolver` | `.WithSymbols(...)` for deterministic symbol and route resolution |
| `CodeGraphFixtureDb` | A real sqlite index, for adapter parity only |
| `KyberWeaveTestPaths` | Locates the repository root by walking up to `KyberWeave.sln` |

## Internals are in scope

`InternalsVisibleTo` exposes `KyberWeave.Core` internals here, so helpers like link
normalization, vocabulary parsing, and docs-root detection are tested directly. Marking a
helper `internal` is not a reason to test it only through a command.
