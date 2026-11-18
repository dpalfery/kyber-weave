# KyberWeave.Content.Tests

Content tests: tests that check the content this repository ships (agents, skills, instructions,
standards, documents and workflows). A content test is neither a unit test nor an integration
test: the content is its subject, so it cannot be faked.

Read [`/AGENTS.md`](../../AGENTS.md) first, and
[`../KyberWeave.RepoPolicy.Tests/AGENTS.md`](../KyberWeave.RepoPolicy.Tests/AGENTS.md) for the
repository-policy project, which this one sits beside.

- A test belongs here when its subject is the shipped content. A test of how product code parses,
  validates, renders or loads content belongs in the unit project of that code and uses fixture
  content in memory (`InMemoryCorpusSource`), not the shipped corpus.
- A test that checks the repository itself (scripts, workflows, gate configuration, source scans) or
  starts a program belongs in `KyberWeave.RepoPolicy.Tests`.
- Read the shipped content only through the corpus seam (`KyberWeave.Tests.Corpus.ICorpusSource`;
  `CheckoutCorpusSource.Checkout` is this checkout) from the shared test library, `KyberWeave.Testing`.
  Never walk up the directory tree to find the repository root, write a file, or start a program.
- These tests run with the unit projects, on every .NET loop and in the unit step of CI, inside the
  120-second budget.
- Some wording checks are scheduled to become eval-style: a model grades whether the content states a
  rule, and the wording pins go away. Do not add new wording pins for behaviour that an eval would
  cover. Prose rules belong in the prose rule catalog (`tests/KyberWeave.Tests/Fixtures/prose-rules/catalog.yml`).
- Classes keep the `KyberWeave.Tests` namespace, so a fully-qualified class name means the same test
  in either project.
- To run one class, build once and filter, running `dotnet` directly from the repository root. A filter
  that matches nothing exits 8 ("Zero tests ran").

  ```bash
  dotnet build tests/KyberWeave.Content.Tests/KyberWeave.Content.Tests.csproj -c Release
  dotnet test tests/KyberWeave.Content.Tests/KyberWeave.Content.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"
  ```
