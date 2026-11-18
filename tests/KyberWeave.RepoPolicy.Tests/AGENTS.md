# KyberWeave.RepoPolicy.Tests

Tests whose subject is the repository, not the product code: the scripts (`fast-loop.sh`,
release and SBOM tooling), CI workflows, `.kyber-weave/kyber-weave.yml`, docs prose, and source
scans such as the spawn-site audit and the root instruction pointers. Read
[`/AGENTS.md`](../../AGENTS.md) first, and
[`../KyberWeave.Tests/AGENTS.md`](../KyberWeave.Tests/AGENTS.md) for the shared fixtures.

- A test belongs here when it reads a repository file as its subject. A test that calls product
  code to check its behaviour belongs in `KyberWeave.Tests`, even if it reads a fixture.
- The project references `KyberWeave.Core` only, for `ProcessRunner`. Needing another product
  assembly means the test is a behaviour test.
- Classes keep the `KyberWeave.Tests` namespace, so a fully-qualified class name means the same
  test in either project. `KyberWeaveTestPaths`, `TempDirectory` and `TestTempRoot` come from the
  shared `KyberWeave.Testing` library, not copies. A test that reads shipped content reads it
  through `KyberWeave.Tests.Corpus.ICorpusSource` (`CheckoutCorpusSource.Checkout`), never by
  walking the tree or by reading `KyberWeaveTestPaths.ToolRoot` with `File.ReadAllText`.
- It runs as the `test-policy` gate, its own CI step, and in `scripts/fast-loop.sh` whenever a
  change is more than C# under `src/` or `tests/KyberWeave.Tests/`. The tests that read source
  text run on every .NET loop; they are named in `POLICY_SOURCE_SCANS` in that script.
- To run one class, build once and filter, running `dotnet` directly from the repository root. A filter
  that matches nothing exits 8 ("Zero tests ran").

  ```bash
  dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release
  dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"
  ```
