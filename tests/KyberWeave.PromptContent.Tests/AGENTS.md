# KyberWeave.PromptContent.Tests

Tests that check the wording of governed content: the markdown and yaml that shapes agent
behaviour (skills, agents, instructions, response guidance, standards templates). Each test reads
a product file as text and asserts that a token, phrase or marker is present or absent.

Read [`/AGENTS.md`](../../AGENTS.md) first, and
[`../KyberWeave.RepoPolicy.Tests/AGENTS.md`](../KyberWeave.RepoPolicy.Tests/AGENTS.md) for the
repository-policy project, which this one sits beside.

- A test belongs here when its subject is the wording of a governed file and nothing else. A test
  that parses, validates, renders or loads that content belongs in `KyberWeave.Tests`.
- A test that checks the repository itself (scripts, workflows, gate configuration, docs
  structure, source scans) belongs in `KyberWeave.RepoPolicy.Tests`.
- These checks are scheduled to become eval-style: a model grades whether the content states a
  rule, and the wording pins go away. Do not add new wording pins for behaviour that an eval would
  cover. Prose rules belong in the prose rule catalog (`tests/KyberWeave.Tests/Fixtures/prose-rules/catalog.yml`).
- The project references no product assembly. It reads shipped content through the corpus seam
  (`KyberWeave.Tests.Corpus.ICorpusSource`, whose checkout implementation finds the repository root
  once) in the shared test library, `KyberWeave.Testing`. Classes keep the `KyberWeave.Tests`
  namespace, so a fully-qualified class name means the same test in either project.
- It runs as its own CI step (`Test (prompt content)`), and in `scripts/fast-loop.sh` whenever a
  change is more than C# under `src/` or `tests/KyberWeave.Tests/`.
- To run one class, build once and filter, running `dotnet` directly from the repository root. A filter
  that matches nothing exits 8 ("Zero tests ran").

  ```bash
  dotnet build tests/KyberWeave.PromptContent.Tests/KyberWeave.PromptContent.Tests.csproj -c Release
  dotnet test tests/KyberWeave.PromptContent.Tests/KyberWeave.PromptContent.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"
  ```
