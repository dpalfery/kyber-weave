---
name: github-devops/ci-build-diagnostics
description: Capture and analyze MSBuild binary logs (binlog) in GitHub Actions — generation, fallback text replay, and MCP server analysis.
source: https://github.com/dotnet/skills/tree/main/plugins/dotnet-msbuild/skills/binlog-generation
       https://github.com/dotnet/skills/tree/main/plugins/dotnet-msbuild/skills/binlog-failure-analysis
---

# CI Build Diagnostics — MSBuild Binary Logs

## Step 1: Always Capture a Binlog in CI

Add `/bl:{}` to every MSBuild-based command in your GitHub Actions workflow. The `{}` placeholder (MSBuild 17.8+ / .NET 8 SDK+) generates a unique filename so parallel jobs never overwrite each other.

```yaml
- name: Build
  run: dotnet build /bl:{}

- name: Test
  run: dotnet test /bl:{}

- name: Publish
  run: dotnet publish /bl:{} -c Release
```

**PowerShell runners** (Windows): escape braces as `{{}}`:

```yaml
- name: Build (Windows)
  shell: pwsh
  run: dotnet build -bl:{{}}
```

If the SDK is older than .NET 8, use an explicit filename:

```bash
dotnet build /bl:build.binlog
```

---

## Step 2: Upload Binlogs as Artifacts

Always retain binlogs on failure so you can download and analyze them:

```yaml
- name: Build
  id: build
  run: dotnet build /bl:{}

- name: Upload binlogs on failure
  if: failure()
  uses: actions/upload-artifact@v4
  with:
    name: binlogs-${{ github.run_id }}
    path: "**/*.binlog"
    retention-days: 7
```

**Preserve binlogs when cleaning:** `git clean -fdx -e "*.binlog"` — binlogs are your only diagnostic record during iterative debugging.

---

## Step 3: Analyze the Binlog

### Primary Method: binlog MCP Server

The `Microsoft.AITools.BinlogMcp` MCP server queries `.binlog` files in their binary format directly — never use `cat`, `head`, or `strings` on a binlog file.

1. Call `tools/list` to see available MCP tools
2. Use the structured tools to query build errors, properties, items, targets, and embedded project files
3. Retrieve embedded source files through the MCP — the original may not exist on the agent's disk

### Fallback: Text Log Replay

When the MCP server is unavailable, replay the binlog to structured text logs:

```bash
dotnet msbuild build.binlog -noconlog \
  -fl  -flp:v=diag;logfile=full.log;performancesummary \
  -fl1 -flp1:errorsonly;logfile=errors.log \
  -fl2 -flp2:warningsonly;logfile=warnings.log
```

Then:

```bash
# Find the root error (ignore cascading failures)
grep -n "error " errors.log | head -30

# Find which target failed
grep -n "Target.*FAILED" full.log

# Check timing — identify the slowest targets
grep "Time Elapsed" full.log | sort -t= -k2 -rn | head -20
```

---

## Generating Verbose Text Logs Directly

For quick ad-hoc runs without a binlog:

```bash
dotnet build \
  -v:diagnostic \
  -fl -flp:logfile=build-diag.log;verbosity=diagnostic
```

The `-v:diagnostic` flag is very noisy (~10–50 MB per project) — use sparingly; binlog + replay is cheaper for iterative debugging.

---

## Common CI Failure Patterns

| Symptom | What to check in binlog/log |
|---|---|
| `error MSB3644` — reference assemblies not found | `TargetFramework` in binlog properties; SDK workload installed on agent |
| `error NU1101` — package not found | NuGet feed configuration; `nuget.config` embedded in binlog |
| `error CS0234` — type not found | Assembly reference resolution order; check items for `PackageReference` |
| `NETSDK1004` — assets file not found | `dotnet restore` step missing or running in wrong directory |
| Build passes locally, fails in CI | Check environment variables; local `global.json` vs CI SDK version |

---

## Multi-Project Diagnostic

When a solution-level build fails and the error is deep in one project:

```bash
# Build only the failing project to get a clean binlog
dotnet build <path-to-failing-project>.csproj /bl:app.binlog
```

Isolating to one project reduces binlog size and makes error tracing faster.

---

## References

- [MSBuild binary log](https://learn.microsoft.com/visualstudio/msbuild/obtaining-build-logs-with-msbuild#save-a-binary-log)
- [Structured log viewer](https://msbuildlog.com/) — open source GUI for `.binlog` files
