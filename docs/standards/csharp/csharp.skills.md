---
id: standards/csharp/skills
title: "csharp skills"
doc-type: skill
status: current
technology: csharp
owner: dpalfery
last-reviewed: 2026-11-22
---

# csharp skills

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

## Build diagnostics

- Rebuilds with nothing changed: `dotnet build -v:diagnostic -fl -flp:logfile=diag.log`, then
  search for `Building target '<T>' completely` and `Input file ... is newer than output file`.
- Slow build: `dotnet build -fl -flp:logfile=perf.log;performancesummary` for target and task
  timings, or `dotnet build /bl:build.binlog` and open it in the MSBuild Structured Log Viewer.
- Several CLI commands in one job: set `DOTNET_CLI_USE_MSBUILD_SERVER=1` so MSBuild Server stays
  warm between them. It is opt-in before the .NET 11 SDK and gains little in a one-command job.
