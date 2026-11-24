---
name: dotnet-build-diagnostics
description: "Diagnose and speed up .NET builds: find why a build rebuilds unexpectedly or runs slowly, capture and replay a binlog, read a diagnostic text log, and triage a .NET build that fails in CI but passes locally, including common MSBuild error symptoms. Load when a dotnet build is slow, rebuilds with nothing changed, fails in CI, or needs a binlog or diagnostic log read."
---

# .NET build diagnostics

## Commands

- `dotnet build -c Release` builds with analyzers; `dotnet watch` runs with hot reload. Run only
  the tests covering your change; a bare `dotnet test` is the council and CI gate.
- Rebuilds with nothing changed: `dotnet build -v:diagnostic -fl -flp:logfile=diag.log`, then
  search for `Building target '<T>' completely` and `Input file ... is newer than output file`.
- Slow build: `dotnet build -fl -flp:"logfile=perf.log;performancesummary"` for target and task
  timings, or `dotnet build /bl:build.binlog` and open it in the MSBuild Structured Log Viewer.
- Several CLI commands in one CI job: set `DOTNET_CLI_USE_MSBUILD_SERVER=1` so MSBuild Server
  stays warm between them. It is opt-in before the .NET 11 SDK, and gains little in a job
  that runs one command.
- Disable the Visual Studio fast up-to-date check (`DisableFastUpToDateCheck`) only while
  diagnosing a discrepancy.
- No spans: check `AddSource` matches the source name, the OTLP endpoint is reachable, and
  sampling is not dropping them.

## Diagnosing a failed .NET build in CI

1. Download the uploaded binlog artifact.
2. Query it with the binlog MCP server (`Microsoft.AITools.BinlogMcp`): `tools/list`, then the
   structured tools for errors, properties, items, targets and embedded project files. Read
   embedded sources through it; the original may not exist on the agent's disk. Never `cat`,
   `head` or `strings` a `.binlog`.
3. Without that server, replay it to text:

   ```bash
   dotnet msbuild build.binlog -noconlog \
     -fl  -flp:"v=diag;logfile=full.log;performancesummary" \
     -fl1 -flp1:"errorsonly;logfile=errors.log" \
     -fl2 -flp2:"warningsonly;logfile=warnings.log"
   grep -n "error " errors.log | head -30        # root error, ignore cascades
   grep -n "Target.*FAILED" full.log             # failing target
   grep "Time Elapsed" full.log | sort -t= -k2 -rn | head -20   # slowest targets
   ```

4. When a solution build fails deep in one project, build that project alone with
   `/bl:app.binlog` for a smaller log.
5. For a quick ad-hoc run with no binlog, write a diagnostic text log directly:
   `dotnet build -v:diagnostic -fl -flp:"logfile=build-diag.log;verbosity=diagnostic"`. It runs
   10–50 MB per project, so use it sparingly; a binlog and replay is cheaper when iterating.

| Symptom | Check |
|---|---|
| `MSB3644` reference assemblies not found | `TargetFramework` in binlog properties; workload installed on the runner |
| `NU1101` package not found | NuGet feed and the `nuget.config` embedded in the binlog |
| `CS0234` type not found | reference resolution order; the `PackageReference` items |
| `NETSDK1004` assets file not found | `dotnet restore` missing or run in the wrong directory |
| Passes locally, fails in CI | environment variables; local `global.json` against the CI SDK version |
