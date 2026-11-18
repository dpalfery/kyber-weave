---
id: standards/test/skills
title: "test skills"
doc-type: skill
status: current
technology: test
owner: dpalfery
last-reviewed: 2026-11-16
---

# test skills

## Commands

```bash
# The product's tests: build once, then the classes the change covers.
dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build \
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
