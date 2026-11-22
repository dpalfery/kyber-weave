---
id: standards/github-actions/skills
title: "github-actions skills"
doc-type: skill
status: current
technology: github-actions
owner: dpalfery
last-reviewed: 2026-11-22
---

# github-actions skills

## Commands

```bash
actionlint <changed workflow files>
shellcheck <changed shell scripts>
kyber-weave review gates . --council    # the council's command, not a dev agent's
```

## What CI runs

- The `test` gate runs every unit, content, policy and architecture project; `test-policy` runs the repository-policy suite.
- The KyberDash and tray gates are `npm --prefix dash …` and `cargo --manifest-path
  dash/tray/src-tauri/Cargo.toml …`. CI and the council run them; a dev agent runs the
  targeted subset.

## Diagnosing a failed .NET build in CI

CI uploads no binlog, so reproduce first: build the failing project alone with `/bl:app.binlog`
(a smaller log than the solution's), then query it.

1. With a binlog MCP server (`Microsoft.AITools.BinlogMcp`) configured, use it: `tools/list`,
   then the structured tools for errors, properties, items, targets and embedded project files.
   Never `cat`, `head` or `strings` a `.binlog`.
2. Without one, replay it to text:

   ```bash
   dotnet msbuild app.binlog -noconlog \
     -fl  -flp:"v=diag;logfile=full.log;performancesummary" \
     -fl1 -flp1:"errorsonly;logfile=errors.log" \
     -fl2 -flp2:"warningsonly;logfile=warnings.log"
   grep -n "error " errors.log | head -30        # root error, ignore cascades
   grep -n "Target.*FAILED" full.log             # failing target
   grep "Time Elapsed" full.log | sort -t= -k2 -rn | head -20   # slowest targets
   ```

3. For a quick run with no binlog, `dotnet build -v:diagnostic -fl
   -flp:"logfile=build-diag.log;verbosity=diagnostic"` works, at 10-50 MB per project.

| Symptom | Check |
|---|---|
| `MSB3644` reference assemblies not found | `TargetFramework` in binlog properties; workload on the runner |
| `NU1101` package not found | NuGet feed and the `nuget.config` embedded in the binlog |
| `CS0234` type not found | reference resolution order; the `PackageReference` items |
| `NETSDK1004` assets file not found | `dotnet restore` missing or run in the wrong directory |
| Passes locally, fails in CI | environment variables; `global.json` against the CI SDK version |
