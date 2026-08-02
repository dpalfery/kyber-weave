# KyberWeave.Tests

xUnit, one project covering all three artifact classes. 144 tests, fast — the whole suite
runs in well under a second, and it should stay that way.

Read [`/AGENTS.md`](../../AGENTS.md) first for repository-wide rules.

## Prefer fakes; use the real sqlite fixture only for parity

`FakeCodeGraphResolver` implements `ICodeGraphResolver` deterministically, so the entire
drift, join, and retrieval surface is testable **without `sqlite3` installed**. That is the
default, and it is why the port exists.

`CodeGraphFixtureDb` builds a real sqlite database by shelling out to `sqlite3`. It exists
for one job — proving the adapter reads a real index the same way the fake claims to. Do
not reach for it to test behaviour a fake can express; it makes the suite depend on a tool
the machine may not have.

## Fixtures

| Helper | For |
|---|---|
| `TempDirectory` | A disposable scratch directory. Implement `IDisposable` on the test class and dispose it. |
| `DocFixture` | A documentation corpus on disk — `.WithCatalog()`, `.WithSourceRoot()`, `.Write()`, `.Load()` |
| `FakeCodeGraphResolver` | `.WithSymbols(...)` for deterministic symbol and route resolution |
| `CodeGraphFixtureDb` | A real sqlite index, for adapter parity only |
| `KyberWeaveTestPaths` | Locates the repository root by walking up to `KyberWeave.sln` |

Never write into the repository tree from a test. Scaffolding and export tests write to a
`TempDirectory` and assert on what landed there.

## Internals are in scope

`InternalsVisibleTo` exposes `KyberWeave.Core` internals here, so helpers like link
normalization, vocabulary parsing, and docs-root detection are tested directly. Marking a
helper `internal` is not a reason to test it only through a command.

## What a good test asserts here

Prefer the property over the mechanism. `DocsScaffolderTests` asserts that a freshly
scaffolded corpus **passes `DocSpecValidator`** rather than that three specific files were
written — the second breaks on every template edit, the first breaks only when the
guarantee breaks.

Name the guarantee in the test name and, when the reason is not obvious, put the failure it
prevents in a `<summary>`. Several tests here exist because a specific regression happened;
that context is what stops someone deleting them later.

Failure messages should identify the offender. Where a report is asserted empty, join the
findings into the assertion message so a failure names the rule and document rather than
just `False != True`.

## Documented behaviour

Ranking weights, thresholds, and rule severities are described in
[`docs/`](../../docs/README.md). A test that pins one of those numbers is pinning a
documented claim — if you change it, change the document in the same commit.
`RetrievalRegressionTests` in particular guards whether real questions find real answers;
treat a failure there as a product regression, not a test to adjust.
