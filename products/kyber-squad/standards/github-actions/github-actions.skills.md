---
id: standards/github-actions/skills
title: "github-actions skills"
doc-type: skill
status: draft
technology: github-actions
owner: unassigned
last-reviewed: 2026-11-22
---

# github-actions skills

How to carry out the common work in github-actions: the commands, tool servers and query patterns
this repository uses.

## What this folder covers

Workflow structure, gates, deployment, supply-chain and secret handling, and the done checklist
are in the standard. CI build diagnostics (binlog capture and upload) are under Commands,
"Build diagnostics". MSBuild project organization, incremental builds and anti-patterns are in
the `csharp` folder.

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
