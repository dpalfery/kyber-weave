---
name: kw-dotnet-inner-loop
description: "Run the Kyber-Weave C# inner loop: build once, run only the test classes that cover the change, check formatting on changed files, isolate parallel builds, and re-run the docs gates after editing docs. Load when changing C# in this repository or when a dotnet filter exits 8."
---

# Kyber-Weave .NET inner loop

## Inner loop

Build once, then run the classes that cover the change. No dev agent runs the full suite:

```bash
dotnet build KyberWeave.sln -c Release
dotnet test tests/KyberWeave.<Project>.Tests/KyberWeave.<Project>.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~<Class>"
dotnet format KyberWeave.sln whitespace --verify-no-changes --include <changed files>
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --include <changed files>
```

A filter that matches no test exits 8, which is "nothing ran", not a pass. The task gate adds
the suppression scan (`kyber-weave review suppressions .`).

## Isolation

Pass `--artifacts-path .agent_scratchpad/<task>/artifacts` (or the equivalent
`BaseOutputPath`/`BaseIntermediateOutputPath` properties) when another worker may be building
the same projects. A shared `obj/` produces diagnostics that belong to neither change.

## Documentation

Editing anything under `docs/` means re-running both gates:

```bash
dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli -- docs drift .
```
