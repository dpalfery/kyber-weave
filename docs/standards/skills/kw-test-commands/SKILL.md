---
name: kw-test-commands
description: "Run Kyber-Weave tests by project: unit, repository-policy and content test commands, the fast-loop router, and the shared fixtures. Load when writing or running tests in this repository."
---

# Kyber-Weave test commands

## Commands

```bash
# The unit tests of the project that holds the code: build once, then the classes the change covers.
dotnet build tests/KyberWeave.<Project>.Tests/KyberWeave.<Project>.Tests.csproj -c Release
dotnet test tests/KyberWeave.<Project>.Tests/KyberWeave.<Project>.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~<Class>"

# Repository policy: scripts, workflows, gate configuration, docs prose, source scans.
dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release
dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~<Class>"

# Shipped content: skills, agents, standards and documents.
dotnet test tests/KyberWeave.Content.Tests/KyberWeave.Content.Tests.csproj -c Release \
  --filter "FullyQualifiedName~<Class>"
```

`scripts/fast-loop.sh --touched` routes modified files to the targeted checks, including the
repository-policy scans. It is a loop aid and never evidence.

## Fixtures

`DocFixture` builds a documentation corpus on disk; `TempDirectory` is a disposable scratch
directory; `KyberWeaveTestPaths` finds the repository root. Dispose a fixture you create.
