# KyberWeave.Testing

Test policy lives in the path declared as **<test-coding-standard>**. Language-level C#
follows **<csharp-coding-standard>**. Read [`/AGENTS.md`](../../AGENTS.md) first for
repository-wide rules.

This library holds the helpers every test project uses. It holds no tests, except the shared
contract test base classes that each project's fake or adapter subclasses. Tests whose subject is the
repository itself live in [`../KyberWeave.RepoPolicy.Tests`](../KyberWeave.RepoPolicy.Tests/AGENTS.md);
tests that check the shipped content live in
[`../KyberWeave.Content.Tests`](../KyberWeave.Content.Tests/AGENTS.md).

## Fixtures

| Helper | For |
|---|---|
| `TempDirectory` | A disposable scratch directory. Implement `IDisposable` on the test class and dispose it. |
| `DocFixture` | A documentation corpus: `.WithCatalog()`, `.WithSourceRoot()`, `.Write()`, `.Load()`. `DocFixture.CreateInMemory()` builds it on the in-memory file system for a unit test; `DocFixture.Create()` uses a real directory, for adapter and integration tests only |
| `InMemoryFileSystem` | The in-memory file system a unit test runs over; `Root` is the platform root every path resolves under |
| `FakeCodeGraphResolver` | `.WithSymbols(...)` for deterministic symbol and route resolution |
| `KyberWeaveTestPaths` | Locates the repository root by walking up to `KyberWeave.sln` |

## Internals are in scope

`InternalsVisibleTo` exposes the internals of the product projects to every test project, so helpers like link
normalization, vocabulary parsing, and docs-root detection are tested directly. Marking a helper `internal` is not a
reason to test it only through a command.
